using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Wyświetlacz kabinowy Burana (kineskop, CRT) – na wzór trzech ekranów lądowania z kabiny Burana:
    ///  1) wejście 100–20 km: kąt natarcia (z zadanym), Mach, przechylenie (z zadanym), paliwo, odległość i zasięg;
    ///  2) podejście 20–4 km: kąt natarcia, prędkość przyrządowa, wysokość nad pasem, przechylenie,
    ///     przewidywana trajektoria w widoku z góry, odchylenie od osi pasa;
    ///  3) lądowanie (poniżej 4 km): kąt natarcia, prędkość, profil ścieżki z przewidywanym i nominalnym punktem
    ///     przyziemienia, prędkość pionowa, odchylenie od osi pasa.
    /// Rysunek własny (zielone linie na czarnym tle), dane z lotu i z programu lądowania.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BuranCrt : MonoBehaviour
    {
        private static bool show, loaded;
        private static Rect win = new Rect(80, 120, 440, 400);
        private static string CfgFile { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "crt.cfg")); } }
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

        private static readonly Color G = new Color(0.25f, 1f, 0.35f), GD = new Color(0.12f, 0.5f, 0.17f), Y = new Color(1f, 0.9f, 0.2f), R = new Color(1f, 0.3f, 0.25f), W = new Color(0.85f, 1f, 0.85f);
        private Texture2D px;
        private GUIStyle lbl, lblR, lblC, big;
        private const float PW = 420, PH = 360;

        private void OnDestroy() { Save(); }

        private void OnGUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || !Show || FlightGlobals.ActiveVessel == null) return;
            if (px == null)
            {
                px = new Texture2D(1, 1); px.SetPixel(0, 0, Color.white); px.Apply();
                lbl = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, padding = new RectOffset(0, 0, 0, 0) };
                lblR = new GUIStyle(lbl) { alignment = TextAnchor.MiddleRight };
                lblC = new GUIStyle(lbl) { alignment = TextAnchor.MiddleCenter };
                big = new GUIStyle(lbl) { fontSize = 16 };
            }
            Rect r = GUI.Window(0x42D4A1, win, DrawWindow, "Buran – wyświetlacz (CRT)");
            if (r.x != win.x || r.y != win.y) win = r;
        }

        // ------------------------------------------------------------------ rysowanie

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
        private void Box(Rect r, Color c, float w = 1.2f)
        {
            Line(new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), c, w); Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), c, w);
            Line(new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), c, w); Line(new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin), c, w);
        }
        private void Fill(Rect r, Color c) { Color oc = GUI.color; GUI.color = c; GUI.DrawTexture(r, px); GUI.color = oc; }
        private void Text(float x, float y, string t, Color c, GUIStyle st = null, float w = 120)
        {
            st = st ?? lbl; st.normal.textColor = c; GUI.Label(new Rect(x, y - 8, w, 16), t, st);
        }
        private void Tri(Vector2 tip, float dir, Color c, float s = 7)   // dir: 0 = w prawo, 180 = w lewo
        {
            float a = dir * Mathf.Deg2Rad;
            Vector2 f = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), n = new Vector2(-f.y, f.x);
            Vector2 b1 = tip - f * s + n * s * 0.6f, b2 = tip - f * s - n * s * 0.6f;
            Line(tip, b1, c); Line(b1, b2, c); Line(b2, tip, c);
        }

        /// <summary>Pionowa taśma wartości z bieżącą wartością (trójkąt) i zadaną (kreska).</summary>
        private void Tape(Rect r, string title, double val, double cmd, double min, double max, double step, string fmt)
        {
            Box(r, GD);
            Text(r.x - 6, r.y - 10, title, G, lblC, r.width + 12);
            for (double t = Math.Ceiling(min / step) * step; t <= max + 1e-6; t += step)
            {
                float yy = (float)(r.yMax - (t - min) / (max - min) * r.height);
                Line(new Vector2(r.xMax - 6, yy), new Vector2(r.xMax, yy), GD, 1f);
                Text(r.x + 2, yy, t.ToString(fmt, Inv), GD, lbl, r.width - 8);
            }
            if (!double.IsNaN(cmd))
            {
                float yc = (float)(r.yMax - (Clamp(cmd, min, max) - min) / (max - min) * r.height);
                Line(new Vector2(r.xMax + 1, yc), new Vector2(r.xMax + 12, yc), Y, 2.5f);
            }
            if (!double.IsNaN(val))
            {
                float yv = (float)(r.yMax - (Clamp(val, min, max) - min) / (max - min) * r.height);
                Tri(new Vector2(r.xMax + 1, yv), 180, G, 8);
                Text(r.xMax + 12, yv, val.ToString(fmt, Inv), W, lbl, 50);
            }
        }

        /// <summary>Wskaźnik przechylenia: łuk ±90°, sylwetka skrzydeł, zadane przechylenie żółtą kreską.</summary>
        private void BankDial(Vector2 c, float rad, double bank, double cmd)
        {
            for (int k = -90; k <= 90; k += 15)
            {
                float a = (k - 90) * Mathf.Deg2Rad;
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad, q = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (rad - (k % 30 == 0 ? 9 : 5));
                Line(p, q, GD, 1.2f);
            }
            if (!double.IsNaN(cmd))
            {
                float a = (float)((Clamp(cmd, -90, 90) - 90) * Mathf.Deg2Rad);
                Line(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (rad + 2), c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (rad + 12), Y, 3f);
            }
            if (!double.IsNaN(bank))
            {
                float a = (float)(bank * Mathf.Deg2Rad);
                Vector2 wv = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Line(c - wv * (rad * 0.75f), c + wv * (rad * 0.75f), G, 2.5f);
                Line(c, c + new Vector2(-wv.y, wv.x) * (-rad * 0.25f), G, 2f);
                float ap = (float)((bank - 90) * Mathf.Deg2Rad);
                Tri(c + new Vector2(Mathf.Cos(ap), Mathf.Sin(ap)) * (rad - 10), (float)(bank - 90), G, 7);
            }
            Text(c.x - 40, c.y + 18, "PRZECH. " + (double.IsNaN(bank) ? "—" : bank.ToString("+0;-0;0", Inv) + "°"), W, lblC, 80);
        }

        private static double Clamp(double v, double a, double b) { return v < a ? a : v > b ? b : v; }

        // ------------------------------------------------------------------ dane lotu

        private struct Data
        {
            public double alt, V, M, aoa, bank, eas, vs, vh, distKm, fuelPct;
            public bool rwy; public double d, y, h, len, vAlong, vCross;
        }

        private static Data Read(Vessel v, FlightProgramRunner run)
        {
            Data x = new Data();
            x.alt = v.altitude; x.V = v.srfSpeed; x.M = v.mach; x.vs = v.verticalSpeed; x.vh = v.horizontalSrfSpeed;
            x.eas = v.srfSpeed * Math.Sqrt(Math.Max(0, v.atmDensity) / 1.225);
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            Vector3d nose = v.ReferenceTransform.up, belly = v.ReferenceTransform.forward, wing = v.ReferenceTransform.right;
            Vector3d vel = v.srf_velocity;
            x.aoa = vel.magnitude > 5 ? Math.Atan2(Vector3d.Dot(vel, belly), Vector3d.Dot(vel, nose)) * 180 / Math.PI : double.NaN;
            // przechylenie: kąt skrzydeł względem horyzontu (dodatnie = prawe skrzydło w dół)
            x.bank = Math.Asin(Math.Max(-1, Math.Min(1, -Vector3d.Dot(wing, up)))) * 180 / Math.PI;
            if (Vector3d.Dot(-belly, up) < 0) x.bank = (x.bank >= 0 ? 180 : -180) - x.bank;   // na plecach
            x.distKm = double.NaN;
            if (run != null && run.SiteKnown)
            {
                double p1 = v.latitude * Math.PI / 180, p2 = run.SiteLatD * Math.PI / 180, dl = (run.SiteLonD - v.longitude) * Math.PI / 180;
                x.distKm = Math.Acos(Math.Max(-1, Math.Min(1, Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos(dl)))) * v.mainBody.Radius / 1000;
            }
            if (run != null) x.rwy = run.RunwayFrame(out x.d, out x.y, out x.h, out x.len, out x.vAlong, out x.vCross);
            // paliwo ОДУ: masa paliw z gęstością (bez EC) – procent pełnych zbiorników
            double m = 0, mMax = 0;
            foreach (Part p in v.parts)
                foreach (PartResource pr in p.Resources)
                {
                    if (pr.info == null || pr.info.density <= 0 || pr.resourceName == "ElectricCharge" || pr.resourceName == "Ablator") continue;
                    m += pr.amount * pr.info.density; mMax += pr.maxAmount * pr.info.density;
                }
            x.fuelPct = mMax > 0 ? 100 * m / mMax : double.NaN;
            return x;
        }

        // ------------------------------------------------------------------ okno

        private void DrawWindow(int id)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            FlightProgramRunner run = FlightProgramRunner.Instance;
            Rect area = new Rect(10, 24, PW, PH);
            Fill(area, new Color(0.01f, 0.04f, 0.02f, 0.97f));
            Box(area, GD);
            Data x = Read(v, run);
            float ox = area.x, oy = area.y;
            int mode = x.alt > 20000 ? 1 : (x.rwy && x.h < 4000 && x.d < 30000) ? 3 : 2;
            if (v.Landed) mode = 3;
            string title = mode == 1 ? "ZEJŚCIE  100–20 km" : mode == 2 ? "PODEJŚCIE  20–4 km" : "LĄDOWANIE";
            Text(ox + 8, oy + 12, title, G, big, 220);
            Text(ox + PW - 128, oy + 12, "H " + (x.alt / 1000).ToString(x.alt < 10000 ? "F2" : "F1", Inv) + " km", W, lblR, 120);
            if (mode == 1) Mode1(x, run, ox, oy);
            else if (mode == 2) Mode2(x, run, ox, oy);
            else Mode3(x, run, ox, oy);
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void Mode1(Data x, FlightProgramRunner run, float ox, float oy)
        {
            Tape(new Rect(ox + 22, oy + 50, 40, 250), "α °", x.aoa, run != null ? run.AoaCommand : double.NaN, 0, 45, 5, "0");
            BankDial(new Vector2(ox + 200, oy + 150), 80, x.bank, run != null && run.EntryActive ? run.BankCommand : double.NaN);
            float tx = ox + 300, ty = oy + 56;
            Text(tx, ty, "V  " + x.V.ToString("F0", Inv) + " m/s", W, lbl, 115); ty += 20;
            Text(tx, ty, "M  " + x.M.ToString("F1", Inv), W, lbl, 115); ty += 20;
            Text(tx, ty, "Vy " + x.vs.ToString("+0;-0;0", Inv) + " m/s", W, lbl, 115); ty += 20;
            Text(tx, ty, "PALIWO " + (double.IsNaN(x.fuelPct) ? "—" : x.fuelPct.ToString("F0", Inv) + " %"), W, lbl, 115); ty += 20;
            // odległość i zasięg
            float by = oy + 268;
            Text(ox + 90, by - 22, "DO LOTNISKA " + (double.IsNaN(x.distKm) ? "—" : x.distKm.ToString("F0", Inv) + " km"), W, lbl, 200);
            double rng = run != null ? run.EntryRangeM / 1000 : double.NaN, cos = run != null ? run.EntryCos : double.NaN;
            Rect bar = new Rect(ox + 90, by - 6, 310, 14);
            Box(bar, GD);
            if (!double.IsNaN(x.distKm) && !double.IsNaN(rng) && run.EntryActive)
            {
                double scale = Math.Max(x.distKm, rng + 100) * 1.05;
                float pd = (float)(x.distKm / scale * bar.width), pr = (float)((rng + 100) / scale * bar.width);
                Color c = cos > 1e-3 && 1 / cos >= 1.1 ? G : cos > 1e-3 && 1 / cos >= 0.95 ? Y : R;
                Fill(new Rect(bar.x, bar.y + 3, pr, bar.height - 6), c * new Color(1, 1, 1, 0.55f));
                Line(new Vector2(bar.x + pd, bar.y - 4), new Vector2(bar.x + pd, bar.yMax + 4), W, 2.5f);
                Text(ox + 90, by + 18, "zasięg ~" + (rng + 100).ToString("F0", Inv) + " km  (pasek)   lotnisko │", c, lbl, 310);
            }
            else Text(ox + 90, by + 18, "zasięg – po wejściu w atmosferę", GD, lbl, 310);
            Text(ox + 90, by + 40, (run != null && run.EntryActive ? "prowadzenie wejścia: α i przechylenie zadane – żółte" : "prowadzenie wejścia nieaktywne"), GD, lbl, 320);
        }

        private void Mode2(Data x, FlightProgramRunner run, float ox, float oy)
        {
            double ias = x.eas * 3.6, vst = run != null ? run.StallEas * 3.6 : 252;
            Tape(new Rect(ox + 22, oy + 50, 34, 250), "α °", x.aoa, double.NaN, -5, 20, 5, "0");
            Tape(new Rect(ox + 92, oy + 50, 40, 250), "Vprz", ias, double.NaN, 200, 700, 100, "0");
            // pasmo przeciągnięcia na taśmie prędkości
            float yst = (float)(oy + 50 + 250 - (Clamp(vst, 200, 700) - 200) / 500 * 250);
            Fill(new Rect(ox + 92, yst, 6, oy + 300 - yst), R * new Color(1, 1, 1, 0.7f));
            // widok z góry: pas, oś, Buran, przewidywana trajektoria 60 s
            Rect map = new Rect(ox + 190, oy + 40, 220, 220);
            Box(map, GD);
            if (x.rwy)
            {
                double span = Math.Max(10000, Math.Min(60000, Math.Max(Math.Abs(x.d), Math.Abs(x.y)) * 2.4));
                Func<double, double, Vector2> P = (dd, yy) => new Vector2((float)(map.center.x + yy / span * map.width), (float)(map.center.y + dd / span * map.height * 0.9 - map.height * 0.15));
                // przedłużona oś pasa i pas
                Line(P(30000, 0), P(-x.len, 0), GD, 1f);
                Vector2 t0 = P(0, -60), t1 = P(-x.len, 60);
                Box(new Rect(Mathf.Min(t0.x, t1.x) - 1, Mathf.Min(t0.y, t1.y), Mathf.Abs(t1.x - t0.x) + 2, Mathf.Abs(t1.y - t0.y)), W, 1.5f);
                // przewidywana trajektoria (zakręt z bieżącym przechyleniem)
                double hdg0 = Math.Atan2(x.vCross, x.vAlong), hdg = hdg0;     // 0 = wzdłuż kierunku lądowania (w górę ekranu)
                double rate = Math.Abs(x.vh) > 20 ? 9.81 * Math.Tan(Clamp(x.bank, -60, 60) * Math.PI / 180) / x.vh : 0;
                double pd = x.d, py = x.y;
                Vector2 prev = P(pd, py);
                for (int k = 1; k <= 30; k++)
                {
                    double dt = 2; hdg += rate * dt;
                    pd -= Math.Cos(hdg) * x.vh * dt; py += Math.Sin(hdg) * x.vh * dt;
                    Vector2 cur = P(pd, py); Line(prev, cur, Y, 1.5f); prev = cur;
                }
                Vector2 me = P(x.d, x.y);
                Tri(me + new Vector2((float)Math.Sin(hdg0), -(float)Math.Cos(hdg0)) * 7, (float)(hdg0 * 180 / Math.PI) - 90, G, 9);
                Text(map.x + 4, map.yMax - 10, "skala " + (span / 1000).ToString("F0", Inv) + " km", GD, lbl, 120);
            }
            else Text(map.x + 10, map.center.y, "brak pasa w programie", GD, lbl, 200);
            float tx = ox + 190, ty = oy + 282;
            Text(tx, ty, "H nad pasem " + (x.rwy ? x.h.ToString("F0", Inv) + " m" : "—"), W, lbl, 220); ty += 18;
            Text(tx, ty, "do progu " + (x.rwy ? (x.d / 1000).ToString("F1", Inv) + " km" : "—") + "   w bok " + (x.rwy ? Side(x.y) : "—"), W, lbl, 230); ty += 18;
            Text(tx, ty, "przechylenie " + x.bank.ToString("+0;-0;0", Inv) + "°   Vy " + x.vs.ToString("+0;-0;0", Inv) + " m/s", W, lbl, 230);
            Text(ox + 14, oy + 322, run != null && run.ApproachActive ? run.ApproachPhase : "", GD, lbl, 400);
        }

        private string Side(double y) { return Math.Abs(y) < 1000 ? Math.Abs(y).ToString("F0", Inv) + " m " + (y >= 0 ? "P" : "L") : (Math.Abs(y) / 1000).ToString("F1", Inv) + " km " + (y >= 0 ? "P" : "L"); }

        private void Mode3(Data x, FlightProgramRunner run, float ox, float oy)
        {
            double ias = x.eas * 3.6;
            Tape(new Rect(ox + 22, oy + 50, 34, 200), "α °", x.aoa, double.NaN, -5, 20, 5, "0");
            Tape(new Rect(ox + 92, oy + 50, 40, 200), "Vprz", ias, double.NaN, 200, 600, 100, "0");
            // profil: x = odległość od progu (−2..12 km), y = wysokość (0..4 km)
            Rect pr = new Rect(ox + 180, oy + 40, 230, 170);
            Box(pr, GD);
            double xmin = -2000, xmax = 12000, hmax = 4000;
            Func<double, double, Vector2> P = (dd, hh) => new Vector2((float)(pr.xMax - (dd - xmin) / (xmax - xmin) * pr.width), (float)(pr.yMax - Clamp(hh, 0, hmax) / hmax * pr.height));
            Line(P(xmax, 0), P(xmin, 0), GD, 1f);
            // pas na profilu
            Line(P(0, 0), P(Math.Max(xmin, -(double.IsNaN(x.len) ? 4500 : x.len)), 0), W, 3f);
            // nominalna ścieżka: 19° do punktu 2,5 km przed progiem, od ~450 m łagodna 1,5° z przyziemieniem 600 m za progiem
            double t19 = Math.Tan(19 * Math.PI / 180), t15 = Math.Tan(1.5 * Math.PI / 180);
            Vector2 a1 = P(2500 + 4000 / t19, 4000), a2 = P(2500 + 450 / t19, 450), a3 = P(-600, 0);
            Line(a1, a2, GD, 1.5f); Line(a2, a3, GD, 1.5f);
            Tri(P(-600, 0) + new Vector2(0, -3), 90, Y, 6);   // nominalny punkt przyziemienia
            if (x.rwy)
            {
                // przewidywany punkt przyziemienia: przedłużenie bieżącego toru lotu
                double gam = x.vh > 5 ? -x.vs / x.vh : 0;
                double tdx = gam > 0.002 ? x.d - x.h / gam : double.NaN;
                Vector2 me = P(x.d, x.h);
                if (!double.IsNaN(tdx)) { Line(me, P(Clamp(tdx, xmin, xmax), tdx < xmin || tdx > xmax ? Math.Max(0, x.h - (x.d - Clamp(tdx, xmin, xmax)) * gam) : 0), G, 1f); Tri(P(Clamp(tdx, xmin, xmax), 0) + new Vector2(0, 3), -90, G, 6); }
                Fill(new Rect(me.x - 4, me.y - 4, 8, 8), G);
                Text(pr.x + 4, pr.y + 10, "przyziemienie: przewid. " + (double.IsNaN(tdx) ? "—" : (-tdx).ToString("F0", Inv) + " m") + "  nom. 600 m za progiem", W, lbl, 240);
            }
            // odchylenie od osi pasa – skala ±300 m
            Rect lat = new Rect(ox + 180, oy + 232, 230, 18);
            Box(lat, GD);
            Line(new Vector2(lat.center.x, lat.y - 4), new Vector2(lat.center.x, lat.yMax + 4), W, 1.5f);
            for (int k = -2; k <= 2; k++) if (k != 0) Line(new Vector2(lat.center.x + k * lat.width / 4, lat.y + 4), new Vector2(lat.center.x + k * lat.width / 4, lat.yMax - 4), GD, 1f);
            if (x.rwy)
            {
                float lx = lat.center.x + (float)(Clamp(x.y, -300, 300) / 300 * lat.width / 2);
                Vector2 c = new Vector2(lx, lat.center.y);
                Line(c + new Vector2(0, -7), c + new Vector2(7, 0), G, 2f); Line(c + new Vector2(7, 0), c + new Vector2(0, 7), G, 2f);
                Line(c + new Vector2(0, 7), c + new Vector2(-7, 0), G, 2f); Line(c + new Vector2(-7, 0), c + new Vector2(0, -7), G, 2f);
            }
            Text(lat.x, lat.yMax + 10, "oś pasa ±300 m   w bok " + (x.rwy ? Side(x.y) : "—"), W, lbl, 240);
            float ty = oy + 290;
            Text(ox + 22, ty, "Vy " + x.vs.ToString("+0.0;-0.0;0.0", Inv) + " m/s", Math.Abs(x.vs) > 25 || (x.rwy && x.h < 30 && x.vs < -3) ? R : W, big, 150);
            Text(ox + 22, ty + 22, "przechylenie " + x.bank.ToString("+0;-0;0", Inv) + "°", W, lbl, 150);
            Text(ox + 180, ty + 22, "H nad pasem " + (x.rwy ? x.h.ToString("F0", Inv) + " m" : "—") + "   do progu " + (x.rwy ? (x.d / 1000).ToString("F2", Inv) + " km" : "—"), W, lbl, 240);
            Text(ox + 14, oy + 336, run != null && run.ApproachActive ? run.ApproachPhase : "", GD, lbl, 400);
        }
    }
}
