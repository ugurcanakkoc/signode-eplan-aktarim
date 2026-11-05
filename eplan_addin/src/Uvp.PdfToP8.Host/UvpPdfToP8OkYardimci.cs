// =====================================================================================
// UvpCizimciOk — "Kesinti oku adı" (B4 / F2-4, 12.10; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Çizimci sayfada bir ya da birkaç kesinti noktası (ok) seçer, butona basar:
//   1. Pencere projedeki ok adlarını (tam ad + kaç okta) ve isteğe bağlı müşteri listesini (/LIST: analyzer_v3.ok_yardimci
//      --json çıktısı, ad_listesi) gösterir; ad listeden seçilir ya da yazılır (yazım hatası = yetim ok, BAG_AYRIK).
//      "PE: eşsiz ad" düğmesi ilk boş `PE-<n>:1` adını verir (MAIN 10.10 R2 md.5; düz "PE" zincir yapar, MAIN:677).
//   2. Ad yapısızsa (`L1`) okun kendi yapısı, yoksa sayfanın yapısı eklenir: `=280+E280-L1` (MAIN 10.10 13:45);
//      yazım ImportAction.WriteName (tam ad, tutmazsa NameParts). Görünen ad boşsa kısa ad yazılır (boşken EPLAN ad basmıyor).
//   3. Yazmadan ÖNCE onay penceresi: proje adı, yazılacak ok sayısı, eski→yeni ad, yazımdan sonraki aynı adlı ok sayısı
//      ve uyarılar (eşi yok / düz PE / PE zinciri / 3+ üye). Hayır → hiçbir şey yazılmaz.
//   4. Hepsi tek UndoStep (EPLAN Geri Al tek adımda geri getirir); yazım sırasında hata olursa adım geri alınır.
// Canlı (UVP_ olmayan) projede de yalnız bu onay + geri al ile çalışır; yalnız seçili okların adını değiştirir.
//
// Test: UvpCizimciOk /PROJECT:"<UVP_*.elk>" /IDS:"<kimlik;kimlik>" /NAME:"<ad>" /OUT:"<json>" [/UNDO_CHECK:1]
//       — yalnız output\p8runs altındaki UVP_ test projesi (Guard); onay sorulmaz. UNDO_CHECK:1 → yaz, oku, geri al, oku.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;
using Label = System.Windows.Forms.Label;

