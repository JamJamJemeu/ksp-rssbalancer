using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Sprzątanie statków: lista wszystkich statków w grze z wyszukiwaniem po nazwie, filtrem typu (odłamki,
    /// asteroidy/komety, sondy…) i ciała, zaznaczaniem wielu naraz i usuwaniem zaznaczonych (Vessel.Die – tak samo
    /// jak „Destroy vessel” w HyperEdit). Aktywnego statku nie da się zaznaczyć. Przed usunięciem okno z potwierdzeniem
    /// (liczba statków i załogi, która zginie) – usunięcia nie da się cofnąć inaczej niż wczytaniem zapisu.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class VesselCleaner : MonoBehaviour
    {
        public static bool Show;
        private const int WindowId = 0x52535356;
        private Rect win = new Rect(420, 120, 560, 520);
        private Vector2 scroll;
        private string search = "";
        private int typeFilter;          // 0 wszystkie, 1 odłamki, 2 asteroidy/komety, 3 bez załogi, 4 z załogą
        private bool onlyThisBody;
        private readonly HashSet<Guid> selected = new HashSet<Guid>();
        private static readonly string[] TypeNames = { "Wszystkie", "Odłamki", "Asteroidy/komety", "Bez załogi", "Z załogą" };
        private string status = "";

        private bool Matches(Vessel v, Vessel active)
        {
            if (v == null || v.state == Vessel.State.DEAD) return false;
            if (!string.IsNullOrEmpty(search) && v.vesselName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (onlyThisBody && active != null && v.mainBody != active.mainBody) return false;
            int crew = v.GetCrewCount();
            switch (typeFilter)
            {
                case 1: return v.vesselType == VesselType.Debris || v.vesselType == VesselType.DroppedPart;
                case 2: return v.vesselType == VesselType.SpaceObject;
                case 3: return crew == 0 && v.vesselType != VesselType.SpaceObject && v.vesselType != VesselType.Flag;
                case 4: return crew > 0;
                default: return true;
            }
        }

        private void OnGUI()
        {
            if (!Show || !HighLogic.LoadedSceneIsFlight) return;
            win = GUILayout.Window(WindowId, win, Draw, "Sprzątanie statków", GUILayout.Width(560), GUILayout.Height(520));
        }

        private void Draw(int id)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Szukaj:", GUILayout.Width(50));
            search = GUILayout.TextField(search ?? "", GUILayout.Width(220));
            if (GUILayout.Button("X", GUILayout.Width(24))) search = "";
            onlyThisBody = GUILayout.Toggle(onlyThisBody, " tylko " + (active != null ? active.mainBody.bodyName : "to ciało"));
            GUILayout.EndHorizontal();
            typeFilter = GUILayout.Toolbar(typeFilter, TypeNames);

            List<Vessel> list = FlightGlobals.Vessels.Where(v => Matches(v, active))
                .OrderBy(v => v.vesselType != VesselType.Debris).ThenBy(v => v.vesselName).ToList();
            // zaznaczenia tylko istniejących statków
            selected.RemoveWhere(g => !FlightGlobals.Vessels.Any(v => v != null && v.id == g && v.state != Vessel.State.DEAD));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Zaznacz widoczne (" + list.Count + ")")) foreach (Vessel v in list) if (v != active) selected.Add(v.id);
            if (GUILayout.Button("Odznacz wszystkie")) selected.Clear();
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(330));
            foreach (Vessel v in list)
            {
                GUILayout.BeginHorizontal();
                bool isActive = v == active;
                bool was = selected.Contains(v.id);
                bool now = GUILayout.Toggle(was && !isActive, "", GUILayout.Width(20));
                if (!isActive && now != was) { if (now) selected.Add(v.id); else selected.Remove(v.id); }
                string dist = active != null && v.mainBody == active.mainBody ? Dist((v.GetWorldPos3D() - active.GetWorldPos3D()).magnitude) : v.mainBody.bodyName;
                int crew = v.GetCrewCount();
                GUILayout.Label((isActive ? "▶ " : "") + v.vesselName, GUILayout.Width(230));
                GUILayout.Label(TypeText(v.vesselType) + (crew > 0 ? ", załoga " + crew : ""), GUILayout.Width(150));
                GUILayout.Label(dist, GUILayout.Width(100));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            int nSel = selected.Count, nCrew = FlightGlobals.Vessels.Where(v => v != null && selected.Contains(v.id)).Sum(v => v.GetCrewCount());
            GUILayout.BeginHorizontal();
            GUI.enabled = nSel > 0;
            if (GUILayout.Button("Usuń zaznaczone (" + nSel + ")" + (nCrew > 0 ? " – załoga " + nCrew + "!" : ""))) Confirm(nSel, nCrew);
            GUI.enabled = true;
            if (GUILayout.Button("Zamknij", GUILayout.Width(80))) Show = false;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
            GUI.DragWindow();
        }

        private void Confirm(int n, int crew)
        {
            string msg = "Usunąć " + n + " " + (n == 1 ? "statek" : "statków") + " z gry?" +
                         (crew > 0 ? "\n\nNa pokładzie jest " + crew + " członków załogi – zginą." : "") +
                         "\n\nTego nie da się cofnąć (tylko wczytaniem zapisu).";
            PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("RSSBalancerVesselCleaner", msg, "Sprzątanie statków", HighLogic.UISkin,
                    new DialogGUIButton("Usuń", DestroySelected),
                    new DialogGUIButton("Anuluj", () => { })),
                false, HighLogic.UISkin);
        }

        private void DestroySelected()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            List<Vessel> kill = FlightGlobals.Vessels.Where(v => v != null && v != active && selected.Contains(v.id) && v.state != Vessel.State.DEAD).ToList();
            int done = 0;
            foreach (Vessel v in kill)
            {
                try { v.Die(); done++; }
                catch (Exception e) { Debug.LogWarning("[RSSBalancer] Sprzątanie: nie udało się usunąć " + v.vesselName + ": " + e.Message); }
            }
            selected.Clear();
            status = "Usunięto " + done + " z " + kill.Count + ".";
            Debug.Log("[RSSBalancer] Sprzątanie statków: " + status);
        }

        private static string Dist(double m)
        {
            return m < 1e4 ? m.ToString("F0") + " m" : m < 1e7 ? (m / 1000).ToString("F0") + " km" : (m / 1e6).ToString("F1") + " Mm";
        }

        private static string TypeText(VesselType t)
        {
            switch (t)
            {
                case VesselType.Debris: return "odłamek";
                case VesselType.DroppedPart: return "część";
                case VesselType.SpaceObject: return "asteroida/kometa";
                case VesselType.Probe: return "sonda";
                case VesselType.Relay: return "przekaźnik";
                case VesselType.Ship: return "statek";
                case VesselType.Station: return "stacja";
                case VesselType.Lander: return "lądownik";
                case VesselType.Rover: return "łazik";
                case VesselType.Base: return "baza";
                case VesselType.Plane: return "samolot";
                case VesselType.EVA: return "EVA";
                case VesselType.Flag: return "flaga";
                default: return t.ToString();
            }
        }
    }
}
