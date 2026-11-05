// =====================================================================================
// UvpPdfToP8Kontrol — çizimci kontrolü (MAIN 04.10 "Kontrollü ilerleme").
//
// Paketin sayfa öğesindeki `control_marks` (analyzer_v3/kontrol.py):
//  * `draw` satırı sayfaya DÜZ YAZI (Text) olarak konur: 'UVP: ' ön ekli, kutulu, renkli, 'UVP_KONTROL' katmanında.
//    Not/yorum nesnesi kullanılmaz; geri okumada sahipsiz yazıdır (nesne/bağ sayımına girmez).
//    Çizimci düzeltince siler; toplu seçim/gizleme katmanla, arama 'UVP:' ile.
//  * Bütün satırlar (çizilmeyen OKUNAMADI dahil) projedeki tek "UVP Kontrol Listesi" sayfasında toplanır
//    (sayfa sayacı 999, taşarsa 999.a, 999.b; ad yapısı aktarılan ilk sayfanınki; pano (+Einbauort) başına bir liste). Her aktarım listeyi yeniden kurar:
//    önceki aktarımların satırları listeden okunup korunur, yeni satırlar eklenir, sayfa adına göre sıralanır.
// Elektrik nesnesine dokunulmaz; işaret yazılamazsa nesne reddedilmez, hata makbuz satırındadır.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;

namespace Uvp.PdfToP8.Host
{
    internal static class Kontrol
    {
        internal const string LayerName = "UVP_KONTROL";
        internal const string ListTitle = "UVP Kontrol Listesi";
        internal const string ListCounter = "999";
        // EPLAN renk dizini: 1 = kırmızı (AutoCAD paleti; gerçek EPLAN PDF'inde gözle doğrulanır).
        internal const short Color = 1;
        // UVP_013 çizim alanı: output/sablon/UVP_antet_V01.json area x 42..384, y 16..292 (mm).
        static readonly double[] ColX = { 44, 76, 104, 224 };
        static readonly int[] ColChars = { 22, 19, 84, 113 };     // 1,4 mm/karakter (yükseklik 2 mm, layout ölçüsü)
        static readonly string[] Header = { "Sayfa", "Cihaz", "Eksik", "Ne yapılmalı" };
        const double TitleY = 284, HeaderY = 277, TopY = 271, RowH = 6, BottomY = 20, H = 2.0;

        static string S(Dictionary<string, object> d, string k) { return ImportAction.S(d, k); }

        static GraphicalLayer Layer(Project prj, List<string> log)
        {
            try
            {
                GraphicalLayerTable table = prj.LayerTable;
                foreach (GraphicalLayer l in table.Layers) if (l.Name == LayerName) return l;
                MultiLangString d = new MultiLangString();
                d.AddString(ISOCode.Language.L___, "UVP kontrol işaretleri (PDF2P8): düzeltince silin");
                GraphicalLayer made = table.AddLayer(LayerName, d);
                return made;
            }
            catch (Exception ex) { log.Add("UVP_KONTROL katmanı kurulamadı (yazı katmansız, renk/kutu yine var): " + ex.Message); return null; }
        }

        static Text Write(Project prj, Page page, string text, PointD at, bool box /* işaret: kırmızı + kutu */, List<StorableObject> created,
                          Dictionary<string, object> row, List<string> log)
        {
            Text t = new Text();
            t.Create(page, text, H);
            created.Add(t);
            t.Location = at;
            t.Justification = TextBase.JustificationType.BottomLeft;
            GraphicalLayer layer = Layer(prj, log);
            // Katman ÖNCE: renk/kutu katmandan gelirse yazının kendi değeri onu ezer.
            if (layer != null) try { t.Layer = layer; } catch (Exception ex) { row["layer_error"] = ex.Message; }
            if (box)
            {
                try { t.TextColorId = Color; } catch (Exception ex) { row["color_error"] = ex.Message; }
                try { t.DrawTextBoxType = TextBase.TextBoxType.Rectangle; t.HasBox = true; }
                catch (Exception ex) { row["box_error"] = ex.Message; }
            }
            row["object_id"] = ImportAction.Id(t);
            row["color_read"] = ImportAction.Text(delegate { return t.TextColorId.ToString(); });
            row["layer_read"] = ImportAction.Text(delegate { return t.Layer == null ? "" : t.Layer.Name; });
            return t;
        }

