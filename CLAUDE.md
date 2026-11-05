# Claude çalışma yönergesi

Kullanıcıya Türkçe, kısa ve net yaz. Tablo kullan, iç içe açıklama yazma.

Bu depo tek iş yapar: Signode EPLAN yedeğini UVP standardına çevirmek. Önce [README.md](README.md) ve [KURALLAR.md](KURALLAR.md) oku. Yeni proje gelince `signode-is` becerisini kullan.

## Değişmez kurallar

- EPLAN yalnız 2026. Açık bir EPLAN varken gizli EPLAN başlatma.
- Yazma yalnız `output\p8runs` altındaki test kopyasına yapılır. Müşteri projesine ve kaynak Excel'e dokunma.
- Klemense ve çok damarlı kabloya yazma.
- Pin ve kesit kararını AI vermez, kullanıcı `pin_kuyruk.py` ekranında verir. Emin değilsen yazma.
- Doğrulama ya da denetim geçmeden "hazır" deme. Kod testini, EPLAN ölçümünü ve kullanıcı kabulünü ayrı bildir.
- Müşteri verisi (`.zw1`, PDF, liste, `output\`, `arsiv\`) depoya girmez. Müşteri verisini internete gönderme. Yalnız halka açık ürün kodu aranabilir.
- Yakalanan her hata `output\musteri\signode\hata_defteri.json` dosyasına girer. Düzeltmeye tekrar engeli olarak test ekle.

## Kod

`tools/yedek_is/`: `calistir.py` adımlar, `cekirdek.py` kurallar, `devre.py` devre izleme, `eplan.py` EPLAN başlatma, `etiket_liste.py` Excel, `pin_kuyruk.py` kuyruk sunucusu, `cikti.py` rapor.
`eplan_addin/src/Uvp.PdfToP8.Host/`: EPLAN eylemleri. Derleme: `eplan_addin\build\build.bat output\eplan_addin_bin <ad>`.
