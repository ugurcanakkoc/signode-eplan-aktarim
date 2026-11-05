// =====================================================================================
// UvpCizimciCihazYerlestir — "Listeden cihaz yerleştir" (B3, 12.10; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Liste: analyzer_v3/cihaz_listesi.py çıktısı (müşteri şeması + Stückliste + varsa program paketi): DT, tam ad, sayfa, ürün kodu
// (kanıtsızsa boş). Çizimci listeden satır seçer; DT ELLE YAZILMAZ (örnek hatalar -18F49→-18F410, -46D21→-49D21).
//   a) Sayfada nesne(ler) seçiliyse: seçili fonksiyonlara DT, ana fonksiyona ürün kodu (parça DB'de tek parçaya çözülürse).
//   b) Değilse açık sayfada tek tıklama: parçanın ŞEMA makrosu (ARTICLE_GROUPSYMBOLMACRO_IEC, yoksa _GROUPSYMBOLMACRO;
//      ARTICLE_MACRO 3D'dir, kullanılmaz — Macros.cs ölçümü) konur, DT + kod yazılır. Makro yoksa yerleştirmez, söyler.
// Canlı (UVP_ olmayan) projede de çalışır: her yazmadan ÖNCE onay penceresi (proje adı + nesne sayısı), yazma TEK UndoStep
// (EPLAN Geri Al). Listede yapı (tam_ad) yoksa yalnız DT yazılır (yapı elle; cihaz başka kutuda olabilir).
//
// Test: UvpCizimciCihazYerlestir /PROJECT:"<UVP_*.elk>" /LIST:"<liste.json>" /DT:"-23K76" /PAGE:"<sayfa|Blatt>" /AT:"x;y"
//       /OUT:"<json>" — yalnız output\p8runs altındaki UVP_ test projesi (Guard); onay sorulmaz, sonuç geri okunur.
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
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.EServices.Ged;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;
using Label = System.Windows.Forms.Label;

