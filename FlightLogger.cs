using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Dziennik lotu (CSV, separator ;) – zapisuje wszystko, co da się odczytać:
    ///   • nagłówek: rakieta, miejsce startu, grupy silników z mnożnikami, paliwem i czasami pracy, ustawienia moda, Δv i TWR,
    ///   • co sekundę: pozycja, prędkości, przyspieszenie i przeciążenie, orbita, orientacja (pochylenie, kurs, kąt natarcia),
    ///     atmosfera (ciśnienia, gęstość, temperatura, Mach), sterowanie (przepustnica, stery, SAS/RCS), ciąg, masa, TWR,
    ///     Δv ze stockowego kalkulatora, zasoby (paliwo, utleniacz, prąd…), 3 najgorętsze części,
    ///     oraz dla KAŻDEJ grupy silników: ciąg, przepustnica, ISP, ile silników pracuje, paliwo i pozostały czas pracy,
    ///   • zdarzenia: staging, zapłon/zgaśnięcie grup, odłączone części (nowe statki), zniszczone części, przegrzanie,
    ///     zmiana sytuacji / ciała niebieskiego, zdarzenia moda (krzyż Korolewa, gorące rozdzielenie).
    /// Plik: GameData/RSSBalancer/PluginData/Logs/ (zapasowo AppData/LocalLow/.../RSSBalancerLogs).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightLogger : MonoBehaviour
    {
        private const double Interval = 1.0;      // s czasu misji między wierszami
        private const double G0 = 9.80665;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static FlightLogger instance;
        public static string CurrentFile { get; private set; }

        private Vessel vessel;
        private StreamWriter writer;
        private double nextRow;
        private string pendingEvents = "";
        private float nextFlush;
        private double maxQ, maxHeat, maxG;
        private Vessel.Situations lastSituation;
        private CelestialBody lastBody;

        private List<EngineGroup> groups = new List<EngineGroup>();
        private readonly Dictionary<EngineGroup, int> lastRunning = new Dictionary<EngineGroup, int>();
        private readonly List<string> resources = new List<string>();
        private readonly HashSet<Part> warnedHot = new HashSet<Part>();

        /// <summary>Foldery dzienników: folder gry, a zapasowo folder danych gry (gdy do folderu gry nie wolno pisać).</summary>
        public static string[] LogFolders
        {
            get
            {
                return new[]
                {
                    Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "RSSBalancer", "PluginData", "Logs"),
                    Path.Combine(Application.persistentDataPath, "RSSBalancerLogs")
                };
            }
        }

        /// <summary>Zdarzenie od innych części moda (np. krzyż Korolewa, gorące rozdzielenie) – do dziennika.</summary>
        public static void Event(string text)
        {
            if (instance != null) instance.AddEvent(text);
        }

        private void Start()
        {
            instance = this;
            BalancerSettings.EnsureLoaded();
            GameEvents.onStageActivate.Add(OnStage);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onVesselCreate.Add(OnVesselCreate);
        }

        private void OnDestroy()
        {
            GameEvents.onStageActivate.Remove(OnStage);
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselCreate.Remove(OnVesselCreate);
            Close("koniec sceny");
            if (instance == this) instance = null;
        }

        // ================================================================== zdarzenia z gry

        private void OnVesselChange(Vessel v)
        {
            if (writer != null && v != vessel) AddEvent("aktywny statek: " + (v != null ? v.vesselName : "?"));
        }

        private void OnStage(int stage)
        {
            AddEvent("STAGE " + stage);
        }

        private void OnVesselCreate(Vessel v)
        {
            if (writer == null || v == null || v == vessel) return;
            // Nowy statek powstały przez odłączenie (np. boczny blok, rdzeń, owiewka) – części mogą dojść klatkę później.
            AddEvent("odłączono: " + v.vesselName + " (" + (v.parts != null ? v.parts.Count : 0) + " części)");
        }

        private void OnPartDie(Part p)
        {
            if (writer == null || p == null || p.vessel != vessel) return;
            AddEvent("ZNISZCZONA część: " + Title(p) + " (temp. " + (HeatRatio(p) * 100).ToString("F0", Inv) + "% limitu, wys. " +
                     (vessel.altitude / 1000.0).ToString("F1", Inv) + " km, q " + vessel.dynamicPressurekPa.ToString("F1", Inv) + " kPa)");
        }

        private void AddEvent(string e)
        {
            if (writer == null) return;
            pendingEvents += (pendingEvents.Length > 0 ? " | " : "") + e.Replace(";", ",").Replace("\n", " ");
        }

        // ================================================================== cykl zapisu

        private void FixedUpdate()
        {
            Vessel v = FlightGlobals.ActiveVessel;

            if (!BalancerSettings.FlightLog)
            {
                Close("wyłączono dziennik");
                return;
            }

            if (writer == null)
            {
                if (v != null && v.situation != Vessel.Situations.PRELAUNCH && v.missionTime < 5.0 &&
                    v.situation != Vessel.Situations.LANDED && v.situation != Vessel.Situations.SPLASHED)
                    Open(v);
                return;
            }

            if (vessel == null) { Close("statek zniknął"); return; }

            WatchChanges(vessel);

            if (vessel.missionTime >= nextRow || pendingEvents.Length > 0)
            {
                nextRow = Math.Floor(vessel.missionTime / Interval) * Interval + Interval;
                WriteRow(vessel);
            }

            if (Time.realtimeSinceStartup > nextFlush)
            {
                nextFlush = Time.realtimeSinceStartup + 5f;
                try { writer.Flush(); } catch { }
            }
        }

        /// <summary>Wykrywa zmiany między wierszami: zapłon/zgaśnięcie grup, sytuacja, ciało niebieskie, przegrzanie.</summary>
        private void WatchChanges(Vessel v)
        {
            if (v.situation != lastSituation)
            {
                AddEvent("sytuacja: " + lastSituation + " → " + v.situation);
                lastSituation = v.situation;
            }
            if (v.mainBody != lastBody)
            {
                AddEvent("strefa wpływu: " + (lastBody != null ? lastBody.bodyName : "?") + " → " + v.mainBody.bodyName);
                lastBody = v.mainBody;
            }

            foreach (EngineGroup g in groups)
            {
                int run = 0, attached = 0;
                foreach (Part p in g.parts)
                {
                    if (p == null || p.vessel != v) continue;
                    attached++;
                    foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                        if (e.EngineIgnited && !e.flameout) { run++; break; }
                }
                int prev;
                lastRunning.TryGetValue(g, out prev);
                if (run != prev)
                {
                    if (prev == 0 && run > 0) AddEvent("zapłon: " + Short(g) + " (" + run + " sil.)");
                    else if (run == 0 && prev > 0) AddEvent((attached > 0 ? "zgaśnięcie: " : "odrzucono: ") + Short(g));
                    else AddEvent(Short(g) + ": pracuje " + run + " z " + g.engines);
                    lastRunning[g] = run;
                }
            }

            foreach (Part p in v.parts)
            {
                double h = HeatRatio(p);
                if (h > 0.8 && !warnedHot.Contains(p))
                {
                    warnedHot.Add(p);
                    AddEvent("PRZEGRZANIE: " + Title(p) + " – " + (h * 100).ToString("F0", Inv) + "% limitu");
                }
            }
        }

        // ================================================================== otwarcie / zamknięcie

        private void Open(Vessel v)
        {
            string name = Safe(v.vesselName) + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv) + ".csv";
            foreach (string dir in LogFolders)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, name);
                    writer = new StreamWriter(path, false, new UTF8Encoding(false));
                    CurrentFile = path;
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RSSBalancer] Dziennik lotu: nie można pisać do " + dir + ": " + e.Message);
                    writer = null;
                }
            }
            if (writer == null) return;

            vessel = v;
            nextRow = 0;
            maxQ = maxHeat = maxG = 0;
            lastSituation = v.situation;
            lastBody = v.mainBody;
            warnedHot.Clear();
            lastRunning.Clear();

            groups = EngineGroups.Build(v.parts, null);
            foreach (EngineGroup g in groups) lastRunning[g] = 0;

            resources.Clear();
            foreach (Part p in v.parts)
                foreach (PartResource r in p.Resources)
                    if (r != null && !resources.Contains(r.resourceName)) resources.Add(r.resourceName);
            resources.Sort(StringComparer.Ordinal);

            WriteHeader(v);
            Debug.Log("[RSSBalancer] Dziennik lotu: " + CurrentFile);
        }

        private void WriteHeader(Vessel v)
        {
            CelestialBody b = v.mainBody;
            writer.WriteLine("# RSSBalancer – dziennik lotu");
            writer.WriteLine("# rakieta: " + v.vesselName + ", części: " + v.parts.Count + ", masa startowa: " + N(v.GetTotalMass(), 2) + " t");
            writer.WriteLine("# start: " + b.bodyName + ", szer. " + N(v.latitude, 3) + "°, dług. " + N(v.longitude, 3) + "°, data: " +
                             DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            double vac, asl;
            writer.WriteLine("# Δv (stock): próżnia " + (StockDeltaV.TotalVac(out vac) ? N(vac, 0) : "?") + " m/s, poziom morza " +
                             (StockDeltaV.TotalAsl(out asl) ? N(asl, 0) : "?") + " m/s");
            writer.WriteLine("# ustawienia moda: krzyż Korolewa " + (BalancerSettings.KorolevCross ? "wł., siła " + N(BalancerSettings.KorolevStrength, 1) : "wył.") +
                             ", gorące rozdzielenie " + (BalancerSettings.HotStaging ? "wł., " + N(BalancerSettings.HotStagingLead, 1) + " s" : "wył.") +
                             ", auto-odrzut rdzenia " + (BalancerSettings.AutoCoreSeparation ? "wł." : "wył.") +
                             ", cele Sojuza: " + N(BalancerSettings.BoosterTime, 0) + "/" + N(BalancerSettings.CoreTime, 0) + "/" +
                             N(BalancerSettings.UpperTime, 0) + " s, TWR " + N(BalancerSettings.LiftoffTwr, 2));
            writer.WriteLine("# grupy silników (odpalenie → odrzut):");
            for (int i = 0; i < groups.Count; i++)
            {
                EngineGroup g = groups[i];
                writer.WriteLine("#   G" + (i + 1) + ": " + g.Label.Replace(";", ",") +
                                 " | ISP× " + N(g.appliedIsp, 2) + ", ciąg× " + N(g.appliedThrust, 2) +
                                 (g.tanks.Count > 0 ? ", masa pustych× " + N(g.appliedDry, 2) + ", paliwo× " + N(g.appliedFuel, 2) : "") +
                                 " | ISP próżnia " + N(g.ispVac, 1) + " s, ciąg (1 atm) " + N(g.thrustAsl, 0) + " kN, paliwo " +
                                 N(g.propMass, 2) + " t, czas pracy " + N(g.BurnTimeNow, 1) + " s");
            }

            StringBuilder h = new StringBuilder();
            h.Append("T+ [s];sytuacja;ciało;szer. [°];dług. [°];wysokość [km];wys. nad terenem [km];")
             .Append("prędk. pionowa [m/s];prędk. pozioma (pow.) [m/s];prędk. powierzchniowa [m/s];prędk. orbitalna [m/s];")
             .Append("przyspieszenie [m/s²];przeciążenie [g];")
             .Append("apocentrum [km];perycentrum [km];do apocentrum [s];do perycentrum [s];inklinacja [°];mimośród;półoś [km];okres [s];")
             .Append("pochylenie [°];kurs [°];kąt toru lotu [°];kąt natarcia [°];prędk. kątowa [°/s];")
             .Append("ciśn. dynamiczne [kPa];ciśn. statyczne [kPa];gęstość [kg/m³];temp. zewn. [K];Mach;")
             .Append("przepustnica [%];ster pochylenia;ster odchylenia;ster przechylenia;SAS;RCS;")
             .Append("ciąg [kN];masa [t];TWR (lokalne);stage;Δv stock próżnia [m/s];Δv stock bieżący stage [m/s];czas bieżącego stage'u [s];");
            foreach (string r in resources) h.Append(r).Append(" [jedn.];");
            for (int i = 0; i < groups.Count; i++)
            {
                string p = "G" + (i + 1) + " ";
                h.Append(p).Append("pracuje [szt.];").Append(p).Append("ciąg [kN];").Append(p).Append("przepustnica [%];")
                 .Append(p).Append("ISP [s];").Append(p).Append("paliwo [t];").Append(p).Append("pozostały czas [s];");
            }
            h.Append("najgorętsza część;temp. [% limitu];2. część;temp. [%];3. część;temp. [%];zdarzenia");
            writer.WriteLine(h.ToString());
        }

        private void Close(string reason)
        {
            if (writer == null) return;
            try
            {
                AddEvent("koniec zapisu (" + reason + ")");
                if (vessel != null) WriteRow(vessel);
                writer.WriteLine("# podsumowanie: maks. ciśnienie dynamiczne " + N(maxQ, 1) + " kPa, maks. przeciążenie " + N(maxG, 2) +
                                 " g, maks. temperatura " + N(maxHeat * 100, 0) + "% limitu");
                writer.Close();
            }
            catch { }
            writer = null;
            vessel = null;
        }

        // ================================================================== wiersz

        private void WriteRow(Vessel v)
        {
            try
            {
                CelestialBody b = v.mainBody;
                Vector3 up = ((Vector3)(v.CoM - b.position)).normalized;
                Vector3 axis = b.transform.up;                                       // oś obrotu planety
                Vector3 north = (axis - Vector3.Dot(axis, up) * up).normalized;
                Vector3 east = Vector3.Cross(up, north);
                Vector3 fwd = v.transform.up;                                        // kierunek „nosa” rakiety
                Vector3 srfVel = (Vector3)v.srf_velocity;

                double pitch = 90.0 - Vector3.Angle(fwd, up);
                double heading = Math.Atan2(Vector3.Dot(fwd, east), Vector3.Dot(fwd, north)) * 180.0 / Math.PI;
                if (heading < 0) heading += 360.0;
                double fpa = v.srfSpeed > 1 ? Math.Asin(Math.Max(-1, Math.Min(1, v.verticalSpeed / v.srfSpeed))) * 180.0 / Math.PI : 90.0;
                double aoa = v.srfSpeed > 1 ? Vector3.Angle(fwd, srfVel) : 0.0;
                double angVel = v.angularVelocity.magnitude * 180.0 / Math.PI;

                double thrust = 0.0;
                foreach (Part p in v.parts)
                    foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                        if (e.EngineIgnited) thrust += e.finalThrust;

                double mass = v.GetTotalMass();
                double r = (v.CoM - b.position).magnitude;
                double gLocal = b.gravParameter / (r * r);
                double twr = mass > 0 ? thrust / (mass * gLocal) : 0;

                maxQ = Math.Max(maxQ, v.dynamicPressurekPa);
                maxG = Math.Max(maxG, v.geeForce);

                FlightCtrlState c = v.ctrlState;
                bool sas = v.ActionGroups[KSPActionGroup.SAS];
                bool rcs = v.ActionGroups[KSPActionGroup.RCS];

                double vac;
                bool hasVac = StockDeltaV.TotalVac(out vac);
                DeltaVStageInfo st = StockDeltaV.Stage(v.currentStage);

                StringBuilder sb = new StringBuilder();
                Col(sb, N(v.missionTime, 1)); Col(sb, v.situation.ToString()); Col(sb, b.bodyName);
                Col(sb, N(v.latitude, 4)); Col(sb, N(v.longitude, 4));
                Col(sb, N(v.altitude / 1000.0, 3)); Col(sb, N(v.heightFromTerrain / 1000.0, 3));
                Col(sb, N(v.verticalSpeed, 1)); Col(sb, N(v.horizontalSrfSpeed, 1)); Col(sb, N(v.srfSpeed, 1)); Col(sb, N(v.obt_speed, 1));
                Col(sb, N(v.acceleration.magnitude, 2)); Col(sb, N(v.geeForce, 2));
                Col(sb, N(v.orbit.ApA / 1000.0, 2)); Col(sb, N(v.orbit.PeA / 1000.0, 2));
                Col(sb, N(v.orbit.timeToAp, 0)); Col(sb, N(v.orbit.timeToPe, 0));
                Col(sb, N(v.orbit.inclination, 3)); Col(sb, N(v.orbit.eccentricity, 4));
                Col(sb, N(v.orbit.semiMajorAxis / 1000.0, 1)); Col(sb, N(v.orbit.period, 0));
                Col(sb, N(pitch, 2)); Col(sb, N(heading, 2)); Col(sb, N(fpa, 2)); Col(sb, N(aoa, 2)); Col(sb, N(angVel, 2));
                Col(sb, N(v.dynamicPressurekPa, 3)); Col(sb, N(v.staticPressurekPa, 4)); Col(sb, N(v.atmDensity, 5));
                Col(sb, N(v.externalTemperature, 1)); Col(sb, N(v.mach, 2));
                Col(sb, N(c != null ? c.mainThrottle * 100.0 : 0, 0));
                Col(sb, N(c != null ? c.pitch : 0, 2)); Col(sb, N(c != null ? c.yaw : 0, 2)); Col(sb, N(c != null ? c.roll : 0, 2));
                Col(sb, sas ? "1" : "0"); Col(sb, rcs ? "1" : "0");
                Col(sb, N(thrust, 1)); Col(sb, N(mass, 3)); Col(sb, N(twr, 3)); Col(sb, v.currentStage.ToString(Inv));
                Col(sb, hasVac ? N(vac, 0) : "");
                Col(sb, st != null ? N(st.deltaVinVac, 0) : ""); Col(sb, st != null ? N(st.stageBurnTime, 1) : "");

                // zasoby
                foreach (string rn in resources)
                {
                    double amount = 0;
                    foreach (Part p in v.parts)
                        foreach (PartResource pr in p.Resources)
                            if (pr != null && pr.resourceName == rn) amount += pr.amount;
                    Col(sb, N(amount, 1));
                }

                // grupy silników
                foreach (EngineGroup g in groups) GroupColumns(sb, g, v);

                // najgorętsze części
                List<KeyValuePair<double, Part>> hot = new List<KeyValuePair<double, Part>>();
                foreach (Part p in v.parts) hot.Add(new KeyValuePair<double, Part>(HeatRatio(p), p));
                hot.Sort((a, c2) => c2.Key.CompareTo(a.Key));
                for (int i = 0; i < 3; i++)
                {
                    if (i < hot.Count) { Col(sb, Title(hot[i].Value).Replace(";", ",")); Col(sb, N(hot[i].Key * 100, 0)); }
                    else { Col(sb, ""); Col(sb, ""); }
                }
                if (hot.Count > 0) maxHeat = Math.Max(maxHeat, hot[0].Key);

                sb.Append(pendingEvents);
                writer.WriteLine(sb.ToString());
                pendingEvents = "";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RSSBalancer] Dziennik lotu: błąd zapisu wiersza: " + e.Message);
            }
        }

        private void GroupColumns(StringBuilder sb, EngineGroup g, Vessel v)
        {
            int run = 0;
            double thrust = 0, flow = 0, throttleSum = 0, ispSum = 0;
            int n = 0;
            List<Part> attached = new List<Part>();
            foreach (Part p in g.parts)
            {
                if (p == null || p.vessel != v) continue;
                attached.Add(p);
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                {
                    if (!e.EngineIgnited || e.flameout) continue;
                    run++;
                    thrust += e.finalThrust;
                    throttleSum += e.currentThrottle;
                    ispSum += e.realIsp;
                    if (e.realIsp > 0) flow += e.finalThrust / (e.realIsp * G0);
                    n++;
                    break;
                }
            }

            double fuel = 0;
            if (attached.Count > 0)
            {
                HashSet<Part> decs = new HashSet<Part>();
                foreach (Part p in attached) decs.Add(EngineGroups.JettisonDecoupler(p));
                fuel = EngineGroups.FuelMass(v.parts, decs, EngineGroups.PropellantNames(attached));
            }

            Col(sb, run.ToString(Inv));
            Col(sb, N(thrust, 1));
            Col(sb, n > 0 ? N(throttleSum / n * 100.0, 0) : "0");
            Col(sb, n > 0 ? N(ispSum / n, 1) : "");
            Col(sb, attached.Count > 0 ? N(fuel, 3) : "");
            Col(sb, flow > 0 ? N(fuel / flow, 1) : "");
        }

        // ================================================================== pomocnicze

        private static double HeatRatio(Part p)
        {
            double h = p.maxTemp > 0 ? p.temperature / p.maxTemp : 0;
            double hs = p.skinMaxTemp > 0 ? p.skinTemperature / p.skinMaxTemp : 0;
            return Math.Max(h, hs);
        }

        private static string Title(Part p)
        {
            return p.partInfo != null ? p.partInfo.title : p.name;
        }

        private string Short(EngineGroup g)
        {
            int i = groups.IndexOf(g);
            return "G" + (i + 1) + " (" + g.engines + "× " + g.title + ")";
        }

        private static void Col(StringBuilder sb, string s) { sb.Append(s).Append(';'); }

        private static string N(double v, int dec)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            return v.ToString("F" + dec, Inv);
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "statek";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
