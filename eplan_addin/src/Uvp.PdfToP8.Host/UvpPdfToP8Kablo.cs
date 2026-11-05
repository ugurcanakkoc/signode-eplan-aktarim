// K28 (11.10): çok damarlı kablo → EPLAN kablo tanım çizgisi (SPECIAL/CABDL "Kabeldefinitionslinie").
//
// Paket sayfası `cables` (analyzer_v3/kablo.py, kind=CABLE): ad, tam ad (=Anlage+Ort-Ad), tip, damar sayısı, kesit,
// hedef çizgi start_mm/end_mm. Cable.Create(page, CABDL, start, end): API belgesi "kablo ile tellerin kesişimlerinde
// bağlantı tanım noktası (CDP) oluşturulur" der; damar ataması bu yolla EPLAN'ındır, geri okumayla ölçülür (Read).
// Kablo hatası sayfayı PARTIAL yapmaz (kablo ek bilgidir, elektrik bağı değil); satırı makbuz `cables`ta, nedeniyle.
// Yok: kalkan (SPECIAL/SH), damar renkleri (CDP).
// 12.10: parça — paket `part_number` (UVP çoklu kablo tablosu, kablo_secici.py) parça DB'de TEK parçaya çözülürse
// AddArticleReference (WritePart; çizimci aracı UvpPdfToP8KabloSecici.cs de aynısını kullanır). MAIN.md:465: çok damarlı
// kablonun damar kesiti BAĞA yazılmaz → kabloya kesit yazılmaz (ölçüm: damar bağı kablonun kesitini alıyor).

