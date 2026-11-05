// =====================================================================================
// UvpPdfToP8Symbols — sembol GEOMETRİSİNİ kütüphaneden doğrudan okur (PDF'e basmadan).
//
//   UvpPdfToP8Symbols /OUT:"<klasör>" [/TEMPLATE:"<.zw9>"] [/LIB:IEC_symbol,SPECIAL] [/MAX:0]
//
// Neden: sembolleri sayfaya basıp PDF'ten okumak dolambaçlı; büyük semboller hücreye
// sığmıyor, iç içe giriyor. `SymbolVariant.SubPlacements` sembolün kendi çizgi/yay/dikdörtgen
// nesnelerini verir — kaynak zaten budur.
//
// Açık proje varsa ONUN kütüphaneleri okunur (hiçbir şey yazılmaz); yoksa şablondan geçici
// proje açılır ve sonunda kapatılır.
//
// Çıktı: <klasör>\semboller.json
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.DataModel.MasterData;

namespace Uvp.PdfToP8.Host
{
    public class SymbolGeometryAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Symbols";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static double[] XY(PointD p) { return new double[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) }; }

        /// <summary>Bir çizim nesnesini ölçülebilir satıra çevir. Tanımadığı türü ÇEVİRMEZ,
        /// null döner; sayan taraf onu türüyle birlikte raporlar — sessiz kayıp olmaz.</summary>
        static Dictionary<string, object> Element(Placement item)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            Line line = item as Line;
            if (line != null)
            {
                row["kind"] = "line";
                row["a"] = XY(line.StartPoint);
                row["b"] = XY(line.EndPoint);
                row["width"] = Math.Round(line.Width, 3);
                return row;
            }
            PolyLine poly = item as PolyLine;
            if (poly != null)
            {
                List<object> points = new List<object>();
                for (int i = 0; i < poly.NumberOfPoints; i++) points.Add(XY(poly.GetPointAt(i)));
                row["kind"] = "polyline";
                row["points"] = points;
                row["closed"] = poly.Closed;
                return row;
            }
            Rectangle rect = item as Rectangle;
            if (rect != null)
            {
                row["kind"] = "rect";
                row["a"] = XY(rect.LowerLeftCorner);
                row["b"] = XY(rect.UpperRightCorner);
                row["angle"] = Math.Round(rect.Angle, 3);
                return row;
            }
            Arc arc = item as Arc;
            if (arc != null)
            {
                row["kind"] = "arc";
                row["center"] = XY(arc.Location);
                row["radius"] = Math.Round(arc.Radius, 3);
                row["radius2"] = Math.Round(arc.SecondRadius, 3);
                row["start_angle"] = Math.Round(arc.StartAngle, 3);
                row["end_angle"] = Math.Round(arc.EndAngle, 3);
                return row;
            }
            return null;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            string template = Io.Param(ctx, "TEMPLATE");
            if (outDir == "") outDir = Path.Combine(Path.GetTempPath(), "uvp_p8symbols");
            Directory.CreateDirectory(outDir);

            string libParam = Io.Param(ctx, "LIB");
            if (libParam == "") libParam = "IEC_symbol,SPECIAL";
            List<string> wantedLibs = new List<string>(libParam.Split(','));
            for (int i = 0; i < wantedLibs.Count; i++) wantedLibs[i] = wantedLibs[i].Trim();
            int max = 0;
            int.TryParse(Io.Param(ctx, "MAX"), NumberStyles.Integer, CultureInfo.InvariantCulture, out max);
            // Projeye kayıtlı kütüphane listesi eksik olabilir (açık projede yalnız SPECIAL çıktı).
            // /DIR verilirse oradaki her .slk doğrudan açılır — kaynak dosyanın kendisi.
            string dirParam = Io.Param(ctx, "DIR");

            List<string> log = new List<string>();
            List<object> symbols = new List<object>();
            List<object> problems = new List<object>();
            Dictionary<string, int> ignoredTypes = new Dictionary<string, int>();
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.symbol-geometry";
            report["contract_version"] = "1.0";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;
            report["source"] = "SymbolVariant.SubPlacements";

