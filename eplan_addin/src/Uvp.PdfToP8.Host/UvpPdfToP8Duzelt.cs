// =====================================================================================
// UvpPdfToP8Duzelt — müşteri EPLAN yedeğinin TEST KOPYASINDA planlı düzeltme (26SB003, kullanıcı 05.11).
//
//   UvpPdfToP8Duzelt /PROJECT:"<output\p8runs\...\UVP_*.elk>" /PLAN:"<plan.json>" /OUT:"<rapor.json>"
//
// Plan: { "pins":  [ { "function_id", "old", "new" } ],
//         "wires": [ { "connection_id", "old_color", "color", "old_cross_section", "cross_section", "wire_type", "wire_code" } ] }
// Uç: fonksiyonun ucu adla bulunur, FUNC_CONNECTIONDESIGNATION[uç sırası + 1] yazılır, ad geri okunur.
// Tel: bağlantının tanım noktası (CDP) varsa değer CDP'ye yazılır (bağlantı yeniden üretilince CDP kazanır), yoksa bağlantıya.
// Tel kodu: parça DB'de LAPP.<kod> bulunursa parça referansı olarak eklenir; aynı parça varsa eklenmez.
// Değeri plandaki eski değerde olmayan satır yazılmaz (STALE). Yalnız Guard.TestProject kopyası; canlı projeye yazmaz.
// Tek UndoStep; hata olursa Yazim geri alır. Rapor satır başına eski/yeni değer taşır.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    public class DuzeltAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Duzelt";
            Ordinal = 28;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static string T(Func<string> f) { return ImportAction.Text(f); }

        /// <summary>Kimliğin ilk parçası EPLAN oturumuna göre değişir (108/18/46825/0, 150/18/46825/0). Oturumlar arası yalnız son 3 parça karşılaştırılır.</summary>
        static string Son(string id)
        {
            if (id == null) return "";
            string[] p = id.Split('/');
            return p.Length == 4 ? p[1] + "/" + p[2] + "/" + p[3] : id;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.duzelt.rapor";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["writes_live_project"] = false;
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                ProjectManager pm = Arac.Pm(ctx);
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                report["project"] = prj.ProjectLinkFilePath;
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> plan = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(Io.Param(ctx, "PLAN")));

                DMObjectsFinder finder = new DMObjectsFinder(prj);
                Dictionary<string, Function> fns = new Dictionary<string, Function>();
                foreach (Function f in finder.GetFunctions(null)) fns[Son(ImportAction.Id(f))] = f;
                Dictionary<string, Connection> cons = new Dictionary<string, Connection>();
                foreach (Connection c in finder.GetConnections(new ConnectionsFilter())) cons[Son(ImportAction.Id(c))] = c;
                // Filtresiz sorgu bazı bağlantıları vermez (26SB002: GND-XD41:PE, 3 bağ). Geri okuma sayfa filtresiyle de okur; burada da aynısı.
#pragma warning disable 618
                foreach (Page pg in prj.Pages)
                {
                    ConnectionsFilter sf = new ConnectionsFilter();
                    sf.Page = pg;
                    foreach (Connection c in finder.GetConnections(sf)) cons[Son(ImportAction.Id(c))] = c;
                }
