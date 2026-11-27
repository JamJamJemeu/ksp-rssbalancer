using System;
using System.Collections.Generic;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Wzorzec Proton-K (8K82K, trzy stopnie, jak przy TKS, Ałmazie, Salutach). Stopnie szeregowe:
    ///   I   8S810  – 6 × RD-253 (odpalane na starcie),
    ///   II  8S811  – 3 × RD-0210 + RD-0211 (odpalane przy odrzucie I stopnia – gorące rozdzielenie),
    ///   III 8S812  – RD-0212 (RD-0213 + 4-komorowy sterujący RD-0214; odpalany przy odrzucie II stopnia).
    /// Rozpoznanie po strukturze stagingu: II stopień odpala się w stage'u odrzutu I, III w stage'u odrzutu II
    /// (silniki na paliwo stałe – rakietki odrzutu, SAS – są pomijane).
    /// Dla każdego stopnia, jak w Energii: paliwo × → masa paliwa, ISP × → ISP w próżni, ciąg × → PRAWDZIWY ciąg w próżni
    /// (czas pracy wynika z paliwa i ciągu – przy danych oryginału wychodzi 124 / 206 / 238 s), masa pustych → konstrukcja.
    /// Ładunek (TKS, Ałmaz…) i wszystko nad III stopniem zostaje bez zmian – mod tylko pokazuje jego masę obok oryginału.
    /// </summary>
    public static class ProtonTemplate
    {
        // Proton-K 8K82K (Wikipedia / astronautix, russianspaceweb):
        //                                       I         II        III
        public static readonly double[] Prop = { 419.41, 156.11, 46.56 };   // t paliwa (brutto − sucha masa)
        public static readonly double[] Dry = { 31.10, 11.72, 4.19 };       // t konstrukcji
        public static readonly double[] IspVac = { 316, 327, 325 };         // s
        public static readonly double[] ThrustVac = { 10470, 2399, 613.8 }; // kN, cały stopień w próżni
        public static readonly double[] Time = { 124, 206, 238 };           // s – tylko do raportu (wynika z powyższych)
        public static readonly int[] Engines = { 6, 4, 1 };
        public const double TksMass = 21.62;      // t, TKS z SAS i owiewką (w orbicie 17,51 t)
        public const double LiftoffMass = 690.6;  // t: 450,5 + 167,8 + 50,7 + 21,6
        public const double LiftoffTwr = 1.39;    // 6 × RD-253: ≈ 9,44 MN przy ziemi (ISP 285 s z 316 s)
        public static readonly string[] Names = { "I stopień (6 × RD-253)", "II stopień (RD-0210/0211)", "III stopień (RD-0212)" };

        public class Targets
        {
            public double[] prop = (double[])Prop.Clone(), dry = (double[])Dry.Clone(), isp = (double[])IspVac.Clone(), thr = (double[])ThrustVac.Clone();
            public bool setDry = true;
        }

        public class Result
        {
            public bool ok;
            public string message = "";
            public EngineGroup[] st = new EngineGroup[3];
            public double massAfter, twrAfter, restMass;
            public double[] dv = new double[3], dryAfter = new double[3], propAfter = new double[3], timeAfter = new double[3];
        }

        public static bool IsLiquid(EngineGroup g)
        {
            if (g == null) return false;
            foreach (Part p in g.parts)
                if (p != null)
                    foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                        if (e.propellants != null && !e.propellants.Exists(pr => pr.name == "SolidFuel")) return true;
            return false;
        }

        /// <summary>Stopnie I–III według stagingu. Zwraca komunikat błędu albo null.</summary>
        public static string Identify(List<EngineGroup> groups, EngineGroup[] st)
        {
            if (groups == null || groups.Count == 0) return "Brak silników na statku.";
            int launch = int.MinValue;
            foreach (EngineGroup g in groups) if (IsLiquid(g)) launch = Math.Max(launch, g.ignition);
            EngineGroup s1 = null;
            foreach (EngineGroup g in groups)
                if (IsLiquid(g) && g.ignition == launch && (s1 == null || g.engines > s1.engines)) s1 = g;
            if (s1 == null) return "Nie znaleziono silników na paliwo ciekłe odpalanych na starcie.";
            foreach (EngineGroup g in groups)
                if (g != s1 && IsLiquid(g) && g.ignition == launch)
                    return "Na starcie pracuje więcej niż jedna grupa silników (boczne bloki?) – to nie wygląda na Protona. Dla R-7 / Energii użyj ich zakładek.";
            st[0] = s1;
            for (int i = 1; i < 3; i++)
            {
                EngineGroup prev = st[i - 1], best = null;
                if (prev == null || prev.jettison < 0) break;
                foreach (EngineGroup g in groups)
                    if (g != prev && IsLiquid(g) && g.ignition == prev.jettison && g.jettison != prev.jettison && (best == null || g.massFlow > best.massFlow)) best = g;
                st[i] = best;
            }
            if (st[1] == null) return "Nie znaleziono II stopnia: grupa silników odpalana w stage'u odrzutu I stopnia (" + s1.jettison + "). Sprawdź staging.";
            return null;
        }

        /// <summary>Ustawia suwaki stopni (bez stosowania). shipMass – masa bez klamer [t].</summary>
        public static Result Solve(List<EngineGroup> groups, double shipMass, double gSurf, Targets t)
        {
            Result r = new Result();
            string err = Identify(groups, r.st);
            if (err != null) { r.message = err; return r; }
            int n = r.st[2] != null ? 3 : 2;
            for (int i = 0; i < n; i++)
            {
                EngineGroup g = r.st[i];
                if (g.massFlow <= 0 || g.ispVac <= 0 || g.propCapacity <= 0 || g.tanks.Count == 0)
                {
                    r.message = "Brak danych dla grupy „" + g.title + "” (paliwo, przepływ albo zbiorniki z modułem balancera).";
                    return r;
                }
            }

            bool clamped = false, dryClamped = false;
            double fixedMass = shipMass;
            for (int i = 0; i < n; i++) fixedMass -= r.st[i].propMass + r.st[i].dryMass;
            r.restMass = fixedMass;

            for (int i = 0; i < n; i++)
            {
                EngineGroup g = r.st[i];
                // EnergiaTemplate.Fit ustawia ciąg na silnik – tu cel jest dla całego stopnia (III: RD-0213 + sterujące)
                r.propAfter[i] = EnergiaTemplate.Fit(g, t.prop[i], t.isp[i], Time[i], t.thr[i] / Math.Max(1, g.engines), ref clamped);
                if (t.setDry) EnergiaTemplate.FitDry(g, t.dry[i], ref dryClamped);
                r.dryAfter[i] = EnergiaTemplate.DryAfter(g);
                r.timeAfter[i] = r.propAfter[i] / (t.thr[i] / (t.isp[i] * G0));
            }

            r.massAfter = fixedMass;
            for (int i = 0; i < n; i++) r.massAfter += r.propAfter[i] + r.dryAfter[i];

            // Δv w próżni stopień po stopniu
            double m = r.massAfter;
            for (int i = 0; i < n; i++)
            {
                double m1 = m - r.propAfter[i];
                if (m1 > 0) r.dv[i] = t.isp[i] * G0 * Math.Log(m / m1);
                m = m1 - r.dryAfter[i];
            }

            // ciąg przy ziemi rośnie z Ciągiem × (ISP × zmienia przepływ, ciąg zostaje)
            EngineGroup s1 = r.st[0];
            double k = s1.appliedThrust > 0 ? s1.thrust / s1.appliedThrust : 1.0;
            r.twrAfter = r.massAfter > 0 && gSurf > 0 ? s1.thrustAsl * k / (r.massAfter * gSurf) : 0;

            string msg = "Rozpoznano Proton: " + (n == 3 ? "3 stopnie (Proton-K)" : "2 stopnie (bez III stopnia – UR-500 / 8K82?)");
            for (int i = 0; i < n; i++)
            {
                EngineGroup g = r.st[i];
                msg += "\n" + Names[i] + " – " + g.engines + "× " + g.title + ": paliwo ×" + g.fuel.ToString("F2") + " (" + r.propAfter[i].ToString("F1") + " t, oryg. " + t.prop[i].ToString("F1") +
                       "), ISP ×" + g.isp.ToString("F2") + ", ciąg ×" + g.thrust.ToString("F2") + ", masa pustych ×" + g.dry.ToString("F2") + " / silników ×" + g.engMass.ToString("F2") +
                       " → konstrukcja " + r.dryAfter[i].ToString("F1") + " t (oryg. " + t.dry[i].ToString("F1") + "), czas pracy ≈" + r.timeAfter[i].ToString("F0") + " s (oryg. " + Time[i].ToString("F0") + ")";
                if (g.engines != Engines[i]) msg += "  [w oryginale " + Engines[i] + " silnik(i/ów) – ciąg ustawiony dla całego stopnia]";
            }
            msg += "\nŁadunek i reszta nad III stopniem: " + r.restMass.ToString("F1") + " t (TKS z SAS i owiewką: " + TksMass.ToString("F1") + " t) – bez zmian" +
                   "\nMasa startowa " + r.massAfter.ToString("F0") + " t (oryg. z TKS ≈" + LiftoffMass.ToString("F0") + ") · TWR " + r.twrAfter.ToString("F2") + " (oryg. ≈" + LiftoffTwr.ToString("F2") + ")" +
                   " · Δv w próżni: " + r.dv[0].ToString("F0") + " + " + r.dv[1].ToString("F0") + (n == 3 ? " + " + r.dv[2].ToString("F0") : "") + " = " + (r.dv[0] + r.dv[1] + r.dv[2]).ToString("F0") + " m/s";
            if (clamped) msg += "\nUWAGA: część mnożników doszła do limitu suwaków (paliwo do ×5, ISP i ciąg do ×5) – nie wszystkie cele osiągnięte.";
            if (dryClamped) msg += "\nUWAGA: konstrukcji nie da się dokładnie ustawić zbiornikami i silnikami (limit 0,1–2×) – resztę ważą inne części (adaptery, odłączacze).";
            if (Math.Abs(r.restMass - TksMass) > 8) msg += "\nŁadunek waży " + r.restMass.ToString("F1") + " t – inaczej niż TKS (21,6 t); Proton-K wynosił do ~20 t na niską orbitę.";
            r.message = msg;
            r.ok = true;
            return r;
        }

        private const double G0 = 9.80665;
    }
}
