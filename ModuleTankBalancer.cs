using System.Collections.Generic;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Dodawany do części z paliwem przez patch RSSBalancer.cfg.
    ///   Masa pustego ×  – mnoży masę własną części (bez paliwa). Lżejsze zbiorniki = więcej Δv, czasy pracy bez zmian.
    ///   Paliwo ×        – mnoży pojemność zbiornika na paliwo ciekłe (bez prądu, paliwa stałego, monopropu).
    /// Oba zapisywane w pliku statku i w save. Zmienia się je tylko w VAB/SPH.
    /// </summary>
    public class ModuleTankBalancer : PartModule, IPartMassModifier
    {
        public const float MinDry = 0.1f, MaxDry = 2f;
        public const float MinFuel = 0.25f, MaxFuel = 5f;

        private static readonly HashSet<string> NotScaled = new HashSet<string>
        {
            "ElectricCharge", "SolidFuel", "MonoPropellant", "Ablator", "IntakeAir", "XenonGas", "Ore", "EVA Propellant"
        };

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Masa pustego ×", guiFormat = "F2")]
        [UI_FloatRange(minValue = MinDry, maxValue = MaxDry, stepIncrement = 0.05f,
                       scene = UI_Scene.Editor, affectSymCounterparts = UI_Scene.All)]
        public float dryMassMultiplier = 1f;

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Paliwo ×", guiFormat = "F2")]
        [UI_FloatRange(minValue = MinFuel, maxValue = MaxFuel, stepIncrement = 0.05f,
                       scene = UI_Scene.Editor, affectSymCounterparts = UI_Scene.All)]
        public float fuelMultiplier = 1f;

        // Mnożnik paliwa, który jest już „wpisany” w pojemność zbiorników, i pojemność po naszym przeskalowaniu –
        // żeby wykryć, że inny mod (np. B9PartSwitch) przywrócił oryginalną pojemność.
        [KSPField(isPersistant = true)] public float appliedFuelMultiplier = 1f;
        [KSPField(isPersistant = true)] public double scaledCapacity = -1.0;

        private float appliedDry = float.NaN;
        private float nextResync;

        public static bool Scaled(PartResource r)
        {
            return r != null && r.info != null && !NotScaled.Contains(r.resourceName);
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            appliedDry = dryMassMultiplier;
        }

        private void Update()
        {
            if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;

            if (HighLogic.LoadedSceneIsEditor)
            {
                bool changed = false;
                if (!Mathf.Approximately(dryMassMultiplier, appliedDry))
                {
                    appliedDry = dryMassMultiplier;
                    part.UpdateMass();
                    changed = true;
                }
                if (!Mathf.Approximately(fuelMultiplier, appliedFuelMultiplier))
                {
                    ApplyFuel();
                    changed = true;
                }
                if (changed) ModuleEngineBalancer.NotifyEditor();
            }

            // Co sekundę: czy ktoś nie przywrócił oryginalnej pojemności zbiornika (np. B9PartSwitch przy wczytaniu).
            if (Time.realtimeSinceStartup > nextResync)
            {
                nextResync = Time.realtimeSinceStartup + 1f;
                if (scaledCapacity > 0 && Mathf.Abs((float)(Capacity() - scaledCapacity)) > 0.01f)
                {
                    appliedFuelMultiplier = 1f; // obecna pojemność jest oryginalna
                    if (!Mathf.Approximately(fuelMultiplier, 1f)) ApplyFuel();
                    else scaledCapacity = Capacity();
                }
            }
        }

        private double Capacity()
        {
            double c = 0.0;
            foreach (PartResource r in part.Resources)
                if (Scaled(r)) c += r.maxAmount;
            return c;
        }

        /// <summary>Przeskalowuje pojemność względem tego, co jest teraz wpisane (zachowuje stopień napełnienia).</summary>
        public void ApplyFuel()
        {
            float ratio = fuelMultiplier / Mathf.Max(0.0001f, appliedFuelMultiplier);
            foreach (PartResource r in part.Resources)
            {
                if (!Scaled(r)) continue;
                r.maxAmount *= ratio;
                r.amount *= ratio;
            }
            appliedFuelMultiplier = fuelMultiplier;
            scaledCapacity = Capacity();
        }

        public void ApplyDry()
        {
            appliedDry = dryMassMultiplier;
            part.UpdateMass();
        }

        // --- IPartMassModifier: dodatkowa masa = masa własna × (mnożnik − 1)
        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit)
        {
            return defaultMass * (dryMassMultiplier - 1f);
        }

        public ModifierChangeWhen GetModuleMassChangeWhen()
        {
            return ModifierChangeWhen.FIXED;
        }
    }
}
