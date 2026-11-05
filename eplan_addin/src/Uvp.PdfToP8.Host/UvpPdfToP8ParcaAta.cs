// =====================================================================================
// UvpCizimciParcaAta — "Parça ata" (fikir A5 / F1.5; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Çizimci 'UVP: ÜRÜN KODU BELİRSİZ / EPLAN'DA YOK / BULUNAMADI' işaretli cihazın bir fonksiyonunu seçer → pencere
// adayları gösterir (<motor klasörü>\output\catalog\parca_adaylari.json = analyzer_v3/parca_sihirbaz.py; Stückliste
// adayı + parça DB durumu + uç kanıtı) ya da kod elle yazılır. Kod parça DB'de TEK parçaya çözülmezse (Macros.FindPart)
// YAZILMAZ (uydurma yok). Onay penceresi: proje adı, cihaz, parça, yazılacak/silinecek nesne sayısı. Sonra tek UndoStep:
// cihazın ana fonksiyonuna parça referansı (AddArticleReference; var olan parça silinmez) + o cihazın parça işaretleri
// ('UVP: ÜRÜN KODU …' UVP_KONTROL katmanında) silinir. EPLAN Geri Al tek adımda geri getirir. Parça DB'sine yazmaz.
// 'UÇLAR PARÇAYA UYMUYOR' işareti silinmez (teller elle bağlanacak). Kontrol listesi sayfası bir sonraki aktarımda kurulur.
//
// Test: UvpCizimciParcaAta /PROJECT:"<UVP_*.elk>" /DEVICE:"=280+E280-3Q22" /CODE:"3RV2011-0GA10" /OUT:"<json>"
//       — yalnız output\p8runs altındaki UVP_ test projesi (Guard); onay sorulmaz, geri okuma rapora yazılır.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;
using Label = System.Windows.Forms.Label;

namespace Uvp.PdfToP8.Host
{
    public class ParcaAtaAction : IEplAction
    {
        internal const string RibbonText = "Parça ata";
        // kontrol.py KINDS etiketleri: parça atanınca anlamı biten işaretler (UC_PARCAYA_UYMUYOR bilinçli olarak yok).
        // 12.10 17:30: yeni etiketler (ürün kodu ': ÜRETİCİ KOD' ayrıntısıyla yazıda); eski adlar eski projeler için.
        static readonly string[] Labels = { "ÜRÜN KODU BELİRSİZ", "ÜRÜN DB'DE YOK – MAKRO EKSİK", "ÜRÜN KODU YOK",
                                            "ÜRÜN KODU EPLAN'DA YOK", "ÜRÜN KODU BULUNAMADI" };

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciParcaAta";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            if (outFile != "") return Test(ctx, outFile);
            Project prj = null;
            Dictionary<string, object> report = Arac.Rapor("uvp.pdf2p8.parca_ata");
            try
            {
                SelectionSet sel = new SelectionSet();
                sel.LockProjectByDefault = false;
                HashSet<string> names = new HashSet<string>();
                foreach (StorableObject so in sel.Selection)
                {
                    Function f = so as Function;
                    if (f == null) continue;
                    names.Add(ImportAction.Text(delegate { return f.Name; }));
                    prj = f.Project;
                }
                if (names.Count != 1) { Say("İşaretli cihazın TEK bir fonksiyonunu seçin (seçili cihaz: " + names.Count + ").", MessageBoxIcon.Warning); return true; }
                string name = new List<string>(names)[0];
                string root = Io.Param(ctx, "ROOT");
                Dictionary<string, object> dev = Candidates(root == "" ? PanelSettings.Load().Root : root, name);
                string code;
                using (ParcaSecimForm form = new ParcaSecimForm(name, dev))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.Code == "") return true;
                    code = form.Code;
                }
                List<string> log = new List<string>();
                Plan p = Prepare(prj, name, code, log);
                if (p.Refusal != null) { Say(p.Refusal, MessageBoxIcon.Warning); return true; }
                if (MessageBox.Show("Proje: " + Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath) + "\nCihaz: " + name
                        + "\nParça: " + p.Part.PartNr + " (kod " + code + ", " + p.Rule + ")\nYazılacak: 1 fonksiyona 1 parça referansı"
                        + "\nSilinecek 'UVP:' işareti: " + p.Marks.Count + "\n\nEPLAN Geri Al (Ctrl+Z) tek adımda geri getirir. Devam edilsin mi?",
                        RibbonText, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;
                report["project"] = prj.ProjectLinkFilePath;
                report["device"] = name;
                report["code"] = code;
                Dictionary<string, object> r = Apply(p);
                report["apply"] = r;
                report["ok"] = true;
                Arac.Bitir(prj, "parca_ata", RibbonText, report, (bool)r["written"] ? "Parça yazıldı: " + p.Part.PartNr + "\nSilinen işaret: " + r["removed_marks"]
                                       : "Parça zaten vardı (" + p.Part.PartNr + "); silinen işaret: " + r["removed_marks"]);
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                Arac.Bitir(prj, "parca_ata", RibbonText, report, "");
            }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        internal class Plan
        {
            public Function Target;
            public MDPart Part;
            public string Rule = "", Refusal;
            public List<Text> Marks = new List<Text>();
        }

