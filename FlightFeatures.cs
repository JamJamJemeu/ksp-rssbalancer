using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Efekty w locie dla rakiet w układzie R-7 / Sojuz (rozpoznanym tak samo jak w zakładce „Sojuz”):
    ///   • Krzyż Korolewa – po odłączeniu każdego bocznego bloku najpierw jego ogon odchyla się na zewnątrz
    ///     (obrót na górnym zawiasie), potem czubek zostaje odepchnięty (zawór utleniacza), jak w prawdziwym Sojuzie.
    ///   • Gorące rozdzielenie – III stopień odpala się kilka chwil przed wypaleniem rdzenia
    ///     (opcjonalnie mod sam odpala następny stage, gdy rdzeń zgaśnie).
    ///   • Oś czasu lotu – czasy odrzutu bloków, rdzenia i końca pracy III stopnia w porównaniu z oryginałem.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightFeatures : MonoBehaviour
    {
        private const int TimelineWindowId = 0x52535343;
        private const float PivotPhase = 0.6f;       // s – ogon na zewnątrz
        private const float VentPhase = 0.6f;        // s – odepchnięcie czubka
        private const float KickAngularAccel = 0.6f; // rad/s² przy sile 1,0

        /// <summary>
        /// Dwie fazy jak w prawdziwym Sojuzie:
        ///   1) blok obraca się na górnym zawiasie – ogon odchyla się na zewnątrz (siła na dole bloku),
        ///   2) zawór na czubku zbiornika utleniacza wypuszcza gaz i odpycha blok od rakiety (słabsza siła na górze).
        /// </summary>
        private class Kick
        {
            public Part top, bottom;
            public Vector3 dir;
            public float force; // kN
            public float start;
        }

        private Vessel tracked;
        private bool identified;
        private readonly List<Part> boosterParts = new List<Part>();
        private readonly List<Part> coreParts = new List<Part>();
        private readonly List<Part> upperParts = new List<Part>();
        private Part coreDecoupler;
        private readonly HashSet<Vessel> kicked = new HashSet<Vessel>();
        private readonly List<Kick> kicks = new List<Kick>();

        private bool hotFired, autoSepDone, upperWasOn, coreWasOn, energia;
        private double tBoosterSep = -1, tCoreSep = -1, tUpperOff = -1, tHotStaging = -1, tCoreOff = -1, tUpperOn = -1;
        private float nextIdentify;

        private static Rect timelineWindow = new Rect(20f, 120f, 330f, 120f);

        private void Start()
        {
            Hud.Hook();
            BalancerSettings.EnsureLoaded();
        }

        // ================================================================== rozpoznanie rakiety

        private void Update()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            // Rozpoznajemy układ na starcie (albo po przełączeniu na inną rakietę stojącą na wyrzutni).
            // Na wyrzutni rozpoznajemy co sekundę – w pierwszych klatkach mnożniki ISP/ciągu mogą nie być jeszcze nałożone.
            bool needs = !identified || v.situation == Vessel.Situations.PRELAUNCH;
            if (needs && Time.realtimeSinceStartup > nextIdentify)
            {
                nextIdentify = Time.realtimeSinceStartup + 1f;
                Identify(v);
            }
        }

        private void Identify(Vessel v)
        {
            identified = false;
            boosterParts.Clear(); coreParts.Clear(); upperParts.Clear();
            coreDecoupler = null;
            kicked.Clear(); kicks.Clear();
            hotFired = autoSepDone = upperWasOn = coreWasOn = energia = false;
            tBoosterSep = tCoreSep = tUpperOff = tHotStaging = tCoreOff = tUpperOn = -1;
            TelemetryWindow.Kind = TelemetryWindow.RocketKind.Other;
            tracked = v;

            List<EngineGroup> groups = EngineGroups.Build(v.parts, null);
            SoyuzTemplate.Result id = SoyuzTemplate.Identify(groups);
            if (!id.ok) return;

            boosterParts.AddRange(id.boosters.parts);
            coreParts.AddRange(id.core.parts);
            if (id.core.parts.Count > 0) coreDecoupler = EngineGroups.JettisonDecoupler(id.core.parts[0]);
            if (id.upper != null) upperParts.AddRange(id.upper.parts);
            energia = EnergiaTemplate.Looks(id);
            TelemetryWindow.Kind = energia ? TelemetryWindow.RocketKind.Energia : TelemetryWindow.RocketKind.Soyuz;
            identified = true;
        }

        // ================================================================== fizyka

        private void FixedUpdate()
        {
            if (!identified || tracked == null) return;
            Vessel v = tracked;
            if (v.situation == Vessel.Situations.PRELAUNCH) return;

            // --- odrzut bocznych bloków + krzyż Korolewa ---
            for (int i = boosterParts.Count - 1; i >= 0; i--)
            {
                Part p = boosterParts[i];
                if (p == null) { boosterParts.RemoveAt(i); continue; }
                if (p.vessel == null || p.vessel == v) continue;

                if (tBoosterSep < 0)
                {
                    tBoosterSep = v.missionTime;
                    FlightLogger.Event(energia ? "Energia: odrzut bloków A (cel " + EnergiaTemplate.BoosterTime.ToString("F0") + " s)"
                                               : "Sojuz: odrzut bocznych bloków (cel " + BalancerSettings.BoosterTime.ToString("F0") + " s)");
                    TelemetryWindow.Lamp(TelemetryWindow.LampId.BoosterSep);
                }
                if (!energia && BalancerSettings.KorolevCross && !kicked.Contains(p.vessel))
                {
                    kicked.Add(p.vessel);
                    StartKick(p.vessel, v);
                }
                boosterParts.RemoveAt(i);
            }

            ApplyKicks();

            bool boostersGone = boosterParts.Count == 0 && tBoosterSep >= 0;

            // --- wyłączenie rdzenia (Energia: rdzeń gaśnie, orbiter odłącza się kilkanaście sekund później) ---
            if (tCoreOff < 0 && tCoreSep < 0 && coreParts.Count > 0)
            {
                bool on = Running(coreParts, v);
                if (on) coreWasOn = true;
                else if (coreWasOn && energia)
                {
                    tCoreOff = v.missionTime;
                    FlightLogger.Event("Energia: wyłączenie rdzenia (cel " + EnergiaTemplate.CoreTime.ToString("F0") + " s), Pe " +
                                       (v.orbit.PeA / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " km, Ap " +
                                       (v.orbit.ApA / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " km");
                    TelemetryWindow.Lamp(TelemetryWindow.LampId.CoreCutoff);
                }
            }

            // --- odrzut rdzenia ---
            if (tCoreSep < 0 && coreParts.Count > 0 && !AnyAttached(coreParts, v))
            {
                tCoreSep = v.missionTime;
                FlightLogger.Event(energia ? "Energia: odłączenie orbitera od rdzenia"
                                           : "Sojuz: odrzut rdzenia (cel " + BalancerSettings.CoreTime.ToString("F0") + " s)");
                TelemetryWindow.Lamp(energia ? TelemetryWindow.LampId.OrbiterSep : TelemetryWindow.LampId.CoreSep);
            }

            // --- gorące rozdzielenie: odpal III stopień tuż przed wypaleniem rdzenia ---
            if (!energia && BalancerSettings.HotStaging && !hotFired && boostersGone && tCoreSep < 0 &&
                upperParts.Count > 0 && Running(coreParts, v))
            {
                double rem = CoreRemaining(v);
                if (rem > 0 && rem <= BalancerSettings.HotStagingLead)
                {
                    foreach (Part p in upperParts)
                        if (p != null)
                            foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                                if (!e.EngineIgnited) e.Activate();
                    hotFired = true;
                    tHotStaging = v.missionTime;
                    FlightLogger.Event("gorące rozdzielenie: zapłon III stopnia, rdzeniowi zostało " + rem.ToString("F1") + " s");
                    TelemetryWindow.Lamp(TelemetryWindow.LampId.UpperIgnition);
                }
            }

            // --- opcjonalnie: odrzut rdzenia, gdy zgaśnie ---
            // Odpalamy bezpośrednio odłączacz rdzenia, a nie „następny stage” – między rdzeniem a III stopniem
            // często jest jeszcze stage z owiewką, który odpadłby zamiast rdzenia.
            if (!energia && BalancerSettings.AutoCoreSeparation && hotFired && !autoSepDone && tCoreSep < 0 && !Running(coreParts, v))
            {
                autoSepDone = true;
                FlightLogger.Event("mod: automatyczny odrzut rdzenia");
                if (coreDecoupler != null && coreDecoupler.vessel == v) EngineGroups.Decouple(coreDecoupler);
                else StageManager.ActivateNextStage();
            }

            // --- koniec pracy III stopnia ---
            if (upperParts.Count > 0 && tUpperOff < 0)
            {
                bool on = Running(upperParts, v);
                if (on)
                {
                    upperWasOn = true;
                    if (energia && tUpperOn < 0)
                    {
                        tUpperOn = v.missionTime;
                        FlightLogger.Event("Energia: zapłon silników orbitera (ОДУ)");
                        TelemetryWindow.Lamp(TelemetryWindow.LampId.OmsIgnition);
                    }
                }
                else if (upperWasOn)
                {
                    tUpperOff = v.missionTime;
                    FlightLogger.Event(energia ? "Energia: wyłączenie silników orbitera" : "Sojuz: koniec pracy III stopnia");
                    TelemetryWindow.Lamp(TelemetryWindow.LampId.UpperCutoff);
                }
            }
        }

        private void StartKick(Vessel booster, Vessel core)
        {
            if (booster.packed || booster.parts == null || booster.parts.Count == 0) return;

            Vector3 up = core.transform.up; // oś rakiety
            Vector3 rel = (Vector3)(booster.CoM - core.CoM);
            Vector3 radial = rel - Vector3.Dot(rel, up) * up;
            if (radial.sqrMagnitude < 1e-4f) return;

            // Czubek = część najdalej w kierunku lotu, ogon = najdalej w przeciwnym kierunku.
            Part top = null, bottom = null;
            float hi = float.MinValue, lo = float.MaxValue;
            foreach (Part p in booster.parts)
            {
                float d = Vector3.Dot(p.transform.position, up);
                if (d > hi) { hi = d; top = p; }
                if (d < lo) { lo = d; bottom = p; }
            }
            if (top == null || bottom == null) return;

            float mass = booster.GetTotalMass();                                   // t
            float length = Mathf.Max(1f, hi - lo);

            // Pręt obracany siłą na końcu: ε = F·(L/2) / (m·L²/12) = 6F / (m·L)  →  F = ε·m·L / 6
            float force = KickAngularAccel * Mathf.Clamp(BalancerSettings.KorolevStrength, 0f, 3f) * mass * length / 6f;
            if (force <= 0f) return;

            kicks.Add(new Kick { top = top, bottom = bottom, dir = radial.normalized, force = force, start = Time.time });
            TelemetryWindow.Lamp(TelemetryWindow.LampId.Korolev);
            FlightLogger.Event("krzyż Korolewa: " + booster.vesselName + ", " + mass.ToString("F1") + " t, siła " + force.ToString("F0") + " kN");
        }

        private void ApplyKicks()
        {
            for (int i = kicks.Count - 1; i >= 0; i--)
            {
                Kick k = kicks[i];
                float t = Time.time - k.start;
                Part target = t < PivotPhase ? k.bottom : k.top;
                float f = t < PivotPhase ? k.force : 0.5f * k.force;

                if (t > PivotPhase + VentPhase || target == null || target.Rigidbody == null)
                {
                    if (t > PivotPhase + VentPhase) kicks.RemoveAt(i);
                    continue;
                }
                target.Rigidbody.AddForceAtPosition(k.dir * f, target.transform.position, ForceMode.Force);
            }
        }

        private static bool AnyAttached(List<Part> parts, Vessel v)
        {
            foreach (Part p in parts)
                if (p != null && p.vessel == v) return true;
            return false;
        }

        private static bool Running(List<Part> parts, Vessel v)
        {
            foreach (Part p in parts)
            {
                if (p == null || p.vessel != v) continue;
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited && !e.flameout) return true;
            }
            return false;
        }

        /// <summary>Pozostały czas pracy rdzenia: paliwo w jego zbiornikach ÷ obecny przepływ (z przepustnicą).</summary>
        private double CoreRemaining(Vessel v)
        {
            if (coreDecoupler == null || coreDecoupler.vessel != v) return -1.0;
            float throttle = v.ctrlState != null ? v.ctrlState.mainThrottle : 1f;
            if (throttle <= 0.01f) return -1.0;

            double flow = 0.0;
            foreach (Part p in coreParts)
                if (p != null && p.vessel == v) flow += EngineGroups.MassFlow(p);
            flow *= throttle;
            if (flow <= 0.0) return -1.0;

            return EngineGroups.FuelMass(v.parts, coreDecoupler, EngineGroups.PropellantNames(coreParts)) / flow;
        }

        // ================================================================== oś czasu

        private void OnGUI()
        {
            if (!BalancerSettings.Timeline || Hud.Hidden || !identified || tracked == null) return;
            timelineWindow = GUILayout.Window(TimelineWindowId, timelineWindow, DrawTimeline, energia ? "Oś czasu lotu – Energia" : "Oś czasu lotu – Sojuz");
        }

        private void DrawTimeline(int id)
        {
            Vessel v = tracked;
            double now = v.situation == Vessel.Situations.PRELAUNCH ? 0 : v.missionTime;
            GUILayout.Label("T+ " + now.ToString("F0", CultureInfo.InvariantCulture) + " s");

            if (energia)
            {
                EventLine("Odrzut bloków A", tBoosterSep, EnergiaTemplate.BoosterTime);
                EventLine("Wyłączenie rdzenia", tCoreOff, EnergiaTemplate.CoreTime);
                EventLine("Odłączenie orbitera", tCoreSep, EnergiaTemplate.CoreTime + 15.0);
                if (tUpperOn >= 0)
                    GUILayout.Label("Zapłon silników orbitera:  T+" + tUpperOn.ToString("F1", CultureInfo.InvariantCulture) + " s");
                if (GUILayout.Button("Ukryj (włączysz w zakładce Sojuz)")) { BalancerSettings.Timeline = false; BalancerSettings.Save(); }
                GUI.DragWindow();
                return;
            }

            EventLine("Odrzut bocznych bloków", tBoosterSep, BalancerSettings.BoosterTime);
            if (tHotStaging >= 0)
                GUILayout.Label("Zapłon III stopnia (gorące rozdzielenie):  T+" + tHotStaging.ToString("F1", CultureInfo.InvariantCulture) + " s");
            EventLine("Odrzut rdzenia", tCoreSep, BalancerSettings.CoreTime);
            if (upperParts.Count > 0 || tUpperOff >= 0)
                EventLine("Koniec pracy III stopnia", tUpperOff, BalancerSettings.CoreTime + BalancerSettings.UpperTime);

            if (GUILayout.Button("Ukryj (włączysz w zakładce Sojuz)"))
            {
                BalancerSettings.Timeline = false;
                BalancerSettings.Save();
            }
            GUI.DragWindow();
        }

        private static void EventLine(string name, double t, double original)
        {
            string orig = "oryginał " + original.ToString("F0", CultureInfo.InvariantCulture) + " s";
            if (t < 0)
            {
                GUILayout.Label(name + ":  —   (" + orig + ")");
                return;
            }
            double diff = t - original;
            GUILayout.Label(name + ":  T+" + t.ToString("F1", CultureInfo.InvariantCulture) + " s   (" + orig + ", " +
                            (diff >= 0 ? "+" : "−") + Math.Abs(diff).ToString("F1", CultureInfo.InvariantCulture) + " s)");
        }
    }
}
