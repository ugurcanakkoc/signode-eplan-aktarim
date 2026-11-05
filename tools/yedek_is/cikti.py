"""Çıktılar: renkli iş emri listesi, ürün başına karar sayfası, kısa durum raporu. EPLAN'a yazmaz."""
import base64
import collections
import functools
import html
import json
import re
from pathlib import Path

import openpyxl
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter

from cekirdek import art, cihaz_karsilastir, dv, fonksiyonlar, gercek_uclar, kod_govde, liste_adaylari, marka_uyar, nrm, pano_ici, urun_aciklama

YESIL = PatternFill('solid', fgColor='A9D08E')
TURUNCU = PatternFill('solid', fgColor='ED7D31')
GRI = Font(color='7F7F7F')
SAYFA = Path(__file__).with_name('karar_sablon.html')
KALSIN = dict(k='MUSTERI_KALSIN', ad='Müşteri uç adı kalsın')
ELLE = dict(k='ELLE', ad='Ben EPLAN’da elle yapacağım')
BEKLE = dict(k='MAKRO_BEKLE', ad='Ürün ve makro gelince tekrar bak')


LISTE_ESANLAM = {'Üretici Kodu': ('Üretici Kodu', 'Sipariş numarası', 'Tip / Model', 'Bestellnummer', 'Artikelnummer', 'Order number', 'Part number'),
                 'Marka': ('Marka', 'Üretici', 'Hersteller', 'Manufacturer'), 'Açıklama': ('Açıklama', 'Malzeme açıklaması', 'Beschreibung', 'Description'), 'Pano': ('Pano', 'Yapı'), 'Kod': ('Kod',)}


def liste_oku(xlsx):
    """Malzeme listesi: iş emri ya da başka biçim. Başlık satırı ve sütun adı aranır, kaynak değişmez. Dönüş: ortak adlarla satırlar.

    Birden çok sayfa varsa en çok satırlı değil, başlığı tutan ilk uygun sayfa okunur. Bulunamazsa boş liste: kod yalnız parça DB ile karşılaştırılır.
    """
    wb = openpyxl.load_workbook(xlsx, read_only=True, data_only=True)
    try:
        sayfalar = sorted(wb.worksheets, key=lambda w: 'CC' not in w.title)
        for ws in sayfalar:
            sat = list(ws.iter_rows(values_only=True, max_row=5000))
            for n, s in enumerate(sat[:15]):
                bas = [str(x or '').strip() for x in s]
                kod_sutun = [i for i, b in enumerate(bas) if b in LISTE_ESANLAM['Üretici Kodu']]
                if not kod_sutun:
                    continue
                ad = {b: b for b in bas}
                for ortak, es in LISTE_ESANLAM.items():
                    for x in es:
                        if x in bas:
                            ad[x] = ortak
                            break
                sonuc = []
                for v in sat[n + 1:]:
                    if not any(v):
                        continue
                    d = {ad.get(b, b): ('' if x is None else str(x)) for b, x in zip(bas, v) if b}
                    d.setdefault('Kod', '%s.%s' % (d.get('Marka', ''), d.get('Üretici Kodu', '')))
                    if d.get('Üretici Kodu'):
                        sonuc.append(d)
                return sonuc
        return []
    finally:
        wb.close()


def metin_yap(wb):
    """'=' ile başlayan etiket formül sanılır, Excel'de boş ya da hata görünür (denetim 26SB003). Hücre metin olarak kalır."""
    for ws in wb.worksheets:
        for sat in ws.iter_rows():
            for c in sat:
                if c.data_type == 'f':
                    c.data_type = 's'


def is_emri_bicimi(xlsx):
    """Renkli iş emri kopyası yalnız kendi biçiminde üretilir: ilk satır başlık, Kod sütunu var."""
    wb = openpyxl.load_workbook(xlsx, read_only=True, data_only=True)
    try:
        return 'Kod' in [str(x or '').strip() for x in next(wb.worksheets[0].iter_rows(values_only=True))]
    finally:
        wb.close()


