// =====================================================================================
// UvpCizimciKabloSec — "Kablo seç ve yerleştir" (kullanıcı 12.10, çizimci aracı; PDF→P8 aktarımının parçası DEĞİL)
//
// Liste: <motor klasörü>\output\catalog\kablo_listesi.json = UVP Standartları xlsx 'Çoklu Kablo Kodu' (analyzer_v3/kablo_secici.py
// kurar; yoksa pencere komutu söyler). Çizimci filtreler (tür / blendaj / PE / damar / kesit / arama) → listeden tıklar:
//   a) Sayfada TEK kablo (kablo tanım çizgisi) seçiliyse: ürün kodu o kabloya yazılır (kabloyu çizimci çekmiştir).
//   b) Değilse açık sayfada iki tıklama ile kablo tanım çizgisi çizilir (UvpKabloCizgisi etkileşimi; yatay/düşey'e
//      oturtulur), EPLAN kestiği tellere damar noktası (CDP) koyar, ürün kodu yazılır.
// Yazılan: parça referansı (kod parça DB'de TEK parçaya çözülürse; Kablo.WritePart), kablo tipi ve damar sayısı listeden.
// Kablo kesiti yazılmaz (MAIN.md:465; ölçüm 12.10: kendi kesiti olmayan damar bağı kablonun kesitini alıyor); kesit parçada.
// Herhangi bir projede çalışır (sıfırdan çizilen dahil) ama YALNIZ kullanıcı tıklamasıyla ve TEK kablo nesnesi yazar; tek UndoStep (EPLAN Geri Al).
//
// Test: UvpCizimciKabloSec /PROJECT:"<UVP_*.elk>" /PAGE:"<sayfa>" /START:"x;y" /END:"x;y" /CODE:"<kod>" /OUT:"<json>"
//       /NAME:"-20W99" [/CS:1 kabloya kesit de yaz (deney)] [/ROOT:<motor klasörü>]
//       — yalnız output\p8runs altındaki UVP_ test projesi (Guard). /PAGE tam ad ya da Blatt no. ('20').
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.EObjects;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.EServices.Ged;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;
using Label = System.Windows.Forms.Label;

