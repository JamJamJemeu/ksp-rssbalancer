using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Stałe statku do wejścia w atmosferę i podejścia (1.41.3). Algorytmy są wspólne (równowagowy lot ślizgowy przy
    /// wejściu, okrąg HAC, stroma ścieżka) – zmieniają się tylko liczby, które opisują dany statek. Pliki:
    /// GameData/RSSBalancer/PluginData/Vehicles/*.cfg (węzeł VEHICLE), program lotu wybiera „vehicle = nazwa”.
    /// Bez „vehicle” albo przy brakującym kluczu – wartości Burana (te same, co wcześniej były w kodzie).
    /// Tabele: pary „x:y” rozdzielone spacjami, rosnąco po x.
    /// </summary>
    public class VehicleProfile
    {
        public string name = "Buran (wbudowany)";
        public double stallEas = 70, stallMass = 75;                // prędkość przeciągnięcia EAS [m/s] przy masie [t]
        public double[] aoaX = { 2, 5, 12 }, aoaY = { 10, 22, 39 };  // kąt natarcia przy wejściu od Macha [°]
        public double[] entLdX = { 0, 320, 580, 900, 1240, 1650, 2600, 3000, 3400, 3900, 4900, 5700, 6500, 7600 };   // prędkość [m/s]
        public double[] entLdY = { 1.4, 1.42, 1.73, 1.67, 1.54, 1.52, 1.55, 1.45, 1.15, 1.0, 1.0, 0.9, 0.82, 0.78 }; // doskonałość przy wejściu
        public double[] glideX = { 70, 90, 130, 170, 220 }, glideY = { 4.6, 5.2, 3.8, 3.0, 2.3 };                    // EAS → doskonałość (poddźwiękowo)
        public double[] machX = { 0.5, 0.55, 0.6, 0.9, 0.95, 1.05, 1.3 }, machY = { 1.0, 0.97, 0.75, 0.7, 0.5, 0.4, 0.4 }; // mnożnik od Macha
        public double steepDeg = 19, steepAim = 2500;               // stroma ścieżka [°], punkt celowania przed progiem [m]
        public double hacBank = 28;                                 // przechylenie na okręgu HAC [°]
        public string file = "";

        public static readonly VehicleProfile Default = new VehicleProfile();
        public static readonly List<VehicleProfile> List = new List<VehicleProfile>();
        public static VehicleProfile Current = Default;

        public static string Folder
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"), "Vehicles"); }
        }

        public static double Interp(double x, double[] xs, double[] ys)
        {
            if (xs == null || xs.Length == 0) return 0;
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++) if (x <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return ys[ys.Length - 1];
        }

        private static double Num(string s, double def)
        {
            if (s == null) return def;
            int c = s.IndexOf("//"); if (c >= 0) s = s.Substring(0, c);
            double v; return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        /// <summary>„x:y x:y …” → dwie tablice; zła składnia – zostają wartości domyślne.</summary>
        private static void Table(ConfigNode n, string key, ref double[] xs, ref double[] ys)
        {
            string s = n.GetValue(key);
            if (s == null) return;
            int c = s.IndexOf("//"); if (c >= 0) s = s.Substring(0, c);
            List<double> X = new List<double>(), Y = new List<double>();
            foreach (string w in s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = w.Split(':');
                double a, b;
                if (kv.Length != 2 || !double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) ||
                    !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                { Debug.Log("[RSSBalancer] VEHICLE " + key + ": zła para „" + w + "” – zostaje tabela domyślna"); return; }
                X.Add(a); Y.Add(b);
            }
            for (int i = 1; i < X.Count; i++) if (X[i] <= X[i - 1]) { Debug.Log("[RSSBalancer] VEHICLE " + key + ": x musi rosnąć – zostaje tabela domyślna"); return; }
            if (X.Count == 0) return;
            xs = X.ToArray(); ys = Y.ToArray();
        }

        public static VehicleProfile FromNode(ConfigNode n, string file)
        {
            VehicleProfile v = new VehicleProfile { file = file };
            v.name = n.GetValue("name") ?? Path.GetFileNameWithoutExtension(file);
            v.stallEas = Num(n.GetValue("stallSpeed"), v.stallEas);
            v.stallMass = Num(n.GetValue("stallMass"), v.stallMass);
            Table(n, "entryAoA", ref v.aoaX, ref v.aoaY);
            Table(n, "entryLD", ref v.entLdX, ref v.entLdY);
            Table(n, "glideLD", ref v.glideX, ref v.glideY);
            Table(n, "glideMach", ref v.machX, ref v.machY);
            v.steepDeg = Num(n.GetValue("steepPath"), v.steepDeg);
            v.steepAim = Num(n.GetValue("steepAim"), v.steepAim);
            v.hacBank = Num(n.GetValue("hacBank"), v.hacBank);
            return v;
        }

        public static void Reload()
        {
            List.Clear();
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (string f in Directory.GetFiles(Folder, "*.cfg"))
                {
                    ConfigNode root = ConfigNode.Load(f);
                    if (root == null) continue;
                    foreach (ConfigNode n in root.GetNodes("VEHICLE")) List.Add(FromNode(n, Path.GetFileName(f)));
                }
            }
            catch (Exception e) { Debug.Log("[RSSBalancer] VehicleProfile.Reload: " + e); }
        }

        /// <summary>Profil o danej nazwie (bez rozróżniania wielkości liter); null/pusty – Buran wbudowany.</summary>
        public static VehicleProfile Find(string name, out bool found)
        {
            found = true;
            if (string.IsNullOrEmpty(name)) return Default;
            int c = name.IndexOf("//"); if (c >= 0) name = name.Substring(0, c);
            name = name.Trim();
            if (List.Count == 0) Reload();
            foreach (VehicleProfile v in List) if (string.Equals(v.name, name, StringComparison.OrdinalIgnoreCase)) return v;
            found = false; return Default;
        }

        public double StallEas(double massT) { return stallEas * Math.Sqrt(Math.Max(10, massT) / stallMass); }
        public float AoA(double mach) { return (float)Interp(mach, aoaX, aoaY); }
        public double EntryLD(double v) { return Interp(v, entLdX, entLdY); }
        public double GlideLD(double eas, double mach) { return Interp(eas, glideX, glideY) * Interp(mach, machX, machY); }
    }
}