def _uc_sorumlusu_degil(ogr):
    return {k['urun'] for k in ogr.get('uc_sorumlu_degil', [])}


def renkli_liste(giris, cikis, r, pano, pr, ms, plan, ogr, dogrulama, eksik=()):
    """Kullanıcının listesini işler. Yeşil: kontrol edildi (aynı ya da güncellendi). Turuncu: eşleşmedi. Gri yazı: P8'de yok.

    Doğrulama geçmediyse ilk satıra KALDI yazılır. Dönüş: sayımlar.
    """
    cih = cihaz_karsilastir(r, pano, pr, ms)
    disarida = re.compile(pr['klemens_deseni'] + '|' + pr['kablo_deseni'])
    hepsi = collections.defaultdict(set)
    for f in fonksiyonlar(r):
        if pano_ici(f['name'], pano):
            for a in f.get('articles') or []:
                hepsi[art(a)].add(dv(f['name']))
    ad_bul = {}
    for pn in hepsi:
        ad_bul.setdefault(nrm(kod_govde(pn)), pn)
    eski = {x['new']: x['old'] for x in plan['parts']}
    uc_guncel = {u for p in plan['pins'] for u in p['urun'].split(' + ')}
    muaf = _uc_sorumlusu_degil(ogr)

    wb = openpyxl.load_workbook(giris)
    ws = wb.worksheets[0]
    bas = {str(c.value or '').strip(): i for i, c in enumerate(ws[1])}
    kod_s = bas['Üretici Kodu']
    marka_s = bas.get('Marka')
    col = ws.max_column
    for i, b in enumerate(['Kontrol', 'P8 ürün kodu', 'Önceki P8 kodu', 'Cihaz', 'Farklı bağlantılı uç', 'Bizdeki uçlar', 'Şema görünümü', 'Not']):
        ws.cell(row=1, column=col + 1 + i, value=b).font = Font(bold=True)
    kullanilan = {ad_bul[nrm(row[kod_s].value)] for row in ws.iter_rows(min_row=2) if nrm(row[kod_s].value) in ad_bul}
    esiz_p8 = [k for k in hepsi if k not in kullanilan and k not in pr.get('yer_tutucu_kodlar', []) and not k.startswith('LAPP.')]
    say = collections.Counter()
    for row in ws.iter_rows(min_row=2):
        kod = row[kod_s].value
        pn = ad_bul.get(nrm(kod))
        if not pn:
            kardes = [k for k in esiz_p8 if marka_s is not None and marka_uyar(k, row[marka_s].value)]
            if kardes:
                durum, renk = 'Eşleşmedi: ürün kodu', TURUNCU
                vals = [durum, '', '', '', '', '', '', 'P8\'de aynı markada listede olmayan kod: %s. Kablo ise dokunulmaz, karar kullanıcıda.' % ' '.join(kardes)]
            else:
                durum, renk = 'P8 pano içinde yok', None
                vals = [durum, '', '', '', '', '', '', 'Şemada kullanılmayan kalem']
        else:
            cihazlar = sorted(hepsi[pn])
            parca_cihaz = [d for d in cihazlar if d in cih and not (pn in muaf and len(cih[d]['parcalar']) > 1)]
            fark = {u for d in parca_cihaz for u in cih[d]['fark']}
            # Parçasız alt cihaz üst cihazın ürünüdür (-15RA7-RA1 -> -15RA7): farkı, ürünün ölçülen uçlarında yoksa bu üründedir.
            if pn not in muaf:
                ust_uc = set(gercek_uclar(ms.get(pn)))
                fark |= {u for k, c in cih.items() if not c['parcalar'] for d in cihazlar if k.startswith(d + '-') for u in c['fark']} - ust_uc
            fark = sorted(fark)
            klemens = all(disarida.search(d) for d in cihazlar)
            p = ms.get(pn) or {}
            sablon = any(any(d.get('pins') or []) for d in p.get('device_functions') or [])
            sema = 'Şema makrosu var' if p.get('has_multiline') else 'Ürün şablonunda uç var' if sablon else 'Klemens/aksesuar' if klemens else 'Uç bilgisi yok'
            notlar = (['Ürün kodu güncellendi'] if pn in eski else []) + (['Uç adı güncellendi'] if pn in uc_guncel else [])
            if fark and not klemens:
                durum, renk = 'Eşleşmedi: bağlantılı uç', TURUNCU
            elif notlar:
                durum, renk = 'Güncellendi', YESIL
            else:
                durum, renk = ('Aynı (klemens/aksesuar, dokunulmadı)' if klemens else 'Aynı'), YESIL
            bizde = sorted({u for d in cihazlar if d in cih for u in cih[d]['uvp']})
            vals = [durum, pn, eski.get(pn, ''), ' '.join(cihazlar[:12]) + (' ...' if len(cihazlar) > 12 else ''), ' '.join(fark), ' '.join(bizde)[:200], sema, '; '.join(notlar)]
        say[durum] += 1
        for c in row[:col]:
            if renk:
                c.fill = renk
            else:
                c.font = GRI
        for i, v in enumerate(vals):
            ws.cell(row=row[0].row, column=col + 1 + i, value=v)
    for i, w in enumerate([30, 26, 22, 44, 26, 46, 22, 44]):
        ws.column_dimensions[get_column_letter(col + 1 + i)].width = w
    ws.freeze_panes = 'C2'
    s2 = wb.create_sheet('P8de var listede yok')
    s2.append(['P8 ürün kodu', 'Cihaz', 'Kontrol'])
    for pn in sorted(set(hepsi) - kullanilan):
        if not pn.startswith('LAPP.'):
            s2.append([pn, ' '.join(sorted(hepsi[pn])[:10]), 'Genel yer tutucu' if pn in pr.get('yer_tutucu_kodlar', []) else 'Listede karşılığı yok'])
            for c in s2[s2.max_row]:
                c.fill = TURUNCU
    s3 = wb.create_sheet('Cihaz uc farki')
    s3.append(['Cihaz etiketi', 'Ürün kodu', 'Farklı bağlantılı uç', 'Bizdeki uçlar'])
    for k, c in sorted(cih.items()):
        if c['fark']:
            s3.append([k, ' '.join(c['parcalar']), ' '.join(c['fark']), ' '.join(c['uvp'])[:200]])
            for q in s3[s3.max_row]:
                q.fill = TURUNCU
    s4 = wb.create_sheet('Eksik kablolar')
    s4.append(['Kesit', 'Renk', 'EPLAN tip no', 'Bağlantı', 'Durum', 'Örnek uçlar', 'Ürün kodu (kullanıcı doldurur)'])
    for x in eksik:
        s4.append([x['kesit'], x['renk'], x['tip'], x['baglanti'], x['durum'], ' | '.join(x['ornek']), ''])
    for s, gen in ((s2, [26, 90, 26]), (s3, [24, 44, 34, 70]), (s4, [8, 8, 24, 10, 24, 90, 24])):
        for c in s[1]:
            c.font = Font(bold=True)
        for i, w in enumerate(gen):
            s.column_dimensions[get_column_letter(i + 1)].width = w
    baslik = None
    if not dogrulama or not dogrulama.get('ok'):
        baslik = 'KALDI: doğrulama geçmedi. Liste hazır değil. ' + '; '.join((dogrulama or {}).get('neden') or ['doğrulama yapılmadı'])
        ws.insert_rows(1)
        ws.cell(row=1, column=1, value=baslik).font = Font(bold=True, color='FFFFFF', size=13)
        for i in range(1, col + 9):
            ws.cell(row=1, column=i).fill = PatternFill('solid', fgColor='C00000')
        ws.freeze_panes = 'C3'
        ws.sheet_properties.tabColor = 'C00000'
    metin_yap(wb)
    wb.save(cikis)
    return dict(say=dict(say), yesil=sum(v for k, v in say.items() if k.startswith(('Aynı', 'Güncellendi'))),
                turuncu=sum(v for k, v in say.items() if k.startswith('Eşleşmedi')), kontrol_disi=say['P8 pano içinde yok'],
                cihaz_fark=sum(1 for c in cih.values() if c['fark']),
                cihaz_ayni=sum(1 for c in cih.values() if c['parcalar'] and c['telli'] and not c['fark']), kaldi=baslik)


