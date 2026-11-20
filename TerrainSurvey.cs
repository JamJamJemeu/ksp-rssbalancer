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

            foreach (MeshCollider mc in tmp) if (mc != null) UnityEngine.Object.Destroy(mc);

            string dir = Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"), "Logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "POMIAR_TERENU_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            return "Pomiar zapisany: " + Path.GetFileName(file) + " (obiektów " + inst.Count + ")";
        }
    }
}
