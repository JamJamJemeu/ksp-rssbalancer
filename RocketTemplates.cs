using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Wzorce rakiet z plików cfg (GameData/RSSBalancer/PluginData/Rakiety/*.cfg) – działają jak zakładka Proton:
    /// dla każdego stopnia paliwo, ISP, ciąg i masa konstrukcji jak w oryginale. Pliki czytane są na bieżąco
    /// (przycisk „Wczytaj ponownie”), więc nową rakietę albo poprawkę danych dodaje się bez restartu KSP.
    ///
    /// ROCKET
    /// {
    ///     name = Saturn V            // nazwa na liście
    ///     aliases = Apollo, Skylab   // dodatkowe słowa dla wyszukiwarki
    ///     info = …                   // opis pod listą
    ///     profile = …                // profil lotu oryginału (\n = nowa linia)
    ///     source = …                 // skąd dane
    ///     liftoffMass = 2950         // t, tylko do porównania
    ///     liftoffTwr = 1.16
    ///     payload = Apollo CSM + LM ≈ 45 t   // tylko do raportu
    ///     exclude = Titan1           // regex: gdy któraś część-silnik pasuje, wzorzec nie dotyczy tej rakiety
    ///     STAGE
    ///     {
    ///         name = S-IC
    ///         role = serial          // serial | booster | core
    ///         engine = Saturn\.Engine\.F1     // regex nazwy części silnika (partInfo.name); puste = dowolny
    ///         count = 4              // tylko booster: liczba bloków w oryginale; wartości poniżej są NA JEDEN blok
    ///         parts = 1              // tylko booster: ile pasujących części-silników ma jeden blok (Atlas: 2 × LR89)
    ///         engines = 5            // silników w oryginale (do raportu)
    ///         prop = 2150            // t paliwa (0 = nie zmieniaj, np. silniki boosterów Atlasa bez własnych zbiorników)
    ///         dry = 135              // t konstrukcji (0 = nie zmieniaj)
    ///         isp = 304              // s w próżni
    ///         thrust = 38850         // kN w próżni (cały stopień / jeden blok)
    ///         time = 168             // s pracy w oryginale (raport; dla prop = 0 – czas pracy z cudzych zbiorników)
    ///         optional = false       // brak stopnia w rakiecie nie jest błędem (np. CZ-5B bez II stopnia)
    ///     }
    /// }
    ///
    /// Rozpoznawanie stopni (EngineGroups = grupy zapłon:odrzut):
    ///   booster – grupa odpalana na starcie z pasującym silnikiem, odrzucana najwcześniej,
    ///   core    – druga grupa odpalana na starcie, odrzucana najpóźniej,
    ///   serial  – następna grupa z pasującym silnikiem odpalana po poprzednim stopniu (pierwszy serial bez boosterów = start).
    /// </summary>
    public static class RocketTemplates
    {
        public class StageDef
        {
            public string name = "", role = "serial", engine = "";
            public Regex rx;
            public int count = 1, engines = 1, parts = 1;
            public double prop, dry, isp, thrust, time;
            public bool optional;
            public double hot;                 // gorące rozdzielenie: zapłon TEGO stopnia, gdy poprzedniemu zostało ≤ hot s (0 = nie)
            public string jettisonAt = "";     // odrzut tego stopnia o czasie / wysokości („135” s, „a60” km) – np. BECO Atlasa
            public bool ascent = true;         // false = stopień nie bierze udziału w starcie na orbitę (Blok L, Blok G/D) – dla programu to ładunek
            public bool IsBooster { get { return role == "booster"; } }
            public bool IsCore { get { return role == "core"; } }
        }

        public class RocketDef
        {
            public string name = "", aliases = "", info = "", profile = "", source = "", payload = "", file = "", exclude = "";
            public Regex excludeRx;
            public int builtin = -1;            // wzorzec wbudowany (0 Sojuz, 1 Energia, 2 Proton): tylko rozpoznanie i staging, panel jak dotąd
            public string les = "", fairing = "", stagingNote = "";
            public Regex lesRx = new Regex(@"(^|[._])les([._]|$)|escape|tower|\bsas\b", RegexOptions.IgnoreCase);
            public Regex fairingRx = new Regex(@"fairing|faring|shroud", RegexOptions.IgnoreCase);
            public double liftoffMass, liftoffTwr;
            public readonly List<StageDef> stages = new List<StageDef>();
        }

        public class Found
        {
            public EngineGroup g;
            public int n = 1;          // ile bloków (booster) znaleziono w rakiecie
        }

        public class Result
        {
            public bool ok;
            public string message = "";
            public Found[] st;
            public double massAfter, twrAfter, restMass, dv;
            public double[] propAfter, dryAfter, timeAfter;
        }

        public static readonly List<RocketDef> Defs = new List<RocketDef>();
        public static string LoadReport = "";
        private static bool loaded;

        public static string Folder
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "RSSBalancer", "PluginData", "Rakiety"); }
        }

        public static void EnsureLoaded() { if (!loaded) Reload(); }

        public static void Reload()
        {
            loaded = true;
            Defs.Clear();
            int files = 0; string errs = "";
            try
            {
                if (Directory.Exists(Folder))
                    foreach (string f in Directory.GetFiles(Folder, "*.cfg"))
                    {
                        files++;
                        try
                        {
                            ConfigNode root = ConfigNode.Load(f);
                            if (root == null) { errs += "\n" + Path.GetFileName(f) + ": nie da się odczytać"; continue; }
                            foreach (ConfigNode n in root.GetNodes("ROCKET"))
                            {
                                string e;
                                RocketDef d = Parse(n, out e);
                                if (d == null) { errs += "\n" + Path.GetFileName(f) + ": " + e; continue; }
                                d.file = Path.GetFileName(f);
                                Defs.Add(d);
                            }
                        }
                        catch (Exception ex) { errs += "\n" + Path.GetFileName(f) + ": " + ex.Message; }
                    }
            }
            catch (Exception ex) { errs += "\n" + ex.Message; }
            Defs.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            LoadReport = "Wzorce z plików: " + Defs.Count + " (plików " + files + ", folder PluginData\\Rakiety)" + errs;
        }

        private static double D(ConfigNode n, string k, double def = 0)
        {
            double v;
            string s = n.GetValue(k);
            return s != null && double.TryParse(s.Replace(',', '.').Replace(" ", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static string S(ConfigNode n, string k) { return (n.GetValue(k) ?? "").Replace("\\n", "\n").Trim(); }

        public static RocketDef Parse(ConfigNode n, out string err)
        {
            err = null;
            RocketDef d = new RocketDef
            {
                name = S(n, "name"), aliases = S(n, "aliases"), info = S(n, "info"), profile = S(n, "profile"),
                source = S(n, "source"), payload = S(n, "payload"), liftoffMass = D(n, "liftoffMass"), liftoffTwr = D(n, "liftoffTwr")
            };
            if (d.name.Length == 0) { err = "ROCKET bez nazwy"; return null; }
            d.exclude = S(n, "exclude");
            d.builtin = (int)D(n, "builtin", -1);
            ConfigNode sg = n.GetNode("STAGING");
            if (sg != null)
            {
                d.les = S(sg, "les"); d.fairing = S(sg, "fairing"); d.stagingNote = S(sg, "note");
                try
                {
                    if (S(sg, "lesPart").Length > 0) d.lesRx = new Regex(S(sg, "lesPart"), RegexOptions.IgnoreCase);
                    if (S(sg, "fairingPart").Length > 0) d.fairingRx = new Regex(S(sg, "fairingPart"), RegexOptions.IgnoreCase);
                }
                catch (Exception ex) { err = d.name + ": zły regex w STAGING (" + ex.Message + ")"; return null; }
            }
            try { d.excludeRx = d.exclude.Length > 0 ? new Regex(d.exclude, RegexOptions.IgnoreCase) : null; }
            catch (Exception ex) { err = d.name + ": zły regex exclude (" + ex.Message + ")"; return null; }
            foreach (ConfigNode s in n.GetNodes("STAGE"))
            {
                StageDef st = new StageDef
                {
                    name = S(s, "name"), role = S(s, "role").ToLowerInvariant(), engine = S(s, "engine"),
                    count = Math.Max(1, (int)D(s, "count", 1)), engines = Math.Max(1, (int)D(s, "engines", 1)), parts = Math.Max(1, (int)D(s, "parts", 1)),
                    prop = D(s, "prop"), dry = D(s, "dry"), isp = D(s, "isp"), thrust = D(s, "thrust"), time = D(s, "time"),
                    optional = S(s, "optional").ToLowerInvariant() == "true",
                    hot = D(s, "hotStaging"), jettisonAt = S(s, "jettisonAt"), ascent = S(s, "ascent").ToLowerInvariant() != "false"
                };
                if (st.role != "booster" && st.role != "core") st.role = "serial";
                if (d.builtin < 0 && (st.isp <= 0 || st.thrust <= 0)) { err = d.name + " / " + st.name + ": brak isp albo thrust"; return null; }
                try { st.rx = st.engine.Length > 0 ? new Regex(st.engine, RegexOptions.IgnoreCase) : null; }
                catch (Exception ex) { err = d.name + " / " + st.name + ": zły regex silnika (" + ex.Message + ")"; return null; }
                d.stages.Add(st);
            }
            if (d.stages.Count == 0) { err = d.name + ": brak STAGE"; return null; }
            return d;
        }

        // ------------------------------------------------------------------ rozpoznanie

        private static int Matching(EngineGroup g, StageDef s)
        {
            int n = 0;
            foreach (Part p in g.parts)
            {
                if (p == null) continue;
                string pn = p.partInfo != null ? p.partInfo.name : p.name;
                if (s.rx == null || s.rx.IsMatch(pn ?? "")) n++;
            }
            return n;
        }

        /// <summary>Przypisuje grupy do stopni wzorca. Zwraca komunikat błędu albo null.</summary>
        public static string Identify(RocketDef d, List<EngineGroup> groups, Found[] st)
        {
            if (groups == null || groups.Count == 0) return "Brak silników na statku.";
            int launch = int.MinValue;
            foreach (EngineGroup g in groups) if (ProtonTemplate.IsLiquid(g)) launch = Math.Max(launch, g.ignition);
            if (launch == int.MinValue) return "Brak silników na paliwo ciekłe.";
            if (d.excludeRx != null)
                foreach (EngineGroup g in groups)
                    foreach (Part p in g.parts)
                    {
                        string pn = p != null && p.partInfo != null ? p.partInfo.name : null;
                        if (pn != null && d.excludeRx.IsMatch(pn)) return "Rakieta ma silnik „" + pn + "” – to nie " + d.name + ".";
                    }
            int launchGroups = 0;
            foreach (EngineGroup g in groups) if (ProtonTemplate.IsLiquid(g) && g.ignition == launch) launchGroups++;
            HashSet<EngineGroup> used = new HashSet<EngineGroup>();
            int prevIgn = int.MaxValue;   // zapłon poprzedniego stopnia (szeregowe muszą być później = niższy numer stage'a)
            for (int i = 0; i < d.stages.Count; i++)
            {
                StageDef s = d.stages[i];
                EngineGroup best = null; int bestN = 0;
                foreach (EngineGroup g in groups)
                {
                    if (used.Contains(g) || !ProtonTemplate.IsLiquid(g)) continue;
                    int m = Matching(g, s);
                    if (m == 0) continue;
                    if (s.IsBooster || s.IsCore)
                    {
                        if (g.ignition != launch) continue;
                        if (s.IsBooster && launchGroups < 2) continue;   // jedna grupa na starcie = to rdzeń, nie bloki boczne
                        // booster: odrzucany najwcześniej (największy numer stage'a odrzutu); rdzeń: najpóźniej
                        int jb = best == null ? 0 : JetKey(best.jettison), jg = JetKey(g.jettison);
                        if (best == null || (s.IsBooster ? jg > jb : jg < jb) || (jg == jb && g.massFlow > best.massFlow)) { best = g; bestN = m; }
                    }
                    else
                    {
                        if (prevIgn == int.MaxValue ? g.ignition != launch : g.ignition >= prevIgn) continue;
                        if (best == null || g.ignition > best.ignition || (g.ignition == best.ignition && g.massFlow > best.massFlow)) { best = g; bestN = m; }
                    }
                }
                if (best == null)
                {
                    if (s.optional) continue;
                    return "Nie znaleziono stopnia „" + s.name + "”" + (s.engine.Length > 0 ? " (silnik pasujący do „" + s.engine + "”" : " (") +
                           (s.IsBooster || s.IsCore ? ", odpalany na starcie)." : (prevIgn == int.MaxValue ? ", odpalany na starcie)." : ", odpalany po poprzednim stopniu).")) +
                           " Sprawdź staging albo wybierz inną rakietę z listy.";
                }
                // rdzeń musi być odrzucany później niż bloki boczne
                if (s.IsCore)
                    for (int k = 0; k < i; k++)
                        if (st[k] != null && d.stages[k].IsBooster && JetKey(best.jettison) >= JetKey(st[k].g.jettison))
                            return "Rdzeń („" + s.name + "”) i bloki boczne odpadają w tym samym stage'u – sprawdź staging (bloki boczne muszą być odrzucane pierwsze).";
                st[i] = new Found { g = best, n = s.IsBooster ? Math.Max(1, (int)Math.Round(bestN / (double)s.parts)) : 1 };
                used.Add(best);
                prevIgn = best.ignition;
            }
            int foundN = 0;
            foreach (Found f in st) if (f != null) foundN++;
            if (foundN == 0) return "Nie rozpoznano żadnego stopnia.";
            return null;
        }

        private static int JetKey(int jettison) { return jettison < 0 ? -1 : jettison; }

        /// <summary>Jak dobrze wzorzec pasuje do rakiety: 0 = wcale; więcej = lepiej (pełne dopasowanie, liczba stopni, liczba bloków).</summary>
        public static int Score(RocketDef d, List<EngineGroup> groups)
        {
            Found[] st = new Found[d.stages.Count];
            if (Identify(d, groups, st) != null) return 0;
            int sc = 0;
            for (int i = 0; i < st.Length; i++)
            {
                if (st[i] == null) continue;
                sc += 10;
                if (d.stages[i].rx != null) sc += 5;
                if (d.stages[i].IsBooster && st[i].n == d.stages[i].count) sc += 3;
            }
            return sc;
        }

        // ------------------------------------------------------------------ staging z wzorca

        public class Plan
        {
            public bool ok;
            public string error = "";
            public int lastStage;
            public string events = "";
            public readonly List<string> lines = new List<string>();
        }

        private static string Ev(string spec, int stage, out string what)
        {
            spec = (spec ?? "").Trim().ToLowerInvariant();
            what = "";
            if (spec.Length == 0 || stage < 0) return null;
            char k = 't';
            if (char.IsLetter(spec[0])) { k = spec[0]; spec = spec.Substring(1); }
            double v;
            if (!double.TryParse(spec.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v <= 0) return null;
            what = k == 'a' ? "na " + v.ToString("0.#", CultureInfo.InvariantCulture) + " km" : "T+" + v.ToString("0.#", CultureInfo.InvariantCulture) + " s";
            return (k == 'a' ? "a" : "") + v.ToString("0.###", CultureInfo.InvariantCulture) + ":" + stage;
        }

        /// <summary>
        /// Staging jak w prawdziwej rakiecie, wyliczony z OBECNEJ konstrukcji: stopnie wzorca → grupy silników (ich stage'e zapłonu
        /// i odrzutu), wieża SAS i owiewka → części w rakiecie. Wynik: ostatni stage rakiety (wszystko niżej to statek) i lista
        /// zdarzeń w zapisie „Stage o czasie” (np. „114:8 157:6 h1.5:7”).
        /// </summary>
        public static Plan StagingPlan(RocketDef d, List<EngineGroup> groups, List<Part> parts)
        {
            Plan pl = new Plan();
            Found[] st = new Found[d.stages.Count];
            string err = Identify(d, groups, st);
            if (err != null) { pl.error = err; return pl; }
            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> ev = new List<string>();
            int last = -1, prevFound = -1;
            for (int i = 0; i < st.Length; i++)
            {
                if (st[i] == null) continue;
                StageDef s = d.stages[i];
                EngineGroup g = st[i].g;
                // ostatni stage rakiety = stage tuż nad oddzieleniem statku od ostatniego stopnia startu (owiewka i wieża
                // mogą odpadać już w trakcie jego pracy – CZ-2F: II stopień stage 10, owiewka 9, oddzielenie Shenzhou 7)
                if (s.ascent) last = g.jettison >= 0 && g.jettison < g.ignition ? g.jettison + 1 : g.ignition;
                pl.lines.Add(s.name + ": zapłon w stage'u " + g.ignition + (g.jettison >= 0 ? ", odrzut w stage'u " + g.jettison : ", bez odrzutu") + (s.ascent ? "" : " – poza startem na orbitę (traktowany jak ładunek)"));
                if (s.hot > 0 && prevFound >= 0 && s.ascent)
                {
                    ev.Add("h" + s.hot.ToString("0.#", c) + ":" + g.ignition);
                    pl.lines.Add("   gorące rozdzielenie: zapłon „" + s.name + "” (stage " + g.ignition + "), gdy poprzedniemu stopniowi zostanie " + s.hot.ToString("0.#", c) + " s pracy");
                }
                if (s.jettisonAt.Length > 0)
                {
                    string w; string e = Ev(s.jettisonAt, g.jettison, out w);
                    if (e != null) { ev.Add(e); pl.lines.Add("   odrzut „" + s.name + "” (stage " + g.jettison + ") " + w); }
                }
                prevFound = i;
            }
            if (last < 0) { pl.error = "Żaden stopień wzorca nie bierze udziału w starcie."; return pl; }
            pl.lastStage = last;

            // wieża SAS: silnik na paliwo stałe o nazwie pasującej do lesRx; owiewka: ModuleProceduralFairing albo część „fairing”
            int les = -1, fair = -1;
            if (parts != null)
                foreach (Part p in parts)
                {
                    if (p == null || p.partInfo == null) continue;
                    string nm = p.partInfo.name + " " + p.partInfo.title;
                    if (p.inverseStage < last) continue;
                    bool engine = p.FindModuleImplementing<ModuleEngines>() != null;   // silnik wieży (paliwo stałe, PBAN…)
                    if (engine && d.lesRx.IsMatch(nm)) les = Math.Max(les, p.inverseStage);
                    bool fairMod = p.FindModuleImplementing<ModuleProceduralFairing>() != null;
                    bool sep = p.FindModuleImplementing<ModuleDecouple>() != null || p.FindModuleImplementing<ModuleAnchoredDecoupler>() != null || p.FindModuleImplementing<ModuleJettison>() != null;
                    if (fairMod || (sep && d.fairingRx.IsMatch(nm))) fair = Math.Max(fair, p.inverseStage);
                }
            if (d.les.Length > 0)
            {
                if (les >= 0) { string w; string e = Ev(d.les, les, out w); if (e != null) { ev.Add(e); pl.lines.Add("wieża SAS (stage " + les + ") " + w); } }
                else pl.lines.Add("wieża SAS: nie ma jej w rakiecie – pominięta");
            }
            if (d.fairing.Length > 0)
            {
                if (fair >= 0) { string w; string e = Ev(d.fairing, fair, out w); if (e != null) { ev.Add(e); pl.lines.Add("owiewka (stage " + fair + ") " + w); } }
                else pl.lines.Add("owiewka: nie znaleziono części owiewki nad stage'em " + last + " – pominięta");
            }
            pl.lines.Add("ostatni stage rakiety: " + last + " – stage " + (last - 1) + " i niższe to statek (oddzielenie po wejściu na orbitę)");
            if (d.stagingNote.Length > 0) pl.lines.Add(d.stagingNote);
            List<string> uniq = new List<string>();
            foreach (string e in ev) if (!uniq.Contains(e)) uniq.Add(e);   // wieża i owiewka w tym samym stage'u – raz
            pl.events = string.Join(" ", uniq.ToArray());
            pl.ok = true;
            return pl;
        }

        /// <summary>Najlepiej pasujący wzorzec (także wbudowane – Sojuz, Proton) albo null.</summary>
        public static RocketDef Best(List<EngineGroup> groups)
        {
            EnsureLoaded();
            RocketDef best = null; int bs = 0;
            foreach (RocketDef d in Defs)
            {
                int sc = Score(d, groups);
                if (d.builtin >= 0 && sc > 0) sc = Math.Max(1, sc - 1);   // przy remisie wygrywa dokładniejszy wzorzec z plików
                if (sc > bs) { bs = sc; best = d; }
            }
            return best;
        }

        // ------------------------------------------------------------------ dopasowanie

        public class Targets
        {
            public double[] prop, dry, isp, thr;   // na blok (booster) / na stopień
            public bool setDry = true;
            public static Targets From(RocketDef d)
            {
                int n = d.stages.Count;
                Targets t = new Targets { prop = new double[n], dry = new double[n], isp = new double[n], thr = new double[n] };
                for (int i = 0; i < n; i++) { StageDef s = d.stages[i]; t.prop[i] = s.prop; t.dry[i] = s.dry; t.isp[i] = s.isp; t.thr[i] = s.thrust; }
                return t;
            }
        }

        public static Result Solve(RocketDef d, List<EngineGroup> groups, double shipMass, double gSurf, Targets t)
        {
            int n = d.stages.Count;
            Result r = new Result { st = new Found[n], propAfter = new double[n], dryAfter = new double[n], timeAfter = new double[n] };
            string err = Identify(d, groups, r.st);
            if (err != null) { r.message = err; return r; }
            for (int i = 0; i < n; i++)
            {
                if (r.st[i] == null) continue;
                EngineGroup g = r.st[i].g;
                if (g.massFlow <= 0 || g.ispVac <= 0 || (t.prop[i] > 0 && (g.propCapacity <= 0 || g.tanks.Count == 0)))
                {
                    r.message = "Brak danych dla grupy „" + g.title + "” (stopień " + d.stages[i].name + ": paliwo, przepływ albo zbiorniki z modułem balancera).";
                    return r;
                }
            }

            bool clamped = false, dryClamped = false;
            double fixedMass = shipMass;
            for (int i = 0; i < n; i++) if (r.st[i] != null) fixedMass -= r.st[i].g.propMass + r.st[i].g.dryMass;
            r.restMass = fixedMass;

            double[] thrTot = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (r.st[i] == null) continue;
                EngineGroup g = r.st[i].g;
                int k = r.st[i].n;     // bloki boczne: wartości na blok × liczba znalezionych bloków
                thrTot[i] = t.thr[i] * k;
                if (t.prop[i] > 0)
                    r.propAfter[i] = EnergiaTemplate.Fit(g, t.prop[i] * k, t.isp[i], d.stages[i].time, thrTot[i] / Math.Max(1, g.engines), ref clamped);
                else
                {
                    float keep = g.fuel;   // bez własnego paliwa (silniki boosterów Atlasa) – tylko ISP i ciąg
                    EnergiaTemplate.Fit(g, 1, t.isp[i], d.stages[i].time, thrTot[i] / Math.Max(1, g.engines), ref clamped);
                    g.fuel = keep;
                    r.propAfter[i] = g.propCapacity > 0 && g.appliedFuel > 0 ? g.propCapacity * g.fuel / g.appliedFuel : 0;
                }
                if (t.setDry && t.dry[i] > 0) EnergiaTemplate.FitDry(g, t.dry[i] * k, ref dryClamped);
                r.dryAfter[i] = EnergiaTemplate.DryAfter(g);
                double flow = thrTot[i] / (t.isp[i] * G0);
                r.timeAfter[i] = t.prop[i] > 0 && flow > 0 ? r.propAfter[i] / flow : d.stages[i].time;
            }

            // stopień zasilający silniki bez własnego paliwa (Atlas: LR89 ciągnie ze zbiorników LR105) – odejmij ich zużycie
            for (int j = 0; j < n; j++)
            {
                if (r.st[j] == null || t.prop[j] > 0) continue;
                int i = NextWithProp(r, t, j);
                if (i < 0) continue;
                double fi = thrTot[i] / (t.isp[i] * G0), fj = thrTot[j] / (t.isp[j] * G0);
                if (fi > 0) r.timeAfter[i] = Math.Max(0, r.propAfter[i] - fj * d.stages[j].time) / fi;
            }

            r.massAfter = fixedMass;
            for (int i = 0; i < n; i++) if (r.st[i] != null) r.massAfter += r.propAfter[i] + r.dryAfter[i];
            r.dv = DeltaV(d, r, t, thrTot);

            double thrAsl = 0; int launch = int.MinValue;
            foreach (EngineGroup g in groups) if (ProtonTemplate.IsLiquid(g)) launch = Math.Max(launch, g.ignition);
            foreach (EngineGroup g in groups)
                if (g.ignition == launch) thrAsl += g.thrustAsl * (g.appliedThrust > 0 ? g.thrust / g.appliedThrust : 1.0);
            r.twrAfter = r.massAfter > 0 && gSurf > 0 ? thrAsl / (r.massAfter * gSurf) : 0;

            CultureInfo c = CultureInfo.InvariantCulture;
            string msg = "Rozpoznano: " + d.name;
            for (int i = 0; i < n; i++)
            {
                StageDef s = d.stages[i];
                if (r.st[i] == null) { msg += "\n" + s.name + ": brak w tej rakiecie (stopień opcjonalny) – pominięty"; continue; }
                EngineGroup g = r.st[i].g;
                int k = r.st[i].n;
                double pT = t.prop[i] * k, dT = t.dry[i] * k;
                msg += "\n" + s.name + (s.IsBooster ? " (" + k + " bl.)" : "") + " – " + g.engines + "× " + g.title + ": ";
                msg += t.prop[i] > 0 ? "paliwo ×" + g.fuel.ToString("F2") + " (" + r.propAfter[i].ToString("F1") + " t, oryg. " + pT.ToString("F1") + "), " : "paliwo bez zmian (bez własnych zbiorników), ";
                msg += "ISP ×" + g.isp.ToString("F2") + ", ciąg ×" + g.thrust.ToString("F2");
                if (t.setDry && t.dry[i] > 0)
                    msg += ", masa pustych ×" + g.dry.ToString("F2") + " / silników ×" + g.engMass.ToString("F2") + " → konstrukcja " + r.dryAfter[i].ToString("F1") + " t (oryg. " + dT.ToString("F1") + ")";
                if (t.prop[i] > 0) msg += ", czas pracy ≈" + r.timeAfter[i].ToString("F0") + " s" + (s.time > 0 ? " (oryg. " + s.time.ToString("F0") + ")" : "");
                if (s.IsBooster && k != s.count) msg += "  [w oryginale " + s.count + " bloki – ustawiono " + k + " × wartości jednego bloku]";
            }
            msg += "\nŁadunek i reszta nad stopniami wzorca: " + r.restMass.ToString("F1") + " t" + (d.payload.Length > 0 ? " (oryg.: " + d.payload + ")" : "") + " – bez zmian" +
                   "\nMasa startowa " + r.massAfter.ToString("F0") + " t" + (d.liftoffMass > 0 ? " (oryg. ≈" + d.liftoffMass.ToString("F0", c) + ")" : "") +
                   " · TWR " + r.twrAfter.ToString("F2") + (d.liftoffTwr > 0 ? " (oryg. ≈" + d.liftoffTwr.ToString("F2", c) + ")" : "") +
                   " · Δv w próżni (bez strat) ≈ " + r.dv.ToString("F0") + " m/s";
            if (clamped) msg += "\nUWAGA: część mnożników doszła do limitu suwaków – nie wszystkie cele osiągnięte.";
            if (dryClamped) msg += "\nUWAGA: konstrukcji nie da się dokładnie ustawić zbiornikami i silnikami (limit 0,1–2×) – resztę ważą inne części (adaptery, odłączacze).";
            r.message = msg;
            r.ok = true;
            return r;
        }

        /// <summary>
        /// Δv w próżni z prostej symulacji (krok 0,5 s): wszystkie stopnie odpalane na starcie pracują razem, blok bez paliwa
        /// odpada z suchą masą, kolejny stopień szeregowy zapala się po wypaleniu poprzedniego. Stopień bez własnego paliwa
        /// (prop = 0, np. silniki boosterów Atlasa) ciągnie z paliwa następnego stopnia przez swój czas pracy.
        /// </summary>
        private static double DeltaV(RocketDef d, Result r, Targets t, double[] thrTot)
        {
            int n = d.stages.Count;
            double[] prop = new double[n], flow = new double[n], left = new double[n];
            bool[] on = new bool[n], gone = new bool[n];
            double m = r.massAfter, dv = 0, dt = 0.5;
            int firstSerial = -1;
            for (int i = 0; i < n; i++)
            {
                if (r.st[i] == null) { gone[i] = true; continue; }
                prop[i] = t.prop[i] > 0 ? r.propAfter[i] : 0;
                flow[i] = thrTot[i] / (t.isp[i] * G0);                      // t/s
                left[i] = t.prop[i] > 0 ? double.MaxValue : Math.Max(1, d.stages[i].time);
                if (d.stages[i].IsBooster || d.stages[i].IsCore) on[i] = true;
                else if (firstSerial < 0) { firstSerial = i; on[i] = true; }
            }
            for (int step = 0; step < 200000; step++)
            {
                double thr = 0, mdot = 0;
                int active = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!on[i] || gone[i]) continue;
                    active++;
                    thr += thrTot[i];
                    mdot += flow[i];
                }
                if (active == 0 || m <= 0) break;
                dv += thr / m * dt;
                // zużycie paliwa
                for (int i = 0; i < n; i++)
                {
                    if (!on[i] || gone[i]) continue;
                    double use = flow[i] * dt;
                    if (t.prop[i] > 0) prop[i] -= use;
                    else
                    {
                        left[i] -= dt;
                        int j = NextWithProp(r, t, i);            // z czyich zbiorników
                        if (j >= 0) prop[j] -= use;
                    }
                    m -= use;
                }
                // odrzuty i zapłony
                for (int i = 0; i < n; i++)
                {
                    if (!on[i] || gone[i]) continue;
                    bool empty = t.prop[i] > 0 ? prop[i] <= 0 : left[i] <= 0;
                    if (!empty) continue;
                    // stopień, który ciągnął z cudzych zbiorników, odpada tylko z suchą masą
                    gone[i] = true;
                    m -= r.dryAfter[i];
                    // zapal następny stopień szeregowy, gdy nic już nie pracuje
                    bool any = false;
                    for (int k = 0; k < n; k++) if (on[k] && !gone[k]) any = true;
                    if (!any)
                        for (int k = 0; k < n; k++)
                            if (!on[k] && !gone[k]) { on[k] = true; break; }
                }
            }
            return dv;
        }

        private static int NextWithProp(Result r, Targets t, int i)
        {
            for (int j = i + 1; j < t.prop.Length; j++) if (r.st[j] != null && t.prop[j] > 0) return j;
            return -1;
        }

        private const double G0 = 9.80665;
    }
}
