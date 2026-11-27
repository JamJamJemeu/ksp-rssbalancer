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
    ///           autoOrbit = auto auto [dopasuj]  // dowolna rakieta na orbitę: analiza stopni, PVG MechJeba, domknięcie orbity
    ///                                            // (wys. km / nachylenie °, „auto” = niska orbita / szerokość startu; „dopasuj” – ignorowane, program nie zmienia rakiety)
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

        /// <summary>Orbita wpisana w zakładce Program („pe-ap nachylenie”, puste = jak w pliku) – zastępuje wartość „autoOrbit” z pliku.</summary>
        public static string AutoOrbitOverride = "";
        /// <summary>Ostatni stage rakiety wpisany w zakładce Program (−1 = jak w pliku programu / zapamiętany dla statku).</summary>
        public static int LastStageOverride = -1;
        /// <summary>Zdarzenia „stage o czasie” wpisane w zakładce Program, np. „114:8 157:6” (null = jak zapamiętane / w pliku).</summary>
        public static string StageAtOverride = null;

        public static string SavedStageAt(string ship)
        {
            try
            {
                if (string.IsNullOrEmpty(ship) || !File.Exists(ShipsFile)) return null;
                ConfigNode root = ConfigNode.Load(ShipsFile);
                if (root == null) return null;
                foreach (ConfigNode n in root.GetNodes("RAKIETA"))
                    if (n.GetValue("ship") == ship) return n.GetValue("stageAt");
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] rakiety.cfg: " + e.Message); }
            return null;
        }

        public static void SaveStageAt(string ship, string stageAt)
        {
            if (string.IsNullOrEmpty(ship) || stageAt == null) return;
            try
            {
                ConfigNode root = File.Exists(ShipsFile) ? (ConfigNode.Load(ShipsFile) ?? new ConfigNode()) : new ConfigNode();
                ConfigNode hit = null;
                foreach (ConfigNode n in root.GetNodes("RAKIETA")) if (n.GetValue("ship") == ship) hit = n;
                if (hit == null) { hit = root.AddNode("RAKIETA"); hit.AddValue("ship", ship); }
                hit.SetValue("stageAt", stageAt.Trim(), true);
                root.Save(ShipsFile);
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] rakiety.cfg: " + e.Message); }
        }

        /// <summary>Zdarzenie stagingu: kind 't' = czas lotu [s], 'a' = wysokość [km], 'h' = gorące rozdzielenie (zostało ≤ v s pracy stopnia).</summary>
        public class StageEv { public char kind; public double v; public int stage; public bool done; }

        /// <summary>„114:8 a110:6 h1.5:7” → zdarzenia (bez litery = czas lotu). Błędne pozycje pomija.</summary>
        public static List<StageEv> ParseStageAt(string txt)
        {
            var list = new List<StageEv>();
            if (string.IsNullOrEmpty(txt)) return list;
            foreach (string w0 in txt.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = w0.Trim();
                char k = 't';
                if (w.Length > 0 && char.IsLetter(w[0])) { k = char.ToLowerInvariant(w[0]); w = w.Substring(1); }
                if (k != 't' && k != 'a' && k != 'h') continue;
                string[] a = w.Split(':');
                double t; int st;
                if (a.Length == 2 && double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t) && int.TryParse(a[1], out st) && t > 0 && st >= 0)
                    list.Add(new StageEv { kind = k, v = t, stage = st });
            }
            // kolejność: od najwyższego stage'a (najwcześniejszego)
            list.Sort((x, y) => y.stage != x.stage ? y.stage.CompareTo(x.stage) : x.v.CompareTo(y.v));
            return list;
        }

        public static string SavedTemplate(string ship)
        {
            try
            {
                if (string.IsNullOrEmpty(ship) || !File.Exists(ShipsFile)) return null;
                ConfigNode root = ConfigNode.Load(ShipsFile);
                if (root == null) return null;
                foreach (ConfigNode n in root.GetNodes("RAKIETA"))
                    if (n.GetValue("ship") == ship) return n.GetValue("template");
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] rakiety.cfg: " + e.Message); }
            return null;
        }

        public static void SaveTemplate(string ship, string template)
        {
            if (string.IsNullOrEmpty(ship) || template == null) return;
            try
            {
                ConfigNode root = File.Exists(ShipsFile) ? (ConfigNode.Load(ShipsFile) ?? new ConfigNode()) : new ConfigNode();
                ConfigNode hit = null;
                foreach (ConfigNode n in root.GetNodes("RAKIETA")) if (n.GetValue("ship") == ship) hit = n;
                if (hit == null) { hit = root.AddNode("RAKIETA"); hit.AddValue("ship", ship); }
                hit.SetValue("template", template.Trim(), true);
                root.Save(ShipsFile);
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] rakiety.cfg: " + e.Message); }
        }

        // Ostatni stage rakiety zapamiętany dla każdego statku (nazwa) – PluginData/rakiety.cfg, zapisywany sam przy starcie.
        // Nie da się go wykryć automatycznie: Blok L pod Mołnią i silnik Sojuza pod kapsułą wyglądają w drzewie części tak samo,
        // a pierwszy jest rakietą, drugi statkiem. Dlatego wpisuje się raz w oknie, bez plików i bez restartu.
        private static string ShipsFile { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "rakiety.cfg")); } }

        public static int SavedLastStage(string ship)
        {
            try
            {
                if (string.IsNullOrEmpty(ship) || !File.Exists(ShipsFile)) return -1;
                ConfigNode root = ConfigNode.Load(ShipsFile);
                if (root == null) return -1;
                foreach (ConfigNode n in root.GetNodes("RAKIETA"))
                {
                    int v;
                    if (n.GetValue("ship") == ship && int.TryParse((n.GetValue("lastStage") ?? "").Trim(), out v)) return Math.Max(0, v);
                }
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] rakiety.cfg: " + e.Message); }
            return -1;
        }

        public static void SaveLastStage(string ship, int stage)
        {
            if (string.IsNullOrEmpty(ship)) return;
            try
            {
                ConfigNode root = File.Exists(ShipsFile) ? (ConfigNode.Load(ShipsFile) ?? new ConfigNode()) : new ConfigNode();
                ConfigNode hit = null;
                foreach (ConfigNode n in root.GetNodes("RAKIETA")) if (n.GetValue("ship") == ship) hit = n;
                if (hit == null) { hit = root.AddNode("RAKIETA"); hit.AddValue("ship", ship); }
                hit.SetValue("lastStage", stage.ToString(CultureInfo.InvariantCulture), true);
                root.Save(ShipsFile);
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] rakiety.cfg: " + e.Message); }
        }
        /// <summary>Start w oknie startowym pod lądowanie w dzień – program powrotu wtedy szuka przelotu w dzień.</summary>
        public static bool DaylightMission;

        public static bool HasAutoOrbit(Program p)
        {
            if (p == null) return false;
            foreach (Step s in p.steps) if (s.node.HasValue("autoOrbit") || s.node.HasValue("autoorbit")) return true;
            return false;
        }

        /// <summary>„lastStage = N” przy autoOrbit: rakieta to stage'e ≥ N (MechJeb nie odpali niższych, Δv liczone bez statku).</summary>
        public static int LastStageFromFile(Program p)
        {
            if (p == null) return 0;
            foreach (Step s in p.steps)
            {
                string v = s.node.GetValue("lastStage") ?? s.node.GetValue("laststage");
                int n; if (v != null && int.TryParse(v.Trim(), out n) && n > 0) return n;
            }
            return 0;
        }

        public static string AutoOrbitFromFile(Program p)
        {
            if (p == null) return "";
            foreach (Step s in p.steps) { string v = s.node.GetValue("autoOrbit") ?? s.node.GetValue("autoorbit"); if (v != null) return v; }
            return "";
        }

        /// <summary>Rozbiór „autoOrbit”: „pe[-ap|xap] [nachylenie]”, „auto” = niska orbita / szerokość startu.
        /// Zwraca perygeum i apogeum [km] (ap ≥ pe) i nachylenie [°] (NaN = auto).</summary>
        public static void ParseAutoOrbit(string val, CelestialBody b, out double peKm, out double apKm, out double inc)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string[] w = (val ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            peKm = 0; apKm = 0; inc = double.NaN;
            if (w.Length > 0 && w[0].ToLowerInvariant() != "auto")
            {
                string[] pa = w[0].Split(new[] { '-', 'x', 'X', '×', '/' }, StringSplitOptions.RemoveEmptyEntries);
                double v;
                if (pa.Length > 0 && double.TryParse(pa[0], NumberStyles.Float, inv, out v)) peKm = v;
                if (pa.Length > 1 && double.TryParse(pa[1], NumberStyles.Float, inv, out v)) apKm = v;
            }
            if (peKm <= 0) peKm = b.atmosphere ? Math.Max(b.atmosphereDepth / 1000 + 60, Math.Round(b.atmosphereDepth / 1000 * 1.4)) : DeltaVPlanner.LowOrbitAlt(b) / 1000;
            if (apKm < peKm) { if (apKm > 0) { double t = apKm; apKm = peKm; peKm = t; } else apKm = peKm; }
            double iv;
            if (w.Length > 1 && w[1].ToLowerInvariant() != "auto" && double.TryParse(w[1], NumberStyles.Float, inv, out iv)) inc = iv;
        }

        public static string Folder
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"), "Programs"); }
        }

        public static void Reload()
        {
            VehicleProfile.Reload();
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
    public partial class FlightProgramRunner : MonoBehaviour
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
            mPe = peKm; mAp = apKm; mInc = incDeg; mEnginesChecked = false;
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
                // 1.47.3: już za apogeum – bez czekania na węzeł (płaszczyznę poprawi później spotkanie/korekta)
                bool pastAp = vessel.verticalSpeed < 0 || o.timeToAp > 0.5 * o.period;
                if (Math.Abs(di) > 0.05 && !pastAp) mplan.Add(MPh.WaitNodePlane);
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

        // ------------------------------------------------------------------ odejście od rdzenia po odłączeniu
        // Lot 24.11 11:03: Buran odłączony od rdzenia (T+438), oba lecą obok siebie prawie bez prędkości względnej,
        // manewr ОДУ-1 włączył warp „na szynach” do apogeum. Na szynach KSP nie liczy zderzeń, a przy wyjściu z warpa
        // (T+722,7) oba statki „rozpakowały się” stykając się – wybuch, 6 części oderwanych, obroty 90°/s.
        // Teraz, jak w prawdziwym locie (Buran odszedł od bloku Ц silnikami orientacji): dopóki jakiś statek jest
        // bliżej niż SepSafeDist – trzymamy bieżące położenie (żadnych obrotów skrzydłem/ogonem w rdzeń), RCS pcha nas
        // w kierunku „od niego” do prędkości rozejścia SepOpenSpeed, a warp jest zablokowany.
        public static double SepSafeDist { get { return Tun.D("sep.safeDist"); } }
        public static double SepOpenSpeed { get { return Tun.D("sep.openSpeed"); } }   // 1.40.2: 150 → 50 m (symulacja: po 50 m prędkość rozejścia dalej rośnie odstęp – w ОДУ-1 ≥ 337 m tak samo jak przy 150 m)
        private bool sepOn, warpAfterSep; private Vessel sepVessel, sepFbwVessel; private Vector3d sepHold; private double sepStart;
        public string SeparationInfo = "";

        /// <summary>Najbliższy inny statek w zasięgu fizyki (m); +∞, gdy brak.</summary>
        private double NearestNeighbour(out double dist, out Vessel near)
        {
            dist = double.PositiveInfinity; near = null;
            if (vessel == null) return dist;
            foreach (Vessel o in FlightGlobals.VesselsLoaded)
            {
                if (o == null || o == vessel || o.packed && !o.loaded) continue;
                double d = (o.CoMD - vessel.CoMD).magnitude;
                if (d < dist) { dist = d; near = o; }
            }
            return dist;
        }

        /// <summary>Prędkość rozejścia z innym statkiem: (v_my − v_on) wzdłuż kierunku „od niego” [m/s].</summary>
        private double OpeningSpeed(Vessel o, out Vector3d away)
        {
            away = PushDir(o);
            return Vector3d.Dot(vessel.obt_velocity - o.obt_velocity, away);
        }

        // Lot 24.11 12:05: odejście 150 m „po prostu od rdzenia” zostawiło rdzeń prawie na linii lotu; ОДУ-1 (wzdłuż
        // prędkości) poprowadził Burana prosto na niego (zbliżanie 8 m/s przy 150 m). Teraz odchodzimy W BOK od kierunku
        // lotu (składowa „od rdzenia” prostopadła do prędkości), a manewr czeka, aż odsunięcie w bok ≥ SepLateral.
        public static double SepLateral { get { return Tun.D("sep.lateral"); } }
        private double sepFarDist { get { return Tun.D("sep.far"); } }
        private Vector3d PushDir(Vessel o)
        {
            Vector3d rel = vessel.CoMD - o.CoMD, vdir = vessel.obt_velocity.normalized;
            Vector3d side = Vector3d.Exclude(vdir, rel);
            if (side.magnitude > 0.3 * rel.magnitude && side.magnitude > 1) return side.normalized;
            // rdzeń prawie dokładnie przed/za nami: w górę (od Ziemi) albo w dół – w stronę, w którą już jesteśmy od niego
            Vector3d upv = (vessel.CoMD - vessel.mainBody.position).normalized;
            Vector3d radial = Vector3d.Exclude(vdir, upv).normalized;
            return Vector3d.Dot(rel, radial) >= 0 ? radial : -radial;
        }

        /// <summary>Odległość od statku w kierunku prostopadłym do naszej prędkości [m].</summary>
        private double LateralDist(Vessel o)
        {
            return Vector3d.Exclude(vessel.obt_velocity.normalized, vessel.CoMD - o.CoMD).magnitude;
        }

        private static bool IsBurnPhase(MPh p)
        {
            return p == MPh.BurnPlane || p == MPh.BurnA || p == MPh.BurnP || p == MPh.BurnRaise || p == MPh.BurnCombo || p == MPh.BurnLower || p == MPh.BurnApRaiseAp;
        }

        // Lot 24.11 12:05: po odłączeniu (stage 2) silniki ОДУ (17D12, stage 1) nie były zapalone – „impuls” robiły tylko
        // silniki orientacji (~12 kN zamiast 2×86 kN). Przy pierwszym impulsie manewru: jeśli żaden silnik rakietowy nie
        // pracuje, zapal wszystkie nieodpalone silniki rakietowe (nie powietrzne) – na Buranie to właśnie ОДУ.
        private bool mEnginesChecked;
        private void EnsureBurnEngines()
        {
            if (mEnginesChecked || vessel == null) return;
            mEnginesChecked = true;
            var idle = new List<ModuleEngines>(); bool running = false;
            foreach (Part p in vessel.parts)
                foreach (PartModule pm in p.Modules)
                {
                    ModuleEngines e = pm as ModuleEngines;
                    // bez silników na paliwo stałe: na Sojuzie to silniki miękkiego lądowania kapsuły (odpalone na orbicie = strata)
                    if (e == null || e.propellants.Exists(pr => pr.name == "IntakeAir" || pr.name == "SolidFuel")) continue;
                    if (e.EngineIgnited && !e.flameout) running = true;
                    else if (!e.EngineIgnited) idle.Add(e);
                }
            if (running || idle.Count == 0) return;
            var names = new List<string>();
            foreach (ModuleEngines e in idle) { e.Activate(); names.Add(e.part.partInfo != null ? e.part.partInfo.title : e.part.name); }
            FlightLogger.Event("Manewr orbitalny: zapłon " + idle.Count + " siln. (" + string.Join(", ", names.ToArray()) + ")");
        }

        /// <summary>true = jeszcze odchodzimy od sąsiada (manewr czeka).</summary>
        private bool SeparationGuard()
        {
            if (vessel.situation != Vessel.Situations.ORBITING && vessel.situation != Vessel.Situations.SUB_ORBITAL) return false;
            double d; Vessel n;
            NearestNeighbour(out d, out n);
            double now = Planetarium.GetUniversalTime();
            double lat = n == null ? double.PositiveInfinity : LateralDist(n);
            // 1.43.3: dalej niż SepFarDist nic nam nie grozi – lot 26.11 15:11: Blok I 2,2 km z przodu na tej samej orbicie
            // (w bok < 40 m) znów włączył odchodzenie na RCS i zatrzymał warp.
            if (n == null || d >= sepFarDist || (d >= SepSafeDist && lat >= SepLateral) || (sepOn && now - sepStart > Tun.D("sep.maxTime")))
            {
                if (sepOn)
                {
                    FlightLogger.Event("Odejście od „" + (sepVessel != null ? sepVessel.vesselName : "?") + "”: " +
                                       (n == null ? "poza zasięgiem" : (now - sepStart > 300 ? "limit czasu 300 s, " : "") + "odległość " + d.ToString("F0", Inv) + " m, w bok od kierunku lotu " + lat.ToString("F0", Inv) + " m") + " – dalej manewr");
                    EndSeparation();
                    if (warpAfterSep && !double.IsNaN(mBurnUT) && mBurnUT - now > 90) ProgWarpTo(mBurnUT - 60);
                    warpAfterSep = false;
                }
                return false;
            }
            if (!sepOn)
            {
                sepOn = true; sepVessel = n; sepStart = now;
                sepHold = (Vector3d)vessel.ReferenceTransform.up;          // nos tam, gdzie jest – bez obrotów przy rdzeniu
                vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                if (sepFbwVessel != vessel) { if (sepFbwVessel != null) sepFbwVessel.OnFlyByWire -= SepFbw; vessel.OnFlyByWire += SepFbw; sepFbwVessel = vessel; }
                Vector3d aw0; double op0 = OpeningSpeed(n, out aw0);
                FlightLogger.Event("Odejście od „" + n.vesselName + "”: odległość " + d.ToString("F0", Inv) + " m, rozejście " + op0.ToString("F2", Inv) + " m/s – RCS od niego, warp zablokowany");
            }
            sepVessel = n;
            holdDir = sepHold; holdMode = Hold.Inertial; holdAttitude = true;
            FlightInputHandler.state.mainThrottle = 0f;
            Vector3d aw; double op = OpeningSpeed(n, out aw);
            SeparationInfo = "odejście od „" + n.vesselName + "”: " + d.ToString("F0", Inv) + " m (w bok " + lat.ToString("F0", Inv) + " m), " + op.ToString("F2", Inv) + " m/s";
            ManeuverInfo = SeparationInfo;
            return true;
        }

        // Ciąg RCS: wg ModuleRCS (Assembly-CSharp) inputLin = R·(X, Z, Y), dysza pali, gdy jej oś ma dodatni rzut na inputLin,
        // a siła działa PRZECIWNIE do osi dyszy ⇒ przyspieszenie statku ∝ −R·(X, Z, Y). Żeby przyspieszać wzdłuż „away”:
        // (X, Z, Y) = −R⁻¹·away.
        private void SepFbw(FlightCtrlState s)
        {
            if (!sepOn || sepVessel == null || vessel == null) return;
            Vector3d aw; double op = OpeningSpeed(sepVessel, out aw);
            if (op >= SepOpenSpeed) return;
            float k = (float)Math.Max(0.2, Math.Min(1.0, (SepOpenSpeed - op) / 0.3));
            Vector3 l = vessel.ReferenceTransform.InverseTransformDirection((Vector3)aw);
            Vector3 c = LinCmd(l) * k;
            s.X = c.x; s.Y = c.y; s.Z = c.z;
        }

        /// <summary>Wejście (X, Y, Z) w FlightCtrlState dające przyspieszenie wzdłuż wektora l (w układzie ReferenceTransform).</summary>
        public static Vector3 LinCmd(Vector3 l)
        {
            // inputLin = (X, Z, Y) = −l  ⇒  X = −l.x, Z = −l.y, Y = −l.z ; normujemy do największej składowej = 1
            float m = Mathf.Max(Mathf.Abs(l.x), Mathf.Max(Mathf.Abs(l.y), Mathf.Abs(l.z)));
            if (m < 1e-6f) return Vector3.zero;
            return new Vector3(-l.x / m, -l.z / m, -l.y / m);   // (X, Y, Z)
        }

        private void EndSeparation()
        {
            sepOn = false; sepVessel = null; SeparationInfo = "";
            if (sepFbwVessel != null) { sepFbwVessel.OnFlyByWire -= SepFbw; sepFbwVessel = null; }
        }

        /// <summary>Warp ręczny/inny przy statku obok – zatrzymaj, zanim do niego „wskoczymy” po wyjściu z warpa.</summary>
        private void WarpNeighbourGuard()
        {
            if (vessel == null || TimeWarp.CurrentRateIndex == 0 || TimeWarp.WarpMode != TimeWarp.Modes.HIGH) return;
            double d; Vessel n;
            if (NearestNeighbour(out d, out n) < SepSafeDist)
            {
                TimeWarp.SetRate(0, true);
                LastMessage += "\nWarp zatrzymany: obok jest „" + n.vesselName + "” (" + d.ToString("F0") + " m).";
                FlightLogger.Event("Warp zatrzymany – obok „" + n.vesselName + "” " + d.ToString("F0", Inv) + " m");
            }
        }

        private void ManeuverWarp(double burnStart)
        {
            double now = Planetarium.GetUniversalTime();
            if (mWarpedFor == burnStart) return;
            double nd; Vessel nv;
            if (NearestNeighbour(out nd, out nv) < SepSafeDist) return;   // obok wisi rdzeń – bez warpa (spróbujemy, gdy się oddalimy)
            mWarpedFor = burnStart;
            if (burnStart - now > 120 && TimeWarp.CurrentRateIndex == 0) ProgWarpTo(burnStart - 60);
        }

        /// <summary>Impuls wektorowy: nos na Δv, przepustnica proporcjonalna przy końcówce. true = skończony.</summary>
        private bool VectorBurn(Vector3d dv)
        {
            double m = dv.magnitude;
            holdDir = dv.normalized; holdMode = Hold.Inertial; holdAttitude = true;
            if (m < 0.3 || (m > mLastDv + 0.05 && mLastDv < 3)) return true;
            mLastDv = Math.Min(mLastDv, m);
            EnsureBurnEngines();
            double a = AccelNow();
            float thr = (float)Math.Max(Tun.D("burn.minThrottle"), Math.Min(1, m / (a * Tun.D("burn.vectorSoftK"))));
            FlightInputHandler.state.mainThrottle = attError < Tun.D("burn.attTol") ? thr : 0f;
            return false;
        }

        /// <summary>Impuls wzdłuż prędkości (prograde/retro) aż wartość dojdzie do celu. true = skończony.</summary>
        private bool AxisBurn(double cur, double target, bool raises)
        {
            holdDir = raises ? Vector3d.forward : Vector3d.back; holdMode = Hold.Orbit; holdAttitude = true;
            double err = (target - cur) * (raises ? 1 : -1);
            if (err <= 0) return true;
            EnsureBurnEngines();
            double a = AccelNow();
            FlightInputHandler.state.mainThrottle = attError < Tun.D("burn.attTol") ? (float)Math.Max(Tun.D("burn.minThrottle"), Math.Min(1, err / Tun.D("burn.axisSoftM"))) : 0f;   // err w m
            return false;
        }

        private void Maneuver()
        {
            if (mph == MPh.None || mph == MPh.Done || vessel == null) return;
            if (!IsBurnPhase(mph) && !mUrgent && SeparationGuard()) return;   // w trakcie impulsu nie przerywamy (lot 24.11 12:05)
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            double now = Planetarium.GetUniversalTime(), R = b.Radius;
            double a = AccelNow();
            if (mUrgent && o.PeA > (b.atmosphere ? b.atmosphereDepth : 0)) mUrgent = false;     // perygeum już nad atmosferą
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
                        // 1.47.3: już za apogeum (spadamy) – następne apogeum byłoby po przejściu przez atmosferę → impuls TERAZ
                        double atmT = b.atmosphere ? b.atmosphereDepth : 0;
                        if (o.PeA < atmT && (vz < 0 || o.timeToAp > 0.5 * o.period))
                        {
                            mBurnUT = now; mUrgent = true;
                            ManeuverInfo = "ОДУ-1 od razu (już za apogeum, perygeum w atmosferze): apogeum do " + mAp.ToString("F0", Inv) + " km";
                        }
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

        // ------------------------------------------------------------------ okno startowe pod lądowanie w dzień
        // Lot 24.11 15:45: start ok. 15:50 czasu słonecznego Bajkonuru (grudzień w grze) – orbita mijała Jubilejnyj tylko
        // przy Słońcu ≤ 6° (wieczór) albo nocą, więc plan zejścia „w dzień” nie znalazł okna w 6 dobach (sprawdzone offline:
        // model toru poniżej odtwarza tor z tego lotu z błędem ~10 km przez 15 h). Prawdziwy 1K1: start rano, lądowanie po 3,4 h.
        // Model: orbita kołowa w płaszczyźnie przez wyrzutnię (nachylenie z programu, lot na północny wschód), Buran w punkcie
        // wyrzutni LwLag s po starcie (dopasowane do logu), bez J2 (KSP go nie ma).
        // 1.40.3 (lot 24.11 18:34): przelot 4,7 h po starcie mijał lotnisko o 117 km, ale planer zejścia liczy odejście
        // boczne inaczej – od punktu wejścia 7000 km przed lotniskiem, wzdłuż kierunku toru w tym punkcie – i wychodziło
        // mu 289 km (> 200), więc go odrzucił, a pozostałe przeloty były nocne. Okno liczy teraz przeloty dokładnie tak
        // jak planer (wejście na entryRange, odejście boczne z kierunku toru, lądowanie = wejście + 1900 s) i z zapasem:
        // odejście ≤ 90 % maxCrossRange, Słońce ≥ 12°. Sprawdzone offline: model 281 km vs log 289 km dla tego przelotu.
        private static double LwLag { get { return Tun.D("lw.lag"); } }
        private static double LwLandAfterEntry { get { return Tun.D("lw.landAfterEntry"); } }
        private static double LwSun { get { return Tun.D("lw.sunDeg"); } }
        private static double LwMaxH { get { return Tun.D("lw.maxHours"); } }
        private double lwEntryKm = 7000, lwMaxXrKm = 200, lwMaxXrLeftKm = 200, lwMinEntryS = 3600;
        // 1.41.2 (lot 24.11 18:34 – „za dużo orbit, 1K1 zrobił dwie”): prawdziwy 1K1 wszedł w atmosferę na 2. okrążeniu
        // (impuls T+2:20, lądowanie T+3:25). Ten przelot mija lotnisko o ~820 km w prawo, więc limit 200 km go odrzucał.
        // Symulacja wejścia offline (model aerodynamiki z logu 18:34: F/q i L/D od Macha, US76, obracająca się Ziemia,
        // to samo naprowadzanie co w grze): w prawo osiągalne ≥ 1000 km (siła Coriolisa na półkuli płn. znosi tor w prawo),
        // w lewo tylko ~400 km – stąd osobne limity. Przelot 1. okrążenia (~760 km w prawo) wycina minEntryMET.
        private double launchHoldUT = double.NaN;

        private double SunElevationAt(CelestialBody b, double lat, double lon, double ut)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || sun == b) return 90;
            double dLon = b.rotates && b.rotationPeriod > 0 ? 360.0 * (ut - Planetarium.GetUniversalTime()) / b.rotationPeriod : 0;
            Vector3d up = (b.GetWorldSurfacePosition(lat, lon + dLon, 0) - b.position).normalized;
            return 90 - Vector3d.Angle(up, (sun.position - b.position).normalized);
        }

        /// <summary>Lokalny czas słoneczny [h] w punkcie (lat, lon) w chwili ut.</summary>
        private double SolarTimeAt(CelestialBody b, double lat, double lon, double ut)
        {
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null || sun == b) return 12;
            double dLon = b.rotates && b.rotationPeriod > 0 ? 360.0 * (ut - Planetarium.GetUniversalTime()) / b.rotationPeriod : 0;
            Vector3d k = b.RotationAxis.normalized;
            Vector3d up = (b.GetWorldSurfacePosition(lat, lon + dLon, 0) - b.position).normalized;
            Vector3d east = (b.GetWorldSurfacePosition(lat, lon + dLon + 0.01, 0) - b.GetWorldSurfacePosition(lat, lon + dLon, 0)).normalized;
            Vector3d s = (sun.position - b.position).normalized;
            Vector3d ue = Vector3d.Exclude(k, up).normalized, ee = Vector3d.Exclude(k, east).normalized, se = Vector3d.Exclude(k, s);
            double hEast = Math.Atan2(Vector3d.Dot(se, ee), Vector3d.Dot(se, ue)) * 180 / Math.PI;   // Słońce na wschód od południka = rano
            double t = 12 - hEast / 15.0;
            return (t % 24 + 24) % 24;
        }

        /// <summary>Tor modelowy: (szer., dług.) punktu podsatelitarnego t s po starcie [rad]. Układ inercjalny z osią z = oś Ziemi,
        /// długości liczone od położenia wyrzutni w chwili startu.</summary>
        private struct LwPlane { public Vector3d r0, v; public double n, w; }
        private static LwPlane LwMakePlane(double padLatDeg, double incDeg, double period, double rotPeriod)
        {
            double la = padLatDeg * Math.PI / 180;
            Vector3d r0 = new Vector3d(Math.Cos(la), 0, Math.Sin(la)), k = new Vector3d(0, 0, 1);
            Vector3d kp = k - Vector3d.Dot(k, r0) * r0; double nk = kp.magnitude; kp = kp / nk;
            Vector3d e2 = Vector3d.Cross(r0, k).normalized;
            double al = Math.Min(1, Math.Cos(incDeg * Math.PI / 180) / nk);
            LwPlane pl = new LwPlane();
            foreach (double sg in new[] { 1.0, -1.0 })
            {
                Vector3d h = al * kp + sg * Math.Sqrt(Math.Max(0, 1 - al * al)) * e2;
                Vector3d v = Vector3d.Cross(h, r0);
                if (v.z > 0) { pl.r0 = r0; pl.v = v.normalized; break; }
            }
            pl.n = 2 * Math.PI / period; pl.w = rotPeriod > 0 ? 2 * Math.PI / rotPeriod : 0;
            return pl;
        }
        private static void LwTrack(LwPlane pl, double t, out double lat, out double dlon)
        {
            double a = pl.n * (t - LwLag);
            Vector3d p = Math.Cos(a) * pl.r0 + Math.Sin(a) * pl.v;
            lat = Math.Asin(Math.Max(-1, Math.Min(1, p.z)));
            dlon = Math.Atan2(p.y, p.x) - pl.w * t;      // względem długości wyrzutni
        }

        /// <summary>Najbliższa chwila startu (UT) z lądowaniem w dzień przy pierwszym przelocie (≤ LwMaxH h po starcie).</summary>
        private bool FindLaunchWindow(CelestialBody b, double sLat, double sLon, double incDeg, double peKm, double apKm,
                                      out double bestL, out double landT, out double sunEl, out double xr)
        {
            bestL = landT = sunEl = xr = double.NaN;
            double padLat = vessel.latitude, padLon = vessel.longitude, R = b.Radius;
            double a = R + 0.5 * (peKm + apKm) * 1000, period = 2 * Math.PI * Math.Sqrt(a * a * a / b.gravParameter);
            LwPlane pl = LwMakePlane(padLat, incDeg, period, b.rotates ? b.rotationPeriod : 0);
            double sla = sLat * Math.PI / 180, slo = (sLon - padLon) * Math.PI / 180;
            // przeloty modelu nie zależą od chwili startu (tor względem wyrzutni) – liczymy je raz, tak jak planer zejścia:
            // chwila, gdy odległość do lotniska spada przez entryRange, i odejście boczne lotniska od kierunku toru w tym punkcie
            var passT = new List<double>(); var passX = new List<double>();
            double dPrev = double.NaN;
            for (double t = 3600; t <= LwMaxH * 3600; t += 10)
            {
                double la, dl; LwTrack(pl, t, out la, out dl);
                double c = Math.Sin(la) * Math.Sin(sla) + Math.Cos(la) * Math.Cos(sla) * Math.Cos(dl - slo);
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, c)));
                double d = ang * R / 1000;
                if (!double.IsNaN(dPrev) && dPrev > lwEntryKm && d <= lwEntryKm)
                {
                    double la2, dl2; LwTrack(pl, t + 10, out la2, out dl2);
                    double trk = Bearing(la, dl, la2, dl2), brg = Bearing(la, dl, sla, slo);
                    double rel = brg - trk; while (rel > Math.PI) rel -= 2 * Math.PI; while (rel < -Math.PI) rel += 2 * Math.PI;
                    if (Math.Abs(rel) < Math.PI / 2)
                    {
                        double xrKm = Math.Asin(Math.Max(-1, Math.Min(1, Math.Sin(ang) * Math.Sin(rel)))) * R / 1000;   // + = w prawo
                        if (t >= lwMinEntryS && (xrKm >= 0 ? xrKm <= 0.9 * lwMaxXrKm : -xrKm <= 0.9 * lwMaxXrLeftKm)) { passT.Add(t); passX.Add(xrKm); }
                    }
                }
                dPrev = d;
            }
            if (passT.Count == 0) return false;
            // tylko pierwszy pasujący przelot – ten sam, który weźmie planer (jak w 1K1: lądowanie na 2. okrążeniu),
            // a nie późniejszy, który akurat wypada w dzień
            passT.RemoveRange(1, passT.Count - 1); passX.RemoveRange(1, passX.Count - 1);
            double now = Planetarium.GetUniversalTime();
            bool inWin = false; double bestEl = -99;
            for (double L = now + 300; L < now + 2 * 86400; L += 300)
            {
                double el = double.NaN, lt = double.NaN, x = double.NaN;
                for (int i = 0; i < passT.Count; i++)
                {
                    double e = SunElevationAt(b, sLat, sLon, L + passT[i] + LwLandAfterEntry);
                    if (e >= LwSun) { el = e; lt = passT[i] + LwLandAfterEntry; x = passX[i]; break; }
                }
                if (!double.IsNaN(el))
                {
                    inWin = true;
                    if (el > bestEl) { bestEl = el; bestL = L; landT = lt; sunEl = el; xr = x; }
                }
                else if (inWin) break;           // koniec pierwszego okna – bierzemy w nim start z najwyżej stojącym Słońcem
            }
            return !double.IsNaN(bestL);
        }

        /// <summary>Na wyrzutni: pytanie, czy przyspieszyć czas do okna startowego pod lądowanie w dzień.</summary>
        private void AskLaunchWindow(FlightPrograms.Program p, string returnProgram)
        {
            FlightPrograms.DaylightMission = false;
            FlightPrograms.Program rp = FlightPrograms.Programs.Find(x => x.name == returnProgram.Trim());
            string site = rp != null && rp.node != null ? rp.node.GetValue("site") : null;
            double sLat = double.NaN, sLon = double.NaN;
            if (site != null)
            {
                string[] ll = site.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (ll.Length >= 2) { sLat = Num(ll[0], double.NaN); sLon = Num(ll[1], double.NaN); }
            }
            double pe = 251, ap = 263, inc = 51.6;
            if (rp != null && rp.node != null) { lwEntryKm = Num(rp.node.GetValue("entryRange"), 7000); lwMaxXrKm = Num(rp.node.GetValue("maxCrossRange"), 200); lwMaxXrLeftKm = Num(rp.node.GetValue("maxCrossRangeLeft"), lwMaxXrKm); lwMinEntryS = Math.Max(3600, Num(rp.node.GetValue("minEntryMET"), 0)); }
            foreach (FlightPrograms.Step st in p.steps)
            {
                string ot = st.node.GetValue("orbittarget");
                if (string.IsNullOrEmpty(ot)) continue;
                string[] w = ot.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length >= 3) { pe = Num(w[0], pe); ap = Num(w[1], ap); inc = Num(w[2], inc); }
            }
            if (double.IsNaN(sLat)) { FlightLogger.Event("Okno startowe: brak lotniska w „" + returnProgram + "” – start bez okna"); return; }
            CelestialBody b = vessel.mainBody;
            double L, landT, el, xr;
            double now = Planetarium.GetUniversalTime();
            double padNow = SolarTimeAt(b, vessel.latitude, vessel.longitude, now);
            string head = "Teraz na wyrzutni: " + HourText(padNow) + " czasu słonecznego.\n\n";
            if (!FindLaunchWindow(b, sLat, sLon, inc, pe, ap, out L, out landT, out el, out xr))
            {
                FlightLogger.Event("Okno startowe: nie znaleziono w 2 dobach – start bez okna, lądowanie o dowolnej porze");
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), "RSSBalancerLaunchWindow",
                    "Okno startowe", head + "Nie znalazłem w 2 dobach startu z lądowaniem w dzień. Start teraz – lądowanie o dowolnej porze.", "OK", false, HighLogic.UISkin);
                return;
            }
            double startH = SolarTimeAt(b, vessel.latitude, vessel.longitude, L), landH = SolarTimeAt(b, sLat, sLon, L + landT);
            string msg = head + "Lądowanie w dzień na „" + (rp.name) + "” wymaga startu za " +
                         KSPUtil.PrintTimeCompact(L - now, false) + " – ok. " + HourText(startH) + " czasu słonecznego Bajkonuru.\n" +
                         "Lądowanie po ok. " + (landT / 3600).ToString("F1", Inv) + " h, ok. " + HourText(landH) + ", Słońce " + el.ToString("F0", Inv) + "° nad lotniskiem" +
                         " (odejście boczne " + XrText(xr) + ").\n\nPrzyspieszyć czas do okna startowego?";
            FlightLogger.Event("Okno startowe: start za " + KSPUtil.PrintTimeCompact(L - now, false) + " (" + HourText(startH) + "), lądowanie ok. " + HourText(landH) + ", Słońce " + el.ToString("F0", Inv) + "° – pytanie");
            launchHoldUT = double.PositiveInfinity;     // kroki stoją do odpowiedzi
            double Lc = L;
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RSSBalancerLaunchWindow", msg, "Okno startowe", HighLogic.UISkin,
                    new DialogGUIButton("Tak – przyspiesz do okna", () =>
                    {
                        FlightPrograms.DaylightMission = true;
                        launchHoldUT = Lc;
                        FlightLogger.Event("Okno startowe: wybrano – czekam do startu, lądowanie w dzień");
                        if (Lc - Planetarium.GetUniversalTime() > 60) ProgWarpTo(Lc - 15);
                    }),
                    new DialogGUIButton("Nie – start teraz", () =>
                    {
                        FlightPrograms.DaylightMission = false;
                        launchHoldUT = double.NaN;
                        lastActivation = Planetarium.GetUniversalTime();
                        FlightLogger.Event("Okno startowe: pominięte – start teraz, lądowanie o dowolnej porze");
                    })),
                false, HighLogic.UISkin);
        }

        private static double Bearing(double la1, double lo1, double la2, double lo2)
        {
            double y = Math.Sin(lo2 - lo1) * Math.Cos(la2), x = Math.Cos(la1) * Math.Sin(la2) - Math.Sin(la1) * Math.Cos(la2) * Math.Cos(lo2 - lo1);
            return Math.Atan2(y, x);
        }

        private static string HourText(double h)
        {
            int m = (int)Math.Round(h * 60) % 1440; if (m < 0) m += 1440;
            return (m / 60).ToString("00") + ":" + (m % 60).ToString("00");
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
        public double StallEas { get { return vessel != null ? VehicleProfile.Current.StallEas(vessel.totalMass) : VehicleProfile.Current.stallEas; } }

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

        private void OnDestroy() { if (Instance == this) Instance = null; EndSeparation(); RestoreWarpLimits(); ReleaseAttitude(); if (steerVessel != null) steerVessel.OnFlyByWire -= SteerFbw; }

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
            {
                string sch = p.node != null ? p.node.GetValue("scheme") : null;
                s1k1 = sch != null && sch.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim().ToUpperInvariant() == "1K1";
                if (p.node != null)
                {
                    s1k1Course = Num(p.node.GetValue("landingCourse"), 246.6);
                    s1k1CreD = Num(p.node.GetValue("creDistance"), 24) * 1000;
                    s1k1CreR = Num(p.node.GetValue("creRadius"), 25) * 1000;
                    s1k1TaemOff = Num(p.node.GetValue("creTaemOffset"), 100) * 1000;
                    s1k1Aim = Num(p.node.GetValue("creAim"), 20) * 1000;
                    string cs = p.node.GetValue("creSide");
                    s1k1CreSide = cs != null && cs.Trim().ToLowerInvariant().StartsWith("l") ? -1 : 1;
                }
                creOn = creDone = creFrozen = false;
                if (s1k1) FlightLogger.Event("Program lotu: schemat podejścia jak 1K1 – kurs lądowania " + s1k1Course.ToString("F1", Inv) + "°, ЦРЭ " + (s1k1CreR / 1000).ToString("F0", Inv) + " km w odl. " + (s1k1CreD / 1000).ToString("F0", Inv) + " km, styczna " + (s1k1CreSide > 0 ? "prawa" : "lewa"));
            }
            {
                bool vf; string vn = p.node != null ? p.node.GetValue("vehicle") : null;
                VehicleProfile.Current = VehicleProfile.Find(vn, out vf);
                if (!vf) { LastMessage = "Brak profilu statku „" + vn.Trim() + "” w PluginData/Vehicles – używam Burana."; FlightLogger.Event("Program lotu: " + LastMessage); }
                else if (!string.IsNullOrEmpty(vn)) FlightLogger.Event("Program lotu: profil statku „" + VehicleProfile.Current.name + "” (" + VehicleProfile.Current.file + ")");
            }
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
            planMaxXrLeft = p.node != null ? Num(p.node.GetValue("maxCrossRangeLeft"), planMaxXr) : planMaxXr;
            planMinEntryMet = p.node != null ? Num(p.node.GetValue("minEntryMET"), 0) : 0;
            entryRangeKm = p.node != null ? Num(p.node.GetValue("entryRange"), 0) : 0;
            deorbitPeriKm = p.node != null ? Num(p.node.GetValue("deorbitPeri"), 30) : 30;
            entryAltKm = p.node != null ? Num(p.node.GetValue("entryAlt"), 120) : 120;
            if (p.node != null && p.node.HasValue("daylight")) RequireDaylight = p.node.GetValue("daylight").Trim().ToLowerInvariant() == "true";
            if (FlightPrograms.DaylightMission && siteSet) RequireDaylight = true;      // start był w oknie pod lądowanie w dzień
            launchHoldUT = double.NaN;
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
            string tunInfo = Tun.Load(p.node);
            if (tunInfo.Length > 0) FlightLogger.Event(tunInfo);
            LoadRendezvousSettings(p.node);
            rdv = RDV.None; rdvTarget = null; rdvB3Orbit = RdvB3Orbit1; rdvEngineAt = double.NaN; blk = null; blkB1UT = double.NaN; blkDocking = false; blkStopPending = false; rdvB3Plan = double.NaN; RendezvousInfo = ""; sepPayloadUT = double.NaN;
            if (vessel.situation == Vessel.Situations.PRELAUNCH && p.node != null && p.node.HasValue("launchWindow"))
                AskLaunchWindow(p, p.node.GetValue("launchWindow"));
            else if (vessel.situation == Vessel.Situations.PRELAUNCH && p.node != null && p.node.HasValue("rendezvousTarget"))
                AskRendezvousWindow(p.node.GetValue("rendezvousTarget"));
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
            mph = MPh.None; mUrgent = false; aoFinishAfterSep = false;
            StopRendezvous();
            StopBlocks();
            EndSeparation();
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

        // ---- dla pulpitu odliczania (CountdownWindow)
        public enum CdKind { None, Launch, StepDelay, Met, Deorbit, Maneuver, Atmosphere }
        /// <summary>Najbliższy przewidywalny moment programu: UT i rodzaj. false = nic do odliczania (czekamy na warunek bez prognozy).</summary>
        public bool Countdown(out double ut, out CdKind kind)
        {
            ut = double.NaN; kind = CdKind.None;
            if (!Active || vessel == null) return false;
            double now = Planetarium.GetUniversalTime();
            if (!double.IsNaN(launchHoldUT))
            {
                if (double.IsPositiveInfinity(launchHoldUT)) return false;
                ut = launchHoldUT; kind = CdKind.Launch; return true;
            }
            FlightPrograms.Step st = Running.steps[Current];
            double delay = Num(st.node.GetValue("delay"), 0);
            if (delay > 0 && now - lastActivation < delay) { ut = lastActivation + delay; kind = CdKind.StepDelay; return true; }
            string when = st.node.GetValue("when") ?? "always";
            string op; double met = FirstNumberAfter(when, "met", out op);
            if (!double.IsNaN(met) && op.StartsWith(">") && vessel.missionTime < met && vessel.situation != Vessel.Situations.PRELAUNCH)
            { ut = now + (met - vessel.missionTime); kind = CdKind.Met; return true; }
            string name; double ev = NextFlightEvent(out name);
            if (double.IsNaN(ev)) return false;
            ut = ev;
            kind = name.StartsWith("okno") ? CdKind.Launch : name.StartsWith("impuls hamujący") ? CdKind.Deorbit : name.StartsWith("impuls manewru") ? CdKind.Maneuver : CdKind.Atmosphere;
            return true;
        }
        public string CurrentWhen { get { return Active ? (Running.steps[Current].node.GetValue("when") ?? "always") : ""; } }
        public bool LaunchHold { get { return !double.IsNaN(launchHoldUT); } }
        public bool ManeuverActive { get { return mph != MPh.None && mph != MPh.Done; } }
        public bool SeparationActive { get { return sepOn; } }

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

            // 0) trwa manewr orbitalny – przyspieszamy tylko do jego impulsu (zrzut 24.11 18:43: przycisk proponował
            //    „do granicy atmosfery”, czyli przez impuls ОДУ-1; na szynach impuls by przepadł)
            if (mph != MPh.None && mph != MPh.Done)
            {
                if (IsBurnPhase(mph)) return "Trwa impuls – bez przyspieszania.";
                if (sepOn) { warpAfterSep = true; return "Najpierw odejście od rdzenia (do " + SepSafeDist.ToString("F0") + " m) – potem sam przyspieszę do impulsu."; }
                if (double.IsNaN(mBurnUT)) return "Impuls jeszcze nie policzony – spróbuj za chwilę.";
                double tb = mBurnUT - now;
                if (tb <= 90) return "Do impulsu " + tb.ToString("F0") + " s – nie ma czego przeskakiwać.";
                ProgWarpTo(mBurnUT - 60);
                return "Przyspieszam do 1 min przed impulsem (" + ManeuverInfo + ").";
            }

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
                    ProgWarpTo(planUT - lead);
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
                    ProgWarpTo(t - 20);
                    return "Przyspieszam do " + (Math.Max(altKm * 1000.0, atmTop + 1000) / 1000).ToString("F0") + " km.";
                }
                // 3) nie da się policzyć – szyny do granicy atmosfery, jeśli tam schodzimy
                if (vessel.orbit.PeA < atmTop)
                {
                    double t = NextDescentToRadius(vessel.orbit, now, b.Radius + atmTop + 1000);
                    if (!double.IsNaN(t) && t > now + 30) { ProgWarpTo(t - 20); physWarpTarget = k; return "Przyspieszam do granicy atmosfery, dalej ×3 do kroku „" + Running.steps[k].name + "”."; }
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
            if (vessel == null || vessel != FlightGlobals.ActiveVessel)
            {
                if (!RendezvousAdopt(FlightGlobals.ActiveVessel)) { LastMessage = "Zmieniono statek – program zatrzymany."; Stop(); return; }
            }

            qMax = Math.Max(qMax, vessel.dynamicPressurekPa);
            // Grupy silników rozpoznajemy RAZ przy starcie programu (na wyrzutni) – po odrzucie bloków
            // ponowne rozpoznanie widzi już tylko rdzeń i nie umie go nazwać „rdzeniem”.
            if ((id == null || !id.ok) && vessel.situation == Vessel.Situations.PRELAUNCH && Time.realtimeSinceStartup > nextGroups)
            { nextGroups = Time.realtimeSinceStartup + 2f; RefreshGroups(); }
            if (holdAttitude) HoldAttitude();
            if (!approachOn && vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth) { double e1, v1, a1; StallLevel = StallCheck(out e1, out v1, out a1); }
            if (approachOn) Approach();
            if (aoPhase == AO.Ascent || aoPhase == AO.Finish) AutoOrbitTick();
            if (mph != MPh.None && mph != MPh.Done) Maneuver();
            SeparatePayloadTick();
            RendezvousTick();
            BlocksTick();
            WarpNeighbourGuard();
            if (burnOn) FlightInputHandler.state.mainThrottle = holdAttitude && attError < Tun.D("burn.alignedTol") ? 1f : 0f;
            // Bezpieczne zatrzymanie warpa przed impulsem. Do 1.30 było tu natychmiastowe SetRate(0) przy „tin < 20 + 3·krok”
            // – przy warpie ×100 000 to wypadało godziny przed impulsem, a nagłe zrzucenie warpa z takiej prędkości
            // (powtarzane, bo warp znów ruszał) psuło orbitę: lot 22:11 155×155 → 173×136 km, lot 23:14 251×263 → 484×42 km.
            // Teraz: przy dużym warpie oddajemy hamowanie stockowemu WarpTo (zwalnia stopniowo), a natychmiast stop dopiero < 20 s.
            WarpEventGuard();
            LogPlanInfo();

            // okno startowe: czekamy (warp) do chwili startu, kroki programu stoją
            if (!double.IsNaN(launchHoldUT))
            {
                double nowL = Planetarium.GetUniversalTime();
                if (nowL < launchHoldUT)
                {
                    LastMessage = double.IsPositiveInfinity(launchHoldUT) ? "Okno startowe: czekam na odpowiedź." : "Okno startowe: start za " + KSPUtil.PrintTimeCompact(launchHoldUT - nowL, false) + ".";
                    return;
                }
                FlightLogger.Event("Okno startowe – start");
                launchHoldUT = double.NaN;
                lastActivation = nowL;
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
                case "aligned": return holdAttitude && attError < Tun.D("burn.alignedTol") ? 1 : 0;
                case "rwydist": return approachOn || rwySet ? RunwayDistance() / 1000.0 : double.NaN;
                case "landed": return vessel.Landed ? 1 : 0;
                case "siteangle": return SiteAngle(false);
                case "sitexrange": return SiteAngle(true);
                case "deorbitin": return DeorbitIn();
                case "orbitdone": return mph == MPh.Done ? 1 : 0;
                case "rendezvous": return RendezvousState;
                case "docked": return Math.Max(RendezvousState, BlkDocked());
                case "done": return BlockState;
                case "targetdist": return BlkTargetDist();
                case "relspeed": return BlkRelSpeed();
                case "autoorbit": return aoPhase == AO.Done ? (double.IsNaN(sepPayloadUT) ? 1 : 0) : aoPhase == AO.Failed ? -1 : 0;
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
        private string warpGuardName = "";

        /// <summary>Najbliższy przewidywalny moment lotu (UT), przed którym trzeba zwolnić czas: impuls hamujący z planu,
        /// impuls manewru orbitalnego, wejście w atmosferę. NaN = nic w zasięgu.</summary>
        private double NextFlightEvent(out string name)
        {
            name = ""; double best = double.NaN, now = Planetarium.GetUniversalTime();
            if (vessel == null) return best;
            CelestialBody b = vessel.mainBody;
            double atmTop = b.atmosphere ? b.atmosphereDepth : 0;
            if (!double.IsNaN(launchHoldUT) && !double.IsPositiveInfinity(launchHoldUT) && launchHoldUT > now) { best = launchHoldUT; name = "okno startowe"; }
            if (!double.IsNaN(planUT) && !burnOn && planUT > now && vessel.orbit.PeA > atmTop && (double.IsNaN(best) || planUT < best)) { best = planUT; name = "impuls hamujący"; }
            if (mph != MPh.None && mph != MPh.Done && !IsBurnPhase(mph) && !double.IsNaN(mBurnUT) && mBurnUT > now && (double.IsNaN(best) || mBurnUT < best))
            { best = mBurnUT; name = "impuls manewru (" + ManeuverInfo + ")"; }
            if (b.atmosphere && vessel.altitude > atmTop && vessel.orbit.PeA < atmTop)
            {
                double t = NextDescentToRadius(vessel.orbit, now, b.Radius + atmTop);
                if (!double.IsNaN(t) && t > now && (double.IsNaN(best) || t < best)) { best = t; name = "granica atmosfery"; }
            }
            return best;
        }

        // Przyspieszenie czasu (także ręczne) przed momentem lotu: przy dużym warpie oddajemy hamowanie stockowemu WarpTo
        // (zwalnia stopniowo – nagłe zrzucenie z ×100 000 psuło orbitę, loty 19.11 22:11 i 23:14), < 20 s – stop.
        // Lot 24.11 17:26: przy starcie impulsu ОДУ-1 trwało przyspieszenie, a osłona (1.39.4) wołała WarpTo(następny moment −30 s)
        // – czyli PRZYSPIESZAŁA do wejścia w atmosferę; impuls na szynach nie działa, ОДУ odpaliły się 9 min za późno
        // (Pe 116 km zamiast 251). Teraz osłona tylko ZWALNIA: zmniejsza stopień tak, żeby do momentu zostało ≥ 15 s
        // czasu rzeczywistego (płynnie; natychmiast tylko, gdy inaczej doleciałaby w < 50 klatek), < 20 s – stop.
        // W trakcie impulsu, spalania i odejścia od rdzenia przyspieszenia nie ma wcale (symulacja: zawsze stop ~20 s przed).
        /// <summary>Warp zlecony przez program (stockowe WarpTo samo zwalnia przed celem) – osłona go nie przycina.</summary>
        private double progWarpUT = double.NaN;
        private void ProgWarpTo(double ut) { progWarpUT = ut; TimeWarp.fetch.WarpTo(ut); }

        private void WarpEventGuard()
        {
            if (TimeWarp.CurrentRateIndex == 0) { progWarpUT = double.NaN; return; }
            if ((mph != MPh.None && mph != MPh.Done && IsBurnPhase(mph)) || burnOn || sepOn || RendezvousBurning || BlockBurning)
            {
                TimeWarp.SetRate(0, true);
                if (warpGuardName != "burn") { warpGuardName = "burn"; FlightLogger.Event("Przyspieszenie zatrzymane – trwa impuls/manewr"); }
                LastMessage = "Przyspieszenie zatrzymane: trwa impuls.";
                return;
            }
            if (TimeWarp.WarpMode != TimeWarp.Modes.HIGH) return;
            string name; double ev = NextFlightEvent(out name);
            if (double.IsNaN(ev)) return;
            double now = Planetarium.GetUniversalTime(), tin = ev - now;
            string key = name + ev.ToString("F0");
            // warp programu kończy się przed tym momentem – stockowe WarpTo samo zwolni, nie przeszkadzamy (bez „szarpania”)
            if (!double.IsNaN(progWarpUT) && progWarpUT > now && progWarpUT <= ev - 10 && tin >= 20) return;
            if (tin < 20)
            {
                TimeWarp.SetRate(0, TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime * 50 > tin);
                if (warpGuardName != key) { warpGuardName = key; FlightLogger.Event("Przyspieszenie zatrzymane – za " + tin.ToString("F0", Inv) + " s " + name); }
                LastMessage = "Przyspieszenie zatrzymane: za " + tin.ToString("F0") + " s " + name + ".";
                return;
            }
            float[] rates = TimeWarp.fetch != null ? TimeWarp.fetch.warpRates : null;
            if (rates == null) return;
            int idx = TimeWarp.CurrentRateIndex, allowed = idx;
            while (allowed > 0 && rates[allowed] * 15.0 > tin) allowed--;
            if (allowed < idx)
            {
                bool instant = TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime * 50 > tin;
                TimeWarp.SetRate(allowed, instant);
                if (Math.Abs(warpGuardUT - ev) > 1) { warpGuardUT = ev; FlightLogger.Event("Przyspieszenie: zwalniam przed – " + name + " (za " + KSPUtil.PrintTimeCompact(tin, false) + ")"); }
                LastMessage = "Zwalniam przyspieszenie: za " + KSPUtil.PrintTimeCompact(tin, false) + " " + name + ".";
            }
        }

        private string loggedPlan = "";
        private void LogPlanInfo()
        {
            if (string.IsNullOrEmpty(PlanInfo) || PlanInfo == "przeliczam…") return;
            // do logu tylko gdy zmienił się sens (bez odliczania „impuls za …”)
            string key = PlanInfo.StartsWith("impuls za") ? "plan:" + (double.IsNaN(planUT) ? "-" : planUT.ToString("F0")) : PlanInfo;
            if (key == loggedPlan) return;
            loggedPlan = key;
            FlightLogger.Event("Plan zejścia: " + PlanInfo);
        }
        private double planMaxXrLeft = 1000, planMinEntryMet = 0;
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
            xrKm = Math.Asin(Math.Max(-1, Math.Min(1, Math.Sin(ang) * Math.Sin(rel * Math.PI / 180)))) * b.Radius / 1000;   // + = lotnisko w prawo
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

        /// <summary>Odejście boczne ze znakiem (+ = lotnisko w prawo) w limicie: w prawo maxCrossRange, w lewo maxCrossRangeLeft.</summary>
        private bool XrOk(double xr) { return xr >= 0 ? xr <= planMaxXr : -xr <= planMaxXrLeft; }
        private static string XrText(double xr) { return Math.Abs(xr).ToString("F0", Inv) + " km " + (xr >= 0 ? "w prawo" : "w lewo"); }

        private void PlanDeorbitPhysical()
        {
            planUT = double.NaN; planXr = double.NaN;
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            if (o.eccentricity >= 1 || o.PeA < (b.atmosphere ? b.atmosphereDepth : 0)) { PlanInfo = "orbita nie jest zamknięta nad atmosferą"; return; }
            double incl = o.inclination > 90 ? 180 - o.inclination : o.inclination;
            double reachDeg = incl + Math.Max(planMaxXr, planMaxXrLeft) / (b.Radius / 1000) * 180 / Math.PI;
            if (reachDeg < Math.Abs(siteLat))
            {
                PlanInfo = "nachylenie orbity " + o.inclination.ToString("F1") + "° – orbita nigdy nie przechodzi bliżej niż " + ((Math.Abs(siteLat) - incl) * b.Radius / 1000 * Math.PI / 180).ToString("F0") +
                           " km od lotniska (szer. " + siteLat.ToString("F1") + "°); potrzeba nachylenia ≥ " + (Math.Abs(siteLat) - Math.Max(planMaxXr, planMaxXrLeft) / (b.Radius / 1000) * 180 / Math.PI).ToString("F1") + "° (1K1: 51,6°)";
                return;
            }
            double now = Planetarium.GetUniversalTime(), step = 30, horizon = 6 * 86400;
            double bestXr = double.MaxValue; int nightSkips = 0, earlySkips = 0;
            double launchUT = vessel.missionTime > 0 ? now - vessel.missionTime : double.NaN;
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
                    if (Math.Abs(xr) < bestXr) bestXr = Math.Abs(xr);
                    bool early = planMinEntryMet > 0 && !double.IsNaN(launchUT) && tI - launchUT < planMinEntryMet;
                    if (early && XrOk(xr)) earlySkips++;
                    if (!early && XrOk(xr))
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
                PlanInfo = nightSkips > 0 && bestXr <= planMaxXr
                    ? "brak okna w dzień w 6 dobach – " + nightSkips + " przelotów pasowało (odejście ≥ " + bestXr.ToString("F0") + " km), ale lądowanie wypadało przy Słońcu < 10°; wyłącz „lądowanie w dzień” albo startuj w oknie startowym"
                    : "brak okna w 6 dób (najmniejsze odejście boczne " + (bestXr < 1e9 ? bestXr.ToString("F0") : "?") + " km; limit w prawo " + planMaxXr.ToString("F0") + ", w lewo " + planMaxXrLeft.ToString("F0") + " km)";
            else
                PlanInfo = "impuls za " + KSPUtil.PrintTimeCompact(planUT - now, false) + ", Δv " + planDv.ToString("F0") + " m/s, wejście (" + entryAltKm.ToString("F0") + " km) " +
                           planDist.ToString("F0") + " km od lotniska, odejście boczne " + XrText(planXr) + (planMinEntryMet > 0 && !double.IsNaN(launchUT) ? ", wejście T+" + KSPUtil.PrintTimeCompact(planEntryUT - launchUT, false) + (earlySkips > 0 ? " (pominięto " + earlySkips + " wcześniejszy przelot – minEntryMET)" : "") : "") + ", Słońce przy lądowaniu " + planSun.ToString("F0") + "° (" +
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
                    if (ExecuteBlock(key, val, n)) continue;      // klocki (Blocks.cs)
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
                        { double nd; Vessel nv; if (NearestNeighbour(out nd, out nv) < SepSafeDist) throw new Exception("obok jest „" + nv.vesselName + "” (" + nd.ToString("F0") + " m) – warp zablokowany, żeby po wyjściu z warpa nie zderzyć się z nim"); }
                        if (w.Length > 0 && (w[0] == "ap" || w[0] == "pe"))
                        {
                            // warpto = ap <s przed apogeum> / pe <s przed perygeum>
                            double tt = w[0] == "ap" ? vessel.orbit.timeToAp : vessel.orbit.timeToPe;
                            if (tt > lead + 5) ProgWarpTo(Planetarium.GetUniversalTime() + tt - lead);
                        }
                        else
                        {
                            double tin = DeorbitIn();
                            if (double.IsNaN(tin)) throw new Exception("brak planu zejścia: " + PlanInfo);
                            if (tin > lead + 5) ProgWarpTo(planUT - lead);
                            LastMessage += "\nPlan: " + PlanInfo;
                            FlightLogger.Event("Program lotu: plan zejścia – " + PlanInfo);
                        }
                    }
                    else if (key == "program") pendingProgram = val.Trim();
                    else if (key == "rendezvous") LastMessage += "\n" + StartRendezvous(val.Trim());
                    else if (key == "askProgram" || key == "askprogram") AskProgram(val.Trim());
                    else if (key == "autoOrbit" || key == "autoorbit")
                    {
                        // 1.47.1: program uruchomiony ponownie na orbicie – NIE startujemy wznoszenia (lot 27.11 09:31: MechJeb Ascent
                        // na orbicie obniżył Sojuza z 252×265 do 140×220 km)
                        if (vessel.situation == Vessel.Situations.ORBITING && vessel.orbit.PeA > (vessel.mainBody.atmosphere ? vessel.mainBody.atmosphereDepth : 0))
                        {
                            aoPhase = AO.Done;
                            FlightLogger.Event("Start na orbitę: statek jest już na orbicie " + (vessel.orbit.PeA / 1000).ToString("F0", Inv) + " × " + (vessel.orbit.ApA / 1000).ToString("F0", Inv) + " km – wznoszenie pominięte");
                            LastMessage += "\nJuż na orbicie – wznoszenie pominięte.";
                            continue;
                        }
                        string ls = n.GetValue("lastStage") ?? n.GetValue("laststage");
                        int saved = FlightPrograms.SavedLastStage(vessel.vesselName);
                        aoLastStage = FlightPrograms.LastStageOverride >= 0 ? FlightPrograms.LastStageOverride
                                    : saved >= 0 ? saved : ls != null ? Math.Max(0, (int)Num(ls, 0)) : 0;
                        if (FlightPrograms.LastStageOverride >= 0) FlightPrograms.SaveLastStage(vessel.vesselName, aoLastStage);
                        string sa = FlightPrograms.StageAtOverride ?? FlightPrograms.SavedStageAt(vessel.vesselName) ?? n.GetValue("stageAt");
                        if (FlightPrograms.StageAtOverride != null) FlightPrograms.SaveStageAt(vessel.vesselName, FlightPrograms.StageAtOverride);
                        aoStageAt = FlightPrograms.ParseStageAt(sa); aoStageAtIdx = 0;
                        LastMessage += "\n" + StartAutoOrbit(val);
                    }
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

        // Gdy wiadomo, gdzie kończy się rakieta („Ostatni stage rakiety” > 0), po wejściu na orbitę program sam oddziela
        // statek od ostatniego stopnia – jeden stage (np. Sojuz: stage 4 = oddzielenie od Bloku I). Dalej nic nie stage'uje.
        // Oddzielenie statku: najpierw wyłączyć silniki ostatniego stopnia i odczekać, dopiero potem stage.
        // Lot 26.11 10:42: przepustnica 0 i stage w tej samej klatce – Blok I odszedł z rozpędzonymi silnikami
        // (odłączony stopień ma własny stan sterowania) i wjechał w Sojuza (uszkodzony panel).
        private double sepPayloadUT = double.NaN;
        private void SeparatePayload()
        {
            if (aoLastStage <= 0 || vessel == null || vessel.currentStage != aoLastStage) return;
            vessel.ctrlState.mainThrottle = 0f; FlightInputHandler.state.mainThrottle = 0f;
            int off = 0;
            foreach (Part p in vessel.parts)
                foreach (PartModule pm in p.Modules)
                {
                    ModuleEngines e = pm as ModuleEngines;
                    if (e != null && e.EngineIgnited && p.inverseStage >= aoLastStage) { e.Shutdown(); off++; }
                }
            sepPayloadUT = Planetarium.GetUniversalTime() + Tun.D("ao.payloadSepDelay");
            FlightLogger.Event("Start na orbitę: wyłączone silniki rakiety (" + off + "), oddzielenie statku za " + Tun.D("ao.payloadSepDelay").ToString("F0", Inv) + " s");
        }

        private void SeparatePayloadTick()
        {
            if (double.IsNaN(sepPayloadUT) || vessel == null) return;
            vessel.ctrlState.mainThrottle = 0f; FlightInputHandler.state.mainThrottle = 0f;
            if (Planetarium.GetUniversalTime() < sepPayloadUT) return;
            sepPayloadUT = double.NaN;
            if (vessel.currentStage != aoLastStage) return;
            StageManager.ActivateNextStage();
            FlightLogger.Event("Start na orbitę: oddzielenie statku od rakiety (stage " + (aoLastStage - 1) + ")");
            ScreenMessages.PostScreenMessage("Statek oddzielony od rakiety", 5f, ScreenMessageStyle.UPPER_CENTER);
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

        // ------------------------------------------------------------------ „autoOrbit” – dowolna rakieta na orbitę
        // 1) analiza: potrzebna Δv (ToOrbit z nachyleniem, straty grawitacyjne i oporu), Δv i TWR każdego stopnia (stock),
        //    słabe ogniwa do dziennika (tylko raport – program lotu nie zmienia rakiety);
        // 2) MechJeb Ascent Guidance (PVG) na orbitę kołową o zadanej wysokości i nachyleniu, autostaging, ≤ 4 g,
        //    początek zakrętu zależny od TWR; MechJeb sam odpala pierwszy stopień i zwalnia klamry;
        // 3) gdy MechJeb skończy (albo zabraknie mu paliwa, orbita suborbitalna/krzywa) – nasz „orbittarget” domyka orbitę.
        private enum AO { None, Ascent, Finish, Done, Failed }
        private bool aoFinishAfterSep;
        /// <summary>Manewr ratunkowy (tor suborbitalny) – bez czekania na odejście od stopnia i bez warpa.</summary>
        private bool mUrgent;

        /// <summary>Czy silniki rakiety nośnej (stage ≥ aoLastStage) mają jeszcze paliwo.</summary>
        private bool RocketHasFuel()
        {
            foreach (Part p in vessel.parts)
                if (p.inverseStage >= aoLastStage)
                    foreach (PartModule pm in p.Modules)
                    {
                        ModuleEngines e = pm as ModuleEngines;
                        if (e != null && e.EngineIgnited && !e.flameout) return true;
                    }
            return false;
        }
        private AO aoPhase = AO.None;
        private double aoAlt, aoAp, aoInc, aoStartUT, nextAoLog;
        private int aoLastStage;
        private List<FlightPrograms.StageEv> aoStageAt = new List<FlightPrograms.StageEv>();
        private int aoStageAtIdx;
        public string AutoOrbitInfo = "";

        private string StartAutoOrbit(string val)
        {
            CelestialBody b = vessel.mainBody;
            bool fromUi = !string.IsNullOrEmpty(FlightPrograms.AutoOrbitOverride);
            if (fromUi) val = FlightPrograms.AutoOrbitOverride;
            string[] w = (val ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool fit = Array.Exists(w, x => x.ToLowerInvariant() == "dopasuj" || x.ToLowerInvariant() == "fit");
            double lat = Math.Abs(vessel.latitude);
            double peKm, apKm, inc;
            FlightPrograms.ParseAutoOrbit(val, b, out peKm, out apKm, out inc);
            // 1.49: „autoOrbit = target” – orbita wstawienia pod cel wybrany na wyrzutni (dowolna rakieta)
            if (w.Length > 0 && w[0].ToLowerInvariant() == "target")
            {
                if (rdvTarget == null || rdvTarget.state == Vessel.State.DEAD) { aoPhase = AO.Failed; return "autoOrbit = target: nie wybrano celu (program musi mieć „rendezvousTarget”)."; }
                TargetInsertion(rdvTarget, b, out peKm, out apKm); inc = rdvTarget.orbit.inclination;
            }
            if (double.IsNaN(inc)) inc = Math.Ceiling(lat * 10) / 10;
            if (rdvTarget != null && rdv == RDV.None && rdvTarget.state != Vessel.State.DEAD) inc = Math.Round(rdvTarget.orbit.inclination, 2);   // spotkanie: płaszczyzna celu
            double altKm = peKm;
            string incNote = "";
            if (inc < lat) { incNote = " (" + inc.ToString("F1", Inv) + "° niemożliwe ze startu na " + lat.ToString("F1", Inv) + "° – biorę " + Math.Ceiling(lat * 10) / 10 + "°)"; inc = Math.Ceiling(lat * 10) / 10; }
            aoAlt = peKm; aoAp = apKm; aoInc = inc;

            // --- analiza
            double need = DeltaVPlanner.ToOrbit(b, peKm * 1000, apKm * 1000, inc);
            double vac; StockDeltaV.TotalVacFrom(aoLastStage, out vac);
            double twr = StockDeltaV.LiftoffTwr();
            List<string> rep = new List<string>();
            rep.Add("Cel: orbita " + OrbitLabel(peKm, apKm) + (fromUi ? " (wpisana w zakładce)" : "") + ", nachylenie " + inc.ToString("F1", Inv) + "°" + incNote + "; potrzeba ≈ " + need.ToString("F0", Inv) + " m/s");
            rep.Add("Rakieta: Δv (próżnia) " + vac.ToString("F0", Inv) + " m/s, TWR na starcie " + twr.ToString("F2", Inv) +
                    (aoLastStage > 0 ? " (stage'e ≥ " + aoLastStage + "; niższe to statek – MechJeb ich nie odpali)" : ""));
            VesselDeltaV vdv = StockDeltaV.Current();
            string weak = "";
            if (vdv != null && vdv.OperatingStageInfo != null)
            {
                List<DeltaVStageInfo> st = new List<DeltaVStageInfo>(vdv.OperatingStageInfo);
                st.Sort((x, y) => y.stage.CompareTo(x.stage));
                bool first = true;
                foreach (DeltaVStageInfo si in st)
                {
                    if (si == null || si.deltaVinVac < 1 || si.stage < aoLastStage) continue;
                    string line = "  stopień " + si.stage + ": Δv " + si.deltaVinVac.ToString("F0", Inv) + " m/s, TWR " + (first ? si.TWRASL.ToString("F2", Inv) + " (ziemia)" : si.TWRVac.ToString("F2", Inv)) +
                                  ", czas pracy " + si.stageBurnTime.ToString("F0", Inv) + " s";
                    if (first && si.TWRASL < Tun.D("ao.minTwr")) { line += "  ← za mały ciąg na start"; weak += "stopień " + si.stage + " (TWR startowy " + si.TWRASL.ToString("F2", Inv) + "); "; }
                    else if (!first && si.TWRVac < 0.35 && si.stageBurnTime > 600) { line += "  ← słaby ciąg, długie palenie (straty)"; weak += "stopień " + si.stage + " (TWR " + si.TWRVac.ToString("F2", Inv) + "); "; }
                    rep.Add(line); first = false;
                }
            }
            bool shortDv = vac > 0 && vac < need;
            if (shortDv) rep.Add("BRAKUJE ok. " + (need - vac).ToString("F0", Inv) + " m/s Δv");
            if (weak.Length > 0) rep.Add("Słabe ogniwa: " + weak);

            // Program lotu NIGDY nie zmienia rakiety (ISP, ciąg, masy) – rakieta ma być jedna, tylko program lotu się zmienia.
            if (fit) rep.Add("Słowo „dopasuj” jest ignorowane – program lotu nie zmienia parametrów rakiety.");
            if (shortDv || weak.Length > 0) rep.Add("Rakieta może nie dolecieć na tę orbitę – popraw rakietę w VAB (części/paliwo), nie program.");
            foreach (string r in rep) FlightLogger.Event("Start na orbitę: " + r);

            // --- MechJeb
            List<PartModule> cores = new List<PartModule>();
            foreach (Part p in vessel.parts) foreach (PartModule pm in p.Modules) if (pm.GetType().Name == "MechJebCore") cores.Add(pm);
            if (cores.Count == 0) { aoPhase = AO.Failed; return string.Join("\n", rep.ToArray()) + "\nNa rakiecie nie ma MechJeba – dodaj część/moduł MechJeb."; }
            double pitchStart = Math.Max(Tun.D("ao.pitchStartMin"), Math.Min(Tun.D("ao.pitchStartMax"), Tun.D("ao.pitchStartK") / Math.Max(0.8, twr)));
            string[][] asc = {
                new[] { "AscentTypeInteger", "1" }, new[] { "DesiredOrbitAltitude", (altKm * 1000).ToString("F0", Inv) },
                new[] { "DesiredApoapsis", (apKm * 1000).ToString("F0", Inv) }, new[] { "AttachAltFlag", "False" },
                // 1.42.3: MechJeb wstawiał na stałej wysokości 200 km (DesiredAttachAltFixed z jego zapisanych ustawień) mimo celu 220 –
                // sprzeczny cel, PVG palił do końca paliwa (200 × 3079 km). Wysokość wstawienia = perygeum celu.
                new[] { "DesiredAttachAltFixed", (altKm * 1000).ToString("F0", Inv) }, new[] { "DesiredAttachAlt", (altKm * 1000).ToString("F0", Inv) },
                new[] { "DesiredInclination", inc.ToString("F2", Inv) }, new[] { "LastStage", aoLastStage.ToString(Inv) }, new[] { "OptimizeStageFlag", "False" },
                new[] { "CoastStageFlag", "False" }, new[] { "UnguidedStagesFlag", "False" }, new[] { "SkipCircularization", "False" },
                new[] { "PitchStartVelocity", pitchStart.ToString("F0", Inv) }, new[] { "PitchRate", Tun.S("ao.pitchRate") },
                new[] { "Autostage", "True" }, new[] { "ForceRoll", "True" }, new[] { "VerticalRoll", "0" }, new[] { "TurnRoll", "0" } };
            string[][] thr = { new[] { "LimitAcceleration", "True" }, new[] { "MaxAcceleration", Tun.S("ao.maxAccel") } };
            string[][] stg = { new[] { "AutostageLimit", aoLastStage.ToString(Inv) } };
            int bad = 0;
            foreach (PartModule core in cores)
            {
                foreach (var pair in new[] { new { m = "MechJebModuleAscentSettings", v = asc }, new { m = "MechJebModuleThrustController", v = thr }, new { m = "MechJebModuleStagingController", v = stg } })
                {
                    object mod = Module(core, pair.m); if (mod == null) { bad++; continue; }
                    foreach (string[] kv in pair.v) { string err; if (!MechJebPresets.SetMember(mod, kv[0], kv[1], out err)) bad++; }
                }
            }
            // pola MechJeba z pliku programu / Ustawienia.cfg: mj.ascent.<pole> = wartość (szukane w Ascent, Thrust, Staging)
            foreach (var kv in Tun.Mj("ascent"))
            {
                bool okSet = false; string err = "";
                foreach (PartModule core in cores)
                    foreach (string mn in new[] { "MechJebModuleAscentSettings", "MechJebModuleThrustController", "MechJebModuleStagingController" })
                    {
                        object mod = Module(core, mn);
                        if (mod != null && MechJebPresets.SetMember(mod, kv.Key, kv.Value, out err)) okSet = true;
                    }
                rep.Add("MechJeb: " + kv.Key + " = " + kv.Value + (okSet ? "" : " – NIE PRZYJĘTO (zła nazwa pola?)"));
            }
            string mj = MechJebAscent(true);
            aoPhase = AO.Ascent; aoStartUT = Planetarium.GetUniversalTime(); nextAoLog = 0;
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            AutoOrbitInfo = "wznoszenie (MechJeb PVG) na " + OrbitLabel(peKm, apKm) + " / " + inc.ToString("F1", Inv) + "°";
            FlightLogger.Event("Start na orbitę: MechJeb PVG ustawiony (początek zakrętu " + pitchStart.ToString("F0", Inv) + " m/s" + (bad > 0 ? ", " + bad + " ustawień nie przyjęto" : "") + "), " + mj);
            return string.Join("\n", rep.ToArray()) + "\n" + mj;
        }

        public static string OrbitLabel(double peKm, double apKm)
        {
            return apKm > peKm + 0.5 ? peKm.ToString("F0", Inv) + " × " + apKm.ToString("F0", Inv) + " km" : peKm.ToString("F0", Inv) + " km (kołowa)";
        }

        /// <summary>Sprawdzenie dla zakładki: ile Δv trzeba na orbitę i ile ma rakieta (bez startu, bez zmian w rakiecie).</summary>
        public static string CheckAutoOrbit(string val, int lastStage = 0)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "Brak statku.";
            CelestialBody b = v.mainBody;
            double pe, ap, inc; FlightPrograms.ParseAutoOrbit(val, b, out pe, out ap, out inc);
            double lat = Math.Abs(v.latitude);
            if (double.IsNaN(inc) || inc < lat) inc = Math.Ceiling(lat * 10) / 10;
            double need = DeltaVPlanner.ToOrbit(b, pe * 1000, ap * 1000, inc);
            double vac; bool ok = StockDeltaV.TotalVacFrom(lastStage, out vac);
            double twr = StockDeltaV.LiftoffTwr();
            string r = "Cel " + OrbitLabel(pe, ap) + ", " + inc.ToString("F1", Inv) + "°: potrzeba ≈ " + need.ToString("F0", Inv) + " m/s";
            if (!ok || vac <= 0) return r + ", rakieta: brak danych Δv.";
            r += ", rakieta" + (lastStage > 0 ? " (stage'e ≥ " + lastStage + ")" : "") + " " + vac.ToString("F0", Inv) + " m/s (TWR " + twr.ToString("F2", Inv) + ") → ";
            r += vac >= need ? "zapas " + (vac - need).ToString("F0", Inv) + " m/s" : "BRAKUJE " + (need - vac).ToString("F0", Inv) + " m/s";
            if (twr > 0 && twr < 1.15) r += "; za mały ciąg na start";
            return r + " (szacunek ±5 %).";
        }

        private object AscentAutopilot()
        {
            object core = Core(); if (core == null) return null;
            object settings = Module(core, "MechJebModuleAscentSettings"); if (settings == null) return null;
            PropertyInfo ap = settings.GetType().GetProperty("AscentAutopilot", Flags);
            return ap != null ? ap.GetValue(settings, null) : null;
        }

        private bool AscentEnabled()
        {
            object a = AscentAutopilot(); if (a == null) return false;
            PropertyInfo en = a.GetType().GetProperty("Enabled", Flags);
            if (en != null) try { return (bool)en.GetValue(a, null); } catch { }
            FieldInfo ef = a.GetType().GetField("enabled", Flags);
            return ef != null && (bool)ef.GetValue(a);
        }

        // Zdarzenia stagingu podczas wznoszenia („Stage o czasie”, z wzorca rakiety albo wpisane ręcznie):
        //   t – czas lotu (wieża SAS T+114 s, owiewka T+157 s, BECO Atlasa), a – wysokość [km], h – gorące rozdzielenie
        //   (zapłon następnego stopnia, gdy pracującemu zostało ≤ v s – Proton, Titan II, N1, CZ-2/3).
        // Odpala zdarzenie tylko, gdy jego stage jest NASTĘPNY w kolejce; gdy już poszedł (MechJeb, ręcznie) – pomija.
        private void StageEventsTick()
        {
            foreach (FlightPrograms.StageEv ev in aoStageAt)
            {
                if (ev.done) continue;
                int next = vessel.currentStage - 1;
                if (next < ev.stage)
                {
                    ev.done = true;
                    FlightLogger.Event("Start na orbitę: zdarzenie " + ev.kind + ev.v.ToString("0.#", Inv) + ":" + ev.stage + " pominięte – stage " + ev.stage + " już odpalony");
                    continue;
                }
                if (next != ev.stage || ev.stage < aoLastStage) continue;
                bool fire = false; string why = "";
                if (ev.kind == 't') { fire = vessel.missionTime >= ev.v; why = "T+" + vessel.missionTime.ToString("F0", Inv) + " s"; }
                else if (ev.kind == 'a') { fire = vessel.altitude >= ev.v * 1000; why = "wysokość " + (vessel.altitude / 1000).ToString("F1", Inv) + " km"; }
                else if (ev.kind == 'h')
                {
                    double rem = BurnRemaining();
                    fire = rem >= 0 && rem <= ev.v; why = "gorące rozdzielenie, pracującemu stopniowi zostało " + rem.ToString("F1", Inv) + " s";
                }
                if (!fire) continue;
                ev.done = true;
                StageManager.ActivateNextStage();
                FlightLogger.Event("Start na orbitę: stage " + ev.stage + " – " + why);
                return;   // jeden stage na klatkę
            }
        }

        /// <summary>Najkrótszy pozostały czas pracy pracujących silników, które zostaną odrzucone: paliwo za ich odłączaczem ÷ obecny przepływ.
        /// −1 = nic nie pracuje.</summary>
        private double BurnRemaining()
        {
            float thr = vessel.ctrlState != null ? vessel.ctrlState.mainThrottle : 1f;
            if (thr <= 0.01f) return -1;
            var byDec = new Dictionary<Part, List<Part>>();
            foreach (Part p in vessel.parts)
            {
                bool on = false;
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited && !e.flameout && e.propellants != null && !e.propellants.Exists(pr => pr.name == "SolidFuel")) on = true;
                if (!on) continue;
                Part d = EngineGroups.JettisonDecoupler(p);
                if (d == null) continue;
                List<Part> l; if (!byDec.TryGetValue(d, out l)) byDec[d] = l = new List<Part>();
                l.Add(p);
            }
            double best = -1;
            foreach (var kv in byDec)
            {
                double flow = 0;
                foreach (Part p in kv.Value) flow += EngineGroups.MassFlow(p);
                flow *= thr;
                if (flow <= 0) continue;
                double rem = EngineGroups.FuelMass(vessel.parts, kv.Key, EngineGroups.PropellantNames(kv.Value)) / flow;
                if (best < 0 || rem < best) best = rem;
            }
            return best;
        }

        private void AutoOrbitTick()
        {
            CelestialBody b = vessel.mainBody; Orbit o = vessel.orbit;
            double now = Planetarium.GetUniversalTime();
            double atmTop = b.atmosphere ? b.atmosphereDepth : 0;
            if (aoPhase == AO.Ascent)
            {
                AutoOrbitInfo = "wznoszenie: apogeum " + (o.ApA / 1000).ToString("F0", Inv) + " km, perygeum " + (o.PeA / 1000).ToString("F0", Inv) + " km, cel " + aoAlt.ToString("F0", Inv) + " km";
                if (now > nextAoLog) { nextAoLog = now + 20; FlightLogger.Event("Start na orbitę: " + AutoOrbitInfo); }
                // Stage o czasie (np. wieża SAS T+114 s, owiewka T+157 s – MechJeb ich nie odpali, bo nic się wtedy nie wypala).
                // Odpala tylko, gdy NASTĘPNY stage to dokładnie ten z listy – jeśli już poszedł (MechJeb, ręcznie), pomija.
                StageEventsTick();
                bool mjOff = now - aoStartUT > 10 && !AscentEnabled();
                bool fuelOut = now - aoStartUT > 30 && EngineThrustKN() < 0.01 && vessel.currentStage <= aoLastStage && vessel.altitude > atmTop;
                bool inOrbit = o.PeA > atmTop + 5000 && EngineThrustKN() < 0.01 && now - aoStartUT > 30;
                // 1.42.2 (Sojuz 7K-T 25.11): MechJeb wszedł na orbitę 200 km i nie wyłączył silnika – Blok I palił do końca
                // paliwa i zrobił 200 × 3079 km. Gdy perygeum jest już nad atmosferą, a apogeum doszło do celu – wyłączamy sami,
                // resztę (podniesienie perygeum do celu) robi „domykanie orbity”.
                // tylko awaryjnie: apogeum UCIEKA ponad cel (lot 16:20: 209 → 297 km w 2 s). Gdy apogeum stoi przy celu, MechJeb
                // sam kończy (lot 17:13: wyłączenie przy 141 × 222 km było za wczesne – jemu brakowało 1–2 s).
                bool overshoot = now - aoStartUT > Tun.D("ao.overshootAfter") && o.PeA > atmTop && o.ApA >= aoAp * 1000 + Math.Max(Tun.D("ao.overshootMinKm") * 1000, aoAp * 1000 * Tun.D("ao.overshootFrac")) && EngineThrustKN() > 0.01;
                if (!(mjOff || fuelOut || inOrbit || overshoot)) return;
                MechJebAscent(false);
                if (overshoot)
                {
                    vessel.ctrlState.mainThrottle = 0f; FlightInputHandler.state.mainThrottle = 0f;
                    FlightLogger.Event("Start na orbitę: apogeum doszło do celu przy perygeum nad atmosferą (" + (o.PeA / 1000).ToString("F0", Inv) + " × " + (o.ApA / 1000).ToString("F0", Inv) + " km) – wyłączam silnik, MechJeb nie wyłączył");
                }
                bool good = o.PeA > atmTop + 5000 && Math.Abs(o.ApA / 1000 - aoAp) < aoAp * 0.1 + 10 && Math.Abs(o.PeA / 1000 - aoAlt) < aoAlt * 0.1 + 10 && Math.Abs(o.inclination - aoInc) < 1.0;
                if (good)
                {
                    aoPhase = AO.Done; AutoOrbitInfo = "na orbicie " + (o.PeA / 1000).ToString("F0", Inv) + " × " + (o.ApA / 1000).ToString("F0", Inv) + " km, " + o.inclination.ToString("F1", Inv) + "°";
                    FlightLogger.Event("Start na orbitę: " + AutoOrbitInfo + ", Δv zostało " + (StockDeltaV.TotalVacFrom(aoLastStage, out double rest) ? rest.ToString("F0", Inv) : "?") + " m/s");
                    ScreenMessages.PostScreenMessage("Na orbicie: " + AutoOrbitInfo, 6f, ScreenMessageStyle.UPPER_CENTER);
                    SeparatePayload();
                    return;
                }
                if (o.eccentricity >= 1 || vessel.altitude < atmTop * 0.6) { aoPhase = AO.Failed; AutoOrbitInfo = "wznoszenie przerwane nisko – rakieta nie dała rady"; FlightLogger.Event("Start na orbitę: " + AutoOrbitInfo); return; }
                FlightLogger.Event("Start na orbitę: MechJeb skończył przy " + (o.PeA / 1000).ToString("F0", Inv) + " × " + (o.ApA / 1000).ToString("F0", Inv) + " km – domykam orbitę");
                aoPhase = AO.Finish;
                // 1.47.3 (lot 27.11 15:28): Blok I wypalony przy −562 × 193 km, tuż za apogeum. Domykanie czekało na NASTĘPNE
                // apogeum (za ~80 min, po drodze atmosfera) – statek spadł, choć KTDU miało 386 m/s. Teraz: tor suborbitalny
                // i rakieta pusta → od razu oddzielenie statku i domykanie jego silnikiem; ОДУ-1 natychmiast, gdy już po apogeum.
                if (o.PeA < atmTop && aoLastStage > 0 && vessel.currentStage == aoLastStage && (fuelOut || EngineThrustKN() < 0.01 && !RocketHasFuel()))
                {
                    FlightLogger.Event("Start na orbitę: rakieta bez paliwa na torze suborbitalnym – oddzielam statek, orbitę domknie jego silnik (od razu)");
                    SeparatePayload();
                    aoFinishAfterSep = true;
                    return;
                }
                double incT = Math.Abs(o.inclination - aoInc) < 1.0 ? o.inclination : aoInc;
                StartManeuver(aoAlt, aoAp, incT);
                return;
            }
            if (aoPhase == AO.Finish)
            {
                if (aoFinishAfterSep)
                {
                    AutoOrbitInfo = "oddzielanie statku – potem domknięcie orbity jego silnikiem";
                    if (!double.IsNaN(sepPayloadUT) || vessel.currentStage >= aoLastStage) return;
                    aoFinishAfterSep = false; mUrgent = true;
                    StartManeuver(aoAlt, aoAp, Math.Abs(o.inclination - aoInc) < 1.0 ? o.inclination : aoInc);
                    return;
                }
                AutoOrbitInfo = "domykanie orbity: " + ManeuverInfo;
                if (mph == MPh.Done)
                {
                    aoPhase = AO.Done; AutoOrbitInfo = "na orbicie " + (o.PeA / 1000).ToString("F0", Inv) + " × " + (o.ApA / 1000).ToString("F0", Inv) + " km, " + o.inclination.ToString("F1", Inv) + "°";
                    FlightLogger.Event("Start na orbitę: " + AutoOrbitInfo);
                    ScreenMessages.PostScreenMessage("Na orbicie: " + AutoOrbitInfo, 6f, ScreenMessageStyle.UPPER_CENTER);
                    SeparatePayload();
                }
            }
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
            HacInit();
            {
                // już na prostej (OK-GLI po kręgu, albo Buran na osi pasa): od razu końcówka; inaczej tor przez HAC
                Vector3d pos0 = vessel.CoMD - vessel.mainBody.position, rel0 = pos0 - thrPos;
                double d0 = -Vector3d.Dot(rel0, rwyDir), y0 = Vector3d.Dot(rel0, Vector3d.Cross(pos0.normalized, rwyDir).normalized * -1);
                Vector3d vel0 = vessel.srf_velocity; double psi0 = Mod(Math.Atan2(Vector3d.Dot(vel0, hacE), Vector3d.Dot(vel0, hacN)) * 180 / Math.PI, 360);
                hacFinal = d0 > 0 && d0 < 25000 && Math.Abs(y0) < 1500 && Math.Abs(W180(psi0 - rwyHeading)) < 30;
                if (s1k1 && Math.Abs(W180(rwyHeading - s1k1Course)) > 90) hacFinal = false;   // 1K1: kurs lądowania z programu
                if (!hacFinal)
                {
                    double h0 = vessel.altitude - rwyAlt;
                    HacOpt o = HacPick(HacP(pos0), psi0, vessel.srfSpeed, h0);
                    HacApply(o);
                    FlightLogger.Event("Podejście (HAC): pas kurs " + rwyHeading.ToString("F0", Inv) + "°, okrąg " + (hacSide > 0 ? "w prawo" : "w lewo") + ", wyjście " + (hacXa / 1000).ToString("F0", Inv) + " km przed progiem, tor " + (o.L / 1000).ToString("F0", Inv) + " km, zapas energii " + o.exc.ToString("F0", Inv) + " m");
                }
            }
            jetsAvail = HasJets(); jetAssist = false; vsCmdPrev = double.NaN;
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
        private bool jetsAvail, jetAssist;
        private double vsCmdPrev = double.NaN;
        private string patternPhase = "";
        private readonly List<Vector2d> patWps = new List<Vector2d>();
        private int patWp;
        private static double PatBank { get { return Tun.D("pat.bank"); } }       // przechylenie w zakrętach kręgu
        private double patR, patDInt;            // promień zakrętu, odległość wejścia na ścieżkę 19° (od progu)
        public List<Vector2d> PatternCorners { get { return patWps; } }
        public int PatternLeg { get { return patWp; } }
        public bool PatternActive { get { return patternOn; } }
        public bool PatternFinalActive { get { return patternOn && patFinal; } }
        public double GlideInterceptD { get { return patDInt > 0 ? patDInt : PatAlt / Math.Tan(SteepPathDeg * Math.PI / 180) + SteepPathAim; } }
        /// <summary>Próg i kierunek lądowania (wektory względem środka ciała) – dla planszetu ПА-3.</summary>
        public bool RunwayGeo(out Vector3d thr, out Vector3d dir, out double len)
        {
            thr = dir = Vector3d.zero; len = 0;
            if (!rwySet || vessel == null) return false;
            Vector3d a = Surf(rwyLatA, rwyLonA, rwyAlt), b = Surf(rwyLatB, rwyLonB, rwyAlt);
            len = (b - a).magnitude;
            if (patternOn || approachOn) { thr = thrPos; dir = rwyDir; return true; }
            Vector3d up = (vessel.CoMD - vessel.mainBody.position).normalized;
            Vector3d vh = Vector3d.Exclude(up, (Vector3d)vessel.srf_velocity);
            if (vh.magnitude < 1) vh = Vector3d.Exclude(up, (Vector3d)vessel.transform.up);
            Vector3d ab = (b - a).normalized;
            if (Vector3d.Dot(vh, ab) >= 0) { thr = a; dir = ab; } else { thr = b; dir = -ab; }
            return true;
        }
        private static double PatAlt { get { return Tun.D("pat.alt"); } }      // wysokość kręgu nad pasem (punkt kluczowy Burana: ~4 km, 12,5 km przed pasem)

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
            // Krąg prostokątny jak w lotnictwie: cztery zakręty po 90° (lewe), układ pasa: x wzdłuż kierunku lądowania od
            // progu, y w PRAWO (ta sama oś co w RunwayFrame i na planszecie ПА-3). Promień zakrętu z prędkości kręgu na
            // wysokości kręgu i przechylenia 30°; zakręt zaczyna się R przed narożnikiem, więc kończy się na nowym boku.
            // Lot 2026-11-22 02:18: stare punkty „po skosie” (1→2 i 3→4) dawały zakręty ~45° i ścinanie narożników,
            // a wyjście na prostą wypadało w złym miejscu. Teraz: 4. zakręt kończy się na osi pasa ~3 km przed miejscem
            // wejścia na stromą ścieżkę 19° (to samo miejsce co dla orbitalnego Burana), bok z wiatrem jest odpowiednio długi.
            double vStP = StallEas, rhoP = 1.225 * Math.Exp(-PatAlt / 8500.0);
            double tasP = 1.9 * vStP * Math.Sqrt(1.225 / rhoP);
            patR = tasP * tasP / (9.81 * Math.Tan(PatBank * Math.PI / 180));
            patDInt = PatAlt / Math.Tan(SteepPathDeg * Math.PI / 180) + SteepPathAim;
            double W = 2 * patR + 6000, U = patR + 3000, Xb = patDInt + 3000 + patR;
            patWps.Clear();
            patWps.Add(new Vector2d(len + U, 0));        // koniec prostej startowej – 1. zakręt
            patWps.Add(new Vector2d(len + U, -W));       // koniec odcinka przecinającego – 2. zakręt
            patWps.Add(new Vector2d(-Xb, -W));           // koniec boku z wiatrem – 3. zakręt
            patWps.Add(new Vector2d(-Xb, 0));            // koniec bazy – 4. zakręt (wyjście na oś pasa)
            patWps.Add(new Vector2d(0, 0));              // prosta do progu
            abOn = false; Afterburner(true, "start i wznoszenie");
            FlightLogger.Event("Krąg: promień zakrętu " + (patR / 1000).ToString("F1", Inv) + " km (przechylenie " + PatBank.ToString("F0", Inv) + "°, TAS ~" + tasP.ToString("F0", Inv) +
                " m/s), szerokość " + (W / 1000).ToString("F1", Inv) + " km, 4. zakręt kończy się " + ((Xb - patR) / 1000).ToString("F1", Inv) + " km przed progiem, ścieżka 19° od " + (patDInt / 1000).ToString("F1", Inv) + " km");
            patWp = 0; patAirborne = false; patGearUp = false; patFinal = false; airUT = -1; rotTarget = double.NaN; lastPitch = double.NaN;
            foreach (Part p in vessel.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (!e.EngineIgnited && e.propellants.Exists(pr => pr.name == "IntakeAir")) e.Activate();
            vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            object pool = Users(ap);
            if (pool != null) PoolCall(pool, "Add");
            SetF(ap, "PitchDownLimit", 15.0); SetF(ap, "PitchUpLimit", 20.0);
            SetF(ap, "RollLimit", PatBank + 5); SetF(ap, "BankAngle", PatBank);
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
                // Lot 2026-11-21 20:06: OK-GLI (93 t, 4× AL-31) sam unosi się z pasa przy ~80 m/s EAS przy nosie na 0,5°.
                // Przy oderwaniu 1,45× (113 m/s) trzymanie nosa „w dół” dociskało go do pasa → podskoki, uderzenie nosem,
                // przewrotka. Teraz obrót od ~0,95× przeciągnięcia (wg wzoru), łagodnie do 7°, a po pierwszym oderwaniu
                // nasz FBW trzyma kąt (z tłumieniem) aż do 30 m – bez pchania nosa w dół przy ziemi.
                double vr = 0.95 * vSt;
                HookSteer();
                steerHdg = CenterlineHeading(pos, up);
                SetF(ap, "HeadingTarget", steerHdg);
                double nowUT = Planetarium.GetUniversalTime();
                if (!vessel.Landed && airUT < 0) { airUT = nowUT; FlightLogger.Event("Krąg: koła w powietrzu, EAS " + easNow.ToString("F0", Inv) + " m/s, h " + h.ToString("F0", Inv) + " m"); }
                takeoffRoll = true; takeoffRotate = easNow > vr || airUT >= 0;
                SetF(ap, "VertSpeedTarget", takeoffRotate ? 2.0 : 0.0);
                patternPhase = (airUT >= 0 ? "oderwanie – trzymam 7°, h " + h.ToString("F0", Inv) + " m" : "rozbieg") + "  EAS " + easNow.ToString("F0", Inv) + " / obrót " + vr.ToString("F0", Inv) + " m/s";
                // Oddanie sterowania autopilotowi kręgu dopiero gdy Buran naprawdę leci: ≥ 40 m, nos uspokojony, prędkość
                // ≥ 1,2× przeciągnięcia (albo awaryjnie > 120 m). Lot 2026-11-23 16:10: podskoki na pasie, oddanie na 10 m
                // przy nosie 12° i +10 m/s – autopilot pchnął nos do −18° i Buran uderzył w ziemię. Lot 2026-11-21 20:48
                // (udany) oddawał przy 98 m/s, nos 4°, spokojnie.
                bool calm = Math.Abs(fbwPitchRate) < 3.0 && easNow > 1.2 * vSt;
                if (!vessel.Landed && ((h > 40 && calm) || h > 120))
                { patAirborne = true; steerHdg = double.NaN; takeoffRoll = false; FlightLogger.Event("Krąg: oderwanie, EAS " + easNow.ToString("F0", Inv) + " m/s, h " + h.ToString("F0", Inv) + " m"); }
                return;
            }
            if (!patGearUp && h > 150) { patGearUp = true; vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, false); FlightLogger.Event("Krąg: podwozie schowane"); }
            // jak w OK-GLI: dopalacz do startu i wznoszenia, na wysokości kręgu tryb maksymalny bez dopalacza
            if (abOn && h > PatAlt - 150) Afterburner(false, "wysokość kręgu " + h.ToString("F0", Inv) + " m");
            if (patFinal) { PatternFinal(ap, pos, up, x, h, easNow, vSt); return; }

            // prowadzenie po bokach kręgu: kurs boku + poprawka na odchylenie boczne, zakręt R przed narożnikiem
            Vector3d rightT = Vector3d.Cross(up, rwyDir).normalized;      // prawdziwe „w prawo” (jak RunwayFrame)
            Vector2d P = new Vector2d(x, Vector3d.Dot(rel, rightT));
            Vector2d A = patWp == 0 ? new Vector2d(0, 0) : patWps[patWp - 1], B = patWps[patWp];
            Vector2d u = (B - A).normalized, nR = new Vector2d(-u.y, u.x);
            double remain = Vector2d.Dot(B - P, u), e = Vector2d.Dot(P - A, nR);
            double legHdg = rwyHeading + Math.Atan2(u.y, u.x) * 180 / Math.PI;
            double hdg = (legHdg - Math.Atan2(e, 2500) * 180 / Math.PI + 720) % 360;
            double dist = Math.Max(0, remain);
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
            if (h < 100) vs = Math.Max(vs, 0.0);   // nisko po starcie nigdy nie każ schodzić
            SetF(ap, "HeadingTarget", hdg);
            SetF(ap, "VertSpeedTarget", vs);
            SetF(ap, "RollLimit", StallLevel >= 1 ? 5.0 : easNow < 1.6 * vSt ? 15.0 : PatBank + 5);
            string[] names = { "prosta startowa (wznoszenie)", "odcinek przecinający (po 1. zakręcie)", "z wiatrem (po 2. zakręcie)", "baza (po 3. zakręcie)", "prosta do lądowania (po 4. zakręcie)" };
            patternPhase = "krąg: " + names[Math.Min(patWp, names.Length - 1)] + ", do zakrętu " + (dist / 1000).ToString("F1", Inv) + " km, z boku " + (e / 1000).ToString("F1", Inv) +
                           " km, h " + h.ToString("F0", Inv) + " m, EAS " + easNow.ToString("F0", Inv) + " m/s";
            if (Planetarium.GetUniversalTime() > nextALog) { nextALog = Planetarium.GetUniversalTime() + 10; FlightLogger.Event("Krąg: " + patternPhase); }
            double v = Math.Max(50, vessel.srfSpeed);
            double lead = v * v / (9.81 * Math.Tan(PatBank * Math.PI / 180));   // promień przy obecnej prędkości
            if (patWp < patWps.Count - 1 && remain <= lead)
            {
                patWp++;
                FlightLogger.Event("Krąg: " + patWp + ". zakręt w lewo o 90° → " + names[patWp] + " (promień " + (lead / 1000).ToString("F1", Inv) + " km, h " + h.ToString("F0", Inv) + " m)");
            }
            else if (patWp == patWps.Count - 1 && Math.Abs(e) < 500)
            {
                double trk = HeadingOf(vessel.srf_velocity, up, north, east);
                if (Math.Abs(((trk - rwyHeading) + 540) % 360 - 180) < 12)
                { patFinal = true; FlightLogger.Event("Krąg: na prostej do lądowania " + (-x / 1000).ToString("F1", Inv) + " km przed progiem, z boku " + e.ToString("F0", Inv) + " m – lot poziomy z silnikami do ścieżki 19°"); }
            }
        }

        // Prosta z napędem: lot poziomy na wysokości kręgu po osi pasa, aż Buran znajdzie się NA stromej ścieżce 19°
        // (cel 2,5 km przed progiem – ta sama ścieżka co w podejściu z orbity). Dopiero wtedy silniki wyłączone.
        // Lot 2026-11-21 20:48: silniki wyłączone 17,4 km przed pasem na 3,9 km – ścieżka była na 5,1 km, Buran „poniżej
        // ścieżki” szybował na najlepszej doskonałości, zgubił prędkość i trzy razy wpadał w przeciągnięcie.
        // Orbitalny Buran (lot 2026-11-20 23:23, udany) wchodził na ścieżkę ~10 km przed progiem na ~4,8 km – nad nią.
        private bool patFinal;

        // Stroma ścieżka Burana – JEDNA definicja dla podejścia z orbity i dla kręgu OK-GLI (19°, cel 2,5 km przed progiem).
        private static double SteepPathDeg { get { return VehicleProfile.Current.steepDeg; } }   // z profilu statku (Buran: 19°, 2,5 km)
        private static double SteepPathAim { get { return VehicleProfile.Current.steepAim; } }
        private static double SteepPathHeight(double d) { return (d - SteepPathAim) * Math.Tan(SteepPathDeg * Math.PI / 180); }
        private void PatternFinal(object ap, Vector3d pos, Vector3d up, double x, double h, double easNow, double vSt)
        {
            double d = -x;
            double ht = SteepPathHeight(d);
            steerHdg = double.NaN;
            SetF(ap, "HeadingTarget", CenterlineHeading(pos, up));
            SetF(ap, "VertSpeedTarget", Math.Max(-10, Math.Min(10, 0.05 * (PatAlt - h))));
            SetF(ap, "RollLimit", 20.0);
            patternPhase = "prosta z silnikami: d " + (d / 1000).ToString("F1", Inv) + " km, h " + h.ToString("F0", Inv) + " m, ścieżka 19° na " + Math.Max(0, ht).ToString("F0", Inv) + " m, EAS " + easNow.ToString("F0", Inv) + " m/s";
            if (Planetarium.GetUniversalTime() > nextALog) { nextALog = Planetarium.GetUniversalTime() + 5; FlightLogger.Event("Krąg: " + patternPhase); }
            // pochylanie na ścieżkę zaczynamy ~350 m wcześniej – zejście z lotu poziomego do −19° trwa kilkanaście sekund
            // (lot 2026-11-22 02:18: skok zadanej prędkości pionowej z 0 na −52 m/s → gwałtowne pchnięcie, α 25°, korkociąg)
            if (h < ht - 350 && d > 2500) return;
            // na ścieżce: silniki wyłączone – dalej jak Buran, bez napędu
            Call(ap, "DisableSpeedHold");
            JetsOff();
            patternOn = false; patFinal = false;
            FlightLogger.Event("Krąg: na ścieżce 19° (d " + (d / 1000).ToString("F1", Inv) + " km, h " + h.ToString("F0", Inv) + " m) – silniki wyłączone, podejście jak Buran");
            StartApproach();
        }

        private bool HasJets()
        {
            foreach (Part p in vessel.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.propellants.Exists(pr => pr.name == "IntakeAir")) return true;
            return false;
        }

        private void JetsOff()
        {
            FlightInputHandler.state.mainThrottle = 0f;
            foreach (Part p in vessel.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited && e.propellants.Exists(pr => pr.name == "IntakeAir")) e.Shutdown();
        }

        // Dopalacz AL-31F: w KSP ogranicznik ciągu mnoży przepustnicę, a krzywa ISP od przepustnicy (Patches/Buran_AL31.cfg)
        // daje tryb bez dopalacza do 24,4 % – więc ogranicznik 24,4 % = „dopalacz wyłączony”, 100 % = dostępny.
        private static float AbOffLimit { get { return (float)Tun.D("ab.offLimit"); } }
        private bool abOn = true;
        private void Afterburner(bool on, string why)
        {
            int n = 0;
            foreach (Part p in vessel.parts)
                if (p.partInfo != null && p.partInfo.name.Replace('.', '_').EndsWith("_AL31F"))   // KSP zamienia „_” w nazwie części na „.”
                    foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>()) { e.thrustPercentage = on ? 100f : AbOffLimit; n++; }
            if (n > 0 && on != abOn) FlightLogger.Event("Dopalacz AL-31F " + (on ? "dostępny" : "wyłączony (tryb maksymalny bez dopalacza)") + " – " + why);
            abOn = on;
        }

        private void JetsOn()
        {
            foreach (Part p in vessel.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (!e.EngineIgnited && e.propellants.Exists(pr => pr.name == "IntakeAir")) e.Activate();
            Afterburner(true, "wspomaganie w podejściu");
            FlightInputHandler.state.mainThrottle = 1f;
        }


        // ------------------------------------------------------------------ sterowanie kołem przednim na ziemi
        // MechJeb (Aircraft Autopilot) steruje tylko lotkami/sterem kierunku – na rozbiegu i dobiegu przy małej
        // prędkości ster nic nie daje, a koła nie skręcają (lot próbny 19:22: Buran zjechał z pasa w lewo).
        private Vessel steerVessel;
        private double steerHdg = double.NaN;
        // Rozbieg: silniki OK-GLI są wysoko nad środkiem masy – ciąg dociska nos i odciąża koła główne (lot 2026-11-21
        // 16:42: pochylenie −2,5°, a MechJeb, trzymając prędkość pionową 0, dodawał jeszcze sterem „nos w dół”; koła
        // główne prawie bez nacisku nie trzymały kierunku i Buran zjechał w prawo z pasa). Na rozbiegu sami trzymamy
        // pochylenie: nos poziomo (+0,5°), od prędkości oderwania – podniesienie nosa do 8°.
        private bool takeoffRoll, takeoffRotate;
        private double airUT = -1, rotTarget = double.NaN, lastPitch = double.NaN, fbwPitchRate;

        private void HookSteer()
        {
            if (steerVessel == vessel) return;
            if (steerVessel != null) steerVessel.OnFlyByWire -= SteerFbw;
            if (vessel != null) vessel.OnFlyByWire += SteerFbw;
            steerVessel = vessel;
        }

        // Linia ciągu przez środek masy (przekłamanie): gondole AL-31 siedzą 2–4 m nad środkiem masy KSP, ~380 kN ciągu
        // daje ~0,75 MN·m momentu „nos w dół”, którego elewony przy 110 m/s nie równoważą (lot 2026-11-21 17:38: zaraz po
        // oderwaniu nos spadł z +10° do −22° w sekundę). W prawdziwym OK-GLI ciąg był wyważony konstrukcyjnie. Tu, gdy
        // pracują silniki odrzutowe, dodajemy moment przeciwny do momentu ciągu względem środka masy – jakby ciąg szedł
        // przez środek masy. Tylko w atmosferze i tylko silniki na IntakeAir.
        private void ThrustLineFix()
        {
            Vessel v = vessel;
            if (v == null || !v.mainBody.atmosphere || v.packed) return;
            Vector3d com = v.CoMD, m = Vector3d.zero;
            foreach (Part p in v.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                {
                    if (!e.EngineIgnited || e.finalThrust <= 0 || !e.propellants.Exists(pr => pr.name == "IntakeAir")) continue;
                    int n = e.thrustTransforms.Count; if (n == 0) continue;
                    for (int i = 0; i < n; i++)
                    {
                        Transform t = e.thrustTransforms[i];
                        float mult = e.thrustTransformMultipliers != null && e.thrustTransformMultipliers.Count == n ? e.thrustTransformMultipliers[i] : 1f / n;
                        Vector3d f = -t.forward * e.finalThrust * mult;
                        m += Vector3d.Cross((Vector3d)t.position - com, f);
                    }
                }
            if (m.sqrMagnitude < 1) return;
            Rigidbody rb = v.rootPart != null ? v.rootPart.Rigidbody : null;
            if (rb != null) rb.AddTorque(-(Vector3)m, ForceMode.Force);
        }

        private void SteerFbw(FlightCtrlState s)
        {
            if (patternOn) ThrustLineFix();
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
            if (takeoffRoll && patternOn)
            {
                double pitchDeg = Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(fwd, up)))) * 180 / Math.PI;
                double dt = Math.Max(0.005, TimeWarp.fixedDeltaTime);
                double rate = double.IsNaN(lastPitch) ? 0 : (pitchDeg - lastPitch) / dt;   // °/s, + = nos w górę
                lastPitch = pitchDeg;
                fbwPitchRate = 0.8 * fbwPitchRate + 0.2 * rate;
                if (!takeoffRotate)
                {
                    rotTarget = double.NaN;
                    s.pitch = Mathf.Clamp((float)((0.5 - pitchDeg) * 0.25 - rate * 0.05), -0.15f, 0.5f);
                }
                else
                {
                    // cel rośnie od bieżącego kąta o 1,5°/s do 7°; PD z tłumieniem prędkości kątowej, przy ziemi nos najwyżej lekko w dół
                    if (double.IsNaN(rotTarget)) rotTarget = Math.Max(0.5, pitchDeg);
                    // obrót szybciej (3°/s) – Buran ma zejść z pasa zdecydowanie, zanim zacznie podskakiwać na podwoziu
                    rotTarget = Math.Min(7.0, rotTarget + 3.0 * dt);
                    float lo = vessel.Landed || vessel.radarAltitude < 15 ? -0.08f : -0.25f;
                    s.pitch = Mathf.Clamp((float)((rotTarget - pitchDeg) * 0.06 - rate * 0.10), lo, 0.4f);
                }
            }
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
            vStall = VehicleProfile.Current.StallEas(vessel.totalMass);
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

        // ------------------------------------------------------------------ HAC – okrąg wyrównania kursu (1.41.3)
        // Lot 24.11 22:14 (lądowanie po 2 okrążeniach jak 1K1): Buran przejął podejście 10 km ZA pasem, lecąc od niego.
        // Stare podejście: koniec pasa „bliższy kursowi”, cel 12 km przed progiem po drugiej stronie, przy nadmiarze
        // krążenie, a w dolocie nurkowanie do EAS 150 m/s przy M 0,7–0,85 (doskonałość tylko ~2,6) → zjadło energię,
        // przyziemienie 2,7 km przed progiem i 3 km w bok. Teraz jak w Buranie/Shuttle: tor przez okrąg HAC stycznie do
        // osi pasa (wyjście na osi 12 km przed progiem, w razie braku energii 8 albo 5 km), wybór końca pasa i strony
        // okręgu po najkrótszym wystarczającym torze, energia z wysokości i prędkości: za mało – najlepsze szybowanie
        // (EAS ~1,4× przeciągnięcia), nadmiar – szybciej, hamulec, esowanie przed HAC, dodatkowe okrążenie na HAC.
        // Sprawdzone offline (model aerodynamiki z logów 18:34 i 22:14, ten sam autopilot): stare podejście odtwarza oba loty
        // (22:14: 2,1 km przed progiem; 18:34: na pasie), nowe ląduje w obu; przejęcie ≤ 15 km od pasa w losowym kursie:
        // 76/80 na pasie (stare 25/80), przy doskonałości −15%: 75/80 (stare 9/80).
        private bool hacFinal, hacOnH, hacSturn;
        // ---- schemat podejścia jak w 1K1 („scheme = 1K1” w programie powrotu; 1.42.0) ----
        // Źródła: buran.ru (landing.htm, algoritm.htm), MOKB „Марс” (Буран.pdf), ru.wikipedia (Полёт 1K1).
        // Na 20 km Buran ma lecieć stycznie do cylindra rozpraszania energii (ЦРЭ, promień 25 km, środek na przedłużeniu
        // osi pasa 24 km od jego środka, po stronie podejścia), 32 ± 13 km przed punktem styczności, z prędkością 520 ± 60 m/s;
        // ЦРЭ i kierunek lądowania wybiera wiatr (w 1K1 kurs 246° – pas 24, wschodni ЦРЭ, prawa styczna). Dalej okrąg
        // wyrównania kursu (ЦВК, północny albo południowy), punkt kluczowy 4 km ~14,5 km od środka pasa, stroma ścieżka.
        // W KSP nie ma wiatru – kurs lądowania podaje program (landingCourse).
        private bool s1k1, hacLatchOn, hacCommitted;
        private double s1k1Course = 246.6, s1k1CreD = 24000, s1k1CreR = 25000, s1k1TaemOff = 100000, s1k1Aim = 20000;
        private bool creDone, creFrozen;
        private Vector2d creAimPt, creAimDir;   // zamrożony cel wejścia (creAim przed punktem styczności ЦРЭ) i kierunek stycznej
        private int s1k1CreSide = 1;      // +1: ЦРЭ po prawej stronie toru (prawa styczna, obieg w prawo – jak 1K1)
        private bool creOn;               // wejście: już na okręgu ЦРЭ
        private int hacSide = 1, hacLap, hacSsgn = 1;
        private double hacXa = 12000, hacPrevArc = double.NaN, hacExc;
        private Vector3d hacC0, hacE, hacN;              // płaski układ przy pasie: początek, wschód, północ
        private static double HacLDP { get { return Tun.D("hac.ldp"); } }
        private static double HacEM { get { return Tun.D("hac.em"); } }
        private static double HacBank { get { return VehicleProfile.Current.hacBank; } }
        private static readonly double[] HacXas = { 12000, 8000, 5000 };

        private Vector2d HacP(Vector3d pos) { Vector3d r = pos - hacC0; return new Vector2d(Vector3d.Dot(r, hacE), Vector3d.Dot(r, hacN)); }
        private Vector2d HacD(Vector3d dir) { Vector2d d = new Vector2d(Vector3d.Dot(dir, hacE), Vector3d.Dot(dir, hacN)); return d / d.magnitude; }
        private static double Mod(double a, double m) { a %= m; return a < 0 ? a + m : a; }
        private static double W180(double a) { return Mod(a + 180, 360) - 180; }
        /// <summary>Doskonałość Burana w locie poddźwiękowym z logów 18:34 i 22:14: zależy od EAS i Macha (M 0,6–0,9 tylko ~0,7×).</summary>
        private static double HacLD(double eas, double M) { return VehicleProfile.Current.GlideLD(eas, M); }
        private static double Interp(double x, double[] xs, double[] ys)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++) if (x <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return ys[ys.Length - 1];
        }
        private static double HacRh(double v) { return Math.Max(5000, Math.Min(9000, v * v / (9.81 * Math.Tan(HacBank * Math.PI / 180)))); }
        private static double HacRv(double v) { return v * v / (9.81 * Math.Tan(30 * Math.PI / 180)); }
        private double HacEreq(double L, double xa) { double vS = StallEas; return SteepPathHeight(xa) + HacEM + L / HacLDP + (1.6 * vS) * (1.6 * vS) / (2 * 9.81); }

        /// <summary>Długość toru: zakręt na styczną + prosta do okręgu + łuk do wyjścia na osi pasa (xa przed progiem).
        /// side = +1: środek okręgu na prawo od osi – obieg w prawo.</summary>
        private static double HacPath(Vector2d p, double psi, Vector2d thr, Vector2d dir, int side, double Rh, double Rv, double xa,
                                      out double tbDeg, out Vector2d C, out double arc)
        {
            Vector2d right = new Vector2d(dir.y, -dir.x);
            Vector2d X = thr - dir * xa; C = X + right * (side * Rh);
            bool cw = side > 0;
            Vector2d PC = C - p; double dc = PC.magnitude; if (dc < Rh * 1.02) dc = Rh * 1.02;
            double a = Math.Asin(Rh / dc), bC = Math.Atan2(PC.x, PC.y);
            double tb = cw ? bC - a : bC + a;
            double L1 = Math.Sqrt(Math.Max(0, dc * dc - Rh * Rh));
            Vector2d T = p + new Vector2d(Math.Sin(tb), Math.Cos(tb)) * L1;
            Vector2d tc = T - C, xc = X - C;
            double a0 = Math.Atan2(tc.x, tc.y), a1 = Math.Atan2(xc.x, xc.y);
            arc = cw ? Mod(a1 - a0, 2 * Math.PI) : Mod(a0 - a1, 2 * Math.PI);
            tbDeg = Mod(tb * 180 / Math.PI, 360);
            return Rv * Math.Abs(W180(tbDeg - psi)) * Math.PI / 180 + L1 + Rh * arc;
        }

        private struct HacOpt { public double exc, L, xa; public bool endA; public int side; }

        private HacOpt HacPick(Vector2d p, double psi, double v, double h)
        {
            Vector3d a3 = Surf(rwyLatA, rwyLonA, rwyAlt), b3 = Surf(rwyLatB, rwyLonB, rwyAlt);
            Vector2d A = HacP(a3), B = HacP(b3), ab = HacD(b3 - a3);
            double Rh = HacRh(v), Eh = h + v * v / (2 * 9.81), tb, arc; Vector2d C;
            HacOpt best = new HacOpt { exc = double.NegativeInfinity }; bool feas = false;
            Vector2d crs1 = new Vector2d(Math.Sin(s1k1Course * Math.PI / 180), Math.Cos(s1k1Course * Math.PI / 180));
            for (int e = 0; e < 2; e++)
            {
                if (s1k1 && Vector2d.Dot(e == 0 ? ab : -ab, crs1) < 0) continue;   // 1K1: tylko kurs lądowania z programu (wiatr)
                for (int sd = 1; sd >= -1; sd -= 2)
                    foreach (double xa in HacXas)
                    {
                        double L = HacPath(p, psi, e == 0 ? A : B, e == 0 ? ab : -ab, sd, Rh, HacRv(v), xa, out tb, out C, out arc);
                        HacOpt o = new HacOpt { exc = Eh - HacEreq(L, xa), L = L, xa = xa, endA = e == 0, side = sd };
                        bool f = o.exc >= 0;
                        // wystarczające: najdalsze wyjście (12 km), z nich najmniejszy nadmiar; brak – największa energia
                        if (f && (!feas || o.xa > best.xa || (o.xa == best.xa && o.exc < best.exc))) { best = o; feas = true; }
                        else if (!f && !feas && o.exc > best.exc) best = o;
                    }
            }
            return best;
        }

        private void HacApply(HacOpt o)
        {
            Vector3d a = Surf(rwyLatA, rwyLonA, rwyAlt), b = Surf(rwyLatB, rwyLonB, rwyAlt);
            Vector3d ab = (b - a).normalized;
            if (o.endA) { thrPos = a; rwyDir = ab; } else { thrPos = b; rwyDir = -ab; }
            rwyHeading = Mod(Math.Atan2(Vector3d.Dot(rwyDir, hacE), Vector3d.Dot(rwyDir, hacN)) * 180 / Math.PI, 360);
            hacSide = o.side; hacXa = o.xa;
        }

        private void HacInit()
        {
            Vector3d a = Surf(rwyLatA, rwyLonA, rwyAlt);
            hacC0 = a; Vector3d up = a.normalized;
            hacN = Vector3d.Exclude(up, vessel.mainBody.transform.up).normalized; hacE = Vector3d.Cross(up, hacN).normalized;
            hacOnH = hacSturn = false; hacLap = 0; hacSsgn = 1; hacPrevArc = double.NaN; hacLatchOn = hacCommitted = false;
        }

        /// <summary>Krok HAC: kurs, prędkość pionowa, limit przechylenia, hamulec. false – czas na końcówkę (stroma ścieżka).</summary>
        private bool HacStep(Vector3d pos, double h, double eas, double y, out double hdg, out double vs, out double roll, out bool brake)
        {
            double vSt = StallEas, v = vessel.srfSpeed, M = vessel.mach;
            Vector2d p = HacP(pos);
            Vector3d vel = vessel.srf_velocity;
            double psi = Mod(Math.Atan2(Vector3d.Dot(vel, hacE), Vector3d.Dot(vel, hacN)) * 180 / Math.PI, 360);
            Vector2d thr = HacP(thrPos), dir = HacD(rwyDir);
            double Rh = HacRh(v), tb, arc; Vector2d C;
            double L = HacPath(p, psi, thr, dir, hacSide, Rh, HacRv(v), hacXa, out tb, out C, out arc);
            bool cw = hacSide > 0;
            double dist = (p - C).magnitude;
            bool onHac = dist < Rh * 1.25 && Math.Abs(W180(tb - psi)) < 40;
            // 1K1: zatrzask – przy okręgu (≤1,6 R) albo po wejściu na niego bez ponownego wyboru; po wejściu na okrąg bez
            // przeskoku na drugą stronę osi (lot 24.11 23:35: przeskok w połowie zakrętu, wyjście 2,3 km nad ścieżką)
            bool near = false;
            if (s1k1)
            {
                if (onHac) { hacLatchOn = true; if (!hacCommitted) { hacCommitted = true; FlightLogger.Event("Podejście (1K1): na ЦВК " + (hacSide > 0 ? "w prawo" : "w lewo") + " – wybór zablokowany"); } }
                if (dist > 2 * Rh) hacLatchOn = false;
                near = hacLatchOn || dist < 1.6 * Rh;
            }
            double Eh = h + v * v / (2 * 9.81);
            double exc = Eh - HacEreq(L + hacLap * 2 * Math.PI * Rh, hacXa);
            if (!onHac && hacLap == 0 && !near)
            {
                // ponowny wybór (koniec pasa / strona okręgu / bliższe wyjście), gdy obecny tor nie wystarcza
                HacOpt bst = HacPick(p, psi, v, h);
                if (((exc < -300 && bst.exc >= 0) || (bst.exc >= 0 && bst.xa > hacXa) || (exc < -300 && bst.exc > exc + 500)) &&
                    !(s1k1 && hacCommitted && bst.side != hacSide))
                {
                    bool chg = bst.side != hacSide || bst.xa != hacXa || (bst.endA != (thrPos - Surf(rwyLatA, rwyLonA, rwyAlt)).magnitude < 1);
                    HacApply(bst);
                    if (chg) FlightLogger.Event("Podejście (HAC): nowy wybór – pas kurs " + rwyHeading.ToString("F0", Inv) + "°, okrąg " + (hacSide > 0 ? "w prawo" : "w lewo") + ", wyjście " + (hacXa / 1000).ToString("F0", Inv) + " km przed progiem, tor " + (bst.L / 1000).ToString("F0", Inv) + " km, zapas energii " + bst.exc.ToString("F0", Inv) + " m");
                    thr = HacP(thrPos); dir = HacD(rwyDir);
                    L = HacPath(p, psi, thr, dir, hacSide, Rh, HacRv(v), hacXa, out tb, out C, out arc); cw = hacSide > 0;
                    dist = (p - C).magnitude; exc = bst.exc;
                }
            }
            if (onHac)
            {
                if (!double.IsNaN(hacPrevArc) && arc > hacPrevArc + Math.PI && hacLap > 0) hacLap--;   // minął wyjście – koniec dodatkowego okrążenia
                hacPrevArc = arc;
                Vector2d r = p - C; double ang = Math.Atan2(r.x, r.y);
                double tang = Mod((ang + (cw ? Math.PI / 2 : -Math.PI / 2)) * 180 / Math.PI, 360);
                double corr = Math.Atan(2.0 * (dist - Rh) / Rh) * 180 / Math.PI;
                hdg = Mod(tang + (cw ? corr : -corr), 360);
                if (exc > 2 * Math.PI * Rh / 3.0 && hacLap == 0 && arc < Math.PI) { hacLap = 1; FlightLogger.Event("Podejście (HAC): nadmiar energii " + exc.ToString("F0", Inv) + " m – dodatkowe okrążenie"); }
                approachPhase = "HAC: " + (arc * 180 / Math.PI + 360 * hacLap).ToString("F0", Inv) + "° łuku do wyjścia" + (hacLap > 0 ? " (+okrążenie)" : "");
                if (hacLap == 0 && (arc < 8 * Math.PI / 180 || arc > 352 * Math.PI / 180) && Math.Abs(y) < 1500)
                {
                    hacFinal = true;
                    FlightLogger.Event("Podejście (HAC): wyjście na oś pasa " + (hacXa / 1000).ToString("F0", Inv) + " km przed progiem, h " + h.ToString("F0", Inv) + " m (stroma ścieżka na " + SteepPathHeight(hacXa).ToString("F0", Inv) + " m), EAS " + eas.ToString("F0", Inv) + " m/s");
                }
            }
            else
            {
                hdg = tb; approachPhase = "lot do HAC, tor " + (L / 1000).ToString("F0", Inv) + " km";
                // nadmiar większy, niż zje tor (szybciej + hamulec): esowanie
                double cap = L * (1 / 2.3 - 1 / HacLDP);
                if (exc > cap + 800 || (hacSturn && exc > cap))
                {
                    if (!hacSturn) { hacSturn = true; Vector2d pc = C - p; hacSsgn = W180(Math.Atan2(pc.x, pc.y) * 180 / Math.PI - psi) < 0 ? 1 : -1; }
                    if (W180(psi - tb) * hacSsgn >= 60) hacSsgn = -hacSsgn;
                    hdg = Mod(tb + hacSsgn * 75, 360);
                    approachPhase = "esowanie – nadmiar energii " + exc.ToString("F0", Inv) + " m";
                }
                else hacSturn = false;
            }
            hacOnH = onHac; hacExc = exc;
            lowEnergy = exc < 0;
            double easT = exc < 0 ? 1.4 * vSt : Math.Max(1.4 * vSt, Math.Min(170, 1.4 * vSt + exc / 40));
            brake = exc > 1500;
            vs = -v / Math.Max(2, HacLD(easT, M) * 0.9) - 1.0 * (easT - eas);
            roll = eas < 1.3 * vSt ? 20 : eas < 1.6 * vSt ? 30 : HacBank + 5;
            approachPhase += ", zapas energii " + exc.ToString("F0", Inv) + " m, EAS " + eas.ToString("F0", Inv) + "/" + easT.ToString("F0", Inv) + " m/s";
            return !hacFinal;
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
            if (!hacFinal && hr < 450) { hacFinal = true; FlightLogger.Event("Podejście (HAC): poniżej 450 m nad terenem – końcówka"); }
            double easH = Math.Sqrt(2 * vessel.dynamicPressurekPa * 1000 / 1.225);
            double hacRoll = 45; bool hacBrake = false;
            bool farAway = !hacFinal;
            if (!hacFinal && HacStep(pos, h, easH, y, out hdg, out vs, out hacRoll, out hacBrake))
            {
                excessTurn = false;
            }            else
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
                    double ht = SteepPathHeight(d);
                    vs = -vh * Math.Tan(SteepPathDeg * Math.PI / 180) + 0.08 * (ht - h);
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
                if (farAway) easT = 1.25 * vSt;   // HAC ma własne sterowanie prędkością – tu tylko ochrona przed przeciągnięciem
                if (easNow < easT)
                {
                    vs = Math.Min(vs, -vh * Math.Tan(4 * Math.PI / 180) - 1.5 * (easT - easNow));
                    approachPhase += " (EAS " + easNow.ToString("F0", Inv) + " m/s – za wolno, nos w dół)";
                }
                // Przechylenie zabiera nośność – przy małej prędkości ostrożne zakręty (lot 18:25: zakręt 45° na 17 km
                // przy EAS ~85 m/s skończył się korkociągiem).
                double rollLim = easNow < 1.3 * vSt ? 10 : easNow < 1.6 * vSt ? 20 : easNow < 2.0 * vSt ? 30 : 45;
                if (farAway) rollLim = hacRoll;
                // Wspomaganie silnikami (OK-GLI ma AL-31): za mało energii albo blisko przeciągnięcia wysoko nad ziemią –
                // pełny ciąg; po odzyskaniu prędkości i powrocie na ścieżkę – znów szybowanie jak Buran.
                if (jetsAvail && !flare)
                {
                    bool need = hr > 150 && (lowEnergy || easNow < 1.3 * vSt || StallLevel >= 1);
                    if (need && !jetAssist) { jetAssist = true; JetsOn(); FlightLogger.Event("Podejście: wspomaganie silnikami – " + (lowEnergy ? "poniżej ścieżki" : "mała prędkość") + ", EAS " + easNow.ToString("F0", Inv) + " m/s, h " + h.ToString("F0", Inv) + " m"); }
                    else if (jetAssist && !lowEnergy && easNow > 1.55 * vSt && StallLevel == 0) { jetAssist = false; JetsOff(); FlightLogger.Event("Podejście: silniki wyłączone – z powrotem na ścieżce, EAS " + easNow.ToString("F0", Inv) + " m/s"); }
                    if (jetAssist) { FlightInputHandler.state.mainThrottle = 1f; approachPhase += " + silniki"; }
                }
                else if (jetAssist) { jetAssist = false; JetsOff(); FlightLogger.Event("Podejście: silniki wyłączone przed wyrównaniem"); }
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
                bool wantBrakes = farAway ? hacBrake : easNow > 155 || (flare && easNow > 1.25 * vSt && hr > 15);
                if (wantBrakes != brakesOut) { brakesOut = wantBrakes; vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, wantBrakes); }
            }
            vs = Math.Max(-250, Math.Min(0, vs));
            // statek z silnikami (OK-GLI) wchodzi na ścieżkę z lotu poziomego: zadana prędkość pionowa zmienia się
            // płynnie (≤ 4 m/s na sekundę), żeby autopilot nie pchał gwałtownie nosa w dół. Orbitalny Buran – bez zmian.
            if (jetsAvail)
            {
                double dtA = Math.Max(0.005, TimeWarp.fixedDeltaTime);
                if (double.IsNaN(vsCmdPrev)) vsCmdPrev = vessel.verticalSpeed;
                vs = Math.Max(vs, vsCmdPrev - 4.0 * dtA);
                vsCmdPrev = vs;
            }
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
        private static float AoAForMach(double m) { return VehicleProfile.Current.AoA(m); }   // tabela entryAoA z profilu statku
        private bool ldSeen;

        // Doskonałość Burana w KSP z łatką „BuranWing” – POMIAR z lotu 2026-11-19 21:20 (α wg programu, częściowo
        // zmniejszony przy oszczędzaniu energii): M 23 → 0,7–0,8, M 20–13 → 0,9–1,0, M 11 → 1,15, M 9–4 → 1,45–1,55,
        // M 3–2 → 1,7, M 1 → 1,4. Bieżący pomiar i tak skaluje tablicę.
        // (tabela entryLD w profilu statku – PluginData/Vehicles)
        private static double LdTable(double v) { return VehicleProfile.Current.EntryLD(v); }

        /// <summary>Zasięg lotu ślizgowego w równowadze z L/D zależną od prędkości: ∫ L/D(v) · r/2 · d ln(1/(1 − v²/gr)).</summary>
        // Koniec fazy wejścia jak w Buranie/Shuttle (TAEM): ~800 m/s (M 2,5), ~100 km przed lotniskiem, ~25–30 km wysokości.
        // Lot 2026-11-20 10:11: prowadzenie celowało „dolecieć nad lotnisko z prędkością → 0” w modelu, który zaniża
        // zasięg przy małych prędkościach (poddźwiękowo L/D ~5, a do tego wysokość) – Buran był nad lotniskiem na 46 km
        // z M 4,5 i przeleciał 190 km dalej. Teraz energia jest wytracana do punktu TAEM, resztę robi podejście (krążenie).
        private static double TaemV { get { return Tun.D("taem.v"); } }
        private static double TaemDist { get { return Tun.D("taem.dist"); } }
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

        /// <summary>Schemat 1K1: cel wejścia = punkt styczności ЦРЭ (płaski rzut wokół środka pasa). Na okręgu – kurs styczny.</summary>
        private void S1k1EntryTarget(Vessel v, ref double rgo, ref double brg, ref double taemDist)
        {
            // 1.42.1 (lot 25.11 10:43): kurs liczony w płaskim rzucie wokół pasa był zły z daleka (błąd kursu 59° przy
            // 5700 km zamiast ~9°) → przechylenie 60° na „wyrównanie kursu” zjadło energię. Teraz cel zamieniamy na
            // szer./dł. i kurs oraz odległość liczymy po wielkim kole z miejsca statku.
            // Cel jak dotychczasowy „środek lotniska”, z tym samym TaemDist 100 km (w grze: V ≈ 510 m/s ~12 km przed celem),
            // ale przesunięty: creAim (20 km) PRZED punktem styczności ЦРЭ na stycznej → V ≈ 510 m/s ~12 + 20 = 32 km przed
            // punktem styczności (1K1: 520 ± 60 m/s, 32 ± 13 km). Punkt zamrażamy 400 km przed nim.
            CelestialBody b = v.mainBody;
            double cLat = (rwyLatA + rwyLatB) / 2, cLon = (rwyLonA + rwyLonB) / 2, bb;
            double dd = GreatCircle(cLat, cLon, v.latitude, v.longitude, out bb) * b.Radius;
            Vector2d p = new Vector2d(dd * Math.Sin(bb * Math.PI / 180), dd * Math.Cos(bb * Math.PI / 180));
            Vector2d crs = new Vector2d(Math.Sin(s1k1Course * Math.PI / 180), Math.Cos(s1k1Course * Math.PI / 180));
            Vector2d cre = crs * (-s1k1CreD), kt = crs * (-14500.0);
            Vector2d pc = cre - p; double dc = pc.magnitude;
            if (!creOn && !creDone && dc < s1k1CreR * 1.03) { creOn = true; FlightLogger.Event("Wejście (1K1): na okręgu ЦРЭ, h " + (v.altitude / 1000).ToString("F1", Inv) + " km, V " + v.srfSpeed.ToString("F0", Inv) + " m/s"); }
            if (creOn)
            {
                // zejście z ЦРЭ, gdy energia wystarcza już tylko na dolot do punktu kluczowego (spirala tylko dla nadmiaru)
                double hr = v.altitude - rwyAlt, V = v.srfSpeed;
                double Eh = hr + V * V / (2 * 9.81);
                double ldS = Math.Max(1.0, (ldSeen ? ldF : 1.5) * 0.85);
                double ereq = 4000 + 170.0 * 170.0 / (2 * 9.81) + ((p - kt).magnitude + 8000) / ldS;
                if (Eh < ereq + 2000) { creOn = false; creDone = true; FlightLogger.Event("Wejście (1K1): zejście z ЦРЭ – energia " + (Eh / 1000).ToString("F1", Inv) + " km wystarcza na dolot do punktu kluczowego (" + (ereq / 1000).ToString("F1", Inv) + " km)"); }
            }
            if (creDone)
            {
                Vector2d dk = kt - p; brg = Mod(Math.Atan2(dk.x, dk.y) * 180 / Math.PI, 360); rgo = 0;
            }
            else if (creOn)
            {
                Vector2d rr = p - cre; double a = Math.Atan2(rr.x, rr.y);
                double tang = Mod((a + (s1k1CreSide > 0 ? Math.PI / 2 : -Math.PI / 2)) * 180 / Math.PI, 360);
                double corr = Math.Atan(3.0 * (dc - s1k1CreR) / s1k1CreR) * 180 / Math.PI;
                brg = Mod(tang + (s1k1CreSide > 0 ? corr : -corr), 360); rgo = 0;
            }
            else
            {
                Vector2d aim;
                if (creFrozen) aim = creAimPt;
                else
                {
                    double aa = Math.Asin(Math.Min(1, s1k1CreR / dc)), bc = Math.Atan2(pc.x, pc.y);
                    double tb = s1k1CreSide > 0 ? bc - aa : bc + aa;
                    double L = Math.Sqrt(Math.Max(0, dc * dc - s1k1CreR * s1k1CreR));
                    Vector2d u = new Vector2d(Math.Sin(tb), Math.Cos(tb));
                    aim = p + u * Math.Max(0, L - s1k1Aim);
                    if (L < 400000) { creFrozen = true; creAimPt = aim; creAimDir = u; FlightLogger.Event("Wejście (1K1): cel zamrożony – " + (s1k1Aim / 1000).ToString("F0", Inv) + " km przed punktem styczności ЦРЭ, kurs styczny " + Mod(tb * 180 / Math.PI, 360).ToString("F0", Inv) + "°"); }
                }
                if (creFrozen && Vector2d.Dot(creAimPt - p, creAimDir) < 5000)
                {
                    // przy celu / za nim: dalej po stycznej do ЦРЭ (inaczej kurs na minięty cel zawróciłby Burana)
                    brg = Mod(Math.Atan2(creAimDir.x, creAimDir.y) * 180 / Math.PI, 360);
                    rgo = Math.Max(0, Vector2d.Dot(creAimPt - p, creAimDir));
                    taemDist = s1k1TaemOff;
                    return;
                }
                // rzut odwrotny (azymutalny równoodległościowy) → szer./dł. celu, potem kurs i odległość po wielkim kole
                double dA = aim.magnitude / b.Radius, bA = Math.Atan2(aim.x, aim.y);
                double f1 = cLat * Math.PI / 180, l1 = cLon * Math.PI / 180;
                double fA = Math.Asin(Math.Sin(f1) * Math.Cos(dA) + Math.Cos(f1) * Math.Sin(dA) * Math.Cos(bA));
                double lA = l1 + Math.Atan2(Math.Sin(bA) * Math.Sin(dA) * Math.Cos(f1), Math.Cos(dA) - Math.Sin(f1) * Math.Sin(fA));
                double bA2;
                rgo = GreatCircle(v.latitude, v.longitude, fA * 180 / Math.PI, lA * 180 / Math.PI, out bA2) * b.Radius;
                brg = bA2;
            }
            taemDist = s1k1TaemOff;
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
            double taemDist = TaemDist;
            if (s1k1 && rwySet) S1k1EntryTarget(v, ref rgo, ref brg, ref taemDist);
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
            double rgoEff = Math.Max(0, rgo - taemDist);
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
            if (v.dynamicPressurekPa >= 0.05 && (Math.Abs(dpsi) > headLimit || V < 1500 || (s1k1 && creOn)))
            {
                if (s1k1 && creOn)
                {
                    // na ЦРЭ: stałe przechylenie w stronę obiegu (~30°) + poprawka kursu, do 50° (1K1: do 50° na ЦРЭ)
                    mag = Math.Min(50, Math.Max(0, Math.Abs(dpsi) * 1.5 + ((dpsi > 0) == (s1k1CreSide > 0) ? 30 : 0)));
                    note = "ЦРЭ – rozpraszanie energii";
                }
                else
                {
                    mag = Math.Max(mag, Math.Min(V < 1500 ? 45 : 60, Math.Abs(dpsi) * 1.5));
                    note = note.StartsWith("ZA MAŁO") ? note + ", wyrównuję kurs" : "wyrównuję kurs";
                }
                side = dpsi >= 0 ? 1 : -1;
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
            double vStG = VehicleProfile.Current.StallEas(v.totalMass);
            gEas = easG; gVst = vStG; hudRMax = rMax; hudDpsi = dpsi; hudCos = cosS;
            // tylko przy małym Machu (< ~2,5) – wyżej kąt natarcia trzyma sterowanie, a przechylenie jest potrzebne do
            // wytracania energii i skrętu (lot 10:11: na 46 km przy M 4,5 EAS 53 m/s wyłączył przechylenie nad lotniskiem)
            if (V < 800 && easG < 1.7 * vStG)
            {
                mag = Math.Min(mag, easG < 1.4 * vStG ? 0 : (s1k1 && easG >= 1.5 * vStG) ? 45 : 20);   // 1K1: do 45° (ЦРЭ, R 25 km przy ~500 m/s)
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