        /// <summary>Yazmadan önce her şeyi bulur: ana fonksiyon, parça (DB'de tek), silinecek işaretler. Bulamazsa Refusal.</summary>
        internal static Plan Prepare(Project prj, string name, string code, List<string> log)
        {
            Plan p = new Plan();
            string shortName = "-" + name.Substring(name.LastIndexOf('-') + 1);
            Function main = null, any = null;
            foreach (Page pg in prj.Pages)
            {
                foreach (Function f in pg.Functions)
                {
                    if (ImportAction.Text(delegate { return f.Name; }) != name || !f.CanHaveArticleData) continue;
                    if (any == null) any = f;
                    if (main == null && f.IsMainFunction) main = f;
                }
                foreach (Placement pl in pg.AllPlacements)
                {
                    Text t = pl as Text;
                    if (t == null || ImportAction.Text(delegate { return t.Layer == null ? "" : t.Layer.Name; }) != Kontrol.LayerName) continue;
                    string s = Plain(t);
                    foreach (string label in Labels)
                        if (s == "UVP: " + label + " " + shortName || s.StartsWith("UVP: " + label + " " + shortName + ": "))
                            p.Marks.Add(t);
                }
            }
            p.Target = main ?? any;
            if (p.Target == null) { p.Refusal = name + ": parça taşıyabilen (ana) fonksiyon projede yok."; return p; }
            MDPartsDatabase db = null;
            try { db = new MDPartsManagement().OpenDatabase(); } catch (Exception ex) { log.Add("parça veritabanı açılamadı: " + ex.Message); }
            int mark = log.Count;
            p.Part = MacroDumpAction.FindPart(db, code, log, out p.Rule);
            if (p.Part == null)
                p.Refusal = "Kod parça veritabanında TEK parçaya çözülmedi, hiçbir şey yazılmadı: " + code
                            + (db == null ? " (veritabanı açılamadı)" : log.Count > mark ? " (" + log[log.Count - 1] + ")" : " (bulunamadı)");
            return p;
        }

