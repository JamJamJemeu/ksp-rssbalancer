using System.Collections.Generic;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Grupa silników = silniki odpalane w tym samym stage'u i odrzucane w tym samym stage'u.
    /// Dzięki temu np. boczne bloki Sojuza (odpalenie 5 → odrzut 4) i rdzeń (5 → 3)
    /// są osobnymi grupami, nawet jeśli mają tę samą część silnika.
    /// </summary>
    public class EngineGroup
    {
        public int ignition;
        public int jettison;           // -1 = nigdy nie odrzucana (ostatni stopień)
        public int engines;
        public string title = "";
        public double thrustAsl;       // aktualny ciąg grupy na poziomie morza [kN]
        public readonly List<Part> parts = new List<Part>(); // części z silnikami w tej grupie

        public float isp = 1f, thrust = 1f;               // suwaki
        public float appliedIsp = 1f, appliedThrust = 1f; // to, co jest teraz na silnikach
        public bool mixed;                                 // silniki w grupie mają różne wartości
        public string targetTime = "";

        public string Key { get { return ignition + ":" + jettison; } }

        public string Label
        {
            get
            {
                return "Odpalenie " + ignition + " → " + (jettison >= 0 ? "odrzut " + jettison : "do końca") +
                       "  ·  " + engines + "× " + title;
            }
        }

        public double BurnTimeNow { get { return StockDeltaV.GroupBurnTime(ignition, jettison); } }

        /// <summary>Czas pracy ∝ ISP / ciąg (przepływ paliwa = ciąg / ISP). Dokładne dla grupy z własnym paliwem.</summary>
        public double BurnTimeAfter(double now)
        {
            return now * (isp / appliedIsp) * (appliedThrust / thrust);
        }

        public bool Changed
        {
            get { return !Mathf.Approximately(isp, appliedIsp) || !Mathf.Approximately(thrust, appliedThrust); }
        }
    }

    public static class EngineGroups
    {
        public static bool IsDecoupler(Part p)
        {
            return p.FindModuleImplementing<ModuleDecouple>() != null ||
                   p.FindModuleImplementing<ModuleAnchoredDecoupler>() != null;
        }

        /// <summary>
        /// Stage, w którym część zostaje odrzucona: najbliższy odłączacz w górę drzewa części
        /// (licząc też samą część – np. silnik albo zbiornik z wbudowanym odłączaczem).
        /// </summary>
        public static int JettisonStage(Part p)
        {
            Part d = JettisonDecoupler(p);
            return d != null ? d.inverseStage : -1;
        }

        /// <summary>Najbliższy odłączacz w górę drzewa części (albo sama część, jeśli ma wbudowany odłączacz).</summary>
        public static Part JettisonDecoupler(Part p)
        {
            Part cur = p;
            int guard = 0;
            while (cur != null && guard++ < 500)
            {
                if (IsDecoupler(cur)) return cur;
                cur = cur.parent;
            }
            return null;
        }

        /// <summary>Odpala odłączacz bezpośrednio (bez aktywowania całego stage'u).</summary>
        public static void Decouple(Part decoupler)
        {
            if (decoupler == null) return;
            foreach (ModuleDecouple d in decoupler.FindModulesImplementing<ModuleDecouple>())
                if (!d.isDecoupled) d.Decouple();
            foreach (ModuleAnchoredDecoupler a in decoupler.FindModulesImplementing<ModuleAnchoredDecoupler>())
                if (!a.isDecoupled) a.Decouple();
        }

        /// <summary>Ciąg części na poziomie morza (1 atm) [kN], z obecnymi mnożnikami i limiterem. Przy silnikach wielotrybowych – najmocniejszy tryb.</summary>
        public static double ThrustAsl(Part p)
        {
            double best = 0.0;
            foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
            {
                if (e.atmosphereCurve == null) continue;
                float vac = e.atmosphereCurve.Evaluate(0f);
                float asl = e.atmosphereCurve.Evaluate(1f);
                if (vac <= 0f) continue;
                double f = e.maxThrust * (asl / vac) * (e.thrustPercentage / 100.0);
                if (f > best) best = f;
            }
            return best;
        }

        /// <summary>Buduje grupy z części statku. Niezatwierdzone zmiany suwaków z poprzedniej listy zostają.</summary>
        public static List<EngineGroup> Build(List<Part> parts, List<EngineGroup> previous)
        {
            Dictionary<string, EngineGroup> old = new Dictionary<string, EngineGroup>();
            if (previous != null)
                foreach (EngineGroup g in previous) old[g.Key] = g;

            Dictionary<string, EngineGroup> found = new Dictionary<string, EngineGroup>();
            if (parts != null)
            {
                foreach (Part p in parts)
                {
                    List<ModuleEngineBalancer> mods = p.FindModulesImplementing<ModuleEngineBalancer>();
                    if (mods == null || mods.Count == 0) continue;
                    ModuleEngineBalancer b = mods[0];

                    int ign = p.inverseStage;
                    int jet = JettisonStage(p);
                    string key = ign + ":" + jet;

                    EngineGroup g;
                    if (!found.TryGetValue(key, out g))
                    {
                        EngineGroup prev;
                        old.TryGetValue(key, out prev);
                        bool keepSliders = prev != null && prev.Changed;

                        g = new EngineGroup
                        {
                            ignition = ign,
                            jettison = jet,
                            title = p.partInfo != null ? p.partInfo.title : p.name,
                            appliedIsp = b.ispMultiplier,
                            appliedThrust = b.thrustMultiplier,
                            targetTime = prev != null ? prev.targetTime : ""
                        };
                        g.isp = keepSliders ? prev.isp : g.appliedIsp;
                        g.thrust = keepSliders ? prev.thrust : g.appliedThrust;
                        found[key] = g;
                    }
                    else if (!Mathf.Approximately(b.ispMultiplier, g.appliedIsp) || !Mathf.Approximately(b.thrustMultiplier, g.appliedThrust))
                    {
                        g.mixed = true;
                    }

                    g.engines++;
                    g.thrustAsl += ThrustAsl(p);
                    g.parts.Add(p);
                }
            }

            List<EngineGroup> list = new List<EngineGroup>(found.Values);
            // Jak w VAB: najpierw to, co odpala się na starcie; przy równym odpaleniu – to, co odpada wcześniej.
            list.Sort((a, c) => a.ignition != c.ignition ? c.ignition.CompareTo(a.ignition) : c.jettison.CompareTo(a.jettison));
            return list;
        }

        public static void Apply(List<Part> parts, List<EngineGroup> groups, bool resetLimiter)
        {
            if (parts == null) return;

            Dictionary<string, EngineGroup> byKey = new Dictionary<string, EngineGroup>();
            foreach (EngineGroup g in groups) byKey[g.Key] = g;

            foreach (Part p in parts)
            {
                List<ModuleEngineBalancer> mods = p.FindModulesImplementing<ModuleEngineBalancer>();
                if (mods != null && mods.Count > 0)
                {
                    EngineGroup g;
                    if (byKey.TryGetValue(p.inverseStage + ":" + JettisonStage(p), out g))
                    {
                        foreach (ModuleEngineBalancer b in mods)
                        {
                            b.ispMultiplier = g.isp;
                            b.thrustMultiplier = g.thrust;
                            b.ApplyMultipliers();
                        }
                    }
                }

                if (resetLimiter)
                    foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                        e.thrustPercentage = 100f;
            }

            foreach (EngineGroup g in groups) { g.appliedIsp = g.isp; g.appliedThrust = g.thrust; g.mixed = false; }
            ModuleEngineBalancer.NotifyEditor();
        }
    }
}
