// =====================================================================================
// UvpPdfToP8Check.cs — EPLAN'ın kendi proje denetimi (Prüflauf / "Projeyi denetle") + mesaj dökümü.
// Yalnız output\p8runs altındaki UVP_ test projesi (Guard). Denetim mesajları PROJEYE yazılır.
//
//   UvpPdfToP8Check /PROJECT:"<elk>" /OUT:"<klasör>" [/SCHEME:"<şema adı>"] [/SCHEMEXML:"<xml>"] [/CATALOG:1]
//     SCHEMEXML : denetimden önce proje ayarı XML'i içe aktarılır (ProjectSettings.ReadSettings);
//                 UVP şeması bu yolla projeye gelir (PROJECT.XEsInspectionGui.Scheme_Verifications.<ad>).
//     SCHEME    : kullanılacak şema; boşsa EPLAN'ın son kullanılan şeması.
//     CATALOG:1 : EPLAN'da kayıtlı bütün denetim mesajlarının listesi (kod, metin şablonu) catalog.json'a.
//   Çıktı: check.json — şemalar, kullanılan şema, mesajlar (kod RRRNNN, metin, nesne, sayfa), kod sayımı.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.EServices;
using Eplan.EplApi.HEServices;

namespace Uvp.PdfToP8.Host
{
    public class CheckAction : IEplAction
    {
        const string SchemeNode = "XEsInspectionGui.Scheme_Verifications";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Check";
            Ordinal = 34;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outDir = Io.Param(ctx, "OUT");
            Directory.CreateDirectory(outDir);
            string target = Path.Combine(outDir, "check.json");
            if (File.Exists(target)) return false;            // önceki kanıt ezilmez
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["contract"] = "uvp.pdf2p8.check";
            result["contract_version"] = "1.0";
            result["utc"] = DateTime.UtcNow.ToString("o");
            result["writes_project"] = true;                  // denetim mesajları projeye yazılır
            ProjectManager pm = new ProjectManager();
            Project prj = null;
            bool close = false;
            try
            {
                string file = Guard.TestProject(Io.Param(ctx, "PROJECT"));
                foreach (Project open in pm.OpenProjects)
                    if (string.Equals(open.ProjectLinkFilePath, file, StringComparison.OrdinalIgnoreCase)) prj = open;
                if (prj == null) { prj = pm.OpenProject(file); close = true; }
                result["project"] = prj.ProjectLinkFilePath;
                result["schemes_before"] = Schemes(prj);
                string xml = Io.Param(ctx, "SCHEMEXML");
                if (xml != "")
                {
                    new ProjectSettings(prj).ReadSettings(Path.GetFullPath(xml));
                    result["scheme_xml_imported"] = Path.GetFullPath(xml);
                    result["schemes_after_import"] = Schemes(prj);
                }
                string scheme = Io.Param(ctx, "SCHEME");
                result["scheme"] = scheme;
                result["last_used_before"] = LastUsed(prj);
                DateTime t0 = DateTime.UtcNow;
                new Check().VerifyProject(prj, scheme);
                result["check_seconds"] = Math.Round((DateTime.UtcNow - t0).TotalSeconds, 1);
                result["last_used_after"] = LastUsed(prj);
                List<object> messages = new List<object>();
                Dictionary<string, int> byCode = new Dictionary<string, int>();
                PrjMessagesCollection col = new PrjMessagesCollection(prj);
                result["count_property"] = col.Count;
                PrjMessagesEnumerator it = col.GetPrjMsgEnumerator();
                while (it.MoveNext())
                {
                    ProjectMessage m = it.CurrentProjectMessage as ProjectMessage;
                    if (m == null) continue;
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    int region = (int)m.GetGroup();
                    int id = m.GetId();
                    string code = region.ToString("000") + id.ToString("000");
                    row["code"] = code;
                    row["region"] = m.GetGroup().ToString();
                    row["category"] = m.GetCategory().ToString();
                    row["text"] = ImportAction.Text(delegate { return m.GetText(); });
                    row["done"] = m.GetDone();
                    row["creation"] = ImportAction.Text(delegate { return m.CreationType.ToString(); });
                    row["object1"] = Describe(m.GetObject1());
                    row["object2"] = Describe(m.GetObject2());
                    messages.Add(row);
                    int n;
                    byCode.TryGetValue(code, out n);
                    byCode[code] = n + 1;
                }
                result["messages"] = messages;
                result["by_code"] = byCode;
                result["ok"] = true;
                // Ek dökümler denetim sonucunu bozmaz: hataları kendi alanlarına yazılır.
                if (Io.Param(ctx, "CATALOG") == "1")
                {
                    try { Io.WriteJson(Path.Combine(outDir, "catalog.json"), Catalog(prj)); }
                    catch (Exception ex) { result["catalog_error"] = ex.GetType().Name + ": " + ex.Message; }
                }
                string dump = Io.Param(ctx, "DUMPSCHEME");
                if (dump != "") result["scheme_dump"] = Dump(prj, dump.Split(';'));
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (prj != null && close) { try { prj.Close(); } catch (Exception) { } }
            }
            Io.WriteJson(target, result);
            return true;
        }

