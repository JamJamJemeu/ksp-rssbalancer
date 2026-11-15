using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RSSBalancer
{
    /// <summary>Ustawienia jednej grupy silników: odpalenie w stage'u <c>stage</c>, odrzucenie w <c>jettison</c>.</summary>
    public class StagePreset
    {
        public const int AnyJettison = int.MinValue; // presety z 1.4 – tylko numer stage'u odpalenia

        public int stage;
        public int jettison = AnyJettison;
        public float isp = 1f;
        public float thrust = 1f;
    }

    /// <summary>
    /// Preset na rakietę: nazwa + mnożniki dla każdego stopnia z silnikami.
    /// Starsze presety (z wersji 1.3) mają tylko wspólne isp/thrust – nakładane na cały statek.
    /// </summary>
    public class Preset
    {
        public string name;
        public float isp = 1f;
        public float thrust = 1f;
        public List<StagePreset> stages = new List<StagePreset>();

        public string Summary()
        {
            if (stages.Count == 0)
                return "ISP ×" + isp.ToString("F2") + ", Ciąg ×" + thrust.ToString("F2");
            return stages.Count + (stages.Count == 1 ? " grupa" : (stages.Count < 5 ? " grupy" : " grup"));
        }
    }

    /// <summary>Presety zapisywane w GameData/RSSBalancer/PluginData/presets.cfg, wspólne dla wszystkich save'ów.</summary>
    public static class PresetStore
    {
        private const string NodeName = "RSSBALANCER_PRESET";

        public static readonly List<Preset> Presets = new List<Preset>();
        public static string LastError;

        private static bool loaded;

        private static string FilePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "RSSBalancer", "PluginData", "presets.cfg"); }
        }

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

            try
            {
                if (!File.Exists(FilePath)) return;

                ConfigNode root = ConfigNode.Load(FilePath);
                if (root == null) return;

                foreach (ConfigNode n in root.GetNodes(NodeName))
                {
                    Preset p = new Preset { name = n.GetValue("name") };
                    if (string.IsNullOrEmpty(p.name)) continue;
                    p.isp = F(n.GetValue("isp"), 1f);
                    p.thrust = F(n.GetValue("thrust"), 1f);

                    foreach (ConfigNode s in n.GetNodes("STAGE"))
                    {
                        int st, jet;
                        if (!int.TryParse(s.GetValue("stage"), out st)) continue;
                        if (!int.TryParse(s.GetValue("jettison"), out jet)) jet = StagePreset.AnyJettison;
                        p.stages.Add(new StagePreset { stage = st, jettison = jet, isp = F(s.GetValue("isp"), 1f), thrust = F(s.GetValue("thrust"), 1f) });
                    }
                    Presets.Add(p);
                }
                Sort();
            }
            catch (Exception e)
            {
                LastError = "Nie udało się wczytać presetów: " + e.Message;
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

        /// <summary>Zapisuje albo nadpisuje preset o tej nazwie. Zwraca false przy błędzie zapisu.</summary>
        public static bool SaveOrUpdate(string name, List<StagePreset> stages)
        {
            name = Clean(name);
            if (name.Length == 0) return false;

            Preset p = Find(name);
            if (p == null)
            {
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
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));

                ConfigNode root = new ConfigNode();
                foreach (Preset p in Presets)
                {
                    ConfigNode n = root.AddNode(NodeName);
                    n.AddValue("name", p.name);
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