namespace Uvp.PdfToP8.Host
{
    public class CihazYerlestirAction : IEplAction
    {
        internal const string RibbonText = "Listeden cihaz yerleştir";
        internal static Dictionary<string, object> Pending;   // etkileşime giden satır (tek kullanıcı, tek etkileşim)
        static string _lastList = "";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciCihazYerlestir";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            if (outFile != "") return Test(ctx, outFile);
            Project prj = null;
            Dictionary<string, object> report = Arac.Rapor("uvp.pdf2p8.cihaz_yerlestir");
            try
            {
                string list = Io.Param(ctx, "LIST");
                if (list == "") list = AskList();
                if (list == "") return true;
                List<Dictionary<string, object>> rows = Load(list);
                SelectionSet sel = new SelectionSet();
                sel.LockProjectByDefault = false;
                List<Function> chosen = new List<Function>();
                foreach (StorableObject so in sel.Selection) { Function f = so as Function; if (f != null) chosen.Add(f); }
                Page page = chosen.Count > 0 ? chosen[0].Page : sel.CurrentlyEdited as Page;
                if (page == null) { Say("Bir şema sayfası açın (cihaz konacak) ya da sayfada cihazı seçin.", MessageBoxIcon.Warning); return true; }
                prj = page.Project;
                report["project"] = prj.ProjectLinkFilePath;
                Dictionary<string, object> row;
                using (CihazSecimForm form = new CihazSecimForm(rows, ProjectDevices(prj), chosen.Count))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.Chosen == null) return true;
                    row = form.Chosen;
                }
                if (chosen.Count > 0)
                {
                    if (!Confirm(prj, chosen.Count + " seçili nesne", row)) return true;
                    report["row"] = row;
                    Dictionary<string, object> r;
                    using (Yazim y = new Yazim(prj, RibbonText + " " + ImportAction.S(row, "dt")))   // kilit + tek Geri Al
                    {
                        r = Write(chosen, row, new List<string>());
                        y.Tamam();
                    }
                    report["written"] = r;
                    report["ok"] = true;
                    Arac.Bitir(prj, "cihaz_yerlestir", RibbonText, report, Summary(r));
                }
                else
                {
                    List<string> log = new List<string>();
                    string macro = MacroOf(ImportAction.S(row, "kod"), log);
                    if (macro == "")
                    {
                        Say("Yerleştirilmedi: " + string.Join("; ", log.ToArray()) + "\nSembolü kendiniz koyun, seçin ve bu düğmeyle DT/kodu yazın.", MessageBoxIcon.Warning);
                        return true;
                    }
                    if (!Confirm(prj, "1 cihaz makrosu (" + Path.GetFileName(macro) + ")", row)) return true;
                    Pending = row;
                    new CommandLineInterpreter().Execute("XGedStartInteractionAction /Name:" + CihazYerlestirInteraction.InteractionName);
                }
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                Arac.Bitir(prj, "cihaz_yerlestir", RibbonText, report, "");
            }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        /// <summary>Canlı projeye yazmadan önce açık onay: proje, nesne sayısı, yazılacak ad/kod, geri alma yolu.</summary>
        static bool Confirm(Project prj, string what, Dictionary<string, object> row)
        {
            string tam = ImportAction.S(row, "tam_ad"), kod = ImportAction.S(row, "kod");
            string text = "Proje: " + ImportAction.Text(delegate { return prj.ProjectName; }) + "\n"
                          + "Değişecek: " + what + "\n"
                          + "DT: " + (tam != "" ? tam : ImportAction.S(row, "dt") + " (yapı listede yok — elle)") + "\n"
                          + "Ürün kodu: " + (kod != "" ? kod : "yazılmaz (" + ImportAction.S(row, "kod_kaynagi") + ")") + "\n\n"
                          + "Geri almak için: EPLAN Geri Al (tek adım). Devam edilsin mi?";
            return MessageBox.Show(text, RibbonText, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        static string AskList()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = RibbonText + " — cihaz listesi (python -X utf8 -m analyzer_v3.cihaz_listesi ...)";
                d.Filter = "Cihaz listesi (*.json)|*.json";
                string root = PanelSettings.Load().Root ?? "";
                d.InitialDirectory = _lastList != "" ? Path.GetDirectoryName(_lastList) : Path.Combine(root, "output");
                if (d.ShowDialog() != DialogResult.OK) return "";
                _lastList = d.FileName;
                return d.FileName;
            }
        }

        internal static List<Dictionary<string, object>> Load(string file)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (object o in (ArrayList)d["satirlar"]) rows.Add((Dictionary<string, object>)o);
            return rows;
        }

        /// <summary>Projedeki cihaz adları '-DT' biçiminde ('=280+E280-18F49:1' → '-18F49').</summary>
        internal static HashSet<string> ProjectDevices(Project prj)
        {
            HashSet<string> names = new HashSet<string>();
            foreach (Function f in new DMObjectsFinder(prj).GetFunctions(new FunctionsFilter()))
            {
                string n = ImportAction.Text(delegate { return f.Name; });
                int plus = n.IndexOf('+'), dash = n.IndexOf('-', plus < 0 ? 0 : plus);
                if (dash < 0) continue;
                n = n.Substring(dash);
                int colon = n.IndexOf(':');
                names.Add(colon > 0 ? n.Substring(0, colon) : n);
            }
            return names;
        }

        static MDPartsDatabase OpenDb(List<string> log)
        {
            try { return new MDPartsManagement().OpenDatabase(); }
            catch (Exception ex) { log.Add("parça veritabanı açılamadı: " + ex.Message); return null; }
        }

        /// <summary>Kodun parçasındaki ŞEMA makrosu; yoksa "" ve neden log'a.</summary>
        internal static string MacroOf(string code, List<string> log)
        {
            if (code == "") { log.Add("listede ürün kodu yok (belirsiz/kod yok)"); return ""; }
            string rule;
            MDPart part = MacroDumpAction.FindPart(OpenDb(log), code, log, out rule);
            if (part == null) { log.Add("kod parça veritabanında tek parçaya çözülmedi: " + code); return ""; }
            string iec = ImportAction.Text(delegate { return part.Properties.ARTICLE_GROUPSYMBOLMACRO_IEC.ToString(); });
            string grp = ImportAction.Text(delegate { return part.Properties.ARTICLE_GROUPSYMBOLMACRO.ToString(); });
            string macro = iec != "" ? iec : grp;
            if (macro == "") log.Add("parçanın şema makrosu yok: " + part.PartNr);
            return macro;
        }

        /// <summary>Fonksiyonlara DT (tam ad varsa yapı alanlarıyla — ImportAction.WriteName), ana fonksiyona parça.</summary>
        internal static Dictionary<string, object> Write(List<Function> functions, Dictionary<string, object> row, List<string> log)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            string dt = ImportAction.S(row, "dt"), tam = ImportAction.S(row, "tam_ad"), kod = ImportAction.S(row, "kod");
            List<object> names = new List<object>();
            Function main = null;
            foreach (Function f in functions)
            {
                try { names.Add(ImportAction.WriteName(f, tam != "" ? tam : dt, "", dt)); }
                catch (Exception ex) { names.Add("ad yazılamadı: " + ex.Message); }
                if (main == null && f.IsMainFunction && f.CanHaveArticleData) main = f;
            }
            r["dt"] = dt;
            r["names"] = names;
            Dictionary<string, object> art = new Dictionary<string, object>();
            art["part_number"] = kod;
            art["written"] = false;
            r["article"] = art;
            if (kod == "") art["reason"] = "NO_CODE:" + ImportAction.S(row, "kod_kaynagi");
            else if (main == null) art["reason"] = "NO_MAIN_FUNCTION";
            else
                try
                {
                    string rule;
                    MDPart part = MacroDumpAction.FindPart(OpenDb(log), kod, log, out rule);
                    if (part == null) art["reason"] = "PART_NOT_IN_DB_OR_AMBIGUOUS";
                    else
                    {
                        art["part_nr"] = part.PartNr;
                        bool present = false;
                        foreach (ArticleReference have in main.ArticleReferences) if (have.PartNr == part.PartNr) present = true;
                        if (present) art["reason"] = "ALREADY_PRESENT";
                        else
                        {
                            string variant = part.Variant ?? "";
                            if (variant == "") main.AddArticleReference(part.PartNr);
                            else main.AddArticleReference(part.PartNr, variant, 1);
                            art["written"] = true;
                        }
                    }
                }
                catch (Exception ex) { art["reason"] = "ERROR"; art["error"] = ex.GetType().Name + ": " + ex.Message; }
            r["log"] = log;
            return r;
        }

        /// <summary>Şema makrosunu (MultiLine) noktaya koyar, konan fonksiyonlara Write.</summary>
        internal static Dictionary<string, object> Place(Page pg, PointD at, Dictionary<string, object> row, List<string> log)
        {
            string macro = MacroOf(ImportAction.S(row, "kod"), log);
            if (macro == "") throw new InvalidOperationException("Makro yok: " + string.Join("; ", log.ToArray()));
            StorableObject[] placed;
            try   // şema sayfasına çok hatlı gösterim (Import.InsertMacro ile aynı); makroda yoksa varsayılan gösterim
            {
                placed = new Insert().WindowMacro(macro, Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType.MultiLine,
                                                  0, pg, at, Insert.MoveKind.Absolute);
            }
            catch (Exception ex)
            {
                log.Add("MultiLine konamadı (" + ex.Message + "), varsayılan gösterim");
                placed = new Insert().WindowMacro(macro, 0, pg, at, Insert.MoveKind.Absolute);
            }
            if (placed == null || placed.Length == 0) throw new InvalidOperationException("makro hiçbir nesne koymadı: " + macro);
            List<Function> fs = new List<Function>();
            foreach (StorableObject so in placed) { Function f = so as Function; if (f != null) fs.Add(f); }
            Dictionary<string, object> r = Write(fs, row, log);
            r["macro"] = macro;
            r["at"] = ImportAction.XY(at);
            r["objects"] = placed.Length;
            return r;
        }

        internal static string Summary(Dictionary<string, object> r)
        {
            Dictionary<string, object> art = (Dictionary<string, object>)r["article"];
            return "Cihaz " + r["dt"] + ": " + ((List<object>)r["names"]).Count + " fonksiyona ad yazıldı.\n"
                   + ((bool)art["written"] ? "Parça yazıldı: " + art["part_nr"] : "Parça YAZILMADI (" + ImportAction.S(art, "reason") + ").")
                   + "\nGeri almak için: EPLAN Geri Al.";
        }

        static PointD Xy(string text)
        {
            string[] v = text.Split(';');
            return new PointD(Guard.Number(v[0], 0), Guard.Number(v[1], 0));
        }

        /// <summary>Otomatik ölçüm: yalnız UVP_ test projesi; listedeki DT'yi makroyla koyar, adları/parçayı geri okur.</summary>
        bool Test(ActionCallingContext ctx, string outFile)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.cihaz_yerlestir";
            report["utc"] = DateTime.UtcNow.ToString("o");
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
                string want = Io.Param(ctx, "PAGE").Replace('\\', '/');
                List<Page> hits = new List<Page>();
                foreach (Page p in prj.Pages) if (p.Name == want || p.Name.EndsWith("/" + want)) hits.Add(p);
                if (hits.Count != 1) throw new InvalidOperationException("Sayfa tek değil (" + hits.Count + "): " + want);
                Dictionary<string, object> row = null;
                foreach (Dictionary<string, object> x in Load(Io.Param(ctx, "LIST")))
                    if (ImportAction.S(x, "dt") == Io.Param(ctx, "DT")) row = x;
                if (row == null) throw new InvalidOperationException("DT listede yok: " + Io.Param(ctx, "DT"));
                report["row"] = row;
                report["in_project_before"] = ProjectDevices(prj).Contains(ImportAction.S(row, "dt"));
                using (Yazim y = new Yazim(prj, RibbonText))   // kilit + tek Geri Al; hata → geri alınır
                {
                    report["placed"] = Place(hits[0], Xy(Io.Param(ctx, "AT")), row, log);
                    y.Tamam();
                }
                report["in_project_after"] = ProjectDevices(prj).Contains(ImportAction.S(row, "dt"));
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

    /// <summary>Tek tıklamalı makro yerleştirme; satır CihazYerlestirAction.Pending'den. OnSuccess EPLAN'ın UndoStep'i içinde.</summary>
    [Interaction(Name = CihazYerlestirInteraction.InteractionName, NameOfBaseInteraction = "", Ordinal = 20, Prio = 20)]
    public class CihazYerlestirInteraction : InsertInteraction
    {
        internal const string InteractionName = "UvpCihazYerlestir";
        PointD _at;

        public override RequestCode OnStart(InteractionContext oContext)
        {
            return CihazYerlestirAction.Pending == null ? RequestCode.Stop : RequestCode.Point;
        }

        public override RequestCode OnPoint(Position oPosition) { _at = oPosition; return RequestCode.Success; }

        public override void OnSuccess(InteractionContext oContext)
        {
            Dictionary<string, object> row = CihazYerlestirAction.Pending;
            CihazYerlestirAction.Pending = null;
            Dictionary<string, object> report = Arac.Rapor("uvp.pdf2p8.cihaz_yerlestir");
            report["row"] = row;
            Project prj = null;
            try
            {
                prj = Page.Project;
                Arac.Kilitle(prj);          // EPLAN etkileşiminin kendi Geri Al adımı içinde
                Dictionary<string, object> r = CihazYerlestirAction.Place(Page, _at, row, new List<string>());
                report["placed"] = r;
                report["ok"] = true;
                Arac.Bitir(prj, "cihaz_yerlestir", CihazYerlestirAction.RibbonText, report, CihazYerlestirAction.Summary(r));
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                report["geri_al_elle"] = true;
                Arac.Bitir(prj, "cihaz_yerlestir", CihazYerlestirAction.RibbonText, report, "");
            }
        }

        public override void OnCancel() { CihazYerlestirAction.Pending = null; }
    }

    /// <summary>Liste penceresi: projede olmayanlar üstte; arama; DT/kod/kaynak/sayfa/uyarı sütunları.</summary>
    internal class CihazSecimForm : Form
    {
        public Dictionary<string, object> Chosen;
        readonly List<Dictionary<string, object>> _rows;
        readonly HashSet<string> _inProject;
        readonly TextBox _search = new TextBox();
        readonly CheckBox _missingOnly = new CheckBox();
        readonly ListView _list = new ListView();

        public CihazSecimForm(List<Dictionary<string, object>> rows, HashSet<string> inProject, int selected)
        {
            _rows = rows;
            _inProject = inProject;
            Text = CihazYerlestirAction.RibbonText + " — müşteri cihaz listesi (" + rows.Count + ")";
            Width = 1000; Height = 600; StartPosition = FormStartPosition.CenterScreen;
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top; top.Height = 36; top.Padding = new Padding(6);
            Label s = new Label(); s.Text = "Ara"; s.AutoSize = true; s.Padding = new Padding(0, 6, 0, 0);
            _search.Width = 200;
            _missingOnly.Text = "yalnız projede olmayanlar"; _missingOnly.AutoSize = true;
            top.Controls.Add(s); top.Controls.Add(_search); top.Controls.Add(_missingOnly);
            _search.TextChanged += delegate { Refill(); };
            _missingOnly.CheckedChanged += delegate { Refill(); };
            _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.HideSelection = false;
            foreach (string[] c in new string[][] { new string[] { "DT", "90" }, new string[] { "Projede", "60" }, new string[] { "Tam ad", "150" },
                     new string[] { "Ürün kodu", "140" }, new string[] { "Kod kaynağı", "170" }, new string[] { "Blatt", "60" },
                     new string[] { "Açıklama", "120" }, new string[] { "Uyarı", "200" } })
                _list.Columns.Add(c[0], int.Parse(c[1]));
            _list.DoubleClick += delegate { Pick(); };
            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 40; bottom.FlowDirection = FlowDirection.RightToLeft;
            Button ok = new Button(); ok.AutoSize = true;
            ok.Text = selected > 0 ? "Seçili " + selected + " nesneye DT/kod yaz" : "Seç ve yerleştir (1 tıklama)";
            ok.Click += delegate { Pick(); };
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
            string q = _search.Text.Trim().ToLowerInvariant();
            List<ListViewItem> missing = new List<ListViewItem>(), present = new List<ListViewItem>();
            foreach (Dictionary<string, object> r in _rows)
            {
                string dt = ImportAction.S(r, "dt");
                bool has = _inProject.Contains(dt);
                if (has && _missingOnly.Checked) continue;
                string line = dt + " " + ImportAction.S(r, "kod") + " " + ImportAction.S(r, "aciklama");
                if (q != "" && line.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                List<string> blatt = new List<string>();
                foreach (object b in ImportAction.A(r.ContainsKey("blatt") ? r["blatt"] : null)) blatt.Add(Convert.ToString(b));
                ListViewItem it = new ListViewItem(new string[] { dt, has ? "var" : "YOK", ImportAction.S(r, "tam_ad"), ImportAction.S(r, "kod"),
                    ImportAction.S(r, "kod_kaynagi"), string.Join(",", blatt.ToArray()), ImportAction.S(r, "aciklama"), ImportAction.S(r, "uyari") });
                it.Tag = r;
                (has ? present : missing).Add(it);
            }
            _list.Items.AddRange(missing.ToArray());
            _list.Items.AddRange(present.ToArray());
            _list.EndUpdate();
        }

        void Pick()
        {
            if (_list.SelectedItems.Count != 1) return;
            Chosen = (Dictionary<string, object>)_list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