using System;
using System.Collections.Generic;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.EObjects;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    internal static class Kablo
    {
        static string S(Dictionary<string, object> d, string k) { return ImportAction.S(d, k); }

        /// <summary>Sayfanın (alt sayfa `sub`) kablolarını koyar; satırlar receipt["cables"]a eklenir.</summary>
        internal static void Place(Project prj, Page pg, Dictionary<string, object> pd, string sub,
                                   List<StorableObject> created, Dictionary<string, object> receipt,
                                   MDPartsDatabase db, List<string> log)
        {
            if (!pd.ContainsKey("cables")) return;
            List<object> rows = receipt.ContainsKey("cables") ? (List<object>)receipt["cables"] : new List<object>();
            receipt["cables"] = rows;
            foreach (object co in ImportAction.A(pd["cables"]))
            {
                Dictionary<string, object> o = ImportAction.D(co);
                if (S(o, "subpage") != sub) continue;
                Dictionary<string, object> row = new Dictionary<string, object>();
                row["id"] = S(o, "id");
                row["device_tag"] = S(o, "device_tag");
                row["eplan_page"] = ImportAction.Text(delegate { return pg.Name; });
                try
                {
                    SymbolVariant sv = ImportAction.SymbolOf(prj, "SPECIAL", "CABDL")[0];
                    HashSet<string> before = new HashSet<string>();
                    foreach (ConnectionDefinitionPoint d in Cdps(pg)) before.Add(ImportAction.Id(d));
                    Cable cab = new Cable();
                    cab.Create(pg, sv, Pt(o, "start_mm"), Pt(o, "end_mm"));
                    created.Add(cab);
                    row["name_write"] = ImportAction.WriteName(cab, S(o, "device_tag"), "", S(o, "name"));
                    // ÖLÇÜM 12.10 (Host_KS): visible_name '' iken CABDL DT yazısı (20011) boş basılıyordu — oktaki ölçümün aynısı
                    // (Import 10.10 Host_duzad). Görünen ad = paketteki kısa ad ('-28W32').
                    try { cab.VisibleName = S(o, "name"); } catch (Exception ex) { row["visible_name_error"] = ex.Message; }
                    // ÖLÇÜM K28 #1: Create kesişimlere CDP koyar ama ad boştur ('=+'); kablo adı sonradan yazılınca CDP'ye
                    // geçmez, damar kabloya bağlanmaz (CableConnections 0). Damar = kablo adını taşıyan CDP: ad yazılır.
                    List<object> cores = new List<object>();
                    foreach (ConnectionDefinitionPoint d in Cdps(pg))
                    {
                        if (before.Contains(ImportAction.Id(d))) continue;
                        created.Add(d);
                        cores.Add(ImportAction.WriteName(d, S(o, "device_tag"), "", S(o, "name")));
                    }
                    row["cdp_created"] = cores.Count;
                    row["cdp_name_write"] = cores;
                    if (S(o, "type") != "") cab.Properties.FUNC_CABLETYPE = S(o, "type");
                    if (S(o, "wire_count") != "") cab.Properties.FUNC_CABLEWIRECOUNT = S(o, "wire_count");
                    // MAIN.md:465 — damar kesiti bağa yazılmaz. ÖLÇÜM 12.10 (Host_KS, s32): kabloya kesit yazılınca kendi kesiti
                    // olmayan damar bağları EPLAN'da kablonun kesitini alıyor (8/8 '0,5'; CDP kesiti boş). Kablo kesiti YAZILMAZ;
                    // kesit bilgisi parçada (LAPP.…) ve paket `cross_section`ta kalır.
                    if (S(o, "cross_section") != "") row["cross_section_not_written"] = S(o, "cross_section");
                    if (S(o, "part_number") != "") row["article"] = WritePart(cab, S(o, "part_number"), db, log);
                    row["name"] = ImportAction.Text(delegate { return cab.Name; });
                    row["placed"] = true;
                }
                catch (Exception ex) { row["placed"] = false; row["error"] = ex.GetType().Name + ": " + ex.Message; }
                rows.Add(row);
            }
        }

        /// <summary>Geri okuma: aktarılan sayfalardaki kablolar, EPLAN'ın atadığı damarlar (Cable.CableConnections).</summary>
        internal static void Read(List<Page> pages, Dictionary<string, object> readback)
        {
            List<object> rows = new List<object>();
            foreach (Page pg in pages)
            {
                Function[] fs = null;
                try { fs = pg.Functions; } catch (Exception) { }
                if (fs == null) continue;
                foreach (Function f in fs)
                {
                    Cable cab = f as Cable;
                    if (cab == null) continue;
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["object_id"] = ImportAction.Id(cab);
                    r["page"] = ImportAction.Text(delegate { return pg.Name; });
                    r["name"] = ImportAction.Text(delegate { return cab.Name; });
                    r["type"] = ImportAction.Text(delegate { return cab.Properties.FUNC_CABLETYPE.ToString(); });
                    r["wire_count"] = ImportAction.Text(delegate { return cab.Properties.FUNC_CABLEWIRECOUNT.ToString(); });
                    r["cross_section"] = ImportAction.Text(delegate { return cab.Properties.FUNC_CABLEWIRECROSSSECTION.ToString(); });
                    r["count_used_wires"] = ImportAction.Text(delegate { return cab.Properties.CABLE_COUNTOFUSEDWIRES.ToString(); });
                    r["count_all_wires"] = ImportAction.Text(delegate { return cab.Properties.CABLE_COUNTOFALLWIRES.ToString(); });
                    r["visible_name"] = ImportAction.Text(delegate { return cab.VisibleName; });
                    r["article"] = ImportAction.Text(delegate { string a = ""; foreach (ArticleReference ar in cab.ArticleReferences) a += (a == "" ? "" : ",") + ar.PartNr; return a; });
                    List<object> cdpSections = new List<object>();
                    foreach (ConnectionDefinitionPoint d in Cdps(pg))
                        if (ImportAction.Text(delegate { return d.Name; }) == r["name"].ToString())
                            cdpSections.Add(ImportAction.Text(delegate { return d.Properties.CONNECTION_WIRECROSSSECTION.ToString(); }));
                    r["cdp_cross_sections"] = cdpSections;
                    try { r["location"] = ImportAction.XY(cab.Location); } catch (Exception) { }
                    int named = 0;
                    foreach (ConnectionDefinitionPoint d in Cdps(pg))
                        if (ImportAction.Text(delegate { return d.Name; }) == r["name"].ToString()) named++;
                    r["cdp_with_cable_name"] = named;
                    List<object> texts = new List<object>();      // gösterilen özellikler (DT görünüyor mu)
                    try
                    {
                        foreach (Placement pl in cab.PropertyPlacements)
                        {
                            PropertyPlacement pp = pl as PropertyPlacement;
                            if (pp == null) continue;
                            texts.Add(ImportAction.Text(delegate { return pp.DisplayedProperty.AsInt.ToString() + "/" + pp.DisplayedProperty.AsString; }) + "="
                                      + ImportAction.Text(delegate { return pp.GetDisplayString(); }));
                        }
                    }
                    catch (Exception ex) { texts.Add("HATA " + ex.Message); }
                    r["texts"] = texts;
                    List<object> cores = new List<object>();
                    try
                    {
                        foreach (Connection k in cab.CableConnections)
                        {
                            Dictionary<string, object> x = new Dictionary<string, object>();
                            x["object_id"] = ImportAction.Id(k);
                            x["start"] = End(k.StartPin);
                            x["end"] = End(k.EndPin);
                            x["cross_section"] = ImportAction.Text(delegate { return k.Properties.CONNECTION_WIRECROSSSECTION.ToString(); });
                            cores.Add(x);
                        }
                    }
                    catch (Exception ex) { r["cores_error"] = ex.GetType().Name + ": " + ex.Message; }
                    r["cores"] = cores;
                    rows.Add(r);
                }
            }
            readback["cables"] = rows;
        }

        /// <summary>UVP standardı kodunu parça DB'de arar ('LAPP.'+kod parça no., sonra kod tip no.); TEK parça bulunursa
        /// kabloya parça referansı yazar. Bulunmazsa YAZMAZ (uydurma yok), neden döner. Kablo tipi/damar/kesit çağıranın.</summary>
        internal static Dictionary<string, object> WritePart(Cable cab, string code, MDPartsDatabase db, List<string> log)
        {
            Dictionary<string, object> art = new Dictionary<string, object>();
            art["part_number"] = code;
            art["written"] = false;
            try
            {
                string rule = "";
                MDPart part = null;
                foreach (string spelling in new string[] { "LAPP." + code, code })
                {
                    part = MacroDumpAction.FindPart(db, spelling, log, out rule);
                    if (part != null) break;
                }
                if (part == null) { art["reason"] = db == null ? "PARTS_DB_UNAVAILABLE" : "PART_NOT_IN_DB"; return art; }
                art["part_nr"] = part.PartNr;
                art["match_rule"] = rule;
                foreach (ArticleReference have in cab.ArticleReferences)
                    if (have.PartNr == part.PartNr) { art["reason"] = "ALREADY_PRESENT"; return art; }
                string variant = part.Variant ?? "";
                if (variant == "") cab.AddArticleReference(part.PartNr);
                else cab.AddArticleReference(part.PartNr, variant, 1);
                art["written"] = true;
            }
            catch (Exception ex) { art["reason"] = "ERROR"; art["error"] = ex.GetType().Name + ": " + ex.Message; }
            return art;
        }

        static List<ConnectionDefinitionPoint> Cdps(Page pg)
        {
            List<ConnectionDefinitionPoint> list = new List<ConnectionDefinitionPoint>();
            foreach (Function f in pg.Functions) { ConnectionDefinitionPoint d = f as ConnectionDefinitionPoint; if (d != null) list.Add(d); }
            return list;
        }

        static string End(Pin p)
        {
            return ImportAction.Text(delegate { return p == null ? "" : p.ParentFunction.Name + ":" + p.Name; });
        }

        static PointD Pt(Dictionary<string, object> o, string key)
        {
            System.Collections.ArrayList v = ImportAction.A(o[key]);
            return new PointD(ImportAction.N(v[0]), ImportAction.N(v[1]));
        }
    }
}
