// =====================================================================================
// UvpCizimciZincir — "Seçili uçları zincirle" = "Seçilenleri ortak hatta bağla" (TEK çekirdek; kullanıcı kuralı 13.10 15:50)
//
// Kullanıcı: "Ok başlangıcı soldan olur; sağdan da devam edecekse sağdan da çıkışı olur. 5 ayrı N24'ü imleçle seçti, sonra
// scripti seçti; script adlar aynı mı diye kontrol eder, aynı olanları birleştirir: N24 >-----T-------T-------T-----T------ ;
// devamı olacaksa (başka sayfada yine aynı hat) ---> N24; orası sonsa --------L (dirsek) ile sonlandırılır."
// K2: tür ölçütü yok (yalnız potansiyel adı aynı). K3: giriş oku EN SOLDA. K7=a: uçların altındaki oklar silinir, tek ortak hat.
// K4=a: canlı projede de çalışır — yalnız düğmeyle; ONAY penceresi (proje, sayfa, uçlar, silinecek/çizilecek); yazmadan önce
// otomatik .zw1 yedeği (Guard.Backup); tek UndoStep (Geri Al hepsini tek adımda geri getirir).
//
// Girdi (EPLAN seçimi): kesinti okları ve/veya cihazlar (cihazlarda ortak dikey uç adı; tek adaysa sorulmaz). Her uç = cihaz ucu +
// o ucun dikeyindeki tel (yalnız köşelerden geçerek) → kendi oku ya da boş uç; başka nesneye giden tel → red (tahmin yok).
// Çizim (x'e göre soldan sağa): oklar (giriş ve varsa çıkış için korunan ikisi hariç) ve köşeler kalkar; her ucun dikeyinde ortak
// y'de T (SPECIAL TLRO/TLRU); ortak y = okların tele bağlı noktası (uçlardan en uzak); en soldaki ucun 8 mm solunda GİRİŞ oku
// (teli sağa bakan varyant); sağda: aynı tam adlı başka kesinti noktası projede varsa (bu zincirin dışında) ÇIKIŞ oku (ikinci ok
// nesnesi korunup taşınır, yoksa yeni), yoksa son düğüm köşe (CO) = dirsek. EPLAN oto-bağ düğümleri tek yatay hatla bağlar.
// Yazdıktan sonra ağ ölçülür: uçlar + oklar tek ağ, fazladan cihaz ucu yok; değilse geri alınır + red.
// Red (hiçbir şey yazmaz): adlar farklı (hangi uçlar yazılır); hiç ok yok (ad bilinmez); uçlar farklı yöne / yatay; iki uç aynı
// dikeyde; tel dallı / çok köşeli; uç oka değil başka nesneye bağlı; sayfa açık değil; onay verilmedi; yedek alınamadı.
//
// Eylemler (aynı çekirdek): UvpCizimciZincir (bu dosya), UvpCizimciOrtakHat (UvpPdfToP8OrtakHat.cs; şerit metni farklı).
// Test: UvpCizimciZincir /PROJECT:"<UVP_*.elk>" /DEVICES:"-12K32:A2,…" ve/veya /IDS:"<ok kimliği;…>" /OUT:"<json>"
//       [/UNDO_CHECK:1] [/ONAY:1]   (ONAY:1: onay metni + .zw1 yedeği test projesinde de uygulanır; pencere açılmaz → confirm="auto")
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.HEServices;
using Label = System.Windows.Forms.Label;      // CS0104: Eplan.EplApi.HEServices.Label ile çakışır (ParcaAta.cs ile aynı)

namespace Uvp.PdfToP8.Host
{
    public class ZincirAction : IEplAction
    {
        internal const string RibbonText = "Seçili uçları zincirle";
        // ponytail: giriş/çıkış oku ilk/son düğümün 8 mm dışına (2 × 4 mm ızgara); sayfa kenarına yakınsa çizimci taşır.
        const double ArrowGapMm = 8.0;
        static int inVariant = -1;        // test: /IN_VARIANT:n (giriş oku varyant ölçümü); -1 = kural

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciZincir";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx) { return Run(ctx, RibbonText); }

        class Refused : Exception { public Refused(string m) : base(m) { } }

        class End
        {
            public Function F;
            public Pin P;
            public PointD At;                 // cihaz ucu (mutlak)
            public string Stub;               // hattan uca giden kol: Up = uç hattın üstünde
            public string Key, Name;
            public InterruptionPoint Arrow;   // ucun dikeyindeki kendi oku; yoksa null (boş uç)
            public PointD ArrowAt;            // okun tele bağlı noktası
            public List<SymbolReference> Corners = new List<SymbolReference>();   // uç ile ok arasındaki köşeler
            public string Potential = "";     // okun görünen adı
        }

        class Path
        {
            public SymbolReference End;       // varılan nesne (cihaz ya da ok)
            public PinBase EndCp;
            public int StartIndex;            // başlangıç nesnesinde telin bağlı noktası
            public List<SymbolReference> Corners = new List<SymbolReference>();
            public List<PointD[]> Hops = new List<PointD[]>();   // her parça: [buradan, oraya]
        }

