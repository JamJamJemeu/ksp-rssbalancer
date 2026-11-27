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
        private const float W = 300f, H = 468f, R = 112f;
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
        private static Texture2D texKmh, texKt, texNeedle, texTop, cellTex;
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
            if (texKmh == null)
            {
                texKmh = Res("us_face_kmh"); texKt = Res("us_face_kt"); texNeedle = Res("us_needle"); texTop = Res("us_top");
                // bębenek licznika: pionowy gradient jak .vem-cell (czarny – szary – czarny, ciemne brzegi)
                cellTex = new Texture2D(1, 32, TextureFormat.ARGB32, false);
                for (int yy = 0; yy < 32; yy++)
                {
                    float t = yy / 31f, g = t < 0.14f ? Mathf.Lerp(0f, 0.10f, t / 0.14f) : t < 0.5f ? Mathf.Lerp(0.10f, 0.17f, (t - 0.14f) / 0.36f) : t < 0.86f ? Mathf.Lerp(0.17f, 0.09f, (t - 0.5f) / 0.36f) : Mathf.Lerp(0.09f, 0f, (t - 0.86f) / 0.14f);
                    float edge = Mathf.Clamp01(Mathf.Min(t, 1 - t) / 0.3f); g *= 0.15f + 0.85f * edge;
                    cellTex.SetPixel(0, yy, new Color(g, g, g, 1));
                }
                cellTex.Apply();
            }
            sNum = SovietSkin.Style(20, White, TextAnchor.MiddleCenter); sNum.fontStyle = FontStyle.Bold;
            sFace = SovietSkin.Style(10, White, TextAnchor.MiddleCenter); sFace.fontStyle = FontStyle.Bold;
            sSmall = SovietSkin.Style(8, new Color(0.8f, 0.8f, 0.78f), TextAnchor.MiddleCenter);
            sCap = SovietSkin.Style(10, new Color(0.85f, 0.86f, 0.84f), TextAnchor.MiddleCenter); sCap.fontStyle = FontStyle.Bold;
            sTitle = SovietSkin.Style(15, new Color(0.88f, 0.89f, 0.87f), TextAnchor.MiddleLeft); sTitle.fontStyle = FontStyle.Bold;
            sDrum = SovietSkin.Style(19, White, TextAnchor.MiddleCenter); sDrum.fontStyle = FontStyle.Bold;
            sCnt = SovietSkin.Style(17, new Color(0.92f, 0.91f, 0.86f), TextAnchor.MiddleCenter); sCnt.fontStyle = FontStyle.Bold;
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

            // --- przyrząd: kopia wysokościomierza ВЭМ-72 z AP-28 (te same gradienty, mosiężne wkręty, litery, wskazówka i szkło),
            //     tylko ze skalą prędkości; tekstury wygenerowane z kodu SVG panelu AP-28 (Res/us_gen.js)
            const float G = 260f; float S = G / 240f;
            Rect bz = new Rect(W / 2 - G / 2, 44, G, G);
            Vector2 c = new Vector2(bz.x + 120 * S, bz.y + 120 * S);
            if (texKmh != null) GUI.DrawTexture(bz, knots ? texKt : texKmh);
            // licznik cyfrowy: 4 bębenki w okienku (jak licznik ВЭМ-72)
            string digits = air ? ((int)Math.Min(9999, Math.Round(ias * k))).ToString("0000", Inv) : "----";
            for (int d = 0; d < 4; d++)
            {
                Rect cell = new Rect(bz.x + (84.5f + d * 18f) * S, bz.y + 135 * S, 17f * S, 22f * S);
                if (cellTex != null) GUI.DrawTexture(cell, cellTex); else Fill(cell, new Color(0.15f, 0.15f, 0.15f));
                GUI.Label(cell, digits.Substring(d, 1), sCnt);
            }
            // wskazówka (obrót wokół środka tarczy)
            float na = Ang(ias * k);
            if (texNeedle != null)
            {
                Matrix4x4 m = GUI.matrix;
                GUIUtility.RotateAroundPivot(na, c);
                GUI.DrawTexture(bz, texNeedle);
                GUI.matrix = m;
                GUI.DrawTexture(bz, texTop);
            }

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

        /// <summary>Tekstura 480×480 RGBA z zasobu DLL (gzip, wiersze od dołu).</summary>
        private static Texture2D Res(string name)
        {
            try
            {
                using (Stream st = typeof(AirspeedIndicator).Assembly.GetManifestResourceStream("RSSBalancer.Res." + name + ".rgba.gz"))
                using (var gz = new System.IO.Compression.GZipStream(st, System.IO.Compression.CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    gz.CopyTo(ms);
                    var t = new Texture2D(480, 480, TextureFormat.RGBA32, false);
                    t.LoadRawTextureData(ms.ToArray()); t.Apply(false, true);
                    t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp;
                    return t;
                }
            }
            catch (Exception e) { Debug.LogError("[RSSBalancer] УС: brak tekstury " + name + ": " + e.Message); return null; }
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
