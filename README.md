# Signode EPLAN aktarım

Signode'un EPLAN yedeğini (`.zw1`) UVP standardına çevirir. Sıfırdan çizmez, yedeğin içinde çalışır.

| Program yapar | Çizimci yapar |
|---|---|
| Yedeği test kopyasına açar, P8'i okur | Eşleşmeyen pini ve ürün kodunu kuyruktan seçer |
| Ürün kodunu UVP koduna çevirir | Kuralla çıkmayan kesiti seçer |
| Bağlı pinleri makroyla karşılaştırır | Klemensleri yapar |
| Tek telde renk, kesit ve kablo kodu yazar | Sonucu kontrol eder |
| Bağlantı farkı 0 mı doğrular | |
| Renkli kontrol Excel'i üretir | |

Müşteri projesine yazmaz. Yazma yalnız `output\p8runs` altındaki test kopyasına yapılır.

## Gereken

- Windows, EPLAN Pro Panel **2026** ve Claude Code
- Python 3.10 veya üstü
- Firma ana verisi: EPLAN parça veritabanı, makrolar, `UVP_013.zw9` şablonu
- `arsiv\standartlar\UVP Standartları  V1.xlsx`: firma dosyası, depoda yok. Kendin kopyala.

## Kurulum

```
pip install -r requirements.txt
python -X utf8 kur.py
```

`kur.py` eksikleri söyler, profili ve kayıtlı pin kararlarını `output\musteri\signode` altına koyar, eklentiyi derler.

## Kullanım

EPLAN'ı kapat. Claude Code'da depo klasörünü aç ve proje klasörünü ver:

```
/signode-is C:\yol\26SB005
```

Klasörde `Kundendaten\*.zw1` olmalı. `*_Produktion.pdf`, `is-emri-urun-listesi_*.xlsx` ve `.edz` varsa kullanılır.

Claude olmadan aynı akış:

```
python -X utf8 tools/yedek_is/calistir.py --is 26SB005 --zw1 <yedek.zw1> --pano +CC --pdf <pdf>
```

## Pin kuyruğu

```
python -X utf8 tools/yedek_is/pin_kuyruk.py
```

Adres: http://127.0.0.1:8766. Yalnız bu bilgisayardan açılır.

- Tüm projelerin eşleşmeyen pinleri, ürün kodları ve kesitleri tek ekrana düşer.
- Kart kaydedilince karar hemen kalıcı olur.
- Pin ve ürün kodu kararı sonraki projelerde sorulmaz. Kesit kararı yalnız o projede geçerlidir.
- **Bilmiyorum** satırı kuyrukta kalır.
- Adı boş bağlı uçlar eşleştirilmez, **Tanımsız bağlantılı ürünler** listesine düşer.

Kararları EPLAN'a yazmak için:

```
python -X utf8 tools/yedek_is/calistir.py --is 26SB005 --adim uygula
python -X utf8 tools/yedek_is/calistir.py --is 26SB005 --adim dogrula
python -X utf8 tools/yedek_is/calistir.py --is 26SB005 --adim rapor
```

Makro ya da ürün eklediysen önce `--adim olcum`. Çizimciye verilecek yedek için `--adim yedek`.

## Çıktılar

`output\ajan_kayit\<tarih>_<iş>\` altında:

| Dosya | İçerik |
|---|---|
| `ETIKET_KONTROL_<iş>.xlsx` | Etiket, ürün kodu, makro, pin durumu ve yapılacaklar |
| `dogrulama.json` | Bağlantı farkı ve yazılan satır kontrolü |
| `denetim.md` | Bağımsız denetçi raporu |

Yeşil yalnız ürün kodu ve pin adı kontrolüdür. Tel ve kesit bu kontrole dahil değildir.

## Pin kararlarını paylaşma

```
python -X utf8 kur.py --paylas
```

Yerel kararları `ornek\ogrenilen.json` dosyasına kopyalar. Dosyayı commit et. Başkası çekip `python -X utf8 kur.py --yeni-kararlar` çalıştırınca kendi kaydı bu dosyayla değişir.

## Test

```
python -X utf8 -m pytest tools/tests/test_yedek_is.py -q
```

Müşteri verisi isteyen testler veri yoksa atlanır.

## Bilinen sınırlar

- Her EPLAN işi eklentiyi kaydettiği için 3 gizli başlatma alır.
- Yardımcı kontak kuralı son rakama bakar: 1-2 NC, 3-4 NO. `3SU1400-1AA10-3CA0` ve `3RH2911-2HA11` için Siemens uç şeması ayrıca doğrulanmadı.
- Bazı bağlarda EPLAN kablo ürününü reddediyor (S063074). Renk ve kesit yazılır, kod elle eklenir.
- Klemens işi yapılmaz.
