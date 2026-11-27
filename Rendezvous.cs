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
        // Ustawienia spotkania – domyślne jak 7K-T; każde można nadpisać w pliku programu (FLIGHT_PROGRAM { klucz = wartość }),
        // pliki programów są czytane z dysku przy każdym „Start programu” – zmiana bez restartu KSP.
        double RdvAscT = 498.0, RdvInsDLat = 5.0781, RdvInsDLon = 22.6763;   // rdv.ascentTime, rdv.insertionDLat, rdv.insertionDLon
        double RdvStop = 10000;          // rdv.arriveBehindKm – przybycie za celem, dalej „Igła” (MechJeb)
        double RdvRMinAlt = 195000;      // rdv.phasingMinKm – orbita fazująca nie niżej
        double RdvPlaneMin = 0.1;        // rdv.planeMinDeg – mniejszej różnicy płaszczyzn nie korygujemy osobnym impulsem
        int RdvWindowDays = 30;
        double RdvNodeLate = 90;         // rdv.nodeLateSec – korekta płaszczyzny najpóźniej tyle [s] po węźle          // rdv.windowDays – jak daleko szukać okna startowego
        int RdvB1Revs = 3;               // rdv.b1AfterRevs – impuls 1 po tylu obiegach (3 → 4. okrążenie)
        int RdvB3Orbit1 = 17, RdvB3Orbit2 = 33;   // rdv.b3Orbit, rdv.b3OrbitSlow – okrążenie zbliżenia (profil 1- i 2-dobowy)
        double RdvEngineDelay = 3;       // rdv.engineDelay – [s] po oddzieleniu: stage silnika statku (KTDU + panele); < 0 = wcale
        string RdvMjDist = "100", RdvMjSpeed = "5";   // rdv.mechjebDistance [m], rdv.mechjebMaxSpeed [m/s]
        double RdvDockStart = 200, RdvDockStartSpeed = 0.5;   // rdv.dockStartDistance [m], rdv.dockStartSpeed [m/s]
        string RdvDockSpeed = "0.3";     // rdv.dockSpeed [m/s] – „Igła”: 0,1–0,35 m/s przy styku

        void LoadRendezvousSettings(ConfigNode n)
        {
            // wartości z Tun (domyślne → Ustawienia.cfg → plik programu)
            RdvAscT = Tun.D("rdv.ascentTime"); RdvInsDLat = Tun.D("rdv.insertionDLat"); RdvInsDLon = Tun.D("rdv.insertionDLon");
            RdvStop = Tun.D("rdv.arriveBehindKm") * 1000; RdvRMinAlt = Tun.D("rdv.phasingMinKm") * 1000; RdvPlaneMin = Tun.D("rdv.planeMinDeg");
            RdvWindowDays = Tun.I("rdv.windowDays"); RdvNodeLate = Tun.D("rdv.nodeLateSec"); RdvB1Revs = Tun.I("rdv.b1AfterRevs");
            RdvB3Orbit1 = Tun.I("rdv.b3Orbit"); RdvB3Orbit2 = Tun.I("rdv.b3OrbitSlow");
            RdvEngineDelay = Tun.D("rdv.engineDelay");
            RdvMjDist = Tun.S("rdv.mechjebDistance"); RdvMjSpeed = Tun.S("rdv.mechjebMaxSpeed");
            RdvDockStart = Tun.D("rdv.dockStartDistance"); RdvDockStartSpeed = Tun.D("rdv.dockStartSpeed"); RdvDockSpeed = Tun.S("rdv.dockSpeed");
        }

        enum RDV { None, Plane, PlaneBurn, B1, B1Burn, B2, B2Burn, B3, B3Burn, B4, B4Burn, Mj, DockWait, Dock, Done, Failed }
        RDV rdv = RDV.None;
        Vessel rdvTarget;
        int rdvB3Orbit = 17;
        double rdvRph, rdvAltT, rdvBurnUT = double.NaN, rdvB3Plan, rdvLaunchUT, rdvDvUsed, rdvNextLog;
        bool rdvWarped, rdvEngine;
        double rdvEngineAt = double.NaN;
        ModuleDockingNode rdvMyPort, rdvTgtPort;
        public string RendezvousInfo = "";
        public bool RendezvousBurning { get { return IsBurnRdv(rdv); } }
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
            foreach (int prof in new[] { RdvB3Orbit1, RdvB3Orbit2 })
                foreach (double Lc in cands)
                {
                    Vector3d pIns = inert(insLat, insLon, Lc + RdvAscT);
                    Vector3d pT = RelPos(to, Lc + RdvAscT).normalized;
                    double th0 = RdvPlanner.Wrap2Pi(RdvPlanner.SignedAngle(pIns, pT, n));
                    double th1 = th0 + nT * RdvB1Revs * TI - 2 * Math.PI * RdvB1Revs;
                    double dtB3 = (prof - 1) * TI + RdvAscT - RdvB1Revs * TI;
                    double d, te, thU;
                    double rph = RdvPlanner.SolveAnyPhase(mu, th1, rp, ra, rT, dtB3, RdvStop, R + RdvRMinAlt, out d, out te, out thU);
                    if (double.IsNaN(rph)) continue;
                    if (double.IsNaN(L) || Lc < L - 1)
                    {
                        // profil dwudobowy tylko, gdy jednodobowy jest dużo później (> 2 doby)
                        if (prof == RdvB3Orbit2 && !double.IsNaN(L) && L - Lc < 2 * 86400) continue;
                        L = Lc; b3 = prof; theta = th0; dv = d; tDock = te + RdvB1Revs * TI + RdvAscT;
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
            if (!FindRendezvousWindow(tgt, RdvWindowDays, out L, out b3, out th, out dv, out td))
            {
                FlightLogger.Event("Spotkanie: brak okna w " + RdvWindowDays + " dobach dla „" + tgt.vesselName + "”");
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "RSSBalancerRdvWindow", "Spotkanie na orbicie",
                    "W ciągu " + RdvWindowDays + " dób nie ma okna na spotkanie z „" + tgt.vesselName + "” (orbita celu ma ślad prawie powtarzalny – faza się nie przesuwa). Zmień wysokość celu o kilka km i spróbuj ponownie.", "OK", false, HighLogic.UISkin);
                launchHoldUT = double.NaN; Stop();
                return;
            }
            rdvB3Orbit = b3;
            double now = Planetarium.GetUniversalTime();
            CelestialBody b = vessel.mainBody;
            string msg = "Cel: „" + tgt.vesselName + "” " + (tgt.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (tgt.orbit.ApA / 1000).ToString("F0", Inv) + " km, " + tgt.orbit.inclination.ToString("F1", Inv) + "°.\n\n" +
                         "Najbliższe okno: start za " + KSPUtil.PrintTimeCompact(L - now, false) + " (ok. " + HourText(SolarTimeAt(b, vessel.latitude, vessel.longitude, L)) + " czasu słonecznego Bajkonuru).\n" +
                         "Profil " + (b3 == RdvB3Orbit1 ? "jednodobowy (jak 7K-T): manewry na 4. i 5. okrążeniu, zbliżenie na 17." : "dwudobowy: manewry na 4. i 5. okrążeniu, zbliżenie na 33.") + "\n" +
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
            rdvDvUsed = 0; rdvEngine = false; rdvDir = 0;
            rdvEngineAt = RdvEngineDelay < 0 ? double.NaN : Planetarium.GetUniversalTime() + RdvEngineDelay;   // KTDU i panele zaraz po oddzieleniu (w RendezvousTick)
            Orbit me = vessel.orbit, to = tgt.orbit;
            double relInc = Vector3d.Angle(Vector3d.Cross(RelPos(me, 0 + Planetarium.GetUniversalTime()), RelVel(me, Planetarium.GetUniversalTime())),
                                           Vector3d.Cross(RelPos(to, Planetarium.GetUniversalTime()), RelVel(to, Planetarium.GetUniversalTime())));
            rdv = relInc > RdvPlaneMin ? RDV.Plane : RDV.B1;      // drobną różnicę wyrówna MechJeb przy zbliżaniu
            rdvBurnUT = double.NaN; rdvWarped = false;
            string s = "Spotkanie z „" + tgt.vesselName + "”: profil " + (rdvB3Orbit == RdvB3Orbit1 ? "jednodobowy" : "dwudobowy") + ", różnica płaszczyzn " + relInc.ToString("F3", Inv) + "°";
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
                    if ((e.EngineIgnited && p.inverseStage >= vessel.currentStage) || p.inverseStage == vessel.currentStage - 1) t += e.maxThrust * e.thrustPercentage / 100.0;
                }
            return t > 0.1 ? t / vessel.totalMass : 0.5;
        }

        /// <summary>Silnik statku gotowy: jeśli nic nie pracuje, a następny stage to sam silnik (Sojuz: KTDU) – odpal go.</summary>
        void RdvEnsureEngine()
        {
            if (rdvEngine) return;
            rdvEngine = true;
            foreach (Part p in vessel.parts)
                // 1.45.1: liczą się tylko silniki już odpalone przez staging – silnik lądowania kapsuły (stage 0, RNLandingEngine)
                // bywa „uzbrojony” (EngineIgnited) i blokował odpalenie KTDU (lot 26.11 17:55)
                foreach (PartModule pm in p.Modules) { ModuleEngines e = pm as ModuleEngines; if (e != null && e.EngineIgnited && !e.flameout && p.inverseStage >= vessel.currentStage && !e.propellants.Exists(pr => pr.name == "SolidFuel")) return; }
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
            if (!rdvWarped && start - now > 2 * Tun.D("burn.warpTo"))
            {
                double nd; Vessel nv;
                if (NearestNeighbour(out nd, out nv) >= SepSafeDist && TimeWarp.CurrentRateIndex == 0) { ProgWarpTo(start - Tun.D("burn.warpTo")); rdvWarped = true; }
            }
            holdDir = Math.Sign(dvEst) >= 0 ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
            // przed impulsem zawsze bez przyspieszenia czasu – na szynach silnik nie pracuje
            // (lot 26.11 16:33: warp trwał jeszcze 650 s po węźle, korekta płaszczyzny poszła w złym miejscu)
            // (lot 26.11 16:33: warp trwał jeszcze 650 s po węźle). Przy dużym warpie – łagodne hamowanie stockowym WarpTo
            // (nagłe SetRate(0) przy ×100 000 psuło orbitę, loty 19.11), przy małym – od razu.
            if (TimeWarp.CurrentRateIndex > 0)
            {
                double lead = Tun.D("burn.warpStopLead");
                if (now >= start - lead) { if (TimeWarp.CurrentRate > 100 && start - now > 30) ProgWarpTo(Math.Max(now + 5, start - 30)); else TimeWarp.SetRate(0, true); }
                else if (TimeWarp.CurrentRate > 100 && (double.IsNaN(progWarpUT) || progWarpUT > start - lead)) ProgWarpTo(start - lead);   // ręczny warp za impuls – skróć
            }
            return now >= start;
        }

        int rdvDir;   // kierunek impulsu ustalony na jego początku (+1 prograde, −1 retrograde)

        /// <summary>
        /// Impuls w apsydzie: prędkość tak, żeby przeciwległa apsyda wypadła na rOpp (vis-viva).
        /// Kierunek ustalony raz na początku – 1.45.2: wcześniej „raises” liczony co klatkę i przy przestrzeleniu
        /// silnik odwracał kierunek tam i z powrotem (symulacja offline: B1 nigdy się nie kończył, statek by się kręcił).
        /// Przepustnica od pozostałej Δv, jak impuls wektorowy. true = skończony.
        /// </summary>
        bool RdvApsisBurn(double rOpp)
        {
            double mu = vessel.mainBody.gravParameter;
            double r = (vessel.CoMD - vessel.mainBody.position).magnitude, v = vessel.obt_velocity.magnitude;
            double vNeed = Math.Sqrt(Math.Max(0, mu * (2 / r - 2 / (r + rOpp))));
            double dvRem = vNeed - v;
            if (rdvDir == 0) rdvDir = dvRem >= 0 ? 1 : -1;
            holdDir = rdvDir > 0 ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
            if (dvRem * rdvDir <= Tun.D("rdv.burnTolMs")) { FlightInputHandler.state.mainThrottle = 0f; rdvDir = 0; return true; }
            EnsureBurnEngines();
            double a = AccelNow();
            float thr = (float)Math.Max(Tun.D("burn.minThrottle"), Math.Min(1, Math.Abs(dvRem) / (a * Tun.D("burn.vectorSoftK"))));
            FlightInputHandler.state.mainThrottle = attError < Tun.D("burn.attTol") ? thr : 0f;
            return false;
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
            // jak w prawdziwym Sojuzie: zaraz po oddzieleniu od Bloku I – KTDU gotowe, panele słoneczne rozłożone (stage 3)
            if (!rdvEngine && !double.IsNaN(rdvEngineAt) && Planetarium.GetUniversalTime() >= rdvEngineAt) { RdvEnsureEngine(); FlightInputHandler.state.mainThrottle = 0f; }
            CelestialBody b = vessel.mainBody; double mu = b.gravParameter, R = b.Radius;
            Orbit me = vessel.orbit, to = rdvTarget.orbit;
            double now = Planetarium.GetUniversalTime();
            double dist = (rdvTarget.CoMD - vessel.CoMD).magnitude;
            if (now > rdvNextLog && rdv < RDV.Mj)
            {
                rdvNextLog = now + 5;
                RendezvousInfo = RdvStateName(rdv) + (double.IsNaN(rdvBurnUT) || IsBurnRdv(rdv) ? "" : " za " + KSPUtil.PrintTimeCompact(Math.Max(0, rdvBurnUT - now), false)) + "; do celu " + (dist / 1000).ToString("F0", Inv) + " km, faza " + (PhaseAt(me, to, now) * 180 / Math.PI).ToString("F1", Inv) + "°, orbita " + (me.PeA / 1000).ToString("F0", Inv) + "×" + (me.ApA / 1000).ToString("F0", Inv) + " km";
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
                        RdvLog("korekta płaszczyzny w węźle za " + KSPUtil.PrintTimeCompact(rdvBurnUT - now, false));
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
                    // za daleko od węzła (impuls by tylko psuł nachylenie) – czekamy na następny węzeł
                    if (now > rdvBurnUT + RdvNodeLate)
                    {
                        FlightInputHandler.state.mainThrottle = 0f;
                        RdvLog("korekta płaszczyzny spóźniona o " + (now - rdvBurnUT).ToString("F0", Inv) + " s – czekam na następny węzeł");
                        rdv = RDV.Plane; rdvBurnUT = double.NaN; break;
                    }
                    // wektor w świecie: obt_velocity jest w świecie; normalna z orbit (xzy) też – spójne
                    if (VectorBurn(-n * vn)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B1; rdvBurnUT = double.NaN; RdvLog("płaszczyzna wyrównana"); }
                    break;
                }
                case RDV.B1:
                {
                    if (double.IsNaN(rdvBurnUT))
                    {
                        double T = me.period, tPe = now + me.timeToPe;
                        double tMin = rdvLaunchUT + RdvB1Revs * T;     // domyślnie 4. okrążenie
                        while (tPe < tMin) tPe += T;
                        double theta1 = PhaseAt(me, to, tPe);
                        // B3 jak w modelu okna: wstawienie + (n−1) obiegów + RdvAscT
                        rdvB3Plan = rdvLaunchUT + RdvAscT + (rdvB3Orbit - 1) * T + RdvAscT;
                        double dv, te, thU;
                        double rB1 = me.semiMajorAxis * (1 - me.eccentricity), rOpp = me.semiMajorAxis * (1 + me.eccentricity);
                        rdvRph = RdvPlanner.SolveAnyPhase(mu, theta1, rB1, rOpp, to.semiMajorAxis, rdvB3Plan - tPe, RdvStop, Math.Max(R + RdvRMinAlt, rB1 + 2000), out dv, out te, out thU);
                        if (double.IsNaN(rdvRph) && rdvB3Orbit != RdvB3Orbit2)
                        {
                            // faza nie pasuje do profilu jednodobowego – dwudobowy (jak część lotów 7K-T)
                            double planSlow = rdvLaunchUT + RdvAscT + (RdvB3Orbit2 - 1) * T + RdvAscT;
                            double rS = RdvPlanner.SolveAnyPhase(mu, theta1, rB1, rOpp, to.semiMajorAxis, planSlow - tPe, RdvStop, Math.Max(R + RdvRMinAlt, rB1 + 2000), out dv, out te, out thU);
                            if (!double.IsNaN(rS)) { rdvRph = rS; rdvB3Orbit = RdvB3Orbit2; rdvB3Plan = planSlow; RdvLog("faza nie pasuje do profilu jednodobowego – profil dwudobowy (zbliżenie na " + RdvB3Orbit2 + ". okrążeniu)"); }
                        }
                        if (!double.IsNaN(rdvRph)) theta1 = thU;
                        else theta1 = RdvPlanner.Wrap2Pi(theta1);
                        if (double.IsNaN(rdvRph))
                        {
                            // poza oboma profilami – nie zgadujemy (wcześniej: „granica + MechJeb”, co mogło zepsuć orbitę)
                            rdv = RDV.Failed; FlightInputHandler.state.mainThrottle = 0f;
                            RdvLog("faza " + (theta1 * 180 / Math.PI).ToString("F0", Inv) + "° poza profilami 1- i 2-dobowym – program zatrzymany, statek na orbicie bez zmian. Start w innym oknie albo zmień rdv.b3Orbit / rdv.phasingMinKm.");
                            break;
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
                    if (RdvApsisBurn(rdvRph)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B2; rdvBurnUT = now + me.timeToAp; rdvWarped = false; RdvLog("B1 gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km"); }
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
                    if (RdvApsisBurn(rdvRph)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B3; rdvBurnUT = double.NaN; rdvWarped = false; RdvLog("B2 gotowe: orbita fazująca " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km"); }
                    break;
                case RDV.B3:
                {
                    if (double.IsNaN(rdvBurnUT))
                    {
                        // chwila B3: przy Hohmannie z bieżącej orbity przybycie RdvStop za celem; szukamy w ±1 obiegu od planu
                        double rT = to.semiMajorAxis, nT = 2 * Math.PI / RdvPlanner.Per(mu, rT);
                        // 1.45.2: dokładnie – gdzie naprawdę będzie cel w chwili przybycia (jego orbita jest eliptyczna:
                        // średni ruch dawał w symulacji 12 km PRZED celem zamiast 10 km za nim)
                        Func<double, double> miss = t3 =>
                        {
                            Vector3d p3 = RelPos(me, t3);
                            double r1 = p3.magnitude;
                            double rA = RelPos(to, t3 + 0.5 * RdvPlanner.Per(mu, 0.5 * (r1 + rT))).magnitude;
                            double th = 0.5 * RdvPlanner.Per(mu, 0.5 * (r1 + rA));
                            Vector3d pT = RelPos(to, t3 + th), nT3 = Vector3d.Cross(RelPos(to, t3), RelVel(to, t3));
                            return RdvPlanner.SignedAngle(-p3, pT, nT3) - RdvStop / rA;
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
                    if (RdvApsisBurn(R + rdvAltT)) { FlightInputHandler.state.mainThrottle = 0f; rdv = RDV.B4; rdvBurnUT = now + me.timeToAp; rdvWarped = false; RdvLog("B3 gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km"); }
                    break;
                case RDV.B4:
                {
                    // 1.45.2: wyrównanie PRĘDKOŚCI z celem (nie samo zaokrąglenie orbity – cel ma orbitę eliptyczną,
                    // symulacja: po zaokrągleniu 15 m/s względnej, po wyrównaniu wektora 0,3 m/s)
                    Vector3d dv4 = RelVel(to, rdvBurnUT) - RelVel(me, rdvBurnUT);
                    bool go4 = RdvWait(rdvBurnUT, dv4.magnitude);
                    holdDir = dv4.normalized; holdMode = Hold.Inertial;
                    if (!go4) break;
                    RdvEnsureEngine(); mLastDv = double.MaxValue; rdv = RDV.B4Burn; RdvLog("B4: wyrównanie prędkości z celem, " + dv4.magnitude.ToString("F1", Inv) + " m/s");
                    break;
                }
                case RDV.B4Burn:
                    if (VectorBurn(RelVel(to, now) - RelVel(me, now)))      // prędkości z orbit (cel bywa poza zasięgiem fizyki)
                    {
                        FlightInputHandler.state.mainThrottle = 0f;
                        RdvLog("B4 gotowe: " + (me.PeA / 1000).ToString("F1", Inv) + "×" + (me.ApA / 1000).ToString("F1", Inv) + " km, do celu " + (dist / 1000).ToString("F1", Inv) + " km, prędkość względna " + (RelVel(to, now) - RelVel(me, now)).magnitude.ToString("F2", Inv) + " m/s – zbliżanie automatyczne (MechJeb)");
                        RdvStartMj();
                    }
                    break;
                case RDV.Mj:
                {
                    Vector3d rv = vessel.obt_velocity - rdvTarget.obt_velocity;
                    RendezvousInfo = "zbliżanie: " + dist.ToString("F0", Inv) + " m, prędkość względna " + rv.magnitude.ToString("F2", Inv) + " m/s";
                    // porty Burana (APAS) chowają się w luku – „Disabled”, dopóki się ich nie wysunie (ModuleAnimateGeneric)
                    RdvExtendPorts(vessel); if (rdvTarget.loaded) RdvExtendPorts(rdvTarget);
                    if (dist < RdvDockStart && rv.magnitude < RdvDockStartSpeed) { RdvStopMj(); rdv = RDV.DockWait; rdvDockWaitUT = now; }
                    break;
                }
                case RDV.DockWait:
                {
                    bool moving = RdvExtendPorts(vessel) | RdvExtendPorts(rdvTarget);
                    ModuleDockingNode mine = FreePort(vessel, null), theirs = mine != null ? FreePort(rdvTarget, mine) : null;
                    RendezvousInfo = "dokowanie: wysuwanie portów (" + (now - rdvDockWaitUT).ToString("F0", Inv) + " s)";
                    if (mine != null && theirs != null) { RdvStartDock(); break; }
                    if (!moving || now - rdvDockWaitUT > Tun.D("rdv.portWaitSec")) { RdvStartDock(); break; }   // RdvStartDock zgłosi brak portów
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

        static string RdvStateName(RDV s)
        {
            switch (s)
            {
                case RDV.Plane: return "korekta płaszczyzny";
                case RDV.B1: return "impuls 1 (4. okrążenie, perygeum)";
                case RDV.B2: return "impuls 2 (apogeum – orbita fazująca)";
                case RDV.B3: return "impuls 3 (" + "zbliżenie – Hohmann)";
                case RDV.B4: return "impuls 4 (wyrównanie przy celu)";
                default: return IsBurnRdv(s) ? "trwa impuls" : s.ToString();
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
            MechJebPresets.SetMember(rdvMjMod, "desiredDistance", RdvMjDist, out err);
            MechJebPresets.SetMember(rdvMjMod, "maxClosingSpeed", RdvMjSpeed, out err);
            foreach (var kv in Tun.Mj("rendezvous")) if (!MechJebPresets.SetMember(rdvMjMod, kv.Key, kv.Value, out err)) RdvLog("MechJeb Rendezvous: pole " + kv.Key + " nie przyjęte (" + err + ")");
            object pool = Users(rdvMjMod); if (pool != null) PoolCall(pool, "Add");
            rdv = RDV.Mj;
        }

        double rdvDockWaitUT;

        /// <summary>Wysuwa porty dokujące schowane animacją (stan „Disabled”). true = jakiś port jeszcze się wysuwa / był schowany.</summary>
        bool RdvExtendPorts(Vessel v)
        {
            if (v == null || !v.loaded) return false;
            bool any = false;
            // celowi otwieramy też luk/drzwi (np. Buran: drzwi ładowni „OPEN”, podstawa portu „OPEN DockPort”) – lista w rdv.openTargetAnims
            if (v == rdvTarget)
            {
                var names = new List<string>();
                foreach (string x in (blkAnimsOverride ?? Tun.S("rdv.openTargetAnims")).Split(',')) if (x.Trim().Length > 0) names.Add(x.Trim());
                foreach (Part p in v.parts)
                    foreach (PartModule pm in p.Modules)
                    {
                        ModuleAnimateGeneric a = pm as ModuleAnimateGeneric;
                        if (a == null || !names.Contains(a.animationName)) continue;
                        if (a.IsMoving()) { any = true; continue; }
                        if (a.animTime < 0.5f) { a.Toggle(); any = true; RdvLog("otwieram „" + a.animationName + "” na „" + p.partInfo.title + "”"); }
                    }
            }
            foreach (Part p in v.parts)
                foreach (PartModule pm in p.Modules)
                {
                    ModuleDockingNode d = pm as ModuleDockingNode;
                    if (d == null || d.deployAnimator == null || d.state == null) continue;
                    if (d.deployAnimator.IsMoving()) { any = true; continue; }
                    if (!d.state.StartsWith("Disabled")) continue;
                    any = true;
                    if (d.deployAnimator.animTime < 0.5f)
                    {
                        d.deployAnimator.Toggle();
                        RdvLog("wysuwam port „" + p.partInfo.title + "” (" + v.vesselName + ")");
                    }
                }
            return any;
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
            MechJebPresets.SetMember(rdvDockMod, "speedLimit", RdvDockSpeed, out err);
            foreach (var kv in Tun.Mj("docking")) if (!MechJebPresets.SetMember(rdvDockMod, kv.Key, kv.Value, out err)) RdvLog("MechJeb Docking: pole " + kv.Key + " nie przyjęte (" + err + ")");      // „Igła”: 0,1–0,35 m/s przy styku
            object pool = Users(rdvDockMod); if (pool != null) PoolCall(pool, "Add");
            rdv = RDV.Dock;
            RdvLog("dokowanie: port „" + rdvMyPort.part.partInfo.title + "” → „" + rdvTgtPort.part.partInfo.title + "”, do " + RdvDockSpeed + " m/s");
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
            if ((rdv != RDV.Dock && !blkDocking) || active == null || rdvMyPort == null || rdvMyPort.part == null || rdvMyPort.part.vessel != active) return false;
            vessel = active; RdvStopDock(); blkDocking = false; rdv = RDV.Done; RdvLog("ZADOKOWANO (statki połączone: „" + active.vesselName + "”)");
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
