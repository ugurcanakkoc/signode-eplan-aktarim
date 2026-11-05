// =====================================================================================
// UvpPdfToP8Panel — aracın EPLAN'IN İÇİNDE açılan penceresi (tarayıcı değil).
//
//   Şerit: UVP sekmesi veya Extensions (Erweiterungen) → API → "UVP PDF → P8"; yoksa "Komutu ara: UVP" (veya eylem: UvpPdfToP8Panel)
//   UvpPdfToP8Panel [/ROOT:"<motor klasörü>"] [/PORT:<port>] [/PYTHON:"<python.exe>"] [/RUN:"<çalışma>"]
//
// Ayarlar DLL'in yanındaki `uvp_pdf2p8.json` dosyasından okunur (root, python, run, port, template);
// eylem parametresi verilirse o geçerlidir. Kullanıcıdan terminal/Python/JSON işi istenmez.
//
// Pencere WebView2 ile YEREL motora bağlanır (127.0.0.1, yalnız bu makine). Motor yoksa add-in
// başlatır, çıktısını günlüğe yazar; yanıt vermezse nedenini (Python yok, paket eksik, port dolu)
// pencerede gösterir. Pencere kapanınca yalnız KENDİ başlattığı motoru kapatır.
//
// Sayfadan gelen mesajlar SINIRLIDIR: 'ping', 'pickPdf', 'catalog', 'target', 'import', 'openTarget', 'checkTarget'.
// 'checkTarget' (14.10): aktarım başlamadan hedefin defterinde aynı belge/sayfa var mı (yalnız okur); sayfa
// 'Üstüne yaz / Yeni proje / Projeyi aç / Vazgeç' sorar. 'import' replace=true → eylem /REPLACE:1.
// Keyfi komut yürütülmez. 'import' yalnız `UvpPdfToP8Import` eylemini çağırır; hedef ya şablondan
// YENİ test projesidir ya da eklentinin daha önce oluşturduğu output\p8runs altındaki UVP_ projesi.
// Açık kullanıcı projesine yazılmaz. Başarısız/kısmi aktarım bu işlemin nesneleriyle geri alınır.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.DataModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Uvp.PdfToP8.Host
{
    public class PanelAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Panel";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx)
        {
            PanelSettings settings = PanelSettings.Load();
            string root = Io.Param(ctx, "ROOT"), port = Io.Param(ctx, "PORT"), python = Io.Param(ctx, "PYTHON"), run = Io.Param(ctx, "RUN");
            if (root != "") settings.Root = root;
            if (python != "") settings.Python = python;
            if (run != "") settings.Run = run;
            int number;
            if (int.TryParse(port, out number)) settings.Port = number;
            // 12.10 çizimci araçları: /SAYFA:gezgin|araclar|cihaz|parca panelin o sayfasını açar (yalnız bu adlar).
            string sayfa = Io.Param(ctx, "SAYFA");
            string path = Array.IndexOf(new string[] { "gezgin", "araclar", "cihaz", "parca" }, sayfa) >= 0 ? "/" + sayfa : "/";
            // MODELESS: pencere açıkken EPLAN kullanılmaya devam eder. Modal ShowDialog
            // EPLAN'ın arayüz iş parçacığını kilitliyordu.
            if (PanelForm.Instance != null && !PanelForm.Instance.IsDisposed)
            {
                if (sayfa != "") { PanelForm.Instance.StartPath = path; PanelForm.Instance.Navigate(); }
                PanelForm.Instance.BringToFront();
                PanelForm.Instance.Activate();
                return true;
            }
            PanelForm.Instance = new PanelForm(settings);
            PanelForm.Instance.StartPath = path;
            PanelForm.Instance.Show();
            return true;
        }
    }

    /// <summary>Panel ayarları: DLL yanındaki uvp_pdf2p8.json; yoksa tahmin.</summary>
    public class PanelSettings
    {
        public string Root, Python, Run, Template, Source;
        public int Port = 8791;

        public static string DllFolder()
        {
            return Path.GetDirectoryName(new Uri(typeof(PanelSettings).Assembly.CodeBase).LocalPath);
        }

        public static PanelSettings Load()
        {
            PanelSettings s = new PanelSettings();
            // ÖLÇÜM (pilot_v01/A): EPLAN eklentiyi ShadowCopyAssemblies altına KOPYALAYIP oradan yükler;
            // kopya klasörde yalnız DLL var, yanındaki ayar dosyası gelmez. Bu yüzden önce kullanıcı başına
            // sabit yer (kurulum yazar), sonra DLL'in yanı (gölge kopya yoksa) denenir.
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "UVP", "PdfToP8", "uvp_pdf2p8.json");
            if (!File.Exists(file)) file = Path.Combine(DllFolder(), "uvp_pdf2p8.json");
            if (File.Exists(file))
            {
                try
                {
                    Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(file, Encoding.UTF8));
                    s.Root = Get(d, "root"); s.Python = Get(d, "python"); s.Run = Get(d, "run"); s.Template = Get(d, "template");
                    int port;
                    if (int.TryParse(Get(d, "port"), out port)) s.Port = port;
                    s.Source = file;
                }
                catch (Exception ex) { s.Source = file + " okunamadı: " + ex.Message; }
            }
            if (string.IsNullOrEmpty(s.Root)) s.Root = PanelForm.GuessRoot();
            if (string.IsNullOrEmpty(s.Python)) s.Python = PanelForm.GuessPython();
            if (string.IsNullOrEmpty(s.Template))
                s.Template = @"C:\ProgramData\EPLAN\O_Data\Electric P8 Data\2026.0.3\Templates\EPLAN\IEC_bas001.zw9";
            return s;
        }

        static string Get(Dictionary<string, object> d, string key)
        {
            return d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : "";
        }
    }

    public class PanelForm : Form
    {
        public static PanelForm Instance;
        readonly PanelSettings _settings;
        readonly WebView2 _web = new WebView2();
        readonly System.Windows.Forms.Label _status = new System.Windows.Forms.Label();
        Process _engine;                     // yalnız bizim başlattığımız motor
        StreamWriter _engineLog;
        string _engineLogPath = "";
        bool _busy;                          // aktarım sürerken ikinci işlem başlamaz (çift tıklama)
        string _target = "";                 // bu pilotun hedef test projesi (sayfalar arasında korunur)
        internal string StartPath = "/";     // motor hazır olunca açılan sayfa (PanelAction /SAYFA)

        static string DataFolder()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVP", "PdfToP8");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public PanelForm(PanelSettings settings)
        {
            _settings = settings;
            Text = "UVP · PDF → EPLAN P8";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 1500; Height = 950;
            _status.Dock = DockStyle.Bottom; _status.Height = 22; _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "Motor başlatılıyor…";
            _web.Dock = DockStyle.Fill;
            Controls.Add(_web); Controls.Add(_status);
            _target = LoadTarget();
            Load += OnLoaded;
            FormClosed += OnClosed;
        }

        public static string GuessRoot()
        {
            // DLL <kök>\output\eplan_addin_bin altında durur.
            DirectoryInfo dir = new DirectoryInfo(PanelSettings.DllFolder());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "analyzer_v3", "server.py")))
                dir = dir.Parent;
            return dir == null ? PanelSettings.DllFolder() : dir.FullName;
        }

        public static string GuessPython()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim() == "" || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "python.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            // EPLAN süreci PATH'i kullanıcının kabuğundan farklı olabilir: bilinen kurulum
            // klasörleri de denenir. Bulunamazsa panel bunu açıkça yazar.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<string> roots = new List<string> { Path.Combine(local, "Programs", "Python"),
                                                    @"C:\Program Files\Python312",
                                                    @"C:\Program Files\Python311",
                                                    @"C:\Program Files\Python310" };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string direct = Path.Combine(root, "python.exe");
                if (File.Exists(direct)) return direct;
                try
                {
                    foreach (string sub in Directory.GetDirectories(root))
                    {
                        string candidate = Path.Combine(sub, "python.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch (IOException) { }
            }
            return "python";
        }

        // ------------------------------------------------------------------ hedef test projesi
        static string StateFile() { return Path.Combine(DataFolder(), "panel_state.json"); }

        static string LoadTarget()
        {
            try
            {
                if (!File.Exists(StateFile())) return "";
                Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(StateFile(), Encoding.UTF8));
                string target = d.ContainsKey("target") ? Convert.ToString(d["target"]) : "";
                return File.Exists(target) ? target : "";       // silinmiş proje hedef kalmaz
            }
            catch (Exception) { return ""; }
        }

        void SaveTarget(string target)
        {
            _target = target ?? "";
            Io.WriteJson(StateFile(), new Dictionary<string, object> { { "target", _target }, { "utc", DateTime.UtcNow.ToString("o") } });
        }

        // ------------------------------------------------------------------ motor
        bool EngineAnswers()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _settings.Port + "/api/pages");
                request.Timeout = 2000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch (WebException) { return false; }
        }

        void Status(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) _status.Text = text; });
            else _status.Text = text;
        }

        void LogLine(string line)
        {
            if (line == null) return;
            lock (this) { try { if (_engineLog != null) { _engineLog.WriteLine(line); _engineLog.Flush(); } } catch (Exception) { } }
        }

        string EngineDiagnosis()
        {
            string tail = "";
            try
            {
                if (File.Exists(_engineLogPath))
                {
                    using (FileStream fs = new FileStream(_engineLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8)) tail = reader.ReadToEnd();
                }
            }
            catch (Exception) { }
            if (tail.Length > 3000) tail = tail.Substring(tail.Length - 3000);
            string cause;
            int module = tail.IndexOf("No module named", StringComparison.Ordinal);
            // Önce motor klasörü: yanlış kökte Python "No module named 'analyzer_v3'" der, bu paket eksiği DEĞİLDİR.
            if (!Directory.Exists(Path.Combine(_settings.Root ?? "", "analyzer_v3")))
                cause = "Motor klasörü bulunamadı (analyzer_v3 yok): " + _settings.Root
                        + ". Kurulum yönergesindeki ayar dosyasını (uvp_pdf2p8.json) yazın.";
            else if (module >= 0) cause = "Python paketi eksik: " + tail.Substring(module).Split('\n')[0].Trim()
                                     + ". Kurulum yönergesindeki bağımlılık listesini yükleyin.";
            else if (tail.IndexOf("Address already in use", StringComparison.OrdinalIgnoreCase) >= 0
                     || tail.IndexOf("10048", StringComparison.Ordinal) >= 0)
                cause = "Port " + _settings.Port + " başka bir program tarafından kullanılıyor.";
            else if (_engine == null) cause = "Python başlatılamadı: " + _settings.Python;
            else cause = "Motor yanıt vermedi. Günlüğe bakın.";
            return cause + "\n\nPython: " + _settings.Python + "\nMotor klasörü: " + _settings.Root
                   + "\nAyar dosyası: " + (_settings.Source ?? "(yok — tahmin edildi)") + "\nGünlük: " + _engineLogPath
                   + (tail == "" ? "" : "\n\nGünlüğün sonu:\n" + tail);
        }

        void ShowEngineError(string title)
        {
            string text = WebUtility.HtmlEncode(EngineDiagnosis());
            string html = "<!doctype html><meta charset='utf-8'><body style='font-family:Segoe UI,sans-serif;margin:24px'>"
                        + "<h2 style='color:#a30'>" + WebUtility.HtmlEncode(title) + "</h2>"
                        + "<p>Aktarım yapılmadı. Aşağıdaki nedeni giderip pencereyi kapatıp yeniden açın.</p>"
                        + "<pre style='white-space:pre-wrap;background:#f4f4f4;padding:12px'>" + text + "</pre></body>";
            if (_web.CoreWebView2 != null) _web.CoreWebView2.NavigateToString(html);
        }

        /// <summary>Motoru AYRI iş parçacığında başlatır: EPLAN'ın arayüzü beklemez.</summary>
        void StartEngineAsync()
        {
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                try
                {
                    if (!EngineAnswers())
                    {
                        _engineLogPath = Path.Combine(DataFolder(), "motor_" + Io.Stamp() + ".log");
                        _engineLog = new StreamWriter(_engineLogPath, false, new UTF8Encoding(false));
                        string args = "-X utf8 -m analyzer_v3.server --port " + _settings.Port;
                        if (!string.IsNullOrEmpty(_settings.Run)) args += " --run \"" + _settings.Run + "\"";
                        ProcessStartInfo info = new ProcessStartInfo(_settings.Python, args);
                        info.WorkingDirectory = _settings.Root;
                        info.UseShellExecute = false;
                        info.CreateNoWindow = true;
                        info.RedirectStandardOutput = true;
                        info.RedirectStandardError = true;
                        info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                        LogLine("başlat: " + _settings.Python + " " + args + " (klasör: " + _settings.Root + ")");
                        // Bozuk ayar dosyası sessizce tahmine düşmez: kullanıcı neyle başlatıldığını görür (öz denetim F5-15).
                        bool badSettings = _settings.Source != null && _settings.Source.IndexOf("okunamadı", StringComparison.Ordinal) >= 0;
                        Status((badSettings ? "UYARI ayar dosyası okunamadı, tahmin kullanılıyor (" + _settings.Source + ") · " : "")
                               + "Motor başlatılıyor: " + _settings.Python);
                        try
                        {
                            _engine = new Process();
                            _engine.StartInfo = info;
                            _engine.OutputDataReceived += delegate(object s, DataReceivedEventArgs a) { LogLine(a.Data); };
                            _engine.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a) { LogLine(a.Data); };
                            _engine.Start();
                            _engine.BeginOutputReadLine();
                            _engine.BeginErrorReadLine();
                        }
                        catch (Exception ex) { _engine = null; LogLine("Python başlatılamadı: " + ex.Message); }
                        for (int i = 0; i < 60 && _engine != null && !_engine.HasExited && !EngineAnswers(); i++)
                            System.Threading.Thread.Sleep(500);
                    }
                    if (EngineAnswers())
                    {
                        Status("Motor hazır (port " + _settings.Port + ", yalnız bu bilgisayar).");
                        BeginInvoke((MethodInvoker)delegate { Navigate(); });
                    }
                    else
                    {
                        Status("Motor yanıt vermedi — ayrıntı pencerede.");
                        BeginInvoke((MethodInvoker)delegate { ShowEngineError("Motor başlatılamadı"); });
                    }
                }
                catch (Exception ex)
                {
                    Status("Motor başlatılamadı: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        internal void Navigate()
        {
            if (_web.CoreWebView2 != null) _web.CoreWebView2.Navigate("http://127.0.0.1:" + _settings.Port + StartPath);
        }

        async void OnLoaded(object sender, EventArgs e)
        {
            try
            {
                // Yalnız test/prova: UVP_PDF2P8_CDP_PORT verilirse yerel hata ayıklama portu açılır ki panel
                // sayfası gerçek EPLAN içinde otomatik sürülebilsin. Normal kullanımda değişken yoktur.
                CoreWebView2EnvironmentOptions options = null;
#if UVP_TEST
                // Yalnız TEST derlemesi (build.bat ... test): kullanıcı teslim DLL'inde bu kapı derlenmez (öz denetim F5-08).
                string cdp = Environment.GetEnvironmentVariable("UVP_PDF2P8_CDP_PORT");
                int cdpPort;
                if (int.TryParse(cdp, out cdpPort) && cdpPort > 1024)
                    options = new CoreWebView2EnvironmentOptions("--remote-debugging-port=" + cdpPort);
#endif
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder(), "WebView2"), options);
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.WebMessageReceived += OnMessage;
                StartEngineAsync();                       // motor hazır olunca sayfa yüklenir
            }
            catch (Exception ex)
            {
                _status.Text = "Panel açılamadı (WebView2): " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        void Reply(object value)
        {
            if (_web.CoreWebView2 == null) return;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            _web.CoreWebView2.PostWebMessageAsJson(js.Serialize(value));
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
        }

        static bool RunAction(string name, ActionCallingContext call)
        {
            Eplan.EplApi.ApplicationFramework.Action action = new ActionManager().FindAction(name);
            if (action == null) throw new InvalidOperationException(name + " eylemi bulunamadı; eklenti yüklü mü?");
            return action.Execute(call);
        }

        /// <summary>Şeritte bu eklentinin komutu gerçekten var mı? (ölçüm; görsel doğrulamanın yerine geçmez)</summary>
        static List<object> RibbonCommands()
        {
            List<object> found = new List<object>();
            try
            {
                foreach (Eplan.EplApi.Gui.RibbonTab tab in new Eplan.EplApi.Gui.RibbonBar().Tabs)
                    foreach (Eplan.EplApi.Gui.RibbonCommandGroup group in tab.CommandGroups)
                        foreach (KeyValuePair<uint, Eplan.EplApi.Gui.RibbonCommand> command in group.Commands)
                        {
                            string text = "";
                            try { text = command.Value.Text; } catch (Exception) { }
                            if (text.IndexOf("UVP", StringComparison.OrdinalIgnoreCase) >= 0)
                                found.Add(tab.Name + " / " + group.Name + " / " + text);
                        }
            }
            catch (Exception ex) { found.Add("okunamadı: " + ex.GetType().Name + ": " + ex.Message); }
            return found;
        }

        Dictionary<string, object> TargetInfo()
        {
            Dictionary<string, object> info = new Dictionary<string, object>();
            info["target"] = _target;
            info["exists"] = _target != "" && File.Exists(_target);
            return info;
        }

        /// <summary>Sayfadan gelen mesaj. YALNIZ bilinen işler.</summary>
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> message;
            try { message = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch (Exception ex) { Reply(new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } }); return; }
            string action = message != null && message.ContainsKey("action") ? Convert.ToString(message["action"]) : "";
            Dictionary<string, object> answer = new Dictionary<string, object>();
            answer["action"] = action;
            try
            {
                if (_busy && action != "ping")
                    throw new InvalidOperationException("Aktarım sürüyor; bitmeden başka EPLAN işlemi yapılmaz.");
                if (action == "ping")
                {
                    answer["ok"] = true;
                    answer["host"] = "EPLAN";
                    answer["eplan_version"] = Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion;
                    answer["dll"] = typeof(PanelForm).Assembly.Location;
                    answer["ribbon"] = RibbonCommands();
                    foreach (KeyValuePair<string, object> kv in TargetInfo()) answer[kv.Key] = kv.Value;
                    // Kullanıcı 08.10: aktarılan proje açıkken panel o projenin PDF'ini kendiliğinden açar (eşleme sayfada).
                    List<object> open = new List<object>();
                    try
                    {
                        Project current = new Eplan.EplApi.HEServices.SelectionSet().GetCurrentProject(false);
                        if (current != null) open.Add(current.ProjectLinkFilePath);
                        foreach (Project p in new ProjectManager().OpenProjects)
                            if (!open.Contains(p.ProjectLinkFilePath)) open.Add(p.ProjectLinkFilePath);
                    }
                    catch (Exception) { }
                    answer["open_projects"] = open;
                }
                else if (action == "pickPdf")
                {
                    // Dosya seçme penceresi EPLAN'ın içinde açılır; sayfa dosya sistemine erişmez.
                    using (OpenFileDialog dialog = new OpenFileDialog())
                    {
                        dialog.Title = "Şema PDF'i seç";
                        dialog.Filter = "PDF (*.pdf)|*.pdf";
                        dialog.CheckFileExists = true;
                        bool chosen = dialog.ShowDialog(this) == DialogResult.OK;
                        answer["ok"] = chosen;
                        answer["path"] = chosen ? dialog.FileName : "";
                        // Aynı PDF'in var olan çalışması (işaretleriyle) önerilebilsin diye kimlik.
                        if (chosen) answer["sha256"] = ImportAction.Sha256(dialog.FileName);
                        if (chosen) _status.Text = "PDF: " + dialog.FileName;
                    }
                }
                else if (action == "target")
                {
                    // Hedef test projesi: sor / yeni başlat (bir sonraki aktarım şablondan yeni proje açar).
                    if (message.ContainsKey("reset") && Convert.ToBoolean(message["reset"])) SaveTarget("");
                    // PDF'in kayıtlı hedefini geri yükle: yalnız output\p8runs altındaki var olan UVP_ test projesi.
                    // Geçersizse hedef boş kalır (eski PDF'in hedefi sessizce kullanılmaz).
                    if (message.ContainsKey("set")) { SaveTarget(""); SaveTarget(Guard.TestProject(Convert.ToString(message["set"]))); }
                    answer["ok"] = true;
                    foreach (KeyValuePair<string, object> kv in TargetInfo()) answer[kv.Key] = kv.Value;
                }
                else if (action == "checkTarget")
                {
                    // Yalnız okur: hedefin defterinde bu belgenin (document) bu sayfaları (pages) zaten var mı? Kural eylemdekiyle
                    // aynı (belge bilinmiyorsa aynı sayılır; REPLACED kayıt sayılmaz); yazma kararı yine eylemde verilir.
                    string document = message.ContainsKey("document") ? Convert.ToString(message["document"]) : "";
                    List<int> want = new List<int>();
                    foreach (object p in ImportAction.A(message.ContainsKey("pages") ? message["pages"] : null)) want.Add(Convert.ToInt32(p));
                    List<object> hits = new List<object>();
                    if (_target != "" && File.Exists(_target))
                        foreach (object entry in ImportAction.LedgerEntries(Path.ChangeExtension(_target, ".uvp_ledger.json")))
                        {
                            Dictionary<string, object> past = ImportAction.D(entry);
                            string st = ImportAction.S(past, "state"), doc = ImportAction.S(past, "document_sha256");
                            if (st == "REPLACED" || !(doc == "" || document == "" || string.Equals(doc, document, StringComparison.OrdinalIgnoreCase))) continue;
                            List<object> same = new List<object>();
                            foreach (object p in ImportAction.A(past.ContainsKey("pages") ? past["pages"] : null))
                                if (want.Contains(Convert.ToInt32(p))) same.Add(Convert.ToInt32(p));
                            if (same.Count > 0 || st == "FAILED_RECOVERY_REQUIRED" || st == "FAILED_PARTIAL_WRITE")
                                hits.Add(new Dictionary<string, object> { { "pages", same }, { "state", st }, { "utc", ImportAction.S(past, "utc") } });
                        }
                    answer["ok"] = true;
                    answer["conflicts"] = hits;
                    answer["target"] = _target;
                }
                else if (action == "openTarget")
                {
                    answer["ok"] = OpenInEplan(_target, message.ContainsKey("page") ? Convert.ToString(message["page"]) : "");
                    if (_openError != "") answer["error"] = _openError;
                    answer["target"] = _target;
                }
                else if (action == "catalog")
                {
                    // Sembol kataloğu: şablondan YENİ test projesi açar, kataloğu yazar, kapatır.
                    string outDir = Path.Combine(_settings.Root, "output", "p8test", "probe_" + Io.Stamp());
                    ActionCallingContext call = new ActionCallingContext();
                    call.AddParameter("OUT", outDir);
                    call.AddParameter("TEMPLATE", _settings.Template);
                    _status.Text = "Katalog alınıyor…";
                    bool done = RunAction("UvpPdfToP8Probe", call);
                    answer["ok"] = done;
                    answer["catalog"] = Path.Combine(outDir, "capabilities.json");
                    _status.Text = done ? "Katalog yazıldı: " + outDir : "Katalog alınamadı.";
                }
                else if (action == "gezgin")
                {
                    // Kontrol Gezgini (A1, 12.10; static/gezgin.js): sayfayı aç / işaret "bitti" → UvpPdfToP8Gezgin eylemi.
                    // Yazma onayı (UVP_ test projesi dışında) ve tek UndoStep eylemin içinde. Panelden açma: LockingStep (S063110).
                    string outFile = Path.Combine(DataFolder(), "gezgin_" + Io.Stamp() + ".json");
                    ActionCallingContext call = new ActionCallingContext();
                    foreach (string key in new string[] { "project", "page", "done", "device", "label" })
                        if (message.ContainsKey(key) && message[key] != null) call.AddParameter(key.ToUpperInvariant(), Convert.ToString(message[key]));
                    call.AddParameter("OUT", outFile);
                    using (Eplan.EplApi.DataModel.LockingStep step = new Eplan.EplApi.DataModel.LockingStep())
                        RunAction("UvpPdfToP8Gezgin", call);
                    Dictionary<string, object> result = ReadJson(outFile);
                    if (result == null) throw new InvalidOperationException("UvpPdfToP8Gezgin sonuç yazmadı.");
                    foreach (KeyValuePair<string, object> kv in result) answer[kv.Key] = kv.Value;
                    answer["action"] = "gezgin";
                    try { File.Delete(outFile); } catch (Exception) { }
                }
                else if (action == "import")
                {
                    if (_busy) throw new InvalidOperationException("Bir aktarım zaten sürüyor; bitmesini bekleyin.");
                    string package = Convert.ToString(message["package"]);
                    string mapping = message.ContainsKey("mapping") ? Convert.ToString(message["mapping"]) : "";
                    string page = message.ContainsKey("page") ? Convert.ToString(message["page"]) : "";
                    // Onay kapısı (MAIN 03.10): incelemede "gördüm, aktar" kutusu işaretliyse sayfa ack=true gönderir.
                    bool ack = message.ContainsKey("ack") && message["ack"] is bool && (bool)message["ack"];
                    // Liste aktarımında ara sayfa (final=false): sayfa görüntüsü ve projeyi EPLAN'da açma yalnız sonda bir kez.
                    bool final = !(message.ContainsKey("final") && message["final"] is bool && !(bool)message["final"]);
                    // 14.10 'Üstüne yaz': kullanıcı uyarı penceresinde seçtiyse eylem /REPLACE:1 alır (önceki sayfalar kaldırılır).
                    bool replace = message.ContainsKey("replace") && message["replace"] is bool && (bool)message["replace"];
                    if (!File.Exists(package)) throw new FileNotFoundException("Paket bulunamadı: " + package);
                    if (mapping == "" || !File.Exists(mapping))
                        throw new InvalidOperationException("Sembol eşlemesi yok: önce katalog alınıp eşleme yazılmalı.");
                    if (_target == "" && !File.Exists(_settings.Template))
                        throw new InvalidOperationException("EPLAN proje şablonu bulunamadı: " + _settings.Template);
                    // Her aktarım YENİ koşu klasörüne: önceki makbuz/geri okuma ezilmez (eylem de reddeder).
                    string outDir = Path.Combine(_settings.Root, "output", "p8runs",
                                                 Io.Stamp() + "_panel_sayfa" + (page == "" ? "" : page));
                    _busy = true;
                    answer["ok"] = true;
                    answer["phase"] = "started";
                    answer["out"] = outDir;
                    answer["target"] = _target;
                    _status.Text = "Aktarım başladı: sayfa " + page + (_target == "" ? " → yeni test projesi" : " → " + _target);
                    // Yanıt önce gider: sayfa "sürüyor" gösterir. Ağır iş sonra aynı (EPLAN) iş parçacığında.
                    BeginInvoke((MethodInvoker)delegate { RunImport(package, mapping, page, outDir, ack, final, replace); });
                }
                else
                {
                    answer["ok"] = false;
                    answer["error"] = "Bilinmeyen istek: " + action;
                }
            }
            catch (Exception ex)
            {
                answer["ok"] = false;
                answer["error"] = ex.GetType().Name + ": " + ex.Message;
                answer["busy"] = _busy;          // sürmekte olan aktarım varsa sayfa kilidini AÇMAZ
                _status.Text = "Hata: " + ex.Message;
            }
            Reply(answer);
        }

        void Progress(string text)
        {
            _status.Text = text;
            Reply(new Dictionary<string, object> { { "action", "import" }, { "phase", "progress" }, { "text", text } });
            Application.DoEvents();        // ilerleme satırı görünsün (işlem aynı iş parçacığında)
        }

        /// <summary>
        /// Hedef test projesi EPLAN'da açıksa kapatır. ÖLÇÜM 08.10 (=280 canlı, 17 sayfa): proje EPLAN'da açık (sayfa
        /// düzenleyicide) iken üstüne yazılan sayfada simgeden konan nesnelerin 115'i "LockingExceptionFailedLockAttempt"
        /// ile reddedildi; projeyi her öğeden sonra kapatan toplu aktarımda (82 sayfa) red 0. Eylem kapalı projeyi
        /// kendisi açıp kapatır. Yalnız output\p8runs altındaki UVP_ test projesi kapatılır.
        /// </summary>
        void CloseIfOpen(string project)
        {
            if (project == "") return;
            Guard.TestProject(project);
            using (Eplan.EplApi.DataModel.LockingStep step = new Eplan.EplApi.DataModel.LockingStep())
                foreach (Project p in new ProjectManager().OpenProjects)
                    if (string.Equals(p.ProjectLinkFilePath, project, StringComparison.OrdinalIgnoreCase)) { p.Close(); break; }
        }

        void RunImport(string package, string mapping, string page, string outDir, bool ack, bool final, bool replace)
        {
            Dictionary<string, object> answer = new Dictionary<string, object>();
            answer["action"] = "import";
            answer["phase"] = "done";
            answer["out"] = outDir;
            try
            {
                if (_target != "" && File.Exists(_target))
                {
                    Progress("Hedef proje EPLAN'da kapatılıyor (açık projeye yazılınca EPLAN nesne kilidi veriyor)…");
                    CloseIfOpen(_target);
                }
                Progress("1/4 Aktarılıyor (" + (_target == "" ? "yeni test projesi" : Path.GetFileName(_target)) + ")…");
                ActionCallingContext call = new ActionCallingContext();
                call.AddParameter("PACKAGE", package);
                call.AddParameter("MAPPING", mapping);
                call.AddParameter("OUT", outDir);
                call.AddParameter("TEMPLATE", _settings.Template);
                if (_target != "") call.AddParameter("TARGET", _target);
                // Kullanıcı kararı: "bulabildiği kadarını aktarsın". KISMİ aktarım (red/güzergâh sorunu) projede
                // kalır, eksikler makbuzda; yalnız istisnada bu işlemin nesneleri geri alınır.
                call.AddParameter("ON_FAILURE", "KEEP_PARTIAL");
                // Kırmızı bayraklı (kısa devre/yanlış bağ riskli) sayfa yalnız kullanıcı onayıyla yazılır; yoksa eylem reddeder.
                if (ack) call.AddParameter("ACK_RISK", "1");
                if (replace && _target != "") call.AddParameter("REPLACE", "1");
                RunAction("UvpPdfToP8Import", call);
                Dictionary<string, object> receipt = ReadJson(Path.Combine(outDir, "receipt.json"));
                if (receipt == null) throw new InvalidOperationException("Aktarım makbuz yazmadı: " + outDir);
                bool ok = receipt.ContainsKey("import_ok") && receipt["import_ok"] is bool && (bool)receipt["import_ok"];
                answer["state"] = receipt.ContainsKey("state") ? receipt["state"] : null;
                answer["import_ok"] = ok;
                if (receipt.ContainsKey("error")) answer["error"] = receipt["error"];
                string project = receipt.ContainsKey("target_project") ? Convert.ToString(receipt["target_project"]) : "";
                bool partial = Convert.ToString(answer["state"]) == "PARTIAL";
                if ((ok || partial) && project != "")
                {
                    SaveTarget(project);
                    Progress("2/4 EPLAN bağlantıları tazeleniyor ve proje geri okunuyor…");
                    ActionCallingContext read = new ActionCallingContext();
                    read.AddParameter("PROJECT", project);
                    read.AddParameter("OUT", Path.Combine(outDir, "guncel"));
                    read.AddParameter("UPDATE", "1");
                    // Aktarım YAZILDI; sonraki aşamaların hatası onu "başarısız" göstermez ama saklanmaz da (F5-05).
                    answer["readback_ok"] = Stage(delegate { RunAction("UvpPdfToP8Readback", read); },
                                                  Path.Combine(outDir, "guncel", "readback_current.json"), answer, "readback_error");
                    answer["snapshot_ok"] = true;
                    if (final)
                    {
                        Progress("3/4 EPLAN sayfa görüntüsü ve yazı kutuları alınıyor…");
                        ActionCallingContext snap = new ActionCallingContext();
                        snap.AddParameter("PROJECT", project);
                        snap.AddParameter("OUT", Path.Combine(outDir, "gorsel"));
                        answer["snapshot_ok"] = Stage(delegate { RunAction("UvpPdfToP8Snapshot", snap); },
                                                      Path.Combine(outDir, "gorsel", "snapshot.json"), answer, "snapshot_error");
                        Progress("4/4 Proje EPLAN'da açılıyor…");
                        string pageName = "";
                        if (receipt.ContainsKey("page_map"))
                        {
                            Dictionary<string, object> map = receipt["page_map"] as Dictionary<string, object>;
                            if (map != null && map.ContainsKey(page)) pageName = Convert.ToString(map[page]);
                        }
                        answer["opened"] = OpenInEplan(project, pageName);
                        answer["opened_page"] = pageName;
                        if (_openError != "") answer["open_error"] = _openError;
                    }
                }
                answer["ok"] = ok;
                answer["target"] = _target;
                if (receipt.ContainsKey("rollback_remaining_objects")) answer["rollback_remaining_objects"] = receipt["rollback_remaining_objects"];
                string state = Convert.ToString(answer["state"]);
                // Kırmızı uyarılı sayfa (MAIN 2026-09-30): yazıldı ama tam aktarılmış sayılmaz.
                string flags = string.Join(", ", ImportAction.A(receipt.ContainsKey("page_flags") ? receipt["page_flags"] : null).ToArray());
                bool stagesOk = !(ok || partial) || ((bool)answer["readback_ok"] && (bool)answer["snapshot_ok"]);
                _status.Text = state == "FAILED_RECOVERY_REQUIRED" ? "Kısmi yazma GERİ ALINAMADI: projede " + answer["rollback_remaining_objects"] + " nesne kaldı; elle temizleyin."
                             : state == "FAILED_PARTIAL_WRITE" ? "Kısmi yazma: nesnelerin bir kısmı projede kaldı; sonuç penceresine bakın."
                             : partial ? "Kısmi aktarım: bulunan kısım EPLAN'a yazıldı; aktarılamayanlar sonuç penceresinde."
                             : state == "REFUSED_RISK_NOT_ACKNOWLEDGED" ? "Aktarım yapılmadı: sayfa kırmızı uyarılı (" + flags + "); incelemede 'gördüm, aktar' onayı gerekir."
                             : !ok ? "Aktarım yapılmadı: " + state
                             : state == "COMPLETED_WITH_RISK" ? "Aktarım yazıldı ama sayfa KIRMIZI UYARILI (" + flags + "): tam aktarılmış sayılmaz; eksikler sonuç penceresinde."
                             : !stagesOk ? "Aktarım yazıldı; doğrulama için geri okuma/görüntü alınamadı (sonuç penceresinde)."
                             : (_openError != "" ? "Aktarım tamamlandı; proje EPLAN'da açılamadı: " + _openError
                                                 : "Aktarım tamamlandı; sayfa EPLAN'da açıldı, doğrulama sonucu pencerede.");
            }
            catch (Exception ex)
            {
                answer["ok"] = false;
                answer["error"] = ex.GetType().Name + ": " + ex.Message;
                _status.Text = "Aktarım hatası: " + ex.Message;
            }
            finally
            {
                _busy = false;
                Reply(answer);
            }
        }

        string _openError = "";

        delegate void Work();

        /// <summary>Aktarım sonrası aşama: istisna VEYA sonuç dosyasında ok=false başarısızlıktır.</summary>
        static bool Stage(Work work, string resultFile, Dictionary<string, object> answer, string errorKey)
        {
            try
            {
                work();
                Dictionary<string, object> result = ReadJson(resultFile);
                if (result == null) { answer[errorKey] = "Sonuç dosyası yok: " + resultFile; return false; }
                if (result.ContainsKey("ok") && result["ok"] is bool && !(bool)result["ok"])
                {
                    answer[errorKey] = result.ContainsKey("error") ? Convert.ToString(result["error"]) : "ok=false";
                    return false;
                }
                return true;
            }
            catch (Exception ex) { answer[errorKey] = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        bool OpenInEplan(string project, string pageName)
        {
            _openError = "";
            if (project == "" || !File.Exists(project)) { _openError = "Proje dosyası yok: " + project; return false; }
            try
            {
                // ÖLÇÜM (pilot_v01/prova R1): panel penceresinden (eylem bağlamı dışında) açarken EPLAN
                // "S063110 'LockingStep' sınıfında hiçbir nesne oluşturulmadı" veriyordu.
                using (Eplan.EplApi.DataModel.LockingStep step = new Eplan.EplApi.DataModel.LockingStep())
                {
                    ProjectManager pm = new ProjectManager();
                    bool open = false;
                    foreach (Project p in pm.OpenProjects)
                        if (string.Equals(p.ProjectLinkFilePath, project, StringComparison.OrdinalIgnoreCase)) open = true;
                    if (!open) pm.OpenProject(project);
                    if (pageName != "") new Eplan.EplApi.HEServices.Edit().OpenPageWithName(project, pageName);
                }
                return true;
            }
            catch (Exception ex)
            {
                _openError = ex.GetType().Name + ": " + ex.Message;
                _status.Text = "Proje açılamadı: " + ex.Message;
                return false;
            }
        }

        void OnClosed(object sender, FormClosedEventArgs e)
        {
            // Kullanıcının kendi başlattığı motora dokunulmaz; yalnız bizimki kapatılır.
            if (_engine != null)
            {
                try { if (!_engine.HasExited) _engine.Kill(); } catch (Exception) { }
            }
            lock (this) { try { if (_engineLog != null) _engineLog.Dispose(); } catch (Exception) { } _engineLog = null; }
            _web.Dispose();
        }
    }
}