        static List<string> Schemes(Project prj)
        {
            List<string> names = new List<string>();
            try
            {
                StringCollection nodes = new StringCollection();
                new ProjectSettingNode(prj, SchemeNode).GetListOfNodes(ref nodes, false);
                foreach (string n in nodes) names.Add(n);
            }
            catch (Exception ex) { names.Add("<hata: " + ex.Message + ">"); }
            return names;
        }

        static string LastUsed(Project prj)
        {
            return ImportAction.Text(delegate { return new ProjectSettings(prj).GetStringSetting(SchemeNode + ".LastUsed", 0); });
        }

        static object Describe(object o)
        {
            if (o == null) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["type"] = o.GetType().Name;
            StorableObject so = o as StorableObject;
            if (so != null) d["id"] = ImportAction.Text(delegate { return so.ToStringIdentifier(); });
            Function f = o as Function;
            if (f != null) d["name"] = ImportAction.Text(delegate { return f.Name; });
            Placement p = o as Placement;
            if (p != null && p.Page != null) d["page"] = ImportAction.Text(delegate { return p.Page.Name; });
            Page page = o as Page;
            if (page != null) d["page"] = page.Name;
            return d;
        }

        static List<object> Catalog(Project prj)
        {
            List<object> rows = new List<object>();
            // Ölçüm 02.10: (false, prj) kurucusu NullReferenceException verdi; yalnız lisanslı mesajlar okunur.
            PrjMessagesRegisteredCollection reg = new PrjMessagesRegisteredCollection(true);
            PrjMessagesRegisteredEnumerator it = reg.GetRegisteredPrjMsgsEnumerator();
            while (it.MoveNext())
            {
                Dictionary<string, object> row = new Dictionary<string, object>();
                try
                {
                    ElectroMessage m = it.Current as ElectroMessage;
                    if (m == null) { row["type"] = it.Current == null ? "null" : it.Current.GetType().Name; rows.Add(row); continue; }
                    row["code"] = ((int)m.GetGroup()).ToString("000") + m.GetId().ToString("000");
                    row["region"] = m.GetGroup().ToString();
                    row["category"] = m.GetCategory().ToString();
                    row["template"] = ImportAction.Text(delegate { return m.GetTextTemplate(); });
                }
                catch (Exception ex) { row["error"] = ex.GetType().Name + ": " + ex.Message; }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Şemanın her mesaj satırı (RRRNNN) için MessageType/VerificationType değerleri.</summary>
        static Dictionary<string, object> Dump(Project prj, string[] names)
        {
            Dictionary<string, object> all = new Dictionary<string, object>();
            ProjectSettings ps = new ProjectSettings(prj);
            foreach (string name in names)
            {
                Dictionary<string, object> rows = new Dictionary<string, object>();
                try
                {
                    StringCollection nodes = new StringCollection();
                    new ProjectSettingNode(prj, SchemeNode + "." + name + ".Data").GetListOfNodes(ref nodes, false);
                    foreach (string code in nodes)
                    {
                        Dictionary<string, object> v = new Dictionary<string, object>();
                        string basePath = SchemeNode + "." + name + ".Data." + code + ".";
                        v["MessageType"] = ImportAction.Text(delegate { return ps.GetNumericSetting(basePath + "MessageType", 0).ToString(); });
                        v["VerificationType"] = ImportAction.Text(delegate { return ps.GetNumericSetting(basePath + "VerificationType", 0).ToString(); });
                        rows[code] = v;
                    }
                }
                catch (Exception ex) { rows["<hata>"] = ex.GetType().Name + ": " + ex.Message; }
                all[name] = rows;
            }
            return all;
        }
    }
}
