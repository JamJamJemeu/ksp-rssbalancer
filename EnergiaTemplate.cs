using System;
using System.Collections.Generic;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Wzorzec Energia (11K25) z Buranem. Układ jak w R-7 (boczne bloki + rdzeń odpalane razem), ale:
    ///   • boczne bloki (Blok A, RD-170, nafta) i rdzeń (Blok Ц, RD-0120, wodór) mają RÓŻNE ISP,
    ///   • rdzeń nie wchodzi na orbitę – po jego wyłączeniu orbiter jest na torze z ujemnym perygeum
    ///     i dopiero własne silniki (ОДУ) wprowadzają go na orbitę.
    ///
    /// Na grupę są trzy cele: masa paliwa, ISP w próżni, czas pracy – rozwiązanie wprost:
    ///   Paliwo × → masa paliwa,  ISP × → ISP w próżni,  Ciąg × → czas pracy (przepływ = paliwo / czas).
    /// TWR na starcie wychodzi z tego sam; mod pokazuje go obok wartości oryginału (≈1,48).
    /// Opcjonalnie masa pustych zbiorników bloków i rdzenia dobierana tak, by masa startowa była jak w oryginale.
    /// </summary>
    public static class EnergiaTemplate
    {
        // Energia-Buran (lot 1K1, 15.11.1988, buran-energia.com, Wikipedia):
        public const double BoosterTime = 143.0;    // wyłączenie RD-170 T+142 s, odrzut T+144 s
        public const double CoreTime = 466.0;       // wyłączenie RD-0120 T+466 s
        public const double BoosterIspVac = 338.0;  // RD-170: 309 s / 338 s
        public const double CoreIspVac = 454.0;     // RD-0120: 359 s / 454 s
        public const double BoosterProp = 1228.0;   // I stopień: 886,8 t tlenu + 341,2 t nafty (4 bloki razem)
        public const double CoreProp = 703.6;       // II stopień: 602,8 t tlenu + 100,9 t wodoru
        public const double LiftoffMass = 2375.0;   // t, z Buranem
        public const double LiftoffTwr = 1.48;      // ≈ (4 × 7,23 + 5,8) MN / (2375 t · g)

        /// <summary>Energia ma rdzeń wodorowy – po tym odróżniamy ją od Sojuza (rdzeń na naftę, ≈320 s).</summary>
        public static bool Looks(SoyuzTemplate.Result id)
        {
            return id != null && id.ok && id.core != null && id.core.ispVac > 400.0;
        }

        public class Targets
        {
            public double tB = BoosterTime, tC = CoreTime;
            public double ispB = BoosterIspVac, ispC = CoreIspVac;
            public double propB = BoosterProp, propC = CoreProp;
            public bool fitMass = true;
            public double mass = LiftoffMass;
            public float dry = 1f;           // gdy fitMass = false
        }

        public class Result
        {
            public bool ok;
            public string message = "";
            public SoyuzTemplate.Result id;
            public double massAfter, twrAfter;
        }

        /// <summary>
        /// Ustawia suwaki bloków i rdzenia (bez stosowania).
        /// shipMass – obecna masa statku bez klamer startowych [t] (z niej liczona przewidywana masa i TWR).
        /// </summary>
        public static Result Solve(List<EngineGroup> groups, double shipMass, double gSurf, Targets t)
        {
            Result r = new Result();
            r.id = SoyuzTemplate.Identify(groups);
            if (!r.id.ok) { r.message = r.id.message; return r; }

            EngineGroup B = r.id.boosters, C = r.id.core;
            foreach (EngineGroup g in new[] { B, C })
            {
                if (g.massFlow <= 0 || g.ispVac <= 0 || g.propCapacity <= 0 || g.tanks.Count == 0)
                {
                    r.message = "Brak danych dla grupy „" + g.title + "” (paliwo, przepływ albo zbiorniki z modułem balancera).";
                    return r;
                }
            }

            bool clamped = false;
            double propB = Fit(B, t.propB, t.ispB, t.tB, ref clamped);
            double propC = Fit(C, t.propC, t.ispC, t.tC, ref clamped);

            // Masa: wszystko poza paliwem i pustymi zbiornikami bloków i rdzenia zostaje bez zmian.
            double dryNow = B.tankDryBase * B.appliedDry + C.tankDryBase * C.appliedDry;
            double fixedMass = shipMass - B.propMass - C.propMass - dryNow;
            double dryBase = B.tankDryBase + C.tankDryBase;

            float dry = t.dry;
            if (t.fitMass && dryBase > 0)
                dry = (float)((t.mass - fixedMass - propB - propC) / dryBase);
            float dryC = Mathf.Clamp(dry, ModuleTankBalancer.MinDry, ModuleTankBalancer.MaxDry);
            bool dryClamped = !Mathf.Approximately(dry, dryC);
            dryC = Mathf.Round(dryC * 100f) / 100f;
            B.dry = C.dry = dryC;

            r.massAfter = fixedMass + propB + propC + dryBase * dryC;

            // Ciąg na poziomie morza rośnie razem z Ciągiem × (ISP i przepływ zmieniają się wzajemnie znosząc).
            double thrust = 0;
            int launch = Math.Max(B.ignition, C.ignition);
            foreach (EngineGroup g in groups)
            {
                if (g.ignition != launch) continue;
                double k = g.appliedThrust > 0 ? g.thrust / g.appliedThrust : 1.0;
                thrust += g.thrustAsl * k;
            }
            r.twrAfter = r.massAfter > 0 && gSurf > 0 ? thrust / (r.massAfter * gSurf) : 0;

            r.message = "Bloki A: paliwo ×" + B.fuel.ToString("F2") + ", ISP ×" + B.isp.ToString("F2") + ", ciąg ×" + B.thrust.ToString("F2") +
                        "   Rdzeń: paliwo ×" + C.fuel.ToString("F2") + ", ISP ×" + C.isp.ToString("F2") + ", ciąg ×" + C.thrust.ToString("F2") +
                        "\nMasa pustych zbiorników ×" + dryC.ToString("F2") + " · masa startowa " + r.massAfter.ToString("F0") + " t (oryg. " +
                        LiftoffMass.ToString("F0") + " t) · TWR " + r.twrAfter.ToString("F2") + " (oryg. ≈" + LiftoffTwr.ToString("F2") + ")";
            if (clamped)
                r.message += "\nUWAGA: część mnożników doszła do limitu suwaków – nie wszystkie cele będą osiągnięte.";
            if (dryClamped)
                r.message += "\nUWAGA: masy startowej nie da się osiągnąć samą masą pustych zbiorników (limit " +
                             ModuleTankBalancer.MinDry.ToString("F1") + "–" + ModuleTankBalancer.MaxDry.ToString("F1") + "×) – sprawdź masę orbitera.";
            r.ok = true;
            return r;
        }

        /// <summary>Ustawia paliwo, ISP i ciąg jednej grupy. Zwraca masę paliwa po zatankowaniu do pełna [t].</summary>
        private static double Fit(EngineGroup g, double propTarget, double ispTarget, double time, ref bool clamped)
        {
            g.fuel = Clamp((float)(g.appliedFuel * propTarget / g.propCapacity), ModuleTankBalancer.MinFuel, ModuleTankBalancer.MaxFuel, ref clamped);
            double prop = g.propCapacity * g.fuel / g.appliedFuel;

            g.isp = Clamp((float)(g.appliedIsp * ispTarget / g.ispVac), ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult, ref clamped);

            // przepływ ∝ ciąg / ISP;  czas = paliwo / przepływ
            double thr = g.appliedThrust * (prop / (g.massFlow * time)) * (g.isp / g.appliedIsp);
            g.thrust = Clamp((float)thr, ModuleEngineBalancer.MinThrustMult, ModuleEngineBalancer.MaxThrustMult, ref clamped);
            return prop;
        }

        private static float Clamp(float v, float min, float max, ref bool clamped)
        {
            float c = Mathf.Clamp(v, min, max);
            if (!Mathf.Approximately(v, c)) clamped = true;
            return Mathf.Round(c * 1000f) / 1000f;
        }
    }
}
