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

        // Ostatnio zastosowany mnożnik paliwa i pojemność (informacyjnie; pojemność zawsze liczona od oryginału części).
        [KSPField(isPersistant = true)] public float appliedFuelMultiplier = 1f;
        [KSPField(isPersistant = true)] public double scaledCapacity = -1.0;

        // Oryginalna pojemność każdego zasobu (taka, jaką ustawiła gra i inne mody, np. B9PartSwitch),
        // zapisywana w pliku statku: "LiquidFuel=882;Oxidizer=1078".
        [KSPField(isPersistant = true)] public string baseCapacities = "";

        private Dictionary<string, double> bases;

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

            // Co sekundę: czy pojemność zgadza się z „oryginał × Paliwo ×”. Jeśli nie (inny mod ją przestawił
            // przy wczytaniu), ustawiamy ją jeszcze raz – zawsze od oryginału, więc nic nie mnoży się podwójnie.
            if (Time.realtimeSinceStartup > nextResync)
            {
                nextResync = Time.realtimeSinceStartup + 1f;
                if (!FuelMatches()) ApplyFuel();
            }
        }

        private double Capacity()
        {
            double c = 0.0;
            foreach (PartResource r in part.Resources)
                if (Scaled(r)) c += r.maxAmount;
            return c;
        }

        private void LoadBases()
        {
            if (bases != null) return;
            bases = new Dictionary<string, double>();
            if (string.IsNullOrEmpty(baseCapacities)) return;
            foreach (string item in baseCapacities.Split(';'))
            {
                string[] kv = item.Split('=');
                double v;
                if (kv.Length == 2 && double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0)
                    bases[kv[0]] = v;
            }
        }

        private void SaveBases()
        {
            List<string> items = new List<string>();
            foreach (KeyValuePair<string, double> kv in bases)
                items.Add(kv.Key + "=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            baseCapacities = string.Join(";", items.ToArray());
        }

        private static bool Same(double a, double b)
        {
            return System.Math.Abs(a - b) <= 0.0001 * System.Math.Max(1.0, System.Math.Abs(b));
        }

        /// <summary>
        /// Ustala oryginalną pojemność zasobu i zwraca, czy obecna pojemność się zgadza z „oryginał × Paliwo ×”.
        ///   • brak zapisanego oryginału → oryginał = obecna ÷ zastosowany mnożnik (pierwsze użycie),
        ///   • obecna = oryginał × mnożnik → wszystko w porządku,
        ///   • obecna = oryginał → inny mod przywrócił oryginał (np. przy wczytaniu) → trzeba przeskalować,
        ///   • obecna jest jeszcze inna → zmieniono typ zbiornika → to nowy oryginał.
        /// </summary>
        private bool CheckBase(PartResource r)
        {
            double cur = r.maxAmount;
            double b;
            if (!bases.TryGetValue(r.resourceName, out b))
            {
                b = cur / Mathf.Max(0.0001f, appliedFuelMultiplier);
                bases[r.resourceName] = b;
                SaveBases();
            }

            if (Same(cur, b * fuelMultiplier)) return true;
            if (Same(cur, b)) return false;

            if (!Same(cur, b * appliedFuelMultiplier))
            {
                bases[r.resourceName] = cur;   // nowy typ zbiornika – jego pojemność jest nowym oryginałem
                SaveBases();
            }
            return false;
        }

        private bool FuelMatches()
        {
            LoadBases();
            bool ok = true;
            foreach (PartResource r in part.Resources)
                if (Scaled(r) && !CheckBase(r)) ok = false;
            return ok;
        }

        /// <summary>Pojemność = oryginał × Paliwo × (zachowuje stopień napełnienia). Nigdy nie mnoży podwójnie.</summary>
        public void ApplyFuel()
        {
            LoadBases();
            foreach (PartResource r in part.Resources)
            {
                if (!Scaled(r)) continue;
                CheckBase(r);
                double baseMax = bases[r.resourceName];
                double fill = r.maxAmount > 0 ? System.Math.Min(1.0, r.amount / r.maxAmount) : 1.0;
                r.maxAmount = baseMax * fuelMultiplier;
                r.amount = r.maxAmount * fill;
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
        /// <summary>
        /// Gdy true (ustawia wzorzec Energii), mnożnik działa na całą masę części – razem z tym, co dodają inne mody
        /// (np. TweakScale przy przeskalowanej części). Gdy false – tylko na masę bazową części, jak dawniej.
        /// </summary>
        [KSPField(isPersistant = true)] public bool scaleWholePart = false;

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit)
        {
            float b = defaultMass + (scaleWholePart ? OtherMass(part, defaultMass, sit) : 0f);
            return b * (dryMassMultiplier - 1f);
        }

        /// <summary>Masa dodawana do części przez moduły innych modów (TweakScale, B9PartSwitch…), bez modułów RSSBalancera.</summary>
        public static float OtherMass(Part p, float defaultMass, ModifierStagingSituation sit)
        {
            if (p == null || p.Modules == null) return 0f;
            float m = 0f;
            for (int i = 0; i < p.Modules.Count; i++)
            {
                PartModule pm = p.Modules[i];
                if (pm is ModuleTankBalancer || pm is ModuleEngineBalancer) continue;
                IPartMassModifier mm = pm as IPartMassModifier;
                if (mm == null) continue;
                try { m += mm.GetModuleMass(defaultMass, sit); } catch { }
            }
            return m;
        }

        /// <summary>Masa części przy wszystkich mnożnikach RSSBalancera = 1 (masa bazowa + dodatki innych modów).</summary>
        public static double WholeBase(Part p)
        {
            return p.prefabMass + OtherMass(p, p.prefabMass, ModifierStagingSituation.CURRENT);
        }

        public ModifierChangeWhen GetModuleMassChangeWhen()
        {
            return ModifierChangeWhen.FIXED;
        }
    }
}
