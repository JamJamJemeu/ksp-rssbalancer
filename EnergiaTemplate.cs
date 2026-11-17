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
        public const double BoosterDry = 262.0;     // I stopień: 1490,4 t − 1228 t paliwa (4 bloki razem)
        public const double CoreDry = 72.6;         // II stopień: 776,2 t − 703,6 t paliwa
        public const double OrbiterMass = 105.0;    // Buran z paliwem ОДУ (2375 − 2270 t)

        public enum DryMode { Original, LiftoffMass, Slider }

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
            public DryMode mode = DryMode.Original;
            public double dryB = BoosterDry, dryC = CoreDry;   // DryMode.Original
            public double mass = LiftoffMass;                  // DryMode.LiftoffMass
            public float dry = 1f;                             // DryMode.Slider
        }

        public class Result
        {
            public bool ok;
            public string message = "";
            public SoyuzTemplate.Result id;
            public double massAfter, twrAfter, restMass, dryBAfter, dryCAfter;
            public double dv1, dv2;          // Δv w próżni: bloki + rdzeń, potem sam rdzeń [m/s]
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

            // Masa: wszystko poza paliwem i konstrukcją bloków i rdzenia (orbiter, adaptery…) zostaje bez zmian.
            double fixedMass = shipMass - B.propMass - C.propMass - B.dryMass - C.dryMass;
            r.restMass = fixedMass;
            bool dryClamped = false;

            if (t.mode == DryMode.Original)
            {
                B.dry = DryFor(B, t.dryB, ref dryClamped);
                C.dry = DryFor(C, t.dryC, ref dryClamped);
            }
            else
            {
                float dry = t.dry;
                double dryBase = B.tankDryBase + C.tankDryBase;
                if (t.mode == DryMode.LiftoffMass && dryBase > 0)
                {
                    // masa konstrukcji bez zbiorników zostaje; zbiorniki przy wspólnym mnożniku m
                    double baseNow = B.tankDryBase * B.appliedDry + C.tankDryBase * C.appliedDry;
                    double nonTank = B.dryMass + C.dryMass - baseNow;
                    dry = (float)((t.mass - fixedMass - propB - propC - nonTank) / dryBase);
                }
                float d = Mathf.Clamp(dry, ModuleTankBalancer.MinDry, ModuleTankBalancer.MaxDry);
                if (!Mathf.Approximately(d, dry)) dryClamped = true;
                B.dry = C.dry = Mathf.Round(d * 100f) / 100f;
            }

            r.dryBAfter = B.dryMass + B.tankDryBase * (B.dry - B.appliedDry);
            r.dryCAfter = C.dryMass + C.tankDryBase * (C.dry - C.appliedDry);
            r.massAfter = fixedMass + propB + propC + r.dryBAfter + r.dryCAfter;

            // Δv w próżni (własny model, niezależny od stockowego kalkulatora):
            //   etap 1: bloki i rdzeń razem do wypalenia bloków; etap 2: sam rdzeń.
            double flowB = propB / t.tB, flowC = propC / t.tC;          // t/s
            double ispEff = (flowB * t.ispB + flowC * t.ispC) / (flowB + flowC);
            double m0 = r.massAfter;
            double m1 = m0 - propB - flowC * t.tB;
            double m2 = m1 - r.dryBAfter;
            double m3 = m2 - flowC * (t.tC - t.tB);
            if (m1 > 0 && m2 > 0 && m3 > 0)
            {
                r.dv1 = ispEff * G0 * Math.Log(m0 / m1);
                r.dv2 = t.ispC * G0 * Math.Log(m2 / m3);
            }

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
                        "\nMasa pustych ×: bloki " + B.dry.ToString("F2") + ", rdzeń " + C.dry.ToString("F2") +
                        " · konstrukcja: bloki " + r.dryBAfter.ToString("F0") + " t (oryg. " + BoosterDry.ToString("F0") + "), rdzeń " +
                        r.dryCAfter.ToString("F0") + " t (oryg. " + CoreDry.ToString("F0") + "), orbiter i reszta " + r.restMass.ToString("F0") +
                        " t (oryg. ≈" + OrbiterMass.ToString("F0") + ")" +
                        "\nMasa startowa " + r.massAfter.ToString("F0") + " t (oryg. " + LiftoffMass.ToString("F0") + ") · TWR " + r.twrAfter.ToString("F2") +
                        " (oryg. ≈" + LiftoffTwr.ToString("F2") + ") · Δv w próżni: " + r.dv1.ToString("F0") + " + " + r.dv2.ToString("F0") + " = " +
                        (r.dv1 + r.dv2).ToString("F0") + " m/s (oryginał ≈ 8 800 m/s)";
            if (clamped)
                r.message += "\nUWAGA: część mnożników doszła do limitu suwaków – nie wszystkie cele będą osiągnięte.";
            if (dryClamped)
                r.message += "\nUWAGA: masy konstrukcji nie da się osiągnąć samymi zbiornikami (limit " +
                             ModuleTankBalancer.MinDry.ToString("F1") + "–" + ModuleTankBalancer.MaxDry.ToString("F1") + "×) – silniki i odłączacze ważą swoje.";
            if (r.restMass > OrbiterMass * 1.4)
                r.message += "\nUWAGA: orbiter i reszta ważą " + r.restMass.ToString("F0") + " t – dużo więcej niż Buran (≈105 t); to zabiera Δv rdzeniowi.";
            r.ok = true;
            return r;
        }

        private const double G0 = 9.80665;

        /// <summary>Mnożnik masy pustych zbiorników, przy którym konstrukcja grupy waży <paramref name="target"/> t.</summary>
        private static float DryFor(EngineGroup g, double target, ref bool clamped)
        {
            if (g.tankDryBase <= 0) return g.appliedDry;
            float m = (float)(g.appliedDry + (target - g.dryMass) / g.tankDryBase);
            return Clamp(m, ModuleTankBalancer.MinDry, ModuleTankBalancer.MaxDry, ref clamped);
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
