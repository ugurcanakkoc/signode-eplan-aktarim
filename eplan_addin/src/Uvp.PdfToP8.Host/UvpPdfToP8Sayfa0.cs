// =====================================================================================
// UvpCizimciSayfa0 — "Sayfa 0: 24V + PE dağıtımı" (B5, 12.10; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Elle çizilmiş (ya da aktarılmış) projede tek tuşla -X4 24V dağıtım bloğu + -XPE PE barası (MAIN 30.09/03.10/10.10 kuralları):
//   1. Geri okuma (Readback.Build, bütün sayfalar, yalnız okuma) → <klasör>\readback_current.json
//   2. python -X utf8 tools/sayfa0.py <geri okuma> --out <klasör> --paket  (motor/python: PanelSettings, panelle aynı)
//      → plan.json, package.json (sayfa 0: 0.a, 0.b … alt sayfaları), mapping.json, yeniden_adlandir.json, ozet.json.
//      Projede numaralı dağıtım bloğu zaten varsa (çıkış 2) hiçbir şey yazılmaz.
//   3. Onay penceresi (proje adı, eklenecek nesne, yeniden adlandırılacak ok sayısı, geri alma yolu).
//   4. Yazma: (a) paket UvpPdfToP8Import ile (/TARGET bu proje, ON_FAILURE ROLLBACK; makbuz + defter klasörde),
//      (b) tüketici okları planın nokta adını alır (EPLAN eşler), TEK UndoStep; eski adlar yeniden_adlandir.json'da (geri-al günlüğü).
// Sınır: Import bugün yalnız output\p8runs altındaki UVP_ test projesine yazar (Guard/REFUSED_TARGET). Canlı proje istisnası
// entegrasyonda eklenecek; o zamana kadar canlı projede 1–3 çalışır (plan + önizleme), 4 reddedilir ve söylenir.
//
// Test: UvpCizimciSayfa0 /PROJECT:"<UVP_*.elk>" /OUT:"<yeni klasör>" [/ZORLA:1] — onay sorulmaz; sonuç <OUT>\sayfa0_sonuc.json.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class Sayfa0Action : IEplAction
    {
        internal const string RibbonText = "Sayfa 0: 24V + PE dağıtımı";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciSayfa0";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string testOut = Io.Param(ctx, "OUT");
            bool test = testOut != "";
            Dictionary<string, object> result = new Dictionary<string, object>();
            string outDir = testOut;
            try
            {
                ProjectManager pm = new ProjectManager();
                Project prj = null;
                if (test)
                {
                    string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                    foreach (Project open in pm.OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) prj = pm.OpenProject(file);
                }
                else prj = new SelectionSet().GetCurrentProject(false);
                if (prj == null) { Say("Önce bir proje açın.", MessageBoxIcon.Warning); return true; }
                string project = prj.ProjectLinkFilePath;
                if (!test) outDir = Path.Combine(Path.GetDirectoryName(project), "sayfa0_" + Io.Stamp());
                if (Directory.Exists(outDir) && Directory.GetFiles(outDir).Length > 0)
                    throw new InvalidOperationException("Klasör boş değil (önceki kayıt ezilmez): " + outDir);
                Directory.CreateDirectory(outDir);
                result["project"] = project;
                result["out"] = outDir;

                // 1. geri okuma (yalnız okur)
                List<Page> pages = new List<Page>();
                Dictionary<Page, int> physical = new Dictionary<Page, int>();
                foreach (Page page in prj.Pages) { pages.Add(page); physical[page] = -1; }
                Dictionary<string, object> rb = new Dictionary<string, object>();
                rb["test_project"] = project;
                rb["scope"] = "CURRENT_PROJECT";
                rb = Readback.Build(prj, pages, physical, rb);
                string rbFile = Path.Combine(outDir, "readback_current.json");
                Io.WriteJson(rbFile, rb);

                // 2. plan + paket (Python)
                PanelSettings settings = PanelSettings.Load();
                if (Io.Param(ctx, "ROOT") != "") settings.Root = Io.Param(ctx, "ROOT");
                if (Io.Param(ctx, "PYTHON") != "") settings.Python = Io.Param(ctx, "PYTHON");
                int code = Python(settings, "\"" + Path.Combine(settings.Root, "tools", "sayfa0.py") + "\" \"" + rbFile + "\" --out \""
                                  + outDir + "\" --paket" + (test && Io.Param(ctx, "ZORLA") == "1" ? " --zorla" : ""));
                Dictionary<string, object> ozet = Read(Path.Combine(outDir, "ozet.json"));
                result["ozet"] = ozet;
                if (code == 2) throw new InvalidOperationException(ImportAction.S(ozet, "paket"));
                Dictionary<string, object> pk = ImportAction.D(ozet["paket"]);
                ArrayList renames = ReadList(Path.Combine(outDir, "yeniden_adlandir.json"));

                // 3. onay (canlı projede)
                if (!test && !Confirm(prj, pk, renames.Count, ozet, outDir)) { result["cancelled"] = true; return true; }

                // 4a. dağıtım bloğu: Import (kendi kuralları, ROLLBACK)
                string importOut = Path.Combine(outDir, "import");
                Directory.CreateDirectory(importOut);
                ActionCallingContext call = new ActionCallingContext();
                call.AddParameter("PACKAGE", Path.Combine(outDir, "package.json"));
                call.AddParameter("MAPPING", Path.Combine(outDir, "mapping.json"));
                call.AddParameter("OUT", importOut);
                call.AddParameter("TEMPLATE", settings.Template);
                call.AddParameter("TARGET", project);
                call.AddParameter("ON_FAILURE", "ROLLBACK");
                new ImportAction().Execute(call);
                Dictionary<string, object> receipt = Read(Path.Combine(importOut, "receipt.json"));
                result["import_state"] = ImportAction.S(receipt, "state");
                result["import_error"] = ImportAction.S(receipt, "error");
                if (!(receipt.ContainsKey("import_ok") && receipt["import_ok"] is bool && (bool)receipt["import_ok"]))
                {
                    string why = ImportAction.S(receipt, "error");
                    // 14.10 kanıt (sayfa0_20261014_061245): Import oklarda import_name ister; tools/sayfa0.py paketi
                    // eplan_export._continuation_ids'ten geçirmiyor → 51 okun hiçbirinde ad yok → REFUSED_CONTRACT "eski paket".
                    if (ImportAction.S(receipt, "state") == "REFUSED_CONTRACT" && why.IndexOf("Devam kimliği", StringComparison.Ordinal) >= 0)
                        why = "Sayfa 0 paketinin oklarında devam adı (import_name) yok — motor hatası (tools/sayfa0.py paketi "
                              + "_continuation_ids'ten geçirmiyor); paket eski değil, proje değişmedi. Motor güncellenince yeniden deneyin. (" + why + ")";
                    throw new InvalidOperationException("Dağıtım bloğu yazılmadı (" + ImportAction.S(receipt, "state") + "): "
                                                        + why + " — makbuz " + importOut);
                }

                // 4b. tüketici okları nokta adını alır (tek UndoStep)
                result["renamed"] = Rename(prj, renames);
                result["ok"] = true;
                if (!test) Say(Summary(result), MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                if (!test) Say(ImportAction.S(result, "error"), MessageBoxIcon.Warning);
            }
            if (outDir != "" && Directory.Exists(outDir)) Io.WriteJson(Path.Combine(outDir, "sayfa0_sonuc.json"), result);
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        static bool Confirm(Project prj, Dictionary<string, object> pk, int renames, Dictionary<string, object> ozet, string outDir)
        {
            string text = "Proje: " + ImportAction.Text(delegate { return prj.ProjectName; }) + "\n"
                          + "Eklenecek: sayfa 0 alt sayfaları " + string.Join(", ", Strings(pk["alt_sayfa"])) + " — "
                          + ImportAction.S(pk, "klemens") + " klemens, " + ImportAction.S(pk, "ok") + " ok, " + ImportAction.S(pk, "bag") + " bağ\n"
                          + "Yeniden adlandırılacak tüketici oku: " + renames + "\n"
                          + "Plan: " + string.Join("; ", Strings(ozet["gruplar"])) + "\n"
                          + "Eksik (uydurulmadı): " + ImportAction.S(ozet, "eksik") + "\n\n"
                          + "Geri alma: ok adları EPLAN Geri Al (tek adım); dağıtım bloğu Import makbuzu ile. Kayıt: " + outDir + "\n\nYazılsın mı?";
            return MessageBox.Show(text, RibbonText, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        static List<string> Strings(object o)
        {
            List<string> r = new List<string>();
            foreach (object x in ImportAction.A(o)) r.Add(Convert.ToString(x));
            return r;
        }

        /// <summary>Planın kesinti adları: object_id ile bulunur, tam ad WriteName ile (yapı alanları), görünen ad nokta adı.</summary>
        static Dictionary<string, object> Rename(Project prj, ArrayList renames)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            List<object> rows = new List<object>();
            if (renames.Count == 0) { r["count"] = 0; return r; }
            Dictionary<string, InterruptionPoint> byId = new Dictionary<string, InterruptionPoint>();
            foreach (InterruptionPoint ip in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
                byId[ImportAction.Id(ip)] = ip;
            int done = 0;
            using (LockingStep step = new LockingStep())
            using (UndoManager um = new UndoManager())
            {
                UndoStep undo = um.CreateUndoStep();
                undo.SetUndoDescription(RibbonText + " — ok adları");
                foreach (object o in renames)
                {
                    Dictionary<string, object> x = ImportAction.D(o);
                    Dictionary<string, object> row = new Dictionary<string, object>(x);
                    InterruptionPoint ip;
                    if (!byId.TryGetValue(ImportAction.S(x, "object_id"), out ip)) { row["error"] = "ok bulunamadı"; rows.Add(row); continue; }
                    string before = ImportAction.Text(delegate { return ip.Name; });
                    if (before != ImportAction.S(x, "ad")) { row["error"] = "ok arada değişti: " + before; rows.Add(row); continue; }
                    ip.LockObject();
                    row["write"] = ImportAction.WriteName(ip, ImportAction.S(x, "yeni"), "", ImportAction.S(x, "yeni_gorunen"));
                    try { ip.VisibleName = ImportAction.S(x, "yeni_gorunen"); } catch (Exception ex) { row["visible_error"] = ex.Message; }
                    row["name_after"] = ImportAction.Text(delegate { return ip.Name; });
                    done += ImportAction.S(row, "name_after") == ImportAction.S(x, "yeni") ? 1 : 0;
                    rows.Add(row);
                }
                try { undo.CloseUndo(); } catch (Exception) { }
            }
            r["count"] = done;
            r["rows"] = rows;
            return r;
        }

        static string Summary(Dictionary<string, object> r)
        {
            Dictionary<string, object> ren = ImportAction.D(r["renamed"]);
            return "Sayfa 0 yazıldı (Import: " + ImportAction.S(r, "import_state") + ").\nYeniden adlandırılan ok: "
                   + ImportAction.S(ren, "count") + "\nKayıt: " + ImportAction.S(r, "out");
        }

        static int Python(PanelSettings s, string args)
        {
            ProcessStartInfo info = new ProcessStartInfo(s.Python, "-X utf8 " + args);
            info.WorkingDirectory = s.Root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(info))
            {
                p.OutputDataReceived += delegate { };
                p.BeginOutputReadLine();
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(180000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    throw new InvalidOperationException("sayfa0.py 180 sn içinde bitmedi.");
                }
                if (p.ExitCode != 0 && p.ExitCode != 2)
                    throw new InvalidOperationException("sayfa0.py başarısız (çıkış " + p.ExitCode + ", python " + s.Python + ", motor "
                                                        + s.Root + "): " + (err.Length > 600 ? err.Substring(err.Length - 600) : err));
                return p.ExitCode;
            }
        }

        static Dictionary<string, object> Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Yazılmadı: " + path);
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
        }

        static ArrayList ReadList(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<ArrayList>(File.ReadAllText(path, Encoding.UTF8));
        }
    }
}
