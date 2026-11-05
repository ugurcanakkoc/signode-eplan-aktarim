// =====================================================================================
// UvpCizimciYaziKaydir — "Yazı çakışmasını gider" (A4 / F1.4, 12.10; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Girdi: analyzer_v3.yazi_cakisma --json planı (EPLAN PDF'i + geri okumadan): taşıma başına sayfa adı, sahip kimliği
// (SymbolReference), görünen metin ve kaydırma vektörü (dx, dy mm). Eylem:
//   1. Her taşıma için sahibi SAYFADA kimlikle bulur; sahibin GÖRÜNÜR PropertyPlacement'larından görünen metni plandaki
//      metne eşit olan TEK yerleşimi seçer. Sayfa adı, kimlik ya da metin tutmazsa / aday tek değilse o taşıma ATLANIR
//      (sebep raporda) — başka projenin planı yanlış nesneyi taşıyamaz.
//   2. Yazmadan ÖNCE onay penceresi: proje adı, taşınacak yazı sayısı, atlananlar, ilk satırlar. Hayır → yazılmaz.
//   3. Hepsi tek UndoStep (EPLAN Geri Al tek adımda geri getirir); hata olursa adım geri alınır.
//      Yerleşim yerel değilse SwitchLocalPropertyPlacements(true) (Import.PlaceLabels ölçümü), yapışık (docked) yazı
//      UndockText ile serbest bırakılır (Import.XrefBeside yolu); konum sembole GÖRELİ: Location += (dx, dy).
// Canlı (UVP_ olmayan) projede yalnız bu onay + tek adım geri al ile çalışır.
//
// Test: UvpCizimciYaziKaydir /PROJECT:"<UVP_*.elk>" /PLAN:"<plan.json>" /OUT:"<json>" [/UNDO_CHECK:1]
//       — yalnız output\p8runs altındaki UVP_ test projesi (Guard); onay sorulmaz. UNDO_CHECK:1 → yaz, oku, geri al, oku.
// ÖLÇÜLMEDİ (12.10): docked/undocked yazıda Location değişiminin PDF'e yansıması (Y7: DockToLeft PDF'te etkisizdi).
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

