using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Prędkościomierz „УС” w osobnym oknie (styl jak panel AP-28: blacha, czarna tarcza, biała wskazówka).
    /// Wskazówka: prędkość przyrządowa (ПР – z ciśnienia dynamicznego, KSP: EAS = V·√(ρ/ρ0)), w km/h albo w węzłach
    /// (przycisk КМ/Ч ⇄ УЗ, wybór zapamiętany). Pod tarczą: prędkość rzeczywista (ИСТ), liczba Macha, prędkość pionowa
    /// i wysokość nad terenem – to, co potrzebne przy podejściu i przyziemieniu samolotu.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AirspeedIndicator : MonoBehaviour
    {
        private const int WindowId = 0x55534931;
        private const float W = 270f, H = 372f, R = 112f;
        private static bool show, knots, loaded;
        private static Rect win = new Rect(40, 300, W, H);
        private static string CfgFile { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "us.cfg")); } }
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const double Rho0 = 1.225, Kmh = 3.6, Kt = 1.943844;

        public static bool Show { get { Load(); return show; } set { Load(); show = value; Save(); } }

        private static void Load()
        {
            if (loaded) return; loaded = true;
            try
            {
                if (!File.Exists(CfgFile)) return;
                ConfigNode n = ConfigNode.Load(CfgFile);
                show = n.GetValue("show") == "True";
                knots = n.GetValue("unit") == "kt";
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
                n.AddValue("unit", knots ? "kt" : "kmh");
                n.AddValue("x", win.x.ToString(Inv)); n.AddValue("y", win.y.ToString(Inv));
                n.Save(CfgFile);
            }
            catch { }
        }

        private Texture2D px, disc;
        private GUIStyle sNum, sCap, sDrum, sUnit, sBtn, sFace;
        private bool styled;

        private void OnDestroy() { Save(); }

        private void OnGUI()
        {
            Load();
            if (!show || !HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null || Hud.Hidden) return;
            TelemetryWindow.EnsureSkin();
            if (!styled) MakeStyles();
            win.x = Mathf.Clamp(win.x, -W + 80, Screen.width - 80);
            win.y = Mathf.Clamp(win.y, 0, Screen.height - 40);
            Rect r = GUI.Window(WindowId, win, DrawWindow, "", new GUIStyle());
            if (r.x != win.x || r.y != win.y) { win = r; if (Event.current.type == EventType.MouseUp) Save(); }
        }

        private void MakeStyles()
        {
            styled = true;
            px = new Texture2D(1, 1); px.SetPixel(0, 0, Color.white); px.Apply();
            // tarcza: czarne koło z wygładzoną krawędzią
            const int N = 256;
            disc = new Texture2D(N, N, TextureFormat.ARGB32, false);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = x - N / 2f + 0.5f, dy = y - N / 2f + 0.5f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(N / 2f - d);
                    float shade = 0.07f + 0.04f * (1 - d / (N / 2f));
                    disc.SetPixel(x, y, new Color(shade, shade, shade + 0.005f, a));
                }
            disc.Apply();
            sNum = SovietSkin.Style(13, SovietSkin.Paint, TextAnchor.MiddleCenter);
            sFace = SovietSkin.Style(11, SovietSkin.Paint, TextAnchor.MiddleCenter);
            sCap = SovietSkin.Style(10, SovietSkin.Paint, TextAnchor.MiddleLeft);
            sDrum = SovietSkin.Style(16, SovietSkin.DrumText, TextAnchor.MiddleRight);
            sUnit = SovietSkin.Style(10, SovietSkin.Brass, TextAnchor.MiddleLeft);
            sBtn = SovietSkin.Style(11, SovietSkin.DrumText, TextAnchor.MiddleCenter);
            sBtn.normal.background = SovietSkin.Button; sBtn.hover.background = SovietSkin.Button;
            sBtn.active.background = SovietSkin.ButtonOn; sBtn.border = new RectOffset(3, 3, 3, 3);
        }

        // skala: km/h 0–600 (cyfry co 100), węzły 0–350 (cyfry co 50); 300° łuku od lewego dołu do prawego dołu
        private static double ScaleMax { get { return knots ? 350 : 600; } }
        private static float Ang(double val) { return (float)(-150 + 300 * Math.Max(0, Math.Min(val, ScaleMax)) / ScaleMax); }
        private static Vector2 Pol(Vector2 c, float deg, float r) { float a = deg * Mathf.Deg2Rad; return new Vector2(c.x + r * Mathf.Sin(a), c.y - r * Mathf.Cos(a)); }

        private void Line(Vector2 a, Vector2 b, Color c, float w)
        {
            Vector2 d = b - a; float len = d.magnitude; if (len < 0.1f) return;
            Matrix4x4 m = GUI.matrix; Color oc = GUI.color;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - w / 2, len, w), px);
            GUI.matrix = m; GUI.color = oc;
        }

        private void DrawWindow(int id)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            GUI.DrawTexture(new Rect(0, 0, W, H), SovietSkin.Metal);
            Screw(6, 6); Screw(W - 18, 6); Screw(6, H - 18); Screw(W - 18, H - 18);

            double k = knots ? Kt : Kmh;
            double rho = v.atmDensity;
            bool air = rho > 1e-6 && v.mainBody.atmosphere;
            double ias = air ? v.srfSpeed * Math.Sqrt(rho / Rho0) : 0;      // [m/s]
            double tas = v.srfSpeed;

            // --- tarcza
            Vector2 c = new Vector2(W / 2, 26 + R);
            GUI.DrawTexture(new Rect(c.x - R - 6, c.y - R - 6, 2 * R + 12, 2 * R + 12), SovietSkin.Plate);
            GUI.DrawTexture(new Rect(c.x - R, c.y - R, 2 * R, 2 * R), disc);
            double max = ScaleMax, minor = 10, major = 50, label = knots ? 50 : 100;
            for (double s = 0; s <= max + 0.01; s += minor)
            {
                bool maj = Math.Abs(s % major) < 0.01;
                float a = Ang(s);
                Line(Pol(c, a, R - 4), Pol(c, a, R - (maj ? 16 : 9)), SovietSkin.Paint, maj ? 2.2f : 1.1f);
                if (Math.Abs(s % label) < 0.01)
                {
                    Vector2 p = Pol(c, a, R - 30);
                    GUI.Label(new Rect(p.x - 20, p.y - 9, 40, 18), (s / (knots ? 1 : 100)).ToString("0", Inv), sFace);
                }
            }
            GUI.Label(new Rect(c.x - 40, c.y - 46, 80, 16), "УС", sNum);
            GUI.Label(new Rect(c.x - 50, c.y + 22, 100, 14), knots ? "УЗЛЫ" : "КМ/Ч ×100", sFace);

            // --- wskazówka ПР
            float na = Ang(ias * k);
            Line(Pol(c, na + 180, 18), Pol(c, na, R - 8), new Color(0.97f, 0.97f, 0.92f), 3.2f);
            Line(Pol(c, na, R - 26), Pol(c, na, R - 8), new Color(1f, 0.55f, 0.25f), 3.2f);
            GUI.DrawTexture(new Rect(c.x - 9, c.y - 9, 18, 18), SovietSkin.Screw);

            // --- cyfrowe odczyty
            float y = c.y + R + 16;
            string u = knots ? "уз" : "км/ч";
            Row(ref y, "ПР  (przyrządowa)", air ? (ias * k).ToString("0", Inv) : "---", u);
            Row(ref y, "ИСТ (rzeczywista)", (tas * k).ToString("0", Inv), u);
            Row(ref y, "М / ВЕРТ", (air ? v.mach.ToString("0.00", Inv) : "-.--") + "  " + v.verticalSpeed.ToString("+0;-0", Inv), "м/с");
            double agl = v.heightFromTerrain >= 0 ? v.heightFromTerrain : v.altitude;
            Row(ref y, "ВЫС над terenem", agl < 10000 ? agl.ToString("0", Inv) : (agl / 1000).ToString("0.0", Inv) + " к", "м");

            if (GUI.Button(new Rect(W - 86, 8, 70, 20), knots ? "УЗ → КМ/Ч" : "КМ/Ч → УЗ", sBtn)) { knots = !knots; Save(); }
            GUI.DragWindow(new Rect(0, 0, W, H));
        }

        private void Row(ref float y, string cap, string val, string unit)
        {
            GUI.Label(new Rect(16, y, 120, 20), cap, sCap);
            GUI.DrawTexture(new Rect(136, y, 82, 20), SovietSkin.Drum);
            GUI.Label(new Rect(136, y, 78, 20), val, sDrum);
            GUI.Label(new Rect(222, y, 44, 20), unit, sUnit);
            y += 23;
        }

        private static void Screw(float x, float y) { GUI.DrawTexture(new Rect(x, y, 12, 12), SovietSkin.Screw); }
    }
}
