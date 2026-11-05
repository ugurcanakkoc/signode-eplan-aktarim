// =====================================================================================
// UvpPdfToP8Experiments — küçük, izole EPLAN deneyleri (P2). Hiçbir hipotez burada "kanıtlandı" sayılmaz;
// yalnız YERLEŞTİRİLEN ve EPLAN'dan GERİ OKUNAN durum yazılır. Karar geri okumayı okuyan kişinin/aracın.
//
//   UvpPdfToP8Experiments /OUT:"<yeni klasör>" /TEMPLATE:"<.zw9>" [/PART:"SIE.6ES7131-6BF01-0BA0"]
//
// Her çalıştırma şablondan YENİ test projesi oluşturur; açık kullanıcı projesine dokunmaz. OUT'ta
// experiments.json varsa çalışmaz (önceki sonuç ezilmez). Her deney kendi sayfasındadır.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.EObjects;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    public class ExperimentsAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Experiments";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        internal class Bench
        {
            public Project Project;
            public Page Page;
            public List<object> Placed = new List<object>();
        }

        static void Note(Bench b, string what, string name, string symbol, StorableObject obj, double x, double y)
        {
            b.Placed.Add(new Dictionary<string, object> { { "what", what }, { "name", name }, { "symbol", symbol },
                                                          { "object_id", ImportAction.Id(obj) }, { "at", new[] { x, y } } });
        }

        internal static Function Fn(Bench b, string library, string symbol, int variant, string name, int cp, double x, double y, bool terminal)
        {
            SymbolVariant sv = ImportAction.SymbolOf(b.Project, library, symbol)[variant];
            if (sv == null) throw new InvalidOperationException(library + "/" + symbol + " #" + variant + " yok");
            Function f = terminal ? new Terminal() : new Function();
            f.Create(b.Page, sv);
            f.Name = name;
            f.Location = ImportAction.InsertionFor(sv, cp, new PointD(x, y));
            Note(b, "function", name, library + "/" + symbol + " #" + variant + " nokta " + cp, f, x, y);
            return f;
        }

        static SymbolReference Node(Bench b, string library, string symbol, string directions, double x, double y, string label)
        {
            string note;
            SymbolVariant sv = ImportAction.VariantWithDirections(b.Project, library, symbol, directions, out note);
            if (sv == null) throw new InvalidOperationException(note);
            SymbolReference sr = SymbolReference.Create(sv, b.Page);
            sr.Location = new PointD(x, y);
            Note(b, "node", label, note, sr, x, y);
            return sr;
        }

        internal static InterruptionPoint Ip(Bench b, string facing, double x, double y, string name)
        {
            int index;
            string note;
            SymbolVariant sv = ImportAction.VariantFacing(b.Project, "SPECIAL", "BP", facing, -1, out index, out note);
            if (sv == null) throw new InvalidOperationException(note);
            SymbolReference sr = SymbolReference.Create(sv, b.Page);
            sr.Location = ImportAction.InsertionFor(sv, index, new PointD(x, y));
            InterruptionPoint ip = sr as InterruptionPoint;
            if (ip == null) throw new InvalidOperationException("BP kesinti noktası üretmedi: " + sr.GetType().Name);
            ip.Name = name;
            Note(b, "interruption", name, note, ip, x, y);
            return ip;
        }

        static Dictionary<string, object> Hypothesis(string id, string experiment, string text, string decide)
        {
            return new Dictionary<string, object> { { "id", id }, { "experiment", experiment }, { "question", text }, { "decide_by", decide } };
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT"), template = Io.Param(ctx, "TEMPLATE"), partNumber = Io.Param(ctx, "PART");
            if (partNumber == "") partNumber = "SIE.6ES7131-6BF01-0BA0";
            Directory.CreateDirectory(outDir);
            string resultPath = Path.Combine(outDir, "experiments.json");
            if (File.Exists(resultPath)) return false;
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.experiments";
            report["contract_version"] = "1.0";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;
            report["template"] = template;
            List<object> hypotheses = new List<object> {
                Hypothesis("H1", "E5b", "Aynı satırdaki T düğümlerinin yan kolları arada çizgi olmadan birbirine bağlanır mı? (13 Eylül L+–M–1–9–3–11 zincirinin aday nedeni)",
                           "E5b sayfasında iki T arasında GraphicalConnection; E5c'de iki klemens arasında Connection"),
                Hypothesis("H2", "E7", "DynamicConnectionLine iki hizasız ucu elektriksel Connection olarak bağlar mı?", "E7'de -E7F:2 ile -E7X:1 arasında Connection var mı"),
                Hypothesis("H3", "E1", "Hizalı ve birbirine bakan iki bağlantı noktası çizgisiz bağlanır mı?", "E1'de -E1F ile -E1X:1 arasında Connection"),
                Hypothesis("H4", "E2,E2b", "Yönle seçilen köşe (CO) güzergâhı bağlar mı; köşe Connection ucu olur mu?", "Connection uçları fonksiyon/kesinti mi; köşe SymbolReferences içinde mi"),
                Hypothesis("H5", "E3", "T düğümü Connection ucu olur mu, yoksa bağlantılar uçlar (pin/kesinti) arasında mı kurulur?", "E3 Connection uçlarının türü"),
                Hypothesis("H6", "E4", "Noktasız kesişim bağlantı üretir mi?", "E4'te yatay ve dikey ağ arasında Connection/GraphicalConnection olmamalı"),
                Hypothesis("H7", "E6", "Aynı adlı kesinti noktası çifti sayfalar arası Connection (SubConnections) ve çapraz referans üretir mi?", "E6 Connection sub_connections ve interruption_points.cross_reference"),
                Hypothesis("H8", "E5", "Parçadan oluşan PLC fonksiyonlarının kategori/grup/kimliği L+/M beslemesini kanaldan ayırır mı; yan yana L+ ve M bağlanır mı?", "E5 function_definitions ve L+–M Connection") };
            report["hypotheses"] = hypotheses;
            List<object> experiments = new List<object>();
            List<string> log = new List<string>();
            Project prj = null;
            List<Page> pages = new List<Page>();
            Dictionary<Page, int> numbers = new Dictionary<Page, int>();
            Dictionary<string, object> stamp = null;      // bağlantı tazeliği damgası (Generate sonrası)
            try
            {
                string dll = typeof(ExperimentsAction).Assembly.Location;
                report["dll"] = dll;
                report["dll_sha256"] = ImportAction.Sha256(dll);
                report["eplan_version"] = Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion;
                prj = new ProjectManager().CreateProject(Path.Combine(outDir, "UVP_DENEY_" + Io.Stamp() + ".elk"), template);
                report["test_project"] = prj.ProjectLinkFilePath;
                prj.LockAllObjects();
                string[] ids = { "E1", "E2", "E2b", "E3", "E4", "E5", "E5b", "E5c", "E6a", "E6b", "E7" };
                using (LockingStep step = new LockingStep())
                {
                    Dictionary<string, Bench> benches = new Dictionary<string, Bench>();
                    foreach (string id in ids)
                    {
                        PagePropertyList names = new PagePropertyList();
                        names.PAGE_COUNTER = id;
                        Page pg = new Page();
                        pg.Create(prj, DocumentTypeManager.DocumentType.Circuit, names);
                        pages.Add(pg);
                        numbers[pg] = pages.Count;
                        Bench b = new Bench();
                        b.Project = prj;
                        b.Page = pg;
                        benches[id] = b;
                    }
                    foreach (string id in ids)
                    {
                        Bench b = benches[id];
                        Dictionary<string, object> row = new Dictionary<string, object>();
                        row["id"] = id;
                        row["page"] = b.Page.Name;
                        row["placed"] = b.Placed;
                        try
                        {
                            switch (id)
                            {
                                case "E1":   // F1 alt ucu (Down) ile klemens üst ucu (Up) aynı dikeyde, çizgi yok
                                    Fn(b, "IEC_symbol", "F1", 0, "-E1F", 1, 100, 196, false);
                                    Fn(b, "IEC_symbol", "X", 0, "-E1X:1", 0, 100, 170, true);
                                    break;
                                case "E2":   // tek köşe: F1 aşağı → köşe (Up,Right) → sağdaki kesinti (Left)
                                    Fn(b, "IEC_symbol", "F1", 0, "-E2F", 1, 100, 196, false);
                                    Node(b, "SPECIAL", "CO", "Right,Up", 100, 170, "köşe");
                                    Ip(b, "Left", 140, 170, "UVP_EXP_E2");
                                    break;
                                case "E2b":  // iki köşe: F1 aşağı → (Up,Right) → (Down,Left) → klemens
                                    Fn(b, "IEC_symbol", "F1", 0, "-E2bF", 1, 100, 196, false);
                                    Node(b, "SPECIAL", "CO", "Right,Up", 100, 185, "köşe 1");
                                    Node(b, "SPECIAL", "CO", "Down,Left", 130, 185, "köşe 2");
                                    Fn(b, "IEC_symbol", "X", 0, "-E2bX:1", 0, 130, 170, true);
                                    break;
                                case "E3":   // T dalı: kesinti → T(Down,Left,Right) → kesinti; T altı → F1 üst ucu
                                    Ip(b, "Right", 60, 230, "UVP_EXP_E3A");
                                    Node(b, "SPECIAL", "TLRU", "Down,Left,Right", 100, 230, "T");
                                    Ip(b, "Left", 140, 230, "UVP_EXP_E3B");
                                    Fn(b, "IEC_symbol", "F1", 0, "-E3F", 0, 100, 210, false);
                                    break;
                                case "E4":   // noktasız kesişim: yatay kesinti çifti ile dikey F1→klemens x=100,y=150'de kesişir
                                    Ip(b, "Right", 60, 150, "UVP_EXP_E4A");
                                    Ip(b, "Left", 140, 150, "UVP_EXP_E4B");
                                    Fn(b, "IEC_symbol", "F1", 0, "-E4F", 1, 100, 176, false);
                                    Fn(b, "IEC_symbol", "X", 0, "-E4X:1", 0, 100, 120, true);
                                    break;
                                case "E5":   // PLC: parçadan cihaz, L+ / M yan yana (5 mm), kanal 1 ayrı
                                    RunPlc(b, partNumber, row, log);
                                    break;
                                case "E5b":  // H1: iki T aynı satırda, arada hiçbir şey yok
                                    Node(b, "SPECIAL", "TLRU", "Down,Left,Right", 60, 100, "T1");
                                    Node(b, "SPECIAL", "TLRU", "Down,Left,Right", 80, 100, "T2");
                                    break;
                                case "E5c":  // H1: iki klemensin tam üstüne T (13 Eylül yerleşiminin benzeri)
                                    Fn(b, "IEC_symbol", "X", 0, "-E5cX:1", 0, 60, 100, true);
                                    Fn(b, "IEC_symbol", "X", 0, "-E5cX:2", 0, 65, 100, true);
                                    Node(b, "SPECIAL", "TLRU", "Down,Left,Right", 60, 100, "T on X:1");
                                    Node(b, "SPECIAL", "TLRU", "Down,Left,Right", 65, 100, "T on X:2");
                                    break;
                                case "E6a":  // devam: F1 aşağı → kesinti (Up)
                                    Fn(b, "IEC_symbol", "F1", 0, "-E6F", 1, 100, 196, false);
                                    Ip(b, "Up", 100, 170, "UVP_EXP_E6");
                                    break;
                                case "E6b":  // devam karşılığı: kesinti (Down) → klemens
                                    Ip(b, "Down", 100, 200, "UVP_EXP_E6");
                                    Fn(b, "IEC_symbol", "X", 0, "-E6X:1", 0, 100, 170, true);
                                    break;
                                case "E7":   // H2: hizasız iki uç arasına DynamicConnectionLine
                                    Fn(b, "IEC_symbol", "F1", 0, "-E7F", 1, 100, 196, false);
                                    Fn(b, "IEC_symbol", "X", 0, "-E7X:1", 0, 140, 170, true);
                                    DynamicConnectionLine line = new DynamicConnectionLine();
                                    line.Create(b.Page);
                                    line.SetGraphics(new PointD(100, 196), new PointD(140, 170));
                                    Note(b, "dynamic_connection_line", "E7 çizgi", "DynamicConnectionLine", line, 100, 196);
                                    break;
                            }
                        }
                        catch (Exception ex) { row["error"] = ex.GetType().Name + ": " + ex.Message; }
                        experiments.Add(row);
                    }
                    new Generate().Connections(pages.ToArray(), true);
                    stamp = new Dictionary<string, object>();
                    stamp["connections_updated"] = true;
                    stamp["connections_update_utc"] = DateTime.UtcNow.ToString("o");
                }
                report["readback"] = Readback.Build(prj, pages, numbers, stamp);
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
                report["experiments"] = experiments;
                report["log"] = log;
                Io.WriteJson(resultPath, report);
                File.WriteAllText(Path.Combine(outDir, "deney_ozet.txt"), Summary(report), new UTF8Encoding(false));
                if (prj != null) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }

        static void RunPlc(Bench b, string partNumber, Dictionary<string, object> row, List<string> log)
        {
            MDPartsDatabase db = new MDPartsManagement().OpenDatabase();
            MDPart part = db.GetPart(partNumber);
            if (part == null) throw new InvalidOperationException("Parça veritabanında yok: " + partNumber);
            Function[] functions = new DeviceService().CreateDevice(b.Project, part.PartNr, part.Variant ?? "", new FunctionPropertyList());
            List<object> definitions = new List<object>();
            foreach (Function fn in functions)
            {
                string only = fn.Pins.Length == 1 ? fn.Pins[0].Name : "";
                try { fn.Name = (fn.IsMainFunction || only == "") ? "-E5D" : "-E5D:" + only; } catch (Exception ex) { log.Add(ex.Message); }
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["name"] = ImportAction.Text(delegate { return fn.Name; });
                d["pins"] = only;
                d["class_name"] = fn.GetType().Name;
                try
                {
                    FunctionDefinition def = fn.FunctionDefinition;
                    d["category"] = def.FunctionCategory.ToString();
                    d["group"] = (int)def.Group;
                    d["definition_id"] = (int)def.Id;
                    d["definition_name"] = ImportAction.Text(delegate { return def.Name.GetAsString(); });
                }
                catch (Exception ex) { d["definition_error"] = ex.Message; }
                definitions.Add(d);
            }
            row["function_definitions"] = definitions;
            PlacePlc(b, functions, "L+", "PLC_CBOX_LEFT", 60, 150, row);
            PlacePlc(b, functions, "M", "PLC_CBOX_CON", 65, 150, row);
            PlacePlc(b, functions, "1", "PLC_CBOX", 100, 150, row);
        }

        static void PlacePlc(Bench b, Function[] functions, string pin, string symbol, double x, double y, Dictionary<string, object> row)
        {
            List<Function> hits = new List<Function>();
            foreach (Function fn in functions)
                if (fn.Pins.Length == 1 && fn.Pins[0].Name == pin) hits.Add(fn);
            if (hits.Count != 1)
            {
                row["plc_" + pin] = hits.Count + " aday; yerleştirilmedi (ad tahmin edilmez)";
                return;
            }
            int index;
            string note;
            SymbolVariant sv = ImportAction.VariantFacing(b.Project, "IEC_symbol", symbol, "Up", 0, out index, out note);
            if (sv == null) { row["plc_" + pin] = note; return; }
            hits[0].PlaceAt(b.Page, new PointD(x, y), DocumentTypeManager.DocumentType.Circuit, sv);
            PointD absolute = ImportAction.Absolute(hits[0], hits[0].Pins[0]);
            hits[0].Location = new PointD(hits[0].Location.X + x - absolute.X, hits[0].Location.Y + y - absolute.Y);
            Note(b, "plc_pin", pin, note, hits[0], x, y);
        }

        static string Summary(Dictionary<string, object> report)
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("UVP EPLAN deneyleri — yalnız GÖZLEM; hipotez kararı geri okumaya bakılarak verilir.");
            s.AppendLine("Test projesi: " + (report.ContainsKey("test_project") ? report["test_project"] : "-"));
            if (report.ContainsKey("error")) s.AppendLine("HATA: " + report["error"]);
            foreach (object e in (List<object>)report["experiments"])
            {
                Dictionary<string, object> row = (Dictionary<string, object>)e;
                s.AppendLine(row["id"] + " (" + row["page"] + ")" + (row.ContainsKey("error") ? "  HATA: " + row["error"] : ""));
            }
            if (report.ContainsKey("readback"))
            {
                Dictionary<string, object> rb = (Dictionary<string, object>)report["readback"];
                foreach (object po in (List<object>)rb["pages"])
                {
                    Dictionary<string, object> page = (Dictionary<string, object>)po;
                    s.AppendLine("sayfa " + page["name"] + ": grafik bağ " + ((List<object>)page["graphical_connections"]).Count
                                 + ", fonksiyon " + ((List<object>)page["functions"]).Count + ", sembol " + ((List<object>)page["symbols"]).Count);
                }
                foreach (object co in (List<object>)rb["connections"])
                {
                    Dictionary<string, object> c = (Dictionary<string, object>)co;
                    Dictionary<string, object> a = (Dictionary<string, object>)c["start"], z = (Dictionary<string, object>)c["end"];
                    s.AppendLine("  bağlantı [sayfa " + c["physical_page"] + "] " + End(a) + "  <->  " + End(z)
                                 + "  köşe/T: " + ((List<object>)c["symbol_refs"]).Count + "  alt: " + ((List<object>)c["sub_connections"]).Count);
                }
                foreach (object io in (List<object>)rb["interruption_points"])
                {
                    Dictionary<string, object> ip = (Dictionary<string, object>)io;
                    s.AppendLine("  kesinti " + ip["page"] + " " + ip["name"] + " çapraz ref='" + ip["cross_reference"] + "'");
                }
            }
            return s.ToString();
        }

        static string End(Dictionary<string, object> end)
        {
            object function = end.ContainsKey("function") ? end["function"] : end.ContainsKey("kind") ? end["kind"] : "?";
            return function + ":" + (end.ContainsKey("pin") && end["pin"] != null ? end["pin"] : "-");
        }
    }

    // =====================================================================================
    // MADDE 3 deneyleri (09.10) — yalnız GÖZLEM; karar output\ajan_kayit\20261009_10madde\duzelt_K_csharp_deney_xref\PLAN.md
    // yorum tablosuyla verilir. Koşu: eplan_addin\build\deney_xref.ps1 (EPLAN kapalıyken).
    //
    //   UvpPdfToP8XrefProbe  /PROJECT:"<output\p8runs\madde3_*\...\UVP_*.elk>" /OUT:"<xref_*.json>"
    //                        [/MUTATE:none|set|updstruct|starsource] [/SETTING:"<proje ayar yolu>" /VALUE:"<değer>"]
    //     Kesinti okları çapraz referansı: UVP_013 şablonlu projede 0/18, IEC_bas001'de 18/18 (madde3/BULGU.md).
    //     Okur: tüm kesinti noktaları + aynı ad grupları, proje ayarları dökümü, yapı tanımlayıcıları (Location).
    //     MUTATE TEK değişiklik yapar → yeniden okur → kapat/aç → yeniden okur. Değişiklik KOPYAYA yazılır: yol
    //     Guard.TestProject (output\p8runs, UVP_) + '\madde3_' şartı.
    //   UvpPdfToP8IdentProbe /OUT:"<output\p8runs\madde3_*\ident>" /TEMPLATE:"<UVP_013.zw9>" [/TEMPLATE_K:"<IEC_bas001.zw9>"] [/ARMS:A,B,C,K]
    //     Sayfa gezgininde sayfa başına ayrı E280 düğümü. Her kol YENİ test projesi: EnsurePageStructure (aktarımın
    //     kendisi, yansımayla) → =280+E280/1 ve /2 (Page.Create), /3 (Create + SetName); /1↔/2 aynı adlı kesinti çifti.
    //     A bugünkü akış + sonda UpdateProjectStructureFromSettings; B yapı ayarından hemen sonra UpdateProjectStructure-
    //     FromSettings; C sayfa başına kapat/aç (panel akışı); K = A, IEC_bas001 şablonu (kontrol).
    // =====================================================================================
    internal static class Madde3
    {
        internal const string Chains = "EsCrossReference.Interruptionpoint.DisplayDestinationAtChains";
        // Kök düğüm listelenemezse dökülecek üst düğümler (Setting.eod'da iki projede görülenler, madde3 ikili karşılaştırma).
        static readonly string[] TopNodes = { "EsCrossReference", "InterruptionPointLog", "EServicesGui", "ESBase", "EsConnection",
                                              "TrDMProject", "TrDMBase", "PageBrowserGui", "MacrosGui", "DisplayScheme", "XEsSettingsGui" };

        static string T(Func<string> read) { return ImportAction.Text(read); }

        internal static void Guarded(string file)
        {
            if (file.IndexOf("\\madde3_", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Yalnız madde3_ kopyası (değişiklik kopyaya yazılır): " + file);
        }

        /// <summary>Ad → kesinti noktaları (sayfa adına göre sıralı).</summary>
        internal static Dictionary<string, List<InterruptionPoint>> Groups(Project prj)
        {
            Dictionary<string, List<InterruptionPoint>> groups = new Dictionary<string, List<InterruptionPoint>>();
            foreach (InterruptionPoint ip in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
            {
                InterruptionPoint one = ip;
                string name = T(() => one.Name);
                if (!groups.ContainsKey(name)) groups[name] = new List<InterruptionPoint>();
                groups[name].Add(ip);
            }
            foreach (List<InterruptionPoint> g in groups.Values)
                g.Sort((a, b) => string.CompareOrdinal(T(() => a.Page.Name), T(() => b.Page.Name)));
            return groups;
        }

        internal static Dictionary<string, object> Ips(Project prj)
        {
            List<object> rows = new List<object>();
            int pairEnds = 0, filled = 0, objectsAll = 0, engine = 0;
            foreach (KeyValuePair<string, List<InterruptionPoint>> g in Groups(prj))
                foreach (InterruptionPoint ip in g.Value)
                {
                    InterruptionPointPropertyList p = ip.Properties;
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["name"] = g.Key;
                    r["group_size"] = g.Value.Count;
                    r["page"] = T(() => ip.Page.Name);
                    r["visible_name"] = T(() => ip.VisibleName);
                    r["plant"] = T(() => ip.IdentifyingNameParts.DESIGNATION_PLANT.ToString());
                    r["location"] = T(() => ip.IdentifyingNameParts.DESIGNATION_LOCATION.ToString());
                    r["sort_code"] = T(() => p.INTERRUPTIONPOINT_SORT_CODE.ToString());
                    r["cross_reference"] = T(() => p.INTERRUPTIONPOINT_CROSSREFERENCE.ToString());
                    r["crossref_destination"] = T(() => p.INTERRUPTIONPOINT_CROSSREFDESTINATION.ToString());
                    r["show_all_crossreferences"] = T(() => p.INTERRUPTIONPOINT_SHOWALLCROSSREFERENCES.ToString());
                    r["is_star_source"] = T(() => ip.IsStarSource.ToString());
                    List<string> all = new List<string>();
                    try
                    {
                        foreach (StorableObject o in ip.CrossReferencedObjectsAll)
                        {
                            Placement pl = o as Placement;
                            all.Add(o.GetType().Name + (pl == null ? "" : " " + T(() => pl.Page.Name)));
                        }
                    }
                    catch (Exception ex) { all.Add("<hata " + ex.Message + ">"); }
                    r["crossreferenced_objects_all"] = all;
                    r["verify_crossreferenced"] = VerifyCount(ip, false);
                    r["verify_same_name"] = VerifyCount(ip, true);
                    r["object_id"] = ImportAction.Id(ip);
                    rows.Add(r);
                    if (g.Value.Count < 2) continue;
                    pairEnds++;
                    if ((string)r["cross_reference"] != "") filled++;
                    if (all.Count > 0) objectsAll++;
                    int n;
                    if (int.TryParse((string)r["verify_crossreferenced"], out n) && n > 0) engine++;
                }
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["pair_ends"] = pairEnds;                 // aynı adı ≥2 noktada taşıyan uçlar
            result["xref_filled"] = filled;                 // INTERRUPTIONPOINT_CROSSREFERENCE dolu
            result["objects_all_nonzero"] = objectsAll;     // CrossReferencedObjectsAll boş değil
            result["verify_found_partner"] = engine;        // GetCrossReferencedInterruptionPoints > 0 (yalnız UVP_TEST derlemesi)
            result["rows"] = rows;
            return result;
        }

#if UVP_TEST
        // ponytail: GetCrossReferencedInterruptionPoints yalnız denetim sınıfının örneğinde var. Özel iç sınıf + yalnız UVP_TEST:
        // eklenti yükleyicisinin bunu proje denetimi olarak kaydetmemesi beklenir (KANITSIZ); teslim DLL'inde bu sınıf yok.
        sealed class Probe : Eplan.EplApi.EServices.InterruptionPointVerification, Eplan.EplApi.EServices.IInterruptionPointVerification
        {
            public override void OnRegister(ref string name, ref int ordinal) { name = "UvpXrefProbe"; ordinal = 0; }
            public override void OnRegister(ref string creator, ref Eplan.EplApi.EServices.IMessage.Region region, ref int id,
                                            ref Eplan.EplApi.EServices.IMessage.Classification classification, ref int ordinal)
            { creator = "UVP"; }
            public override string GetMessageText() { return ""; }
            public override void DoHelp() { }
            public override void OnStartInspection(bool online) { }
            public override void OnEndInspection() { }
            public override void Execute(StorableObject o) { }
        }
        static Probe probe;

        static string VerifyCount(InterruptionPoint ip, bool sameName)
        {
            try
            {
                if (probe == null) probe = new Probe();
                ArrayList list = new ArrayList();
                if (sameName) probe.GetAllInterruptionPointsWithSameName(ip, ref list);
                else probe.GetCrossReferencedInterruptionPoints(ip, ref list);
                return list.Count.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { return "<hata " + ex.GetType().Name + ": " + ex.Message + ">"; }
        }
#else
        static string VerifyCount(InterruptionPoint ip, bool sameName) { return "<UVP_TEST derlemesi değil>"; }
#endif

        internal static string Value(ProjectSettings ps, string path)
        {
            try
            {
                ISettings.SettingType type = ps.GetTypeOfSetting(path);
                int count = Math.Max(1, Math.Min(ps.GetCountOfValues(path), 50));
                List<string> v = new List<string>();
                for (int i = 0; i < count; i++)
                    switch (type)
                    {
                        case ISettings.SettingType.Bool: v.Add(ps.GetBoolSetting(path, i) ? "1" : "0"); break;
                        case ISettings.SettingType.Double: v.Add(ps.GetDoubleSetting(path, i).ToString(CultureInfo.InvariantCulture)); break;
                        case ISettings.SettingType.String: v.Add(ps.GetStringSetting(path, i)); break;
                        case ISettings.SettingType.MLString: v.Add(ps.GetMultiLangStringSetting(path, i).GetAsString()); break;
                        default: v.Add(ps.GetNumericSetting(path, i).ToString(CultureInfo.InvariantCulture)); break;
                    }
                return type + ":" + string.Join("|", v);
            }
            catch (Exception ex) { return "<hata " + ex.GetType().Name + ": " + ex.Message + ">"; }
        }

        internal static string Set(ProjectSettings ps, string path, string value)
        {
            ISettings.SettingType type = ps.GetTypeOfSetting(path);
            if (type == ISettings.SettingType.Bool) ps.SetBoolSetting(path, value == "1" || value.ToLowerInvariant() == "true", 0);
            else if (type == ISettings.SettingType.String) ps.SetStringSetting(path, value, 0);
            else if (type == ISettings.SettingType.Double) ps.SetDoubleSetting(path, double.Parse(value, CultureInfo.InvariantCulture), 0);
            else ps.SetNumericSetting(path, int.Parse(value, CultureInfo.InvariantCulture), 0);
            return path + " = " + Value(ps, path);
        }

        /// <summary>Proje ayarlarının tamamı (yol → "tür:değer"). Liste girdisi göreli/tam olabilir: ExistSetting ile çözülür.</summary>
        internal static Dictionary<string, object> SettingsDump(Project prj, List<string> notes)
        {
            Dictionary<string, object> all = new Dictionary<string, object>();
            ProjectSettings ps = new ProjectSettings(prj);
            List<string> tops = new List<string>();
            try
            {
                StringCollection sc = new StringCollection();
                new ProjectSettingNode(prj, "").GetListOfNodes(ref sc, false);
                foreach (string s in sc) tops.Add(s);
            }
            catch (Exception ex) { notes.Add("kök ayar düğümü listelenemedi: " + ex.Message); }
            if (tops.Count == 0) tops.AddRange(TopNodes);
            notes.Add("üst düğümler: " + string.Join(",", tops));
            foreach (string top in tops)
            {
                StringCollection sc = new StringCollection();
                try { new ProjectSettingNode(prj, top).GetListOfAllSettings(ref sc, true); }
                catch (Exception ex) { all[top + ".<liste_hatasi>"] = ex.Message; continue; }
                foreach (string raw in sc)
                {
                    string path = raw;
                    int at = raw.IndexOf(top + ".", StringComparison.Ordinal);
                    if (at > 0) path = raw.Substring(at);
                    else if (at < 0) path = top + "." + raw;
                    bool exists = false;
                    try { exists = ps.ExistSetting(path); } catch (Exception) { }
                    if (!exists) { try { if (ps.ExistSetting(raw)) path = raw; } catch (Exception) { } }
                    all[path] = Value(ps, path);
                }
            }
            if (!all.ContainsKey(Chains)) all[Chains] = Value(ps, Chains);
            return all;
        }

        /// <summary>Yapı tanımlayıcıları (Proje verileri > Yapı tanımlayıcı yönetimi) ve hiyerarşi:ad sayımı.</summary>
        internal static Dictionary<string, object> Locations(Project prj)
        {
            List<object> rows = new List<object>();
            Dictionary<string, object> counts = new Dictionary<string, object>();
            try
            {
                foreach (Location l in prj.GetLocationObjects())
                {
                    Location one = l;
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["hierarchy"] = T(() => one.HierarchyId.ToString());
                    r["name"] = T(() => one.Name);
                    r["full_name"] = T(() => one.Properties.LOCATION_FULLNAME.ToString());
                    r["parent"] = T(() => one.ParentNode == null ? "" : one.ParentNode.Name);
                    r["used"] = T(() => one.IsUsed.ToString());
                    r["sort_id"] = T(() => one.SortId.ToString(CultureInfo.InvariantCulture));
                    r["object_id"] = ImportAction.Id(one);
                    rows.Add(r);
                    string key = r["hierarchy"] + ":" + r["name"];
                    counts[key] = (counts.ContainsKey(key) ? (int)counts[key] : 0) + 1;
                }
            }
            catch (Exception ex) { counts["<hata>"] = ex.GetType().Name + ": " + ex.Message; }
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["counts"] = counts;
            result["rows"] = rows;
            return result;
        }

        internal static Dictionary<string, object> PageRow(Page pg)
        {
            PagePropertyList p = pg.Properties;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["name"] = T(() => pg.Name);
            r["identifying_name"] = T(() => pg.IdentifyingName);
            r["object_id"] = ImportAction.Id(pg);
            r["11038_objectcode_structure"] = T(() => p.PAGE_OBJECTCODE_STRUCTUREWITHOUTPREFIX.ToString());
            r["11039_objectcode_parts"] = T(() => p.PAGE_OBJECTCODE_STRUCTUREPARTWITHOUTPREFIX[1] + "|" + p.PAGE_OBJECTCODE_STRUCTUREPARTWITHOUTPREFIX[2]
                                                  + "|" + p.PAGE_OBJECTCODE_STRUCTUREPARTWITHOUTPREFIX[3]);
            r["objectcode"] = T(() => p.PAGE_OBJECTCODE.ToString());
            r["identname"] = T(() => p.PAGE_IDENTNAME.ToString());
            r["fullname"] = T(() => p.PAGE_FULLNAME.ToString());
            r["plant"] = T(() => p.DESIGNATION_PLANT.ToString());
            r["location"] = T(() => p.DESIGNATION_LOCATION.ToString());
            r["full_plant"] = T(() => p.DESIGNATION_FULLPLANT.ToString());
            r["full_location"] = T(() => p.DESIGNATION_FULLLOCATION.ToString());
            return r;
        }

        internal static Dictionary<string, object> DbFiles(string elk)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            string edb = Path.ChangeExtension(elk, ".edb");
            foreach (string f in new[] { "Location.eod", "Block.eod", "Page.eod", "ProjectInfo.xml" })
            {
                string path = Path.Combine(edb, f);
                r[f] = File.Exists(path) ? new FileInfo(path).Length : -1;
            }
            return r;
        }

        internal static void Header(Dictionary<string, object> report, string contract)
        {
            report["contract"] = contract;
            report["contract_version"] = "1.0";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;
            string dll = typeof(Madde3).Assembly.Location;
            report["dll"] = dll;
            report["dll_sha256"] = ImportAction.Sha256(dll);
            report["eplan_version"] = Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion;
#if UVP_TEST
            report["uvp_test_build"] = true;
#else
            report["uvp_test_build"] = false;
#endif
        }
    }

    public class XrefProbeAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8XrefProbe";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outPath = Io.Param(ctx, "OUT"), mutate = Io.Param(ctx, "MUTATE").ToLowerInvariant();
            if (mutate == "") mutate = "none";
            if (outPath == "" || File.Exists(outPath)) return false;           // önceki sonuç ezilmez
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            Dictionary<string, object> report = new Dictionary<string, object>();
            List<string> notes = new List<string>();
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                Madde3.Header(report, "uvp.pdf2p8.xref_probe");
                report["mutate"] = mutate;
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                Madde3.Guarded(file);
                report["project"] = file;
                prj = pm.OpenProject(file);
                report["settings"] = Madde3.SettingsDump(prj, notes);
                report["locations"] = Madde3.Locations(prj);
                report["before"] = Madde3.Ips(prj);
                if (mutate != "none")
                {
                    report["mutation"] = Mutate(prj, mutate, Io.Param(ctx, "SETTING"), Io.Param(ctx, "VALUE"));
                    report["after"] = Madde3.Ips(prj);
                    report["locations_after"] = Madde3.Locations(prj);
                    prj.Close();
                    prj = pm.OpenProject(file);
                    report["after_reopen"] = Madde3.Ips(prj);
                    report["locations_after_reopen"] = Madde3.Locations(prj);
                    if (mutate == "set") report["setting_after_reopen"] = Madde3.Value(new ProjectSettings(prj), Io.Param(ctx, "SETTING"));
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
                report["notes"] = notes;
                Io.WriteJson(outPath, report);
                if (prj != null) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }

        static string Mutate(Project prj, string mutate, string setting, string value)
        {
            switch (mutate)
            {
                case "set":
                    if (setting == "") throw new InvalidOperationException("MUTATE:set için /SETTING ve /VALUE gerekir.");
                    ProjectSettings ps = new ProjectSettings(prj);
                    string before = Madde3.Value(ps, setting);
                    return "önce " + before + " → " + Madde3.Set(ps, setting, value);
                case "updstruct":
                    new ProjectManagement().UpdateProjectStructureFromSettings(prj);
                    return "ProjectManagement.UpdateProjectStructureFromSettings";
                case "starsource":
                    prj.LockAllObjects();
                    int n = 0;
                    using (LockingStep step = new LockingStep())
                    {
                        foreach (List<InterruptionPoint> g in Madde3.Groups(prj).Values)
                            if (g.Count >= 2) { g[0].IsStarSource = true; n++; }
                    }
                    return "IsStarSource=true: " + n + " grup (her grubun sayfa adına göre ilk oku)";
                case "sortcode":
                case "star":
                    // S1 10.10: /VALUE:"<object_id>=<sıra kodu>,..." (sortcode) ya da "<object_id>,..." (star → IsStarSource).
                    Dictionary<string, string> want = new Dictionary<string, string>();
                    foreach (string part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] kv = part.Split('=');
                        want[kv[0].Trim()] = kv.Length > 1 ? kv[1].Trim() : "";
                    }
                    prj.LockAllObjects();
                    List<string> done = new List<string>();
                    using (LockingStep step = new LockingStep())
                    {
                        foreach (InterruptionPoint ip in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
                        {
                            string id = ImportAction.Id(ip);
                            if (!want.ContainsKey(id)) continue;
                            try
                            {
                                if (mutate == "star") ip.IsStarSource = true;
                                else ip.Properties.INTERRUPTIONPOINT_SORT_CODE = long.Parse(want[id], CultureInfo.InvariantCulture);
                                done.Add(id + (mutate == "star" ? "" : "=" + want[id]));
                            }
                            catch (Exception ex) { done.Add(id + "=" + want[id] + " HATA " + ex.GetType().Name); }
                        }
                    }
                    return mutate + ": " + string.Join(",", done.ToArray()) + " (istenen " + want.Count + ")";
                default:
                    throw new InvalidOperationException("MUTATE bilinmiyor: " + mutate + " (none|set|updstruct|starsource|sortcode|star)");
            }
        }
    }

    public class IdentProbeAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8IdentProbe";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Path.GetFullPath(Io.Param(ctx, "OUT"));
            if (outDir.IndexOf("\\output\\p8runs\\madde3_", StringComparison.OrdinalIgnoreCase) < 0) return false;   // yalnız deney klasörü
            Directory.CreateDirectory(outDir);
            string resultPath = Path.Combine(outDir, "ident_probe.json");
            if (File.Exists(resultPath)) return false;
            string template = Io.Param(ctx, "TEMPLATE"), templateK = Io.Param(ctx, "TEMPLATE_K"), arms = Io.Param(ctx, "ARMS");
            if (arms == "") arms = templateK == "" ? "A,B,C" : "A,B,C,K";
            Dictionary<string, object> report = new Dictionary<string, object>();
            List<object> rows = new List<object>();
            try
            {
                Madde3.Header(report, "uvp.pdf2p8.ident_probe");
                report["template"] = template;
                report["template_k"] = templateK;
                foreach (string raw in arms.Split(','))
                {
                    string arm = raw.Trim().ToUpperInvariant();
                    rows.Add(Arm(arm, outDir, arm == "K" ? templateK : template));
                }
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().FullName + ": " + ex.Message;
            }
            finally
            {
                report["arms"] = rows;
                Io.WriteJson(resultPath, report);
            }
            return true;
        }

        static void Step(List<object> steps, string label, Project prj, string elk)
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["label"] = label;
            s["locations"] = Madde3.Locations(prj);
            List<object> pages = new List<object>();
            try { foreach (Page pg in prj.Pages) pages.Add(Madde3.PageRow(pg)); }
            catch (Exception ex) { pages.Add("<hata " + ex.Message + ">"); }
            s["pages"] = pages;
            s["db_files"] = Madde3.DbFiles(elk);
            steps.Add(s);
        }

        static Dictionary<string, object> Arm(string arm, string outDir, string template)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["arm"] = arm;
            r["template"] = template;
            List<object> steps = new List<object>();
            List<string> log = new List<string>();
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                string dir = Path.Combine(outDir, arm);
                Directory.CreateDirectory(dir);
                string elk = Path.Combine(dir, "UVP_IDENT_" + arm + "_" + Io.Stamp() + ".elk");
                prj = pm.CreateProject(elk, template);
                r["project"] = prj.ProjectLinkFilePath;
                prj.LockAllObjects();
                Step(steps, "created", prj, elk);
                // Aktarımın KENDİ yapı kurulumu (UvpPdfToP8Import.cs EnsurePageStructure; private → yansıma). Kopya kod yok.
                Dictionary<string, object> receipt = new Dictionary<string, object>();
                typeof(ImportAction).GetMethod("EnsurePageStructure", BindingFlags.NonPublic | BindingFlags.Static)
                                    .Invoke(null, new object[] { prj, receipt, log });
                r["page_structure"] = receipt.ContainsKey("page_structure") ? receipt["page_structure"] : "";
                Step(steps, "after_ensure_page_structure", prj, elk);
                if (arm == "B")
                {
                    new ProjectManagement().UpdateProjectStructureFromSettings(prj);
                    Step(steps, "after_update_structure", prj, elk);
                }
                // 09.10 gece 2. koşu: D = yapı açıldıktan sonra BİR kez kapat/aç, sonra her sayfa ayrı oturum (gerçek liste
                // akışı); E = yalnız bir kez kapat/aç, sayfalar tek oturum. Soru: kapat/aç sayfadan ÖNCE yapılırsa eşleşir mi?
                // 09.10 gece 3. koşu: H = E + sayfa doküman tipi &EFS1 (UVP çizimci yapısı); I = D + &EFS1 (sayfa başına oturum);
                // F = E + kesinti oku adı TAM yapıyla ("=280+E280-UVP…"). Soru: eşleşmeyi doküman tipi mi, ok adının yapısı mı açıyor?
                // 5. koşu: L = yapı güncelle (UpdateProjectStructureFromSettings) SONRA kapat/aç; M = kapat/aç SONRA yapı güncelle.
                if (arm == "L") { new ProjectManagement().UpdateProjectStructureFromSettings(prj); Step(steps, "after_update_structure", prj, elk); }
                // 6. koşu N: 10019 "iç içe cihaz etiketleri" (PROJ_HIERARCHY_CODEDNESTINGSETTINGS) UVP_013'te 16	1	1	1	1	1	1	1	1	0	0	0	0	1	0	1	0,
                // IEC_bas001'de 16	0	1	1	1	1	0	1	0	0	0	0	0	0	0	0	0 (ProjectInfo.xml farkı). IEC değeri yazılıp sayfalar kurulur.
                // 6. koşu O: sayfa yapısından doküman tipi düzeyi (<10006>) çıkarılır (IEC gibi <10002><10004><11012><11013>).
                if (arm == "O")
                {
                    prj.Properties.PROJ_HIERARCHY_PAGE = "<10002><10004><11012><11013>";
                    prj.Properties.PROJ_NAMEFORMAT_PAGE = "<10002><10004><11012><11013>";
                    log.Add("10050/10070 sonra: " + prj.Properties.PROJ_HIERARCHY_PAGE.ToString() + " | " + prj.Properties.PROJ_NAMEFORMAT_PAGE.ToString());
                    Step(steps, "after_page_structure_no_doctype", prj, elk);
                }
                // 7. koşu P: yapı açılıp kapat/aç SONRA Reorganize(prj, true) (proje reorganizasyonu, iç dizinler) → sayfalar.
                // Q: sayfalar kurulduktan sonra Reorganize (mevcut projeyi onarır mı?). R: kapat/aç sonra CompressProject("").
                if (arm == "N")
                {
                    string before = prj.Properties.PROJ_HIERARCHY_CODEDNESTINGSETTINGS.ToString();
                    prj.Properties.PROJ_HIERARCHY_CODEDNESTINGSETTINGS = "16	0	1	1	1	1	0	1	0	0	0	0	0	0	0	0	0";
                    log.Add("10019 once: " + before + " | sonra: " + prj.Properties.PROJ_HIERARCHY_CODEDNESTINGSETTINGS.ToString());
                    Step(steps, "after_nesting_10019", prj, elk);
                }
                if (arm == "D" || arm == "E" || arm == "F" || arm == "H" || arm == "I" || arm == "J" || arm == "L" || arm == "M" || arm == "N" || arm == "O" || arm == "P" || arm == "Q" || arm == "R")
                {
                    prj.Close();
                    prj = pm.OpenProject(elk);
                    prj.LockAllObjects();
                    Step(steps, "after_reopen_before_pages", prj, elk);
                }
                if (arm == "M") { new ProjectManagement().UpdateProjectStructureFromSettings(prj); Step(steps, "after_update_structure", prj, elk); }
                if (arm == "P") { new ProjectManagement().Reorganize(prj, true); Step(steps, "after_reorganize", prj, elk); }
                if (arm == "R") { try { new ProjectManagement().CompressProject(prj, ""); } catch (Exception ex) { log.Add("compress: " + ex.Message); } Step(steps, "after_compress", prj, elk); }
                for (int i = 1; i <= 3; i++)
                {
                    if ((arm == "C" || arm == "D" || arm == "I") && i > 1)
                    {
                        prj.Close();
                        prj = pm.OpenProject(elk);
                        prj.LockAllObjects();
                    }
                    MakePage(prj, i, log, (arm == "H" || arm == "I") ? "EFS1" : "", arm == "F", arm == "J");
                    Step(steps, "after_page_" + i, prj, elk);
                }
                r["xref"] = Madde3.Ips(prj);
                if (arm == "Q") { new ProjectManagement().Reorganize(prj, true); Step(steps, "after_reorganize_posthoc", prj, elk); r["xref_after_reorganize"] = Madde3.Ips(prj); }
                if (arm != "B")
                {
                    new ProjectManagement().UpdateProjectStructureFromSettings(prj);
                    Step(steps, "after_update_structure_posthoc", prj, elk);
                    r["xref_after_update"] = Madde3.Ips(prj);
                }
                prj.Close();
                prj = pm.OpenProject(elk);
                Step(steps, "reopened", prj, elk);
                r["xref_after_reopen"] = Madde3.Ips(prj);
                prj.Close();
                prj = null;
                r["db_files_closed"] = Madde3.DbFiles(elk);
                r["ok"] = true;
            }
            catch (Exception ex)
            {
                Exception e = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                r["ok"] = false;
                r["error"] = e.GetType().FullName + ": " + e.Message;
                r["stack"] = e.StackTrace;
            }
            finally
            {
                r["steps"] = steps;
                r["log"] = log;
                if (prj != null) { try { prj.Close(); } catch (Exception) { } }
            }
            return r;
        }

        /// <summary>=280+E280/i. 1 ve 2 aktarım gibi (PagePropertyList ile Create); 3 yapısız Create + SetName.
        /// 1'de F1 → kesinti (Up), 2'de kesinti (Down) → klemens; ikisi AYNI ad (aktarımın ad biçimi: UVP&lt;10&gt;_UVP_&lt;hash&gt;).</summary>
        static void MakePage(Project prj, int i, List<string> log) { MakePage(prj, i, log, "", false); }

        static void MakePage(Project prj, int i, List<string> log, string doctype, bool fullIpName) { MakePage(prj, i, log, doctype, fullIpName, false); }

        static void MakePage(Project prj, int i, List<string> log, string doctype, bool fullIpName, bool noUnderscore)
        {
            // 4. koşu J: alt çizgisiz ad (DT sözdizimi denetimi UVP_013'te açık olabilir; '_' geçersiz sayılıyorsa motor oku dizinlemez).
            string ipName = fullIpName ? "=280+E280-UVP0000000000_UVP_M3IDENT" : (noUnderscore ? "UVPM3IDENT" : "UVP0000000000_UVP_M3IDENT");
            using (LockingStep step = new LockingStep())
            {
                PagePropertyList names = new PagePropertyList();
                // Doküman tipli kolda 3. sayfa da doğrudan tam adla kurulur: şablonda "&EFS1/3" zaten var, yapısız "&EFS1/3" çakışır (3. koşu H/I hatası).
                if (i < 3 || doctype != "") { names.DESIGNATION_PLANT = "280"; names.DESIGNATION_LOCATION = "E280"; }
                if (doctype != "") names.DESIGNATION_DOCTYPE = doctype;
                names.PAGE_COUNTER = i.ToString(CultureInfo.InvariantCulture);
                Page pg = new Page();
                pg.Create(prj, DocumentTypeManager.DocumentType.Circuit, names);
                if (i == 3 && doctype == "")
                {
                    PagePropertyList full = new PagePropertyList();
                    full.DESIGNATION_PLANT = "280";
                    full.DESIGNATION_LOCATION = "E280";
                    full.PAGE_COUNTER = "3";
                    if (doctype != "") full.DESIGNATION_DOCTYPE = doctype;
                    pg.SetName(full);
                }
                log.Add("sayfa " + i + ": " + pg.Name);
                ExperimentsAction.Bench b = new ExperimentsAction.Bench();
                b.Project = prj;
                b.Page = pg;
                try
                {
                    if (i == 1)
                    {
                        ExperimentsAction.Fn(b, "IEC_symbol", "F1", 0, "-M3F", 1, 100, 196, false);
                        ExperimentsAction.Ip(b, "Up", 100, 170, ipName);
                    }
                    if (i == 2)
                    {
                        ExperimentsAction.Ip(b, "Down", 100, 200, ipName);
                        ExperimentsAction.Fn(b, "IEC_symbol", "X", 0, "-M3X:1", 0, 100, 170, true);
                    }
                    if (i < 3) new Generate().Connections(new[] { pg }, true);
                }
                catch (Exception ex) { log.Add("sayfa " + i + " yerleşim: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
    }
}
