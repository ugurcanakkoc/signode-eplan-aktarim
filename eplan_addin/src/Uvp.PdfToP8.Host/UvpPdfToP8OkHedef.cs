// =====================================================================================
// UvpCizimciOkHedef — "OKUNAMADI ok hedefi" (A3 / F1.3, 12.10; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Çizimci hedefi okunamamış TEK bir oku (görünen ad 'OKUNAMADI') seçer, butona basar:
//   1. Projenin geri okuması (Readback.Build, bütün oklar) → python -m analyzer_v3.ok_hedef aday: müşteri PDF'inden
//      hedef adayları (karşı sayfanın bu sayfaya dönen uçları, basılı konum eki ızgarayla iki yönde tutan; ad: karşı okun
//      proje adı > potansiyel > tek cihaz:uç; iki yan da OKUNAMADI → H3 ortak adı; adı başka okta dolu aday elenir).
//      Karar Python'da (test: analyzer_v3/tests/test_ok_hedef.py); burası seçtirir, yazar ve ölçer.
//   2. Pencere: adaylar (sıra eşlemesi '*' önde) + serbest ad. Seçilen ad okun yapısıyla tam ad olur: '=280+E280-X4.S:27'.
//   3. Yazmadan ÖNCE onay: proje adı, değişecek nesne sayısı (1 ok), eski → yeni ad, adı taşıyan ok sayısı, aday notları.
//   4. Tek UndoStep: ad (ImportAction.WriteName) + görünen ad → geri okuma → python ok_hedef olc: aynı adlı okların kaynak
//      uçları {bu ok, beklenen eş} dışına taşıyorsa YANLIS_BIRLESME → Geri Al (yanlış bağ sessiz geçmez). YETIM (eşsiz dış
//      devam) kalır, uyarılır. Elle geri almak: EPLAN Geri Al (tek adım).
// Canlı (UVP_ olmayan) projede yalnız bu onay + geri al ile çalışır; yalnız seçili okun adını değiştirir.
//
// Test: UvpCizimciOkHedef /PROJECT:"<UVP_*.elk>" /ID:"<kimlik>" /NAME:"<ad|AUTO>" /OUT:"<klasör>" [/UNDO_CHECK:1]
//       [/ROOT:"<motor>"] [/PYTHON:"<python.exe>"] — yalnız output\p8runs altındaki UVP_ test projesi (Guard); onay
//       sorulmaz. AUTO: TEK_ADAY ise o aday, değilse yazmaz. UNDO_CHECK:1 → yaz, ölç, geri al, oku.
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
    public class OkHedefAction : IEplAction
    {
        internal const string RibbonText = "OKUNAMADI ok hedefi";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciOkHedef";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        internal class Secim { public string Ad = "", TamAd = "", KarsiEid = "", Notlar = ""; }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            if (outDir != "") return Test(ctx, outDir);
            Project prj = null;
            Dictionary<string, object> report = Arac.Rapor("uvp.pdf2p8.ok_hedef");
            try
            {
                SelectionSet sel = new SelectionSet();
                sel.LockProjectByDefault = false;
                List<InterruptionPoint> ips = new List<InterruptionPoint>();
                foreach (StorableObject so in sel.Selection)
                {
                    InterruptionPoint q = so as InterruptionPoint;
                    if (q != null && !ips.Exists(delegate (InterruptionPoint x) { return ImportAction.Id(x) == ImportAction.Id(q); })) ips.Add(q);
                }
                if (ips.Count != 1) { Say("Hedefi yazılacak TEK oku (OKUNAMADI) sayfada seçin.", MessageBoxIcon.Warning); return true; }
                InterruptionPoint ip = ips[0];
                prj = ip.Project;
                outDir = Path.Combine(Path.GetDirectoryName(prj.ProjectLinkFilePath), "ok_hedef_" + Io.Stamp());
                Directory.CreateDirectory(outDir);
                PanelSettings settings = PanelSettings.Load();
                Dictionary<string, object> row = Adaylar(prj, ip, settings, outDir);
                Secim s;
                using (OkHedefForm form = new OkHedefForm(row))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.Chosen == null) return true;
                    s = form.Chosen;
                }
                string old = ImportAction.Text(delegate { return ip.Name; });
                int same = SameName(prj, s.TamAd);
                string ask = "Proje: " + ImportAction.Text(delegate { return prj.ProjectName; }) + "\nDeğişecek nesne: 1 ok ("
                             + ImportAction.Text(delegate { return ip.Page.Name; }) + ")\n'" + old + "' → '" + s.TamAd + "'\n"
                             + "Bu adı taşıyan ok (şimdi): " + same + (s.Notlar != "" ? "\n" + s.Notlar : "")
                             + "\n\nYazımdan sonra ağ ölçülür; yanlış birleşmede otomatik geri alınır. Elle: EPLAN Geri Al (tek adım). Yazılsın mı?";
                if (MessageBox.Show(ask, RibbonText, MessageBoxButtons.YesNo, s.Notlar != "" ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
                    return true;
                Run(prj, ip, s, settings, outDir, report, false);
                Dictionary<string, object> olc = (Dictionary<string, object>)report["olc"];
                bool undone = report.ContainsKey("undone");
                report["ok"] = !undone;
                Io.WriteJson(Path.Combine(outDir, "ok_hedef.json"), report);
                Arac.Bitir(prj, "ok_hedef", RibbonText, report, (undone ? "GERİ ALINDI: " : "Yazıldı: ") + ImportAction.S(olc, "verdict")
                           + "\nAd: " + ImportAction.S(olc, "ad") + "\nÇapraz referans: " + ImportAction.S(olc, "cross_reference") + "\nRapor: " + outDir);
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                Arac.Bitir(prj, "ok_hedef", RibbonText, report, "");
            }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        static Dictionary<string, object> ReadAll(Project prj)
        {
            Dictionary<string, object> rb = Readback.Build(prj, new List<Page>(), new Dictionary<Page, int>(), null);
            rb["test_project"] = prj.ProjectLinkFilePath;
            rb["scope"] = "INTERRUPTION_POINTS";
            return rb;
        }

        /// <summary>Seçili okun aday satırı (ok_hedef aday çıktısından); ok OKUNAMADI değilse satır yok → hata.</summary>
        static Dictionary<string, object> Adaylar(Project prj, InterruptionPoint ip, PanelSettings s, string outDir)
        {
            string before = Path.Combine(outDir, "readback_before.json");
            Io.WriteJson(before, ReadAll(prj));
            Dictionary<string, object> r = Python(s, "aday \"" + before + "\"", Path.Combine(outDir, "aday.json"));
            string id = ImportAction.Id(ip);
            foreach (object o in (ArrayList)r["oklar"])
            {
                Dictionary<string, object> row = (Dictionary<string, object>)o;
                if (ImportAction.S(row, "object_id") == id) return row;
            }
            throw new InvalidOperationException("Seçili ok 'OKUNAMADI' değil ya da aday listesinde yok: " + id);
        }

        static int SameName(Project prj, string name)
        {
            int n = 0;
            foreach (InterruptionPoint q in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
                if (ImportAction.Text(delegate { return q.Name; }) == name) n++;
            return n;
        }

        /// <summary>Tek kilit + tek UndoStep: yaz, geri oku, ölç; YANLIS_BIRLESME / YAZILMADI → geri al. undoAfter: test.</summary>
        static void Run(Project prj, InterruptionPoint ip, Secim s, PanelSettings settings, string outDir,
                        Dictionary<string, object> report, bool undoAfter)
        {
            report["object_id"] = ImportAction.Id(ip);
            report["old_name"] = ImportAction.Text(delegate { return ip.Name; });
            report["old_visible"] = ImportAction.Text(delegate { return ip.VisibleName; });
            report["expected"] = s.TamAd;
            using (LockingStep step = new LockingStep())
            using (UndoManager um = new UndoManager())
            {
                Arac.Kilitle(prj);          // düğme yolu nesneleri kilitsiz verir (14.10 LockingException)
                UndoStep undo = um.CreateUndoStep();
                undo.SetUndoDescription("UVP · " + RibbonText + " (" + s.Ad + ")");
                bool undone = false;
                try
                {
                    report["name_write"] = ImportAction.WriteName(ip, s.TamAd, "", s.Ad.TrimStart('-'));
                    try { ip.VisibleName = s.Ad; } catch (Exception ex) { report["visible_name_error"] = ex.Message; }
                    string after = Path.Combine(outDir, "readback_after.json");
                    Io.WriteJson(after, ReadAll(prj));
                    Dictionary<string, object> plan = new Dictionary<string, object>();
                    plan["object_id"] = ImportAction.Id(ip);
                    plan["tam_ad"] = s.TamAd;
                    plan["karsi_eid"] = s.KarsiEid == "" ? null : s.KarsiEid;
                    string planFile = Path.Combine(outDir, "plan.json");
                    Io.WriteJson(planFile, plan);
                    Dictionary<string, object> olc = Python(settings, "olc \"" + after + "\" --plan \"" + planFile + "\"",
                                                            Path.Combine(outDir, "olc.json"));
                    report["olc"] = olc;
                    string v = ImportAction.S(olc, "verdict");
                    if (v != "OK" && v != "YETIM")
                    {
                        undo.DoUndo();
                        undone = true;
                        report["undone"] = "ölçüm " + v;
                    }
                    else if (undoAfter)
                    {
                        undo.DoUndo();
                        undone = true;
                        report["undo_check"] = true;
                    }
                    if (undone)
                    {
                        report["after_undo_name"] = ImportAction.Text(delegate { return ip.Name; });
                        report["after_undo_visible"] = ImportAction.Text(delegate { return ip.VisibleName; });
                    }
                }
                catch (Exception)
                {
                    if (!undone) { try { undo.DoUndo(); report["undone"] = "hata"; } catch (Exception) { } }
                    throw;
                }
                finally { try { undo.CloseUndo(); } catch (Exception) { } }
            }
        }

        /// <summary>python -X utf8 -m analyzer_v3.ok_hedef &lt;args&gt; --json &lt;output&gt; (motor/python: PanelSettings, panelle aynı).
        /// olc'un çıkış kodu 2'si (YANLIS_BIRLESME) hata değildir; karar JSON'dadır.</summary>
        static Dictionary<string, object> Python(PanelSettings s, string args, string output)
        {
            ProcessStartInfo info = new ProcessStartInfo(s.Python, "-X utf8 -m analyzer_v3.ok_hedef " + args + " --json \"" + output + "\"");
            info.WorkingDirectory = s.Root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            info.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(info))
            {
                p.StandardOutput.ReadToEndAsync();
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(300000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    throw new InvalidOperationException("Python ok_hedef 300 sn içinde bitmedi.");
                }
                if ((p.ExitCode != 0 && p.ExitCode != 2) || !File.Exists(output))
                    throw new InvalidOperationException("Python ok_hedef başarısız (çıkış " + p.ExitCode + ", python " + s.Python
                                                        + ", motor " + s.Root + "): " + (err.Length > 600 ? err.Substring(err.Length - 600) : err));
            }
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(output, Encoding.UTF8));
        }

        internal static List<Dictionary<string, object>> Candidates(Dictionary<string, object> row)
        {
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            if (row.ContainsKey("adaylar"))
                foreach (object o in (ArrayList)row["adaylar"]) l.Add((Dictionary<string, object>)o);
            return l;
        }

        internal static string Notes(Dictionary<string, object> c)
        {
            List<string> n = new List<string>();
            if (c.ContainsKey("notlar") && c["notlar"] is ArrayList) foreach (object o in (ArrayList)c["notlar"]) n.Add(Convert.ToString(o));
            return string.Join("; ", n.ToArray());
        }

        static Secim Make(Dictionary<string, object> row, string ad, Dictionary<string, object> c)
        {
            Secim s = new Secim();
            s.Ad = ad;
            s.TamAd = ad.StartsWith("=") ? ad : ImportAction.S(row, "yapi") + ad.TrimStart('-');
            if (c != null) { s.KarsiEid = ImportAction.S(c, "karsi_eid"); s.Notlar = Notes(c); }
            else s.Notlar = "Serbest ad: aday listesinde yok; eşleşme yalnız yazımdan sonraki ölçümle bilinir.";
            return s;
        }

        internal class OkHedefForm : Form
        {
            internal Secim Chosen;
            readonly ListView _list = new ListView();
            readonly TextBox _text = new TextBox();
            readonly Dictionary<string, object> _row;

            internal OkHedefForm(Dictionary<string, object> row)
            {
                _row = row;
                Text = RibbonText + " — " + ImportAction.S(row, "sayfa") + "  " + ImportAction.S(row, "uc") + "  (" + ImportAction.S(row, "durum") + ")";
                Width = 860; Height = 380; StartPosition = FormStartPosition.CenterScreen;
                _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.HideSelection = false;
                _list.Columns.Add("Ad", 150); _list.Columns.Add("Kaynak", 90); _list.Columns.Add("Karşı sayfa", 80);
                _list.Columns.Add("Sıra", 40); _list.Columns.Add("Notlar", 480);
                foreach (Dictionary<string, object> c in Candidates(row))
                {
                    string ad = ImportAction.S(c, "ad");
                    ListViewItem it = new ListViewItem(new string[] { ad == "" ? "(ad yok)" : ad, ImportAction.S(c, "kaynak"),
                        ImportAction.S(c, "karsi_sayfa"), c.ContainsKey("sira") && c["sira"] is bool && (bool)c["sira"] ? "*" : "", Notes(c) });
                    it.Tag = c;
                    _list.Items.Add(it);
                }
                if (_list.Items.Count > 0) _list.Items[0].Selected = true;     // Python sırası: sıra eşlemesi önde
                _list.DoubleClick += delegate { Pick(); };
                FlowLayoutPanel bottom = new FlowLayoutPanel();
                bottom.Dock = DockStyle.Bottom; bottom.Height = 40; bottom.FlowDirection = FlowDirection.RightToLeft;
                Button ok = new Button(); ok.Text = "Seçili adı yaz…"; ok.AutoSize = true; ok.Click += delegate { Pick(); };
                Button free = new Button(); free.Text = "Serbest adı yaz…"; free.AutoSize = true;
                free.Click += delegate
                {
                    string t = _text.Text.Trim();
                    if (t == "") { _text.Focus(); return; }
                    Chosen = Make(_row, t, null); DialogResult = DialogResult.OK; Close();
                };
                Button cancel = new Button(); cancel.Text = "İptal"; cancel.DialogResult = DialogResult.Cancel;
                _text.Width = 200;
                bottom.Controls.Add(cancel); bottom.Controls.Add(ok); bottom.Controls.Add(free); bottom.Controls.Add(_text);
                AcceptButton = ok; CancelButton = cancel;
                Controls.Add(_list); Controls.Add(bottom);
            }

            void Pick()
            {
                if (_list.SelectedItems.Count != 1) return;
                Dictionary<string, object> c = (Dictionary<string, object>)_list.SelectedItems[0].Tag;
                if (ImportAction.S(c, "ad") == "") { MessageBox.Show("Bu adayın tek adı yok; serbest ad yazın.", RibbonText); return; }
                Chosen = Make(_row, ImportAction.S(c, "ad"), c);
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        bool Test(ActionCallingContext ctx, string outDir)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.ok_hedef";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(OkHedefAction).Assembly.GetName().Name;
            ProjectManager pm = Arac.Pm(ctx);
            Project prj = null;
            bool close = false;
            try
            {
                Directory.CreateDirectory(outDir);
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                report["project"] = file;
                PanelSettings settings = PanelSettings.Load();
                if (Io.Param(ctx, "ROOT") != "") settings.Root = Io.Param(ctx, "ROOT");
                if (Io.Param(ctx, "PYTHON") != "") settings.Python = Io.Param(ctx, "PYTHON");
                string id = Io.Param(ctx, "ID").Trim();
                List<InterruptionPoint> m = new List<InterruptionPoint>();
                foreach (InterruptionPoint q in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
                    if (ImportAction.Id(q) == id) m.Add(q);
                if (m.Count != 1) throw new InvalidOperationException("Kimlik tek oka düşmedi (" + m.Count + "): " + id);
                Dictionary<string, object> row = Adaylar(prj, m[0], settings, outDir);
                report["durum"] = ImportAction.S(row, "durum");
                string name = Io.Param(ctx, "NAME").Trim();
                Secim s = null;
                if (name == "AUTO")
                {
                    if (ImportAction.S(row, "durum") != "TEK_ADAY") throw new InvalidOperationException("AUTO: tek aday yok (" + ImportAction.S(row, "durum") + "); yazılmadı.");
                    foreach (Dictionary<string, object> c in Candidates(row))
                        if (ImportAction.S(c, "ad") != "" && !(c.ContainsKey("ad_dolu") && c["ad_dolu"] is ArrayList && ((ArrayList)c["ad_dolu"]).Count > 0)) s = Make(row, ImportAction.S(c, "ad"), c);
                }
                else if (name != "")
                {
                    foreach (Dictionary<string, object> c in Candidates(row))
                        if (ImportAction.S(c, "ad") == name) s = Make(row, name, c);
                    if (s == null) s = Make(row, name, null);
                }
                if (s == null) throw new InvalidOperationException("NAME verilmedi.");
                Run(prj, m[0], s, settings, outDir, report, Io.Param(ctx, "UNDO_CHECK") == "1");
                report["ok"] = true;
            }
            catch (Exception ex) { report["ok"] = false; report["error"] = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                try { Io.WriteJson(Path.Combine(outDir, "ok_hedef.json"), report); } catch (Exception) { }
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }
    }
}
