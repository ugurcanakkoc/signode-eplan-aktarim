// =====================================================================================
// UvpPdfToP8Gezgin — Kontrol Gezgini'nin EPLAN eylemi (F1.1; liste analyzer_v3/kontrol_gezgini.py, sayfa static/gezgin.html).
//
//   UvpPdfToP8Gezgin [/PROJECT:"<elk>"] /PAGE:"<sayfa tam adı>" [/DONE:"<işaret metni>" [/DEVICE:"-23K76"] [/LABEL:"..."]]
//                    [/OUT:"<sonuc.json>"]
//   * PAGE: sayfayı açar (Edit.OpenPageWithName, Panel.cs ile aynı yol). Salt okuma; her projede.
//   * DONE: o sayfadaki UVP_KONTROL katmanında metni tam bu olan yazı(lar) silinir ve "UVP Kontrol Listesi" (999)
//     sayfasında satır (Sayfa = PAGE, Cihaz = DEVICE, Eksik LABEL ile başlar) "[BİTTİ] " ön ekiyle tiklenir.
//     Tek UndoStep (EPLAN Geri Al tek adımda geri getirir). Kontrol.WriteList sonraki aktarımda tikli satırı korur.
//     Yazma onayı: output\p8runs altındaki UVP_ test projesi dışında (çizimcinin kendi projesi) önce onay penceresi
//     (proje adı + silinecek yazı + tiklenecek satır sayısı); /OUT ile otomatik çağrıda da sorulur.
//   * Proje verilmezse açık (geçerli) proje. Elektrik nesnesine dokunulmaz.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class GezginAction : IEplAction
    {
        internal const string Done = "[BİTTİ] ";
        // Kontrol.cs ColX = { 44, 76, 104, 224 }: sütun sınırları (WriteList'in okuma kuralıyla aynı, 0,5 mm pay).
        static readonly double[] ColX = { 44, 76, 104, 224 };
        const double TopY = 271;

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Gezgin";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static string Plain(Text t)
        {
            return ImportAction.Text(delegate { return t.Contents.GetString(ISOCode.Language.L___); }).Trim();
        }

        static bool OnLayer(Text t)
        {
            return ImportAction.Text(delegate { return t.Layer == null ? "" : t.Layer.Name; }) == Kontrol.LayerName;
        }

        static bool IsList(Page p)
        {
            string counter = ImportAction.Text(delegate { return p.Properties.PAGE_COUNTER.ToString(); });
            string desc = ImportAction.Text(delegate { return p.Properties.PAGE_NOMINATIOMN.ToString(); });
            return (counter == Kontrol.ListCounter || counter.StartsWith(Kontrol.ListCounter + ".")) && desc.Contains(Kontrol.ListTitle);
        }

        /// <summary>999 sayfasında ilk tiklenmemiş eşleşen satırın 'Eksik' hücresi (yoksa null).</summary>
        static Text FindListCell(Project prj, string page, string device, string label)
        {
            foreach (Page p in prj.Pages)
            {
                if (!IsList(p)) continue;
                SortedDictionary<double, Text[]> byY = new SortedDictionary<double, Text[]>();
                foreach (Placement pl in p.AllPlacements)
                {
                    Text t = pl as Text;
                    if (t == null || !OnLayer(t) || t.Location.Y > TopY + 1) continue;
                    double y = -Math.Round(t.Location.Y, 1);
                    int col = 0;
                    for (int i = 0; i < ColX.Length; i++) if (t.Location.X >= ColX[i] - 0.5) col = i;
                    if (!byY.ContainsKey(y)) byY[y] = new Text[4];
                    byY[y][col] = t;
                }
                foreach (Text[] row in byY.Values)
                {
                    if (row[0] == null || row[2] == null || Plain(row[0]) != page) continue;
                    if ((row[1] == null ? "" : Plain(row[1])) != device) continue;
                    string missing = Plain(row[2]);
                    if (missing.StartsWith(Done) || (label != "" && !missing.StartsWith(label))) continue;
                    return row[2];
                }
            }
            return null;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT"), pageName = Io.Param(ctx, "PAGE"), done = Io.Param(ctx, "DONE").Trim();
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["contract"] = "uvp.pdf2p8.gezgin";
            r["utc"] = DateTime.UtcNow.ToString("o");
            r["page"] = pageName;
            r["done"] = done != "";
            try
            {
                Project prj = null;
                string file = Io.Param(ctx, "PROJECT");
                ProjectManager pm = new ProjectManager();
                if (file != "")
                {
                    foreach (Project open in pm.OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) prj = pm.OpenProject(Path.GetFullPath(file));
                }
                else prj = new SelectionSet().GetCurrentProject(false);
                if (prj == null) throw new InvalidOperationException("Açık proje yok.");
                r["project"] = prj.ProjectLinkFilePath;
                Page page = null;
                foreach (Page p in prj.Pages) if (ImportAction.Text(delegate { return p.Name; }) == pageName) page = p;
                if (page == null) throw new InvalidOperationException("Sayfa projede yok: " + pageName);
                new Edit().OpenPageWithName(prj.ProjectLinkFilePath, pageName);
                if (done != "")
                {
                    List<Text> marks = new List<Text>();
                    foreach (Placement pl in page.AllPlacements)
                    {
                        Text t = pl as Text;
                        if (t != null && OnLayer(t) && Plain(t) == done) marks.Add(t);
                    }
                    Text cell = FindListCell(prj, pageName, Io.Param(ctx, "DEVICE"), Io.Param(ctx, "LABEL"));
                    r["found_texts"] = marks.Count;
                    r["found_list_row"] = cell != null;
                    if (marks.Count == 0 && cell == null) throw new InvalidOperationException("İşaret yazısı da liste satırı da bulunamadı (elle silinmiş olabilir).");
                    bool testProject = true;
                    try { Guard.TestProject(prj.ProjectLinkFilePath); } catch (Exception) { testProject = false; }
                    r["confirm_asked"] = !testProject;
                    if (!testProject && MessageBox.Show("Proje: " + Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath)
                            + "\nSilinecek işaret yazısı: " + marks.Count + "\nTiklenecek kontrol listesi satırı: " + (cell == null ? 0 : 1)
                            + "\n\nEPLAN Geri Al (Ctrl+Z) tek adımda geri getirir. Devam edilsin mi?", "UVP Kontrol Gezgini",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        throw new InvalidOperationException("Kullanıcı onaylamadı; hiçbir şey değişmedi.");
                    using (LockingStep step = new LockingStep())
                    using (UndoManager um = new UndoManager())
                    {
                        UndoStep undo = um.CreateUndoStep();
                        undo.SetUndoDescription("UVP Kontrol Gezgini: Bitti");
                        foreach (Text t in marks) t.Remove();
                        if (cell != null)
                        {
                            MultiLangString s = new MultiLangString();
                            s.AddString(ISOCode.Language.L___, Done + Plain(cell));
                            cell.Contents = s;
                        }
                    }
                    r["removed_texts"] = marks.Count;
                    r["ticked_rows"] = cell == null ? 0 : 1;
                }
                r["ok"] = true;
            }
            catch (Exception ex)
            {
                r["ok"] = false;
                r["error"] = ex.GetType().Name + ": " + ex.Message;
                if (outFile == "") MessageBox.Show(ex.Message, "UVP Kontrol Gezgini", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (outFile != "") Io.WriteJson(outFile, r);
            return true;
        }
    }
}
