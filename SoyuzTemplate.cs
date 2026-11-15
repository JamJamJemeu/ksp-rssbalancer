using System;
using System.Collections.Generic;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Wzorzec R-7 / Sojuz: boczne bloki i rdzeń odpalane razem na starcie,
    /// boczne bloki odrzucane wcześniej (krzyż Korolewa), rdzeń pracuje dalej.
    ///
    /// Cele: czas pracy bocznych bloków, czas pracy rdzenia, TWR na starcie.
    /// Suwaki: wspólne ISP × (bloki i rdzeń – jak w realnym Sojuzie, RD-107A i RD-108A mają ≈320 s w próżni),
    ///         osobny Ciąg × dla bloków i dla rdzenia.
    /// Trzy cele, trzy niewiadome – rozwiązanie dokładne:
    ///   czas  ∝ ISP / ciąg,  TWR ∝ suma ciągów.
    /// Opcjonalnie: czas pracy trzeciego stopnia (Blok I) – dopasowany przez ISP przy stałym ciągu.
    /// </summary>
    public static class SoyuzTemplate
    {
        // Soyuz-2.1b: boczne bloki 118 s, Blok A 286 s, Blok I (RD-0124) 270 s; 2.1a: Blok I (RD-0110) 250 s.
        public const double BoosterTime = 118.0;
        public const double CoreTime = 286.0;
        public const double UpperTime = 270.0;
        public const double LiftoffTwr = 1.35;

        public class Result
        {
            public bool ok;
            public string message;
            public EngineGroup boosters, core, upper;
        }

        /// <summary>Szuka bocznych bloków, rdzenia i trzeciego stopnia wśród grup.</summary>
        public static Result Identify(List<EngineGroup> groups)
        {
            Result r = new Result();
            if (groups == null || groups.Count == 0)
            {
                r.message = "Brak silników na statku.";
                return r;
            }

            int launch = int.MinValue;
            foreach (EngineGroup g in groups) launch = Math.Max(launch, g.ignition);

            List<EngineGroup> atLaunch = groups.FindAll(g => g.ignition == launch);
            if (atLaunch.Count < 2)
            {
                r.message = "Na starcie odpala się tylko jedna grupa silników – to nie wygląda na układ Sojuza (boczne bloki + rdzeń odpalane razem).";
                return r;
            }

            // Boczne bloki odpadają najwcześniej (najwyższy numer odrzutu), rdzeń później.
            atLaunch.Sort((a, b) => b.jettison.CompareTo(a.jettison));
            r.boosters = atLaunch[0];
            r.core = atLaunch[atLaunch.Count - 1];

            if (r.boosters.jettison == r.core.jettison)
            {
                r.message = "Boczne bloki i rdzeń są odrzucane w tym samym stage'u – sprawdź odłączacze bocznych bloków.";
                return r;
            }

            // Trzeci stopień: pierwsza grupa odpalana przy odrzucie rdzenia albo później.
            // Pomija silniczki odrzutu bocznych bloków (odpalane razem z odrzutem bloków, wcześniej niż odrzut rdzenia).
            EngineGroup upper = null;
            foreach (EngineGroup g in groups)
                if (g.ignition <= r.core.jettison && g.ignition < launch && (upper == null || g.ignition > upper.ignition))
                    upper = g;
            r.upper = upper;

            r.ok = true;
            return r;
        }

        /// <summary>Ustawia suwaki grup (bez stosowania). Zwraca opis wyniku.</summary>
        public static Result Solve(List<EngineGroup> groups, double twr0, double tBooster, double tCore, double twr, bool doUpper, double tUpper)
        {
            Result r = Identify(groups);
            if (!r.ok) return r;

            EngineGroup B = r.boosters, C = r.core;
            double tb0 = B.BurnTimeNow, tc0 = C.BurnTimeNow;

            if (tb0 <= 0 || tc0 <= 0 || twr0 <= 0 || B.thrustAsl <= 0 || C.thrustAsl <= 0)
            {
                r.ok = false;
                r.message = "Brak danych o czasie pracy / TWR – sprawdź, czy bloki i rdzeń mają paliwo.";
                return r;
            }

            // Względem tego, co jest teraz na silnikach:
            //   czas_B = tb0 · (k / ispB0) · (thrB0 / thrB)    → thrB = thrB0 · (k/ispB0) · tb0 / tBooster
            //   czas_C = tc0 · (k / ispC0) · (thrC0 / thrC)    → thrC = thrC0 · (k/ispC0) · tc0 / tCore
            //   TWR    = twr0 · (FB·thrB/thrB0 + FC·thrC/thrC0) / (FB + FC)
            // Podstawienie daje k wprost.
            double FB = B.thrustAsl, FC = C.thrustAsl;
            double k = (twr * (FB + FC) / twr0) /
                       (FB * tb0 / (B.appliedIsp * tBooster) + FC * tc0 / (C.appliedIsp * tCore));

            double thrB = B.appliedThrust * (k / B.appliedIsp) * tb0 / tBooster;
            double thrC = C.appliedThrust * (k / C.appliedIsp) * tc0 / tCore;

            bool clamped = false;
            B.isp = C.isp = Clamp((float)k, ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult, ref clamped);
            B.thrust = Clamp((float)thrB, ModuleEngineBalancer.MinThrustMult, ModuleEngineBalancer.MaxThrustMult, ref clamped);
            C.thrust = Clamp((float)thrC, ModuleEngineBalancer.MinThrustMult, ModuleEngineBalancer.MaxThrustMult, ref clamped);

            string msg = "Boczne bloki: ISP ×" + B.isp.ToString("F2") + ", Ciąg ×" + B.thrust.ToString("F2") +
                         "   Rdzeń: ISP ×" + C.isp.ToString("F2") + ", Ciąg ×" + C.thrust.ToString("F2");

            if (doUpper && r.upper != null)
            {
                double tu0 = r.upper.BurnTimeNow;
                if (tu0 > 0)
                {
                    r.upper.thrust = r.upper.appliedThrust;
                    r.upper.isp = Clamp((float)(r.upper.appliedIsp * tUpper / tu0), ModuleEngineBalancer.MinIspMult, ModuleEngineBalancer.MaxIspMult, ref clamped);
                    msg += "   III stopień: ISP ×" + r.upper.isp.ToString("F2");
                }
            }

            if (clamped)
                msg += "\nUWAGA: część wartości doszła do limitu suwaków – cele będą osiągnięte tylko częściowo (proporcje paliwa w tym modzie mocno różnią się od Sojuza).";

            r.message = msg;
            return r;
        }

        private static float Clamp(float v, float min, float max, ref bool clamped)
        {
            float c = Mathf.Clamp(v, min, max);
            if (!Mathf.Approximately(v, c)) clamped = true;
            return Mathf.Round(c * 100f) / 100f;
        }
    }
}
