// =====================================================================================
// UvpCizimciKesitTamamla — "Kesit/renk tamamla" (fikir B2 / F2-2; çizimci aracı, PDF→P8 aktarımının parçası DEĞİL)
//
// Projedeki bağlantıların BOŞ kesit (31002/31059), renk (31004 Farbe/Nummer), tel türü (31048) ve tel parçası alanlarını
// kurala göre doldurur. Karar Python'dadır (analyzer_v3/kesit_tamamla.py: seri devam, sigorta→kesit tablosu, profil rengi,
// etiket kapısı); C# yalnız okur ve planı uygular:
//   1. OKU: bağlantılar + uçlardaki işlevler (kategori, işlev metni, teknik değer) → <proje klasörü>\kesit_tamamla_<zaman>\kesit_oku.json
//   2. PLAN: python -X utf8 -m analyzer_v3.kesit_tamamla plan (motor klasörü/python: PanelSettings, panelle aynı)
//   3. ONAY: proje adı + değişecek bağlantı sayısı + ilk satırlar (canlı projede her zaman sorulur)
//   4. UYGULA: tek UndoStep (EPLAN Geri Al tek adımda geri getirir) + geri-al günlüğü kesit_tamamla_gunluk.json (eski değerler).
//      Plan eskidiyse (alan artık boş değil / değer değişmiş) o bağlantı YAZILMAZ (STALE). Yalnız boş alan yazılır.
//   5. GERİ AL (/MODE:GERIAL /LOG:<günlük>): günlükteki bağlantı hâlâ yazılan değerdeyse eski değer geri yazılır, eklenen tel
//      parçası kaldırılır; tek UndoStep. Proje kapatılıp açıldıktan sonra da çalışır.
//
// Test (yalnız output\p8runs altındaki UVP_ projesi; onay sorulmaz):
//   UvpCizimciKesitTamamla /PROJECT:"<UVP_*.elk>" /MODE:OKU /OUT:"<kesit_oku.json>"
//   UvpCizimciKesitTamamla /PROJECT:"<UVP_*.elk>" /MODE:UYGULA /PLAN:"<plan.json>" /OUT:"<rapor.json>"
//   UvpCizimciKesitTamamla /PROJECT:"<UVP_*.elk>" /MODE:GERIAL /LOG:"<günlük.json>" /OUT:"<rapor.json>"
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
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    public class KesitTamamlaAction : IEplAction
    {
        internal const string RibbonText = "Kesit/renk tamamla";
        const string LogName = "kesit_tamamla_gunluk.json";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciKesitTamamla";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT"), mode = Io.Param(ctx, "MODE").ToUpperInvariant();
            bool test = outFile != "";
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["contract"] = "uvp.kesit_tamamla.rapor";
            report["utc"] = DateTime.UtcNow.ToString("o");
            report["mode"] = mode == "" ? "UYGULA" : mode;
            Project prj = null;
            bool close = false;
            try
            {
                if (test)
                {
                    string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                    ProjectManager pm = Arac.Pm(ctx);
                    foreach (Project open in pm.OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) { prj = pm.OpenProject(file); close = true; }
                }
                else prj = new SelectionSet().GetCurrentProject(false);
                if (prj == null) throw new InvalidOperationException("Açık proje yok.");
                report["project"] = prj.ProjectLinkFilePath;
                string name = Path.GetFileNameWithoutExtension(prj.ProjectLinkFilePath);
                string dir = Path.Combine(Path.GetDirectoryName(prj.ProjectLinkFilePath), "kesit_tamamla_" + Io.Stamp());

                if (mode == "OKU")
                {
                    Io.WriteJson(test ? outFile : Path.Combine(Created(dir), "kesit_oku.json"), Read(prj));
                    report["ok"] = true;
                }
                else if (mode == "GERIAL")
                {
                    string logFile = Io.Param(ctx, "LOG");
                    if (logFile == "") logFile = LatestLog(Path.GetDirectoryName(prj.ProjectLinkFilePath));
                    if (logFile == "" || !File.Exists(logFile)) throw new InvalidOperationException("Geri-al günlüğü yok (" + LogName + ").");
                    ArrayList entries = ImportAction.A(Load(logFile)["entries"]);
                    if (!test && !Confirm("Proje: " + name + "\nGeri alınacak bağlantı: " + entries.Count + "\nGünlük: " + logFile
                                          + "\n\nYalnız hâlâ yazılan değerde duran alanlar eski hâline döner. Devam edilsin mi?")) return true;
                    report["restore"] = Restore(prj, entries);
                    report["ok"] = true;
                    if (!test) Say("Geri alındı: " + ImportAction.D(report["restore"])["restored"] + " bağlantı.", MessageBoxIcon.Information);
                }
                else
                {
                    string planFile = Io.Param(ctx, "PLAN");
                    if (planFile == "")
                    {
                        Created(dir);
                        string readFile = Path.Combine(dir, "kesit_oku.json");
                        planFile = Path.Combine(dir, "plan.json");
                        Io.WriteJson(readFile, Read(prj));
                        PanelSettings s = PanelSettings.Load();
                        if (Io.Param(ctx, "ROOT") != "") s.Root = Io.Param(ctx, "ROOT");
                        if (Io.Param(ctx, "PYTHON") != "") s.Python = Io.Param(ctx, "PYTHON");
                        Python(s, readFile, planFile);
                    }
                    Dictionary<string, object> plan = Load(planFile);
                    ArrayList changes = ImportAction.A(plan["changes"]);
                    report["plan"] = planFile;
                    report["planned"] = changes.Count;
                    if (changes.Count == 0)
                    {
                        report["ok"] = true;
                        if (!test) Say("Kanıtla doldurulabilecek boş kesit/renk yok; hiçbir şey yazılmadı.\nPlan: " + planFile, MessageBoxIcon.Information);
                        return true;
                    }
                    if (!test && !Confirm(Preview(name, plan, changes, planFile))) return true;
                    string logFile = Path.Combine(Path.GetDirectoryName(planFile), LogName);
                    report["apply"] = Apply(prj, changes, logFile);
                    report["log"] = logFile;
                    report["ok"] = true;
                    if (!test)
                    {
                        Dictionary<string, object> a = ImportAction.D(report["apply"]);
                        Say("Yazılan bağlantı: " + a["written"] + "\nEskimiş (yazılmadı): " + a["stale"] + "\nBulunamayan: " + a["missing"]
                            + "\n\nGeri al: Ctrl+Z ya da UvpCizimciKesitTamamla /MODE:GERIAL /LOG:\"" + logFile + "\"", MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                if (!test) Arac.Bitir(prj, "kesit_tamamla", RibbonText, report, "");
            }
            finally
            {
                if (test) Io.WriteJson(outFile, report);
                else if (report.ContainsKey("ok") && report["ok"] is bool && (bool)report["ok"]) Arac.Makbuz(prj, "kesit_tamamla", report);
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            return true;
        }

        static void Say(string text, MessageBoxIcon icon) { MessageBox.Show(text, RibbonText, MessageBoxButtons.OK, icon); }

        static bool Confirm(string text)
        {
            return MessageBox.Show(text, RibbonText, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        static string Created(string dir) { Directory.CreateDirectory(dir); return dir; }

        static Dictionary<string, object> Load(string file)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
        }

        static string LatestLog(string projectDir)
        {
            string best = "";
            foreach (string d in Directory.GetDirectories(projectDir, "kesit_tamamla_*"))
            {
                string f = Path.Combine(d, LogName);
                if (File.Exists(f) && (best == "" || File.GetLastWriteTimeUtc(f) > File.GetLastWriteTimeUtc(best))) best = f;
            }
            return best;
        }

        /// <summary>'1173/17/30714/0' → '17/30714/0' (ilk parça oturum önekidir; kesit_tamamla.nid ile aynı).</summary>
        static string Nid(string id)
        {
            if (id == null) return "";
            return id.Split('/').Length >= 4 ? id.Substring(id.IndexOf('/') + 1) : id;
        }

        static string Preview(string name, Dictionary<string, object> plan, ArrayList changes, string planFile)
        {
            Dictionary<string, object> st = ImportAction.D(plan["stats"]);
            StringBuilder b = new StringBuilder();
            b.AppendLine("Proje: " + name);
            b.AppendLine("Değişecek bağlantı: " + changes.Count + " (kesit " + ImportAction.S(st, "fill_cross_section") + ", renk "
                         + ImportAction.S(st, "fill_wire_color") + ", tel kodu " + ImportAction.S(st, "fill_wire_code") + ")");
            b.AppendLine("Uyarı (yazılmaz) seri yolda küçülen kesit: " + ImportAction.S(st, "flags_series_smaller"));
            b.AppendLine();
            int shown = 0;
            foreach (object o in changes)
            {
                if (++shown > 12) { b.AppendLine("… (tamamı: " + planFile + ")"); break; }
                Dictionary<string, object> c = ImportAction.D(o), set = ImportAction.D(c["set"]);
                b.AppendLine(ImportAction.S(c, "start") + " → " + ImportAction.S(c, "end") + ": " + ImportAction.S(set, "cross_section")
                             + " " + ImportAction.S(set, "wire_color") + " " + ImportAction.S(set, "wire_code"));
            }
            b.AppendLine();
            b.Append("Yalnız BOŞ alan yazılır. EPLAN Geri Al (Ctrl+Z) tek adımda geri getirir; ayrıca geri-al günlüğü tutulur. Devam edilsin mi?");
            return b.ToString();
        }

        /// <summary>Bağlantılar + uçlarındaki işlevler; Python'un okuduğu geri okuma alanlarıyla aynı adlar.</summary>
        internal static Dictionary<string, object> Read(Project prj)
        {
            List<object> conns = new List<object>(), functions = new List<object>();
            HashSet<string> seen = new HashSet<string>();
            foreach (Connection c in new DMObjectsFinder(prj).GetConnections(new ConnectionsFilter()))
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["object_id"] = ImportAction.Id(c);
                r["start"] = End(c.StartPin, functions, seen);
                r["end"] = End(c.EndPin, functions, seen);
                List<string> pots = new List<string>();
                try { foreach (PotentialDefinition p in c.PotentialDefinitions) pots.Add(p.PotentialName); } catch (Exception) { }
                r["potentials"] = pots;
                r["cross_section"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRECROSSSECTION.ToString(); });
                r["cross_section_unit"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRECROSSSECTION_UNIT.ToString(); });
                r["wire_color"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRENUMBER.ToString(); });
                r["wire_type"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRETYPE.ToString(); });
                r["wire_part"] = Parts(c);
                r["description"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_DESCRIPTION.ToMultiLangString().GetStringToDisplay(ISOCode.Language.L___); });
                conns.Add(r);
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["contract"] = "uvp.kesit_tamamla.oku";
            d["utc"] = DateTime.UtcNow.ToString("o");
            d["project"] = prj.ProjectLinkFilePath;
            d["connections"] = conns;
            d["pages"] = new List<object> { new Dictionary<string, object> { { "functions", functions } } };
            return d;
        }

        static string Parts(Connection c)
        {
            return ImportAction.Text(delegate { string s = ""; foreach (ArticleReference ar in c.ArticleReferences) s += (s == "" ? "" : ",") + ar.PartNr; return s; });
        }

        static Dictionary<string, object> End(Pin pin, List<object> functions, HashSet<string> seen)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            if (pin == null || pin.ParentFunction == null) return r;
            Function f = pin.ParentFunction;
            string id = ImportAction.Id(f);
            r["object_id"] = id;
            r["pin"] = pin.Name;
            r["function"] = ImportAction.Text(delegate { return f.Name; });
            r["page"] = ImportAction.Text(delegate { return f.Page.Name; });
            if (id != null && seen.Add(id))
            {
                Dictionary<string, object> fn = new Dictionary<string, object>();
                fn["object_id"] = id;
                fn["name"] = r["function"];
                fn["function_text"] = ImportAction.Text(delegate { return f.Properties.FUNC_TEXT.ToString(); });
                fn["technical"] = ImportAction.Text(delegate { return f.Properties.FUNC_TECHNICAL_CHARACTERISTIC.ToString(); });
                try
                {
                    FunctionDefinition def = f.FunctionDefinition;
                    if (def != null)
                    {
                        fn["category"] = def.FunctionCategory.ToString();
                        fn["definition_name"] = ImportAction.Text(delegate { return def.Name.GetAsString(); });
                    }
                }
                catch (Exception ex) { fn["definition_error"] = ex.Message; }
                List<object> pins = new List<object>();
                try { foreach (Pin p in f.Pins) pins.Add(new Dictionary<string, object> { { "name", p.Name } }); } catch (Exception) { }
                fn["pins"] = pins;
                functions.Add(fn);
            }
            return r;
        }

        static Dictionary<string, Connection> ById(Project prj)
        {
            Dictionary<string, Connection> by = new Dictionary<string, Connection>();
            foreach (Connection c in new DMObjectsFinder(prj).GetConnections(new ConnectionsFilter()))
                by[Nid(ImportAction.Id(c))] = c;
            return by;
        }

        static Dictionary<string, string> Now(Connection c)
        {
            return new Dictionary<string, string> {
                { "cross_section", ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRECROSSSECTION.ToString(); }) },
                { "wire_color", ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRENUMBER.ToString(); }) },
                { "wire_type", ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRETYPE.ToString(); }) },
                { "wire_part", Parts(c) } };
        }

        /// <summary>Planı tek UndoStep'te yazar; her bağlantının eski değeri günlüğe. Alan plandaki eski değerde değilse STALE.</summary>
        internal static Dictionary<string, object> Apply(Project prj, ArrayList changes, string logFile)
        {
            Dictionary<string, Connection> by = ById(prj);
            MDPartsDatabase db = null;
            List<string> log = new List<string>();
            try { db = new MDPartsManagement().OpenDatabase(); } catch (Exception ex) { log.Add("parça veritabanı açılamadı: " + ex.Message); }
            Dictionary<string, string> partOf = new Dictionary<string, string>();
            List<object> entries = new List<object>(), stale = new List<object>();
            int missing = 0;
            using (Yazim y = new Yazim(prj, RibbonText))   // kilit + tek Geri Al; hata → geri alınır
            {
                foreach (object o in changes)
                {
                    Dictionary<string, object> ch = ImportAction.D(o), set = ImportAction.D(ch["set"]), old = ImportAction.D(ch["old"]);
                    Connection k;
                    if (!by.TryGetValue(Nid(ImportAction.S(ch, "object_id")), out k)) { missing++; continue; }
                    Dictionary<string, string> now = Now(k);
                    bool fresh = true;
                    foreach (KeyValuePair<string, string> kv in now) if (kv.Value != ImportAction.S(old, kv.Key)) fresh = false;
                    if (!fresh) { stale.Add(ImportAction.S(ch, "object_id")); continue; }
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["object_id"] = ImportAction.Id(k);
                    e["old"] = now;
                    e["old_unit"] = ImportAction.Text(delegate { return k.Properties.CONNECTION_WIRECROSSSECTION_UNIT.ToString(); });
                    if (ImportAction.S(set, "cross_section") != "")
                    {
                        k.Properties.CONNECTION_WIRECROSSSECTION = ImportAction.S(set, "cross_section");
                        k.Properties.CONNECTION_WIRECROSSSECTION_UNIT = 1;
                    }
                    if (ImportAction.S(set, "wire_color") != "") k.Properties.CONNECTION_WIRENUMBER = ImportAction.S(set, "wire_color");
                    if (ImportAction.S(set, "wire_type") != "") k.Properties.CONNECTION_WIRETYPE = ImportAction.S(set, "wire_type");
                    string code = ImportAction.S(set, "wire_code");
                    if (code != "" && !partOf.ContainsKey(code))
                    {
                        string rule, found = "";
                        foreach (string spelling in new string[] { "LAPP." + code, code })
                        {
                            MDPart mp = db == null ? null : MacroDumpAction.FindPart(db, spelling, log, out rule);
                            if (mp != null) { found = mp.PartNr; break; }
                        }
                        partOf[code] = found;
                    }
                    if (code != "" && partOf[code] != "") { k.AddArticleReference(partOf[code]); e["part_added"] = partOf[code]; }
                    else if (code != "") e["part_not_found"] = code;
                    e["new"] = Now(k);
                    entries.Add(e);
                }
                y.Tamam();
            }
            Dictionary<string, object> journal = new Dictionary<string, object> {
                { "contract", "uvp.kesit_tamamla.gunluk" }, { "utc", DateTime.UtcNow.ToString("o") },
                { "project", prj.ProjectLinkFilePath }, { "entries", entries } };
            Io.WriteJson(logFile, journal);
            return new Dictionary<string, object> { { "written", entries.Count }, { "stale", stale.Count }, { "stale_ids", stale },
                                                    { "missing", missing }, { "log", log } };
        }

        /// <summary>Günlükteki bağlantı hâlâ yazılan değerdeyse eski değeri geri yazar, eklenen parçayı kaldırır; tek UndoStep.</summary>
        internal static Dictionary<string, object> Restore(Project prj, ArrayList entries)
        {
            Dictionary<string, Connection> by = ById(prj);
            int restored = 0, changedSince = 0, missing = 0;
            using (Yazim y = new Yazim(prj, RibbonText + ": geri al"))   // kilit + tek Geri Al; hata → geri alınır
            {
                foreach (object o in entries)
                {
                    Dictionary<string, object> e = ImportAction.D(o), old = ImportAction.D(e["old"]), neu = ImportAction.D(e["new"]);
                    Connection k;
                    if (!by.TryGetValue(Nid(ImportAction.S(e, "object_id")), out k)) { missing++; continue; }
                    Dictionary<string, string> now = Now(k);
                    bool same = true;
                    foreach (KeyValuePair<string, string> kv in now) if (kv.Value != ImportAction.S(neu, kv.Key)) same = false;
                    if (!same) { changedSince++; continue; }
                    string part = ImportAction.S(e, "part_added");
                    if (part != "")
                        foreach (ArticleReference ar in k.ArticleReferences)
                            if (ar.PartNr == part) { k.RemoveArticleReference(ar); break; }
                    k.Properties.CONNECTION_WIRECROSSSECTION = ImportAction.S(old, "cross_section");
                    int unit;
                    if (int.TryParse(ImportAction.S(e, "old_unit"), out unit)) k.Properties.CONNECTION_WIRECROSSSECTION_UNIT = unit;
                    k.Properties.CONNECTION_WIRENUMBER = ImportAction.S(old, "wire_color");
                    k.Properties.CONNECTION_WIRETYPE = ImportAction.S(old, "wire_type");
                    restored++;
                }
                y.Tamam();
            }
            return new Dictionary<string, object> { { "restored", restored }, { "changed_since", changedSince }, { "missing", missing } };
        }

        static void Python(PanelSettings s, string input, string output)
        {
            ProcessStartInfo info = new ProcessStartInfo(s.Python,
                "-X utf8 -m analyzer_v3.kesit_tamamla plan --input \"" + input + "\" --out \"" + output + "\"");
            info.WorkingDirectory = s.Root;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(info))
            {
                p.StandardOutput.ReadToEndAsync();
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    throw new InvalidOperationException("Python kesit_tamamla 120 sn içinde bitmedi.");
                }
                if (p.ExitCode != 0 || !File.Exists(output))
                    throw new InvalidOperationException("Python kesit_tamamla başarısız (çıkış " + p.ExitCode + ", python " + s.Python
                                                        + ", motor " + s.Root + "): " + (err.Length > 600 ? err.Substring(err.Length - 600) : err));
            }
        }
    }
}
