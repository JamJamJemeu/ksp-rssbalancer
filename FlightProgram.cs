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
        public string ApproachInfo { get { return approachOn ? approachPhase : patternOn ? patternPhase : ""; } }
        private List<EngineGroup> groups;
        private SoyuzTemplate.Result id;
        private float nextGroups;

        private void Awake() { Instance = this; }
        private void OnDestroy() { if (Instance == this) Instance = null; ReleaseAttitude(); if (steerVessel != null) steerVessel.OnFlyByWire -= SteerFbw; }

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
            if (p.node != null && p.node.HasValue("daylight")) RequireDaylight = p.node.GetValue("daylight").Trim().ToLowerInvariant() == "true";
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
                    double lead = !double.IsNaN(din) && op.StartsWith("<") ? Math.Max(din, 60) : 240;
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

        private double planUT = double.NaN, planXr = double.NaN, nextPlan, planSun;
        private double planAngle = 185, planMaxXr = 1000;
        public bool RequireDaylight;
        public void Replan() { planUT = double.NaN; nextPlan = 0; PlanInfo = "przeliczam…"; }

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
            approachOn = true; gearDone = false; landedDone = false;
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
                double vr = 1.35 * vSt;                     // prędkość oderwania
                HookSteer();
                steerHdg = CenterlineHeading(pos, up);
                SetF(ap, "HeadingTarget", steerHdg);
                SetF(ap, "VertSpeedTarget", easNow > vr ? 8.0 : 0.0);
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
            if (StallLevel >= 1) { vs = Math.Min(vs, -10); hdg = HeadingOf(vessel.srf_velocity, up, north, east); }
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
            s.wheelSteer = Mathf.Clamp((float)(-err / 12.0), -1f, 1f);     // A (+1) = w lewo
        }

        /// <summary>Kurs na punkt na osi pasa ~400 m przed nosem (koryguje też zjechanie w bok).</summary>
        private double CenterlineHeading(Vector3d pos, Vector3d up)
        {
            Vector3d rel = pos - thrPos;
            double along = Vector3d.Dot(rel, rwyDir);
            Vector3d aim = thrPos + rwyDir * (along + 400);
            Vector3d to = aim - pos; to -= up * Vector3d.Dot(to, up);
            Vector3d north = Vector3d.Exclude(up, vessel.mainBody.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            return HeadingOf(to, up, north, east);
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
        private double nextALog;
        private bool excessTurn, brakesOut;

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
                    vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                    foreach (Part p in vessel.parts)
                        foreach (ModuleParachute c in p.FindModulesImplementing<ModuleParachute>())
                            if (c.deploymentState == ModuleParachute.deploymentStates.STOWED) c.Deploy();
                    FlightLogger.Event("Przyziemienie: " + (vessel.srfSpeed * 3.6).ToString("F0", Inv) + " km/h, odchyłka " + y.ToString("F0", Inv) + " m");
                    approachPhase = "dobieg";
                }
                HookSteer();
                steerHdg = CenterlineHeading(pos, up);
                SetF(ap, "HeadingTarget", steerHdg);
                if (vessel.srfSpeed < 3) { steerHdg = double.NaN; StopApproach(); approachPhase = "stop"; }
                return;
            }

            Vector3d relH = rel - up * Vector3d.Dot(rel, up);
            double dThr = relH.magnitude;                                         // do progu po ziemi (prosto)
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized;
            double vs, hdg;
            bool farAway = dThr > 30000 || Math.Abs(y) > 8000 || d < 3000;

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
                // NADMIAR ENERGII (jest wyżej, niż trzeba): krąży wokół punktu dolotu jak Buran/Shuttle na okręgu HAC,
                // aż kąt potrzebny do dolotu spadnie; do tego hamulec aerodynamiczny (ster kierunku), gdy za szybko.
                if (gNeed > 14 || (excessTurn && gNeed > 10))
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
                Vector3d carrot = thrPos + rwyDir * (-(Math.Max(d, 0) - L));
                Vector3d to = carrot - pos;
                to -= up * Vector3d.Dot(to, up);
                hdg = (Math.Atan2(Vector3d.Dot(to, east), Vector3d.Dot(to, north)) * 180 / Math.PI + 360) % 360;

                if (hr < 20) { vs = -1.0; approachPhase = "przyziemienie"; }
                else if (h < 550 && d < 6000)
                {
                    double ht = (d + 600) * Math.Tan(1.5 * Math.PI / 180);
                    vs = -vh * Math.Tan(1.5 * Math.PI / 180) + 0.15 * (ht - h);
                    vs = Math.Max(vs, -25); approachPhase = "wyrównanie (1,5°)";
                }
                else
                {
                    // stroma ścieżka jak w Buranie: 17–22° (tu 19°), punkt kluczowy ~12,5 km przed pasem na ~4 km
                    double ht = (d - 900) * Math.Tan(19 * Math.PI / 180);
                    vs = -vh * Math.Tan(19 * Math.PI / 180) + 0.08 * (ht - h);
                    approachPhase = "stroma ścieżka 19°";
                    double vsMin = -vh * Math.Tan(10 * Math.PI / 180);
                    if (vs > vsMin) { vs = vsMin; approachPhase = "PONIŻEJ ścieżki – najlepszy lot ślizgowy"; }
                }
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
                double easT = flare ? 1.15 * vSt : farAway ? Math.Max(150, 1.8 * vSt) : Math.Max(140, 1.6 * vSt);
                if (easNow < easT)
                {
                    vs = Math.Min(vs, -vh * Math.Tan(4 * Math.PI / 180) - 1.5 * (easT - easNow));
                    approachPhase += " (EAS " + easNow.ToString("F0", Inv) + " m/s – za wolno, nos w dół)";
                }
                // Przechylenie zabiera nośność – przy małej prędkości ostrożne zakręty (lot 18:25: zakręt 45° na 17 km
                // przy EAS ~85 m/s skończył się korkociągiem).
                double rollLim = easNow < 1.3 * vSt ? 10 : easNow < 1.6 * vSt ? 20 : easNow < 2.0 * vSt ? 30 : 45;
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
                bool wantBrakes = easNow > (farAway ? 200 : 155) || (excessTurn && easNow > 170);
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
        private double bankCmd, rollSign = 1, ldF = 1.0, calStart = -1, calHdg0, calBank, nextGLog;
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
                ldF += (Math.Max(0.2, Math.Min(6, cl / cd)) - ldF) * 0.05;
            else if (double.IsNaN(cl) && v.dynamicPressurekPa > 0.3)
            {
                // bez FAR: L/D z przyspieszenia aerodynamicznego (przyspieszenie − grawitacja), rozłożonego na oś prędkości
                Vector3d aAero = v.acceleration - FlightGlobals.getGeeForceAtPosition(v.CoMD);
                Vector3d vn = vel / V;
                double drag = -Vector3d.Dot(aAero, vn);
                double lift = (aAero + vn * drag).magnitude;
                if (drag > 0.5) ldF += (Math.Max(0.2, Math.Min(6, lift / drag)) - ldF) * 0.02;
            }
            double r = pos.magnitude, g = b.gravParameter / (r * r);
            double e = Math.Min(0.999, V * V / (g * r));
            double kR = r / 2 * Math.Log(1 / (1 - e));                              // zasięg przy L/D = 1, σ = 0
            double rMax = ldF * kR;
            double cosS = rMax > 1 ? rgo / rMax : 1;

            double mag;
            string note = "";
            if (v.dynamicPressurekPa < 0.5) { mag = 0; note = "za rzadkie powietrze"; }
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
            if (v.dynamicPressurekPa >= 0.5 && (Math.Abs(dpsi) > headLimit || V < 1500))
            {
                mag = Math.Max(mag, Math.Min(V < 1500 ? 45 : 60, Math.Abs(dpsi) * 1.5));
                side = dpsi >= 0 ? 1 : -1;
                note = note.StartsWith("ZA MAŁO") ? note + ", wyrównuję kurs" : "wyrównuję kurs";
            }
            // Za mało energii przy dużej prędkości: zmniejsz kąt natarcia (większa doskonałość) – do 12° mniej, min. 22°.
            aoaBias = 0;
            if (cosS > 1 && V > 1500 && v.dynamicPressurekPa >= 0.5)
                aoaBias = (float)Math.Min(18, (cosS - 1) * 40);
            // Duży nadmiar energii (lot 2026-11-19 18:25: Buran przeleciał nad lotniskiem z prędkością naddźwiękową
            // mimo przechylenia 70°): większy kąt natarcia = większy opór, do +8° (maks. 45°).
            else if (cosS < 0.6 && V > 1500 && v.dynamicPressurekPa >= 0.5)
                aoaBias = -(float)Math.Min(8, (0.6 - cosS) * 25);
            // Mała prędkość równoważna nisko: przechylenie zabiera nośność – ograniczamy je, żeby nie przeciągnąć
            double easG = Math.Sqrt(2 * v.dynamicPressurekPa * 1000 / 1.225);
            double vStG = 70 * Math.Sqrt(Math.Max(10, v.totalMass) / 75.0);
            if (V < 2000 && easG < 1.7 * vStG)
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