        /// <summary>Sayfanın işaretleri. `when_article_missing` satırı, adı geçen nesnelerden birine EPLAN parçası yazıldıysa
        /// (makbuz satırı article.written) konmaz ve listeye girmez. Döner: listeye girecek satırlar.</summary>
        internal static List<object> PlaceMarks(Project prj, Page page, Dictionary<string, object> pd, string sub,
                                                List<object> created, List<StorableObject> createdObjects,
                                                List<object> placedLog, List<string> log)
        {
            List<object> rows = new List<object>();
            foreach (object mo in ImportAction.A(pd.ContainsKey("control_marks") ? pd["control_marks"] : null))
            {
                Dictionary<string, object> m = ImportAction.D(mo);
                if (S(m, "subpage") != sub) continue;
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["kind"] = S(m, "kind");
                r["text"] = S(m, "text");
                r["device"] = S(m, "device");
                r["page"] = ImportAction.Text(delegate { return page.Name; });
                ArrayList ids = ImportAction.A(m.ContainsKey("when_article_missing") ? m["when_article_missing"] : null);
                // 24.10 (B2): müşteri cihazının kodu PDF kodundan farklı; müşteri kodu kaldı, çizimci karar verir. Önce bakılır:
                // aynı cihazın kontağı CUSTOMER_DEVICE_PART taşır, ArticleWritten onu "yazıldı" sayıp işareti atlıyordu (ölçüm E3 R2).
                string differs = ids.Count > 0 ? CustomerDiffers(created, ids) : "";
                if (differs != "")
                {
                    m = new Dictionary<string, object>(m);
                    m["text"] = "UVP: MÜŞTERİ KODU FARKLI " + S(m, "device") + ": " + differs;
                    m["label"] = "MÜŞTERİ KODU FARKLI";
                    m["detail"] = differs;
                    m["action"] = "Müşteri projesindeki kod kaldı; doğru kodu seçin (Parça ata)";
                    r["text"] = S(m, "text");
                }
                else if (ids.Count > 0 && ArticleWritten(created, ids)) { r["skipped"] = "ARTICLE_WRITTEN"; placedLog.Add(r); continue; }
                bool draw = m.ContainsKey("draw") && m["draw"] is bool && (bool)m["draw"] && m.ContainsKey("at_mm") && m["at_mm"] != null;
                if (draw)
                {
                    try { Write(prj, page, S(m, "text"), ImportAction.P(m["at_mm"]), true, createdObjects, r, log); }
                    catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; }
                }
                else r["drawn"] = false;
                placedLog.Add(r);
                string detail = S(m, "detail");
                rows.Add(new string[] { r["page"].ToString(), S(m, "device"), S(m, "label") + (detail == "" ? "" : ": " + detail),
                                        S(m, "action") });
            }
            return rows;
        }

        static bool ArticleWritten(List<object> created, ArrayList ids)
        {
            foreach (object ro in created)
            {
                Dictionary<string, object> r = ImportAction.D(ro);
                if (!ids.Contains(S(r, "id")) || !r.ContainsKey("article")) continue;
                Dictionary<string, object> art = ImportAction.D(r["article"]);
                if (art.ContainsKey("written") && art["written"] is bool && (bool)art["written"]) return true;
                // 24.10: parça müşterinin parça listesi cihazında zaten var (yazılmaması kural; eksik değil).
                if (S(art, "reason") == "CUSTOMER_DEVICE_PART") return true;
                // Fonksiyon parça taşımıyor (yardımcı: kontaktör kontağı) ve kod EPLAN parça DB'de VAR: eksik bu sayfanın değil,
                // ana fonksiyonun (bobin) sayfasınındır; orada yazılmazsa o sayfa işaretlenir (04.10 BELIRSIZ2, s10 -11K55).
                if (S(art, "reason") == "NOT_MAIN_FUNCTION" && art.ContainsKey("part_in_db") && art["part_in_db"] is bool
                    && (bool)art["part_in_db"]) return true;
            }
            return false;
        }

