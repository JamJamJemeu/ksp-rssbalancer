using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Presety ustawień MechJeba (Ascent Guidance / PVG, przepustnica, staging, manewry) wczytywane z plików
    /// GameData/RSSBalancer/PluginData/MechJeb/*.cfg. MechJeb jest obsługiwany przez refleksję –
    /// RSSBalancer nie zależy od MechJeba i działa bez niego.
    ///
    /// Format pliku:
    ///   MECHJEB_PRESET
    ///   {
    ///       name = …            // nazwa na liście
    ///       description = …     // opis (może być wiele linii description)
    ///       MODULE              // jeden blok na moduł MechJeba
    ///       {
    ///           name = MechJebModuleAscentSettings
    ///           DesiredOrbitAltitude = -10000   // nazwy pól jak w kodzie MechJeba, jednostki SI (m, Pa, m/s²)
    ///       }
    ///   }
    /// </summary>
    public static class MechJebPresets
    {
        public class Preset
        {
            public string name, file;
            public readonly List<string> description = new List<string>();
            public ConfigNode node;
        }

        public static readonly List<Preset> Presets = new List<Preset>();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static string Folder
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "RSSBalancer"), "PluginData"), "MechJeb"); }
        }

        public static void Reload()
        {
            Presets.Clear();
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (string f in Directory.GetFiles(Folder, "*.cfg"))
                {
                    ConfigNode root = ConfigNode.Load(f);
                    if (root == null) continue;
                    foreach (ConfigNode n in root.GetNodes("MECHJEB_PRESET"))
                    {
                        Preset p = new Preset { node = n, file = Path.GetFileName(f), name = n.GetValue("name") ?? Path.GetFileNameWithoutExtension(f) };
                        foreach (string d in n.GetValues("description")) p.description.Add(d);
                        Presets.Add(p);
                    }
                }
            }
            catch (Exception e) { Debug.Log("[RSSBalancer] MechJebPresets.Reload: " + e); }
        }

        // ------------------------------------------------------------------ dostęp do MechJeba

        private static List<Part> ShipParts()
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null) return FlightGlobals.ActiveVessel.parts;
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null) return EditorLogic.fetch.ship.parts;
            return null;
        }

        /// <summary>Wszystkie MechJebCore na statku (każdy ma własne ustawienia – ustawiamy wszystkie).</summary>
        private static List<PartModule> Cores()
        {
            List<PartModule> list = new List<PartModule>();
            List<Part> parts = ShipParts();
            if (parts == null) return list;
            foreach (Part p in parts)
                foreach (PartModule pm in p.Modules)
                    if (pm != null && pm.GetType().Name == "MechJebCore") list.Add(pm);
            return list;
        }

        public static bool MechJebPresent { get { return Cores().Count > 0; } }

        private static object Module(PartModule core, string type)
        {
            MethodInfo m = core.GetType().GetMethod("GetComputerModule", new[] { typeof(string) });
            return m != null ? m.Invoke(core, new object[] { type }) : null;
        }

        /// <summary>Ustawia jedno pole/właściwość modułu MechJeba. Obsługuje EditableDouble/Int (pole Val), bool, liczby i listy int.</summary>
        internal static bool SetMember(object target, string name, string value, out string error)
        {
            error = null;
            Type t = target.GetType();
            FieldInfo f = null;
            PropertyInfo prop = null;
            for (Type tt = t; tt != null && f == null && prop == null; tt = tt.BaseType)
            {
                f = tt.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f == null) prop = tt.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
            }
            if (f == null && prop == null) { error = "brak pola " + name; return false; }

            Type mt = f != null ? f.FieldType : prop.PropertyType;
            object cur = f != null ? f.GetValue(target) : (prop.CanRead ? prop.GetValue(target, null) : null);

            // Editable* z MechJeba: mają właściwość Val
            if (cur != null && mt.Name.StartsWith("Editable"))
            {
                PropertyInfo val = cur.GetType().GetProperty("Val", Flags);
                FieldInfo valF = val == null ? cur.GetType().GetField("Val", Flags) : null;
                Type vt = val != null ? val.PropertyType : valF != null ? valF.FieldType : null;
                if (vt == null) { error = name + ": nieznany typ " + mt.Name; return false; }
                if (vt == typeof(List<int>))
                {
                    List<int> l = (List<int>)(val != null ? val.GetValue(cur, null) : valF.GetValue(cur));
                    l.Clear();
                    foreach (string s in value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int i;
                        if (int.TryParse(s, NumberStyles.Integer, Inv, out i)) l.Add(i);
                    }
                    // tekst w okienku MechJeba
                    PropertyInfo txt = cur.GetType().GetProperty("Text", Flags);
                    if (txt != null && txt.CanWrite) try { txt.SetValue(cur, string.Join(",", l.ConvertAll(x => x.ToString(Inv)).ToArray()), null); } catch { }
                    return true;
                }
                object parsed;
                if (!Parse(value, vt, out parsed)) { error = name + ": zła wartość " + value; return false; }
                if (val != null && val.CanWrite) val.SetValue(cur, parsed, null);
                else if (valF != null) valF.SetValue(cur, parsed);
                else { error = name + ": tylko do odczytu"; return false; }
                return true;
            }

            object v;
            if (!Parse(value, mt, out v)) { error = name + ": zła wartość " + value; return false; }
            if (prop != null && prop.CanWrite) { prop.SetValue(target, v, null); return true; }
            if (f != null && !f.IsInitOnly) { f.SetValue(target, v); return true; }
            error = name + ": tylko do odczytu";
            return false;
        }

        private static bool Parse(string s, Type t, out object v)
        {
            v = null;
            s = s.Trim();
            if (t == typeof(bool)) { bool b; if (!bool.TryParse(s, out b)) return false; v = b; return true; }
            if (t == typeof(int)) { int i; if (!int.TryParse(s, NumberStyles.Integer, Inv, out i)) return false; v = i; return true; }
            if (t == typeof(float)) { float x; if (!float.TryParse(s, NumberStyles.Float, Inv, out x)) return false; v = x; return true; }
            if (t == typeof(double)) { double d; if (!double.TryParse(s, NumberStyles.Float, Inv, out d)) return false; v = d; return true; }
            if (t == typeof(string)) { v = s; return true; }
            if (t.IsEnum) { try { v = Enum.Parse(t, s, true); return true; } catch { return false; } }
            return false;
        }

        /// <summary>Wczytuje preset do MechJeba na statku. Zwraca opis wyniku.</summary>
        public static string Apply(Preset p)
        {
            List<PartModule> cores = Cores();
            if (cores.Count == 0) return "Na statku nie ma MechJeba.";
            int ok = 0;
            List<string> errors = new List<string>();
            foreach (PartModule core in cores)
                foreach (ConfigNode mod in p.node.GetNodes("MODULE"))
                {
                    string type = mod.GetValue("name");
                    object m = string.IsNullOrEmpty(type) ? null : Module(core, type);
                    if (m == null) { if (!errors.Contains("brak modułu " + type)) errors.Add("brak modułu " + type); continue; }
                    foreach (ConfigNode.Value v in mod.values)
                    {
                        if (v.name == "name") continue;
                        string err;
                        if (SetMember(m, v.name, v.value, out err)) ok++;
                        else if (!errors.Contains(err)) errors.Add(err);
                    }
                }
            string msg = "MechJeb: wczytano „" + p.name + "” (" + (ok / Math.Max(1, cores.Count)) + " ustawień" + (cores.Count > 1 ? ", " + cores.Count + " rdzenie MechJeba" : "") + ").";
            if (errors.Count > 0) msg += "\nNie ustawiono: " + string.Join("; ", errors.ToArray());
            Debug.Log("[RSSBalancer] " + msg);
            return msg;
        }
    }
}
