// =====================================================================================
// UvpPdfToP8DeviceProbe — bir ürün kodundan EPLAN'ın hangi FONKSİYONLARI oluşturduğunu
// ve bunların tek tek sayfaya konup konamadığını ölçer.
//
//   UvpPdfToP8DeviceProbe /PART:"SIE.6ES7131-6BF01-0BA0" /OUT:"<klasör>" /TEMPLATE:"<.zw9>"
//
// Neden: PLC ve slim röle elle Cihaz Gezgini'nden konuyor — cihaz parçadan oluşur, kutu
// (L+ / M) ve her kanal ayrı fonksiyon olarak sırayla yerleştirilir. Biz ise 8 serbest
// sembol koyup adlarını 1 2 3 4 diye kendimiz yazıyorduk. Doğru yolu kurmadan önce parçanın
// gerçekten ne getirdiği ölçülür.
//
// AÇIK PROJEYE DOKUNMAZ: şablondan geçici proje açar, deneme bitince kapatır.
// Çıktı: <klasör>\cihaz_deneme.json
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class DeviceProbeAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8DeviceProbe";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static string Text(Func<string> read)
        {
            try { string value = read(); return value ?? ""; }
            catch (Exception ex) { return "!" + ex.GetType().Name; }
        }

        static double[] XY(PointD p) { return new double[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) }; }

        static Dictionary<string, object> Describe(Function function)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();
            row["type"] = function.GetType().Name;
            row["name"] = Text(delegate { return function.Name; });
            row["visible_name"] = Text(delegate { return function.VisibleName; });
            row["is_main"] = Text(delegate { return function.IsMainFunction.ToString(); });
            row["is_placed"] = Text(delegate { return function.IsPlaced.ToString(); });
            row["definition"] = Text(delegate { return function.FunctionDefinition.ToString(); });
            row["symbol"] = Text(delegate { return function.SymbolVariant == null ? ""
                                                   : function.SymbolVariant.SymbolName + " #" + function.SymbolVariant.VariantNr; });
            row["connection_designations"] = Text(delegate { return function.Properties.FUNC_ALLCONNECTIONDESIGNATIONS.ToString(); });
            row["plc_address"] = Text(delegate { return function.Properties.FUNC_PLCADDRESS.ToString(); });
            List<string> pins = new List<string>();
            try { foreach (Pin pin in function.Pins) pins.Add(pin.Name); }
            catch (Exception ex) { pins.Add("!" + ex.GetType().Name); }
            row["pins"] = pins;
            if (function.IsPlaced)
                row["location"] = XY(function.Location);
            return row;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string part = Io.Param(ctx, "PART");
            string outDir = Io.Param(ctx, "OUT");
            string template = Io.Param(ctx, "TEMPLATE");
            if (part == "") part = "SIE.6ES7131-6BF01-0BA0";
            if (outDir == "") outDir = Path.Combine(Path.GetTempPath(), "uvp_device_probe");
            Directory.CreateDirectory(outDir);

            List<string> log = new List<string>();
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.device-probe";
            report["contract_version"] = "1.0";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;
            report["part"] = part;

            Project scratch = null;
            try
            {
                if (template == "") throw new InvalidOperationException("/TEMPLATE gerekli (geçici proje).");
                string elk = Path.Combine(outDir, "UVP_CIHAZ_" + Io.Stamp() + ".elk");
                scratch = new ProjectManager().CreateProject(elk, template);
                PagePropertyList names = new PagePropertyList();
                names.DESIGNATION_PLANT = "UVPDEN";
                names.PAGE_COUNTER = "1";
                Page page = new Page();
                page.Create(scratch, DocumentTypeManager.DocumentType.Circuit, names);

                // 0) Parçanın kendisi: varyant ve FONKSİYON ŞABLONLARI. İlk denemede varyant boş
                //    verildi ve CreateDevice "S029017 cihaz oluşturulurken hata" döndü.
                string variant = "";
                try
                {
                    Eplan.EplApi.MasterData.MDPartsDatabase db =
                        new Eplan.EplApi.MasterData.MDPartsManagement().OpenDatabase();
                    Eplan.EplApi.MasterData.MDPart mdPart = db.GetPart(part);
                    if (mdPart == null)
                    {
                        report["part_found"] = false;
                    }
                    else
                    {
                        report["part_found"] = true;
                        variant = mdPart.Variant ?? "";
                        report["part_variant"] = variant;
                        List<object> templates = new List<object>();
                        Eplan.EplApi.MasterData.MDFunctionTemplatePosition[] positions = mdPart.FunctionTemplatePositions;
                        if (positions != null)
                            foreach (Eplan.EplApi.MasterData.MDFunctionTemplatePosition position in positions)
                                templates.Add(new Dictionary<string, object> {
                                    { "definition_id", position.FunctionDefinitionId },
                                    { "definition_group", position.FunctionDefinitionGroup },
                                    { "category", Text(delegate { return position.FunctionDefinitionCategory.ToString(); }) },
                                    { "template_group", Text(delegate { return position.TemplateGroup; }) },
                                    { "description", Text(delegate { return position.AdditionalDescription.GetStringToDisplay(ISOCode.Language.L_tr_TR); }) } });
                        report["function_templates"] = templates;
                        report["function_template_count"] = templates.Count;
                    }
                }
                catch (Exception ex) { report["part_error"] = ex.GetType().Name + ": " + ex.Message; }

                // 1) Cihazı PARÇADAN oluştur: Cihaz Gezgini'ndeki "yeni cihaz" ile aynı iş.
                //    Üç yol sırayla denenir; hangisinin çalıştığı kayda geçer.
                Function[] functions = null;
                List<object> attempts = new List<object>();
                DeviceService service = new DeviceService();
                for (int attempt = 0; attempt < 3 && (functions == null || functions.Length == 0); attempt++)
                {
                    Dictionary<string, object> tried = new Dictionary<string, object>();
                    try
                    {
                        if (attempt == 0)
                        {
                            tried["way"] = "CreateDevice(proje, kod, gerçek varyant, boş konum)";
                            functions = service.CreateDevice(scratch, part, variant, new FunctionPropertyList());
                        }
                        else if (attempt == 1)
                        {
                            tried["way"] = "CreateDevice(proje, kod, gerçek varyant, yapı dolu konum)";
                            FunctionPropertyList location = new FunctionPropertyList();
                            location.DESIGNATION_PLANT = "UVPDEN";
                            location.DESIGNATION_LOCATION = "E122";
                            functions = service.CreateDevice(scratch, part, variant, location);
                        }
                        else
                        {
                            tried["way"] = "CreateDevice(kod, gerçek varyant, sayfa, nokta)";
                            functions = service.CreateDevice(part, variant, page, new PointD(40.0, 250.0));
                        }
                        tried["result"] = functions == null ? "null" : functions.Length + " fonksiyon";
                    }
                    catch (Exception ex)
                    {
                        tried["error"] = ex.GetType().Name + ": " + ex.Message;
                        functions = null;
                    }
                    attempts.Add(tried);
                }
                report["create_attempts"] = attempts;
                List<object> created = new List<object>();
                if (functions != null)
                    foreach (Function function in functions) created.Add(Describe(function));
                report["created"] = created;
                report["created_count"] = functions == null ? 0 : functions.Length;
                log.Add("CreateDevice: " + (functions == null ? "yok" : functions.Length + " fonksiyon"));

                // 2) Ana fonksiyona AD ver ve ötekiler bu adı alıyor mu bak.
                Function main = null;
                if (functions != null)
                    foreach (Function function in functions)
                        if (function.IsMainFunction) { main = function; break; }
                if (main == null && functions != null && functions.Length > 0) main = functions[0];
                if (main != null)
                {
                    try
                    {
                        main.Name = "=UVPDEN+E122-27D22";
                        report["main_named"] = main.Name;
                    }
                    catch (Exception ex) { report["main_name_error"] = ex.GetType().Name + ": " + ex.Message; }
                }

                // 3) Her fonksiyonu AYRI noktaya yerleştir: Cihaz Gezgini'nden sürükleme.
                List<object> placedRows = new List<object>();
                double x = 40.0, y = 220.0;
                if (functions != null)
                    foreach (Function function in functions)
                    {
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        row["name_before"] = Text(delegate { return function.Name; });
                        try
                        {
                            function.PlaceAt(page, new PointD(x, y), DocumentTypeManager.DocumentType.Circuit,
                                             function.SymbolVariant);
                            row["placed_with"] = "PlaceAt";
                        }
                        catch (Exception first)
                        {
                            row["place_at_error"] = first.GetType().Name + ": " + first.Message;
                            try
                            {
                                function.Place(page, new PointD(x, y), false, false, false);
                                row["placed_with"] = "Place";
                            }
                            catch (Exception second)
                            {
                                row["place_error"] = second.GetType().Name + ": " + second.Message;
                            }
                        }
                        row["after"] = Describe(function);
                        placedRows.Add(row);
                        x += 30.0;
                        if (x > 380.0) { x = 40.0; y -= 40.0; }
                    }
                report["placed"] = placedRows;
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
                if (scratch != null) { try { scratch.Close(); log.Add("geçici proje kapatıldı."); } catch (Exception) { } }
            }
            report["log"] = log;
            Io.WriteJson(Path.Combine(outDir, "cihaz_deneme.json"), report);
            return true;
        }
    }
}
