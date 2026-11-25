using System.Collections.Generic;

namespace RSSBalancer
{
    /// <summary>Odczyt stockowych statystyk Δv/TWR/czasu pracy (te same, co w oknie Delta-V Stats w grze).</summary>
    public static class StockDeltaV
    {
        public static VesselDeltaV Current()
        {
            if (HighLogic.LoadedSceneIsEditor)
            {
                if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return null;
                return EditorLogic.fetch.ship.vesselDeltaV;
            }

            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                return FlightGlobals.ActiveVessel.VesselDeltaV;

            return null;
        }

        public static bool TotalVac(out double dv)
        {
            dv = 0.0;
            VesselDeltaV v = Current();
            if (v == null) return false;
            dv = v.TotalDeltaVVac;
            return dv > 0.0;
        }

        /// <summary>Δv (próżnia) tylko stage'ów o numerze ≥ minStage – np. bez silników statku nad rakietą.</summary>
        public static bool TotalVacFrom(int minStage, out double dv)
        {
            dv = 0.0;
            if (minStage <= 0) return TotalVac(out dv);
            VesselDeltaV v = Current();
            if (v == null || v.OperatingStageInfo == null) return false;
            foreach (DeltaVStageInfo s in v.OperatingStageInfo)
                if (s != null && s.stage >= minStage) dv += s.deltaVinVac;
            return dv > 0.0;
        }

        public static bool TotalAsl(out double dv)
        {
            dv = 0.0;
            VesselDeltaV v = Current();
            if (v == null) return false;
            dv = v.TotalDeltaVASL;
            return dv > 0.0;
        }

        /// <summary>Statystyki stopnia o numerze z listy stage'ów (jak w VAB).</summary>
        public static DeltaVStageInfo Stage(int stage)
        {
            VesselDeltaV v = Current();
            if (v == null || v.OperatingStageInfo == null) return null;

            foreach (DeltaVStageInfo s in v.OperatingStageInfo)
                if (s != null && s.stage == stage)
                    return s;

            return null;
        }

        /// <summary>
        /// Czas pracy grupy silników odpalanych w stage'u <paramref name="ignition"/> i odrzucanych w <paramref name="jettison"/>:
        /// suma czasów wszystkich stage'ów, w których ta grupa pracuje (jettison &lt; stage ≤ ignition).
        /// Np. boczne bloki Sojuza 5→4 = czas stage'u 5; rdzeń 5→3 = czas stage'u 5 + 4.
        /// </summary>
        public static double GroupBurnTime(int ignition, int jettison)
        {
            VesselDeltaV v = Current();
            if (v == null || v.OperatingStageInfo == null) return 0.0;

            double t = 0.0;
            foreach (DeltaVStageInfo s in v.OperatingStageInfo)
                if (s != null && s.stage > jettison && s.stage <= ignition)
                    t += s.stageBurnTime;

            return t;
        }

        /// <summary>TWR na poziomie morza w stage'u startowym (najwyższy numer na liście).</summary>
        public static double LiftoffTwr()
        {
            VesselDeltaV v = Current();
            if (v == null || v.OperatingStageInfo == null) return 0.0;

            DeltaVStageInfo best = null;
            foreach (DeltaVStageInfo s in v.OperatingStageInfo)
                if (s != null && (best == null || s.stage > best.stage))
                    best = s;

            return best != null ? best.TWRASL : 0.0;
        }
    }
}
