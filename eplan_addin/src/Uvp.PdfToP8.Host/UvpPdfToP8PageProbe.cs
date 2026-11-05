// =====================================================================================
// UvpPdfToP8PageProbe — açık sayfadaki HER nesneyi, sayfa çerçevesini (Normblatt) ve
// projenin katman tablosunu döker.
//
//   UvpPdfToP8PageProbe /OUT:"<klasör>"
//
// Neden: UVP şablonunda 'U' ile görünen taslak yerleri (Sigorta/MKŞ, PLC IO, Klemens ...)
// cihazları sayfaya yaymak için kullanılacak. Bu yerlerin sayfada mı, çerçevede mi, hangi
// katmanda durduğu bilinmeden okunamaz. PROJEYİ DEĞİŞTİRMEZ, yalnız okur.
// Çıktı: <klasör>\sayfa_dokum.json
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class PageProbeAction : IEplAction
    {
        const int Limit = 3000;

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8PageProbe";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static string Safe(Func<string> read)
        {
            try { string value = read(); return value ?? ""; }
            catch (Exception ex) { return "!" + ex.GetType().Name; }
        }

        static double[] XY(PointD p) { return new double[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }; }

        static Dictionary<string, object> Describe(Placement placement)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            row["type"] = placement.GetType().Name;
            try { row["at"] = XY(placement.Location); } catch (Exception) { }
            try
            {
                PointD[] box = placement.GetBoundingBox();
                if (box != null && box.Length >= 2) row["box"] = new object[] { XY(box[0]), XY(box[1]) };
            }
            catch (Exception) { }
            GraphicalPlacement graphic = placement as GraphicalPlacement;
            if (graphic != null)
            {
                row["layer"] = Safe(() => graphic.Layer == null ? "" : graphic.Layer.Name);
                row["visible"] = Safe(() => graphic.IsVisible.ToString());
            }
            Line line = placement as Line;
            if (line != null)
            {
                try { row["from"] = XY(line.StartPoint); row["to"] = XY(line.EndPoint); } catch (Exception) { }
            }
            Eplan.EplApi.DataModel.Graphics.Text text = placement as Eplan.EplApi.DataModel.Graphics.Text;
            if (text != null)
                row["text"] = Safe(() => text.Contents.GetAsString());
            PlaceHolder holder = placement as PlaceHolder;
            if (holder != null)
            {
                row["name"] = Safe(() => holder.Name);
                row["records"] = Safe(() => holder.NumberOfRecords.ToString());
            }
            Function function = placement as Function;
            if (function != null)
            {
                row["name"] = Safe(() => function.Name);
                row["symbol"] = Safe(() => function.SymbolVariant == null ? ""
                                            : function.SymbolVariant.SymbolName + " #" + function.SymbolVariant.VariantNr);
            }
            return row;
        }

        static List<object> DescribeAll(Placement[] placements, Dictionary<string, int> types)
        {
            List<object> rows = new List<object>();
            if (placements == null) return rows;
            foreach (Placement placement in placements)
            {
                string type = placement.GetType().Name;
                types[type] = types.ContainsKey(type) ? types[type] + 1 : 1;
                if (rows.Count < Limit) rows.Add(Describe(placement));
            }
            return rows;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            if (outDir == "") outDir = Path.Combine(Path.GetTempPath(), "uvp_page_probe");
            Directory.CreateDirectory(outDir);

            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.page-probe";
            report["contract_version"] = "1.0";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;
            try
            {
                SelectionSet selection = new SelectionSet();
                Project project = selection.GetCurrentProject(true);
                if (project == null) throw new InvalidOperationException("Açık proje yok.");
                report["project"] = Safe(() => project.ProjectLinkFilePath);

                // Grafik düzenleyicide açık sayfa önce; yoksa sayfa gezgininde seçili olan.
                Page page = selection.CurrentlyEdited as Page;
                string how = "düzenlenen sayfa";
                if (page == null)
                {
                    Page[] selected = selection.GetSelectedPages();
                    if (selected != null && selected.Length > 0) { page = selected[0]; how = "seçili sayfa"; }
                }
                if (page == null) throw new InvalidOperationException("Sayfa yok: şablon sayfasını açıp tekrar çalıştır.");
                report["page"] = Safe(() => page.Name);
                report["page_chosen_by"] = how;
                report["page_type"] = Safe(() => page.PageType.ToString());

                Dictionary<string, int> pageTypes = new Dictionary<string, int>();
                report["page_placements"] = DescribeAll(page.AllPlacements, pageTypes);
                report["page_types"] = pageTypes;

                try
                {
                    Eplan.EplApi.DataModel.MasterData.PlotFrame frame = page.PlotFrame;
                    if (frame != null)
                    {
                        report["plot_frame"] = Safe(() => frame.Name);
                        Dictionary<string, int> frameTypes = new Dictionary<string, int>();
                        report["plot_frame_placements"] = DescribeAll(frame.SubPlacements, frameTypes);
                        report["plot_frame_types"] = frameTypes;
                    }
                }
                catch (Exception ex) { report["plot_frame_error"] = ex.GetType().Name + ": " + ex.Message; }

                try
                {
                    List<object> layers = new List<object>();
                    foreach (GraphicalLayer layer in project.LayerTable.Layers)
                        layers.Add(new Dictionary<string, object> {
                            { "name", Safe(() => layer.Name) },
                            { "description", Safe(() => layer.Description.GetAsString()) },
                            { "visible", Safe(() => layer.isVisible.ToString()) },
                            { "printed", Safe(() => layer.isPrinted.ToString()) } });
                    report["layers"] = layers;
                }
                catch (Exception ex) { report["layers_error"] = ex.GetType().Name + ": " + ex.Message; }
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().FullName + ": " + ex.Message;
            }
            Io.WriteJson(Path.Combine(outDir, "sayfa_dokum.json"), report);
            return true;
        }
    }
}