        class Plan
        {
            public Page Page;
            public List<End> Ends;
            public List<string> Keys = new List<string>();
            public string Stub, Potential;
            public double Y;
            public End Entry, Exit;           // korunan ok nesneleri (Exit null: yeni ok ya da dirsek)
            public bool Continues;
            public List<string> ElsewherePages = new List<string>();
            public SymbolVariant Tee, Corner, ArrowIn, ArrowOut;
            public int InIdx, OutIdx;
            public int RemoveArrows, RemoveCorners;
        }

        // ---------------------------------------------------------------- giriş noktası (iki eylem)

        internal static bool Run(ActionCallingContext ctx, string title)
        {
            string outFile = Io.Param(ctx, "OUT");
            bool test = outFile != "";
            inVariant = test && Io.Param(ctx, "IN_VARIANT") != "" ? int.Parse(Io.Param(ctx, "IN_VARIANT")) : -1;
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.zincir";
            report["rule"] = "13.10 K2/K3/K4/K7: giriş oku solda, T'ler, sağda çıkış oku ya da dirsek; onay + yedek + tek Geri Al";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(ZincirAction).Assembly.GetName().Name;
            report["action"] = title;
            ProjectManager pm = Arac.Pm(ctx);
            Project prj = null;
            bool close = false;
            string detail = "";
            try
            {
                Func<List<End>> resolve;
                if (test)
                {
                    string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                    foreach (Project open in pm.OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) { prj = pm.OpenProject(file); close = true; }
                    Project p = prj;
                    string ids = Io.Param(ctx, "IDS"), devs = Io.Param(ctx, "DEVICES");
                    resolve = delegate { return FromSpec(p, ids, devs); };
                }
                else
                {
                    string pin = Io.Param(ctx, "PIN");
                    resolve = delegate { return FromSelection(pin); };
                }
                List<End> ends = resolve();
                if (prj == null) prj = ends[0].F.Project;
                report["project"] = prj.ProjectLinkFilePath;
                report["live_project"] = !Guard.IsTest(prj);
                // Düğmeyle her projede onay + yedek (K4); otomatik testte yalnız /ONAY:1 ile (pencere açılmaz).
                bool confirm = !test || Io.Param(ctx, "ONAY") == "1";

                if (test && Io.Param(ctx, "UNDO_CHECK") == "1")
                {
                    // Geri alma kanıtı: aynı kod yolu çizer, ölçer, kendi UndoStep'ini geri alır; sonra girdiler yeniden çözülür.
                    Dictionary<string, object> trial = new Dictionary<string, object>();
                    Page pg = ends[0].F.Page;
                    trial["placements_before"] = Count(pg);
                    trial["arrows_before"] = Arrows(ends);
                    Write(prj, Prepare(prj, ends, trial), trial, true);
                    trial["placements_after_undo"] = Count(pg);
                    ends = resolve();
                    trial["arrows_back_after_undo"] = Arrows(ends);
                    List<string> keys = new List<string>();
                    foreach (End e in ends) keys.Add(e.Key);
                    trial["nets_after_undo"] = Net.Of(pg).Count(keys);
                    report["undo_check"] = trial;
                }

                Plan plan = Prepare(prj, ends, report);
                if (confirm)
                {
                    string text = Summary(prj, plan);
                    report["confirm_text"] = text;
                    if (!test)
                    {
                        if (MessageBox.Show(text + "\n\nYazmadan önce proje yedeği (.zw1) alınır; Geri Al (Ctrl+Z) tek adımda geri getirir. Devam?",
                                            title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                            throw new Refused("Onaylanmadı; hiçbir şey değiştirilmedi.");
                        report["confirm"] = "yes";
                    }
                    else report["confirm"] = "auto";
                    report["backup"] = Guard.Backup(prj);
                }
                else report["confirm"] = "none_test";
                Write(prj, plan, report, false);
                report["ok"] = true;
                if (!test) detail = "Zincir kuruldu: " + plan.Ends.Count + " uç, " + plan.RemoveArrows + " ok silindi, hat y=" + ImportAction.S(report, "line_y")
                         + " mm; sağda " + (plan.Continues ? "çıkış oku" : "dirsek") + ".\nYedek: " + ImportAction.S(report, "backup")
                         + "\nBeğenmezseniz Geri Al (Ctrl+Z) tek adımda geri getirir.";
            }
            catch (Refused r)
            {
                report["ok"] = false;
                report["refused"] = r.Message;
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (test) Io.WriteJson(outFile, report);
                else Arac.Bitir(prj, title == OrtakHatAction.RibbonText ? "ortak_hat" : "zincir", title, report, detail);
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }

        static int Count(Page pg) { int n = 0; foreach (Placement pl in pg.AllPlacements) n++; return n; }
        static int Arrows(List<End> ends) { int n = 0; foreach (End e in ends) if (e.Arrow != null) n++; return n; }

        // ---------------------------------------------------------------- girişler

        /// <summary>EPLAN seçimi: oklar ucu söyler; cihazlarda ortak dikey uç adı seçilir (tek adaysa sorulmaz). Sayfa açık olmalı.</summary>
        static List<End> FromSelection(string pinParam)
        {
            SelectionSet sel = new SelectionSet();
            sel.LockProjectByDefault = false;
            List<Function> fns = new List<Function>();
            List<InterruptionPoint> ips = new List<InterruptionPoint>();
            HashSet<string> seen = new HashSet<string>();
            foreach (StorableObject so in sel.Selection)
            {
                string id = ImportAction.Id(so);
                if (id == null || !seen.Add(id)) continue;
                InterruptionPoint ip = so as InterruptionPoint;
                Function f = so as Function;
                if (ip != null) ips.Add(ip);
                else if (f != null && !(f is ConnectionDefinitionPoint)) fns.Add(f);
            }
            if (fns.Count + ips.Count == 0) throw new Refused("Zincirlenecek okları (kesinti noktası) ya da cihazları sayfada seçin.");
            List<End> ends = new List<End>();
            foreach (InterruptionPoint ip in ips) ends.Add(FromArrow(ip));
            if (fns.Count > 0)
            {
                string pin = pinParam != "" ? pinParam : ChoosePin(fns);
                foreach (Function f in fns) ends.Add(FromPin(f, pin));
            }
            string pid = ImportAction.Id(ends[0].F.Page);
            bool opened = false;
            try { foreach (Page p in sel.OpenedPages) if (ImportAction.Id(p) == pid) opened = true; } catch (Exception) { }
            if (!opened) throw new Refused("Uçların sayfası açık değil. Sayfayı açıp nesneleri sayfada seçin.");
            return Dedupe(ends);
        }

        /// <summary>Test girişi: IDS = ok kimlikleri (ToStringIdentifier ya da UVP-SRC kalıcı kimlik, ';'); DEVICES = "cihaz:uç,…"
        /// (cihaz tam ad ya da '-' ile başlayan kısa ad). İkisi birlikte de verilebilir; aynı uç tek sayılır.</summary>
        static List<End> FromSpec(Project prj, string ids, string devs)
        {
            List<End> ends = new List<End>();
            if (ids.Trim() != "")
            {
                List<InterruptionPoint> all = new List<InterruptionPoint>(new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()));
                foreach (string raw in ids.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string id = raw.Trim();
                    List<InterruptionPoint> m = all.FindAll(delegate (InterruptionPoint ip)
                    { return ImportAction.Id(ip) == id || (id.StartsWith(ImportAction.SOURCE_UID_PREFIX) && ImportAction.UidOf(ip) == id); });
                    if (m.Count != 1) throw new Refused("Kimlik tek oka düşmedi (" + m.Count + "): " + id);
                    ends.Add(FromArrow(m[0]));
                }
            }
            foreach (string raw in devs.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim();
                int colon = item.LastIndexOf(':');
                if (colon <= 0) throw new Refused("DEVICES biçimi cihaz:uç olmalı: " + item);
                string dev = item.Substring(0, colon), pin = item.Substring(colon + 1);
                List<Function> hits = new List<Function>();
                foreach (Page pg in prj.Pages)
                    foreach (Function f in pg.Functions)
                    {
                        if (f is ConnectionDefinitionPoint) continue;
                        string name = ImportAction.Text(delegate { return f.Name; });
                        if (name != dev && !(dev.StartsWith("-") && name.EndsWith(dev))) continue;
                        int n;
                        if (Ag.PinNamed(f, pin, out n) != null) hits.Add(f);
                    }
                if (hits.Count != 1) throw new Refused("Cihaz:uç tek fonksiyona düşmedi (" + hits.Count + "): " + item);
                ends.Add(FromPin(hits[0], pin));
            }
            if (ends.Count == 0) throw new Refused("IDS ya da DEVICES verin.");
            return Dedupe(ends);
        }

        static List<End> Dedupe(List<End> ends)
        {
            List<End> out_ = new List<End>();
            foreach (End e in ends) if (!out_.Exists(delegate (End q) { return q.Key == e.Key; })) out_.Add(e);
            return out_;
        }

        /// <summary>Bütün seçili cihazlarda ortak dikey uç adları; tek adaysa o, yoksa pencere (kendi oku olan ad ön seçili).</summary>
        static string ChoosePin(List<Function> fns)
        {
            List<string> common = null;
            foreach (Function f in fns)
            {
                List<string> names = new List<string>();
                foreach (Pin p in f.Pins)
                {
                    string d = ImportAction.Text(delegate { return p.Direction.ToString(); });
                    string n = ImportAction.Text(delegate { return p.Name; });
                    if ((d == "Up" || d == "Down") && n != "" && !names.Contains(n)) names.Add(n);
                }
                common = common == null ? names : common.FindAll(delegate (string n) { return names.Contains(n); });
            }
            if (common == null || common.Count == 0) throw new Refused("Seçili cihazlarda ortak (dikey) uç adı yok; okları seçin.");
            if (common.Count == 1) return common[0];
            string best = common[0];
            int bestArrows = -1;
            foreach (string n in common)
            {
                int arrows = 0;
                foreach (Function f in fns)
                {
                    try { End e = FromPin(f, n); if (e.Arrow != null) arrows++; } catch (Refused) { }
                }
                if (arrows > bestArrows) { bestArrows = arrows; best = n; }
            }
            using (Form form = new Form())
            using (ComboBox box = new ComboBox())
            using (Button ok = new Button())
            using (Label l = new Label())
            {
                form.Text = RibbonText + " — hangi uç?";
                form.Width = 360; form.Height = 140; form.StartPosition = FormStartPosition.CenterScreen; form.FormBorderStyle = FormBorderStyle.FixedDialog;
                l.Text = "Seçili " + fns.Count + " cihazda ortak uç adı:"; l.Left = 12; l.Top = 12; l.AutoSize = true;
                box.DropDownStyle = ComboBoxStyle.DropDownList; box.Left = 12; box.Top = 36; box.Width = 200;
                foreach (string n in common) box.Items.Add(n);
                box.SelectedItem = best;
                ok.Text = "Zincirle"; ok.Left = 230; ok.Top = 34; ok.DialogResult = DialogResult.OK;
                form.Controls.Add(l); form.Controls.Add(box); form.Controls.Add(ok); form.AcceptButton = ok;
                if (form.ShowDialog() != DialogResult.OK || box.SelectedItem == null) throw new Refused("Uç seçilmedi; hiçbir şey değişmedi.");
                return (string)box.SelectedItem;
            }
        }

        /// <summary>Ok (kesinti noktası) → tek teli (köşelerden geçebilir) → cihaz ucu; uca giren son parça dikey olmalı.</summary>
        static End FromArrow(InterruptionPoint ip)
        {
            string label = "Ok " + ImportAction.Text(delegate { return ip.VisibleName; }) + " @"
                           + string.Join(",", Array.ConvertAll(ImportAction.XY(ip.Location), d => d.ToString("0.#")));
            Path path = Walk(ip, false, new PointD(), label);
            Function f = path.End as Function;
            if (f == null) throw new Refused(label + ": teli cihaz ucuna değil " + path.End.GetType().Name + " nesnesine gidiyor; zincir kurulamaz.");
            PointD[] last = path.Hops[path.Hops.Count - 1];
            if (Math.Abs(last[1].X - last[0].X) > Ag.Tol || Math.Abs(last[1].Y - last[0].Y) < Ag.Tol)
                throw new Refused(label + ": cihaz ucuna giren tel dikey değil; zincir kurulamaz.");
            End e = new End();
            e.F = f;
            e.At = last[1];
            e.Stub = last[1].Y > last[0].Y ? "Up" : "Down";
            e.Arrow = ip;
            e.ArrowAt = path.Hops[0][0];
            e.Corners = path.Corners;
            e.Potential = ImportAction.Text(delegate { return ip.VisibleName; }).Trim();
            e.Key = ImportAction.Id(f) + "#" + path.EndCp.Index;
            foreach (Pin q in f.Pins) if (Ag.Same(ImportAction.Absolute(f, q), e.At)) { e.P = q; break; }
            e.Name = e.P == null ? ImportAction.Text(delegate { return f.Name; }) : Ag.PinName(f, e.P);
            return e;
        }

        /// <summary>Cihazın adlı ucu; dikeyindeki tel izlenir: oka varırsa ok, tel yoksa boş uç; başka nesneye varırsa red.</summary>
        static End FromPin(Function f, string pinName)
        {
            int hits;
            Pin p = Ag.PinNamed(f, pinName, out hits);
            string fname = ImportAction.Text(delegate { return f.Name; });
            if (p == null) throw new Refused(fname + ": '" + pinName + "' adlı uç " + (hits == 0 ? "yok" : "birden çok (" + hits + ")") + ".");
            End e = new End();
            e.F = f; e.P = p;
            e.At = ImportAction.Absolute(f, p);
            e.Name = Ag.PinName(f, p);
            string dir = ImportAction.Text(delegate { return p.Direction.ToString(); });
            if (dir != "Up" && dir != "Down") throw new Refused(e.Name + ": uç yatay (" + dir + "); yalnız dikey uçlar zincirlenir.");
            e.Stub = Ag.Opposite(dir);
            e.Key = ImportAction.Id(f) + "#" + p.Index;
            Path path = Walk(f, true, e.At, e.Name);
            if (path == null) return e;                       // boş uç: yalnız saplama çizilir
            e.Key = ImportAction.Id(f) + "#" + path.StartIndex;
            PointD[] first = path.Hops[0];
            if (Math.Abs(first[1].X - first[0].X) > Ag.Tol || ImportAction.Way(first[1].X - first[0].X, first[1].Y - first[0].Y) != dir)
                throw new Refused(e.Name + ": ucun teli dikeyinde değil; zincir kurulamaz.");
            InterruptionPoint ip = path.End as InterruptionPoint;
            if (ip == null) throw new Refused(e.Name + ": uç oka değil " + path.End.GetType().Name + " nesnesine bağlı; zincir kurulamaz.");
            e.Arrow = ip;
            e.ArrowAt = path.Hops[path.Hops.Count - 1][1];
            e.Corners = path.Corners;
            e.Potential = ImportAction.Text(delegate { return ip.VisibleName; }).Trim();
            return e;
        }

        /// <summary>Teli yalnız köşelerden (2 noktalı, fonksiyon olmayan sembol) geçerek izler: başlangıç nesnesinden (atPin ise yalnız
        /// `only` noktasındaki telden) ilk cihaz ucuna ya da kesinti okuna kadar TEK yol. Dal ya da başka nesne → red (tahmin yok).
        /// atPin ve o noktada tel yoksa null.</summary>
        static Path Walk(SymbolReference start, bool atPin, PointD only, string label)
        {
            Path path = new Path();
            SymbolReference cur = start;
            string from = null;
            for (int step = 0; step < 12; step++)
            {
                string me = ImportAction.Id(cur);
                List<SymbolReference.GraphicalConnection> links = new List<SymbolReference.GraphicalConnection>();
                SymbolReference.GraphicalConnection[] gs = null;
                try { gs = cur.GraphicalConnections; } catch (Exception) { }
                if (gs != null)
                    foreach (SymbolReference.GraphicalConnection g in gs)
                    {
                        bool mine = ImportAction.Id(g.StartSymbolRef) == me;
                        string other = ImportAction.Id(mine ? g.EndSymbolRef : g.StartSymbolRef);
                        if (other == me || other == from) continue;
                        if (step == 0 && atPin && !Ag.Same(ImportAction.Absolute(cur, mine ? g.StartConnectionPoint : g.EndConnectionPoint), only)) continue;
                        links.Add(g);
                    }
                if (step == 0 && atPin && links.Count == 0) return null;
                if (links.Count != 1)
                    throw new Refused(label + ": tel " + (step == 0 ? "nesneden" : "köşeden") + " " + links.Count + " yöne gidiyor; tek kısa tel beklenir.");
                SymbolReference.GraphicalConnection link = links[0];
                bool fwd = ImportAction.Id(link.StartSymbolRef) == me;
                SymbolReference next = fwd ? link.EndSymbolRef : link.StartSymbolRef;
                PinBase here = fwd ? link.StartConnectionPoint : link.EndConnectionPoint;
                PinBase there = fwd ? link.EndConnectionPoint : link.StartConnectionPoint;
                if (step == 0) path.StartIndex = here.Index;
                path.Hops.Add(new PointD[] { ImportAction.Absolute(cur, here), ImportAction.Absolute(next, there) });
                if (next is Function || next is InterruptionPoint) { path.End = next; path.EndCp = there; return path; }
                if (Ag.PointCount(next) != 2)
                    throw new Refused(label + ": tel cihaz ucuna/oka değil " + next.GetType().Name + " nesnesine gidiyor; zincir kurulamaz.");
                path.Corners.Add(next);
                from = me;
                cur = next;
            }
            throw new Refused(label + ": tel çok köşeli; zincir kurulamaz.");
        }

        // ---------------------------------------------------------------- plan (yazmadan önce her şey denetlenir)

        static Plan Prepare(Project prj, List<End> ends, Dictionary<string, object> report)
        {
            if (ends.Count < 2) throw new Refused("En az iki uç gerekir; seçili uç: " + ends.Count + ".");
            Plan pl = new Plan();
            pl.Ends = ends;
            pl.Page = ends[0].F.Page;
            string pageId = ImportAction.Id(pl.Page);
            report["page"] = pl.Page.Name;
            foreach (End e in ends)
                if (ImportAction.Id(e.F.Page) != pageId) throw new Refused("Uçlar tek sayfada olmalı (" + pl.Page.Name + " / " + e.F.Page.Name + ").");
            ends.Sort(delegate (End a, End b) { return a.At.X.CompareTo(b.At.X); });
            for (int i = 1; i < ends.Count; i++)
                if (ends[i].At.X - ends[i - 1].At.X < 1.0) throw new Refused("İki uç aynı dikeyde (" + ends[i - 1].Name + ", " + ends[i].Name + "); zincir belirsiz.");
            pl.Stub = ends[0].Stub;
            foreach (End e in ends)
                if (e.Stub != pl.Stub) throw new Refused("Uçların telleri aynı yöne çıkmıyor (" + ends[0].Name + " / " + e.Name + "); ortak hat kurulamaz.");

            // K2: tek ölçüt potansiyel adı. Farklıysa hangi uçlar hangi adda — hiçbir şey yazılmaz.
            Dictionary<string, List<string>> byName = new Dictionary<string, List<string>>();
            List<string> order = new List<string>();
            foreach (End e in ends)
            {
                if (e.Arrow == null) continue;
                if (!byName.ContainsKey(e.Potential)) { byName[e.Potential] = new List<string>(); order.Add(e.Potential); }
                byName[e.Potential].Add(e.Name);
            }
            if (order.Count == 0) throw new Refused("Seçili uçlarda potansiyel oku yok; en az bir uçta ok olmalı (ad buradan alınır). Hiçbir şey değiştirilmedi.");
            if (order.Count > 1 || order[0] == "")
            {
                List<string> parts = new List<string>();
                foreach (string n in order) parts.Add((n == "" ? "(adsız)" : n) + ": " + string.Join(", ", byName[n].ToArray()));
                throw new Refused("Seçili uçların potansiyel adı aynı değil — " + string.Join(" | ", parts.ToArray()) + ". Hiçbir şey değiştirilmedi.");
            }
            pl.Potential = order[0];
            report["potential"] = pl.Potential;

            // Ortak y: okların tele bağlı noktası, uçlardan en uzak; hiçbir uç hattın öbür yanında kalmaz.
            bool first = true;
            foreach (End e in ends)
            {
                if (e.Arrow == null) continue;
                pl.Y = first ? e.ArrowAt.Y : (pl.Stub == "Up" ? Math.Min(pl.Y, e.ArrowAt.Y) : Math.Max(pl.Y, e.ArrowAt.Y));
                first = false;
            }
            foreach (End e in ends)
                if (pl.Stub == "Up" ? e.At.Y <= pl.Y + Ag.Tol : e.At.Y >= pl.Y - Ag.Tol)
                    throw new Refused("Cihaz ucu " + e.Name + " ortak hattın öbür yanında; ortak hat kurulamaz.");
            report["line_y"] = Math.Round(pl.Y, 3);

            // Devam: aynı TAM adlı başka kesinti noktası (bu zincirin okları dışında) varsa sağda çıkış oku, yoksa dirsek.
            foreach (End e in ends) if (e.Arrow != null) { if (pl.Entry == null) pl.Entry = e; }
            HashSet<string> mine = new HashSet<string>();
            foreach (End e in ends) if (e.Arrow != null) mine.Add(ImportAction.Id(e.Arrow));
            string full = ImportAction.Text(delegate { return pl.Entry.Arrow.Name; });
            foreach (InterruptionPoint q in new DMObjectsFinder(prj).GetInterruptionPoints(new InterruptionPointsFilter()))
            {
                if (mine.Contains(ImportAction.Id(q)) || ImportAction.Text(delegate { return q.Name; }) != full) continue;
                string pn = ImportAction.Text(delegate { return q.Page.Name; });
                if (!pl.ElsewherePages.Contains(pn)) pl.ElsewherePages.Add(pn);
            }
            pl.Continues = pl.ElsewherePages.Count > 0;
            if (pl.Continues)
                foreach (End e in ends) if (e.Arrow != null && e != pl.Entry) { pl.Exit = e; break; }

            // Semboller: T (Stub,Left,Right), dirsek CO (Stub,Left); oklar kendi sembolünde — teli sağa (giriş) / sola (çıkış) bakan varyant.
            string tn, cn, inN, outN;
            pl.Tee = Ag.Junction(prj, Ag.Arms(pl.Stub, "Left", "Right"), out tn);
            pl.Corner = Ag.Junction(prj, Ag.Arms(pl.Stub, "Left"), out cn);
            SymbolVariant own = pl.Entry.Arrow.SymbolVariant;
            pl.ArrowIn = EntryVariant(prj, own, out pl.InIdx, out inN);
            if (inVariant >= 0)
            {
                // Ölçüm düğmesi (test): giriş oku varyantı zorla — ad yazısının hangi yanda durduğu yalnız PDF'te görülür.
                pl.ArrowIn = null;
                try { pl.ArrowIn = ImportAction.SymbolOf(prj, own.SymbolLibraryName, own.SymbolName)[inVariant]; } catch (Exception ex) { inN = ex.Message; }
                pl.InIdx = -1;
                PinBase[] cps = pl.ArrowIn == null ? null : pl.ArrowIn.ConnectionPoints;
                if (cps != null) for (int i = 0; i < cps.Length; i++) if (cps[i].Direction.ToString() == "Right") { pl.InIdx = i; break; }
                if (pl.InIdx < 0) throw new Refused("IN_VARIANT " + inVariant + ": sağa bakan bağlantı noktası yok (" + inN + ").");
                inN = own.SymbolLibraryName + "/" + own.SymbolName + " #" + inVariant + " nokta " + pl.InIdx + " (Right, zorlandı)";
            }
            pl.ArrowOut = ImportAction.VariantFacing(prj, own.SymbolLibraryName, own.SymbolName, "Left", -1, out pl.OutIdx, out outN);
            if (pl.Tee == null || pl.Corner == null || pl.ArrowIn == null || pl.ArrowOut == null)
                throw new Refused("Sembol bulunamadı: " + tn + " | " + cn + " | " + inN + " | " + outN);
            report["symbols"] = new Dictionary<string, object> { { "tee", tn }, { "corner", cn }, { "arrow_in", inN }, { "arrow_out", outN } };

            List<object> rows = new List<object>();
            foreach (End e in ends)
            {
                pl.Keys.Add(e.Key);
                if (e.Arrow != null && e != pl.Entry && e != pl.Exit) pl.RemoveArrows++;
                pl.RemoveCorners += e.Corners.Count;
                rows.Add(new Dictionary<string, object> { { "end", e.Name }, { "key", e.Key }, { "at", ImportAction.XY(e.At) }, { "stub", e.Stub },
                    { "arrow", e.Arrow == null ? "" : ImportAction.Id(e.Arrow) }, { "arrow_uid", e.Arrow == null ? "" : ImportAction.UidOf(e.Arrow) },
                    { "arrow_at", e.Arrow == null ? null : ImportAction.XY(e.ArrowAt) }, { "corners", e.Corners.Count }, { "potential", e.Potential },
                    { "role", e == pl.Entry ? "entry" : e == pl.Exit ? "exit" : e.Arrow == null ? "bare" : "remove" } });
            }
            report["ends"] = rows;
            report["continues"] = pl.Continues;
            report["continues_pages"] = pl.ElsewherePages;
            report["plan"] = new Dictionary<string, object> { { "remove_arrows", pl.RemoveArrows }, { "remove_corners", pl.RemoveCorners },
                { "tees", pl.Continues ? ends.Count : ends.Count - 1 }, { "elbow", pl.Continues ? 0 : 1 },
                { "exit", pl.Continues ? (pl.Exit != null ? "arrow" : "arrow_new") : "elbow" } };
            return pl;
        }

        /// <summary>Giriş oku varyantı: teli sağa bakan ilk varyantın AYNALI eşi (EPLAN varyant düzeni A–D döndürme, E–H = A–D aynalı →
        /// nr+4). Ölçüm 13.10 (=530, SPECIAL/BP): #0 ok sağa ama ad yazısı sağda, hattın üstüne biniyor; #4 ok sağa, yazı solda
        /// ("N24.10 ▶───T"), #2/#6 ok sola. Aynalı eş yoksa ya da sağa bakan noktası yoksa ilk varyant kalır.</summary>
        static SymbolVariant EntryVariant(Project prj, SymbolVariant own, out int idx, out string note)
        {
            string tag = own.SymbolLibraryName + "/" + own.SymbolName;
            idx = -1;
            note = tag + ": sağa bakan bağlantı noktası olan varyant yok";
            try
            {
                Symbol symbol = ImportAction.SymbolOf(prj, own.SymbolLibraryName, own.SymbolName);
                int nr = -1;
                for (int i = 0; i < symbol.Variants.Length && nr < 0; i++)
                {
                    int j = RightPoint(symbol[i]);
                    if (j >= 0) { nr = i; idx = j; }
                }
                if (nr < 0) return null;
                int mirror = nr + 4;
                int k = mirror < symbol.Variants.Length ? RightPoint(symbol[mirror]) : -1;
                if (k >= 0) { idx = k; note = tag + " #" + mirror + " nokta " + k + " (Right, aynalı: yazı solda)"; return symbol[mirror]; }
                note = tag + " #" + nr + " nokta " + idx + " (Right)";
                return symbol[nr];
            }
            catch (Exception ex) { note = tag + " okunamadı: " + ex.Message; return null; }
        }

        static int RightPoint(SymbolVariant sv)
        {
            PinBase[] cps = null;
            try { cps = sv == null ? null : sv.ConnectionPoints; } catch (Exception) { }
            for (int i = 0; cps != null && i < cps.Length; i++) if (cps[i].Direction.ToString() == "Right") return i;
            return -1;
        }

        /// <summary>Onay penceresi metni (K4): proje, sayfa, uçlar, ne silinecek, ne çizilecek.</summary>
        static string Summary(Project prj, Plan pl)
        {
            List<string> names = new List<string>();
            foreach (End e in pl.Ends) names.Add(e.Name);
            string right = pl.Continues
                ? "çıkış oku (" + pl.Potential + " başka yerde de var: " + string.Join(", ", pl.ElsewherePages.ToArray()) + ")"
                : "dirsek (" + pl.Potential + " başka sayfada yok)";
            return "Proje: " + System.IO.Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath)
                 + "\nSayfa: " + pl.Page.Name
                 + "\nPotansiyel: " + pl.Potential + " — " + pl.Ends.Count + " uç: " + string.Join(", ", names.ToArray())
                 + "\nSilinecek: " + pl.RemoveArrows + " ok, " + pl.RemoveCorners + " köşe"
                 + "\nÇizilecek: ortak hat y=" + Math.Round(pl.Y, 1) + " mm, " + (pl.Continues ? pl.Ends.Count : pl.Ends.Count - 1) + " T düğümü"
                 + (pl.Continues ? "" : " + 1 dirsek") + "; solda giriş oku; sağda " + right;
        }

        // ---------------------------------------------------------------- yazma (tek kilit, tek Geri Al adımı, ölçüm)

        static void Write(Project prj, Plan pl, Dictionary<string, object> report, bool undoAfter)
        {
            Page page = pl.Page;
            Net before = Net.Of(page);
            HashSet<string> allowed = new HashSet<string>();
            foreach (string k in pl.Keys) foreach (string p in before.Pins(k)) allowed.Add(p);
            report["before"] = new Dictionary<string, object> { { "nets", before.Count(pl.Keys) }, { "pins_in_nets", allowed.Count } };

            List<object> removed = new List<object>();
            List<object> placed = new List<object>();
            int n = pl.Ends.Count;
            using (LockingStep step = new LockingStep())
            using (UndoManager um = new UndoManager())
            {
                Arac.Kilitle(prj);          // düğme yolu nesneleri kilitsiz verir (14.10 LockingException)
                UndoStep undo = um.CreateUndoStep();
                undo.SetUndoDescription("UVP · Zincir (" + pl.Potential + ")");
                try
                {
                    foreach (End e in pl.Ends)
                    {
                        if (e.Arrow != null && e != pl.Entry && e != pl.Exit)
                        {
                            removed.Add(new Dictionary<string, object> { { "kind", "arrow" }, { "object_id", ImportAction.Id(e.Arrow) }, { "end", e.Name }, { "at", ImportAction.XY(e.ArrowAt) } });
                            e.Arrow.Remove();
                        }
                        foreach (SymbolReference k in e.Corners)
                        {
                            removed.Add(new Dictionary<string, object> { { "kind", "corner" }, { "object_id", ImportAction.Id(k) }, { "end", e.Name } });
                            k.Remove();
                        }
                    }
                    for (int i = 0; i < n; i++)
                    {
                        bool elbow = i == n - 1 && !pl.Continues;
                        SymbolVariant sv = elbow ? pl.Corner : pl.Tee;
                        SymbolReference sr = SymbolReference.Create(sv, page);
                        sr.Location = Insertion(sv, pl.Stub, pl.Ends[i].At.X, pl.Y);
                        placed.Add(new Dictionary<string, object> { { "kind", elbow ? "CO" : "T" }, { "object_id", ImportAction.Id(sr) },
                            { "at", new double[] { Math.Round(pl.Ends[i].At.X, 3), Math.Round(pl.Y, 3) } } });
                    }
                    // K3: giriş oku en soldaki düğümün 8 mm solunda, teli sağa; devam varsa çıkış oku en sağdaki düğümün 8 mm sağında.
                    Place(pl.Entry.Arrow, pl.ArrowIn, pl.InIdx, new PointD(pl.Ends[0].At.X - ArrowGapMm, pl.Y), report, "entry_arrow");
                    InterruptionPoint exitIp = null;
                    if (pl.Continues)
                    {
                        exitIp = pl.Exit != null ? pl.Exit.Arrow : null;
                        if (exitIp == null)
                        {
                            exitIp = new InterruptionPoint();
                            exitIp.Create(page, pl.ArrowOut);
                            exitIp.Name = ImportAction.Text(delegate { return pl.Entry.Arrow.Name; });
                            report["exit_arrow_created"] = true;
                        }
                        Place(exitIp, pl.ArrowOut, pl.OutIdx, new PointD(pl.Ends[n - 1].At.X + ArrowGapMm, pl.Y), report, "exit_arrow");
                    }
                    new Generate().Connections(new Page[] { page }, true);
                    report["removed"] = removed;
                    report["placed"] = placed;

                    // Yanlış bağ sessiz geçmez: uçlar + oklar tek ağ, fazladan cihaz ucu yok; değilse geri al + red.
                    Net after = Net.Of(page);
                    List<string> extra = new List<string>();
                    foreach (string p in after.Pins(pl.Keys[0])) if (!allowed.Contains(p)) extra.Add(p);
                    int nets = after.Count(pl.Keys);
                    string root = after.Root(pl.Keys[0]);
                    bool entryIn = after.Root(ImportAction.Id(pl.Entry.Arrow)) == root;
                    bool exitIn = exitIp == null || after.Root(ImportAction.Id(exitIp)) == root;
                    report["after"] = new Dictionary<string, object> { { "nets", nets }, { "pins_in_net", after.Pins(pl.Keys[0]).Count }, { "extra_pins", extra },
                        { "entry_arrow_in_net", entryIn }, { "exit_arrow_in_net", exitIn } };
                    if (nets != 1 || extra.Count > 0 || !entryIn || !exitIn)
                        throw new Refused("Zincir beklenmeyen bağ kurdu (ağ " + nets + ", fazladan uç " + extra.Count + ", giriş oku hatta: " + entryIn
                                          + ", çıkış oku hatta: " + exitIn + "); değişiklik geri alındı.");
                    if (undoAfter) undo.DoUndo();
                }
                catch (Exception)
                {
                    try { undo.DoUndo(); } catch (Exception) { }
                    throw;
                }
                finally { try { undo.CloseUndo(); } catch (Exception) { } }
            }
        }

        /// <summary>Oku (adı/kimliği korunarak) verilen varyantla taşır: `idx` bağlantı noktası `at` noktasına düşer.</summary>
        static void Place(InterruptionPoint ip, SymbolVariant sv, int idx, PointD at, Dictionary<string, object> report, string key)
        {
            ip.SymbolVariant = sv;
            ip.Location = ImportAction.InsertionFor(sv, idx, at);
            // Dikey/göreli taşınmış ad yazısı yatay oka uymaz: sembolün kendi varsayılan gösterimine dönülür.
            try { if (ip.UseLocalPropertyPlacements) ip.SwitchLocalPropertyPlacements(false); }
            catch (Exception ex) { report[key + "_label_reset_error"] = ex.Message; }
            report[key] = new Dictionary<string, object> { { "object_id", ImportAction.Id(ip) }, { "at", ImportAction.XY(at) },
                { "visible_name", ImportAction.Text(delegate { return ip.VisibleName; }) } };
        }

        /// <summary>Düğümün dikey kolu ucun x'inde, yatay kolu hat y'sinde dursun (kol noktaları kökten kaymış olabilir; ölçüm 09.10).</summary>
        static PointD Insertion(SymbolVariant sv, string toPin, double x, double y)
        {
            PinBase v = null, h = null;
            foreach (PinBase cp in sv.ConnectionPoints)
            {
                string d = cp.Direction.ToString();
                if (d == toPin) v = cp;
                else if ((d == "Left" || d == "Right") && h == null) h = cp;
            }
            if (v == null || h == null) throw new InvalidOperationException("Düğüm sembolünün kolları okunamadı.");
            return new PointD(x - v.Location.X, y - h.Location.Y);
        }
    }
}
