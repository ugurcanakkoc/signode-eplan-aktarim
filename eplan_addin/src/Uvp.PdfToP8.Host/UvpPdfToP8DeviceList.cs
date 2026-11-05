// =====================================================================================
// UvpPdfToP8DeviceList — malzeme listesi seçeneği B (MAIN 24.10 akşam): çizilemeyen cihaz EPLAN cihaz listesine.
//
// Paket `device_list_imports` (analyzer_v3/cihaz_listesi_aktar.py). Her kayıt için SAYFAYA KOYMADAN cihaz kurulur
// (DeviceService.CreateDevice: Cihaz Gezgini'ndeki "parçadan yeni cihaz"); ana fonksiyona ürün + aksesuarlar yazılır,
// çizimci cihazı listeden sayfaya sürükler. Kod EPLAN parça DB'sinde yoksa parça kurulmaz: cihaz sayfada (DCP) varsa
// yalnız DT kalır, yoksa ürünsüz kara kutu fonksiyonu (Gerätekasten 1303/1/1) yalnız DT olarak; satır kontrol listesine.
// Kurulan cihazın etiketi müşteri-cihazı yoluna girer (24.10): pakette aynı DT'nin sayfa nesneleri (DCP, kara kutu)
// yardımcı fonksiyon olur, parça yazmaz → BOM çift olmaz, ana fonksiyon listedeki cihazdır.
// EKLEMEZ: hedefte aynı DT zaten varsa (önceki paket ya da müşteri) ve hedef müşteri listeli projeyse (seçenek A:
// defterde olmayan yerleşmemiş parçalı fonksiyon var) — o zaman hiçbir kayıt eklenmez. Her kayıt makbuz `device_list_imported`.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Eplan.EplApi.Base;
using Eplan.EplApi.Base.Enums;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.EObjects;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    public partial class ImportAction
    {
        /// <summary>Hedefteki bütün fonksiyonların etiketleri (ad ':' öncesi).</summary>
        static HashSet<string> ExistingTags(Project prj)
        {
            HashSet<string> tags = new HashSet<string>();
            foreach (Function f in new DMObjectsFinder(prj).GetFunctions(new FunctionsFilter()))
            {
                string name = Text(delegate { return f.Name; });
                int cut = name.IndexOf(':');
                tags.Add(cut < 0 ? name : name.Substring(0, cut));
            }
            return tags;
        }

        /// <summary>Sayfa yazımından ÖNCE çağrılır (hedef henüz bu aktarımın nesnelerini taşımaz). Döner: makbuz satırları.</summary>
        static List<object> ImportDeviceList(Context c, Dictionary<string, object> pkg, List<string> createdNames, List<object> checklist)
        {
            List<object> rows = new List<object>();
            ArrayList entries = A(pkg.ContainsKey("device_list_imports") ? pkg["device_list_imports"] : null);
            if (entries.Count == 0) return rows;
            HashSet<string> existing = ExistingTags(c.Project);
            // Seçenek A: müşterinin parça listesiyle kurduğu cihaz (defterde bizim adımız olmayan yerleşmemiş parçalı fonksiyon).
            HashSet<string> ours = new HashSet<string>();
            foreach (object e in LedgerEntries(Path.ChangeExtension(c.Project.ProjectLinkFilePath, ".uvp_ledger.json")))
                foreach (object n in A(D(e).ContainsKey("created_names") ? D(e)["created_names"] : null)) ours.Add(Convert.ToString(n));
            List<string> customer = new List<string>();
            foreach (string tag in c.CustomerParts.Keys) if (!ours.Contains(tag)) customer.Add(tag);
            foreach (object eo in entries)
            {
                Dictionary<string, object> e = D(eo);
                string tag = S(e, "device_tag"), code = S(e, "part_number");
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["device_tag"] = tag;
                r["reason_in_package"] = S(e, "reason");
                r["part_number"] = code;
                r["written"] = false;
                rows.Add(r);
                if (customer.Count > 0) { r["skipped"] = "TARGET_CUSTOMER_LIST"; continue; }   // A: müşteri kuralı yeter
                if (existing.Contains(tag)) { r["skipped"] = "TARGET_HAS_DT"; continue; }
                int mark = c.Created.Count;
                try
                {
                    string rule;
                    int logMark = c.Log.Count;
                    MDPart part = MacroDumpAction.FindPart(c.PartsDb, code, c.Log, out rule);
                    if (part == null)
                    {
                        r["reason"] = c.PartsDb == null ? "PARTS_DB_UNAVAILABLE" : c.Log.Count > logMark ? "PART_AMBIGUOUS_OR_ERROR" : "PART_NOT_IN_DB";
                        bool onPage = e.ContainsKey("on_page") && e["on_page"] is bool && (bool)e["on_page"];
                        if (!onPage)
                        {
                            // Yalnız DT: ürünsüz kara kutu fonksiyonu, sayfaya konmaz (tanım 1303/1/1 geri okumayla ölçülü).
                            BoxedDevice bb = new BoxedDevice();
                            bb.Create(c.Project, new FunctionDefinition(c.Project, FunctionCategory.Blackbox, 1, 1));
                            c.Created.Add(bb);
                            r["name"] = WriteName(bb, tag, "", tag);
                            r["dt_only"] = true;
                            if (!createdNames.Contains(tag)) createdNames.Add(tag);
                        }
                        else r["dt_on_page"] = true;
                        checklist.Add(new string[] { "Cihaz listesi", "-" + tag.Substring(tag.LastIndexOf('-') + 1),
                            "PARÇA DB'DE YOK: " + code, "Parçayı EPLAN parça DB'sine ekleyip cihaza atayın (Parça ata)" });
                        continue;
                    }
                    Function[] functions = new DeviceService().CreateDevice(c.Project, part.PartNr, part.Variant ?? "", new FunctionPropertyList());
                    if (functions == null || functions.Length == 0) throw new InvalidOperationException("Parçadan cihaz oluşmadı: " + part.PartNr);
                    Function main = null;
                    List<object> names = new List<object>();
                    int dcpRemoved = 0;
                    foreach (Function fn in functions)
                    {
                        c.Created.Add(fn);
                        // Şablonun cihaz bağlantı noktası (DCP) konmadan adlandırılamaz (ölçüm 28.09); adsız kalınca Cihaz Gezgini'nde
                        // yapısız '=+:U2' yetim cihazı olur (ölçüm 24.10 cl2: =131 -3U32 21 adet). Kaldırılır: sayfadaki DCP'ler zaten
                        // cihazın uçlarıdır, sayfada olmayan cihazda çizimci sembolü/makroyu koyar.
                        if (S(Definition(fn), "category") == "DeviceEndTerminal" && !fn.IsMainFunction)
                        {
                            try { fn.Remove(); dcpRemoved++; continue; } catch (Exception ex) { names.Add("DCP kaldırılamadı: " + ex.Message); continue; }
                        }
                        if (fn.IsMainFunction && main == null) main = fn;
                        string only = SinglePinName(fn);
                        string suffix = (fn.IsMainFunction || only == "") ? "" : ":" + only;
                        names.Add(WriteName(fn, tag, suffix, tag));
                    }
                    // Mekanik/aksesuar parçası (RIT.9342310 ölçüm 24.10): şablonda ana fonksiyon yok; parça taşıyabilen ilk fonksiyon.
                    if (main == null)
                        foreach (Function fn in functions) if (fn.CanHaveArticleData) { main = fn; r["main_fallback"] = "CAN_HAVE_ARTICLE_DATA"; break; }
                    if (main == null) throw new InvalidOperationException("Parçadan kurulan cihazda parça taşıyabilen fonksiyon yok: " + part.PartNr);
                    r["functions"] = names;
                    if (dcpRemoved > 0) r["template_dcp_removed"] = dcpRemoved;
                    Dictionary<string, object> art = new Dictionary<string, object>();
                    art["part_nr"] = part.PartNr;
                    art["match_rule"] = rule;
                    bool present = false;
                    foreach (ArticleReference have in main.ArticleReferences) if (have.PartNr == part.PartNr) present = true;
                    if (!present)
                    {
                        string variant = part.Variant ?? "";
                        if (variant == "") main.AddArticleReference(part.PartNr); else main.AddArticleReference(part.PartNr, variant, 1);
                    }
                    art["written"] = true;
                    WriteAccessories(c, main, e, art);
                    r["article"] = art;
                    r["written"] = true;
                    r["main_function"] = Text(delegate { return main.Name; });
                    if (!createdNames.Contains(tag)) createdNames.Add(tag);
                    // Yazılamayan aksesuar kodu satır listeye (ana ürün yazıldı; eksik yalnız aksesuar).
                    foreach (object ao in (art.ContainsKey("accessories") ? (List<object>)art["accessories"] : new List<object>()))
                        if (!(bool)D(ao)["written"] && S(D(ao), "reason") != "ALREADY_PRESENT")
                            checklist.Add(new string[] { "Cihaz listesi", "-" + tag.Substring(tag.LastIndexOf('-') + 1),
                                (S(D(ao), "reason") == "PART_NOT_IN_DB" ? "AKSESUAR PARÇA DB'DE YOK: " : "AKSESUAR YAZILAMADI (" + S(D(ao), "reason") + "): ")
                                + S(D(ao), "part_number"), "Aksesuarı elle ekleyin (Parça ata)" });
                    // Sayfadaki aynı DT nesneleri yardımcı olur, parça yazmaz (müşteri-cihazı yolu, 24.10).
                    c.DeviceListTags.Add(tag);
                    c.CustomerDevices.Add(tag);
                    c.CustomerParts[tag] = new HashSet<string> { part.PartNr };
                }
                catch (Exception ex)
                {
                    r["error"] = ex.GetType().Name + ": " + ex.Message;
                    r["written"] = false;
                    r["partial_removed"] = RemoveSince(c, mark, "devicelist:" + tag);
                }
            }
            int written = 0;
            foreach (object ro in rows) if ((bool)D(ro)["written"]) written++;
            c.Log.Add("cihaz listesi (seçenek B): " + entries.Count + " kayıt, " + written + " parçalı cihaz kuruldu"
                      + (customer.Count > 0 ? "; hedef müşteri listeli (A), eklenmedi" : ""));
            return rows;
        }
    }
}
