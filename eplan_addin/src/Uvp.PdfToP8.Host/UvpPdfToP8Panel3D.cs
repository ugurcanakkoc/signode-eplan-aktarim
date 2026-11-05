// =====================================================================================
// UvpPdfToP8Panel3D — OLCUM eylemi (04.10 Pro Panel / "24V'yi dagit" arastirmasi). Uretim kodu degil.
//
//   UvpPdfToP8Panel3D /PROJECT:"<output\p8runs\...\UVP_*.elk>" /OUT:"<yeni klasor>" [/BUILD:1] [/MACRODIR:"<ema klasoru>"]
//
// 1) Projedeki kurulum alanlari / 3B yerlesimler / Function3D'leri okur (ray, plaka, bilesen: tur, ad, ata,
//    mutlak konum, sinir kutusu).  2) /BUILD:1 ise test kopyasinda bir kurulum alani + 3B makrolar yerlestirir ve
//    ayni okumayi tekrarlar. 3) Sayfa sayaci "0" olan sayfa kurulabiliyor mu. 4) Terminal uzerinde "elle
//    kopru" ozelligi (20350/20351/20808) yazilip geri okunur.
// Matrix3D/Vector3D referansi (PresentationCore) gerekmesin diye bu dosyada 3B tur/uyeler yansima ile okunur;
// build.bat degismeden derler. Yalniz Guard.TestProject kopyasina yazar. Sonuc: <OUT>\panel3d.json
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.E3D;
using Eplan.EplApi.DataModel.EObjects;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class Panel3DAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Panel3D";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            try { return o.GetType().GetProperty(name).GetValue(o, null); }
            catch (Exception ex) { return "ERR:" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
        }

        static string Str(object o)
        {
            try { return o == null ? "" : Convert.ToString(o); } catch (Exception ex) { return "ERR:" + ex.Message; }
        }

        static string Chain(Type t)
        {
            List<string> names = new List<string>();
            for (; t != null && t != typeof(object); t = t.BaseType) names.Add(t.Name);
            return string.Join(">", names.ToArray());
        }

        static Dictionary<string, object> Describe(Placement3D p)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            row["type"] = p.GetType().Name;
            row["chain"] = Chain(p.GetType());
            row["id"] = Str(Get(p, "ObjectIdentifier"));
            row["name"] = Str(Get(p, "VisibleName"));
            row["is_placed"] = Str(Get(p, "IsPlaced"));
            try { Placement3D par = p.Parent; row["parent"] = par == null ? "" : par.GetType().Name + ":" + Str(Get(par, "VisibleName")); }
            catch (Exception ex) { row["parent"] = "ERR:" + ex.Message; }
            object m = Get(p, "AbsoluteTransformation");
            if (m != null && !(m is string))
                row["abs"] = new double[] { Dbl(Get(m, "OffsetX")), Dbl(Get(m, "OffsetY")), Dbl(Get(m, "OffsetZ")),
                                            Dbl(Get(m, "M11")), Dbl(Get(m, "M12")), Dbl(Get(m, "M21")), Dbl(Get(m, "M22")) };
            else row["abs"] = Str(m);
                        try
            {
                object b = p.GetType().GetMethod("GetBoundingBox", new Type[] { typeof(bool) }).Invoke(p, new object[] { true });
                row["bbox"] = Str(b);
                if (b != null)
                {
                    List<string> props = new List<string>();
                    foreach (PropertyInfo pi in b.GetType().GetProperties())
                        props.Add(pi.Name + "=" + Str(Get(b, pi.Name)));
                    row["bbox_props"] = props;
                }
            }
            catch (Exception) { }
            try
            {
                Function3D f3 = p as Function3D;
                if (f3 != null)
                {
                    row["f3_name"] = f3.VisibleName;
                    List<string> arts = new List<string>();
                    foreach (ArticleReference a in f3.ArticleReferences) arts.Add(a.PartNr + "|" + a.VariantNr);
                    row["articles"] = arts;
                }
            }
            catch (Exception ex) { row["f3_error"] = ex.Message; }
            return row;
        }

        static double Dbl(object o)
        {
            try { return Math.Round(Convert.ToDouble(o), 3); } catch (Exception) { return double.NaN; }
        }

        static List<object> Dump(Project prj)
        {
            List<object> rows = new List<object>();
            DMObjectsFinder finder = new DMObjectsFinder(prj);
            Placements3DFilter filter = new Placements3DFilter();
            try { filter.Recursive = true; } catch (Exception) { }
            Placement3D[] all = finder.GetPlacements3D(filter);
            if (all != null) foreach (Placement3D p in all) rows.Add(Describe(p));
            return rows;
        }

        static object Identity(Type m3, double x, double y, double z)
        {
            // Matrix3D(m11..m14, m21..m24, m31..m34, offsetX, offsetY, offsetZ, m44)
            return Activator.CreateInstance(m3, new object[] { 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, x, y, z, 1.0 });
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            string target = Path.Combine(outDir, "panel3d.json");
            if (File.Exists(target)) return false;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["contract"] = "uvp.pdf2p8.panel3d";
            r["utc"] = DateTime.UtcNow.ToString("o");
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                prj = pm.OpenProject(file);
                r["project"] = prj.ProjectLinkFilePath;
                string macroDir = Io.Param(ctx, "MACRODIR");
                if (macroDir == "") macroDir = @"C:\Users\Public\Eplan\Data\Makrolar\Company name\Weidmueller";
                using (LockingStep step = new LockingStep())
                {
                    // 1) mevcut 3B
                    try
                    {
                        List<object> spaces = new List<object>();
                        foreach (InstallationSpace s in prj.InstallationSpaces) spaces.Add(Str(Get(s, "VisibleName")));
                        r["spaces_before"] = spaces;
                    }
                    catch (Exception ex) { r["spaces_error"] = ex.Message; }
                    try { r["placements_before"] = Dump(prj); } catch (Exception ex) { r["placements_before_error"] = ex.ToString(); }
                    try
                    {
                        DMObjectsFinder finder = new DMObjectsFinder(prj);
                        Function3D[] f3 = finder.GetFunctions3D(new Functions3DFilter());
                        r["functions3d_before"] = f3 == null ? -1 : f3.Length;
                    }
                    catch (Exception ex) { r["functions3d_error"] = ex.Message; }

                    // 2) kurma (yalniz /BUILD:1)
                    if (Io.Param(ctx, "BUILD") == "1")
                    {
                        Dictionary<string, object> b = new Dictionary<string, object>();
                        r["build"] = b;
                        try
                        {
                            InstallationSpace space = InstallationSpace.Create(prj, "UVPPP", null);
                            b["space"] = Str(Get(space, "VisibleName"));
                            Type m3 = null;
                            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try { m3 = asm.GetType("System.Windows.Media.Media3D.Matrix3D"); } catch (Exception) { }
                                if (m3 != null) break;
                            }
                            if (m3 == null) { try { m3 = Assembly.Load("PresentationCore").GetType("System.Windows.Media.Media3D.Matrix3D"); } catch (Exception ex) { b["load_pc_error"] = ex.Message; } }
                            b["matrix_type"] = m3 == null ? "YOK" : m3.AssemblyQualifiedName;
                            Placement3D host = space;
                            try
                            {
                                MountingPanel panel = MountingPanel.Create(prj, 800.0, 600.0, 3.0, null);
                                b["panel_created"] = Chain(panel.GetType());
                                try { panel.Parent = space; } catch (Exception ex) { b["panel_parent_error"] = ex.Message; }
                                b["panel_is_placed"] = Str(Get(panel, "IsPlaced"));
                                host = panel;
                            }
                            catch (Exception ex) { b["panel_error"] = ex.GetType().Name + ": " + ex.Message; }
                            try
                            {
                                MountingRail rail = MountingRail.Create(prj, "", "", 500.0, null);
                                b["rail_created"] = Chain(rail.GetType());
                                try { rail.Parent = host; b["rail_parent_ok"] = true; } catch (Exception ex) { b["rail_parent_error"] = ex.Message; }
                            }
                            catch (Exception ex) { b["rail_error"] = ex.GetType().Name + ": " + ex.Message; }
                            Insert3D ins = new Insert3D();
                            MethodInfo wm = null;
                            foreach (MethodInfo mi in typeof(Insert3D).GetMethods())
                                if (mi.Name == "WindowMacro" && mi.GetParameters().Length == 6 && mi.GetParameters()[0].ParameterType == typeof(string))
                                    wm = mi;
                            b["windowmacro"] = wm == null ? "YOK" : "var";
                            double[] xs = new double[] { 0, 6.1, 12.2, 60, 66.1 };
                            List<object> placed = new List<object>();
                            string ema = Path.Combine(macroDir, "1521690000_3D.ema");
                            b["ema"] = ema + (File.Exists(ema) ? " (var)" : " (YOK)");
                            ParameterInfo[] pr = wm.GetParameters();
                            foreach (double x in xs)
                            {
                                try
                                {
                                    object res = wm.Invoke(ins, new object[] { ema, 0, host, Identity(m3, x, 0, 0),
                                        Enum.ToObject(pr[4].ParameterType, 0), Enum.ToObject(pr[5].ParameterType, 0) });
                                    IEnumerable en = res as IEnumerable;
                                    int n = 0; if (en != null) foreach (object o in en) n++;
                                    placed.Add(new Dictionary<string, object> { { "x", x }, { "objects", n } });
                                }
                                catch (Exception ex)
                                {
                                    Exception e = ex; while (e.InnerException != null) e = e.InnerException;
                                    placed.Add(new Dictionary<string, object> { { "x", x }, { "error", e.GetType().Name + ": " + e.Message } });
                                }
                            }
                            b["placed"] = placed;
                        }
                        catch (Exception ex) { b["error"] = ex.ToString(); }
                        try { r["placements_after"] = Dump(prj); } catch (Exception ex) { r["placements_after_error"] = ex.ToString(); }
                    }

                    // 3) sayfa sayaci "0"
                    try
                    {
                        PagePropertyList names = new PagePropertyList();
                        names.DESIGNATION_PLANT = "UVPDEN";
                        names.PAGE_COUNTER = "0";
                        Page page = new Page();
                        page.Create(prj, DocumentTypeManager.DocumentType.Circuit, names);
                        r["page0_name"] = page.Name;
                        r["page0_counter"] = Str(page.Properties.PAGE_COUNTER.ToString());
                    }
                    catch (Exception ex) { r["page0_error"] = ex.GetType().Name + ": " + ex.Message; }

                    // 4) elle kopru ozellikleri
                    try
                    {
                        DMObjectsFinder finder = new DMObjectsFinder(prj);
                        Terminal[] terms = finder.GetTerminals(new FunctionsFilter());
                        List<object> rows = new List<object>();
                        int n = 0;
                        if (terms != null)
                            foreach (Terminal t in terms)
                            {
                                if (n++ >= 3) break;
                                Dictionary<string, object> row = new Dictionary<string, object>();
                                row["name"] = t.VisibleName;
                                try { row["jumper_intern_before"] = Str(t.Properties[20350, 1].ToString()); } catch (Exception ex) { row["intern_read_error"] = ex.Message; }
                                try { t.Properties[20350, 1] = "1/0"; row["jumper_intern_after"] = Str(t.Properties[20350, 1].ToString()); } catch (Exception ex) { row["intern_write_error"] = ex.Message; }
                                try { t.Properties[20808] = 1; row["jumperbar_after"] = Str(t.Properties[20808].ToString()); } catch (Exception ex) { row["jumperbar_error"] = ex.Message; }
                                rows.Add(row);
                            }
                        r["terminal_count"] = terms == null ? -1 : terms.Length;
                        r["jumper_trials"] = rows;
                    }
                    catch (Exception ex) { r["jumper_error"] = ex.GetType().Name + ": " + ex.Message; }
                }
                r["ok"] = true;
            }
            catch (Exception ex)
            {
                r["ok"] = false;
                r["error"] = ex.ToString();
            }
            finally
            {
                if (prj != null) { try { prj.Close(); } catch (Exception) { } }
            }
            Io.WriteJson(target, r);
            return true;
        }
    }
}