# ---------------------------------------------------------------- karar sayfası

def pdf_sayfa_haritasi(pdf):
    """EPLAN sayfa adı -> PDF sayfa no, PDF içindekiler tablosundan. Tablo yoksa boş: sayfa uydurulmaz."""
    if not pdf or not Path(pdf).exists():
        return {}
    import fitz
    with fitz.open(pdf) as d:
        harita = {}
        for lv, t, p in d.get_toc():
            if lv == 2:
                harita.setdefault(t.split(' ')[0], p)
    return harita


@functools.lru_cache(maxsize=2)
def _pdf_metin(pdf):
    import fitz
    with fitz.open(pdf) as d:
        return [p.get_text() for p in d]


def kirp(pdf, sayfa, capa):
    """Cihaz etiketinin çevresi. Sayfa bilinmiyorsa (EPLAN'dan üretilen PDF) etiketin geçtiği ilk sayfa. Etiket yoksa resim yok."""
    if not (pdf and capa):
        return None
    import fitz
    if not sayfa:
        sayfa = next((n + 1 for n, m in enumerate(_pdf_metin(pdf)) if capa in m), None)
        if not sayfa:
            return None
    with fitz.open(pdf) as d:
        p = d[sayfa - 1]
        hits = p.search_for(capa)
        if not hits:
            return None
        h = hits[0]
        x0, y0 = max(0, h.x0 - 170), max(0, h.y0 - 100)
        r = fitz.Rect(x0, y0, min(p.rect.width, x0 + 340), min(p.rect.height, y0 + 220))
        img = p.get_pixmap(matrix=fitz.Matrix(2.2, 2.2), clip=r, colorspace=fitz.csGRAY).tobytes('png')
    return 'data:image/png;base64,' + base64.b64encode(img).decode()