            Project temporary = null;
            try
            {
                ProjectManager pm = new ProjectManager();
                Project prj = null;
                foreach (Project open in pm.OpenProjects) { prj = open; break; }
                if (prj != null)
                {
                    report["read_from"] = "AÇIK PROJE (yalnız okuma): " + prj.ProjectLinkFilePath;
                }
                else
                {
                    if (template == "") throw new InvalidOperationException(
                        "Açık proje yok; kütüphaneleri okumak için /TEMPLATE gerekli.");
                    string elk = Path.Combine(outDir, "UVP_SEMBOL_" + Io.Stamp() + ".elk");
                    temporary = pm.CreateProject(elk, template);
                    prj = temporary;
                    report["read_from"] = "GEÇİCİ PROJE: " + elk;
                }

                int count = 0;
                bool allLibs = wantedLibs.Contains("*");
                List<string> seenLibs = new List<string>();

                // Okunacak kütüphaneler: projede kayıtlı olanlar + /DIR altındaki .slk dosyaları.
                List<SymbolLibrary> libraries = new List<SymbolLibrary>();
                foreach (SymbolLibrary lib in prj.SymbolLibraries)
                {
                    seenLibs.Add(lib.Name);
                    libraries.Add(lib);
                }
                foreach (string folder in dirParam.Split(';'))
                {
                    string trimmed = folder.Trim();
                    if (trimmed == "" || !Directory.Exists(trimmed)) continue;
                    foreach (string file in Directory.GetFiles(trimmed, "*.slk", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (seenLibs.Contains(name)) continue;
                        SymbolLibrary opened = new SymbolLibrary();
                        bool started = false;
                        try { started = opened.Initialize(name); }
                        catch (Exception ex)
                        {
                            problems.Add(new Dictionary<string, object> {
                                { "library", name }, { "reason", "kütüphane açılamadı: " + ex.Message } });
                            continue;
                        }
                        if (!started)
                        {
                            problems.Add(new Dictionary<string, object> {
                                { "library", name }, { "reason", "kütüphane açılamadı (Initialize false)" } });
                            continue;
                        }
                        seenLibs.Add(name);
                        libraries.Add(opened);
                    }
                }

                foreach (SymbolLibrary lib in libraries)
                {
                    if (!allLibs && !wantedLibs.Contains(lib.Name)) continue;
                    int libCount = 0;
                    foreach (Symbol symbol in lib.Symbols)
                    {
                        int variants = 0;
                        try { variants = symbol.Variants.Length; }
                        catch (Exception ex)
                        {
                            problems.Add(new Dictionary<string, object> {
                                { "library", lib.Name }, { "symbol", symbol.Name },
                                { "reason", "varyant listesi okunamadı: " + ex.Message } });
                            continue;
                        }
                        for (int nr = 0; nr < variants; nr++)
                        {
                            if (max > 0 && count >= max) break;
                            SymbolVariant sv = null;
                            try { sv = symbol[nr]; }
                            catch (Exception ex)
                            {
                                problems.Add(new Dictionary<string, object> {
                                    { "library", lib.Name }, { "symbol", symbol.Name }, { "variant", nr },
                                    { "reason", "varyant okunamadı: " + ex.Message } });
                                continue;
                            }
                            if (sv == null) continue;

                            List<object> elements = new List<object>();
                            Dictionary<string, int> ignoredHere = new Dictionary<string, int>();
                            string error = null;
                            try
                            {
                                Placement[] parts = sv.SubPlacements;
                                if (parts != null)
                                    foreach (Placement item in parts)
                                    {
                                        Dictionary<string, object> row = null;
                                        try { row = Element(item); }
                                        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                                        if (row != null) { elements.Add(row); continue; }
                                        string name = item.GetType().Name;
                                        ignoredHere[name] = (ignoredHere.ContainsKey(name) ? ignoredHere[name] : 0) + 1;
                                        ignoredTypes[name] = (ignoredTypes.ContainsKey(name) ? ignoredTypes[name] : 0) + 1;
                                    }
                            }
                            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

                            List<object> points = new List<object>();
                            try
                            {
                                PinBase[] connectionPoints = sv.ConnectionPoints;
                                if (connectionPoints != null)
                                    foreach (PinBase cp in connectionPoints)
                                        points.Add(new Dictionary<string, object> {
                                            { "index", cp.Index },
                                            { "direction", cp.Direction.ToString() },
                                            { "point", XY(cp.Location) } });
                            }
                            catch (Exception) { }

                            Dictionary<string, object> entry = new Dictionary<string, object>();
                            entry["library"] = lib.Name;
                            entry["symbol"] = symbol.Name;
                            entry["variant"] = nr;
                            entry["symbol_type"] = symbol.Type.ToString();
                            entry["elements"] = elements;
                            entry["connection_points"] = points;
                            if (ignoredHere.Count > 0) entry["ignored"] = ignoredHere;
                            if (error != null) entry["error"] = error;
                            symbols.Add(entry);
                            count++; libCount++;
                        }
                        if (max > 0 && count >= max) break;
                    }
                    log.Add("kütüphane " + lib.Name + ": " + libCount + " varyant okundu");
                    if (max > 0 && count >= max) break;
                }
                report["libraries_in_project"] = seenLibs;
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().FullName + ": " + ex.Message;
                report["stack"] = ex.StackTrace;
            }
            finally
            {
                if (temporary != null) { try { temporary.Close(); log.Add("geçici proje kapatıldı."); } catch (Exception) { } }
            }
            int withGeometry = 0;
            foreach (object o in symbols)
                if (((List<object>)((Dictionary<string, object>)o)["elements"]).Count > 0) withGeometry++;
            report["symbols"] = symbols;
            report["problems"] = problems;
            report["ignored_types"] = ignoredTypes;
            report["count"] = symbols.Count;
            report["with_geometry"] = withGeometry;
            report["log"] = log;
            Io.WriteJson(Path.Combine(outDir, "semboller.json"), report);
            return true;
        }
    }
}
