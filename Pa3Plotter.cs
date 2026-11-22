using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Planszet nawigacyjny w stylu radzieckiego ПА-3 (планшет автоматический): kartka mapy z siatką, na której
    /// „pisak” rysuje przebytą trasę, a na niej pas, zaplanowany krąg (4 zakręty po 90°), miejsce wejścia na
    /// stromą ścieżkę 19° i znak samolotu. Widok w układzie pasa: kierunek lądowania do góry.
    /// Dane z programu lotu (pas, krąg, podejście) – te same, którymi steruje autopilot.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Pa3Plotter : MonoBehaviour
    {
        private static bool show, loaded;
        private static Rect win = new Rect(540, 120, 470, 560);
        private static int scaleIdx = 2;
        private static readonly double[] Scales = { 10000, 20000, 40000, 80000 };   // szerokość kartki [m]
        private static bool centerOnPlane;
        private static string CfgFile { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "pa3.cfg")); } }
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
                show = n.GetValue("show") == "True";
                centerOnPlane = n.GetValue("center") == "plane";
                int si; if (int.TryParse(n.GetValue("scale"), out si)) scaleIdx = Mathf.Clamp(si, 0, Scales.Length - 1);
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
                n.AddValue("center", centerOnPlane ? "plane" : "runway");
                n.AddValue("scale", scaleIdx.ToString(Inv));
                n.AddValue("x", win.x.ToString(Inv)); n.AddValue("y", win.y.ToString(Inv));
                n.Save(CfgFile);
            }
            catch { }
        }

        // ---- ślad pisaka: szerokość/długość/wysokość co ~1 s (geograficznie – nie zależy od obrotu planety)
        private struct Pt { public double lat, lon, alt; }
        private readonly List<Pt> track = new List<Pt>();
        private Guid trackVessel;
        private double nextSample;

        private void FixedUpdate()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            if (v.id != trackVessel) { track.Clear(); trackVessel = v.id; }
            double now = Planetarium.GetUniversalTime();
            if (now < nextSample) return;
            nextSample = now + 1.0;
            if (v.situation == Vessel.Situations.ORBITING || v.situation == Vessel.Situations.ESCAPING) return;
            if (track.Count > 0)
            {
                Pt l = track[track.Count - 1];
                if (Math.Abs(l.lat - v.latitude) < 1e-6 && Math.Abs(l.lon - v.longitude) < 1e-6) return;
            }
            track.Add(new Pt { lat = v.latitude, lon = v.longitude, alt = v.altitude });
            if (track.Count > 20000) track.RemoveRange(0, 2000);
        }

        // ---- wygląd: papier mapy, tusz, czerwony znak samolotu, rama przyrządu
        private static readonly Color Paper = new Color(0.86f, 0.84f, 0.72f), PaperDark = new Color(0.74f, 0.72f, 0.60f),
            Grid = new Color(0.55f, 0.60f, 0.50f, 0.7f), Ink = new Color(0.10f, 0.12f, 0.35f), InkLight = new Color(0.25f, 0.30f, 0.60f, 0.8f),
            Plan = new Color(0.15f, 0.15f, 0.15f, 0.85f), Rwy = new Color(0.12f, 0.12f, 0.12f), Red = new Color(0.80f, 0.10f, 0.08f),
            Frame = new Color(0.20f, 0.22f, 0.20f), FrameLight = new Color(0.33f, 0.36f, 0.33f), Amber = new Color(1f, 0.78f, 0.30f), Green = new Color(0.55f, 0.95f, 0.55f);
        private Texture2D px;
        private GUIStyle lbl, lblC, lblR, small, btn;
        private const float MW = 440, MH = 440;

        private void OnDestroy() { Save(); }

        private void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !Show || FlightGlobals.ActiveVessel == null || Hud.Hidden) return;
            if (px == null)
            {
                px = new Texture2D(1, 1); px.SetPixel(0, 0, Color.white); px.Apply();
                lbl = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
                lblC = new GUIStyle(lbl) { alignment = TextAnchor.MiddleCenter };
                lblR = new GUIStyle(lbl) { alignment = TextAnchor.MiddleRight };
                small = new GUIStyle(lbl) { fontSize = 10 };
                btn = new GUIStyle(GUI.skin.button) { fontSize = 11, fontStyle = FontStyle.Bold, padding = new RectOffset(2, 2, 1, 1) };
            }
            Rect r = GUI.Window(0x42D4A3, win, DrawWindow, "ПА-3 – planszet nawigacyjny");
            if (r.x != win.x || r.y != win.y) win = r;
        }

        private void Line(Vector2 a, Vector2 b, Color c, float w = 1.5f)
        {
            Vector2 d = b - a; float len = d.magnitude; if (len < 0.1f) return;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 m = GUI.matrix; Color oc = GUI.color;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(ang, a);
            GUI.DrawTexture(new Rect(a.x, a.y - w / 2, len, w), px);
            GUI.matrix = m; GUI.color = oc;
        }
        private void Dashed(Vector2 a, Vector2 b, Color c, float w = 1.3f, float dash = 7, float gap = 5)
        {
            Vector2 d = b - a; float len = d.magnitude; if (len < 0.1f) return;
            Vector2 u = d / len;
            for (float t = 0; t < len; t += dash + gap) Line(a + u * t, a + u * Mathf.Min(len, t + dash), c, w);
        }
        private void Fill(Rect r, Color c) { Color oc = GUI.color; GUI.color = c; GUI.DrawTexture(r, px); GUI.color = oc; }
        private void Box(Rect r, Color c, float w = 1.2f)
        {
            Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), c, w); Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), c, w);
            Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), c, w); Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), c, w);
        }
        private void Text(float x, float y, string t, Color c, GUIStyle st = null, float w = 120)
        {
            st = st ?? lbl; st.normal.textColor = c; GUI.Label(new Rect(x, y - 8, w, 16), t, st);
        }
        private void Circle(Vector2 c, float rad, Color col, float w = 1.3f)
        {
            Vector2 p = c + new Vector2(rad, 0);
            for (int k = 1; k <= 24; k++) { float a = k * Mathf.PI * 2 / 24; Vector2 q = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad; Line(p, q, col, w); p = q; }
        }

        // układ pasa → ekran: x (wzdłuż kierunku lądowania) do góry, y (w prawo) w prawo
        private Rect map;
        private double mPerPx, cx, cy;
        private Vector2 S(double x, double y) { return new Vector2((float)(map.center.x + (y - cy) / mPerPx), (float)(map.center.y - (x - cx) / mPerPx)); }
        private bool In(Vector2 p) { return map.Contains(p); }

        private void DrawWindow(int id)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            FlightProgramRunner run = FlightProgramRunner.Instance;
            Rect body = new Rect(6, 22, win.width - 12, win.height - 28);
            Fill(body, Frame);
            Box(body, FrameLight, 2f);
            map = new Rect(body.x + 9, body.y + 30, MW, MH);
            // nagłówek jak tabliczka przyrządu
            Text(body.x + 10, body.y + 14, "ПА-3", Amber, lbl, 50);
            Text(body.x + 48, body.y + 14, "ПЛАНШЕТ АВТОМАТИЧЕСКИЙ", new Color(0.85f, 0.85f, 0.78f), small, 200);

            Vector3d thr = Vector3d.zero, dir = Vector3d.zero; double len = 0;
            bool rw = run != null && run.RunwayGeo(out thr, out dir, out len);
            CelestialBody b = v.mainBody;
            Vector3d upT = rw ? thr.normalized : Vector3d.up;
            Vector3d dirH = rw ? Vector3d.Exclude(upT, dir).normalized : Vector3d.zero;
            Vector3d rightH = rw ? Vector3d.Cross(upT, dirH).normalized : Vector3d.zero;
            Func<double, double, double, Vector2d> F = (lat, lon, alt) =>
            {
                Vector3d p = b.GetWorldSurfacePosition(lat, lon, alt) - b.position - thr;
                return new Vector2d(Vector3d.Dot(p, dirH), Vector3d.Dot(p, rightH));
            };
            Vector2d me = rw ? F(v.latitude, v.longitude, v.altitude) : Vector2d.zero;

            mPerPx = Scales[scaleIdx] / MW;
            if (centerOnPlane || !rw) { cx = me.x; cy = me.y; }
            else
            {
                // środek kartki: środek zaplanowanego kręgu (gdy jest), inaczej środek pasa
                List<Vector2d> c = run.PatternCorners;
                if (c != null && c.Count >= 4) { cx = (c[0].x + c[2].x) / 2; cy = (c[0].y + c[2].y) / 2; }
                else { cx = len / 2; cy = 0; }
            }

            // papier + siatka kilometrowa
            Fill(map, Paper);
            double gridM = Scales[scaleIdx] <= 10000 ? 1000 : Scales[scaleIdx] <= 40000 ? 2000 : 5000;
            double x0 = cx - MH / 2 * mPerPx, x1 = cx + MH / 2 * mPerPx, y0 = cy - MW / 2 * mPerPx, y1 = cy + MW / 2 * mPerPx;
            for (double g = Math.Ceiling(x0 / gridM) * gridM; g <= x1; g += gridM) { Vector2 a = S(g, y0), c2 = S(g, y1); Line(a, c2, Math.Abs(g % (gridM * 5)) < 1 ? PaperDark : Grid, 1f); }
            for (double g = Math.Ceiling(y0 / gridM) * gridM; g <= y1; g += gridM) { Vector2 a = S(x0, g), c2 = S(x1, g); Line(a, c2, Math.Abs(g % (gridM * 5)) < 1 ? PaperDark : Grid, 1f); }

            if (!rw)
            {
                Text(map.x + 20, map.center.y, "Brak pasa – uruchom program lotu z „runway”", Ink, lbl, 400);
            }
            else
            {
                // przedłużona oś pasa
                Vector2 ax0 = S(-60000, 0), ax1 = S(60000, 0);
                ClipDashed(ax0, ax1, InkLight, 1f);
                // pas: prostokąt 60 m (wizualnie min. 4 px) z numerami kursu
                float wpx = Mathf.Max(4f, (float)(60 / mPerPx));
                Vector2 r0 = S(0, 0), r1 = S(len, 0);
                ClipLine(r0, r1, Rwy, wpx);
                double hdg = Heading(dirH, upT, b);
                string nA = (((int)Math.Round(hdg / 10) + 35) % 36 + 1).ToString("00"), nB = (((int)Math.Round((hdg + 180) % 360 / 10) + 35) % 36 + 1).ToString("00");
                if (In(r0)) Text(r0.x - 15, r0.y + 12, nA, Rwy, lblC, 30);
                if (In(r1)) Text(r1.x - 15, r1.y - 12, nB, Rwy, lblC, 30);

                // zaplanowany krąg: boki przerywane, narożniki z numerami zakrętów, łuki zakrętów
                List<Vector2d> c = run.PatternCorners;
                if (c != null && c.Count >= 5)
                {
                    Vector2d prev = new Vector2d(0, 0);
                    for (int i = 0; i < c.Count; i++)
                    {
                        Color col = i == run.PatternLeg && run.PatternActive ? Red : Plan;
                        ClipDashed(S(prev.x, prev.y), S(c[i].x, c[i].y), col, i == run.PatternLeg && run.PatternActive ? 2f : 1.3f);
                        if (i < 4)
                        {
                            Vector2 pc = S(c[i].x, c[i].y);
                            if (In(pc)) { Circle(pc, 7, Plan, 1.2f); Text(pc.x - 10, pc.y, (i + 1).ToString(), Plan, lblC, 20); }
                        }
                        prev = c[i];
                    }
                }
                // wejście na stromą ścieżkę 19° i punkt celowania
                double dInt = run.GlideInterceptD;
                Vector2 gi = S(-dInt, 0);
                if (In(gi)) { Line(gi + new Vector2(-9, 0), gi + new Vector2(9, 0), Red, 2.5f); Text(gi.x + 12, gi.y, "19°  " + (dInt / 1000).ToString("F1", Inv) + " km", Red, small, 100); }
                Vector2 aim = S(-2500, 0);
                if (In(aim)) { Circle(aim, 4, Red, 1.5f); }

                // ślad pisaka
                if (track.Count > 1)
                {
                    Vector2d pp = F(track[0].lat, track[0].lon, track[0].alt);
                    Vector2 a = S(pp.x, pp.y);
                    for (int i = 1; i < track.Count; i++)
                    {
                        Vector2d q = F(track[i].lat, track[i].lon, track[i].alt);
                        Vector2 bq = S(q.x, q.y);
                        if (In(a) || In(bq)) ClipLine(a, bq, Ink, 1.8f);
                        a = bq;
                    }
                }

                // samolot: czerwony znak w kierunku toru lotu
                Vector3d up = (v.CoMD - b.position).normalized;
                Vector3d vh = Vector3d.Exclude(up, (Vector3d)v.srf_velocity);
                if (vh.magnitude < 1) vh = Vector3d.Exclude(up, (Vector3d)v.transform.up);
                double ang = Math.Atan2(Vector3d.Dot(vh, rightH), Vector3d.Dot(vh, dirH));   // 0 = w kierunku lądowania (w górę)
                Vector2 pm = S(me.x, me.y);
                pm.x = Mathf.Clamp(pm.x, map.xMin + 6, map.xMax - 6); pm.y = Mathf.Clamp(pm.y, map.yMin + 6, map.yMax - 6);
                Vector2 f = new Vector2((float)Math.Sin(ang), -(float)Math.Cos(ang)), n = new Vector2(-f.y, f.x);
                Line(pm - f * 9, pm + f * 11, Red, 2.5f);
                Line(pm - n * 9 + f * 1, pm + n * 9 + f * 1, Red, 2.5f);
                Line(pm - f * 9 - n * 4, pm - f * 9 + n * 4, Red, 2f);
                // wektor prędkości na 60 s
                Vector2d ahead = me + new Vector2d(Vector3d.Dot(vh, dirH), Vector3d.Dot(vh, rightH)) * 60;
                ClipDashed(S(me.x, me.y), S(ahead.x, ahead.y), Red, 1.2f, 4, 4);
            }
            Box(map, FrameLight, 2f);

            // podziałka
            float sbar = (float)(gridM / mPerPx);
            Vector2 s0 = new Vector2(map.xMin + 10, map.yMax - 12);
            Line(s0, s0 + new Vector2(sbar, 0), Rwy, 2f);
            Text(s0.x + sbar + 4, s0.y, (gridM / 1000).ToString("F0", Inv) + " km", Rwy, small, 60);

            // dolna listwa: odczyty i przełączniki
            float by = map.yMax + 6;
            double d = double.NaN, y = double.NaN, h = double.NaN, l2, va, vc;
            if (run != null) run.RunwayFrame(out d, out y, out h, out l2, out va, out vc);
            string rd = double.IsNaN(d) ? "Д –" : "Д " + (d / 1000).ToString("F1", Inv) + " km";
            string rz = double.IsNaN(y) ? "Z –" : "Z " + (y >= 0 ? "П " : "Л ") + (Math.Abs(y) / 1000).ToString("F2", Inv) + " km";
            string rh = double.IsNaN(h) ? "Н –" : "Н " + h.ToString("F0", Inv) + " m";
            Text(body.x + 10, by + 8, rd, Green, lbl, 110);
            Text(body.x + 120, by + 8, rz, Green, lbl, 120);
            Text(body.x + 245, by + 8, rh, Green, lbl, 100);
            string ph = run != null ? run.ApproachInfo : "";
            if (ph.Length > 72) ph = ph.Substring(0, 72) + "…";
            Text(body.x + 10, by + 26, ph, Amber, small, MW);
            GUILayout.BeginArea(new Rect(body.x + 300, body.y + 4, 150, 22));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("−", btn, GUILayout.Width(22))) { scaleIdx = Math.Min(Scales.Length - 1, scaleIdx + 1); Save(); }
            if (GUILayout.Button("+", btn, GUILayout.Width(22))) { scaleIdx = Math.Max(0, scaleIdx - 1); Save(); }
            if (GUILayout.Button(centerOnPlane ? "САМ" : "ВПП", btn, GUILayout.Width(40))) { centerOnPlane = !centerOnPlane; Save(); }
            if (GUILayout.Button("СБР", btn, GUILayout.Width(40))) track.Clear();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private static double Heading(Vector3d dirH, Vector3d up, CelestialBody b)
        {
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
            return (Math.Atan2(Vector3d.Dot(dirH, east), Vector3d.Dot(dirH, north)) * 180 / Math.PI + 360) % 360;
        }

        // przycinanie odcinka do kartki (Liang–Barsky)
        private bool Clip(ref Vector2 a, ref Vector2 b)
        {
            float t0 = 0, t1 = 1, dx = b.x - a.x, dy = b.y - a.y;
            float[] p = { -dx, dx, -dy, dy }, q = { a.x - map.xMin, map.xMax - a.x, a.y - map.yMin, map.yMax - a.y };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Abs(p[i]) < 1e-6f) { if (q[i] < 0) return false; continue; }
                float t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
            Vector2 a0 = a; a = a0 + new Vector2(dx, dy) * t0; b = a0 + new Vector2(dx, dy) * t1;
            return true;
        }
        private void ClipLine(Vector2 a, Vector2 b, Color c, float w) { if (Clip(ref a, ref b)) Line(a, b, c, w); }
        private void ClipDashed(Vector2 a, Vector2 b, Color c, float w, float dash = 7, float gap = 5) { if (Clip(ref a, ref b)) Dashed(a, b, c, w, dash, gap); }
    }
}