namespace Uvp.PdfToP8.Host
{
    public class YaziKaydirAction : IEplAction
    {
        internal const string RibbonText = "Yazı çakışmasını gider";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciYaziKaydir";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        /// <summary>Bulunan taşıma: yerleşim + sahibi + plan satırı.</summary>
        class Hit
        {
            public PropertyPlacement Text;
            public SymbolReference Owner;
            public Dictionary<string, object> Move;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            if (outFile != "") return Test(ctx, outFile);
            try
            {
                string plan = Io.Param(ctx, "PLAN");
                if (plan == "")
                    using (OpenFileDialog d = new OpenFileDialog())
                    {
                        d.Title = RibbonText + " — analyzer_v3.yazi_cakisma planı (--json)";
                        d.Filter = "Plan (*.json)|*.json";
                        if (d.ShowDialog() != DialogResult.OK) return true;
                        plan = d.FileName;
                    }
                Project prj = new SelectionSet().GetCurrentProject(false);
                if (prj == null) { Say("Açık proje yok.", MessageBoxIcon.Warning); return true; }
                List<string> skipped;
                List<Hit> hits = Find(prj, LoadMoves(plan), out skipped);
                if (hits.Count == 0) { Say("Taşınacak yazı bulunamadı.\n" + Lines(skipped, 15), MessageBoxIcon.Warning); return true; }
                StringBuilder sb = new StringBuilder();
                foreach (Hit h in hits)
                    sb.Append("  ").Append(ImportAction.S(h.Move, "page")).Append(": '").Append(ImportAction.S(h.Move, "text"))
                      .Append("' (").Append(ImportAction.S(h.Move, "dx")).Append(", ").Append(ImportAction.S(h.Move, "dy")).Append(") mm\n");
                string ask = "Proje: " + ImportAction.Text(delegate { return prj.ProjectName; }) + "\n" + hits.Count
                             + " yazı taşınacak (elektrik verisi değişmez, yalnız yazı konumu):\n" + Lines(new List<string>(sb.ToString().TrimEnd('\n').Split('\n')), 15)
                             + (skipped.Count > 0 ? "\nAtlanacak " + skipped.Count + " (sahip/metin tutmadı):\n" + Lines(skipped, 5) : "")
                             + "\n\nGeri almak için EPLAN Geri Al (tek adım). Taşınsın mı?";
                if (MessageBox.Show(ask, RibbonText, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;
                Dictionary<string, object> r = new Dictionary<string, object>();
                Run(hits, r, false);
                Say("Taşındı: " + hits.Count + " yazı. PDF'i yeniden verip analyzer_v3.yazi_cakisma ile ölçün.", MessageBoxIcon.Information);
            }
            catch (Exception ex) { Say(ex.GetType().Name + ": " + ex.Message, MessageBoxIcon.Error); }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        static string Lines(List<string> l, int max)
        {
            List<string> head = l.Count > max ? l.GetRange(0, max) : l;
            return string.Join("\n", head.ToArray()) + (l.Count > max ? "\n  … +" + (l.Count - max) : "");
        }

        static List<Dictionary<string, object>> LoadMoves(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (ImportAction.S(d, "contract") != "uvp.pdf2p8.yazi_kaydir") throw new InvalidOperationException("Plan sözleşmesi değil: " + path);
            List<Dictionary<string, object>> l = new List<Dictionary<string, object>>();
            foreach (object o in ImportAction.A(d.ContainsKey("moves") ? d["moves"] : null)) l.Add(ImportAction.D(o));
            return l;
        }

        static List<PropertyPlacement> Visible(SymbolReference sr, string text)
        {
            List<PropertyPlacement> l = new List<PropertyPlacement>();
            foreach (Placement pl in sr.PropertyPlacements)
            {
                PropertyPlacement t = pl as PropertyPlacement;
                if (t == null) continue;
                bool vis = true;
                try { vis = t.IsVisible; } catch (Exception) { }
                if (vis && ImportAction.Text(delegate { return t.GetDisplayString(); }) == text) l.Add(t);
            }
            return l;
        }

        /// <summary>Plan satırlarını projede bulur; yazmaz. Tutmayan satır sebebiyle skipped'e.</summary>
        static List<Hit> Find(Project prj, List<Dictionary<string, object>> moves, out List<string> skipped)
        {
            skipped = new List<string>();
            Dictionary<string, Page> pages = new Dictionary<string, Page>();
            foreach (Page p in prj.Pages) pages[ImportAction.Text(delegate { return p.Name; })] = p;
            List<Hit> hits = new List<Hit>();
            foreach (Dictionary<string, object> m in moves)
            {
                string page = ImportAction.S(m, "page"), owner = ImportAction.S(m, "owner"), text = ImportAction.S(m, "text");
                string tag = page + ": '" + text + "'";
                if (!pages.ContainsKey(page)) { skipped.Add(tag + " — sayfa yok"); continue; }
                SymbolReference sr = null;
                foreach (Placement pl in pages[page].AllPlacements)
                    if (ImportAction.Id(pl) == owner) { sr = pl as SymbolReference; break; }
                if (sr == null) { skipped.Add(tag + " — sahip " + owner + " sayfada yok"); continue; }
                int n = Visible(sr, text).Count;
                if (n != 1) { skipped.Add(tag + " — görünen yerleşim " + n + " (tek olmalı)"); continue; }
                Hit h = new Hit();
                h.Owner = sr; h.Move = m;
                hits.Add(h);
            }
            return hits;
        }

        /// <summary>Tek kilit + tek UndoStep; hata olursa adım geri alınır. undoAfter: test için yaz-oku-geri al-oku.</summary>
        static void Run(List<Hit> hits, Dictionary<string, object> report, bool undoAfter)
        {
            List<object> rows = new List<object>();
            using (LockingStep step = new LockingStep())
            using (UndoManager um = new UndoManager())
            {
                UndoStep undo = um.CreateUndoStep();
                undo.SetUndoDescription("UVP · " + RibbonText + " (" + hits.Count + " yazı)");
                try
                {
                    foreach (Hit h in hits)
                    {
                        Dictionary<string, object> e = new Dictionary<string, object>();
                        string text = ImportAction.S(h.Move, "text");
                        e["page"] = ImportAction.S(h.Move, "page"); e["owner"] = ImportAction.S(h.Move, "owner"); e["text"] = text;
                        if (!h.Owner.UseLocalPropertyPlacements) { h.Owner.SwitchLocalPropertyPlacements(true); e["switched_local"] = true; }
                        List<PropertyPlacement> c = Visible(h.Owner, text);      // yerelleştirme yerleşimleri kopyalar: yeniden bul
                        if (c.Count != 1) { e["error"] = "görünen yerleşim " + c.Count; rows.Add(e); continue; }
                        PropertyPlacement t = c[0];
                        if (ImportAction.Id(t).Split('/')[0] != ImportAction.Id(h.Owner).Split('/')[0])
                        { e["error"] = "PLACEMENT_NOT_IN_PROJECT"; rows.Add(e); continue; }
                        t.LockObject();
                        e["before"] = ImportAction.PlacementRow(t, h.Owner);
                        PointD rel = t.Location;
                        if (t.IsDocked) { t.UndockText(); e["undocked"] = true; t.Location = rel; }
                        t.Location = new PointD(rel.X + ImportAction.N(h.Move["dx"]), rel.Y + ImportAction.N(h.Move["dy"]));
                        e["after"] = ImportAction.PlacementRow(t, h.Owner);
                        h.Text = t;
                        rows.Add(e);
                    }
                    report["moved"] = rows;
                    if (undoAfter)
                    {
                        undo.DoUndo();
                        List<object> back = new List<object>();
                        foreach (Hit h in hits)
                        {
                            List<PropertyPlacement> c = Visible(h.Owner, ImportAction.S(h.Move, "text"));
                            back.Add(c.Count == 1 ? (object)ImportAction.PlacementRow(c[0], h.Owner) : "görünen yerleşim " + c.Count);
                        }
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
        }

        bool Test(ActionCallingContext ctx, string outFile)
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.pdf2p8.yazi_kaydir.result";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["assembly"] = typeof(YaziKaydirAction).Assembly.GetName().Name;
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                report["project"] = file;
                List<string> skipped;
                List<Hit> hits = Find(prj, LoadMoves(Io.Param(ctx, "PLAN")), out skipped);
                report["found"] = hits.Count;
                report["skipped"] = skipped;
                Run(hits, report, Io.Param(ctx, "UNDO_CHECK") == "1");
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
    }
}