        /// <summary>Tek UndoStep: parça referansı + işaret silme. Var olan referans ikinci kez yazılmaz.</summary>
        internal static Dictionary<string, object> Apply(Plan p)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["part_nr"] = p.Part.PartNr;
            r["match_rule"] = p.Rule;
            r["function"] = ImportAction.Id(p.Target);
            bool present = false;
            foreach (ArticleReference have in p.Target.ArticleReferences) if (have.PartNr == p.Part.PartNr) present = true;
            int removed = 0;
            using (Yazim y = new Yazim(p.Target.Project, RibbonText))   // kilit + tek Geri Al; hata → geri alınır
            {
                if (!present)
                {
                    string variant = p.Part.Variant ?? "";
                    if (variant == "") p.Target.AddArticleReference(p.Part.PartNr);
                    else p.Target.AddArticleReference(p.Part.PartNr, variant, 1);
                }
                foreach (Text t in p.Marks) { t.Remove(); removed++; }
                y.Tamam();
            }
            r["written"] = !present;
            r["removed_marks"] = removed;
            return r;
        }

        static string Plain(Text t)
        {
            string s = ImportAction.Text(delegate { return t.Contents.GetString(ISOCode.Language.L___); });
            if (s == "") s = ImportAction.Text(delegate { return t.Contents.GetAsString(); });
            int at = s.IndexOf('@');
            if (s.StartsWith("??_??@") && at >= 0) s = s.Substring(at + 1);
            return s.TrimEnd(';').Trim();
        }

        /// <summary>parca_adaylari.json'da cihaz: tam ad, yoksa dosyada TEK olan kısa ad ('-3Q22'). Dosya/kayıt yoksa null.</summary>
        internal static Dictionary<string, object> Candidates(string root, string name)
        {
            string file = Path.Combine(root ?? "", "output", "catalog", "parca_adaylari.json");
            if (!File.Exists(file)) return null;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            string shortName = "-" + name.Substring(name.LastIndexOf('-') + 1);
            List<Dictionary<string, object>> bySuffix = new List<Dictionary<string, object>>();
            foreach (object o in ImportAction.A(d.ContainsKey("devices") ? d["devices"] : null))
            {
                Dictionary<string, object> dev = ImportAction.D(o);
                if (ImportAction.S(dev, "device_tag") == name) return dev;
                if (ImportAction.S(dev, "short") == shortName) bySuffix.Add(dev);
            }
            return bySuffix.Count == 1 ? bySuffix[0] : null;
        }

        /// <summary>Otomatik ölçüm: yalnız UVP_ test projesi; onaysız Prepare+Apply, sonra fonksiyonun parça listesi ve kalan işaret.</summary>
        bool Test(ActionCallingContext ctx, string outFile)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.parca_ata";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(ParcaAtaAction).Assembly.GetName().Name;
            ProjectManager pm = Arac.Pm(ctx);
            Project prj = null;
            bool close = false;
            List<string> log = new List<string>();
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                report["project"] = file;
                string name = Io.Param(ctx, "DEVICE"), code = Io.Param(ctx, "CODE");
                Plan p = Prepare(prj, name, code, log);
                report["marks_found"] = p.Marks.Count;
                if (p.Refusal != null) throw new InvalidOperationException(p.Refusal);
                report["applied"] = Apply(p);
                List<object> parts = new List<object>();
                foreach (ArticleReference a in p.Target.ArticleReferences) parts.Add(a.PartNr);
                report["readback_parts"] = parts;
                report["readback_marks_left"] = Prepare(prj, name, code, new List<string>()).Marks.Count;
                report["ok"] = true;
            }
            catch (Exception ex) { report["ok"] = false; report["error"] = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                report["log"] = log;
                Io.WriteJson(outFile, report);
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }
    }

    /// <summary>Aday listesi + elle kod. Öneri (TEK_ADAY) ön seçili; durum sütunu parca_sihirbaz.py'nin kanıtı.</summary>
    internal class ParcaSecimForm : Form
    {
        readonly TextBox _code = new TextBox();
        readonly ListView _list = new ListView();
        public string Code { get { return _code.Text.Trim(); } }

        public ParcaSecimForm(string name, Dictionary<string, object> dev)
        {
            Text = ParcaAtaAction.RibbonText + " — " + name + (dev == null ? " (aday listesi yok: python -X utf8 -m analyzer_v3.parca_sihirbaz <koşu> --readback)"
                                                                   : " — " + ImportAction.S(dev, "verdict"));
            Width = 900; Height = 420; StartPosition = FormStartPosition.CenterScreen;
            _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.HideSelection = false;
            foreach (string[] c in new string[][] { new string[] { "Kod", "170" }, new string[] { "Açıklama (Stückliste)", "200" },
                     new string[] { "Üretici", "80" }, new string[] { "Parça DB / uç kanıtı", "260" }, new string[] { "EPLAN parça no", "150" } })
                _list.Columns.Add(c[0], int.Parse(c[1]));
            string suggestion = dev == null ? "" : ImportAction.S(dev, "suggestion");
            foreach (object o in ImportAction.A(dev == null ? null : (dev.ContainsKey("candidates") ? dev["candidates"] : null)))
            {
                Dictionary<string, object> c = ImportAction.D(o);
                ListViewItem it = new ListViewItem(new string[] { ImportAction.S(c, "code"), ImportAction.S(c, "description"),
                    ImportAction.S(c, "manufacturer"), ImportAction.S(c, "state"), ImportAction.S(c, "part_nr") });
                if (ImportAction.S(c, "code") == suggestion) { it.Selected = true; it.Font = new System.Drawing.Font(_list.Font, System.Drawing.FontStyle.Bold); }
                _list.Items.Add(it);
            }
            _list.SelectedIndexChanged += delegate { if (_list.SelectedItems.Count == 1) _code.Text = _list.SelectedItems[0].Text; };
            _code.Text = suggestion;
            _code.Width = 200;
            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 40; bottom.FlowDirection = FlowDirection.RightToLeft;
            Button ok = new Button(); ok.Text = "Ata (onay sorulur)"; ok.AutoSize = true; ok.DialogResult = DialogResult.OK;
            Button cancel = new Button(); cancel.Text = "İptal"; cancel.DialogResult = DialogResult.Cancel;
            Label l = new Label(); l.Text = "Kod (listeden ya da elle)"; l.AutoSize = true; l.Padding = new Padding(0, 6, 0, 0);
            bottom.Controls.Add(cancel); bottom.Controls.Add(ok); bottom.Controls.Add(_code); bottom.Controls.Add(l);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(_list); Controls.Add(bottom);
        }
    }
}