def _kisa_etiket(k):
    m = re.search(r'-[^-:]+$', k or '')
    return m.group(0) if m else ''


def _kanit_bul(kabul, tur, anahtar):
    """Kapının reddettiği ya da AI'nın emin olmadığı satırın gerekçesi. Kullanıcı neden sorulduğunu görür."""
    for s in (kabul or {}).get('red', []):
        if s['tur'] == tur and s['anahtar'] == anahtar:
            return 'Otomatik karar reddedildi: ' + s['neden']
    for s in (kabul or {}).get('belirsiz', []):
        if s['tur'] == tur and s['anahtar'] == anahtar:
            return (s['kanit'] or 'Otomatik karar emin değil') + ' (' + s['karar'] + ')'
    return ''


def sorular(girdi, liste, ms, kabul, pdf):
    """Soruları veriden üretir. Ürün adı önce iş emri listesinin açıklaması, sonra parça DB açıklaması."""
    def ad(kod):
        la = liste_adaylari(kod, liste)
        return (la[0].get('Açıklama') if la else '') or urun_aciklama(ms.get(kod)) or kod

    S = []
    for s in girdi.get('uc', []):
        parcalar = s['urun'].split(' + ')
        uvp, olcum_var = s['uvp_uclari'], all((ms.get(u) or {}).get('found') for u in parcalar)
        if not olcum_var:
            sec, oneri = [BEKLE, KALSIN, ELLE], ''
            neden, bizde = 'Ürün ya da ölçüm yok. Karşılaştırma yapılamaz.', 'Ürün ölçümü yok'
        elif not uvp:
            sec, oneri = [KALSIN, ELLE], 'MUSTERI_KALSIN'
            neden, bizde = 'Bizde yazılacak gerçek uç adı yok. Önerim müşteri adının kalması.', 'Gerçek uç adı yok'
        else:
            sec, oneri = [dict(k='ESLE', ad='Uçları aşağıdaki gibi eşleştir'), KALSIN, ELLE], ''
            neden, bizde = 'Aynı adlı uç yok. Çizime bakarak eşleştir ya da müşteri adını bırak.', '  '.join(uvp)
        pdf_no = next((x.get('pdf') for x in s['musteri_uclari'] if x.get('pdf')), None)
        S.append(dict(tur='uc', anahtar=s['urun'], baslik=' + '.join(ad(u) for u in parcalar), kod=s['urun'], cihazlar=s['cihazlar'],
                      sorun='%d bağlı uç adı farklı.' % len(s['fark']), musteri='  '.join(s['fark']), bizde=bizde,
                      neden=_kanit_bul(kabul, 'uc', s['urun']) or neden, sec=sec, oneri=oneri, sayfa=pdf_no,
                      resim=kirp(pdf, pdf_no, _kisa_etiket(s['cihazlar'][0])), esle={u: '' for u in s['fark']} if uvp else None, hedef=uvp, esle_karar='ESLE'))
    for s in girdi.get('urun_kodu', []):
        adaylar = sorted(set(s['db_adaylari']))
        S.append(dict(tur='urun_kodu', anahtar=s['p8_kod'], baslik=ad(s['p8_kod']), kod=s['p8_kod'], cihazlar=s['cihazlar'],
                      sorun='Bu kod UVP parça veritabanında yok.', musteri=s['p8_kod'],
                      bizde='  '.join(adaylar) or 'Aday yok', neden=_kanit_bul(kabul, 'urun_kodu', s['p8_kod']) or 'Emin eşleşme yok. Değiştirilmedi.',
                      sec=[dict(k='DEGISTIR', ad='Şu kodla değiştir'), dict(k='ELLE', ad='Ben EPLAN’da elle yapacağım')], oneri='',
                      sayfa=s.get('pdf_sayfa'), resim=kirp(pdf, s.get('pdf_sayfa'), _kisa_etiket(s['cihazlar'][0]) if s['cihazlar'] else ''),
                      esle=dict(uvp_kod=''), hedef=adaylar, esle_karar='DEGISTIR'))
    for s in girdi.get('tel', []):
        S.append(dict(tur='tel', anahtar=s['bag'], baslik='Kesitsiz tel', kod=' ↔ '.join(s['uclar']), cihazlar=s['cihaz'],
                      sorun='Telin gerçek kesiti yok, devre sınıfı kesin değil: %s.' % (s.get('aday_devre') or 'bilinmiyor'), musteri=s.get('kanit_ag') or 'Kanıt yok', bizde=' / '.join('%s: %s' % (a['cihaz'], a['aciklama']) for a in s['aday_amper_kaynak']) or 'Amper bilgisi yok',
                      neden=_kanit_bul(kabul, 'tel', s['bag']) or 'Kesit ve rengi cihazın amper/voltajına göre seç.',
                      sec=[dict(k='YAZ', ad='Şu kesit ve renkle yaz'), dict(k='ELLE', ad='Ben EPLAN’da elle yapacağım')], oneri='', sayfa=s.get('pdf_sayfa'),
                      resim=kirp(pdf, s.get('pdf_sayfa'), _kisa_etiket(s['cihaz'][0]) if s['cihaz'] else ''), esle=dict(kesit=s.get('aday_kesit', ''), renk=s.get('aday_renk', '')),
                      hedef=dict(kesit=['0,5', '0,75', '1', '1,5', '2,5', '4', '6', '10'], renk=['DBU', 'DBUWH', 'BK', 'RD', 'OG', 'GNYE']), esle_karar='YAZ'))
    for i, s in enumerate(S):
        s['id'] = 's%d' % i
    return S