#pragma warning restore 618
                MDPartsDatabase db = null;
                List<string> log = new List<string>();
                try { db = new MDPartsManagement().OpenDatabase(); } catch (Exception ex) { log.Add("parça veritabanı açılamadı: " + ex.Message); }

                List<object> pinRows = new List<object>(), wireRows = new List<object>(), partRows = new List<object>();
                Dictionary<string, string> partOf = new Dictionary<string, string>();
                using (Yazim y = new Yazim(prj, "UVP planlı düzeltme"))
                {
                    foreach (object o in ImportAction.A(plan.ContainsKey("pins") ? plan["pins"] : null))
                        pinRows.Add(WritePin(fns, ImportAction.D(o)));
                    foreach (object o in ImportAction.A(plan.ContainsKey("wires") ? plan["wires"] : null))
                        wireRows.Add(WriteWire(cons, ImportAction.D(o), db, partOf, log));
                    foreach (object o in ImportAction.A(plan.ContainsKey("parts") ? plan["parts"] : null))
                        partRows.Add(WritePart(fns, ImportAction.D(o), db, log));
                    y.Tamam();
                }
                report["pins"] = pinRows;
                report["wires"] = wireRows;
                report["parts"] = partRows;
                report["log"] = log;
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Io.WriteJson(outFile, report);
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }

        static Dictionary<string, object> WritePin(Dictionary<string, Function> fns, Dictionary<string, object> row)
        {
            string id = ImportAction.S(row, "function_id"), old = ImportAction.S(row, "old"), yeni = ImportAction.S(row, "new");
            Dictionary<string, object> r = new Dictionary<string, object> { { "function_id", id }, { "old", old }, { "new", yeni } };
            Function f;
            if (!fns.TryGetValue(Son(id), out f)) { r["status"] = "FUNCTION_NOT_FOUND"; return r; }
            r["function"] = T(delegate { return f.Name; });
            Pin[] pins = f.Pins;
            int at = -1;
            for (int i = 0; i < pins.Length; i++)
            {
                Pin p = pins[i];
                if (T(delegate { return p.Name; }) == old) { at = i; break; }
            }
            if (at < 0) { r["status"] = "STALE_PIN_NOT_FOUND"; return r; }
            try
            {
                AnyPropertyId a = f.Properties.FUNC_CONNECTIONDESIGNATION.Id;
                a.Index = (short)(at + 1);
                f.Properties[a] = yeni;
                Pin q = f.Pins[at];
                string now = T(delegate { return q.Name; });
                r["readback"] = now;
                r["status"] = now == yeni ? "WRITTEN" : "READBACK_DIFFERS";
            }
            catch (Exception ex) { r["status"] = "ERROR"; r["error"] = ex.GetType().Name + ": " + ex.Message; }
            return r;
        }

        static bool UrunReddi(System.Action ekle)
        {
            try { ekle(); return false; }
            catch (Exception ex) { if (ex.GetType().Name != "CannotAddArticleException") throw; return true; }
        }

        static Dictionary<string, object> WriteWire(Dictionary<string, Connection> cons, Dictionary<string, object> row, MDPartsDatabase db,
                                                    Dictionary<string, string> partOf, List<string> log)
        {
            string id = ImportAction.S(row, "connection_id");
            Dictionary<string, object> r = new Dictionary<string, object> { { "connection_id", id } };
            Connection k;
            if (!cons.TryGetValue(Son(id), out k)) { r["status"] = "CONNECTION_NOT_FOUND"; return r; }
            string oldColor = T(delegate { return k.Properties.CONNECTION_WIRENUMBER.ToString(); });
            string oldCs = T(delegate { return k.Properties.CONNECTION_WIRECROSSSECTION.ToString(); });
            r["old_color"] = oldColor;
            r["old_cross_section"] = oldCs;
            if (oldColor != ImportAction.S(row, "old_color") || oldCs != ImportAction.S(row, "old_cross_section")) { r["status"] = "STALE"; return r; }
            try
            {
                ConnectionDefinitionPoint[] cps = null;
                try { cps = k.ConnectionDefPoints; } catch (Exception) { }
                int n = cps == null ? 0 : cps.Length;
                r["cdp_count"] = n;
                // Kablo tanım noktası: kablo damarıdır, tek tel kodu/rengi yazılmaz (kullanıcı 05.11: çok damarlı kabloya müdahale yok).
                if (n > 0)
                    foreach (ConnectionDefinitionPoint cp in cps)
                    {
                        string ad = T(delegate { return cp.Name; });
                        if (System.Text.RegularExpressions.Regex.IsMatch(ad, @"-\d+W[A-Z]|-W[DG]\d")) { r["status"] = "SKIPPED_CABLE_CDP"; r["cdp"] = ad; return r; }
                    }
                string color = ImportAction.S(row, "color"), cs = ImportAction.S(row, "cross_section"), kind = ImportAction.S(row, "wire_type");
                string part = FindPart(ImportAction.S(row, "wire_code"), db, partOf, log);
                bool reddedildi = false;
                if (n > 0)
                    foreach (ConnectionDefinitionPoint cp in cps)
                    {
                        // EPLAN bazı bağa ürün eklemez (S063074). Renk ve kesit yine yazılır; satır ayrı durumla raporlanır.
                        if (part != "")
                        {
                            bool present = false;
                            foreach (ArticleReference have in cp.ArticleReferences) if (have.PartNr == part) present = true;
                            if (!present) reddedildi |= UrunReddi(delegate { cp.AddArticleReference(part); });
                        }
                        if (color != "") cp.Properties.CONNECTION_WIRENUMBER = color;
                        if (kind != "") cp.Properties.CONNECTION_WIRETYPE = kind;
                        if (cs != "") { cp.Properties.CONNECTION_WIRECROSSSECTION = cs; cp.Properties.CONNECTION_WIRECROSSSECTION_UNIT = 1; }
                    }
                else
                {
                    // EPLAN bazı bağa ürün eklemez (S063074). Renk ve kesit yine yazılır; satır ayrı durumla raporlanır.
                    if (part != "")
                    {
                        bool present = false;
                        foreach (ArticleReference have in k.ArticleReferences) if (have.PartNr == part) present = true;
                        if (!present) reddedildi |= UrunReddi(delegate { k.AddArticleReference(part); });
                    }
                    if (color != "") k.Properties.CONNECTION_WIRENUMBER = color;
                    if (kind != "") k.Properties.CONNECTION_WIRETYPE = kind;
                    if (cs != "") { k.Properties.CONNECTION_WIRECROSSSECTION = cs; k.Properties.CONNECTION_WIRECROSSSECTION_UNIT = 1; }
                }
                r["part"] = part;
                r["status"] = reddedildi ? "WRITTEN_ARTICLE_REFUSED" : ImportAction.S(row, "wire_code") != "" && part == "" ? "WRITTEN_PART_NOT_IN_DB" : "WRITTEN";
            }
            catch (Exception ex) { r["status"] = "ERROR"; r["error"] = ex.GetType().Name + ": " + ex.Message; }
            return r;
        }

        /// <summary>Fonksiyondaki parça referansını plandaki yeni parça ile değiştirir. Yeni parça DB'de birebir yoksa yazılmaz.
        /// Sıra ve adet korunur.</summary>
        static Dictionary<string, object> WritePart(Dictionary<string, Function> fns, Dictionary<string, object> row, MDPartsDatabase db, List<string> log)
        {
            string id = ImportAction.S(row, "function_id"), eski = ImportAction.S(row, "old"), yeni = ImportAction.S(row, "new");
            Dictionary<string, object> r = new Dictionary<string, object> { { "function_id", id }, { "old", eski }, { "new", yeni } };
            Function f;
            if (!fns.TryGetValue(Son(id), out f)) { r["status"] = "FUNCTION_NOT_FOUND"; return r; }
            r["function"] = T(delegate { return f.Name; });
            try
            {
                string rule;
                MDPart mp = db == null ? null : MacroDumpAction.FindPart(db, yeni, log, out rule);
                if (mp == null || !string.Equals(mp.PartNr, yeni, StringComparison.OrdinalIgnoreCase)) { r["status"] = "NEW_PART_NOT_IN_DB"; return r; }
                ArticleReference hedef = null;
                List<ArticleReference> sonraki = new List<ArticleReference>();
                foreach (ArticleReference a in f.ArticleReferences)
                {
                    if (hedef != null) sonraki.Add(a);
                    else if (a.PartNr == eski) hedef = a;
                }
                if (hedef == null) { r["status"] = "STALE_OLD_PART_NOT_FOUND"; return r; }
                int adet = hedef.Count;
                // Sıra korunur: sonraki referanslar çıkarılıp yeninin ardından aynı sırayla eklenir (denetim: 17/79495/0).
                List<object[]> geri = new List<object[]>();
                foreach (ArticleReference a in sonraki) geri.Add(new object[] { a.PartNr, a.VariantNr, a.Count });
                foreach (ArticleReference a in sonraki) f.RemoveArticleReference(a);
                f.RemoveArticleReference(hedef);
                string v = ImportAction.S(row, "variant") != "" ? ImportAction.S(row, "variant") : (mp.Variant ?? "");
                if (v == "") f.AddArticleReference(mp.PartNr); else f.AddArticleReference(mp.PartNr, v, (uint)Math.Max(1, adet));
                foreach (object[] g in geri) f.AddArticleReference((string)g[0], (string)g[1], (uint)Math.Max(1, (int)g[2]));
                r["status"] = "WRITTEN";
            }
            catch (Exception ex) { r["status"] = "ERROR"; r["error"] = ex.GetType().Name + ": " + ex.Message; }
            return r;
        }

        static string FindPart(string code, MDPartsDatabase db, Dictionary<string, string> partOf, List<string> log)
        {
            if (code == "") return "";
            string found;
            if (partOf.TryGetValue(code, out found)) return found;
            found = "";
            foreach (string spelling in new string[] { "LAPP." + code, code })
            {
                string rule;
                MDPart mp = db == null ? null : MacroDumpAction.FindPart(db, spelling, log, out rule);
                if (mp != null) { found = mp.PartNr; break; }
            }
            partOf[code] = found;
            return found;
        }
    }

    /// <summary>UvpPdfToP8Yedek /PROJECT:"output\p8runs\...\UVP_x.elk" /OUT:"rapor.json". Test kopyasının EPLAN yedeği (.zw1).</summary>
    public class YedekAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Yedek";
            Ordinal = 28;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.yedek.rapor";
            string outFile = Io.Param(ctx, "OUT");
            Project prj = null;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                prj = Arac.Pm(ctx).OpenProject(file);
                report["yedek"] = Guard.Backup(prj);
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Io.WriteJson(outFile, report);
                if (prj != null) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }
    }

    /// <summary>UvpPdfToP8GeriYukle /ARCHIVE:"x.zw1" /PROJECT:"output\p8runs\...\UVP_x.elk" /OUT:"rapor.json".
    /// Müşteri yedeğini EPLAN'ın kendi restore eylemiyle test kopyasına açar (py7zr ve bsdtar bu dosyada eksik açıyor).
    /// Yalnız yeni hedefe yazar: restore, false döndüğü durumda bile hedefin üzerine yazıp ilgisiz dosyaları silebilir (pitfalls.md);
    /// bu yüzden hedef varsa reddedilir ve eylem tekrar edilmez. Sonuç dönüş değerinden değil, dosyadan okunur.</summary>
    public class GeriYukleAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8GeriYukle";
            Ordinal = 28;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.geriyukle.rapor";
            report["writes_live_project"] = false;
            try
            {
                string archive = Path.GetFullPath(Io.Param(ctx, "ARCHIVE"));
                string target = Path.GetFullPath(Io.Param(ctx, "PROJECT"));
                if (target.IndexOf("\\output\\p8runs\\", StringComparison.OrdinalIgnoreCase) < 0
                    || !Path.GetFileName(target).StartsWith("UVP_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Yalnız output\\p8runs altındaki UVP_ test projesi: " + target);
                string edb = Path.ChangeExtension(target, ".edb");
                if (File.Exists(target) || Directory.Exists(edb)) throw new InvalidOperationException("Hedef zaten var, üzerine yazılmaz: " + target);
                if (!File.Exists(archive)) throw new InvalidOperationException("Yedek bulunamadı: " + archive);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                ActionCallingContext acc = new ActionCallingContext();
                acc.AddParameter("TYPE", "PROJECT");
                acc.AddParameter("ARCHIVENAME", archive);
                acc.AddParameter("PROJECTNAME", target);
                acc.AddParameter("UNPACKPROJECT", "0");
                bool ret = new CommandLineInterpreter().Execute("restore", acc);
                report["restore_returned"] = ret;
                report["project"] = target;
                report["file_count"] = Directory.Exists(edb) ? Directory.GetFiles(edb, "*", SearchOption.AllDirectories).Length : 0;
                report["ok"] = File.Exists(target) && Directory.Exists(edb);
                if (!File.Exists(target)) report["error"] = "restore proje dosyasını üretmedi";
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            Io.WriteJson(Io.Param(ctx, "OUT"), report);
            return true;
        }
    }

    /// <summary>UvpPdfToP8BagCdp /PROJECT:"UVP_x.elk" /OUT:"rapor.json": bağlantı kimliği -> CDP adları, EPLAN nesnesinden (salt okuma).
    /// Tek tel ile kablo damarını ayırmak için. Konumla eşleme iki yönde yanılıyor (denetim A ve B, 26SB003).</summary>
    public class BagCdpAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8BagCdp";
            Ordinal = 28;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.bagcdp.rapor";
            report["writes_live_project"] = false;
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                ProjectManager pm = Arac.Pm(ctx);
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                Dictionary<string, object> map = new Dictionary<string, object>();
                foreach (Connection c in new DMObjectsFinder(prj).GetConnections(new ConnectionsFilter()))
                {
                    List<object> names = new List<object>();
                    ConnectionDefinitionPoint[] cps = null;
                    try { cps = c.ConnectionDefPoints; } catch (Exception) { }
                    if (cps != null) foreach (ConnectionDefinitionPoint cp in cps) { ConnectionDefinitionPoint q = cp; names.Add(ImportAction.Text(delegate { return q.Name; })); }
                    if (names.Count > 0) map[ImportAction.Id(c)] = names;
                }
                report["connections"] = map;
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Io.WriteJson(Io.Param(ctx, "OUT"), report);
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }
    }

    /// <summary>UvpPdfToP8Dizi /PLAN:"dizi.json" /OUT:"rapor.json": birkaç eylemi TEK EPLAN başlatmasında sırayla çalıştırır.
    /// Komut satırı başlatma başına tek eylem kabul eder (run_action.ps1 ölçümü); kullanıcı başlatmada hata bildirimi görüyor (05.11).
    /// Plan: { "steps": [ { "action", "params": {..}, "result" } ] }. Sonuç dosyası oluşmayan adımda durur; sonraki adım çalışmaz.</summary>
    public class DiziAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Dizi";
            Ordinal = 28;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.dizi.rapor";
            List<object> rows = new List<object>();
            bool ok = true;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> plan = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(Io.Param(ctx, "PLAN")));
                foreach (object o in ImportAction.A(plan["steps"]))
                {
                    Dictionary<string, object> step = ImportAction.D(o);
                    string action = ImportAction.S(step, "action"), result = ImportAction.S(step, "result");
                    Dictionary<string, object> row = new Dictionary<string, object> { { "action", action }, { "result", result } };
                    rows.Add(row);
                    DateTime t0 = DateTime.UtcNow;
                    try
                    {
                        ActionCallingContext acc = new ActionCallingContext();
                        foreach (KeyValuePair<string, object> p in ImportAction.D(step["params"])) acc.AddParameter(p.Key, Convert.ToString(p.Value));
                        row["returned"] = new CommandLineInterpreter().Execute(action, acc);
                    }
                    catch (Exception ex) { row["error"] = ex.GetType().Name + ": " + ex.Message; }
                    row["seconds"] = (int)(DateTime.UtcNow - t0).TotalSeconds;
                    row["result_exists"] = File.Exists(result);
                    bool good = File.Exists(result);
                    // Eylem hatayı da JSON'a yazar: dosyanın varlığı başarı değildir. Bozuk adımdan sonra devam edilmez.
                    if (good && result.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Dictionary<string, object> res = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(result));
                            if (res.ContainsKey("ok") && false.Equals(res["ok"])) { good = false; row["error"] = res.ContainsKey("error") ? Convert.ToString(res["error"]) : "ok=false"; }
                        }
                        catch (Exception) { good = false; row["error"] = "sonuç JSON okunamadı"; }
                    }
                    row["dogru"] = good;
                    if (!good) { ok = false; break; }
                }
                report["ok"] = ok;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            report["steps"] = rows;
            Io.WriteJson(Io.Param(ctx, "OUT"), report);
            return true;
        }
    }
}
