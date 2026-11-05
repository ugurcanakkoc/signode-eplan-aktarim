// =====================================================================================
// UvpCizimciKuralDenetim — "UVP kural denetimi" (fikir B1 / F2-1, entegrasyon 12.10).
//
// Açık projeyi SALT okur (Readback.Build — Sayfa0 ile aynı yol; kullanıcı tıklamasıyla, projeye yazmaz), sonra
//   python -X utf8 tools\kural_denetle.py <readback_current.json> --json bulgu.json
// bulguları türe göre sayar (DT sözdizimi, klemens -n / aynı ad / 4 mm², kesit/renk, PE, sayfa adı). Sonuç pencerede.
// Çıktı: <motor>\output\kural_denetim\<proje>_<damga>\ (readback_current.json, bulgu.json, KURAL_DENETIM.txt) —
// proje klasörüne hiçbir şey yazılmaz.
//   UvpCizimciKuralDenetim [/PROJECT:"<UVP_*.elk>" /OUT:"<klasör>"]   (test kipi: yalnız Guard.TestProject; pencere yok)
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class KuralDenetimAction : IEplAction
    {
        internal const string RibbonText = "UVP kural denetimi";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciKuralDenetim";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            bool test = outDir != "";
            string text;
            try
            {
                Project prj = null;
                if (test)
                {
                    string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                    foreach (Project open in new ProjectManager().OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) prj = new ProjectManager().OpenProject(file);
                }
                else prj = new SelectionSet().GetCurrentProject(false);
                if (prj == null) { MessageBox.Show("Önce bir proje açın.", RibbonText); return true; }
                PanelSettings settings = PanelSettings.Load();
                if (!test) outDir = Path.Combine(settings.Root, "output", "kural_denetim",
                                                 Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath) + "_" + Io.Stamp());
                Directory.CreateDirectory(outDir);

                List<Page> pages = new List<Page>();
                Dictionary<Page, int> physical = new Dictionary<Page, int>();
                foreach (Page page in prj.Pages) { pages.Add(page); physical[page] = -1; }
                Dictionary<string, object> rb = new Dictionary<string, object>();
                rb["test_project"] = prj.ProjectLinkFilePath;
                rb["scope"] = "CURRENT_PROJECT";
                rb = Readback.Build(prj, pages, physical, rb);
                string rbFile = Path.Combine(outDir, "readback_current.json");
                Io.WriteJson(rbFile, rb);

                ProcessStartInfo info = new ProcessStartInfo(settings.Python, "-X utf8 tools\\kural_denetle.py \"" + rbFile
                                                             + "\" --json \"" + Path.Combine(outDir, "bulgu.json") + "\"");
                info.WorkingDirectory = settings.Root;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(info))
                {
                    System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();   // iki boru birlikte: kilitlenmez
                    text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(180000)) { try { p.Kill(); } catch (Exception) { } throw new InvalidOperationException("kural_denetle.py 180 sn içinde bitmedi."); }
                    if (p.ExitCode != 0)
                        throw new InvalidOperationException("kural_denetle.py başarısız (çıkış " + p.ExitCode + ", python " + settings.Python
                                                            + ", motor " + settings.Root + "): " + err.Result);
                }
                File.WriteAllText(Path.Combine(outDir, "KURAL_DENETIM.txt"), text, new UTF8Encoding(false));
                text = "Proje: " + Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath) + " (salt okundu, değişmedi)\r\nKayıt: "
                       + outDir + "\r\n\r\n" + text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            catch (Exception ex)
            {
                text = "Denetim yapılamadı: " + ex.GetType().Name + ": " + ex.Message;
                if (test) { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "HATA.txt"), text, new UTF8Encoding(false)); }
            }
            if (!test) Show(text);
            return true;
        }

        static void Show(string text)
        {
            using (Form f = new Form())
            {
                TextBox box = new TextBox();
                box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Both; box.WordWrap = false;
                box.Dock = DockStyle.Fill; box.Font = new Font(FontFamily.GenericMonospace, 9f); box.Text = text;
                f.Text = RibbonText; f.Width = 1000; f.Height = 700; f.StartPosition = FormStartPosition.CenterScreen;
                f.Controls.Add(box);
                f.ShowDialog();
            }
        }
    }
}
