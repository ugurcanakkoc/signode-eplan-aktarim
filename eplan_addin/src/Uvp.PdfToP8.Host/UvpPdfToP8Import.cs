// =====================================================================================
// UvpPdfToP8Import — seçili kaynak paketini AYRI test projesine yazar ve EPLAN'dan geri okur.
//
//   UvpPdfToP8Import /PACKAGE:"<package.json>" /MAPPING:"<mapping.json>" /OUT:"<yeni klasör>"
//                    /TEMPLATE:"<.zw9>" [/TARGET:"<output\p8runs altındaki UVP_*.elk>"]
//                    [/PAGES:4,5] [/MACRO:1] [/ON_FAILURE:ROLLBACK|KEEP_PARTIAL] [/REPLACE:1]
//   ROLLBACK: kısmi (red/güzergâh) aktarım da, istisna da geri alınır.
//   KEEP_PARTIAL: bulunabilen kısım projede kalır (PARTIAL, eksikler makbuzda); istisna yine geri alınır.
//
// Güvenlik ve doğruluk kuralları (2026-09-14 paketi):
//  * Açık kullanıcı projesi hedef olamaz (/TARGET:OPEN reddedilir). TARGET verilmezse şablondan YENİ
//    test projesi oluşturulur. TARGET yalnız output\p8runs altındaki, adı UVP_ ile başlayan projedir.
//  * OUT klasöründe receipt.json varsa hiçbir şey yazılmaz: önceki kayıt ezilmez.
//  * Projenin defterinde (.uvp_ledger.json) aynı paket ya da aynı belgenin aynı fiziksel sayfası varsa aktarım
//    açık Türkçe metinle reddedilir; /REPLACE:1 ('Üstüne yaz', 14.10) ile o kayıtların sayfaları önce .zw1 yedek
//    alınıp kaldırılır, kayıtlar REPLACED olur, sonra normal aktarım. Hedefte aynı tam adlı yabancı fonksiyon
//    varsa yazılmaz. Sessiz mükerrer cihaz/sayfa yok.
//  * Yön, sembol varyantı veya pin eşleşmesi belirsizse nesne REDDEDİLİR: varyant 0'a düşülmez, ilk
//    uygun ada körlemesine gidilmez; eşleşmeyen pin nesneyi başarılı saymaz.
//  * T düğümü çizilmiş KOLLARIN yön kümesiyle, köşe iki kolun yönüyle katalogdan seçilir. Pin üstüne T
//    konmaz (paket zaten göndermez). DynamicConnectionLine kullanılmaz; bağlantı sayısının artması
//    başarı sayılmaz.
//  * Makbuz: kaynak düğüm → EPLAN nesne kimliği (ToStringIdentifier; yalnız bu oturumda geçerli,
//    geri okuma aynı oturumda yapılır). Geri okuma 2.0: fonksiyon tanımı (kategori/grup/kimlik),
//    sembol bağlantı noktaları, GraphicalConnections, Connection uçları + SymbolReferences +
//    SubConnections, kesinti noktası çapraz referansı.
//  * Doğrulama kararı burada VERİLMEZ: python -m analyzer_v3.eplan_verify.
// =====================================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.EObjects;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;

namespace Uvp.PdfToP8.Host
{
    internal class RefusedException : Exception
    {
        public readonly string State;
        public RefusedException(string state, string message) : base(message) { State = state; }
    }

    public partial class ImportAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8Import";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        // ------------------------------------------------------------------ küçük yardımcılar
        internal static Dictionary<string, object> D(object o) { return (Dictionary<string, object>)o; }

        internal static ArrayList A(object o)
        {
            if (o == null) return new ArrayList();
            ArrayList list = o as ArrayList;
            if (list != null) return list;
            object[] array = o as object[];
            if (array != null) return new ArrayList(array);
            throw new InvalidOperationException("Dizi bekleniyordu: " + o.GetType().Name);
        }

