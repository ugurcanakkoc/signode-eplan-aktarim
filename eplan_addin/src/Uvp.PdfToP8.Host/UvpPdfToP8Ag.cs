// =====================================================================================
// Ag — çizimci araçlarının ortak ağ/geometri yardımcıları (13.10; Zincir ve YenidenBagla kullanır).
//   * Net: sayfanın grafik bağlarından (SymbolReference.GraphicalConnections) birleşik küme. Fonksiyon ucu ayrı düğüm
//     ("id#index"), T/köşe/ok gibi fonksiyon olmayan sembolün bütün noktaları tek düğüm. OrtakHat.Net ile aynı kural;
//     burada yazmadan ÖNCE/SONRA karşılaştırılabilir (yanlış bağ sessiz geçmez).
//   * Junction: kol kümesi → SPECIAL sembol adı (CO köşe; TLRO/TLRU/TOUR/TOUL T; CR dört kollu). Adlar
//     output/p8test/probe/capabilities.json ölçümünden (13.10): CR = "Double junction" Down,Left,Right,Up.
// =====================================================================================
using System;
using System.Collections.Generic;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.MasterData;

namespace Uvp.PdfToP8.Host
{
    internal class Net
    {
        readonly Dictionary<string, string> _parent = new Dictionary<string, string>();
        internal readonly Dictionary<string, string> Kind = new Dictionary<string, string>();

        internal string Root(string k)
        {
            if (!_parent.ContainsKey(k)) _parent[k] = k;
            while (_parent[k] != k) k = _parent[k];
            return k;
        }

        void Union(string a, string b)
        {
            string ra = Root(a), rb = Root(b);
            if (ra != rb) _parent[ra] = rb;
        }

        internal static string Key(SymbolReference sr, PinBase cp)
        {
            string id = ImportAction.Id(sr);
            return sr is Function ? id + "#" + cp.Index : id;
        }

        internal static Net Of(Page page)
        {
            Net n = new Net();
            foreach (Placement pl in page.AllPlacements)
            {
                SymbolReference sr = pl as SymbolReference;
                if (sr == null) continue;
                SymbolReference.GraphicalConnection[] gs = null;
                try { gs = sr.GraphicalConnections; } catch (Exception) { }
                if (gs == null) continue;
                foreach (SymbolReference.GraphicalConnection g in gs)
                {
                    string a = Key(g.StartSymbolRef, g.StartConnectionPoint), b = Key(g.EndSymbolRef, g.EndConnectionPoint);
                    n.Kind[a] = g.StartSymbolRef is Function ? "PIN" : g.StartSymbolRef.GetType().Name;
                    n.Kind[b] = g.EndSymbolRef is Function ? "PIN" : g.EndSymbolRef.GetType().Name;
                    n.Union(a, b);
                }
            }
            return n;
        }

        /// <summary>Verilen anahtarın ağındaki bütün fonksiyon uçları (anahtarın kendisi dahil).</summary>
        internal List<string> Pins(string key)
        {
            string r = Root(key);
            List<string> out_ = new List<string>();
            foreach (string k in new List<string>(_parent.Keys))
                if (Root(k) == r && Kind.ContainsKey(k) && Kind[k] == "PIN") out_.Add(k);
            if (!out_.Contains(key)) out_.Add(key);
            return out_;
        }

        internal int Count(IEnumerable<string> keys)
        {
            HashSet<string> roots = new HashSet<string>();
            foreach (string k in keys) roots.Add(Root(k));
            return roots.Count;
        }
    }

    internal static class Ag
    {
        internal const double Tol = 0.05;
        internal const double Grid = 4.0;

        internal static string Opposite(string d)
        {
            switch (d) { case "Up": return "Down"; case "Down": return "Up"; case "Left": return "Right"; case "Right": return "Left"; }
            return "";
        }

        internal static PointD Step(PointD p, string d, double mm)
        {
            switch (d)
            {
                case "Up": return new PointD(p.X, p.Y + mm);
                case "Down": return new PointD(p.X, p.Y - mm);
                case "Left": return new PointD(p.X - mm, p.Y);
                case "Right": return new PointD(p.X + mm, p.Y);
            }
            throw new InvalidOperationException("Yön bilinmiyor: " + d);
        }

        internal static bool Same(PointD a, PointD b) { return Math.Abs(a.X - b.X) < Tol && Math.Abs(a.Y - b.Y) < Tol; }

        internal static string Arms(params string[] arms)
        {
            List<string> l = new List<string>(arms);
            l.Sort(StringComparer.Ordinal);
            return string.Join(",", l.ToArray());
        }

        /// <summary>Kol kümesine uyan SPECIAL sembol varyantı; yoksa null (not nedeni yazar). Düz geçiş (Left,Right / Down,Up) sembol istemez.</summary>
        internal static SymbolVariant Junction(Project prj, string arms, out string note)
        {
            string name;
            switch (arms)
            {
                case "Left,Right,Up": name = "TLRO"; break;
                case "Down,Left,Right": name = "TLRU"; break;
                case "Down,Right,Up": name = "TOUR"; break;
                case "Down,Left,Up": name = "TOUL"; break;
                case "Down,Left,Right,Up": name = "CR"; break;
                default: name = arms.Split(',').Length == 2 ? "CO" : null; break;
            }
            if (name == null) { note = "kol kümesi için sembol yok: " + arms; return null; }
            return ImportAction.VariantWithDirections(prj, "SPECIAL", name, arms, out note);
        }

        /// <summary>Sembolün bu noktada duran bağlantı noktası (mutlak konum); yoksa null.</summary>
        internal static PinBase PointAt(SymbolReference sr, PointD at)
        {
            PinBase[] cps = null;
            try { cps = sr.SymbolVariant.ConnectionPoints; } catch (Exception) { }
            if (cps == null) return null;
            foreach (PinBase cp in cps) if (Same(ImportAction.Absolute(sr, cp), at)) return cp;
            return null;
        }

        internal static int PointCount(SymbolReference sr)
        {
            try { PinBase[] cps = sr.SymbolVariant.ConnectionPoints; return cps == null ? 0 : cps.Length; } catch (Exception) { return 0; }
        }

        /// <summary>Fonksiyonun adlı ucu (Pins.NamesMatch); birden çok uyan ya da hiç yoksa null, sayı `hits`.</summary>
        internal static Pin PinNamed(Function f, string name, out int hits)
        {
            Pin found = null;
            hits = 0;
            foreach (Pin p in f.Pins)
            {
                string pn = ImportAction.Text(delegate { return p.Name; });
                if (Pins.NamesMatch(pn, name)) { hits++; found = p; }
            }
            return hits == 1 ? found : null;
        }

        internal static string PinName(Function f, Pin p)
        {
            return ImportAction.Text(delegate { return f.Name; }) + ":" + ImportAction.Text(delegate { return p.Name; });
        }
    }
}