namespace Uvp.PdfToP8.Host
{
    public class KabloSeciciAction : IEplAction
    {
        internal const string RibbonText = "Kablo seç ve yerleştir";
        internal static Dictionary<string, object> Pending;     // etkileşime giden seçim (tek kullanıcı, tek etkileşim)
        internal static string PendingName;

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciKabloSec";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            if (outFile != "") return Test(ctx, outFile);
            Project prj = null;
            Dictionary<string, object> report = Arac.Rapor("uvp.pdf2p8.kablo_sec");
            try
            {
                string root = Io.Param(ctx, "ROOT");
                List<Dictionary<string, object>> rows = Load(root == "" ? PanelSettings.Load().Root : root);
                SelectionSet sel = new SelectionSet();
                sel.LockProjectByDefault = false;
                List<Cable> cables = new List<Cable>();
                foreach (StorableObject so in sel.Selection) { Cable c = so as Cable; if (c != null) cables.Add(c); }
                if (cables.Count > 1) { Say("Birden çok kablo seçili; tek kablo seçin.", MessageBoxIcon.Warning); return true; }
                Page page = cables.Count == 1 ? cables[0].Page : sel.CurrentlyEdited as Page;
                if (page == null) { Say("Bir şema sayfası açın (kablo çizilecek) ya da sayfada tek kabloyu seçin.", MessageBoxIcon.Warning); return true; }
                Dictionary<string, object> row;
                string cableName;
                using (KabloSecimForm form = new KabloSecimForm(rows, cables.Count == 1 ? cables[0] : null))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.Chosen == null) return true;
                    row = form.Chosen;
                    cableName = form.CableName;
                }
                if (cables.Count == 1)
                {
                    prj = page.Project;
                    report["project"] = prj.ProjectLinkFilePath;
                    report["row"] = row;
                    Dictionary<string, object> r;
                    using (Yazim y = new Yazim(prj, RibbonText))   // kilit + tek Geri Al; hata → geri alınır
                    {
                        r = Write(cables[0], row, false, new List<string>());
                        y.Tamam();
                    }
                    report["written"] = r;
                    report["ok"] = true;
                    Arac.Bitir(prj, "kablo_sec", RibbonText, report, Summary(r));
                }
                else
                {
                    Pending = row;
                    PendingName = cableName;
                    new CommandLineInterpreter().Execute("XGedStartInteractionAction /Name:" + KabloCizgisiInteraction.InteractionName);
                }
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                Arac.Bitir(prj, "kablo_sec", RibbonText, report, "");
            }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        internal static string Summary(Dictionary<string, object> r)
        {
            Dictionary<string, object> art = r.ContainsKey("article") ? (Dictionary<string, object>)r["article"] : null;
            return "Kablo " + r["name"] + ": " + r["code"] + " " + r["type"] + " " + r["size"] + "\n"
                   + (art != null && art["written"] is bool && (bool)art["written"]
                       ? "Parça yazıldı: " + art["part_nr"]
                       : "Parça YAZILMADI (" + (art == null ? "" : ImportAction.S(art, "reason")) + "): kod parça veritabanında tek parçaya çözülmedi. Tip/damar/kesit listeden yazıldı.")
                   + (r.ContainsKey("cdp_created") ? "\nDamar noktası: " + r["cdp_created"] : "");
        }

        /// <summary>Kablo listesi (kablo_secici.py önbelleği). Yoksa ne yapılacağını söyleyen hata.</summary>
        internal static List<Dictionary<string, object>> Load(string root)
        {
            string file = Path.Combine(root ?? "", "output", "catalog", "kablo_listesi.json");
            if (!File.Exists(file))
                throw new FileNotFoundException("Kablo listesi yok: " + file + "\nMotor klasöründe: python -X utf8 -m analyzer_v3.kablo_secici yap");
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (object o in (ArrayList)d["rows"]) rows.Add((Dictionary<string, object>)o);
            return rows;
        }

        internal static string Mm2(Dictionary<string, object> row)
        {
            return Convert.ToDouble(row["mm2"], CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        /// <summary>Kabloya listeden tip, damar sayısı, (section ise) kesit ve parça.</summary>
        internal static Dictionary<string, object> Write(Cable cab, Dictionary<string, object> row, bool section, List<string> log)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["code"] = ImportAction.S(row, "code");
            r["type"] = ImportAction.S(row, "type");
            r["size"] = ImportAction.S(row, "size");
            cab.Properties.FUNC_CABLETYPE = ImportAction.S(row, "type");
            cab.Properties.FUNC_CABLEWIRECOUNT = ImportAction.S(row, "cores");
            if (section)
            {
                cab.Properties.FUNC_CABLEWIRECROSSSECTION = Mm2(row);
                cab.Properties.FUNC_CABLE_CROSSSECTION_UNIT = 1;
            }
            r["article"] = Kablo.WritePart(cab, ImportAction.S(row, "code"), OpenDb(log), log);
            string name = ImportAction.Text(delegate { return cab.Name; });
            r["name"] = name;
            return r;
        }

        static MDPartsDatabase OpenDb(List<string> log)
        {
            try { return new MDPartsManagement().OpenDatabase(); }
            catch (Exception ex) { log.Add("parça veritabanı açılamadı: " + ex.Message); return null; }
        }

        /// <summary>İki nokta arasında kablo tanım çizgisi (CABDL) + Write. Create'in kurduğu yeni CDP'ler kablo adını alır.</summary>
        internal static Dictionary<string, object> Place(Page pg, PointD a, PointD b, string cableName, Dictionary<string, object> row, bool section, List<string> log)
        {
            if (string.IsNullOrEmpty(cableName)) throw new InvalidOperationException("Kablo adı (DT) boş; damar noktaları kabloya bağlanmaz.");
            if (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y)) b = new PointD(b.X, a.Y); else b = new PointD(a.X, b.Y);   // yatay/düşey
            HashSet<string> before = new HashSet<string>();
            foreach (Function f in pg.Functions) if (f is ConnectionDefinitionPoint) before.Add(ImportAction.Id(f));
            SymbolVariant sv = ImportAction.SymbolOf(pg.Project, "SPECIAL", "CABDL")[0];
            Cable cab = new Cable();
            cab.Create(pg, sv, a, b);
            // Sayfanın yapısı ('=280+E280&EFS1/28' → '=280+E280') + çizimcinin adı; yapısız (sıfırdan) projede ad olduğu gibi.
            // K28 ölçümü: Create CDP'leri adsız kurar; damar = kablo adını taşıyan CDP → her yeni CDP'ye aynı ad.
            string pageName = ImportAction.Text(delegate { return pg.Name; });
            int amp = pageName.IndexOfAny(new char[] { '&', '/' });
            string tag = (amp > 0 && pageName.StartsWith("=") ? pageName.Substring(0, amp) : "") + cableName;
            List<object> names = new List<object>();
            names.Add(ImportAction.WriteName(cab, tag, "", cableName));
            try { cab.VisibleName = cableName; } catch (Exception ex) { log.Add("görünen ad: " + ex.Message); }   // boşsa DT basılmaz (Kablo.Place)
            int fresh = 0;
            foreach (Function f in pg.Functions)
            {
                ConnectionDefinitionPoint d = f as ConnectionDefinitionPoint;
                if (d == null || before.Contains(ImportAction.Id(d))) continue;
                fresh++;
                names.Add(ImportAction.WriteName(d, tag, "", cableName));
            }
            Dictionary<string, object> r = Write(cab, row, section, log);
            r["name_write"] = names;
            r["cdp_created"] = fresh;
            r["start"] = ImportAction.XY(a);
            r["end"] = ImportAction.XY(b);
            r["object_id"] = ImportAction.Id(cab);
            return r;
        }

        static PointD Xy(string text)
        {
            string[] v = text.Split(';');
            return new PointD(Guard.Number(v[0], 0), Guard.Number(v[1], 0));
        }

        /// <summary>Otomatik ölçüm: yalnız UVP_ test projesi; listedeki kodla Place, sonra o sayfanın kablo geri okuması.</summary>
        bool Test(ActionCallingContext ctx, string outFile)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.kablo_sec";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(KabloSeciciAction).Assembly.GetName().Name;
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
                List<Page> hits = new List<Page>();          // tam ad ya da '/20' sonu (Blatt numarası)
                foreach (Page p in prj.Pages) if (p.Name == want || p.Name.EndsWith("/" + want)) hits.Add(p);
                if (hits.Count != 1) throw new InvalidOperationException("Sayfa tek değil (" + hits.Count + "): " + want);
                Page page = hits[0];
                report["page"] = page.Name;
                string root = Io.Param(ctx, "ROOT");
                Dictionary<string, object> row = null;
                foreach (Dictionary<string, object> x in Load(root == "" ? PanelSettings.Load().Root : root))
                    if (ImportAction.S(x, "code") == Io.Param(ctx, "CODE")) row = x;
                if (row == null) throw new InvalidOperationException("Kod listede yok: " + Io.Param(ctx, "CODE"));
                using (Yazim y = new Yazim(prj, RibbonText))   // kilit + tek Geri Al; hata → geri alınır
                {
                    report["placed"] = Place(page, Xy(Io.Param(ctx, "START")), Xy(Io.Param(ctx, "END")), Io.Param(ctx, "NAME"),
                                             row, Io.Param(ctx, "CS") == "1", log);
                    y.Tamam();
                }
                try { new Generate().Connections(new Page[] { page }, true); } catch (Exception ex) { log.Add("Generate: " + ex.Message); }
                Dictionary<string, object> rb = new Dictionary<string, object>();
                Kablo.Read(new List<Page> { page }, rb);
                report["readback"] = rb["cables"];
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

    /// <summary>İki tıklamalı kablo tanım çizgisi; seçim KabloSeciciAction.Pending'den. OnSuccess EPLAN'ın UndoStep'i içinde.</summary>
    [Interaction(Name = KabloCizgisiInteraction.InteractionName, NameOfBaseInteraction = "", Ordinal = 20, Prio = 20)]
    public class KabloCizgisiInteraction : InsertInteraction
    {
        internal const string InteractionName = "UvpKabloCizgisi";
        bool _hasFirst;
        PointD _first, _second;

        public override RequestCode OnStart(InteractionContext oContext)
        {
            _hasFirst = false;
            return KabloSeciciAction.Pending == null ? RequestCode.Stop : RequestCode.Point;
        }

        public override RequestCode OnPoint(Position oPosition)
        {
            PointD p = oPosition;
            if (!_hasFirst) { _first = p; _hasFirst = true; SetRubberlineCursor(); return RequestCode.Point; }
            _second = p;
            return RequestCode.Success;
        }

        public override void OnSuccess(InteractionContext oContext)
        {
            Dictionary<string, object> row = KabloSeciciAction.Pending;
            KabloSeciciAction.Pending = null;
            Dictionary<string, object> report = Arac.Rapor("uvp.pdf2p8.kablo_sec");
            report["row"] = row;
            Project prj = null;
            try
            {
                prj = Page.Project;
                Arac.Kilitle(prj);          // EPLAN etkileşiminin kendi Geri Al adımı içinde
                Dictionary<string, object> r = KabloSeciciAction.Place(Page, _first, _second, KabloSeciciAction.PendingName, row, false, new List<string>());
                report["placed"] = r;
                report["ok"] = true;
                Arac.Bitir(prj, "kablo_sec", KabloSeciciAction.RibbonText, report, KabloSeciciAction.Summary(r));
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                report["geri_al_elle"] = true;
                Arac.Bitir(prj, "kablo_sec", KabloSeciciAction.RibbonText, report, "");
            }
        }

        public override void OnCancel() { KabloSeciciAction.Pending = null; }
    }

    /// <summary>Filtre + liste penceresi. Seçili kablo varsa damar/kesit ön filtresi ondan.</summary>
    internal class KabloSecimForm : Form
    {
        public Dictionary<string, object> Chosen;
        public string CableName { get { return _name.Text.Trim(); } }
        readonly TextBox _name = new TextBox();
        readonly List<Dictionary<string, object>> _rows;
        readonly ComboBox _family = Combo(), _shield = Combo(), _pe = Combo(), _cores = Combo(), _mm2 = Combo();
        readonly TextBox _search = new TextBox();
        readonly ListView _list = new ListView();
        const string All = "(hepsi)";

        static ComboBox Combo() { ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; c.Width = 150; return c; }

        public KabloSecimForm(List<Dictionary<string, object>> rows, Cable selected)
        {
            _rows = rows;
            Text = KabloSeciciAction.RibbonText + " — UVP çoklu kablo listesi (" + rows.Count + ")";
            Width = 900; Height = 560; StartPosition = FormStartPosition.CenterScreen;
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top; top.Height = 64; top.Padding = new Padding(6);
            List<string> fam = new List<string>(), cores = new List<string>(), mm2 = new List<string>();
            foreach (Dictionary<string, object> r in rows)
            {
                Add(fam, ImportAction.S(r, "family")); Add(cores, ImportAction.S(r, "cores")); Add(mm2, KabloSeciciAction.Mm2(r));
            }
            cores.Sort(delegate (string a, string b) { return int.Parse(a).CompareTo(int.Parse(b)); });
            mm2.Sort(delegate (string a, string b) { return Guard.Number(a, 0).CompareTo(Guard.Number(b, 0)); });
            Fill(_family, fam); Fill(_cores, cores); Fill(_mm2, mm2);
            Fill(_shield, new List<string> { "blendajlı", "blendajsız" });
            Fill(_pe, new List<string> { "PE'li (G)", "PE'siz (x)" });
            _family.Width = 260;
            foreach (object[] pair in new object[][] { new object[] { "Tür", _family }, new object[] { "Blendaj", _shield },
                     new object[] { "PE", _pe }, new object[] { "Damar", _cores }, new object[] { "Kesit mm²", _mm2 } })
            {
                Label l = new Label(); l.Text = (string)pair[0]; l.AutoSize = true; l.Padding = new Padding(0, 6, 0, 0);
                top.Controls.Add(l); top.Controls.Add((Control)pair[1]);
                ((ComboBox)pair[1]).SelectedIndexChanged += delegate { Refill(); };
            }
            Label s = new Label(); s.Text = "Ara"; s.AutoSize = true; s.Padding = new Padding(0, 6, 0, 0);
            top.Controls.Add(s); top.Controls.Add(_search);
            _search.TextChanged += delegate { Refill(); };
            _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.HideSelection = false;
            foreach (string[] c in new string[][] { new string[] { "Ürün kodu", "90" }, new string[] { "Tür", "260" }, new string[] { "Ölçü", "170" },
                     new string[] { "Dış çap mm", "80" }, new string[] { "Blendaj", "70" }, new string[] { "Üretici", "70" }, new string[] { "Kaynak", "120" } })
                _list.Columns.Add(c[0], int.Parse(c[1]));
            _list.DoubleClick += delegate { Pick(); };
            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 40; bottom.FlowDirection = FlowDirection.RightToLeft;
            Button ok = new Button(); ok.Text = selected != null ? "Seçili kabloya yaz" : "Seç ve yerleştir (2 tıklama)"; ok.AutoSize = true;
            ok.Click += delegate { Pick(); };
            Button cancel = new Button(); cancel.Text = "İptal"; cancel.DialogResult = DialogResult.Cancel;
            Label nl = new Label(); nl.Text = "Kablo adı (DT, ör. -28W33)"; nl.AutoSize = true; nl.Padding = new Padding(0, 6, 0, 0);
            _name.Width = 120;
            if (selected != null) { _name.Text = ImportAction.Text(delegate { return selected.Name; }); _name.Enabled = false; }
            bottom.Controls.Add(cancel); bottom.Controls.Add(ok); bottom.Controls.Add(_name); bottom.Controls.Add(nl);
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(_list); Controls.Add(top); Controls.Add(bottom);
            if (selected != null)
            {
                Preset(_cores, ImportAction.Text(delegate { return selected.Properties.FUNC_CABLEWIRECOUNT.ToString(); }));
                Preset(_mm2, ImportAction.Text(delegate { return selected.Properties.FUNC_CABLEWIRECROSSSECTION.ToString(); }));
            }
            Refill();
        }

        static void Add(List<string> l, string v) { if (v != "" && !l.Contains(v)) l.Add(v); }
        static void Fill(ComboBox c, List<string> items) { c.Items.Add(All); foreach (string i in items) c.Items.Add(i); c.SelectedIndex = 0; }
        static void Preset(ComboBox c, string v) { int i = c.Items.IndexOf(v.Trim()); if (i > 0) c.SelectedIndex = i; }
        static string Val(ComboBox c) { return c.SelectedItem == null || (string)c.SelectedItem == All ? "" : (string)c.SelectedItem; }

        void Refill()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            string q = _search.Text.Trim().ToLowerInvariant();
            foreach (Dictionary<string, object> r in _rows)
            {
                string sh = r["shielded"] == null ? "?" : (bool)r["shielded"] ? "var" : "yok";
                if (Val(_family) != "" && ImportAction.S(r, "family") != Val(_family)) continue;
                if (Val(_shield) != "" && sh != (Val(_shield) == "blendajlı" ? "var" : "yok")) continue;
                if (Val(_pe) != "" && (bool)r["pe"] != Val(_pe).StartsWith("PE'li")) continue;
                if (Val(_cores) != "" && ImportAction.S(r, "cores") != Val(_cores)) continue;
                if (Val(_mm2) != "" && KabloSeciciAction.Mm2(r) != Val(_mm2)) continue;
                string line = ImportAction.S(r, "code") + " " + ImportAction.S(r, "family") + " " + ImportAction.S(r, "size");
                if (q != "" && line.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                ListViewItem it = new ListViewItem(new string[] { ImportAction.S(r, "code"), ImportAction.S(r, "family"), ImportAction.S(r, "size"),
                    ImportAction.S(r, "outer_mm"), sh, ImportAction.S(r, "manufacturer"), ImportAction.S(r, "source") });
                it.Tag = r;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            if (_list.Items.Count == 1) _list.Items[0].Selected = true;
        }

        void Pick()
        {
            if (_list.SelectedItems.Count != 1) return;
            if (CableName == "") { MessageBox.Show("Kablo adını (DT) yazın; damar noktaları bu adla kabloya bağlanır.", Text); _name.Focus(); return; }
            Chosen = (Dictionary<string, object>)_list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