def karar_html(cikis, is_adi, pano, S, bilgi):
    t = SAYFA.read_text(encoding='utf-8')
    t = t.replace('__BASLIK__', html.escape('%s %s kararlar' % (is_adi, pano))).replace('__KEY__', re.sub(r'\W', '_', 'uvp_%s_%s' % (is_adi, pano)))
    t = t.replace('__PROJE__', json.dumps(is_adi, ensure_ascii=False))
    t = t.replace('__VERI__', json.dumps(S, ensure_ascii=False)).replace('__BILGI__', json.dumps(bilgi, ensure_ascii=False))
    Path(cikis).write_text(t, encoding='utf-8')


# ---------------------------------------------------------------- durum raporu

def _rapor_durumu(plan, dogrulama):
    if not dogrulama:
        return 'DOĞRULANMADI: hazır değil', '#b3261e'
    if not dogrulama.get('ok'):
        return 'KALDI: hazır değil', '#b3261e'
    if plan['bekleyen']['urun_kodu'] or plan['bekleyen']['uc'] or plan['bekleyen']['tel']:
        return 'DOĞRULAMA GEÇTİ: karar bekleyen satır var, pano hazır değil', '#a35200'
    return 'HAZIR (kullanıcı kabulü ayrı)', '#1b6e3c'


