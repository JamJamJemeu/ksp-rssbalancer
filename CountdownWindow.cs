using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Pulpit odliczania programu lotu w stylu radzieckim (jak ТЕЛЕМЕТРИЯ: blacha, liczniki bębenkowe, lampki,
    /// bursztynowa taśma). Pokazuje, na co program czeka i ile zostało: start w oknie startowym, opóźnienie kroku,
    /// warunek czasu lotu, impuls hamujący z planu, impuls manewru orbitalnego, wejście w atmosferę.
    /// Gdy krok czeka na warunek, którego nie da się przewidzieć, zapala się „БЕЗ ПРОГНОЗА” i taśma mówi, na co –
    /// żeby było widać, czy program czeka, czy coś się zacięło (lot 24.11 15:45: plan zejścia stał 15 h bez okna).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CountdownWindow : MonoBehaviour
    {
        private const int WindowId = 0x52535355;
        private const float W = 430f, H = 318f;
        private static bool show = true, loaded;
        private static Rect win = new Rect(300, 80, W, H);
        private static string CfgFile { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "countdown.cfg")); } }
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private GUIStyle sWin, sPlate, sPlateR, sCap, sDrum, sDrumBig, sUnit, sAmber, sLamp, sBtn;
        private bool styled;

        public static bool Show
        {
            get { Load(); return show; }
            set { Load(); show = value; Save(); }
        }

        private static void Load()
        {
            if (loaded) return; loaded = true;
            try
            {
                if (!File.Exists(CfgFile)) return;
                ConfigNode n = ConfigNode.Load(CfgFile);
                show = n.GetValue("show") != "False";
                float x, y;
                if (float.TryParse(n.GetValue("x"), NumberStyles.Float, Inv, out x) && float.TryParse(n.GetValue("y"), NumberStyles.Float, Inv, out y)) { win.x = x; win.y = y; }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                ConfigNode n = new ConfigNode();
                n.AddValue("show", show ? "True" : "False");
                n.AddValue("x", win.x.ToString(Inv)); n.AddValue("y", win.y.ToString(Inv));
                n.Save(CfgFile);
            }
            catch { }
        }

        private static bool Ru { get { return BalancerSettings.TelemetryRussian; } }
        private static string L(string ru, string pl) { return Ru ? ru : pl; }

        private void OnGUI()
        {
            Load();
            FlightProgramRunner run = FlightProgramRunner.Instance;
            if (!show || run == null || !run.Active || FlightGlobals.ActiveVessel == null) return;
            if (Hud.Hidden && !BalancerSettings.TelemetryOnF2) return;
            TelemetryWindow.EnsureSkin();
            if (!styled) MakeStyles();
            win.x = Mathf.Clamp(win.x, -W + 80, Screen.width - 80);
            win.y = Mathf.Clamp(win.y, 0, Screen.height - 40);
            Rect r = GUI.Window(WindowId, win, DrawWindow, "", sWin);
            if (r.x != win.x || r.y != win.y) { win = r; if (Event.current.type == EventType.MouseUp) Save(); }
        }

        private void MakeStyles()
        {
            styled = true;
            sWin = new GUIStyle();
            sPlate = SovietSkin.Style(12, SovietSkin.Paint, TextAnchor.MiddleLeft);
            sPlateR = SovietSkin.Style(11, SovietSkin.Brass, TextAnchor.MiddleRight);
            sCap = SovietSkin.Style(10, SovietSkin.Paint, TextAnchor.MiddleCenter);
            sDrum = SovietSkin.Style(15, SovietSkin.DrumText, TextAnchor.MiddleCenter);
            sDrumBig = SovietSkin.Style(20, SovietSkin.DrumText, TextAnchor.MiddleCenter);
            sUnit = SovietSkin.Style(11, SovietSkin.Brass, TextAnchor.MiddleLeft);
            sAmber = SovietSkin.Style(11, SovietSkin.AmberText, TextAnchor.MiddleLeft);
            sLamp = SovietSkin.Style(10, SovietSkin.Paint, TextAnchor.MiddleLeft);
            sBtn = SovietSkin.Style(10, SovietSkin.DrumText, TextAnchor.MiddleCenter);
            sBtn.normal.background = SovietSkin.Button; sBtn.hover.background = SovietSkin.Button;
            sBtn.active.background = SovietSkin.ButtonOn; sBtn.border = new RectOffset(3, 3, 3, 3);
        }

        private static string KindRu(FlightProgramRunner.CdKind k)
        {
            switch (k)
            {
                case FlightProgramRunner.CdKind.Launch: return "ДО ПУСКА";
                case FlightProgramRunner.CdKind.StepDelay: return "ДО СЛЕДУЮЩЕЙ КОМАНДЫ";
                case FlightProgramRunner.CdKind.Met: return "ДО КОМАНДЫ ПО ВРЕМЕНИ";
                case FlightProgramRunner.CdKind.Deorbit: return "ДО ТОРМОЗНОГО ИМПУЛЬСА";
                case FlightProgramRunner.CdKind.Maneuver: return "ДО ВКЛЮЧЕНИЯ ОДУ";
                case FlightProgramRunner.CdKind.Atmosphere: return "ДО ВХОДА В АТМОСФЕРУ";
                default: return "ОЖИДАНИЕ УСЛОВИЯ";
            }
        }
        private static string KindPl(FlightProgramRunner.CdKind k)
        {
            switch (k)
            {
                case FlightProgramRunner.CdKind.Launch: return "DO STARTU";
                case FlightProgramRunner.CdKind.StepDelay: return "DO NASTĘPNEJ KOMENDY";
                case FlightProgramRunner.CdKind.Met: return "DO KOMENDY CZASOWEJ";
                case FlightProgramRunner.CdKind.Deorbit: return "DO IMPULSU HAMUJĄCEGO";
                case FlightProgramRunner.CdKind.Maneuver: return "DO ZAPŁONU ОДУ";
                case FlightProgramRunner.CdKind.Atmosphere: return "DO WEJŚCIA W ATMOSFERĘ";
                default: return "CZEKAM NA WARUNEK";
            }
        }

        /// <summary>Czas w postaci „ggg:mm:ss” (do 999 h).</summary>
        public static string Hms(double s)
        {
            if (double.IsNaN(s)) return "---:--:--";
            s = Math.Max(0, Math.Min(s, 999 * 3600 + 3599));
            long t = (long)Math.Floor(s);
            return (t / 3600).ToString("000") + ":" + ((t / 60) % 60).ToString("00") + ":" + (t % 60).ToString("00");
        }

        private void DrawWindow(int id)
        {
            FlightProgramRunner run = FlightProgramRunner.Instance;
            Vessel v = FlightGlobals.ActiveVessel;
            if (run == null || !run.Active || v == null) return;
            double now = Planetarium.GetUniversalTime();

            GUI.DrawTexture(new Rect(0, 0, W, H), SovietSkin.Metal);
            Screw(6, 6); Screw(W - 18, 6); Screw(6, H - 18); Screw(W - 18, H - 18);
            Rect plate = new Rect(16, 12, W - 80, 26);
            GUI.DrawTexture(plate, SovietSkin.Plate);
            GUI.Label(new Rect(plate.x + 10, plate.y, 190, plate.height), L("ПУЛЬТ ОТСЧЁТА", "PULPIT ODLICZANIA"), sPlate);
            GUI.Label(new Rect(plate.x + 150, plate.y, plate.width - 158, plate.height), "«" + run.Running.name + "»", sPlateR);
            if (GUI.Button(new Rect(W - 58, 13, 40, 24), "X", sBtn)) Show = false;

            // główny licznik
            double ut; FlightProgramRunner.CdKind kind;
            bool has = run.Countdown(out ut, out kind);
            string cap = L(KindRu(kind), KindPl(kind));
            Counter(16, 48, W - 32, cap, Hms(has ? ut - now : double.NaN), has ? "Т−" : "", sDrumBig, 22f, 34f);

            // czas lotu
            double met = v.situation == Vessel.Situations.PRELAUNCH ? 0 : Math.Max(0, v.missionTime);
            Counter(16, 116, (W - 32) / 2, L("ВРЕМЯ ПОЛЁТА", "CZAS LOTU"), Hms(met), "Т+", sDrum, 15f, 24f);
            int steps = run.Running.steps.Count;
            Counter(16 + (W - 32) / 2, 116, (W - 32) / 2, L("КОМАНДА", "KROK"), (run.Current + 1).ToString("00") + "/" + steps.ToString("00"), "", sDrum, 15f, 24f);

            // lampki
            bool warp = TimeWarp.CurrentRateIndex > 0;
            bool blink = (Time.realtimeSinceStartup % 1f) < 0.5f;
            Lamp(22, 174, true, false, L("ПРОГРАММА", "PROGRAM"));
            Lamp(160, 174, has, false, L("ПРОГНОЗ", "PROGNOZA"));
            Lamp(298, 174, !has, true, L("БЕЗ ПРОГНОЗА", "BEZ PROGNOZY"));      // bursztyn ciągły: czekamy na warunek bez przewidywanego czasu
            Lamp(22, 198, warp, false, L("УСКОРЕНИЕ", "PRZYSPIESZENIE"));
            Lamp(160, 198, run.ManeuverActive || run.SeparationActive, false, L("МАНЁВР", "MANEWR"));
            Lamp(298, 198, run.LaunchHold && blink, true, L("ОКНО ПУСКА", "OKNO STARTU"));   // miga: czekamy na okno startowe

            // taśma: krok, warunek, powód
            Rect amb = new Rect(16, 226, W - 32, 76);
            GUI.DrawTexture(amb, SovietSkin.Amber);
            FlightPrograms.Step st = run.Running.steps[run.Current];
            string l1 = L("КОМАНДА: ", "KROK: ") + st.name;
            string l2 = L("УСЛОВИЕ: ", "WARUNEK: ") + run.CurrentWhen;
            string l3 = Reason(run, has, kind);
            SovietSkin.Glow(new Rect(amb.x + 10, amb.y + 6, amb.width - 18, 18), l1, sAmber);
            SovietSkin.Glow(new Rect(amb.x + 10, amb.y + 27, amb.width - 18, 18), l2, sAmber);
            SovietSkin.Glow(new Rect(amb.x + 10, amb.y + 48, amb.width - 18, 18), l3 + (blink ? " █" : ""), sAmber);
            GUI.DrawTexture(amb, SovietSkin.AmberOverlay);
            GUI.DrawTexture(new Rect(amb.x + 4, amb.y + 4, amb.width - 8, amb.height - 8), SovietSkin.Glare);

            GUI.DragWindow(new Rect(0, 0, W, 44));
        }

        private static string Reason(FlightProgramRunner run, bool has, FlightProgramRunner.CdKind kind)
        {
            string w = run.CurrentWhen.ToLowerInvariant();
            if (kind == FlightProgramRunner.CdKind.Maneuver || run.ManeuverActive) return run.ManeuverInfo;
            if (w.Contains("deorbitin") && !string.IsNullOrEmpty(run.PlanInfo)) return (Ru ? "ПЛАН: " : "PLAN: ") + run.PlanInfo;
            if (!has && !string.IsNullOrEmpty(run.LastMessage)) return run.LastMessage.Replace("\n", " · ");
            return has ? L("ИДЁТ ОТСЧЁТ", "TRWA ODLICZANIE") : L("ЖДУ ВЫПОЛНЕНИЯ УСЛОВИЯ", "CZEKAM, AŻ WARUNEK SIĘ SPEŁNI");
        }

        private void Counter(float x, float y, float boxW, string caption, string digits, string prefix, GUIStyle drum, float cw, float chh)
        {
            SovietSkin.Printed(new Rect(x, y, boxW, 14), caption, sCap);
            float pw = prefix.Length > 0 ? 28f : 0f;
            float total = digits.Length * cw + pw + 6;
            float x0 = x + (boxW - total) / 2f;
            GUI.DrawTexture(new Rect(x0 - 3, y + 16, total, chh + 6), SovietSkin.Slot);
            if (pw > 0) GUI.Label(new Rect(x0 + 2, y + 19, pw, chh), prefix, sUnit);
            for (int i = 0; i < digits.Length; i++)
            {
                Rect c = new Rect(x0 + pw + i * cw, y + 19, cw, chh);
                if (digits[i] == ':' || digits[i] == '/') { GUI.Label(c, digits[i].ToString(), sUnit); continue; }
                GUI.DrawTexture(c, SovietSkin.Drum);
                GUI.Label(c, digits[i].ToString(), drum);
            }
        }

        private void Lamp(float x, float y, bool on, bool amber, string label)
        {
            Texture2D tex = !on ? SovietSkin.LampOff : amber ? SovietSkin.LampAmber : SovietSkin.LampGreen;
            GUI.DrawTexture(new Rect(x - 4, y - 4, 26, 26), tex);
            SovietSkin.Printed(new Rect(x + 22, y, 116, 18), label, sLamp);
        }

        private static void Screw(float x, float y) { GUI.DrawTexture(new Rect(x, y, 12, 12), SovietSkin.Screw); }
    }
}
