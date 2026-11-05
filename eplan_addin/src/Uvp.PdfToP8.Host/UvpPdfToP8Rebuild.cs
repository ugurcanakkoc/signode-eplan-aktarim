// =====================================================================================
// UvpPdfToP8Rebuild — "Bağlantıları yeniden kur" (kullanıcı 09.10 sabah; tasarım m12 (a'), MAIN asistan kararı 09.10)
//
// Çizimci UVP aktarımıyla gelmiş bir sayfada cihaz/klemens/ok taşır (fareyle), UVP sekmesi → Bağlantılar →
// "Bağlantıları yeniden kur". Sayfanın telleri yeni yerlere göre kurulur; ucu kıpırdamayan tel aynen kalır.
// EPLAN'da tel nesnesi yok (Import.cs H2-H5): bağ = uçlar + köşe (CO) / T sembolü + Generate.Connections (oto-bağ).
//
// Akış (tek sayfa; yazma tek UndoStep — EPLAN Geri Al tek adımda geri getirir):
//   1. Sayfa: /PAGE ya da düzenlenen / seçili TEK sayfa. Test kipi (/OUT) yalnız output\p8runs altındaki UVP_ test projesi
//      (Guard). Şeritten: çizimcinin kendi projesinde de (13.10) — plan sonrası onay penceresi, tek UndoStep.
//   2. Geri okuma (Readback.Build, bu sayfa) → <proje klasörü>\rebuild_<zaman>\readback_before.json.
//   3. Plan: python -X utf8 -m analyzer_v3.yeniden_bagla plan (motor klasörü/python: PanelSettings, panelle aynı).
//      Python defterden (<proje>.uvp_ledger.json) sayfayı yazan aktarımın makbuz+paketini bulur; her paket bağı için
//      KEEP / TRANSLATED / ROUTED / NOT_ROUTABLE / UNRESOLVED + silinecek eski köşeler, yeni köşeler (nokta + kol
//      kümesi), kayacak kendi okları, taşınacak kesit/renk noktaları (CDP), kırmızı notlar. Kararın tamamı Python'da
//      (test edilir: analyzer_v3/tests/test_yeniden_bagla.py); burası yalnız uygular ve ölçer.
//      Sunucu uç noktası yok: sunucu POST'u oturum anahtarı (CSRF) ister ve dosya yolu almaz; yerel çağrı yeter.
//   4. Yazma: eski köşe sil → ok kaydır → köşe koy (eşleme 'corner' sembolü, kol kümesine uyan varyant; PlaceCorners
//      ile aynı) → CDP taşı → not (Kontrol.PlaceMarks, UVP_KONTROL katmanı) → Generate.Connections(sayfa).
//   5. Ölçüm: geri okuma (readback_after.json) → Python check: her bağın iki ucu sayfanın grafik ağında birleşik mi,
//      iki ayrı kaynak ağı birleşti mi. Yeniden kurma YENİ yanlış birleşme kurduysa ya da ölçüm yapılamadıysa UndoStep
//      geri alınır (yanlış bağ sessiz geçmez); birleşme taşımanın kendisinden geliyorsa (öncesinde de var) uyarı.
//   6. Kayıt: <proje>.uvp_rebuild.json sayfa → kurulan yollar (sonraki basışta "eski köşe" bunlardır; tekrar basmak
//      bozmaz). Rapor <klasör>\rebuild.json; arayüzde kısa özet penceresi.
// Dokunulmaz: kullanıcı çizgileri (Line/PolyLine), yolu paket/kayıtta olmayan köşe (makro iç teli, çizimcinin teli),
// T düğümleri (yerinde kalır; kullanıcı T'yi taşıdıysa o bağ UNRESOLVED, tahmin yok), başka sayfalar.
//
// Eylem: UvpPdfToP8Rebuild                                 (şerit: UVP → Bağlantılar → Bağlantıları yeniden kur)
//        UvpPdfToP8Rebuild /PROJECT:"<UVP_*.elk>" /PAGE:"<tam sayfa adı; '/' yerine ters bölü olabilir>" /OUT:"<klasör>" [/MOVE:"-7K29,0,-40"]
//                          [/ROOT:"<motor klasörü>"] [/PYTHON:"<python.exe>"]
//        Otomatik deney: MOVE sayfadaki DT'si '-7K29' ile biten bütün fonksiyonları dx,dy mm kaydırır (çizimcinin fareyle
//        taşımasını temsil eder); öncesi readback_before_move.json, ağ karşılaştırmasının referansıdır.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class RebuildAction : IEplAction
    {
        internal const string RibbonText = "Bağlantıları yeniden kur";
        internal const string GroupText = "Bağlantılar";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Rebuild";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        class Refused : Exception { public Refused(string m) : base(m) { } }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            bool test = outDir != "";
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.rebuild";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(RebuildAction).Assembly.GetName().Name;
            ProjectManager pm = Arac.Pm(ctx);
            Project prj = null;
            bool close = false, confirm = false;
            try
            {
                Page page = null;
                if (test)
                {
                    string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                    foreach (Project open in pm.OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) { prj = pm.OpenProject(file); close = true; }
                    // Komut satırında değer içindeki '/' EPLAN'da parametre ayırıcısı sayılabilir (ölçülmedi); ters bölü
                    // yolda kanıtlı geçiyor (/PROJECT). Sayfa adındaki '/' yerine ters bölü de kabul edilir.
                    string want = Io.Param(ctx, "PAGE").Replace('\\', '/');
                    foreach (Page p in prj.Pages) if (p.Name == want) page = p;
                    if (page == null) throw new Refused("Sayfa yok: " + want);
                }
                else
                {
                    SelectionSet sel = new SelectionSet();
                    sel.LockProjectByDefault = false;
                    page = sel.CurrentlyEdited as Page;
                    if (page == null)
                    {
                        Page[] chosen = sel.GetSelectedPages();
                        if (chosen != null && chosen.Length == 1) page = chosen[0];
                    }
                    if (page == null) throw new Refused("Tek sayfa açın (grafik düzenleyicide) ya da sayfa gezgininde tek sayfa seçin.");
                    prj = page.Project;
                    // 13.10 (kullanıcı: "ürünü yerine koyduktan sonra bağlantıları tekrar oluştur"): çizimcinin kendi projesinde
                    // de çalışır — 12.10 "Düzenle" kuralı: plan çıkınca onay penceresi (proje, sayfa, bağ sayıları), tek UndoStep,
                    // ölçüm UNDO derse kendiliğinden geri. UVP_ test projesinde onay sorulmaz (otomasyon).
                    try { Guard.TestProject(prj.ProjectLinkFilePath); } catch (Exception) { confirm = true; }
                    outDir = Path.Combine(Path.GetDirectoryName(prj.ProjectLinkFilePath), "rebuild_" + Io.Stamp());
                }
                report["confirm_asked"] = confirm;
                Directory.CreateDirectory(outDir);
                report["out"] = outDir;
                Run(ctx, prj, page, outDir, report, confirm);
                report["ok"] = true;
            }
            catch (Refused r) { report["ok"] = false; report["refused"] = r.Message; }
            catch (Exception ex) { report["ok"] = false; report["error"] = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                if (outDir != "") { try { Io.WriteJson(Path.Combine(outDir, "rebuild.json"), report); } catch (Exception) { } }
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            if (!test) Arac.Bitir(prj, "yeniden_kur", RibbonText, report, Summary(report));
            return true;
        }

        static void Run(ActionCallingContext ctx, Project prj, Page page, string outDir, Dictionary<string, object> report, bool confirm)
        {
            string project = prj.ProjectLinkFilePath;
            report["project"] = project;
            report["page"] = page.Name;
            PanelSettings settings = PanelSettings.Load();
            if (Io.Param(ctx, "ROOT") != "") settings.Root = Io.Param(ctx, "ROOT");
            if (Io.Param(ctx, "PYTHON") != "") settings.Python = Io.Param(ctx, "PYTHON");
            Dictionary<string, object> request = new Dictionary<string, object>();
            request["project"] = project;
            request["page"] = page.Name;
            using (LockingStep step = new LockingStep())
            {
                Arac.Kilitle(prj);          // düğme yolu nesneleri kilitsiz verir (14.10 LockingException)
                string move = Io.Param(ctx, "MOVE");
                if (move != "")
                {
                    string reference = Path.Combine(outDir, "readback_before_move.json");
                    Io.WriteJson(reference, Read(prj, page));
                    request["reference_readback"] = reference;
                    report["moved"] = Move(page, move);
                    new Generate().Connections(new Page[] { page }, true);
                }
                string before = Path.Combine(outDir, "readback_before.json");
                Io.WriteJson(before, Read(prj, page));
                request["readback"] = before;
                string planFile = Path.Combine(outDir, "plan.json");
                Io.WriteJson(Path.Combine(outDir, "plan_request.json"), request);
                Stopwatch sw = Stopwatch.StartNew();
                Dictionary<string, object> plan = Python(settings, "plan", Path.Combine(outDir, "plan_request.json"), planFile);
                report["plan_seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
                if (plan.ContainsKey("counts")) report["plan_counts"] = plan["counts"];
                if (plan.ContainsKey("refused")) throw new Refused("Yeniden kurulamaz: " + plan["refused"]);
                if (confirm && MessageBox.Show("Proje: " + Path.GetFileNameWithoutExtension(project) + "\nSayfa: " + page.Name + "\n"
                        + CountsLine(plan.ContainsKey("counts") ? ImportAction.D(plan["counts"]) : null)
                        + "\n\nYazma tek Geri Al adımıdır; sonra sayfa ölçülür, yeni yanlış bağ kurulduysa kendiliğinden geri alınır."
                        + "\nRapor: " + outDir + "\n\nDevam edilsin mi?", RibbonText, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    throw new Refused("Kullanıcı onaylamadı; hiçbir şey değişmedi.");
                using (UndoManager um = new UndoManager())
                {
                    UndoStep undo = um.CreateUndoStep();
                    undo.SetUndoDescription("UVP · Bağlantıları yeniden kur (" + page.Name + ")");
                    bool undone = false;
                    try
                    {
                        report["applied"] = Apply(prj, page, plan);
                        new Generate().Connections(new Page[] { page }, true);
                        string after = Path.Combine(outDir, "readback_after.json");
                        Io.WriteJson(after, Read(prj, page));
                        request["plan"] = planFile;
                        request["readback_before"] = before;
                        request["readback_after"] = after;
                        Io.WriteJson(Path.Combine(outDir, "check_request.json"), request);
                        Dictionary<string, object> check = Python(settings, "check", Path.Combine(outDir, "check_request.json"),
                                                                  Path.Combine(outDir, "check.json"));
                        report["check"] = check;
                        if (ImportAction.S(check, "verdict") != "OK")
                        {
                            undo.DoUndo();
                            undone = true;
                            report["undone"] = true;
                            throw new Refused("Yeniden kurma yeni yanlış bağ kurdu ya da ölçülemedi (" + ImportAction.S(check, "verdict")
                                              + "); değişiklik geri alındı. Rapor: " + outDir);
                        }
                        WriteRecord(project, page.Name, plan);
                    }
                    catch (Exception)
                    {
                        if (!undone) { try { undo.DoUndo(); report["undone"] = true; } catch (Exception) { } }
                        throw;
                    }
                    finally { try { undo.CloseUndo(); } catch (Exception) { } }
                }
            }
        }

        static Dictionary<string, object> Read(Project prj, Page page)
        {
            Dictionary<Page, int> physical = new Dictionary<Page, int>();
            physical[page] = -1;
            Dictionary<string, object> rb = Readback.Build(prj, new List<Page> { page }, physical, null);
            rb["test_project"] = prj.ProjectLinkFilePath;
            rb["scope"] = "PAGE";
            return rb;
        }

        static Dictionary<string, object> Python(PanelSettings s, string verb, string request, string output)
        {
            ProcessStartInfo info = new ProcessStartInfo(s.Python,
                "-X utf8 -m analyzer_v3.yeniden_bagla " + verb + " --request \"" + request + "\" --out \"" + output + "\"");
            info.WorkingDirectory = s.Root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(info))
            {
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    throw new InvalidOperationException("Python " + verb + " 120 sn içinde bitmedi.");
                }
                if (p.ExitCode != 0 || !File.Exists(output))
                    throw new InvalidOperationException("Python " + verb + " başarısız (çıkış " + p.ExitCode + ", python " + s.Python
                                                        + ", motor " + s.Root + "): " + (err.Length > 600 ? err.Substring(err.Length - 600) : err));
            }
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(output, Encoding.UTF8));
        }

        static Placement Need(Dictionary<string, Placement> byId, string id)
        {
            Placement pl;
            if (!byId.TryGetValue(id, out pl)) throw new InvalidOperationException("Plan eskidi: nesne sayfada yok (" + id + ").");
            return pl;
        }

        /// <summary>Planı sayfaya uygular. Plandaki nesne sayfada yoksa istisna atar; çağıran UndoStep'i geri alır
        /// (plan geri okumadan hemen sonra üretildi; eksik nesne = sayfa arada değişti).</summary>
        static Dictionary<string, object> Apply(Project prj, Page page, Dictionary<string, object> plan)
        {
            Dictionary<string, object> log = new Dictionary<string, object>();
            Dictionary<string, Placement> byId = new Dictionary<string, Placement>();
            foreach (Placement pl in page.AllPlacements)
            {
                string id = ImportAction.Id(pl);
                if (id != null) byId[id] = pl;
            }
            int removed = 0, moved = 0, placed = 0, cdps = 0;
            foreach (object o in ImportAction.A(plan["delete"])) { Need(byId, Convert.ToString(o)).Remove(); removed++; }
            foreach (object o in ImportAction.A(plan["move"]))
            {
                Dictionary<string, object> m = ImportAction.D(o);
                Placement pl = Need(byId, ImportAction.S(m, "object_id"));
                pl.Location = new PointD(pl.Location.X + ImportAction.N(m["dx"]), pl.Location.Y + ImportAction.N(m["dy"]));
                moved++;
            }
            Dictionary<string, object> cs = ImportAction.D(plan["corner_symbol"]);
            Dictionary<string, SymbolVariant> variants = new Dictionary<string, SymbolVariant>();
            foreach (object o in ImportAction.A(plan["corners"]))
            {
                Dictionary<string, object> c = ImportAction.D(o);
                string arms = ImportAction.S(c, "arms");
                SymbolVariant sv;
                if (!variants.TryGetValue(arms, out sv))
                {
                    string note;
                    sv = ImportAction.VariantWithDirections(prj, ImportAction.S(cs, "library"), ImportAction.S(cs, "symbol"), arms, out note);
                    if (sv == null) throw new InvalidOperationException("Köşe sembolü: " + note);
                    variants[arms] = sv;
                }
                SymbolReference corner = SymbolReference.Create(sv, page);
                corner.Location = ImportAction.InsertionFor(sv, 0, ImportAction.P(c["point"]));
                placed++;
            }
            foreach (object o in ImportAction.A(plan["cdp_moves"]))
            {
                Dictionary<string, object> m = ImportAction.D(o);
                Need(byId, ImportAction.S(m, "object_id")).Location = ImportAction.P(m["to"]);
                cdps++;
            }
            List<object> marks = new List<object>();
            List<string> notes = new List<string>();
            if (ImportAction.A(plan["control_marks"]).Count > 0)
                Kontrol.PlaceMarks(prj, page, new Dictionary<string, object> { { "control_marks", plan["control_marks"] } }, "",
                                   new List<object>(), new List<StorableObject>(), marks, notes);
            log["corners_removed"] = removed;
            log["arrows_moved"] = moved;
            log["corners_placed"] = placed;
            log["cdp_moved"] = cdps;
            log["marks"] = marks;
            if (notes.Count > 0) log["notes"] = notes;
            return log;
        }

        /// <summary>Deney: DT'si `tag` ile biten sayfa fonksiyonlarını (CDP hariç) dx,dy kaydırır. Hedef MUTLAK atanır ve
        /// iki tur denetlenir: grup üyesini taşımak grubu da sürüklerse ikinci atama düzeltir (ölçülecek: rows.at).</summary>
        static List<object> Move(Page page, string spec)
        {
            string[] parts = spec.Split(',');
            if (parts.Length != 3) throw new Refused("MOVE biçimi: -DT,dx,dy (" + spec + ")");
            string tag = parts[0].Trim();
            double dx = Guard.Number(parts[1], double.NaN), dy = Guard.Number(parts[2], double.NaN);
            if (double.IsNaN(dx) || double.IsNaN(dy)) throw new Refused("MOVE sayıları okunamadı: " + spec);
            List<Function> hits = new List<Function>();
            Dictionary<Function, PointD> from = new Dictionary<Function, PointD>();
            foreach (Function f in page.Functions)
            {
                if (f is ConnectionDefinitionPoint) continue;
                string name = ImportAction.Text(delegate { return f.Name; });
                if (name.Split(':')[0].EndsWith(tag, StringComparison.Ordinal)) { hits.Add(f); from[f] = f.Location; }
            }
            if (hits.Count == 0) throw new Refused("MOVE: sayfada " + tag + " yok.");
            for (int pass = 0; pass < 2; pass++)
                foreach (Function f in hits)
                {
                    PointD want = new PointD(from[f].X + dx, from[f].Y + dy);
                    if (Math.Abs(f.Location.X - want.X) > 0.001 || Math.Abs(f.Location.Y - want.Y) > 0.001) f.Location = want;
                }
            List<object> rows = new List<object>();
            foreach (Function f in hits)
                rows.Add(new Dictionary<string, object> { { "name", ImportAction.Text(delegate { return f.Name; }) },
                    { "from", ImportAction.XY(from[f]) }, { "at", ImportAction.XY(f.Location) } });
            return rows;
        }

        static void WriteRecord(string project, string page, Dictionary<string, object> plan)
        {
            string file = Path.ChangeExtension(project, ".uvp_rebuild.json");
            Dictionary<string, object> rec = File.Exists(file)
                ? new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8))
                : new Dictionary<string, object>();
            rec["contract"] = "uvp.pdf2p8.rebuild-record";
            Dictionary<string, object> pages = rec.ContainsKey("pages") ? ImportAction.D(rec["pages"]) : new Dictionary<string, object>();
            Dictionary<string, object> entry = ImportAction.D(plan["record"]);
            entry["utc"] = DateTime.UtcNow.ToString("o");
            pages[page] = entry;
            rec["pages"] = pages;
            Io.WriteJson(file, rec);
        }

        static string Count(Dictionary<string, object> d, string key)
        {
            return d != null && d.ContainsKey(key) ? Convert.ToString(d[key]) : "0";
        }

        static int Len(Dictionary<string, object> d, string key)
        {
            return d != null && d.ContainsKey(key) ? ImportAction.A(d[key]).Count : 0;
        }

        static string CountsLine(Dictionary<string, object> c)
        {
            if (c == null) return "";
            return Count(c, "links") + " bağ: " + Count(c, "KEEP") + " aynen, " + Count(c, "TRANSLATED") + " kaydırılacak, "
                   + Count(c, "ROUTED") + " yeniden çizilecek, " + Count(c, "NOT_ROUTABLE") + " kurulamaz (kırmızı not), "
                   + Count(c, "UNRESOLVED") + " uç bulunamadı (dokunulmaz), " + Count(c, "UNDRAWN")
                   + " aktarımda çizilmemiş (ucu taşınmadı).";
        }

        static string Summary(Dictionary<string, object> report)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Sayfa: " + ImportAction.S(report, "page"));
            Dictionary<string, object> c = report.ContainsKey("plan_counts") ? ImportAction.D(report["plan_counts"]) : null;
            if (c != null) sb.AppendLine(CountsLine(c));
            Dictionary<string, object> k = report.ContainsKey("check") ? ImportAction.D(report["check"]) : null;
            if (k != null)
                sb.AppendLine("Ölçüm: " + Count(k, "links_connected") + "/" + Count(k, "links") + " bağ birleşik; yanlış birleşme "
                              + Len(k, "wrong_joins") + " (yeni " + Len(k, "wrong_joins_new") + "); doğrulama " + ImportAction.S(k, "verdict") + ".");
            if (report.ContainsKey("refused")) sb.AppendLine(ImportAction.S(report, "refused"));
            if (report.ContainsKey("out")) sb.AppendLine("Rapor: " + ImportAction.S(report, "out"));
            return sb.ToString();
        }
    }
}
