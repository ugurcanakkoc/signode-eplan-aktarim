// =====================================================================================
// UvpPdfToP8Ribbon — şerit kurulumu, günlüğü ve teşhisi (13.10; kök neden: "UVP Çizimci Araçları" sekmesi görünmedi)
//
// 13.10 11:55 kullanıcı ekranı (EPLAN 2026): şeritte "UVP" sekmesi ve Genişletmeler → API → "UVP PDF → P8" VAR, ama 15 çizimci
// aracının sekmesi YOK; kurulu DLL'de (Host_1012d) adlar var. Eski OnInitGui her AddToGroup'u sessiz try/catch ile yutuyordu.
// Guiu.xml (2026.0.3) kanıtı: RibbonBar.AddTab / RibbonTab.AddCommandGroup / RibbonCommandGroup.AddCommand başarısızlıkta
// İSTİSNA ATMAZ, NULL döndürür ("Created tab, or null if nothing was created"); RibbonTab.IsSetAsVisible "sekme işaretli
// (görünür) mi" — kullanıcı şerit özelleştirmesi sekmeyi işaretsiz bırakabilir; GetTab(string) dil duyarlı olabilir
// (MultiLangString+bool "başka dilde de ara" aşırı yüklemesi var). Hangisi olduğu yalnız canlı EPLAN'da ölçülür → günlük.
//
// Kurallar:
//   * Her adım %LOCALAPPDATA%\UVP\PdfToP8\host_init.log'a yazılır (istisna türü+mesajı, null dönüşler, görünürlük).
//   * Hiçbir komut kaybolmaz: araç sekmesi açılamazsa "Çizim/Kontrol/Düzenle" grupları MEVCUT "UVP" sekmesine; o da
//     yoksa Genişletmeler → API grubuna (her çalışma alanında var; "Komutu ara" bulur).
//   * Sekme bulunduğunda/oluşturulduğunda IsSetAsVisible=false ise true yapılır (günlüğe önce/sonra).
//   * UvpPdfToP8RibbonProbe: şeritteki sekme/grup/komutları JSON'a döker (kullanıcı kanıtı; "UVP şerit teşhisi" düğmesi).
// =====================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Gui;

namespace Uvp.PdfToP8.Host
{
    internal static class Ribbon
    {
        internal const string ProbeText = "UVP şerit teşhisi";
        internal const string ProbeAction = "UvpPdfToP8RibbonProbe";
        internal const string DiagGroup = "Teşhis";

        // Çizimci araçları: grup, düğme yazısı, eylem satırı. "Kontrol" salt okur, "Çizim"/"Düzenle" yazar (onay + tek UndoStep).
        static readonly string[][] Tools = {
            new[] { OrtakHatAction.GroupText, OrtakHatAction.RibbonText, "UvpCizimciOrtakHat" },
            new[] { OrtakHatAction.GroupText, ZincirAction.RibbonText, "UvpCizimciZincir" },                     // kullanıcı 13.10
            new[] { OrtakHatAction.GroupText, KabloSeciciAction.RibbonText, "UvpCizimciKabloSec" },
            new[] { "Kontrol", "Kontrol Gezgini", "UvpPdfToP8Panel /SAYFA:gezgin" },                             // A1
            new[] { "Kontrol", TeslimDenetimAction.RibbonText, "UvpPdfToP8TeslimDenetim" },                     // A2
            new[] { "Kontrol", KuralDenetimAction.RibbonText, "UvpCizimciKuralDenetim" },                       // B1
            new[] { "Kontrol", "Çizimci araçları (panel)", "UvpPdfToP8Panel /SAYFA:araclar" },                  // C1-C5
            new[] { "Düzenle", OkHedefAction.RibbonText, "UvpCizimciOkHedef" },                                 // A3
            new[] { "Düzenle", YaziKaydirAction.RibbonText, "UvpCizimciYaziKaydir" },                           // A4
            new[] { "Düzenle", ParcaAtaAction.RibbonText, "UvpCizimciParcaAta" },                               // A5
            new[] { "Düzenle", KesitTamamlaAction.RibbonText, "UvpCizimciKesitTamamla" },                       // B2
            new[] { "Düzenle", KesitTamamlaAction.RibbonText + ": geri al", "UvpCizimciKesitTamamla /MODE:GERIAL" },
            new[] { "Düzenle", CihazYerlestirAction.RibbonText, "UvpCizimciCihazYerlestir" },                   // B3
            new[] { "Düzenle", OkYardimciAction.RibbonText, "UvpCizimciOk" },                                   // B4
            new[] { "Düzenle", Sayfa0Action.RibbonText, "UvpCizimciSayfa0" },                                   // B5
        };

