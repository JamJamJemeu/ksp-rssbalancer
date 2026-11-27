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
        private const float W = 300f, H = 468f, R = 116f;
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
        private GUIStyle sNum, sFace, sSmall, sCap, sTitle, sDrum, sCnt, sBtn;
        private bool styled;

        // kolory jak panel AP-28 (wersja „radziecka kabina”): turkusowa rama, ciemna blacha, mosiężne wkręty
        private static readonly Color Teal = new Color(0.16f, 0.45f, 0.45f), PanelC = new Color(0.115f, 0.12f, 0.13f),
            Bezel = new Color(0.17f, 0.175f, 0.18f), BezelEdge = new Color(0.07f, 0.07f, 0.075f), GroupLine = new Color(0.42f, 0.44f, 0.44f),
            White = new Color(0.95f, 0.95f, 0.92f), BrassC = new Color(0.80f, 0.64f, 0.30f), Orange = new Color(1f, 0.62f, 0.15f);

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
            // tarcza: czarne koło, lekko jaśniejsze w środku, wygładzona krawędź (jak ВЭМ-72 w AP-28)
            const int N = 256;
            disc = new Texture2D(N, N, TextureFormat.ARGB32, false);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = x - N / 2f + 0.5f, dy = y - N / 2f + 0.5f, d = Mathf.Sqrt(dx * dx + dy * dy) / (N / 2f);
                    float a = Mathf.Clamp01((1 - d) * N / 2f);
                    float sh = d > 0.96f ? 0.22f : 0.035f + 0.05f * (1 - d);      // jasny pierścień na brzegu tarczy
                    disc.SetPixel(x, y, new Color(sh, sh, sh, a));
                }
            disc.Apply();
            sNum = SovietSkin.Style(20, White, TextAnchor.MiddleCenter); sNum.fontStyle = FontStyle.Bold;
            sFace = SovietSkin.Style(10, White, TextAnchor.MiddleCenter); sFace.fontStyle = FontStyle.Bold;
            sSmall = SovietSkin.Style(8, new Color(0.8f, 0.8f, 0.78f), TextAnchor.MiddleCenter);
            sCap = SovietSkin.Style(10, new Color(0.85f, 0.86f, 0.84f), TextAnchor.MiddleCenter); sCap.fontStyle = FontStyle.Bold;
            sTitle = SovietSkin.Style(15, new Color(0.88f, 0.89f, 0.87f), TextAnchor.MiddleLeft); sTitle.fontStyle = FontStyle.Bold;
            sDrum = SovietSkin.Style(19, White, TextAnchor.MiddleCenter); sDrum.fontStyle = FontStyle.Bold;
            sCnt = SovietSkin.Style(14, White, TextAnchor.MiddleCenter); sCnt.fontStyle = FontStyle.Bold;
            sBtn = SovietSkin.Style(12, new Color(0.88f, 0.89f, 0.87f), TextAnchor.MiddleCenter); sBtn.fontStyle = FontStyle.Bold;
            sBtn.normal.background = SovietSkin.Button; sBtn.hover.background = SovietSkin.Button;
            sBtn.active.background = SovietSkin.ButtonOn; sBtn.border = new RectOffset(3, 3, 3, 3);
        }

        // skala: km/h 0–600 (cyfry ×100: 0…6), węzły 0–350 (cyfry ×10: 0, 5 … 35); 300° łuku od lewego dołu do prawego dołu
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
        private void Fill(Rect r, Color c) { Color oc = GUI.color; GUI.color = c; GUI.DrawTexture(r, px); GUI.color = oc; }
        private void Frame(Rect r, Color c, float w)
        {
            Fill(new Rect(r.x, r.y, r.width, w), c); Fill(new Rect(r.x, r.yMax - w, r.width, w), c);
            Fill(new Rect(r.x, r.y, w, r.height), c); Fill(new Rect(r.xMax - w, r.y, w, r.height), c);
        }
        private void Brass(float x, float y, float sz) { Color oc = GUI.color; GUI.color = BrassC; GUI.DrawTexture(new Rect(x, y, sz, sz), SovietSkin.Screw); GUI.color = oc; }

        private void DrawWindow(int id)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            // rama i blacha jak AP-28
            Fill(new Rect(0, 0, W, H), Teal);
            Fill(new Rect(8, 8, W - 16, H - 16), PanelC);
            Screw(13, 13); Screw(W - 25, 13); Screw(13, H - 25); Screw(W - 25, H - 25);
            GUI.Label(new Rect(34, 14, 200, 24), "С К О Р О С Т Ь", sTitle);

            double k = knots ? Kt : Kmh;
            double rho = v.atmDensity;
            bool air = rho > 1e-6 && v.mainBody.atmosphere;
            double ias = air ? v.srfSpeed * Math.Sqrt(rho / Rho0) : 0;      // [m/s]

            // --- przyrząd: kwadratowa oprawa z mosiężnymi wkrętami, czarna tarcza (jak ВЭМ-72)
            Rect bz = new Rect(W / 2 - 130, 44, 260, 260);
            Fill(bz, BezelEdge); Fill(new Rect(bz.x + 3, bz.y + 3, bz.width - 6, bz.height - 6), Bezel);
            Brass(bz.x + 7, bz.y + 7, 13); Brass(bz.xMax - 20, bz.y + 7, 13); Brass(bz.x + 7, bz.yMax - 20, 13); Brass(bz.xMax - 20, bz.yMax - 20, 13);
            Vector2 c = new Vector2(bz.center.x, bz.center.y);
            GUI.DrawTexture(new Rect(c.x - R, c.y - R, 2 * R, 2 * R), disc);
            double max = ScaleMax, minor = knots ? 5 : 10, major = knots ? 50 : 50, label = knots ? 50 : 100;
            for (double s = 0; s <= max + 0.01; s += minor)
            {
                bool lab = Math.Abs(s % label) < 0.01, maj = Math.Abs(s % major) < 0.01;
                float a = Ang(s);
                Line(Pol(c, a, R - 5), Pol(c, a, R - (lab ? 20 : maj ? 15 : 10)), White, lab ? 3f : maj ? 2f : 1.2f);
                if (lab)
                {
                    Vector2 p = Pol(c, a, R - 36);
                    GUI.Label(new Rect(p.x - 20, p.y - 12, 40, 24), (s / (knots ? 10 : 100)).ToString("0", Inv), sNum);
                }
            }
            GUI.Label(new Rect(c.x - 50, c.y - 44, 100, 14), knots ? "×10 УЗ" : "×100 КМ/Ч", sFace);
            GUI.Label(new Rect(c.x - 50, c.y - 30, 100, 14), "ПРИБОРНАЯ", sFace);
            // licznik cyfrowy w okienku (jak licznik wysokości ВЭМ-72)
            Rect cnt = new Rect(c.x - 34, c.y + 26, 68, 22);
            Fill(cnt, White); Fill(new Rect(cnt.x + 2, cnt.y + 2, cnt.width - 4, cnt.height - 4), new Color(0.02f, 0.02f, 0.02f));
            GUI.Label(cnt, air ? Math.Min(9999, ias * k).ToString("0000", Inv) : "----", sCnt);
            GUI.Label(new Rect(c.x - 40, c.y + 50, 80, 12), "УС-" + (knots ? "350" : "600"), sSmall);
            // wskazówka: biała, z pomarańczowym końcem i przeciwwagą
            float na = Ang(ias * k);
            Line(Pol(c, na + 180, 22), Pol(c, na, R - 10), White, 3.5f);
            Line(Pol(c, na + 180, 22), Pol(c, na + 180, 8), White, 7f);
            Line(Pol(c, na, R - 30), Pol(c, na, R - 10), Orange, 3.5f);
            GUI.DrawTexture(new Rect(c.x - 8, c.y - 8, 16, 16), SovietSkin.Screw);

            // --- bębenki pod przyrządem (podpisy pod spodem, jak w AP-28)
            float y = bz.yMax + 14;
            Rect grp = new Rect(18, y - 4, W - 36, 74);
            Frame(grp, GroupLine, 1f);
            double agl = v.heightFromTerrain >= 0 ? v.heightFromTerrain : v.altitude;
            Drum(new Rect(28, y + 6, 78, 34), (v.srfSpeed * k).ToString("0", Inv), "ИСТ, " + (knots ? "УЗ" : "КМ/Ч"));
            Drum(new Rect(114, y + 6, 66, 34), air ? v.mach.ToString("0.00", Inv) : "-.--", "М");
            Drum(new Rect(188, y + 6, 84, 34), v.verticalSpeed.ToString("+0;-0;0", Inv), "ВЕРТ, М/С");
            y = grp.yMax + 10;
            Drum(new Rect(28, y, 112, 34), agl < 100000 ? agl.ToString("0", Inv) : "-----", "ВЫС. НАД ЗЕМЛЕЙ, М");
            if (GUI.Button(new Rect(156, y, 116, 34), knots ? "ЕД. УЗ" : "ЕД. КМ/Ч", sBtn)) { knots = !knots; Save(); }
            GUI.DragWindow(new Rect(0, 0, W, H));
        }

        private void Drum(Rect r, string val, string cap)
        {
            Fill(r, new Color(0.03f, 0.03f, 0.035f));
            GUI.DrawTexture(new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6), SovietSkin.Drum);
            GUI.Label(r, val, sDrum);
            GUI.Label(new Rect(r.x - 10, r.yMax + 2, r.width + 20, 14), cap, sCap);
        }

        private static void Screw(float x, float y) { GUI.DrawTexture(new Rect(x, y, 12, 12), SovietSkin.Screw); }
    }
}