        /// <summary>24.10: `when_article_missing` nesnelerinden biri müşteri cihazında farklı kodla duruyorsa açıklama, yoksa "".</summary>
        static string CustomerDiffers(List<object> created, ArrayList ids)
        {
            foreach (object ro in created)
            {
                Dictionary<string, object> r = ImportAction.D(ro);
                if (!ids.Contains(S(r, "id")) || !r.ContainsKey("article")) continue;
                Dictionary<string, object> art = ImportAction.D(r["article"]);
                if (S(art, "reason") != "CUSTOMER_DEVICE_PART_DIFFERS") continue;
                return "PDF " + S(art, "part_nr") + (art.ContainsKey("sibling_device") ? ", önceki UVP aktarımı " : ", müşteri projesi ")
                    + string.Join(", ", (string[])art["customer_parts"]);
            }
            return "";
        }

        static bool IsListPage(Page p)
        {
            string counter = ImportAction.Text(delegate { return p.Properties.PAGE_COUNTER.ToString(); });
            string desc = ImportAction.Text(delegate { return p.Properties.PAGE_NOMINATIOMN.ToString(); });
            return (counter == ListCounter || counter.StartsWith(ListCounter + ".")) && desc.Contains(ListTitle);
        }

        static string Cut(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        /// <summary>Sayfa adı doğal sırası: yapı, sonra sayfa numarası, sonra alt sayfa harfi.</summary>
        static int ComparePages(string[] a, string[] b)
        {
            Func<string, string[]> key = delegate(string name)
            {
                int slash = name.LastIndexOf('/');
                string head = slash < 0 ? "" : name.Substring(0, slash), tail = slash < 0 ? name : name.Substring(slash + 1);
                int n = 0;
                while (n < tail.Length && char.IsDigit(tail[n])) n++;
                return new string[] { head, (n == 0 ? "" : tail.Substring(0, n)).PadLeft(9, '0'), tail.Substring(n) };
            };
            string[] ka = key(a[0]), kb = key(b[0]);
            for (int i = 0; i < 3; i++) { int c = string.CompareOrdinal(ka[i], kb[i]); if (c != 0) return c; }
            return 0;
        }

        /// <summary>Kontrol listesini yeniden kurar (eski liste satırları + yeni satırlar). Döner: makbuz kaydı.</summary>
        internal static Dictionary<string, object> WriteList(Project prj, List<object> fresh, string anlage, string einbauort,
                                                            string doctype, List<StorableObject> createdObjects, List<string> log)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            List<string[]> rows = new List<string[]>();
            List<Page> old = new List<Page>();
            // Çok panolu belge (MAIN 04.10 akşam): her pano (+Einbauort) kendi listesini tutar; başka panonun listesi kalır.
            // Ad '=280+E280&EFS1/999' (09.10 doküman tipi) ya da eski '=280+E280/999': ikisi de bu panonun listesidir.
            foreach (Page p in prj.Pages)
            {
                string name = ImportAction.Text(delegate { return p.Name; });
                if (IsListPage(p) && (name.Contains("+" + einbauort + "/") || name.Contains("+" + einbauort + "&"))) old.Add(p);
            }
            foreach (Page p in old)
            {
                SortedDictionary<double, string[]> byY = new SortedDictionary<double, string[]>();
                foreach (Placement pl in p.AllPlacements)
                {
                    Text t = pl as Text;
                    if (t == null) continue;
                    string layer = ImportAction.Text(delegate { return t.Layer == null ? "" : t.Layer.Name; });
                    if (layer != LayerName) continue;
                    PointD at = t.Location;
                    if (at.Y > TopY + 1) continue;                                   // başlık ve sütun adları
                    double y = -Math.Round(at.Y, 1);                                  // yukarıdan aşağı
                    int col = 0;
                    for (int i = 0; i < ColX.Length; i++) if (at.X >= ColX[i] - 0.5) col = i;
                    if (!byY.ContainsKey(y)) byY[y] = new string[] { "", "", "", "" };
                    // GetAsString çok dilli biçimi verir ('??_??@metin;'); yazı dilsiz (L___) kondu, düz metin o dilden okunur.
                    byY[y][col] = ImportAction.Text(delegate { return t.Contents.GetString(ISOCode.Language.L___); });
                }
                rows.AddRange(byY.Values);
            }
            result["kept_rows"] = rows.Count;
            foreach (object o in fresh) rows.Add((string[])o);
            result["new_rows"] = fresh.Count;
            // Kararlı sıralama (List.Sort kararsız): eşit sayfada ekleme sırası korunur.
            List<KeyValuePair<int, string[]>> indexed = new List<KeyValuePair<int, string[]>>();
            for (int i = 0; i < rows.Count; i++) indexed.Add(new KeyValuePair<int, string[]>(i, rows[i]));
            indexed.Sort(delegate(KeyValuePair<int, string[]> a, KeyValuePair<int, string[]> b)
            {
                int c = ComparePages(a.Value, b.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            foreach (Page p in old) { try { p.Remove(); } catch (Exception ex) { log.Add("eski kontrol listesi sayfası silinemedi: " + ex.Message); } }
            result["removed_pages"] = old.Count;
            List<string> names = new List<string>();
            int perPage = (int)Math.Floor((TopY - BottomY) / RowH) + 1;
            for (int start = 0, k = 0; start == 0 || start < indexed.Count; start += perPage, k++)
            {
                PagePropertyList pp = new PagePropertyList();
                pp.DESIGNATION_PLANT = anlage;
                pp.DESIGNATION_LOCATION = einbauort;
                if (!string.IsNullOrEmpty(doctype)) pp.DESIGNATION_DOCTYPE = doctype;
                pp.PAGE_COUNTER = ListCounter + (k == 0 ? "" : "." + (char)('a' + k - 1));
                Page pg = new Page();
                try { pg.Create(prj, DocumentTypeManager.DocumentType.Graphics, pp); }
                catch (Exception ex) { log.Add("kontrol listesi grafik sayfası kurulamadı, şema sayfası: " + ex.Message); pg = new Page(); pg.Create(prj, DocumentTypeManager.DocumentType.Circuit, pp); }
                createdObjects.Add(pg);
                try
                {
                    MultiLangString d = new MultiLangString();
                    d.AddString(ISOCode.Language.L___, ListTitle);
                    pg.Properties.PAGE_NOMINATIOMN = d;
                }
                catch (Exception ex) { log.Add("kontrol listesi sayfa açıklaması yazılamadı: " + ex.Message); }
                names.Add(ImportAction.Text(delegate { return pg.Name; }));
                Dictionary<string, object> sink = new Dictionary<string, object>();
                Write(prj, pg, ListTitle + " — programın tamamlayamadığı işler (" + indexed.Count + " satır). Sayfadaki "
                      + "işaretler 'UVP:' ön ekli, katman " + LayerName + "; düzeltince işareti ve satırı silin.",
                      new PointD(ColX[0], TitleY), false, createdObjects, sink, log);
                for (int i = 0; i < Header.Length; i++) Write(prj, pg, Header[i], new PointD(ColX[i], HeaderY), false, createdObjects, sink, log);
                for (int i = start; i < Math.Min(indexed.Count, start + perPage); i++)
                {
                    string[] row = indexed[i].Value;
                    double y = TopY - (i - start) * RowH;
                    for (int col = 0; col < 4; col++)
                        if (row[col] != "") Write(prj, pg, Cut(row[col], ColChars[col]), new PointD(ColX[col], y), false, createdObjects, sink, log);
                }
            }
            result["pages"] = names;
            result["rows"] = indexed.Count;
            return result;
        }
    }
}
