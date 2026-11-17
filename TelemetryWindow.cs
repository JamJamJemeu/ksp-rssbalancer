using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Ekran telemetrii w stylu radzieckiego ЦУП (wygląd jak AP-28): turkusowa blacha, bębenkowe liczniki,
    /// zielony kineskop z trajektorią, lampki zdarzeń i bursztynowa taśma zdarzeń. Opisy po rosyjsku albo po polsku.
    /// Wykres: wysokość / zasięg, wysokość / czas, prędkość / czas, ciśnienie dynamiczne, przeciążenie,
    /// ze znacznikami stage'ów i z porównaniem z poprzednim lotem z dziennika (CSV).
    /// Włączanie: F8 → zakładka Statek → „Ekran telemetrii”. Po F2 zostaje na ekranie (opcja).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TelemetryWindow : MonoBehaviour
    {
        public enum LampId { Liftoff, BoosterSep, Korolev, Fairing, UpperIgnition, CoreSep, UpperCutoff, Orbit, MaxQ, CoreCutoff, OrbiterSep, OmsIgnition }
        public enum RocketKind { Other, Soyuz, Energia }

        /// <summary>Rodzaj rakiety rozpoznany przez FlightFeatures – decyduje o zestawie lampek.</summary>
        public static RocketKind Kind = RocketKind.Other;

        private static readonly LampId[] LampsSoyuz =
            { LampId.Liftoff, LampId.BoosterSep, LampId.Korolev, LampId.Fairing, LampId.UpperIgnition, LampId.CoreSep, LampId.UpperCutoff, LampId.Orbit };
        private static readonly LampId[] LampsEnergia =
            { LampId.Liftoff, LampId.MaxQ, LampId.BoosterSep, LampId.CoreCutoff, LampId.OrbiterSep, LampId.OmsIgnition, LampId.UpperCutoff, LampId.Orbit };

        private const int WindowId = 0x52535354;
        private const float W = 520f, H = 892f;
        private const double SampleInterval = 0.5;
        private const double G0 = 9.80665;
        private const double FreshLamp = 10.0;   // s – tyle lampka świeci na bursztynowo, potem na zielono
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // geometria (względem okna)
        private static readonly Rect PlateRect = new Rect(16, 12, 384, 26);
        private static readonly Rect CrtRect = new Rect(16, 266, 488, 384);
        private static readonly Rect AmberRect = new Rect(16, 702, 488, 112);
        private const float MinScale = 0.5f, MaxScale = 1.6f;
        private const int GW = 456, GH = 214;            // obszar wykresu w kineskopie
        private const float GX = 15f, GY = 32f;          // położenie wykresu wewnątrz kineskopu

        private struct Sample { public float t, dr, alt, vel, q, g; }
        private struct Ev { public double t; public string ru, pl; }

        private static readonly string[] ModeRu = { "ВЫС/ДАЛЬН", "ВЫС/ВРЕМЯ", "СКОР/ВРЕМЯ", "НАПОР", "ПЕРЕГРУЗКА" };
        private static readonly string[] ModePl = { "WYS/ZASIĘG", "WYS/CZAS", "PRĘDK/CZAS", "CIŚN. DYN.", "PRZECIĄŻ." };
        private static readonly string[] TitleRu = { "ВЫСОТА / ДАЛЬНОСТЬ", "ВЫСОТА / ВРЕМЯ", "СКОРОСТЬ / ВРЕМЯ", "СКОРОСТНОЙ НАПОР / ВРЕМЯ", "ПЕРЕГРУЗКА / ВРЕМЯ" };
        private static readonly string[] TitlePl = { "WYSOKOŚĆ / ZASIĘG", "WYSOKOŚĆ / CZAS", "PRĘDKOŚĆ / CZAS", "CIŚNIENIE DYNAMICZNE / CZAS", "PRZECIĄŻENIE / CZAS" };
        // indeksowane wartością LampId
        private static readonly string[] LampRu = { "КОНТАКТ ПОДЪЁМА", "ОТДЕЛ. БОКОВЫХ", "КРЕСТ КОРОЛЁВА", "СБРОС ОБТЕКАТЕЛЯ", "ЗАЖИГАНИЕ III СТ.", "ОТДЕЛ. БЛОКА А", "ВЫКЛ. III СТ.", "ОРБИТА",
                                                    "МАКС. НАПОР", "ВЫКЛ. БЛОКА Ц", "ОТДЕЛЕНИЕ ОК", "ВКЛЮЧ. ОДУ" };
        private static readonly string[] LampPl = { "ODERWANIE", "ODRZUT BOCZNYCH", "KRZYŻ KOROLEWA", "ZRZUT OWIEWKI", "ZAPŁON III ST.", "ODRZUT RDZENIA", "WYŁ. III ST.", "ORBITA",
                                                    "MAKS. CIŚN. DYN.", "WYŁ. RDZENIA", "ODŁĄCZ. ORBITERA", "ZAPŁON OMS" };
        private static readonly string[] EventRu = { "КОНТАКТ ПОДЪЁМА", "ОТДЕЛЕНИЕ БОКОВЫХ БЛОКОВ", "КРЕСТ КОРОЛЁВА", "СБРОС ГОЛОВНОГО ОБТЕКАТЕЛЯ", "ЗАЖИГАНИЕ III СТУПЕНИ", "ОТДЕЛЕНИЕ ЦЕНТРАЛЬНОГО БЛОКА", "ВЫКЛЮЧЕНИЕ III СТУПЕНИ", "ВЫХОД НА ОРБИТУ",
                                                     "МАКСИМАЛЬНЫЙ СКОРОСТНОЙ НАПОР", "ВЫКЛЮЧЕНИЕ БЛОКА Ц", "ОТДЕЛЕНИЕ ОРБИТАЛЬНОГО КОРАБЛЯ", "ВКЛЮЧЕНИЕ ОДУ" };
        private static readonly string[] EventPl = { "ODERWANIE OD WYRZUTNI", "ODRZUT BOCZNYCH BLOKÓW", "KRZYŻ KOROLEWA", "ZRZUT OWIEWKI", "ZAPŁON III STOPNIA", "ODRZUT RDZENIA", "WYŁĄCZENIE III STOPNIA", "WEJŚCIE NA ORBITĘ",
                                                     "MAKS. CIŚNIENIE DYNAMICZNE", "WYŁĄCZENIE RDZENIA", "ODŁĄCZENIE ORBITERA", "ZAPŁON SILNIKÓW ORBITERA" };

        private static TelemetryWindow instance;

        private Rect window = new Rect(60, 60, W, H);   // współrzędne przed skalowaniem
        private bool resizing;
        private int mode;
        private bool styled;
        private GUIStyle sWin, sPlate, sPlateR, sCap, sDrum, sUnit, sCrt, sCrtR, sCrtSmall, sAmber, sLamp, sBtn, sBtnOn, sNote;

        private readonly List<Sample> samples = new List<Sample>();
        private readonly List<Sample> refSamples = new List<Sample>();
        private readonly List<Sample> marks = new List<Sample>();
        private readonly List<Ev> events = new List<Ev>();
        private readonly double[] lampTime = new double[12];

        private Texture2D graph;
        private Color32[] buf;
        private bool dirty = true;

        private Vessel vessel;
        private double lat0, lon0;
        private double nextSample;
        private double maxQ, maxG;
        private List<EngineGroup> groups = new List<EngineGroup>();

        private string[] refFiles = new string[0];
        private int refIndex = -1;
        private string refName = "";

        private static bool Ru { get { return BalancerSettings.TelemetryRussian; } }
        private static string L(string ru, string pl) { return Ru ? ru : pl; }

        // ================================================================== API dla reszty moda

        /// <summary>Zapala lampkę i dopisuje zdarzenie na taśmę (tylko za pierwszym razem).</summary>
        public static void Lamp(LampId id)
        {
            if (instance != null) instance.SetLamp(id);
        }

        private void SetLamp(LampId id)
        {
            if (vessel == null) return;
            int i = (int)id;
            if (!double.IsNaN(lampTime[i])) return;
            lampTime[i] = vessel.missionTime;
            AddEvent(EventRu[i], EventPl[i]);
        }

        private void AddEvent(string ru, string pl)
        {
            if (vessel == null) return;
            events.Add(new Ev { t = vessel.missionTime, ru = ru, pl = pl });
            if (events.Count > 6) events.RemoveAt(0);
            marks.Add(Current(vessel));
            dirty = true;
        }

        // ================================================================== cykl życia

        private void Start()
        {
            instance = this;
            Hud.Hook();
            BalancerSettings.EnsureLoaded();
            ResetLamps();
            GameEvents.onStageActivate.Add(OnStage);
            GameEvents.onVesselCreate.Add(OnVesselCreate);
        }

        private void OnDestroy()
        {
            GameEvents.onStageActivate.Remove(OnStage);
            GameEvents.onVesselCreate.Remove(OnVesselCreate);
            if (instance == this) instance = null;
            if (graph != null) Destroy(graph);
        }

        private void ResetLamps()
        {
            for (int i = 0; i < lampTime.Length; i++) lampTime[i] = double.NaN;
        }

        private void OnStage(int stage)
        {
            AddEvent("ЭТАП " + stage, "STAGE " + stage);
        }

        private void OnVesselCreate(Vessel v)
        {
            if (vessel == null || v == null || v == vessel || v.parts == null) return;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                string t = ((p.partInfo != null ? p.partInfo.title : "") + " " + p.name).ToLowerInvariant();
                if (t.Contains("fairing") || t.Contains("обтекател") || t.Contains("owiewk"))
                {
                    SetLamp(LampId.Fairing);
                    return;
                }
            }
        }

        private void FixedUpdate()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            if (vessel == null)
            {
                if (v.situation == Vessel.Situations.PRELAUNCH || v.missionTime <= 0) return;
                vessel = v;
                lat0 = v.latitude;
                lon0 = v.longitude;
                samples.Clear(); marks.Clear(); events.Clear(); ResetLamps();
                maxQ = maxG = 0;
                nextSample = 0;
                groups = EngineGroups.Build(v.parts, null);
                SetLamp(LampId.Liftoff);
            }
            if (vessel.mainBody == null) return;

            maxQ = Math.Max(maxQ, vessel.dynamicPressurekPa);
            maxG = Math.Max(maxG, vessel.geeForce);
            if (double.IsNaN(lampTime[(int)LampId.MaxQ]) && maxQ > 5.0 && vessel.dynamicPressurekPa < 0.95 * maxQ)
                SetLamp(LampId.MaxQ);

            if (double.IsNaN(lampTime[(int)LampId.Orbit]) && vessel.situation == Vessel.Situations.ORBITING &&
                vessel.orbit.PeA > (vessel.mainBody.atmosphere ? vessel.mainBody.atmosphereDepth : 1000.0))
                SetLamp(LampId.Orbit);

            if (vessel.missionTime >= nextSample)
            {
                nextSample = vessel.missionTime + SampleInterval;
                samples.Add(Current(vessel));
                if (samples.Count > 3000)   // długi lot: co druga próbka, żeby wykres nie spowalniał gry
                {
                    List<Sample> thin = new List<Sample>(samples.Count / 2 + 1);
                    for (int i = 0; i < samples.Count; i += 2) thin.Add(samples[i]);
                    if ((samples.Count - 1) % 2 != 0) thin.Add(samples[samples.Count - 1]);
                    samples.Clear(); samples.AddRange(thin);
                }
                dirty = true;
            }
        }

        private Sample Current(Vessel v)
        {
            return new Sample
            {
                t = (float)v.missionTime,
                dr = (float)(Downrange(lat0, lon0, v.latitude, v.longitude, v.mainBody.Radius) / 1000.0),
                alt = (float)(v.altitude / 1000.0),
                vel = (float)v.obt_speed,
                q = (float)v.dynamicPressurekPa,
                g = (float)v.geeForce
            };
        }

        private static double Downrange(double la1, double lo1, double la2, double lo2, double radius)
        {
            double d2r = Math.PI / 180.0;
            double dLa = (la2 - la1) * d2r, dLo = (lo2 - lo1) * d2r;
            double a = Math.Sin(dLa / 2) * Math.Sin(dLa / 2) +
                       Math.Cos(la1 * d2r) * Math.Cos(la2 * d2r) * Math.Sin(dLo / 2) * Math.Sin(dLo / 2);
            return 2.0 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        // ================================================================== okno

        private void OnGUI()
        {
            if (!BalancerSettings.Telemetry || FlightGlobals.ActiveVessel == null) return;
            if (Hud.Hidden && !BalancerSettings.TelemetryOnF2) return;

            SovietSkin.Ensure((int)W, (int)H, (int)CrtRect.width, (int)CrtRect.height,
                              (int)AmberRect.width, (int)AmberRect.height, (int)PlateRect.width, (int)PlateRect.height);
            if (!styled) MakeStyles();
            if (dirty && Event.current.type == EventType.Repaint) { Redraw(); dirty = false; }

            float sc = Mathf.Clamp(BalancerSettings.TelemetryScale, MinScale, MaxScale);
            // okno nie może uciec za ekran
            window.x = Mathf.Clamp(window.x, -W + 80, Screen.width / sc - 80);
            window.y = Mathf.Clamp(window.y, 0, Screen.height / sc - 40);

            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sc, sc, 1f));
            window = GUI.Window(WindowId, window, DrawWindow, "", sWin);
            GUI.matrix = old;
        }

        /// <summary>Przeciąganie uchwytu w prawym dolnym rogu zmienia skalę okna (lewy górny róg zostaje w miejscu).</summary>
        private void Update()
        {
            if (!resizing) return;
            if (!Input.GetMouseButton(0))
            {
                resizing = false;
                BalancerSettings.Save();
                return;
            }
            float sc = BalancerSettings.TelemetryScale;
            float left = window.x * sc, top = window.y * sc;
            float mx = Input.mousePosition.x, my = Screen.height - Input.mousePosition.y;
            float ns = Mathf.Clamp(Math.Max((mx - left) / W, (my - top) / H), MinScale, MaxScale);
            BalancerSettings.TelemetryScale = ns;
            window.x = left / ns;
            window.y = top / ns;
        }

        private void MakeStyles()
        {
            styled = true;
            sWin = new GUIStyle();
            sPlate = SovietSkin.Style(12, SovietSkin.Paint, TextAnchor.MiddleLeft);
            sPlateR = SovietSkin.Style(12, SovietSkin.Brass, TextAnchor.MiddleRight);
            sCap = SovietSkin.Style(10, SovietSkin.Paint, TextAnchor.MiddleCenter);
            sDrum = SovietSkin.Style(17, SovietSkin.DrumText, TextAnchor.MiddleCenter);
            sUnit = SovietSkin.Style(11, SovietSkin.Brass, TextAnchor.MiddleLeft);
            sCrt = SovietSkin.Style(12, SovietSkin.Phosphor, TextAnchor.MiddleLeft);
            sCrtR = SovietSkin.Style(12, SovietSkin.Phosphor, TextAnchor.MiddleRight);
            sCrtSmall = SovietSkin.Style(10, SovietSkin.PhosphorDim, TextAnchor.MiddleRight);
            sAmber = SovietSkin.Style(11, SovietSkin.AmberText, TextAnchor.MiddleLeft);
            sLamp = SovietSkin.Style(10, SovietSkin.Paint, TextAnchor.MiddleLeft);
            sNote = SovietSkin.Style(10, SovietSkin.Paint, TextAnchor.MiddleLeft);
            sBtn = SovietSkin.Style(10, SovietSkin.DrumText, TextAnchor.MiddleCenter);
            sBtn.normal.background = SovietSkin.Button;
            sBtn.hover.background = SovietSkin.Button;
            sBtn.hover.textColor = Color.white;
            sBtn.active.background = SovietSkin.ButtonOn;
            sBtn.active.textColor = SovietSkin.Phosphor;
            sBtn.border = new RectOffset(3, 3, 3, 3);
            sBtnOn = new GUIStyle(sBtn);
            sBtnOn.normal.background = SovietSkin.ButtonOn;
            sBtnOn.normal.textColor = SovietSkin.Phosphor;
            sBtnOn.hover.background = SovietSkin.ButtonOn;
        }

        private void DrawWindow(int id)
        {
            Vessel v = FlightGlobals.ActiveVessel;

            // blacha, śrubki, tabliczka
            GUI.DrawTexture(new Rect(0, 0, W, H), SovietSkin.Metal);
            Screw(6, 6); Screw(W - 18, 6); Screw(6, H - 18); Screw(W - 18, H - 18);
            GUI.DrawTexture(PlateRect, SovietSkin.Plate);
            GUI.Label(new Rect(PlateRect.x + 10, PlateRect.y, 300, PlateRect.height), L("ЦУП · ТЕЛЕМЕТРИЯ", "ЦУП · TELEMETRIA"), sPlate);
            GUI.Label(new Rect(PlateRect.x + 300, PlateRect.y, PlateRect.width - 310, PlateRect.height), "«" + v.vesselName + "»", sPlateR);

            if (GUI.Button(new Rect(W - 112, 13, 44, 24), Ru ? "PL" : "RU", sBtn))
            {
                BalancerSettings.TelemetryRussian = !BalancerSettings.TelemetryRussian;
                BalancerSettings.Save();
                dirty = true;
            }
            if (GUI.Button(new Rect(W - 62, 13, 44, 24), "X", sBtn))
            {
                BalancerSettings.Telemetry = false;
                BalancerSettings.Save();
            }

            DrawCounters(v);
            DrawLamps(v);
            DrawAmber();
            DrawCompare();
            DrawCrt(v);
            DrawModes();

            // uchwyt skalowania (prawy dolny róg)
            Rect grip = new Rect(W - 34, H - 34, 22, 22);
            GUI.DrawTexture(grip, SovietSkin.Grip);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && grip.Contains(Event.current.mousePosition))
            {
                resizing = true;
                Event.current.Use();
            }

            GUI.DragWindow(new Rect(0, 0, W, 44));
        }

        private static void Screw(float x, float y)
        {
            GUI.DrawTexture(new Rect(x, y, 12, 12), SovietSkin.Screw);
        }

        // ---------------------------------------------------------------- liczniki bębenkowe

        private void DrawCounters(Vessel v)
        {
            double t = Math.Max(0, v.missionTime);
            Counter(16, 50, L("ВРЕМЯ ПОЛЁТА", "CZAS LOTU"), Pad(Math.Min(t, 99999).ToString("F0", Inv), 5), L("с", "s"));
            Counter(262, 50, L("ВЫСОТА", "WYSOKOŚĆ"), Pad(Comma((v.altitude / 1000.0).ToString("F1", Inv)), 6), L("км", "km"));
            Counter(16, 104, L("СКОРОСТЬ", "PRĘDKOŚĆ"), Pad(Math.Min(v.obt_speed, 99999).ToString("F0", Inv), 5), L("м/с", "m/s"));
            Counter(262, 104, L("ПЕРЕГРУЗКА", "PRZECIĄŻENIE"), Pad(Comma(v.geeForce.ToString("F2", Inv)), 5), "g");
        }

        private void Counter(float x, float y, string caption, string digits, string unit)
        {
            const float boxW = 242f;
            SovietSkin.Printed(new Rect(x, y, boxW, 14), caption, sCap);
            const float cw = 16f, chh = 26f;
            float unitW = 30f;
            float total = digits.Length * cw + unitW + 6;
            float x0 = x + (boxW - total) / 2f;
            GUI.DrawTexture(new Rect(x0 - 3, y + 16, total, chh + 6), SovietSkin.Slot);
            for (int i = 0; i < digits.Length; i++)
            {
                Rect c = new Rect(x0 + i * cw, y + 19, cw, chh);
                GUI.DrawTexture(c, SovietSkin.Drum);
                GUI.Label(c, digits[i].ToString(), sDrum);
            }
            GUI.Label(new Rect(x0 + digits.Length * cw + 4, y + 19, unitW, chh), unit, sUnit);
        }

        private static string Pad(string s, int n) { return s.Length >= n ? s : new string(' ', n - s.Length) + s; }
        private static string Comma(string s) { return s.Replace('.', ','); }

        // ---------------------------------------------------------------- lampki

        private void DrawLamps(Vessel v)
        {
            string[] labels = Ru ? LampRu : LampPl;
            LampId[] set = Kind == RocketKind.Energia ? LampsEnergia : LampsSoyuz;
            for (int k = 0; k < 8; k++)
            {
                int i = (int)set[k];
                float x = 20 + (k % 2) * 246, y = 166 + (k / 2) * 24;
                Texture2D tex = SovietSkin.LampOff;
                if (!double.IsNaN(lampTime[i]))
                    tex = v.missionTime - lampTime[i] < FreshLamp ? SovietSkin.LampAmber : SovietSkin.LampGreen;
                GUI.DrawTexture(new Rect(x - 4, y - 4, 26, 26), tex);
                SovietSkin.Printed(new Rect(x + 22, y, 220, 18), labels[i], sLamp);
            }
        }

        // ---------------------------------------------------------------- taśma zdarzeń (bursztynowa)

        private void DrawAmber()
        {
            GUI.DrawTexture(AmberRect, SovietSkin.Amber);
            float y = AmberRect.y + 8;
            int start = Math.Max(0, events.Count - 5);
            for (int i = start; i < events.Count; i++)
            {
                Ev e = events[i];
                string line = (Ru ? "Т+" : "T+") + e.t.ToString("F0", Inv) + " " + (Ru ? e.ru : e.pl);
                if (i == events.Count - 1 && (Time.realtimeSinceStartup % 1f) < 0.5f) line += " █";
                SovietSkin.Glow(new Rect(AmberRect.x + 10, y, AmberRect.width - 18, 18), line, sAmber);
                y += 19;
            }
            if (events.Count == 0)
                SovietSkin.Glow(new Rect(AmberRect.x + 10, y, AmberRect.width - 18, 18), L("ГОТОВНОСТЬ К ПУСКУ", "GOTOWOŚĆ DO STARTU"), sAmber);
            GUI.DrawTexture(AmberRect, SovietSkin.AmberOverlay);
        }

        // ---------------------------------------------------------------- porównanie

        private void DrawCompare()
        {
            float y = AmberRect.yMax + 12;
            if (GUI.Button(new Rect(16, y, 330, 26), L("СРАВНИТЬ С ПОЛЁТОМ", "PORÓWNAJ Z LOTEM"), sBtn)) NextReference();
            if (GUI.Button(new Rect(354, y, 150, 26), L("СБРОС", "WYCZYŚĆ"), sBtn))
            {
                refIndex = -1; refName = ""; refSamples.Clear(); dirty = true;
            }
            string txt = refName.Length == 0 ? L("— БЕЗ СРАВНЕНИЯ", "— bez porównania") : "— — " + refName;
            SovietSkin.Printed(new Rect(16, y + 32, 440, 16), txt, sNote);
        }

        // ---------------------------------------------------------------- kineskop

        private void DrawCrt(Vessel v)
        {
            Rect r = CrtRect;
            GUI.DrawTexture(r, SovietSkin.Crt);

            float xMax, yMax; string xu, yu;
            Ranges(out xMax, out yMax, out xu, out yu);
            xMax = Nice(xMax); yMax = Nice(yMax);

            SovietSkin.Glow(new Rect(r.x + GX, r.y + 12, 330, 18), (Ru ? TitleRu : TitlePl)[mode], sCrt);
            SovietSkin.Glow(new Rect(r.x + GX + GW - 120, r.y + 12, 120, 18), Fmt(yMax) + " " + yu, sCrtR);

            Rect g = new Rect(r.x + GX, r.y + GY, GW, GH);
            if (graph != null) GUI.DrawTexture(g, graph);
            if (vessel == null)
                SovietSkin.Glow(new Rect(g.x + 10, g.y + 10, 300, 18), L("ОЖИДАНИЕ ПУСКА…", "OCZEKIWANIE NA START…"), sCrt);

            if ((mode == 0 || mode == 1) && v.mainBody.atmosphere)
            {
                float ay = g.yMax - (float)(v.mainBody.atmosphereDepth / 1000.0 / yMax) * GH;
                if (ay > g.y + 14)
                    GUI.Label(new Rect(g.x, ay - 14, GW - 4, 12), L("ГРАНИЦА АТМОСФЕРЫ", "GRANICA ATMOSFERY"), sCrtSmall);
            }

            GUI.Label(new Rect(g.x, g.yMax + 2, 60, 14), "0", sCrtSmall);
            SovietSkin.Glow(new Rect(g.x, g.yMax + 2, GW, 14), Fmt(xMax) + " " + xu, sCrtR);

            // liczby pod wykresem
            float ly = g.yMax + 20;
            float cx2 = r.x + GX + GW / 2f;
            double dr = vessel != null && vessel.mainBody == v.mainBody ? Downrange(lat0, lon0, v.latitude, v.longitude, v.mainBody.Radius) / 1000.0 : 0;
            Line2(r.x + GX, cx2, ref ly,
                  L("АПОГЕЙ ", "APOCENTRUM ") + Comma((v.orbit.ApA / 1000.0).ToString("F1", Inv)) + L(" км", " km"),
                  L("ПЕРИГЕЙ ", "PERYCENTRUM ") + Comma((v.orbit.PeA / 1000.0).ToString("F1", Inv)) + L(" км", " km"));
            Line2(r.x + GX, cx2, ref ly,
                  L("НАКЛОН ", "INKLINACJA ") + Comma(v.orbit.inclination.ToString("F2", Inv)) + "°",
                  L("ДАЛЬНОСТЬ ", "ZASIĘG ") + dr.ToString("F0", Inv) + L(" км", " km"));
            Line2(r.x + GX, cx2, ref ly,
                  L("ТАНГАЖ ", "POCHYLENIE ") + Comma(Pitch(v).ToString("F1", Inv)) + "°  " + L("АТАКА ", "AoA ") + Comma(Aoa(v).ToString("F1", Inv)) + "°",
                  L("НАПОР ", "CIŚN. DYN. ") + Comma(v.dynamicPressurekPa.ToString("F1", Inv)) + L(" кПа (МАКС ", " kPa (maks ") + Comma(maxQ.ToString("F1", Inv)) + ")");
            Line2(r.x + GX, cx2, ref ly,
                  L("МАССА ", "MASA ") + Comma(v.GetTotalMass().ToString("F1", Inv)) + L(" т", " t"),
                  L("ТЯГОВООР. ", "TWR ") + Comma(Twr(v).ToString("F2", Inv)) + L("  МАКС g ", "  maks g ") + Comma(maxG.ToString("F2", Inv)));

            int shown = 0;
            foreach (EngineGroup grp in groups)
            {
                int run; double thrust, remaining;
                Live(grp, v, out run, out thrust, out remaining);
                if (run == 0 || shown >= 2) continue;
                shown++;
                string line = grp.engines + "× " + grp.title.ToUpperInvariant() + ": " + thrust.ToString("F0", Inv) + L(" кН · ОСТ. ", " kN · zostało ") +
                              (remaining >= 0 ? remaining.ToString("F0", Inv) + L(" с", " s") : "–");
                SovietSkin.Glow(new Rect(r.x + GX, ly, GW, 16), line, sCrt);
                ly += 16;
            }
            if (shown == 0 && vessel != null)
                SovietSkin.Glow(new Rect(r.x + GX, ly, GW, 16), L("ДВИГАТЕЛИ ВЫКЛЮЧЕНЫ", "SILNIKI WYŁĄCZONE"), sCrt);

            GUI.DrawTexture(r, SovietSkin.CrtOverlay);
        }

        private void Line2(float x1, float x2, ref float y, string a, string b)
        {
            SovietSkin.Glow(new Rect(x1, y, x2 - x1 - 6, 16), a, sCrt);
            SovietSkin.Glow(new Rect(x2, y, CrtRect.xMax - x2 - GX, 16), b, sCrt);
            y += 16;
        }

        private void DrawModes()
        {
            string[] names = Ru ? ModeRu : ModePl;
            float bw = (CrtRect.width - 4 * 6) / 5f;
            for (int i = 0; i < 5; i++)
            {
                Rect b = new Rect(CrtRect.x + i * (bw + 6), CrtRect.yMax + 12, bw, 28);
                if (GUI.Button(b, names[i], i == mode ? sBtnOn : sBtn) && mode != i) { mode = i; dirty = true; }
            }
        }

        private static string Fmt(float v) { return Comma(v.ToString("0.#", Inv)); }

        private static double Pitch(Vessel v)
        {
            Vector3 up = ((Vector3)(v.CoM - v.mainBody.position)).normalized;
            return 90.0 - Vector3.Angle(v.transform.up, up);
        }

        private static double Aoa(Vessel v)
        {
            return v.srfSpeed > 1 ? Vector3.Angle(v.transform.up, (Vector3)v.srf_velocity) : 0.0;
        }

        private static double Twr(Vessel v)
        {
            double thrust = 0;
            foreach (Part p in v.parts)
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited) thrust += e.finalThrust;
            double r = (v.CoM - v.mainBody.position).magnitude;
            double g = v.mainBody.gravParameter / (r * r);
            double m = v.GetTotalMass();
            return m > 0 ? thrust / (m * g) : 0;
        }

        /// <summary>Stan grupy: ile silników pracuje, ich ciąg i pozostały czas pracy (paliwo ÷ przepływ).</summary>
        private static void Live(EngineGroup g, Vessel v, out int run, out double thrust, out double remaining)
        {
            run = 0; thrust = 0; remaining = -1;
            double flow = 0;
            List<Part> attached = new List<Part>();
            foreach (Part p in g.parts)
            {
                if (p == null || p.vessel != v) continue;
                attached.Add(p);
                foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>())
                {
                    if (!e.EngineIgnited || e.flameout) continue;
                    run++;
                    thrust += e.finalThrust;
                    if (e.realIsp > 0) flow += e.finalThrust / (e.realIsp * G0);
                    break;
                }
            }
            if (run == 0 || flow <= 0) return;

            HashSet<Part> decs = new HashSet<Part>();
            foreach (Part p in attached) decs.Add(EngineGroups.JettisonDecoupler(p));
            remaining = EngineGroups.FuelMass(v.parts, decs, EngineGroups.PropellantNames(attached)) / flow;
        }

        // ================================================================== wykres (tekstura z poświatą)

        private float X(Sample s) { return mode == 0 ? s.dr : s.t; }

        private float Y(Sample s)
        {
            switch (mode)
            {
                case 2: return s.vel;
                case 3: return s.q;
                case 4: return s.g;
                default: return s.alt;
            }
        }

        private void Ranges(out float xMax, out float yMax, out string xu, out string yu)
        {
            xMax = 1f; yMax = 1f;
            foreach (Sample s in samples) { xMax = Math.Max(xMax, X(s)); yMax = Math.Max(yMax, Y(s)); }
            foreach (Sample s in refSamples) { xMax = Math.Max(xMax, X(s)); yMax = Math.Max(yMax, Y(s)); }
            xMax *= 1.05f; yMax *= 1.05f;
            xu = mode == 0 ? L("км", "km") : L("с", "s");
            yu = mode == 2 ? L("м/с", "m/s") : mode == 3 ? L("кПа", "kPa") : mode == 4 ? "g" : L("км", "km");
        }

        private static float Nice(float v)
        {
            if (v <= 0) return 1f;
            double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double f = v / e;
            double n = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
            return (float)(n * e);
        }

        private void Redraw()
        {
            if (graph == null)
            {
                graph = new Texture2D(GW, GH, TextureFormat.RGBA32, false);
                graph.filterMode = FilterMode.Bilinear;
                graph.wrapMode = TextureWrapMode.Clamp;
                buf = new Color32[GW * GH];
            }
            for (int i = 0; i < buf.Length; i++) buf[i] = new Color32(0, 0, 0, 0);

            float xMax, yMax; string xu, yu;
            Ranges(out xMax, out yMax, out xu, out yu);
            xMax = Nice(xMax); yMax = Nice(yMax);

            Color grid = new Color(0.06f, 0.23f, 0.10f, 1f);
            for (int k = 1; k < 10; k++) { int gx = k * (GW - 1) / 10; Line(gx, 0, gx, GH - 1, grid, 1); }
            for (int k = 1; k < 5; k++) { int gy = k * (GH - 1) / 5; Line(0, gy, GW - 1, gy, grid, 1); }

            Vessel v = FlightGlobals.ActiveVessel;
            if ((mode == 0 || mode == 1) && v != null && v.mainBody.atmosphere)
            {
                int ay = Py((float)(v.mainBody.atmosphereDepth / 1000.0), yMax);
                Color atm = new Color(0.11f, 0.42f, 0.20f, 1f);
                for (int x = 0; x < GW; x += 5) Line(x, ay, Math.Min(x + 2, GW - 1), ay, atm, 1);
            }

            Color refC = new Color(0.25f, 0.62f, 0.33f, 1f);
            for (int i = 1; i < refSamples.Count; i++)
                if ((i / 3) % 2 == 0) Seg(refSamples[i - 1], refSamples[i], xMax, yMax, refC, 0.9f, 1);

            Color main = new Color(0.55f, 1f, 0.67f, 1f);
            for (int i = 1; i < samples.Count; i++) Seg(samples[i - 1], samples[i], xMax, yMax, main, 0.35f, 3); // poświata
            for (int i = 1; i < samples.Count; i++) Seg(samples[i - 1], samples[i], xMax, yMax, main, 1f, 1);

            Color amber = new Color(1f, 0.70f, 0.25f, 1f);
            foreach (Sample m in marks)
            {
                int px = Px(X(m), xMax), py = Py(Y(m), yMax);
                Line(px - 4, py, px + 4, py, amber, 1);
                Line(px, py - 4, px, py + 4, amber, 1);
            }

            if (samples.Count > 0)
            {
                Sample last = samples[samples.Count - 1];
                Dot(Px(X(last), xMax), Py(Y(last), yMax), 3, new Color(0.78f, 1f, 0.83f, 1f));
            }

            graph.SetPixels32(buf);
            graph.Apply();
        }

        private int Px(float x, float xMax) { return Mathf.Clamp((int)(x / xMax * (GW - 1)), 0, GW - 1); }
        private int Py(float y, float yMax) { return Mathf.Clamp((int)(y / yMax * (GH - 1)), 0, GH - 1); }

        private void Seg(Sample a, Sample b, float xMax, float yMax, Color c, float alpha, int width)
        {
            c.a = alpha;
            Line(Px(X(a), xMax), Py(Y(a), yMax), Px(X(b), xMax), Py(Y(b), yMax), c, width);
        }

        private void Line(int x0, int y0, int x1, int y1, Color c, int width)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            int h = width / 2;
            for (int guard = 0; guard < 4000; guard++)
            {
                for (int ox = -h; ox <= h; ox++)
                    for (int oy = -h; oy <= h; oy++)
                        Plot(x0 + ox, y0 + oy, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private void Dot(int cx, int cy, int r, Color c)
        {
            for (int y = -r - 3; y <= r + 3; y++)
                for (int x = -r - 3; x <= r + 3; x++)
                {
                    float d = (float)Math.Sqrt(x * x + y * y);
                    if (d <= r) { Color cc = c; cc.a = 1; Plot(cx + x, cy + y, cc); }
                    else if (d <= r + 3) { Color cc = c; cc.a = 0.35f * (1 - (d - r) / 3f); Plot(cx + x, cy + y, cc); }
                }
        }

        /// <summary>Mieszanie alfa – poświata nakłada się na siatkę, a tło kineskopu prześwituje.</summary>
        private void Plot(int x, int y, Color c)
        {
            if (x < 0 || x >= GW || y < 0 || y >= GH) return;
            int i = y * GW + x;
            Color o = buf[i];
            float a = c.a + o.a * (1 - c.a);
            if (a <= 0) return;
            Color r = (c * c.a + o * (o.a * (1 - c.a))) / a;
            r.a = a;
            buf[i] = r;
        }

        // ================================================================== porównanie z poprzednim lotem

        private void NextReference()
        {
            List<string> files = new List<string>();
            foreach (string dir in FlightLogger.LogFolders)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*.csv"))
                        if (!string.Equals(f, FlightLogger.CurrentFile, StringComparison.OrdinalIgnoreCase)) files.Add(f);
                }
                catch { }
            }
            files.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            refFiles = files.ToArray();

            if (refFiles.Length == 0) { refName = L("НЕТ ЗАПИСЕЙ", "brak dzienników"); refSamples.Clear(); dirty = true; return; }
            refIndex = (refIndex + 1) % refFiles.Length;
            LoadReference(refFiles[refIndex]);
        }

        private void LoadReference(string path)
        {
            refSamples.Clear();
            refName = Path.GetFileNameWithoutExtension(path);
            try
            {
                double radius = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody.Radius : 6371000.0;
                int iT = -1, iLa = -1, iLo = -1, iAlt = -1, iV = -1, iQ = -1, iG = -1;
                bool header = false, first = true;
                double la0 = 0, lo0 = 0;

                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split(';');
                    if (!header)
                    {
                        for (int i = 0; i < f.Length; i++)
                        {
                            if (f[i].StartsWith("T+")) iT = i;
                            else if (f[i].StartsWith("szer.")) iLa = i;
                            else if (f[i].StartsWith("dług.")) iLo = i;
                            else if (f[i].StartsWith("wysokość [")) iAlt = i;
                            else if (f[i].StartsWith("prędk. orbitalna")) iV = i;
                            else if (f[i].StartsWith("ciśn. dynamiczne")) iQ = i;
                            else if (f[i].StartsWith("przeciążenie")) iG = i;
                        }
                        header = true;
                        continue;
                    }

                    float t = P(f, iT), alt = P(f, iAlt);
                    if (float.IsNaN(t) || float.IsNaN(alt)) continue;
                    if (refSamples.Count > 0 && t - refSamples[refSamples.Count - 1].t < (float)SampleInterval) continue;

                    double la = P(f, iLa), lo = P(f, iLo);
                    float dr = 0f;
                    if (!double.IsNaN(la) && !double.IsNaN(lo))
                    {
                        if (first) { la0 = la; lo0 = lo; first = false; }
                        dr = (float)(Downrange(la0, lo0, la, lo, radius) / 1000.0);
                    }

                    refSamples.Add(new Sample { t = t, dr = dr, alt = alt, vel = Z(P(f, iV)), q = Z(P(f, iQ)), g = Z(P(f, iG)) });
                }
            }
            catch (Exception e)
            {
                refName = L("ОШИБКА ЧТЕНИЯ", "błąd odczytu");
                Debug.LogWarning("[RSSBalancer] Telemetria: nie można wczytać " + path + ": " + e.Message);
            }
            dirty = true;
        }

        private static float P(string[] f, int i)
        {
            float v;
            if (i < 0 || i >= f.Length) return float.NaN;
            return float.TryParse(f[i], NumberStyles.Float, Inv, out v) ? v : float.NaN;
        }

        private static float Z(float v) { return float.IsNaN(v) ? 0f : v; }
    }
}
