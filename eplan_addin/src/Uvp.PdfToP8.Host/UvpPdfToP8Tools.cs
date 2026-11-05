// =====================================================================================
// UvpPdfToP8Tools.cs — iki küçük yardımcı action (yalnız output\p8runs altındaki UVP_ test projesi):
//
//   UvpPdfToP8Snapshot /PROJECT:"<elk>" /OUT:"<klasör>"
//       Her sayfayı EPLAN'ın kendi çıktısıyla PNG'ye verir (Export.GraphicPage) ve fonksiyonların
//       adı/konumu/metni ile yerleşimlerin KUTULARINI (yazı kutuları dahil) JSON'a yazar.
//       Yazma yok: yalnız okuma + dosya çıktısı.
//
//   UvpPdfToP8Readback /PROJECT:"<elk>" /OUT:"<klasör>" [/UPDATE:1]
//       Projenin TAMAMININ güncel geri okuması. UPDATE:1 verilirse okumadan ÖNCE
//       Generate().Connections çalışır (projeyi değiştirir) ve `connections_updated` damgalanır;
//       verilmezse damga YANLIŞ kalır ve doğrulayıcı "ağ değişmedi" sonucunu güncel saymaz.
//
//   UvpPdfToP8Retouch /PROJECT:"<elk>" /FUNCTION:"<tam ad>" [/UID:"UVP-SRC:..."] [/DX:mm] [/DY:mm] [/TEXT:"..."]
//       Çizimcinin elle rötuşunu TEMSİL eder: adlandırılmış fonksiyonu taşır ve/veya işlev metnini
//       yazar. Öncesi/sonrası JSON'a yazılır; sonraki sayfa aktarımından sonra korunup korunmadığı
//       ölçülebilsin diye. Kullanıcı projesine dokunmaz, başka nesneyi değiştirmez.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    /// <summary>Çizimci araçlarının ortak yazma/makbuz kalıbı (ARAC_TEST 14.10 kök nedeni). Düğme yolu nesneleri
    /// SelectionSet ile LockProjectByDefault=false alır → nesneler kilitsiz, ilk yazma LockingException (kullanıcı makbuzu
    /// rebuild_20261014_055906). Test kipi ProjectManager ile açtığı için kilit kendiliğinden vardı; gizli ölçüm hatayı göstermedi.
    /// Kural: her araç yazmadan önce LockingStep içinde Kilitle; düğmeden çağrı da makbuz yazar ve sonucu mesaj kutusunda söyler.</summary>
    internal static class Arac
    {
        /// <summary>Yazmadan önce, LockingStep içinde: projenin bütün nesnelerini bu oturuma kilitler.</summary>
        internal static void Kilitle(Project prj) { prj.LockAllObjects(); }

        /// <summary>Test kipi proje yöneticisi. /GUI_TAKLIT:1 → LockProjectByDefault=false: düğme yolunun kilitsiz nesnelerini
        /// gizli EPLAN'da yeniden üretir (Kilitle yoksa LockingException).</summary>
        internal static ProjectManager Pm(ActionCallingContext ctx)
        {
            ProjectManager pm = new ProjectManager();
            if (Io.Param(ctx, "GUI_TAKLIT") == "1") pm.LockProjectByDefault = false;
            return pm;
        }

        /// <summary>Düğme yolu raporunun başı (test raporuyla aynı alanlar).</summary>
        internal static Dictionary<string, object> Rapor(string contract)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["contract"] = contract;
            r["utc"] = DateTime.UtcNow.ToString("o");
            r["assembly"] = typeof(Arac).Assembly.GetName().Name;
            r["mode"] = "GUI";
            return r;
        }

        /// <summary>Makbuz: [proje klasörü]\uvp_araclar\[araç]_[zaman].json (proje yoksa %LOCALAPPDATA%\UVP\PdfToP8\uvp_araclar).</summary>
        internal static string Makbuz(Project prj, string arac, Dictionary<string, object> report)
        {
            try
            {
                string dir = prj != null
                    ? Path.Combine(Path.GetDirectoryName(prj.ProjectLinkFilePath), "uvp_araclar")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVP", "PdfToP8", "uvp_araclar");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, arac + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".json");
                report["receipt"] = file;
                Io.WriteJson(file, report);
                return file;
            }
            catch (Exception ex) { return "(makbuz yazılamadı: " + ex.Message + ")"; }
        }

        /// <summary>Düğme yolu sonu: makbuz + Türkçe mesaj. Hata: istisna türü ve "Geri alındı" (yazma tek Geri Al adımı, istisnada
        /// kendiliğinden geri alınır; report["geri_al_elle"] varsa Ctrl+Z söylenir); reddedilen: gerekçe. `detail` başta gösterilir.</summary>
        internal static void Bitir(Project prj, string arac, string title, Dictionary<string, object> report, string detail)
        {
            string file = Makbuz(prj, arac, report);
            bool ok = report.ContainsKey("ok") && report["ok"] is bool && (bool)report["ok"];
            string text = detail ?? "";
            System.Windows.Forms.MessageBoxIcon icon = System.Windows.Forms.MessageBoxIcon.Information;
            if (!ok && report.ContainsKey("error"))
            {
                text += (text == "" ? "" : "\n\n") + "Hata: " + Convert.ToString(report["error"])
                        + (report.ContainsKey("geri_al_elle")   // EPLAN etkileşimi: adım EPLAN'ın, kendiliğinden geri alınmaz
                           ? "\nYarım yazılmış olabilir: EPLAN Geri Al (Ctrl+Z) ile geri alın."
                           : "\nGeri alındı: proje değişmedi (yazma tek Geri Al adımıdır, hata olunca kendiliğinden geri alınır).");
                icon = System.Windows.Forms.MessageBoxIcon.Error;
            }
            else if (!ok)
            {
                string why = report.ContainsKey("refused") ? Convert.ToString(report["refused"]) : "";
                if (why != "" && text.IndexOf(why, StringComparison.Ordinal) < 0) text += (text == "" ? "" : "\n\n") + why;
                icon = System.Windows.Forms.MessageBoxIcon.Warning;
            }
            System.Windows.Forms.MessageBox.Show(text + "\n\nMakbuz: " + file, title, System.Windows.Forms.MessageBoxButtons.OK, icon);
        }
    }

    /// <summary>Kilit + tek UndoStep kapsamı (yazma kalıbı). Tamam() çağrılmadan kapanırsa (istisna) adım geri alınır.
    /// Kullanım: using (Yazim y = new Yazim(prj, "açıklama")) { ...yaz...; y.Tamam(); }</summary>
    internal sealed class Yazim : IDisposable
    {
        readonly LockingStep _step;
        readonly UndoManager _um;
        readonly UndoStep _undo;
        bool _tamam;

        internal Yazim(Project prj, string desc)
        {
            _step = new LockingStep();
            try
            {
                Arac.Kilitle(prj);
                _um = new UndoManager();
                _undo = _um.CreateUndoStep();
                _undo.SetUndoDescription(desc);
            }
            catch (Exception)
            {
                if (_um != null) _um.Dispose();
                _step.Dispose();
                throw;
            }
        }

        internal void Tamam() { _tamam = true; }

        public void Dispose()
        {
            if (!_tamam) { try { _undo.DoUndo(); } catch (Exception) { } }
            try { _undo.CloseUndo(); } catch (Exception) { }
            _um.Dispose();
            _step.Dispose();
        }
    }

    internal static class Guard
    {
        /// <summary>Hedef yalnız output\p8runs altındaki UVP_ test projesi olabilir.</summary>
        internal static string TestProject(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("PROJECT verilmedi.");
            string full = Path.GetFullPath(path);
            if (full.IndexOf("\\output\\p8runs\\", StringComparison.OrdinalIgnoreCase) < 0
                || !Path.GetFileName(full).StartsWith("UVP_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Yalnız output\\p8runs altındaki UVP_ test projesi: " + full);
            if (!File.Exists(full)) throw new InvalidOperationException("Proje bulunamadı: " + full);
            return full;
        }

        internal static double Number(string text, double fallback)
        {
            double value;
            return double.TryParse((text ?? "").Replace(',', '.'), NumberStyles.Any,
                                   CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        /// <summary>Açık proje UVP_ test projesi mi (yalnız bilgi; Zincir canlı projeye de yazar — K4, 13.10).</summary>
        internal static bool IsTest(Project prj)
        {
            try { TestProject(prj.ProjectLinkFilePath); return true; } catch (Exception) { return false; }
        }

        /// <summary>K4 (13.10): yazmadan önce otomatik proje yedeği — EPLAN'ın kendi "backup" eylemi,
        /// [proje klasörü]\uvp_yedek\[proje]_[damga].zw1. Dosya oluşmazsa hata: yedeksiz yazılmaz.</summary>
        internal static string Backup(Project prj)
        {
            string dir = Path.Combine(Path.GetDirectoryName(prj.ProjectLinkFilePath), "uvp_yedek");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath) + "_" + Io.Stamp();
            ActionCallingContext acc = new ActionCallingContext();
            acc.AddParameter("TYPE", "PROJECT");
            acc.AddParameter("PROJECTNAME", prj.ProjectLinkFilePath);
            acc.AddParameter("DESTINATIONPATH", dir);
            acc.AddParameter("ARCHIVENAME", name);
            acc.AddParameter("BACKUPMEDIA", "DISK");
            acc.AddParameter("BACKUPMETHOD", "BACKUP");
            bool ok = new CommandLineInterpreter().Execute("backup", acc);
            string zw1 = Path.Combine(dir, name + ".zw1");
            if (!ok || !File.Exists(zw1)) throw new InvalidOperationException("Proje yedeği alınamadı (" + zw1 + ", backup=" + ok + "); yedeksiz yazılmaz.");
            return zw1;
        }
    }

    /// <summary>Sayfaları PNG'ye verir ve yerleşim kutularını (yazılar dahil) döker.</summary>
    public class SnapshotAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Snapshot";
            Ordinal = 26;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["contract"] = "uvp.pdf2p8.snapshot";
            result["contract_version"] = "1.0";
            result["utc"] = DateTime.UtcNow.ToString("o");
            result["writes_live_project"] = false;
            List<object> pages = new List<object>();
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                result["project"] = prj.ProjectLinkFilePath;
                Export export = new Export();
                foreach (Page page in prj.Pages)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["page"] = page.Name;
                    string image = Path.Combine(outDir, Safe(page.Name) + ".png");
                    try
                    {
                        export.GraphicPage(page, image, Export.Format.PNG, 24, 3000, Export.Compression.None, false);
                        row["image"] = image;
                    }
                    catch (Exception ex) { row["image_error"] = ex.Message; }
                    List<object> items = new List<object>();
                    foreach (Placement placement in page.AllPlacements)
                    {
                        Dictionary<string, object> item = new Dictionary<string, object>();
                        item["class_name"] = placement.GetType().Name;
                        try { item["at"] = ImportAction.XY(placement.Location); } catch (Exception) { }
                        try
                        {
                            PointD[] box = placement.GetBoundingBox();
                            if (box != null && box.Length >= 2)
                                item["box"] = new object[] { ImportAction.XY(box[0]), ImportAction.XY(box[1]) };
                        }
                        catch (Exception) { }
                        Function function = placement as Function;
                        if (function != null)
                        {
                            item["name"] = ImportAction.Text(delegate { return function.Name; });
                            item["function_text"] = ImportAction.Text(delegate { return function.Properties.FUNC_TEXT.ToString(); });
                            try { item["main"] = function.IsMainFunction; } catch (Exception) { }
                            // Hangi ad özelliği DOLU? Etiket gösterimi bu ölçüme göre seçilir.
                            item["visible_name"] = ImportAction.Text(delegate { return function.VisibleName; });
                            item["visible_dt"] = ImportAction.Text(delegate { return function.Properties.FUNC_VISIBLEDEVICETAG.ToString(); });
                            item["full_dt"] = ImportAction.Text(delegate { return function.Properties.FUNC_FULLDEVICETAG.ToString(); });
                            item["ident_dt"] = ImportAction.Text(delegate { return function.Properties.FUNC_IDENTDEVICETAG.ToString(); });
                            item["dt_name"] = ImportAction.Text(delegate { return function.Properties.FUNC_DEVICETAG_FULLNAME.ToString(); });
                        }
                        SymbolReference reference = placement as SymbolReference;
                        if (reference != null)
                        {
                            List<object> subs = new List<object>();
                            try
                            {
                                foreach (Eplan.EplApi.DataModel.Graphics.PropertyPlacement sub in reference.PropertyPlacements)
                                {
                                    Dictionary<string, object> row2 = new Dictionary<string, object>();
                                    row2["class_name"] = sub.GetType().Name;
                                    // Alt yerleşimin konumu/kutusu sembole GÖRELİDİR; çakışma ölçümü için
                                    // sembolün konumu eklenerek MUTLAK kutu da yazılır.
                                    PointD origin = reference.Location;
                                    try
                                    {
                                        row2["relative_at"] = ImportAction.XY(sub.Location);
                                        row2["at"] = ImportAction.XY(new PointD(origin.X + sub.Location.X, origin.Y + sub.Location.Y));
                                    }
                                    catch (Exception) { }
                                    try
                                    {
                                        PointD[] sbox = sub.GetBoundingBox();
                                        if (sbox != null && sbox.Length >= 2)
                                            row2["box"] = new object[] {
                                                ImportAction.XY(new PointD(origin.X + sbox[0].X, origin.Y + sbox[0].Y)),
                                                ImportAction.XY(new PointD(origin.X + sbox[1].X, origin.Y + sbox[1].Y)) };
                                    }
                                    catch (Exception) { }
                                    row2["shown"] = ImportAction.Text(delegate { return sub.GetDisplayString(); });
                                    row2["object_id"] = ImportAction.Id(sub);
                                    try { row2["rotation_rad"] = Math.Round(sub.Rotation, 4); } catch (Exception) { }
                                    row2["visible"] = ImportAction.Text(delegate { return sub.IsVisible.ToString(); });
                                    subs.Add(row2);
                                }
                            }
                            catch (Exception ex) { item["sub_error"] = ex.Message; }
                            item["sub_placements"] = subs;
                        }
                        Eplan.EplApi.DataModel.Graphics.Text text = placement as Eplan.EplApi.DataModel.Graphics.Text;
                        if (text != null) item["text"] = ImportAction.Text(delegate { return text.Contents.GetAsString(); });
                        items.Add(item);
                    }
                    row["placements"] = items;
                    pages.Add(row);
                }
                result["pages"] = pages;
                result["ok"] = true;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            Io.WriteJson(Path.Combine(outDir, "snapshot.json"), result);
            return true;
        }

        static string Safe(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }

    /// <summary>Projenin TAMAMININ güncel geri okuması (readback 2.0). Aktarım anındaki kayıt değil,
    /// ŞU ANKİ durum: rötuş sonrası yeniden doğrulama bununla yapılır.
    /// UPDATE:1 verilmedikçe YALNIZ OKUR; UPDATE:1 verilirse okumadan önce bağlantıları yeniden
    /// üretir (`Generate().Connections`) ve bu PROJEYE YAZAR (`writes_project: true`).</summary>
    public class CurrentReadbackAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Readback";
            Ordinal = 28;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            // Önceki kayıt EZİLMEZ: taze damgalı bir kanıt, damgasız yeni koşuyla sessizce
            // değiştirilebiliyordu. Aktarım makbuzunda olan kural burada da geçerlidir.
            if (File.Exists(Path.Combine(outDir, "readback_current.json")))
            {
                File.WriteAllText(Path.Combine(outDir, "REDDEDILDI_" + Io.Stamp() + ".txt"),
                    "OUT klasöründe readback_current.json var; önceki kayıt ezilmez. Yeni klasör verin.",
                    new UTF8Encoding(false));
                return false;
            }
            Dictionary<string, object> rb = new Dictionary<string, object>();
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                List<Page> pages = new List<Page>();
                Dictionary<Page, int> physical = new Dictionary<Page, int>();
                foreach (Page page in prj.Pages) { pages.Add(page); physical[page] = -1; }
                rb["test_project"] = prj.ProjectLinkFilePath;
                rb["scope"] = "CURRENT_PROJECT";          // aktarım anı değil, şu anki durum
                // Ölçüm (Astra 4. tur): aktarım `Generate().Connections` çağırıyor, düz geri okuma
                // çağırmıyordu; rötuştan sonra okunan bağlantı verisi ESKİ olabilir. UPDATE=1
                // verilirse bağlantılar önce tazelenir (projeyi DEĞİŞTİRİR) ve damgalanır.
                if (Io.Param(ctx, "UPDATE") == "1")
                {
                    prj.LockAllObjects();                 // yazan her eylem gibi önce kilit alır
                    new Generate().Connections(pages.ToArray(), true);
                    rb["connections_updated"] = true;
                    rb["connections_update_utc"] = DateTime.UtcNow.ToString("o");
                    rb["writes_project"] = true;
                }
                rb = Readback.Build(prj, pages, physical, rb);
                rb["import_ok"] = null;                   // bu bir aktarım kaydı değildir
                rb["ok"] = true;
            }
            catch (Exception ex)
            {
                rb["ok"] = false;
                rb["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            Io.WriteJson(Path.Combine(outDir, "readback_current.json"), rb);
            return true;
        }
    }

    /// <summary>Test projesini EPLAN'ın KENDİ PDF çıktısıyla dışa verir (`Export.PdfProject`).
    /// Yalnız okur + dosya yazar; projeye dokunmaz. Kullanıcının çizimi kendi araçlarıyla açıp
    /// görebilmesi için teslim edilir.</summary>
    public class PdfExportAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Pdf";
            Ordinal = 30;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["contract"] = "uvp.pdf2p8.pdf-export";
            result["contract_version"] = "1.0";
            result["utc"] = DateTime.UtcNow.ToString("o");
            result["writes_live_project"] = false;
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                result["project"] = prj.ProjectLinkFilePath;
                string target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath) + ".pdf");
                // Şema adı boş: projenin son kullandığı PDF şeması. Renk: tam renk (çizimdeki gibi).
                new Export().PdfProject(prj, "", target, Export.DegreeOfColor.Color, false, "", false);
                result["pdf"] = target;
                result["exists"] = File.Exists(target);
                if (!File.Exists(target))
                {
                    List<object> made = new List<object>();
                    foreach (string found in Directory.GetFiles(outDir, "*.pdf")) made.Add(found);
                    result["pdf_files_in_folder"] = made;
                }
                result["ok"] = true;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            Io.WriteJson(Path.Combine(outDir, "pdf_export.json"), result);
            return true;
        }
    }

    /// <summary>Cihaz adı özelliklerinin GERÇEK değerlerini ölçer (tahmin yok): bir fonksiyonun
    /// bütün "device tag" özellikleri yansımayla okunur ve değerleriyle yazılır. Etiketin hangi
    /// özelliği göstereceği bu ölçümle seçilir. Yalnız okur.</summary>
    public class DeviceTagProbeAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8DtProbe";
            Ordinal = 29;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["contract"] = "uvp.pdf2p8.dt-probe";
            result["contract_version"] = "1.0";
            result["utc"] = DateTime.UtcNow.ToString("o");
            result["writes_live_project"] = false;
            List<object> rows = new List<object>();
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                string want = Io.Param(ctx, "FUNCTION");          // boşsa projedeki her fonksiyon
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                result["project"] = prj.ProjectLinkFilePath;
                DMObjectsFinder finder = new DMObjectsFinder(prj);
                foreach (Function function in finder.GetFunctions(new FunctionsFilter()))
                {
                    string name = ImportAction.Text(delegate { return function.Name; });
                    if (want != "" && name != want) continue;
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    row["name"] = name;
                    row["page"] = ImportAction.Text(delegate { return function.Page.Name; });
                    // 24.10 (=530 zw1 testi): hazır parça listeli projede çift ana fonksiyon / çift parça ölçümü.
                    try { row["main"] = function.IsMainFunction; } catch (Exception) { }
                    try { row["placed"] = function.IsPlaced; } catch (Exception) { }
                    List<object> articles = new List<object>();
                    try
                    {
                        foreach (ArticleReference ar in function.ArticleReferences)
                            articles.Add(new Dictionary<string, object> { { "part_nr", ar.PartNr }, { "variant", ar.VariantNr }, { "count", ar.Count } });
                    }
                    catch (Exception ex) { row["articles_error"] = ex.GetType().Name + ": " + ex.Message; }
                    row["articles"] = articles;
                    Dictionary<string, object> values = new Dictionary<string, object>();
                    object properties = function.Properties;
                    foreach (System.Reflection.PropertyInfo info in properties.GetType().GetProperties())
                    {
                        if (info.Name.IndexOf("DEVICETAG", StringComparison.OrdinalIgnoreCase) < 0
                            && info.Name.IndexOf("_NAME", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string value;
                        try
                        {
                            // EPLAN özellik sarmalayıcılarının çoğu INDEKSLİdir (dil/çoklu değer);
                            // parametresiz okuma TargetParameterCountException atar (ölçüm 02_dt_olcum).
                            object[] index = info.GetIndexParameters().Length == 1 ? new object[] { 0 } : null;
                            object raw = info.GetValue(properties, index);
                            value = raw == null ? null : raw.ToString();
                        }
                        catch (Exception ex)
                        {
                            Exception inner = ex.InnerException ?? ex;
                            value = "<" + inner.GetType().Name + ">";
                        }
                        values[info.Name] = value;
                    }
                    row["properties"] = values;
                    rows.Add(row);
                }
                result["ok"] = true;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            result["functions"] = rows;
            Io.WriteJson(Path.Combine(outDir, "dt_probe.json"), result);
            return true;
        }
    }

    /// <summary>Çizimcinin elle rötuşunu temsil eder: bir fonksiyonu taşır ve/veya metnini yazar.
    /// Tek nesneye dokunur; geri alma yoktur, değişiklik KALICIDIR (ölçüm bunun için yapılır).</summary>
    public class RetouchAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Retouch";
            Ordinal = 27;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["contract"] = "uvp.pdf2p8.retouch";
            result["contract_version"] = "1.0";
            result["utc"] = DateTime.UtcNow.ToString("o");
            result["writes_live_project"] = false;
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                string want = Io.Param(ctx, "FUNCTION");
                double dx = Guard.Number(Io.Param(ctx, "DX"), 0), dy = Guard.Number(Io.Param(ctx, "DY"), 0);
                string note = Io.Param(ctx, "TEXT");
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                result["project"] = prj.ProjectLinkFilePath;
                DMObjectsFinder finder = new DMObjectsFinder(prj);
                FunctionsFilter filter = new FunctionsFilter();
                filter.Name = want;
                filter.ExactNameMatching = true;
                Function[] hits = finder.GetFunctions(filter);
                // Aynı adlı iki klemens ada göre ayrılamaz. Aktarımda yazılan KALICI kimlik
                // (FUNC_SUPPLEMENTARYFIELD[900]) verilmişse çizimcinin dokunduğu tekil örnek odur;
                // kimlik verilmezse ad çok adaya denk geliyorsa belirsizlik KALIR, tahmin edilmez.
                string uid = ImportAction.UidValue(Io.Param(ctx, "UID"));
                if (uid.Length > 0)
                {
                    result["uid"] = uid;
                    result["uid_candidates"] = hits.Length;
                    List<Function> keep = new List<Function>();
                    foreach (Function candidate in hits)
                        if (ImportAction.UidOf(candidate) == uid) keep.Add(candidate);
                    hits = keep.ToArray();
                    if (hits.Length != 1)
                        throw new InvalidOperationException("Kalıcı kimlikle tekil fonksiyon bulunamadı (" + uid + "): " + hits.Length + " aday.");
                }
                if (hits.Length != 1)
                    throw new InvalidOperationException("Rötuş için tekil fonksiyon bulunamadı (" + want + "): " + hits.Length + " aday. Kalıcı kimlik için /UID: verin.");
                Function target = hits[0];
                result["function"] = target.Name;
                result["object_id"] = ImportAction.Id(target);
                result["source_uid"] = ImportAction.UidOf(target);
                result["page"] = target.Page.Name;
                result["before"] = new Dictionary<string, object> {
                    { "at", ImportAction.XY(target.Location) },
                    { "function_text", ImportAction.Text(delegate { return target.Properties.FUNC_TEXT.ToString(); }) } };
                prj.LockAllObjects();
                target.Location = new PointD(target.Location.X + dx, target.Location.Y + dy);
                if (!string.IsNullOrEmpty(note)) target.Properties.FUNC_TEXT = note;
                result["after"] = new Dictionary<string, object> {
                    { "at", ImportAction.XY(target.Location) },
                    { "function_text", ImportAction.Text(delegate { return target.Properties.FUNC_TEXT.ToString(); }) } };
                result["ok"] = true;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            Io.WriteJson(Path.Combine(outDir, "retouch.json"), result);
            return true;
        }
    }
}
