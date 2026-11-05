// =====================================================================================
// UvpCizimciOrtakHat — "Seçilenleri ortak hatta bağla" (çizimci aracı, MAIN 2026-10-06; kural 13.10 15:50'den itibaren TEK)
// "Seçili uçları zincirle" (UvpPdfToP8Zincir.cs) ile AYNI çekirdek: giriş oku en solda, T düğümleri, sağda çıkış oku ya da
// dirsek, onay penceresi + .zw1 yedeği + tek Geri Al. 06.10 sürümü (ok en sağda, "eşli ok" reddi) kaldırıldı — iki ayrı
// davranış kalmadı (kullanıcı 13.10). Eylem adı ve şerit metni sabit (Ribbon.cs'e dokunulmadı).
//
// Eylem: UvpCizimciOrtakHat                        (EPLAN içinde: seçimden — oklar ve/veya cihazlar)
//        UvpCizimciOrtakHat /PROJECT:"<UVP_*.elk>" /IDS:"<kimlik;…>" [/DEVICES:"cihaz:uç,…"] /OUT:"<json>" [/UNDO_CHECK:1] [/ONAY:1]
// =====================================================================================
using Eplan.EplApi.ApplicationFramework;

namespace Uvp.PdfToP8.Host
{
    public class OrtakHatAction : IEplAction
    {
        internal const string RibbonText = "Seçilenleri ortak hatta bağla";
        internal const string TabText = "UVP Çizimci Araçları";
        internal const string GroupText = "Çizim";

        public bool OnRegister(ref string Name, ref int Ordinal)
        {
            Name = "UvpCizimciOrtakHat";
            Ordinal = 20;
            return true;
        }

        public void GetActionProperties(ref ActionProperties actionProperties) { }

        public bool Execute(ActionCallingContext ctx) { return ZincirAction.Run(ctx, RibbonText); }
    }
}
