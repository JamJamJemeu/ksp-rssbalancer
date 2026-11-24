using System;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Smuga dymu/pary za silnikiem – tylko w atmosferze. Stockowe efekty silnika znają jedno wejście (moc 0–1),
    /// nie znają gęstości powietrza, więc ten moduł sam podaje moc efektu: ciąg × współczynnik gęstości.
    /// Współczynnik: logarytm gęstości między ρ = 1,225 kg/m³ (1,0) a ρ = minDensity (0,0); w RSS: 10 km ≈ 0,88,
    /// 30 km ≈ 0,52, 50 km ≈ 0,2, 60 km ≈ 0,05 – smuga gęsta przy starcie, zanika w górnych warstwach.
    /// Dodawany łatką (Patches/Energia_Waterfall.cfg) do RD-170 i RD-0120; efekt „rssbSmoke” w EFFECTS części.
    /// </summary>
    public class ModuleRssbSmoke : PartModule
    {
        [KSPField] public string effectName = "rssbSmoke";
        [KSPField] public string engineID = "";
        [KSPField] public float minDensity = 0.0002f;
        [KSPField] public float maxDensity = 1.225f;

        private ModuleEngines engine;
        private float last = -1f;

        public static float DensityFactor(double rho, double minRho, double maxRho)
        {
            if (rho <= minRho || minRho <= 0) return 0f;
            double f = (Math.Log(rho) - Math.Log(minRho)) / (Math.Log(maxRho) - Math.Log(minRho));
            return (float)Math.Max(0, Math.Min(1, f));
        }

        public override void OnStart(StartState state)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            foreach (ModuleEngines e in part.FindModulesImplementing<ModuleEngines>())
                if (string.IsNullOrEmpty(engineID) || e.engineID == engineID) { engine = e; break; }
            part.Effect(effectName, 0f);
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || engine == null || vessel == null) return;
            float p = 0f;
            if (engine.EngineIgnited && !engine.flameout && engine.finalThrust > 0.01f)
                p = Mathf.Clamp01(engine.currentThrottle) * DensityFactor(vessel.atmDensity, minDensity, maxDensity);
            if (Math.Abs(p - last) < 0.005f) return;
            last = p;
            part.Effect(effectName, p);
        }
    }
}