        internal static string DataDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVP", "PdfToP8");
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            return dir;
        }

        internal static string LogFile() { return Path.Combine(DataDir(), "host_init.log"); }

        internal static void Log(string line)
        {
            try { File.AppendAllText(LogFile(), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\r\n", new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        static string Err(Exception ex) { return ex.GetType().FullName + ": " + ex.Message; }

        /// <summary>Gecikmeli şerit eylemi (sistem başlangıcı bitince). Asla istisna sızdırmaz; her adım günlükte.</summary>
        internal static void Build(RibbonBar bar)
        {
            try
            {
                System.Reflection.Assembly me = typeof(Ribbon).Assembly;
                Log("==== şerit kurulumu: " + me.GetName().Name + " | " + me.Location);
                // 1. Genişletmeler → API (bugün kanıtlı görünen tek yer; "Komutu ara" bulur). Önceki oturumdan kalan aynı adlı
                //    komut kaldırılır (çift düğme olmaz).
                ApiCommand(bar, AddInModule.RibbonText, "UvpPdfToP8Panel");
                ApiCommand(bar, ProbeText, ProbeAction);
                // 2. UVP sekmesi: panel, bağlantılar, teşhis.
                RibbonTab uvp = Tab(bar, AddInModule.TabText);
                Command(Group(uvp, AddInModule.GroupText), AddInModule.RibbonText, "UvpPdfToP8Panel");
                Command(Group(uvp, RebuildAction.GroupText), RebuildAction.RibbonText, "UvpPdfToP8Rebuild");
                Command(Group(uvp, DiagGroup), ProbeText, ProbeAction);
                // 3. Çizimci araçları (kullanıcı 06.10: ayrı sekme). Açılamazsa gruplar UVP sekmesine; o da yoksa API grubuna.
                // ÖLÇÜM 13.10 13:50 (kullanıcı GUI, host_init.log): 'UVP Çizimci Araçları' AddTab → oluştu, IsVisible=True yazıldı ama
                // şeritte GÖRÜNMEDİ; 'UVP' sekmesi (ASCII ad) görünüyor. Ayrı sekme açılmaz: bütün çizimci grupları 'UVP' sekmesine.
                RibbonTab home = uvp;
                Log(home != null ? "araçlar: gruplar '" + AddInModule.TabText + "' sekmesine (ayrı sekme GUI'de görünmediği için)"
                    : "araçlar: hiçbir sekme yok → Genişletmeler → API (YEDEK 2)");
                foreach (string[] row in Tools)
                {
                    if (home != null) Command(Group(home, row[0]), row[1], row[2]);
                    else ApiCommand(bar, row[1], row[2]);
                }
                // ÖLÇÜM 13.10 15:43 (Host_1013c, kullanıcı GUI): gruplar/komutlar 'eklendi' yazıldı ama 'UVP' sekmesinde de
                // GÖRÜNMEDİ. Guiu.xml: RibbonBar.RegisterCommand(info, icon, tab, group) "ribbon yoksa sistem başlangıcı bitince
                // eklenir" — kalıcı kayıt yolu; ayrıca her araç Genişletmeler → API grubuna da eklenir (orası kesin görünüyor).
                foreach (string[] row in Tools)
                {
                    Register(bar, AddInModule.TabText, row[0], row[1], row[2]);
                    ApiCommand(bar, row[1], row[2]);
                }
                Register(bar, AddInModule.TabText, RebuildAction.GroupText, RebuildAction.RibbonText, "UvpPdfToP8Rebuild");
                ApiCommand(bar, RebuildAction.RibbonText, "UvpPdfToP8Rebuild");
                Log("==== şerit kurulumu bitti");
            }
            catch (Exception ex) { Log("şerit kurulumu DURDU: " + Err(ex)); }
        }

        /// <summary>RegisterCommand: EPLAN'ın kalıcı şerit kaydı (Guiu.xml). Önce aynı eylemi aynı sekme/gruptan siler (çift düğme olmaz).</summary>
        static void Register(RibbonBar bar, string tab, string group, string text, string action)
        {
            try { bar.UnregisterCommand(action, tab, group); } catch (Exception ex) { Log("kayıt '" + tab + "/" + group + "/" + text + "': Unregister " + Err(ex)); }
            try
            {
                RibbonCommandInfo info = new RibbonCommandInfo(text, action);
                info.Tooltip = text;
                bar.RegisterCommand(info, new RibbonIcon(CommandIcon.Application), tab, group);
                Log("kayıt '" + tab + "/" + group + "/" + text + "': RegisterCommand → " + action);
            }
            catch (Exception ex) { Log("kayıt '" + tab + "/" + group + "/" + text + "': RegisterCommand istisna " + Err(ex)); }
        }

        static void ApiCommand(RibbonBar bar, string text, string action)
        {
            try { bar.RemoveCommand(text); } catch (Exception ex) { Log("API '" + text + "': RemoveCommand " + Err(ex)); }
            try { bar.AddCommand(text, action); Log("API '" + text + "': eklendi → " + action); }
            catch (Exception ex) { Log("API '" + text + "': AddCommand istisna " + Err(ex)); }
        }

        /// <summary>Sekme: önce Tabs listesinden (ad ya da görünen ad), sonra GetTab, sonra AddTab. Bulunan/oluşan sekme görünür yapılır.</summary>
        internal static RibbonTab Tab(RibbonBar bar, string name)
        {
            RibbonTab tab = null;
            try
            {
                foreach (RibbonTab t in bar.Tabs)
                {
                    string n = "", d = "";
                    try { n = t.Name ?? ""; } catch (Exception) { }
                    try { d = t.DisplayName ?? ""; } catch (Exception) { }
                    if (n == name || d == name) { tab = t; break; }
                }
                Log("sekme '" + name + "': Tabs listesinde " + (tab != null ? "VAR" : "yok"));
            }
            catch (Exception ex) { Log("sekme '" + name + "': Tabs okunamadı " + Err(ex)); }
            if (tab == null)
            {
                try { tab = bar.GetTab(name); Log("sekme '" + name + "': GetTab → " + (tab == null ? "null" : "VAR")); }
                catch (Exception ex) { Log("sekme '" + name + "': GetTab istisna " + Err(ex)); }
            }
            if (tab == null)
            {
                try { tab = bar.AddTab(name); Log("sekme '" + name + "': AddTab → " + (tab == null ? "NULL (oluşmadı)" : "oluştu")); }
                catch (Exception ex) { Log("sekme '" + name + "': AddTab istisna " + Err(ex)); }
            }
            if (tab != null) Visible(tab, name);
            return tab;
        }

        static void Visible(RibbonTab tab, string name)
        {
            try
            {
                bool set = tab.IsSetAsVisible, vis = tab.IsVisible, custom = tab.IsCustom;
                Log("sekme '" + name + "': IsSetAsVisible=" + set + " IsVisible=" + vis + " IsCustom=" + custom);
                if (!set) { tab.IsSetAsVisible = true; Log("sekme '" + name + "': IsSetAsVisible → " + tab.IsSetAsVisible); }
            }
            catch (Exception ex) { Log("sekme '" + name + "': görünürlük okunamadı/yazılamadı " + Err(ex)); }
        }

        static RibbonCommandGroup Group(RibbonTab tab, string name)
        {
            if (tab == null) return null;
            string tabName = "";
            try { tabName = tab.Name; } catch (Exception) { }
            RibbonCommandGroup group = null;
            try
            {
                foreach (RibbonCommandGroup g in tab.CommandGroups)
                {
                    string n = "";
                    try { n = g.Name ?? ""; } catch (Exception) { }
                    if (n == name) { group = g; break; }
                }
            }
            catch (Exception ex) { Log("grup '" + tabName + "/" + name + "': CommandGroups okunamadı " + Err(ex)); }
            if (group == null)
            {
                try { group = tab.AddCommandGroup(name); Log("grup '" + tabName + "/" + name + "': AddCommandGroup → " + (group == null ? "NULL (oluşmadı)" : "oluştu")); }
                catch (Exception ex) { Log("grup '" + tabName + "/" + name + "': AddCommandGroup istisna " + Err(ex)); }
            }
            return group;
        }

        static void Command(RibbonCommandGroup group, string text, string action)
        {
            if (group == null) { Log("komut '" + text + "': grup yok, eklenmedi"); return; }
            string where = "";
            try { where = group.Parent.Name + "/" + group.Name; } catch (Exception) { }
            try
            {
                foreach (KeyValuePair<uint, RibbonCommand> kv in group.Commands)
                {
                    string t = "";
                    try { t = kv.Value.Text ?? ""; } catch (Exception) { }
                    if (t == text) return;          // önceki oturumdan kalmış (şerit kalıcı); ikinci düğme olmaz
                }
            }
            catch (Exception ex) { Log("komut '" + where + "/" + text + "': Commands okunamadı " + Err(ex)); }
            try
            {
                RibbonCommand c = group.AddCommand(text, action);
                Log("komut '" + where + "/" + text + "': AddCommand → " + (c == null ? "NULL (oluşmadı)" : "eklendi") + " → " + action);
            }
            catch (Exception ex) { Log("komut '" + where + "/" + text + "': AddCommand istisna " + Err(ex)); }
        }

        static string Safe(Func<string> read) { try { return read() ?? ""; } catch (Exception ex) { return "!" + ex.GetType().Name; } }
        static object SafeBool(Func<bool> read) { try { return read(); } catch (Exception ex) { return "!" + ex.GetType().Name; } }

        /// <summary>Şeritteki bütün sekme/grup/komutlar (teşhis dökümü). `ours`: UVP komutlarının yerleri.</summary>
        internal static List<object> Dump(RibbonBar bar, List<string> ours)
        {
            List<object> tabs = new List<object>();
            foreach (RibbonTab t in bar.Tabs)
            {
                Dictionary<string, object> tr = new Dictionary<string, object>();
                tr["name"] = Safe(delegate { return t.Name; });
                tr["display_name"] = Safe(delegate { return t.DisplayName; });
                tr["identifier"] = Safe(delegate { return Convert.ToString(t.Identifier); });
                tr["is_custom"] = SafeBool(delegate { return t.IsCustom; });
                tr["is_visible"] = SafeBool(delegate { return t.IsVisible; });
                tr["is_set_as_visible"] = SafeBool(delegate { return t.IsSetAsVisible; });
                List<object> groups = new List<object>();
                try
                {
                    foreach (RibbonCommandGroup g in t.CommandGroups)
                    {
                        Dictionary<string, object> gr = new Dictionary<string, object>();
                        gr["name"] = Safe(delegate { return g.Name; });
                        gr["is_custom"] = SafeBool(delegate { return g.IsCustom; });
                        List<object> cmds = new List<object>();
                        try
                        {
                            foreach (KeyValuePair<uint, RibbonCommand> kv in g.Commands)
                            {
                                RibbonCommand c = kv.Value;
                                Dictionary<string, object> cr = new Dictionary<string, object>();
                                cr["id"] = kv.Key;
                                cr["text"] = Safe(delegate { return c.Text; });
                                cr["action"] = Safe(delegate { return c.ActionCommandLine; });
                                cr["is_custom"] = SafeBool(delegate { return c.IsCustom; });
                                cr["is_enabled"] = SafeBool(delegate { return c.IsEnabled; });
                                cmds.Add(cr);
                                string a = Convert.ToString(cr["action"]);
                                if (a.StartsWith("Uvp", StringComparison.Ordinal) || Convert.ToString(cr["text"]).IndexOf("UVP", StringComparison.OrdinalIgnoreCase) >= 0)
                                    ours.Add(Convert.ToString(tr["name"]) + " / " + Convert.ToString(gr["name"]) + " / " + Convert.ToString(cr["text"]) + " → " + a);
                            }
                        }
                        catch (Exception ex) { gr["commands_error"] = Err(ex); }
                        gr["commands"] = cmds;
                        groups.Add(gr);
                    }
                }
                catch (Exception ex) { tr["groups_error"] = Err(ex); }
                tr["groups"] = groups;
                tabs.Add(tr);
            }
            return tabs;
        }
    }

    /// <summary>UvpPdfToP8RibbonProbe [/OUT:"<json>"]: şerit dökümü + host_init.log kuyruğu. Projeye dokunmaz.
    /// Şeritten ("UVP şerit teşhisi": Genişletmeler → API ve UVP → Teşhis) tıklanınca dosya yolu pencerede gösterilir.</summary>
    public class RibbonProbeAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = Ribbon.ProbeAction;
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            string outFile = Io.Param(ctx, "OUT");
            bool interactive = outFile == "";
            if (interactive) outFile = Path.Combine(Ribbon.DataDir(), "ribbon_probe_" + Io.Stamp() + ".json");
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["contract"] = "uvp.pdf2p8.ribbon-probe";
            r["utc"] = DateTime.UtcNow.ToString("o");
            r["assembly"] = typeof(RibbonProbeAction).Assembly.GetName().Name;
            r["location"] = typeof(RibbonProbeAction).Assembly.Location;
            try { r["eplan_file_version"] = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion; } catch (Exception) { }
            r["log_file"] = Ribbon.LogFile();
            List<string> ours = new List<string>();
            try
            {
                r["tabs"] = Ribbon.Dump(new RibbonBar(), ours);
                r["ok"] = true;
            }
            catch (Exception ex) { r["ok"] = false; r["error"] = ex.GetType().FullName + ": " + ex.Message; }
            r["uvp_commands"] = ours;
            try
            {
                if (File.Exists(Ribbon.LogFile()))
                {
                    string[] lines = File.ReadAllLines(Ribbon.LogFile(), Encoding.UTF8);
                    List<string> tail = new List<string>();
                    for (int i = Math.Max(0, lines.Length - 80); i < lines.Length; i++) tail.Add(lines[i]);
                    r["host_init_log_tail"] = tail;
                }
            }
            catch (Exception ex) { r["log_error"] = ex.Message; }
            Io.WriteJson(outFile, r);
            if (interactive)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Şerit dökümü yazıldı:\n" + outFile + "\n\nBaşlangıç günlüğü:\n" + Ribbon.LogFile() + "\n");
                sb.AppendLine("UVP komutları (" + ours.Count + "):");
                foreach (string s in ours) sb.AppendLine("  " + s);
                sb.AppendLine("\nBu iki dosyayı asistana gönderin.");
                MessageBox.Show(sb.ToString(), Ribbon.ProbeText, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }
    }
}
