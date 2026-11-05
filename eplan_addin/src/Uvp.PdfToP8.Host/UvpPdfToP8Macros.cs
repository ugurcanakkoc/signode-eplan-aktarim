// =====================================================================================
// UvpPdfToP8Macros — ürün kodlarının MAKROLARINI ve makro UÇLARINI dosyaya yazar.
//
//   UvpPdfToP8Macros /PACKAGE:"<import-request.json>" /OUT:"<klasör>" [/TEMPLATE:"<.zw9>"]
//
// Neden: müşteri şemasında uç adı `1 2 3`, makroda `L1 L2 L3` olabiliyor. Eşlemeyi
// yapabilmek için önce makronun GERÇEK uçlarını bilmek gerekir — tahminle değil, makroyu
// açıp okuyarak.
//
// Nasıl: paketteki her ürün kodu parça veritabanında aranır; parçanın şema makrosu geçici
// bir projede boş sayfaya konur, fonksiyonların uçları okunur, sonra proje kapatılır.
// AÇIK PROJEYE DOKUNULMAZ.
//
// Çıktı: <klasör>\makro_pinleri.json
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Filters;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    public class MacroDumpAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Macros";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static Dictionary<string, object> D(object o) { return (Dictionary<string, object>)o; }
        static ArrayList A(object o) { return o == null ? new ArrayList() : (ArrayList)o; }
        static string S(Dictionary<string, object> d, string k)
        {
            return d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : "";
        }

        /// <summary>Paketteki ürün kodları: kod → onu kullanan cihaz etiketleri.</summary>
        static Dictionary<string, List<object>> PartNumbers(Dictionary<string, object> package)
        {
            Dictionary<string, List<object>> out_ = new Dictionary<string, List<object>>();
            foreach (object po in A(package["pages"]))
            {
                Dictionary<string, object> page = D(po);
                foreach (object oo in A(page["objects"]))
                {
                    Dictionary<string, object> o = D(oo);
                    if (S(o, "kind") != "DEVICE") continue;
                    // Kesin kod VE belgedeki adaylar (28.09): sembolsüz cihazın ana ürünü adaylar arasından
                    // parça veritabanı bilgisiyle (ürün grubu, makro uçları) seçilir; önce hepsi ölçülür.
                    List<string> codes = new List<string>();
                    if (S(o, "part_number") != "") codes.Add(S(o, "part_number"));
                    if (o.ContainsKey("part_candidates"))
                        foreach (object co in A(o["part_candidates"]))
                        {
                            string code = S(D(co), "type_number");
                            if (code != "" && !codes.Contains(code)) codes.Add(code);
                        }
                    List<string> pins = new List<string>();
                    foreach (object pr in A(o["pins"])) pins.Add(S(D(pr), "name"));
                    foreach (string part in codes)
                    {
                        if (!out_.ContainsKey(part)) out_[part] = new List<object>();
                        out_[part].Add(new Dictionary<string, object> {
                            { "device_tag", S(o, "device_tag") }, { "family", S(o, "family") },
                            { "source_pins", pins.ToArray() },
                            { "role", part == S(o, "part_number") ? "part_number" : "candidate" } });
                    }
                }
            }
            return out_;
        }

        /// <summary>Kodun parça DB'de denenecek yazımları. Müşteri katalog yazımı (Rittal 'SZ2500.210') DB'de
        /// tip numarası '2500210' olarak durur (ölçüm 12.09: RIT.2500210 type_nr 2500210). Harf öneki ve
        /// ayırıcılar atılmış hâli yalnız TİP NUMARASI olarak aranır; eşleşen kural kayda yazılır.</summary>
        static List<string[]> Spellings(string number)
        {
            List<string[]> out_ = new List<string[]>();
            out_.Add(new string[] { "AS_IS", number });
            string compact = number.Replace(" ", "");
            if (compact != number) out_.Add(new string[] { "NO_SPACE", compact });
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(compact, @"^[A-Za-z]{1,3}(\d[\d.\-]*\d)$");
            if (m.Success) out_.Add(new string[] { "DIGITS_ONLY", m.Groups[1].Value.Replace(".", "").Replace("-", "") });
            return out_;
        }

        // Import.WriteArticle de kullanır (artikel EPLAN'a): aynı yazım kuralları, tek parça dışında sonuç yok.
        internal static MDPart FindPart(MDPartsDatabase db, string number, List<string> log, out string rule)
        {
            rule = "";
            if (db == null || string.IsNullOrEmpty(number)) return null;
            foreach (string[] spelling in Spellings(number))
            {
                if (spelling[0] != "DIGITS_ONLY")
                    try
                    {
                        MDPart direct = db.GetPart(spelling[1]);
                        if (direct != null) { rule = "PART_NR:" + spelling[0]; return direct; }
                    }
                    catch (Exception) { }
                try
                {
                    MDPartsDatabaseItemPropertyList filter = new MDPartsDatabaseItemPropertyList();
                    filter.ARTICLE_TYPENR = spelling[1];
                    MDPart[] hits = db.GetParts(filter);
                    if (hits != null && hits.Length == 1) { rule = "TYPE_NR:" + spelling[0]; return hits[0]; }
                    if (hits != null && hits.Length > 1)
                        log.Add("parça arama (" + number + " → " + spelling[1] + "): " + hits.Length + " parça, seçilmedi");
                }
                catch (Exception ex) { log.Add("parça arama (" + number + "): " + ex.Message); }
            }
            // 12.10: Stückliste kodu üreticinin sipariş numarasıdır (Hersteller-Teilenummer; Phoenix 2904620, Pilz 774340);
            // parça no ve tip no tutmazsa sipariş no alanında TEK parça aranır.
            try
            {
                MDPartsDatabaseItemPropertyList filter = new MDPartsDatabaseItemPropertyList();
                filter.ARTICLE_ORDERNR = number;
                MDPart[] hits = db.GetParts(filter);
                if (hits != null && hits.Length == 1) { rule = "ORDER_NR:AS_IS"; return hits[0]; }
                if (hits != null && hits.Length > 1)
                    log.Add("parça arama (" + number + " sipariş no): " + hits.Length + " parça, seçilmedi");
            }
            catch (Exception ex) { log.Add("parça arama (" + number + " sipariş no): " + ex.Message); }
            return null;
        }

        static string Text(Func<string> read)
        {
            try { string v = read(); return v ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Parçadan kurulan cihazın fonksiyonları (yerleştirilmez; geçici projede kalır).</summary>
        static List<object> DeviceFunctions(Project scratch, MDPart part)
        {
            List<object> out_ = new List<object>();
            try
            {
                Function[] made = new DeviceService().CreateDevice(scratch, part.PartNr, part.Variant ?? "", new FunctionPropertyList());
                foreach (Function fn in made ?? new Function[0])
                {
                    List<string> pins = new List<string>();
                    try { foreach (Pin pin in fn.Pins) pins.Add(pin.Name); } catch (Exception) { }
                    out_.Add(new Dictionary<string, object> {
                        { "type", fn.GetType().Name }, { "main", Text(delegate { return fn.IsMainFunction.ToString(); }) },
                        { "definition", Text(delegate { return fn.FunctionDefinition.ToString(); }) }, { "pins", pins } });
                }
            }
            catch (Exception ex) { out_.Add(new Dictionary<string, object> { { "error", ex.GetType().Name + ": " + ex.Message } }); }
            return out_;
        }

        /// <summary>Makronun iç köprüleri: sayfanın bağlantıları üretilir; bağlantı (uç↔uç) ve cihaz bağlantı noktasının
        /// (DeviceEndTerminal) kendi uçları birleşir, öbür fonksiyonların (dönüştürücü, sigorta…) uçları birleşmez.
        /// Aynı bileşene düşen ADLI bağlantı noktaları bir köprü grubudur. row: bridges, internal_links.</summary>
        static void Bridges(Page page, List<Function> functions, Dictionary<string, object> row)
        {
            new Generate().Connections(new Page[] { page }, true);
            Dictionary<string, string> parent = new Dictionary<string, string>();
            Func<string, string> find = null;
            find = delegate (string k)
            {
                if (!parent.ContainsKey(k)) parent[k] = k;
                if (parent[k] != k) parent[k] = find(parent[k]);
                return parent[k];
            };
            Action<string, string> join = delegate (string a, string b) { parent[find(a)] = find(b); };
            HashSet<string> mine = new HashSet<string>();
            Dictionary<string, string> named = new Dictionary<string, string>();   // fonksiyon -> bağlantı noktası adı
            foreach (Function f in functions)
            {
                string id = ImportAction.Id(f);
                mine.Add(id);
                if (Text(delegate { return Convert.ToString(ImportAction.Definition(f)["category"]); }) != "DeviceEndTerminal") continue;
                string name = ImportAction.SinglePinName(f);
                if (name != "") named[id] = name;
                Pin[] pins = f.Pins;
                for (int i = 1; i < pins.Length; i++) join(id + "#" + pins[0].Index, id + "#" + pins[i].Index);
            }
            List<object> seen = new List<object>();
            ConnectionsFilter filter = new ConnectionsFilter();
#pragma warning disable 618
            filter.Page = page;
#pragma warning restore 618
            foreach (Connection k in new DMObjectsFinder(page.Project).GetConnections(filter))
            {
                Pin a = k.StartPin, b = k.EndPin;
                if (a == null || b == null || a.ParentFunction == null || b.ParentFunction == null) continue;
                string ia = ImportAction.Id(a.ParentFunction), ib = ImportAction.Id(b.ParentFunction);
                if (!mine.Contains(ia) || !mine.Contains(ib)) continue;
                join(ia + "#" + a.Index, ib + "#" + b.Index);
                seen.Add(Text(delegate { return a.ParentFunction.Name; }) + "[" + a.Index + "] " + a.Name + " <-> "
                         + Text(delegate { return b.ParentFunction.Name; }) + "[" + b.Index + "] " + b.Name);
            }
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
            foreach (Function f in functions)
            {
                string id = ImportAction.Id(f);
                if (!named.ContainsKey(id) || f.Pins.Length == 0) continue;
                string root = find(id + "#" + f.Pins[0].Index);
                if (!groups.ContainsKey(root)) groups[root] = new List<string>();
                if (!groups[root].Contains(named[id])) groups[root].Add(named[id]);
            }
            List<object> bridges = new List<object>();
            foreach (List<string> g in groups.Values)
                if (g.Count > 1) { g.Sort(StringComparer.Ordinal); bridges.Add(g); }
            row["bridges"] = bridges;
            row["internal_links"] = seen;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string packagePath = Io.Param(ctx, "PACKAGE");
            string outDir = Io.Param(ctx, "OUT");
            string template = Io.Param(ctx, "TEMPLATE");
            // /ONE_PER_PAGE:1 (02.10, makro görüntüsü): her makro kendi sayfasına konur ve SAYFADA KALIR; geçici proje
            // ardından UvpPdfToP8Snapshot ile PNG'ye verilir. Satıra sayfa adı ve konan nesnelerin kutusu yazılır.
            bool onePerPage = Io.Param(ctx, "ONE_PER_PAGE") == "1";
            // /LINKS:1 (03.10 akşam, makroyla aktarım): makronun KENDİ telleri hangi dış uçları birleştiriyor (iç köprü).
            // Aktarım bu bilgiyle makronun müşterinin ayrı ağlarını birleştirip birleştirmeyeceğine karar verir.
            bool links = onePerPage && Io.Param(ctx, "LINKS") == "1";
            if (outDir == "") outDir = Path.Combine(Path.GetTempPath(), "uvp_p8macros");
            Directory.CreateDirectory(outDir);

            List<string> log = new List<string>();
            List<object> rows = new List<object>();
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.macro-pins";
            report["contract_version"] = "1.0";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;

            Project scratch = null;
            try
            {
                if (packagePath == "" || !File.Exists(packagePath))
                    throw new InvalidOperationException("Paket dosyası yok: " + packagePath);
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                // DeserializeObject dizileri object[] verir; Deserialize<T> ArrayList verir.
                // Import ile aynı yol kullanılır ki A() yardımcısı ikisinde de çalışsın.
                Dictionary<string, object> package =
                    js.Deserialize<Dictionary<string, object>>(File.ReadAllText(packagePath));
                Dictionary<string, List<object>> parts = PartNumbers(package);
                report["part_numbers"] = parts.Count;
                log.Add("pakette " + parts.Count + " farklı ürün kodu");

                MDPartsDatabase db = null;
                try { db = new MDPartsManagement().OpenDatabase(); }
                catch (Exception ex) { log.Add("Parça veritabanı açılamadı: " + ex.Message); }

                // Makro ancak bir sayfaya konunca uçlarını gösterir: geçici proje, sonra kapatılır.
                ProjectManager pm = new ProjectManager();
                if (template == "") throw new InvalidOperationException("/TEMPLATE gerekli (geçici proje).");
                string elk = Path.Combine(outDir, "UVP_MAKRO_" + Io.Stamp() + ".elk");
                scratch = pm.CreateProject(elk, template);
                PagePropertyList names = new PagePropertyList();
                names.DESIGNATION_PLANT = "UVPMAK";
                names.PAGE_COUNTER = "1";
                Page page = new Page();
                page.Create(scratch, DocumentTypeManager.DocumentType.Circuit, names);

                double x = 20.0, y = 250.0;
                int pageNumber = 1;
                bool pageUsed = false;
                foreach (KeyValuePair<string, List<object>> entry in parts)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["part_number"] = entry.Key;
                    row["used_by"] = entry.Value;
                    string rule;
                    MDPart part = FindPart(db, entry.Key, log, out rule);
                    if (part == null)
                    {
                        row["found"] = false;
                        row["reason"] = "parça veritabanında bulunamadı";
                        rows.Add(row);
                        continue;
                    }
                    row["found"] = true;
                    row["match_rule"] = rule;
                    row["part_nr"] = Text(delegate { return part.PartNr; });
                    // Ana ürün / aksesuar ayrımı için ürün grubu ve üretici (28.09 kullanıcı sorusu).
                    row["manufacturer"] = Text(delegate { return part.Properties.ARTICLE_MANUFACTURER.ToString(); });
                    row["product_top_group"] = Text(delegate { return part.Properties.ARTICLE_PRODUCTTOPGROUP.ToString(); });
                    row["product_group"] = Text(delegate { return part.Properties.ARTICLE_PRODUCTGROUP.ToString(); });
                    row["product_sub_group"] = Text(delegate { return part.Properties.ARTICLE_PRODUCTSUBGROUP.ToString(); });
                    row["type_nr"] = Text(delegate { return part.Properties.ARTICLE_TYPENR.ToString(); });
                    row["description"] = Text(delegate { return part.Properties.ARTICLE_DESCR1.ToString(); });
                    // Parçada birden çok makro alanı var. ŞEMA makrosu ayrıdır:
                    // ARTICLE_MACRO ölçümde 3D makrosunu veriyordu (13/13 '*_3D.ema').
                    string schematic = Text(delegate { return part.Properties.ARTICLE_GROUPSYMBOLMACRO_IEC.ToString(); });
                    string generic = Text(delegate { return part.Properties.ARTICLE_GROUPSYMBOLMACRO.ToString(); });
                    string any = Text(delegate { return part.Properties.ARTICLE_MACRO.ToString(); });
                    row["macro_iec"] = schematic;
                    row["macro_group"] = generic;
                    row["macro_any"] = any;
                    row["macro_3d"] = Text(delegate { return part.Properties.ARTICLE_3DMACRO.ToString(); });
                    row["macro_name"] = Text(delegate { return part.Properties.ARTICLE_MACRONAME.ToString(); });
                    // Parçanın kendi SEMBOLÜ de kayıtlı: aile→sembol kararını bu çözebilir.
                    row["symbol_file"] = Text(delegate { return part.Properties.ARTICLE_SYMBOLFILE.ToString(); });
                    row["symbol_number"] = Text(delegate { return part.Properties.ARTICLE_SYMBOLNUMBER.ToString(); });
                    // Aktarım cihazı makrodan değil PARÇADAN kurar (CreateDevice, Import.PlaceFromPart); uç eşleşmesine
                    // bakan uç adları onunkilerdir. PLC'de ikisi farklı (29.09: makro L+/M, CreateDevice 1–16 + L+/M).
                    if (onePerPage) row["device_functions"] = DeviceFunctions(scratch, part);

                    string macro = schematic != "" ? schematic : (generic != "" ? generic : any);
                    row["macro"] = macro;
                    row["macro_source"] = schematic != "" ? "ARTICLE_GROUPSYMBOLMACRO_IEC"
                        : (generic != "" ? "ARTICLE_GROUPSYMBOLMACRO" : "ARTICLE_MACRO");
                    if (macro == "")
                    {
                        row["reason"] = "parçanın şema makrosu yok";
                        rows.Add(row);
                        continue;
                    }

                    // Makro birden çok GÖSTERİM TÜRÜ taşır (şema / montaj / tek hat…).
                    // Şema sayfasına MultiLine konur; varsayılan montaj görünümü olabiliyor.
                    List<string> available = new List<string>();
                    bool hasMultiLine = false;
                    try
                    {
                        Eplan.EplApi.DataModel.MasterData.WindowMacro probe =
                            new Eplan.EplApi.DataModel.MasterData.WindowMacro();
                        probe.Open(macro, scratch);
                        foreach (Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType type
                                 in probe.RepresentationTypes)
                        {
                            available.Add(type.ToString());
                            if (type == Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType.MultiLine)
                                hasMultiLine = true;
                        }
                    }
                    catch (Exception ex) { row["representation_error"] = ex.Message; }
                    row["representation_types"] = available;
                    row["has_multiline"] = hasMultiLine;

                    List<object> functions = new List<object>();
                    StorableObject[] placed = null;
                    // Her makro AYRI noktaya konur: aynı noktaya ikinci kez koymak
                    // "aynı semboller üst üste yerleştirilir" hatası veriyordu (12/13).
                    x += 60.0;
                    if (x > 380.0) { x = 20.0; y -= 60.0; }
                    if (onePerPage)
                    {
                        // Makrolar çoğunlukla ekleme noktasının sağına/altına uzanır (ölçüm 28.09: 6EP1961 +120/−85 mm).
                        if (pageUsed)
                        {
                            names.PAGE_COUNTER = Convert.ToString(++pageNumber);
                            page = new Page();
                            page.Create(scratch, DocumentTypeManager.DocumentType.Circuit, names);
                        }
                        x = 100.0; y = 230.0;
                        pageUsed = true;
                        row["page"] = page.Name;
                    }
                    else if (y < 20.0)
                    {
                        names.PAGE_COUNTER = Convert.ToString(++pageNumber);
                        page = new Page();
                        page.Create(scratch, DocumentTypeManager.DocumentType.Circuit, names);
                        x = 20.0; y = 250.0;
                    }
                    row["at"] = new double[] { x, y };
                    try
                    {
                        placed = hasMultiLine
                            ? new Insert().WindowMacro(macro,
                                  Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType.MultiLine,
                                  0, page, new PointD(x, y), Insert.MoveKind.Absolute)
                            : new Insert().WindowMacro(macro, 0, page, new PointD(x, y),
                                                       Insert.MoveKind.Absolute);
                        row["placed_representation"] = hasMultiLine ? "MultiLine" : "Default";
                        // Konan nesne türleri kayda geçer: uç bulunamazsa nerede olduğu görünsün.
                        Dictionary<string, int> kinds = new Dictionary<string, int>();
                        foreach (StorableObject item in placed)
                        {
                            string kind = item.GetType().Name;
                            kinds[kind] = (kinds.ContainsKey(kind) ? kinds[kind] : 0) + 1;
                        }
                        row["placed_types"] = kinds;

                        // Üst düzey nesneler yetmiyor: makronun İÇİNDEKİ fonksiyonlar da
                        // sayfada durur. Sayfadaki bütün fonksiyonlar taranır ve yalnız bu
                        // makronun konduğu bölgedekiler alınır.
                        List<Function> found = new List<Function>();
                        foreach (StorableObject item in placed)
                        {
                            Function direct = item as Function;
                            if (direct != null) found.Add(direct);
                        }
                        try
                        {
                            DMObjectsFinder finder = new DMObjectsFinder(scratch);
                            FunctionsFilter filter = new FunctionsFilter();
                            filter.SetFilteredPropertyList(new FunctionPropertyList());
                            foreach (Function candidate in finder.GetFunctions(filter))
                            {
                                if (candidate.Page == null || !candidate.Page.Equals(page)) continue;
                                if (!onePerPage && (Math.Abs(candidate.Location.X - x) > 55.0
                                    || Math.Abs(candidate.Location.Y - y) > 55.0)) continue;
                                if (!found.Contains(candidate)) found.Add(candidate);
                            }
                        }
                        catch (Exception ex) { row["scan_error"] = ex.Message; }

                        foreach (Function function in found)
                        {
                            if (function == null) continue;
                            List<object> pins = new List<object>();
                            foreach (Pin pin in function.Pins)
                                pins.Add(new Dictionary<string, object> {
                                    { "name", pin.Name },
                                    { "index", pin.Index },
                                    { "direction", Text(delegate { return pin.Direction.ToString(); }) },
                                    { "offset", new double[] { Math.Round(pin.Location.X - function.Location.X, 3),
                                                               Math.Round(pin.Location.Y - function.Location.Y, 3) } },
                                    // Makro ekleme noktasına göre (yerleşim bu ofsetle teli uca getirir).
                                    { "at_offset", new double[] { Math.Round(pin.Location.X - x, 3),
                                                                  Math.Round(pin.Location.Y - y, 3) } },
                                    { "location", new double[] { Math.Round(pin.Location.X, 3), Math.Round(pin.Location.Y, 3) } } });
                            functions.Add(new Dictionary<string, object> {
                                { "type", function.GetType().Name },
                                { "name", Text(delegate { return function.Name; }) },
                                { "visible_name", Text(delegate { return function.VisibleName; }) },
                                { "symbol", Text(delegate { return function.SymbolVariant == null ? ""
                                                            : function.SymbolVariant.SymbolName; }) },
                                { "pins", pins } });
                        }
                        row["objects"] = placed.Length;
                        if (links)
                        {
                            try { Bridges(page, found, row); }
                            catch (Exception ex) { row["bridges_error"] = ex.GetType().Name + ": " + ex.Message; }
                        }
                        if (onePerPage)
                        {
                            // Görüntü kırpması için konan nesnelerin kutusu (ekleme noktasına göre mm, y yukarı).
                            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                            foreach (StorableObject item in placed)
                            {
                                Placement placement = item as Placement;
                                if (placement == null) continue;
                                try
                                {
                                    PointD[] box = placement.GetBoundingBox();
                                    if (box == null || box.Length < 2) continue;
                                    foreach (PointD p in box)
                                    {
                                        x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                                        x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
                                    }
                                }
                                catch (Exception) { }
                            }
                            if (x0 <= x1)
                                row["placed_bbox_mm"] = new double[] { Math.Round(x0 - x, 3), Math.Round(y0 - y, 3),
                                                                       Math.Round(x1 - x, 3), Math.Round(y1 - y, 3) };
                        }
                    }
                    catch (Exception ex)
                    {
                        row["reason"] = "makro açılamadı: " + ex.GetType().Name + ": " + ex.Message;
                    }
                    finally
                    {
                        if (placed != null && !onePerPage)
                            foreach (StorableObject item in placed)
                            {
                                Placement placement = item as Placement;
                                if (placement == null) continue;
                                try { placement.Remove(); } catch (Exception) { }
                            }
                    }
                    row["functions"] = functions;
                    rows.Add(row);
                }
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
            report["parts"] = rows;
            report["log"] = log;
            Io.WriteJson(Path.Combine(outDir, "makro_pinleri.json"), report);
            return true;
        }
    }
}
