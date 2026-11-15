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
        public double propMass;        // paliwo dostępne dla grupy [t] (zbiorniki odrzucane razem z nią)
        public double massFlow;        // przepływ paliwa przy pełnym ciągu [t/s]
        public double ispVac;          // ISP w próżni głównego silnika grupy [s] (z obecnymi mnożnikami)
        public readonly List<Part> parts = new List<Part>(); // części z silnikami w tej grupie

        public float isp = 1f, thrust = 1f;               // suwaki
        public float appliedIsp = 1f, appliedThrust = 1f; // to, co jest teraz na silnikach

        // Zbiorniki odrzucane razem z grupą (moduł ModuleTankBalancer)
        public readonly List<ModuleTankBalancer> tanks = new List<ModuleTankBalancer>();
        public float dry = 1f, fuel = 1f;                 // suwaki
        public float appliedDry = 1f, appliedFuel = 1f;   // to, co jest teraz na zbiornikach
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

        /// <summary>
        /// Czas pracy liczony przez mod: paliwo ÷ przepływ (bez stockowego kalkulatora Δv,
        /// który myli się przy stagingu z osobnym stage'em klamer startowych).
        /// </summary>
        public double BurnTimeNow { get { return massFlow > 0 ? propMass / massFlow : 0.0; } }

        /// <summary>Czas pracy ∝ ISP / ciąg (przepływ paliwa = ciąg / ISP). Dokładne dla grupy z własnym paliwem.</summary>
        public double BurnTimeAfter(double now)
        {
            return now * (fuel / appliedFuel) * (isp / appliedIsp) * (appliedThrust / thrust);
        }

        public bool EnginesChanged
        {
            get { return !Mathf.Approximately(isp, appliedIsp) || !Mathf.Approximately(thrust, appliedThrust); }
        }

        public bool TanksChanged
        {
            get { return !Mathf.Approximately(dry, appliedDry) || !Mathf.Approximately(fuel, appliedFuel); }
        }

        public bool Changed { get { return EnginesChanged || TanksChanged; } }
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

        public const double G0 = 9.80665;

        /// <summary>Przepływ paliwa części przy pełnym ciągu [t/s], z mnożnikami i limiterem. Przy silnikach wielotrybowych – najmocniejszy tryb.</summary>
        public static double MassFlow(Part p)
        {
            double best = 0.0;
            foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
            {
                double f = e.maxFuelFlow * (e.thrustPercentage / 100.0);
                if (f > best) best = f;
            }
            return best;
        }

        /// <summary>Ciąg części na poziomie morza (1 atm) [kN] = przepływ × ISP(1 atm) × g0.</summary>
        public static double ThrustAsl(Part p)
        {
            double best = 0.0;
            foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
            {
                if (e.atmosphereCurve == null) continue;
                double f = e.maxFuelFlow * (e.thrustPercentage / 100.0) * e.atmosphereCurve.Evaluate(1f) * G0;
                if (f > best) best = f;
            }
            return best;
        }

        /// <summary>Nazwy paliw, których używają silniki z listy (bez prądu).</summary>
        public static HashSet<string> PropellantNames(List<Part> engineParts)
        {
            HashSet<string> names = new HashSet<string>();
            foreach (Part p in engineParts)
            {
                if (p == null) continue;
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.propellants != null)
                        foreach (Propellant pr in e.propellants)
                            if (pr != null && pr.name != "ElectricCharge") names.Add(pr.name);
            }
            return names;
        }

        /// <summary>
        /// Masa paliwa [t] w częściach odrzucanych razem przez ten sam odłączacz
        /// (np. zbiornik bocznego bloku; zbiornik rdzenia). Zakłada brak przelewu paliwa między blokami a rdzeniem – jak w R-7.
        /// </summary>
        public static double FuelMass(List<Part> allParts, Part decoupler, HashSet<string> names)
        {
            return FuelMass(allParts, new HashSet<Part> { decoupler }, names);
        }

        /// <summary>Jak wyżej, ale dla kilku odłączaczy naraz – np. cztery boczne bloki, każdy z własnym odłączaczem.</summary>
        public static double FuelMass(List<Part> allParts, HashSet<Part> decouplers, HashSet<string> names)
        {
            double m = 0.0;
            foreach (Part p in allParts)
            {
                if (p == null || !decouplers.Contains(JettisonDecoupler(p))) continue;
                foreach (PartResource r in p.Resources)
                    if (r != null && r.info != null && names.Contains(r.resourceName))
                        m += r.amount * r.info.density;
            }
            return m;
        }

        private static bool IsClampRelated(Part p)
        {
            Part cur = p;
            int guard = 0;
            while (cur != null && guard++ < 500)
            {
                if (cur.FindModuleImplementing<LaunchClamp>() != null) return true;
                cur = cur.parent;
            }
            return false;
        }

        /// <summary>TWR na starcie: ciąg grup odpalanych na starcie (1 atm) ÷ ciężar rakiety bez klamer startowych.</summary>
        public static double LiftoffTwr(List<Part> parts, double totalMass, List<EngineGroup> groups, CelestialBody body)
        {
            if (parts == null || groups == null || groups.Count == 0 || body == null || totalMass <= 0) return 0.0;

            double mass = totalMass;
            foreach (Part p in parts)
                if (p != null && IsClampRelated(p))
                    mass -= p.mass + p.GetResourceMass();
            if (mass <= 0) return 0.0;

            int launch = int.MinValue;
            foreach (EngineGroup g in groups) launch = System.Math.Max(launch, g.ignition);

            double thrust = 0.0;
            foreach (EngineGroup g in groups)
                if (g.ignition == launch) thrust += g.thrustAsl;

            double gSurf = body.gravParameter / (body.Radius * body.Radius);
            return thrust / (mass * gSurf);
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

            // Paliwo i przepływ każdej grupy.
            foreach (EngineGroup g in found.Values)
            {
                g.massFlow = 0.0;
                g.ispVac = 0.0;
                double bestFlow = -1.0;
                foreach (Part p in g.parts)
                {
                    double f = MassFlow(p);
                    g.massFlow += f;
                    // ISP z silnika o największym przepływie (główny silnik, nie vernier).
                    if (f > bestFlow)
                        foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                            if (e.atmosphereCurve != null) { bestFlow = f; g.ispVac = e.atmosphereCurve.Evaluate(0f); break; }
                }
                if (parts != null && g.parts.Count > 0)
                {
                    // Każdy silnik grupy może mieć własny odłączacz (np. 4 boczne bloki) – liczymy paliwo ze wszystkich.
                    HashSet<Part> decs = new HashSet<Part>();
                    foreach (Part p in g.parts) decs.Add(JettisonDecoupler(p));
                    g.propMass = FuelMass(parts, decs, PropellantNames(g.parts));

                    // Zbiorniki tej grupy i ich obecne mnożniki.
                    foreach (Part p in parts)
                    {
                        if (p == null || !decs.Contains(JettisonDecoupler(p))) continue;
                        ModuleTankBalancer t = p.FindModuleImplementing<ModuleTankBalancer>();
                        if (t != null) g.tanks.Add(t);
                    }
                    if (g.tanks.Count > 0)
                    {
                        EngineGroup prev;
                        old.TryGetValue(g.Key, out prev);
                        g.appliedDry = g.tanks[0].dryMassMultiplier;
                        g.appliedFuel = g.tanks[0].fuelMultiplier;
                        bool keep = prev != null && prev.TanksChanged;
                        g.dry = keep ? prev.dry : g.appliedDry;
                        g.fuel = keep ? prev.fuel : g.appliedFuel;
                    }
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

            // Zbiorniki – tylko w grupach, w których ruszono suwaki zbiorników
            // (kilka grup może dzielić te same zbiorniki, np. boczne bloki i ich silniczki odrzutu).
            foreach (EngineGroup g in groups)
            {
                if (!g.TanksChanged) continue;
                foreach (ModuleTankBalancer t in g.tanks)
                {
                    if (t == null) continue;
                    t.dryMassMultiplier = g.dry;
                    t.fuelMultiplier = g.fuel;
                    t.ApplyDry();
                    t.ApplyFuel();
                }
            }

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

            foreach (EngineGroup g in groups)
            {
                g.appliedIsp = g.isp; g.appliedThrust = g.thrust; g.mixed = false;
                g.appliedDry = g.dry; g.appliedFuel = g.fuel;
            }
            ModuleEngineBalancer.NotifyEditor();
        }
    }
}
