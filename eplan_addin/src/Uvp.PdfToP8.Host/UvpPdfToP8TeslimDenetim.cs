// =====================================================================================
// UvpPdfToP8TeslimDenetim.cs — "Teslim öncesi denetim" tek düğme (F1-2 / A2).
//
// Var olan eylemleri SIRAYLA çağırır (onları değiştirmez), sonra Python raporunu üretir:
//   1. UvpPdfToP8Readback  → <OUT>\readback\readback_current.json   (UPDATE verilmez: yalnız okur)
//   2. UvpPdfToP8Pdf       → <OUT>\pdf\<proje>.pdf                  (EPLAN'ın kendi PDF'i; yalnız okur)
//   3. UvpPdfToP8Check     → <OUT>\check\check.json                 (yalnız /CHECK:1; mesajlar PROJEYE yazılır)
//   4. python -X utf8 tools\teslim_oncesi.py → <OUT>\TESLIM_ONCESI.md + teslim_oncesi.json
//      (karar: HAZIR / DEĞİL / BELİRSİZ; çıkış kodu 0/1/2)
//
//   UvpPdfToP8TeslimDenetim [/PROJECT:"<elk>"] [/OUT:"<klasör>"] [/CHECK:1] [/KOSU:"<p8runs koşu>"]
//                           [/MUSTERIPDF:"<müşteri.pdf>"] [/ROOT:"<motor>"] [/PYTHON:"<python.exe>"]
//   PROJECT yoksa açık (seçili) proje; OUT yoksa <proje klasörü>\teslim_denetim_<damga> ve sonuç pencerede gösterilir.
//   Bu eylem projeye kendisi yazmaz. Alt eylemler bugün Guard ile yalnız output\p8runs\UVP_ test projesinde çalışır;
//   çizimcinin canlı projesinde salt okuyan Readback/Pdf için Guard istisnası entegrasyon işidir (RAPOR.md).
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class TeslimDenetimAction : IEplAction
    {
        internal const string RibbonText = "Teslim öncesi denetim";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8TeslimDenetim";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            bool interactive = outDir == "";
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.teslim-denetim";
            report["utc"] = DateTime.UtcNow.ToString("o");
            string summary = "";
            try
            {
                string project = Io.Param(ctx, "PROJECT");
                if (project == "")
                {
                    Project current = new SelectionSet().GetCurrentProject(false);
                    if (current == null) throw new InvalidOperationException("Açık proje yok.");
                    project = current.ProjectLinkFilePath;
                }
                if (interactive) outDir = Path.Combine(Path.GetDirectoryName(project), "teslim_denetim_" + Io.Stamp());
                Directory.CreateDirectory(outDir);
                report["project"] = project;
                report["out"] = outDir;
                PanelSettings settings = PanelSettings.Load();
                if (Io.Param(ctx, "ROOT") != "") settings.Root = Io.Param(ctx, "ROOT");
                if (Io.Param(ctx, "PYTHON") != "") settings.Python = Io.Param(ctx, "PYTHON");

                string rbDir = Path.Combine(outDir, "readback");
                report["readback"] = Step("UvpPdfToP8Readback", project, rbDir, null);
                string rb = Path.Combine(rbDir, "readback_current.json");

                string pdfDir = Path.Combine(outDir, "pdf");
                report["pdf"] = Step("UvpPdfToP8Pdf", project, pdfDir, null);
                Dictionary<string, object> pdfResult = ReadJson(Path.Combine(pdfDir, "pdf_export.json"));
                string pdf = pdfResult != null && pdfResult.ContainsKey("pdf") ? Convert.ToString(pdfResult["pdf"]) : "";

                string check = "";
                if (Io.Param(ctx, "CHECK") == "1")
                {
                    string checkDir = Path.Combine(outDir, "check");
                    Dictionary<string, string> extra = new Dictionary<string, string>();
                    extra["SCHEMEXML"] = Path.Combine(settings.Root, "eplan_addin", "denetim", "UVP_PDF2P8_Denetim.xml");
                    extra["SCHEME"] = "UVP_PDF2P8";
                    report["check"] = Step("UvpPdfToP8Check", project, checkDir, extra);
                    check = Path.Combine(checkDir, "check.json");
                }

                StringBuilder args = new StringBuilder("-X utf8 tools\\teslim_oncesi.py");
                Arg(args, "--readback", rb);
                if (File.Exists(pdf)) Arg(args, "--pdf", pdf);
                if (check != "" && File.Exists(check)) Arg(args, "--check", check);
                if (Io.Param(ctx, "KOSU") != "") Arg(args, "--kosu", Io.Param(ctx, "KOSU"));
                if (Io.Param(ctx, "MUSTERIPDF") != "") Arg(args, "--musteri-pdf", Io.Param(ctx, "MUSTERIPDF"));
                Arg(args, "--cikti", outDir);
                string md = Path.Combine(outDir, "TESLIM_ONCESI.md");
                if (File.Exists(md)) File.Delete(md);   // eski rapor yeni karar sanılmasın
                int code = Python(settings, args.ToString(), out summary);
                report["python_exit"] = code;
                // Çıkış 0..2 ama rapor yoksa (ör. argparse hatası 2) karar değil, HATA.
                if (!File.Exists(md)) throw new InvalidOperationException("teslim_oncesi.py rapor yazmadı (çıkış " + code + "): " + summary);
                report["verdict"] = code == 0 ? "HAZIR" : code == 1 ? "DEĞİL" : "BELİRSİZ";
                report["report"] = md;
                report["ok"] = true;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            if (outDir != "") { try { Io.WriteJson(Path.Combine(outDir, "teslim_denetim.json"), report); } catch (Exception) { } }
            if (interactive)
            {
                bool ok = report["ok"] is bool && (bool)report["ok"];
                string text = ok ? summary + "\n\nRapor: " + report["report"]
                                 : "Denetim tamamlanamadı: " + (report.ContainsKey("error") ? report["error"] : summary);
                MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, ok && Convert.ToString(report["verdict"]) == "HAZIR"
                                ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            return true;
        }

        /// <summary>Var olan eylemi çağırır; eylem yoksa ya da false dönerse istisna.</summary>
        static string Step(string name, string project, string outDir, Dictionary<string, string> extra)
        {
            Eplan.EplApi.ApplicationFramework.Action action = new ActionManager().FindAction(name);
            if (action == null) throw new InvalidOperationException(name + " eylemi bulunamadı; eklenti yüklü mü?");
            ActionCallingContext call = new ActionCallingContext();
            call.AddParameter("PROJECT", project);
            call.AddParameter("OUT", outDir);
            if (extra != null) foreach (KeyValuePair<string, string> kv in extra) call.AddParameter(kv.Key, kv.Value);
            if (!action.Execute(call)) throw new InvalidOperationException(name + " reddetti (klasörde önceki kayıt olabilir): " + outDir);
            return outDir;
        }

        static void Arg(StringBuilder sb, string name, string value)
        {
            sb.Append(' ').Append(name).Append(" \"").Append(value.TrimEnd('\\')).Append('"');
        }

        static int Python(PanelSettings s, string args, out string stdout)
        {
            ProcessStartInfo info = new ProcessStartInfo(s.Python, args);
            info.WorkingDirectory = s.Root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(info))
            {
                System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();   // iki boru birlikte: kilitlenmez
                stdout = p.StandardOutput.ReadToEnd();
                string err = errTask.Result;
                // dogrula_hepsi (KOSU) ile =280 v1011 ölçümü 8-10 sn sürdü; 10 dk sınır büyük projeye pay
                if (!p.WaitForExit(600000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    throw new InvalidOperationException("teslim_oncesi.py 10 dk içinde bitmedi.");
                }
                if (p.ExitCode < 0 || p.ExitCode > 2)
                    throw new InvalidOperationException("teslim_oncesi.py başarısız (çıkış " + p.ExitCode + ", python " + s.Python
                                                        + ", motor " + s.Root + "): " + (err.Length > 600 ? err.Substring(err.Length - 600) : err));
                return p.ExitCode;
            }
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
        }
    }
}
