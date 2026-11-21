using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Pomiar terenu kosmodromu: dla obiektów Kerbal Konstructs (grupy Baikonur, BaikonurProton, Jubilejnyj) – wysokość
    /// obiektu względem terenu gry (PQS) pod nim; dla mapy-tekstury Katnisss (BAIKONURmap) – wysokość jej powierzchni nad
    /// terenem gry w siatce punktów i pod każdym obiektem. Wynik w PluginData/Logs/POMIAR_TERENU_*.txt.
    /// </summary>
    public static class TerrainSurvey
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] Groups = { "Baikonur", "BaikonurProton", "Jubilejnyj" };
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object F(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            FieldInfo f = t.GetField(name, I);
            if (f != null) return f.GetValue(o);
            PropertyInfo p = t.GetProperty(name, I);
            return p != null ? p.GetValue(o, null) : null;
        }

        private static double Terrain(CelestialBody b, double lat, double lon)
        {
            if (b.pqsController == null) return double.NaN;
            Vector3d rad = b.GetRelSurfaceNVector(lat, lon);
            return b.pqsController.GetSurfaceHeight(rad) - b.Radius;
        }

        private static string V(Vector3 v) { return v.x.ToString("F4", Inv) + "," + v.y.ToString("F4", Inv) + "," + v.z.ToString("F4", Inv); }

        // h = c0 + c1·a + c2·c + c3·r²  (najmniejsze kwadraty, równania normalne 4×4)
        private static double[] Fit(List<double[]> pts, bool quad)
        {
            int n = quad ? 4 : 3; double[,] M = new double[n, n + 1];
            foreach (double[] p in pts)
            {
                double[] f = quad ? new[] { 1, p[0], p[1], p[0] * p[0] + p[1] * p[1] } : new[] { 1, p[0], p[1] };
                for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) M[i, j] += f[i] * f[j]; M[i, n] += f[i] * p[2]; }
            }
            for (int i = 0; i < n; i++)
            {
                int piv = i; for (int r = i + 1; r < n; r++) if (Math.Abs(M[r, i]) > Math.Abs(M[piv, i])) piv = r;
                for (int k = 0; k <= n; k++) { double t = M[i, k]; M[i, k] = M[piv, k]; M[piv, k] = t; }
                for (int r = 0; r < n; r++) if (r != i) { double q = M[r, i] / M[i, i]; for (int k = 0; k <= n; k++) M[r, k] -= q * M[i, k]; }
            }
            double[] c = new double[4]; for (int i = 0; i < n; i++) c[i] = M[i, n] / M[i, i];
            return c;
        }

        private static double Eval(double[] c, double a, double cc) { return c[0] + c[1] * a + c[2] * cc + c[3] * (a * a + cc * cc); }

        private static void Leveling(StringBuilder sb, CelestialBody b, List<object> mapInst, List<object> inst, Func<double, double, double> MapAlt)
        {
            if (mapInst.Count == 0) return;
            GameObject go = F(mapInst[0], "gameObject") as GameObject;
            if (go == null) return;
            Transform t = go.transform;
            Vector3d piv = t.position;
            Vector3d up = (piv - b.position).normalized;
            Vector3d north = Vector3d.Exclude(up, b.transform.up).normalized;
            Vector3d east = Vector3d.Cross(up, north).normalized; // kierunek nieistotny dla dopasowania – tylko spójny
            double pLat = b.GetLatitude(piv), pLon = b.GetLongitude(piv);

            sb.AppendLine();
            sb.AppendLine("== POZIOMOWANIE MAPY");
            sb.AppendLine("transform mapy: rodzic " + (t.parent != null ? t.parent.name : "(brak)") + ", localEuler " + V(t.localEulerAngles) + ", localPos " + V(t.localPosition) +
                          ", skala " + V(t.localScale) + ", Orientation(KK) " + Convert.ToString(F(mapInst[0], "Orientation"), Inv));
            sb.AppendLine("środek mapy: szer " + pLat.ToString("F5", Inv) + " dł " + pLon.ToString("F5", Inv) + " wys " + b.GetAltitude(piv).ToString("F2", Inv));

            // gęsta siatka 1 km wokół środka, wysokości mapy i terenu
            List<double[]> pts = new List<double[]>();
            List<double[]> ter = new List<double[]>();
            for (int i = -26; i <= 26; i++)
                for (int j = -26; j <= 26; j++)
                {
                    Vector3d hp = piv + east * (i * 1000.0) + north * (j * 1000.0);
                    double lat = b.GetLatitude(hp), lon = b.GetLongitude(hp);
                    double m = MapAlt(lat, lon), tr = Terrain(b, lat, lon);
                    ter.Add(new[] { i * 1000.0, j * 1000.0, tr });
                    if (!double.IsNaN(m)) pts.Add(new[] { i * 1000.0, j * 1000.0, m, tr });
                }
            // dopasowanie odporne: odrzucamy krawędzie/wykopy (reszta > 2,5σ), kilka przebiegów
            List<double[]> use = new List<double[]>(pts);
            double[] c = null; double rms = 0;
            for (int it = 0; it < 6; it++)
            {
                c = Fit(use, true);
                double s = 0; foreach (double[] p in use) { double d = p[2] - Eval(c, p[0], p[1]); s += d * d; }
                rms = Math.Sqrt(s / Math.Max(1, use.Count));
                double lim = Math.Max(1.0, 2.5 * rms);
                List<double[]> nu = new List<double[]>();
                foreach (double[] p in pts) if (Math.Abs(p[2] - Eval(c, p[0], p[1])) < lim) nu.Add(p);
                if (nu.Count == use.Count) break; use = nu;
            }
            sb.AppendLine("punktów mapy " + pts.Count + ", w dopasowaniu " + use.Count + ", rms " + rms.ToString("F2", Inv) + " m");
            sb.AppendLine("h = " + c[0].ToString("F2", Inv) + " + " + c[1].ToString("E4", Inv) + "·E + " + c[2].ToString("E4", Inv) + "·N + " + c[3].ToString("E4", Inv) + "·r²   (krzywizna kuli dałaby " + (1.0 / (2 * b.Radius)).ToString("E4", Inv) + ")");
            double slope = Math.Sqrt(c[1] * c[1] + c[2] * c[2]);
            sb.AppendLine("pochylenie " + (slope * 180 / Math.PI).ToString("F4", Inv) + "°, w dół w kierunku " + ((Math.Atan2(-c[1], -c[2]) * 180 / Math.PI + 360) % 360).ToString("F1", Inv) + "° (azymut)");
            double minA = 1e9, maxA = -1e9, minC = 1e9, maxC = -1e9;
            foreach (double[] p in use) { minA = Math.Min(minA, p[0]); maxA = Math.Max(maxA, p[0]); minC = Math.Min(minC, p[1]); maxC = Math.Max(maxC, p[1]); }
            sb.AppendLine("zasięg płaskiej części mapy: E " + minA + "…" + maxA + " m, N " + minC + "…" + maxC + " m");

            // reszty w funkcji odległości od środka (czy mapa jest zakrzywiona)
            sb.AppendLine("reszty mapy wzgl. samej płaszczyzny (bez r²) wg odległości [km: średnia]:");
            double[] lc = Fit(use, false);
            StringBuilder rr = new StringBuilder();
            for (int k = 0; k < 26; k += 2)
            {
                double s = 0; int n = 0;
                foreach (double[] p in use) { double r = Math.Sqrt(p[0] * p[0] + p[1] * p[1]) / 1000; if (r >= k && r < k + 2) { s += p[2] - Eval(lc, p[0], p[1]); n++; } }
                if (n > 0) rr.Append(k + "-" + (k + 2) + ": " + (s / n).ToString("+0.0;-0.0", Inv) + " (" + n + ")  ");
            }
            sb.AppendLine(rr.ToString());

            // teren pod mapą
            double ts = 0, tmin = 1e9, tmax = -1e9; int tn = 0;
            foreach (double[] p in use) { ts += p[3]; tn++; tmin = Math.Min(tmin, p[3]); tmax = Math.Max(tmax, p[3]); }
            sb.AppendLine("teren gry pod płaską częścią mapy: średnio " + (ts / tn).ToString("F1", Inv) + " m, min " + tmin.ToString("F1", Inv) + ", max " + tmax.ToString("F1", Inv));

            // obrót poziomujący: normalna mapy (po liniowej części) → pion
            Vector3d grad = east * lc[1] + north * lc[2];
            Vector3d nrm = (up - grad).normalized;
            Quaternion corr = Quaternion.FromToRotation((Vector3)nrm, (Vector3)up);
            Quaternion newWorld = corr * t.rotation;
            Quaternion parentRot = t.parent != null ? t.parent.rotation : Quaternion.identity;
            Vector3 newLocal = (Quaternion.Inverse(parentRot) * newWorld).eulerAngles;
            sb.AppendLine("NOWA Orientation mapy (lokalnie względem rodzica): " + V(newLocal) + "   obecnie " + V(t.localEulerAngles));
            sb.AppendLine("wysokość płaszczyzny mapy w środku: " + lc[0].ToString("F2", Inv) + " m");

            // obiekty: o ile zmieni się mapa pod nimi po wypoziomowaniu (żeby zachować ich położenie względem mapy)
            sb.AppendLine("obiekty – zmiana wysokości mapy pod nimi po wypoziomowaniu (UUID; model; E; N; Δ [m]; teren gry; obiekt):");
            foreach (object si in inst)
            {
                GameObject og = F(si, "gameObject") as GameObject; if (og == null) continue;
                Vector3d v = (Vector3d)og.transform.position - piv;
                double a = Vector3d.Dot(v, east), cc = Vector3d.Dot(v, north);
                double d = -(lc[1] * a + lc[2] * cc);
                sb.AppendLine(Convert.ToString(F(si, "UUID")) + "; " + Convert.ToString(F(F(si, "model"), "name")) + "; " + a.ToString("F0", Inv) + "; " + cc.ToString("F0", Inv) + "; " +
                              d.ToString("+0.00;-0.00", Inv) + "; " + Terrain(b, b.GetLatitude(og.transform.position), b.GetLongitude(og.transform.position)).ToString("F2", Inv) + "; " + b.GetAltitude(og.transform.position).ToString("F2", Inv) +
                              "; grupa " + Convert.ToString(F(si, "Group")) + "; RelPos " + Convert.ToString(F(si, "RelativePosition"), Inv) + "; lokalnie " + V(og.transform.localPosition) + "; rodzic " + (og.transform.parent != null ? og.transform.parent.name : "-"));
            }
            // siatka terenu do ewentualnej nakładki (co 2 km)
            sb.AppendLine("teren gry wokół środka mapy co 2 km (E,N → wys):");
            StringBuilder tg = new StringBuilder();
            foreach (double[] p in ter) if (((int)(p[0] / 1000)) % 2 == 0 && ((int)(p[1] / 1000)) % 2 == 0) tg.Append(((int)(p[0] / 1000)) + "," + ((int)(p[1] / 1000)) + ":" + p[2].ToString("F0", Inv) + " ");
            sb.AppendLine(tg.ToString());
        }

        private static void Decals(StringBuilder sb, CelestialBody b)
        {
            sb.AppendLine();
            sb.AppendLine("== NAKŁADKI TERENU (MapDecal) na " + b.bodyName + " (nazwa wewn. " + b.name + ")");
            if (b.pqsController == null) return;
            foreach (PQSMod m in b.pqsController.GetComponentsInChildren<PQSMod>(true))
            {
                if (!(m.GetType().Name.Contains("Decal"))) continue;
                StringBuilder s = new StringBuilder(m.GetType().Name + " '" + m.name + "' aktywny " + m.modEnabled + " order " + m.order);
                foreach (string fn in new[] { "position", "radius", "angle", "absolute", "absoluteOffset", "heightMapDeformity", "smoothHeight", "smoothColour", "cullBlack", "removeScatter", "heightMap", "colorMap" })
                {
                    object val = F(m, fn); if (val == null) continue;
                    if (val is Vector3) val = V((Vector3)val);
                    if (val is UnityEngine.Object) val = ((UnityEngine.Object)val).name;
                    s.Append(", " + fn + "=" + Convert.ToString(val, Inv));
                }
                object pos = F(m, "position");
                if (pos is Vector3)
                {
                    Vector3d dir = ((Vector3d)(Vector3)pos).normalized;
                    double lat = Math.Asin(dir.y) * 180 / Math.PI, lon = Math.Atan2(dir.z, dir.x) * 180 / Math.PI;
                    s.Append("  (kierunek ~ szer " + lat.ToString("F3", Inv) + ")");
                }
                sb.AppendLine(s.ToString());
            }
        }

        public static string Run()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "Brak statku.";
            CelestialBody b = v.mainBody;
            Type db = null;
            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                if (la.name == "KerbalKonstructs") { db = la.assembly.GetType("KerbalKonstructs.Core.StaticDatabase"); break; }
            if (db == null) return "Nie znaleziono Kerbal Konstructs.";
            IEnumerable all = null;
            foreach (string n in new[] { "allStaticInstances", "_allStaticInstances" })
            {
                FieldInfo fi = db.GetField(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                all = fi != null ? fi.GetValue(null) as IEnumerable : null;
                if (all != null) break;
            }
            if (all == null) return "Kerbal Konstructs: brak listy obiektów.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("POMIAR TERENU – " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", ciało " + b.bodyName);
            sb.AppendLine("Statek: " + v.vesselName + "  szer. " + v.latitude.ToString("F5", Inv) + "  dł. " + v.longitude.ToString("F5", Inv) +
                          "  wys. " + v.altitude.ToString("F2", Inv) + " m  teren gry (PQS) " + Terrain(b, v.latitude, v.longitude).ToString("F2", Inv) +
                          " m  nad terenem (radar) " + v.heightFromTerrain.ToString("F2", Inv) + " m  sytuacja " + v.situation);
            sb.AppendLine();

            // grupy
            sb.AppendLine("== ŚRODKI GRUP");
            List<object> mapInst = new List<object>();
            List<object> inst = new List<object>();
            HashSet<object> centers = new HashSet<object>();
            foreach (object si in all)
            {
                string g = Convert.ToString(F(si, "Group"));
                if (Array.IndexOf(Groups, g) < 0) continue;
                inst.Add(si);
                object gc = F(si, "groupCenter"); if (gc != null) centers.Add(gc);
                object model = F(si, "model");
                if (Convert.ToString(F(model, "name")) == "BAIKONURmap") mapInst.Add(si);
            }
            foreach (object gc in centers)
            {
                GameObject go = F(gc, "gameObject") as GameObject;
                string name = Convert.ToString(F(gc, "Group"));
                if (go == null) { sb.AppendLine(name + ": brak obiektu"); continue; }
                Vector3d p = go.transform.position;
                double lat = b.GetLatitude(p), lon = b.GetLongitude(p), alt = b.GetAltitude(p);
                sb.AppendLine(name + ": szer. " + lat.ToString("F5", Inv) + " dł. " + lon.ToString("F5", Inv) + "  wys. środka " + alt.ToString("F2", Inv) +
                              " m  teren gry " + Terrain(b, lat, lon).ToString("F2", Inv) + " m  RadiusOffset " + Convert.ToString(F(gc, "RadiusOffset"), Inv) +
                              "  SeaLevelAsReference " + Convert.ToString(F(gc, "SeaLevelAsReference")));
            }
            sb.AppendLine();

            // kolidery na mapie Katnisss (tymczasowe – tylko do pomiaru)
            List<MeshCollider> tmp = new List<MeshCollider>();
            HashSet<Collider> mapCols = new HashSet<Collider>();
            Bounds mapBounds = new Bounds(); bool haveBounds = false;
            foreach (object mi in mapInst)
            {
                GameObject go = F(mi, "gameObject") as GameObject;
                if (go == null) continue;
                foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    try
                    {
                        MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                        tmp.Add(mc); mapCols.Add(mc);
                    }
                    catch (Exception e) { sb.AppendLine("mapa: nie da się dodać kolidera (" + e.Message + ")"); }
                }
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!haveBounds) { mapBounds = r.bounds; haveBounds = true; } else mapBounds.Encapsulate(r.bounds);
                }
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) mapCols.Add(c);
                sb.AppendLine("Mapa BAIKONURmap: aktywna " + go.activeInHierarchy + ", siatek " + go.GetComponentsInChildren<MeshFilter>(true).Length + ", kolidery " + mapCols.Count);
            }
            if (mapInst.Count == 0) sb.AppendLine("Mapa BAIKONURmap: nie znaleziono w bazie KK.");
            Physics.SyncTransforms();

            Func<double, double, double> MapAlt = (lat, lon) =>
            {
                if (mapCols.Count == 0) return double.NaN;
                Vector3d top = b.GetWorldSurfacePosition(lat, lon, Terrain(b, lat, lon) + 1500);
                Vector3d down = (b.position - top).normalized;
                RaycastHit[] hits = Physics.RaycastAll(top, down, 4000f, ~0, QueryTriggerInteraction.Collide);
                double best = double.NaN;
                foreach (RaycastHit h in hits)
                    if (mapCols.Contains(h.collider))
                    {
                        double a = b.GetAltitude(h.point);
                        if (double.IsNaN(best) || a > best) best = a;
                    }
                return best;
            };

            // obiekty
            sb.AppendLine();
            sb.AppendLine("== OBIEKTY  (grupa; model; nazwa startu; szer; dł; wys. obiektu [m]; teren gry pod nim; obiekt − teren; mapa pod nim; obiekt − mapa)");
            foreach (object si in inst)
            {
                GameObject go = F(si, "gameObject") as GameObject;
                string g = Convert.ToString(F(si, "Group")), model = Convert.ToString(F(F(si, "model"), "name"));
                object ls = F(si, "launchSite");
                string lsName = ls != null ? Convert.ToString(F(ls, "LaunchSiteName")) : "";
                if (go == null) { sb.AppendLine(g + "; " + model + "; " + lsName + "; (nie wczytany)"); continue; }
                Vector3d p = go.transform.position;
                double lat = b.GetLatitude(p), lon = b.GetLongitude(p), alt = b.GetAltitude(p), ter = Terrain(b, lat, lon), map = MapAlt(lat, lon);
                sb.AppendLine(g + "; " + model + "; " + lsName + "; " + lat.ToString("F5", Inv) + "; " + lon.ToString("F5", Inv) + "; " + alt.ToString("F2", Inv) + "; " +
                              ter.ToString("F2", Inv) + "; " + (alt - ter).ToString("+0.00;-0.00", Inv) + "; " + (double.IsNaN(map) ? "—" : map.ToString("F2", Inv)) + "; " +
                              (double.IsNaN(map) ? "—" : (alt - map).ToString("+0.00;-0.00", Inv)) + "; aktywny " + go.activeInHierarchy);
            }

            // siatka punktów na mapie
            if (haveBounds && mapCols.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== SIATKA MAPY  (szer; dł; teren gry [m]; mapa [m]; mapa − teren)");
                Vector3 c = mapBounds.center, e = mapBounds.extents;
                int n = 13; int hitsCount = 0; double sum = 0, mn = double.MaxValue, mx = double.MinValue;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        for (int k = 0; k < 1; k++)
                        {
                            Vector3 wp = c + new Vector3(e.x * (2f * i / (n - 1) - 1f), 0, e.z * (2f * j / (n - 1) - 1f));
                            double lat = b.GetLatitude(wp), lon = b.GetLongitude(wp);
                            double ter = Terrain(b, lat, lon), map = MapAlt(lat, lon);
                            if (double.IsNaN(map)) continue;
                            hitsCount++; double d = map - ter; sum += d; mn = Math.Min(mn, d); mx = Math.Max(mx, d);
                            sb.AppendLine(lat.ToString("F4", Inv) + "; " + lon.ToString("F4", Inv) + "; " + ter.ToString("F1", Inv) + "; " + map.ToString("F1", Inv) + "; " + d.ToString("+0.0;-0.0", Inv));
                        }
                sb.AppendLine("punktów na mapie " + hitsCount + (hitsCount > 0 ? ", mapa − teren: średnio " + (sum / hitsCount).ToString("F1", Inv) + " m, min " + mn.ToString("F1", Inv) + ", max " + mx.ToString("F1", Inv) : ""));
            }

            try { Leveling(sb, b, mapInst, inst, MapAlt); } catch (Exception ex) { sb.AppendLine("Poziomowanie: błąd " + ex); }
            try { Decals(sb, b); } catch (Exception ex) { sb.AppendLine("Nakładki terenu: błąd " + ex.Message); }

            foreach (MeshCollider mc in tmp) if (mc != null) UnityEngine.Object.Destroy(mc);

            string dir = Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"), "Logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "POMIAR_TERENU_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            return "Pomiar zapisany: " + Path.GetFileName(file) + " (obiektów " + inst.Count + ")";
        }
    }
}
