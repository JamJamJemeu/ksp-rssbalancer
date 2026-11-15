using System.Collections.Generic;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Dodawany do każdego silnika przez patch RSSBalancer.cfg.
    /// Trzyma mnożnik ISP i mnożnik ciągu danej części (zapisywane w save i w pliku statku)
    /// i nakłada je na wszystkie moduły silnika tej części (też silniki wielotrybowe).
    ///   ISP ×   – zmienia wydajność, ciąg zostaje bez zmian (przepływ paliwa liczony odwrotnie do ISP).
    ///   Ciąg ×  – zmienia ciąg, ISP zostaje bez zmian (przepływ paliwa rośnie razem z ciągiem).
    /// </summary>
    public class ModuleEngineBalancer : PartModule
    {
        public const float MinIspMult = 0.1f;
        public const float MaxIspMult = 5f;
        public const float MinThrustMult = 0.1f;
        public const float MaxThrustMult = 3f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "ISP ×", guiFormat = "F2")]
        [UI_FloatRange(minValue = MinIspMult, maxValue = MaxIspMult, stepIncrement = 0.05f,
                       scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public float ispMultiplier = 1f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Ciąg ×", guiFormat = "F2")]
        [UI_FloatRange(minValue = MinThrustMult, maxValue = MaxThrustMult, stepIncrement = 0.05f,
                       scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public float thrustMultiplier = 1f;

        private class EngineBase
        {
            public ModuleEngines engine;
            public Keyframe[] keys;     // oryginalna krzywa ISP tego modułu (kopia)
            public float maxFuelFlow;   // oryginalny przepływ paliwa
            public float minFuelFlow;
            public float maxThrust;     // oryginalny ciąg
            public float minThrust;
        }

        private readonly List<EngineBase> engines = new List<EngineBase>();
        private float appliedIsp = float.NaN;
        private float appliedThrust = float.NaN;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            // Wartości bazowe bierzemy z własnych modułów silnika przed jakąkolwiek zmianą.
            // Prefab części nie jest nigdy modyfikowany, więc inne statki nic nie odczuwają.
            engines.Clear();
            foreach (ModuleEngines e in part.FindModulesImplementing<ModuleEngines>())
            {
                if (e.atmosphereCurve == null) continue;
                engines.Add(new EngineBase
                {
                    engine = e,
                    keys = e.atmosphereCurve.Curve.keys,
                    maxFuelFlow = e.maxFuelFlow,
                    minFuelFlow = e.minFuelFlow,
                    maxThrust = e.maxThrust,
                    minThrust = e.minThrust
                });
            }

            ApplyMultipliers();
        }

        private void Update()
        {
            if (engines.Count == 0) return;

            // Wyłapuje zmianę suwaków w menu części (także na częściach w symetrii).
            if (!Mathf.Approximately(ispMultiplier, appliedIsp) || !Mathf.Approximately(thrustMultiplier, appliedThrust))
            {
                ApplyMultipliers();
                NotifyEditor();
            }
        }

        public void ApplyMultipliers()
        {
            if (engines.Count == 0) return;

            float isp = Mathf.Clamp(ispMultiplier, MinIspMult, MaxIspMult);
            float thr = Mathf.Clamp(thrustMultiplier, MinThrustMult, MaxThrustMult);

            foreach (EngineBase b in engines)
            {
                FloatCurve fc = new FloatCurve();
                foreach (Keyframe k in b.keys)
                    fc.Add(k.time, k.value * isp, k.inTangent * isp, k.outTangent * isp);

                b.engine.atmosphereCurve = fc;

                // Ciąg = przepływ × ISP × g.
                // Przepływ × (thr / isp) → ciąg rośnie dokładnie o thr, a ISP o isp.
                b.engine.maxFuelFlow = b.maxFuelFlow * thr / isp;
                b.engine.minFuelFlow = b.minFuelFlow * thr / isp;
                b.engine.maxThrust = b.maxThrust * thr;
                b.engine.minThrust = b.minThrust * thr;
            }

            appliedIsp = ispMultiplier;
            appliedThrust = thrustMultiplier;
        }

        public static void NotifyEditor()
        {
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        public override string GetInfo()
        {
            return "Mnożniki ISP i ciągu regulowane w grze (RSSBalancer).";
        }
    }
}
