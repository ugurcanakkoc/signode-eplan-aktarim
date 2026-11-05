---
name: signode-is
description: Signode EPLAN yedeğini (.zw1) UVP ürün kodu, pin ve tel kurallarına çevirir. Kullanıcı yedek, PDF ve Excel dosyası ya da klasör verip aktar dediğinde, /signode-is yazdığında ya da yeni Signode/SNCT/SNBP projesi işlenmek istendiğinde kullan. Prompt istemez.
---

# Signode işi

Kullanıcıdan yalnız dosya ya da klasör istenir.

0. `output/musteri/signode/profil.json` yoksa önce `python -X utf8 kur.py` çalıştır. Eksik varsa yalnız onu söyle.
1. Verilen dosya ya da klasörde bul: `.zw1` (gerekli, tek), `.pdf`, malzeme listesi `.xlsx` (iş emri, teklif ya da başka biçim, sütun adından okunur), `.edz`. Yalnız `.zw1` zorunludur. Birden çok `.zw1` varsa adında "test" geçmeyeni seç, emin değilsen sor. İş adı: klasör adı ya da liste adındaki iş numarası, yoksa `.zw1` adı.
2. EPLAN'ın kapalı olduğunu ve `output\ajan_kayit\20261003_troester\EPLAN_KILIT` kilidinin olmadığını doğrula. Açıksa kullanıcıdan kapatmasını iste.
3. `Workflow` aracını `name: "signode-is"` ve `args: {is, zw1, liste, edz, pdf, pano}` ile çalıştır. Kullanıcının bu komutu vermesi akış onayıdır. Pano verilmezse `+CC`; `+CP` profildeki `ek_pano` ile kapsamdadır.
4. Pin kuyruğu kapalıysa arka planda başlat: `python -X utf8 tools/yedek_is/pin_kuyruk.py`. Adres: http://127.0.0.1:8766
5. Bitince kullanıcıya en çok 5 satır ver: yazılan satır, bağlantı farkı, bekleyen pin ve kesit, `ETIKET_KONTROL_<is>.xlsx` yolu, kuyruk adresi. Doğrulama ya da denetim geçmediyse "hazır" deme.

Kullanıcı kuyrukta karar verdikten sonra: `python -X utf8 tools/yedek_is/calistir.py --is <IS> --adim uygula`, sonra `dogrula`, `rapor`. Kullanıcı yeni makro ya da ürün eklediyse önce `--adim olcum`. Çizimciye verilecek yedek: `--adim yedek`.

Kurallar: `KURALLAR.md` (yoksa MAIN.md sonundaki 2026-11-05 maddeleri). Yazma yalnız `output\p8runs` altındaki test kopyasına yapılır. Müşteri projesine dokunulmaz. EPLAN yalnız 2026.
