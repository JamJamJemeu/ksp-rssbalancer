using System;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Smuga za silnikiem – tylko tam, gdzie w prawdziwym locie ją widać. Rakiety na paliwo ciekłe nie dymią jak
    /// rakiety na paliwo stałe: biała „chmura” to skroplona para wodna ze spalin. Przy starcie robi ją woda z systemu
    /// tłumienia dźwięku i spaliny przy wyrzutni (chmura przy ziemi); w ciepłym powietrzu nisko ślad jest słaby;
    /// prawdziwa smuga kondensacyjna powstaje w zimnym powietrzu górnej troposfery (jak u samolotów: ~7,5–12 km)
    /// i ciągnie się wyżej, rzednąc w suchej stratosferze; wyżej spaliny rozpraszają się w próżni (to rysuje
    /// Waterfall – rozszerzający się płomień). Profil (współczynnik × ciąg):
    ///   przy ziemi (≤ 300 m nad terenem) 1,0 – chmura startowa; 1–6 km 0,2; 6→9 km narasta do 1,0;
    ///   9–18 km 1,0; 18→35 km spada do 0,3; 35→50 km do 0; wyżej 0.
    /// Dodawany łatką (Patches/Energia_Waterfall.cfg) do RD-170 i RD-0120; efekt „rssbSmoke” w EFFECTS części.
    /// </summary>
    public class ModuleRssbSmoke : PartModule
    {
        [KSPField] public string effectName = "rssbSmoke";
        [KSPField] public string engineID = "";

        private ModuleEngines engine;
        private float last = -1f;

        private static readonly double[] AltKm = { 0, 1, 6, 9, 18, 35, 50 };
        private static readonly double[] AltF  = { 0.2, 0.2, 0.2, 1.0, 1.0, 0.3, 0.0 };

        /// <summary>Współczynnik widoczności smugi od wysokości (n.p.m. [m]) i wysokości nad terenem [m].</summary>
        public static float TrailFactor(double altAsl, double altAgl)
        {
            double km = altAsl / 1000.0, f;
            if (km <= AltKm[0]) f = AltF[0];
            else if (km >= AltKm[AltKm.Length - 1]) f = AltF[AltF.Length - 1];
            else
            {
                int i = 1; while (km > AltKm[i]) i++;
                double u = (km - AltKm[i - 1]) / (AltKm[i] - AltKm[i - 1]);
                f = AltF[i - 1] + u * (AltF[i] - AltF[i - 1]);
            }
            // chmura startowa przy wyrzutni: do 300 m nad terenem pełna, do 1000 m zanika do profilu
            if (altAgl < 1000) f = Math.Max(f, altAgl <= 300 ? 1.0 : 1.0 - (altAgl - 300) / 700.0 * (1.0 - f));
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
                p = vessel.mainBody.atmosphere ? Mathf.Clamp01(engine.currentThrottle) * TrailFactor(vessel.altitude, vessel.heightFromTerrain >= 0 ? vessel.heightFromTerrain : vessel.altitude) : 0f;
            if (Math.Abs(p - last) < 0.005f) return;
            last = p;
            part.Effect(effectName, p);
        }
    }
}
