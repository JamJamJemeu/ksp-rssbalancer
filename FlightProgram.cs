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
        private enum Hold { SurfaceNorth, Orbit, SrfVel }
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
        public string ApproachInfo { get { return approachOn ? approachPhase : ""; } }
        private List<EngineGroup> groups;
        private SoyuzTemplate.Result id;
        private float nextGroups;

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; ReleaseAttitude(); }

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
            if (site != null)
            {
                string[] ll = site.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (ll.Length >= 2) { siteLat = Num(ll[0], 0); siteLon = Num(ll[1], 0); siteSet = true; }
            }
            siteLead = p.node != null ? Num(p.node.GetValue("siteLead"), 3900) : 3900;
            planAngle = p.node != null ? Num(p.node.GetValue("deorbitAngle"), 185) : 185;
            planMaxXr = p.node != null ? Num(p.node.GetValue("maxCrossRange"), 1000) : 1000;
            planUT = double.NaN; nextPlan = 0; PlanInfo = "";
            approachOn = false; rwySet = false;
            string rwy = p.node != null ? p.node.GetValue("runway") : null;
            if (rwy != null)
            {
                string[] r = rwy.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (r.Length >= 5) { rwyLatA = Num(r[0], 0); rwyLonA = Num(r[1], 0); rwyLatB = Num(r[2], 0); rwyLonB = Num(r[3], 0); rwyAlt = Num(r[4], 0); rwySet = true; }
            }
            qMax = 0;
            lastActivation = Planetarium.GetUniversalTime();
            RefreshGroups();
            LastMessage = "Program „" + p.name + "” uzbrojony.";
            FlightLogger.Event("Program lotu: start „" + p.name + "”");
            Debug.Log("[RSSBalancer] Program lotu: start " + p.name);
        }

        public void Stop()
        {
            if (Running != null) FlightLogger.Event("Program lotu: zatrzymany („" + Running.name + "”)");
            ReleaseAttitude();
            StopApproach();
            if (burnOn) { burnOn = false; FlightInputHandler.state.mainThrottle = 0f; }
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

        private void FixedUpdate()
        {
            if (approachOn && vessel != null && vessel == FlightGlobals.ActiveVessel && !Active) Approach();
            if (!Active) return;
            if (vessel == null || vessel != FlightGlobals.ActiveVessel) { LastMessage = "Zmieniono statek – program zatrzymany."; Stop(); return; }

            qMax = Math.Max(qMax, vessel.dynamicPressurekPa);
            // Grupy silników rozpoznajemy RAZ przy starcie programu (na wyrzutni) – po odrzucie bloków
            // ponowne rozpoznanie widzi już tylko rdzeń i nie umie go nazwać „rdzeniem”.
            if ((id == null || !id.ok) && vessel.situation == Vessel.Situations.PRELAUNCH && Time.realtimeSinceStartup > nextGroups)
            { nextGroups = Time.realtimeSinceStartup + 2f; RefreshGroups(); }
            if (holdAttitude) HoldAttitude();
            if (approachOn) Approach();
            if (burnOn) FlightInputHandler.state.mainThrottle = holdAttitude && attError < 5.0 ? 1f : 0f;
            if (!double.IsNaN(planUT) && !burnOn && TimeWarp.CurrentRateIndex > 0)
            {
                double tin = planUT - Planetarium.GetUniversalTime();
                if (tin > 0 && tin < 90 + 3 * TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime) { TimeWarp.SetRate(0, true); LastMessage += "\nZatrzymano przyspieszenie przed impulsem."; }
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

        private double planUT = double.NaN, planXr = double.NaN, nextPlan;
        private double planAngle = 185, planMaxXr = 1000;
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
            if (!locked && Time.realtimeSinceStartup > nextPlan)
            {
                nextPlan = Time.realtimeSinceStartup + 5f;
                PlanDeorbit();
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

        private void PlanDeorbit()
        {
            planUT = double.NaN; planXr = double.NaN;
            if (!siteSet || vessel == null || vessel.orbit == null) { PlanInfo = "brak miejsca lądowania w programie"; return; }
            Orbit o = vessel.orbit; CelestialBody b = vessel.mainBody;
            if (o.eccentricity >= 1 || o.PeA < 0) { PlanInfo = "orbita nie jest zamknięta"; return; }
            double now = Planetarium.GetUniversalTime(), step = 20, horizon = 3 * 86400;
            double tPrev = now + 30, aPrev = AngleAt(o, b, tPrev, false);
            double bestXr = double.MaxValue;
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
                    if (xr <= planMaxXr) { planUT = hi; planXr = xr; break; }
                }
                tPrev = t; aPrev = a;
            }
            if (double.IsNaN(planUT))
                PlanInfo = "brak okna w 3 doby (najmniejsze odejście boczne " + (bestXr < 1e9 ? bestXr.ToString("F0") : "?") + " km > " + planMaxXr.ToString("F0") + ")";
            else
                PlanInfo = "impuls za " + KSPUtil.PrintTimeCompact(planUT - now, false) + ", odejście boczne " + planXr.ToString("F0") + " km";
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
                            float pit = w.Length > 1 ? (float)Num(w[1], 0) : 0f, yaw = w.Length > 2 ? (float)Num(w[2], 0) : 0f, rol = w.Length > 3 ? (float)Num(w[3], 0) : 0f;
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
                    else if (key == "approach")
                    {
                        if (val == "on") StartApproach();
                        else StopApproach();
                    }
                    else if (key == "warp")
                    {
                        if (val == "stop" && TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true);
                    }
                    else if (key == "warpto")
                    {
                        // warpto = deorbit <ile sekund przed impulsem>
                        string[] w = val.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        double lead = w.Length > 1 ? Num(w[1], 300) : 300;
                        double tin = DeorbitIn();
                        if (double.IsNaN(tin)) throw new Exception("brak planu zejścia: " + PlanInfo);
                        if (tin > lead + 5) TimeWarp.fetch.WarpTo(planUT - lead);
                        LastMessage += "\nPlan: " + PlanInfo;
                        FlightLogger.Event("Program lotu: plan zejścia – " + PlanInfo);
                    }
                    else if (key == "sas") vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, val == "on");
                    else if (key == "rcs") vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, val == "on");
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
            approachOn = true; gearDone = false; landedDone = false;
            FlightLogger.Event("Podejście: pas kurs " + rwyHeading.ToString("F0", Inv) + "°, odległość " + (RunwayDistance() / 1000).ToString("F1", Inv) + " km");
        }

        private void StopApproach()
        {
            if (!approachOn) return;
            approachOn = false;
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
                    vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                    foreach (Part p in vessel.parts)
                        foreach (ModuleParachute c in p.FindModulesImplementing<ModuleParachute>())
                            if (c.deploymentState == ModuleParachute.deploymentStates.STOWED) c.Deploy();
                    FlightLogger.Event("Przyziemienie: " + (vessel.srfSpeed * 3.6).ToString("F0", Inv) + " km/h, odchyłka " + y.ToString("F0", Inv) + " m");
                    approachPhase = "dobieg";
                }
                SetF(ap, "HeadingTarget", rwyHeading);
                if (vessel.srfSpeed < 3) { StopApproach(); approachPhase = "stop"; }
                return;
            }

            // kurs: punkt na osi pasa przed nami (marchewka)
            double L = Math.Max(1500, Math.Min(8000, Math.Max(d, 0) * 0.4));
            Vector3d carrot = thrPos + rwyDir * (-(Math.Max(d, 0) - L));
            Vector3d to = carrot - pos;
            to -= up * Vector3d.Dot(to, up);
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            double hdg = (Math.Atan2(Vector3d.Dot(to, east), Vector3d.Dot(to, north)) * 180 / Math.PI + 360) % 360;
            SetF(ap, "HeadingTarget", hdg);

            // prędkość pionowa: ścieżki schodzenia
            double vs;
            if (hr < 20) { vs = -1.0; approachPhase = "przyziemienie"; }
            else if (h < 550)
            {
                double ht = (d + 600) * Math.Tan(1.5 * Math.PI / 180);
                vs = -vh * Math.Tan(1.5 * Math.PI / 180) + 0.15 * (ht - h);
                vs = Math.Max(vs, -25); approachPhase = "wyrównanie (1,5°)";
            }
            else
            {
                double ht = (d - 2000) * Math.Tan(18 * Math.PI / 180);
                vs = -vh * Math.Tan(18 * Math.PI / 180) + 0.08 * (ht - h);
                approachPhase = "ścieżka 18°";
            }
            vs = Math.Max(-150, Math.Min(10, vs));
            SetF(ap, "VertSpeedTarget", vs);

            if (!gearDone && hr < 300)
            {
                gearDone = true;
                vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, true);
                FlightLogger.Event("Podwozie wypuszczone");
            }
            approachPhase += "  d=" + (d / 1000).ToString("F1", Inv) + " km  h=" + h.ToString("F0", Inv) + " m  boczna=" + y.ToString("F0", Inv) + " m";
        }

        private void HoldAttitude()
        {
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