RAPOR_CSS = """
*{box-sizing:border-box}
body{font:15px/1.45 "Segoe UI",sans-serif;max-width:1180px;margin:0 auto;padding:24px 20px 60px;color:#1c2733;background:#fff}
h1{font-size:22px;margin:0 0 10px}
h2{font-size:17px;margin:30px 0 8px;padding-bottom:4px;border-bottom:2px solid #1c2733}
.s{color:#fff;padding:10px 14px;font-weight:600}
.akis{display:grid;grid-template-columns:repeat(6,1fr);margin-top:18px}
.ad{border:1px solid #c9d1d8;border-left-width:0;padding:10px 12px;min-height:104px}
.ad:first-child{border-left-width:1px}
.ad b{display:block;font-size:13px;color:#47535e;font-weight:600}
.ad i{display:block;font-style:normal;font-size:27px;font-weight:700;margin:2px 0;font-variant-numeric:tabular-nums}
.ad span{font-size:12.5px;color:#47535e}
.ad.iyi{border-top:5px solid #1b6e3c}.ad.bekle{border-top:5px solid #e07b00}.ad.kot{border-top:5px solid #b3261e}
table{border-collapse:collapse;width:100%}
th,td{border:1px solid #d9dfe5;padding:6px 8px;text-align:left;vertical-align:top}
th{background:#f0f3f5;font-weight:600}
.y{background:#e3f1e7}.t{background:#fbe4cd}.k{background:#f8d7d3}
@media(max-width:800px){.akis{grid-template-columns:repeat(2,1fr)}}
"""