        internal static string S(Dictionary<string, object> d, string k) { return d != null && d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : ""; }
        internal static bool IsFalse(Dictionary<string, object> d, string k) { return d.ContainsKey(k) && d[k] is bool && !(bool)d[k]; }
        internal static double N(object o) { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); }
        internal static PointD P(object o) { ArrayList a = A(o); return new PointD(N(a[0]), N(a[1])); }
        internal static double[] XY(PointD p) { return new double[] { Math.Round(p.X, 3), Math.Round(p.Y, 3) }; }

        internal static string Id(StorableObject o)
        {
            try { return o == null ? null : o.ToStringIdentifier(); }
            catch (Exception) { return null; }
        }

        internal static string Text(Func<string> read)
        {
            try { string value = read(); return value ?? ""; }
            catch (Exception) { return ""; }
        }

        internal static string Sha256(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>mm, y yukarı: iki nokta arasındaki yön adı (EPLAN PinBase.Directions ile aynı adlar).</summary>
        internal static string Way(double dx, double dy)
        {
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx > 0 ? "Right" : "Left";
            return dy > 0 ? "Up" : "Down";
        }

        internal static string DirectionSet(PinBase[] points)
        {
            List<string> names = new List<string>();
            if (points != null) foreach (PinBase p in points) names.Add(p.Direction.ToString());
            names.Sort(StringComparer.Ordinal);
            return string.Join(",", names.ToArray());
        }

        internal static PointD Absolute(Placement parent, PinBase pin)
        {
            if (parent == null || pin == null) throw new InvalidOperationException("Bağlantı noktası sahibi/konumu okunamadı.");
            return new PointD(parent.Location.X + pin.Location.X, parent.Location.Y + pin.Location.Y);
        }

        /// <summary>Seçilen bağlantı noktası hedefe düşecek ekleme noktası.</summary>
        internal static PointD InsertionFor(SymbolVariant sv, int index, PointD target)
        {
            PinBase[] cps = sv.ConnectionPoints;
            if (cps == null || index < 0 || index >= cps.Length)
                throw new InvalidOperationException("Sembol bağlantı noktası indeksi geçersiz: " + index);
            return new PointD(target.X - cps[index].Location.X, target.Y - cps[index].Location.Y);
        }

        internal static Symbol SymbolOf(Project prj, string library, string name)
        {
            return new Symbol(new SymbolLibrary(prj, library), name);
        }

        /// <summary>Bağlantı noktası yön KÜMESİ istenen kümeye eşit ilk varyant; yoksa null (varyant 0'a düşülmez).</summary>
        internal static SymbolVariant VariantWithDirections(Project prj, string library, string name, string wanted, out string note)
        {
            note = "";
            try
            {
                Symbol symbol = SymbolOf(prj, library, name);
                for (int nr = 0; nr < symbol.Variants.Length; nr++)
                {
                    SymbolVariant candidate = symbol[nr];
                    if (candidate != null && DirectionSet(candidate.ConnectionPoints) == wanted)
                    {
                        note = library + "/" + name + " #" + nr + " [" + wanted + "]";
                        return candidate;
                    }
                }
                note = library + "/" + name + ": [" + wanted + "] yön kümeli varyant yok";
            }
            catch (Exception ex) { note = "sembol okunamadı (" + library + "/" + name + "): " + ex.Message; }
            return null;
        }

        /// <summary>Herhangi bir bağlantı noktası istenen yöne bakan ilk varyant ve o noktanın indeksi; yoksa null.</summary>
        internal static SymbolVariant VariantFacing(Project prj, string library, string name, string direction,
                                                    int onlyIndex, out int index, out string note)
        {
            index = -1;
            note = "";
            if (string.IsNullOrEmpty(direction)) { note = "yön bilinmiyor; varyant seçilmedi"; return null; }
            try
            {
                Symbol symbol = SymbolOf(prj, library, name);
                for (int nr = 0; nr < symbol.Variants.Length; nr++)
                {
                    SymbolVariant candidate = symbol[nr];
                    if (candidate == null || candidate.ConnectionPoints == null) continue;
                    PinBase[] points = candidate.ConnectionPoints;
                    for (int i = 0; i < points.Length; i++)
                        if ((onlyIndex < 0 || onlyIndex == i) && points[i].Direction.ToString() == direction)
                        {
                            index = i;
                            note = library + "/" + name + " #" + nr + " nokta " + i + " (" + direction + ")";
                            return candidate;
                        }
                }
                note = library + "/" + name + ": '" + direction + "' yönlü bağlantı noktası olan varyant yok";
            }
            catch (Exception ex) { note = "sembol okunamadı (" + library + "/" + name + "): " + ex.Message; }
            return null;
        }

        /// <summary>Projenin yanındaki aktarım defterindeki kayıtlar. Eski tek kayıtlı biçim de okunur.</summary>
        internal static List<object> LedgerEntries(string path)
        {
            List<object> out_ = new List<object>();
            if (!File.Exists(path)) return out_;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> data = js.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (data == null) return out_;
                if (data.ContainsKey("imports")) out_.AddRange(A(data["imports"]).ToArray());
                else if (data.ContainsKey("package_sha256")) out_.Add(data);   // 1.0 biçimi: tek aktarım
            }
            catch (Exception ex)
            {
                // Defter okunamıyorsa neyin aktarıldığı bilinmiyor: yazmak yerine reddedilir.
                throw new RefusedException("REFUSED_LEDGER_UNREADABLE", "Aktarım defteri okunamadı (" + path + "): " + ex.Message);
            }
            return out_;
        }

        /// <summary>Paketteki yerleşim kararı (Python önizlemesiyle aynı); yoksa null.</summary>
        internal static Dictionary<string, object> Planned(Dictionary<string, object> o)
        {
            if (o == null || !o.ContainsKey("placement") || o["placement"] == null) return null;
            return o["placement"] as Dictionary<string, object>;
        }

        /// <summary>Paketin verdiği sembol/varyant/bağlantı noktası kararını HOSTTA DOĞRULAR.
        /// Varyant yoksa, nokta yoksa veya noktanın yönü karardan farklıysa null döner: başka
        /// sembol/varyant seçilmez, nesne çağıran tarafından reddedilir.</summary>
        internal static SymbolVariant VariantFromPackage(Project prj, Dictionary<string, object> placement,
                                                         out int index, out string note)
        {
            index = -1;
            note = "";
            string library = S(placement, "library"), name = S(placement, "symbol"), want = S(placement, "direction");
            int nr = placement.ContainsKey("variant") ? Convert.ToInt32(placement["variant"]) : 0;
            int wanted = placement.ContainsKey("connection_point_index") ? Convert.ToInt32(placement["connection_point_index"]) : 0;
            try
            {
                Symbol symbol = SymbolOf(prj, library, name);
                if (nr < 0 || nr >= symbol.Variants.Length)
                {
                    note = library + "/" + name + ": paket varyant " + nr + ", hostta " + symbol.Variants.Length + " varyant";
                    return null;
                }
                SymbolVariant variant = symbol[nr];
                PinBase[] points = variant == null ? null : variant.ConnectionPoints;
                if (points == null || wanted < 0 || wanted >= points.Length)
                {
                    note = library + "/" + name + " #" + nr + ": paket " + wanted + ". bağlantı noktasını istedi, hostta "
                           + (points == null ? 0 : points.Length) + " nokta var";
                    return null;
                }
                string actual = points[wanted].Direction.ToString();
                if (want != "" && actual != want)
                {
                    note = library + "/" + name + " #" + nr + " nokta " + wanted + ": paket '" + want + "' dedi, hostta '" + actual + "'";
                    return null;
                }
                index = wanted;
                note = library + "/" + name + " #" + nr + " nokta " + wanted + " (" + actual + ") [paket kararı doğrulandı]";
                return variant;
            }
            catch (Exception ex) { note = "sembol okunamadı (" + library + "/" + name + "): " + ex.Message; }
            return null;
        }

        static SymbolVariant MappedVariant(Project prj, Dictionary<string, object> entry)
        {
            int nr = entry.ContainsKey("variant") ? Convert.ToInt32(entry["variant"]) : 0;
            SymbolVariant sv = SymbolOf(prj, S(entry, "library"), S(entry, "symbol"))[nr];
            if (sv == null) throw new InvalidOperationException("Sembol varyantı yok: " + S(entry, "library") + "/" + S(entry, "symbol") + " #" + nr);
            return sv;
        }

        static bool SameDirection(double dx, double dy, double ox, double oy)
        {
            const double eps = 0.0005;
            return (Math.Abs(dx) < eps) == (Math.Abs(ox) < eps) && (Math.Abs(dy) < eps) == (Math.Abs(oy) < eps)
                   && dx * ox >= 0 && dy * oy >= 0;
        }

        // ------------------------------------------------------------------ parça veritabanı (salt okunur)
        static MDPart FindPart(MDPartsDatabase db, string typeNumber, List<string> log)
        {
            if (db == null || string.IsNullOrEmpty(typeNumber)) return null;
            try
            {
                MDPart direct = db.GetPart(typeNumber);
                if (direct != null) return direct;
            }
            catch (Exception) { }
            try
            {
                MDPartsDatabaseItemPropertyList filter = new MDPartsDatabaseItemPropertyList();
                filter.ARTICLE_TYPENR = typeNumber;
                MDPart[] hits = db.GetParts(filter);
                if (hits != null && hits.Length == 1) return hits[0];
                if (hits != null && hits.Length > 1) log.Add("Belirsiz parça: " + typeNumber + " (" + hits.Length + " sonuç); seçilmedi.");
            }
            catch (Exception ex) { log.Add("parça arama (" + typeNumber + "): " + ex.Message); }
            return null;
        }

        /// <summary>PLC ucu giriş mi çıkış mı: fonksiyon tanımının adından. İkisi de/hiçbiri geçiyorsa boş.</summary>
        static string IoKind(Function fn)
        {
            string name;
            try { name = fn.FunctionDefinition.Name.GetAsString().ToLowerInvariant(); }
            catch (Exception) { return ""; }
            bool input = name.Contains("input") || name.Contains("eingang") || name.Contains("giriş");
            bool output = name.Contains("output") || name.Contains("ausgang") || name.Contains("çıkış");
            return input == output ? "" : input ? "input" : "output";
        }

        internal static Dictionary<string, object> Definition(Function fn)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            try
            {
                FunctionDefinition d = fn.FunctionDefinition;
                if (d == null) return r;
                r["category"] = d.FunctionCategory.ToString();
                r["group"] = (int)d.Group;
                r["definition_id"] = (int)d.Id;
                r["definition_name"] = Text(delegate { return d.Name.GetAsString(); });
            }
            catch (Exception ex) { r["error"] = ex.Message; }
            return r;
        }

        /// <summary>Parça fonksiyonunun TEK uç adı: bütün adlı uçları aynı addaysa o ad, değilse "". Ölçüm 02.10
        /// (CreateDevice): SITOP 6EP3436 fonksiyonu ['+1',''], 6EP1961 ['+24V (1)','+24V (1)'] — iki taraflı tek uç;
        /// öbür taraf aynı fonksiyonla gelir. Tek uçlu fonksiyonda eski davranışla aynı (Pins[0].Name).</summary>
        internal static string SinglePinName(Function fn)
        {
            string name = "";
            foreach (Pin p in fn.Pins)
            {
                if (p.Name == "") continue;
                if (name == "") name = p.Name;
                else if (p.Name != name) return "";
            }
            return name;
        }

        /// <summary>Fonksiyonun adı `name` olan ilk ucu (yoksa ilk uç).</summary>
        static Pin NamedPin(Function fn, string name)
        {
            foreach (Pin p in fn.Pins) if (p.Name == name) return p;
            return fn.Pins[0];
        }

        /// <summary>Adaylar aynı uç adını ve aynı fonksiyon tanımını taşıyor mu (birbirinin yerine geçer mi).</summary>
        static bool Interchangeable(List<Function> fns)
        {
            string first = null;
            foreach (Function fn in fns)
            {
                Dictionary<string, object> d = Definition(fn);
                if (!d.ContainsKey("category")) return false;
                string key = SinglePinName(fn) + "|" + d["category"] + "|" + d["group"] + "|" + d["definition_id"];
                if (first == null) first = key;
                else if (key != first) return false;
            }
            return true;
        }

        static string RoleProblem(Function fn, Dictionary<string, object> entry, string io)
        {
            Dictionary<string, object> role = null;
            if (io != "" && entry.ContainsKey("expected_function_by_io") && D(entry["expected_function_by_io"]).ContainsKey(io))
                role = D(D(entry["expected_function_by_io"])[io]);
            else if (entry.ContainsKey("expected_function"))
                role = D(entry["expected_function"]);
            if (role == null) return null;
            Dictionary<string, object> read = Definition(fn);
            if (!read.ContainsKey("category")) return "fonksiyon tanımı okunamadı";
            if (S(role, "category") != S(read, "category")) return "kategori " + S(read, "category") + " ≠ " + S(role, "category");
            ArrayList allowed = A(role.ContainsKey("definitions") ? role["definitions"] : null);
            if (allowed.Count == 0) return null;
            foreach (object pair in allowed)
            {
                ArrayList gi = A(pair);
                if (Convert.ToInt32(gi[0]) == (int)read["group"] && Convert.ToInt32(gi[1]) == (int)read["definition_id"]) return null;
            }
            return "tanım " + read["group"] + "/" + read["definition_id"] + " izinli listede değil";
        }

        // ------------------------------------------------------------------ yerleştirme bağlamı
        class Context
        {
            public Project Project;
            public Page Page;
            public Dictionary<string, object> Families;
            public Dictionary<string, List<string>> Leaving = new Dictionary<string, List<string>>();
            public Dictionary<string, object> SourceMap = new Dictionary<string, object>();
            public List<object> Ambiguous = new List<object>();
            public List<StorableObject> Created = new List<StorableObject>();
            // Aynı belgenin önceki sayfasında zaten oluşturulmuş adlar: ikinci sayfada YARDIMCI gösterim.
            public List<string> Auxiliary = new List<string>();
            // Hedefte aynı tam adlı ama defterde bu belgenin yarattığı adlar arasında OLMAYAN (yabancı) fonksiyon: o nesne
            // REJECTED_FOREIGN_SAME_NAME, yardımcı gösterimle sessizce birleştirilmez (Fable ortak 1, B 2.2).
            public List<string> Foreign = new List<string>();
            // 24.10 (=530 zw1): hedefte müşterinin parça listesiyle (BMK + ürün kodu) önceden kurulmuş cihaz etiketleri. Bizim
            // nesne bu cihaza sembol olarak girer; parça cihazda zaten var → yazılmaz, ana fonksiyon olmaz (BOM çift olmasın).
            public HashSet<string> CustomerDevices = new HashSet<string>();
            public Dictionary<string, HashSet<string>> CustomerParts = new Dictionary<string, HashSet<string>>();   // etiket → PartNr
            // 24.10 grup aktarımı (MAIN 24.10): aynı projeye BİZİM önceki paketimizin (başka belge/Anlage) kurduğu, parçalı ana
            // fonksiyonu olan cihaz etiketleri. CustomerDevices'a da eklenir (sembol girer, parça yazılmaz); burada yalnız makbuz/işaret için.
            public HashSet<string> SiblingDevices = new HashSet<string>();
            // MAIN 24.10 akşam (seçenek B): cihaz listesine parçalı kurulan etiketler (UvpPdfToP8DeviceList.cs); CustomerDevices'ta da.
            public HashSet<string> DeviceListTags = new HashSet<string>();
            public List<string> Deviations = new List<string>();
            public Dictionary<string, Function[]> DevicesFromPart = new Dictionary<string, Function[]>();
            // MAIN 03.10 akşam: cihaz etiketi -> sayfaya konan pencere makrosu (MACRO_FROM_PART).
            public Dictionary<string, MacroPlacement> Macros = new Dictionary<string, MacroPlacement>();
            // Ürün kodu metni (WritePartText) yazılmış cihaz etiketleri: cihaz başına bir kez.
            public HashSet<string> PartText = new HashSet<string>();
            // Cihaz etiketi ("=215+E215-X1") -> bu koşuda yaratılan/bulunan klemens sırası.
            public Dictionary<string, TerminalStrip> Strips = new Dictionary<string, TerminalStrip>();
            public Dictionary<string, List<Placement>> BoxGroups = new Dictionary<string, List<Placement>>();
            public MDPartsDatabase PartsDb;
            public List<string> Log;
            public string Namespace;
            public string TraceFile;
        }

        /// <summary>Konan makro: fonksiyonları, parça ucu adı -> fonksiyon, günlük. Error doluysa makro konmadı (geri
        /// alındı) ve cihazın nesneleri parçadan cihaz yoluna düşer.</summary>
        class MacroPlacement
        {
            public string Error;
            public List<Function> Functions = new List<Function>();
            public List<string> Ids = new List<string>();
            public List<string> ObjectIds = new List<string>();     // makronun koyduğu BÜTÜN nesneler (köşe, yazı…)
            public Dictionary<string, Function> ByPin = new Dictionary<string, Function>();
            public List<string> Log = new List<string>();
            public List<string> Articles = new List<string>();
            public bool Reported;
        }

        /// <summary>Adım adım ilerleme ANINDA diske yazılır: aktarım bir yerde kilitlenirse (gizli EPLAN'da
        /// modal pencere) makbuz hiç yazılmaz; kilidin yeri ancak bu günlükle kanıtlanır.</summary>
        static void Trace(Context c, string text)
        {
            if (c == null || string.IsNullOrEmpty(c.TraceFile)) return;
            try { File.AppendAllText(c.TraceFile, DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine, new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        static Dictionary<string, object> Entry(Context c, string family)
        {
            if (!c.Families.ContainsKey(family)) throw new InvalidOperationException("Eşleme yok: " + family);
            return D(c.Families[family]);
        }

        static int RemoveSince(Context c, int mark, string objectId)
        {
            int removed = 0;
            for (int k = c.Created.Count - 1; k >= mark; k--)
            {
                Placement placed = c.Created[k] as Placement;   // fonksiyon, sembol, köşe, yazı: hepsi Placement
                try { if (placed != null) { placed.Remove(); removed++; } } catch (Exception) { }
            }
            if (c.Created.Count > mark) c.Created.RemoveRange(mark, c.Created.Count - mark);
            foreach (string key in new List<string>(c.SourceMap.Keys))
                if (key == objectId || key.StartsWith(objectId + "#")) c.SourceMap.Remove(key);
            foreach (List<Placement> group in c.BoxGroups.Values)
                group.RemoveAll(delegate (Placement pl) { try { return !pl.IsValid; } catch (Exception) { return true; } });
            return removed;
        }

        static void MapPin(Context c, string node, StorableObject owner, string pin)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["object_id"] = Id(owner);
            m["pin"] = pin;
            // Uçlar aynı NESNEYE aittir: kalıcı kimlik nesnenin kendi düğümünden (`node` içindeki
            // '#' öncesi) kurulur ki aynı adlı iki klemens ayrı izlensin.
            m["source_uid"] = SOURCE_UID_PREFIX + c.Namespace + node.Split('#')[0];
            c.SourceMap[node] = m;
        }

        // Kaynak örneğini hedef nesneye KALICI bağlayan alan. `ObjectIdentifier`/`DatabaseIdentifier`
        // SDK'nın kendi belgesine göre yalnız oturum boyunca geçerlidir; proje kapanıp açılınca
        // değişir. `FUNC_SUPPLEMENTARYFIELD` (20901, indeks 1..1000) projede saklanır ve geri okunur:
        // aynı adlı iki klemens (`-X4:N24.30`) ancak böyle ayrı izlenebilir. Görünür bir fiziksel
        // uç eki DEĞİLDİR; yalnız iç takip kimliğidir.
        internal const int SOURCE_UID_FIELD = 900;
        internal const string SOURCE_UID_PREFIX = "UVP-SRC:";

        /// <summary>Geri okumada gelen değer EPLAN'ın çok dilli metin sarmalıdır ("??_??@UVP-SRC:...;").
        /// Kalıcı kimliği bu sarmaldan çıkarır; damgalanan değerle birebir karşılaştırılabilir olur.</summary>
        internal static string UidValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int start = raw.IndexOf(SOURCE_UID_PREFIX, StringComparison.Ordinal);
            if (start < 0) return raw.Trim();
            string value = raw.Substring(start);
            int end = value.IndexOf(';');
            if (end >= 0) value = value.Substring(0, end);
            // ÖLÇÜM: EPLAN çok dilli metinde '@' işaretini ikileyerek saklıyor; damgadaki
            // 'X4:N24.30@2' geri okumada 'X4:N24.30@@2' geliyor. Ters çevrilmezse aynı adlı iki
            // klemensten TAM AYIRT EDİLMESİ gereken ikincisi eşleşmez.
            return value.Replace("@@", "@").Trim();
        }

        /// <summary>Bir nesnenin damgalı kalıcı kimliği; damga yoksa boş.</summary>
        internal static string UidOf(StorableObject owner)
        {
            Function function = owner as Function;
            InterruptionPoint point = owner as InterruptionPoint;
            if (function != null)
                return UidValue(Text(delegate { return function.Properties.FUNC_SUPPLEMENTARYFIELD[SOURCE_UID_FIELD].ToString(); }));
            if (point != null)
                return UidValue(Text(delegate { return point.Properties.FUNC_SUPPLEMENTARYFIELD[SOURCE_UID_FIELD].ToString(); }));
            return "";
        }

        /// <summary>Koşu klasörünün output\p8runs kökü (sonunda ayırıcıyla); değilse boş.</summary>
        internal static string RunsRoot(string outDir)
        {
            string full = Path.GetFullPath(outDir ?? "");
            int at = full.IndexOf("\\output\\p8runs\\", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? "" : full.Substring(0, at + "\\output\\p8runs\\".Length);
        }

        // ------------------------------------------------------------------ sayfa/cihaz yapisi
        // Yapi alanlari proje OZELLIGIDIR (Eplan.EplApi.DataModelu.dll - ProjectPropertyList):
        //   10002 PROJ_HIERARCHY_PLANT     "Proje yapisi: Fonksiyon tanimlayicisi"   ( = )
        //   10004 PROJ_HIERARCHY_LOCATION  "Proje yapisi: Konum tanimlayicisi"       ( + )
        //   10050 PROJ_HIERARCHY_PAGE ve 10070 PROJ_NAMEFORMAT_PAGE = sayfa adinin yapi alanlari
        //   10051/10053..10062  kesinti noktasi, cihaz, klemens sirasi, kablo ... yapi alanlari
        // UVP_013.zw9 sablonunda 10002 ve 10004 BOS ("kullanilmiyor"), 10050 yalniz <10006><11012><11013>.
        // Olculdu: sayfa adi "/1" (Page.Create'e verilen anlage/einbauort sessizce atiliyor) ve cihaz adi
        // ayristirilamayip "-=215+E215-1F23" olarak tek parca yaziliyor; kanit:
        // output/p8runs/20260922_153330_uvp_antet_deneme/readback.json ve .../UVP_*.edb/ProjectInfo.xml.
        // IEC_bas001.zw9'da 10002=<1100>..<1109>, 10004=<1200>..<1209>, 10050=<10002><10004><11012><11013>
        // ve ayni paket "=215+E215/1" + gorunen DT "1F23" veriyor (20260922_151046_madde4_olcum).
        // Asagidaki degerler o CALISAN projeden birebir alinmistir.
        const string PLANT_LEVELS = "<1100><1101><1102><1103><1104><1105><1106><1107><1108><1109>";
        const string LOCATION_LEVELS = "<1200><1201><1202><1203><1204><1205><1206><1207><1208><1209>";
        /// <summary>Sayfa doküman tipi (&amp;, 10006) — paket sayfası `doctype` vermezse. UVP çizimci standardı (13SB004_04_+E280_V06:
        /// '=280+E280&amp;EFS1/1', yer imi =280 → +E280 → &amp;EFS1 Schaltplan); UVP_013 şablonu da &amp;EFS1 kullanır. Liste sayfası aynı.</summary>
        internal const string PageDoctype = "EFS1";
        /// <summary>Nesnesiz sayfa bu nedenlerle KURULMAZ (orkestra kararı 09.10, UVP çizimci pratiği: Reserve ve yalnız-saha sayfası yok).</summary>
        static readonly string[] SkipEmptyReasons = { "RESERVE", "SAHA" };
        /// <summary>Kullanıcı 09.10: kurulan şema sayfasının rasteri ("Kılavuz çizgileri", Page.GridSize). Paket bağlantı noktalarını
        /// aynı ızgaraya oturtur (analyzer_v3/layout.py GRID_MM); eklenti konumu yeniden yuvarlamaz.</summary>
        internal const double PageGridMm = 2.0;

        /// <summary>Yapi alani listesinin basina = (10002) ve + (10004) ekler. Okunamayan (bos) veya
        /// zaten = iceren liste DEGISTIRILMEZ: bos okuma yuzunden sayfa sayaci (11012) silinmez.</summary>
        static string AddPlantLocation(Func<string> read, Action<string> write)
        {
            string now = Text(read);
            if (now == "" || now.Contains("<10002>")) return now;
            string want = "<10002><10004>" + now;
            write(want);
            return want;
        }

        /// <summary>YALNIZ bu kosuda YARATILAN test projesinde sayfa/cihaz yapisini = ve + icerecek
        /// sekilde kurar. Kullanicinin mevcut projesinde CAGRILMAZ (MAIN.md 9). Basarisiz olursa aktarim
        /// dusmez: neden makbuza "page_structure" alaninda yazilir.</summary>
        static void EnsurePageStructure(Project prj, Dictionary<string, object> receipt, List<string> log)
        {
            try
            {
                ProjectPropertyList p = prj.Properties;
                string plant = Text(delegate { return p.PROJ_HIERARCHY_PLANT.ToString(); });
                string location = Text(delegate { return p.PROJ_HIERARCHY_LOCATION.ToString(); });
                string before = Text(delegate { return p.PROJ_HIERARCHY_PAGE.ToString(); });
                if (plant != "" && location != "" && before.Contains("<10002>") && before.Contains("<10004>"))
                {
                    receipt["page_structure"] = "zaten uygun: 10050=" + before;
                    return;
                }
                if (plant == "") p.PROJ_HIERARCHY_PLANT = PLANT_LEVELS;
                if (location == "") p.PROJ_HIERARCHY_LOCATION = LOCATION_LEVELS;
                string after = AddPlantLocation(delegate { return p.PROJ_HIERARCHY_PAGE.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_PAGE = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_PAGE.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_PAGE = v; });
                // Cihaz adi da AYNI yapi alanlarindan ayristirilir: sayfa duzelip cihaz duzelmezse
                // "-=215+E215-1F23" yazilmaya devam eder ve sayfa yine okunmaz olur.
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_INTERRUPTIONPOINT.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_INTERRUPTIONPOINT = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_BUSBAR.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_BUSBAR = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_MASTERDEVICES.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_MASTERDEVICES = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_STANDARDDEVICE.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_STANDARDDEVICE = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_DEVICEBOX.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_DEVICEBOX = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_PLC.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_PLC = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_TERMINALSTRIP.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_TERMINALSTRIP = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_PLUG.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_PLUG = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_CABLE.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_CABLE = v; });
                AddPlantLocation(delegate { return p.PROJ_HIERARCHY_LOCATIONBOX.ToString(); }, delegate(string v) { p.PROJ_HIERARCHY_LOCATIONBOX = v; });
                // Cihaz ADI FORMATI (10071..10086, "... isimlendirme formatı") ayrı alandır: yapı (100xx) açık olsa da format
                // <10002><10004> içermezse EPLAN tam adı yapısız kurar ('-X1:1'); çok panolu belgede =152 ve =122'nin X1:1'i
                // aynı ad olur (REJECTED_FOREIGN_SAME_NAME). ÖLÇÜ 04.10 (YAPI.md): çizimcinin UVP_013'ten açtığı E217
                // projesinde bu 13 format <10002><10004><20012>..., bizim yeni projede <20012>... — tek fark buydu.
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_INTERRUPTIONPOINT.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_INTERRUPTIONPOINT = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_BUSBAR.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_BUSBAR = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_MASTERDEVICES.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_MASTERDEVICES = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_DEVICE.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_DEVICE = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_DEVICEBOX.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_DEVICEBOX = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_PLC.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_PLC = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_TERMINALSTRIP.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_TERMINALSTRIP = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_PLUG.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_PLUG = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_CABLE.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_CABLE = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_LOCATIONBOX.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_LOCATIONBOX = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_FLUID.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_FLUID = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_DISTRIBUTOR.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_DISTRIBUTOR = v; });
                AddPlantLocation(delegate { return p.PROJ_NAMEFORMAT_MECHANIC.ToString(); }, delegate(string v) { p.PROJ_NAMEFORMAT_MECHANIC = v; });
                receipt["page_structure"] = "ayarlandi: 10002/10004 acildi, 10050=" + after;
                log.Add("Sayfa yapisi kuruldu (= ve + alanlari): 10050=" + after);
            }
            catch (Exception ex)
            {
                receipt["page_structure"] = "ayarlanamadi: " + ex.Message;
                log.Add("Sayfa yapisi ayarlanamadi, aktarim suruyor: " + ex.Message);
            }
        }

        // Onay kapısı (MAIN 03.10, Fable ortak 1): EPLAN'a YANLIŞ elektrik bağı / kimlik birleşmesi yazabilecek kırmızı
        // bayraklar. Bu bayraklı sayfa /ACK_RISK:1 olmadan yazılmaz (LockingStep'ten ÖNCE red; hiçbir nesne yazılmaz).
        // analyzer_v3/akis.py ACK_FLAGS ile aynı liste (test eşitliği denetler). Yalnız eksik bırakan bayraklar
        // (PARTIAL_UNMARKED, DEVICE_NAME_NOT_A_TAG, UNREADABLE_END, ROUTE_NOT_ADAPTABLE) kapıya girmez.
        internal static readonly string[] AckRequiredFlags = { "CUSTOMER_SHORT_CIRCUIT", "SHORT_CIRCUIT", "POTENTIAL_NAMES_DISAGREE", "AUTOCONNECT_RISK", "SAME_TERMINAL_TWICE" };

        /// <summary>Paket cihazının hedefteki tam adı (çakışma denetimi ve defter created_names ile aynı anahtar).</summary>
        static string PackageName(Dictionary<string, object> o)
        {
            return S(o, "family") == "terminal" ? S(o, "device_tag") + ":" + S(o, "terminal") : S(o, "device_tag");
        }

        /// <summary>Nesnenin hedefte TAŞIYABİLECEĞİ adlar: tam ad ve WriteName geri düşüşünün yazdığı ad (name_on_page + uç).
        /// ÖLÇÜM 03.10 (20261003_guvenlik_troester, UVP_013 şablonu): yapı alanları tutmadığı için EPLAN adı '-X1:N';
        /// yalnız tam adla ('=215+E215-X1:N') aranınca aynı adlı klemens HİÇ bulunmuyordu → çakışma denetimi kördü.</summary>
        static List<string> TargetNames(Dictionary<string, object> o)
        {
            string suffix = S(o, "family") == "terminal" ? ":" + S(o, "terminal") : "";
            List<string> names = new List<string> { S(o, "device_tag") + suffix };
            string shown = S(o, "name_on_page");
            if (shown != "" && !names.Contains(shown + suffix)) names.Add(shown + suffix);
            return names;
        }

        /// <summary>24.10 (=530 zw1, MAIN 24.10): müşterinin parça listesiyle önceden kurduğu cihazlar — tam DT'sinde SAYFAYA
        /// KONMAMIŞ ve parça taşıyan bir fonksiyon olan etiketler (TROESTER BMK + ürün kodu importu; ölçüm E0b: 27 DT, 44
        /// fonksiyon, hepsi yerleşmemiş + parçalı). Ad, fonksiyon adının ':' öncesidir ('=530+E530-8D21:SH' → '=530+E530-8D21').</summary>
        /// <param name="mainParts">Dolar: parça taşıyan ANA fonksiyonu olan etiketler (yerleşik ya da değil) → PartNr; grup aktarımı.</param>
        static Dictionary<string, HashSet<string>> CustomerPartDevices(Project prj, Dictionary<string, HashSet<string>> mainParts)
        {
            Dictionary<string, HashSet<string>> tags = new Dictionary<string, HashSet<string>>();
            foreach (Function f in new DMObjectsFinder(prj).GetFunctions(new FunctionsFilter()))
            {
                try
                {
                    if (f.ArticleReferences.Length == 0) continue;
                    bool unplaced = !f.IsPlaced, main = f.IsMainFunction;
                    if (!unplaced && !main) continue;
                    string name = Text(delegate { return f.Name; });
                    int cut = name.IndexOf(':');
                    string tag = cut < 0 ? name : name.Substring(0, cut);
                    foreach (Dictionary<string, HashSet<string>> into in new[] { unplaced ? tags : null, main ? mainParts : null })
                    {
                        if (into == null) continue;
                        if (!into.ContainsKey(tag)) into[tag] = new HashSet<string>();
                        foreach (ArticleReference ar in f.ArticleReferences) into[tag].Add(ar.PartNr);
                    }
                }
                catch (Exception) { }
            }
            return tags;
        }

        /// <summary>Müşteri cihazına giren bizim fonksiyonlar (makro, parçadan cihaz, kara kutu) parça taşımaz ve ana fonksiyon
        /// olmaz: cihazın parçası ve ana fonksiyonu müşterinin yerleşmemiş fonksiyonundadır. Ölçüm E1 SAHTE: bu yapılmazsa aynı
        /// PartNr cihazda iki kez (BOM çift) ve 017005 19→55. Döner: makbuz satırları.</summary>
        static List<object> DropCustomerParts(IEnumerable<Function> functions)
        {
            List<object> rows = new List<object>();
            foreach (Function f in functions)
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                try
                {
                    r["function"] = Text(delegate { return f.Name; });
                    List<string> removed = new List<string>();
                    foreach (ArticleReference ar in f.ArticleReferences) { removed.Add(ar.PartNr); f.RemoveArticleReference(ar); }
                    if (removed.Count > 0) r["parts_removed"] = removed;
                    if (f.IsMainFunction) { f.IsMainFunction = false; r["main_function"] = false; }
                }
                catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; }
                if (r.Count > 1) rows.Add(r);
            }
            return rows;
        }

        // Kırmızı uyarı (MAIN 2026-09-30): sayfa aktarılır ama "tam aktarıldı" sayılmaz. Kodlar
        // analyzer_v3/akis.py risk_flags ile aynıdır; sayfanın kendi coverage/layout kaydından okunur.
        static void AddRiskFlags(Dictionary<string, object> page, List<string> flags)
        {
            Dictionary<string, object> cov = page.ContainsKey("coverage") ? D(page["coverage"]) : null;
            Dictionary<string, object> lay = page.ContainsKey("layout") ? D(page["layout"]) : null;
            List<string> found = new List<string>();
            if (cov != null)
            {
                foreach (object f in A(cov.ContainsKey("page_flags") ? cov["page_flags"] : null)) found.Add(Convert.ToString(f));
                if (A(cov.ContainsKey("customer_short_circuits") ? cov["customer_short_circuits"] : null).Count > 0) found.Add("CUSTOMER_SHORT_CIRCUIT");
                if (A(cov.ContainsKey("invalid_device_names") ? cov["invalid_device_names"] : null).Count > 0) found.Add("DEVICE_NAME_NOT_A_TAG");
            }
            if (lay != null && A(lay.ContainsKey("routes_not_adaptable") ? lay["routes_not_adaptable"] : null).Count > 0) found.Add("ROUTE_NOT_ADAPTABLE");
            foreach (string f in found) if (!flags.Contains(f)) flags.Add(f);
        }

        static void WriteLedger(Project prj, Dictionary<string, object> pkg, string outDir, string state, Dictionary<Page, int> physical,
                                List<string> createdNames)
        {
            ICollection<int> written = new List<int>(physical.Values);
            string ledgerFile = Path.ChangeExtension(prj.ProjectLinkFilePath, ".uvp_ledger.json");
            Dictionary<string, object> entry = new Dictionary<string, object>();
            // 14.10 'Üstüne yaz' (/REPLACE:1): bu aktarımın kurduğu EPLAN sayfa adları; sonraki üstüne yazma bunları kaldırır.
            List<object> pageNames = new List<object>();
            foreach (Page pg in physical.Keys)
            {
                try { if (pg.IsValid) pageNames.Add(pg.Name); } catch (Exception) { }
            }
            entry["page_names"] = pageNames;
            entry["package_sha256"] = S(pkg, "payload_sha256");
            entry["document_sha256"] = S(pkg, "document_sha256");
            entry["receipt"] = Path.Combine(outDir, "receipt.json");
            entry["state"] = state;
            entry["utc"] = DateTime.UtcNow.ToString("o");
            List<object> importedPages = new List<object>();
            // /PAGES süzgecinde yazılmayan sayfa "aktarıldı" sayılmaz (R4-C2): yalnız gerçekten yazılanlar.
            foreach (object po in A(pkg["pages"]))
            {
                int pageNo = Convert.ToInt32(D(po)["physical_page"]);
                if ((written == null || written.Contains(pageNo)) && !importedPages.Contains(pageNo)) importedPages.Add(pageNo);
            }
            entry["pages"] = importedPages;
            // Bu aktarımın YARATTIĞI cihaz tam adları: sonraki sayfada aynı ad ancak bu kümedeyse "bizim" (yardımcı gösterim);
            // değilse yabancıdır ve birleştirilmez (REJECTED_FOREIGN_SAME_NAME).
            entry["created_names"] = createdNames ?? new List<string>();
            List<object> entries = new List<object>(LedgerEntries(ledgerFile));
            entries.Add(entry);
            Dictionary<string, object> ledger = new Dictionary<string, object>();
            ledger["contract"] = "uvp.pdf2p8.ledger";
            ledger["contract_version"] = "2.0";
            ledger["imports"] = entries;      // sayfa sayfa ilerleme: her aktarım eklenir, silinmez
            Io.WriteJson(ledgerFile, ledger);
        }

        /// <summary>
        /// 14.10 'Üstüne yaz' (/REPLACE:1): çakışan defter kayıtlarının yazdığı EPLAN sayfaları kaldırılır. K4: önce .zw1
        /// yedek (alınamazsa yazılmaz), sonra tek Geri Al adımı. Sayfa adı defterden (page_names) ya da makbuzdan (pages);
        /// ikisi de yoksa hangi sayfanın bizim olduğu bilinmez → red (tahminle sayfa silinmez). Kalan sayfa varsa geri alınır.
        /// Kayıtlar REPLACED işaretlenir (önceki durum replaced_state'te); sayfaları artık projede değildir.
        /// </summary>
        static void ReplacePrevious(Project prj, List<Dictionary<string, object>> hits, string outDir,
                                    Dictionary<string, object> receipt, List<string> log)
        {
            List<string> names = new List<string>();
            foreach (Dictionary<string, object> past in hits)
            {
                ArrayList known = A(past.ContainsKey("page_names") ? past["page_names"] : null);
                if (known.Count == 0 && File.Exists(S(past, "receipt")))
                {
                    try
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        js.MaxJsonLength = int.MaxValue;
                        Dictionary<string, object> old = js.DeserializeObject(File.ReadAllText(S(past, "receipt"))) as Dictionary<string, object>;
                        if (old != null) known = A(old.ContainsKey("pages") ? old["pages"] : null);
                    }
                    catch (Exception ex) { log.Add("eski makbuz okunamadı: " + S(past, "receipt") + ": " + ex.Message); }
                }
                if (known.Count == 0 && A(past.ContainsKey("pages") ? past["pages"] : null).Count > 0)
                    throw new RefusedException("REFUSED_REPLACE_UNKNOWN_PAGES", "Üstüne yazılamadı: önceki aktarımın hangi EPLAN sayfalarını yazdığı "
                        + "belirsiz (defterde sayfa adı yok, makbuz okunamadı: " + S(past, "receipt") + "). 'Yeni proje' seçin; hiçbir nesne yazılmadı.");
                foreach (object n in known) if (!names.Contains(Convert.ToString(n))) names.Add(Convert.ToString(n));
            }
            List<Page> doomed = new List<Page>();
            List<string> missing = new List<string>(names);
            foreach (Page pg in prj.Pages)
                if (names.Contains(pg.Name)) { doomed.Add(pg); missing.Remove(pg.Name); }
            receipt["replace_backup"] = Guard.Backup(prj);
            List<string> removed = new List<string>();
            using (LockingStep step = new LockingStep())
            {
                UndoStep undo = new UndoManager().CreateUndoStep();
                undo.SetUndoDescription("UVP · Üstüne yaz: önceki aktarımın sayfaları");
                foreach (Page pg in doomed) { string n = pg.Name; pg.Remove(); removed.Add(n); }
                int left = 0;
                foreach (Page pg in prj.Pages) if (names.Contains(pg.Name)) left++;
                if (left > 0)
                {
                    undo.DoUndo();
                    throw new RefusedException("REFUSED_REPLACE_FAILED", "Üstüne yazılamadı: " + left + " eski sayfa kaldırılamadı; geri alındı, hiçbir nesne yazılmadı.");
                }
            }
            receipt["replaced_pages"] = removed;
            receipt["replaced_pages_already_missing"] = missing;   // kullanıcı elle silmiş olabilir
            List<object> receipts = new List<object>();
            foreach (Dictionary<string, object> past in hits) receipts.Add(S(past, "receipt"));
            receipt["replaced_imports"] = receipts;
            log.Add("Üstüne yaz: " + removed.Count + " eski sayfa kaldırıldı (" + string.Join(", ", removed.ToArray()) + "), yedek " + receipt["replace_backup"]);
            // Defter: sayfaları kaldırılan kayıtlar REPLACED (silinmez; geçmiş kanıt korunur).
            string ledgerFile = Path.ChangeExtension(prj.ProjectLinkFilePath, ".uvp_ledger.json");
            List<object> entries = LedgerEntries(ledgerFile);
            foreach (object e in entries)
            {
                Dictionary<string, object> row = D(e);
                foreach (Dictionary<string, object> past in hits)
                    if (S(row, "receipt") == S(past, "receipt") && S(row, "utc") == S(past, "utc") && S(row, "state") != "REPLACED")
                    {
                        row["replaced_state"] = S(row, "state");
                        row["state"] = "REPLACED";
                        row["replaced_utc"] = DateTime.UtcNow.ToString("o");
                        row["replaced_by"] = Path.Combine(outDir, "receipt.json");
                    }
            }
            Dictionary<string, object> ledger = new Dictionary<string, object>();
            ledger["contract"] = "uvp.pdf2p8.ledger";
            ledger["contract_version"] = "2.0";
            ledger["imports"] = entries;
            Io.WriteJson(ledgerFile, ledger);
        }

        static void StampSource(Context c, string node, StorableObject owner, Dictionary<string, object> row)
        {
            string value = SOURCE_UID_PREFIX + c.Namespace + node;
            try
            {
                Function function = owner as Function;
                InterruptionPoint point = owner as InterruptionPoint;
                if (function == null && point == null) return;
                // Alan DOLUYSA yazılmaz: müşteri/makro verisi (veya başka bir kimlik) ezilmez; kimlik
                // yazılmadığı için doğrulayıcı bu nesneyi ada göre ve belirsizlik denetimiyle izler.
                string existing = UidOf(owner);
                if (existing == "")
                {
                    string raw = function != null
                        ? Text(delegate { return function.Properties.FUNC_SUPPLEMENTARYFIELD[SOURCE_UID_FIELD].ToString(); })
                        : Text(delegate { return point.Properties.FUNC_SUPPLEMENTARYFIELD[SOURCE_UID_FIELD].ToString(); });
                    if (raw.Trim() != "" && raw.Trim() != "??_??@;") existing = raw.Trim();
                }
                if (existing == UidValue(value))
                {
                    // Çok uçlu cihazın sonraki ucu: kimliği bu aktarım az önce yazdı, hata değildir (öz denetim F5-03).
                    if (row != null) row["source_uid"] = value;
                    return;
                }
                if (existing != "")
                {
                    if (row != null) row["source_uid_error"] = "FIELD_IN_USE: ek alan " + SOURCE_UID_FIELD + " dolu (" + existing + "); ezilmedi.";
                    return;
                }
                if (function != null) function.Properties.FUNC_SUPPLEMENTARYFIELD[SOURCE_UID_FIELD] = value;
                else point.Properties.FUNC_SUPPLEMENTARYFIELD[SOURCE_UID_FIELD] = value;
                if (row != null) row["source_uid"] = value;
            }
            catch (Exception ex) { if (row != null) row["source_uid_error"] = ex.GetType().Name + ": " + ex.Message; }
        }

        static void MapObject(Context c, string node, StorableObject owner)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["object_id"] = Id(owner);
            m["source_uid"] = SOURCE_UID_PREFIX + c.Namespace + node;
            c.SourceMap[node] = m;
        }

        static void CheckDeviation(Context c, string node, Placement owner, PointD real, PointD planned, Dictionary<string, object> row)
        {
            double shift = Math.Round(Math.Abs(real.X - planned.X) + Math.Abs(real.Y - planned.Y), 3);
            row["real_point"] = XY(real);
            row["planned_point"] = XY(planned);
            row["deviation_mm"] = shift;
            if (shift > 0.05) c.Deviations.Add(node + ": gerçek bağlantı noktası planlanandan " + shift + " mm farklı");
            // R2 (kullanıcı 09.10): raster BAĞLANTI NOKTASI esaslı; yer = nokta − sembol ofseti. Ofset ızgara katı değilse
            // (DCP +3, klemens 0,75, makro iç aralığı) yer ızgarada olmaz, uç olur: POINT_ONLY. Uç dışarıdaysa OFF_GRID.
            bool point = OnGrid(real), origin = owner != null && OnGrid(owner.Location);
            row["snap_mode"] = point ? (origin ? "ORIGIN_AND_POINT" : "POINT_ONLY") : "OFF_GRID";
        }

        static bool OnGrid(PointD p)
        {
            return Math.Abs(p.X - Math.Round(p.X / PageGridMm) * PageGridMm) <= 0.01
                && Math.Abs(p.Y - Math.Round(p.Y / PageGridMm) * PageGridMm) <= 0.01;
        }

        static void PlaceJunction(Context c, Dictionary<string, object> o, Dictionary<string, object> row)
        {
            Dictionary<string, object> entry = Entry(c, S(o, "family"));
            List<string> arms = new List<string>();
            foreach (object arm in A(o.ContainsKey("arms") ? o["arms"] : null)) arms.Add(Convert.ToString(arm));
            arms.Sort(StringComparer.Ordinal);
            string key = string.Join(",", arms.ToArray());
            Dictionary<string, object> byArms = entry.ContainsKey("symbols_by_arms") ? D(entry["symbols_by_arms"]) : null;
            if (arms.Count != 3 || byArms == null || !byArms.ContainsKey(key))
                throw new InvalidOperationException("T düğümü kol kümesi eşlenmedi: [" + key + "]");
            string note;
            SymbolVariant sv = VariantWithDirections(c.Project, S(entry, "library"), Convert.ToString(byArms[key]), key, out note);
            row["symbol"] = note;
            if (sv == null) throw new InvalidOperationException(note);
            SymbolReference sr = SymbolReference.Create(sv, c.Page);
            c.Created.Add(sr);
            sr.Location = P(o["point_mm"]);
            row["object_id"] = Id(sr);
            MapObject(c, S(o, "id"), sr);
        }

        static void PlaceEnd(Context c, Dictionary<string, object> o, Dictionary<string, object> row)
        {
            string kind = S(o, "kind"), id = S(o, "id");
            Dictionary<string, object> entry = Entry(c, S(o, "family"));
            PointD here = P(o["point_mm"]);
            // Yön KAYNAK ÇİZİMDEN gelir: güzergâh varsa onun çıkış yönü, yoksa paketin `facing`
            // alanı (çizilmiş telin yönü). İkisi de yoksa nesne reddedilir; yön uydurulmaz.
            List<string> ways = c.Leaving.ContainsKey(id) ? c.Leaving[id] : new List<string>();
            string facing = S(o, "facing");
            string way = ways.Count == 1 ? ways[0] : (ways.Count == 0 ? facing : "");
            row["facing_source"] = ways.Count == 1 ? "ROUTE" : (way != "" ? "SOURCE_DRAWING" : "NONE");
            if (S(o, "link_state") != "") { row["link_state"] = S(o, "link_state"); row["link_reason"] = S(o, "link_reason"); }
            if (way == "")
                throw new InvalidOperationException(ways.Count > 1
                    ? "Ucun güzergâh yönü tekil değil: [" + string.Join(",", ways.ToArray()) + "]"
                    : "Ucun yönü bilinmiyor: pakette güzergâh da `facing` de yok.");
            if (kind == "POTENTIAL_BOUNDARY")
            {
                // Kanıtlı güzergâh bu uca çizilebiliyorsa BAĞLANTI NOKTALI varyant kullanılır.
                // ÖLÇÜM (capabilities.json): SPECIAL/PDP bu kütüphanede 0 bağlantı noktası taşıyor —
                // potansiyel tanım noktası telin ÜSTÜNDE durur, telin ucu değildir. O durumda nokta
                // yine konur, ama BAĞLANTI İDDİA EDİLMEZ: durumu makbuzda görünür kalır.
                int pindex;
                string pnote;
                SymbolVariant pv = VariantFacing(c.Project, S(entry, "library"), S(entry, "symbol"), way, -1, out pindex, out pnote);
                bool routable = pv != null;
                if (!routable) { pv = MappedVariant(c.Project, entry); pindex = -1; }
                row["symbol"] = routable ? pnote : pnote + " → yönsüz varyantla kondu";
                if (pv == null) throw new InvalidOperationException(pnote);
                SymbolReference pr = SymbolReference.Create(pv, c.Page);
                c.Created.Add(pr);
                pr.Location = routable ? InsertionFor(pv, pindex, here) : here;
                PotentialDefinition pdef = pr as PotentialDefinition;
                if (pdef == null) throw new InvalidOperationException("Sembol potansiyel tanımı üretmedi: " + pr.GetType().Name);
                pdef.PotentialName = S(o, "name");
                row["object_id"] = Id(pr);
                row["connection_points"] = pv.ConnectionPoints == null ? 0 : pv.ConnectionPoints.Length;
                row["route_state"] = routable ? "ROUTED_TO_CONNECTION_POINT" : "NO_CONNECTION_POINT_ON_SYMBOL";
                if (!routable)
                    row["note"] = "Potansiyel tanım noktasının bu kütüphanede bağlantı noktası yok; nokta "
                                + "kondu ve potansiyel adı yazıldı, bu uca elektriksel bağlantı İDDİA EDİLMEDİ.";
                if (routable) CheckDeviation(c, id, pr, Absolute(pr, pv.ConnectionPoints[pindex]), here, row);
                StampSource(c, id, pr, row);
                MapObject(c, id, pr);
                return;
            }
            int index;
            string note;
            // Eşlemede `wire_point` varsa tel o noktaya bağlanır (BP: 1 = kuyruk, ok telden dışarı bakar).
            // Yoksa veya o noktası bu yöne bakan varyant yoksa eski davranış: yöne bakan ilk nokta.
            // `wire_point_by_facing` (ucun baktığı yöne göre) önce gelir: 29.09 kullanıcı "hat her zaman soldan sağa";
            // sol kenardaki okta tel sağdadır (uç Right'a bakar) → tel ok UCUNA bağlanır, ok sağa (tele) bakar.
            string byFacing = entry.ContainsKey("wire_point_by_facing") ? S(D(entry["wire_point_by_facing"]), way) : "";
            int wirePoint = byFacing != "" ? Convert.ToInt32(byFacing)
                          : S(entry, "wire_point") != "" ? Convert.ToInt32(entry["wire_point"]) : -1;
            SymbolVariant sv = VariantFacing(c.Project, S(entry, "library"), S(entry, "symbol"), way, wirePoint, out index, out note);
            if (sv == null && wirePoint >= 0)
            {
                string first = note;
                sv = VariantFacing(c.Project, S(entry, "library"), S(entry, "symbol"), way, -1, out index, out note);
                note += " (wire_point " + wirePoint + " uymadı: " + first + ")";
            }
            row["symbol"] = note;
            if (sv == null) throw new InvalidOperationException(note);
            SymbolReference sr = SymbolReference.Create(sv, c.Page);
            c.Created.Add(sr);
            sr.Location = InsertionFor(sv, index, here);
            InterruptionPoint ip = sr as InterruptionPoint;
            if (ip == null) throw new InvalidOperationException("Sembol kesinti noktası üretmedi: " + sr.GetType().Name);
            // KULLANICI KARARI 09.10 14:00 (MAIN): DT = basılı hat adı ('L1', 'P24.i1'); yoksa hedef cihaz:uç (yapı ayraçları
            // '_'); OKUNAMADI'da benzersiz geçici ad. Belge öneki YOK: aynı adlı okları EPLAN sayfa sırasıyla zincirler.
            // KULLANICI KARARI 10.10 13:45 (MAIN): DT'de pano yapısı '=280+E280-L1'; yapı sayfadaki gibi AYRI alanlarla
            // (WriteName: tam ad, tutmazsa NameParts), geri okunur. Beklenen ad çıkmazsa basılı ad yapısız yazılır (NAME_FALLBACK).
            string dt = S(o, "import_name");
            if (S(o, "dt_anlage") != "" && S(o, "dt_einbauort") != "")
            {
                string tag = "=" + S(o, "dt_anlage") + "+" + S(o, "dt_einbauort") + "-" + dt;
                row["name_write"] = WriteName(ip, tag, "", dt);
                if (Text(delegate { return ip.Name; }) != tag) row["name_fallback"] = "NAME_FALLBACK";
            }
            else ip.Name = dt;
            // GÖRÜNEN ad: MAIN 30.09 müşterinin hedefi cihaz:uç ('-6K36:9') önce; yoksa hat adı (L1, P24.32).
            // Sayfa no YAZILMAZ (EPLAN çapraz referansı ekler). Adlı hatta da hedef önce: s40 /2.49 müşteri
            // '-X4:6' basmış, 'P24.60' değil. Eşleşme kimliği yalnız `Name`de; layout match_text aynı sırada.
            string shown = S(o, "target_text") != "" ? S(o, "target_text") : S(o, "name");
            // DT ile aynı olsa da YAZILIR: 10.10 ölçümü (Host_duzad, =280 s5-s11) VisibleName boşken EPLAN oka ad basmıyor
            // (P24.i1/N24.i PDF'te yok, geri okuma visible_name '').
            if (shown != "") { try { ip.VisibleName = shown; } catch (Exception ex) { row["visible_name_error"] = ex.Message; } }
            if (S(o, "target_text") != "") row["target_text"] = S(o, "target_text");
            PlaceLabels(c, ip, o, row);
            XrefBeside(ip, o, here, row);
            row["name_written"] = ip.Name;
            row["visible_name_written"] = Text(delegate { return ip.VisibleName; });
            row["source_potential"] = S(o, "name");
            row["source_reference"] = S(o, "reference");
            row["object_id"] = Id(sr);
            CheckDeviation(c, id, sr, Absolute(sr, sv.ConnectionPoints[index]), here, row);
            StampSource(c, id, sr, row);
            MapObject(c, id, sr);
        }

        /// <summary>Paketin istediği GÖRÜNÜR etiketleri EPLAN özellik gösterimi olarak koyar.
        /// Düz metin kopyası yazılmaz: gösterim nesnenin kendi özelliğine bağlıdır, kimlik değişirse
        /// gösterim de değişir. Konum paketten gelir (UVP kuralı), burada uydurulmaz.</summary>
        static void PlaceLabels(Context c, SymbolReference sr, Dictionary<string, object> o,
                                Dictionary<string, object> row, string onlyRole = null, string skipRole = null)
        {
            List<object> placed = new List<object>();
            foreach (object lo in A(o.ContainsKey("labels") ? o["labels"] : null))
            {
                Dictionary<string, object> label = D(lo);
                // DCP uç adı (pin_text) ad yazıldıktan SONRA taşınır (PlaceDevice): iki geçiş, aynı etiket bir kez.
                if ((onlyRole != null && S(label, "role") != onlyRole) || (skipRole != null && S(label, "role") == skipRole)) continue;
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["role"] = S(label, "role");
                entry["property"] = S(label, "property");
                try
                {
                    bool moveExisting = S(label, "move_existing") == "True" || S(label, "move_existing") == "true";
                    string matchText = S(label, "match_text");
                    bool rotate = label.ContainsKey("rotation_deg");
                    AnyPropertyId wanted = S(label, "property") != "" ? LabelPropertyId(sr, S(label, "property")) : null;
                    PropertyPlacement text = null;
                    // `move_existing`: sembolün KENDİ gösterimi taşınır/döndürülür, YENİ yazı eklenmez.
                    // (Ölçüm: ikinci yazı eklenince aynı ad iki kez basılıyordu.)
                    if (moveExisting)
                    {
                        // ÖLÇÜM (astra6/10_teslim): örnek yerel yerleşim kullanmıyorsa `PropertyPlacements`
                        // sembol VARYANTININ ortak gösterimini döndürüyor (üç kesinti noktasında aynı nesne,
                        // başka veritabanı: 70/45/3012/0) ve yazma LockingException veriyordu. SDK:
                        // SwitchLocalPropertyPlacements(true) gösterimleri örneğe kopyalar.
                        if (!sr.UseLocalPropertyPlacements)
                        {
                            sr.SwitchLocalPropertyPlacements(true);
                            entry["switched_local"] = true;
                        }
                        List<PropertyPlacement> found = new List<PropertyPlacement>();
                        foreach (Placement pl in sr.PropertyPlacements)
                        {
                            PropertyPlacement candidate = pl as PropertyPlacement;
                            if (candidate == null) continue;
                            // `DisplayedProperty` gösterilen özelliği verir (SDK: PropertyPlacement).
                            if (wanted != null)
                            {
                                try { if (candidate.DisplayedProperty.ToString() != wanted.ToString()) continue; }
                                catch (Exception) { continue; }
                            }
                            // Görünen metin eşit olmalı: aynı özelliği gösteren BOŞ ikinci yerleşim var
                            // (ölçüm astra6/30_teslim: kesinti noktasında 2 aday).
                            if (matchText != "" && Text(delegate { return candidate.GetDisplayString(); }) != matchText) continue;
                            // Döndürülecek yazı şu an DÖNÜKSÜZ olandır. Klemenste aynı metnin gizli dikey
                            // kopyası var (dönüş 1,5708, ölçüm astra6/31_gorsel); kutu oranı kısa adlarda
                            // ('1', 'PE') yanlış seçiyordu.
                            if (rotate)
                            {
                                double current;
                                try { current = candidate.Rotation; } catch (Exception) { continue; }
                                if (Math.Abs(current) > 1e-6) continue;
                            }
                            found.Add(candidate);
                        }
                        entry["candidates"] = found.Count;
                        if (found.Count != 1)
                            throw new InvalidOperationException("EXISTING_TEXT_NOT_UNIQUE: " + found.Count
                                + " aday; yeni yazı EKLENMEDİ (aynı ad iki kez basılırdı).");
                        text = found[0];
                        // Kütüphane nesnesine asla yazılmaz: gösterim örnekle AYNI veritabanında olmalı.
                        if (Id(text).Split('/')[0] != Id(sr).Split('/')[0])
                            throw new InvalidOperationException("PLACEMENT_NOT_IN_PROJECT: " + Id(text) + " sembol kütüphanesine ait; yazılmadı.");
                        text.LockObject();
                        entry["moved_existing"] = true;
                    }
                    if (text == null)
                    {
                        if (wanted == null) throw new InvalidOperationException("Etiket özelliği yok; yeni yazı konamaz.");
                        text = new PropertyPlacement(); text.Create(sr, wanted);
                    }
                    if (rotate)
                    {
                        // Kaynak çizimin kuralı: sık klemens adları ve tel ucu devam yazıları DİKEY.
                        double degrees = Convert.ToDouble(label["rotation_deg"], System.Globalization.CultureInfo.InvariantCulture);
                        text.Rotation = degrees * Math.PI / 180.0;       // SDK: radyan
                        entry["rotation_rad"] = Math.Round(text.Rotation, 4);
                    }
                    if (label.ContainsKey("at_mm"))
                    {
                        // ÖLÇÜM (21_gorsel_A): PropertyPlacement.Location sembol referansına GÖRELİDİR.
                        // Mutlak yazılınca etiket sayfanın dışına düşüyor ve hiç çizilmiyordu.
                        PointD want = P(label["at_mm"]), origin = sr.Location;
                        text.Location = new PointD(want.X - origin.X, want.Y - origin.Y);
                        entry["absolute_at"] = XY(want);
                    }
                    double height = label.ContainsKey("height_mm") ? Convert.ToDouble(label["height_mm"]) : 0;
                    if (height > 0) text.Height = height;
                    string justification = S(label, "justification");
                    if (justification != "")
                        text.Justification = (Eplan.EplApi.DataModel.Graphics.TextBase.JustificationType)
                            Enum.Parse(typeof(Eplan.EplApi.DataModel.Graphics.TextBase.JustificationType), justification);
                    c.Created.Add(text);
                    entry["object_id"] = Id(text);
                    entry["relative_at"] = XY(text.Location);
                    entry["shown"] = Text(delegate { return text.GetDisplayString(); });
                }
                catch (Exception ex) { entry["error"] = ex.GetType().Name + ": " + ex.Message; }
                placed.Add(entry);
            }
            if (placed.Count == 0) return;
            if (row.ContainsKey("labels") && row["labels"] is List<object>) ((List<object>)row["labels"]).AddRange(placed);
            else row["labels"] = placed;
        }

        /// <summary>Y7 kesinti oku çapraz referans modu (R2 09.10). 'place' (varsayılan): sol okta zincir adın SOLUNA alınır;
        /// 'chains0': proje ayarı DisplayDestinationAtChains=0 (yalnız yaratılan test projesi), yazılar EPLAN'ın dizilişinde.
        /// UVP_XREF_MODE ortam değişkeni yalnız ölçüm içindir (iki seçeneği aynı DLL ile karşılaştırmak).</summary>
        internal static string XrefMode()
        {
            string m = Environment.GetEnvironmentVariable("UVP_XREF_MODE");
            return string.IsNullOrEmpty(m) ? "place" : m.Trim().ToLowerInvariant();
        }

        /// <summary>EPLAN yazı genişliği tahmini: 0,64 × yükseklik × karakter (ölçüm V_sabah PDF B4: 'L1' 2,5 mm → 3,2 mm;
        /// '-5Q13:1' 1,8 mm → 8,1 mm).</summary>
        internal static double TextWidth(string text, double height) { return 0.64 * height * text.Length; }
        internal const double XrefGapMm = 0.8;            // EPLAN yapışık yazı aralığı (aynı PDF: 'L1' sağ 34,6 → '/' sol 35,4)

        internal static Dictionary<string, object> PlacementRow(PropertyPlacement t, Placement owner)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["text"] = Text(delegate { return t.GetDisplayString(); });
            try { e["docked"] = t.IsDocked; e["dock"] = t.DockingDirection.ToString(); } catch (Exception) { }
            try { e["location"] = XY(new PointD(owner.Location.X + t.Location.X, owner.Location.Y + t.Location.Y)); } catch (Exception) { }
            try { e["justification"] = t.Justification.ToString(); e["height"] = Math.Round(t.Height, 3); e["rotation"] = Math.Round(t.Rotation, 4); } catch (Exception) { }
            try { e["visible"] = t.IsVisible; } catch (Exception) { }
            return e;
        }

        /// <summary>Y7 (09.10): kesinti noktasının '/' ve çapraz referans yazıları ad yazısına YAPIŞIK (IsDocked) ve EPLAN onları
        /// adın SAĞINA dizer. Ad okun solunda (sol kenar oku, 'L1 ▶') olunca zincir ok gövdesine biniyordu (V_sabah B4/B5 6/6);
        /// DockingDirection=DockToLeft etkisizdi (aynı PDF). Kural: ad yazı ekseninde okun ÖNÜNDEYSE yapışık yazılar ayrılır
        /// (UndockText) ve adın SOLUNA sağa hizalı dizilir: UVP çizimi '-2Q12:3 /2.9 / L1 ▶' (13SB004_04 p15). Ad okun
        /// arkasındaysa EPLAN'ın dizilişi zaten UVP'deki gibi '▶ L1 / -2Q12:1 /2.0' (p12): dokunulmaz. Şeritteki ada dokunulmaz.
        /// Konum sembole GÖRELİ yazılır (PlaceLabels ölçümü). Makbuz: xref_placements (önce), xref_place (taşınan).</summary>
        static void XrefBeside(InterruptionPoint ip, Dictionary<string, object> o, PointD here, Dictionary<string, object> row)
        {
            Dictionary<string, object> label = null;
            foreach (object lo in A(o.ContainsKey("labels") ? o["labels"] : null))
                if (S(D(lo), "role") == "visible_name" && D(lo).ContainsKey("at_mm")) { label = D(lo); break; }
            List<object> before = new List<object>(), moved = new List<object>();
            try
            {
                if (!ip.UseLocalPropertyPlacements) ip.SwitchLocalPropertyPlacements(true);
                List<PropertyPlacement> docked = new List<PropertyPlacement>();
                foreach (Placement pl in ip.PropertyPlacements)
                {
                    PropertyPlacement t = pl as PropertyPlacement;
                    if (t == null) continue;
                    before.Add(PlacementRow(t, ip));
                    if (t.IsDocked) docked.Add(t);
                }
                row["xref_placements"] = before;
                if (label == null || docked.Count == 0 || XrefMode() != "place") return;
                PointD at = P(label["at_mm"]);
                double rad = label.ContainsKey("rotation_deg") ? N(label["rotation_deg"]) * Math.PI / 180.0 : 0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad), dx = at.X - here.X, dy = at.Y - here.Y;
                double along = dx * cos + dy * sin, across = -dx * sin + dy * cos;
                if (along >= 0 || Math.Abs(along) <= Math.Abs(across)) return;
                string just = S(label, "justification");
                double h = label.ContainsKey("height_mm") ? N(label["height_mm"]) : 2.5;
                double w = TextWidth(S(label, "match_text") != "" ? S(label, "match_text") : Text(delegate { return ip.VisibleName; }), h);
                double cursor = (just.EndsWith("Right") ? along - w : just.EndsWith("Center") ? along - w / 2 : along) - XrefGapMm;
                string vertical = just.StartsWith("Top") ? "Top" : just.StartsWith("Bottom") ? "Bottom" : "Middle";
                TextBase.JustificationType right = (TextBase.JustificationType)Enum.Parse(typeof(TextBase.JustificationType), vertical + "Right");
                foreach (PropertyPlacement t in docked)
                {
                    string text = Text(delegate { return t.GetDisplayString(); });
                    t.LockObject();
                    t.UndockText();
                    t.Rotation = rad;
                    t.Justification = right;
                    PointD abs = new PointD(here.X + cursor * cos - across * sin, here.Y + cursor * sin + across * cos);
                    t.Location = new PointD(abs.X - ip.Location.X, abs.Y - ip.Location.Y);
                    Dictionary<string, object> e = PlacementRow(t, ip);
                    e["text_at_import"] = text;
                    moved.Add(e);
                    // Aktarım anında çapraz referans henüz boş olabilir (karşı sayfa sonra gelir): sağa hizalı yazı sola büyür.
                    cursor -= TextWidth(text == "" ? "/" : text, t.Height > 0 ? t.Height : h) + XrefGapMm;
                }
            }
            catch (Exception ex) { row["xref_place_error"] = ex.GetType().Name + ": " + ex.Message; }
            row["xref_place"] = moved;
        }

        /// <summary>İki taraflı tek uçlu parça fonksiyonu (MAIN 02.10 akşam; SITOP 6EP1961 'S'/'S') tek noktalı DCP ile
        /// konunca EPLAN bütün uç adlarını/açıklamalarını ';' ile basıyordu ('S;S', '+24V (1);+24V (1)', 'STATUS OUTPUT;
        /// STATUS OUTPUT'; 02.10 h3 s6 yazı çarpışması). Aynı metni tekrarlayan yerleşim, adlı tarafın KENDİ özelliğine
        /// çevrilir: uç adı 20022[k], açıklama FUNC_CONNECTIONDESCRIPTION[k]. Fonksiyon verisi değişmez, yalnız gösterim.
        /// Sonuç tek ad değilse eski gösterime dönülür. Döner: yerleşim başına kayıt.</summary>
        static List<object> SingleSideTexts(Function fn, Pin pin)
        {
            List<object> log = new List<object>();
            int k = pin.Index + 1;
            if (!fn.UseLocalPropertyPlacements) fn.SwitchLocalPropertyPlacements(true);
            // AnyPropertyId.ToString() yalnız tür adını verir (ölçüm 03.10 20261003b: iki yerleşim de aynı dize); hangi
            // özellik olduğu karşılaştırılamaz. Adaylar sırayla denenir, metni tek ada inen kalır, olmazsa eski gösterim.
            AnyPropertyId[] side = { fn.Properties[20022, k].Id, fn.Properties.FUNC_CONNECTIONDESCRIPTION[k].Id };
            foreach (Placement p in fn.PropertyPlacements)
            {
                PropertyPlacement text = p as PropertyPlacement;
                if (text == null) continue;
                string shown = Text(delegate { return text.GetDisplayString(); });
                string[] parts = shown.Split(';');
                if (parts.Length < 2 || parts[0] == "" || Array.Exists(parts, s => s != parts[0])) continue;
                Dictionary<string, object> e = new Dictionary<string, object>();
                e["shown"] = shown;
                try
                {
                    AnyPropertyId old = text.DisplayedProperty;
                    text.LockObject();
                    foreach (AnyPropertyId want in side)
                    {
                        text.DisplayedProperty = want;
                        e["now"] = Text(delegate { return text.GetDisplayString(); });
                        if (S(e, "now") == parts[0]) break;
                    }
                    if (S(e, "now") != parts[0]) { text.DisplayedProperty = old; e["reverted"] = true; }
                }
                catch (Exception ex) { e["error"] = ex.GetType().Name + ": " + ex.Message; }
                log.Add(e);
            }
            return log;
        }

        /// <summary>Etiketin göstereceği ÖZELLİK. Yalnız ölçülmüş, dolu dönen özellikler desteklenir:
        /// `FUNC_DEVICETAG_FULLNAME` cihazın kısa adını ('4F22', 'X1:4') verir; `FUNC_VISIBLEDEVICETAG`
        /// kesinti noktasında görünen adı (hat adı) verir. Başka ad istenirse reddedilir.</summary>
        static AnyPropertyId LabelPropertyId(SymbolReference sr, string property)
        {
            Function function = sr as Function;
            InterruptionPoint point = sr as InterruptionPoint;
            if (property == "FUNC_DEVICETAG_FULLNAME" && function != null)
                return function.Properties.FUNC_DEVICETAG_FULLNAME.Id;
            // PLC kutusunda ORTAK cihaz adı istenir: `FUNC_DEVICETAG_FULLNAME` bu fonksiyonlarda
            // bağlantı noktasını da yazıyor ('13D22:L+'), kutunun adı gibi okunuyordu (ölçüm
            // 58_gorsel_son). Aşağıdakiler ölçülmüş ve DOLU dönen cihaz kimliği özellikleridir.
            // ÖLÇÜM (03_dt_olcum2): FUNC_DEVICETAG_FULL → '13D22' / 'X1' / '17K52' (uç adı YOK);
            // FUNC_DEVICETAG_FULLNAME → '13D22:L+' / 'X1:1'. PLC kutusu ilkini, klemens ikincisini ister.
            if (property == "FUNC_DEVICETAG_FULL" && function != null)
                return function.Properties.FUNC_DEVICETAG_FULL.Id;
            // Kullanıcı 2026-09-23: ad başında '-' ile ('-1F23', '-X1'). ÖLÇÜM (03_dt_olcum2):
            // FUNC_DEVICETAG_FULL_WITHSEPARATOR → '-3F22' (FUNC_DEVICETAG_FULL → '3F22').
            if (property == "FUNC_DEVICETAG_FULL_WITHSEPARATOR" && function != null)
                return function.Properties.FUNC_DEVICETAG_FULL_WITHSEPARATOR.Id;
            if (property == "FUNC_FULLDEVICETAG" && function != null)
                return function.Properties.FUNC_FULLDEVICETAG.Id;
            if (property == "FUNC_IDENTDEVICETAG" && function != null)
                return function.Properties.FUNC_IDENTDEVICETAG.Id;
            if (property == "FUNC_VISIBLEDEVICETAG")
            {
                if (function != null) return function.Properties.FUNC_VISIBLEDEVICETAG.Id;
                if (point != null) return point.Properties.FUNC_VISIBLEDEVICETAG.Id;
            }
            throw new InvalidOperationException("Desteklenmeyen özellik gösterimi: " + property + " / " + sr.GetType().Name);
        }

        /// <summary>Cihaz adını SAYFA GİBİ, AYRI YAPI ALANLARIYLA yazar.
        ///
        /// ÖLÇÜLEN KUSUR (2026-09-22, UVP_013.zw9 şablonu): adı tek parça olarak <c>Name</c>'e yazmak
        /// çalışmıyor. Tam ad yazıldığında geri okuma <c>-=215+E215-1F23</c> (EPLAN hepsini tanımlayıcı ad
        /// sanıyor), sayfaya göreli ad yazıldığında <c>=+-1F23</c> (yapıyı sayfadan DEVRALMIYOR).
        /// Sayfa tarafı DOĞRU çalışıyor çünkü yapı ayrı alanlarla veriliyor (bu dosyada ~1410:
        /// <c>PagePropertyList.DESIGNATION_PLANT/DESIGNATION_LOCATION</c>) — fonksiyon tarafında da aynısını yap.
        ///
        /// YANSIMAYLA DOĞRULANDI (Eplan.EplApi.DataModelu.dll 2026.0.3, tahmin değil):
        ///   FunctionBase.NameParts                                        FunctionBasePropertyList  get+set
        ///   FunctionBasePropertyList.DESIGNATION_PLANT                    PropertyValue  yazılabilir
        ///   FunctionBasePropertyList.DESIGNATION_LOCATION                 PropertyValue  yazılabilir
        ///   FunctionBasePropertyList.FUNC_IDENTDEVICETAGWITHOUTSTRUCTURES PropertyValue  yazılabilir
        /// Kalıtım zinciri: TerminalStrip &lt;- Function &lt;- FunctionBase; Terminal de Function'dan türer,
        /// yani cihaz / klemens / klemens sırası aynı yolu kullanır.
        ///
        /// Değerler YALNIZ <paramref name="tag"/>'ten (<c>device_tag</c>) gelir; UYDURMA YOK (MAIN.md §5).
        /// Yapı alanları sayfadaki gibi AYRAÇSIZ yazılır (<c>215</c>, <c>E215</c>) — Python tarafı da
        /// <c>anlage</c>/<c>einbauort</c> alanlarını ayraçsız üretir (document.py:38).
        /// <paramref name="suffix"/> klemensin uç adıdır (<c>":1"</c>); tanımlayıcı ada eklenir, böylece
        /// bugünkü <c>ad + ":" + terminal</c> davranışı korunur ve FUNC_PINORTERMINALNUMBER'a DOKUNULMAZ.
        ///
        /// Yazdıktan sonra ad GERİ OKUNUR: beklenen tam ad çıkmazsa (şablon yine ayrıştırmadıysa veya
        /// ':' kabul edilmediyse) eski <c>Name</c> yoluna düşülür. Aktarım DÜŞMEZ, neden makbuza döner.</summary>
        internal static string WriteName(FunctionBase f, string tag, string suffix, string fallback)
        {
            int plus = tag.IndexOf('+');
            int dash = plus < 0 ? -1 : tag.IndexOf('-', plus + 1);
            if (!tag.StartsWith("=") || plus < 2 || dash < 0 || dash + 1 >= tag.Length)
                return NameFallback(f, fallback + suffix, "'device_tag' '=Anlage+Ort-Ad' yapısında değil: '" + tag + "'");
            // ÖLÇÜ 04.10 (YAPI.md): ad formatı (10075 vb.) <10002><10004> içerince EPLAN tam adı ('=215+E215-1F23') kendisi
            // ayrıştırır; NameParts yolu ise yalnız yapıyı tutar ('=215+E215', tanımlayıcı düşer). Önce tam ad denenir.
            string fullTry;
            try { f.Name = tag + suffix; fullTry = Text(delegate { return f.Name; }); }
            catch (Exception ex) { fullTry = ex.GetType().Name + ": " + ex.Message; }
            if (fullTry == tag + suffix || (suffix == "" && fullTry.StartsWith(tag + ":")))
                return "tam adla yazıldı: " + fullTry;
            try
            {
                FunctionBasePropertyList names = f.NameParts;
                names.DESIGNATION_PLANT = tag.Substring(1, plus - 1);
                names.DESIGNATION_LOCATION = tag.Substring(plus + 1, dash - plus - 1);
                names.FUNC_IDENTDEVICETAGWITHOUTSTRUCTURES = tag.Substring(dash + 1) + suffix;
                f.NameParts = names;
            }
            catch (Exception ex) { return NameFallback(f, fallback + suffix, ex.GetType().Name + ": " + ex.Message); }
            string got = Text(delegate { return f.Name; });
            // Cihaz bağlantı noktasında EPLAN adı kendisi ':uç' ile tamamlar (DT + ':' + uç): o da doğru yazımdır.
            if (got != tag + suffix && !(suffix == "" && got.StartsWith(tag + ":")))
                return NameFallback(f, fallback + suffix, "yapı alanları yazıldı ama ad '" + got + "' okundu, beklenen '" + tag + suffix + "'; tam ad denemesi '" + fullTry + "'");
            return "yapı alanlarıyla yazıldı: " + got;
        }

        /// <summary>Eski yol: adı tek parça yaz. Hata yükselirse çağıranın bugünkü davranışı sürer.</summary>
        static string NameFallback(FunctionBase f, string name, string why)
        {
            f.Name = name;
            return "eski yola düşüldü (" + why + "); yazılan: '" + name + "'";
        }

        /// <summary>Klemensin bağlanacağı klemens SIRASI (Klemmenleistendefinition). EPLAN veri modelinde
        /// klemensler bir <c>TerminalStrip</c>'in YARDIMCI fonksiyonlarıdır (API belgesi: "Usually a few
        /// Terminals are related to a single TerminalStrip as their auxiliary functions"); sıra nesnesi yoksa
        /// klemens grubu da oluşmaz — bug.md md.3, ölçüm: 14/14 klemens sırasız, projede `-X1` sırası YOK.
        /// Ad daima paketteki `device_tag`'tir; UYDURMA YOK, `device_tag` boşsa sıra kurulmaz.
        /// Aktarım SAYFA SAYFA ilerler: aynı ad için bu koşuda kurulan önbellekten, önceki koşudan/sayfadan
        /// kalan ise <c>DMObjectsFinder.GetTerminalStrips</c> ile bulunur. MEVCUT SIRA YALNIZ KULLANILIR,
        /// özellikleri (ad dahil) DEĞİŞTİRİLMEZ. Kurulamazsa null döner: çağıran eski sırasız yola düşer,
        /// neden makbuza yazılır (MAIN.md: sessiz düşme yok, tek nesne yüzünden sayfa kaybedilmez).</summary>
        static TerminalStrip Strip(Context c, string tag, string relative, Dictionary<string, object> row)
        {
            if (tag == "")
            {
                row["terminal_strip"] = "yaratılmadı: pakette 'device_tag' yok";
                return null;
            }
            TerminalStrip strip;
            if (c.Strips.TryGetValue(tag, out strip))
            {
                row["terminal_strip"] = "mevcut kullanıldı: " + tag;
                return strip;
            }
            try
            {
                FunctionsFilter filter = new FunctionsFilter();
                filter.Name = tag;
                filter.ExactNameMatching = true;
                TerminalStrip[] found = new DMObjectsFinder(c.Project).GetTerminalStrips(filter);
                if (found != null && found.Length > 0) strip = found[0];
            }
            catch (Exception ex) { row["terminal_strip_find_error"] = ex.Message; }
            if (strip != null) row["terminal_strip"] = "mevcut kullanıldı: " + tag;
            else
            {
                try
                {
                    strip = new TerminalStrip();
                    strip.Create(c.Page);          // sıra tanımı sembol istemez (tek parametreli Create)
                    c.Created.Add(strip);
                    // Sıra adı da cihazlarla AYNI yolu izler: yapı alanları tutmazsa sayfaya göreli ada düşer,
                    // yoksa sıra "-=215+E215-X1" diye boş ayraçlı çıkıyordu (ölçüm 2026-09-22).
                    row["terminal_strip_name"] = WriteName(strip, tag, "", relative != "" ? relative : tag);
                    row["terminal_strip"] = "yaratıldı: " + tag;
                }
                catch (Exception ex)
                {
                    row["terminal_strip"] = "yaratılamadı: " + ex.Message;
                    return null;
                }
            }
            c.Strips[tag] = strip;
            return strip;
        }

        static void PlaceDevice(Context c, Dictionary<string, object> o, Dictionary<string, object> row, bool useMacros)
        {
            string family = S(o, "family"), id = S(o, "id"), tag = S(o, "device_tag");
            Dictionary<string, object> entry = Entry(c, family);
            // MAIN 03.10 akşam: uç eşlemeli ürün kodlu cihazın EPLAN pencere makrosu. Konamazsa (makbuzda neden)
            // aşağıdaki parçadan cihaz yolu sürer: paketin uç noktası makro ucunun yeridir, teller yine buluşur.
            Dictionary<string, object> macro = o.ContainsKey("macro") && o["macro"] is Dictionary<string, object> ? D(o["macro"]) : null;
            if (macro != null && S(macro, "placement") == "MACRO_FROM_PART" && PlaceFromMacro(c, o, macro, row)) return;
            if (S(entry, "placement") == "DEVICE_FROM_PART")
            {
                if (S(o, "part_number") != "")
                {
                    PlaceFromPart(c, o, entry, row);
                    return;
                }
                row["part_note"] = "Ürün kodu kesin değil (" + S(o, "part_decision") + "); makrosuz sembol yolu, ürün uydurulmadı.";
            }
            ArrayList pins = A(o["pins"]);
            if (pins.Count == 0) throw new InvalidOperationException("Cihazın ucu yok.");
            SymbolVariant sv;
            int firstIndex;
            string note;
            // Sembol/varyant/bağlantı noktası kararı PAKETTEN gelir (Python önizlemesiyle AYNI karar).
            // Host kararı yalnız DOĞRULAR; uymazsa sessizce başka sembol koymaz, nesneyi reddeder.
            Dictionary<string, object> planned = Planned(o);
            if (planned != null)
            {
                sv = VariantFromPackage(c.Project, planned, out firstIndex, out note);
                row["symbol"] = note;
                if (sv == null) throw new InvalidOperationException("Paket yerleşim kararı hostta doğrulanmadı: " + note);
            }
            else if (pins.Count == 1)
            {
                string direction = S(D(pins[0]), "wire_direction");
                sv = VariantFacing(c.Project, S(entry, "library"), S(entry, "symbol"), direction, -1, out firstIndex, out note);
                row["symbol"] = note;
                if (sv == null) throw new InvalidOperationException(note);
            }
            else
            {
                sv = MappedVariant(c.Project, entry);
                firstIndex = 0;
                row["symbol"] = S(entry, "library") + "/" + S(entry, "symbol") + " #" + sv.VariantNr;
            }
            if (pins.Count > 1)
            {
                PinBase[] cps = sv.ConnectionPoints;
                if (cps == null || cps.Length != pins.Count)
                    throw new InvalidOperationException("Uç sayısı (" + pins.Count + ") sembol bağlantı noktası sayısıyla uyuşmuyor.");
                PointD p0 = P(D(pins[0])["point_mm"]);
                for (int i = 1; i < pins.Count; i++)
                {
                    PointD pi = P(D(pins[i])["point_mm"]);
                    if (!SameDirection(pi.X - p0.X, pi.Y - p0.Y, cps[i].Location.X - cps[0].Location.X, cps[i].Location.Y - cps[0].Location.Y))
                        throw new InvalidOperationException("Uç sırası sembol yönüyle doğrulanmadı; dönüş/ayna uydurulmadı.");
                }
            }
            string writeName = S(o, "name_on_page");
            if (writeName == "") writeName = tag;
            Function f;
            if (family == "terminal")
            {
                TerminalStrip strip = Strip(c, tag, writeName, row);
                Terminal t = new Terminal();
                if (strip == null) t.Create(c.Page, sv);       // sıra kurulamadı: eski yol, neden makbuzda
                else
                {
                    try { t.Create(strip, c.Page, sv); }
                    catch (Exception ex)
                    {
                        // Sıraya BAĞLI yaratma gerçek EPLAN'da ölçülmedi. Reddedilirse sıranın bütün
                        // klemensleri birden düşmesin diye sırasız kurulur; iki alan da makbuzda kalır.
                        row["terminal_strip_bind_error"] = ex.GetType().Name + ": " + ex.Message;
                        t = new Terminal();
                        t.Create(c.Page, sv);
                    }
                }
                f = t;
            }
            else
            {
                f = new Function();
                f.Create(c.Page, sv);
            }
            c.Created.Add(f);
            // Ad AYRI YAPI ALANLARIYLA yazılır (WriteName): kaynak daima `device_tag`, kısaltmayı EPLAN
            // sayfa yapısına bakarak kendi yapar. `name_on_page` yalnız GERİ DÜŞME dizesidir — yapı
            // alanları yazılamazsa bugünkü tek parça `Name` yolu aynen kullanılır.
            string suffix = family == "terminal" ? ":" + S(o, "terminal") : "";
            row["device_name"] = WriteName(f, tag, suffix, writeName);
            row["name_written"] = f.Name;
            // bug.md md.4 (kullanıcı, 2026-09-22): klemensin "Tanımlayıcı metni" kendi adı olsun (L1/L2/N/PE/1).
            // Alan = FUNC_PINORTERMINALNUMBER #20030 ("Klemens / pin tanımlayıcı metni"), yazılabilir.
            // YALNIZ klemens; YALNIZ alan BOŞsa (MAIN.md/EPLAN_ADDIN_PLAN §4: dolu alan ezilmez — EPLAN'ın
            // Name'den türettiği değer de ezilmez); değer daima paketteki 'terminal', uydurma yok.
            if (family == "terminal")
            {
                string want = S(o, "terminal");
                string have = Text(delegate { return f.Properties.FUNC_PINORTERMINALNUMBER.ToString(); });
                if (have != "") row["terminal_designation"] = "yazılmadı, zaten dolu: " + have;
                else if (want == "") row["terminal_designation"] = "yazılmadı: pakette 'terminal' alanı boş";
                else
                    try { f.Properties.FUNC_PINORTERMINALNUMBER = want; row["terminal_designation"] = "yazıldı: " + want; }
                    catch (Exception ex) { row["terminal_designation"] = "yazılamadı: " + ex.Message; }
            }
            // Çakışma listesi TAM ETİKETLE kurulur (bu dosyada ~1372); karar da etikete göre verilir.
            // `f.Name` EPLAN'ın çözdüğü/kısalttığı hâldir; ona bakmak yardımcı gösterim kararını kaydırır.
            bool auxiliary = c.Auxiliary.Contains(tag + suffix);
            bool customer = c.CustomerDevices.Contains(tag);   // 24.10: ana fonksiyon + parça müşterinin yerleşmemiş fonksiyonunda
            if (IsFalse(o, "main_function") || auxiliary || customer)
            {
                try
                {
                    f.IsMainFunction = false;
                    row["main_function"] = false;
                    if (auxiliary) row["auxiliary_display_of"] = tag + suffix;   // aynı cihazın başka sayfadaki gösterimi
                    if (customer) row["customer_device"] = tag;
                }
                catch (Exception ex) { row["main_function_error"] = ex.Message; }
            }
            f.Location = InsertionFor(sv, firstIndex, P(D(pins[0])["point_mm"]));
            WriteArticle(c, f, o, row);
            // DCP'nin uç adı aşağıdaki döngüde yazılır; uç adı yazısını taşıyan etiket (pin_text) ondan SONRA (03.10).
            bool dcpText = family == "device_connection_point";
            PlaceLabels(c, f, o, row, null, dcpText ? "pin_text" : null);          // cihaz adı çizimde GÖRÜNÜR olsun
            row["object_id"] = Id(f);
            row["name_written"] = f.Name;
            List<object> pinLog = new List<object>();
            for (int i = 0; i < pins.Count; i++)
            {
                Dictionary<string, object> pr = D(pins[i]);
                string name = S(pr, "name");
                int index = pins.Count == 1 ? firstIndex : i;
                Dictionary<string, object> pl = new Dictionary<string, object>();
                pl["pin"] = name;
                pl["connection_point"] = index;
                // Aynı adlı iki uçlu sembolde (FI N giriş/çıkış) paket adı tekildir ('N@2'), EPLAN'a `designation` yazılır.
                string designation = S(pr, "designation");
                if (designation == "") designation = name;
                else pl["designation"] = designation;
                // Ölçüm (P2 04.10): sembolün/fonksiyon tanımının kendi uç adı, paket adı yazılmadan önce.
                pl["symbol_default_pin"] = Text(delegate { return f.Properties[20022, index + 1].ToString(); });
                try { f.Properties[20022, index + 1] = designation; }
                catch (Exception ex) { pl["designation_error"] = ex.Message; }
                Pin actual = null;
                foreach (Pin candidate in f.Pins) if (candidate.Index == index) actual = candidate;
                if (actual == null) throw new InvalidOperationException("Fonksiyonda bağlantı noktası " + index + " yok.");
                pl["eplan_pin"] = actual.Name;
                CheckDeviation(c, id + "#" + name, f, Absolute(f, actual), P(pr["point_mm"]), pl);
                MapPin(c, id + "#" + name, f, actual.Name);
                StampSource(c, id, f, row);
                pinLog.Add(pl);
            }
            row["pins"] = pinLog;
            // EK 12.10 (=180 -1X12): iki bağlantı noktalı DCP (paket kuralı DCP_THROUGH, iki zıt yöne tel) TEK uçtur; öbür
            // noktaya da aynı uç adı yazılır (boş kalınca EPLAN adı ';L2' basıyor, fonksiyon adı ':uç'suz kalıyordu).
            Dictionary<string, object> plan = Planned(o);
            if (pins.Count == 1 && plan != null && S(plan, "rule") == "DCP_THROUGH")
            {
                string same = S(D(pins[0]), "designation") != "" ? S(D(pins[0]), "designation") : S(D(pins[0]), "name");
                List<object> through = new List<object>();
                foreach (Pin other in f.Pins)
                {
                    if (other.Index == firstIndex) continue;
                    try { f.Properties[20022, other.Index + 1] = same; through.Add(other.Index); }
                    catch (Exception ex) { row["through_pin_error"] = ex.Message; }
                }
                row["through_pins"] = through;
                row["through_text"] = SingleThroughText(f, same);
            }
            if (dcpText) PlaceLabels(c, f, o, row, "pin_text", null);
        }

        /// <summary>EK 13.10 (=180 -1X12 'L1;L1'): DCP2 sembolü TÜM uç adlarını (';' ile) gösteren tek yazı taşır; iki noktaya
        /// aynı ad yazılınca 'L1;L1' basılıyordu. Bağlantı/ad verisine dokunulmaz; yalnız o yazının GÖSTERDİĞİ özellik ilk uç
        /// adına çevrilir. Aday yazı görünen metniyle seçilir ('ad;ad;…'); aday özellik sonrası metin tam 'ad' değilse eski
        /// özelliğe geri dönülür (yazı kaybolmaz). ÖLÇÜLMEDİ (13.10): makbuz `through_text` gerçek EPLAN'da okunacak.</summary>
        static Dictionary<string, object> SingleThroughText(Function f, string same)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            try
            {
                if (!f.UseLocalPropertyPlacements) f.SwitchLocalPropertyPlacements(true);
                List<AnyPropertyId> want = new List<AnyPropertyId>();
                try { want.Add(f.Properties.FUNC_CONNECTIONDESIGNATION1.Id); } catch (Exception) { }
                try { AnyPropertyId first = f.Properties.FUNC_CONNECTIONDESIGNATION.Id; first.Index = 1; want.Add(first); } catch (Exception) { }
                List<object> done = new List<object>();
                foreach (Placement pl in f.PropertyPlacements)
                {
                    PropertyPlacement t = pl as PropertyPlacement;
                    if (t == null) continue;
                    string shown = Text(delegate { return t.GetDisplayString(); });
                    string[] parts = shown.Split(';');
                    if (parts.Length < 2 || Array.Exists(parts, delegate (string x) { return x != same; })) continue;
                    AnyPropertyId was = t.DisplayedProperty;
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["before"] = shown;
                    e["property_before"] = Text(delegate { return was.AsInt + "[" + was.Index + "]"; });
                    foreach (AnyPropertyId id in want)
                    {
                        try { t.DisplayedProperty = id; } catch (Exception ex) { e["error_" + id.AsInt] = ex.Message; continue; }
                        string now = Text(delegate { return t.GetDisplayString(); });
                        if (now == same) { e["after"] = now; e["property_after"] = id.AsInt + "[" + id.Index + "]"; break; }
                        e["tried_" + id.AsInt + "_" + id.Index] = now;
                    }
                    if (!e.ContainsKey("after")) { t.DisplayedProperty = was; e["after"] = Text(delegate { return t.GetDisplayString(); }); e["reverted"] = true; }
                    done.Add(e);
                }
                r["placements"] = done;
            }
            catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; }
            return r;
        }

        /// <summary>Artikel (Fable ortak 6): müşteri malzeme/etiket listesinden TEK kodu kesinleşen sıradan cihaz fonksiyonuna
        /// EPLAN parça referansı (Function.AddArticleReference). Kod parça DB'de tek parçaya çözülmezse (yok/çok aday), aday
        /// belirsizse (AMBIGUOUS) ya da fonksiyon parça taşıyamıyorsa (yardımcı gösterim) YAZILMAZ; neden makbuz satırında.
        /// Artikel hatası nesneyi reddetmez: elektrik nesnesi doğru, eksik olan bilgi.</summary>
        static void WriteArticle(Context c, Function f, Dictionary<string, object> o, Dictionary<string, object> row)
        {
            string number = S(o, "part_number"), source = S(o, "part_source");
            if (c.CustomerDevices.Contains(S(o, "device_tag")))
            {
                // 24.10: parça müşterinin cihazında zaten var (BMK + ürün kodu importu); ikinci kez yazılırsa BOM çift (ölçüm E1).
                // Kod farklıysa müşterinin kodu kalır ama sessiz kalmaz: CUSTOMER_DEVICE_PART_DIFFERS → sayfada kırmızı işaret (B2).
                HashSet<string> have = c.CustomerParts[S(o, "device_tag")];
                Dictionary<string, object> cart = new Dictionary<string, object> { { "part_number", number }, { "written", false },
                    { "reason", "CUSTOMER_DEVICE_PART" }, { "customer_parts", new List<string>(have).ToArray() } };
                if (c.SiblingDevices.Contains(S(o, "device_tag"))) cart["sibling_device"] = true;   // parça önceki UVP paketinde (kardeş Anlage)
                if (c.DeviceListTags.Contains(S(o, "device_tag"))) cart["device_list"] = true;      // parça bu aktarımda cihaz listesine yazıldı (B)
                row["article"] = cart;
                if (number == "") return;
                try
                {
                    string rule;
                    MDPart part = MacroDumpAction.FindPart(c.PartsDb, number, c.Log, out rule);
                    if (part != null && !have.Contains(part.PartNr)) { cart["reason"] = "CUSTOMER_DEVICE_PART_DIFFERS"; cart["part_nr"] = part.PartNr; }
                }
                catch (Exception ex) { cart["compare_error"] = ex.GetType().Name + ": " + ex.Message; }
                return;
            }
            // UVP_RULE: UVP'nin kendi seçtiği parça (24V dağıtım klemensi WEI.1521690000; MAIN 03.10 gece md.7).
            if (number == "" || S(o, "part_decision") == "AMBIGUOUS"
                || (source != "DOCUMENT_PARTS_LIST" && source != "LABEL_LIST" && source != "UVP_RULE"))
            {
                WritePartText(c, f, o, row, "NO_PART_DECISION_TEXT");
                return;
            }
            Dictionary<string, object> art = new Dictionary<string, object>();
            art["part_number"] = number;
            art["source"] = source;
            art["written"] = false;
            row["article"] = art;
            try
            {
                string rule;
                int logMark = c.Log.Count;      // FindPart yalnız çok sonuç/hata durumunda günlüğe yazar
                MDPart part = MacroDumpAction.FindPart(c.PartsDb, number, c.Log, out rule);
                // Yardımcı fonksiyon (kontaktörün güç/yardımcı kontağı) parça taşımaz; parça ana fonksiyona (bobin) yazılır.
                // `part_in_db`: kod EPLAN'da var → bu sayfadaki "EPLAN'DA BULUNAMADI" işareti yanlış olurdu (Kontrol atlar).
                if (!f.CanHaveArticleData) { art["reason"] = "NOT_MAIN_FUNCTION"; art["part_in_db"] = part != null; return; }
                if (part == null)
                {
                    art["reason"] = c.PartsDb == null ? "PARTS_DB_UNAVAILABLE" : c.Log.Count > logMark ? "PART_AMBIGUOUS_OR_ERROR" : "PART_NOT_IN_DB";
                    // Kullanıcı 12.10 17:05: DB'de olmayan kod parça olarak YAZILMAZ (Function.AddArticleReference yalnız DB'deki
                    // parçayı ekler, API belgesi); kod cihaza metin olarak konur.
                    if (S(art, "reason") == "PART_NOT_IN_DB") WritePartText(c, f, o, row, "PART_NOT_IN_DB_TEXT");
                    return;
                }
                string variant = part.Variant ?? "";
                art["part_nr"] = part.PartNr;
                art["variant"] = variant;
                art["match_rule"] = rule;
                bool present = false;
                foreach (ArticleReference have in f.ArticleReferences)
                    if (have.PartNr == part.PartNr) present = true;
                if (present) art["reason"] = "ALREADY_PRESENT";
                else
                {
                    if (variant == "") f.AddArticleReference(part.PartNr);
                    else f.AddArticleReference(part.PartNr, variant, 1);
                    art["written"] = true;
                }
                WriteAccessories(c, f, o, art);
            }
            catch (Exception ex) { art["reason"] = "ERROR"; art["error"] = ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>Kullanıcı 12.10 17:30: makrosuz/sembolsüz çok uçlu kutu cihazı (paket sayfa `black_boxes`, Python
        /// eplan_export._black_boxes) EPLAN kara kutusu (BoxedDevice): dikdörtgen uç noktalarının çevresinde, adlı uçlar (DCP)
        /// kenarında, ad cihazın DT'si. Kara kutunun bağlantı noktası yok: bağ/ağ değişmez. Boyut RECTANGLE_WIDTH/HEIGHT
        /// (# 20221/20222; yükseklik eksi girilir); konum geri okunan kutuyla bir kez düzeltilir. Konamazsa DCP'ler + uyarı
        /// yazısı kalır; her satır makbuz `black_boxes`.</summary>
        static void PlaceBlackBoxes(Context c, Page pg, Dictionary<string, object> pd, string sub, Dictionary<string, object> receipt)
        {
            foreach (object bo in A(pd.ContainsKey("black_boxes") ? pd["black_boxes"] : null))
            {
                Dictionary<string, object> b = D(bo);
                if (S(b, "subpage") != sub) continue;
                string tag = S(b, "device_tag");
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["device_tag"] = tag;
                r["page"] = Text(delegate { return pg.Name; });
                if (!receipt.ContainsKey("black_boxes")) receipt["black_boxes"] = new List<object>();
                ((List<object>)receipt["black_boxes"]).Add(r);
                if (S(b, "skipped") != "") { r["skipped"] = S(b, "skipped"); continue; }
                int mark = c.Created.Count;
                try
                {
                    ArrayList rect = A(b["rect_mm"]);
                    double x0 = Convert.ToDouble(rect[0]), y0 = Convert.ToDouble(rect[1]);
                    double x1 = Convert.ToDouble(rect[2]), y1 = Convert.ToDouble(rect[3]);
                    r["rect_mm"] = rect;
                    BoxedDevice bb = new BoxedDevice();
                    bb.Create(pg);
                    c.Created.Add(bb);
                    // ÖLÇÜM 12.10 17:48 (E180_TAM): RECTANGLE_WIDTH/HEIGHT (# 20221/20222) kara kutuyu büyütmedi (2×2 mm kaldı).
                    // Kutu alanı SymbolReference.SetLogicalArea ile (API: "rectangular logical area"; PLC/kara kutu).
                    bb.Location = new PointD(x0, y1);                 // sol üst köşe
                    bb.SetLogicalArea(new PointD(x0, y0), new PointD(x1, y1));
                    r["name"] = WriteName(bb, tag, "", "-" + tag.Substring(tag.LastIndexOf('-') + 1));
                    if (c.CustomerDevices.Contains(tag)) r["customer_device"] = DropCustomerParts(new Function[] { bb });   // 24.10
                    r["object_id"] = Id(bb);
                    try { RectangleD la = bb.GetLogicalArea(); r["logical_area_read"] = Readback.Box(new PointD[] { la.LowerLeft, la.UpperRight }); } catch (Exception ex) { r["logical_area_error"] = ex.Message; }
                    // ÖLÇÜM 12.10 17:50: mantıksal alan konuyor (geri okuma birebir) ama PDF'te çizgi YOK; görünür kenar ayrı
                    // dikdörtgen, kara kutuyla gruplu (birlikte taşınır/silinir).
                    Eplan.EplApi.DataModel.Graphics.Rectangle edge = new Eplan.EplApi.DataModel.Graphics.Rectangle();
                    edge.Create(pg);
                    c.Created.Add(edge);
                    edge.SetArea(new PointD(x0, y0), new PointD(x1, y1));
                    r["edge_bbox_read"] = Readback.Box(edge.GetBoundingBox());
                    try { new Eplan.EplApi.DataModel.Group().Create(new Placement[] { bb, edge }); r["grouped"] = true; }
                    catch (Exception ex) { r["group_error"] = ex.Message; }
                    r["bbox_read"] = Readback.Box(bb.GetBoundingBox());
                    r["written"] = true;
                }
                catch (Exception ex)
                {
                    r["written"] = false;
                    r["error"] = ex.GetType().Name + ": " + ex.Message;
                    r["partial_removed"] = RemoveSince(c, mark, "blackbox:" + tag);
                }
            }
        }

        /// <summary>Kullanıcı 12.10 17:05: EPLAN'a parça olarak yazılamayan ürün kodu (paket `part_text` = 'ÜRETİCİ KOD', Stückliste
        /// Hersteller-Teilenummer) cihazın ana fonksiyonuna (DCP cihazda ilk bağlantı noktasına) METİN olarak: Teknik özellik
        /// FUNC_TECHNICAL_CHARACTERISTIC #20027. Cihaz başına bir kez; dolu alan ezilmez. Makbuz `part_text`.</summary>
        static void WritePartText(Context c, Function f, Dictionary<string, object> o, Dictionary<string, object> row, string reason)
        {
            string text = S(o, "part_text"), tag = S(o, "device_tag");
            if (text == "" || c.PartText.Contains(tag)) return;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["text"] = text;
            r["reason"] = reason;
            r["written"] = false;
            try
            {
                if (!f.IsMainFunction && S(o, "family") != "device_connection_point") return;
                row["part_text"] = r;
                string have = Text(delegate { return f.Properties.FUNC_TECHNICAL_CHARACTERISTIC.ToString(); });
                if (have != "") { r["skipped"] = "alan dolu: " + have; return; }
                f.Properties.FUNC_TECHNICAL_CHARACTERISTIC = text;
                r["written"] = true;
                c.PartText.Add(tag);
                // EK 12.10 (=180 -1Q21): sembolün #20027 gösterimi DT yazısının üstüne basılıyordu ('EATON 1Q2153'). Kod sayfada
                // kırmızı UVP yazısında (kontrol.py) ve özellikte kalır; sembol üstündeki gösterim gizlenir.
                try
                {
                    // ÖLÇÜM 12.10 18:19 (E180_TAM_e): AnyPropertyId.ToString() HER özellikte aynı döndü, 20 yazı (uç numaraları dahil)
                    // gizlendi. Karşılaştırma özellik numarasıyla (AsInt).
                    int want = f.Properties.FUNC_TECHNICAL_CHARACTERISTIC.Id.AsInt;
                    if (!f.UseLocalPropertyPlacements) f.SwitchLocalPropertyPlacements(true);
                    int hidden = 0;
                    foreach (Placement pl in f.PropertyPlacements)
                    {
                        PropertyPlacement t = pl as PropertyPlacement;
                        if (t == null) continue;
                        try { if (t.DisplayedProperty.AsInt != want) continue; } catch (Exception) { continue; }
                        t.IsSetAsVisible = Placement.Visibility.Invisible;
                        hidden++;
                    }
                    r["hidden_on_symbol"] = hidden;
                }
                catch (Exception ex) { r["hide_error"] = ex.GetType().Name + ": " + ex.Message; }
            }
            catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; row["part_text"] = r; }
        }

        /// <summary>Ana ürünün aksesuarları (paket `part_accessories`; Stückliste'de aynı BMK'nin yardımcı şalter bloğu,
        /// adaptör, varistör…; ana ürün seçimi Python main_part, 04.10): aynı ana fonksiyona EK parça referansı. Kod parça
        /// DB'de tek parçaya çözülmezse yazılmaz; her kodun sonucu makbuzda `article.accessories`. Adet 1 (Stückliste adedi okunmuyor).</summary>
        static void WriteAccessories(Context c, Function f, Dictionary<string, object> o, Dictionary<string, object> art)
        {
            ArrayList codes = A(o.ContainsKey("part_accessories") ? o["part_accessories"] : null);
            if (codes.Count == 0) return;
            List<object> log = new List<object>();
            art["accessories"] = log;
            foreach (object co in codes)
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                string code = co as string ?? "";
                r["part_number"] = code;
                r["written"] = false;
                log.Add(r);
                try
                {
                    string rule;
                    int logMark = c.Log.Count;
                    MDPart part = MacroDumpAction.FindPart(c.PartsDb, code, c.Log, out rule);
                    if (part == null) { r["reason"] = c.Log.Count > logMark ? "PART_AMBIGUOUS_OR_ERROR" : "PART_NOT_IN_DB"; continue; }
                    r["part_nr"] = part.PartNr;
                    r["match_rule"] = rule;
                    bool present = false;
                    foreach (ArticleReference have in f.ArticleReferences)
                        if (have.PartNr == part.PartNr) present = true;
                    if (present) { r["reason"] = "ALREADY_PRESENT"; continue; }
                    string variant = part.Variant ?? "";
                    if (variant == "") f.AddArticleReference(part.PartNr);
                    else f.AddArticleReference(part.PartNr, variant, 1);
                    r["written"] = true;
                }
                catch (Exception ex) { r["reason"] = "ERROR"; r["error"] = ex.GetType().Name + ": " + ex.Message; }
            }
        }

        /// <summary>MACRO_FROM_PART (MAIN 03.10 akşam): cihazın ilk nesnesi parçanın şema makrosunu paketin `anchor_mm`
        /// noktasına koyar (Insert.WindowMacro, çok hatlı gösterim); her nesne ucunu makronun `pin` adlı fonksiyonuna
        /// eşler (SourceMap: teller ve köşeler o uca bağlanır). Makro konamazsa false: çağıran parçadan cihaz yoluna düşer.</summary>
        static bool PlaceFromMacro(Context c, Dictionary<string, object> o, Dictionary<string, object> m, Dictionary<string, object> row)
        {
            string tag = S(o, "device_tag"), id = S(o, "id");
            MacroPlacement mp;
            if (!c.Macros.TryGetValue(tag, out mp))
            {
                mp = InsertMacro(c, o, m);
                c.Macros[tag] = mp;
            }
            row["macro"] = S(m, "path");
            if (mp.Error != null)
            {
                row["macro_fallback"] = mp.Error;
                if (!mp.Reported) { row["macro_log"] = mp.Log; mp.Reported = true; }
                return false;
            }
            Dictionary<string, object> pr = D(A(o["pins"])[0]);
            // `eplan_pin`: EPLAN'daki gerçek ad; `pin` paket adı ('L(2)': makroda aynı adlı uçların ikincisi, MAIN 03.10 gece).
            string want = S(pr, "name"), partPin = S(m, "eplan_pin") != "" ? S(m, "eplan_pin") : S(m, "pin");
            PointD target = P(pr["point_mm"]);
            Function fn;
            if (!mp.ByPin.TryGetValue(partPin, out fn))
            {
                // Aynı adlı makro uçları: paketin istediği konuma (makro kökü + ölçülmüş uç) en yakın olan, ≤ 1 mm.
                double near = double.MaxValue;
                foreach (Function f in mp.Functions)
                {
                    if (SinglePinName(f) != partPin) continue;
                    foreach (Pin q in f.Pins)
                    {
                        if (q.Name != partPin) continue;
                        PointD at = Absolute(f, q);
                        double dist = Math.Abs(at.X - target.X) + Math.Abs(at.Y - target.Y);
                        if (dist < near) { near = dist; fn = f; }
                    }
                }
                if (fn == null || near > 1.0)
                    throw new InvalidOperationException("Makroda '" + S(m, "pin") + "' ucu yok (makro konduktan sonra; nesne reddedildi).");
            }
            Pin pin = null;
            double best = double.MaxValue;
            foreach (Pin q in fn.Pins)
            {
                if (q.Name != partPin) continue;
                PointD at = Absolute(fn, q);
                double dist = Math.Abs(at.X - target.X) + Math.Abs(at.Y - target.Y);
                if (dist < best) { best = dist; pin = q; }
            }
            if (pin == null) throw new InvalidOperationException("Makro fonksiyonunda '" + partPin + "' adlı uç yok.");
            row["source"] = "MACRO_FROM_PART";
            row["part_number"] = S(o, "part_number");
            row["object_id"] = Id(fn);
            row["name_written"] = Text(delegate { return fn.Name; });
            Dictionary<string, object> pl = new Dictionary<string, object>();
            pl["pin"] = want;
            pl["eplan_pin"] = pin.Name;
            pl["part_pin"] = partPin;
            pl["match"] = "USER_PIN_MAP_MACRO";
            pl["function_definition"] = Definition(fn);
            pl["function_pins"] = fn.Pins.Length;
            CheckDeviation(c, id + "#" + want, fn, Absolute(fn, pin), target, pl);
            MapPin(c, id + "#" + want, fn, pin.Name);
            StampSource(c, id, fn, row);
            row["pins"] = new List<object> { pl };
            if (!mp.Reported)
            {
                // Makronun bütün fonksiyonları (doğrulayıcı makro içi bağları bunlardan tanır) cihazın ilk satırında.
                row["macro_functions"] = mp.Ids;
                row["macro_objects"] = mp.ObjectIds;
                row["macro_anchor_mm"] = A(m["anchor_mm"]);
                row["macro_articles"] = mp.Articles;
                row["macro_log"] = mp.Log;
                mp.Reported = true;
            }
            return true;
        }

        /// <summary>B13 10.10: paket makro ucunu 0,001 mm'ye yuvarlanmış ölçümle planlar (kök + uç ofseti, ikisi de 3 ondalık);
        /// makronun kendi koordinatı ise kesirli olabilir (SCALANCE 6GK5208: PLC uçları x = 88,033589922847 → sayfada 217,99959,
        /// paket 218). EPLAN oto-bağı bu 0,0004 mm kaymada teli ÇİZMİYOR (ölçüm 20261010_B13_deney3: aynı ok tam x'te bağlandı,
        /// 218'de bağlanmadı). Makronun fonksiyonları tel ekseninde yuvarlama artığı kadar kaydırılır: dikey uçların (Up/Down)
        /// x artığı, yatay uçların y artığı. Bir eksende artıklar farklıysa o eksen kaydırılmaz, makbuza yazılır.</summary>
        static void AlignMacroPins(MacroPlacement mp)
        {
            List<double> rx = new List<double>(), ry = new List<double>();
            foreach (Function f in mp.Functions)
                foreach (Pin pin in f.Pins)
                {
                    PointD p = Absolute(f, pin);
                    string way = pin.Direction.ToString();
                    if (way == "Up" || way == "Down") rx.Add(Math.Round(p.X, 3) - p.X);
                    else if (way == "Left" || way == "Right") ry.Add(Math.Round(p.Y, 3) - p.Y);
                }
            double dx = Residual(rx, "x", mp.Log), dy = Residual(ry, "y", mp.Log);
            if (dx == 0 && dy == 0) return;
            foreach (Function f in mp.Functions) f.Location = new PointD(f.Location.X + dx, f.Location.Y + dy);
            mp.Log.Add("MACRO_PINS_ALIGNED: fonksiyonlar " + dx.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
                       + dy.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " mm kaydırıldı (uçlar paketin 0,001 mm noktasına)");
        }

        /// <summary>Bir eksenin ortak yuvarlama artığı; artık yoksa ya da uçlar arasında farklıysa 0.</summary>
        static double Residual(List<double> all, string axis, List<string> log)
        {
            if (all.Count == 0) return 0;
            foreach (double r in all)
                if (Math.Abs(r - all[0]) > 1e-9)
                {
                    log.Add("MACRO_PIN_RESIDUAL_MIXED: " + axis + " ekseninde uç artıkları farklı; kaydırılmadı");
                    return 0;
                }
            return Math.Abs(all[0]) > 1e-12 ? all[0] : 0;
        }

        /// <summary>Makro DT yazısı yazılamadı: makbuzda 'Yazı yerleşim hatası' sayacına ve `dt_label_errors` listesine girer.</summary>
        const string DtLabelError = "DT yazısı yazılamadı: ";

        static MacroPlacement InsertMacro(Context c, Dictionary<string, object> o, Dictionary<string, object> m)
        {
            MacroPlacement mp = new MacroPlacement();
            string path = S(m, "path"), tag = S(o, "device_tag");
            StorableObject[] placed = null;
            try
            {
                PointD at = P(m["anchor_mm"]);
                // Makro birden çok gösterim taşır; şema sayfasına çok hatlı (MultiLine) konur (Macros.cs ölçümüyle aynı yol).
                bool multi = false;
                try
                {
                    Eplan.EplApi.DataModel.MasterData.WindowMacro probe = new Eplan.EplApi.DataModel.MasterData.WindowMacro();
                    probe.Open(path, c.Project);
                    foreach (Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType type in probe.RepresentationTypes)
                        if (type == Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType.MultiLine) multi = true;
                }
                catch (Exception ex) { mp.Log.Add("gösterim türleri okunamadı: " + ex.Message); }
                placed = multi
                    ? new Insert().WindowMacro(path, Eplan.EplApi.DataModel.MasterData.WindowMacro.Enums.RepresentationType.MultiLine,
                                               0, c.Page, at, Insert.MoveKind.Absolute)
                    : new Insert().WindowMacro(path, 0, c.Page, at, Insert.MoveKind.Absolute);
                if (placed == null || placed.Length == 0) throw new InvalidOperationException("makro hiçbir nesne koymadı");
                mp.Log.Add("makro kondu: " + path + " @" + at.X + "," + at.Y + " (" + (multi ? "MultiLine" : "Default") + ", "
                           + placed.Length + " nesne)");
                Dictionary<string, List<Function>> byName = new Dictionary<string, List<Function>>();
                foreach (StorableObject so in placed)
                {
                    c.Created.Add(so);
                    mp.ObjectIds.Add(Id(so));
                    Function f = so as Function;
                    if (f == null) continue;
                    mp.Functions.Add(f);
                    mp.Ids.Add(Id(f));
                    string single = SinglePinName(f);
                    if (single == "") continue;
                    if (!byName.ContainsKey(single)) byName[single] = new List<Function>();
                    byName[single].Add(f);
                }
                AlignMacroPins(mp);
                // Paketin istediği her parça ucu makroda TEK fonksiyonla bulunmalı; yoksa makro bütünüyle geri alınır.
                foreach (object po in A(m.ContainsKey("device_pins") ? m["device_pins"] : null))
                {
                    string name = Convert.ToString(po);
                    if (!byName.ContainsKey(name))
                        throw new InvalidOperationException("makroda '" + name + "' ucu yok");
                    // Aynı adlı uçlar (lamba L/N/PE giriş+çıkış): ad seçmez; PlaceFromMacro konumla seçer.
                    if (byName[name].Count == 1) mp.ByPin[name] = byName[name][0];
                }
                // Cihaz adı (DT) makronun BÜTÜN fonksiyonlarına: yapı alanlarıyla; tutmazsa sayfaya göreli kısa ad (WriteName).
                string shown = S(o, "name_on_page") != "" ? S(o, "name_on_page") : tag;
                foreach (Function f in mp.Functions)
                {
                    string before = Text(delegate { return f.Name; });
                    try { mp.Log.Add("ad " + before + " -> " + WriteName(f, tag, "", shown)); }
                    catch (Exception ex) { mp.Log.Add("ad yazılamadı (" + before + "): " + ex.Message); }
                }
                // Ölçüm 03.10 akşam (20261003e, s6 -3F54): makronun DT yazısı ön rakamsız gösterimdi ('-F54'); EPLAN adı
                // doğru ('-3F54', ön rakam 3). O yazının gösterdiği özellik FUNC_DEVICETAG_FULL_WITHSEPARATOR ('-3F22' ölçüm
                // 03_dt_olcum2) yapılır (yeni yazı eklenmez, yeri değişmez).
                string tail = tag.Substring(tag.LastIndexOf('-') + 1), bare = "-" + tail.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (bare != "-" + tail)
                    foreach (Function f in mp.Functions)
                    {
                        try
                        {
                            // Ölçüm 08.10 (=280 Blatt 7 -7K28/-7K29, SIE.3UG4622): örnek yerel yerleşim kullanmıyorsa yazı sembol
                            // varyantının ORTAK gösterimidir (başka veritabanı, 529/45/1857/0) ve yazma LockingException veriyordu;
                            // EPLAN '-K28' gösterdi. PlaceLabels'teki çözüm: yerel kopyaya geç, yeniden ara; kütüphaneye asla yazma.
                            for (int pass = 0; pass < 2; pass++)
                            {
                                bool switched = false;
                                foreach (Placement pl in f.PropertyPlacements)
                                {
                                    PropertyPlacement pp = pl as PropertyPlacement;
                                    // Ölçüm 04.10 (P1, s6 -3G32 SITOP PSU8200): yazı '-G' gösteriyordu (VisibleName '-G', '-G32'
                                    // değil). Kısa DT yazısı fonksiyonun VisibleName'i olarak da tanınır.
                                    if (pp == null) continue;
                                    string disp = Text(delegate { return pp.GetDisplayString(); });
                                    string vis = Text(delegate { return f.VisibleName; });
                                    if ((disp != bare && (disp != vis || !vis.StartsWith("-") || vis == "-" + tail))) continue;
                                    if (Id(pp).Split('/')[0] != Id(f).Split('/')[0])
                                    {
                                        if (pass == 0 && !f.UseLocalPropertyPlacements) { f.SwitchLocalPropertyPlacements(true); switched = true; break; }
                                        mp.Log.Add(DtLabelError + Id(pp) + " sembol kütüphanesine ait (" + disp + "); yazılmadı.");
                                        continue;
                                    }
                                    pp.LockObject();
                                    pp.DisplayedProperty = LabelPropertyId(f, "FUNC_DEVICETAG_FULL_WITHSEPARATOR");
                                    mp.Log.Add("DT yazısı " + bare + " -> " + Text(delegate { return pp.GetDisplayString(); })
                                               + (switched || pass == 1 ? " (yerel kopya)" : ""));
                                }
                                if (!switched) break;
                            }
                        }
                        catch (Exception ex) { mp.Log.Add(DtLabelError + ex.GetType().Name + ": " + ex.Message); }
                    }
                // 24.10: müşteri cihazında makronun parçası ve ana fonksiyonu kaldırılır (parça müşteri fonksiyonunda).
                if (c.CustomerDevices.Contains(S(o, "device_tag"))) mp.Log.Add("müşteri cihazı: " + new JavaScriptSerializer().Serialize(DropCustomerParts(mp.Functions)));
                // Parça ataması makrodan gelir: ana fonksiyonun artikelleri kayda (yazılmaz).
                foreach (Function f in mp.Functions)
                {
                    try
                    {
                        if (!f.IsMainFunction) continue;
                        foreach (ArticleReference ar in f.ArticleReferences)
                            if (!mp.Articles.Contains(ar.PartNr)) mp.Articles.Add(ar.PartNr);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                mp.Error = "MACRO_INSERT_FAILED: " + ex.GetType().Name + ": " + ex.Message + " — parçadan cihaz yoluna düşüldü";
                int removed = 0;
                if (placed != null)
                    foreach (StorableObject so in placed)
                    {
                        Placement pl = so as Placement;
                        try { if (pl != null && pl.IsValid) { pl.Remove(); removed++; } } catch (Exception) { }
                    }
                mp.Log.Add("makro geri alındı: " + removed + " nesne silindi");
                mp.Functions.Clear();
                mp.Ids.Clear();
                mp.ObjectIds.Clear();
                mp.ByPin.Clear();
            }
            return mp;
        }

        /// <summary>Y2 (ölçüm 08.10, =280 Blatt 4/5/7): aynı sayfaya İKİNCİ makro konunca birincinin Blackbox içi bağlantı
        /// noktaları iç içe ad alıyor ('=280.280+E280.E280-7K28-7K28:IN'; aynı makro -7K29 ikinci: doğru). Yazım anındaki geri
        /// okuma doğruydu, bozulma sonraki Insert.WindowMacro'da. Bu yüzden bütün makrolardan SONRA her makro fonksiyonunun adı
        /// okunur; DT ile başlamıyorsa yeniden yazılır ve makbuza NAME_NESTED satırı düşer (önce/sonra, düzeldi mi).</summary>
        static List<object> CheckMacroNames(Context c)
        {
            List<object> rows = new List<object>();
            foreach (KeyValuePair<string, MacroPlacement> kv in c.Macros)
            {
                string tag = kv.Key;
                if (!tag.StartsWith("=")) continue;     // yapısız ad WriteName'de zaten eski yola düşer; karşılaştırılamaz
                foreach (Function f in kv.Value.Functions)
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    try
                    {
                        if (!f.IsValid) continue;              // geri alınmış makro nesnesi
                        string before = Text(delegate { return f.Name; });
                        if (before == tag || before.StartsWith(tag + ":")) continue;
                        r["flag"] = "NAME_NESTED";
                        r["device"] = tag;
                        r["function"] = Id(f);
                        r["before"] = before;
                        r["write"] = WriteName(f, tag, "", tag);
                        string after = Text(delegate { return f.Name; });
                        r["after"] = after;
                        r["fixed"] = after == tag || after.StartsWith(tag + ":");
                    }
                    catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; }
                    if (r.Count > 0) rows.Add(r);
                }
            }
            return rows;
        }

        /// <summary>Cihazı PARÇADAN kur (Cihaz Gezgini yolu) ve yalnız kaynak uçlarına karşılık gelen tek uçlu
        /// fonksiyonları yerleştir. Eşleşmeyen veya belirsiz uç → nesne REDDEDİLİR.</summary>
        static void PlaceFromPart(Context c, Dictionary<string, object> o, Dictionary<string, object> entry, Dictionary<string, object> row)
        {
            string partNumber = S(o, "part_number"), tag = S(o, "device_tag"), id = S(o, "id");
            Function[] functions;
            if (!c.DevicesFromPart.TryGetValue(tag, out functions))
            {
                MDPart part = FindPart(c.PartsDb, partNumber, c.Log);
                if (part == null) throw new InvalidOperationException("Parça bulunamadı: " + partNumber);
                functions = new DeviceService().CreateDevice(c.Project, part.PartNr, part.Variant ?? "", new FunctionPropertyList());
                if (functions == null || functions.Length == 0) throw new InvalidOperationException("Parçadan cihaz oluşmadı: " + partNumber);
                foreach (Function fn in functions)
                {
                    c.Created.Add(fn);
                    // Cihaz bağlantı noktası (DeviceEndTerminal) sayfaya konmadan adlandırılmaz: ölçüm 28.09 konmadan
                    // yazılan ad tam yapıyı cihaz adı sanıyor ('-=215+E215-2E32:L'). Adı yerleştirmeden sonra yazılır.
                    if (S(Definition(fn), "category") == "DeviceEndTerminal") continue;
                    string only = SinglePinName(fn);
                    string suffix = (fn.IsMainFunction || only == "") ? "" : ":" + only;
                    // Parçadan kurulan cihazda da ad yapı alanlarıyla yazılır; aynı kusur burada da vardı.
                    try { c.Log.Add("ad " + tag + suffix + ": " + WriteName(fn, tag, suffix, tag)); }
                    catch (Exception ex) { c.Log.Add("ad yazılamadı (" + tag + "): " + ex.Message); }
                }
                c.DevicesFromPart[tag] = functions;
                c.Log.Add("parçadan cihaz " + tag + " <- " + partNumber + " (" + functions.Length + " fonksiyon)");
                // 24.10: CreateDevice yeni cihaz başı + parça kurar; müşteri cihazında ikisi de kaldırılır (uç fonksiyonları kalır).
                if (c.CustomerDevices.Contains(tag)) row["customer_device"] = DropCustomerParts(functions);
            }
            row["source"] = "DEVICE_FROM_PART";
            row["part_number"] = partNumber;
            ArrayList pins = A(o["pins"]);
            List<object> pinLog = new List<object>();
            foreach (object po in pins)
            {
                Dictionary<string, object> pr = D(po);
                string want = S(pr, "name");
                // Kullanıcı uç eşlemesi (MAIN 02.10): paket ucun parça ucu adını `part_pin` ile verir; o zaman ad
                // eşleşmesi yapılmaz, yalnız o parça ucu (birebir ad) aranır. Alan yoksa eski yol (exact/loose).
                string partPin = S(pr, "part_pin");
                string look = partPin != "" ? partPin : want;
                List<Function> exact = new List<Function>(), loose = new List<Function>();
                foreach (Function fn in functions)
                {
                    // Tek ADLI uçlu fonksiyon: tek uçlu ya da iki taraflı tek uç (SITOP; SinglePinName). Farklı adlı
                    // çok uçlu fonksiyon (ör. kontak 13/14) bu yolda seçilmez — eski davranış.
                    string single = fn.IsPlaced ? "" : SinglePinName(fn);
                    if (single == "") continue;
                    if (single == look) exact.Add(fn);
                    else if (partPin == "" && Pins.NamesMatch(want, single)) loose.Add(fn);
                }
                List<Function> candidates = exact.Count > 0 ? exact : loose;
                // Aynı adlı uçlar birbirinin AYNISIYSA (aynı ad + fonksiyon tanımı) hangisine konduğu elektriksel
                // sonucu değiştirmez; şablon sırasındaki ilk boştaki alınır. Ölçüm 28.09 RIT.2500210 (lamba):
                // giriş ve devam L/N/PE takımı, 8 fonksiyonun hepsi DeviceEndTerminal 1/1.
                bool interchangeable = candidates.Count > 1 && Interchangeable(candidates);
                if (candidates.Count != 1 && !interchangeable)
                {
                    if (candidates.Count > 1)
                    {
                        Dictionary<string, object> amb = new Dictionary<string, object>();
                        amb["node"] = id + "#" + want;
                        List<string> names = new List<string>();
                        foreach (Function fn in candidates) names.Add(fn.Name);
                        amb["candidates"] = names;
                        c.Ambiguous.Add(amb);
                    }
                    throw new InvalidOperationException(candidates.Count == 0
                        ? "Parçada '" + look + "' ucu yok; cihaz başarılı sayılmadı."
                        : "Parçada '" + look + "' için " + candidates.Count + " aday; seçilmedi.");
                }
                Function chosen = candidates[0];
                string io = IoKind(chosen);
                string deviceIo = S(o, "io");
                string problem = RoleProblem(chosen, entry, io != "" ? io : deviceIo);
                if (problem != null) throw new InvalidOperationException("Fonksiyon rolü uyuşmuyor (" + want + "): " + problem);
                string role = S(pr, "box_role");
                string symbolName = role == "first" ? S(entry, "symbol_group_first") : role == "extra" ? S(entry, "symbol_group_extra") : "";
                if (symbolName == "") symbolName = S(entry, "symbol");
                string direction = S(pr, "wire_direction");
                string ruleKey = io != "" ? io : deviceIo;
                if (ruleKey != "" && entry.ContainsKey("facing_by_io") && S(D(entry["facing_by_io"]), ruleKey) != "")
                    direction = S(D(entry["facing_by_io"]), ruleKey);
                int index;
                string note;
                SymbolVariant variant;
                // Tek uçlu parça yerleşiminde de karar paketten gelir; host yalnız doğrular.
                Dictionary<string, object> planned = pins.Count == 1 ? Planned(o) : null;
                if (planned != null)
                {
                    variant = VariantFromPackage(c.Project, planned, out index, out note);
                    if (variant == null) throw new InvalidOperationException("Uç " + want + ": paket yerleşim kararı hostta doğrulanmadı: " + note);
                    if (S(planned, "symbol") != symbolName)
                        throw new InvalidOperationException("Uç " + want + ": paket sembolü '" + S(planned, "symbol") + "', kutu rolü '" + symbolName + "' istiyor.");
                }
                else
                {
                    variant = VariantFacing(c.Project, S(entry, "library"), symbolName, direction, 0, out index, out note);
                    if (variant == null) throw new InvalidOperationException("Uç " + want + ": " + note);
                }
                PointD target = P(pr["point_mm"]);
                string placedWith = "PACKAGE_VARIANT";
                try { chosen.PlaceAt(c.Page, target, DocumentTypeManager.DocumentType.Circuit, variant); }
                catch (Exception ex)
                {
                    // İki taraflı tek uçlu parça fonksiyonu (SITOP) paket sembolünü (tek noktalı DCP) kabul etmezse
                    // KENDİ sembolüyle konur; adlı uç yine hedefe hizalanır, öbür taraf aynı fonksiyonla gelir.
                    if (chosen.Pins.Length < 2) throw;
                    chosen.PlaceAt(c.Page, target, DocumentTypeManager.DocumentType.Circuit, chosen.SymbolVariant);
                    placedWith = "OWN_SYMBOL after " + ex.GetType().Name + ": " + ex.Message;
                }
                Pin pin = NamedPin(chosen, SinglePinName(chosen));
                PointD absolute = Absolute(chosen, pin);
                chosen.Location = new PointD(chosen.Location.X + target.X - absolute.X, chosen.Location.Y + target.Y - absolute.Y);
                string boxGroup = S(pr, "box_group");
                if (boxGroup != "")
                {
                    string key = c.Page.Name + "|" + tag + "|" + boxGroup;
                    if (!c.BoxGroups.ContainsKey(key)) c.BoxGroups[key] = new List<Placement>();
                    c.BoxGroups[key].Add(chosen);
                }
                Dictionary<string, object> pl = new Dictionary<string, object>();
                pl["pin"] = want;
                pl["eplan_pin"] = pin.Name;
                pl["match"] = (partPin != "" ? "USER_PIN_MAP" : exact.Count > 0 ? "EXACT" : "LOOSE") + (interchangeable ? "_INTERCHANGEABLE_OF_" + candidates.Count : "");
                if (partPin != "") pl["part_pin"] = partPin;
                pl["function_definition"] = Definition(chosen);
                pl["function_pins"] = chosen.Pins.Length;
                if (chosen.Pins.Length > 1) pl["placed_with"] = placedWith;
                // Cihaz bağlantı noktasının adı EPLAN'da DT + ':' + uçtur. Ad sayfaya KONDUKTAN sonra yazılır:
                // ölçüm 28.09 (RIT.2500210) konmadan yazılınca ':L' okunuyor, geri dönüş tam yapıyı DT sanıp
                // '-=215+E215-2E32:L' basıyordu. Geri dönüşte kısa ad (sayfa yapısı hariç) kullanılır.
                // PLC bağlantı noktası da aynı: EPLAN adı DT + ':' + uç kendisi kurar. Ölçüm 02.10 (20261002h, s42 -39D21):
                // konmadan yazılan ad sayfada '-=215+E215-39D21', geri okumada '-=215+E215-39D21:1:1' çıktı.
                string category = S(D(pl["function_definition"]), "category");
                if (category == "DeviceEndTerminal" || category == "PLCTerminal")
                    pl["device_name"] = WriteName(chosen, tag, "", S(o, "name_on_page") != "" ? S(o, "name_on_page") : tag);
                // Ölçüm 02.10 (20261002h2): parçadan gelen PLC ucunun görünen adı (VisibleName) '-D21' kalıyor ve kanal
                // kutusunun ad satırına basılıyordu. Kanal kutusu yoluyla aynı görünüm: görünen ad boş, cihaz adı etiketle.
                if (category == "PLCTerminal")
                {
                    try { chosen.VisibleName = ""; } catch (Exception ex) { pl["visible_name_error"] = ex.Message; }
                    pl["visible_name"] = Text(delegate { return chosen.VisibleName; });
                }
                // İki taraflı tek uç tek noktalı DCP'de: uç adı/açıklaması bir kez (etiket eşleşmesi tek adla yapılır, önce).
                if (chosen.Pins.Length > 1 && placedWith == "PACKAGE_VARIANT")
                {
                    try { pl["single_side_text"] = SingleSideTexts(chosen, pin); }
                    catch (Exception ex) { pl["single_side_text_error"] = ex.GetType().Name + ": " + ex.Message; }
                }
                if ((category == "DeviceEndTerminal" || category == "PLCTerminal") && pinLog.Count == 0)   // nesne başına bir kez
                    PlaceLabels(c, chosen, o, row);      // DCP/PLC kutusu adı basmaz; cihaz adı etiketle görünür (hibrit/kanal kutusu yoluyla aynı)
                pl["symbol"] = note;
                pl["io"] = io;
                CheckDeviation(c, id + "#" + want, chosen, Absolute(chosen, pin), target, pl);
                MapPin(c, id + "#" + want, chosen, pin.Name);
                pinLog.Add(pl);
            }
            row["pins"] = pinLog;
            row["object_id"] = pinLog.Count > 0 ? S(D(pinLog[0]), "object_id") : "";
        }

        static void PlaceCorners(Context c, Dictionary<string, object> link, Dictionary<string, object> lr, List<string> routeErrors)
        {
            Dictionary<string, object> transform = link.ContainsKey("route_transform") ? D(link["route_transform"]) : null;
            if (transform != null && S(transform, "kind") == "NOT_ADAPTABLE")
            {
                lr["skipped"] = "Güzergâh hedefe uyarlanamadı (paket kaydı); çizilmedi.";
                routeErrors.Add(S(link, "a") + " — " + S(link, "b") + ": " + lr["skipped"]);
                return;
            }
            ArrayList poly = A(link.ContainsKey("polyline_mm") ? link["polyline_mm"] : null);
            List<string> corners = new List<string>();
            lr["corners"] = corners;
            if (!c.Families.ContainsKey("corner") && poly.Count > 2)
            {
                lr["skipped"] = "Köşe eşlemesi yok.";
                routeErrors.Add(S(link, "a") + " — " + S(link, "b") + ": köşe eşlemesi yok");
                return;
            }
            for (int i = 1; i < poly.Count - 1; i++)
            {
                PointD before = P(poly[i - 1]), here = P(poly[i]), after = P(poly[i + 1]);
                List<string> arms = new List<string> { Way(before.X - here.X, before.Y - here.Y), Way(after.X - here.X, after.Y - here.Y) };
                arms.Sort(StringComparer.Ordinal);
                if ((arms[0] == "Left" && arms[1] == "Right") || (arms[0] == "Down" && arms[1] == "Up")) continue;   // düz geçiş
                Dictionary<string, object> entry = D(c.Families["corner"]);
                string note;
                SymbolVariant sv = VariantWithDirections(c.Project, S(entry, "library"), S(entry, "symbol"), string.Join(",", arms.ToArray()), out note);
                if (sv == null)
                {
                    lr["error"] = note;
                    routeErrors.Add(S(link, "a") + " — " + S(link, "b") + ": " + note);
                    return;
                }
                SymbolReference corner = SymbolReference.Create(sv, c.Page);
                c.Created.Add(corner);
                corner.Location = InsertionFor(sv, 0, here);
                corners.Add(Id(corner));
            }
        }

        /// <summary>MAIN 03.10: paketteki bağ kesiti (`cross_section_mm2`, müşteri çizimindeki tel yazısı) o tel parçasını
        /// taşıyan EPLAN bağlantılarına yazılır: Connection 31002 CONNECTION_WIRECROSSSECTION, birim 31059 = 1 (mm²).
        /// EPLAN bağlantısı uçtan uca (pin→pin) üretilir, T/köşe sembolleri SymbolReferences'tadır; bağın iki ucu
        /// (SourceMap kimliği) bağlantının ucu ya da geçtiği sembolse bağlantı o parçayı taşır. Aynı bağlantıya farklı
        /// iki kesit düşerse YAZILMAZ (CONFLICT). Bağlantılar Generate.Connections'tan sonra var olduğundan orada çağrılır.</summary>
        const string DefaultNoteText = "Kesit genel nottan (DEFAULT_NOTE): müşteri telde kesit yazmamış";
        // MAIN 04.10 akşam seri devam: kesit seri elemanın (kontak, kontaktör ana kontağı) öbür yanındaki telden taşındı.
        const string SeriesText = "Kesit seri devamdan (SERIES): müşteri bu telde kesit yazmamış, seri elemanın öbür yanından";

        static List<object> WriteCrossSections(Context c, Dictionary<string, object> pkg)
        {
            List<object> rows = new List<object>();
            List<Dictionary<string, object>> links = new List<Dictionary<string, object>>();
            foreach (object lo in A(pkg["expected_links"]))
            {
                Dictionary<string, object> link = D(lo);
                if ((link.ContainsKey("cross_section_mm2") && link["cross_section_mm2"] != null)
                    || (link.ContainsKey("wire_color") && S(link, "wire_color") != "")) links.Add(link);
            }
            if (links.Count == 0) return rows;
            List<KeyValuePair<Connection, HashSet<string>>> all = new List<KeyValuePair<Connection, HashSet<string>>>();
            try
            {
                foreach (Connection k in new DMObjectsFinder(c.Project).GetConnections(new ConnectionsFilter()))
                {
                    HashSet<string> keys = new HashSet<string>();
                    foreach (Pin pin in new Pin[] { k.StartPin, k.EndPin })
                    {
                        if (pin == null || pin.ParentFunction == null) continue;
                        keys.Add(Id(pin.ParentFunction));
                        keys.Add(Id(pin.ParentFunction) + "#" + pin.Name);
                    }
                    try { foreach (SymbolReference s in k.SymbolReferences) keys.Add(Id(s)); } catch (Exception) { }
                    all.Add(new KeyValuePair<Connection, HashSet<string>>(k, keys));
                }
            }
            catch (Exception ex) { rows.Add(new Dictionary<string, object> { { "error", "bağlantılar okunamadı: " + ex.Message } }); return rows; }
            // MAIN 05.10: tel rengi (31004 Farbe/Nummer) + tel türü (31048) + tel ürün kodu (bağlantı parça referansı) kesitle
            // aynı döngüde yazılır; alan başına çelişki ayrı sayılır (boş alan çelişki değildir).
            Dictionary<Connection, HashSet<string>> texts = new Dictionary<Connection, HashSet<string>>();
            Dictionary<Connection, HashSet<string>> wires = new Dictionary<Connection, HashSet<string>>();
            Dictionary<string, string> partOf = new Dictionary<string, string>();
            List<KeyValuePair<Dictionary<string, object>, List<Connection>>> plan = new List<KeyValuePair<Dictionary<string, object>, List<Connection>>>();
            foreach (Dictionary<string, object> link in links)
            {
                bool hasCross = link.ContainsKey("cross_section_mm2") && link["cross_section_mm2"] != null;
                string text = hasCross ? N(link["cross_section_mm2"]).ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) : "";
                Dictionary<string, object> row = new Dictionary<string, object> { { "a", S(link, "a") }, { "b", S(link, "b") }, { "value", text } };
                string wireColor = S(link, "wire_color"), wireType = S(link, "wire_type"), wireTip = S(link, "wire_tip"), wirePart = S(link, "wire_part");
                if (wireColor != "") row["wire_color"] = wireColor;
                if (wireType != "") { row["wire_type"] = wireType; row["wire_tip"] = wireTip; }
                if (wirePart != "" && !partOf.ContainsKey(wirePart))
                {
                    string rule, found = "";
                    foreach (string spelling in new string[] { "LAPP." + wirePart, wirePart })
                    {
                        MDPart mp = c.PartsDb == null ? null : MacroDumpAction.FindPart(c.PartsDb, spelling, c.Log, out rule);
                        if (mp != null) { found = mp.PartNr; break; }
                    }
                    partOf[wirePart] = found;
                }
                if (wirePart != "") { row["wire_code"] = wirePart; row["wire_part"] = partOf[wirePart]; }
                // MAIN 03.10 gece 6: genel nottan gelen kesit (DEFAULT_NOTE) yazılı kesitten ayrılsın diye bağlantı açıklamasına not.
                object src;
                if (link.TryGetValue("cross_section_source", out src) && src is Dictionary<string, object>
                    && (S(D(src), "method") == "DEFAULT_NOTE" || S(D(src), "method") == "SERIES")) row["source"] = S(D(src), "method");
                rows.Add(row);
                string a = EndKey(c, S(link, "a")), b = EndKey(c, S(link, "b"));
                List<Connection> hits = new List<Connection>();
                if (a != null && b != null)
                    foreach (KeyValuePair<Connection, HashSet<string>> k in all)
                        if (k.Value.Contains(a) && k.Value.Contains(b)) hits.Add(k.Key);
                if (a == null || b == null) row["status"] = "END_NOT_PLACED";
                else if (hits.Count == 0 && S(link, "via") == "OWN_ARROW" && hasCross)
                {
                    try { PlaceCrossSectionPoint(c, link, text, row); row["status"] = "CDP_WRITTEN"; }
                    catch (Exception ex) { row["status"] = "NO_CONNECTION"; row["cdp_error"] = ex.GetType().Name + ": " + ex.Message; }
                }
                else if (hits.Count == 0) row["status"] = "NO_CONNECTION";
                foreach (Connection k in hits)
                {
                    if (!texts.ContainsKey(k)) { texts[k] = new HashSet<string>(); wires[k] = new HashSet<string>(); }
                    if (hasCross) texts[k].Add(text);
                    if (wireColor != "") wires[k].Add("C:" + wireColor);
                    if (wireType != "") wires[k].Add("T:" + wireType);
                    if (wirePart != "" && partOf[wirePart] != "") wires[k].Add("P:" + partOf[wirePart]);
                }
                plan.Add(new KeyValuePair<Dictionary<string, object>, List<Connection>>(row, hits));
            }
            foreach (KeyValuePair<Dictionary<string, object>, List<Connection>> item in plan)
            {
                List<string> written = new List<string>(), conflicts = new List<string>();
                foreach (Connection k in item.Value)
                {
                    if (texts[k].Count > 1 || WireConflict(wires[k])) { conflicts.Add(Id(k)); continue; }
                    try
                    {
                        if (S(item.Key, "value") != "")
                        {
                        k.Properties.CONNECTION_WIRECROSSSECTION = S(item.Key, "value");
                        k.Properties.CONNECTION_WIRECROSSSECTION_UNIT = 1;
                        }
                        if (S(item.Key, "wire_color") != "") k.Properties.CONNECTION_WIRENUMBER = S(item.Key, "wire_color");
                        if (S(item.Key, "wire_type") != "") k.Properties.CONNECTION_WIRETYPE = S(item.Key, "wire_type");
                        if (S(item.Key, "wire_part") != "")
                        {
                            bool present = false;
                            foreach (ArticleReference have in k.ArticleReferences) if (have.PartNr == S(item.Key, "wire_part")) present = true;
                            if (!present) k.AddArticleReference(S(item.Key, "wire_part"));
                            item.Key["part_written"] = true;
                        }
                        if (S(item.Key, "value") != "" && item.Key.ContainsKey("source"))
                        {
                            MultiLangString note = new MultiLangString();
                            note.AddString(ISOCode.Language.L___, S(item.Key, "source") == "SERIES" ? SeriesText : DefaultNoteText);
                            k.Properties.CONNECTION_DESCRIPTION = note;
                        }
                        written.Add(Id(k));
                    }
                    catch (Exception ex) { item.Key["error"] = ex.GetType().Name + ": " + ex.Message; }
                }
                item.Key["connections"] = written;
                if (conflicts.Count > 0) item.Key["conflicts"] = conflicts;
                if (!item.Key.ContainsKey("status"))
                    item.Key["status"] = written.Count > 0 ? (conflicts.Count > 0 ? "PARTIAL_CONFLICT" : "WRITTEN")
                                       : conflicts.Count > 0 ? "CONFLICT" : "NOT_WRITTEN";
            }
            return rows;
        }

        /// <summary>MAIN 04.10 akşam: kendi oku bağında (cihaz ucu → eşsiz adlı kesinti noktası) EPLAN Connection üretmez
        /// (ölçüm 20261004_HAT_son: 97 kendi-ok kesiti NO_CONNECTION) → kesit tele yazılamıyor, görünmüyordu. Kesit telin
        /// ortasına bağlantı tanım noktası (SPECIAL/CDP, capabilities.json: ConnectionDefinition, 1 varyant, 0 bağlantı
        /// noktası) ile yazılır; CDP özelliği 31002/31059 bağlantınınkiyle aynı alandır.</summary>
        static bool WireConflict(HashSet<string> v)
        {
            foreach (string prefix in new string[] { "C:", "T:", "P:" })
            {
                int n = 0;
                foreach (string x in v) if (x.StartsWith(prefix)) n++;
                if (n > 1) return true;
            }
            return false;
        }

        static void PlaceCrossSectionPoint(Context c, Dictionary<string, object> link, string text, Dictionary<string, object> row)
        {
            string target = S(D(c.SourceMap[S(link, "b")]), "object_id");
            Placement arrow = null;
            foreach (StorableObject so in c.Created)
                if (so is Placement && Id(so) == target) { arrow = (Placement)so; break; }
            if (arrow == null) throw new InvalidOperationException("kendi oku nesnesi bulunamadı: " + target);
            ArrayList line = A(link["polyline_mm"]);
            if (line.Count < 2) throw new InvalidOperationException("tel çizgisi yok");
            PointD p0 = P(line[0]), p1 = P(line[1]);
            SymbolVariant sv = SymbolOf(c.Project, "SPECIAL", "CDP")[0];
            if (sv == null) throw new InvalidOperationException("SPECIAL/CDP varyantı yok");
            SymbolReference sr = SymbolReference.Create(sv, arrow.Page);
            c.Created.Add(sr);
            sr.Location = new PointD(Math.Round((p0.X + p1.X) / 2, 3), Math.Round((p0.Y + p1.Y) / 2, 3));
            ConnectionDefinitionPoint cdp = sr as ConnectionDefinitionPoint;
            if (cdp == null) throw new InvalidOperationException("sembol bağlantı tanım noktası üretmedi: " + sr.GetType().Name);
            cdp.Properties.CONNECTION_WIRECROSSSECTION = text;
            cdp.Properties.CONNECTION_WIRECROSSSECTION_UNIT = 1;
            // MAIN 05.10: kendi-ok telinde Connection yok; renk ve tel türü aynı CDP'ye yazılır (parça referansı yok).
            if (S(row, "wire_color") != "") cdp.Properties.CONNECTION_WIRENUMBER = S(row, "wire_color");
            if (S(row, "wire_type") != "") cdp.Properties.CONNECTION_WIRETYPE = S(row, "wire_type");
            if (row.ContainsKey("source"))
            {
                MultiLangString note = new MultiLangString();
                note.AddString(ISOCode.Language.L___, S(row, "source") == "SERIES" ? SeriesText : DefaultNoteText);
                cdp.Properties.CONNECTION_DESCRIPTION = note;
            }
            row["cdp"] = Id(sr);
            row["cdp_point"] = XY(sr.Location);
        }

        static string EndKey(Context c, string node)
        {
            if (node == null || !c.SourceMap.ContainsKey(node)) return null;
            Dictionary<string, object> m = D(c.SourceMap[node]);
            string pin = m.ContainsKey("pin") ? m["pin"] as string : null;
            return string.IsNullOrEmpty(pin) ? S(m, "object_id") : S(m, "object_id") + "#" + pin;
        }

        /// <summary>YAZI 04.10: kesit yazısı (CDP) paketin seçtiği boş tel yerine taşınır (layout.plan_cross_sections,
        /// `cross_section_label`). EPLAN kendi CDP'sini bağlantının ilk parçasının ortasına koyuyor; kısa telde uç yazısına
        /// biniyordu (=215 s9/s10 -7Q26:2 '1,5', s38 A2 '1'). Yalnız T'siz uç→uç bağlantı (tek bağlantı, iki ucu cihaz ucu)
        /// ve kendi oku teli: T'li bağlantıda yeni yer başka bağlantının teline düşebilirdi. Yeni yer aynı bağın çizgisindedir.
        /// Yazı karşı yana geçecekse CDP'nin KENDİ gösterimi taşınır (PlaceLabels move_existing; yeni yazı eklenmez).</summary>
        static void MoveCrossSectionPoints(Context c, Dictionary<string, object> pkg, List<object> rows)
        {
            int i = 0;
            List<object[]> tees = new List<object[]>();      // T'li bağlantının bağları: {satır, bağ, plan|null}
            foreach (object lo in A(pkg["expected_links"]))
            {
                Dictionary<string, object> link = D(lo);
                // Satır sırası WriteCrossSections'ınkiyle aynı süzgeç (kesit YA DA renk): yalnız renkli bağ atlanırsa sıra kayıp
                // "sıra bozuk" diye T'li taşıma hiç çalışmıyordu (RENKYAZI 05.10, =215 s9/s10 11K53/11K56).
                bool hasCs = link.ContainsKey("cross_section_mm2") && link["cross_section_mm2"] != null;
                if (!hasCs && S(link, "wire_color") == "") continue;
                if (i >= rows.Count) return;
                Dictionary<string, object> row = D(rows[i++]);
                if (S(row, "a") != S(link, "a") || S(row, "b") != S(link, "b")) return;     // sıra bozuksa hiçbirini taşıma
                if (!hasCs) continue;
                object lab;
                Dictionary<string, object> plan = link.TryGetValue("cross_section_label", out lab) && lab is Dictionary<string, object>
                    && D(lab).ContainsKey("point_mm") ? D(lab) : null;
                string ea = EndKey(c, S(link, "a")), eb = EndKey(c, S(link, "b"));
                if (S(row, "status") == "WRITTEN" && !(ea != null && eb != null && ea.Contains("#") && eb.Contains("#")))
                {
                    tees.Add(new object[] { row, link, plan });
                    continue;
                }
                if (plan == null) continue;
                try
                {
                    ConnectionDefinitionPoint cdp = null;
                    string status = S(row, "status");
                    List<string> conns = row.ContainsKey("connections") ? row["connections"] as List<string> : null;
                    string a = EndKey(c, S(link, "a")), b = EndKey(c, S(link, "b"));
                    if (status == "CDP_WRITTEN")
                    {
                        foreach (StorableObject so in c.Created)
                            if (so is ConnectionDefinitionPoint && Id(so) == S(row, "cdp")) { cdp = (ConnectionDefinitionPoint)so; break; }
                    }
                    else if (status == "WRITTEN" && conns != null && conns.Count == 1 && a != null && b != null
                             && a.Contains("#") && b.Contains("#"))
                    {
                        string owner = a.Split('#')[0];
                        Page page = null;
                        foreach (StorableObject so in c.Created)
                            if (so is Placement && Id(so) == owner) { page = ((Placement)so).Page; break; }
                        List<ConnectionDefinitionPoint> on = new List<ConnectionDefinitionPoint>();
                        if (page != null)
                            foreach (Placement pl in page.AllPlacements)
                            {
                                ConnectionDefinitionPoint q = pl as ConnectionDefinitionPoint;
                                if (q != null && OnLine(q.Location, A(link["polyline_mm"]))) on.Add(q);
                            }
                        if (on.Count != 1) { row["cs_move"] = "CDP_NOT_UNIQUE: " + on.Count; continue; }
                        cdp = on[0];
                    }
                    else { row["cs_move"] = "NOT_MOVED: " + status; continue; }
                    if (cdp == null) { row["cs_move"] = "CDP_NOT_FOUND"; continue; }
                    MoveCdp(c, cdp, plan, row);
                }
                catch (Exception ex) { row["cs_move"] = "ERROR: " + ex.GetType().Name + ": " + ex.Message; }
            }
            // T'li bağlantı (YAZI 04.10; KESIT2 =215 s9/s10 '6'): EPLAN'ın CDP'si bağlantının ilk parçasında, çoğu kez uçtan T'ye
            // 2 mm'lik saplamada (uç yazısına biner). Bağlantının KENDİ tek CDP'si (Connection.ConnectionDefPoints) yalnız o
            // bağlantının (başka bağlantıyla paylaşılmayan: tek bağlantıya düşen) bir bağının planlı yerine taşınır; T ağındaki
            // bütün bağların kesiti aynı değilse taşınmaz.
            if (tees.Count == 0) return;
            Dictionary<string, Connection> byId = new Dictionary<string, Connection>();
            try { foreach (Connection k in new DMObjectsFinder(c.Project).GetConnections(new ConnectionsFilter())) byId[Id(k)] = k; }
            catch (Exception) { return; }
            // Önce bağlantıya özgü (tek bağlantıya düşen) bağ; yoksa (=215 s9/s10 11K53/11K56:2: 2 mm'lik saplama ve T→X1 ortak
            // parçası) bütün bağlantıları aynı kesitli bir bağ. Her planlı yer bir kez kullanılır.
            Func<Dictionary<string, object>, List<string>> connsOf = delegate (Dictionary<string, object> r)
            { return (r.ContainsKey("connections") ? r["connections"] as List<string> : null) ?? new List<string>(); };
            Func<string, string> valueOf = delegate (string id)
            {
                string v = null;
                foreach (object[] o in tees)
                    if (connsOf((Dictionary<string, object>)o[0]).Contains(id))
                    {
                        string w = S((Dictionary<string, object>)o[0], "value");
                        if (v == null) v = w; else if (v != w) return "?";
                    }
                return v;
            };
            HashSet<string> done = new HashSet<string>();
            HashSet<object[]> used = new HashSet<object[]>();
            List<string> ids = new List<string>();
            foreach (object[] e in tees)
                foreach (string id in connsOf((Dictionary<string, object>)e[0])) if (!ids.Contains(id)) ids.Add(id);
            foreach (bool sharedOk in new[] { false, true })
                foreach (string id in ids)
                {
                    if (done.Contains(id)) continue;
                    string value = valueOf(id);
                    if (value == null || value == "?") continue;
                    // KESIT_CDP 14.10 (=280 Blatt 2): sayfa geçen tel (kesinti noktası üzerinden) EPLAN'da TEK Connection; CDP'si
                    // ilk sayfada durur. Plan başka sayfanınsa CDP oraya taşınmaz (koordinat yabancı sayfaya düşüyordu).
                    Connection con;
                    byId.TryGetValue(id, out con);
                    ConnectionDefinitionPoint[] cps = null;
                    if (con != null) try { cps = con.ConnectionDefPoints; } catch (Exception) { }
                    string cdpPage = cps != null && cps.Length == 1 ? Id(cps[0].Page) : null;
                    object[] pick = null;
                    List<object[]> otherPage = new List<object[]>();
                    foreach (object[] e in tees)
                    {
                        List<string> cs = connsOf((Dictionary<string, object>)e[0]);
                        if (e[2] == null || used.Contains(e) || !cs.Contains(id)) continue;
                        bool ok = cs.Count == 1;
                        if (!ok && sharedOk) { ok = true; foreach (string other in cs) if (valueOf(other) != value) ok = false; }
                        if (ok && cdpPage != null && LinkPageId(c, (Dictionary<string, object>)e[1]) != cdpPage) { otherPage.Add(e); ok = false; }
                        if (ok) { pick = e; break; }
                    }
                    foreach (object[] e in otherPage)
                        if (!((Dictionary<string, object>)e[0]).ContainsKey("cs_move"))
                            ((Dictionary<string, object>)e[0])["cs_move"] = "T_CDP_OTHER_PAGE: " + cps[0].Page.Name;
                    if (pick == null) continue;
                    Dictionary<string, object> row = (Dictionary<string, object>)pick[0];
                    if (con == null) { row["cs_move"] = "T_CONNECTION_NOT_FOUND"; continue; }
                    if (cps == null || cps.Length != 1) { row["cs_move"] = "T_CDP_COUNT: " + (cps == null ? "?" : cps.Length.ToString()); continue; }
                    try { MoveCdp(c, cps[0], (Dictionary<string, object>)pick[2], row); row["cs_connection"] = id; done.Add(id); used.Add(pick); }
                    catch (Exception ex) { row["cs_move"] = "ERROR: " + ex.GetType().Name + ": " + ex.Message; }
                }
            // Sayfa geçen bağlantının kesiti kendi sayfasında da görünsün: yalnız o bağlantıya özgü bağın planlı yerine ek CDP
            // (aynı değer/renk/tür/not). Bağlantı+sayfa başına bir kez.
            HashSet<string> extra = new HashSet<string>();
            foreach (object[] e in tees)
            {
                Dictionary<string, object> row = (Dictionary<string, object>)e[0];
                List<string> cs = connsOf(row);
                if (e[2] == null || cs.Count != 1 || !S(row, "cs_move").StartsWith("T_CDP_OTHER_PAGE")) continue;
                Connection con;
                if (!byId.TryGetValue(cs[0], out con)) continue;
                string pg = LinkPageId(c, (Dictionary<string, object>)e[1]);
                if (pg == null || !extra.Add(cs[0] + "|" + pg)) continue;
                try { AddPageCdp(c, con, (Dictionary<string, object>)e[1], (Dictionary<string, object>)e[2], row); row["cs_connection"] = cs[0]; }
                catch (Exception ex) { row["cs_extra_error"] = ex.GetType().Name + ": " + ex.Message; }
            }
            foreach (object[] e in tees)
            {
                Dictionary<string, object> row = (Dictionary<string, object>)e[0];
                if (!row.ContainsKey("cs_move")) row["cs_move"] = "NOT_MOVED: bu bağlantının CDP'si başka bağa taşındı ya da yer yok";
            }
        }

        /// <summary>Bağın sayfası: uçlarından birinin (a, yoksa b) bu aktarımda oluşan yerleşiminin sayfa kimliği; bulunamazsa null.</summary>
        static string LinkPageId(Context c, Dictionary<string, object> link)
        {
            Page p = LinkPage(c, link);
            return p == null ? null : Id(p);
        }

        static Page LinkPage(Context c, Dictionary<string, object> link)
        {
            foreach (string end in new[] { "a", "b" })
            {
                string key = EndKey(c, S(link, end));
                if (key == null) continue;
                string owner = key.Split('#')[0];
                foreach (StorableObject so in c.Created)
                    if (so is Placement && Id(so) == owner) return ((Placement)so).Page;
            }
            return null;
        }

        /// <summary>KESIT_CDP 14.10: sayfa geçen bağlantının (CDP'si başka sayfada) bu bağın sayfasındaki planlı yerine ek CDP;
        /// özellikler satırdakiyle (bağlantıya yazılanla) aynı. Makbuz: cs_move EXTRA_CDP, cs_extra_attached (CDP bağlantıya bağlandı mı).</summary>
        static void AddPageCdp(Context c, Connection con, Dictionary<string, object> link, Dictionary<string, object> plan, Dictionary<string, object> row)
        {
            Page page = LinkPage(c, link);
            if (page == null) throw new InvalidOperationException("bağın sayfası yok");
            SymbolVariant sv = SymbolOf(c.Project, "SPECIAL", "CDP")[0];
            if (sv == null) throw new InvalidOperationException("SPECIAL/CDP varyantı yok");
            SymbolReference sr = SymbolReference.Create(sv, page);
            c.Created.Add(sr);
            ConnectionDefinitionPoint cdp = sr as ConnectionDefinitionPoint;
            if (cdp == null) throw new InvalidOperationException("sembol bağlantı tanım noktası üretmedi: " + sr.GetType().Name);
            if (S(row, "value") != "") { cdp.Properties.CONNECTION_WIRECROSSSECTION = S(row, "value"); cdp.Properties.CONNECTION_WIRECROSSSECTION_UNIT = 1; }
            if (S(row, "wire_color") != "") cdp.Properties.CONNECTION_WIRENUMBER = S(row, "wire_color");
            if (S(row, "wire_type") != "") cdp.Properties.CONNECTION_WIRETYPE = S(row, "wire_type");
            if (S(row, "wire_part") != "") cdp.AddArticleReference(S(row, "wire_part"));
            if (row.ContainsKey("source"))
            {
                MultiLangString note = new MultiLangString();
                note.AddString(ISOCode.Language.L___, S(row, "source") == "SERIES" ? SeriesText : DefaultNoteText);
                cdp.Properties.CONNECTION_DESCRIPTION = note;
            }
            cdp.Location = P(plan["point_mm"]);
            MoveCdp(c, cdp, plan, row);
            row["cs_move"] = "EXTRA_CDP";
            row["cs_extra_cdp"] = Id(cdp);
            bool attached = false;
            try { foreach (Connection k in cdp.Connections) if (Id(k) == Id(con)) attached = true; } catch (Exception) { }
            row["cs_extra_attached"] = attached;
        }

        static void MoveCdp(Context c, ConnectionDefinitionPoint cdp, Dictionary<string, object> plan, Dictionary<string, object> row)
        {
            row["cs_from"] = XY(cdp.Location);
            cdp.Location = P(plan["point_mm"]);
            row["cs_to"] = XY(cdp.Location);
            row["cs_move"] = "MOVED";
            if (plan.ContainsKey("text_at_mm"))
            {
                Dictionary<string, object> label = new Dictionary<string, object> {
                    { "role", "cross_section" }, { "match_text", S(row, "value") }, { "move_existing", "True" },
                    { "at_mm", plan["text_at_mm"] }, { "justification", S(plan, "justification") } };
                PlaceLabels(c, cdp, new Dictionary<string, object> { { "labels", new ArrayList { label } } }, row);
            }
        }

        static bool OnLine(PointD p, ArrayList line)
        {
            for (int k = 0; k + 1 < line.Count; k++)
            {
                PointD s = P(line[k]), e = P(line[k + 1]);
                if (p.X >= Math.Min(s.X, e.X) - 0.05 && p.X <= Math.Max(s.X, e.X) + 0.05
                    && p.Y >= Math.Min(s.Y, e.Y) - 0.05 && p.Y <= Math.Max(s.Y, e.Y) + 0.05) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ ana akış
        public bool Execute(ActionCallingContext ctx)
        {
            string pkgPath = Io.Param(ctx, "PACKAGE"), mapPath = Io.Param(ctx, "MAPPING"), outDir = Io.Param(ctx, "OUT");
            string template = Io.Param(ctx, "TEMPLATE"), target = Io.Param(ctx, "TARGET");
            string onFailure = Io.Param(ctx, "ON_FAILURE").ToUpperInvariant();
            // Boş parametre (UvpSayfaAktar.cs) istisnada geri almasız FAILED_PARTIAL_WRITE bırakıp projeyi defterde
            // kilitliyordu (inceleme R4-C3, 01.10). Varsayılan panelinkiyle aynı: KEEP_PARTIAL.
            if (onFailure == "") onFailure = "KEEP_PARTIAL";
            bool useMacros = Io.Param(ctx, "MACRO") == "1";
            Directory.CreateDirectory(outDir);
            if (File.Exists(Path.Combine(outDir, "receipt.json")))
            {
                File.WriteAllText(Path.Combine(outDir, "REDDEDILDI_" + Io.Stamp() + ".txt"),
                    "OUT klasöründe receipt.json var; önceki kayıt ezilmez. Yeni bir koşu klasörü verin.", new UTF8Encoding(false));
                return false;
            }
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> receipt = new Dictionary<string, object>();
            Dictionary<string, object> readback = new Dictionary<string, object>();
            List<string> log = new List<string>();
            List<object> created = new List<object>(), rejected = new List<object>(), routes = new List<object>();
            List<string> routeErrors = new List<string>();
            List<string> riskFlags = new List<string>();
            List<string> createdNames = new List<string>();     // defter created_names: bu aktarımın yarattığı cihaz tam adları
            // MAIN 04.10 çizimci kontrolü: sayfa işaretleri (makbuz) ve kontrol listesi satırları (UvpPdfToP8Kontrol.cs).
            List<object> controlMarks = new List<object>(), checklist = new List<object>();
            string listAnlage = null, listEinbauort = null;
            receipt["contract"] = "uvp.pdf2p8.receipt";
            receipt["contract_version"] = "2.0";
            receipt["utc"] = DateTime.UtcNow.ToString("o");
            receipt["writes_live_project"] = false;
            // Kullanıcı kararı: "bulabildiği kadarını aktarsın" — KEEP_PARTIAL yalnız kısmi sonucu tutar.
            bool rollbackOnError = onFailure == "ROLLBACK" || onFailure == "KEEP_PARTIAL";
            receipt["on_failure"] = onFailure == "ROLLBACK" ? "ROLLBACK"
                                  : onFailure == "KEEP_PARTIAL" ? "KEEP_PARTIAL_ROLLBACK_ON_ERROR" : "KEEP_PARTIAL_AND_REPORT";
            try
            {
                string dll = typeof(ImportAction).Assembly.Location;
                receipt["dll"] = dll;
                receipt["dll_sha256"] = Sha256(dll);
                receipt["eplan_version"] = Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion;
            }
            catch (Exception ex) { log.Add("kimlik okunamadı: " + ex.Message); }
            Context c = new Context();
            c.Log = log;
            c.TraceFile = Path.Combine(outDir, "ilerleme.log");
            Trace(c, "başladı: paket=" + pkgPath + " hedef=" + (target == "" ? "(yeni)" : target) + " on_failure=" + onFailure);
            Project prj = null;
            bool closeProject = false;
            UndoManager undoManager = null;
            UndoStep undo = null;
            List<Page> pages = new List<Page>();
            Dictionary<Page, int> physical = new Dictionary<Page, int>();
            Dictionary<Page, string> subOf = new Dictionary<Page, string>();   // alt sayfa harfi (MAIN 03.10 gece)
            string state = "NOT_STARTED";
            Dictionary<string, object> ledgerPackage = null;
            // 14.10 'Üstüne yaz': /REPLACE:1 → defterde bu belgenin çakışan aktarımlarının sayfaları yazmadan önce kaldırılır.
            bool replace = Io.Param(ctx, "REPLACE") == "1";
            List<Dictionary<string, object>> replaceHits = new List<Dictionary<string, object>>();
            try
            {
                Dictionary<string, object> pkg = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(pkgPath));
                ledgerPackage = pkg;
                Dictionary<string, object> map = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(mapPath));
                if (S(pkg, "contract") != "uvp.pdf2p8.import-request" || S(pkg, "contract_version") != "1.0")
                    throw new RefusedException("REFUSED_CONTRACT", "Beklenmeyen paket sözleşmesi: " + S(pkg, "contract") + " " + S(pkg, "contract_version"));
                receipt["package_file"] = pkgPath;
                receipt["package_file_sha256"] = Sha256(pkgPath);
                receipt["package_sha256"] = S(pkg, "payload_sha256");
                receipt["mapping_file"] = mapPath;
                receipt["mapping_file_sha256"] = Sha256(mapPath);
                readback["package_sha256"] = S(pkg, "payload_sha256");
                readback["document_sha256"] = S(pkg, "document_sha256");
                c.Families = D(map["families"]);
                foreach (object po in A(pkg["pages"]))
                    foreach (object oo in A(D(po)["objects"]))
                        if (S(D(oo), "kind") == "INTERRUPTION" && S(D(oo), "import_name") == "")
                            throw new RefusedException("REFUSED_CONTRACT", "Devam kimliği olmayan eski paket; güncel motorla yeniden üretin.");
                // Ad alanı BELGEDEN gelir, koşudan değil: aynı PDF'in sayfaları ayrı ayrı aktarılsa da
                // karşılıklı devam uçları aynı adı taşımalı (EPLAN çifti ada göre eşler). Rastgele
                // koşu kimliği kullanılırsa ikinci sayfanın ucu birinciyle EŞLEŞMEZ (ölçüm: 04_aktarim_A).
                string document = S(pkg, "document_sha256");
                if (document.Length < 10) throw new RefusedException("REFUSED_CONTRACT", "Pakette belge kimliği (document_sha256) yok.");
                c.Namespace = "UVP" + document.Substring(0, 10) + "_";

                List<string> onlyPages = new List<string>();
                foreach (string piece in Io.Param(ctx, "PAGES").Split(','))
                    if (piece.Trim() != "") onlyPages.Add(piece.Trim());
                receipt["pages_filter"] = onlyPages.ToArray();

                // ---------------------------------------------------------- onay kapısı (MAIN 03.10)
                // Kırmızı bayraklar YAZMADAN önce toplanır; kapı bayrağı varsa /ACK_RISK:1 (panelde "gördüm, aktar",
                // toplu betikte -AckRisk) olmadan proje açılmaz, LockingStep/UndoStep kurulmaz, hiçbir nesne yazılmaz.
                foreach (object po in A(pkg["pages"]))
                    if (onlyPages.Count == 0 || onlyPages.Contains(Convert.ToString(D(po)["physical_page"]))) AddRiskFlags(D(po), riskFlags);
                bool ackRisk = Io.Param(ctx, "ACK_RISK") == "1";
                receipt["risk_acknowledged"] = ackRisk;
                List<string> needAck = riskFlags.FindAll(delegate(string f) { return Array.IndexOf(AckRequiredFlags, f) >= 0; });
                receipt["risk_ack_flags"] = needAck;
                if (needAck.Count > 0 && !ackRisk)
                    throw new RefusedException("REFUSED_RISK_NOT_ACKNOWLEDGED", "Kırmızı uyarılı sayfa onaysız yazılmaz ("
                        + string.Join(", ", needAck.ToArray()) + "): incelemede 'gördüm, aktar' onayı (ACK_RISK=1) gerekir. Hiçbir nesne yazılmadı.");

                // ---------------------------------------------------------- hedef proje
                ProjectManager pm = new ProjectManager();
                if (target.ToUpperInvariant() == "OPEN")
                    throw new RefusedException("REFUSED_TARGET", "Açık kullanıcı projesi hedef olamaz. Ayrı test projesi kullanın (TARGET boş = yeni proje).");
                if (target != "")
                {
                    if (!Path.IsPathRooted(target))
                        throw new RefusedException("REFUSED_TARGET", "TARGET tam yol olmalı: " + target);
                    string full = Path.GetFullPath(target);
                    // Hedef, bu koşunun çıktısıyla AYNI output\p8runs kökünde olmalı (öz denetim F5-12): başka
                    // diskteki '...\output\p8runs\UVP_x.elk' kabul edilmez.
                    string runs = RunsRoot(outDir);
                    if (runs == "" || !full.StartsWith(runs, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(full).StartsWith("UVP_", StringComparison.OrdinalIgnoreCase))
                        throw new RefusedException("REFUSED_TARGET", "TARGET yalnız output\\p8runs altındaki UVP_ test projesi olabilir: " + full);
                    // Aynı paketin veya aynı KAYNAK SAYFANIN ikinci kez aktarılması reddedilir (sessiz
                    // kopya yok). Aynı test projesine BAŞKA sayfanın eklenmesi normal akıştır: sayfa
                    // sayfa ilerleme ürünün kendisidir, önceki sayfa yeniden yerleştirilmez.
                    // 14.10 (kullanıcı): çakışan kayıtlar toplanır. /REPLACE:1 yoksa açık Türkçe red (panel önceden
                    // 'Üstüne yaz / Yeni proje / Projeyi aç' sorar); varsa çakışan kayıtların sayfaları yazmadan önce kaldırılır.
                    string ledgerPath = Path.ChangeExtension(full, ".uvp_ledger.json");
                    List<object> ledgerAll = LedgerEntries(ledgerPath);
                    List<int> hitPages = new List<int>();
                    foreach (object entry in ledgerAll)
                    {
                        Dictionary<string, object> past = D(entry);
                        if (S(past, "state") == "REPLACED") continue;      // sayfaları önceki üstüne yazmada kaldırıldı
                        if (S(past, "state") == "FAILED_RECOVERY_REQUIRED" || S(past, "state") == "FAILED_PARTIAL_WRITE")
                            throw new RefusedException("REFUSED_RECOVERY_REQUIRED", "Bu test projesinde geri alınamamış yarım aktarım var ("
                                + S(past, "receipt") + "). Önce EPLAN'da elle temizleyin veya yeni test projesi başlatın; hiçbir nesne yazılmadı.");
                        bool hit = S(past, "package_sha256") == S(pkg, "payload_sha256");
                        // Kaynak sayfa kimliği BELGE + FİZİKSEL SAYFA'dır (EPLAN_ADDIN_PLAN §2); yalnız
                        // numara değildir. Başka bir PDF'in 4. sayfası bu projeye aktarılmış olması, BU
                        // PDF'in 4. sayfasını mükerrer yapmaz — ikisi ayrı kaynak sayfadır ve hedef EPLAN
                        // sayfa adları da ayrıdır. Belge sha'sı bilinmiyorsa (eski defter sürümü) eski davranış
                        // sürer: bilinmeyen veriyle koruma gevşetilmez.
                        string pastDoc = S(past, "document_sha256");
                        string thisDoc = S(pkg, "document_sha256");
                        bool sameDocument = pastDoc == "" || thisDoc == ""
                            || string.Equals(pastDoc, thisDoc, StringComparison.OrdinalIgnoreCase);
                        foreach (object p in A(past.ContainsKey("pages") ? past["pages"] : null))
                            foreach (object po in A(pkg["pages"]))
                                if (Convert.ToInt32(p) == Convert.ToInt32(D(po)["physical_page"])
                                    && (onlyPages.Count == 0 || onlyPages.Contains(Convert.ToString(p))))
                                {
                                    hit |= sameDocument;
                                    if (sameDocument && !hitPages.Contains(Convert.ToInt32(p))) hitPages.Add(Convert.ToInt32(p));
                                }
                        if (hit) replaceHits.Add(past);
                    }
                    receipt["already_imported"] = replaceHits.Count;
                    if (replaceHits.Count > 0 && !replace)
                    {
                        hitPages.Sort();
                        string when = S(replaceHits[0], "utc");
                        DateTime t;
                        if (DateTime.TryParse(when, null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) when = t.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                        throw new RefusedException("REFUSED_ALREADY_IMPORTED", "Bu belge bu projeye zaten aktarılmış: "
                            + Path.GetFileNameWithoutExtension(full) + ", fiziksel sayfa " + (hitPages.Count > 0
                                ? string.Join(", ", hitPages.ConvertAll(delegate(int n) { return n.ToString(); }).ToArray()) : "(aynı paket)")
                            + " (" + replaceHits.Count + " önceki aktarım, ilki " + when + "). Panelden 'Üstüne yaz' ya da 'Yeni proje' seçin"
                            + " (komutla: /REPLACE:1). Hiçbir nesne yazılmadı.");
                    }
                    foreach (Project open in pm.OpenProjects)
                        if (string.Equals(open.ProjectLinkFilePath, full, StringComparison.OrdinalIgnoreCase)) prj = open;
                    if (prj == null) { prj = pm.OpenProject(full); closeProject = true; }
                    receipt["created_project"] = false;
                }
                else
                {
                    string elk = Path.Combine(outDir, "UVP_PDF2P8_" + Io.Stamp() + ".elk");
                    prj = pm.CreateProject(elk, template);
                    closeProject = true;
                    receipt["created_project"] = true;
                }
                receipt["target_project"] = prj.ProjectLinkFilePath;
                readback["test_project"] = prj.ProjectLinkFilePath;
                Trace(c, "proje hazır: " + prj.ProjectLinkFilePath);
                c.Project = prj;
                prj.LockAllObjects();
                receipt["replace_requested"] = replace;
                if (replace && replaceHits.Count > 0) ReplacePrevious(prj, replaceHits, outDir, receipt, log);

                // Sayfa yapisi (= ve +) YALNIZ yeni yaratilan test projesinde kurulur; kullanicinin
                // mevcut hedef projesinin ayari degistirilmez (MAIN.md 9).
                if (Convert.ToBoolean(receipt["created_project"]))
                {
                    EnsurePageStructure(prj, receipt, log);
                    // Y7 seçenek 2 (R2 09.10): hedef cihaz yazısı kapanır, okta yalnız '/5.1' kalır. Yalnız yaratılan test projesi.
                    if (XrefMode() == "chains0")
                    {
                        try { receipt["xref_chains"] = Madde3.Set(new ProjectSettings(prj), Madde3.Chains, "0"); }
                        catch (Exception ex) { receipt["xref_chains"] = ex.GetType().Name + ": " + ex.Message; }
                    }
                    // ÖLÇÜM 09.10 gece (madde 3, output/p8runs/madde3_xref_20261009_015157 kol P/Q): UVP_013 şablonunda = ve +
                    // yapısı proje kurulduktan SONRA açıldığı için projenin iç dizinleri bayat kalıyor; yapılı sayfalardaki aynı
                    // adlı kesinti noktaları EPLAN tarafından EŞLENMİYOR (çapraz referans 0/18; kapat/aç, doküman tipi, tam ad,
                    // UpdateProjectStructureFromSettings, IsStarSource, 10019 etkisiz). Reorganize (Projekt reorganisieren) dizinleri
                    // yeniden kurar: sonrasında kurulan sayfalarda 2/2, var olan projede sonradan da 2/2. IEC_bas001'de yapı
                    // şablonda açık olduğu için gerek yok; yine de zararsız. Yalnız bu koşuda YARATILAN test projesinde çalışır.
                    try
                    {
                        new Eplan.EplApi.HEServices.ProjectManagement().Reorganize(prj, true);
                        receipt["reorganized"] = true;
                        log.Add("proje reorganize edildi (yapı açıldıktan sonra; kesinti çapraz referansı için, 09.10 deneyi)");
                    }
                    catch (Exception ex) { receipt["reorganized"] = ex.GetType().Name + ": " + ex.Message; log.Add("reorganize başarısız: " + ex.Message); }
                }

                // Yazmadan önce çakışma: hedefte aynı tam adlı fonksiyon (kullanıcı nesnesi veya önceki aktarım).
                DMObjectsFinder before = new DMObjectsFinder(prj);
                List<string> conflicts = new List<string>();
                Dictionary<string, string> conflictFamily = new Dictionary<string, string>();
                Dictionary<string, string> conflictTag = new Dictionary<string, string>();
                HashSet<string> conflictPlaced = new HashSet<string>();
                Dictionary<string, HashSet<string>> mainParts = new Dictionary<string, HashSet<string>>();
                c.CustomerParts = CustomerPartDevices(prj, mainParts);
                c.CustomerDevices = new HashSet<string>(c.CustomerParts.Keys);
                foreach (object po in A(pkg["pages"]))
                {
                    // /PAGES dışında kalan sayfa yazılmayacak; çakışma denetimine de girmez (R4-C2).
                    if (onlyPages.Count > 0 && !onlyPages.Contains(Convert.ToString(D(po)["physical_page"]))) continue;
                    foreach (object oo in A(D(po)["objects"]))
                    {
                        Dictionary<string, object> o = D(oo);
                        if (S(o, "kind") != "DEVICE") continue;
                        string name = PackageName(o);
                        if (conflicts.Contains(name)) continue;
                        foreach (string look in TargetNames(o))
                        {
                            FunctionsFilter filter = new FunctionsFilter();
                            filter.Name = look;
                            filter.ExactNameMatching = true;
                            Function[] same = before.GetFunctions(filter);
                            if (same.Length == 0) continue;
                            foreach (Function f in same) if (f.IsPlaced) conflictPlaced.Add(name);
                            conflicts.Add(name);
                            conflictFamily[name] = S(o, "family");
                            conflictTag[name] = S(o, "device_tag");
                            break;
                        }
                    }
                }
                if (conflicts.Count > 0)
                {
                    // Aynı ad, AYNI BELGENİN önceki sayfasından geliyorsa bu kopya değildir: bir klemens
                    // birden çok sayfada görünür. Bu durumda nesne YARDIMCI GÖSTERİM olarak konur
                    // (IsMainFunction=false), ana fonksiyon ve kullanıcı nesnesi değiştirilmez.
                    // Defterde bu belge yoksa (yabancı/kullanıcı nesnesi) red sürer.
                    // Yalnız TAMAMLANMIŞ/KISMİ kayıtlı aktarım "bizim" sayılır; artık nesne yardımcı gösterimle örtülmez.
                    // Ad bazında (Fable ortak 1, B 2.2): defterde bu belgenin aktarımlarının YARATTIĞI adlar (created_names)
                    // "bizim"dir → yardımcı gösterim. Kümede olmayan aynı ad yabancıdır (kullanıcı/başka kaynak nesnesi):
                    // o nesne REJECTED_FOREIGN_SAME_NAME, birleştirilmez. created_names taşımayan eski defter satırı adı
                    // kanıtlamaz (bilinmeyen veriyle koruma gevşetilmez) → ad yabancı sayılır.
                    // 24.10 grup aktarımı (MAIN 24.10, birlikte verilen Anlage'lar tek projede): BİZİM önceki paketimizin (başka belge)
                    // yarattığı ad da yabancı değildir — müşteri aynı cihazı kardeş Anlage sayfasında da çizer (=131+E111-4Q22 =111'de).
                    // Böyle ad `known`a girer (aynı DT'nin başka sayfadaki fonksiyonu); `ours` (toptan red kapısı) aynı belgeye bağlı kalır.
                    bool ours = false;
                    HashSet<string> known = new HashSet<string>(), sibling = new HashSet<string>();
                    foreach (object entry in LedgerEntries(Path.ChangeExtension(prj.ProjectLinkFilePath, ".uvp_ledger.json")))
                    {
                        Dictionary<string, object> e = D(entry);
                        string st = S(e, "state");
                        if (!(st.StartsWith("COMPLETED") || st == "PARTIAL" || st == "" || st == "REPLACED")) continue;   // REPLACED: adları yine bizim
                        bool sameDoc = S(e, "document_sha256") == S(pkg, "document_sha256");
                        if (sameDoc) ours = true;
                        foreach (object n in A(e.ContainsKey("created_names") ? e["created_names"] : null))
                            (sameDoc ? known : sibling).Add(Convert.ToString(n));
                    }
                    // Kardeş paketin cihazında parçalı ana fonksiyon varsa bu paket parçayı ikinci kez yazmaz: müşteri cihazı yolu
                    // (yardımcı fonksiyon, parça yok, makro/kara kutu parçası kaldırılır). Klemens hariç: aynı ad zaten yardımcı gösterim;
                    // şerit etiketi müşteri kümesine girerse şeridin YENİ klemensleri de ana fonksiyonsuz kalırdı.
                    foreach (string name in conflicts)
                    {
                        if (known.Contains(name) || !sibling.Contains(name)) continue;
                        known.Add(name);
                        string tag = conflictTag[name];
                        if (conflictFamily[name] == "terminal" || !mainParts.ContainsKey(tag)) continue;
                        c.SiblingDevices.Add(tag);
                        if (c.CustomerDevices.Add(tag)) c.CustomerParts[tag] = mainParts[tag];
                    }
                    if (c.SiblingDevices.Count > 0)
                    {
                        receipt["sibling_device_names"] = new List<string>(c.SiblingDevices).ToArray();
                        log.Add("Önceki UVP paketinin (başka Anlage) kurduğu parçalı cihaz; sembol aynı cihaza eklenecek, parça yazılmayacak: "
                                + string.Join(", ", new List<string>(c.SiblingDevices).ToArray()));
                    }
                    // 24.10 (MAIN 24.10): müşterinin parça listesiyle kurduğu cihazın adı yabancı DEĞİLDİR: nesne o cihaza sembol
                    // olarak girer (parça yazılmaz, ana fonksiyon olmaz). Red yalnız kalan yabancı adlar içindir.
                    List<string> hard = new List<string>(), customer = new List<string>();
                    foreach (string name in conflicts)
                        // Aynı adın sayfada duran fonksiyonu varsa (çizimci yerleştirmiş) müşteri listesi sayılmaz: red sürer (E2 inceleme B1).
                        if (!known.Contains(name) && c.CustomerDevices.Contains(conflictTag[name]) && !conflictPlaced.Contains(name)) customer.Add(name);
                        else if (!known.Contains(name)) hard.Add(name);   // bizim ad (aynı belge ya da kardeş paket) toptan red sebebi değil
                    if (customer.Count > 0)
                    {
                        receipt["customer_device_names"] = customer.ToArray();
                        log.Add("Müşteri parça listesi cihazı (aynı etiket + parça hedefte var); sembol olarak eklenecek, parça yazılmayacak: "
                                + string.Join(", ", customer.ToArray()));
                    }
                    if (!ours && hard.Count > 0)
                        throw new RefusedException("REFUSED_CONFLICT", "Hedef projede aynı adlı fonksiyon var; ezilmez/çoğaltılmaz: " + string.Join(", ", hard.ToArray()));
                    // Bizim ad: yalnız KLEMENS tekrarı yardımcı gösterimdir (aynı klemens başka sayfada). Aynı cihazın başka
                    // fonksiyonu (bobin ↔ kontak, MKŞ ↔ yardımcı kontak; TROESTER 38 sayfada 42 adın 41'i) kendi rolüyle aynı
                    // DT altında konur: bobin yardımcı yapılırsa cihaz ana fonksiyonsuz kalır (parça taşıyamaz).
                    List<string> sameDevice = new List<string>();
                    foreach (string name in conflicts)
                    {
                        if (customer.Contains(name)) { if (conflictFamily[name] == "terminal") c.Auxiliary.Add(name); }
                        else if (!known.Contains(name)) c.Foreign.Add(name);
                        else if (conflictFamily[name] == "terminal") c.Auxiliary.Add(name);
                        else sameDevice.Add(name);
                    }
                    receipt["auxiliary_displays"] = c.Auxiliary.ToArray();
                    if (sameDevice.Count > 0) receipt["same_device_other_page"] = sameDevice.ToArray();
                    if (c.Foreign.Count > 0) receipt["foreign_same_name"] = c.Foreign.ToArray();
                    log.Add("Aynı belgenin önceki sayfasından gelen ad(lar) yardımcı gösterim olarak konacak: " + string.Join(", ", c.Auxiliary.ToArray()));
                    if (c.Foreign.Count > 0)
                        log.Add("Aynı tam adlı YABANCI fonksiyon (defterde bu belgenin yarattığı ad değil); nesne reddedilecek: " + string.Join(", ", c.Foreign.ToArray()));
                }

                // Güzergâhın uçtan çıkış yönü: kesinti noktasının hangi bağlantı noktasıyla konacağını belirler.
                foreach (object lo in A(pkg["expected_links"]))
                {
                    Dictionary<string, object> link = D(lo);
                    ArrayList poly = A(link.ContainsKey("polyline_mm") ? link["polyline_mm"] : null);
                    if (poly.Count < 2) continue;
                    PointD a0 = P(poly[0]), a1 = P(poly[1]), b0 = P(poly[poly.Count - 1]), b1 = P(poly[poly.Count - 2]);
                    foreach (KeyValuePair<string, string> way in new[] {
                        new KeyValuePair<string, string>(S(link, "a"), Way(a1.X - a0.X, a1.Y - a0.Y)),
                        new KeyValuePair<string, string>(S(link, "b"), Way(b1.X - b0.X, b1.Y - b0.Y)) })
                    {
                        if (!c.Leaving.ContainsKey(way.Key)) c.Leaving[way.Key] = new List<string>();
                        if (!c.Leaving[way.Key].Contains(way.Value)) c.Leaving[way.Key].Add(way.Value);
                    }
                }
                try { c.PartsDb = new MDPartsManagement().OpenDatabase(); }
                catch (Exception ex) { log.Add("Parça veritabanı açılamadı (salt okunur kullanım): " + ex.Message); }
                receipt["macro_enabled"] = useMacros;
                receipt["xref_mode"] = XrefMode();
                if (useMacros) log.Add("Makro yolu bu sürümde kapalıdır: çok fonksiyonlu makro tek kutbun yerine konmaz; /MACRO:1 yok sayıldı.");

                using (LockingStep step = new LockingStep())
                {
                    Trace(c, "yazma başlıyor");
                    undoManager = new UndoManager();
                    undo = undoManager.CreateUndoStep();
                    undo.SetUndoDescription("UVP · PDF paket aktarımı");
                    state = "WRITING";
                    // MAIN 24.10 akşam (seçenek B): çizilemeyen cihaz sayfasız cihaz listesine; sayfalardan ÖNCE (aynı DT'nin sayfa
                    // nesneleri müşteri-cihazı yolundan yardımcı girsin, parça ikinci kez yazılmasın).
                    if (pkg.ContainsKey("device_list_imports"))
                        receipt["device_list_imported"] = ImportDeviceList(c, pkg, createdNames, checklist);
                    foreach (object po in A(pkg["pages"]))
                    {
                        Dictionary<string, object> pd = D(po);
                        string number = Convert.ToString(pd["physical_page"]);
                        if (onlyPages.Count > 0 && !onlyPages.Contains(number)) continue;
                        string doctype = S(pd, "doctype") != "" ? S(pd, "doctype") : PageDoctype;
                        receipt["page_doctype"] = doctype;
                        if (A(pd["objects"]).Count == 0 && Array.IndexOf(SkipEmptyReasons, S(pd, "empty_reason")) >= 0)
                        {
                            Dictionary<string, object> sk = new Dictionary<string, object>();
                            sk["physical_page"] = pd["physical_page"];
                            sk["blatt"] = S(pd, "blatt");
                            sk["reason"] = S(pd, "empty_reason");
                            if (!receipt.ContainsKey("skipped_pages")) receipt["skipped_pages"] = new List<object>();
                            ((List<object>)receipt["skipped_pages"]).Add(sk);
                            log.Add("sayfa kurulmadı: fiziksel " + number + " Blatt " + S(pd, "blatt") + " (" + S(pd, "empty_reason") + ", nesne yok)");
                            continue;
                        }
                        // MAIN 03.10 gece: alt sayfa. Nesne/bağ `subpage` ('a', 'b') taşıyorsa aynı yapıda '<blatt>.<subpage>'
                        // sayfası kurulur; ana sayfa ('' ) önce. Alt sayfa ile ana sayfa aynı adlı kesinti oklarıyla bağlıdır.
                        List<string> subs = new List<string> { "" };
                        foreach (object oo in A(pd["objects"]))
                            if (!subs.Contains(S(D(oo), "subpage"))) subs.Add(S(D(oo), "subpage"));
                        foreach (string sub in subs)
                        {
                            PagePropertyList names = new PagePropertyList();
                            names.DESIGNATION_PLANT = S(pd, "anlage");
                            names.DESIGNATION_LOCATION = S(pd, "einbauort");
                            names.DESIGNATION_DOCTYPE = doctype;   // '=280+E280&EFS1/2.a'; alt sayfa aynı yapıda
                            names.PAGE_COUNTER = S(pd, "blatt") + (sub == "" ? "" : "." + sub);
                            Page pg = new Page();
                            pg.Create(prj, DocumentTypeManager.DocumentType.Circuit, names);   // ad çakışırsa hata: sessiz yeni ad yok
                            c.Created.Add(pg);
                            pages.Add(pg);
                            physical[pg] = Convert.ToInt32(pd["physical_page"]);
                            if (sub != "") subOf[pg] = sub;
                            c.Page = pg;
                            log.Add("sayfa " + pg.Name + " <- fiziksel " + number + (sub == "" ? "" : " alt sayfa " + sub));
                            Dictionary<string, object> gr = new Dictionary<string, object>();
                            gr["page"] = Text(delegate { return pg.Name; });
                            try { pg.GridSize = PageGridMm; gr["grid_mm"] = pg.GridSize; }
                            catch (Exception ex) { gr["error"] = ex.GetType().Name + ": " + ex.Message; }
                            if (!receipt.ContainsKey("page_grid_mm")) receipt["page_grid_mm"] = new List<object>();
                            ((List<object>)receipt["page_grid_mm"]).Add(gr);
                            // MAIN 09.10: müşteri sayfa başlığı ('Reserve') EPLAN sayfa açıklamasına (# 11011); paket alanı yoksa yazılmaz.
                            if (S(pd, "description") != "")
                            {
                                Dictionary<string, object> dr = new Dictionary<string, object>();
                                dr["page"] = Text(delegate { return pg.Name; });
                                dr["description"] = S(pd, "description");
                                try
                                {
                                    MultiLangString d = new MultiLangString();
                                    d.AddString(ISOCode.Language.L___, S(pd, "description"));
                                    pg.Properties.PAGE_NOMINATIOMN = d;
                                    dr["written"] = true;
                                }
                                catch (Exception ex) { dr["written"] = false; dr["error"] = ex.GetType().Name + ": " + ex.Message; }
                                if (!receipt.ContainsKey("page_description_written")) receipt["page_description_written"] = new List<object>();
                                ((List<object>)receipt["page_description_written"]).Add(dr);
                            }
                            // Nesnesiz sayfa boş görünmesin: nedeni kırmızı UVP notu (Kontrol yolu; listeye girmez, eksik değil).
                            string empty = S(pd, "empty_reason");
                            if (sub == "" && empty != "" && A(pd["objects"]).Count == 0)
                            {
                                Dictionary<string, object> cov = pd.ContainsKey("coverage") ? D(pd["coverage"]) : null;
                                List<string> who = new List<string>();
                                if (empty == "SAHA")
                                {
                                    foreach (object fo in A(cov != null && cov.ContainsKey("field_zone_devices") ? cov["field_zone_devices"] : null))
                                        // 'place' serbest etikettir ('T280.1'); yalnız '+Ort' biçimi alınır.
                                        if (S(D(fo), "place").StartsWith("+") && !who.Contains(S(D(fo), "place"))) who.Add(S(D(fo), "place"));
                                    foreach (object to in A(cov != null && cov.ContainsKey("out_of_scope_terminals") ? cov["out_of_scope_terminals"] : null))
                                    {
                                        Dictionary<string, object> ev = D(to).ContainsKey("evidence") ? D(D(to)["evidence"]) : null;
                                        if (S(ev, "location").StartsWith("+") && !who.Contains(S(ev, "location"))) who.Add(S(ev, "location"));
                                    }
                                    who.Sort(StringComparer.Ordinal);
                                }
                                else if (empty == "ISARETSIZ")
                                    foreach (object uo in A(cov != null && cov.ContainsKey("unmarked_devices") ? cov["unmarked_devices"] : null))
                                    {
                                        string dev = S(D(uo), "device");
                                        who.Add("-" + dev.Substring(dev.LastIndexOf('-') + 1));
                                    }
                                string text = empty == "RESERVE" ? "UVP: Bu sayfada pano içi cihaz yok (Reserve)"
                                    : empty == "SAHA" ? "UVP: Bu sayfada pano içi cihaz yok (" + (who.Count == 0 ? "" : string.Join(" ", who.ToArray()) + " ") + "saha cihazları)"
                                    : empty == "ISARETSIZ" ? "UVP: işaretsiz cihaz: " + (who.Count == 0 ? "?" : string.Join(", ", who.ToArray()))
                                    : "UVP: Bu sayfada cihaz yok (" + empty + ")";
                                Dictionary<string, object> mark = new Dictionary<string, object>();
                                mark["kind"] = "BOS_SAYFA";
                                mark["text"] = text;
                                mark["subpage"] = "";
                                mark["draw"] = true;
                                mark["at_mm"] = new object[] { 60.0, 150.0 };   // UVP_013 çizim alanı x 42..384, y 16..292: sol orta
                                Dictionary<string, object> holder = new Dictionary<string, object>();
                                holder["control_marks"] = new object[] { mark };
                                Kontrol.PlaceMarks(prj, pg, holder, "", created, c.Created, controlMarks, log);
                            }
                            foreach (object oo in A(pd["objects"]))
                            {
                                Dictionary<string, object> o = D(oo);
                                if (S(o, "subpage") != sub) continue;
                                string kind = S(o, "kind");
                                Dictionary<string, object> row = new Dictionary<string, object>();
                                row["id"] = S(o, "id");
                                row["kind"] = kind;
                                row["family"] = S(o, "family");
                                row["page"] = number;                       // kaynak fiziksel sayfa
                                row["eplan_page"] = Text(delegate { return pg.Name; });   // hedef EPLAN sayfası
                                int createdMark = c.Created.Count;
                                try
                                {
                                    // Belirsiz uç adı taşıyan nesne yerleştirilmez: eksik/yanlış uçla cihaz kurulmaz.
                                    ArrayList unresolved = A(o.ContainsKey("ambiguous") ? o["ambiguous"] : null);
                                    if (unresolved.Count > 0)
                                    {
                                        Dictionary<string, object> amb = new Dictionary<string, object>();
                                        amb["object_id"] = S(o, "id");
                                        amb["reason"] = "AMBIGUOUS_PIN_NAMES";
                                        amb["detail"] = unresolved;
                                        c.Ambiguous.Add(amb);
                                        throw new InvalidOperationException("Belirsiz uç adı: nesne yerleştirilmedi (paket kaydı).");
                                    }
                                    if (kind == "DEVICE" && c.Foreign.Contains(PackageName(o)))
                                    {
                                        row["reason"] = "REJECTED_FOREIGN_SAME_NAME";
                                        throw new InvalidOperationException("REJECTED_FOREIGN_SAME_NAME: hedefte aynı tam adlı yabancı fonksiyon var ("
                                            + PackageName(o) + "; defterde bu belgenin yarattığı ad değil); yardımcı gösterimle birleştirilmedi.");
                                    }
                                    if (kind == "DEVICE") PlaceDevice(c, o, row, useMacros);
                                    else if (kind == "JUNCTION") PlaceJunction(c, o, row);
                                    else if (kind == "INTERRUPTION" || kind == "POTENTIAL_BOUNDARY") PlaceEnd(c, o, row);
                                    else throw new InvalidOperationException("Tanınmayan nesne türü: " + kind);
                                    created.Add(row);
                                    if (kind == "DEVICE" && !createdNames.Contains(PackageName(o))) createdNames.Add(PackageName(o));
                                }
                                catch (Exception ex)
                                {
                                    row["error"] = ex.GetType().Name + ": " + ex.Message;
                                    // Reddedilen nesnenin yarım konmuş parçaları projede kalmasın; uçları yerleşmiş sayılmasın
                                    // (inceleme R4-C1, 01.10: KEEP_PARTIAL'de önceki uçlar sayfada kalıyor, köşe çiziliyordu).
                                    row["partial_removed"] = RemoveSince(c, createdMark, S(o, "id"));
                                    rejected.Add(row);
                                }
                            }
                            foreach (object lo in A(pkg["expected_links"]))
                            {
                                Dictionary<string, object> link = D(lo);
                                if (S(link, "page") != number || S(link, "subpage") != sub) continue;
                                Dictionary<string, object> lr = new Dictionary<string, object>();
                                lr["a"] = S(link, "a");
                                lr["b"] = S(link, "b");
                                routes.Add(lr);
                                if (!c.SourceMap.ContainsKey(S(link, "a")) || !c.SourceMap.ContainsKey(S(link, "b")))
                                {
                                    lr["skipped"] = "Uçlardan biri yerleşmedi.";
                                    routeErrors.Add(lr["a"] + " — " + lr["b"] + ": uç yerleşmedi");
                                    continue;
                                }
                                PlaceCorners(c, link, lr, routeErrors);
                            }
                            Kablo.Place(c.Project, pg, pd, sub, c.Created, receipt, c.PartsDb, c.Log);   // K28: kablo tanım çizgisi (tellerden sonra); 12.10 parça
                            foreach (KeyValuePair<string, List<Placement>> box in c.BoxGroups)
                            {
                                if (!box.Key.StartsWith(pg.Name + "|") || box.Value.Count < 2) continue;
                                try { new Eplan.EplApi.DataModel.Group().Create(box.Value.ToArray()); }
                                catch (Exception ex) { log.Add("gruplanamadı " + box.Key + ": " + ex.Message); }
                            }
                            PlaceBlackBoxes(c, pg, pd, sub, receipt);   // kullanıcı 12.10 17:30: makrosuz kutu cihazı kara kutu
                            // MAIN 04.10: tamamlanamayan her şey sayfada görünür işaret; satırı kontrol listesine.
                            checklist.AddRange(Kontrol.PlaceMarks(prj, pg, pd, sub, created, c.Created, controlMarks, log));
                            if (listAnlage == null) { listAnlage = S(pd, "anlage"); listEinbauort = S(pd, "einbauort"); }
                        }
                    }
                    if (pages.Count > 0)
                    {
                        try { receipt["checklist"] = Kontrol.WriteList(prj, checklist, listAnlage, listEinbauort, S(receipt, "page_doctype"), c.Created, log); }
                        catch (Exception ex) { receipt["checklist_error"] = ex.GetType().Name + ": " + ex.Message; }
                    }
                    receipt["control_marks"] = controlMarks;
                    Trace(c, "nesneler kondu: " + created.Count + " kondu, " + rejected.Count + " red, " + routeErrors.Count + " güzergâh sorunu");
                    // Y2: makro adları BÜTÜN makrolar konduktan sonra denetlenir (aynı sayfadaki sonraki makro öncekinin adını bozuyor).
                    List<object> nested = CheckMacroNames(c);
                    if (nested.Count > 0)
                    {
                        receipt["name_nested"] = nested;
                        log.Add("NAME_NESTED: " + nested.Count + " makro fonksiyonunun adı yazımdan sonra değişti; yeniden yazıldı (makbuz name_nested)");
                    }
                    // Bütün sayfalar atlandıysa (RESERVE/SAHA) bağ yok; EPLAN boş diziyle çağrılmaz.
                    if (pages.Count > 0) new Generate().Connections(pages.ToArray(), true);
                    log.Add("Generate.Connections(" + pages.Count + " sayfa)");
                    Trace(c, "bağlantılar üretildi");
                    receipt["cross_sections"] = WriteCrossSections(c, pkg);
                    Trace(c, "kesitler yazıldı");
                    // YAZI 04.10: kesit yazısı (CDP) uç yazısından uzak, telin boş yerine.
                    try { MoveCrossSectionPoints(c, pkg, (List<object>)receipt["cross_sections"]); }
                    catch (Exception ex) { log.Add("kesit yeri taşınamadı: " + ex.Message); }
                    // Bağlantı verisi bu andan İTİBAREN tazedir; geri okuma bunu damgalar ki
                    // doğrulayıcı "ağ değişmedi" sonucunu eski veriden çıkarmasın.
                    readback["connections_updated"] = true;
                    readback["connections_update_utc"] = DateTime.UtcNow.ToString("o");
                    bool failed = rejected.Count > 0 || routeErrors.Count > 0 || c.Deviations.Count > 0 || c.Ambiguous.Count > 0;
                    // Kırmızı uyarılı sayfa tamamen yazıldı ama "tam aktarıldı" sayılmaz (PARTIAL daha ağır, önce gelir).
                    state = failed ? "PARTIAL" : riskFlags.Count > 0 ? "COMPLETED_WITH_RISK" : "COMPLETED_UNVERIFIED";
                    if (failed && onFailure == "ROLLBACK")
                    {
                        Trace(c, "kısmi: geri alma başlıyor");
                        undo.DoUndo();
                        Trace(c, "geri alma bitti");
                        // Geri alma bağlantıları YENİDEN ÜRETMEZ: tazelik damgası düşer,
                        // yoksa geri alınmış aktarımın geri okuması "taze" görünür.
                        readback["connections_updated"] = false;
                        readback["connections_stale_reason"] = "ROLLBACK_AFTER_GENERATE";
                        int remaining = 0;
                        foreach (StorableObject item in c.Created) { try { if (item.IsValid) remaining++; } catch (Exception) { } }
                        receipt["rollback_remaining_objects"] = remaining;
                        state = remaining == 0 ? "FAILED_ROLLED_BACK" : "FAILED_RECOVERY_REQUIRED";
                        // Geri alınmış sayfalar geçersizdir: geri okumaya verilmez (geçersiz nesneye erişim).
                        pages.RemoveAll(delegate(Page gone) { try { return !gone.IsValid; } catch (Exception) { return true; } });
                    }
                }

                // R4-C4: yazma bu noktada BİTTİ. Geri okuma ve defter hatası kendi alanına yazılır; genel catch'e düşüp
                // tamamen yazılmış sayfayı geri ALMAZ, durumu (state) değiştirmez.
                Trace(c, "geri okuma başlıyor (" + pages.Count + " sayfa)");
                try
                {
                    readback = Readback.Build(prj, pages, physical, readback);
                    Kablo.Read(pages, readback);                              // K28: kablo + EPLAN'ın atadığı damarlar
                    // Alt sayfa (MAIN 03.10 gece): aynı fiziksel sayfanın ikinci EPLAN sayfası; doğrulayıcı ikisini birlikte okur.
                    foreach (object rp in (IEnumerable)readback["pages"])
                        foreach (KeyValuePair<Page, string> kv in subOf)
                            if (S(D(rp), "name") == Text(delegate { return kv.Key.Name; })) D(rp)["subpage"] = kv.Value;
                }
                catch (Exception ex)
                {
                    receipt["readback_error"] = ex.GetType().FullName + ": " + ex.Message;
                    readback["readback_error"] = receipt["readback_error"];
                    Trace(c, "geri okuma hatası (yazım korunur): " + ex.Message);
                }
                Trace(c, "geri okuma bitti");
                receipt["source_map"] = c.SourceMap;
                // import_ok = yazma tamam (riskli dahil); risk ayrıca state ve page_flags ile söylenir.
                receipt["import_ok"] = state.StartsWith("COMPLETED");
                readback["import_ok"] = state.StartsWith("COMPLETED");
                if (state.StartsWith("COMPLETED") || state == "PARTIAL" || state == "FAILED_RECOVERY_REQUIRED")
                {
                    try { WriteLedger(prj, pkg, outDir, state, physical, createdNames); }
                    catch (Exception ex)
                    {
                        // Nesneler projede kalır; defter yazılamadıysa sonraki aktarımın mükerrer koruması bu kaydı göremez:
                        // makbuzda açıkça yazılır (RefusedException dahil — durum REFUSED_* yapılmaz, yazım gerçekleşti).
                        receipt["ledger_error"] = ex.GetType().FullName + ": " + ex.Message;
                        Trace(c, "defter hatası (yazım korunur): " + ex.Message);
                    }
                }
            }
            catch (RefusedException ex)
            {
                state = ex.State;
                receipt["error"] = ex.Message;
                receipt["import_ok"] = false;
                readback["import_ok"] = false;
            }
            catch (Exception ex)
            {
                receipt["error"] = ex.GetType().FullName + ": " + ex.Message;
                receipt["stack"] = ex.StackTrace;
                receipt["import_ok"] = false;
                readback["import_ok"] = false;
                if (undo != null && c.Created.Count > 0 && rollbackOnError)
                {
                    try
                    {
                        undo.DoUndo();
                        // Geri alma bağlantıları YENİDEN ÜRETMEZ: tazelik damgası düşer,
                        // yoksa geri alınmış aktarımın geri okuması "taze" görünür.
                        readback["connections_updated"] = false;
                        readback["connections_stale_reason"] = "ROLLBACK_AFTER_GENERATE";
                        int remaining = 0;
                        foreach (StorableObject item in c.Created) { try { if (item.IsValid) remaining++; } catch (Exception) { } }
                        receipt["rollback_remaining_objects"] = remaining;
                        state = remaining == 0 ? "FAILED_ROLLED_BACK" : "FAILED_RECOVERY_REQUIRED";
                    }
                    catch (Exception undoError) { receipt["rollback_error"] = undoError.Message; state = "FAILED_RECOVERY_REQUIRED"; }
                Trace(c, "hata yolu bitti: " + state);
                }
                else state = c.Created.Count > 0 ? "FAILED_PARTIAL_WRITE" : "FAILED_NOTHING_WRITTEN";
                // Projede artık nesne kaldıysa defter bunu bilir: sonraki aktarım reddedilir (öz denetim F5-04).
                if ((state == "FAILED_RECOVERY_REQUIRED" || state == "FAILED_PARTIAL_WRITE") && prj != null && ledgerPackage != null)
                {
                    try { WriteLedger(prj, ledgerPackage, outDir, state, physical, createdNames); }
                    catch (Exception ledgerError) { receipt["ledger_error"] = ledgerError.Message; }
                }
            }
            finally
            {
                Trace(c, "makbuz yazılıyor: " + state);
                receipt["state"] = state;
                receipt["created"] = created;
                receipt["rejected"] = rejected;
                receipt["routes"] = routes;
                receipt["route_errors"] = routeErrors;
                receipt["page_flags"] = riskFlags;
                receipt["deviations"] = c.Deviations;
                receipt["ambiguous"] = c.Ambiguous;
                receipt["source_map"] = c.SourceMap;
                receipt["created_object_count"] = c.Created.Count;
                List<string> pageNames = new List<string>();
                // Fiziksel sayfa -> hedef EPLAN sayfası eşlemesi KAYITLI tutulur. Yalnız "bu aktarımda şu
                // sayfalar vardı" listesi yetmez: doğrulayıcı sayfalar arası devamda partnerin HANGİ EPLAN
                // sayfasında olduğunu bilmeli, liste sırasından/yakınlıktan tahmin etmemeli.
                Dictionary<string, object> pageMap = new Dictionary<string, object>();
                foreach (Page pg in pages)
                {
                    string name = Text(delegate { return pg.Name; });
                    pageNames.Add(name);
                    // Alt sayfa ayrı anahtarla ('6.a'); '6' ana sayfa kalır (fiziksel sayfa başına tek değer varsayımı bozulmaz).
                    if (physical.ContainsKey(pg)) pageMap[physical[pg].ToString() + (subOf.ContainsKey(pg) ? "." + subOf[pg] : "")] = name;
                }
                receipt["pages"] = pageNames;
                receipt["page_map"] = pageMap;
                receipt["log"] = log;
                receipt["verification_state"] = "NOT_VERIFIED — python -m analyzer_v3.eplan_verify gerekir";
                if (!readback.ContainsKey("contract"))
                {
                    readback["contract"] = "uvp.pdf2p8.readback";
                    readback["contract_version"] = "2.0";
                    readback["note"] = "Aktarım başlamadan durdu; geri okunacak nesne yok.";
                }
                Io.WriteJson(Path.Combine(outDir, "receipt.json"), receipt);
                Io.WriteJson(Path.Combine(outDir, "readback.json"), readback);
                StringBuilder summary = new StringBuilder();
                summary.AppendLine("Durum    : " + state);
                if (riskFlags.Count > 0)
                    summary.AppendLine("KIRMIZI UYARI (tam aktarılmış sayılmaz): " + string.Join(", ", riskFlags.ToArray()));
                summary.AppendLine("Proje    : " + (receipt.ContainsKey("target_project") ? receipt["target_project"] : "-"));
                summary.AppendLine("Sayfalar : " + (pageNames.Count == 0 ? "-" : string.Join(", ", pageNames.ToArray())));
                summary.AppendLine("Konan nesne: " + created.Count + "   reddedilen: " + rejected.Count
                                   + "   güzergâh sorunu: " + routeErrors.Count + "   konum sapması: " + c.Deviations.Count);
                // Yazı yerleşimi hatası görsel kabul sorunudur; satır içine gömülüp kaybolmasın.
                int labelErrors = 0;
                List<string> dtLabelErrors = new List<string>();
                foreach (object row in created)
                {
                    // Makro DT yazısı (InsertMacro) sessiz düşmesin: makro günlüğündeki hata da yazı yerleşim hatasıdır.
                    IEnumerable macroLog = D(row).ContainsKey("macro_log") ? D(row)["macro_log"] as IEnumerable : null;
                    if (macroLog != null)
                        foreach (object line in macroLog)
                            if (Convert.ToString(line).StartsWith(DtLabelError)) dtLabelErrors.Add(D(row)["id"] + ": " + line);
                    IEnumerable labels = D(row).ContainsKey("labels") ? D(row)["labels"] as IEnumerable : null;
                    if (labels == null) continue;
                    foreach (object lo in labels)
                        if (D(lo).ContainsKey("error")) { labelErrors++; summary.AppendLine("  yazı: " + D(row)["id"] + " " + S(D(lo), "role") + ": " + D(lo)["error"]); }
                }
                foreach (string e in dtLabelErrors) summary.AppendLine("  yazı: " + e);
                labelErrors += dtLabelErrors.Count;
                summary.AppendLine("Yazı yerleşim hatası: " + labelErrors);
                receipt["label_errors"] = labelErrors;
                receipt["dt_label_errors"] = dtLabelErrors;
                if (receipt.ContainsKey("rollback_remaining_objects"))
                    summary.AppendLine("GERİ ALINDI: bu aktarımın yazdığı nesneler kaldırıldı; kalan nesne: "
                                       + receipt["rollback_remaining_objects"] + ". Yukarıdaki sayılar geri almadan ÖNCEKİ durumdur.");
                if (receipt.ContainsKey("error")) summary.AppendLine("HATA/RED: " + receipt["error"]);
                foreach (object row in rejected)
                {
                    Dictionary<string, object> r = D(row);
                    summary.AppendLine("  reddedildi " + r["id"] + ": " + (r.ContainsKey("error") ? r["error"] : ""));
                }
                foreach (string e in routeErrors) summary.AppendLine("  güzergâh: " + e);
                summary.AppendLine();
                summary.AppendLine("Bu makbuz aktarımın ÇALIŞTIĞINI gösterir; doğruluk kararı değildir.");
                summary.AppendLine("Doğrulama: python -m analyzer_v3.eplan_verify --package <paket> --readback readback.json --receipt receipt.json --mapping <eşleme> --out diff.json");
                File.WriteAllText(Path.Combine(outDir, "ozet.txt"), summary.ToString(), new UTF8Encoding(false));
                try { if (undo != null) undo.CloseUndo(); } catch (Exception) { }
                try { if (undoManager != null) undoManager.Dispose(); } catch (Exception) { }
                if (prj != null && closeProject) { try { prj.Close(); } catch (Exception) { } }
            }
            return state.StartsWith("COMPLETED");
        }
    }

    /// <summary>
    /// UvpPdfToP8ImportBatch /LIST:"<liste.json>" /RESULT:"<toplu_sonuc.json>" [/READBACK:1] [/PDF:"<klasör>"]
    /// Tek EPLAN oturumunda birden çok paketi SIRAYLA aktarır (soğuk başlatma ≈ 30-50 sn/sayfa yerine bir kez; ölçüm 02.10).
    /// Liste: {"target":"<elk>"|"", "template":"<.zw9>", "on_failure":"KEEP_PARTIAL", "items":[{"package","mapping","out"}]}.
    /// Her öğe tek-paket eyleminin KENDİSİNDEN geçer (ImportAction.Execute: aynı makbuz, defter, geri okuma, geri alma).
    /// target boşsa ilk öğe şablondan yeni test projesi kurar; sonrakiler onun target_project'ine yazar.
    /// Öğe hatası sonrakileri durdurmaz. READBACK:1 → RESULT klasöründe guncel\ (UvpPdfToP8Readback /UPDATE:1),
    /// PDF → UvpPdfToP8Pdf. RESULT en son yazılır (run_action.ps1 bu dosyayı görünce EPLAN'ı kapatır).
    /// </summary>
    public class ImportBatchAction : IEplAction
    {
        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpPdfToP8ImportBatch";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        static Dictionary<string, object> ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        }

        static int Count(Dictionary<string, object> d, string k)
        {
            return d.ContainsKey(k) && d[k] != null ? ImportAction.A(d[k]).Count : 0;
        }

        public bool Execute(ActionCallingContext ctx)
        {
            string listPath = Io.Param(ctx, "LIST"), resultPath = Io.Param(ctx, "RESULT");
            if (resultPath == "" || File.Exists(resultPath)) return false;     // önceki sonuç ezilmez
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath)));
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["contract"] = "uvp.pdf2p8.import-batch";
            result["contract_version"] = "1.0";
            result["utc"] = DateTime.UtcNow.ToString("o");
            result["list"] = listPath;
            result["dll"] = typeof(ImportBatchAction).Assembly.Location;
            List<object> items = new List<object>();
            result["items"] = items;
            Stopwatch total = Stopwatch.StartNew();
            string target = "";
            try
            {
                Dictionary<string, object> list = ReadJson(listPath);
                if (list == null) throw new InvalidOperationException("LIST dosyası yok: " + listPath);
                target = ImportAction.S(list, "target");
                string template = ImportAction.S(list, "template"), onFailure = ImportAction.S(list, "on_failure");
                // Onay kapısı (MAIN 03.10): varsayılan ONAY YOK; kırmızı bayraklı sayfa yalnız liste ack_risk=true ise
                // (aktar_toplu.ps1 -AckRisk, test projelerinde) yazılır.
                bool ackRisk = list.ContainsKey("ack_risk") && list["ack_risk"] is bool && (bool)list["ack_risk"];
                result["ack_risk"] = ackRisk;
                foreach (object io in ImportAction.A(list.ContainsKey("items") ? list["items"] : null))
                {
                    Dictionary<string, object> item = ImportAction.D(io);
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    string outDir = ImportAction.S(item, "out");
                    row["out"] = outDir;
                    row["target_in"] = target;
                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        if (!Path.IsPathRooted(outDir)) throw new RefusedException("REFUSED_LIST", "out tam yol olmalı: " + outDir);
                        // Execute bu durumda makbuz YAZMADAN döner; eski makbuz bu koşunun sonucu sanılmasın.
                        if (File.Exists(Path.Combine(outDir, "receipt.json")))
                            throw new RefusedException("SKIPPED_RECEIPT_EXISTS", "OUT klasöründe receipt.json var; öğe atlandı.");
                        ActionCallingContext call = new ActionCallingContext();
                        call.AddParameter("PACKAGE", ImportAction.S(item, "package"));
                        call.AddParameter("MAPPING", ImportAction.S(item, "mapping"));
                        call.AddParameter("OUT", outDir);
                        if (target != "") call.AddParameter("TARGET", target);
                        else call.AddParameter("TEMPLATE", template);
                        if (onFailure != "") call.AddParameter("ON_FAILURE", onFailure);
                        if (ackRisk) call.AddParameter("ACK_RISK", "1");
                        new ImportAction().Execute(call);
                        Dictionary<string, object> receipt = ReadJson(Path.Combine(outDir, "receipt.json"));
                        if (receipt == null) throw new InvalidOperationException("Aktarım makbuz yazmadı.");
                        row["state"] = ImportAction.S(receipt, "state");
                        row["import_ok"] = receipt.ContainsKey("import_ok") ? receipt["import_ok"] : null;
                        row["created"] = Count(receipt, "created");
                        row["rejected"] = Count(receipt, "rejected");
                        row["route_errors"] = Count(receipt, "route_errors");
                        row["page_map"] = receipt.ContainsKey("page_map") ? receipt["page_map"] : null;
                        if (receipt.ContainsKey("page_flags")) row["page_flags"] = receipt["page_flags"];
                        if (receipt.ContainsKey("error")) row["error"] = receipt["error"];
                        // İlk proje kuran öğe hedefi belirler (öğe red/başarısız olsa da proje oluştuysa o proje).
                        if (target == "") target = ImportAction.S(receipt, "target_project");
                    }
                    catch (RefusedException ex) { row["state"] = ex.State; row["error"] = ex.Message; }
                    catch (Exception ex) { row["state"] = "BATCH_ITEM_EXCEPTION"; row["error"] = ex.GetType().Name + ": " + ex.Message; }
                    row["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    items.Add(row);
                }
                result["target_project"] = target;
                if (Io.Param(ctx, "READBACK") == "1")
                    result["readback"] = Stage(new CurrentReadbackAction(), target,
                        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultPath)), "guncel"), "readback_current.json", true);
                if (Io.Param(ctx, "PDF") != "")
                    result["pdf"] = Stage(new PdfExportAction(), target, Io.Param(ctx, "PDF"), "pdf_export.json", false);
                result["ok"] = true;          // toplu akış sonuna kadar yürüdü; öğe sonuçları items[].state'te
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            result["seconds"] = Math.Round(total.Elapsed.TotalSeconds, 1);
            Io.WriteJson(resultPath, result);
            return Convert.ToBoolean(result["ok"]);
        }

        /// <summary>Geri okuma / PDF eylemini aynı oturumda çağırır; sonucu kendi JSON'undan özetler.</summary>
        static Dictionary<string, object> Stage(IEplAction action, string project, string outDir, string file, bool update)
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["out"] = outDir;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                if (project == "") throw new InvalidOperationException("Hedef proje oluşmadı; atlandı.");
                // Eski sonuç bu koşunun sonucu sanılmasın.
                if (File.Exists(Path.Combine(outDir, file))) throw new InvalidOperationException(file + " zaten var; atlandı. Yeni klasör verin.");
                ActionCallingContext call = new ActionCallingContext();
                call.AddParameter("PROJECT", project);
                call.AddParameter("OUT", outDir);
                if (update) call.AddParameter("UPDATE", "1");
                action.Execute(call);
                Dictionary<string, object> r = ReadJson(Path.Combine(outDir, file));
                if (r == null) throw new InvalidOperationException(file + " yazılmadı (klasörde önceki kayıt olabilir).");
                s["ok"] = r.ContainsKey("ok") ? r["ok"] : null;
                if (r.ContainsKey("error")) s["error"] = r["error"];
                if (r.ContainsKey("pdf")) s["pdf"] = r["pdf"];
            }
            catch (Exception ex) { s["ok"] = false; s["error"] = ex.GetType().Name + ": " + ex.Message; }
            s["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
            return s;
        }
    }

    /// <summary>Uç adı eşleşmesi (analyzer_v3/pins.py ile aynı kural): birebir → gevşek → varyant.</summary>
    internal static class Pins
    {
        internal static bool NamesMatch(string a, string b)
        {
            if (a == null || b == null) return false;
            a = a.Trim(); b = b.Trim();
            if (a.Length == 0 || b.Length == 0) return false;
            if (a == b) return true;
            // Kutup işareti ayırıcı değildir: 'L-' ile 'L+' gevşek karşılaştırmada aynı çıkıyordu (ölçüm 2026-09-23).
            if (Polarity(a) != Polarity(b)) return false;
            if (Loose(a) == Loose(b)) return true;
            foreach (string left in Variants(a))
                foreach (string right in Variants(b))
                    if (left == right) return true;
            return false;
        }

        static string Polarity(string name)
        {
            return (name.IndexOf('+') >= 0 ? "+" : "") + (name.IndexOf('-') >= 0 ? "-" : "");
        }

        static string Loose(string name)
        {
            StringBuilder builder = new StringBuilder();
            foreach (char ch in name.ToLowerInvariant())
                if (char.IsLetterOrDigit(ch) || ch == '\'') builder.Append(ch);
            return builder.ToString();
        }

        static List<string> Variants(string name)
        {
            List<string> out_ = new List<string> { name };
            if (name.Length > 6 && name.ToLowerInvariant().EndsWith("intern")) out_.Add(name.Substring(0, name.Length - 6));
            int slash = name.IndexOf('/');
            if (slash > 0) out_.Add(name.Substring(0, slash));
            System.Text.RegularExpressions.Match phase = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)[Ll]\d+$");
            if (phase.Success) out_.Add(phase.Groups[1].Value);
            System.Text.RegularExpressions.Match prefix = System.Text.RegularExpressions.Regex.Match(name, @"^\d+\.(\d+)$");
            if (prefix.Success) out_.Add(prefix.Groups[1].Value);
            return out_;
        }
    }

    /// <summary>Geri okuma 2.0: EPLAN'dan taze okunan fonksiyon, sembol, grafik bağ ve elektriksel bağlantılar.</summary>
    internal static class Readback
    {
        static string Id(StorableObject o) { return ImportAction.Id(o); }

        internal static Dictionary<string, object> Build(Project prj, List<Page> pages, Dictionary<Page, int> physical,
                                                         Dictionary<string, object> into)
        {
            Dictionary<string, object> rb = into ?? new Dictionary<string, object>();
            rb["contract"] = "uvp.pdf2p8.readback";
            rb["contract_version"] = "2.0";
            rb["utc"] = DateTime.UtcNow.ToString("o");
            // Bağlantılar okunmadan ÖNCE tazelendiyse çağıran bunu yazar (Generate.Connections).
            // Yazmadıysa varsayılan YANLIŞ'tır: tazelik iddiası sessizce doğru sayılmaz.
            if (!rb.ContainsKey("connections_updated")) rb["connections_updated"] = false;
            List<object> rbPages = new List<object>();
            Dictionary<string, object> connections = new Dictionary<string, object>();
            DMObjectsFinder finder = new DMObjectsFinder(prj);
            foreach (Page pg in pages) rbPages.Add(PageRows(finder, pg, physical.ContainsKey(pg) ? physical[pg] : -1, connections));
            // ConnectionsFilter.Page kullanımdan kalktı (CS0618) ve ölçümde EKSİK sonuç verdi: kesinti
            // noktasında biten bağlantılar listeye girmiyor, ağ "birleşmemiş" görünüyordu (04/07 koşusu).
            // Bu yüzden proje genelinde filtresiz okunur; sayfa bilgisi bağlantının kendi sayfasından gelir.
            try
            {
                foreach (Connection c in finder.GetConnections(new ConnectionsFilter()))
                {
                    string id = Id(c);
                    if (id == null || connections.ContainsKey(id)) continue;
                    int number = -1;
                    try { if (c.Page != null && physical.ContainsKey(c.Page)) number = physical[c.Page]; }
                    catch (Exception) { }
                    connections[id] = ConnectionRow(c, number);
                }
            }
            catch (Exception ex) { rb["connections_error"] = ex.Message; }
            rb["pages"] = rbPages;
            rb["connections"] = new List<object>(connections.Values);
            List<object> ips = new List<object>();
            HashSet<string> ours = new HashSet<string>();
            foreach (Page pg in pages) ours.Add(Id(pg));
            foreach (InterruptionPoint ip in finder.GetInterruptionPoints(new InterruptionPointsFilter()))
            {
                Page page = null;
                try { page = ip.Page; } catch (Exception) { }
                if (page == null) continue;
                // Bu aktarımın sayfalarındakiler ASIL kayıt; projedeki ÖTEKİ kesinti noktaları da
                // adlarıyla yazılır (yalnız okuma). Doğrulayıcı sayfa aşırı devamda karşı ucun
                // kimliğini ancak adı okunabiliyorsa kanıtlayabilir (ölçüm: 11_kabul koşusu).
                bool mine = ours.Contains(Id(page));
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["in_imported_pages"] = mine;
                r["object_id"] = Id(ip);
                r["page"] = page.Name;
                r["physical_page"] = physical.ContainsKey(page) ? physical[page] : -1;
                r["name"] = ImportAction.Text(delegate { return ip.Name; });
                r["visible_name"] = ImportAction.Text(delegate { return ip.VisibleName; });
                // Y1 10.10: DT yapı alanları (ayrı alan olarak yazıldı mı; '=280+E280-L1').
                r["plant"] = ImportAction.Text(delegate { return ip.Properties.DESIGNATION_PLANT.ToString(); });
                r["place"] = ImportAction.Text(delegate { return ip.Properties.DESIGNATION_LOCATION.ToString(); });
                r["cross_reference"] = ImportAction.Text(delegate { return ip.Properties.INTERRUPTIONPOINT_CROSSREFERENCE.ToString(); });
                r["crossref_destination"] = ImportAction.Text(delegate { return ip.Properties.INTERRUPTIONPOINT_CROSSREFDESTINATION.ToString(); });
                r["destination"] = ImportAction.Text(delegate { return ip.Properties.INTERRUPTIONPOINT_DESTINATION.ToString(); });
                r["source_uid"] = ImportAction.UidOf(ip);
                try { r["location"] = ImportAction.XY(((Placement)ip).Location); } catch (Exception) { }
                ips.Add(r);
            }
            rb["interruption_points"] = ips;
            return rb;
        }

        static Dictionary<string, object> PageRows(DMObjectsFinder finder, Page pg, int number, Dictionary<string, object> connections)
        {
            Dictionary<string, object> rp = new Dictionary<string, object>();
            rp["physical_page"] = number;
            rp["name"] = pg.Name;
            rp["object_id"] = Id(pg);
            List<object> functions = new List<object>();
            HashSet<string> functionIds = new HashSet<string>();
            foreach (Function f in pg.Functions)
            {
                functions.Add(FunctionRow(f));
                functionIds.Add(Id(f));
            }
            rp["functions"] = functions;
            List<object> symbols = new List<object>(), graphical = new List<object>();
            HashSet<string> seen = new HashSet<string>();
            foreach (Placement pl in pg.AllPlacements)
            {
                SymbolReference sr = pl as SymbolReference;
                if (sr == null) continue;
                if (!functionIds.Contains(Id(sr))) symbols.Add(SymbolRow(sr));
                SymbolReference.GraphicalConnection[] links = null;
                try { links = sr.GraphicalConnections; }
                catch (Exception ex) { rp["graphical_error"] = ex.Message; }
                if (links == null) continue;
                foreach (SymbolReference.GraphicalConnection g in links)
                {
                    Dictionary<string, object> start = GraphicEnd(g.StartSymbolRef, g.StartConnectionPoint);
                    Dictionary<string, object> end = GraphicEnd(g.EndSymbolRef, g.EndConnectionPoint);
                    string k1 = start["object_id"] + "#" + start["index"], k2 = end["object_id"] + "#" + end["index"];
                    string key = string.CompareOrdinal(k1, k2) < 0 ? k1 + "|" + k2 : k2 + "|" + k1;
                    if (seen.Add(key))
                        graphical.Add(new Dictionary<string, object> { { "start", start }, { "end", end } });
                }
            }
            rp["symbols"] = symbols;
            rp["graphical_connections"] = graphical;
            try { rp["geometry"] = Geometry(pg); }
            catch (Exception ex) { rp["geometry_error"] = ex.GetType().Name + ": " + ex.Message; }
            ConnectionsFilter filter = new ConnectionsFilter();
            filter.Page = pg;
            foreach (Connection c in finder.GetConnections(filter))
            {
                string id = Id(c) ?? ("page" + number + "_" + connections.Count);
                if (!connections.ContainsKey(id)) connections[id] = ConnectionRow(c, number);
            }
            return rp;
        }

        internal static object Box(PointD[] b)
        {
            if (b == null || b.Length < 2) return null;
            double x0 = Math.Min(b[0].X, b[1].X), y0 = Math.Min(b[0].Y, b[1].Y);
            double x1 = Math.Max(b[0].X, b[1].X), y1 = Math.Max(b[0].Y, b[1].Y);
            return new double[] { Math.Round(x0, 3), Math.Round(y0, 3), Math.Round(x1, 3), Math.Round(y1, 3) };
        }

        /// <summary>MAIN 02.10 görünüm denetimi: sayfadaki yerleşmiş nesnelerin GERÇEK EPLAN kutuları (mm, sayfa
        /// koordinatı, sol alt köken). Gövde = Placement.GetBoundingBox() (sembol/fonksiyon, kesinti, grafik); yazı =
        /// sembolün PropertyPlacement'ı, GetBoundingBox(relative:false) ile mutlak (döndürme dahil). Teller (Connection,
        /// çizgi) AllPlacements'ta yok/atlanır. Salt okuma; okunamayan kutu "box" alanı olmadan nedeniyle kalır.</summary>
        static Dictionary<string, object> Geometry(Page pg)
        {
            Dictionary<string, object> g = new Dictionary<string, object>();
            g["unit"] = "mm";
            try { g["page_size"] = ImportAction.XY(pg.Size); } catch (Exception ex) { g["page_size_error"] = ex.Message; }
            try { g["page_box"] = Box(pg.GetBoundingBox()); } catch (Exception ex) { g["page_box_error"] = ex.Message; }
            try
            {
                Eplan.EplApi.DataModel.MasterData.PlotFrame pf = pg.PlotFrame;
                if (pf != null) { g["plot_frame"] = pf.Name; g["plot_frame_size"] = ImportAction.XY(pf.Size); }
            }
            catch (Exception ex) { g["plot_frame_error"] = ex.Message; }
            List<object> items = new List<object>(), texts = new List<object>();
            foreach (Placement pl in pg.AllPlacements)
            {
                if (pl is Line || pl is PolyLine) continue;
                Dictionary<string, object> it = new Dictionary<string, object>();
                it["object_id"] = Id(pl);
                it["class_name"] = pl.GetType().Name;
                try { it["box"] = Box(pl.GetBoundingBox()); } catch (Exception ex) { it["box_error"] = ex.Message; }
                SymbolReference sr = pl as SymbolReference;
                Function f = pl as Function;
                if (f != null)
                {
                    it["name"] = ImportAction.Text(delegate { return f.Name; });
                    it["source_uid"] = ImportAction.UidOf(f);
                }
                if (sr != null)
                {
                    string type = "";
                    try { SymbolVariant sv = sr.SymbolVariant; it["symbol"] = sv.SymbolName + " #" + sv.VariantNr; type = sv.Parent.Type.ToString(); } catch (Exception) { }
                    it["kind"] = sr is InterruptionPoint ? "INTERRUPTION" : sr is PotentialDefinition ? "POTENTIAL"
                               : type.StartsWith("TNode") ? "T_NODE" : type == "Routing" ? "ROUTING"
                               : f != null ? "FUNCTION" : "SYMBOL";
                    if (sr is InterruptionPoint) it["name"] = ImportAction.Text(delegate { return ((InterruptionPoint)sr).Name; });
                    PropertyPlacement[] subs = null;
                    try { subs = sr.PropertyPlacements; } catch (Exception ex) { it["texts_error"] = ex.Message; }
                    foreach (PropertyPlacement sub in subs ?? new PropertyPlacement[0])
                    {
                        string shown = ImportAction.Text(delegate { return sub.GetDisplayString(); });
                        if (string.IsNullOrEmpty(shown) || shown.Trim() == "") continue;
                        bool visible = true;
                        try { visible = sub.IsVisible; } catch (Exception) { }
                        if (!visible) continue;
                        Dictionary<string, object> t = new Dictionary<string, object>();
                        t["owner"] = it["object_id"];
                        t["text"] = shown;
                        try { t["box"] = Box(sub.GetBoundingBox(false)); }
                        catch (Exception ex) { t["box_error"] = ex.Message; }
                        double[] b = t.ContainsKey("box") ? t["box"] as double[] : null;
                        if (b == null || b[2] - b[0] < 0.01 || b[3] - b[1] < 0.01)
                        {
                            // Ölçüm 02.10: kesinti noktası yazılarının kutusu API'den 0 döner. Kutu Python'da
                            // konum + yazı yüksekliği + hizalamadan TAHMİN edilir (Location sembole göreli).
                            Dictionary<string, object> e = new Dictionary<string, object>();
                            try { PointD o = sr.Location, l = sub.Location; e["at"] = ImportAction.XY(new PointD(o.X + l.X, o.Y + l.Y)); } catch (Exception) { }
                            try { e["height"] = Math.Round(sub.Height, 3); } catch (Exception) { }
                            try { e["justification"] = sub.Justification.ToString(); } catch (Exception) { }
                            try { e["rotation"] = Math.Round(sub.Rotation, 4); } catch (Exception) { }
                            t["estimate"] = e;
                        }
                        texts.Add(t);
                    }
                }
                else
                {
                    TextBase tb = pl as TextBase;
                    it["kind"] = tb != null ? "TEXT" : "GRAPHIC";
                    Eplan.EplApi.DataModel.Graphics.Text tx = pl as Eplan.EplApi.DataModel.Graphics.Text;
                    if (tx != null)
                    {
                        Dictionary<string, object> t = new Dictionary<string, object>();
                        t["owner"] = null;
                        t["text"] = ImportAction.Text(delegate { return tx.Contents.GetAsString(); });
                        t["box"] = it.ContainsKey("box") ? it["box"] : null;
                        texts.Add(t);
                        continue;
                    }
                }
                items.Add(it);
            }
            g["items"] = items;
            g["texts"] = texts;
            return g;
        }

        /// <summary>Salt okunur ölçüm alanı. Okunamazsa satır BOŞ kalır ve nedeni "&lt;key&gt;_error" olarak yazılır;
        /// geri okuma çökmez.</summary>
        static void Probe(Dictionary<string, object> r, string key, Func<string> read)
        {
            try { string v = read(); r[key] = v == null ? "" : v; }
            catch (Exception ex) { r[key] = ""; r[key + "_error"] = ex.Message; }
        }

        static Dictionary<string, object> FunctionRow(Function f)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["object_id"] = Id(f);
            r["name"] = ImportAction.Text(delegate { return f.Name; });
            r["visible_name"] = ImportAction.Text(delegate { return f.VisibleName; });
            // Rötuş denetimi geri okumadan yapılabilsin: kullanıcının yazdığı işlev metni ve sayfa.
            r["function_text"] = ImportAction.Text(delegate { return f.Properties.FUNC_TEXT.ToString(); });
            // ÖLÇÜM KAPISI (bug.md md.3/4): "CE dolu mu, klemens adı nerede?" sorusu ancak bu iki alanla
            // ölçülebilir. #20030 = klemens/pin tanımlayıcı metni, #20009 = CE (tam, uç adı olmadan).
            Probe(r, "terminal_designation", delegate { return f.Properties.FUNC_PINORTERMINALNUMBER.ToString(); });
            Probe(r, "dt_full", delegate { return f.Properties.FUNC_DEVICETAG_FULL.ToString(); });
            r["page"] = ImportAction.Text(delegate { return f.Page.Name; });
            r["class_name"] = f.GetType().Name;
            // Aktarımda yazılan KALICI kaynak kimliği (oturumdan bağımsız): aynı adlı iki klemens
            // ancak bununla ayrı izlenir.
            r["source_uid"] = ImportAction.UidOf(f);
            try { r["main"] = f.IsMainFunction; } catch (Exception) { }
            // Artikel (Fable ortak 6): fonksiyonun parça referansları; paket ↔ EPLAN karşılaştırması (bilgi) bununla yapılır.
            try
            {
                List<object> arts = new List<object>();
                foreach (ArticleReference ar in f.ArticleReferences)
                    arts.Add(new Dictionary<string, object> { { "part_nr", ar.PartNr }, { "variant", ar.VariantNr }, { "count", ar.Count } });
                r["articles"] = arts;
            }
            catch (Exception ex) { r["articles_error"] = ex.Message; }
            try
            {
                FunctionDefinition d = f.FunctionDefinition;
                if (d != null)
                {
                    r["category"] = d.FunctionCategory.ToString();
                    r["group"] = (int)d.Group;
                    r["definition_id"] = (int)d.Id;
                    r["definition_name"] = ImportAction.Text(delegate { return d.Name.GetAsString(); });
                }
            }
            catch (Exception ex) { r["definition_error"] = ex.Message; }
            try { SymbolVariant sv = f.SymbolVariant; if (sv != null) r["symbol"] = sv.SymbolName + " #" + sv.VariantNr; } catch (Exception) { }
            try { r["location"] = ImportAction.XY(f.Location); } catch (Exception) { }
            List<object> pins = new List<object>();
            foreach (Pin pin in f.Pins)
            {
                Dictionary<string, object> p = new Dictionary<string, object>();
                p["name"] = pin.Name;
                p["index"] = pin.Index;
                try { p["direction"] = pin.Direction.ToString(); } catch (Exception) { }
                try { p["location"] = ImportAction.XY(ImportAction.Absolute(f, pin)); } catch (Exception) { }
                List<object> targets = new List<object>();
                try
                {
                    foreach (Pin t in pin.TargetPins)
                        targets.Add(new Dictionary<string, object> { { "object_id", Id(t.ParentFunction) }, { "pin", t.Name } });
                }
                catch (Exception) { }
                p["target_pins"] = targets;
                pins.Add(p);
            }
            r["pins"] = pins;
            return r;
        }

        static Dictionary<string, object> SymbolRow(SymbolReference sr)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["object_id"] = Id(sr);
            r["class_name"] = sr.GetType().Name;
            string type = "", symbol = "";
            try { SymbolVariant sv = sr.SymbolVariant; symbol = sv.SymbolName + " #" + sv.VariantNr; type = sv.Parent.Type.ToString(); } catch (Exception) { }
            r["symbol"] = symbol;
            r["symbol_type"] = type;
            try { r["location"] = ImportAction.XY(((Placement)sr).Location); } catch (Exception) { }
            List<object> points = new List<object>();
            List<string> directions = new List<string>();
            try
            {
                foreach (PinBase cp in sr.GraphicalConnectionPoints)
                {
                    directions.Add(cp.Direction.ToString());
                    points.Add(new Dictionary<string, object> { { "index", cp.Index }, { "direction", cp.Direction.ToString() },
                                                                { "location", ImportAction.XY(ImportAction.Absolute(sr, cp)) } });
                }
            }
            catch (Exception) { }
            r["connection_points"] = points;
            bool perpendicular = directions.Count == 2
                && ((directions[0] == "Up" || directions[0] == "Down") != (directions[1] == "Up" || directions[1] == "Down"));
            r["kind"] = sr is InterruptionPoint ? "INTERRUPTION"
                      : sr is PotentialDefinition ? "POTENTIAL"
                      : type.StartsWith("TNode") ? "T_NODE"
                      : type == "Routing" && perpendicular ? "CORNER" : "OTHER";
            InterruptionPoint ip = sr as InterruptionPoint;
            if (ip != null) r["name"] = ImportAction.Text(delegate { return ip.Name; });
            if (ip != null)
            {
                // Y7 ölçümü (R2): '/' ve çapraz referans yazılarının yapışıklık/konum/hizası. Yalnız okur (yerel kopyaya geçmez).
                List<object> texts = new List<object>();
                try { foreach (Placement pl in ip.PropertyPlacements) if (pl is PropertyPlacement) texts.Add(ImportAction.PlacementRow((PropertyPlacement)pl, sr)); }
                catch (Exception ex) { texts.Add(ex.GetType().Name + ": " + ex.Message); }
                r["placements"] = texts;
            }
            // ÖLÇÜM KAPISI (bug.md md.5): PDP nesneleri bugün geri okumada ADSIZ görünüyor; yazılan
            // PotentialName (#33000) doğrulanabilsin. PotentialDefinition bir Function DEĞİLDİR: CE/#20030 taşımaz.
            PotentialDefinition pd = sr as PotentialDefinition;
            if (pd != null) Probe(r, "potential_name", delegate { return pd.PotentialName; });
            return r;
        }

        static Dictionary<string, object> GraphicEnd(SymbolReference sr, PinBase cp)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["object_id"] = Id(sr);
            r["index"] = cp != null ? cp.Index : -1;
            try { if (sr != null && cp != null) r["location"] = ImportAction.XY(ImportAction.Absolute(sr, cp)); } catch (Exception) { }
            return r;
        }

        static Dictionary<string, object> ConnectionEnd(Pin pin, SymbolReference sr, PinBase cp)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            if (pin != null && pin.ParentFunction != null)
            {
                r["object_id"] = Id(pin.ParentFunction);
                r["pin"] = pin.Name;
                r["index"] = pin.Index;
                r["kind"] = "PIN";
                r["function"] = ImportAction.Text(delegate { return pin.ParentFunction.Name; });
                try { r["page"] = pin.ParentFunction.Page.Name; } catch (Exception) { }
                try { r["location"] = ImportAction.XY(ImportAction.Absolute(pin.ParentFunction, pin)); } catch (Exception) { }
            }
            else if (sr != null)
            {
                r["object_id"] = Id(sr);
                r["pin"] = null;
                r["index"] = cp != null ? cp.Index : -1;
                r["kind"] = sr.GetType().Name;
                try { r["location"] = ImportAction.XY(ImportAction.Absolute(sr, cp)); } catch (Exception) { }
            }
            else
            {
                r["object_id"] = null;
                r["pin"] = null;
            }
            return r;
        }

        static Dictionary<string, object> ConnectionRow(Connection c, int number)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["object_id"] = Id(c);
            r["physical_page"] = number;
            r["start"] = ConnectionEnd(c.StartPin, c.StartSymbolReference, c.StartSymbolConnPoint);
            r["end"] = ConnectionEnd(c.EndPin, c.EndSymbolReference, c.EndSymbolConnPoint);
            List<object> refs = new List<object>();
            try
            {
                foreach (SymbolReference s in c.SymbolReferences)
                {
                    Dictionary<string, object> x = new Dictionary<string, object>();
                    x["object_id"] = Id(s);
                    x["class_name"] = s.GetType().Name;
                    try { x["location"] = ImportAction.XY(((Placement)s).Location); } catch (Exception) { }
                    refs.Add(x);
                }
            }
            catch (Exception ex) { r["symbol_refs_error"] = ex.Message; }
            r["symbol_refs"] = refs;
            List<object> subs = new List<object>();
            try
            {
                foreach (Connection s in c.SubConnections)
                    subs.Add(new Dictionary<string, object> {
                        { "start", ConnectionEnd(s.StartPin, s.StartSymbolReference, s.StartSymbolConnPoint) },
                        { "end", ConnectionEnd(s.EndPin, s.EndSymbolReference, s.EndSymbolConnPoint) } });
            }
            catch (Exception ex) { r["sub_connections_error"] = ex.Message; }
            r["sub_connections"] = subs;
            List<string> pots = new List<string>();
            try { foreach (PotentialDefinition p in c.PotentialDefinitions) pots.Add(p.PotentialName); } catch (Exception) { }
            r["potentials"] = pots;
            // MAIN 03.10: kesit (31002) ve birimi (31059; 1 = mm²) — eplan_verify paketteki bağ kesitiyle karşılaştırır.
            r["cross_section"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRECROSSSECTION.ToString(); });
            r["cross_section_unit"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRECROSSSECTION_UNIT.ToString(); });
            // MAIN 05.10: tel rengi (31004), tel türü (31048) ve tel ürün kodu (bağlantı parça referansı) geri okuma.
            r["wire_color"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRENUMBER.ToString(); });
            r["wire_type"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_WIRETYPE.ToString(); });
            r["wire_part"] = ImportAction.Text(delegate { string s = ""; foreach (ArticleReference ar in c.ArticleReferences) s += (s == "" ? "" : ",") + ar.PartNr; return s; });
            r["description"] = ImportAction.Text(delegate { return c.Properties.CONNECTION_DESCRIPTION.ToMultiLangString().GetStringToDisplay(ISOCode.Language.L___); });
            return r;
        }
    }
}
