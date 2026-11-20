using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Lotniska do lądowania: z pliku PluginData/Lotniska.cfg (dokładne końce pasów) oraz automatycznie z Kerbal
    /// Konstructs (wszystkie miejsca startowe kategorii „Runway” / typu SPH na Ziemi). Wybrane lotnisko trafia do
    /// programów, które mają „site = wybrane” / „runway = wybrane”.
    /// </summary>
    public static class Airports
    {
        public class Airport
        {
            public string name, source;
            public double latA, lonA, latB, lonB, alt, length;
            public bool approxHeading;
            public double Lat { get { return 0.5 * (latA + latB); } }
            public double Lon { get { return 0.5 * (lonA + lonB); } }
            public double Heading
            {
                get
                {
                    double p1 = latA * Math.PI / 180, p2 = latB * Math.PI / 180, dl = (lonB - lonA) * Math.PI / 180;
                    double y = Math.Sin(dl) * Math.Cos(p2), x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
                    return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
                }
            }
        }

        public static readonly List<Airport> List = new List<Airport>();
        public static Airport Selected;
        public static string LastScan = "";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string Dir { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"); } }
        private static string FileList { get { return Path.Combine(Dir, "Lotniska.cfg"); } }
        private static string FileSel { get { return Path.Combine(Dir, "lotnisko_wybrane.cfg"); } }

        private static double D(string s, double def) { double v; return s != null && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v) ? v : def; }

        public static void Refresh()
        {
            List.Clear();
            int fromFile = 0, fromKK = 0;
            try
            {
                if (File.Exists(FileList))
                {
                    ConfigNode root = ConfigNode.Load(FileList);
                    foreach (ConfigNode n in root.GetNodes("AIRPORT"))
                    {
                        string[] r = (n.GetValue("runway") ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (r.Length < 5) continue;
                        Airport a = new Airport
                        {
                            name = n.GetValue("name") ?? "?", source = "plik",
                            latA = D(r[0], 0), lonA = D(r[1], 0), latB = D(r[2], 0), lonB = D(r[3], 0), alt = D(r[4], 0)
                        };
                        a.length = GreatCircleM(a.latA, a.lonA, a.latB, a.lonB);
                        List.Add(a); fromFile++;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] Lotniska.cfg: " + e.Message); }
            try { fromKK = ScanKK(); }
            catch (Exception e) { Debug.LogWarning("[RSSBalancer] Kerbal Konstructs: " + e); }
            LastScan = "lotnisk: " + List.Count + " (z pliku " + fromFile + ", z Kerbal Konstructs " + fromKK + ")";
            LoadSelected();
        }

        private static int ScanKK()
        {
            Type lsm = null;
            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                if (la.name == "KerbalKonstructs") { lsm = la.assembly.GetType("KerbalKonstructs.Core.LaunchSiteManager"); break; }
            if (lsm == null) return 0;
            FieldInfo allF = lsm.GetField("allLaunchSites", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            IEnumerable all = allF != null ? allF.GetValue(null) as IEnumerable : null;
            if (all == null) return 0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            int n = 0;
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (object ls in all)
            {
                if (ls == null) continue;
                Type t = ls.GetType();
                string name = Convert.ToString(t.GetField("LaunchSiteName", I)?.GetValue(ls));
                string type = Convert.ToString(t.GetField("LaunchSiteType", I)?.GetValue(ls));
                string cat = "";
                try { PropertyInfo pc = t.GetProperty("Category", I); cat = pc != null ? Convert.ToString(pc.GetValue(ls, null)) : Convert.ToString(t.GetField("sitecategory", I)?.GetValue(ls)); } catch { }
                bool runway = (cat ?? "").IndexOf("Runway", StringComparison.OrdinalIgnoreCase) >= 0 || (type ?? "").ToUpperInvariant() == "SPH";
                if (!runway || string.IsNullOrEmpty(name)) continue;
                CelestialBody body = t.GetField("body", I)?.GetValue(ls) as CelestialBody;
                if (body != null && home != null && body != home) continue;
                body = body ?? home;
                if (List.Exists(x => SameName(x.name, name))) continue;

                double lat = Convert.ToDouble(t.GetField("refLat", I)?.GetValue(ls) ?? 0.0);
                double lon = Convert.ToDouble(t.GetField("refLon", I)?.GetValue(ls) ?? 0.0);
                double alt = Convert.ToDouble(t.GetField("refAlt", I)?.GetValue(ls) ?? 0.0);
                double len = Convert.ToDouble(t.GetField("LaunchSiteLength", I)?.GetValue(ls) ?? 0.0);
                if (len < 500) len = 2500;
                double hdg = double.NaN; bool approx = true;

                // kierunek z punktu startowego (transform na końcu pasa, skierowany wzdłuż pasa)
                try
                {
                    object si = t.GetField("staticInstance", I)?.GetValue(ls);
                    string padPath = Convert.ToString(t.GetField("LaunchPadTransform", I)?.GetValue(ls));
                    GameObject go = si != null ? si.GetType().GetField("gameObject", I)?.GetValue(si) as GameObject : null;
                    Transform pad = go != null && !string.IsNullOrEmpty(padPath) ? (go.transform.Find(padPath) ?? FindDeep(go.transform, LastSegment(padPath))) : null;
                    if (pad != null && body != null)
                    {
                        Vector3d up = ((Vector3d)pad.position - body.position).normalized;
                        Vector3d north = Vector3d.Exclude(up, body.transform.up).normalized, east = Vector3d.Cross(up, north).normalized;
                        Vector3d f = Vector3d.Exclude(up, (Vector3d)pad.forward);
                        if (f.magnitude < 0.1) f = Vector3d.Exclude(up, (Vector3d)pad.up);
                        hdg = (Math.Atan2(Vector3d.Dot(f, east), Vector3d.Dot(f, north)) * 180 / Math.PI + 360) % 360;
                        lat = body.GetLatitude(pad.position); lon = body.GetLongitude(pad.position);
                        approx = false;
                    }
                    else if (si != null)
                    {
                        object ra = si.GetType().GetField("RotationAngle", I)?.GetValue(si);
                        if (ra != null) hdg = Convert.ToDouble(ra);
                    }
                }
                catch { }
                if (double.IsNaN(hdg)) hdg = 90;   // stockowy pas KSC: 09/27

                Airport a = new Airport { name = name, source = "Kerbal Konstructs" + (approx ? " (kierunek przybliżony)" : ""), latA = lat, lonA = lon, alt = alt, length = len, approxHeading = approx };
                Destination(lat, lon, hdg, len, body != null ? body.Radius : 6371000, out a.latB, out a.lonB);
                List.Add(a); n++;
            }
            return n;
        }

        private static bool SameName(string a, string b) { return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) || a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0; }
        private static string LastSegment(string p) { int i = p.LastIndexOf('/'); return i >= 0 ? p.Substring(i + 1) : p; }
        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { Transform r = FindDeep(c, name); if (r != null) return r; }
            return null;
        }

        public static double GreatCircleM(double la1, double lo1, double la2, double lo2)
        {
            double R = FlightGlobals.GetHomeBody() != null ? FlightGlobals.GetHomeBody().Radius : 6371000;
            double p1 = la1 * Math.PI / 180, p2 = la2 * Math.PI / 180, dl = (lo2 - lo1) * Math.PI / 180;
            double c = Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos(dl);
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) * R;
        }

        private static void Destination(double lat, double lon, double hdg, double dist, double R, out double lat2, out double lon2)
        {
            double d = dist / R, h = hdg * Math.PI / 180, p1 = lat * Math.PI / 180, l1 = lon * Math.PI / 180;
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(h));
            double l2 = l1 + Math.Atan2(Math.Sin(h) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
            lat2 = p2 * 180 / Math.PI; lon2 = ((l2 * 180 / Math.PI) + 540) % 360 - 180;
        }

        public static void Select(Airport a)
        {
            Selected = a;
            try
            {
                Directory.CreateDirectory(Dir);
                ConfigNode root = new ConfigNode(); root.AddValue("name", a != null ? a.name : "");
                root.Save(FileSel);
            }
            catch { }
        }

        private static void LoadSelected()
        {
            try
            {
                string want = File.Exists(FileSel) ? ConfigNode.Load(FileSel).GetValue("name") : null;
                Selected = List.Find(x => x.name == want) ?? (Selected != null ? List.Find(x => x.name == Selected.name) : null) ?? (List.Count > 0 ? List[0] : null);
            }
            catch { Selected = List.Count > 0 ? List[0] : null; }
        }
    }
}
