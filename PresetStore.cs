using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RSSBalancer
{
    /// <summary>
    /// Ustawienia jednej grupy silników: odpalenie w stage'u <c>stage</c>, odrzucenie w <c>jettison</c>.
    /// Od 1.8 także zbiorniki tej grupy (masa pustych ×, paliwo ×); NaN = nie zmieniaj zbiorników.
    /// </summary>
    public class StagePreset
    {
        public const int AnyJettison = int.MinValue; // presety z 1.4 – tylko numer stage'u odpalenia

        public int stage;
        public int jettison = AnyJettison;
        public float isp = 1f;
        public float thrust = 1f;
        public float dry = float.NaN;
        public float fuel = float.NaN;

        public bool HasTanks { get { return !float.IsNaN(dry) || !float.IsNaN(fuel); } }
    }

    /// <summary>
    /// Preset na rakietę: nazwa + mnożniki dla każdej grupy silników.
    /// Starsze presety (z wersji 1.3) mają tylko wspólne isp/thrust – nakładane na cały statek.
    /// </summary>
    public class Preset
    {
        public string name;
        public string description = "";
        public float isp = 1f;
        public float thrust = 1f;
        public List<StagePreset> stages = new List<StagePreset>();

        /// <summary>Plik, z którego preset pochodzi. Pusty = presets.cfg (Twoje presety).</summary>
        public string sourceFile = "";
        public bool ReadOnly { get { return !string.IsNullOrEmpty(sourceFile); } }

        public string Summary()
        {
            if (stages.Count == 0)
                return "ISP ×" + isp.ToString("F2") + ", Ciąg ×" + thrust.ToString("F2");

            string s = stages.Count + (stages.Count == 1 ? " grupa" : (stages.Count < 5 ? " grupy" : " grup"));
            if (stages.Exists(x => x.HasTanks)) s += ", ze zbiornikami";
            if (ReadOnly) s += ", plik " + sourceFile;
            return s;
        }
    }

    /// <summary>
    /// Presety:
    ///   • GameData/RSSBalancer/PluginData/presets.cfg – Twoje presety (zapis/usuwanie z gry),
    ///   • GameData/RSSBalancer/PluginData/Presets/*.cfg – gotowe presety z plików (tylko do wczytania).
    /// „Wczytaj ponownie” czyta wszystkie pliki od nowa, bez restartu gry.
    /// </summary>
    public static class PresetStore
    {
        private const string NodeName = "RSSBALANCER_PRESET";

        public static readonly List<Preset> Presets = new List<Preset>();
        public static string LastError;

        private static bool loaded;

        private static string PluginData
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "RSSBalancer", "PluginData"); }
        }

        private static string FilePath { get { return Path.Combine(PluginData, "presets.cfg"); } }
        public static string ExtraFolder { get { return Path.Combine(PluginData, "Presets"); } }

        private static float F(string s, float def)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0f ? v : def;
        }

        private static string S(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Presets.Clear();
            LastError = null;

            LoadFile(FilePath, "");

            try
            {
                Directory.CreateDirectory(ExtraFolder); // żeby łatwo było znaleźć, gdzie wrzucać pliki presetów
                if (Directory.Exists(ExtraFolder))
                {
                    string[] files = Directory.GetFiles(ExtraFolder, "*.cfg");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                        LoadFile(f, Path.GetFileName(f));
                }
            }
            catch (Exception e)
            {
                LastError = "Nie udało się odczytać folderu Presets: " + e.Message;
            }

            Sort();
        }

        /// <summary>Czyta wszystkie pliki presetów od nowa (bez restartu gry).</summary>
        public static int Reload()
        {
            loaded = false;
            EnsureLoaded();
            return Presets.Count;
        }

        private static void LoadFile(string path, string sourceName)
        {
            try
            {
                if (!File.Exists(path)) return;

                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return;

                foreach (ConfigNode n in root.GetNodes(NodeName))
                {
                    Preset p = new Preset { name = n.GetValue("name"), sourceFile = sourceName };
                    if (string.IsNullOrEmpty(p.name)) continue;
                    p.description = n.GetValue("description") ?? "";
                    p.isp = F(n.GetValue("isp"), 1f);
                    p.thrust = F(n.GetValue("thrust"), 1f);

                    foreach (ConfigNode s in n.GetNodes("STAGE"))
                    {
                        int st, jet;
                        if (!int.TryParse(s.GetValue("stage"), out st)) continue;
                        if (!int.TryParse(s.GetValue("jettison"), out jet)) jet = StagePreset.AnyJettison;
                        p.stages.Add(new StagePreset
                        {
                            stage = st,
                            jettison = jet,
                            isp = F(s.GetValue("isp"), 1f),
                            thrust = F(s.GetValue("thrust"), 1f),
                            dry = F(s.GetValue("dry"), float.NaN),
                            fuel = F(s.GetValue("fuel"), float.NaN)
                        });
                    }

                    // Preset z presets.cfg o tej samej nazwie ma pierwszeństwo przed presetem z folderu.
                    if (Find(p.name) != null) continue;
                    Presets.Add(p);
                }
            }
            catch (Exception e)
            {
                LastError = "Nie udało się wczytać " + (sourceName == "" ? "presets.cfg" : sourceName) + ": " + e.Message;
                UnityEngine.Debug.LogError("[RSSBalancer] " + LastError);
            }
        }

        public static string Clean(string name)
        {
            if (name == null) return "";
            name = name.Replace("{", "").Replace("}", "").Replace("=", "").Replace("//", "/");
            return name.Trim();
        }

        public static Preset Find(string name)
        {
            foreach (Preset p in Presets)
                if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        /// <summary>Zapisuje albo nadpisuje preset o tej nazwie w presets.cfg. Zwraca false przy błędzie zapisu.</summary>
        public static bool SaveOrUpdate(string name, List<StagePreset> stages)
        {
            name = Clean(name);
            if (name.Length == 0) return false;

            Preset p = Find(name);
            if (p == null || p.ReadOnly)
            {
                if (p != null) Presets.Remove(p); // Twój preset zasłania ten z pliku
                p = new Preset { name = name };
                Presets.Add(p);
            }
            p.stages = stages;
            p.isp = 1f;
            p.thrust = 1f;
            Sort();
            return WriteFile();
        }

        public static bool Delete(Preset p)
        {
            if (p.ReadOnly)
            {
                LastError = "Ten preset pochodzi z pliku Presets\\" + p.sourceFile + " – usuń ten plik, żeby go usunąć.";
                return false;
            }
            Presets.Remove(p);
            return WriteFile();
        }

        private static void Sort()
        {
            Presets.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool WriteFile()
        {
            try
            {
                Directory.CreateDirectory(PluginData);

                ConfigNode root = new ConfigNode();
                foreach (Preset p in Presets)
                {
                    if (p.ReadOnly) continue;
                    ConfigNode n = root.AddNode(NodeName);
                    n.AddValue("name", p.name);
                    if (!string.IsNullOrEmpty(p.description)) n.AddValue("description", p.description);
                    n.AddValue("isp", S(p.isp));
                    n.AddValue("thrust", S(p.thrust));
                    foreach (StagePreset s in p.stages)
                    {
                        ConfigNode sn = n.AddNode("STAGE");
                        sn.AddValue("stage", s.stage.ToString(CultureInfo.InvariantCulture));
                        if (s.jettison != StagePreset.AnyJettison)
                            sn.AddValue("jettison", s.jettison.ToString(CultureInfo.InvariantCulture));
                        sn.AddValue("isp", S(s.isp));
                        sn.AddValue("thrust", S(s.thrust));
                        if (!float.IsNaN(s.dry)) sn.AddValue("dry", S(s.dry));
                        if (!float.IsNaN(s.fuel)) sn.AddValue("fuel", S(s.fuel));
                    }
                }
                root.Save(FilePath);
                LastError = null;
                return true;
            }
            catch (Exception e)
            {
                LastError = "Nie udało się zapisać presetów (uprawnienia do folderu gry?): " + e.Message;
                UnityEngine.Debug.LogError("[RSSBalancer] " + LastError);
                return false;
            }
        }
    }
}
