using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Ustawienia algorytmów programu lotu – wszystko, co da się zmienić bez nowego DLL.
    /// Źródła (późniejsze wygrywa):
    ///   1) wartości domyślne z katalogu poniżej,
    ///   2) PluginData/Ustawienia.cfg  (USTAWIENIA { klucz = wartość }) – dla wszystkich programów,
    ///   3) plik programu: FLIGHT_PROGRAM { klucz = wartość } – tylko dla tego programu.
    /// Czytane od nowa przy każdym „Start programu” – zmiana bez restartu KSP.
    /// Pełna lista z opisami: PluginData/USTAWIENIA_lista.txt (zapisywana przy każdym starcie programu).
    /// Dodatkowo dowolne pole MechJeba: „mj.ascent.Pole”, „mj.rendezvous.Pole”, „mj.docking.Pole” = wartość.
    /// </summary>
    public static class Tun
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public class Def { public string key, def, unit, desc, group; }
        static readonly List<Def> catalog = new List<Def>();
        static readonly Dictionary<string, Def> byKey = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, double> cacheD = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<KeyValuePair<string, string>> Extra = new List<KeyValuePair<string, string>>();   // mj.* i nieznane klucze z kropką

        static void A(string group, string key, string def, string unit, string desc)
        {
            var d = new Def { group = group, key = key, def = def, unit = unit, desc = desc };
            catalog.Add(d); byKey[key] = d;
        }

        static Tun()
        {
            // --- odejście od odrzuconego stopnia ---
            A("Odejście od stopnia", "sep.safeDist", "50", "m", "do tej odległości od odrzuconego stopnia: bez warpa i bez manewrów, RCS od niego");
            A("Odejście od stopnia", "sep.lateral", "40", "m", "… chyba że jest już tyle w bok od kierunku lotu");
            A("Odejście od stopnia", "sep.openSpeed", "1.0", "m/s", "prędkość rozchodzenia się, do której pcha RCS");
            A("Odejście od stopnia", "sep.far", "500", "m", "dalej niż tyle stopień nie przeszkadza (bez względu na kierunek)");
            A("Odejście od stopnia", "sep.maxTime", "300", "s", "najdłużej tyle trwa odchodzenie");

            // --- impulsy ---
            A("Impulsy", "burn.attTol", "3", "°", "silnik pracuje dopiero, gdy nos jest tak blisko kierunku impulsu");
            A("Impulsy", "burn.axisSoftM", "5000", "m", "impuls na wysokość: przepustnica spada liniowo w ostatnich tylu metrach");
            A("Impulsy", "burn.vectorSoftK", "1.5", "s", "impuls wektorowy: przepustnica = Δv / (przyspieszenie × tyle)");
            A("Impulsy", "burn.minThrottle", "0.02", "", "najmniejsza przepustnica przy końcówce impulsu");
            A("Impulsy", "burn.warpStopLead", "60", "s", "tyle przed impulsem warp jest zawsze zatrzymany");
            A("Impulsy", "burn.warpTo", "90", "s", "program przyspiesza czas do tylu sekund przed impulsem");
            A("Impulsy", "burn.alignedTol", "5", "°", "warunek „aligned” i stały ciąg „burn” – tolerancja kierunku");

            // --- start na orbitę (autoOrbit) ---
            A("Start na orbitę", "ao.overshootMinKm", "10", "km", "awaryjne wyłączenie silnika, gdy apogeum przekroczy cel o tyle …");
            A("Start na orbitę", "ao.overshootFrac", "0.05", "", "… albo o taką część celu (większa wartość wygrywa)");
            A("Start na orbitę", "ao.overshootAfter", "30", "s", "awaryjne wyłączenie dopiero po tylu sekundach od startu");
            A("Start na orbitę", "ao.payloadSepDelay", "3", "s", "po wyłączeniu silników rakiety: tyle do oddzielenia statku");
            A("Start na orbitę", "ao.pitchStartK", "55", "m/s", "początek zakrętu PVG = tyle / TWR …");
            A("Start na orbitę", "ao.pitchStartMin", "25", "m/s", "… ale nie mniej niż");
            A("Start na orbitę", "ao.pitchStartMax", "70", "m/s", "… i nie więcej niż");
            A("Start na orbitę", "ao.pitchRate", "0.75", "°/s", "MechJeb PitchRate");
            A("Start na orbitę", "ao.maxAccel", "39.2", "m/s²", "MechJeb – ograniczenie przyspieszenia (39,2 = 4 g)");
            A("Start na orbitę", "ao.minTwr", "1.15", "", "ostrzeżenie: za mały TWR na starcie");

            // --- spotkanie i dokowanie ---
            A("Spotkanie", "rdv.planeMinDeg", "0.1", "°", "mniejszej różnicy płaszczyzn nie korygujemy osobnym impulsem (wyrówna MechJeb przy zbliżaniu)");
            A("Spotkanie", "rdv.nodeLateSec", "90", "s", "korekta płaszczyzny najpóźniej tyle po węźle, inaczej czeka na następny");
            A("Spotkanie", "rdv.burnTolMs", "0.05", "m/s", "impuls w apsydzie kończy się, gdy zostało mniej niż tyle Δv");
            A("Spotkanie", "rdv.b1AfterRevs", "3", "obiegi", "impuls 1 po tylu obiegach od wstawienia (3 → 4. okrążenie)");
            A("Spotkanie", "rdv.b3Orbit", "17", "okrążenie", "zbliżenie (Hohmann pod cel), profil jednodobowy");
            A("Spotkanie", "rdv.b3OrbitSlow", "33", "okrążenie", "zbliżenie, profil dwudobowy");
            A("Spotkanie", "rdv.arriveBehindKm", "10", "km", "przybycie tyle za celem – dalej MechJeb");
            A("Spotkanie", "rdv.phasingMinKm", "195", "km", "orbita fazująca nie niżej");
            A("Spotkanie", "rdv.windowDays", "30", "doby", "jak daleko szukać okna startowego");
            A("Spotkanie", "rdv.engineDelay", "3", "s", "po oddzieleniu: stage silnika statku (KTDU + panele); −1 = nie odpalaj");
            A("Spotkanie", "rdv.mechjebDistance", "100", "m", "MechJeb Rendezvous: zatrzymaj się tyle od celu");
            A("Spotkanie", "rdv.mechjebMaxSpeed", "5", "m/s", "MechJeb Rendezvous: maks. prędkość zbliżania");
            A("Spotkanie", "rdv.dockStartDistance", "200", "m", "dokowanie zaczyna się poniżej tej odległości …");
            A("Spotkanie", "rdv.dockStartSpeed", "0.5", "m/s", "… i prędkości względnej");
            A("Spotkanie", "rdv.openTargetAnims", "OPEN, OPEN DockPort", "", "animacje otwierane na celu przed dokowaniem (nazwy animationName, po przecinku) – Buran: drzwi ładowni i podstawa portu");
            A("Spotkanie", "rdv.portWaitSec", "60", "s", "najdłużej tyle czekamy na wysunięcie portów dokujących");
            A("Spotkanie", "rdv.dockSpeed", "0.3", "m/s", "MechJeb Docking: prędkość podejścia („Igła”: 0,1–0,35)");
            A("Spotkanie", "rdv.ascentTime", "498", "s", "model wynoszenia do okna startowego: czas do wstawienia");
            A("Spotkanie", "rdv.insertionDLat", "5.0781", "°", "przesunięcie punktu wstawienia względem wyrzutni (szerokość)");
            A("Spotkanie", "rdv.insertionDLon", "22.6763", "°", "… (długość)");

            // --- powrót / okno startowe pod lądowanie ---
            A("Okno startowe pod lądowanie", "lw.lag", "18", "s", "zapas czasu w modelu okna startowego");
            A("Okno startowe pod lądowanie", "lw.landAfterEntry", "1900", "s", "od wejścia w atmosferę do przyziemienia");
            A("Okno startowe pod lądowanie", "lw.sunDeg", "12", "°", "„w dzień” = Słońce co najmniej tyle nad horyzontem");
            A("Okno startowe pod lądowanie", "lw.maxHours", "30", "h", "jak daleko szukać okna");

            // --- podejście i lądowanie (Buran / OK-GLI) ---
            A("Podejście i lądowanie", "pat.bank", "30", "°", "przechylenie w zakrętach kręgu");
            A("Podejście i lądowanie", "pat.alt", "4000", "m", "wysokość kręgu nad pasem");
            A("Podejście i lądowanie", "ab.offLimit", "24.4", "", "hamulec aerodynamiczny – limit");
            A("Podejście i lądowanie", "hac.ldp", "4.0", "", "HAC – parametr LDP");
            A("Podejście i lądowanie", "hac.em", "600", "m", "HAC – zapas energii");
            A("Podejście i lądowanie", "taem.v", "800", "m/s", "TAEM – prędkość początku");
            A("Podejście i lądowanie", "taem.dist", "100000", "m", "TAEM – odległość początku");
        }

        public static string GlobalFile
        {
            get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "Ustawienia.cfg")); }
        }
        public static string ListFile
        {
            get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), Path.Combine("PluginData", "USTAWIENIA_lista.txt")); }
        }

        static bool IsSettingKey(string k) { return k.IndexOf('.') > 0 && !k.StartsWith("thrust.", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Wczytuje ustawienia: domyślne → Ustawienia.cfg → plik programu. Zwraca opis zmienionych.</summary>
        public static string Load(ConfigNode program)
        {
            vals.Clear(); cacheD.Clear(); Extra.Clear();
            var changed = new List<string>();
            try
            {
                if (File.Exists(GlobalFile))
                {
                    ConfigNode root = ConfigNode.Load(GlobalFile);
                    ConfigNode n = root != null ? (root.GetNode("USTAWIENIA") ?? root) : null;
                    if (n != null) foreach (ConfigNode.Value v in n.values) Put(v.name, v.value, "Ustawienia.cfg", changed);
                }
            }
            catch (Exception e) { Debug.Log("[RSSBalancer] Ustawienia.cfg: " + e.Message); }
            if (program != null)
            {
                foreach (ConfigNode.Value v in program.values) if (IsSettingKey(v.name) || byKey.ContainsKey(v.name)) Put(v.name, v.value, "program", changed);
                // zgodność wstecz
                if (program.HasValue("separationFar")) Put("sep.far", program.GetValue("separationFar"), "program", changed);
            }
            WriteList();
            return changed.Count == 0 ? "" : "Ustawienia zmienione: " + string.Join(", ", changed.ToArray());
        }

        static void Put(string k, string v, string src, List<string> changed)
        {
            k = k.Trim(); v = (v ?? "").Trim();
            if (k.Length == 0) return;
            if (byKey.ContainsKey(k)) { vals[k] = v; if (v != byKey[k].def) changed.Add(k + " = " + v + " (" + src + ")"); }
            else
            {
                Extra.RemoveAll(e => string.Equals(e.Key, k, StringComparison.OrdinalIgnoreCase));
                Extra.Add(new KeyValuePair<string, string>(k, v));
                if (!k.StartsWith("mj.", StringComparison.OrdinalIgnoreCase)) Debug.Log("[RSSBalancer] Ustawienia: nieznany klucz " + k + " (" + src + ")");
                changed.Add(k + " = " + v + " (" + src + ")");
            }
        }

        public static double D(string key)
        {
            double x;
            if (cacheD.TryGetValue(key, out x)) return x;
            string s;
            Def d; byKey.TryGetValue(key, out d);
            if (!vals.TryGetValue(key, out s)) s = d != null ? d.def : "0";
            if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out x))
            { x = d != null ? double.Parse(d.def, Inv) : 0; Debug.Log("[RSSBalancer] Ustawienia: zła liczba „" + s + "” dla " + key); }
            cacheD[key] = x;
            return x;
        }
        public static int I(string key) { return (int)Math.Round(D(key)); }
        public static string S(string key)
        {
            string s; if (vals.TryGetValue(key, out s)) return s.Replace(',', '.');
            Def d; return byKey.TryGetValue(key, out d) ? d.def : "";
        }

        /// <summary>Pola MechJeba z ustawień: prefix „mj.ascent.” itd.</summary>
        public static List<KeyValuePair<string, string>> Mj(string module)
        {
            string pre = "mj." + module + ".";
            var r = new List<KeyValuePair<string, string>>();
            foreach (var e in Extra) if (e.Key.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) r.Add(new KeyValuePair<string, string>(e.Key.Substring(pre.Length), e.Value));
            return r;
        }

        public static void WriteList()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("RSSBalancer – ustawienia programu lotu (bez nowego DLL i bez restartu KSP)");
                sb.AppendLine("Plik tworzony automatycznie przy każdym „Start programu” – nie edytuj go, tylko kopiuj wiersze.");
                sb.AppendLine();
                sb.AppendLine("Gdzie wpisać:");
                sb.AppendLine("  • dla jednego programu: w jego pliku PluginData/Programs/*.cfg, wewnątrz FLIGHT_PROGRAM { … }, np.  rdv.dockSpeed = 0.2");
                sb.AppendLine("  • dla wszystkich programów: PluginData/Ustawienia.cfg:   USTAWIENIA { sep.far = 300 }");
                sb.AppendLine("  Plik programu wygrywa z Ustawienia.cfg. Zmiana działa od następnego „Start programu”.");
                sb.AppendLine();
                sb.AppendLine("Dowolne pole MechJeba (nazwa jak w MechJebie, ustawiane po wartościach programu):");
                sb.AppendLine("  mj.ascent.<pole> = …       moduły Ascent (np. mj.ascent.PitchRate = 0.6)");
                sb.AppendLine("  mj.rendezvous.<pole> = …   MechJebModuleRendezvousAutopilot (np. mj.rendezvous.maxPhasingOrbits = 10)");
                sb.AppendLine("  mj.docking.<pole> = …      MechJebModuleDockingAutopilot (np. mj.docking.overrideSafeDistance = True)");
                sb.AppendLine();
                string g = null;
                foreach (Def d in catalog)
                {
                    if (d.group != g) { g = d.group; sb.AppendLine("--- " + g + " ---"); }
                    string cur = vals.ContainsKey(d.key) ? vals[d.key] : d.def;
                    sb.AppendLine(string.Format(Inv, "{0,-24} = {1,-8} {2,-9} {3}{4}", d.key, d.def, d.unit, d.desc, cur != d.def ? "   [teraz: " + cur + "]" : ""));
                }
                File.WriteAllText(ListFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception e) { Debug.Log("[RSSBalancer] USTAWIENIA_lista.txt: " + e.Message); }
        }
    }
}
