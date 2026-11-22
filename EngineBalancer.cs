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
        private static readonly string[] Tabs = { "Statek", "Grupy", "Sojuz", "Energia", "Misja", "Presety", "MechJeb", "Program" };

        private static bool show = false;
        private static int tab = 0;
        private const float WinW = 440f, WinMinH = 600f;
        private static Rect window = new Rect(300f, 120f, WinW, WinMinH);
        private bool scaling; private Vector2 scaleStartMouse; private float scaleStart;

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
        private Vector2 mjScroll;
        private bool mjLoaded;
        private bool progLoaded;
        private Vector2 progScroll;
        private int progSel;
        private int aoFor = -1;
        private string aoPe = "", aoAp = "", aoInc = "", aoCheck = "";
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

            float sc = Mathf.Clamp(BalancerSettings.WindowScale, 0.6f, 1.6f);
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sc, sc, 1f));

            // Wąskie, wysokie okno: stała szerokość, etykiety zawijane.
            bool wrap = GUI.skin.label.wordWrap;
            GUI.skin.label.wordWrap = true;
            window.width = WinW;
            window.height = Mathf.Max(window.height, WinMinH);
            window = GUILayout.Window(WindowId, window, DrawWindow, "Engine Balancer", GUILayout.Width(WinW), GUILayout.MinHeight(WinMinH));
            GUI.skin.label.wordWrap = wrap;

            // Kursor nad oknem = gra nie reaguje na klawisze (wpisywanie liczb i nazw nie odpala stage'ów itd.)
            if (window.Contains(Event.current.mousePosition))
                InputLockManager.SetControlLock(ControlTypes.All, LockId);
            else
                InputLockManager.RemoveControlLock(LockId);
            GUI.matrix = old;
        }

        private void DrawWindow(int id)
        {
            // skala okna
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Skala ×" + BalancerSettings.WindowScale.ToString("F2"), GUILayout.Width(80f));
            if (GUILayout.Button("−", GUILayout.Width(26f))) { BalancerSettings.WindowScale = Mathf.Max(0.6f, BalancerSettings.WindowScale - 0.1f); BalancerSettings.Save(); }
            if (GUILayout.Button("+", GUILayout.Width(26f))) { BalancerSettings.WindowScale = Mathf.Min(1.6f, BalancerSettings.WindowScale + 0.1f); BalancerSettings.Save(); }
            if (GUILayout.Button("1:1", GUILayout.Width(36f))) { BalancerSettings.WindowScale = 1f; BalancerSettings.Save(); }
            GUILayout.EndHorizontal();

            // zakładki w dwóch rzędach
            int newTab = GUILayout.SelectionGrid(tab, Tabs, 4);
            if (newTab != tab)
            {
                tab = newTab;
                status = null;
                pendingDelete = null;
                window.height = WinMinH; // okno samo dopasuje wysokość do zawartości zakładki
            }
            GUILayout.Space(6f);

            switch (tab)
            {
                case 0: DrawShipTab(); break;
                case 1: DrawGroupsTab(); break;
                case 2: DrawSoyuzTab(); break;
                case 3: DrawEnergiaTab(); break;
                case 4: DrawMissionTab(); break;
                case 5: DrawPresetsTab(); break;
                case 6: DrawMechJebTab(); break;
                case 7: DrawProgramTab(); break;
            }

            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(4f);
                GUILayout.Label(status);
            }

            // uchwyt skalowania w prawym dolnym rogu
            Rect grip = new Rect(window.width - 18f, window.height - 18f, 16f, 16f);
            GUI.Label(grip, "◢");
            Event ev = Event.current;
            if (ev.type == EventType.MouseDown && grip.Contains(ev.mousePosition)) { scaling = true; scaleStartMouse = GUIUtility.GUIToScreenPoint(ev.mousePosition); scaleStart = BalancerSettings.WindowScale; ev.Use(); }
            if (scaling && ev.type == EventType.MouseDrag)
            {
                Vector2 now = GUIUtility.GUIToScreenPoint(ev.mousePosition);
                float k = 1f + (now.x - scaleStartMouse.x + now.y - scaleStartMouse.y) / (WinW + WinMinH);
                BalancerSettings.WindowScale = Mathf.Clamp(scaleStart * k, 0.6f, 1.6f);
                ev.Use();
            }
            if (scaling && ev.type == EventType.MouseUp) { scaling = false; BalancerSettings.Save(); ev.Use(); }

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
                foreach (EngineGroup g in groups) g.engMass = 1f;
                ApplyGroups(true);
                // Zbiorniki: masa pustych ×1, obecne paliwo zostaje i staje się nowym ×1.
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                    foreach (Part p in EditorLogic.fetch.ship.parts)
                    {
                        foreach (ModuleTankBalancer tb in p.FindModulesImplementing<ModuleTankBalancer>()) tb.ResetKeepCapacity();
                        foreach (ModuleEngineBalancer eb in p.FindModulesImplementing<ModuleEngineBalancer>()) { eb.scaleWholePart = false; eb.massMultiplier = 1f; }
                        p.UpdateMass();
                    }
                ModuleEngineBalancer.NotifyEditor();
                Refresh();
                status = "Przywrócono oryginalne wartości silników (ISP, ciąg, masa) i masy pustych zbiorników (paliwo bez zmian).";
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

        // ================================================================== zakładka: Energia

        private static string eTB, eTC, eIspB, eIspC, ePropB, ePropC, eMass, eDryB, eDryC;
        private static int eMode = 0;
        private static readonly string[] EModes = { "Konstrukcja jak w oryginale", "Masa startowa", "Suwak" };
        private static float eDry = 0.3f;

        private static void EnergiaDefaults()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            eTB = EnergiaTemplate.BoosterTime.ToString("F0", c);
            eTC = EnergiaTemplate.CoreTime.ToString("F0", c);
            eIspB = EnergiaTemplate.BoosterIspVac.ToString("F0", c);
            eIspC = EnergiaTemplate.CoreIspVac.ToString("F0", c);
            ePropB = EnergiaTemplate.BoosterProp.ToString("F0", c);
            ePropC = EnergiaTemplate.CoreProp.ToString("F1", c);
            eMass = EnergiaTemplate.LiftoffMass.ToString("F0", c);
            eDryB = EnergiaTemplate.BoosterDry.ToString("F0", c);
            eDryC = EnergiaTemplate.CoreDry.ToString("F1", c);
        }

        private void DrawEnergiaTab()
        {
            if (eTB == null) EnergiaDefaults();
            GUILayout.Label("Wzorzec Energia / Buran: boczne bloki A (RD-170, nafta) i rdzeń Ц (RD-0120, wodór) mają własne ISP. " +
                            "Dla każdego mod ustawia paliwo, ISP i ciąg tak, by masa paliwa, ISP w próżni i czas pracy były jak w oryginale.");

            SoyuzTemplate.Result id = SoyuzTemplate.Identify(groups);
            GUILayout.BeginVertical("box");
            if (id.ok)
            {
                GUILayout.Label("Bloki A:  " + id.boosters.Label + "  ·  teraz " + Sec(id.boosters.BurnTimeNow) + ", ISP " + Sec(id.boosters.ispVac) +
                                ", paliwo " + id.boosters.propMass.ToString("F0") + " / " + id.boosters.propCapacity.ToString("F0") + " t");
                GUILayout.Label("Rdzeń Ц:  " + id.core.Label + "  ·  teraz " + Sec(id.core.BurnTimeNow) + ", ISP " + Sec(id.core.ispVac) +
                                ", paliwo " + id.core.propMass.ToString("F0") + " / " + id.core.propCapacity.ToString("F0") + " t");
                GUILayout.Label(id.upper != null
                    ? "Orbiter (ОДУ):  " + id.upper.Label + "  ·  ISP " + Sec(id.upper.ispVac) + ", paliwo " + id.upper.propMass.ToString("F1") + " t (bez zmian)"
                    : "Silniki orbitera: nie znaleziono");
                GUILayout.Label("Masa startowa: teraz " + ShipMassNoClamps().ToString("F0") + " t · TWR na starcie: teraz " + Twr(CurrentTwr()));
            }
            else
            {
                GUILayout.Label(id.message);
            }
            GUILayout.EndVertical();

            GUILayout.Space(4f);
            GUILayout.Label("                                        Bloki A           Rdzeń Ц");
            Row3("Czas pracy od startu [s]:", ref eTB, ref eTC);
            Row3("ISP w próżni [s]:", ref eIspB, ref eIspC);
            Row3("Paliwo [t] (bloki razem):", ref ePropB, ref ePropC);
            GUILayout.Label("Masa pustych bloków i rdzenia:");
            eMode = GUILayout.Toolbar(eMode, EModes);
            if (eMode == 0)
                Row3("Konstrukcja bez paliwa [t]:", ref eDryB, ref eDryC);
            else if (eMode == 1)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Masa startowa (wspólny mnożnik):", GUILayout.Width(200f));
                eMass = GUILayout.TextField(eMass ?? "", 7, GUILayout.Width(80f));
                GUILayout.Label("t");
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("Masa pustych zbiorników × (bloki i rdzeń): " + eDry.ToString("F2"));
                eDry = Slider(eDry, ModuleTankBalancer.MinDry, ModuleTankBalancer.MaxDry);
            }
            GUILayout.Label("Energia-Buran 1K1: bloki 143 s, rdzeń 466 s, ISP 338 / 454 s, paliwo 1228 / 704 t, konstrukcja 262 / 73 t, Buran ≈105 t, masa startowa 2375 t, TWR ≈ 1,48.");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Wartości Energii (1K1)")) EnergiaDefaults();
            GUI.enabled = id.ok && HighLogic.LoadedSceneIsEditor;
            if (GUILayout.Button("Oblicz i zastosuj")) RunEnergiaSolve();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!HighLogic.LoadedSceneIsEditor)
                GUILayout.Label("Paliwo i zbiorniki da się zmieniać tylko w VAB.");

            GUILayout.Space(6f);
            GUILayout.BeginVertical("box");
            GUILayout.Label("Profil lotu jak w oryginale:\n" +
                            "• T+143 s – wyłączenie i odrzut bloków A (parami),\n" +
                            "• T+466 s – wyłączenie rdzenia na torze z perygeum ok. −10 km (rdzeń spada do oceanu),\n" +
                            "• kilkanaście sekund później – odłączenie orbitera,\n" +
                            "• w apogeum – impuls silników orbitera (ОДУ), który podnosi perygeum i daje orbitę.\n" +
                            "W MechJeb (PVG): orbita docelowa Pe −10 km / Ap ok. 150–160 km, Last Stage = stage odrzutu rdzenia.");
            GUILayout.EndVertical();
        }

        private static void Row3(string label, ref string a, ref string b)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(200f));
            a = GUILayout.TextField(a ?? "", 7, GUILayout.Width(80f));
            GUILayout.Space(30f);
            b = GUILayout.TextField(b ?? "", 7, GUILayout.Width(80f));
            GUILayout.EndHorizontal();
        }

        private void RunEnergiaSolve()
        {
            EnergiaTemplate.Targets t = new EnergiaTemplate.Targets { mode = (EnergiaTemplate.DryMode)eMode, dry = eDry };
            if (!TryNum(eTB, out t.tB) || !TryNum(eTC, out t.tC) || !TryNum(eIspB, out t.ispB) || !TryNum(eIspC, out t.ispC) ||
                !TryNum(ePropB, out t.propB) || !TryNum(ePropC, out t.propC) || (eMode == 1 && !TryNum(eMass, out t.mass)) ||
                (eMode == 0 && (!TryNum(eDryB, out t.dryB) || !TryNum(eDryC, out t.dryC) || t.dryB <= 0 || t.dryC <= 0)) ||
                t.tB <= 0 || t.tC <= t.tB || t.ispB <= 0 || t.ispC <= 0 || t.propB <= 0 || t.propC <= 0 || (eMode == 1 && t.mass <= 0))
            {
                status = "Wpisz poprawne wartości (rdzeń musi pracować dłużej niż bloki).";
                return;
            }

            Refresh();
            CelestialBody body = LaunchBody();
            double g = body != null ? body.gravParameter / (body.Radius * body.Radius) : 9.80665;
            EnergiaTemplate.Result r = EnergiaTemplate.Solve(groups, ShipMassNoClamps(), g, t);
            if (r.ok)
            {
                // Energia: mnożniki masy obejmują całą część (też masę z TweakScale itp.).
                foreach (EngineGroup g2 in new[] { r.id.boosters, r.id.core })
                {
                    foreach (ModuleTankBalancer tb in g2.tanks)
                        if (tb != null) tb.scaleWholePart = true;
                    foreach (Part p in g2.parts)
                        foreach (ModuleEngineBalancer eb in p.FindModulesImplementing<ModuleEngineBalancer>())
                            eb.scaleWholePart = true;
                }
                ApplyGroups(false);
                foreach (EngineGroup g2 in new[] { r.id.boosters, r.id.core })
                {
                    foreach (ModuleTankBalancer tb in g2.tanks) if (tb != null && tb.part != null) tb.part.UpdateMass();
                    foreach (Part p in g2.parts) if (p != null) p.UpdateMass();
                }
                EngineGroups.FillTanks(r.id.boosters);
                EngineGroups.FillTanks(r.id.core);
                ModuleEngineBalancer.NotifyEditor();
                Refresh();
            }
            status = r.message + (r.ok ? "\nZastosowano, zbiorniki bloków i rdzenia zatankowane do pełna." : "");
        }

        /// <summary>Masa statku bez klamer startowych [t] – z ciągu i TWR liczonych tak samo jak w całym modzie.</summary>
        private double ShipMassNoClamps()
        {
            CelestialBody body = LaunchBody();
            double twr = CurrentTwr();
            if (body == null || twr <= 0) return TotalMass();
            int launch = int.MinValue;
            foreach (EngineGroup g in groups) launch = Math.Max(launch, g.ignition);
            double thrust = 0;
            foreach (EngineGroup g in groups) if (g.ignition == launch) thrust += g.thrustAsl;
            return thrust / (twr * body.gravParameter / (body.Radius * body.Radius));
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

        // ================================================================== zakładka: Program lotu
        private bool airportsLoaded, airportsOpen;
        private Vector2 airportScroll;

        // Pola orbity dla programów z „autoOrbit”: puste = wartości z pliku programu.
        private string AutoOrbitText()
        {
            string pe = aoPe.Trim().Replace(',', '.'), ap = aoAp.Trim().Replace(',', '.'), inc = aoInc.Trim().Replace(',', '.');
            if (pe == "" && ap == "" && inc == "") return "";
            string alt = pe == "" ? (ap == "" ? "auto" : ap) : (ap == "" ? pe : pe + "-" + ap);
            return alt + " " + (inc == "" ? "auto" : inc);
        }

        private void DrawAutoOrbitFields(FlightPrograms.Program p)
        {
            if (aoFor != progSel)
            {
                aoFor = progSel; aoCheck = "";
                string f = FlightPrograms.AutoOrbitFromFile(p);
                string[] w = f.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                aoPe = aoAp = aoInc = "";
                if (w.Length > 0 && w[0].ToLowerInvariant() != "auto")
                {
                    string[] pa = w[0].Split(new[] { '-', 'x', 'X', '×', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (pa.Length > 0) aoPe = pa[0];
                    if (pa.Length > 1) aoAp = pa[1];
                }
                if (w.Length > 1 && w[1].ToLowerInvariant() != "auto") aoInc = w[1];
            }
            GUILayout.Space(4f);
            GUILayout.Label("Orbita docelowa (puste = auto: niska orbita nad atmosferą, nachylenie = szerokość startu; apogeum puste = orbita kołowa):");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Perygeum [km]", GUILayout.Width(95f)); aoPe = GUILayout.TextField(aoPe, GUILayout.Width(60f));
            GUILayout.Label("  Apogeum [km]", GUILayout.Width(95f)); aoAp = GUILayout.TextField(aoAp, GUILayout.Width(60f));
            GUILayout.Label("  Nachylenie [°]", GUILayout.Width(100f)); aoInc = GUILayout.TextField(aoInc, GUILayout.Width(50f));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Sprawdź", GUILayout.Width(90f)))
                aoCheck = HighLogic.LoadedSceneIsFlight ? FlightProgramRunner.CheckAutoOrbit(AutoOrbitText() == "" ? FlightPrograms.AutoOrbitFromFile(p) : AutoOrbitText()) : "Sprawdzenie działa w locie (rakieta na wyrzutni).";
            if (GUILayout.Button("Jak w pliku", GUILayout.Width(100f))) aoFor = -1;
            if (GUILayout.Button("Auto", GUILayout.Width(60f))) { aoPe = aoAp = aoInc = ""; aoCheck = ""; }
            GUILayout.EndHorizontal();
            if (aoCheck != "") GUILayout.Label(aoCheck);
        }

        private void DrawProgramTab()
        {
            if (!progLoaded) { FlightPrograms.Reload(); progLoaded = true; }
            GUILayout.Label("Programy lotu (cyklogramy) z plików GameData/RSSBalancer/PluginData/Programs/*.cfg.\nKroki wykonują się po kolei, każdy czeka na swój warunek. Działa tylko w locie.");
            if (GUILayout.Button("Odśwież listę", GUILayout.Width(140f))) { FlightPrograms.Reload(); status = "Programów: " + FlightPrograms.Programs.Count; }
            FlightProgramRunner run = FlightProgramRunner.Instance;
            if (FlightPrograms.Programs.Count == 0) { GUILayout.Label("Brak programów w folderze."); return; }

            string[] names = FlightPrograms.Programs.ConvertAll(x => x.name).ToArray();
            progSel = Mathf.Clamp(progSel, 0, names.Length - 1);
            progSel = GUILayout.SelectionGrid(progSel, names, 1);
            FlightPrograms.Program p = run != null && run.Active ? run.Running : FlightPrograms.Programs[progSel];

            foreach (string d in p.description) GUILayout.Label("• " + d);
            GUILayout.Space(4f);
            progScroll = GUILayout.BeginScrollView(progScroll, GUILayout.Height(240f));
            for (int i = 0; i < p.steps.Count; i++)
            {
                FlightPrograms.Step st = p.steps[i];
                bool cur = run != null && run.Active && run.Running == p && run.Current == i;
                string mark = st.activatedAt >= 0 ? "✓ T+" + st.activatedAt.ToString("F0") + " s " : st.activatedAt == -2 ? "– pominięty " : cur ? "▶ " : "· ";
                GUILayout.BeginHorizontal();
                GUILayout.Label(mark + st.name + "   [" + (st.node.GetValue("when") ?? "always") + "]");
                if (run != null && run.Active && run.Running == p && i >= run.Current && st.activatedAt == -1)
                {
                    bool tgt = run.PhysWarpTarget == i;
                    if (GUILayout.Button(tgt ? "⏩×3" : "⏩", GUILayout.Width(44f))) status = run.WarpToStep(i);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (FlightPrograms.HasAutoOrbit(p) && !(run != null && run.Active)) DrawAutoOrbitFields(p);

            GUILayout.BeginHorizontal();
            if (run == null) GUILayout.Label("Program lotu działa tylko w locie.");
            else if (!run.Active)
            {
                if (GUILayout.Button("Start programu"))
                {
                    FlightPrograms.AutoOrbitOverride = FlightPrograms.HasAutoOrbit(FlightPrograms.Programs[progSel]) ? AutoOrbitText() : "";
                    run.Begin(FlightPrograms.Programs[progSel]); status = run.LastMessage;
                }
            }
            else
            {
                if (GUILayout.Button("Pomiń krok")) run.Skip();
                if (GUILayout.Button("⏩ Następny")) status = run.WarpToStep(run.Current);
                if (GUILayout.Button("Zatrzymaj")) { run.Stop(); status = "Program zatrzymany."; }
            }
            GUILayout.EndHorizontal();
            if (run != null)
            {
                bool day = GUILayout.Toggle(run.RequireDaylight, " ☀ Lądowanie w dzień (Słońce ≥ 10° nad lotniskiem)");
                if (day != run.RequireDaylight) { run.RequireDaylight = day; run.Replan(); status = day ? "Szukam przelotu z lądowaniem w dzień…" : "Najbliższy przelot, bez względu na porę dnia."; }
            }
            bool hud = GUILayout.Toggle(FlightProgramRunner.BigHud, " Duży licznik na ekranie (odległość, zasięg, manewr)");
            if (hud != FlightProgramRunner.BigHud) FlightProgramRunner.BigHud = hud;
            bool crt = GUILayout.Toggle(BuranCrt.Show, " Wyświetlacz Burana (CRT) – ekrany wejścia, podejścia i lądowania jak w kabinie");
            if (crt != BuranCrt.Show) BuranCrt.Show = crt;
            bool pa3 = GUILayout.Toggle(Pa3Plotter.Show, " Planszet ПА-3 – mapa: pas, krąg, ścieżka 19° i ślad lotu");
            if (pa3 != Pa3Plotter.Show) Pa3Plotter.Show = pa3;
            if (GUILayout.Button("Pomiar terenu Bajkonuru (zapis do Logs)"))
            {
                try { status = TerrainSurvey.Run(); } catch (Exception e) { status = "Pomiar: błąd " + e.Message; Debug.LogException(e); }
            }
            // lotnisko docelowe dla programów z „site = wybrane” (pasy z Lotniska.cfg i z Kerbal Konstructs)
            if (!airportsLoaded) { Airports.Refresh(); airportsLoaded = true; }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lotnisko docelowe: " + (Airports.Selected != null ? Airports.Selected.name : "—"));
            if (GUILayout.Button(airportsOpen ? "▲ Zwiń" : "▼ Zmień", GUILayout.Width(90f))) airportsOpen = !airportsOpen;
            GUILayout.EndHorizontal();
            if (airportsOpen)
            {
                if (GUILayout.Button("Odśwież listę lotnisk", GUILayout.Width(180f))) { Airports.Refresh(); status = Airports.LastScan; }
                Vessel av = FlightGlobals.ActiveVessel;
                airportScroll = GUILayout.BeginScrollView(airportScroll, GUILayout.Height(160f));
                foreach (Airports.Airport a in Airports.List)
                {
                    string dist = av != null ? " · " + (Airports.GreatCircleM(av.latitude, av.longitude, a.Lat, a.Lon) / 1000).ToString("F0") + " km" : "";
                    string label = (a == Airports.Selected ? "● " : "○ ") + a.name + "  (" + a.Lat.ToString("F2") + "°, " + a.Lon.ToString("F2") + "°, pas " +
                                   (a.length / 1000).ToString("F1") + " km, kurs " + a.Heading.ToString("F0") + "°" + (a.approxHeading ? "?" : "") + ")" + dist;
                    if (GUILayout.Button(label, GUI.skin.label))
                    {
                        Airports.Select(a); airportsOpen = false;
                        status = run != null ? run.AirportChosen(a) : "Lotnisko: " + a.name;
                        if (run != null && run.Active) run.Replan();
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.Label(Airports.LastScan + ". „?” przy kursie – kierunek pasa przybliżony (lepiej dopisać pas do Lotniska.cfg).");
            }
            if (run != null && !string.IsNullOrEmpty(run.SiteInfo)) GUILayout.Label(run.SiteInfo);
            if (run != null && !string.IsNullOrEmpty(run.PlanInfo)) GUILayout.Label("Zejście: " + run.PlanInfo);
            if (run != null && !string.IsNullOrEmpty(run.EntryInfo)) GUILayout.Label("Wejście: " + run.EntryInfo);
            if (run != null && !string.IsNullOrEmpty(run.ApproachInfo)) GUILayout.Label("Podejście: " + run.ApproachInfo);
            if (run != null && !string.IsNullOrEmpty(run.AutoOrbitInfo)) GUILayout.Label("Start na orbitę: " + run.AutoOrbitInfo);
            if (run != null && !string.IsNullOrEmpty(run.LandingVerdict))
            {
                Color oc = GUI.color;
                GUI.color = run.LandingVerdict.StartsWith("NA PASIE") ? Color.green : Color.red;
                GUILayout.Label("Lądowanie: " + run.LandingVerdict);
                GUI.color = oc;
            }
            if (run != null && !string.IsNullOrEmpty(run.StallInfo))
            {
                Color old = GUI.color;
                GUI.color = run.StallLevel == 2 ? Color.red : run.StallLevel == 1 ? Color.yellow : old;
                GUILayout.Label("Przeciągnięcie: " + run.StallInfo);
                GUI.color = old;
            }
            if (run != null && !string.IsNullOrEmpty(run.LastMessage)) GUILayout.Label(run.LastMessage);
        }

        // ================================================================== zakładka: MechJeb
        private void DrawMechJebTab()
        {
            if (!mjLoaded) { MechJebPresets.Reload(); mjLoaded = true; }
            GUILayout.Label("Presety ustawień MechJeba (Ascent Guidance / PVG, przepustnica, staging) z plików\nGameData/RSSBalancer/PluginData/MechJeb/*.cfg. „Wczytaj” wpisuje je do MechJeba na tym statku – także w locie.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Odśwież listę", GUILayout.Width(140f))) { MechJebPresets.Reload(); status = "Znaleziono presetów: " + MechJebPresets.Presets.Count; }
            GUILayout.Label(MechJebPresets.MechJebPresent ? "MechJeb na statku: tak" : "MechJeb na statku: nie");
            GUILayout.EndHorizontal();
            if (MechJebPresets.Presets.Count == 0) { GUILayout.Label("Brak presetów w folderze."); return; }
            mjScroll = GUILayout.BeginScrollView(mjScroll, GUILayout.Height(320f));
            foreach (MechJebPresets.Preset p in MechJebPresets.Presets)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(p.name + "  (" + p.file + ")");
                if (GUILayout.Button("Wczytaj", GUILayout.Width(90f))) status = MechJebPresets.Apply(p);
                GUILayout.EndHorizontal();
                foreach (string d in p.description) GUILayout.Label("• " + d);
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
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
            GUILayout.Label(label, GUILayout.Width(200f));
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