namespace Uvp.PdfToP8.Host
{
    public class OkYardimciAction : IEplAction
    {
        internal const string RibbonText = "Kesinti oku adı";
        static readonly Regex Yapi = new Regex(@"^(=[^+\-]*)?(\+[^\-]*)?-");
        static readonly Regex PeAdi = new Regex(@"^PE-(\d+):(\d+)$");

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciOk";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            if (outFile != "") return Test(ctx, outFile);
            Project prj = null;
            Dictionary<string, object> r = Arac.Rapor("uvp.pdf2p8.ok_yardimci");
            try
            {
                SelectionSet sel = new SelectionSet();
                sel.LockProjectByDefault = false;
                List<InterruptionPoint> ips = new List<InterruptionPoint>();
                foreach (StorableObject so in sel.Selection)
                {
                    InterruptionPoint ip = so as InterruptionPoint;
                    if (ip != null && !ips.Exists(delegate (InterruptionPoint q) { return ImportAction.Id(q) == ImportAction.Id(ip); })) ips.Add(ip);
                }
                if (ips.Count == 0) { Say("Adı yazılacak kesinti noktalarını (ok) sayfada seçin.", MessageBoxIcon.Warning); return true; }
                prj = ips[0].Project;
                Dictionary<string, int> counts = Counts(prj);
                string name;
                using (OkAdForm form = new OkAdForm(counts, LoadList(Io.Param(ctx, "LIST")), ips.Count))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.Chosen == "") return true;
                    name = form.Chosen;
                }
                List<string> warn;
                string plan = Plan(ips, name, counts, out warn);
                string ask = "Proje: " + ImportAction.Text(delegate { return prj.ProjectName; }) + "\n" + ips.Count
                             + " kesinti noktasının adı değişecek:\n" + plan + (warn.Count > 0 ? "\n" + string.Join("\n", warn.ToArray()) : "")
                             + "\n\nGeri almak için EPLAN Geri Al (tek adım). Yazılsın mı?";
                if (MessageBox.Show(ask, RibbonText, MessageBoxButtons.YesNo, warn.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
                    return true;
                Run(ips, name, r, false);
                r["ok"] = true;
                Arac.Bitir(prj, "ok_yardimci", RibbonText, r, "Yazıldı (" + ips.Count + " ok):\n" + string.Join("\n", ((List<string>)r["lines"]).ToArray()));
            }
            catch (Exception ex)
            {
                r["ok"] = false;
                r["error"] = ex.GetType().Name + ": " + ex.Message;
                Arac.Bitir(prj, "ok_yardimci", RibbonText, r, "");
            }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        internal static string Kisa(string name) { return Yapi.Replace(name ?? "", "", 1); }

        static Dictionary<string, int> Counts(Project prj)
        {
            Dictionary<string, int> c = new Dictionary<string, int>();
            foreach (InterruptionPoint ip in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
            {
                string n = ImportAction.Text(delegate { return ip.Name; });
                c[n] = (c.ContainsKey(n) ? c[n] : 0) + 1;
            }
            return c;
        }

        /// <summary>analyzer_v3.ok_yardimci --json çıktısının ad_listesi'nden adlar (yoksa boş).</summary>
        static List<string> LoadList(string path)
        {
            List<string> l = new List<string>();
            if (path == "" || !File.Exists(path)) return l;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (d.ContainsKey("ad_listesi"))
                foreach (object o in (ArrayList)d["ad_listesi"]) l.Add(ImportAction.S((Dictionary<string, object>)o, "ad"));
            return l;
        }

        /// <summary>Yazılacak tam ad: ad '=' ile başlıyorsa olduğu gibi; değilse okun kendi yapısı, yoksa sayfanın yapısı + '-' + ad.</summary>
        internal static string Tag(InterruptionPoint ip, string name)
        {
            if (name.StartsWith("=")) return name;
            string cur = ImportAction.Text(delegate { return ip.Name; });
            int plus = cur.IndexOf('+'), dash = plus < 0 ? -1 : cur.IndexOf('-', plus + 1);
            if (cur.StartsWith("=") && plus >= 2 && dash > plus + 1) return cur.Substring(0, dash + 1) + name;
            string pn = ImportAction.Text(delegate { return ip.Page.Name; });
            int cut = pn.IndexOfAny(new char[] { '&', '/' });
            string s = cut > 0 ? pn.Substring(0, cut) : "";
            plus = s.IndexOf('+');
            return s.StartsWith("=") && plus >= 2 && plus < s.Length - 1 ? s + "-" + name : name;
        }

        /// <summary>Onay metni (eski → yeni) ve uyarılar; yazmaz.</summary>
        static string Plan(List<InterruptionPoint> ips, string name, Dictionary<string, int> counts, out List<string> warn)
        {
            warn = new List<string>();
            Dictionary<string, int> after = new Dictionary<string, int>();
            StringBuilder sb = new StringBuilder();
            foreach (InterruptionPoint ip in ips)
            {
                string cur = ImportAction.Text(delegate { return ip.Name; }), tag = Tag(ip, name);
                if (!after.ContainsKey(tag)) after[tag] = counts.ContainsKey(tag) ? counts[tag] : 0;
                if (cur != tag) after[tag]++;
                sb.Append("  ").Append(ImportAction.Text(delegate { return ip.Page.Name; })).Append(": '").Append(cur).Append("' → '").Append(tag).Append("'\n");
            }
            foreach (KeyValuePair<string, int> kv in after)
            {
                string k = Kisa(kv.Key);
                sb.Append("Yazımdan sonra '").Append(kv.Key).Append("' adlı ok: ").Append(kv.Value).Append('\n');
                if (k.StartsWith("PE") && !PeAdi.IsMatch(k))
                    warn.Add("UYARI: düz/desensiz PE adı ('" + k + "') EPLAN'da PE zinciri yapar (MAIN:677); eşsiz PE-<n>:<k> kullanın.");
                else if (PeAdi.IsMatch(k) && kv.Value > 2)
                    warn.Add("UYARI: '" + k + "' " + kv.Value + " okta: PE noktası en çok iki okta olur (zincir).");
                else if (kv.Value == 1)
                    warn.Add("UYARI: '" + kv.Key + "' adının eşi yok, ok yetim kalır (çapraz referans almaz).");
                else if (kv.Value > 2)
                    warn.Add("Bilgi: '" + kv.Key + "' " + kv.Value + " okta; EPLAN aynı adlı okları ikişer eşler (MAIN:1117).");
            }
            return sb.ToString();
        }

        /// <summary>Tek kilit + tek UndoStep içinde yazar; hata olursa adım geri alınır. undoAfter: test için yaz-oku-geri al.</summary>
        static void Run(List<InterruptionPoint> ips, string name, Dictionary<string, object> report, bool undoAfter)
        {
            List<string> lines = new List<string>();
            List<object> rows = new List<object>();
            using (LockingStep step = new LockingStep())
            using (UndoManager um = new UndoManager())
            {
                Arac.Kilitle(ips[0].Project);   // düğme yolu nesneleri kilitsiz verir (14.10 LockingException)
                UndoStep undo = um.CreateUndoStep();
                undo.SetUndoDescription("UVP · " + RibbonText + " (" + name + ")");
                try
                {
                    foreach (InterruptionPoint ip in ips)
                    {
                        string old = ImportAction.Text(delegate { return ip.Name; }), tag = Tag(ip, name), plain = Kisa(tag);
                        string how = tag.StartsWith("=") ? ImportAction.WriteName(ip, tag, "", plain) : "yapısız";
                        if (!tag.StartsWith("=")) ip.Name = plain;
                        if (ImportAction.Text(delegate { return ip.VisibleName; }) == "") ip.VisibleName = plain;
                        string got = ImportAction.Text(delegate { return ip.Name; });
                        lines.Add("'" + old + "' → '" + got + "'" + (got != tag ? "  (beklenen '" + tag + "': " + how + ")" : ""));
                        rows.Add(Row(ip, old, tag, how));
                    }
                    report["written"] = rows;
                    if (undoAfter)
                    {
                        undo.DoUndo();
                        List<object> back = new List<object>();
                        foreach (InterruptionPoint ip in ips) back.Add(Row(ip, "", "", ""));
                        report["after_undo"] = back;
                    }
                }
                catch (Exception)
                {
                    try { undo.DoUndo(); } catch (Exception) { }
                    throw;
                }
                finally { try { undo.CloseUndo(); } catch (Exception) { } }
            }
            report["lines"] = lines;
        }

        static Dictionary<string, object> Row(InterruptionPoint ip, string old, string tag, string how)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["object_id"] = ImportAction.Id(ip);
            if (old != "") { r["old_name"] = old; r["expected"] = tag; r["name_write"] = how; }
            r["name"] = ImportAction.Text(delegate { return ip.Name; });
            r["visible_name"] = ImportAction.Text(delegate { return ip.VisibleName; });
            r["cross_reference"] = ImportAction.Text(delegate { return ip.Properties.INTERRUPTIONPOINT_CROSSREFERENCE.ToString(); });
            return r;
        }

        bool Test(ActionCallingContext ctx, string outFile)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.ok_yardimci";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(OkYardimciAction).Assembly.GetName().Name;
            ProjectManager pm = Arac.Pm(ctx);
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                report["project"] = file;
                string name = Io.Param(ctx, "NAME").Trim();
                if (name == "") throw new InvalidOperationException("NAME verilmedi.");
                List<InterruptionPoint> all = new List<InterruptionPoint>(new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()));
                List<InterruptionPoint> ips = new List<InterruptionPoint>();
                foreach (string raw in Io.Param(ctx, "IDS").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    List<InterruptionPoint> m = all.FindAll(delegate (InterruptionPoint ip) { return ImportAction.Id(ip) == raw.Trim(); });
                    if (m.Count != 1) throw new InvalidOperationException("Kimlik tek oka düşmedi (" + m.Count + "): " + raw);
                    ips.Add(m[0]);
                }
                if (ips.Count == 0) throw new InvalidOperationException("IDS verilmedi.");
                List<string> warn;
                report["plan"] = Plan(ips, name, Counts(prj), out warn);
                report["warnings"] = warn;
                Run(ips, name, report, Io.Param(ctx, "UNDO_CHECK") == "1");
                Dictionary<string, int> c = Counts(prj);
                report["same_name_count"] = c.ContainsKey(Tag(ips[0], name)) ? c[Tag(ips[0], name)] : 0;
                report["ok"] = true;
            }
            catch (Exception ex) { report["ok"] = false; report["error"] = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                Io.WriteJson(outFile, report);
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }

        /// <summary>İlk boş PE klemensi: 'PE-<max n + 1>:1'.</summary>
        internal static string PeNext(IEnumerable<string> names)
        {
            int max = 0;
            foreach (string n in names)
            {
                Match m = PeAdi.Match(Kisa(n));
                if (m.Success) max = Math.Max(max, int.Parse(m.Groups[1].Value));
            }
            return "PE-" + (max + 1) + ":1";
        }
    }

    /// <summary>Ad seçimi: projedeki ok adları (kaç okta) + müşteri listesi; arama kutusu aynı zamanda yazılacak ad.</summary>
    internal class OkAdForm : Form
    {
        public string Chosen = "";
        readonly TextBox _text = new TextBox();
        readonly ListView _list = new ListView();
        readonly Dictionary<string, int> _counts;
        readonly List<string> _extra;

        public OkAdForm(Dictionary<string, int> counts, List<string> extra, int selected)
        {
            _counts = counts; _extra = extra;
            Text = OkYardimciAction.RibbonText + " — " + selected + " ok seçili; projede " + counts.Count + " ad";
            Width = 620; Height = 520; StartPosition = FormStartPosition.CenterScreen;
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top; top.Height = 40; top.Padding = new Padding(6);
            Label l = new Label(); l.Text = "Ad (ara / yaz)"; l.AutoSize = true; l.Padding = new Padding(0, 6, 0, 0);
            _text.Width = 300;
            _text.TextChanged += delegate { Refill(); };
            Button pe = new Button(); pe.Text = "PE: eşsiz ad"; pe.AutoSize = true;
            pe.Click += delegate { _text.Text = OkYardimciAction.PeNext(_counts.Keys); };
            top.Controls.Add(l); top.Controls.Add(_text); top.Controls.Add(pe);
            _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.HideSelection = false;
            _list.Columns.Add("Ad", 330); _list.Columns.Add("Projede ok", 80); _list.Columns.Add("Kaynak", 150);
            _list.DoubleClick += delegate { if (_list.SelectedItems.Count == 1) Pick(_list.SelectedItems[0].Text); };
            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 40; bottom.FlowDirection = FlowDirection.RightToLeft;
            Button ok = new Button(); ok.Text = "Seçili oklara yaz…"; ok.AutoSize = true;
            ok.Click += delegate { Pick(_list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Text : _text.Text); };
            Button cancel = new Button(); cancel.Text = "İptal"; cancel.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(cancel); bottom.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(_list); Controls.Add(top); Controls.Add(bottom);
            Refill();
        }

        void Refill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            string q = _text.Text.Trim().ToLowerInvariant();
            List<string> names = new List<string>(_counts.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (string n in names)
                if (n != "" && (q == "" || n.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0))
                {
                    ListViewItem it = new ListViewItem(new string[] { n, _counts[n].ToString(), _counts[n] == 1 ? "proje (YETİM)" : "proje" });
                    _list.Items.Add(it);
                }
            foreach (string n in _extra)
                if (n != "" && (q == "" || n.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0))
                    _list.Items.Add(new ListViewItem(new string[] { n, "", "müşteri listesi" }));
            _list.EndUpdate();
        }

        void Pick(string name)
        {
            name = (name ?? "").Trim();
            if (name == "") { _text.Focus(); return; }
            Chosen = name;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