def durum_raporu(cikis, is_adi, pano, plan, dogrulama, liste_ozet, kabul, sureler, uyarilar, ogrenilen_satir, eksik=(), vk=None, defter=(), urun_reddi=()):
    """Durum raporu. Her kalem kendi satırında. Tek dosya, dış bağlantı yok."""
    e = html.escape
    sonuc, renk = _rapor_durumu(plan, dogrulama)
    dog = dogrulama or {}
    bek = plan['bekleyen']
    kodsuz = [w for w in plan['wires'] if not w['wire_code']]

    def tablo(bas, sat, sinif=None):
        return '<table><tr>' + ''.join('<th>%s</th>' % e(b) for b in bas) + '</tr>' + ''.join(
            '<tr%s>' % (' class=%s' % sinif(r) if sinif and sinif(r) else '') + ''.join('<td>%s</td>' % e(str(x)) for x in r) + '</tr>' for r in sat) + '</table>'

    def adim(ad, sayi, alt, durum):
        return '<div class="ad %s"><b>%s</b><i>%s</i><span>%s</span></div>' % (durum, e(ad), e(str(sayi)), e(alt))

    bag = dog.get('baglanti') or {}
    tel = dog.get('tel') or {}
    gecti = bool(dog.get('ok'))
    karar = len(bek['urun_kodu']) + len(bek['uc']) + len(bek['tel'])
    akis = ''.join([
        adim('1 Uç adı', len(plan['pins']), 'yazıldı. Bekleyen cihaz %d.' % len(bek['uc']), 'bekle' if bek['uc'] else 'iyi'),
        adim('2 Ürün kodu', len(plan['parts']), 'yazıldı. Bekleyen %d.' % len(bek['urun_kodu']), 'bekle' if bek['urun_kodu'] else 'iyi'),
        adim('3 Tek tel', len(plan['wires']), 'yazıldı. Kodsuz %d.' % len(kodsuz), 'bekle' if kodsuz else 'iyi'),
        adim('4 Bağlantı', '%s / %s' % (bag.get('eksik', '-'), bag.get('fazla', '-')), 'eksik / fazla', 'iyi' if gecti else 'kot'),
        adim('5 Doğrulama', '%s/%s' % (tel.get('dogru', '-'), tel.get('plan', '-')), ('GEÇTİ' if gecti else 'KALDI') + ('. %s tel kodsuz.' % tel['urun_reddi'] if tel.get('urun_reddi') else ''), 'iyi' if gecti else 'kot'),
        adim('6 Karar sende', karar, 'satır', 'bekle' if karar else 'iyi')])
    govde = ['<h1>%s %s</h1><div class=s style="background:%s">%s</div><div class=akis>%s</div>' % (e(is_adi), e(pano), renk, e(sonuc), akis)]
    if uyarilar:
        govde.append('<h2>Uyarı</h2>' + tablo(['Uyarı'], [(u,) for u in uyarilar]))
    if eksik:
        govde.append('<h2>Eksik kablolar: %d tip</h2>' % len(eksik) + tablo(['Tip', 'Kesit', 'Renk', 'Tel', 'Durum', 'Örnek'], [
            (x['tip'], x['kesit'], x['renk'], x['baglanti'], x['durum'], (x['ornek'] or [''])[0]) for x in eksik]))
    if urun_reddi:
        govde.append('<h2>EPLAN ürün kabul etmedi: %d tel</h2>' % len(urun_reddi) + tablo(['Bağlantı', 'Yazılan', 'Eklenemeyen kod'], [
            ('%s %s ↔ %s %s' % tuple(w['ce']), w['tip'], w['wire_code']) for w in urun_reddi]))
    kalan = [('Ürün kodu', k) for k in bek['urun_kodu']] + [('Uç', k) for k in bek['uc']] + \
            [('Tel', t.get('connection_id')) for t in bek['tel']] + \
            [('Reddedilen AI kararı', '%s: %s' % (s['anahtar'], s['neden'])) for s in (kabul or {}).get('red', [])] + \
            [('Klemens arası bağ: köprü ya da tel', '%s ↔ %s' % (x['ce'][0], x['ce'][2])) for x in plan.get('haric', []) if str(x.get('kaynak', '')).startswith('Köprü')] + \
            [('Atlanan uç', '%s: %s' % (s['cihaz'], s['neden'])) for s in plan['atlanan']['uc']] + \
            [('Atlanan parça', '%s: %s' % (s['ce'], s['neden'])) for s in plan['atlanan']['parca']]
    if kalan:
        govde.append('<h2>Yapamadım: %d satır</h2>' % len(kalan) + tablo(['Tür', 'Kalem'], kalan))
    if defter:
        acik = sum(d.get('durum') != 'DÜZELTİLDİ' for d in defter)
        govde.append('<h2>Yakalanan hatalar: %d kayıt, %d açık</h2>' % (len(defter), acik) + tablo(
            ['No', 'İş', 'Yakalayan', 'Hata', 'Kök neden', 'Düzeltme', 'Tekrar engeli', 'Durum'],
            [(d.get('no'), d.get('is'), d.get('yakalayan'), d.get('hata'), d.get('kok_neden'), d.get('duzeltme'), d.get('test'), d.get('durum')) for d in defter],
            lambda r: 'y' if r[-1] == 'DÜZELTİLDİ' else 't' if r[-1] == 'DOĞRULANIYOR' else 'k'))
    dt = [('Bağlantı eksik', bag.get('eksik', '-')), ('Bağlantı fazla', bag.get('fazla', '-')), ('Plan dışı fark', dog.get('plan_disi', '-')),
          ('Yeni fonksiyon', len(dog.get('yeni_fonksiyon') or [])), ('Yeni CDP: tel etiketi, tel yazımının sonucu', dog.get('yeni_cdp', '-')), ('Tel satırı', '%s/%s' % (tel.get('dogru', '-'), tel.get('plan', '-'))),
          ('Uç satırı', '%s/%s' % ((dog.get('uc') or {}).get('dogru', '-'), (dog.get('uc') or {}).get('plan', '-'))),
          ('Parça satırı', '%s/%s' % ((dog.get('parca') or {}).get('dogru', '-'), (dog.get('parca') or {}).get('plan', '-')))]
    dt += [('Neden', n) for n in dog.get('neden') or []]
    govde.append('<h2>Doğrulama kapıları</h2>' + tablo(['Kapı', 'Sonuç'], dt))
    if liste_ozet:
        govde.append('<h2>İş emri listesi</h2>' + tablo(['Durum', 'Satır'], [
            ('Yeşil: kontrol edildi', liste_ozet['yesil']), ('Turuncu: eşleşmedi', liste_ozet['turuncu']), ('Gri: P8\'de yok', liste_ozet['kontrol_disi']),
            ('Cihaz: uç farkı var', liste_ozet['cihaz_fark']), ('Cihaz: uç aynı', liste_ozet['cihaz_ayni'])]))
    if vk:
        satirlar = [('Liste kodu DB\'de yok', x['kod'], x['aciklama']) for x in vk['liste_eksik']] + \
                   [('EDZ parçası DB\'de yok', x, '') for x in vk['parca_eksik']] + \
                   [('EDZ @ öneki: DB\'de düz adla var', x, '') for x in vk['at_oneki_db_de_duz']] + \
                   [('EDZ makrosu klasörde yok', x['makro'], x['durum']) for x in vk['makro_eksik']]
        if satirlar:
            govde.append('<h2>Ana veri eksiği: %d kalem</h2>' % len(satirlar) + tablo(['Denetim', 'Kalem', 'Açıklama'], satirlar))
    if ogrenilen_satir:
        govde.append('<h2>Öğrenilen kararla yapılan</h2>' + tablo(['Tür', 'Kalem', 'Kaynak'], ogrenilen_satir))
    if sureler:
        govde.append('<h2>Süre</h2>' + tablo(['Adım', 'dk'], sureler))
    Path(cikis).write_text('<!doctype html><html lang=tr><meta charset=utf-8><title>%s durum</title><style>%s</style>%s</html>' % (e(is_adi), RAPOR_CSS, ''.join(govde)), encoding='utf-8')
    return sonuc


def gercek_uclar_birlesik(parcalar, ms):
    uc = set()
    for u in parcalar:
        uc |= gercek_uclar(ms.get(u))
    return sorted(uc)
