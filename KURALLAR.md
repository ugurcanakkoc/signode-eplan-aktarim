# Kurallar

Kaynak: kullanıcı kararları, 2026-11-05. Kod: `tools/yedek_is/cekirdek.py`, `devre.py`.

## Kapsam

- Pano `+CC`, `+CP` ve `+CP1`. Ek pano `ek_pano` ile profilde tanımlıdır.
- Yedekte çalışılır, sıfırdan çizilmez.
- Klemense müdahale edilmez. Çok damarlı kabloya müdahale edilmez, yalnız tek damarlı tel yazılır.
- Ürünü olmayan ve müşteri sayfasında ("Kunde") çizili cihazın teli panoda değildir, yazılmaz.

## Ürün kodu

- Kod parça veritabanında aynıysa değişmez.
- Liste ve veritabanında tek eşleşme varsa UVP koduna çevrilir.
- Kalanı kuyruğa düşer. Verilen karar sonraki projelerde geçerlidir.

## Pin

- Yalnız bağlı pin karşılaştırılır. Bağlantısız pin sorun değildir.
- Bağlı pinde her zaman UVP makrosundaki ad kullanılır.
- Ad aynıysa işlem yok. Tek aday varsa otomatik eşlenir: 1→1/L1, L1→1/L1, T1_1→T1. Kalanı kuyruğa düşer. Karar ürün + müşteri pini anahtarıyla kalıcı tutulur.
- Yardımcı kontak: son rakam işlevdir (1-2 NC, 3-4 NO), ilk rakam sıra numarasıdır. Makro yer tutucu veriyorsa (`.1 .2 .3 .4`) ve işlev adedi yetiyorsa şema numarası korunur, pin eşleşmiş sayılır.
- Adı boş bağlı uç eşleştirilmez. "Tanımsız bağlantılı ürünler" listesine düşer.

## Tek tel

Sıra:

1. Müşteri kesiti varsa korunur. Yalnız kod eklenir. Mavi renk koyu maviye çevrilir: BU→DBU, BUWH→DBUWH.
2. CDP yoksa kesit ve renk proje varsayılanıdır, müşterinin sayılmaz.
3. Kontak zincirinde tek kesit varsa kesit tel boyunca devam eder. İki farklı kesit varsa yazılmaz.
4. Analog modül sinyali: WH 0,75.
5. Kesitsiz 24V ve PLC sinyali: DBU 0,75. Kesitsiz 0V: DBUWH 0,75.
6. Aynı cihazın besleme ucunda eksi tel, artı telin en büyük kesitine çıkar. Kontak ve bobin girmez. Müşteri kesiti değişmez.
7. Toprak (GNYE önerilir), harici gerilim (OG önerilir) ve belirsiz devrede kesiti çizimci verir.

Kablo kodu: `arsiv\standartlar\UVP Standartları  V1.xlsx`, sayfa "NYAF Kablo Kodu ve Bilgileri". Kod yoksa renk ve kesit yazılır, tel eksik kablo listesine düşer.

## Doğrulama

Taban ve yazılmış kopya bağlantıları yeniden üretilip karşılaştırılır. Geçme şartı: bağlantı farkı 0, plan dışı fark 0, yazılan satırlar doğru, ürün sırası korunmuş, okunan bağlantı adedi güncel adede eşit.

## Excel renkleri

| Renk | Anlam |
|---|---|
| Yeşil | Ürün kodu veritabanında, makro ölçümü var, bağlı pinler uyumlu |
| Koyu turuncu | Program doğrulayamadı, çizimci bakacak |
| Kırmızı | Doğrulama bu satırda kaldı |
| Gri | Gövde parçası, kablo ya da aksesuar: pin yok |

Şüphede yeşil verilmez.

## Çizimci sırası

1. Ürün kodu
2. Pin
3. Klemens
4. Klemens yaparken kesit kontrolü
