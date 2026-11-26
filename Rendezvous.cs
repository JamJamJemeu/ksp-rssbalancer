using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Spotkanie na orbicie jak Sojuz 7K-T z Salutami – profil jednodobowy (albo dwudobowy):
    ///   wstawienie ~193 × 253 km w płaszczyźnie celu, 4. okrążenie: impuls w perygeum (apogeum → r_ph),
    ///   5.: w apogeum (perygeum → r_ph, orbita fazująca kołowa poniżej celu), 17. (33.): Hohmann r_ph → cel,
    ///   przybycie ~10 km za celem, dalej MechJeb (Rendezvous do 100 m, Docking 0,3 m/s – jak „Igła”).
    /// Źródła: Soyuz 21 – manewry na 5. i 17. okrążeniu, dokowanie następnego dnia; Soyuz 11 – pierwsza korekta na 4.,
    /// „Igła” od ~7 km; stacje na orbitach o powtarzalnym śladzie, warunki co 1–2 tygodnie.
    /// Czyste obliczenia w RdvPlanner (sprawdzane offline z modelem Pythona /tmp/rdv/plan.py).
    /// </summary>
    public static class RdvPlanner
    {
        public static double Per(double mu, double a) { return 2 * Math.PI * Math.Sqrt(a * a * a / mu); }

        /// <summary>Hohmann r1 → r2: impulsy i czas przelotu.</summary>
        public static void Hohmann(double mu, double r1, double r2, out double dv1, out double dv2, out double t)
        {
            double a = 0.5 * (r1 + r2);
            dv1 = Math.Sqrt(mu * (2 / r1 - 1 / a)) - Math.Sqrt(mu / r1);
            dv2 = Math.Sqrt(mu / r2) - Math.Sqrt(mu * (2 / r2 - 1 / a));
            t = 0.5 * Per(mu, a);
        }

        /// <summary>
        /// Kąt fazowy (cel przed nami, rad) przy przybyciu na wysokość celu, jeśli orbita fazująca ma promień rph.
        /// theta1 – kąt w chwili B1 (perygeum rB1), dtB3 – czas od B1 do B3.
        /// </summary>
        public static double Miss(double mu, double theta1, double rB1, double rph, double rT, double dtB3, double stop, out double tEnd)
        {
            double nT = 2 * Math.PI / Per(mu, rT);
            double t = 0, th = theta1;
            double a1 = 0.5 * (rB1 + rph), h = 0.5 * Per(mu, a1);
            t += h; th += nT * h - Math.PI;
            double dt = Math.Max(0, dtB3 - t), nph = 2 * Math.PI / Per(mu, rph);
            th += (nT - nph) * dt; t += dt;
            double d1, d2, th3; Hohmann(mu, rph, rT, out d1, out d2, out th3);
            th += nT * th3 - Math.PI; t += th3;
            tEnd = t;
            return th - stop / rT;
        }

        /// <summary>
        /// Promień orbity fazującej (bisekcja). NaN = brak rozwiązania w [rMin, rT − 5 km]
        /// (cel za blisko albo za daleko na ten profil). dv – suma czterech impulsów.
        /// </summary>
        public static double SolvePhasing(double mu, double theta1, double rB1, double rOpp, double rT, double dtB3, double stop, double rMin,
                                          out double dv, out double tEnd)
        {
            dv = double.NaN; tEnd = double.NaN;
            double lo = rMin, hi = rT - 5000, te;
            double flo = Miss(mu, theta1, rB1, lo, rT, dtB3, stop, out te), fhi = Miss(mu, theta1, rB1, hi, rT, dtB3, stop, out te);
            if (flo * fhi > 0) return double.NaN;
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi), fm = Miss(mu, theta1, rB1, mid, rT, dtB3, stop, out te);
                if (flo * fm <= 0) hi = mid; else { lo = mid; flo = fm; }
            }
            double rph = 0.5 * (lo + hi);
            Miss(mu, theta1, rB1, rph, rT, dtB3, stop, out tEnd);
            double aI = 0.5 * (rB1 + rOpp), a1 = 0.5 * (rB1 + rph);
            double b1 = Math.Sqrt(mu * (2 / rB1 - 1 / a1)) - Math.Sqrt(mu * (2 / rB1 - 1 / aI));
            double b2 = Math.Sqrt(mu / rph) - Math.Sqrt(mu * (2 / rph - 1 / a1));
            double d3, d4, tt; Hohmann(mu, rph, rT, out d3, out d4, out tt);
            dv = Math.Abs(b1) + Math.Abs(b2) + Math.Abs(d3) + Math.Abs(d4);
            return rph;
        }

        /// <summary>Jak SolvePhasing, ale faza jest znana tylko modulo 2π – sprawdza θ + 2πk (k = −1…2), wybiera najtańsze.</summary>
        public static double SolveAnyPhase(double mu, double theta1, double rB1, double rOpp, double rT, double dtB3, double stop, double rMin,
                                           out double dv, out double tEnd, out double thetaUsed)
        {
            double best = double.NaN; dv = double.NaN; tEnd = double.NaN; thetaUsed = theta1;
            for (int k = -1; k <= 2; k++)
            {
                double d, te, th = theta1 + 2 * Math.PI * k;
                double r = SolvePhasing(mu, th, rB1, rOpp, rT, dtB3, stop, rMin, out d, out te);
                if (double.IsNaN(r) || (!double.IsNaN(dv) && d >= dv)) continue;
                best = r; dv = d; tEnd = te; thetaUsed = th;
            }
            return best;
        }

        /// <summary>Kąt od a do b (rad) w płaszczyźnie o normalnej n, dodatni zgodnie z ruchem.</summary>
        public static double SignedAngle(Vector3d a, Vector3d b, Vector3d n)
        {
            return Math.Atan2(Vector3d.Dot(Vector3d.Cross(a, b), n.normalized), Vector3d.Dot(a, b));
        }

        public static double Wrap2Pi(double x) { x %= 2 * Math.PI; return x < 0 ? x + 2 * Math.PI : x; }
    }

    public partial class FlightProgramRunner
    {
        // kalibracja wynoszenia Sojuza z lotu 25.11 17:13 (wyrzutnia 46,0083° N 63,3055° E → wstawienie T+497,7 s
        // 51,0864° N 85,9818° E); dla 51,6° z Bajkonuru
        const double RdvAscT = 498.0, RdvInsDLat = 5.0781, RdvInsDLon = 22.6763;
        const double RdvStop = 10000;          // przybycie 10 km za celem – dalej „Igła” (MechJeb)
        const double RdvRMinAlt = 195000;      // orbita fazująca nie niżej (powyżej perygeum wstawienia)

        enum RDV { None, Plane, PlaneBurn, B1, B1Burn, B2, B2Burn, B3, B3Burn, B4, B4Burn, Mj, Dock, Done, Failed }
        RDV rdv = RDV.None;
        Vessel rdvTarget;
        int rdvB3Orbit = 17;
        double rdvRph, rdvAltT, rdvBurnUT = double.NaN, rdvB3Plan, rdvLaunchUT, rdvDvUsed, rdvNextLog;
        bool rdvWarped, rdvEngine;
        ModuleDockingNode rdvMyPort, rdvTgtPort;
        public string RendezvousInfo = "";
        public int RendezvousState { get { return rdv == RDV.Done ? 1 : rdv == RDV.Failed ? -1 : 0; } }

        // ------------------------------------------------------------------ cel
        Vessel FindTarget(string name)
        {
            ITargetable t = FlightGlobals.fetch != null ? FlightGlobals.fetch.VesselTarget : null;
            Vessel v = t != null ? t.GetVessel() : null;
            if (v != null && v != vessel && !v.vesselName.ToUpperInvariant().StartsWith(vessel.vesselName.ToUpperInvariant())) return v;
            if (string.IsNullOrEmpty(name)) return null;
            string key = name.Trim().ToUpperInvariant();
            string own = vessel.vesselName.ToUpperInvariant();
            Vessel best = null;
            foreach (Vessel o in FlightGlobals.Vessels)
            {
                if (o == null || o == vessel || o.situation != Vessel.Situations.ORBITING) continue;
                if (o.vesselType == VesselType.Debris || o.vesselType == VesselType.SpaceObject) continue;
                string nm = o.vesselName.ToUpperInvariant();
                // własne stopnie i odłamki („SOJUZ … BURAN DOCK Probe” też zawiera „BURAN”) – nigdy
                if (nm.StartsWith(own)) continue;
                if (nm == key) return o;                                    // dokładna nazwa ma pierwszeństwo
                if (nm.Contains(key) && (best == null || o.GetTotalMass() > best.GetTotalMass())) best = o;
            }
            return best;
        }

        static Vector3d RelPos(Orbit o, double ut) { return o.getRelativePositionAtUT(ut).xzy; }
        static Vector3d RelVel(Orbit o, double ut) { return o.getOrbitalVelocityAtUT(ut).xzy; }

        /// <summary>Kąt fazowy celu względem nas w chwili ut (rad, dodatni = cel przed nami), w płaszczyźnie celu.</summary>
        double PhaseAt(Orbit me, Orbit tg, double ut)
        {
            Vector3d rT = RelPos(tg, ut), n = Vector3d.Cross(rT, RelVel(tg, ut));
            return RdvPlanner.SignedAngle(RelPos(me, ut), rT, n);
        }

        // ------------------------------------------------------------------ okno startowe
        /// <summary>
        /// Najbliższe starty (UT), gdy punkt wstawienia leży w płaszczyźnie celu, i profil, który z nich wychodzi.
        /// Model: wstawienie RdvAscT s po starcie w punkcie wyrzutnia + (RdvInsDLat, RdvInsDLon) – tak leciał Sojuz 25.11.
        /// </summary>
        bool FindRendezvousWindow(Vessel tgt, int maxDays, out double L, out int b3, out double theta, out double dv, out double tDock)
        {
            L = theta = dv = tDock = double.NaN; b3 = 0;
            CelestialBody b = vessel.mainBody; Orbit to = tgt.orbit;
            double now = Planetarium.GetUniversalTime(), rotP = b.rotationPeriod, mu = b.gravParameter, R = b.Radius;
            Vector3d n = Vector3d.Cross(RelPos(to, now), RelVel(to, now)).normalized;
            Vector3d ax = b.transform.up;
            Vector3d node = Vector3d.Cross(ax, n).normalized, m90 = Vector3d.Cross(n, node);
            double padLat = vessel.latitude, padLon = vessel.longitude;
            double insLat = padLat + RdvInsDLat, insLon = padLon + RdvInsDLon;
            Func<double, double, double, Vector3d> inert = (lat, lon, ut) =>
                (b.GetWorldSurfacePosition(lat, lon + (ut - now) * 360.0 / rotP, 0) - b.position).normalized;
            Func<double, double> f = Lt => Vector3d.Dot(inert(insLat, insLon, Lt + RdvAscT), n);
            double rT = to.semiMajorAxis, rp = R + 193000, ra = R + 253000, TI = RdvPlanner.Per(mu, 0.5 * (rp + ra)), nT = 2 * Math.PI / RdvPlanner.Per(mu, rT);
            var cands = new List<double>();
            double step = 60, prev = f(now + 600);
            for (double t = now + 600 + step; t < now + maxDays * 86400.0; t += step)
            {
                double cur = f(t);
                if (prev * cur <= 0)
                {
                    double lo = t - step, hi = t;
                    for (int i = 0; i < 40; i++) { double mid = 0.5 * (lo + hi); if (f(lo) * f(mid) <= 0) hi = mid; else lo = mid; }
                    double Lc = 0.5 * (lo + hi);
                    Vector3d ps = inert(padLat, padLon, Lc);
                    double uSite = Math.Atan2(Vector3d.Dot(ps, m90), Vector3d.Dot(ps, node));
                    if (uSite > -Math.PI / 2 && uSite < Math.PI / 2) cands.Add(Lc);      // wyrzutnia po stronie wstępującej – start na NE
                }
                prev = cur;
            }
            foreach (int prof in new[] { 17, 33 })
                foreach (double Lc in cands)
                {
                    Vector3d pIns = inert(insLat, insLon, Lc + RdvAscT);
                    Vector3d pT = RelPos(to, Lc + RdvAscT).normalized;
                    double th0 = RdvPlanner.Wrap2Pi(RdvPlanner.SignedAngle(pIns, pT, n));
                    double th1 = th0 + nT * 3 * TI - 6 * Math.PI;
                    double dtB3 = (prof - 1) * TI + RdvAscT - 3 * TI;
                    double d, te, thU;
                    double rph = RdvPlanner.SolveAnyPhase(mu, th1, rp, ra, rT, dtB3, RdvStop, R + RdvRMinAlt, out d, out te, out thU);
                    if (double.IsNaN(rph)) continue;
                    if (double.IsNaN(L) || Lc < L - 1)
                    {
                        // profil dwudobowy tylko, gdy jednodobowy jest dużo później (> 2 doby)
                        if (prof == 33 && !double.IsNaN(L) && L - Lc < 2 * 86400) continue;
                        L = Lc; b3 = prof; theta = th0; dv = d; tDock = te + 3 * TI + RdvAscT;
                    }
                    break;      // dla danego profilu interesuje nas pierwsze dobre okno
                }
            return !double.IsNaN(L);
        }

        static bool HasDockingPort(Vessel v)
        {
            if (v.loaded) { foreach (Part p in v.parts) if (p.FindModuleImplementing<ModuleDockingNode>() != null) return true; return false; }
            if (v.protoVessel == null) return false;
            foreach (ProtoPartSnapshot pp in v.protoVessel.protoPartSnapshots)
                foreach (ProtoPartModuleSnapshot m in pp.modules) if (m.moduleName == "ModuleDockingNode") return true;
            return false;
        }

        /// <summary>Statki, do których da się polecieć z tej wyrzutni: na orbicie wokół tego ciała, nachylenie ≥ szerokość startu,
        /// 120–1000 km; bez odłamków, asteroid, EVA i własnych stopni. Najpierw cel z programu, potem z portem dokującym.</summary>
        List<Vessel> RendezvousCandidates(string targetName)
        {
            var list = new List<Vessel>();
            string own = vessel.vesselName.ToUpperInvariant(), key = (targetName ?? "").Trim().ToUpperInvariant();
            double lat = Math.Abs(vessel.latitude);
            foreach (Vessel o in FlightGlobals.Vessels)
            {
                if (o == null || o == vessel || o.mainBody != vessel.mainBody || o.situation != Vessel.Situations.ORBITING) continue;
                if (o.vesselType == VesselType.Debris || o.vesselType == VesselType.SpaceObject || o.vesselType == VesselType.EVA || o.vesselType == VesselType.Flag || o.vesselType == VesselType.DroppedPart) continue;
                if (o.vesselName.ToUpperInvariant().StartsWith(own)) continue;
                Orbit ob = o.orbit;
                if (ob.PeA < 120000 || ob.ApA > 1000000 || ob.eccentricity > 0.05) continue;
                double inc = ob.inclination > 90 ? 180 - ob.inclination : ob.inclination;
                if (inc < lat - 0.3) continue;
                list.Add(o);
            }
            Func<Vessel, int> rank = o => (key.Length > 0 && o.vesselName.ToUpperInvariant() == key ? 0 : key.Length > 0 && o.vesselName.ToUpperInvariant().Contains(key) ? 1 : 2) * 2 + (HasDockingPort(o) ? 0 : 1);
            list.Sort((a, b) => { int c = rank(a).CompareTo(rank(b)); return c != 0 ? c : string.Compare(a.vesselName, b.vesselName, StringComparison.Ordinal); });
            if (list.Count > 14) list.RemoveRange(14, list.Count - 14);
            return list;
        }

        void AskRendezvousWindow(string targetName)
        {
            launchHoldUT = double.PositiveInfinity;
            List<Vessel> cands = RendezvousCandidates(targetName);
            if (cands.Count == 0)
            {
                FlightLogger.Event("Spotkanie: na orbicie nie ma żadnego osiągalnego celu");
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "RSSBalancerRdvWindow", "Spotkanie na orbicie",
                    "Na orbicie nie ma statku, do którego da się polecieć z tej wyrzutni (nachylenie co najmniej " + Math.Abs(vessel.latitude).ToString("F1", Inv) + "°, wysokość 120–1000 km).", "OK", false, HighLogic.UISkin);
                launchHoldUT = double.NaN; Stop();
                return;
            }
            var opts = new List<DialogGUIBase>();
            foreach (Vessel c in cands)
            {
                Vessel cc = c;
                string lbl = c.vesselName + "  –  " + (c.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (c.orbit.ApA / 1000).ToString("F0", Inv) + " km, " + c.orbit.inclination.ToString("F1", Inv) + "°" + (HasDockingPort(c) ? ", port dokujący" : ", bez portu");
                opts.Add(new DialogGUIButton(lbl, () => RendezvousWindowFor(cc), 520f, 26f, true));
            }
            opts.Add(new DialogGUIButton("Anuluj", () => { launchHoldUT = double.NaN; Stop(); }, 520f, 26f, true));
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RSSBalancerRdvTarget", "Do kogo lecimy? (na górze cel z programu i statki z portem dokującym)", "Spotkanie na orbicie – wybór celu", HighLogic.UISkin, 560f,
                    new DialogGUIVerticalLayout(opts.ToArray())),
                false, HighLogic.UISkin);
        }

        void RendezvousWindowFor(Vessel tgt)
        {
            FlightLogger.Event("Spotkanie: wybrany cel „" + tgt.vesselName + "” " + (tgt.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (tgt.orbit.ApA / 1000).ToString("F0", Inv) + " km, " + tgt.orbit.inclination.ToString("F2", Inv) + "°");
            FlightGlobals.fetch.SetVesselTarget(tgt, true);
            rdvTarget = tgt;
            double L, th, dv, td; int b3;
            if (!FindRendezvousWindow(tgt, 30, out L, out b3, out th, out dv, out td))
            {
                FlightLogger.Event("Spotkanie: brak okna w 30 dobach dla „" + tgt.vesselName + "”");
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "RSSBalancerRdvWindow", "Spotkanie na orbicie",
                    "W ciągu 30 dób nie ma okna na spotkanie z „" + tgt.vesselName + "” (orbita celu ma ślad prawie powtarzalny – faza się nie przesuwa). Zmień wysokość celu o kilka km i spróbuj ponownie.", "OK", false, HighLogic.UISkin);
                launchHoldUT = double.NaN; Stop();
                return;
            }
            rdvB3Orbit = b3;
            double now = Planetarium.GetUniversalTime();
            CelestialBody b = vessel.mainBody;
            string msg = "Cel: „" + tgt.vesselName + "” " + (tgt.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (tgt.orbit.ApA / 1000).ToString("F0", Inv) + " km, " + tgt.orbit.inclination.ToString("F1", Inv) + "°.\n\n" +
                         "Najbliższe okno: start za " + KSPUtil.PrintTimeCompact(L - now, false) + " (ok. " + HourText(SolarTimeAt(b, vessel.latitude, vessel.longitude, L)) + " czasu słonecznego Bajkonuru).\n" +
                         "Profil " + (b3 == 17 ? "jednodobowy (jak 7K-T): manewry na 4. i 5. okrążeniu, zbliżenie na 17." : "dwudobowy: manewry na 4. i 5. okrążeniu, zbliżenie na 33.") + "\n" +
                         "Cel w chwili wstawienia " + (th * 180 / Math.PI).ToString("F0", Inv) + "° przed nami, manewry ok. " + dv.ToString("F0", Inv) + " m/s, dokowanie ok. " + (td / 3600).ToString("F1", Inv) + " h po starcie.\n\n" +
                         "Przyspieszyć czas do okna startowego?";
            FlightLogger.Event("Spotkanie: okno za " + KSPUtil.PrintTimeCompact(L - now, false) + ", profil B3 na " + b3 + ". okr., faza " + (th * 180 / Math.PI).ToString("F1", Inv) + "°, Δv ≈ " + dv.ToString("F0", Inv) + " m/s");
            launchHoldUT = double.PositiveInfinity;
            double Lc = L;
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RSSBalancerRdvWindow", msg, "Spotkanie na orbicie", HighLogic.UISkin,
                    new DialogGUIButton("Tak – przyspiesz do okna", () =>
                    {
                        launchHoldUT = Lc;
                        FlightLogger.Event("Spotkanie: okno wybrane – czekam do startu");
                        if (Lc - Planetarium.GetUniversalTime() > 60) ProgWarpTo(Lc - 15);
                    }),
                    new DialogGUIButton("Anuluj", () => { launchHoldUT = double.NaN; Stop(); })),
                false, HighLogic.UISkin);
        }

        // ------------------------------------------------------------------ lot
        string StartRendezvous(string targetName)
        {
            Vessel tgt = rdvTarget != null && rdvTarget.state != Vessel.State.DEAD ? rdvTarget : FindTarget(targetName);
            if (tgt == null) { rdv = RDV.Failed; return "Spotkanie: brak celu „" + targetName + "”."; }
            rdvTarget = tgt;
            FlightGlobals.fetch.SetVesselTarget(tgt, true);
            rdvLaunchUT = Planetarium.GetUniversalTime() - vessel.missionTime;
            rdvDvUsed = 0; rdvEngine = false;
            Orbit me = vessel.orbit, to = tgt.orbit;
            double relInc = Vector3d.Angle(Vector3d.Cross(RelPos(me, 0 + Planetarium.GetUniversalTime()), RelVel(me, Planetarium.GetUniversalTime())),
                                           Vector3d.Cross(RelPos(to, Planetarium.GetUniversalTime()), RelVel(to, Planetarium.GetUniversalTime())));
            rdv = relInc > 0.02 ? RDV.Plane : RDV.B1;
            rdvBurnUT = double.NaN; rdvWarped = false;
            string s = "Spotkanie z „" + tgt.vesselName + "”: profil " + (rdvB3Orbit == 17 ? "jednodobowy" : "dwudobowy") + ", różnica płaszczyzn " + relInc.ToString("F3", Inv) + "°";
            FlightLogger.Event(s);
            return s;
        }

        double MaxAccel()
        {
            double t = 0;
            foreach (Part p in vessel.parts)
                foreach (PartModule pm in p.Modules)
                {
                    ModuleEngines e = pm as ModuleEngines;
                    if (e == null || e.propellants.Exists(pr => pr.name == "SolidFuel" || pr.name == "IntakeAir")) continue;
                    if (e.EngineIgnited || p.inverseStage == vessel.currentStage - 1) t += e.maxThrust * e.thrustPercentage / 100.0;
                }
            return t > 0.1 ? t / vessel.totalMass : 0.5;
        }

        /// <summary>Silnik statku gotowy: jeśli nic nie pracuje, a następny stage to sam silnik (Sojuz: KTDU) – odpal go.</summary>
        void RdvEnsureEngine()
        {
            if (rdvEngine) return;
            rdvEngine = true;
            foreach (Part p in vessel.parts)
                foreach (PartModule pm in p.Modules) { ModuleEngines e = pm as ModuleEngines; if (e != null && e.EngineIgnited && !e.flameout && !e.propellants.Exists(pr => pr.name == "SolidFuel")) return; }
            bool eng = false, dec = false; int next = vessel.currentStage - 1;
            foreach (Part p in vessel.parts)
            {
                if (p.inverseStage != next) continue;
                foreach (PartModule pm in p.Modules)
                {
                    if (pm is ModuleEngines && !((ModuleEngines)pm).propellants.Exists(pr => pr.name == "SolidFuel")) eng = true;
                    if (pm is ModuleDecouple || pm is ModuleAnchoredDecoupler || pm is ModuleParachute) dec = true;
                }
            }
            if (eng && !dec) { StageManager.ActivateNextStage(); FlightLogger.Event("Spotkanie: zapłon silnika statku (stage " + next + ")"); }
            else mEnginesChecked = false;     // EnsureBurnEngines (bez paliwa stałego) przy pierwszym impulsie
        }

        /// <summary>Czekanie na impuls o czasie tB (środek impulsu ~ tB). true = czas zaczynać.</summary>
        bool RdvWait(double tB, double dvEst)
        {
            double now = Planetarium.GetUniversalTime();
            double start = tB - 0.5 * Math.Abs(dvEst) / MaxAccel();
            if (!rdvWarped && start - now > 180)
            {
                double nd; Vessel nv;
                if (NearestNeighbour(out nd, out nv) >= SepSafeDist && TimeWarp.CurrentRateIndex == 0) { ProgWarpTo(start - 90); rdvWarped = true; }
            }
            holdDir = Math.Sign(dvEst) >= 0 ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
            return now >= start;
        }

        void RdvLog(string s) { FlightLogger.Event("Spotkanie: " + s); RendezvousInfo = s; }

        private void RendezvousTick()
        {
            if (rdv == RDV.None || rdv == RDV.Done || rdv == RDV.Failed || vessel == null) return;
            if (rdvTarget == null || rdvTarget.state == Vessel.State.DEAD)
            {
                // po zadokowaniu cel staje się częścią naszego statku
                if (rdv == RDV.Dock && RdvDocked()) { RdvLog("zadokowano"); rdv = RDV.Done; return; }
                rdv = RDV.Failed; RdvLog("cel zniknął"); return;
            }
            if (!IsBurnRdv(rdv) && rdv < RDV.Mj && SeparationGuard()) return;
            CelestialBody b = vessel.mainBody; double mu = b.gravParameter, R = b.Radius;
            Orbit me = vessel.orbit, to = rdvTarget.orbit;
            double now = Planetarium.GetUniversalTime();
            double dist = (rdvTarget.CoMD - vessel.CoMD).magnitude;
            if (now > rdvNextLog && rdv < RDV.Mj)
            {
                rdvNextLog = now + 600;
                RendezvousInfo = rdv + ": odległość " + (dist / 1000).ToString("F0", Inv) + " km, faza " + (PhaseAt(me, to, now) * 180 / Math.PI).ToString("F1", Inv) + "°, orbita " + (me.PeA / 1000).ToString("F0", Inv) + "×" + (me.ApA / 1000).ToString("F0", Inv) + " km";
            }
            switch (rdv)
            {
                case RDV.Plane:
                {
                    // najbliższy węzeł względny: nasze położenie przechodzi przez płaszczyznę celu
                    if (double.IsNaN(rdvBurnUT))
                    {
                        Vector3d n = Vector3d.Cross(RelPos(to, now), RelVel(to, now)).normalized;
                        double step = me.period / 360, prev = Vector3d.Dot(RelPos(me, now + 120), n);
                        for (double t = now + 120 + step; t < now + me.period + 120; t += step)
                        {
                            double c = Vector3d.Dot(RelPos(me, t), n);
                            if (prev * c <= 0) { rdvBurnUT = t; break; }
                            prev = c;
                        }
                        rdvWarped = false;
                        if (double.IsNaN(rdvBurnUT)) { rdv = RDV.B1; break; }
                    }
                    Vector3d nn = Vector3d.Cross(RelPos(to, rdvBurnUT), RelVel(to, rdvBurnUT)).normalized;
                    double vn = Vector3d.Dot(RelVel(me, rdvBurnUT), nn);
                    bool go = RdvWait(rdvBurnUT, Math.Abs(vn));
                    holdDir = (-nn * vn).normalized; holdMode = Hold.Inertial;      // normalna – inercjalnie
                    if (!go) break;
                    RdvEnsureEngine(); mLastDv = double.MaxValue; rdv = RDV.PlaneBurn;
                    RdvLog("korekta płaszczyzny " + Math.Abs(vn).ToString("F1", Inv) + " m/s");
                    break;
                }
                case RDV.PlaneBurn:
                {
                    Vector3d n = Vector3d.Cross(RelPos(to, now), RelVel(to, now)).normalized;
                    double vn = Vector3d.Dot(RelVel(me, now), n);
                    // wektor w świecie: obt_velocity jest w świecie; normalna z orbit (xzy) też – spójne
                    if (VectorBurn(-n * vn)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B1; rdvBurnUT = double.NaN; RdvLog("płaszczyzna wyrównana"); }
                    break;
                }
                case RDV.B1:
                {
                    if (double.IsNaN(rdvBurnUT))
                    {
                        double T = me.period, tPe = now + me.timeToPe;
                        double tMin = rdvLaunchUT + 3 * T;              // 4. okrążenie
                        while (tPe < tMin) tPe += T;
                        double theta1 = PhaseAt(me, to, tPe);
                        // B3 jak w modelu okna: wstawienie + (n−1) obiegów + RdvAscT
                        rdvB3Plan = rdvLaunchUT + RdvAscT + (rdvB3Orbit - 1) * T + RdvAscT;
                        double dv, te, thU;
                        double rB1 = me.semiMajorAxis * (1 - me.eccentricity), rOpp = me.semiMajorAxis * (1 + me.eccentricity);
                        rdvRph = RdvPlanner.SolveAnyPhase(mu, theta1, rB1, rOpp, to.semiMajorAxis, rdvB3Plan - tPe, RdvStop, Math.Max(R + RdvRMinAlt, rB1 + 2000), out dv, out te, out thU);
                        if (!double.IsNaN(rdvRph)) theta1 = thU;
                        else theta1 = RdvPlanner.Wrap2Pi(theta1);
                        if (double.IsNaN(rdvRph))
                        {
                            // poza zakresem profilu – najbliższa granica, MechJeb dokończy (drożej)
                            rdvRph = theta1 > Math.PI / 2 ? R + RdvRMinAlt : to.semiMajorAxis - 5000;
                            RdvLog("faza " + (theta1 * 180 / Math.PI).ToString("F0", Inv) + "° poza profilem – orbita fazująca " + ((rdvRph - R) / 1000).ToString("F0", Inv) + " km, resztę zrobi MechJeb");
                        }
                        else RdvLog("plan: B1 za " + KSPUtil.PrintTimeCompact(tPe - now, false) + ", faza " + (theta1 * 180 / Math.PI).ToString("F1", Inv) + "°, orbita fazująca " + ((rdvRph - R) / 1000).ToString("F1", Inv) + " km, Δv ≈ " + dv.ToString("F0", Inv) + " m/s, przybycie za " + KSPUtil.PrintTimeCompact(te + tPe - now, false));
                        rdvBurnUT = tPe; rdvWarped = false;
                    }
                    double dvE = Math.Sqrt(mu * (2 / (me.semiMajorAxis * (1 - me.eccentricity)) - 2 / (me.semiMajorAxis * (1 - me.eccentricity) + rdvRph))) - Math.Sqrt(mu * (2 / (me.semiMajorAxis * (1 - me.eccentricity)) - 1 / me.semiMajorAxis));
                    if (!RdvWait(rdvBurnUT, dvE)) break;
                    RdvEnsureEngine(); rdv = RDV.B1Burn; RdvLog("B1 (4. okrążenie): apogeum → " + ((rdvRph - R) / 1000).ToString("F1", Inv) + " km");
                    break;
                }
                case RDV.B1Burn:
                    if (AxisBurn(me.ApA, rdvRph - R, rdvRph - R > me.ApA)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B2; rdvBurnUT = now + me.timeToAp; rdvWarped = false; RdvLog("B1 gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km"); }
                    break;
                case RDV.B2:
                {
                    double r = me.semiMajorAxis * (1 + me.eccentricity);
                    double dvE = Math.Sqrt(mu / r) - Math.Sqrt(mu * (2 / r - 1 / me.semiMajorAxis));
                    if (!RdvWait(rdvBurnUT, dvE)) break;
                    RdvEnsureEngine(); rdv = RDV.B2Burn; RdvLog("B2 (5. okrążenie): perygeum → " + ((rdvRph - R) / 1000).ToString("F1", Inv) + " km");
                    break;
                }
                case RDV.B2Burn:
                    if (AxisBurn(me.PeA, Math.Min(rdvRph - R, me.ApA - 300), true)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B3; rdvBurnUT = double.NaN; rdvWarped = false; RdvLog("B2 gotowe: orbita fazująca " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km"); }
                    break;
                case RDV.B3:
                {
                    if (double.IsNaN(rdvBurnUT))
                    {
                        // chwila B3: przy Hohmannie z bieżącej orbity przybycie RdvStop za celem; szukamy w ±1 obiegu od planu
                        double rT = to.semiMajorAxis, nT = 2 * Math.PI / RdvPlanner.Per(mu, rT);
                        Func<double, double> miss = t3 =>
                        {
                            double r1 = RelPos(me, t3).magnitude, d1, d2, th;
                            RdvPlanner.Hohmann(mu, r1, rT, out d1, out d2, out th);
                            double ph = PhaseAt(me, to, t3);
                            return ph + nT * th - Math.PI - RdvStop / rT;
                        };
                        double T = me.period, best = double.NaN;
                        double t0 = Math.Max(now + 300, rdvB3Plan - T), prevM = miss(t0);
                        for (double t = t0 + 30; t < rdvB3Plan + 2 * T; t += 30)
                        {
                            double c = miss(t);
                            if (prevM > 0 && c <= 0 && prevM - c < 1) { best = t; break; }
                            prevM = c;
                        }
                        if (double.IsNaN(best)) { best = Math.Max(now + 300, rdvB3Plan); RdvLog("B3: faza nie trafia w okno – Hohmann o planowanym czasie, resztę zrobi MechJeb"); }
                        rdvBurnUT = best; rdvWarped = false;
                        rdvAltT = RelPos(to, best + 0.5 * RdvPlanner.Per(mu, 0.5 * (RelPos(me, best).magnitude + rT))).magnitude - R;
                        RdvLog("B3 (" + rdvB3Orbit + ". okrążenie) za " + KSPUtil.PrintTimeCompact(best - now, false) + ": apogeum → " + (rdvAltT / 1000).ToString("F1", Inv) + " km");
                    }
                    double r = RelPos(me, rdvBurnUT).magnitude, d1e, d2e, the;
                    RdvPlanner.Hohmann(mu, r, R + rdvAltT, out d1e, out d2e, out the);
                    if (!RdvWait(rdvBurnUT, d1e)) break;
                    RdvEnsureEngine(); rdv = RDV.B3Burn;
                    break;
                }
                case RDV.B3Burn:
                    if (AxisBurn(me.ApA, rdvAltT, rdvAltT > me.ApA)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B4; rdvBurnUT = now + me.timeToAp; rdvWarped = false; RdvLog("B3 gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km"); }
                    break;
                case RDV.B4:
                {
                    double r = me.semiMajorAxis * (1 + me.eccentricity);
                    double dvE = Math.Sqrt(mu / r) - Math.Sqrt(mu * (2 / r - 1 / me.semiMajorAxis));
                    if (!RdvWait(rdvBurnUT, dvE)) break;
                    RdvEnsureEngine(); rdv = RDV.B4Burn; RdvLog("B4: wyrównanie orbity przy celu");
                    break;
                }
                case RDV.B4Burn:
                    if (AxisBurn(me.PeA, Math.Min(rdvAltT, me.ApA - 300), true))
                    {
                        FlightInputHandler.state.mainThrottle = 0f;
                        RdvLog("B4 gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km, do celu " + (dist / 1000).ToString("F1", Inv) + " km – zbliżanie automatyczne (MechJeb)");
                        RdvStartMj();
                    }
                    break;
                case RDV.Mj:
                {
                    Vector3d rv = vessel.obt_velocity - rdvTarget.obt_velocity;
                    RendezvousInfo = "zbliżanie: " + dist.ToString("F0", Inv) + " m, prędkość względna " + rv.magnitude.ToString("F2", Inv) + " m/s";
                    if (dist < 200 && rv.magnitude < 0.5) { RdvStopMj(); RdvStartDock(); }
                    break;
                }
                case RDV.Dock:
                {
                    if (RdvDocked()) { RdvStopDock(); rdv = RDV.Done; RdvLog("ZADOKOWANO z „" + rdvTarget.vesselName + "”"); break; }
                    RendezvousInfo = "dokowanie: " + dist.ToString("F0", Inv) + " m";
                    break;
                }
            }
        }

        static bool IsBurnRdv(RDV s) { return s == RDV.PlaneBurn || s == RDV.B1Burn || s == RDV.B2Burn || s == RDV.B3Burn || s == RDV.B4Burn; }

        // ------------------------------------------------------------------ MechJeb: zbliżanie i dokowanie
        object rdvMjMod, rdvDockMod;

        void RdvStartMj()
        {
            ReleaseAttitude();
            holdAttitude = false;
            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            FlightGlobals.fetch.SetVesselTarget(rdvTarget, true);
            object core = Core();
            rdvMjMod = core != null ? Module(core, "MechJebModuleRendezvousAutopilot") : null;
            if (rdvMjMod == null) { rdv = RDV.Failed; RdvLog("brak MechJeba (Rendezvous) – dokończ ręcznie"); return; }
            string err;
            MechJebPresets.SetMember(rdvMjMod, "desiredDistance", "100", out err);
            MechJebPresets.SetMember(rdvMjMod, "maxClosingSpeed", "5", out err);
            object pool = Users(rdvMjMod); if (pool != null) PoolCall(pool, "Add");
            rdv = RDV.Mj;
        }

        void RdvStopMj() { if (rdvMjMod != null) { object pool = Users(rdvMjMod); if (pool != null) PoolCall(pool, "Remove"); } rdvMjMod = null; }

        ModuleDockingNode FreePort(Vessel v, ModuleDockingNode match)
        {
            ModuleDockingNode best = null;
            foreach (Part p in v.parts)
                foreach (PartModule pm in p.Modules)
                {
                    ModuleDockingNode d = pm as ModuleDockingNode;
                    if (d == null || d.state == null || !d.state.StartsWith("Ready")) continue;
                    if (match != null && d.nodeType != match.nodeType) continue;
                    if (match != null && p.partInfo != null && match.part.partInfo != null && p.partInfo.name == match.part.partInfo.name) return d;
                    if (best == null) best = d;
                }
            return best;
        }

        void RdvStartDock()
        {
            rdvMyPort = FreePort(vessel, null);
            rdvTgtPort = rdvMyPort != null ? FreePort(rdvTarget, rdvMyPort) : null;
            if (rdvMyPort == null || rdvTgtPort == null)
            {
                rdv = RDV.Failed;
                RdvLog("brak pasujących wolnych portów (nasz: " + (rdvMyPort != null ? rdvMyPort.nodeType : "brak") + ") – dokuj ręcznie");
                return;
            }
            rdvMyPort.MakeReferenceTransform();
            FlightGlobals.fetch.SetVesselTarget(rdvTgtPort, true);
            object core = Core();
            rdvDockMod = core != null ? Module(core, "MechJebModuleDockingAutopilot") : null;
            if (rdvDockMod == null) { rdv = RDV.Failed; RdvLog("brak MechJeba (Docking) – dokuj ręcznie"); return; }
            string err;
            MechJebPresets.SetMember(rdvDockMod, "speedLimit", "0.3", out err);      // „Igła”: 0,1–0,35 m/s przy styku
            object pool = Users(rdvDockMod); if (pool != null) PoolCall(pool, "Add");
            rdv = RDV.Dock;
            RdvLog("dokowanie: port „" + rdvMyPort.part.partInfo.title + "” → „" + rdvTgtPort.part.partInfo.title + "”, do 0,3 m/s");
        }

        void RdvStopDock() { if (rdvDockMod != null) { object pool = Users(rdvDockMod); if (pool != null) PoolCall(pool, "Remove"); } rdvDockMod = null; }

        bool RdvDocked()
        {
            if (rdvMyPort == null) return false;
            string s = rdvMyPort.state ?? "";
            return s.StartsWith("Docked") || s.StartsWith("PreAttached") || (rdvTarget != null && rdvTarget.state == Vessel.State.DEAD) || (rdvTarget != null && rdvTarget == vessel);
        }

        /// <summary>Po zadokowaniu KSP łączy statki – aktywny może stać się innym obiektem Vessel. true = to nasze dokowanie.</summary>
        public bool RendezvousAdopt(Vessel active)
        {
            if (rdv != RDV.Dock || active == null || rdvMyPort == null || rdvMyPort.part == null || rdvMyPort.part.vessel != active) return false;
            vessel = active; RdvStopDock(); rdv = RDV.Done; RdvLog("ZADOKOWANO (statki połączone: „" + active.vesselName + "”)");
            return true;
        }

        void StopRendezvous()
        {
            if (rdv != RDV.None && rdv != RDV.Done && rdv != RDV.Failed) FlightInputHandler.state.mainThrottle = 0f;
            RdvStopMj(); RdvStopDock();
            rdv = RDV.None;
        }
    }
}
