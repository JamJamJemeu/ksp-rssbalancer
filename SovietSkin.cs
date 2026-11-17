using System;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Tekstury i style „radzieckiego pulpitu” w stylu AP-28: turkusowa blacha z lakierem młotkowym,
    /// kineskop w ramce, bursztynowy wyświetlacz, bębenkowe liczniki, lampki i śrubki.
    /// Wszystko generowane raz, w pamięci – mod nie ma plików graficznych.
    /// </summary>
    public static class SovietSkin
    {
        public static Texture2D Metal, Crt, CrtOverlay, Amber, AmberOverlay, Plate;
        public static Texture2D Glare, Grip, Drum, Slot, LampGreen, LampAmber, LampOff, Screw, Button, ButtonOn;
        public static Font Mono;

        public static readonly Color Phosphor = new Color(0.49f, 1f, 0.60f);
        public static readonly Color PhosphorDim = new Color(0.25f, 0.62f, 0.33f);
        public static readonly Color AmberText = new Color(1f, 0.68f, 0.13f);
        public static readonly Color Paint = new Color(0.90f, 0.95f, 0.93f);
        public static readonly Color Brass = new Color(0.79f, 0.70f, 0.48f);
        public static readonly Color DrumText = new Color(0.95f, 0.93f, 0.84f);

        private static int mw, mh, cw, ch, aw, ah, pw, ph;

        public static void Ensure(int metalW, int metalH, int crtW, int crtH, int ambW, int ambH, int plateW, int plateH)
        {
            if (Metal == null || mw != metalW || mh != metalH) { Free(Metal); Metal = MakeMetal(metalW, metalH); mw = metalW; mh = metalH; }
            if (Crt == null || cw != crtW || ch != crtH)
            {
                Free(Crt); Free(CrtOverlay);
                Crt = MakeScreen(crtW, crtH, 14, true, out CrtOverlay);
                cw = crtW; ch = crtH;
            }
            if (Amber == null || aw != ambW || ah != ambH)
            {
                Free(Amber); Free(AmberOverlay);
                Amber = MakeScreen(ambW, ambH, 5, false, out AmberOverlay);
                aw = ambW; ah = ambH;
            }
            if (Plate == null || pw != plateW || ph != plateH) { Free(Plate); Plate = MakePlate(plateW, plateH); pw = plateW; ph = plateH; }

            if (Drum == null) Drum = MakeDrum();
            if (Grip == null) Grip = MakeGrip();
            if (Glare == null) Glare = MakeGlare();
            if (Slot == null) Slot = MakeVertical(8, 16, new Color32(3, 3, 3, 255), new Color32(23, 24, 24, 255));
            if (Button == null) Button = MakeButton(false);
            if (ButtonOn == null) ButtonOn = MakeButton(true);
            if (LampGreen == null) LampGreen = MakeLamp(0);
            if (LampAmber == null) LampAmber = MakeLamp(1);
            if (LampOff == null) LampOff = MakeLamp(2);
            if (Screw == null) Screw = MakeScrew();

            if (Mono == null)
            {
                try { Mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New", "DejaVu Sans Mono" }, 13); }
                catch { Mono = null; }
            }
        }

        private static void Free(Texture2D t) { if (t != null) UnityEngine.Object.Destroy(t); }

        // ================================================================== style tekstu

        public static GUIStyle Style(int size, Color color, TextAnchor anchor)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            if (Mono != null) s.font = Mono;
            s.fontSize = size;
            s.normal.textColor = color;
            s.alignment = anchor;
            s.wordWrap = false;
            s.clipping = TextClipping.Clip;
            s.padding = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        /// <summary>Tekst z poświatą (luminofor / lampa): kilka przyciemnionych kopii wokół i właściwy tekst na wierzchu.</summary>
        public static void Glow(Rect r, string text, GUIStyle s)
        {
            Color c = s.normal.textColor;
            s.normal.textColor = new Color(c.r, c.g, c.b, 0.22f);
            GUI.Label(new Rect(r.x - 1, r.y, r.width, r.height), text, s);
            GUI.Label(new Rect(r.x + 1, r.y, r.width, r.height), text, s);
            GUI.Label(new Rect(r.x, r.y - 1, r.width, r.height), text, s);
            GUI.Label(new Rect(r.x, r.y + 1, r.width, r.height), text, s);
            s.normal.textColor = c;
            GUI.Label(r, text, s);
        }

        /// <summary>Napis „nadrukowany na lakierze” – z ciemnym cieniem pod spodem.</summary>
        public static void Printed(Rect r, string text, GUIStyle s)
        {
            Color c = s.normal.textColor;
            s.normal.textColor = new Color(0, 0, 0, 0.35f);
            GUI.Label(new Rect(r.x, r.y + 1, r.width, r.height), text, s);
            s.normal.textColor = c;
            GUI.Label(r, text, s);
        }

        // ================================================================== szum

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float Fbm(float x, float y, int seed)
        {
            float v = 0, amp = 0.5f, f = 1f;
            for (int o = 0; o < 3; o++) { v += amp * ValueNoise(x * f, y * f, seed + o * 17); f *= 2.1f; amp *= 0.5f; }
            return v / 0.875f * 2f - 1f; // -1..1
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        /// <summary>Odległość od krawędzi zaokrąglonego prostokąta (w px, dodatnia w środku).</summary>
        private static float Inside(float x, float y, float w, float h, float r)
        {
            float dx = Math.Max(Math.Max(r - x, x - (w - 1 - r)), 0);
            float dy = Math.Max(Math.Max(r - y, y - (h - 1 - r)), 0);
            if (dx > 0 && dy > 0) return r - (float)Math.Sqrt(dx * dx + dy * dy);
            return Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
        }

        private static Texture2D New(int w, int h)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            return t;
        }

        private static Color Lerp3(Color a, Color b, Color c, float mid, float t)
        {
            return t < mid ? Color.Lerp(a, b, t / mid) : Color.Lerp(b, c, (t - mid) / (1 - mid));
        }

        // ================================================================== blacha

        private static Texture2D MakeMetal(int w, int h)
        {
            Texture2D t = New(w, h);
            Color32[] px = new Color32[w * h];
            Color c0 = new Color32(51, 163, 155, 255), c1 = new Color32(38, 144, 138, 255), c2 = new Color32(28, 120, 115, 255);
            float gx = 0.423f, gy = 0.906f;                       // kierunek gradientu 155°
            float gn = 0.5f * (gx + gy);
            float hx = 0.883f, hy = 0.469f;                       // kierunek odblasku 118°

            for (int y = 0; y < h; y++)
            {
                int sy = h - 1 - y;                                // wiersz ekranu (tekstura liczy od dołu)
                float rowStreak = (Hash(0, sy, 7) * 2 - 1) * 3.2f;
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / (w - 1), v = (float)sy / (h - 1);
                    float g = Mathf.Clamp01(((u - 0.5f) * gx + (v - 0.5f) * gy) / gn * 0.5f + 0.5f);
                    Color c = Lerp3(c0, c1, c2, 0.48f, g);

                    // winieta
                    float dx = (u - 0.5f) * 2f, dy = (v - 0.45f) * 2f;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy) / 1.414f;
                    c *= 1f - Smooth(0.52f, 1f, d) * 0.30f;

                    // lakier młotkowy: drobne ziarno + łagodne plamy, szczotkowanie wzdłuż wierszy
                    float grain = Fbm(x / 1.6f, sy / 1.6f, 11) * 9f;
                    float mottle = Fbm(x / 16f, sy / 16f, 23) * 6f;
                    float brush = (ValueNoise(x / 45f, sy * 0.9f, 31) * 2 - 1) * 3f + rowStreak;
                    float add = (grain + mottle + brush) / 255f;
                    c.r += add; c.g += add; c.b += add;

                    // przetarcia: jaśniejsze rogi lewy górny i prawy dolny, przybrudzony prawy górny
                    float tl = (float)Math.Sqrt(u * u + v * v * 1.6f);
                    float br = (float)Math.Sqrt((1 - u) * (1 - u) + (1 - v) * (1 - v) * 1.6f);
                    float tr = (float)Math.Sqrt((1 - u) * (1 - u) + v * v * 1.6f);
                    float wear = (1 - Smooth(0f, 0.12f, tl)) * 0.20f + (1 - Smooth(0f, 0.10f, br)) * 0.16f;
                    wear *= 0.6f + 0.4f * ValueNoise(x / 3f, sy / 3f, 41);
                    c = Color.Lerp(c, new Color(0.86f, 0.92f, 0.88f), wear);
                    c *= 1f - (1 - Smooth(0f, 0.14f, tr)) * 0.16f;

                    // odblask
                    float s = (u * hx + v * hy) / (hx + hy);
                    c = Color.Lerp(c, Color.white, Mathf.Clamp01(1 - s / 0.35f) * 0.07f);

                    // fazowanie krawędzi
                    float e = Inside(x, sy, w, h, 6);
                    if (sy < 2) c = Color.Lerp(c, Color.white, 0.22f);
                    if (sy > h - 3) c *= 0.72f;
                    float a = Mathf.Clamp01(e + 0.5f);
                    if (e < 1.5f) c *= 0.65f;
                    c.a = a;
                    px[y * w + x] = c;
                }
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        private static Texture2D MakePlate(int w, int h)
        {
            Texture2D t = New(w, h);
            Color32[] px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = h - 1 - y;
                for (int x = 0; x < w; x++)
                {
                    float u = (float)x / (w - 1), v = (float)sy / (h - 1);
                    float g = Mathf.Clamp01((u * 0.42f + v * 0.9f) / 1.32f);
                    Color c = Lerp3(new Color32(28, 30, 31, 255), new Color32(18, 20, 21, 255), new Color32(11, 12, 13, 255), 0.45f, g);
                    float e = Inside(x, sy, w, h, 3);
                    c *= 1f - (1 - Smooth(0, 8, e)) * 0.45f;
                    float n = Fbm(x / 1.5f, sy / 1.5f, 61) * 0.012f; c.r += n; c.g += n; c.b += n;
                    if (e < 1) c = new Color(0.17f, 0.18f, 0.18f);
                    c.a = Mathf.Clamp01(e + 0.5f);
                    px[y * w + x] = c;
                }
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        // ================================================================== ekrany

        /// <summary>Ekran w ramce: kineskop (zielony) albo wyświetlacz bursztynowy. Overlay = linie skanowania, winieta, odblask.</summary>
        private static Texture2D MakeScreen(int w, int h, int radius, bool crt, out Texture2D overlay)
        {
            Texture2D t = New(w, h);
            overlay = New(w, h);
            Color32[] px = new Color32[w * h];
            Color32[] ov = new Color32[w * h];

            int ring = crt ? 7 : 4;
            Color center = crt ? (Color)new Color32(6, 36, 15, 255) : (Color)new Color32(28, 16, 0, 255);
            Color edge = crt ? (Color)new Color32(2, 10, 4, 255) : (Color)new Color32(6, 4, 2, 255);
            float cx = crt ? 0.45f : 0.30f, cy = crt ? 0.45f : 0.50f, reach = crt ? 0.75f : 0.72f;
            float shadow = crt ? 22f : 10f;

            for (int y = 0; y < h; y++)
            {
                int sy = h - 1 - y;
                for (int x = 0; x < w; x++)
                {
                    float e = Inside(x, sy, w, h, radius);
                    Color c;
                    if (e < 0) { px[y * w + x] = new Color32(0, 0, 0, 0); ov[y * w + x] = new Color32(0, 0, 0, 0); continue; }

                    if (crt && e < 2) c = new Color32(36, 40, 42, 255);
                    else if (crt && e < 3) c = new Color32(12, 13, 13, 255);
                    else if (!crt && e < 1) c = new Color32(12, 13, 13, 255);
                    else if (e < ring - 1) c = new Color32(58, 60, 62, 255);
                    else if (e < ring) c = new Color32(0, 0, 0, 255);
                    else
                    {
                        float u = (float)x / (w - 1), v = (float)sy / (h - 1);
                        float dx = (u - cx) / 0.5f, dy = (v - cy) / 0.5f;
                        float d = (float)Math.Sqrt(dx * dx + dy * dy) / 1.414f;
                        c = Color.Lerp(center, edge, Mathf.Clamp01(d / reach));
                        float ie = e - ring;
                        c *= 1f - (1 - Smooth(0, shadow, ie)) * (1 - Smooth(0, shadow, ie)) * 0.9f;

                        // nakładka: tylko przyciemnienie (linie skanowania i winieta) – odblask jest osobną teksturą
                        float a = (sy % 3 == 0) ? 0.18f : 0f;
                        float vig = crt ? Smooth(0.6f, 1.0f, d) * 0.45f : 0f;
                        a = 1 - (1 - a) * (1 - vig);
                        ov[y * w + x] = new Color(0f, 0f, 0f, a);
                        c.a = 1f;
                        px[y * w + x] = c;
                        continue;
                    }
                    c.a = Mathf.Clamp01(e + 0.5f);
                    px[y * w + x] = c;
                    ov[y * w + x] = new Color32(0, 0, 0, 0);
                }
            }
            t.SetPixels32(px); t.Apply();
            overlay.SetPixels32(ov); overlay.Apply();
            return t;
        }

        // ================================================================== drobne elementy

        /// <summary>Odblask szyby: biały, płynnie zanikający od lewego górnego rogu (bez ostrej krawędzi).</summary>
        private static Texture2D MakeGlare()
        {
            int n = 64;
            Texture2D t = New(n, n);
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)(n - 1), v = 1f - y / (float)(n - 1);   // v = 0 u góry
                    float s = (u * 0.883f + v * 0.469f) / 1.352f;
                    float a = (1f - Smooth(0f, 0.55f, s)) * 0.07f;
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        /// <summary>Uchwyt skalowania: trzy ukośne rowki wytłoczone w blasze.</summary>
        private static Texture2D MakeGrip()
        {
            int n = 22;
            Texture2D t = New(n, n);
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int sy = n - 1 - y;
                    int d = x + sy - (n - 1);          // >= 0 w prawym dolnym trójkącie
                    Color c = Color.clear;
                    if (d >= 0)
                    {
                        int k = d % 6;
                        if (k == 0) c = new Color(0f, 0f, 0f, 0.55f);
                        else if (k == 1) c = new Color(1f, 1f, 1f, 0.30f);
                    }
                    px[y * n + x] = c;
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        private static Texture2D MakeDrum()
        {
            int w = 16, h = 32;
            Texture2D t = New(w, h);
            Color32[] px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = 1f - (float)y / (h - 1);
                Color c;
                if (v < 0.14f) c = Color.Lerp(Color.black, new Color32(26, 26, 26, 255), v / 0.14f);
                else if (v < 0.5f) c = Color.Lerp(new Color32(26, 26, 26, 255), new Color32(43, 43, 43, 255), (v - 0.14f) / 0.36f);
                else if (v < 0.86f) c = Color.Lerp(new Color32(43, 43, 43, 255), new Color32(23, 23, 23, 255), (v - 0.5f) / 0.36f);
                else c = Color.Lerp(new Color32(23, 23, 23, 255), Color.black, (v - 0.86f) / 0.14f);
                for (int x = 0; x < w; x++)
                {
                    Color cc = x == 0 ? (Color)new Color32(5, 5, 5, 255) : x == w - 1 ? (Color)new Color32(53, 53, 53, 255) : c;
                    px[y * w + x] = cc;
                }
            }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        private static Texture2D MakeVertical(int w, int h, Color32 top, Color32 bottom)
        {
            Texture2D t = New(w, h);
            Color32[] px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                Color c = Color.Lerp(bottom, top, (float)y / (h - 1));
                for (int x = 0; x < w; x++) px[y * w + x] = c;
            }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        private static Texture2D MakeButton(bool on)
        {
            int w = 32, h = 24;
            Texture2D t = New(w, h);
            Color32[] px = new Color32[w * h];
            Color top = on ? (Color)new Color32(47, 74, 58, 255) : (Color)new Color32(44, 46, 48, 255);
            Color bot = on ? (Color)new Color32(22, 38, 29, 255) : (Color)new Color32(21, 22, 23, 255);
            for (int y = 0; y < h; y++)
            {
                int sy = h - 1 - y;
                Color c = Color.Lerp(top, bot, (float)sy / (h - 1));
                for (int x = 0; x < w; x++)
                {
                    Color cc = c;
                    if (x == 0 || x == w - 1 || sy == 0 || sy == h - 1) cc = Color.black;
                    else if (sy == 1) cc = Color.Lerp(c, Color.white, 0.16f);
                    else if (sy >= h - 3) cc = c * 0.6f;
                    cc.a = 1;
                    px[y * w + x] = cc;
                }
            }
            t.SetPixels32(px); t.Apply();
            t.filterMode = FilterMode.Point;
            return t;
        }

        /// <summary>0 = zielona zapalona, 1 = bursztynowa zapalona, 2 = zgaszona.</summary>
        private static Texture2D MakeLamp(int kind)
        {
            int s = 32;
            Texture2D t = New(s, s);
            Color32[] px = new Color32[s * s];
            float r = 7f, c0 = (s - 1) / 2f;
            Color inner, mid, outer, halo;
            if (kind == 0) { inner = new Color32(200, 255, 200, 255); mid = new Color32(63, 216, 96, 255); outer = new Color32(20, 128, 44, 255); halo = new Color(0.31f, 1f, 0.47f); }
            else if (kind == 1) { inner = new Color32(255, 242, 176, 255); mid = new Color32(255, 181, 46, 255); outer = new Color32(176, 106, 0, 255); halo = new Color(1f, 0.67f, 0.16f); }
            else { inner = new Color32(36, 80, 44, 255); mid = new Color32(26, 60, 32, 255); outer = new Color32(13, 36, 16, 255); halo = Color.clear; }

            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    int sy = s - 1 - y;
                    float dx = x - c0, dy = sy - c0, d = (float)Math.Sqrt(dx * dx + dy * dy);
                    Color c = Color.clear;
                    if (d <= r)
                    {
                        float k = d / r;
                        c = k < 0.5f ? Color.Lerp(inner, mid, k / 0.5f) : Color.Lerp(mid, outer, (k - 0.5f) / 0.5f);
                        float hx = x - (c0 - r * 0.28f), hy = sy - (c0 - r * 0.40f);
                        float hd = (float)Math.Sqrt(hx * hx + hy * hy) / (r * 0.75f);
                        c = Color.Lerp(c, Color.white, Mathf.Clamp01(1 - hd) * (kind == 2 ? 0.3f : 0.8f));
                        if (kind == 2) c *= 1f - Smooth(0.6f, 1f, k) * 0.6f;
                        c.a = 1;
                    }
                    else if (d <= r + 2) c = new Color32(58, 60, 63, 255);
                    else if (d <= r + 3) c = new Color32(10, 10, 10, 255);
                    else if (kind != 2)
                    {
                        float a = Mathf.Clamp01(1 - (d - r - 3) / 9f);
                        c = new Color(halo.r, halo.g, halo.b, a * a * 0.5f);
                    }
                    px[y * s + x] = c;
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        private static Texture2D MakeScrew()
        {
            int s = 16;
            Texture2D t = New(s, s);
            Color32[] px = new Color32[s * s];
            float c0 = (s - 1) / 2f, r = 6.5f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    int sy = s - 1 - y;
                    float dx = x - c0, dy = sy - c0, d = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (d > r + 0.8f) { px[y * s + x] = d < r + 2 && sy > c0 ? new Color32(0, 0, 0, 90) : new Color32(0, 0, 0, 0); continue; }
                    float hx = x - (c0 - 2), hy = sy - (c0 - 2);
                    float k = (float)Math.Sqrt(hx * hx + hy * hy) / (r * 1.6f);
                    Color c = Lerp3(new Color32(214, 215, 210, 255), new Color32(154, 155, 150, 255), new Color32(58, 59, 54, 255), 0.4f, Mathf.Clamp01(k));
                    if (Math.Abs(dx - dy) < 1.1f && d < r - 1) c = new Color32(42, 43, 39, 255); // nacięcie pod śrubokręt
                    c.a = Mathf.Clamp01(r + 0.8f - d);
                    px[y * s + x] = c;
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }
    }
}
