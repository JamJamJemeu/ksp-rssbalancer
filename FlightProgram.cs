using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Programy lotu (cyklogramy) z plików GameData/RSSBalancer/PluginData/Programs/*.cfg.
    /// Program to lista kroków wykonywanych po kolei: krok czeka na swój warunek (when), potem jednorazowo
    /// wykonuje akcje (log, staging, ciąg grup silników, preset/autopilot MechJeba, trzymanie kursu, SAS/RCS).
    ///
    ///   FLIGHT_PROGRAM
    ///   {
    ///       name = …
    ///       description = …
    ///       STEP
    ///       {
    ///           name = Max-Q
    ///           when = q > 25 & met > 20        // warunki łączone „&”; zmienne niżej
    ///           delay = 0                        // s od aktywacji poprzedniego kroku, zanim sprawdzimy warunek
    ///           log = …                          // wpis do dziennika lotu
    ///           thrust.boosters = 80             // % ciągu grupy (boosters / core / upper / all)
    ///           throttle = 1                     // główna przepustnica 0–1
    ///           stage = 1                        // ile razy wcisnąć spację
    ///           stageTo = 2                      // stage'uj, dopóki obecny stage > 2
    ///           mechjebPreset = nazwa            // preset z zakładki MechJeb
    ///           mechjebAscent = on / off         // włącz / wyłącz Ascent Guidance MechJeba
    ///           attitude = 90 85 / off           // kurs i pochylenie [°] przez MechJeba (powierzchniowo), off = zwolnij
    ///           orient = retrograde | prograde | normal | antinormal | radialout | radialin | srfvel <AoA> <odchylenie> <przechylenie> | off
    ///           burn = on / off                  // pełny ciąg, gdy statek jest ustawiony (błąd < 5°)
    ///           warp = stop
    ///           sas = on / off,  rcs = on / off
    ///       }
    ///   }
    ///
    /// Zmienne w „when”: met [s], alt [km], speed [m/s pow.], vspeed, orbspeed, apo [km], peri [km], q [kPa],
    /// qfalling (1 po minięciu max-Q), g, stage, boosters / core / upper (ile silników grupy pracuje), always,
    /// timetoap, timetope [s], lat, lon, mach, aligned, siteangle [°], sitexrange [km] (miejsce „site = lat lon” w programie).
    /// </summary>
    public static class FlightPrograms
    {
        public class Step
        {
            public string name;
            public ConfigNode node;
            public double activatedAt = -1;
        }

        public class Program
        {
            public string name, file;
            public ConfigNode node;
            public readonly List<string> description = new List<string>();
            public readonly List<Step> steps = new List<Step>();
        }

        public static readonly List<Program> Programs = new List<Program>();

        public static string Folder
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"), "Programs"); }
        }

        public static void Reload()
        {
            Programs.Clear();
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (string f in Directory.GetFiles(Folder, "*.cfg"))
                {
                    ConfigNode root = ConfigNode.Load(f);
                    if (root == null) continue;
                    foreach (ConfigNode n in root.GetNodes("FLIGHT_PROGRAM"))
                    {
                        Program p = new Program { node = n, file = Path.GetFileName(f), name = n.GetValue("name") ?? Path.GetFileNameWithoutExtension(f) };
                        foreach (string d in n.GetValues("description")) p.description.Add(d);
                        int i = 0;
                        foreach (ConfigNode s in n.GetNodes("STEP"))
                            p.steps.Add(new Step { node = s, name = s.GetValue("name") ?? ("krok " + (++i)) });
                        Programs.Add(p);
                    }
                }
            }
            catch (Exception e) { Debug.Log("[RSSBalancer] FlightPrograms.Reload: " + e); }
        }
    }

    /// <summary>Wykonuje uzbrojony program lotu dla aktywnego statku.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightProgramRunner : MonoBehaviour
    {
        public static FlightProgramRunner Instance;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public FlightPrograms.Program Running;
        public int Current = -1;
        public string LastMessage = "";
        private Vessel vessel;
        private double lastActivation;
        private double qMax;
        private bool holdAttitude;
        private double holdHeading, holdPitch;
        private enum Hold { SurfaceNorth, Orbit, SrfVel, Inertial }
        private Hold holdMode = Hold.SurfaceNorth;
        private Vector3d holdDir = Vector3d.forward;
        private Quaternion holdRot = Quaternion.identity;
        private bool burnOn;
        private bool siteSet;
        private double siteLat, siteLon, siteLead = 3900;
        private double attError = 180;

        // --- podejście i lądowanie (MechJeb Aircraft Autopilot) ---
        private bool approachOn, rwySet, gearDone, landedDone;
        private double rwyLatA, rwyLonA, rwyLatB, rwyLonB, rwyAlt;
        private Vector3d thrPos, rwyDir;     // próg pasa (świat, względem ciała) i kierunek lądowania (jednostkowy)
        private double rwyHeading;
        private string approachPhase = "";
        public string LandingVerdict = "";
        public string ApproachInfo { get { return approachOn ? approachPhase : patternOn ? patternPhase : ""; } }
        private List<EngineGroup> groups;
        private SoyuzTemplate.Result id;
        private float nextGroups;

        private void Awake() { Instance = this; }
        // ------------------------------------------------------------------ manewr orbitalny „orbittarget = pe ap inc”
        // Ustawia orbitę o zadanym perygeum, apogeum [km] i nachyleniu [°] (węzeł dowolny) z dowolnej orbity zamkniętej.
        // Liczy dwa warianty i wybiera tańszy w Δv:
        //  A) bezpośredni: zmiana płaszczyzny w węźle (przejście przez równik) + Hohmann (apogeum w perygeum, perygeum w apogeum),
        //     przy podnoszeniu orbity najpierw kształt (płaszczyzna na wyższej orbicie = taniej), przy obniżaniu odwrotnie;
        //  B) dwueliptyczny: w węźle podniesienie apogeum na R (apogeum wypada w drugim węźle), tam – przy małej prędkości –
        //     zmiana płaszczyzny razem z ustawieniem perygeum, potem w perygeum obniżenie apogeum. Przy dużej zmianie
        //     nachylenia (np. 0° → 51,6°) wyraźnie taniej, ale trwa dłużej (R dobierane tak, by całość < 3 doby).
        // Impulsy wektorowe w pętli zamkniętej: co klatkę liczymy prędkość docelową w bieżącym punkcie, nos na Δv,
        // przepustnica maleje przy końcówce, koniec przy |Δv| < 0,3 m/s. Bez cheatu sprawdzamy, czy starczy paliwa.
        private enum MPh { None, WaitNodePlane, BurnPlane, WaitPeA, BurnA, WaitApP, BurnP, WaitNodeRaise, BurnRaise, WaitApoCombo, BurnCombo, WaitPeLower, BurnLower, WaitApRaiseAp, BurnApRaiseAp, Done }
        private MPh mph = MPh.None;
        private string pendingProgram;
        private readonly List<MPh> mplan = new List<MPh>();
        private int mIdx;
        private double mComboRa, mPe, mAp, mInc, mRhi, mBurnUT = double.NaN, mLastDv = double.MaxValue, mWarpedFor = double.NaN;
        private bool mBi;
        public string ManeuverInfo = "";
        public bool ManeuverDone { get { return mph == MPh.Done; } }

        private double Mu { get { return vessel.mainBody.gravParameter; } }
        private double AccelNow() { double t = EngineThrustKN(); return t > 1 ? t / vessel.totalMass : 2.0; }
        private static double IncDv(double v, double di) { return 2 * v * Math.Sin(Math.Abs(di) * Math.PI / 360); }

        private void StartManeuver(double peKm, double apKm, double incDeg)
        {
            CelestialBody b = vessel.mainBody; Orbit o = vessel.orbit;
            if (apKm < peKm) { double t = apKm; apKm = peKm; peKm = t; }
            mPe = peKm; mAp = apKm; mInc = incDeg;
            double R = b.Radius, r0 = o.semiMajorAxis, rt = R + 0.5 * (peKm + apKm) * 1000;
            double di = incDeg - o.inclination;
            // Δv wariantu A (przybliżenie orbit kołowych)
            double vt = Math.Sqrt(Mu / rt), v0 = Math.Sqrt(Mu / r0);
            double at = 0.5 * (r0 + rt);
            double hoh = Math.Abs(Math.Sqrt(Mu * (2 / r0 - 1 / at)) - v0) + Math.Abs(vt - Math.Sqrt(Mu * (2 / rt - 1 / at)));
            double planeA = IncDv(Math.Min(v0, vt), di);
            double dvA = hoh + planeA;
            // wariant B: najlepsze R przy limicie czasu ~3 doby
            double dvB = double.MaxValue, bestR = 0;
            if (Math.Abs(di) > 5)
                for (double Rhi = Math.Max(r0, rt) * 1.5; Rhi < 400e6; Rhi *= 1.15)
                {
                    double a1 = 0.5 * (r0 + Rhi), a2 = 0.5 * (rt + Rhi);
                    double period = Math.PI * (Math.Sqrt(a1 * a1 * a1 / Mu) + Math.Sqrt(a2 * a2 * a2 / Mu));
                    if (period > 3 * 86400) break;
                    if (Rhi > b.sphereOfInfluence * 0.5) break;
                    double dv1 = Math.Sqrt(Mu * (2 / r0 - 1 / a1)) - v0;
                    double va1 = Math.Sqrt(Mu * (2 / Rhi - 1 / a1)), va2 = Math.Sqrt(Mu * (2 / Rhi - 1 / a2));
                    double dv2 = Math.Sqrt(va1 * va1 + va2 * va2 - 2 * va1 * va2 * Math.Cos(di * Math.PI / 180));
                    double dv3 = Math.Sqrt(Mu * (2 / rt - 1 / a2)) - vt;
                    double tot = dv1 + dv2 + dv3;
                    if (tot < dvB) { dvB = tot; bestR = Rhi; }
                }
            mBi = dvB < dvA * 0.97;
            mRhi = bestR;
            double need = mBi ? dvB : dvA;
            mplan.Clear();
            // Tor suborbitalny (perygeum w atmosferze, np. po Energii −11 × 154 km): jak w 1K1 – ОДУ-1 w apogeum
            // podnosi drugą stronę orbity do apogeum docelowego, ОДУ-2 w nowym apogeum podnosi perygeum.
            double atmTop = b.atmosphere ? b.atmosphereDepth : 0;
            if (o.PeA < atmTop && o.ApA > atmTop)
            {
                mBi = false;
                if (Math.Abs(di) > 0.05) mplan.Add(MPh.WaitNodePlane);
                mplan.Add(MPh.WaitApRaiseAp); mplan.Add(MPh.WaitApP); mplan.Add(MPh.Done);
                mIdx = 0; mph = mplan[0]; mBurnUT = double.NaN; mWarpedFor = double.NaN;
                ManeuverInfo = "z toru suborbitalnego: ОДУ-1 w apogeum (apogeum → " + apKm.ToString("F0", Inv) + " km), ОДУ-2 w nowym apogeum (perygeum → " + peKm.ToString("F0", Inv) + " km)";
                FlightLogger.Event("Manewr orbitalny: cel " + peKm.ToString("F0", Inv) + "×" + apKm.ToString("F0", Inv) + " km – " + ManeuverInfo);
                return;
            }
            if (mBi) { mplan.Add(MPh.WaitNodeRaise); mplan.Add(MPh.WaitApoCombo); mplan.Add(MPh.WaitPeLower); }
            else
            {
                bool plane = Math.Abs(di) > 0.05;
                bool higher = rt > r0;
                if (plane && !higher) mplan.Add(MPh.WaitNodePlane);
                mplan.Add(MPh.WaitPeA); mplan.Add(MPh.WaitApP);
                if (plane && higher) mplan.Add(MPh.WaitNodePlane);
            }
            mplan.Add(MPh.Done);
            mIdx = 0; mph = mplan[0]; mBurnUT = double.NaN; mWarpedFor = double.NaN;
            double have = 0;
            try { VesselDeltaV vd = vessel.VesselDeltaV; if (vd != null) have = vd.TotalDeltaVVac; } catch { }
            bool cheat = CheatOptions.InfinitePropellant;
            ManeuverInfo = (mBi ? "dwueliptyczny przez " + ((mRhi - R) / 1000).ToString("F0", Inv) + " km" : "bezpośredni") +
                           ", Δv ≈ " + need.ToString("F0", Inv) + " m/s (A " + dvA.ToString("F0", Inv) + (dvB < 1e9 ? ", B " + dvB.ToString("F0", Inv) : "") + "), zmiana nachylenia " + di.ToString("F1", Inv) + "°" +
                           (cheat ? ", paliwo nieskończone (cheat)" : ", w zbiornikach " + have.ToString("F0", Inv) + " m/s");
            FlightLogger.Event("Manewr orbitalny: cel " + peKm.ToString("F0", Inv) + "×" + apKm.ToString("F0", Inv) + " km, " + incDeg.ToString("F1", Inv) + "° – " + ManeuverInfo);
            if (!cheat && have > 1 && need > have * 0.98)
            {
                ManeuverInfo = "ZA MAŁO PALIWA: potrzeba ~" + need.ToString("F0", Inv) + " m/s, jest " + have.ToString("F0", Inv) + " m/s (Buran ma tylko ODU; większą zmianę nachylenia zrób z cheatem paliwa albo HyperEdit)";
                FlightLogger.Event("Manewr orbitalny: " + ManeuverInfo);
                mph = MPh.Done;
            }
        }

        private void NextManeuverPhase()
        {
            FlightInputHandler.state.mainThrottle = 0f;
            mIdx++; mph = mIdx < mplan.Count ? mplan[mIdx] : MPh.Done; mBurnUT = double.NaN; mWarpedFor = double.NaN; mLastDv = double.MaxValue;
            if (mph == MPh.Done) { FlightLogger.Event("Manewr orbitalny: gotowe – " + (vessel.orbit.PeA / 1000).ToString("F0", Inv) + "×" + (vessel.orbit.ApA / 1000).ToString("F0", Inv) + " km, nachylenie " + vessel.orbit.inclination.ToString("F2", Inv) + "°"); holdDir = Vector3d.back; holdMode = Hold.Orbit; }
        }

        /// <summary>Najbliższe przejście przez równik (węzeł) po chwili t0.</summary>
        private double NextNodeUT(double t0)
        {
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            Vector3d ax = b.transform.up;
            double step = Math.Max(5, o.period / 720), prev = Vector3d.Dot(o.getPositionAtUT(t0) - b.position, ax);
            for (double t = t0 + step; t < t0 + o.period + step; t += step)
            {
                double z = Vector3d.Dot(o.getPositionAtUT(t) - b.position, ax);
                if (Math.Sign(z) != Math.Sign(prev))
                {
                    double lo = t - step, hi = t;
                    for (int i = 0; i < 30; i++) { double m = 0.5 * (lo + hi); if (Math.Sign(Vector3d.Dot(o.getPositionAtUT(m) - b.position, ax)) == Math.Sign(prev)) lo = m; else hi = m; }
                    return hi;
                }
                prev = z;
            }
            return t0;   // orbita równikowa – każdy punkt jest węzłem
        }

        /// <summary>Prędkość docelowa (świat) w bieżącym punkcie: pozioma składowa o długości vh pod kursem dla nachylenia mInc, pionowa vz.</summary>
        private Vector3d TargetVel(double vh, double vz)
        {
            CelestialBody b = vessel.mainBody;
            Vector3d up = (vessel.CoMD - b.position).normalized, ax = b.transform.up;
            Vector3d north = Vector3d.Exclude(up, ax).normalized, east = Vector3d.Cross(up, north).normalized;
            double lat = Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(up, ax))));
            double s = Math.Cos(mInc * Math.PI / 180) / Math.Max(1e-6, Math.Cos(lat));
            double psi = Math.Asin(Math.Max(-1, Math.Min(1, s)));            // kurs (od północy) dla ruchu „na wschód”
            Vector3d v = vessel.obt_velocity; Vector3d vhNow = v - up * Vector3d.Dot(v, up);
            double curHdg = Math.Atan2(Vector3d.Dot(vhNow, east), Vector3d.Dot(vhNow, north));
            double h1 = psi, h2 = Math.PI - psi;                               // wznoszący / zstępujący
            if (mInc > 90) { h1 = -psi; h2 = Math.PI + psi; }
            double d1 = Math.Abs(Math.IEEERemainder(h1 - curHdg, 2 * Math.PI)), d2 = Math.Abs(Math.IEEERemainder(h2 - curHdg, 2 * Math.PI));
            double h = d1 <= d2 ? h1 : h2;
            return (north * Math.Cos(h) + east * Math.Sin(h)) * vh + up * vz;
        }

        private void ManeuverWarp(double burnStart)
        {
            double now = Planetarium.GetUniversalTime();
            if (mWarpedFor == burnStart) return;
            mWarpedFor = burnStart;
            if (burnStart - now > 120 && TimeWarp.CurrentRateIndex == 0) TimeWarp.fetch.WarpTo(burnStart - 60);
        }

        /// <summary>Impuls wektorowy: nos na Δv, przepustnica proporcjonalna przy końcówce. true = skończony.</summary>
        private bool VectorBurn(Vector3d dv)
        {
            double m = dv.magnitude;
            holdDir = dv.normalized; holdMode = Hold.Inertial; holdAttitude = true;
            if (m < 0.3 || (m > mLastDv + 0.05 && mLastDv < 3)) return true;
            mLastDv = Math.Min(mLastDv, m);
            double a = AccelNow();
            float thr = (float)Math.Max(0.02, Math.Min(1, m / (a * 1.5)));
            FlightInputHandler.state.mainThrottle = attError < 3 ? thr : 0f;
            return false;
        }

        /// <summary>Impuls wzdłuż prędkości (prograde/retro) aż wartość dojdzie do celu. true = skończony.</summary>
        private bool AxisBurn(double cur, double target, bool raises)
        {
            holdDir = raises ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
            double err = (target - cur) * (raises ? 1 : -1);
            if (err <= 0) return true;
            double a = AccelNow();
            FlightInputHandler.state.mainThrottle = attError < 3 ? (float)Math.Max(0.02, Math.Min(1, err / 5000.0)) : 0f;   // err w m
            return false;
        }

        private void Maneuver()
        {
            if (mph == MPh.None || mph == MPh.Done || vessel == null) return;
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            double now = Planetarium.GetUniversalTime(), R = b.Radius;
            double a = AccelNow();
            Vector3d up = (vessel.CoMD - b.position).normalized;
            Vector3d v = vessel.obt_velocity;
            double vz = Vector3d.Dot(v, up), vhm = (v - up * vz).magnitude;
            bool circ = o.eccentricity < 0.002;
            switch (mph)
            {
                case MPh.WaitNodePlane:
                case MPh.WaitNodeRaise:
                {
                    if (double.IsNaN(mBurnUT))
                    {
                        double tn = NextNodeUT(now + 30);
                        double dv = mph == MPh.WaitNodePlane ? IncDv(o.orbitalSpeed, mInc - o.inclination)
                                  : Math.Sqrt(Mu * (2 / o.radius - 2 / (o.radius + mRhi))) - o.orbitalSpeed;
                        mBurnUT = tn - 0.5 * dv / a;
                        if (mBurnUT < now + 20) mBurnUT = NextNodeUT(tn + 60) - 0.5 * dv / a;
                        ManeuverInfo = (mph == MPh.WaitNodePlane ? "zmiana nachylenia" : "podniesienie apogeum na " + ((mRhi - R) / 1000).ToString("F0", Inv) + " km") + " w węźle, Δv " + dv.ToString("F0", Inv) + " m/s";
                    }
                    holdDir = mph == MPh.WaitNodeRaise ? Vector3d.forward : holdDir; if (mph == MPh.WaitNodeRaise) holdMode = Hold.Orbit; holdAttitude = true;
                    if (mph == MPh.WaitNodePlane) VectorBurnAim(TargetVel(vhm, vz) - v);
                    ManeuverWarp(mBurnUT);
                    if (now >= mBurnUT) { mph = mph == MPh.WaitNodePlane ? MPh.BurnPlane : MPh.BurnRaise; mLastDv = double.MaxValue; FlightLogger.Event("Manewr orbitalny: start – " + ManeuverInfo); }
                    break;
                }
                case MPh.BurnPlane:
                    if (VectorBurn(TargetVel(vhm, vz) - v)) NextManeuverPhase();
                    break;
                case MPh.BurnRaise:
                    if (AxisBurn(o.ApR, mRhi, true)) NextManeuverPhase();
                    break;
                case MPh.WaitPeA:
                {
                    double tgt = R + mAp * 1000;
                    bool raise = o.ApR < tgt;
                    if (double.IsNaN(mBurnUT))
                    {
                        double dv = Math.Abs(Math.Sqrt(Mu * (2 / o.PeR - 2 / (o.PeR + tgt))) - Math.Sqrt(Mu * (2 / o.PeR - 1 / o.semiMajorAxis)));
                        mBurnUT = (circ ? now + 60 : now + o.timeToPe) - 0.5 * dv / a;
                        if (mBurnUT < now + 20) mBurnUT = now + 20;
                        ManeuverInfo = (raise ? "podniesienie" : "obniżenie") + " apogeum do " + mAp.ToString("F0", Inv) + " km, Δv " + dv.ToString("F0", Inv) + " m/s";
                    }
                    if (Math.Abs(o.ApR - tgt) < 300) { NextManeuverPhase(); break; }
                    holdDir = raise ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
                    ManeuverWarp(mBurnUT);
                    if (now >= mBurnUT) { mph = MPh.BurnA; FlightLogger.Event("Manewr orbitalny: start – " + ManeuverInfo); }
                    break;
                }
                case MPh.BurnA:
                {
                    double tgt = R + mAp * 1000;
                    bool raise = holdDir == Vector3d.forward;
                    if (AxisBurn(o.ApR, tgt, raise)) NextManeuverPhase();
                    break;
                }
                case MPh.WaitApP:
                case MPh.WaitPeLower:
                {
                    bool atAp = mph == MPh.WaitApP;
                    double tgt = atAp ? R + mPe * 1000 : R + mAp * 1000;
                    double curV = atAp ? o.PeR : o.ApR;
                    bool raise = curV < tgt;
                    if (double.IsNaN(mBurnUT))
                    {
                        double rr = atAp ? o.ApR : o.PeR;
                        double dv = Math.Abs(Math.Sqrt(Mu * (2 / rr - 2 / (rr + tgt))) - Math.Sqrt(Mu * (2 / rr - 1 / o.semiMajorAxis)));
                        double tt = atAp ? o.timeToAp : o.timeToPe;
                        mBurnUT = (circ ? now + 60 : now + tt) - 0.5 * dv / a;
                        if (mBurnUT < now + 20) mBurnUT = now + 20;
                        ManeuverInfo = (raise ? "podniesienie " : "obniżenie ") + (atAp ? "perygeum do " + mPe.ToString("F0", Inv) : "apogeum do " + mAp.ToString("F0", Inv)) + " km, Δv " + dv.ToString("F0", Inv) + " m/s";
                    }
                    if (Math.Abs(curV - tgt) < 300) { NextManeuverPhase(); break; }
                    holdDir = raise ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
                    ManeuverWarp(mBurnUT);
                    if (now >= mBurnUT) { mph = atAp ? MPh.BurnP : MPh.BurnLower; FlightLogger.Event("Manewr orbitalny: start – " + ManeuverInfo); }
                    break;
                }
                case MPh.BurnP:
                case MPh.BurnLower:
                {
                    bool atAp = mph == MPh.BurnP;
                    double tgt = atAp ? R + mPe * 1000 : R + mAp * 1000;
                    double curV = atAp ? o.PeR : o.ApR;
                    if (AxisBurn(curV, tgt, holdDir == Vector3d.forward)) NextManeuverPhase();
                    break;
                }
                case MPh.WaitApRaiseAp:
                {
                    double tgt = R + mAp * 1000;
                    if (double.IsNaN(mBurnUT))
                    {
                        double ra = o.ApR;
                        double dv = Math.Sqrt(Mu * (2 / ra - 2 / (ra + tgt))) - Math.Sqrt(Mu * (2 / ra - 1 / o.semiMajorAxis));
                        mBurnUT = now + o.timeToAp - 0.5 * dv / a;
                        if (mBurnUT < now + 5) mBurnUT = now + 5;
                        ManeuverInfo = "ОДУ-1 w apogeum: apogeum do " + mAp.ToString("F0", Inv) + " km, Δv " + dv.ToString("F0", Inv) + " m/s";
                    }
                    holdDir = Vector3d.forward; holdMode = Hold.Orbit; holdAttitude = true;
                    ManeuverWarp(mBurnUT);
                    if (now >= mBurnUT) { mph = MPh.BurnApRaiseAp; FlightLogger.Event("Manewr orbitalny: start – " + ManeuverInfo); }
                    break;
                }
                case MPh.BurnApRaiseAp:
                    // ApR rośnie monotonicznie (najpierw = punkt impulsu, po przejściu przez orbitę kołową – druga strona)
                    if (AxisBurn(o.ApR, R + mAp * 1000, true)) NextManeuverPhase();
                    break;
                case MPh.WaitApoCombo:
                {
                    // w apogeum (wysoko, wolno): płaszczyzna + perygeum razem
                    double ra = o.ApR, rp = R + mPe * 1000;
                    double vNew = Math.Sqrt(Mu * 2 * rp / (ra * (ra + rp))), vOld = Math.Sqrt(Mu * (2 / ra - 1 / o.semiMajorAxis));
                    if (double.IsNaN(mBurnUT))
                    {
                        double dv = Math.Sqrt(vOld * vOld + vNew * vNew - 2 * vOld * vNew * Math.Cos((mInc - o.inclination) * Math.PI / 180));
                        mBurnUT = now + o.timeToAp - 0.5 * dv / a;
                        if (mBurnUT < now + 20) mBurnUT = now + 20;
                        ManeuverInfo = "w apogeum " + ((ra - R) / 1000).ToString("F0", Inv) + " km: nachylenie " + mInc.ToString("F1", Inv) + "° i perygeum " + mPe.ToString("F0", Inv) + " km, Δv " + dv.ToString("F0", Inv) + " m/s";
                    }
                    VectorBurnAim(TargetVel(vNew, 0) - v);
                    ManeuverWarp(mBurnUT);
                    if (now >= mBurnUT) { mph = MPh.BurnCombo; mLastDv = double.MaxValue; mComboRa = o.ApR; FlightLogger.Event("Manewr orbitalny: start – " + ManeuverInfo); }
                    break;
                }
                case MPh.BurnCombo:
                {
                    // prędkość docelowa w bieżącym punkcie dla orbity r × rp o zadanym nachyleniu (energia i moment pędu)
                    double r = o.radius, rp = R + mPe * 1000, ra = Math.Max(mComboRa, r);
                    double aT = 0.5 * (ra + rp);
                    double spd = Math.Sqrt(Math.Max(0, Mu * (2 / r - 1 / aT)));
                    double hT = Math.Sqrt(Mu * 2 * ra * rp / (ra + rp));
                    double vht = Math.Min(spd, hT / r), vzt = Math.Sign(vz) * Math.Sqrt(Math.Max(0, spd * spd - vht * vht));
                    if (VectorBurn(TargetVel(vht, vzt) - v)) NextManeuverPhase();
                    break;
                }
            }
        }

        private void VectorBurnAim(Vector3d dv)
        {
            if (dv.magnitude > 0.1) { holdDir = dv.normalized; holdMode = Hold.Inertial; holdAttitude = true; }
        }

        /// <summary>„askProgram = nazwa” – okno: kontynuować lot programem „nazwa” czy zostać na orbicie.</summary>
        private void AskProgram(string name)
        {
            string msg = "Buran jest na orbicie roboczej " + (vessel.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (vessel.orbit.ApA / 1000).ToString("F0", Inv) +
                         " km, nachylenie " + vessel.orbit.inclination.ToString("F1", Inv) + "°.\n\nKontynuować lot jak 1K1 („" + name + "”: impuls hamujący i lądowanie), czy zostać na orbicie?\n" +
                         "(Powrót można też uruchomić później w zakładce Program.)";
            FlightLogger.Event("Program lotu: pytanie – kontynuować „" + name + "” czy zostać na orbicie");
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RSSBalancerAskProgram", msg, "Orbita robocza", HighLogic.UISkin,
                    new DialogGUIButton("Kontynuuj 1K1 – powrót", () =>
                    {
                        FlightPrograms.Program np = FlightPrograms.Programs.Find(x => x.name == name);
                        if (np == null) { LastMessage = "Brak programu „" + name + "”."; return; }
                        FlightLogger.Event("Program lotu: wybrano kontynuację – „" + name + "”");
                        Begin(np);
                    }),
                    new DialogGUIButton("Zostań na orbicie", () => { FlightLogger.Event("Program lotu: wybrano pozostanie na orbicie"); })),
                false, HighLogic.UISkin);
        }

        // ---- wybór lotniska: czy obecna orbita przelatuje nad nim? jeśli nie – pytanie o zmianę orbity
        public string SiteName = "";

        /// <summary>Orbita pod lotnisko: zostaje perygeum/apogeum (albo 251×263, gdy niższe niż 200 km), nachylenie co najmniej szerokość lotniska + 1°.</summary>
        public bool OrbitForAirport(Airports.Airport a, out double pe, out double apo, out double inc)
        {
            pe = 251; apo = 263; inc = 51.6;
            if (a == null) return false;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null && v.orbit != null && v.orbit.PeA > 200000 && v.orbit.eccentricity < 0.05) { pe = v.orbit.PeA / 1000; apo = v.orbit.ApA / 1000; }
            double cur = v != null && v.orbit != null ? v.orbit.inclination : 51.6;
            inc = Math.Max(cur, Math.Abs(a.Lat) + 1.0);
            return true;
        }

        /// <summary>Wołane po wyborze lotniska w zakładce Program. Zwraca opis; przy niedostępnym lotnisku pokazuje okno z pytaniem.</summary>
        public string AirportChosen(Airports.Airport a)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (a == null) return "";
            if (v == null || v.orbit == null || v.situation != Vessel.Situations.ORBITING) return "Lotnisko: " + a.name + " (" + a.Lat.ToString("F2") + "°, " + a.Lon.ToString("F2") + "°).";
            double R = v.mainBody.Radius / 1000, xr = 200;
            double inc = v.orbit.inclination > 90 ? 180 - v.orbit.inclination : v.orbit.inclination;
            double reach = inc + xr / R * 180 / Math.PI;
            if (reach >= Math.Abs(a.Lat)) return "Lotnisko: " + a.name + " – obecna orbita (nachylenie " + v.orbit.inclination.ToString("F1") + "°) przelatuje w zasięgu. Uruchom „Buran – powrót na wybrane lotnisko”.";
            double pe, apo, ti;
            OrbitForAirport(a, out pe, out apo, out ti);
            double dv = 2 * v.orbit.orbitalSpeed * Math.Sin(Math.Abs(ti - v.orbit.inclination) * Math.PI / 360);
            string msg = "Lotnisko „" + a.name + "” leży na szerokości " + Math.Abs(a.Lat).ToString("F1") + "°, a orbita ma nachylenie " + v.orbit.inclination.ToString("F1") +
                         "° – nigdy nie przeleci w jego zasięgu.\n\nUstawić orbitę " + pe.ToString("F0") + " × " + apo.ToString("F0") + " km, nachylenie " + ti.ToString("F1") +
                         "°?\n(zmiana nachylenia ≈ " + dv.ToString("F0") + " m/s" + (dv > 700 ? " – więcej niż paliwa w Buranie; program sam wybierze tańszy wariant, ale zwykle potrzebny cheat paliwa" : "") + ")";
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RSSBalancerOrbitForAirport", msg, "Orbita pod lotnisko", HighLogic.UISkin,
                    new DialogGUIButton("Tak – ustaw orbitę", () =>
                    {
                        FlightPrograms.Program op = FlightPrograms.Programs.Find(x => x.node != null && x.node.HasValue("orbitForAirport"));
                        if (op == null) { LastMessage = "Brak programu „Buran – orbita pod wybrane lotnisko” w folderze Programs."; return; }
                        Begin(op);
                    }),
                    new DialogGUIButton("Nie", () => { })),
                false, HighLogic.UISkin);
            return "Lotnisko: " + a.name + " – poza zasięgiem obecnej orbity (pytanie o zmianę orbity).";
        }

        // ---- dane dla wyświetlacza Burana (BuranCrt.cs)
        public bool SiteKnown { get { return siteSet; } }
        public double SiteLatD { get { return siteLat; } }
        public double SiteLonD { get { return siteLon; } }
        public bool RunwayKnown { get { return rwySet; } }
        public double RunwayAlt { get { return rwyAlt; } }
        public bool ApproachActive { get { return approachOn; } }
        public string ApproachPhase { get { return approachPhase; } }
        public bool EntryActive { get { return entryGuide && vessel != null && vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth; } }
        public double EntryRangeM { get { return hudRMax; } }
        public double EntryCos { get { return hudCos; } }
        public double AoaCommand { get { return entryGuide ? gAoaCmd : double.NaN; } }
        public double BankCommand { get { return entryGuide ? bankCmd : double.NaN; } }
        public double StallEas { get { return vessel != null ? 70 * Math.Sqrt(Math.Max(10, vessel.totalMass) / 75.0) : 70; } }

        /// <summary>Położenie względem pasa: d – odległość do progu wzdłuż osi (dodatnia przed progiem), y – w bok (dodatnia w prawo),
        /// h – wysokość nad pasem, len – długość pasa, vAlong/vCross – prędkość wzdłuż osi / w bok.</summary>
        public bool RunwayFrame(out double d, out double y, out double h, out double len, out double vAlong, out double vCross)
        {
            d = y = h = len = vAlong = vCross = double.NaN;
            if (!rwySet || vessel == null) return false;
            Vector3d a = Surf(rwyLatA, rwyLonA, rwyAlt), b = Surf(rwyLatB, rwyLonB, rwyAlt);
            Vector3d ab = (b - a); len = ab.magnitude; ab /= Math.Max(1, len);
            Vector3d pos = vessel.CoMD - vessel.mainBody.position, up = pos.normalized;
            Vector3d thr, dir;
            if (approachOn) { thr = thrPos; dir = rwyDir; }
            else
            {
                Vector3d vh = Vector3d.Exclude(up, (Vector3d)vessel.srf_velocity);
                if (vh.magnitude < 1) vh = Vector3d.Exclude(up, (Vector3d)vessel.transform.up);
                if (Vector3d.Dot(vh, ab) >= 0) { thr = a; dir = ab; } else { thr = b; dir = -ab; }
            }
            dir = Vector3d.Exclude(up, dir).normalized;
            Vector3d right = Vector3d.Cross(up, dir).normalized;
            Vector3d rel = pos - thr;
            d = -Vector3d.Dot(rel, dir); y = Vector3d.Dot(rel, right);
            h = vessel.altitude - rwyAlt;
            Vector3d v = vessel.srf_velocity;
            vAlong = Vector3d.Dot(v, dir); vCross = Vector3d.Dot(v, right);
            return true;
        }

        // ---- duży licznik odległości od lotniska (na górze ekranu, gdy program albo podejście działa)
        private double hudRMax = double.NaN, hudDpsi = double.NaN, hudCos = double.NaN;
        private GUIStyle hudBig, hudSmall, hudShadowBig, hudShadowSmall;
        private static void HudText(Rect r, string t, GUIStyle st, GUIStyle sh, Color c)
        {
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), t, sh);
            st.normal.textColor = c; GUI.Label(r, t, st);
        }
        // włącznik dużego licznika (zakładka Program); zapamiętywany w PluginData/hud.cfg
        private static bool bigHud = true, bigHudLoaded;
        private static string HudFile { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "hud.cfg")); } }
        public static bool BigHud
        {
            get
            {
                if (!bigHudLoaded) { bigHudLoaded = true; try { if (File.Exists(HudFile)) bigHud = ConfigNode.Load(HudFile).GetValue("bigHud") != "False"; } catch { } }
                return bigHud;
            }
            set
            {
                bigHud = value; bigHudLoaded = true;
                try { ConfigNode n = new ConfigNode(); n.AddValue("bigHud", value ? "True" : "False"); n.Save(HudFile); } catch { }
            }
        }

        private void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel != FlightGlobals.ActiveVessel) return;
            if (!BigHud) return;
            if (!Active && !approachOn) return;
            if (hudBig == null)
            {
                hudBig = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                hudSmall = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                hudShadowBig = new GUIStyle(hudBig); hudShadowBig.normal.textColor = Color.black;
                hudShadowSmall = new GUIStyle(hudSmall); hudShadowSmall.normal.textColor = Color.black;
            }
            if (mph != MPh.None && mph != MPh.Done)
            {
                double tb = !double.IsNaN(mBurnUT) ? mBurnUT - Planetarium.GetUniversalTime() : double.NaN;
                bool burning = mph == MPh.BurnApRaiseAp || mph == MPh.BurnPlane || mph == MPh.BurnRaise || mph == MPh.BurnA || mph == MPh.BurnP || mph == MPh.BurnCombo || mph == MPh.BurnLower;
                string bigM = burning ? "IMPULS" : !double.IsNaN(tb) && tb > 0 ? "IMPULS ZA " + KSPUtil.PrintTimeCompact(tb, false) : "MANEWR";
                string smallM = ManeuverInfo + " · orbita " + (vessel.orbit.PeA / 1000).ToString("F0", Inv) + "×" + (vessel.orbit.ApA / 1000).ToString("F0", Inv) + " km, " + vessel.orbit.inclination.ToString("F1", Inv) + "° → cel " +
                                mPe.ToString("F0", Inv) + "×" + mAp.ToString("F0", Inv) + " km, " + mInc.ToString("F1", Inv) + "°";
                float wm = 1100, xm = (Screen.width - wm) / 2, ym = Screen.height * 0.11f;
                HudText(new Rect(xm, ym, wm, 50), bigM, hudBig, hudShadowBig, burning ? Color.yellow : Color.white);
                HudText(new Rect(xm, ym + 46, wm, 26), smallM, hudSmall, hudShadowSmall, Color.white);
                return;
            }
            if (!siteSet) return;
            double brg;
            double km = GreatCircle(vessel.latitude, vessel.longitude, siteLat, siteLon, out brg) * vessel.mainBody.Radius / 1000;
            string big; Color col = Color.white;
            if (approachOn && rwySet && !vessel.Landed)
                big = "DO PROGU " + (RunwayDistance() / 1000).ToString(RunwayDistance() < 100000 ? "F1" : "F0", Inv) + " km";
            else if (vessel.Landed && LandingVerdict.Length > 0)
            { big = LandingVerdict.StartsWith("NA PASIE") ? "NA PASIE" : "POZA PASEM"; col = LandingVerdict.StartsWith("NA PASIE") ? Color.green : Color.red; }
            else big = (SiteName.Length > 0 ? SiteName.ToUpperInvariant() + ": " : "DO LOTNISKA ") + km.ToString(km < 100 ? "F1" : "F0", Inv) + " km";
            string small = "wys. " + (vessel.altitude / 1000).ToString("F1", Inv) + " km · " + vessel.srfSpeed.ToString("F0", Inv) + " m/s";
            bool inEntry = entryGuide && vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth;
            if (inEntry && !double.IsNaN(hudRMax) && km > 1)
            {
                double ratio = hudCos > 1e-3 ? 1 / hudCos : 99;
                small += " · zasięg do TAEM (M 2,5, 100 km przed) ~" + (hudRMax / 1000).ToString("F0", Inv) + " km (" + (ratio >= 1.1 ? "zapas" : ratio >= 0.95 ? "na styk" : "ZA MAŁO") + ")";
                if (!approachOn) col = ratio >= 1.1 ? Color.green : ratio >= 0.95 ? Color.yellow : Color.red;
                if (!double.IsNaN(hudDpsi)) small += " · lotnisko " + Math.Abs(hudDpsi).ToString("F0", Inv) + "° w " + (hudDpsi >= 0 ? "prawo" : "lewo");
            }
            else if (!double.IsNaN(planUT) && planUT > Planetarium.GetUniversalTime())
                small += " · impuls za " + KSPUtil.PrintTimeCompact(planUT - Planetarium.GetUniversalTime(), false);
            if (StallLevel > 0 && !vessel.Landed) { small += StallLevel == 2 ? " · PRZECIĄGNIĘCIE" : " · blisko przeciągnięcia"; }
            float w = 900, x = (Screen.width - w) / 2, y = Screen.height * 0.11f;
            HudText(new Rect(x, y, w, 50), big, hudBig, hudShadowBig, col);
            HudText(new Rect(x, y + 46, w, 26), small, hudSmall, hudShadowSmall, Color.white);
        }

        private void OnDestroy() { if (Instance == this) Instance = null; RestoreWarpLimits(); ReleaseAttitude(); if (steerVessel != null) steerVessel.OnFlyByWire -= SteerFbw; }

        public bool Active { get { return Running != null && Current >= 0 && Current < Running.steps.Count; } }

        public void Begin(FlightPrograms.Program p)
        {
            Stop();
            vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || p == null || p.steps.Count == 0) { LastMessage = "Brak statku albo pusty program."; return; }
            foreach (FlightPrograms.Step s in p.steps) s.activatedAt = -1;
            Running = p;
            Current = 0;
            burnOn = false;
            siteSet = false;
            string site = p.node != null ? p.node.GetValue("site") : null;
            string rwyCfg = p.node != null ? p.node.GetValue("runway") : null;
            bool chosen = (site != null && site.Trim().ToLowerInvariant() == "wybrane") || (rwyCfg != null && rwyCfg.Trim().ToLowerInvariant() == "wybrane");
            if (chosen)
            {
                if (Airports.List.Count == 0) Airports.Refresh();
                Airports.Airport ap = Airports.Selected;
                if (ap == null) { Running = null; LastMessage = "Nie wybrano lotniska (zakładka Program → Lotnisko docelowe)."; return; }
                site = ap.Lat.ToString("F5", CultureInfo.InvariantCulture) + " " + ap.Lon.ToString("F5", CultureInfo.InvariantCulture);
                rwyCfg = ap.latA.ToString("F5", CultureInfo.InvariantCulture) + " " + ap.lonA.ToString("F5", CultureInfo.InvariantCulture) + " " +
                         ap.latB.ToString("F5", CultureInfo.InvariantCulture) + " " + ap.lonB.ToString("F5", CultureInfo.InvariantCulture) + " " + ap.alt.ToString("F1", CultureInfo.InvariantCulture);
                SiteName = ap.name;
            }
            else SiteName = "";
            if (site != null)
            {
                string[] ll = site.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (ll.Length >= 2) { siteLat = Num(ll[0], 0); siteLon = Num(ll[1], 0); siteSet = true; }
            }
            siteLead = p.node != null ? Num(p.node.GetValue("siteLead"), 3900) : 3900;
            planAngle = p.node != null ? Num(p.node.GetValue("deorbitAngle"), 185) : 185;
            planMaxXr = p.node != null ? Num(p.node.GetValue("maxCrossRange"), 1000) : 1000;
            entryRangeKm = p.node != null ? Num(p.node.GetValue("entryRange"), 0) : 0;
            deorbitPeriKm = p.node != null ? Num(p.node.GetValue("deorbitPeri"), 30) : 30;
            entryAltKm = p.node != null ? Num(p.node.GetValue("entryAlt"), 120) : 120;
            if (p.node != null && p.node.HasValue("daylight")) RequireDaylight = p.node.GetValue("daylight").Trim().ToLowerInvariant() == "true";
            planUT = double.NaN; nextPlan = 0; PlanInfo = "";
            approachOn = false; rwySet = false;
            string rwy = rwyCfg;
            if (rwy != null)
            {
                string[] r = rwy.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (r.Length >= 5) { rwyLatA = Num(r[0], 0); rwyLonA = Num(r[1], 0); rwyLatB = Num(r[2], 0); rwyLonB = Num(r[3], 0); rwyAlt = Num(r[4], 0); rwySet = true; }
            }
            qMax = 0;
            lastActivation = Planetarium.GetUniversalTime();
            RefreshGroups();
            LastMessage = "Program „" + p.name + "” uzbrojony.";
            FlightLogger.StartFor(vessel);
            FlightLogger.Event("Program lotu: start „" + p.name + "”");
            Debug.Log("[RSSBalancer] Program lotu: start " + p.name);
        }

        public void Stop()
        {
            if (Running != null) FlightLogger.Event("Program lotu: zatrzymany („" + Running.name + "”)");
            ReleaseAttitude();
            StopPattern();
            StopApproach();
            steerHdg = double.NaN;
            if (burnOn) { burnOn = false; FlightInputHandler.state.mainThrottle = 0f; }
            if (mph != MPh.None && mph != MPh.Done) FlightInputHandler.state.mainThrottle = 0f;
            mph = MPh.None;
            Running = null;
            Current = -1;
        }

        public void Skip()
        {
            if (!Active) return;
            Running.steps[Current].activatedAt = -2;
            Current++;
            lastActivation = Planetarium.GetUniversalTime();
            if (!Active) Finish();
        }

        private void Finish()
        {
            FlightLogger.Event("Program lotu: koniec „" + Running.name + "”");
            LastMessage = "Program „" + Running.name + "” zakończony.";
            Running = null;
            Current = -1;
        }

        private void RefreshGroups()
        {
            if (vessel == null) return;
            try
            {
                groups = EngineGroups.Build(vessel.parts, null);
                id = SoyuzTemplate.Identify(groups);
            }
            catch { groups = null; id = null; }
        }


        // ------------------------------------------------------------------ przyspieszanie czasu do kroku
        // Krok, którego chwilę da się policzyć z orbity (impuls „deorbitin”, wysokość „alt < X” nad atmosferą),
        // przeskakujemy warpem „na szynach” (jak warp do manewru). Kroki w atmosferze – przyspieszeniem fizycznym ×3
        // aż program dojdzie do wybranego kroku (wtedy wraca ×1). Wcześniejsze kroki i tak wykonują się po drodze:
        // warp na szynach zatrzymuje się przed impulsem, a przy wejściu w atmosferę KSP sam go przerywa.
        private int physWarpTarget = -1;
        public int PhysWarpTarget { get { return physWarpTarget; } }

        private static double FirstNumberAfter(string when, string key, out string op)
        {
            op = "";
            int i = when.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return double.NaN;
            string rest = when.Substring(i + key.Length).TrimStart();
            int k = 0;
            while (k < rest.Length && "<>=!".IndexOf(rest[k]) >= 0) k++;
            op = rest.Substring(0, k);
            rest = rest.Substring(k).TrimStart();
            int e = 0;
            while (e < rest.Length && (char.IsDigit(rest[e]) || rest[e] == '.' || rest[e] == '-')) e++;
            double v;
            return double.TryParse(rest.Substring(0, e), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        /// <summary>Najbliższa chwila, w której statek schodzi (malejący promień) do promienia r; NaN gdy nie schodzi.</summary>
        private static double NextDescentToRadius(Orbit o, double now, double r)
        {
            if (o.PeR >= r) return double.NaN;
            double period = o.eccentricity < 1 ? o.period : 86400;
            double step = Math.Max(5, Math.Min(60, period / 400));
            double tPrev = now, rPrev = o.getRelativePositionAtUT(now).magnitude;
            if (rPrev <= r) return now;
            for (double t = now + step; t <= now + period + step; t += step)
            {
                double rr = o.getRelativePositionAtUT(t).magnitude;
                if (rr <= r)
                {
                    double lo = tPrev, hi = t;
                    for (int i = 0; i < 30; i++) { double m = 0.5 * (lo + hi); if (o.getRelativePositionAtUT(m).magnitude > r) lo = m; else hi = m; }
                    return hi;
                }
                tPrev = t; rPrev = rr;
            }
            return double.NaN;
        }

        public string WarpToStep(int k)
        {
            if (!Active || vessel == null) return "Program nie działa.";
            if (k < Current) return "Ten krok już się wykonał.";
            physWarpTarget = -1;
            double now = Planetarium.GetUniversalTime();
            CelestialBody b = vessel.mainBody;
            double atmTop = b.atmosphere ? b.atmosphereDepth : 0;
            bool inAtm = b.atmosphere && vessel.altitude < atmTop;
            string when = Running.steps[k].node.GetValue("when") ?? "always";
            string op;

            if (!inAtm)
            {
                // 1) impuls hamujący – według planu (krok z deorbitin albo dowolny późniejszy, gdy impulsu jeszcze nie było)
                double din = FirstNumberAfter(when, "deorbitin", out op);
                if (siteSet && vessel.orbit.PeA > atmTop) DeorbitIn();
                bool burnPending = !double.IsNaN(planUT) && planUT > now && vessel.orbit.PeA > atmTop;
                if (!double.IsNaN(din) || burnPending)
                {
                    double tin = DeorbitIn();
                    if (double.IsNaN(tin)) return "Brak planu zejścia: " + PlanInfo;
                    double lead = !double.IsNaN(din) && op.StartsWith("<") ? Math.Max(din, 20) : 60;
                    if (tin <= lead + 5) return "Do impulsu zostało " + tin.ToString("F0") + " s – nie ma czego przeskakiwać.";
                    TimeWarp.fetch.WarpTo(planUT - lead);
                    return "Przyspieszam do " + lead.ToString("F0") + " s przed impulsem" + (double.IsNaN(din) ? " (najpierw impuls – dalsze kroki po nim)." : ".");
                }
                // 2) wysokość nad atmosferą – z orbity
                double altKm = FirstNumberAfter(when, "alt", out op);
                if (!double.IsNaN(altKm) && op.StartsWith("<"))
                {
                    double r = b.Radius + Math.Max(altKm * 1000.0, atmTop + 1000);
                    if (vessel.orbit.PeR >= r) return "Orbita nie schodzi tak nisko – najpierw impuls hamujący.";
                    double t = NextDescentToRadius(vessel.orbit, now, r);
                    if (double.IsNaN(t) || t <= now + 30) return "Ten punkt jest za chwilę.";
                    TimeWarp.fetch.WarpTo(t - 20);
                    return "Przyspieszam do " + (Math.Max(altKm * 1000.0, atmTop + 1000) / 1000).ToString("F0") + " km.";
                }
                // 3) nie da się policzyć – szyny do granicy atmosfery, jeśli tam schodzimy
                if (vessel.orbit.PeA < atmTop)
                {
                    double t = NextDescentToRadius(vessel.orbit, now, b.Radius + atmTop + 1000);
                    if (!double.IsNaN(t) && t > now + 30) { TimeWarp.fetch.WarpTo(t - 20); physWarpTarget = k; return "Przyspieszam do granicy atmosfery, dalej ×3 do kroku „" + Running.steps[k].name + "”."; }
                }
                return "Tego kroku nie umiem przewidzieć na orbicie.";
            }

            if (vessel.LandedOrSplashed) return "Już na ziemi.";
            physWarpTarget = k;
            TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
            TimeWarp.SetRate(Math.Min(2, TimeWarp.fetch.physicsWarpRates.Length - 1), false);
            return "Przyspieszenie fizyczne ×3 do kroku „" + Running.steps[k].name + "”.";
        }

        // Limity wysokości dla przyspieszenia czasu: w RSS na orbicie 155 km KSP pozwalał tylko na ×10 (lot 2026-11-19 21:20:
        // „Warping to … Max Rate Allowed: 10.0x” – godzina czekania na impuls = 6 minut). Na czas programu obniżamy je
        // do granicy atmosfery (+2 km na stopień) – na szynach orbita liczy się dokładnie, a WarpTo sam zwalnia przed celem.
        private float[] warpLimOrig; private CelestialBody warpLimBody;
        private void LowerWarpLimits()
        {
            if (vessel == null) return;
            CelestialBody b = vessel.mainBody;
            if (b == null || b.timeWarpAltitudeLimits == null || warpLimBody == b) return;
            RestoreWarpLimits();
            warpLimBody = b; warpLimOrig = (float[])b.timeWarpAltitudeLimits.Clone();
            float top = b.atmosphere ? (float)b.atmosphereDepth : 0f;
            for (int i = 1; i < b.timeWarpAltitudeLimits.Length; i++)
                b.timeWarpAltitudeLimits[i] = Mathf.Min(warpLimOrig[i], top + 2000f * i);
        }
        private void RestoreWarpLimits()
        {
            if (warpLimBody != null && warpLimOrig != null) warpLimBody.timeWarpAltitudeLimits = warpLimOrig;
            warpLimBody = null; warpLimOrig = null;
        }

        private void PhysWarpUpdate()
        {
            if (physWarpTarget < 0 || vessel == null) return;
            CelestialBody b = vessel.mainBody;
            bool inAtm = b.atmosphere && vessel.altitude < b.atmosphereDepth;
            if (!Active || Current >= physWarpTarget || approachOn || vessel.LandedOrSplashed)
            {
                if (inAtm && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.LOW) TimeWarp.SetRate(0, false);
                physWarpTarget = -1;
                return;
            }
            // po przerwaniu warpa „na szynach” przy wejściu w atmosferę – włącz fizyczne ×3
            if (inAtm && TimeWarp.CurrentRateIndex == 0)
            {
                TimeWarp.fetch.Mode = TimeWarp.Modes.LOW;
                TimeWarp.SetRate(Math.Min(2, TimeWarp.fetch.physicsWarpRates.Length - 1), false);
            }
        }

        private void FixedUpdate()
        {
            if (warpLimBody != null) RestoreWarpLimits();   // 1.29 obniżał limity warpa – przy wyjściu z szybkiego warpa tuż nad atmosferą KSP „przesuwał” orbitę (lot 22:11: 155×155 → 173×136 km bez paliwa)
            PhysWarpUpdate();
            if (patternOn && vessel != null && vessel == FlightGlobals.ActiveVessel) Pattern();
            if (approachOn && vessel != null && vessel == FlightGlobals.ActiveVessel && !Active) Approach();
            if (!Active) return;
            if (vessel == null || vessel != FlightGlobals.ActiveVessel) { LastMessage = "Zmieniono statek – program zatrzymany."; Stop(); return; }

            qMax = Math.Max(qMax, vessel.dynamicPressurekPa);
            // Grupy silników rozpoznajemy RAZ przy starcie programu (na wyrzutni) – po odrzucie bloków
            // ponowne rozpoznanie widzi już tylko rdzeń i nie umie go nazwać „rdzeniem”.
            if ((id == null || !id.ok) && vessel.situation == Vessel.Situations.PRELAUNCH && Time.realtimeSinceStartup > nextGroups)
            { nextGroups = Time.realtimeSinceStartup + 2f; RefreshGroups(); }
            if (holdAttitude) HoldAttitude();
            if (!approachOn && vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth) { double e1, v1, a1; StallLevel = StallCheck(out e1, out v1, out a1); }
            if (approachOn) Approach();
            if (mph != MPh.None && mph != MPh.Done) Maneuver();
            if (burnOn) FlightInputHandler.state.mainThrottle = holdAttitude && attError < 5.0 ? 1f : 0f;
            // Bezpieczne zatrzymanie warpa przed impulsem. Do 1.30 było tu natychmiastowe SetRate(0) przy „tin < 20 + 3·krok”
            // – przy warpie ×100 000 to wypadało godziny przed impulsem, a nagłe zrzucenie warpa z takiej prędkości
            // (powtarzane, bo warp znów ruszał) psuło orbitę: lot 22:11 155×155 → 173×136 km, lot 23:14 251×263 → 484×42 km.
            // Teraz: przy dużym warpie oddajemy hamowanie stockowemu WarpTo (zwalnia stopniowo), a natychmiast stop dopiero < 20 s.
            if (!double.IsNaN(planUT) && !burnOn && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
            {
                double tin = planUT - Planetarium.GetUniversalTime();
                if (tin > 0 && tin < 20) { TimeWarp.SetRate(0, false); LastMessage += "\nZatrzymano przyspieszenie przed impulsem."; }
                else if (tin > 0 && tin < 60 + 3 * TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime && Planetarium.GetUniversalTime() > warpGuardUT)
                {
                    warpGuardUT = planUT;                       // raz na plan
                    TimeWarp.fetch.WarpTo(planUT - 30);
                }
            }

            // kilka kroków może się wykonać w jednej klatce (np. „always”)
            for (int guard = 0; guard < 8 && Active; guard++)
            {
                FlightPrograms.Step s = Running.steps[Current];
                double delay = Num(s.node.GetValue("delay"), 0);
                if (Planetarium.GetUniversalTime() - lastActivation < delay) return;
                string when = s.node.GetValue("when") ?? "always";
                if (!Condition(when)) return;
                Execute(s);
                s.activatedAt = vessel.missionTime;
                lastActivation = Planetarium.GetUniversalTime();
                Current++;
                if (pendingProgram != null)
                {
                    // „program = nazwa” – przejście do kolejnego programu (np. po wynoszeniu od razu powrót)
                    string pn = pendingProgram; pendingProgram = null;
                    FlightPrograms.Program np = FlightPrograms.Programs.Find(x => x.name == pn);
                    if (np == null) { LastMessage += "\nBrak programu „" + pn + "”."; FlightLogger.Event("Program lotu: brak programu „" + pn + "”"); return; }
                    Finish();
                    Begin(np);
                    return;
                }
            }
            if (!Active) Finish();
        }

        // ------------------------------------------------------------------ warunki

        private bool Condition(string expr)
        {
            foreach (string part in expr.Split('&'))
            {
                string c = part.Trim();
                if (c.Length == 0 || c == "always") continue;
                string[] ops = { ">=", "<=", "==", "!=", ">", "<" };
                string op = null; int at = -1;
                foreach (string o in ops) { at = c.IndexOf(o, StringComparison.Ordinal); if (at > 0) { op = o; break; } }
                if (op == null)
                {
                    if (Var(c) <= 0) return false;      // samo „qfalling” = prawda, gdy > 0
                    continue;
                }
                double a = Var(c.Substring(0, at).Trim());
                double b = Num(c.Substring(at + op.Length).Trim(), double.NaN);
                if (double.IsNaN(a) || double.IsNaN(b)) return false;
                bool ok = op == ">=" ? a >= b : op == "<=" ? a <= b : op == ">" ? a > b : op == "<" ? a < b : op == "==" ? Math.Abs(a - b) < 1e-6 : Math.Abs(a - b) >= 1e-6;
                if (!ok) return false;
            }
            return true;
        }

        private double Var(string name)
        {
            Vessel v = vessel;
            switch (name.ToLowerInvariant())
            {
                case "always": return 1;
                case "met": return v.missionTime;
                case "alt": return v.altitude / 1000.0;
                case "speed": return v.srfSpeed;
                case "vspeed": return v.verticalSpeed;
                case "orbspeed": return v.obt_speed;
                case "apo": return v.orbit != null ? v.orbit.ApA / 1000.0 : 0;
                case "peri": return v.orbit != null ? v.orbit.PeA / 1000.0 : 0;
                case "q": return v.dynamicPressurekPa;
                case "qfalling": return qMax > 5 && v.dynamicPressurekPa < qMax * 0.9 ? 1 : 0;
                case "g": return v.geeForce;
                case "stage": return v.currentStage;
                case "timetoap": return v.orbit != null ? v.orbit.timeToAp : double.NaN;
                case "timetope": return v.orbit != null ? v.orbit.timeToPe : double.NaN;
                case "lat": return v.latitude;
                case "lon": return v.longitude;
                case "mach": return v.mach;
                case "aligned": return holdAttitude && attError < 5.0 ? 1 : 0;
                case "rwydist": return approachOn || rwySet ? RunwayDistance() / 1000.0 : double.NaN;
                case "landed": return vessel.Landed ? 1 : 0;
                case "siteangle": return SiteAngle(false);
                case "sitexrange": return SiteAngle(true);
                case "deorbitin": return DeorbitIn();
                case "orbitdone": return mph == MPh.Done ? 1 : 0;
                case "boosters": return id != null && id.ok ? Running_(id.boosters, v) : double.NaN;
                case "core": return id != null && id.ok ? Running_(id.core, v) : double.NaN;
                case "upper": return id != null && id.ok ? Running_(id.upper, v) : double.NaN;
            }
            return double.NaN;
        }

        /// <summary>
        /// Kąt [°] wzdłuż toru od statku do miejsca lądowania (0–360, w kierunku lotu) albo odległość boczna [km]
        /// – dla pozycji miejsca przesuniętej o obrót Ziemi przez siteLead sekund (ok. czas od impulsu do lądowania).
        /// </summary>
        private double SiteAngle(bool crossRange)
        {
            if (!siteSet || vessel == null) return double.NaN;
            CelestialBody b = vessel.mainBody;
            Vector3d r = vessel.CoMD - b.position;
            Vector3d h = Vector3d.Cross(r, vessel.obt_velocity).normalized;
            return SiteGeometry(b, r, h, Planetarium.GetUniversalTime() + siteLead, crossRange);
        }

        /// <summary>
        /// Geometria względem miejsca lądowania: r – położenie statku (względem środka planety, układ świata „teraz”),
        /// h – normalna płaszczyzny orbity, siteUT – chwila, w której liczymy położenie miejsca.
        /// Obrót Ziemi uwzględniamy przesunięciem długości geograficznej na wschód (bez kwaternionów – bez pułapki znaku).
        /// </summary>
        private double SiteGeometry(CelestialBody b, Vector3d r, Vector3d h, double siteUT, bool crossRange)
        {
            double now = Planetarium.GetUniversalTime();
            double dLon = b.rotates && b.rotationPeriod > 0 ? 360.0 * (siteUT - now) / b.rotationPeriod : 0;
            Vector3d site = b.GetWorldSurfacePosition(siteLat, siteLon + dLon, 0) - b.position;
            double off = Vector3d.Dot(site.normalized, h);
            if (crossRange) return Math.Abs(Math.Asin(Math.Max(-1, Math.Min(1, off)))) * b.Radius / 1000.0;
            Vector3d sp = site - h * Vector3d.Dot(site, h);
            double ang = Math.Atan2(Vector3d.Dot(Vector3d.Cross(r, sp), h), Vector3d.Dot(r, sp)) * 180.0 / Math.PI;
            return ang < 0 ? ang + 360 : ang;
        }

        // ------------------------------------------------------------------ plan zejścia z orbity
        // Zamiast „łapać” okno siteangle w locie (przy dużym przyspieszeniu czasu jedna klatka fizyki to setki
        // sekund i okno 14° po prostu się przeskakuje) liczymy z orbity Keplera chwilę impulsu z góry.

        private double planUT = double.NaN, planXr = double.NaN, nextPlan, planSun;
        private double warpGuardUT = double.NegativeInfinity;
        private double planAngle = 185, planMaxXr = 1000, planApA = double.NaN, planPeA = double.NaN;
        public bool RequireDaylight;
        public void Replan() { planUT = double.NaN; nextPlan = 0; planApA = double.NaN; PlanInfo = "przeliczam…"; }

        /// <summary>Wysokość Słońca nad horyzontem w miejscu lądowania w chwili ut (stopnie).</summary>
        private double SunElevationAtSite(CelestialBody b, double ut)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || sun == b) return 90;
            double now = Planetarium.GetUniversalTime();
            double dLon = b.rotates && b.rotationPeriod > 0 ? 360.0 * (ut - now) / b.rotationPeriod : 0;
            Vector3d siteUp = (b.GetWorldSurfacePosition(siteLat, siteLon + dLon, 0) - b.position).normalized;
            Vector3d toSun = (sun.position - b.position).normalized;      // przez kilka dób kierunek Słońca prawie stały
            return 90 - Vector3d.Angle(siteUp, toSun);
        }
        public string PlanInfo = "";
        public string SiteInfo
        {
            get
            {
                if (!Active || !siteSet || vessel == null) return "";
                double a = SiteAngle(false), x = SiteAngle(true);
                return "miejsce lądowania: " + a.ToString("F1") + "° przed statkiem, odejście boczne " + x.ToString("F0") + " km";
            }
        }

        private double DeorbitIn()
        {
            double now = Planetarium.GetUniversalTime();
            // plan „zamrażamy” na ostatnie 10 min przed impulsem i do 10 min po nim (żeby warunek „<= 0” był stabilny)
            bool locked = !double.IsNaN(planUT) && now > planUT - 600 && now < planUT + 600;
            // plan fizyczny jest kosztowny – liczymy go raz, a ponownie tylko gdy orbita się zmieni albo plan minął
            bool orbitChanged = vessel != null && vessel.orbit != null && (double.IsNaN(planApA) || Math.Abs(vessel.orbit.ApA - planApA) > 500 || Math.Abs(vessel.orbit.PeA - planPeA) > 500);
            bool need = entryRangeKm > 0 ? (double.IsNaN(planUT) || orbitChanged || planUT < now - 600) && Time.realtimeSinceStartup > nextPlan : Time.realtimeSinceStartup > nextPlan;
            if (!locked && need)
            {
                nextPlan = Time.realtimeSinceStartup + (entryRangeKm > 0 ? 20f : 5f);
                PlanDeorbit();
                if (vessel != null && vessel.orbit != null) { planApA = vessel.orbit.ApA; planPeA = vessel.orbit.PeA; }
            }
            return double.IsNaN(planUT) ? double.NaN : planUT - now;
        }

        private Vector3d PosAt(Orbit o, CelestialBody b, double ut) { return o.getPositionAtUT(ut) - b.position; }

        private double AngleAt(Orbit o, CelestialBody b, double ut, bool xr)
        {
            Vector3d r = PosAt(o, b, ut), r2 = PosAt(o, b, ut + 1);
            Vector3d h = Vector3d.Cross(r, r2 - r).normalized;
            return SiteGeometry(b, r, h, ut + siteLead, xr);
        }

        // ---- planowanie impulsu fizycznie (od wersji 1.30): dla każdej chwili impulsu liczymy orbitę po impulsie
        // (Δv wstecz do perygeum deorbitPeri), punkt wejścia (entryAlt) z obrotem Ziemi i odległość od niego do
        // lotniska po powierzchni. Impuls wtedy, gdy ta odległość = entryRange. Dawniej był stały kąt „deorbitAngle”,
        // dobierany na oko do jednej orbity – przy innej wysokości orbity trafiał gdzie indziej.
        private double entryRangeKm, deorbitPeriKm = 30, entryAltKm = 120, planDv = double.NaN, planDist = double.NaN, planEntryUT = double.NaN;

        private void GroundLL(Orbit n, CelestialBody b, double ut, out double lat, out double lon)
        {
            Vector3d w = n.getPositionAtUT(ut);
            lat = b.GetLatitude(w); lon = b.GetLongitude(w);
            if (b.rotates && b.rotationPeriod > 0) lon -= 360.0 * (ut - Planetarium.GetUniversalTime()) / b.rotationPeriod;
        }

        private readonly Orbit predOrbit = new Orbit();
        private bool PredictEntry(Orbit o, CelestialBody b, double tb, out double distKm, out double xrKm, out double tI, out double dv)
        {
            distKm = xrKm = tI = dv = double.NaN;
            Vector3d p = o.getRelativePositionAtUT(tb), v = o.getOrbitalVelocityAtUT(tb), vn = v.normalized;
            double targetPe = b.Radius + deorbitPeriKm * 1000;
            Orbit n = predOrbit;
            n.UpdateFromStateVectors(p, v, b, tb);
            double lo = 0, hi = 800;
            if (n.PeR <= targetPe) hi = 0;
            else for (int i = 0; i < 18; i++) { double m = 0.5 * (lo + hi); n.UpdateFromStateVectors(p, v - vn * m, b, tb); if (n.PeR > targetPe) lo = m; else hi = m; }
            dv = hi;
            n.UpdateFromStateVectors(p, v - vn * dv, b, tb);
            tI = NextDescentToRadius(n, tb, b.Radius + entryAltKm * 1000);
            if (double.IsNaN(tI)) return false;
            double lat1, lon1, lat2, lon2, trk, brg;
            GroundLL(n, b, tI, out lat1, out lon1); GroundLL(n, b, tI + 10, out lat2, out lon2);
            GreatCircle(lat1, lon1, lat2, lon2, out trk);
            double ang = GreatCircle(lat1, lon1, siteLat, siteLon, out brg);
            double rel = ((brg - trk + 540) % 360) - 180;
            xrKm = Math.Abs(Math.Asin(Math.Max(-1, Math.Min(1, Math.Sin(ang) * Math.Sin(rel * Math.PI / 180))))) * b.Radius / 1000;
            distKm = ang * b.Radius / 1000;
            if (Math.Abs(rel) > 90) distKm = -distKm;          // lotnisko za plecami
            return true;
        }

        private double EngineThrustKN()
        {
            double t = 0;
            foreach (Part pp in vessel.parts)
                foreach (ModuleEngines e in pp.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited && !e.flameout) t += e.maxThrust;
            return t;
        }

        private void PlanDeorbitPhysical()
        {
            planUT = double.NaN; planXr = double.NaN;
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            if (o.eccentricity >= 1 || o.PeA < (b.atmosphere ? b.atmosphereDepth : 0)) { PlanInfo = "orbita nie jest zamknięta nad atmosferą"; return; }
            double incl = o.inclination > 90 ? 180 - o.inclination : o.inclination;
            double reachDeg = incl + planMaxXr / (b.Radius / 1000) * 180 / Math.PI;
            if (reachDeg < Math.Abs(siteLat))
            {
                PlanInfo = "nachylenie orbity " + o.inclination.ToString("F1") + "° – orbita nigdy nie przechodzi bliżej niż " + ((Math.Abs(siteLat) - incl) * b.Radius / 1000 * Math.PI / 180).ToString("F0") +
                           " km od lotniska (szer. " + siteLat.ToString("F1") + "°); potrzeba nachylenia ≥ " + (Math.Abs(siteLat) - planMaxXr / (b.Radius / 1000) * 180 / Math.PI).ToString("F1") + "° (1K1: 51,6°)";
                return;
            }
            double now = Planetarium.GetUniversalTime(), step = 30, horizon = 6 * 86400;
            double bestXr = double.MaxValue; int nightSkips = 0;
            double tPrev = double.NaN, dPrev = double.NaN;
            for (double t = now + 120; t < now + horizon; t += step)
            {
                double a = AngleAt(o, b, t, false);
                if (a < 30 || a > 270) { tPrev = double.NaN; continue; }   // lotnisko daleko – szybki odsiew
                double d, xr, tI, dv;
                if (!PredictEntry(o, b, t, out d, out xr, out tI, out dv)) { tPrev = double.NaN; continue; }
                if (!double.IsNaN(tPrev) && dPrev > entryRangeKm && d <= entryRangeKm && d > 0)
                {
                    double lo = tPrev, hi = t;
                    for (int i = 0; i < 12; i++) { double m = 0.5 * (lo + hi), dm, xm, tm, vm; if (PredictEntry(o, b, m, out dm, out xm, out tm, out vm) && dm > entryRangeKm) lo = m; else hi = m; }
                    PredictEntry(o, b, hi, out d, out xr, out tI, out dv);
                    if (xr < bestXr) bestXr = xr;
                    if (xr <= planMaxXr)
                    {
                        double tLand = tI + 1900;
                        double sunEl = SunElevationAtSite(b, tLand);
                        if (!RequireDaylight || sunEl >= 10)
                        {
                            double thr = EngineThrustKN();
                            double half = thr > 1 ? 0.5 * dv * vessel.totalMass / thr : 0;
                            planUT = hi - half; planXr = xr; planSun = sunEl; planDv = dv; planDist = d; planEntryUT = tI;
                            break;
                        }
                        nightSkips++;
                    }
                }
                tPrev = t; dPrev = d;
            }
            if (double.IsNaN(planUT))
                PlanInfo = "brak okna w 6 dób (najmniejsze odejście boczne " + (bestXr < 1e9 ? bestXr.ToString("F0") : "?") + " km > " + planMaxXr.ToString("F0") + ")" +
                           (nightSkips > 0 ? ", pominięto " + nightSkips + " nocnych" : "");
            else
                PlanInfo = "impuls za " + KSPUtil.PrintTimeCompact(planUT - now, false) + ", Δv " + planDv.ToString("F0") + " m/s, wejście (" + entryAltKm.ToString("F0") + " km) " +
                           planDist.ToString("F0") + " km od lotniska, odejście boczne " + planXr.ToString("F0") + " km, Słońce przy lądowaniu " + planSun.ToString("F0") + "° (" +
                           (planSun >= 10 ? "dzień" : planSun > -6 ? "zmierzch/świt" : "noc") + ")" + (nightSkips > 0 ? ", pominięto " + nightSkips + " nocnych przelotów" : "");
        }

        private void PlanDeorbit()
        {
            if (entryRangeKm > 0 && siteSet && vessel != null && vessel.orbit != null) { PlanDeorbitPhysical(); return; }
            planUT = double.NaN; planXr = double.NaN;
            if (!siteSet || vessel == null || vessel.orbit == null) { PlanInfo = "brak miejsca lądowania w programie"; return; }
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            if (o.eccentricity >= 1 || o.PeA < 0) { PlanInfo = "orbita nie jest zamknięta"; return; }
            double now = Planetarium.GetUniversalTime(), step = 20, horizon = 6 * 86400;
            double tPrev = now + 30, aPrev = AngleAt(o, b, tPrev, false);
            double bestXr = double.MaxValue;
            int nightSkips = 0;
            for (double t = tPrev + step; t < now + horizon; t += step)
            {
                double a = AngleAt(o, b, t, false);
                // kąt do miejsca maleje w miarę lotu; przejście przez planAngle (bez skoku 0↔360)
                if (aPrev > planAngle && a <= planAngle && aPrev - a < 90)
                {
                    double lo = t - step, hi = t;
                    for (int i = 0; i < 20; i++) { double m = 0.5 * (lo + hi); if (AngleAt(o, b, m, false) > planAngle) lo = m; else hi = m; }
                    double xr = AngleAt(o, b, hi, true);
                    if (xr < bestXr) bestXr = xr;
                    if (xr <= planMaxXr)
                    {
                        double sunEl = SunElevationAtSite(b, hi + siteLead);
                        if (!RequireDaylight || sunEl >= 10) { planUT = hi; planXr = xr; planSun = sunEl; break; }
                        nightSkips++;
                    }
                }
                tPrev = t; aPrev = a;
            }
            if (double.IsNaN(planUT))
                PlanInfo = "brak okna w 6 dób (najmniejsze odejście boczne " + (bestXr < 1e9 ? bestXr.ToString("F0") : "?") + " km > " + planMaxXr.ToString("F0") + ")" +
                           (nightSkips > 0 ? ", pominięto " + nightSkips + " nocnych" : "");
            else
                PlanInfo = "impuls za " + KSPUtil.PrintTimeCompact(planUT - now, false) + ", odejście boczne " + planXr.ToString("F0") + " km, Słońce nad lotniskiem przy lądowaniu " +
                           planSun.ToString("F0") + "° (" + (planSun >= 10 ? "dzień" : planSun > -6 ? "zmierzch/świt" : "noc") + ")" + (nightSkips > 0 ? ", pominięto " + nightSkips + " nocnych przelotów" : "");
        }

        private static int Running_(EngineGroup g, Vessel v)
        {
            if (g == null) return 0;
            int n = 0;
            foreach (Part p in g.parts)
            {
                if (p == null || p.vessel != v) continue;
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited && !e.flameout && e.finalThrust > 0.01f) { n++; break; }
            }
            return n;
        }

        private static double Num(string s, double def)
        {
            double d;
            return s != null && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d) ? d : def;
        }

        // ------------------------------------------------------------------ akcje

        private void Execute(FlightPrograms.Step s)
        {
            ConfigNode n = s.node;
            string log = n.GetValue("log");
            FlightLogger.Event("Program lotu: " + s.name + (string.IsNullOrEmpty(log) ? "" : " – " + log));
            LastMessage = s.name + (string.IsNullOrEmpty(log) ? "" : ": " + log);
            Debug.Log("[RSSBalancer] Program lotu: krok " + s.name + " MET " + vessel.missionTime.ToString("F0"));

            foreach (ConfigNode.Value v in n.values)
            {
                string key = v.name, val = v.value.Trim();
                try
                {
                    if (key == "throttle")
                    {
                        float t = (float)Num(val, -1);
                        if (t >= 0) FlightInputHandler.state.mainThrottle = Mathf.Clamp01(t);
                    }
                    else if (key.StartsWith("thrust."))
                    {
                        float pct = Mathf.Clamp((float)Num(val, 100), 0f, 100f);
                        foreach (EngineGroup g in GroupsFor(key.Substring(7)))
                            foreach (Part p in g.parts)
                                if (p != null) foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>()) e.thrustPercentage = pct;
                    }
                    else if (key == "stage")
                    {
                        int k = (int)Num(val, 1);
                        for (int i = 0; i < k; i++) StageManager.ActivateNextStage();
                    }
                    else if (key == "stageTo" && id != null && id.ok && Running_(id.core, vessel) > 0 && n.GetValue("force") != "true")
                    {
                        LastMessage += "\nPominięto staging: rdzeń jeszcze pracuje (dodaj force = true, żeby wymusić).";
                        FlightLogger.Event("Program lotu: staging wstrzymany – rdzeń pracuje");
                    }
                    else if (key == "stageTo")
                    {
                        int to = (int)Num(val, 0);
                        for (int i = 0; i < 10 && vessel.currentStage > to; i++) StageManager.ActivateNextStage();
                    }
                    else if (key == "mechjebPreset")
                    {
                        MechJebPresets.Reload();
                        MechJebPresets.Preset pr = MechJebPresets.Presets.Find(x => x.name == val);
                        LastMessage += "\n" + (pr != null ? MechJebPresets.Apply(pr) : "Brak presetu MechJeba „" + val + "”.");
                    }
                    else if (key == "mechjebAscent")
                    {
                        LastMessage += "\n" + MechJebAscent(val == "on" || val == "true" || val == "1");
                    }
                    else if (key == "attitude")
                    {
                        if (val == "off") ReleaseAttitude();
                        else
                        {
                            string[] hp = val.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            if (hp.Length >= 2) { holdHeading = Num(hp[0], 90); holdPitch = Num(hp[1], 90); holdMode = Hold.SurfaceNorth; holdAttitude = true; }
                        }
                    }
                    else if (key == "orient")
                    {
                        string[] w = val.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (w.Length == 0 || w[0] == "off") ReleaseAttitude();
                        else if (w[0] == "srfvel")
                        {
                            // jak SmartASS „Surface prograde”: pochylenie (kąt natarcia), odchylenie, przechylenie względem wektora prędkości
                            aoaByMach = w.Length > 1 && w[1] == "mach";
                            float pit = aoaByMach ? AoAForMach(vessel.mach) : w.Length > 1 ? (float)Num(w[1], 0) : 0f, yaw = w.Length > 2 ? (float)Num(w[2], 0) : 0f, rol = w.Length > 3 ? (float)Num(w[3], 0) : 0f;
                            bool wasGuide = entryGuide;
                            entryGuide = w.Length > 3 && w[3] == "guide";
                            gAoa = pit; gYaw = yaw;
                            if (entryGuide && !wasGuide) { bankCmd = 0; calStart = -1; nextGLog = 0; gAoaCmd = pit; }
                            if (entryGuide) { rol = (float)bankCmd; pit = gAoaCmd; }
                            holdRot = Quaternion.AngleAxis(-rol, Vector3.forward) * Quaternion.AngleAxis(-pit, Vector3.right) * Quaternion.AngleAxis(yaw, Vector3.up);
                            holdMode = Hold.SrfVel; holdAttitude = true;
                        }
                        else
                        {
                            Vector3d d;
                            switch (w[0])
                            {
                                case "prograde": d = Vector3d.forward; break;
                                case "retrograde": d = Vector3d.back; break;
                                case "normal": d = Vector3d.left; break;
                                case "antinormal": d = Vector3d.right; break;
                                case "radialout": d = Vector3d.up; break;
                                case "radialin": d = Vector3d.down; break;
                                default: throw new Exception("nieznany kierunek " + w[0]);
                            }
                            holdDir = d; holdMode = Hold.Orbit; holdAttitude = true;
                        }
                        attError = 180;
                    }
                    else if (key == "burn")
                    {
                        burnOn = val == "on";
                        if (!burnOn) FlightInputHandler.state.mainThrottle = 0f;
                    }
                    else if (key == "pattern")
                    {
                        if (val == "on") StartPattern(); else StopPattern();
                    }
                    else if (key == "approach")
                    {
                        if (val == "on") StartApproach();
                        else StopApproach();
                    }
                    else if (key == "warp")
                    {
                        if (val == "stop" && TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, false);
                    }
                    else if (key == "warpto")
                    {
                        // warpto = deorbit <ile sekund przed impulsem>
                        string[] w = val.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        double lead = w.Length > 1 ? Num(w[1], 300) : 300;
                        if (w.Length > 0 && (w[0] == "ap" || w[0] == "pe"))
                        {
                            // warpto = ap <s przed apogeum> / pe <s przed perygeum>
                            double tt = w[0] == "ap" ? vessel.orbit.timeToAp : vessel.orbit.timeToPe;
                            if (tt > lead + 5) TimeWarp.fetch.WarpTo(Planetarium.GetUniversalTime() + tt - lead);
                        }
                        else
                        {
                            double tin = DeorbitIn();
                            if (double.IsNaN(tin)) throw new Exception("brak planu zejścia: " + PlanInfo);
                            if (tin > lead + 5) TimeWarp.fetch.WarpTo(planUT - lead);
                            LastMessage += "\nPlan: " + PlanInfo;
                            FlightLogger.Event("Program lotu: plan zejścia – " + PlanInfo);
                        }
                    }
                    else if (key == "program") pendingProgram = val.Trim();
                    else if (key == "askProgram" || key == "askprogram") AskProgram(val.Trim());
                    else if (key == "orbittarget")
                    {
                        // orbittarget = <perygeum km> <apogeum km> <nachylenie °>
                        string[] w = val.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (w.Length == 1 && w[0].ToLowerInvariant() == "auto")
                        {
                            double pe, apo, inc;
                            if (!OrbitForAirport(Airports.Selected, out pe, out apo, out inc)) throw new Exception("nie wybrano lotniska");
                            StartManeuver(pe, apo, inc);
                        }
                        else
                        {
                            if (w.Length < 3) throw new Exception("orbittarget = perygeum apogeum nachylenie | auto");
                            StartManeuver(Num(w[0], 251), Num(w[1], 263), Num(w[2], 51.6));
                        }
                        LastMessage += "\nManewr: " + ManeuverInfo;
                    }
                    else if (key == "sas") vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, val == "on");
                    else if (key == "rcs") vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, val == "on");
                    else if (key == "rcsaxes")
                    {
                        // jak w Buranie: przy gęstszym powietrzu silniki orientacji tylko w odchyleniu (pochylenie i przechylenie – stery)
                        bool all = val.Trim().ToLowerInvariant() != "yaw";
                        foreach (Part pp in vessel.parts)
                            foreach (ModuleRCS r in pp.FindModulesImplementing<ModuleRCS>())
                            { r.enablePitch = all; r.enableRoll = all; r.enableYaw = true; r.enableX = all; r.enableY = all; r.enableZ = all; }
                    }
                }
                catch (Exception e)
                {
                    Debug.Log("[RSSBalancer] Program lotu, akcja " + key + ": " + e);
                    LastMessage += "\nBłąd akcji " + key + ": " + e.Message;
                }
            }
        }

        private List<EngineGroup> GroupsFor(string which)
        {
            List<EngineGroup> l = new List<EngineGroup>();
            if (which == "all") { if (groups != null) l.AddRange(groups); return l; }
            if (id == null || !id.ok) return l;
            if (which == "boosters" && id.boosters != null) l.Add(id.boosters);
            if (which == "core" && id.core != null) l.Add(id.core);
            if (which == "upper" && id.upper != null) l.Add(id.upper);
            return l;
        }

        // ------------------------------------------------------------------ MechJeb (refleksja)

        private object Core()
        {
            if (vessel == null) return null;
            foreach (Part p in vessel.parts)
                foreach (PartModule pm in p.Modules)
                    if (pm != null && pm.GetType().Name == "MechJebCore" && pm.isEnabled) return pm;
            return null;
        }

        private static object Module(object core, string type)
        {
            MethodInfo m = core.GetType().GetMethod("GetComputerModule", new[] { typeof(string) });
            return m != null ? m.Invoke(core, new object[] { type }) : null;
        }

        private static object Users(object module)
        {
            for (Type t = module.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField("Users", Flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(module);
                PropertyInfo p = t.GetProperty("Users", Flags | BindingFlags.DeclaredOnly);
                if (p != null) return p.GetValue(module, null);
            }
            return null;
        }

        private void PoolCall(object pool, string method)
        {
            MethodInfo m = pool.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(object) }, null);
            if (m != null) m.Invoke(pool, new object[] { this });
        }

        private string MechJebAscent(bool on)
        {
            object core = Core();
            if (core == null) return "MechJeb: brak na statku.";
            object settings = Module(core, "MechJebModuleAscentSettings");
            if (settings == null) return "MechJeb: brak modułu Ascent.";
            PropertyInfo ap = settings.GetType().GetProperty("AscentAutopilot", Flags);
            object autopilot = ap != null ? ap.GetValue(settings, null) : null;
            object pool = autopilot != null ? Users(autopilot) : null;
            if (pool == null) return "MechJeb: nie znaleziono autopilota wznoszenia.";
            PoolCall(pool, on ? "Add" : "Remove");
            return on ? "MechJeb: Ascent Guidance włączone." : "MechJeb: Ascent Guidance wyłączone.";
        }

        // ------------------------------------------------------------------ podejście i lądowanie

        private Vector3d Surf(double lat, double lon, double alt)
        {
            CelestialBody b = vessel.mainBody;
            return b.GetWorldSurfacePosition(lat, lon, alt) - b.position;
        }

        /// <summary>Odległość [m] do progu pasa wzdłuż osi pasa (dodatnia przed progiem).</summary>
        private double RunwayDistance()
        {
            if (!rwySet) return double.NaN;
            if (!approachOn) ChooseRunwayEnd();
            Vector3d rel = (vessel.CoMD - vessel.mainBody.position) - thrPos;
            return -Vector3d.Dot(rel, rwyDir);
        }

        private void ChooseRunwayEnd()
        {
            Vector3d a = Surf(rwyLatA, rwyLonA, rwyAlt), b = Surf(rwyLatB, rwyLonB, rwyAlt);
            Vector3d ab = (b - a).normalized;
            Vector3d v = vessel.srf_velocity;
            Vector3d up = a.normalized;
            Vector3d vh = (v - up * Vector3d.Dot(v, up)).normalized;
            // lądujemy w kierunku bliższym obecnemu kursowi
            if (Vector3d.Dot(vh, ab) >= 0) { thrPos = a; rwyDir = ab; } else { thrPos = b; rwyDir = -ab; }
            Vector3d north = Vector3d.Exclude(up, vessel.mainBody.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            rwyHeading = (Math.Atan2(Vector3d.Dot(rwyDir, east), Vector3d.Dot(rwyDir, north)) * 180 / Math.PI + 360) % 360;
        }

        private object Airplane()
        {
            object core = Core();
            return core != null ? Module(core, "MechJebModuleAirplaneAutopilot") : null;
        }

        private static void SetF(object o, string name, object value)
        {
            FieldInfo f = o.GetType().GetField(name, Flags);
            if (f == null) return;
            if (f.FieldType.Name.StartsWith("Editable"))
            {
                object e = f.GetValue(o);
                PropertyInfo val = e.GetType().GetProperty("Val", Flags);
                if (val != null) val.SetValue(e, Convert.ChangeType(value, val.PropertyType, Inv), null);
            }
            else f.SetValue(o, Convert.ChangeType(value, f.FieldType, Inv));
        }

        private static void Call(object o, string name)
        {
            MethodInfo m = o.GetType().GetMethod(name, Type.EmptyTypes);
            if (m != null) m.Invoke(o, null);
        }

        private void StartApproach()
        {
            if (!rwySet) { LastMessage += "\nBrak „runway” w programie."; return; }
            object ap = Airplane();
            if (ap == null) { LastMessage += "\nMechJeb: brak Aircraft Autopilot."; return; }
            ReleaseAttitude();
            ChooseRunwayEnd();
            object pool = Users(ap);
            if (pool != null) PoolCall(pool, "Add");
            SetF(ap, "PitchDownLimit", 35.0);
            SetF(ap, "PitchUpLimit", 25.0);
            SetF(ap, "RollLimit", 45.0);
            SetF(ap, "BankAngle", 35.0);
            SetF(ap, "HeadingTarget", rwyHeading);
            SetF(ap, "VertSpeedTarget", vessel.verticalSpeed);
            Call(ap, "EnableHeadingHold");
            Call(ap, "EnableVertSpeedHold");
            approachOn = true; gearDone = false; landedDone = false; LandingVerdict = ""; rolloutNoseDown = false; chuteDone = false;
            FlightLogger.Event("Podejście: pas kurs " + rwyHeading.ToString("F0", Inv) + "°, odległość " + (RunwayDistance() / 1000).ToString("F1", Inv) + " km");
        }


        // ------------------------------------------------------------------ lot próbny po kręgu (jak OK-GLI)
        // Analog Burana OK-GLI (BTS-002) z silnikami odrzutowymi latał w latach 1985–88 próby automatycznego podejścia
        // i lądowania. Tu: start z pasa, wznoszenie na 4 km, krąg z czterema zakrętami (lewy), na prostej do lądowania
        // silniki na jałowy bieg i dalej zwykłe podejście Burana (stroma ścieżka 19°, wyrównanie, przyziemienie).
        private bool patternOn, patAirborne, patGearUp;
        private string patternPhase = "";
        private readonly List<Vector2d> patWps = new List<Vector2d>();
        private int patWp;
        private const double PatAlt = 4000;      // wysokość kręgu nad pasem (punkt kluczowy Burana: ~4 km, 12,5 km przed pasem)

        private void StartPattern()
        {
            if (!rwySet) { LastMessage += "\nBrak „runway” w programie."; return; }
            object ap = Airplane();
            if (ap == null) { LastMessage += "\nMechJeb: brak Aircraft Autopilot."; return; }
            ReleaseAttitude();
            // kierunek startu = kierunek pasa najbliższy kursowi nosa; lądujemy w tym samym kierunku (próg „za plecami”)
            Vector3d a = Surf(rwyLatA, rwyLonA, rwyAlt), b = Surf(rwyLatB, rwyLonB, rwyAlt);
            Vector3d ab = (b - a).normalized, up = a.normalized;
            Vector3d fwd = (Vector3d)vessel.ReferenceTransform.up; fwd -= up * Vector3d.Dot(fwd, up);
            if (Vector3d.Dot(fwd, ab) >= 0) { thrPos = a; rwyDir = ab; } else { thrPos = b; rwyDir = -ab; }
            Vector3d north = Vector3d.Exclude(up, vessel.mainBody.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            rwyHeading = (Math.Atan2(Vector3d.Dot(rwyDir, east), Vector3d.Dot(rwyDir, north)) * 180 / Math.PI + 360) % 360;
            double len = (b - a).magnitude;
            // punkty kręgu w układzie pasa: x wzdłuż kierunku lądowania od progu, y w prawo; krąg lewy
            patWps.Clear();
            patWps.Add(new Vector2d(len + 7000, 0));        // wznoszenie po starcie
            patWps.Add(new Vector2d(len + 9000, -6000));    // 1. i 2. zakręt – przecięcie, potem z wiatrem
            patWps.Add(new Vector2d(-14000, -6000));        // koniec odcinka z wiatrem
            patWps.Add(new Vector2d(-16000, 0));            // 3. i 4. zakręt – baza, wyjście na prostą
            patWp = 0; patAirborne = false; patGearUp = false;
            foreach (Part p in vessel.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (!e.EngineIgnited && e.propellants.Exists(pr => pr.name == "IntakeAir")) e.Activate();
            vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            object pool = Users(ap);
            if (pool != null) PoolCall(pool, "Add");
            SetF(ap, "PitchDownLimit", 15.0); SetF(ap, "PitchUpLimit", 20.0);
            SetF(ap, "RollLimit", 30.0); SetF(ap, "BankAngle", 25.0);
            SetF(ap, "HeadingTarget", rwyHeading); SetF(ap, "VertSpeedTarget", 0.0);
            Call(ap, "EnableHeadingHold"); Call(ap, "EnableVertSpeedHold");
            SetF(ap, "SpeedTarget", 200.0); Call(ap, "EnableSpeedHold");
            patternOn = true; patternPhase = "rozbieg";
            FlightLogger.Event("Krąg: start z pasa kurs " + rwyHeading.ToString("F0", Inv) + "°, krąg lewy na " + PatAlt.ToString("F0", Inv) + " m");
        }

        private void StopPattern()
        {
            if (!patternOn) return;
            patternOn = false;
            object ap = Airplane();
            if (ap == null) return;
            Call(ap, "DisableSpeedHold");
            if (!approachOn) { Call(ap, "DisableVertSpeedHold"); Call(ap, "DisableHeadingHold"); object pool = Users(ap); if (pool != null) PoolCall(pool, "Remove"); }
        }

        private void Pattern()
        {
            object ap = Airplane();
            if (ap == null) { patternOn = false; return; }
            CelestialBody b = vessel.mainBody;
            Vector3d pos = vessel.CoMD - b.position, up = pos.normalized;
            Vector3d right = Vector3d.Cross(up, rwyDir).normalized * -1;
            Vector3d rel = pos - thrPos;
            double x = Vector3d.Dot(rel, rwyDir), y = Vector3d.Dot(rel, right);
            double h = vessel.altitude - rwyAlt;
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            double easNow, vSt, aoaNow;
            StallLevel = StallCheck(out easNow, out vSt, out aoaNow);
            // prędkość jak Buran w kręgu: ~1,9× przeciągnięcia (EAS), przeliczona na prędkość prawdziwą
            double rho = Math.Max(0.05, vessel.atmDensity);
            double easT = 1.9 * vSt;
            SetF(ap, "SpeedTarget", easT * Math.Sqrt(1.225 / rho));

            if (!patAirborne)
            {
                double vr = 1.45 * vSt;                     // prędkość oderwania (lot 2026-11-21 15:49: 1,35× – za mało zapasu)
                HookSteer();
                steerHdg = CenterlineHeading(pos, up);
                SetF(ap, "HeadingTarget", steerHdg);
                SetF(ap, "VertSpeedTarget", easNow > vr ? 4.0 : 0.0);
                patternPhase = "rozbieg  EAS " + easNow.ToString("F0", Inv) + " / oderwanie " + vr.ToString("F0", Inv) + " m/s";
                if (!vessel.Landed && h > 30) { patAirborne = true; steerHdg = double.NaN; FlightLogger.Event("Krąg: oderwanie, EAS " + easNow.ToString("F0", Inv) + " m/s"); }
                return;
            }
            if (!patGearUp && h > 150) { patGearUp = true; vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, false); FlightLogger.Event("Krąg: podwozie schowane"); }

            Vector2d wp = patWps[patWp];
            Vector3d target = thrPos + rwyDir * wp.x + right * wp.y;
            Vector3d to = target - pos; to -= up * Vector3d.Dot(to, up);
            double dist = to.magnitude;
            double hdg = (Math.Atan2(Vector3d.Dot(to, east), Vector3d.Dot(to, north)) * 180 / Math.PI + 360) % 360;
            double vs = Math.Max(-25, Math.Min(25, 0.05 * (PatAlt - h)));
            // Wznoszenie tylko z nadmiaru prędkości: Buran z dwoma AL-31 ma ciąg/ciężar ~0,3 – lot 2026-11-21 15:49 wznosił
            // się 25–30 m/s zaraz po oderwaniu, zgubił prędkość, ochrona przed przeciągnięciem kazała schodzić −10 m/s
            // i Buran uderzył w ziemię. Teraz: poniżej 1,6× przeciągnięcia prawie płasko (rozpędzanie), wyżej stopniowo.
            if (vs > 0) vs = Math.Min(vs, Math.Max(1.0, (easNow - 1.55 * vSt) * 0.8));
            if (StallLevel >= 1)
            {
                // nisko nie wolno nurkować: lot poziomy i rozpędzanie; wysoko jak dotąd – w dół po prędkość
                vs = h < 400 ? Math.Min(vs, 0.0) : Math.Min(vs, -10);
                hdg = HeadingOf(vessel.srf_velocity, up, north, east);
            }
            SetF(ap, "HeadingTarget", hdg);
            SetF(ap, "VertSpeedTarget", vs);
            SetF(ap, "RollLimit", StallLevel >= 1 ? 5.0 : easNow < 1.6 * vSt ? 15.0 : 30.0);
            string[] names = { "wznoszenie po starcie", "odcinek przecinający (zakręty 1–2)", "z wiatrem", "baza (zakręty 3–4)" };
            patternPhase = "krąg: " + names[Math.Min(patWp, names.Length - 1)] + ", do punktu " + (dist / 1000).ToString("F1", Inv) + " km, h " + h.ToString("F0", Inv) +
                           " m, EAS " + easNow.ToString("F0", Inv) + " m/s";
            if (Planetarium.GetUniversalTime() > nextALog) { nextALog = Planetarium.GetUniversalTime() + 10; FlightLogger.Event("Krąg: " + patternPhase); }
            if (dist < 1500)
            {
                patWp++;
                if (patWp >= patWps.Count)
                {
                    // na prostej: silniki na jałowy bieg – dalej jak Buran, bez napędu
                    Call(ap, "DisableSpeedHold");
                    FlightInputHandler.state.mainThrottle = 0f;
                    foreach (Part p in vessel.parts)
                        foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                            if (e.EngineIgnited && e.propellants.Exists(pr => pr.name == "IntakeAir")) e.Shutdown();
                    patternOn = false;
                    FlightLogger.Event("Krąg: na prostej, silniki wyłączone – podejście jak Buran");
                    StartApproach();
                }
                else FlightLogger.Event("Krąg: punkt " + patWp + " – " + names[Math.Min(patWp, names.Length - 1)]);
            }
        }


        // ------------------------------------------------------------------ sterowanie kołem przednim na ziemi
        // MechJeb (Aircraft Autopilot) steruje tylko lotkami/sterem kierunku – na rozbiegu i dobiegu przy małej
        // prędkości ster nic nie daje, a koła nie skręcają (lot próbny 19:22: Buran zjechał z pasa w lewo).
        private Vessel steerVessel;
        private double steerHdg = double.NaN;

        private void HookSteer()
        {
            if (steerVessel == vessel) return;
            if (steerVessel != null) steerVessel.OnFlyByWire -= SteerFbw;
            if (vessel != null) vessel.OnFlyByWire += SteerFbw;
            steerVessel = vessel;
        }

        private void SteerFbw(FlightCtrlState s)
        {
            if (double.IsNaN(steerHdg) || vessel == null || !vessel.Landed) return;
            Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
            Vector3d north = Vector3d.Exclude(up, vessel.mainBody.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            Vector3d fwd = (Vector3d)vessel.ReferenceTransform.up;
            double hdg = HeadingOf(fwd, up, north, east);
            double err = ((steerHdg - hdg + 540) % 360) - 180;
            double gain = Math.Min(1.0, 25.0 / Math.Max(1, vessel.srfSpeed));     // przy dużej prędkości mniejszy skręt kółka
            s.wheelSteer = Mathf.Clamp((float)(-err / 12.0 * gain), -1f, 1f);     // A (+1) = w lewo
            // po przyziemieniu: elewony lekko w dół dopiero po opuszczeniu nosa (< ~290 km/h). Lot 23:23: −0,5 od razu przy
            // 400 km/h mocno docisnął przednie koło („taczka”) – przy 330 km/h Buranem zarzuciło o 95° w lewo.
            if (rolloutNoseDown && vessel.srfSpeed > 15 && vessel.srfSpeed < 80) s.pitch = -0.15f;
        }

        /// <summary>Kurs na punkt na osi pasa ~400 m przed nosem (koryguje też zjechanie w bok).</summary>
        private double CenterlineHeading(Vector3d pos, Vector3d up)
        {
            Vector3d rel = pos - thrPos;
            double along = Vector3d.Dot(rel, rwyDir);
            Vector3d aim = thrPos + rwyDir * (along + Math.Max(400, 8 * vessel.srfSpeed));   // szybko – dalszy punkt, łagodniej
            Vector3d to = aim - pos; to -= up * Vector3d.Dot(to, up);
            Vector3d north = Vector3d.Exclude(up, vessel.mainBody.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            return HeadingOf(to, up, north, east);
        }

        private void StopApproach()
        {
            if (!approachOn) return;
            approachOn = false; rolloutNoseDown = false;
            if (recovering) { recovering = false; ReleaseAttitude(); }
            object ap = Airplane();
            if (ap == null) return;
            Call(ap, "DisableVertSpeedHold");
            Call(ap, "DisableHeadingHold");
            object pool = Users(ap);
            if (pool != null) PoolCall(pool, "Remove");
        }

        /// <summary>
        /// Uproszczone podejście jak Buran/Shuttle: stroma ścieżka 18° (punkt celowania 2 km przed progiem),
        /// wyrównanie od 550 m na ścieżkę 1,5° (przyziemienie 600 m za progiem), przyziemienie z −1 m/s,
        /// podwozie poniżej 300 m, po przyziemieniu hamulce i spadochron.
        /// </summary>
        private double nextALog;
        private bool excessTurn, brakesOut, lowEnergy, rolloutNoseDown, chuteDone;

        // ------------------------------------------------------------------ wyprowadzanie z korkociągu
        // Lot 2026-11-19 21:20: przy przeciągnięciu autopilot samolotowy MechJeba (kurs + prędkość pionowa) tylko
        // pogarszał sprawę – Buran wirował od 21 do 10 km. Teraz przy przeciągnięciu (α > 25° albo EAS < 1,05×
        // przeciągnięcia) odłączamy autopilot samolotowy, włączamy wszystkie osie RCS (w prawdziwym Buranie RCS
        // w atmosferze działał tylko do pewnej prędkości – tu przekłamanie dla bezpieczeństwa) i ustawiamy nos
        // na wektor prędkości (α 3°), skrzydła poziomo. Koniec, gdy przez 3 s: EAS > 1,35× przeciągnięcia i α < 12°.
        private bool recovering;
        private double recoverOk = -1, nextRLog;

        private static void SetRcsAxes(Vessel v, bool all)
        {
            foreach (Part pp in v.parts)
                foreach (ModuleRCS r in pp.FindModulesImplementing<ModuleRCS>())
                { r.enablePitch = all; r.enableRoll = all; r.enableYaw = true; r.enableX = all; r.enableY = all; r.enableZ = all; }
        }

        private bool SpinRecovery(object ap, double eas, double vSt, double aoa, double hr, double d, double h)
        {
            double now = Planetarium.GetUniversalTime();
            bool bad = aoa > 25 || eas < 1.05 * vSt;
            if (!recovering)
            {
                if (!bad || hr < 300) return false;
                recovering = true; recoverOk = -1; nextRLog = 0;
                Call(ap, "DisableVertSpeedHold");
                Call(ap, "DisableHeadingHold");
                SetRcsAxes(vessel, true);
                vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                entryGuide = false; holdMode = Hold.SrfVel; holdAttitude = true;
                ScreenMessages.PostScreenMessage("KORKOCIĄG – wyprowadzam: nos na wektor prędkości, skrzydła poziomo", 4f, ScreenMessageStyle.UPPER_CENTER, Color.red);
                FlightLogger.Event("Wyprowadzanie z korkociągu: start – α " + aoa.ToString("F0", Inv) + "°, EAS " + eas.ToString("F0", Inv) + " m/s, h=" + h.ToString("F0", Inv) + " m");
            }
            holdRot = Quaternion.AngleAxis(-3f, Vector3.right);
            HoldAttitude();
            bool ok = eas > 1.35 * vSt && aoa < 12;
            if (!ok || hr < 150) recoverOk = -1;
            else if (recoverOk < 0) recoverOk = now;
            if ((ok && now - recoverOk > 3) || hr < 150)
            {
                recovering = false;
                ReleaseAttitude();
                SetRcsAxes(vessel, false);
                vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
                SetF(ap, "VertSpeedTarget", vessel.verticalSpeed);
                SetF(ap, "HeadingTarget", HeadingOf(vessel.srf_velocity, (vessel.CoMD - vessel.mainBody.position).normalized,
                    Vector3d.Exclude((vessel.CoMD - vessel.mainBody.position).normalized, vessel.mainBody.transform.up).normalized,
                    Vector3d.Cross((vessel.CoMD - vessel.mainBody.position).normalized, Vector3d.Exclude((vessel.CoMD - vessel.mainBody.position).normalized, vessel.mainBody.transform.up).normalized).normalized));
                Call(ap, "EnableHeadingHold");
                Call(ap, "EnableVertSpeedHold");
                FlightLogger.Event("Wyprowadzanie z korkociągu: koniec – EAS " + eas.ToString("F0", Inv) + " m/s, α " + aoa.ToString("F0", Inv) + "°, h=" + h.ToString("F0", Inv) + " m");
                return false;
            }
            approachPhase = "KORKOCIĄG – wyprowadzam (RCS, nos na wektor prędkości, skrzydła poziomo), EAS " + eas.ToString("F0", Inv) + " m/s, α " + aoa.ToString("F0", Inv) + "°";
            if (now > nextRLog) { nextRLog = now + 5; FlightLogger.Event("Podejście: " + approachPhase + " d=" + (d / 1000).ToString("F1", Inv) + " km h=" + h.ToString("F0", Inv) + " m"); }
            return true;
        }

        // ------------------------------------------------------------------ czujnik przeciągnięcia
        // Jak w samolocie: ostrzega, gdy kąt natarcia zbliża się do krytycznego albo prędkość równoważna (EAS –
        // ta sama nośność na każdej wysokości) spada blisko prędkości przeciągnięcia. Prędkość przeciągnięcia Burana
        // ~70 m/s EAS przy masie 75 t (Buran przyziemia przy 300–320 km/h ≈ 1,2× prędkości przeciągnięcia), skalowana z pierwiastkiem z masy.
        public string StallInfo = "";
        public int StallLevel;      // 0 ok, 1 ostrzeżenie, 2 przeciągnięcie
        private float nextStallMsg;

        private static double HeadingOf(Vector3d v, Vector3d up, Vector3d north, Vector3d east)
        {
            Vector3d h = v - up * Vector3d.Dot(v, up);
            return (Math.Atan2(Vector3d.Dot(h, east), Vector3d.Dot(h, north)) * 180 / Math.PI + 360) % 360;
        }

        private int StallCheck(out double eas, out double vStall, out double aoa)
        {
            eas = Math.Sqrt(2 * vessel.dynamicPressurekPa * 1000 / 1.225);
            vStall = 70 * Math.Sqrt(Math.Max(10, vessel.totalMass) / 75.0);
            Vector3d vel = vessel.srf_velocity;
            aoa = vel.magnitude > 1 ? Vector3d.Angle((Vector3d)vessel.ReferenceTransform.up, vel) : 0;
            if (vessel.mach > 2 || vessel.LandedOrSplashed || vessel.altitude > 60000) { StallInfo = ""; return 0; }
            int lvl = aoa > 22 || eas < 1.05 * vStall ? 2 : aoa > 16 || eas < 1.25 * vStall ? 1 : 0;
            StallInfo = (lvl == 2 ? "PRZECIĄGNIĘCIE! " : lvl == 1 ? "Ostrzeżenie: blisko przeciągnięcia. " : "") +
                        "EAS " + eas.ToString("F0", Inv) + " m/s (przeciągnięcie ~" + vStall.ToString("F0", Inv) + "), kąt natarcia " + aoa.ToString("F0", Inv) + "°";
            if (lvl > 0 && Time.realtimeSinceStartup > nextStallMsg)
            {
                nextStallMsg = Time.realtimeSinceStartup + 4f;
                ScreenMessages.PostScreenMessage(lvl == 2 ? "PRZECIĄGNIĘCIE – oddaję nos w dół" : "Ostrzeżenie: blisko przeciągnięcia", 3f,
                    ScreenMessageStyle.UPPER_CENTER, lvl == 2 ? Color.red : Color.yellow);
                FlightLogger.Event("Czujnik przeciągnięcia: " + StallInfo);
            }
            return lvl;
        }

        private void Approach()
        {
            object ap = Airplane();
            if (ap == null) { approachOn = false; return; }
            CelestialBody b = vessel.mainBody;
            Vector3d pos = vessel.CoMD - b.position;
            Vector3d up = pos.normalized;
            Vector3d rel = pos - thrPos;
            double d = -Vector3d.Dot(rel, rwyDir);                                // do progu wzdłuż osi
            Vector3d right = Vector3d.Cross(up, rwyDir).normalized * -1;          // w prawo od osi (patrząc w kierunku lądowania)
            double y = Vector3d.Dot(rel, right);                                  // odchyłka boczna
            double h = vessel.altitude - rwyAlt;
            double hr = vessel.heightFromTerrain > 0 && vessel.heightFromTerrain < 2000 ? vessel.heightFromTerrain : h;   // wysokość nad gruntem (radar)
            Vector3d v = vessel.srf_velocity;
            double vh = (v - up * Vector3d.Dot(v, up)).magnitude;

            if (vessel.Landed)
            {
                if (!landedDone)
                {
                    landedDone = true;
                    Call(ap, "DisableVertSpeedHold");
                    vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false); brakesOut = false;   // hamulce kół dopiero poniżej ~250 km/h (lot 17:54: hamowanie przy 427 km/h → obrót na pasie)
                    rolloutNoseDown = true;
                    // na pasie czy obok? (d – odległość przed progiem; za progiem ujemna)
                    double len = (Surf(rwyLatB, rwyLonB, rwyAlt) - Surf(rwyLatA, rwyLonA, rwyAlt)).magnitude;
                    double past = -d;                                              // metry za progiem
                    bool onRwy = past >= -50 && past <= len + 50 && Math.Abs(y) <= 60;
                    double vsTd = Math.Abs(vessel.verticalSpeed);
                    string verdict = onRwy
                        ? "NA PASIE – " + past.ToString("F0", Inv) + " m za progiem, " + Math.Abs(y).ToString("F0", Inv) + " m od osi"
                        : "POZA PASEM – " + (Math.Sqrt(Math.Max(0, Math.Max(-past, past - len)) * Math.Max(0, Math.Max(-past, past - len)) + y * y) / 1000).ToString("F1", Inv) +
                          " km od pasa (" + (past < 0 ? (-past / 1000).ToString("F1", Inv) + " km przed progiem" : past > len ? ((past - len) / 1000).ToString("F1", Inv) + " km za końcem" : "na wysokości pasa") +
                          ", " + (Math.Abs(y) / 1000).ToString("F1", Inv) + " km w bok)";
                    LandingVerdict = verdict;
                    FlightLogger.Event("Przyziemienie " + verdict + ", " + (vessel.srfSpeed * 3.6).ToString("F0", Inv) + " km/h, opadanie " + vsTd.ToString("F1", Inv) + " m/s");
                    ScreenMessages.PostScreenMessage("Przyziemienie " + verdict, 8f, ScreenMessageStyle.UPPER_CENTER, onRwy ? Color.green : Color.red);
                    approachPhase = "dobieg";
                }
                HookSteer();
                bool wb = vessel.srfSpeed < 70;
                if (wb != brakesOut) { brakesOut = wb; vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, wb); }
                // Spadochrony hamujące dopiero poniżej ~250 km/h (lot 18:57: spadochron na ogonie, nad środkiem masy,
                // przy 330 km/h zadarł nos i Buran znów wzleciał na ~30 m – „kangur”). W Buranie: po opuszczeniu przedniej goleni.
                if (wb && !chuteDone)
                {
                    chuteDone = true;
                    foreach (Part p in vessel.parts)
                        foreach (ModuleParachute c in p.FindModulesImplementing<ModuleParachute>())
                            if (c.deploymentState == ModuleParachute.deploymentStates.STOWED) c.Deploy();
                }
                steerHdg = CenterlineHeading(pos, up);
                SetF(ap, "HeadingTarget", steerHdg);
                if (vessel.srfSpeed < 3) { steerHdg = double.NaN; vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true); StopApproach(); approachPhase = "stop"; }
                return;
            }

            Vector3d relH = rel - up * Vector3d.Dot(rel, up);
            double dThr = relH.magnitude;                                         // do progu po ziemi (prosto)
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            double vs, hdg;
            // „za blisko / za progiem” tylko wysoko – lot 2026-11-20 16:19: Buran był idealnie na osi 3,6 km przed progiem
            // na 950 m, a przy d < 3 km warunek „d < 3000” przełączył go na dolot do punktu 12 km przed pasem – odbił w bok
            // i usiadł 4 km od pasa. Nisko na krótkiej prostej zostajemy na ścieżce.
            bool farAway = dThr > 30000 || Math.Abs(y) > 8000 || (d < 3000 && h > 1500) || d < -1500;

            if (farAway && hr >= 20)
            {
                // DOLOT: lecimy na punkt 12 km przed progiem na osi pasa (jak wejście w okrąg HAC w Buranie/Shuttle),
                // kąt schodzenia z energii – tak, żeby dolecieć, nie wytracając prędkości (wcześniej Buran 150 km
                // obok pasa „wyrównywał” na 500 m nad stepem, aż przeciągnął).
                Vector3d aim = thrPos - rwyDir * 12000;
                Vector3d to = aim - pos; to -= up * Vector3d.Dot(to, up);
                hdg = (Math.Atan2(Vector3d.Dot(to, east), Vector3d.Dot(to, north)) * 180 / Math.PI + 360) % 360;
                double togo = to.magnitude + 12000;
                double gNeed = Math.Atan2(Math.Max(h, 0), togo) * 180 / Math.PI;
                double gamma = Math.Max(4, Math.Min(14, gNeed));
                vs = -vh * Math.Tan(gamma * Math.PI / 180);
                approachPhase = "dolot do pasa " + (togo / 1000).ToString("F0", Inv) + " km, schodzenie " + gamma.ToString("F0", Inv) + "°" +
                                (gNeed < 4 ? " (za mało wysokości – nie doleci)" : "");
                lowEnergy = gNeed < 8;
                // NADMIAR ENERGII (jest wyżej, niż trzeba): krąży wokół punktu dolotu jak Buran/Shuttle na okręgu HAC,
                // aż kąt potrzebny do dolotu spadnie; do tego hamulec aerodynamiczny (ster kierunku), gdy za szybko.
                // wejście w krążenie przy > 20°, wyjście przy < 16° (lot 2026-11-20 12:29: wyjście dopiero przy 10° zostawiło
                // za mało energii – Buran usiadł 6 km przed progiem); resztę nadmiaru zjada stroma ścieżka i hamulec
                if (gNeed > 20 || (excessTurn && gNeed > 16))
                {
                    excessTurn = true;
                    hdg = (hdg + 75) % 360;
                    approachPhase = "NADMIAR ENERGII – krążę nad punktem dolotu (" + gNeed.ToString("F0", Inv) + "° potrzeba), " + (togo / 1000).ToString("F0", Inv) + " km";
                }
                else excessTurn = false;
            }
            else
            {
                // kurs: punkt na osi pasa przed nami (marchewka)
                double L = Math.Max(1500, Math.Min(8000, Math.Max(d, 0) * 0.4));
                Vector3d carrot = thrPos + rwyDir * (L - d);     // L przed Buranem na osi (także za progiem – lot 17:54: punkt stał 1,5 km za progiem i po jego minięciu kurs uciekł o 20°)
                Vector3d to = carrot - pos;
                to -= up * Vector3d.Dot(to, up);
                hdg = (Math.Atan2(Vector3d.Dot(to, east), Vector3d.Dot(to, north)) * 180 / Math.PI + 360) % 360;

                // Końcówka jak w Buranie/Shuttle: stroma ścieżka 19° celuje ~2,5 km przed progiem, na ~450 m wyrównanie
                // wstępne na łagodną ścieżkę 1,5° z przyziemieniem ~600 m za progiem, poniżej 30 m końcowe wyrównanie.
                // Lot 17:54: ścieżka celowała 900 m przed progiem, wyrównanie od 550 m nie zdążyło zejść – próg na 130 m
                // przy 545 km/h, przyziemienie 2,5 km za progiem przy 427 km/h.
                if (hr < 30 && Math.Abs(y) < 1500 && d < 2000) { vs = -Math.Max(1.5, hr * 0.1); approachPhase = "wyrównanie końcowe"; }   // 0,8 m/s dawało ~1,8 km „pływania” nad pasem
                else if (hr < 20) { vs = -1.0; approachPhase = "przyziemienie"; }
                else if (h < 450 && d < 8000 && d > -3000 && Math.Abs(y) < 1500)
                {
                    double ht = (d + 600) * Math.Tan(1.5 * Math.PI / 180);
                    vs = -vh * Math.Tan(1.5 * Math.PI / 180) + 0.15 * (ht - h);
                    vs = Math.Max(vs, -30); approachPhase = "wyrównanie (1,5°)";
                }
                else
                {
                    // stroma ścieżka jak w Buranie: 17–22° (tu 19°), punkt kluczowy ~12,5 km przed pasem na ~4 km
                    double ht = (d - 2500) * Math.Tan(19 * Math.PI / 180);
                    vs = -vh * Math.Tan(19 * Math.PI / 180) + 0.08 * (ht - h);
                    approachPhase = "stroma ścieżka 19°";
                    double vsMin = -vh * Math.Tan(10 * Math.PI / 180);
                    lowEnergy = false;
                    if (vs > vsMin) { vs = vsMin; approachPhase = "PONIŻEJ ścieżki – najlepszy lot ślizgowy"; lowEnergy = true; }
                }
            }
            // Wyrównanie nad terenem zawsze poniżej ~550 m nad gruntem – także gdy Buran nie jest nad samym pasem
            // (lot 12:29: usiadł 6 km przed progiem bez wyrównania, 385 km/h i 8 m/s opadania – wyrównanie było tylko „przy pasie”).
            if (hr >= 20 && hr < 450 && !approachPhase.StartsWith("wyrównanie"))
            {
                vs = Math.Max(-25, -Math.Max(1.5, hr * 0.08));
                approachPhase = "wyrównanie nad terenem";
            }
            SetF(ap, "HeadingTarget", hdg);

            // Utrzymanie prędkości (prędkość równoważna EAS – to ona decyduje o nośności, nie prędkość prawdziwa):
            // wysoko w rzadkim powietrzu Buran musi lecieć dużo szybciej i stromiej. Lot 2026-11-19 17:32: na 20 km
            // autopilot trzymał łagodne schodzenie 5°, prędkość spadła z 350 do 30 m/s i Buran wpadł w płaski korkociąg.
            // Cel: EAS ~150 m/s w dolocie, ~110 m/s na ścieżce, przy przyziemieniu zostawiamy wyrównanie.
            double easNow, vSt, aoaNow;
            StallLevel = StallCheck(out easNow, out vSt, out aoaNow);
            if (hr >= 20 && approachPhase != "przyziemienie")
            {
                bool flare = approachPhase.StartsWith("wyrównanie");
                // Buran: szybowanie przed lądowaniem 450–540 km/h (≈125–150 m/s), przyziemienie 300–320 km/h (≈86 m/s)
                // przy braku energii – prędkość najlepszego szybowania (~1,4× przeciągnięcia), nie nurkowanie po 140 m/s EAS
                // (lot 12:29: trzymanie 140 m/s dawało opadanie 25–30 m/s i Buran zjadł zapas wysokości)
                double easT = flare ? 1.15 * vSt : lowEnergy ? 1.4 * vSt : farAway ? Math.Max(150, 1.8 * vSt) : Math.Max(140, 1.6 * vSt);
                if (easNow < easT)
                {
                    vs = Math.Min(vs, -vh * Math.Tan(4 * Math.PI / 180) - 1.5 * (easT - easNow));
                    approachPhase += " (EAS " + easNow.ToString("F0", Inv) + " m/s – za wolno, nos w dół)";
                }
                // Przechylenie zabiera nośność – przy małej prędkości ostrożne zakręty (lot 18:25: zakręt 45° na 17 km
                // przy EAS ~85 m/s skończył się korkociągiem).
                double rollLim = easNow < 1.3 * vSt ? 10 : easNow < 1.6 * vSt ? 20 : easNow < 2.0 * vSt ? 30 : 45;
                if (SpinRecovery(ap, easNow, vSt, aoaNow, hr, d, h)) return;
                if (StallLevel == 2)
                {
                    // przeciągnięcie / korkociąg: skrzydła poziomo, kurs bieżący, nos w dół – aż odzyska prędkość
                    vs = Math.Min(vs, -vh * Math.Tan(25 * Math.PI / 180) - 30);
                    rollLim = 0;
                    SetF(ap, "HeadingTarget", vessel.srf_velocity.magnitude > 1 ? HeadingOf(vessel.srf_velocity, up, north, east) : hdg);
                    approachPhase = "PRZECIĄGNIĘCIE – wyprowadzam (skrzydła poziomo, nos w dół)";
                }
                SetF(ap, "RollLimit", rollLim);
                // hamulec aerodynamiczny przy nadmiarze prędkości
                // hamulec (rozchylony ster kierunku w Buranie) trzyma prędkość na stromej ścieżce
                bool wantBrakes = easNow > (farAway ? 200 : 155) || (excessTurn && easNow > 170) || (flare && easNow > 1.25 * vSt && hr > 15);
                if (wantBrakes != brakesOut) { brakesOut = wantBrakes; vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, wantBrakes); }
            }
            vs = Math.Max(-250, Math.Min(0, vs));
            SetF(ap, "VertSpeedTarget", vs);

            if (!gearDone && hr < 300)
            {
                gearDone = true;
                vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, true);
                FlightLogger.Event("Podwozie wypuszczone");
            }
            if (Planetarium.GetUniversalTime() > nextALog) { nextALog = Planetarium.GetUniversalTime() + 10; FlightLogger.Event("Podejście: " + approachPhase + " d=" + (d / 1000).ToString("F1", Inv) + " km h=" + h.ToString("F0", Inv) + " m v=" + vessel.srfSpeed.ToString("F0", Inv)); }
            approachPhase += "  do progu " + (dThr / 1000).ToString("F1", Inv) + " km, d=" + (d / 1000).ToString("F1", Inv) + " km  h=" + h.ToString("F0", Inv) + " m  boczna=" + y.ToString("F0", Inv) + " m";
        }

        // ------------------------------------------------------------------ naprowadzanie przy wejściu w atmosferę
        // Jak w Buranie/Shuttle: kąt natarcia z programu, a zasięg reguluje przechylenie (bank).
        // Przewidywany zasięg lotu ślizgowego w równowadze: R = (L/D)·cosσ · r/2 · ln(1 / (1 − v²/(g·r))).
        // cosσ dobieramy tak, by R = odległość do lotniska; strona przechylenia – w stronę lotniska,
        // z przerzutami (S-zakręty), gdy błąd kursu przekroczy próg. L/D na żywo z FAR.

        private bool entryGuide;
        private float gAoa, gYaw, gAoaCmd, aoaBias;
        private bool aoaByMach;

        /// <summary>Program kąta natarcia Burana α(M) z lotu 1K1: 39° w górnej atmosferze, 22° przy M = 5, 10° przy M = 2.</summary>
        private static float AoAForMach(double m)
        {
            if (m >= 12) return 39f;
            if (m >= 5) return (float)(22 + (39 - 22) * (m - 5) / 7.0);
            if (m >= 2) return (float)(10 + (22 - 10) * (m - 2) / 3.0);
            return 10f;
        }
        private bool ldSeen;

        // Doskonałość Burana w KSP z łatką „BuranWing” – POMIAR z lotu 2026-11-19 21:20 (α wg programu, częściowo
        // zmniejszony przy oszczędzaniu energii): M 23 → 0,7–0,8, M 20–13 → 0,9–1,0, M 11 → 1,15, M 9–4 → 1,45–1,55,
        // M 3–2 → 1,7, M 1 → 1,4. Bieżący pomiar i tak skaluje tablicę.
        private static readonly double[,] LdTab = { { 7600, 0.78 }, { 6500, 0.82 }, { 5700, 0.9 }, { 4900, 1.0 }, { 3900, 1.0 }, { 3400, 1.15 },
                                                    { 3000, 1.45 }, { 2600, 1.55 }, { 1650, 1.52 }, { 1240, 1.54 }, { 900, 1.67 }, { 580, 1.73 }, { 320, 1.42 }, { 0, 1.4 } };
        private static double LdTable(double v)
        {
            if (v >= LdTab[0, 0]) return LdTab[0, 1];
            for (int i = 0; i < LdTab.GetLength(0) - 1; i++)
                if (v <= LdTab[i, 0] && v >= LdTab[i + 1, 0])
                    return LdTab[i + 1, 1] + (LdTab[i, 1] - LdTab[i + 1, 1]) * (v - LdTab[i + 1, 0]) / (LdTab[i, 0] - LdTab[i + 1, 0]);
            return LdTab[LdTab.GetLength(0) - 1, 1];
        }

        /// <summary>Zasięg lotu ślizgowego w równowadze z L/D zależną od prędkości: ∫ L/D(v) · r/2 · d ln(1/(1 − v²/gr)).</summary>
        // Koniec fazy wejścia jak w Buranie/Shuttle (TAEM): ~800 m/s (M 2,5), ~100 km przed lotniskiem, ~25–30 km wysokości.
        // Lot 2026-11-20 10:11: prowadzenie celowało „dolecieć nad lotnisko z prędkością → 0” w modelu, który zaniża
        // zasięg przy małych prędkościach (poddźwiękowo L/D ~5, a do tego wysokość) – Buran był nad lotniskiem na 46 km
        // z M 4,5 i przeleciał 190 km dalej. Teraz energia jest wytracana do punktu TAEM, resztę robi podejście (krążenie).
        private const double TaemV = 800, TaemDist = 100000;
        private static double RangeIntegral(double v0, double r, double g)
        {
            double s = 0, dv = 20;
            for (double v = v0; v > TaemV; v -= dv)
            {
                double e1 = Math.Min(0.999, v * v / (g * r)), e0 = Math.Min(0.999, (v - dv) * (v - dv) / (g * r));
                s += LdTable(v - dv / 2) * r / 2 * (Math.Log(1 / (1 - e1)) - Math.Log(1 / (1 - e0)));
            }
            return s;
        }

        private double bankCmd, rollSign = 1, ldF = 0.8, gEas = -1, gVst = 1, calStart = -1, calHdg0, calBank, nextGLog;
        private bool calDone;
        public string EntryInfo = "";
        private static Type farApi; private static bool farLooked;

        private static double FarCall(string m)
        {
            if (!farLooked)
            {
                farLooked = true;
                foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                {
                    Type t = a.assembly.GetType("FerramAerospaceResearch.FARAPI");
                    if (t != null) { farApi = t; break; }
                }
            }
            if (farApi == null) return double.NaN;
            MethodInfo mi = farApi.GetMethod(m, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (mi == null) return double.NaN;
            object r = mi.Invoke(null, null);
            return r is double ? (double)r : r is float ? (float)r : double.NaN;
        }

        private static double GreatCircle(double lat1, double lon1, double lat2, double lon2, out double bearing)
        {
            double f1 = lat1 * Math.PI / 180, f2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
            double c = Math.Sin(f1) * Math.Sin(f2) + Math.Cos(f1) * Math.Cos(f2) * Math.Cos(dl);
            double ang = Math.Acos(Math.Max(-1, Math.Min(1, c)));
            bearing = (Math.Atan2(Math.Sin(dl) * Math.Cos(f2), Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl)) * 180 / Math.PI + 360) % 360;
            return ang;
        }

        private void EntryGuidance()
        {
            Vessel v = vessel; CelestialBody b = v.mainBody;
            Vector3d pos = v.CoMD - b.position, up = pos.normalized;
            Vector3d vel = v.srf_velocity;
            double V = vel.magnitude;
            if (V < 100 || !siteSet) return;
            Vector3d vh = vel - up * Vector3d.Dot(vel, up);
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            double hdg = (Math.Atan2(Vector3d.Dot(vh, east), Vector3d.Dot(vh, north)) * 180 / Math.PI + 360) % 360;
            double brg;
            double ang = GreatCircle(v.latitude, v.longitude, siteLat, siteLon, out brg);
            double rgo = ang * b.Radius;                                            // m do lotniska po powierzchni
            double dpsi = ((brg - hdg + 540) % 360) - 180;                          // + = lotnisko na prawo

            double cl = FarCall("ActiveVesselLiftCoeff"), cd = FarCall("ActiveVesselDragCoeff");
            if (!double.IsNaN(cl) && !double.IsNaN(cd) && cd > 1e-4 && v.dynamicPressurekPa > 0.3)
            { ldF += (Math.Max(0.2, Math.Min(6, cl / cd)) - ldF) * 0.05; ldSeen = true; }
            else if (double.IsNaN(cl) && v.dynamicPressurekPa > 0.3)
            {
                // bez FAR: L/D z przyspieszenia aerodynamicznego (przyspieszenie − grawitacja), rozłożonego na oś prędkości
                Vector3d aAero = v.acceleration - FlightGlobals.getGeeForceAtPosition(v.CoMD);
                Vector3d vn = vel / V;
                double drag = -Vector3d.Dot(aAero, vn);
                double lift = (aAero + vn * drag).magnitude;
                if (drag > 0.5) { ldF += (Math.Max(0.2, Math.Min(6, lift / drag)) - ldF) * 0.02; ldSeen = true; }
            }
            double r = pos.magnitude, g = b.gravParameter / (r * r);
            double e = Math.Min(0.999, V * V / (g * r));
            double kR = r / 2 * Math.Log(1 / (1 - e));                              // zasięg przy L/D = 1, σ = 0
            // Doskonałość zmienia się z prędkością (program α(M)): ~0,35 przy 39°, ~1,3 przy 18°, ~1,7 przy 10°
            // (pomiar z lotu 2026-11-19 20:05). Zamiast stałej L/D całkujemy zasięg po prędkości z tablicą,
            // przeskalowaną bieżącym pomiarem (ldF / tablica przy obecnej prędkości).
            double calib = ldSeen ? Math.Max(0.3, Math.Min(2.5, ldF / LdTable(V))) : 1.0;
            double rMax = RangeIntegral(V, r, g) * calib;
            double rgoEff = Math.Max(0, rgo - TaemDist);
            double cosS = rMax > 1 ? rgoEff / rMax : (rgoEff > 0 ? 2 : 0);

            double mag;
            string note = "";
            if (v.dynamicPressurekPa < 0.05) { mag = 0; note = "za rzadkie powietrze"; }
            else if (V <= TaemV) { mag = 0; note = "TAEM – kurs na lotnisko"; cosS = 1; }
            else if (cosS >= 1) { mag = 0; note = "ZA MAŁO ENERGII – lecę bez przechylenia"; }
            else mag = Math.Acos(Math.Max(0, cosS)) * 180 / Math.PI;
            mag = Math.Min(mag, 80);

            // strona przechylenia: w stronę lotniska; przerzut, gdy błąd kursu przekroczy próg (zawęża się z prędkością)
            double dead = V > 4000 ? 12 : V > 2000 ? 8 : 5;
            double side = Math.Sign(bankCmd);
            if (side == 0 || Math.Abs(bankCmd) < 3) side = dpsi >= 0 ? 1 : -1;
            else if (side > 0 && dpsi < -dead) side = -1;
            else if (side < 0 && dpsi > dead) side = 1;
            // Wyrównanie kursu: gdy lotnisko ucieka w bok (duży błąd kursu przy małej odległości), kurs ma pierwszeństwo
            // przed zasięgiem – inaczej Buran przelatuje obok lotniska z wystarczającą energią, ale w złą stronę.
            double headLimit = V < 3000 ? 20 : V < 5000 ? 30 : 45;
            if (v.dynamicPressurekPa >= 0.05 && (Math.Abs(dpsi) > headLimit || V < 1500))
            {
                mag = Math.Max(mag, Math.Min(V < 1500 ? 45 : 60, Math.Abs(dpsi) * 1.5));
                side = dpsi >= 0 ? 1 : -1;
                note = note.StartsWith("ZA MAŁO") ? note + ", wyrównuję kurs" : "wyrównuję kurs";
            }
            // Za mało energii przy dużej prędkości: zmniejsz kąt natarcia (większa doskonałość) – do 12° mniej, min. 22°.
            aoaBias = 0;
            if (cosS > 1 && V > 1500 && v.dynamicPressurekPa >= 0.05)
                aoaBias = (float)Math.Min(18, (cosS - 1) * 40);
            // Duży nadmiar energii (lot 2026-11-19 18:25: Buran przeleciał nad lotniskiem z prędkością naddźwiękową
            // mimo przechylenia 70°): większy kąt natarcia = większy opór, do +8° (maks. 45°).
            else if (cosS < 0.6 && V > 1500 && v.dynamicPressurekPa >= 0.05)
                aoaBias = -(float)Math.Min(8, (0.6 - cosS) * 25);
            // Mała prędkość równoważna nisko: przechylenie zabiera nośność – ograniczamy je, żeby nie przeciągnąć
            double easG = Math.Sqrt(2 * v.dynamicPressurekPa * 1000 / 1.225);
            double vStG = 70 * Math.Sqrt(Math.Max(10, v.totalMass) / 75.0);
            gEas = easG; gVst = vStG; hudRMax = rMax; hudDpsi = dpsi; hudCos = cosS;
            // tylko przy małym Machu (< ~2,5) – wyżej kąt natarcia trzyma sterowanie, a przechylenie jest potrzebne do
            // wytracania energii i skrętu (lot 10:11: na 46 km przy M 4,5 EAS 53 m/s wyłączył przechylenie nad lotniskiem)
            if (V < 800 && easG < 1.7 * vStG)
            {
                mag = Math.Min(mag, easG < 1.4 * vStG ? 0 : 20);
                note += (note.Length > 0 ? ", " : "") + "mała prędkość – ograniczam przechylenie";
            }
            double target = side * mag;
            double dt = TimeWarp.fixedDeltaTime;
            bankCmd += Math.Max(-8 * dt, Math.Min(8 * dt, target - bankCmd));     // max 8°/s – przerzut trwa kilkanaście s

            // samokalibracja znaku przechylenia (nie wiemy na pewno, w którą stronę MechJeb liczy „roll”):
            // po 15 s z przechyleniem > 25° kurs musi iść w stronę przechylenia, inaczej odwracamy znak.
            if (!calDone)
            {
                if (Math.Abs(bankCmd) > 25 && attError < 10 && v.dynamicPressurekPa > 2)
                {
                    if (calStart < 0) { calStart = Planetarium.GetUniversalTime(); calHdg0 = hdg; calBank = Math.Sign(bankCmd); }
                    else if (Math.Sign(bankCmd) != calBank) calStart = -1;
                    else if (Planetarium.GetUniversalTime() - calStart > 15)
                    {
                        double turn = ((hdg - calHdg0 + 540) % 360) - 180;
                        if (Math.Abs(turn) > 0.5)
                        {
                            calDone = true;
                            if (Math.Sign(turn) != calBank) { rollSign = -rollSign; FlightLogger.Event("Wejście: odwrócono znak przechylenia (kalibracja)"); }
                            else FlightLogger.Event("Wejście: znak przechylenia potwierdzony");
                        }
                        calStart = -1;
                    }
                }
                else calStart = -1;
            }

            EntryInfo = (aoaBias > 0.5f ? "kąt natarcia −" + aoaBias.ToString("F0", Inv) + "° (oszczędzam energię), " : aoaBias < -0.5f ? "kąt natarcia +" + (-aoaBias).ToString("F0", Inv) + "° (wytracam nadmiar), " : "") + "do lotniska " + (rgo / 1000).ToString("F0", Inv) + " km, zasięg bez przechylenia " + (rMax / 1000).ToString("F0", Inv) +
                        " km, L/D " + ldF.ToString("F2", Inv) + ", przechylenie " + bankCmd.ToString("F0", Inv) + "°, błąd kursu " + dpsi.ToString("F0", Inv) + "°" +
                        (note.Length > 0 ? " – " + note : "");
            if (Planetarium.GetUniversalTime() > nextGLog)
            {
                nextGLog = Planetarium.GetUniversalTime() + 10;
                FlightLogger.Event("Wejście: " + EntryInfo);
            }
        }

        private void HoldAttitude()
        {
            if (holdMode == Hold.SrfVel && entryGuide)
            {
                try { EntryGuidance(); } catch (Exception e) { EntryInfo = "błąd naprowadzania: " + e.Message; }
                // zmiana kąta natarcia płynnie, 1°/s (skok 35°→25° przy dużym ciśnieniu dynamicznym wytrącał Burana)
                if (aoaByMach) gAoa = AoAForMach(vessel.mach);
                float step = 1.0f * TimeWarp.fixedDeltaTime;
                float aoaTarget = aoaBias < 0 ? Mathf.Min(45f, gAoa - aoaBias) : gAoa > 18 ? Mathf.Max(18f, gAoa - aoaBias) : gAoa;
                // Ochrona prędkości nisko (lot 2026-11-19 21:20: przy M 2→1 na 27→21 km Buran trzymał α 10°, prędkość
                // równoważna spadła do prędkości przeciągnięcia i wpadł w korkociąg). Poniżej 1,8× prędkości przeciągnięcia
                // zmniejszamy kąt natarcia (do 3°) – nos idzie w dół, Buran schodzi stromiej i nabiera prędkości.
                if (vessel.srfSpeed < 800 && gEas > 0 && gEas < 1.8 * gVst)
                {
                    float cut = (float)Math.Min(1.0, (1.8 * gVst - gEas) / (0.5 * gVst));
                    aoaTarget = Mathf.Max(3f, aoaTarget - cut * 9f);
                    step = 3.0f * TimeWarp.fixedDeltaTime;
                }
                gAoaCmd += Mathf.Clamp(aoaTarget - gAoaCmd, -step, step);
                holdRot = Quaternion.AngleAxis(-(float)(bankCmd * rollSign), Vector3.forward) * Quaternion.AngleAxis(-gAoaCmd, Vector3.right) * Quaternion.AngleAxis(gYaw, Vector3.up);
            }
            object core = Core();
            if (core == null) return;
            object att = Module(core, "MechJebModuleAttitudeController");
            if (att == null) return;
            object pool = Users(att);
            if (pool != null) PoolCall(pool, "Add");
            Type refType = att.GetType().Assembly.GetType("MuMech.AttitudeReference");
            foreach (MethodInfo m in att.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.Name != "attitudeTo") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < 3) continue;
                object[] args = null;
                if (holdMode == Hold.SurfaceNorth && ps.Length >= 4 && ps[0].ParameterType == typeof(double) && ps[1].ParameterType == typeof(double) && ps[2].ParameterType == typeof(double))
                {
                    args = new object[ps.Length];
                    args[0] = holdHeading; args[1] = holdPitch; args[2] = 0.0; args[3] = this;
                    for (int i = 4; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (object)true;
                }
                else if (holdMode == Hold.Inertial && ps.Length == 3 && ps[0].ParameterType == typeof(Vector3d) && refType != null)
                {
                    // kierunek w świecie → układ ORBIT MechJeba (LookRotation(prędkość orbitalna, góra)): z = prograde, y = „góra”, x = góra × prograde
                    Vector3d fwd = vessel.obt_velocity.normalized, upv = (vessel.CoMD - vessel.mainBody.position).normalized;
                    Vector3d yAx = Vector3d.Exclude(fwd, upv).normalized, xAx = Vector3d.Cross(yAx, fwd);
                    Vector3d loc = new Vector3d(Vector3d.Dot(holdDir, xAx), Vector3d.Dot(holdDir, yAx), Vector3d.Dot(holdDir, fwd));
                    args = new object[] { loc, Enum.Parse(refType, "ORBIT"), this };
                }
                else if (holdMode == Hold.Orbit && ps.Length == 3 && ps[0].ParameterType == typeof(Vector3d) && refType != null)
                {
                    args = new object[] { holdDir, Enum.Parse(refType, "ORBIT"), this };
                }
                else if (holdMode == Hold.SrfVel && ps[0].ParameterType.Name == "QuaternionD" && refType != null)
                {
                    object q = Activator.CreateInstance(ps[0].ParameterType, (double)holdRot.x, (double)holdRot.y, (double)holdRot.z, (double)holdRot.w);
                    args = new object[ps.Length];
                    args[0] = q; args[1] = Enum.Parse(refType, "SURFACE_VELOCITY"); args[2] = this;
                    for (int i = 3; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (object)true;
                }
                if (args == null) continue;
                m.Invoke(att, args);
                MethodInfo err = att.GetType().GetMethod("attitudeAngleFromTarget", Type.EmptyTypes);
                if (err != null) try { attError = (double)err.Invoke(att, null); } catch { }
                return;
            }
        }

        private void ReleaseAttitude()
        {
            entryGuide = false; EntryInfo = "";
            if (!holdAttitude) return;
            holdAttitude = false;
            try
            {
                object core = Core();
                object att = core != null ? Module(core, "MechJebModuleAttitudeController") : null;
                object pool = att != null ? Users(att) : null;
                if (pool != null) PoolCall(pool, "Remove");
            }
            catch { }
        }
    }
}
