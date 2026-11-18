using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RSSBalancer
{
    /// <summary>
    /// Diagnostyka: kilka sekund po załadowaniu rakiety na wyrzutni zapisuje do PluginData/Logs
    /// plik CZESCI_*.txt z masami, modułami mas, zasobami i silnikami każdej części – to samo,
    /// co czyta symulacja paliwa MechJeba. Oznacza wartości NaN / ujemne / nieskończone.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class PartDump : MonoBehaviour
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private Vessel done;
        private float at = -1f;

        private void Update()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v == done || v.situation != Vessel.Situations.PRELAUNCH) return;
            if (at < 0f) { at = Time.realtimeSinceStartup + 4f; return; }
            if (Time.realtimeSinceStartup < at) return;
            at = -1f;
            done = v;
            try { Dump(v); } catch (Exception e) { Debug.Log("[RSSBalancer] PartDump: " + e); }
        }

        private static string F(double x)
        {
            string s = x.ToString("0.#####", Inv);
            if (double.IsNaN(x) || double.IsInfinity(x)) s += " <<<NaN/Inf";
            return s;
        }

        private static void Dump(Vessel v)
        {
            StringBuilder sb = new StringBuilder();
            int bad = 0;
            sb.AppendLine("# RSSBalancer – części rakiety " + v.vesselName + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            foreach (Part p in v.parts)
            {
                float st = 0f, un = 0f;
                StringBuilder mods = new StringBuilder();
                foreach (PartModule pm in p.Modules)
                {
                    IPartMassModifier mm = pm as IPartMassModifier;
                    if (mm == null) continue;
                    float a = mm.GetModuleMass(p.prefabMass, ModifierStagingSituation.STAGED);
                    float b = mm.GetModuleMass(p.prefabMass, ModifierStagingSituation.UNSTAGED);
                    st += a; un += b;
                    mods.Append("  ").Append(pm.moduleName).Append(" staged=").Append(F(a)).Append(" unstaged=").Append(F(b));
                    if (pm is ModuleTankBalancer t) mods.Append(" [dry×").Append(F(t.dryMassMultiplier)).Append(" fuel×").Append(F(t.fuelMultiplier)).Append(t.scaleWholePart ? " całość" : "").Append("]");
                    if (pm is ModuleEngineBalancer e) mods.Append(" [isp×").Append(F(e.ispMultiplier)).Append(" ciąg×").Append(F(e.thrustMultiplier)).Append(" masa×").Append(F(e.massMultiplier)).Append(e.scaleWholePart ? " całość" : "").Append("]");
                }
                double res = 0.0;
                StringBuilder rs = new StringBuilder();
                foreach (PartResource r in p.Resources)
                {
                    res += r.amount * r.info.density;
                    rs.Append("  ").Append(r.resourceName).Append(" ").Append(F(r.amount)).Append("/").Append(F(r.maxAmount));
                    if (r.amount > r.maxAmount + 1e-6 || r.amount < 0 || r.maxAmount < 0) { rs.Append(" <<<ZŁA ILOŚĆ"); bad++; }
                }
                double dryS = p.prefabMass + st, dryU = p.prefabMass + un;
                string flag = "";
                if (dryS <= 0 || dryU <= 0 || double.IsNaN(dryS) || double.IsNaN(dryU)) { flag = "  <<<MASA ≤ 0 / NaN"; bad++; }
                sb.Append("PART ").Append(p.partInfo != null ? p.partInfo.name : p.name)
                  .Append(" | inverseStage ").Append(p.inverseStage).Append(" | odrzut ").Append(EngineGroups.JettisonDecoupler(p) != null ? EngineGroups.JettisonDecoupler(p).inverseStage.ToString() : "-")
                  .Append(" | prefab ").Append(F(p.prefabMass)).Append(" | mass ").Append(F(p.mass))
                  .Append(" | sucha staged ").Append(F(dryS)).Append(" unstaged ").Append(F(dryU))
                  .Append(" | paliwo ").Append(F(res)).Append(flag).AppendLine();
                if (mods.Length > 0) sb.Append(" moduły:").Append(mods).AppendLine();
                if (rs.Length > 0) sb.Append(" zasoby:").Append(rs).AppendLine();
                foreach (ModuleEngines en in p.FindModulesImplementing<ModuleEngines>())
                {
                    double i0 = en.atmosphereCurve != null ? en.atmosphereCurve.Evaluate(0f) : double.NaN;
                    sb.Append(" silnik ").Append(en.engineID).Append(": maxFuelFlow ").Append(F(en.maxFuelFlow)).Append(" minFuelFlow ").Append(F(en.minFuelFlow))
                      .Append(" maxThrust ").Append(F(en.maxThrust)).Append(" ISP(0) ").Append(F(i0))
                      .Append(" thrust% ").Append(F(en.thrustPercentage)).Append(" curveThrust ").Append(en.useThrustCurve);
                    if (en.atmosphereCurve != null)
                    {
                        sb.Append(" krzywa:");
                        foreach (Keyframe k in en.atmosphereCurve.Curve.keys)
                        {
                            sb.Append(" (").Append(F(k.time)).Append(",").Append(F(k.value)).Append(",").Append(F(k.inTangent)).Append(",").Append(F(k.outTangent)).Append(")");
                            if (float.IsNaN(k.inTangent) || float.IsInfinity(k.inTangent) || float.IsNaN(k.outTangent) || float.IsInfinity(k.outTangent)) bad++;
                        }
                    }
                    if (en.maxFuelFlow <= 0 || float.IsNaN(en.maxFuelFlow) || double.IsNaN(i0) || i0 <= 0) { sb.Append(" <<<ZŁY SILNIK"); bad++; }
                    sb.AppendLine();
                }
            }
            try { AppendOrbiterGeometry(v, sb); } catch (Exception e) { sb.AppendLine("# geometria orbitera: błąd " + e.Message); }
            sb.Insert(0, "# podejrzanych wartości: " + bad + Environment.NewLine);

            foreach (string dir in FlightLogger.LogFolders)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string safe = v.vesselName;
                    foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                    string path = Path.Combine(dir, "CZESCI_" + safe + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv) + ".txt");
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    Debug.Log("[RSSBalancer] Lista części: " + path + " (podejrzanych: " + bad + ")");
                    return;
                }
                catch { }
            }
        }

        /// <summary>
        /// Geometria Burana (części BURAN*/Buran*): długość z brył modeli wzdłuż osi rakiety, środek masy
        /// orbitera w % długości od nosa (prawdziwy Buran: 64–66 %), i które części mają moduły FAR skrzydeł/sterów.
        /// </summary>
        private static void AppendOrbiterGeometry(Vessel v, StringBuilder sb)
        {
            Vector3 ax = v.transform.up;
            double lo = double.MaxValue, hi = double.MinValue, m = 0, mx = 0;
            int n = 0;
            StringBuilder aero = new StringBuilder();
            foreach (Part p in v.parts)
            {
                string nm = p.partInfo != null ? p.partInfo.name : p.name;
                if (!nm.StartsWith("BURAN", StringComparison.OrdinalIgnoreCase)) continue;
                n++;
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    Bounds b = r.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        double d = Vector3.Dot(c, ax);
                        lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                    }
                }
                double pm = p.mass + p.GetResourceMass();
                Vector3 com = p.transform.position + p.transform.rotation * p.CoMOffset;
                m += pm; mx += pm * Vector3.Dot(com, ax);
                string mods = "";
                foreach (PartModule pmod in p.Modules)
                {
                    string mn = pmod.moduleName;
                    if (mn.StartsWith("FARWing") || mn.StartsWith("FARControllable") || mn.Contains("LiftingSurface") || mn.Contains("ControlSurface")) mods += " " + mn;
                }
                if (mods.Length > 0) aero.Append("#   ").Append(nm).Append(":").Append(mods).AppendLine();
            }
            if (n == 0 || m <= 0) return;
            double cm = mx / m, len = hi - lo;
            sb.AppendLine("# ORBITER (" + n + " części): masa " + F(m) + " t, długość z modeli " + F(len) + " m, środek masy " +
                          F((hi - cm) / len * 100) + " % długości od nosa (" + F(hi - cm) + " m od nosa) – prawdziwy Buran: 19,7–20,4 m od nosa przy długości całkowitej 36,4 m (54–56 %), w skali 0,93 to 18,4–18,9 m");
            sb.AppendLine("# aerodynamika części orbitera:");
            sb.Append(aero);
        }
    }
}
