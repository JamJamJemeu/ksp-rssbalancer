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
            qMax = 0;
            lastActivation = Planetarium.GetUniversalTime();
            RefreshGroups();
            LastMessage = "Program „" + p.name + "” uzbrojony.";
            FlightLogger.Event("Program lotu: start „" + p.name + "”");
        }

        public void Stop()
        {
            if (Running != null) FlightLogger.Event("Program lotu: zatrzymany („" + Running.name + "”)");
            ReleaseAttitude();
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
            if (!Active) return;
            if (vessel == null || vessel != FlightGlobals.ActiveVessel) { LastMessage = "Zmieniono statek – program zatrzymany."; Stop(); return; }

            qMax = Math.Max(qMax, vessel.dynamicPressurekPa);
            // Grupy silników rozpoznajemy RAZ przy starcie programu (na wyrzutni) – po odrzucie bloków
            // ponowne rozpoznanie widzi już tylko rdzeń i nie umie go nazwać „rdzeniem”.
            if ((id == null || !id.ok) && vessel.situation == Vessel.Situations.PRELAUNCH && Time.realtimeSinceStartup > nextGroups)
            { nextGroups = Time.realtimeSinceStartup + 2f; RefreshGroups(); }
            if (holdAttitude) HoldAttitude();
            if (burnOn) FlightInputHandler.state.mainThrottle = holdAttitude && attError < 5.0 ? 1f : 0f;

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
                case "siteangle": return SiteAngle(false);
                case "sitexrange": return SiteAngle(true);
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
            Vector3d site = b.GetWorldSurfacePosition(siteLat, siteLon, 0) - b.position;
            double deg = b.rotates && b.rotationPeriod > 0 ? 360.0 * siteLead / b.rotationPeriod : 0;
            Vector3d axis = b.angularVelocity.normalized;
            if (axis.sqrMagnitude > 0.5) site = (Vector3d)(Quaternion.AngleAxis((float)deg, (Vector3)axis) * (Vector3)site);
            Vector3d r = vessel.CoMD - b.position;
            Vector3d h = Vector3d.Cross(r, vessel.obt_velocity).normalized;
            double off = Vector3d.Dot(site.normalized, h);
            if (crossRange) return Math.Abs(Math.Asin(Math.Max(-1, Math.Min(1, off)))) * b.Radius / 1000.0;
            Vector3d sp = site - h * Vector3d.Dot(site, h);
            double ang = Math.Atan2(Vector3d.Dot(Vector3d.Cross(r, sp), h), Vector3d.Dot(r, sp)) * 180.0 / Math.PI;
            return ang < 0 ? ang + 360 : ang;
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
                    else if (key == "warp")
                    {
                        if (val == "stop" && TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true);
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
