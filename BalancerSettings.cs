using System;
using System.Globalization;
using System.IO;

namespace RSSBalancer
{
    /// <summary>Ustawienia moda (wzorzec Sojuza + efekty w locie), zapisywane w GameData/RSSBalancer/PluginData/settings.cfg.</summary>
    public static class BalancerSettings
    {
        // Cele wzorca Sojuza
        public static double BoosterTime = SoyuzTemplate.BoosterTime;
        public static double CoreTime = SoyuzTemplate.CoreTime;
        public static double UpperTime = SoyuzTemplate.UpperTime;
        public static double LiftoffTwr = SoyuzTemplate.LiftoffTwr;
        public static bool DoUpper = true;

        // W locie
        public static bool KorolevCross = true;
        public static float KorolevStrength = 1f;     // 0..3
        public static bool HotStaging = true;
        public static float HotStagingLead = 1.5f;    // s przed wypaleniem rdzenia
        public static bool AutoCoreSeparation = false;
        public static bool Timeline = true;
        public static bool FlightLog = true;
        public static bool Telemetry = true;
        public static bool TelemetryRussian = true;
        public static bool TelemetryOnF2 = true;
        public static float TelemetryScale = 1f;

        // Misja (zapamiętane między uruchomieniami; NaN = domyślne)
        public static double LaunchLatitude = 45.9;   // Bajkonur
        public static double OrbitAltKm = double.NaN;

        private static bool loaded;

        private static string FilePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "RSSBalancer", "PluginData", "settings.cfg"); }
        }

        private static string S(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        private static double D(ConfigNode n, string key, double def)
        {
            double v;
            return double.TryParse(n.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static bool B(ConfigNode n, string key, bool def)
        {
            bool v;
            return bool.TryParse(n.GetValue(key), out v) ? v : def;
        }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                ConfigNode root = ConfigNode.Load(FilePath);
                if (root == null) return;
                ConfigNode n = root.GetNode("RSSBALANCER_SETTINGS");
                if (n == null) return;

                BoosterTime = D(n, "boosterTime", BoosterTime);
                CoreTime = D(n, "coreTime", CoreTime);
                UpperTime = D(n, "upperTime", UpperTime);
                LiftoffTwr = D(n, "liftoffTwr", LiftoffTwr);
                DoUpper = B(n, "doUpper", DoUpper);

                KorolevCross = B(n, "korolevCross", KorolevCross);
                KorolevStrength = (float)D(n, "korolevStrength", KorolevStrength);
                HotStaging = B(n, "hotStaging", HotStaging);
                HotStagingLead = (float)D(n, "hotStagingLead", HotStagingLead);
                AutoCoreSeparation = B(n, "autoCoreSeparation", AutoCoreSeparation);
                Timeline = B(n, "timeline", Timeline);
                FlightLog = B(n, "flightLog", FlightLog);
                Telemetry = B(n, "telemetry", Telemetry);
                TelemetryRussian = B(n, "telemetryRussian", TelemetryRussian);
                TelemetryOnF2 = B(n, "telemetryOnF2", TelemetryOnF2);
                {
                    float sc;
                    if (float.TryParse(n.GetValue("telemetryScale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sc) && sc >= 0.5f && sc <= 1.6f)
                        TelemetryScale = sc;
                }
                LaunchLatitude = D(n, "launchLatitude", LaunchLatitude);
                OrbitAltKm = D(n, "orbitAltKm", OrbitAltKm);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[RSSBalancer] Nie udało się wczytać ustawień: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                ConfigNode root = new ConfigNode();
                ConfigNode n = root.AddNode("RSSBALANCER_SETTINGS");

                n.AddValue("boosterTime", S(BoosterTime));
                n.AddValue("coreTime", S(CoreTime));
                n.AddValue("upperTime", S(UpperTime));
                n.AddValue("liftoffTwr", S(LiftoffTwr));
                n.AddValue("doUpper", DoUpper.ToString());

                n.AddValue("korolevCross", KorolevCross.ToString());
                n.AddValue("korolevStrength", S(KorolevStrength));
                n.AddValue("hotStaging", HotStaging.ToString());
                n.AddValue("hotStagingLead", S(HotStagingLead));
                n.AddValue("autoCoreSeparation", AutoCoreSeparation.ToString());
                n.AddValue("timeline", Timeline.ToString());
                n.AddValue("flightLog", FlightLog.ToString());
                n.AddValue("telemetry", Telemetry.ToString());
                n.AddValue("telemetryRussian", TelemetryRussian.ToString());
                n.AddValue("telemetryOnF2", TelemetryOnF2.ToString());
                n.AddValue("telemetryScale", TelemetryScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("launchLatitude", S(LaunchLatitude));
                if (!double.IsNaN(OrbitAltKm)) n.AddValue("orbitAltKm", S(OrbitAltKm));

                root.Save(FilePath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[RSSBalancer] Nie udało się zapisać ustawień: " + e.Message);
            }
        }
    }
}
