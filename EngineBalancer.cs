using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Okno pod F8 (VAB/SPH i lot), zakładki:
    ///   Statek  – ISP × i Ciąg × dla całego statku, dopasowanie całkowitej Δv.
    ///   Grupy   – osobne mnożniki dla każdej grupy silników (odpalenie → odrzut), czas pracy przed/po, dopasowanie czasu.
    ///   Sojuz   – automat: czasy pracy bocznych bloków / rdzenia / III stopnia + TWR na starcie, jednym kliknięciem.
    ///   Misja   – ile Δv trzeba na orbitę, do księżyca, na planetę (liczone z planet w grze).
    ///   Presety – zapis/wczytanie ustawień wszystkich grup pod nazwą rakiety.
    /// Wartości trafiają do ModuleEngineBalancer w każdym silniku, więc zapisują się w save i w pliku statku.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndEditor, false)]
    public class EngineBalancer : MonoBehaviour
    {
        private const int WindowId = 0x52535342;
        private const string LockId = "RSSBalancer_WindowLock";
        private static readonly string[] Tabs = { "Statek", "Grupy", "Sojuz", "Misja", "Presety" };

        private static bool show = false;
        private static int tab = 0;
        private static Rect window = new Rect(300f, 150f, 420f, 200f);

        // --- Statek ---
        private static float ispMult = 1f;
        private static float thrustMult = 1f;
        private string totalTarget = "";

        // --- Grupy ---
        private List<EngineGroup> groups = new List<EngineGroup>();
        private float nextRefresh;
        private Vector2 groupScroll;

        // --- Sojuz ---
        private string sBooster, sCore, sTwr, sUpper, sLead;

        // --- Misja ---
        private int targetIdx = -1;     // -1 = tylko orbita
        private int missionMode = 1;    // 0 orbita celu, 1 lądowanie, 2 lądowanie i powrót
        private string altKm = "";
        private string latDeg = "";
        private CelestialBody lastLaunchBody;

        // --- Presety ---
        private string presetName = "";
        private Vector2 presetScroll;
        private Preset pendingDelete;

        private string status;

        // ================================================================== cykl życia

        private void Start()
        {
            Hud.Hook();
            PresetStore.EnsureLoaded();
            BalancerSettings.EnsureLoaded();
            LoadSoyuzFields();
        }

        private void LoadSoyuzFields()
        {
            sBooster = BalancerSettings.BoosterTime.ToString(CultureInfo.InvariantCulture);
            sCore = BalancerSettings.CoreTime.ToString(CultureInfo.InvariantCulture);
            sTwr = BalancerSettings.LiftoffTwr.ToString("F2", CultureInfo.InvariantCulture);
            sUpper = BalancerSettings.UpperTime.ToString(CultureInfo.InvariantCulture);
            sLead = BalancerSettings.HotStagingLead.ToString("F1", CultureInfo.InvariantCulture);
        }

        private void OnDestroy()
        {
            InputLockManager.RemoveControlLock(LockId);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                show = !show;
                if (show)
                {
                    if (string.IsNullOrEmpty(presetName)) presetName = CurrentShipName();
                    Refresh();
                }
                else
                {
                    InputLockManager.RemoveControlLock(LockId);
                }
            }

            if (show && Time.realtimeSinceStartup > nextRefresh)
            {
                nextRefresh = Time.realtimeSinceStartup + 1f;
                Refresh();
            }
        }

        private void OnGUI()
        {
            if (!show || Hud.Hidden) return;

            window = GUILayout.Window(WindowId, window, DrawWindow, "Engine Balancer");

            // Kursor nad oknem = gra nie reaguje na klawisze (wpisywanie liczb i nazw nie odpala stage'ów itd.)
            if (window.Contains(Event.current.mousePosition))
                InputLockManager.SetControlLock(ControlTypes.All, LockId);
            else
                InputLockManager.RemoveControlLock(LockId);
        }

        private void DrawWindow(int id)
        {
            int newTab = GUILayout.Toolbar(tab, Tabs);
            if (newTab != tab)
            {
                tab = newTab;
                status = null;
                pendingDelete = null;
                window.height = 100f; // okno samo dopasuje wysokość do zawartości zakładki
            }
            GUILayout.Space(6f);

            switch (tab)
            {
                case 0: DrawShipTab(); break;
                case 1: DrawGroupsTab(); break;
                case 2: DrawSoyuzTab(); break;
                case 3: DrawMissionTab(); break;
                case 4: DrawPresetsTab(); break;
            }

            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(4f);
                GUILayout.Label(status);
            }

            GUI.DragWindow();
        }

        // ================================================================== zakładka: Statek

        private void DrawShipTab()
        {
            GUILayout.Label("Ustawia wszystkie silniki statku naraz.");

            GUILayout.Label("ISP ×: " + ispMult.ToString("F2"));
            ispMult = Slider(ispMult, ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult);
            GUILayout.Space(4f);

            GUILayout.Label("Ciąg ×: " + thrustMult.ToString("F2"));
            thrustMult = Slider(thrustMult, ModuleEngineBalancer.MinThrustMult, ModuleEngineBalancer.MaxThrustMult);
            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply"))
            {
                SetAll(ispMult, thrustMult);
                ApplyGroups(false);
                status = "Zastosowano do całego statku.";
            }
            if (GUILayout.Button("Reset"))
            {
                ispMult = 1f;
                thrustMult = 1f;
                SetAll(1f, 1f);
                ApplyGroups(true);
                status = "Przywrócono oryginalne wartości.";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            double vac, asl;
            bool hasVac = StockDeltaV.TotalVac(out vac);
            bool hasAsl = StockDeltaV.TotalAsl(out asl);
            GUILayout.Label("Δv statku: " + (hasVac ? Ms(vac) : "–") + " (próżnia), " + (hasAsl ? Ms(asl) : "–") + " (poziom morza)");
            GUILayout.Label("TWR na starcie: " + Twr(CurrentTwr()));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Dopasuj Δv (próżnia) do:", GUILayout.Width(170f));
            totalTarget = GUILayout.TextField(totalTarget ?? "", 8, GUILayout.Width(80f));
            GUILayout.Label("m/s", GUILayout.Width(30f));
            if (GUILayout.Button("Dopasuj"))
            {
                double t;
                if (TryNum(totalTarget, out t) && t > 0) FitTotal(t);
                else status = "Wpisz docelową Δv w m/s.";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Mnoży ISP wszystkich grup o ten sam współczynnik – dokładne, bo Δv rośnie razem z ISP.");

            GUILayout.Space(10f);
            bool tm = GUILayout.Toggle(BalancerSettings.Telemetry, " Ekran telemetrii w locie (parametry, stopnie, wykres trajektorii, porównanie z poprzednim lotem)");
            if (tm != BalancerSettings.Telemetry) { BalancerSettings.Telemetry = tm; BalancerSettings.Save(); }
            if (BalancerSettings.Telemetry)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(20f);
                bool ru = GUILayout.Toggle(BalancerSettings.TelemetryRussian, " opisy po rosyjsku (jak w ЦУП)");
                if (ru != BalancerSettings.TelemetryRussian) { BalancerSettings.TelemetryRussian = ru; BalancerSettings.Save(); }
                bool f2 = GUILayout.Toggle(BalancerSettings.TelemetryOnF2, " zostaje po F2 (ukryty interfejs)");
                if (f2 != BalancerSettings.TelemetryOnF2) { BalancerSettings.TelemetryOnF2 = f2; BalancerSettings.Save(); }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(20f);
                GUILayout.Label("Skala ekranu telemetrii: ×" + BalancerSettings.TelemetryScale.ToString("F2"), GUILayout.Width(200f));
                float ts = GUILayout.HorizontalSlider(BalancerSettings.TelemetryScale, 0.5f, 1.6f);
                ts = Mathf.Round(ts * 20f) / 20f;
                if (Math.Abs(ts - BalancerSettings.TelemetryScale) > 0.001f) { BalancerSettings.TelemetryScale = ts; BalancerSettings.Save(); }
                if (GUILayout.Button("1:1", GUILayout.Width(40f))) { BalancerSettings.TelemetryScale = 1f; BalancerSettings.Save(); }
                GUILayout.EndHorizontal();
            }
            bool fl = GUILayout.Toggle(BalancerSettings.FlightLog, " Zapisuj dziennik lotu (CSV, co sekundę: wysokość, prędkości, apocentrum, pochylenie, ciśnienie dynamiczne, temperatury, zdarzenia)");
            if (fl != BalancerSettings.FlightLog) { BalancerSettings.FlightLog = fl; BalancerSettings.Save(); }
            if (BalancerSettings.FlightLog)
                GUILayout.Label(string.IsNullOrEmpty(FlightLogger.CurrentFile)
                    ? "Dziennik zacznie się przy starcie. Pliki: GameData\\RSSBalancer\\PluginData\\Logs\\"
                    : "Ostatni dziennik: " + FlightLogger.CurrentFile);
        }

        // ================================================================== zakładka: Grupy

        private void DrawGroupsTab()
        {
            if (groups.Count == 0)
            {
                GUILayout.Label("Brak silników z RSSBalancer na tym statku.");
                return;
            }

            GUILayout.Label("Grupa = silniki odpalane i odrzucane w tych samych stage'ach (np. boczne bloki osobno od rdzenia).");
            GUILayout.Label("TWR na starcie: " + Twr(CurrentTwr()) + PreviewTwrSuffix());

            groupScroll = GUILayout.BeginScrollView(groupScroll, GUILayout.Height(Mathf.Min(430f, 190f * groups.Count)));
            foreach (EngineGroup g in groups)
            {
                GUILayout.BeginVertical("box");
                GUILayout.Label(g.Label + (g.mixed ? "  (różne ustawienia w grupie)" : ""));

                GUILayout.Label("ISP ×: " + g.isp.ToString("F2") + Was(g.isp, g.appliedIsp));
                g.isp = Slider(g.isp, ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult);
                GUILayout.Label("Ciąg ×: " + g.thrust.ToString("F2") + Was(g.thrust, g.appliedThrust));
                g.thrust = Slider(g.thrust, ModuleEngineBalancer.MinThrustMult, ModuleEngineBalancer.MaxThrustMult);

                if (g.tanks.Count > 0 && HighLogic.LoadedSceneIsEditor)
                {
                    GUILayout.Label("Masa pustych zbiorników ×: " + g.dry.ToString("F2") + Was(g.dry, g.appliedDry) + "   (zbiorniki: " + g.tanks.Count + ")");
                    g.dry = Slider(g.dry, ModuleTankBalancer.MinDry, ModuleTankBalancer.MaxDry);
                    GUILayout.Label("Paliwo ×: " + g.fuel.ToString("F2") + Was(g.fuel, g.appliedFuel));
                    g.fuel = Slider(g.fuel, ModuleTankBalancer.MinFuel, ModuleTankBalancer.MaxFuel);
                }

                double tNow = g.BurnTimeNow;
                if (tNow > 0)
                {
                    GUILayout.Label(g.Changed
                        ? "Czas pracy: " + Sec(tNow) + " → " + Sec(g.BurnTimeAfter(tNow))
                        : "Czas pracy: " + Sec(tNow));

                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Dopasuj czas pracy do:", GUILayout.Width(150f));
                    g.targetTime = GUILayout.TextField(g.targetTime ?? "", 6, GUILayout.Width(60f));
                    GUILayout.Label("s", GUILayout.Width(15f));
                    if (GUILayout.Button("Ustaw (ISP)"))
                    {
                        double t;
                        if (TryNum(g.targetTime, out t) && t > 0)
                        {
                            // Przy obecnym suwaku ciągu: czas ∝ ISP / ciąg
                            double isp = g.appliedIsp * (t / tNow) * (g.thrust / g.appliedThrust);
                            g.isp = Round2(Mathf.Clamp((float)isp, ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult));
                            status = "ISP × " + g.isp.ToString("F2") + " – kliknij „Zastosuj grupy”.";
                        }
                        else status = "Wpisz docelowy czas pracy w sekundach.";
                    }
                    GUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.Label("Czas pracy: brak danych (brak paliwa albo silnik wyłączony).");
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Zastosuj grupy"))
            {
                ApplyGroups(false);
                status = "Zastosowano ustawienia grup.";
            }
            if (GUILayout.Button("Cofnij zmiany"))
            {
                foreach (EngineGroup g in groups) { g.isp = g.appliedIsp; g.thrust = g.appliedThrust; }
                status = null;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Czas pracy rośnie z ISP i paliwem, maleje z ciągiem (czas = paliwo × ISP × g ÷ ciąg). Lżejsze zbiorniki dają więcej Δv bez zmiany czasów pracy.");
        }

        private string PreviewTwrSuffix()
        {
            double twr0 = CurrentTwr();
            if (twr0 <= 0 || groups.Count == 0) return "";

            int launch = int.MinValue;
            foreach (EngineGroup g in groups) launch = Math.Max(launch, g.ignition);

            double f0 = 0, f1 = 0;
            bool changed = false;
            foreach (EngineGroup g in groups)
            {
                if (g.ignition != launch) continue;
                f0 += g.thrustAsl;
                f1 += g.thrustAsl * g.thrust / g.appliedThrust;
                if (!Mathf.Approximately(g.thrust, g.appliedThrust)) changed = true;
            }
            return changed && f0 > 0 ? " → " + Twr(twr0 * f1 / f0) : "";
        }

        // ================================================================== zakładka: Sojuz

        private void DrawSoyuzTab()
        {
            GUILayout.Label("Wzorzec R-7 / Sojuz: wspólne ISP dla bocznych bloków i rdzenia (jak RD-107A/RD-108A), osobny ciąg dla każdego – tak, by czasy pracy i TWR zgadzały się z oryginałem.");

            SoyuzTemplate.Result id = SoyuzTemplate.Identify(groups);
            GUILayout.BeginVertical("box");
            if (id.ok)
            {
                GUILayout.Label("Boczne bloki:  " + id.boosters.Label + "  ·  teraz " + Sec(id.boosters.BurnTimeNow) + ", ISP " + Sec(id.boosters.ispVac) + " (oryg. ≈320 s)");
                GUILayout.Label("Rdzeń:  " + id.core.Label + "  ·  teraz " + Sec(id.core.BurnTimeNow) + ", ISP " + Sec(id.core.ispVac) + " (oryg. ≈320 s)");
                GUILayout.Label(id.upper != null
                    ? "III stopień:  " + id.upper.Label + "  ·  teraz " + Sec(id.upper.BurnTimeNow) + ", ISP " + Sec(id.upper.ispVac) + " (oryg. 326–359 s)"
                    : "III stopień: nie znaleziono");
                GUILayout.Label("TWR na starcie: teraz " + Twr(CurrentTwr()));
            }
            else
            {
                GUILayout.Label(id.message);
            }
            GUILayout.EndVertical();

            GUILayout.Space(4f);
            Field("Czas pracy bocznych bloków:", ref sBooster, "s");
            Field("Czas pracy rdzenia (od startu):", ref sCore, "s");
            Field("TWR na starcie:", ref sTwr, "");
            GUILayout.BeginHorizontal();
            BalancerSettings.DoUpper = GUILayout.Toggle(BalancerSettings.DoUpper, " III stopień, czas pracy:", GUILayout.Width(230f));
            sUpper = GUILayout.TextField(sUpper ?? "", 6, GUILayout.Width(60f));
            GUILayout.Label("s");
            GUILayout.EndHorizontal();
            GUILayout.Label("Sojuz-2: bloki 118 s, rdzeń 286 s, III stopień 270 s (2.1b) / 250 s (2.1a), TWR ≈ 1,35.");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Wartości Sojuza-2"))
            {
                BalancerSettings.BoosterTime = SoyuzTemplate.BoosterTime;
                BalancerSettings.CoreTime = SoyuzTemplate.CoreTime;
                BalancerSettings.LiftoffTwr = SoyuzTemplate.LiftoffTwr;
                BalancerSettings.UpperTime = SoyuzTemplate.UpperTime;
                BalancerSettings.Save();
                LoadSoyuzFields();
            }
            GUI.enabled = id.ok;
            if (GUILayout.Button("Oblicz i zastosuj"))
                RunSoyuzSolve();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (HighLogic.LoadedSceneIsEditor && id.ok) DrawSoyuzTanks(id);
            DrawFlightOptions();
        }

        private static float sDry = 1f, sUpperFuel = 1f;
        private static bool sTanksInit;

        private void DrawSoyuzTanks(SoyuzTemplate.Result id)
        {
            if (!sTanksInit)
            {
                sTanksInit = true;
                if (id.core.tanks.Count > 0) sDry = id.core.appliedDry;
                if (id.upper != null && id.upper.tanks.Count > 0) sUpperFuel = id.upper.appliedFuel;
            }

            GUILayout.Space(8f);
            GUILayout.BeginVertical("box");
            GUILayout.Label("Zbiorniki – więcej Δv bez zmiany czasów pracy");

            GUILayout.Label("Masa pustych zbiorników × (bloki, rdzeń, III stopień): " + sDry.ToString("F2"));
            sDry = Slider(sDry, ModuleTankBalancer.MinDry, ModuleTankBalancer.MaxDry);

            if (id.upper != null && id.upper.tanks.Count > 0)
            {
                GUILayout.Label("Paliwo III stopnia ×: " + sUpperFuel.ToString("F2") + "   (Blok I Sojuza-2 ma ok. 25 t paliwa; teraz " +
                                (id.upper.propMass * sUpperFuel / Math.Max(0.01f, id.upper.appliedFuel)).ToString("F1") + " t)");
                sUpperFuel = Slider(sUpperFuel, ModuleTankBalancer.MinFuel, ModuleTankBalancer.MaxFuel);
            }

            double vac;
            GUILayout.Label("Δv statku (próżnia): " + (StockDeltaV.TotalVac(out vac) ? Ms(vac) : "–") + "  – porównaj z zakładką Misja.");

            if (GUILayout.Button("Zastosuj zbiorniki i przelicz Sojuza"))
            {
                foreach (EngineGroup g in new[] { id.boosters, id.core, id.upper })
                    if (g != null && g.tanks.Count > 0) g.dry = sDry;
                if (id.upper != null && id.upper.tanks.Count > 0) id.upper.fuel = sUpperFuel;

                ApplyGroups(false);
                Refresh();
                RunSoyuzSolve();
            }
            GUILayout.Label("Po zmianie zbiorników mod od razu przelicza ISP i ciąg, żeby czasy pracy i TWR zostały jak w Sojuzie.");
            GUILayout.EndVertical();
        }

        private void RunSoyuzSolve()
        {
            double tb, tc, twr, tu;
            if (!TryNum(sBooster, out tb) || !TryNum(sCore, out tc) || !TryNum(sTwr, out twr) || tb <= 0 || tc <= 0 || twr <= 0)
            {
                status = "Wpisz poprawne czasy pracy i TWR.";
                return;
            }
            if (tc <= tb)
            {
                status = "Rdzeń musi pracować dłużej niż boczne bloki.";
                return;
            }
            if (!TryNum(sUpper, out tu) || tu <= 0) tu = SoyuzTemplate.UpperTime;

            BalancerSettings.BoosterTime = tb;
            BalancerSettings.CoreTime = tc;
            BalancerSettings.LiftoffTwr = twr;
            BalancerSettings.UpperTime = tu;
            BalancerSettings.Save();

            Refresh();
            SoyuzTemplate.Result r = SoyuzTemplate.Solve(groups, CurrentTwr(), tb, tc, twr, BalancerSettings.DoUpper, tu);
            if (r.ok) ApplyGroups(false);
            status = r.message + (r.ok ? "\nZastosowano. Liczby w stockowym oknie Delta-V odświeżą się za chwilę." : "");
        }

        private void DrawFlightOptions()
        {
            GUILayout.Space(10f);
            GUILayout.BeginVertical("box");
            GUILayout.Label("W locie (działa automatycznie, gdy mod rozpozna układ Sojuza)");

            bool changed = false;

            bool kc = GUILayout.Toggle(BalancerSettings.KorolevCross, " Krzyż Korolewa – bloki odchylają się ogonem i odpadają na boki");
            if (kc != BalancerSettings.KorolevCross) { BalancerSettings.KorolevCross = kc; changed = true; }

            if (BalancerSettings.KorolevCross)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("   Siła: " + BalancerSettings.KorolevStrength.ToString("F1"), GUILayout.Width(90f));
                float ks = Mathf.Round(GUILayout.HorizontalSlider(BalancerSettings.KorolevStrength, 0f, 3f) * 10f) / 10f;
                if (!Mathf.Approximately(ks, BalancerSettings.KorolevStrength)) { BalancerSettings.KorolevStrength = ks; changed = true; }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            bool hs = GUILayout.Toggle(BalancerSettings.HotStaging, " Gorące rozdzielenie – zapłon III stopnia", GUILayout.Width(280f));
            if (hs != BalancerSettings.HotStaging) { BalancerSettings.HotStaging = hs; changed = true; }
            sLead = GUILayout.TextField(sLead ?? "", 4, GUILayout.Width(40f));
            GUILayout.Label("s przed końcem");
            GUILayout.EndHorizontal();
            double lead;
            if (TryNum(sLead, out lead) && lead > 0 && lead <= 10 && !Mathf.Approximately((float)lead, BalancerSettings.HotStagingLead))
            {
                BalancerSettings.HotStagingLead = (float)lead;
                changed = true;
            }

            bool ac = GUILayout.Toggle(BalancerSettings.AutoCoreSeparation, " Sam odrzuć rdzeń po jego wypaleniu (wyłącz, jeśli staging robi MechJeb)");
            if (ac != BalancerSettings.AutoCoreSeparation) { BalancerSettings.AutoCoreSeparation = ac; changed = true; }

            bool tl = GUILayout.Toggle(BalancerSettings.Timeline, " Oś czasu lotu w porównaniu z oryginałem");
            if (tl != BalancerSettings.Timeline) { BalancerSettings.Timeline = tl; changed = true; }

            GUILayout.EndVertical();

            if (changed) BalancerSettings.Save();
        }

        // ================================================================== zakładka: Misja

        private void DrawMissionTab()
        {
            CelestialBody launch = LaunchBody();
            if (launch == null)
            {
                GUILayout.Label("Brak danych o planecie startowej.");
                return;
            }

            if (launch != lastLaunchBody)
            {
                lastLaunchBody = launch;
                targetIdx = -1;
                altKm = (double.IsNaN(BalancerSettings.OrbitAltKm)
                            ? Math.Round(DeltaVPlanner.LowOrbitAlt(launch) / 1000.0)
                            : BalancerSettings.OrbitAltKm).ToString(CultureInfo.InvariantCulture);
                latDeg = DefaultLatitude().ToString("F1", CultureInfo.InvariantCulture);
            }

            List<CelestialBody> targets = DeltaVPlanner.Targets(launch);
            if (targetIdx >= targets.Count) targetIdx = -1;

            GUILayout.Label("Start z: " + DeltaVPlanner.Name(launch));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Orbita startowa:", GUILayout.Width(110f));
            altKm = GUILayout.TextField(altKm ?? "", 6, GUILayout.Width(60f));
            GUILayout.Label("km    szer. geogr.:", GUILayout.Width(115f));
            latDeg = GUILayout.TextField(latDeg ?? "", 6, GUILayout.Width(50f));
            GUILayout.Label("°");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Cel:", GUILayout.Width(40f));
            if (GUILayout.Button("◀", GUILayout.Width(30f))) targetIdx = targetIdx <= -1 ? targets.Count - 1 : targetIdx - 1;
            GUILayout.Label(targetIdx < 0 ? "(tylko orbita)" : DeltaVPlanner.Name(targets[targetIdx]), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(30f))) targetIdx = targetIdx >= targets.Count - 1 ? -1 : targetIdx + 1;
            GUILayout.EndHorizontal();

            double alt, lat;
            if (!TryNum(altKm, out alt) || alt < 0) alt = DeltaVPlanner.LowOrbitAlt(launch) / 1000.0;
            else if (HighLogic.LoadedSceneIsEditor && (double.IsNaN(BalancerSettings.OrbitAltKm) || Math.Abs(alt - BalancerSettings.OrbitAltKm) > 1e-6))
            {
                BalancerSettings.OrbitAltKm = alt;
                BalancerSettings.Save();
            }
            if (!TryNum(latDeg, out lat)) lat = 0.0;
            else if (HighLogic.LoadedSceneIsEditor && Math.Abs(lat - BalancerSettings.LaunchLatitude) > 1e-6)
            {
                BalancerSettings.LaunchLatitude = lat;
                BalancerSettings.Save();
            }

            CelestialBody target = targetIdx < 0 ? null : targets[targetIdx];
            DvPlan p = DeltaVPlanner.Plan(launch, target, alt * 1000.0, lat);

            GUILayout.Space(6f);
            GUILayout.BeginVertical("box");
            Row("Na orbitę " + alt.ToString("F0") + " km", p.toOrbit);
            double required = p.toOrbit;

            if (target != null)
            {
                string tn = DeltaVPlanner.Name(target);
                Row("Odlot do: " + tn, p.transfer);
                Row("Wejście na orbitę " + tn + " (" + (p.targetLowAlt / 1000.0).ToString("F0") + " km)", p.capture);
                if (p.targetAtmosphere) GUILayout.Label("Lądowanie: ≈ 0 m/s (atmosfera – spadochrony / aerohamowanie)");
                else Row("Lądowanie", p.landing);

                GUILayout.Space(4f);
                missionMode = GUILayout.Toolbar(missionMode, new[] { "Orbita celu", "Lądowanie", "Lądowanie + powrót" });

                Row("Razem do orbity " + tn, p.ToTargetOrbit);
                Row("Razem z lądowaniem", p.ToLanding);
                Row("Razem z powrotem", p.RoundTrip);

                required = missionMode == 0 ? p.ToTargetOrbit : (missionMode == 1 ? p.ToLanding : p.RoundTrip);
            }
            GUILayout.EndVertical();

            GUILayout.Label("Szacunek: transfery Hohmanna, bez zmiany nachylenia orbity, dokładność ok. ±10%. Doliczone straty na grawitację i opór przy starcie.");

            GUILayout.Space(6f);
            double vac;
            if (StockDeltaV.TotalVac(out vac))
            {
                double diff = vac - required;
                GUILayout.Label("Potrzeba: " + Ms(required) + "    Rakieta (próżnia): " + Ms(vac) + "    " +
                                (diff >= 0 ? "zapas " + Ms(diff) : "BRAKUJE " + Ms(-diff)));

                if (GUILayout.Button("Dopasuj ISP rakiety do tej misji (+5% zapasu)"))
                    FitTotal(required * 1.05);
            }
            else
            {
                GUILayout.Label("Potrzeba: " + Ms(required) + "    (brak danych Δv rakiety)");
            }
        }

        // ================================================================== zakładka: Presety

        private void DrawPresetsTab()
        {
            GUILayout.Label("Preset zapamiętuje ISP × i Ciąg × każdej grupy silników oraz masę pustych × i paliwo × jej zbiorników.");
            if (GUILayout.Button("Wczytaj presety ponownie (bez restartu gry)"))
            {
                int n = PresetStore.Reload();
                pendingDelete = null;
                status = string.IsNullOrEmpty(PresetStore.LastError)
                    ? "Wczytano presety: " + n + " (presets.cfg + folder PluginData\\Presets)."
                    : PresetStore.LastError;
            }

            GUILayout.BeginHorizontal();
            presetName = GUILayout.TextField(presetName ?? "", 40, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Z rakiety", GUILayout.Width(75f)))
                presetName = CurrentShipName();
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Zapisz preset"))
            {
                string name = PresetStore.Clean(presetName);
                if (name.Length == 0)
                    status = "Wpisz nazwę presetu.";
                else
                {
                    Refresh();
                    List<StagePreset> list = new List<StagePreset>();
                    foreach (EngineGroup g in groups)
                        list.Add(new StagePreset
                        {
                            stage = g.ignition, jettison = g.jettison, isp = g.appliedIsp, thrust = g.appliedThrust,
                            dry = g.tanks.Count > 0 ? g.appliedDry : float.NaN,
                            fuel = g.tanks.Count > 0 ? g.appliedFuel : float.NaN
                        });

                    bool existed = PresetStore.Find(name) != null;
                    status = PresetStore.SaveOrUpdate(name, list)
                        ? (existed ? "Nadpisano: " : "Zapisano: ") + name
                        : PresetStore.LastError;
                }
                pendingDelete = null;
            }

            if (PresetStore.Presets.Count == 0)
            {
                GUILayout.Label("Brak zapisanych presetów.");
                return;
            }

            presetScroll = GUILayout.BeginScrollView(presetScroll, GUILayout.Height(Mathf.Min(200f, 28f * PresetStore.Presets.Count + 8f)));
            foreach (Preset p in new List<Preset>(PresetStore.Presets))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(p.name + "  (" + p.Summary() + ")" +
                                (string.IsNullOrEmpty(p.description) ? "" : "\n" + p.description), GUILayout.ExpandWidth(true));

                if (GUILayout.Button("Wczytaj", GUILayout.Width(65f)))
                {
                    LoadPreset(p);
                    presetName = p.name;
                    pendingDelete = null;
                }

                if (pendingDelete == p)
                {
                    if (GUILayout.Button("Na pewno?", GUILayout.Width(75f)))
                    {
                        status = PresetStore.Delete(p) ? "Usunięto: " + p.name : PresetStore.LastError;
                        pendingDelete = null;
                    }
                }
                else
                {
                    GUI.enabled = !p.ReadOnly;
                    if (GUILayout.Button(p.ReadOnly ? "z pliku" : "Usuń", GUILayout.Width(75f)))
                        pendingDelete = p;
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(PresetStore.LastError) && string.IsNullOrEmpty(status))
                GUILayout.Label(PresetStore.LastError);
        }

        private void LoadPreset(Preset p)
        {
            Refresh();

            if (p.stages.Count == 0)
            {
                // Preset z wersji 1.3: wspólne wartości dla całego statku.
                SetAll(p.isp, p.thrust);
                ApplyGroups(false);
                status = "Wczytano: " + p.name;
                return;
            }

            int matched = 0;
            foreach (StagePreset sp in p.stages)
            {
                bool any = false;
                foreach (EngineGroup g in groups)
                {
                    // Presety z 1.4 nie mają numeru odrzutu – pasują do wszystkich grup z tym odpaleniem.
                    if (g.ignition != sp.stage) continue;
                    if (sp.jettison != StagePreset.AnyJettison && g.jettison != sp.jettison) continue;
                    g.isp = sp.isp;
                    g.thrust = sp.thrust;
                    if (g.tanks.Count > 0 && HighLogic.LoadedSceneIsEditor)
                    {
                        if (!float.IsNaN(sp.dry)) g.dry = sp.dry;
                        if (!float.IsNaN(sp.fuel)) g.fuel = sp.fuel;
                    }
                    any = true;
                }
                if (any) matched++;
            }
            ApplyGroups(false);

            status = matched == p.stages.Count
                ? "Wczytano: " + p.name
                : "Wczytano: " + p.name + " – pasuje " + matched + " z " + p.stages.Count + " grup (inna kolejność stage'ów?)";
        }

        // ================================================================== logika

        private void Refresh()
        {
            groups = EngineGroups.Build(GetParts(), groups);
        }

        private void SetAll(float isp, float thrust)
        {
            Refresh();
            foreach (EngineGroup g in groups) { g.isp = isp; g.thrust = thrust; }
        }

        private void ApplyGroups(bool resetLimiter)
        {
            EngineGroups.Apply(GetParts(), groups, resetLimiter);
        }

        /// <summary>Mnoży ISP wszystkich grup tak, by całkowita Δv (próżnia) była równa celowi. Dokładne.</summary>
        private void FitTotal(double target)
        {
            double dv;
            if (!StockDeltaV.TotalVac(out dv))
            {
                status = "Brak danych Δv statku.";
                return;
            }

            Refresh();
            float k = (float)(target / dv);
            bool clamped = false;
            foreach (EngineGroup g in groups)
            {
                float v = g.appliedIsp * k;
                float c = Mathf.Clamp(v, ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult);
                if (!Mathf.Approximately(v, c)) clamped = true;
                g.isp = Round2(c);
                g.thrust = g.appliedThrust;
            }
            ApplyGroups(false);

            status = "ISP pomnożone ×" + k.ToString("F2") + " → cel " + Ms(target) +
                     (clamped ? " (część grup doszła do limitu " + ModuleEngineBalancer.MaxIspMult.ToString("F0") + "×)" : "");
        }

        // ================================================================== pomocnicze

        private double CurrentTwr()
        {
            return EngineGroups.LiftoffTwr(GetParts(), TotalMass(), groups, LaunchBody());
        }

        private static double TotalMass()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                return EditorLogic.fetch.ship.GetTotalMass();
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                return FlightGlobals.ActiveVessel.GetTotalMass();
            return 0.0;
        }

        private static CelestialBody LaunchBody()
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                return FlightGlobals.ActiveVessel.mainBody;
            return FlightGlobals.GetHomeBody();
        }

        private static double DefaultLatitude()
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                return FlightGlobals.ActiveVessel.latitude;
            return BalancerSettings.LaunchLatitude; // zapamiętana (domyślnie Bajkonur 45.9°)
        }

        private static string CurrentShipName()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                return EditorLogic.fetch.ship.shipName ?? "";

            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                return FlightGlobals.ActiveVessel.vesselName ?? "";

            return "";
        }

        private static List<Part> GetParts()
        {
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return null;
                return EditorLogic.fetch.ship.parts;
            }

            if (HighLogic.LoadedSceneIsFlight)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return v != null ? v.parts : null;
            }

            return null;
        }

        private static void Field(string label, ref string value, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(230f));
            value = GUILayout.TextField(value ?? "", 6, GUILayout.Width(60f));
            GUILayout.Label(unit);
            GUILayout.EndHorizontal();
        }

        private static void Row(string label, double ms)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.ExpandWidth(true));
            GUILayout.Label(Ms(ms), GUILayout.Width(90f));
            GUILayout.EndHorizontal();
        }

        private static string Ms(double v) { return v.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ") + " m/s"; }

        private static string Sec(double s) { return s > 0 ? s.ToString("F0", CultureInfo.InvariantCulture) + " s" : "–"; }

        private static string Twr(double t) { return t > 0 ? t.ToString("F2", CultureInfo.InvariantCulture) : "–"; }

        private static float Step(float v) { return Mathf.Round(v * 20f) / 20f; }

        /// <summary>Suwak z krokiem 0,05, który nie psuje dokładnych wartości (np. 1,37 z automatu), dopóki się go nie ruszy.</summary>
        private static float Slider(float v, float min, float max)
        {
            float n = GUILayout.HorizontalSlider(v, min, max);
            return Mathf.Approximately(n, v) ? v : Step(n);
        }

        private static float Round2(float v) { return Mathf.Round(v * 100f) / 100f; }

        private static string Was(float now, float applied)
        {
            return Mathf.Approximately(now, applied) ? "" : "   (teraz " + applied.ToString("F2") + ")";
        }

        private static bool TryNum(string s, out double v)
        {
            return double.TryParse((s ?? "").Replace(',', '.').Replace(" ", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
