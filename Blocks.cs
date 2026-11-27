using System;
using System.Collections.Generic;
using KSP.UI.Screens;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Klocki programu lotu – pojedyncze czynności orbitalne, z których misje składa się w pliku .cfg (bez nowego DLL):
    ///   target = NAZWA                    wybór celu (statek, którego nazwa zawiera NAZWA; nie własne stopnie)
    ///   engine = ready                    silnik statku gotowy (następny stage, jeśli to sam silnik – KTDU + panele)
    ///   planeBurn = target                korekta płaszczyzny w najbliższym węźle (pomijana poniżej minDeg)
    ///   phasingPlan = target              plan orbity fazującej (jak 7K-T): b1Orbit, b3Orbit, b3OrbitSlow, behindKm, minKm
    ///   apsisBurn = KIEDY DOKĄD           impuls w apsydzie: KIEDY = b1 | pe | ap, DOKĄD = km | phasing | arrival | circular
    ///   transferBurn = target             Hohmann pod cel, przybycie behindKm za nim (okrążenie: orbit, domyślnie z planu)
    ///   matchVelocity = ap | now          wyrównanie prędkości z celem
    ///   openParts = target                otwarcie animacji celu (anims = …) i wysunięcie portów dokujących (obu statków)
    ///   mechjeb = rendezvous on|off       MechJeb Rendezvous (pola: mj.rendezvous.* w kroku/programie)
    ///   mechjeb = docking on|off          MechJeb Docking z wyborem portów (pola: mj.docking.*)
    /// Krok czeka na koniec poprzedniego klocka warunkiem „when = done”. Warunki: done, targetdist [m], relspeed [m/s], docked.
    /// Każdy klocek korzysta z tego samego, sprawdzonego kodu co „rendezvous” (impuls w apsydzie, wyszukiwanie B3, porty…).
    /// Przed każdym impulsem warp jest zatrzymywany (RdvWait). Błąd klocka zatrzymuje program – statek zostaje na orbicie.
    /// </summary>
    public partial class FlightProgramRunner
    {
        class Blk
        {
            public string kind, name;
            public int st;                  // 0 – czekanie/plan, 1 – impuls, 2 – gotowe, −1 – błąd
            public double ut = double.NaN, rOpp = double.NaN, t0, timeout = double.NaN;
            public string to;               // apsisBurn: dokąd
            public bool atNow;
        }
        Blk blk;
        double blkB1UT = double.NaN;
        bool blkDocking;

        public int BlockState { get { return blk == null || blk.st == 2 ? 1 : blk.st < 0 ? -1 : 0; } }
        bool BlkBusy { get { return blk != null && blk.st >= 0 && blk.st < 2; } }
        public bool BlockBurning { get { return blk != null && blk.st == 1; } }

        static double StepNum(ConfigNode n, string key, double def)
        {
            string v = n != null ? n.GetValue(key) : null;
            double x;
            return v != null && double.TryParse(v.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ? x : def;
        }

        void BlkStart(string kind, string name) { blk = new Blk { kind = kind, name = name, st = 0, t0 = Planetarium.GetUniversalTime() }; rdvWarped = false; rdvWarpIdx = -1; rdvWarpUser = false; rdvDir = 0; mLastDv = double.MaxValue; }
        void BlkDone(string msg) { if (blk != null) blk.st = 2; FlightInputHandler.state.mainThrottle = 0f; RdvLog(msg); }
        void BlkFail(string msg)
        {
            if (blk != null) blk.st = -1;
            FlightInputHandler.state.mainThrottle = 0f;
            RdvLog("BŁĄD: " + msg + " – program zatrzymany, statek zostaje na obecnej orbicie");
            ScreenMessages.PostScreenMessage("Program lotu: " + msg, 8f, ScreenMessageStyle.UPPER_CENTER);
            blkStopPending = true;      // zatrzymanie w następnej klatce (nie w trakcie wykonywania kroku)
        }
        bool blkStopPending;

        bool BlkNeedTarget()
        {
            if (rdvTarget != null && rdvTarget.state != Vessel.State.DEAD) return true;
            BlkFail("brak celu – najpierw krok „target = …”");
            return false;
        }

        /// <summary>Klucze kroku programu – zwraca true, jeśli klucz był klockiem.</summary>
        bool ExecuteBlock(string key, string val, ConfigNode n)
        {
            string k = key.ToLowerInvariant();
            switch (k)
            {
                case "target":
                {
                    Vessel t = rdvTarget != null && rdvTarget.state != Vessel.State.DEAD && (string.IsNullOrEmpty(val) || rdvTarget.vesselName.ToUpperInvariant().Contains(val.Trim().ToUpperInvariant())) ? rdvTarget : FindTarget(val);
                    if (t == null) { blk = new Blk { kind = "target", st = 0 }; BlkFail("nie ma celu „" + val + "” na orbicie"); return true; }
                    rdvTarget = t; FlightGlobals.fetch.SetVesselTarget(t, true);
                    rdvLaunchUT = Planetarium.GetUniversalTime() - vessel.missionTime;
                    RdvLog("cel: „" + t.vesselName + "” " + (t.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (t.orbit.ApA / 1000).ToString("F0", Inv) + " km");
                    return true;
                }
                case "engine":
                    rdvEngine = false; RdvEnsureEngine(); FlightInputHandler.state.mainThrottle = 0f;
                    return true;
                case "planeburn":
                {
                    if (!BlkNeedTarget()) return true;
                    BlkStart("plane", "korekta płaszczyzny");
                    Orbit me = vessel.orbit, to = rdvTarget.orbit; double now = Planetarium.GetUniversalTime();
                    double relInc = Vector3d.Angle(Vector3d.Cross(RelPos(me, now), RelVel(me, now)), Vector3d.Cross(RelPos(to, now), RelVel(to, now)));
                    double min = StepNum(n, "minDeg", RdvPlaneMin);
                    if (relInc <= min) BlkDone("różnica płaszczyzn " + relInc.ToString("F3", Inv) + "° ≤ " + min.ToString("F2", Inv) + "° – bez korekty (wyrówna MechJeb)");
                    else RdvLog("różnica płaszczyzn " + relInc.ToString("F3", Inv) + "° – korekta w węźle");
                    return true;
                }
                case "phasingplan":
                {
                    if (!BlkNeedTarget()) return true;
                    BlkStart("plan", "plan fazowania");
                    BlkPhasingPlan(n);
                    return true;
                }
                case "apsisburn":
                {
                    string[] w = val.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length < 2) { blk = new Blk(); BlkFail("apsisBurn: podaj KIEDY i DOKĄD, np. „apsisBurn = b1 phasing”"); return true; }
                    BlkStart("apsis", "impuls w apsydzie (" + val + ")");
                    blk.to = w[1];
                    double now = Planetarium.GetUniversalTime(); Orbit me = vessel.orbit;
                    if (w[0] == "b1") { if (double.IsNaN(blkB1UT)) { BlkFail("apsisBurn = b1 bez wcześniejszego „phasingPlan”"); return true; } blk.ut = blkB1UT; }
                    else if (w[0] == "pe" || w[0] == "ap")
                    {
                        double T = me.period, tA = now + (w[0] == "pe" ? me.timeToPe : me.timeToAp);
                        double minOrbit = StepNum(n, "orbit", 0);
                        if (minOrbit > 0) { double tMin = (now - vessel.missionTime) + RdvAscT + (minOrbit - 1) * T - 0.5 * T; while (tA < tMin) tA += T; }
                        if (tA - now < 20) tA += T;
                        blk.ut = tA;
                    }
                    else { BlkFail("apsisBurn: KIEDY = b1 | pe | ap (jest „" + w[0] + "”)"); return true; }
                    RdvLog(blk.name + " za " + KSPUtil.PrintTimeCompact(blk.ut - now, false));
                    return true;
                }
                case "transferburn":
                {
                    if (!BlkNeedTarget()) return true;
                    BlkStart("transfer", "zbliżenie (Hohmann pod cel)");
                    double orb = StepNum(n, "orbit", rdvB3Orbit);
                    if (orb != rdvB3Orbit || double.IsNaN(rdvB3Plan) || rdvB3Plan <= 0)
                        rdvB3Plan = (Planetarium.GetUniversalTime() - vessel.missionTime) + RdvAscT + (orb - 1) * vessel.orbit.period + RdvAscT;
                    blkBehind = StepNum(n, "behindKm", RdvStop / 1000) * 1000;
                    return true;
                }
                case "matchvelocity":
                {
                    if (!BlkNeedTarget()) return true;
                    BlkStart("match", "wyrównanie prędkości z celem");
                    blk.atNow = val.Trim().ToLowerInvariant() == "now";
                    blk.ut = blk.atNow ? Planetarium.GetUniversalTime() : Planetarium.GetUniversalTime() + vessel.orbit.timeToAp;
                    return true;
                }
                case "approach":
                {
                    if (!BlkNeedTarget()) return true;
                    BlkStart("approach", "zbliżanie (jak „Igła”)");
                    blkAp = new double[] {
                        StepNum(n, "stopDist", 200), StepNum(n, "rateK", 0.002), StepNum(n, "vmax", 10), StepNum(n, "vmin", 0.3),
                        StepNum(n, "tolFar", 0.5), StepNum(n, "tolNear", 0.1), StepNum(n, "maxDv", 80), StepNum(n, "doneSpeed", 0.15) };
                    blkApDv = 0; blkApBurns = 0; blkApBurn = false;
                    ReleaseAttitude(); holdAttitude = true;
                    vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                    RdvLog("zbliżanie: do " + blkAp[0].ToString("F0", Inv) + " m, prędkość ≈ " + blkAp[1].ToString("0.####", Inv) + " × odległość (" + blkAp[3].ToString("F1", Inv) + "–" + blkAp[2].ToString("F0", Inv) + " m/s), limit Δv " + blkAp[6].ToString("F0", Inv) + " m/s");
                    return true;
                }
                case "openparts":
                {
                    if (!BlkNeedTarget()) return true;
                    BlkStart("open", "otwarcie ładowni i portów");
                    string anims = n.GetValue("anims");
                    blkAnims = anims != null ? anims : Tun.S("rdv.openTargetAnims");
                    blk.timeout = StepNum(n, "timeout", Tun.D("rdv.portWaitSec"));
                    return true;
                }
                case "mechjeb":
                {
                    string[] w = val.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    bool on = w.Length < 2 || w[1] != "off";
                    if (w.Length > 0 && w[0] == "rendezvous") BlkMjRendezvous(on, n);
                    else if (w.Length > 0 && w[0] == "docking") BlkMjDocking(on, n);
                    else return false;     // inne „mechjeb …” – stare znaczenie (jeśli było)
                    return true;
                }
            }
            return false;
        }

        double blkBehind = 10000;
        double[] blkAp; double blkApDv; int blkApBurns; bool blkApBurn;
        string blkAnims = "";

        void BlkPhasingPlan(ConfigNode n)
        {
            CelestialBody b = vessel.mainBody; double mu = b.gravParameter, R = b.Radius;
            Orbit me = vessel.orbit, to = rdvTarget.orbit; double now = Planetarium.GetUniversalTime();
            rdvLaunchUT = now - vessel.missionTime;
            double T = me.period, tPe = now + me.timeToPe;
            double b1Orbit = StepNum(n, "b1Orbit", RdvB1Revs + 1);
            double tMin = rdvLaunchUT + (b1Orbit - 1) * T;
            while (tPe < tMin) tPe += T;
            if (tPe - now < 120) tPe += T;
            int o1 = (int)StepNum(n, "b3Orbit", RdvB3Orbit1), o2 = (int)StepNum(n, "b3OrbitSlow", RdvB3Orbit2);
            double stop = StepNum(n, "behindKm", RdvStop / 1000) * 1000;
            double rB1 = me.semiMajorAxis * (1 - me.eccentricity), rOpp = me.semiMajorAxis * (1 + me.eccentricity);
            double rMin = Math.Max(R + StepNum(n, "minKm", RdvRMinAlt / 1000) * 1000, rB1 + 2000);
            double theta1 = PhaseAt(me, to, tPe), dv, te, thU;
            foreach (int orb in new[] { o1, o2 })
            {
                double plan = rdvLaunchUT + RdvAscT + (orb - 1) * T + RdvAscT;
                double rph = RdvPlanner.SolveAnyPhase(mu, theta1, rB1, rOpp, to.semiMajorAxis, plan - tPe, stop, rMin, out dv, out te, out thU);
                if (double.IsNaN(rph)) continue;
                rdvRph = rph; rdvB3Orbit = orb; rdvB3Plan = plan; blkB1UT = tPe; RdvStop = stop;
                BlkDone("plan: impuls 1 za " + KSPUtil.PrintTimeCompact(tPe - now, false) + " (" + b1Orbit.ToString("F0", Inv) + ". okrążenie), faza " + (thU * 180 / Math.PI).ToString("F1", Inv) +
                        "°, orbita fazująca " + ((rph - R) / 1000).ToString("F1", Inv) + " km, zbliżenie na " + orb + ". okrążeniu, Δv ≈ " + dv.ToString("F0", Inv) + " m/s, przybycie za " + KSPUtil.PrintTimeCompact(te + tPe - now, false));
                return;
            }
            BlkFail("faza " + (RdvPlanner.Wrap2Pi(theta1) * 180 / Math.PI).ToString("F0", Inv) + "° nie pasuje do profili (" + o1 + ". i " + o2 + ". okrążenie) – start w innym oknie albo zmień b3Orbit/minKm");
        }

        /// <summary>Docelowy promień przeciwległej apsydy dla apsisBurn.</summary>
        double BlkTargetROpp(string to, double rAtBurn)
        {
            double R = vessel.mainBody.Radius, x;
            switch (to)
            {
                case "phasing": return rdvRph;
                case "arrival": return R + rdvAltT;
                case "circular": return rAtBurn;
            }
            return double.TryParse(to.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ? R + x * 1000 : double.NaN;
        }

        void BlocksTick()
        {
            if (blkStopPending) { blkStopPending = false; Stop(); return; }
            if (!BlkBusy || vessel == null) return;
            if (blk.st == 0 && blk.kind != "open" && blk.kind != "approach" && SeparationGuard()) return;     // przy otwieraniu cel jest tuż obok – to nie odrzucony stopień
            if (!rdvEngine && !double.IsNaN(rdvEngineAt) && Planetarium.GetUniversalTime() >= rdvEngineAt) { RdvEnsureEngine(); FlightInputHandler.state.mainThrottle = 0f; }
            CelestialBody b = vessel.mainBody; double mu = b.gravParameter, R = b.Radius, now = Planetarium.GetUniversalTime();
            Orbit me = vessel.orbit;
            Orbit to = rdvTarget != null && rdvTarget.state != Vessel.State.DEAD ? rdvTarget.orbit : null;
            if (blk.st == 0 && !double.IsNaN(blk.ut)) RendezvousInfo = blk.name + " za " + KSPUtil.PrintTimeCompact(Math.Max(0, blk.ut - now), false) + (to != null ? "; do celu " + ((rdvTarget.CoMD - vessel.CoMD).magnitude / 1000).ToString("F0", Inv) + " km" : "") + "; orbita " + (me.PeA / 1000).ToString("F0", Inv) + "×" + (me.ApA / 1000).ToString("F0", Inv) + " km";
            switch (blk.kind)
            {
                case "apsis":
                {
                    if (blk.st == 0)
                    {
                        double rB = RelPos(me, blk.ut).magnitude, rO = BlkTargetROpp(blk.to, rB);
                        if (double.IsNaN(rO)) { BlkFail("apsisBurn: nieznane DOKĄD „" + blk.to + "”"); return; }
                        double vB = RelVel(me, blk.ut).magnitude, dvE = Math.Sqrt(Math.Max(0, mu * (2 / rB - 2 / (rB + rO)))) - vB;
                        if (!RdvWait(blk.ut, dvE)) return;
                        blk.rOpp = BlkTargetROpp(blk.to, (vessel.CoMD - b.position).magnitude);
                        RdvEnsureEngine(); blk.st = 1; RdvLog(blk.name + ": start, przeciwległa apsyda → " + ((blk.rOpp - R) / 1000).ToString("F1", Inv) + " km");
                        return;
                    }
                    if (RdvApsisBurn(blk.rOpp)) BlkDone(blk.name + " gotowy: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km");
                    return;
                }
                case "transfer":
                {
                    if (to == null) { BlkFail("cel zniknął"); return; }
                    if (blk.st == 0)
                    {
                        if (double.IsNaN(blk.ut))
                        {
                            double rT = to.semiMajorAxis, stop = blkBehind;
                            Func<double, double> miss = t3 =>
                            {
                                Vector3d p3 = RelPos(me, t3);
                                double r1 = p3.magnitude;
                                double rA = RelPos(to, t3 + 0.5 * RdvPlanner.Per(mu, 0.5 * (r1 + rT))).magnitude;
                                double th = 0.5 * RdvPlanner.Per(mu, 0.5 * (r1 + rA));
                                Vector3d pT = RelPos(to, t3 + th), nT3 = Vector3d.Cross(RelPos(to, t3), RelVel(to, t3));
                                return RdvPlanner.SignedAngle(-p3, pT, nT3) - stop / rA;
                            };
                            double T = me.period, best = double.NaN;
                            double t0 = Math.Max(now + 300, rdvB3Plan - T), prevM = miss(t0);
                            for (double t = t0 + 30; t < rdvB3Plan + 2 * T; t += 30)
                            {
                                double c = miss(t);
                                if (prevM > 0 && c <= 0 && prevM - c < 1) { best = t; break; }
                                prevM = c;
                            }
                            if (double.IsNaN(best)) { BlkFail("zbliżenie: faza nie trafia w okno okrążenia " + rdvB3Orbit + " (±1 obieg)"); return; }
                            blk.ut = best;
                            rdvAltT = RelPos(to, best + 0.5 * RdvPlanner.Per(mu, 0.5 * (RelPos(me, best).magnitude + rT))).magnitude - R;
                            RdvLog(blk.name + " za " + KSPUtil.PrintTimeCompact(best - now, false) + ": apogeum → " + (rdvAltT / 1000).ToString("F1", Inv) + " km, przybycie " + (stop / 1000).ToString("F1", Inv) + " km za celem");
                        }
                        double rB = RelPos(me, blk.ut).magnitude, d1, d2, th0;
                        RdvPlanner.Hohmann(mu, rB, R + rdvAltT, out d1, out d2, out th0);
                        if (!RdvWait(blk.ut, d1)) return;
                        blk.rOpp = R + rdvAltT;
                        RdvEnsureEngine(); blk.st = 1; RdvLog(blk.name + ": start");
                        return;
                    }
                    if (RdvApsisBurn(blk.rOpp)) BlkDone(blk.name + " gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km");
                    return;
                }
                case "match":
                {
                    if (to == null) { BlkFail("cel zniknął"); return; }
                    if (blk.st == 0)
                    {
                        Vector3d dv = RelVel(to, blk.ut) - RelVel(me, blk.ut);
                        bool go = blk.atNow || RdvWait(blk.ut, dv.magnitude);
                        holdDir = dv.normalized; holdMode = Hold.Inertial; holdAttitude = true;
                        if (!go) return;
                        RdvEnsureEngine(); mLastDv = double.MaxValue; blk.st = 1; RdvLog(blk.name + ": " + dv.magnitude.ToString("F1", Inv) + " m/s");
                        return;
                    }
                    if (VectorBurn(RelVel(to, now) - RelVel(me, now)))
                        BlkDone(blk.name + " gotowe: do celu " + ((rdvTarget.CoMD - vessel.CoMD).magnitude / 1000).ToString("F2", Inv) + " km, prędkość względna " + (RelVel(to, now) - RelVel(me, now)).magnitude.ToString("F2", Inv) + " m/s");
                    return;
                }
                case "plane":
                {
                    if (to == null) { BlkFail("cel zniknął"); return; }
                    if (blk.st == 0)
                    {
                        if (double.IsNaN(blk.ut))
                        {
                            Vector3d n0 = Vector3d.Cross(RelPos(to, now), RelVel(to, now)).normalized;
                            double step = me.period / 360, prev = Vector3d.Dot(RelPos(me, now + 120), n0);
                            for (double t = now + 120 + step; t < now + me.period + 120; t += step)
                            {
                                double c = Vector3d.Dot(RelPos(me, t), n0);
                                if (prev * c <= 0) { blk.ut = t; break; }
                                prev = c;
                            }
                            if (double.IsNaN(blk.ut)) { BlkDone("korekta płaszczyzny: nie znalazłem węzła – pomijam"); return; }
                            rdvWarped = false;
                            RdvLog("korekta płaszczyzny w węźle za " + KSPUtil.PrintTimeCompact(blk.ut - now, false));
                        }
                        Vector3d nn = Vector3d.Cross(RelPos(to, blk.ut), RelVel(to, blk.ut)).normalized;
                        double vn = Vector3d.Dot(RelVel(me, blk.ut), nn);
                        bool go = RdvWait(blk.ut, Math.Abs(vn));
                        holdDir = (-nn * vn).normalized; holdMode = Hold.Inertial;
                        if (!go) return;
                        RdvEnsureEngine(); mLastDv = double.MaxValue; blk.st = 1; RdvLog("korekta płaszczyzny " + Math.Abs(vn).ToString("F1", Inv) + " m/s");
                        return;
                    }
                    if (now > blk.ut + RdvNodeLate) { FlightInputHandler.state.mainThrottle = 0f; RdvLog("korekta płaszczyzny spóźniona – czekam na następny węzeł"); blk.st = 0; blk.ut = double.NaN; return; }
                    Vector3d n1 = Vector3d.Cross(RelPos(to, now), RelVel(to, now)).normalized;
                    if (VectorBurn(-n1 * Vector3d.Dot(RelVel(me, now), n1))) BlkDone("płaszczyzna wyrównana");
                    return;
                }
                case "approach":
                {
                    // „Igła”: prędkość zbliżania proporcjonalna do odległości (rateK), wzdłuż linii celu; korekty, gdy błąd > tol.
                    // Symulacja offline (tools/rdv_approach_sim.py): z 9 km → 200 m w ~32 min, ~27 m/s, bez ruszania orbity.
                    if (to == null) { BlkFail("cel zniknął"); return; }
                    Vector3d rel = RelPos(to, now) - RelPos(me, now), u = RelVel(me, now) - RelVel(to, now);
                    double d = rel.magnitude, stopD = blkAp[0];
                    double vdes = d <= stopD ? 0 : Math.Max(blkAp[3], Math.Min(blkAp[2], d * blkAp[1]));
                    Vector3d e = rel.normalized * vdes - u;
                    double em = e.magnitude, tol = d < 1000 ? blkAp[5] : blkAp[4];
                    RendezvousInfo = "zbliżanie: " + d.ToString("F0", Inv) + " m, prędkość " + Vector3d.Dot(u, rel.normalized).ToString("F2", Inv) + " m/s (cel " + vdes.ToString("F2", Inv) + "), korekt " + blkApBurns + ", Δv " + blkApDv.ToString("F1", Inv) + " m/s";
                    if (!blkApBurn && d <= stopD && u.magnitude < blkAp[7])
                    {
                        FlightInputHandler.state.mainThrottle = 0f; blk.st = 0;
                        BlkDone("zbliżanie gotowe: " + d.ToString("F0", Inv) + " m, prędkość względna " + u.magnitude.ToString("F2", Inv) + " m/s, " + blkApBurns + " korekt, Δv " + blkApDv.ToString("F1", Inv) + " m/s");
                        return;
                    }
                    if (blkApDv > blkAp[6]) { BlkFail("zbliżanie zużyło " + blkApDv.ToString("F0", Inv) + " m/s (limit maxDv " + blkAp[6].ToString("F0", Inv) + ")"); return; }
                    if (!blkApBurn && em > tol) { blkApBurn = true; blkApBurns++; blk.st = 1; }
                    holdMode = Hold.Inertial; holdAttitude = true;
                    if (blkApBurn)
                    {
                        holdDir = e.normalized;
                        if (em < Math.Max(0.03, 0.5 * tol)) { blkApBurn = false; blk.st = 0; FlightInputHandler.state.mainThrottle = 0f; return; }
                        // na szynach silnik nie działa – warp fizyczny (×2–×4) może zostać
                        if (TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH) TimeWarp.SetRate(0, true);
                        EnsureBurnEngines();
                        double a = MaxAccel();     // pełny ciąg silnika statku / masa
                        float thr = (float)Math.Max(Tun.D("burn.minThrottle"), Math.Min(1, em / (a * Tun.D("burn.vectorSoftK"))));
                        bool on = attError < Tun.D("burn.attTol");
                        FlightInputHandler.state.mainThrottle = on ? thr : 0f;
                        // 1.47.2: Δv z rzeczywistego ciągu (wcześniej z przepustnicy – licznik nie zgadzał się z paliwem)
                        blkApDv += EngineThrustKN() / vessel.totalMass * TimeWarp.fixedDeltaTime;
                    }
                    else
                    {
                        // 1.47.2: między korektami nos zostaje w kierunku ostatniej korekty (następna jest zwykle podobna) –
                        // obracanie na cel i z powrotem co 20 s zjadało paliwo RCS (lot 27.11 11:49)
                        if (holdDir.sqrMagnitude < 0.5) holdDir = rel.normalized;
                        FlightInputHandler.state.mainThrottle = 0f;
                    }
                    return;
                }
                case "open":
                {
                    if (to == null) { BlkFail("cel zniknął"); return; }
                    if (!rdvTarget.loaded) { RendezvousInfo = "otwarcie: czekam, aż cel będzie w zasięgu fizyki (" + ((rdvTarget.CoMD - vessel.CoMD).magnitude / 1000).ToString("F1", Inv) + " km)"; blk.t0 = now; return; }
                    bool moving = RdvExtendPorts(vessel) | RdvExtendPortsWith(rdvTarget, blkAnims);
                    if (!moving) { BlkDone("ładownia i porty otwarte"); return; }
                    if (now - blk.t0 > blk.timeout) { BlkDone("otwieranie: limit czasu " + blk.timeout.ToString("F0", Inv) + " s – dalej bez czekania"); return; }
                    RendezvousInfo = "otwieranie ładowni i portów (" + (now - blk.t0).ToString("F0", Inv) + " s)";
                    return;
                }
            }
        }

        bool RdvExtendPortsWith(Vessel v, string anims)
        {
            string old = null;
            // RdvExtendPorts czyta listę z Tun – na czas wywołania podmieniamy ją listą z kroku
            if (anims != null) { old = blkAnimsOverride; blkAnimsOverride = anims; }
            bool r = RdvExtendPorts(v);
            if (anims != null) blkAnimsOverride = old;
            return r;
        }
        string blkAnimsOverride;

        void BlkMjRendezvous(bool on, ConfigNode n)
        {
            if (!on) { RdvStopMj(); RdvLog("MechJeb Rendezvous wyłączony"); return; }
            if (!BlkNeedTarget()) return;
            ReleaseAttitude(); holdAttitude = false;
            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true); vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            FlightGlobals.fetch.SetVesselTarget(rdvTarget, true);
            object core = Core();
            rdvMjMod = core != null ? Module(core, "MechJebModuleRendezvousAutopilot") : null;
            if (rdvMjMod == null) { blk = new Blk(); BlkFail("brak MechJeba (Rendezvous)"); return; }
            string err;
            MechJebPresets.SetMember(rdvMjMod, "desiredDistance", n.GetValue("distance") ?? RdvMjDist, out err);
            MechJebPresets.SetMember(rdvMjMod, "maxClosingSpeed", n.GetValue("maxSpeed") ?? RdvMjSpeed, out err);
            foreach (var kv in Tun.Mj("rendezvous")) if (!MechJebPresets.SetMember(rdvMjMod, kv.Key, kv.Value, out err)) RdvLog("MechJeb Rendezvous: pole " + kv.Key + " nie przyjęte (" + err + ")");
            foreach (ConfigNode.Value v in n.values) if (v.name.StartsWith("mj.rendezvous.")) MechJebPresets.SetMember(rdvMjMod, v.name.Substring(14), v.value.Trim(), out err);
            object pool = Users(rdvMjMod); if (pool != null) PoolCall(pool, "Add");
            RdvLog("MechJeb Rendezvous włączony (do " + (n.GetValue("distance") ?? RdvMjDist) + " m)");
        }

        void BlkMjDocking(bool on, ConfigNode n)
        {
            if (!on) { RdvStopDock(); blkDocking = false; RdvLog("MechJeb Docking wyłączony"); return; }
            if (!BlkNeedTarget()) return;
            ReleaseAttitude(); holdAttitude = false; FlightInputHandler.state.mainThrottle = 0f;   // sterowanie przejmuje MechJeb
            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true); vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            rdvMyPort = FreePort(vessel, null);
            rdvTgtPort = rdvMyPort != null ? FreePort(rdvTarget, rdvMyPort) : null;
            if (rdvMyPort == null || rdvTgtPort == null) { blk = new Blk(); BlkFail("brak wolnych, wysuniętych portów dokujących (najpierw „openParts = target”)"); return; }
            rdvMyPort.MakeReferenceTransform();
            FlightGlobals.fetch.SetVesselTarget(rdvTgtPort, true);
            object core = Core();
            rdvDockMod = core != null ? Module(core, "MechJebModuleDockingAutopilot") : null;
            if (rdvDockMod == null) { blk = new Blk(); BlkFail("brak MechJeba (Docking)"); return; }
            string err;
            MechJebPresets.SetMember(rdvDockMod, "speedLimit", n.GetValue("speed") ?? RdvDockSpeed, out err);
            foreach (var kv in Tun.Mj("docking")) if (!MechJebPresets.SetMember(rdvDockMod, kv.Key, kv.Value, out err)) RdvLog("MechJeb Docking: pole " + kv.Key + " nie przyjęte (" + err + ")");
            foreach (ConfigNode.Value v in n.values) if (v.name.StartsWith("mj.docking.")) MechJebPresets.SetMember(rdvDockMod, v.name.Substring(11), v.value.Trim(), out err);
            object pool = Users(rdvDockMod); if (pool != null) PoolCall(pool, "Add");
            blkDocking = true;
            RdvLog("MechJeb Docking: port „" + rdvMyPort.part.partInfo.title + "” → „" + rdvTgtPort.part.partInfo.title + "”, " + (n.GetValue("speed") ?? RdvDockSpeed) + " m/s");
        }

        double BlkTargetDist() { return rdvTarget != null && rdvTarget.state != Vessel.State.DEAD ? (rdvTarget.CoMD - vessel.CoMD).magnitude : double.NaN; }
        double BlkRelSpeed()
        {
            if (rdvTarget == null || rdvTarget.state == Vessel.State.DEAD) return double.NaN;
            double now = Planetarium.GetUniversalTime();
            return (RelVel(rdvTarget.orbit, now) - RelVel(vessel.orbit, now)).magnitude;
        }
        double BlkDocked() { return (blkDocking || rdv == RDV.Dock || rdv == RDV.Done) && RdvDocked() ? 1 : rdv == RDV.Done ? 1 : 0; }

        void StopBlocks()
        {
            if (BlkBusy) FlightInputHandler.state.mainThrottle = 0f;
            blk = null; blkB1UT = double.NaN;
            if (blkDocking) { RdvStopDock(); blkDocking = false; }
        }
    }
}
