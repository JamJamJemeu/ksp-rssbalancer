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
        public const float MaxThrustMult = 5f;

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

            // Co ostatnio ustawiliśmy – żeby wykryć, że inny mod (np. B9PartSwitch, TweakScale)
            // zmienił silnik po nas. Wtedy jego nowe wartości stają się bazą.
            public FloatCurve setCurve;
            public float setMaxFlow = float.NaN, setMinFlow = float.NaN, setMaxThrust = float.NaN, setMinThrust = float.NaN;
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

            // Wyłapuje zmianę suwaków w menu części (także na częściach w symetrii)
            // albo zmianę silnika przez inny mod po naszym ustawieniu.
            bool slidersMoved = !Mathf.Approximately(ispMultiplier, appliedIsp) || !Mathf.Approximately(thrustMultiplier, appliedThrust);
            if (slidersMoved)
            {
                ApplyMultipliers();
                NotifyEditor();
            }
            else if (ChangedByOthers())
            {
                // Bez powiadamiania edytora – żeby nie wywołać przepychanki z modem, który zmienia silnik przy każdej modyfikacji statku.
                ApplyMultipliers();
            }
        }

        public void ApplyMultipliers()
        {
            if (engines.Count == 0) return;

            float isp = Mathf.Clamp(ispMultiplier, MinIspMult, MaxIspMult);
            float thr = Mathf.Clamp(thrustMultiplier, MinThrustMult, MaxThrustMult);

            foreach (EngineBase b in engines)
            {
                Resync(b);

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

                b.setCurve = fc;
                b.setMaxFlow = b.engine.maxFuelFlow;
                b.setMinFlow = b.engine.minFuelFlow;
                b.setMaxThrust = b.engine.maxThrust;
                b.setMinThrust = b.engine.minThrust;
            }

            appliedIsp = ispMultiplier;
            appliedThrust = thrustMultiplier;
        }

        /// <summary>
        /// Jeśli wartości silnika różnią się od tych, które ostatnio ustawiliśmy, zmienił je inny mod
        /// (bez wiedzy o naszych mnożnikach) – bierzemy je jako nową bazę.
        /// </summary>
        private static void Resync(EngineBase b)
        {
            ModuleEngines e = b.engine;
            if (e.atmosphereCurve != null && !ReferenceEquals(e.atmosphereCurve, b.setCurve) && b.setCurve != null)
                b.keys = e.atmosphereCurve.Curve.keys;
            if (!float.IsNaN(b.setMaxFlow) && !Mathf.Approximately(e.maxFuelFlow, b.setMaxFlow)) b.maxFuelFlow = e.maxFuelFlow;
            if (!float.IsNaN(b.setMinFlow) && !Mathf.Approximately(e.minFuelFlow, b.setMinFlow)) b.minFuelFlow = e.minFuelFlow;
            if (!float.IsNaN(b.setMaxThrust) && !Mathf.Approximately(e.maxThrust, b.setMaxThrust)) b.maxThrust = e.maxThrust;
            if (!float.IsNaN(b.setMinThrust) && !Mathf.Approximately(e.minThrust, b.setMinThrust)) b.minThrust = e.minThrust;
        }

        private bool ChangedByOthers()
        {
            foreach (EngineBase b in engines)
            {
                if (b.setCurve == null) continue;
                ModuleEngines e = b.engine;
                if (!ReferenceEquals(e.atmosphereCurve, b.setCurve) ||
                    !Mathf.Approximately(e.maxFuelFlow, b.setMaxFlow) ||
                    !Mathf.Approximately(e.maxThrust, b.setMaxThrust))
                    return true;
            }
            return false;
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
