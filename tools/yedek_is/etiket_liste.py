"""Çizimci teslim Excel'i: P8'den etiket ve ürün başına satır. Müşteri listesi ve ERP numarası kullanılmaz, kaynak Excel değişmez.

Yeşil: ürün kodu parça veritabanında, makro var, tüm bağlı uç adları makroyla uyumlu. Tel ve kesit bu kontrole dahil değil. Şüphede yeşil verilmez.
Kullanım: python -X utf8 tools/yedek_is/etiket_liste.py --is <ad>
"""
import argparse
import collections
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cekirdek as C  # noqa: E402

YESIL, TURUNCU, KIRMIZI, GRI = 'Kontrol edildi', 'Çizimci bakacak', 'Arıza', 'Kapsam dışı'
RENK = {YESIL: 'A9D08E', TURUNCU: 'ED7D31', KIRMIZI: 'C00000', GRI: 'D9D9D9'}
BASLIK = ('Etiket', 'Ürün kodu', 'Ürün adı', 'Ürün kodu güncellendi', 'Makro', 'Bağlı pin', 'Makroyla aynı pin', 'Pin güncellendi', 'Kontak kuralı', 'Eşleşmeyen pin', 'Adı boş bağlı uç', 'Durum', 'Yapılacak')


def satir_durumu(kod, db, olcum, cihaz, bos, kotu=False, kapsam_disi=False):
    """Dönüş: (durum, yapılacak). cihaz None: gövde parçası, uç yok."""
    if kotu:
        return KIRMIZI, 'Doğrulama bu satırda kaldı'
    if kapsam_disi:
        return GRI, 'Kablo ya da aksesuar: uç yok'
    if kod.lower() not in db:
        return TURUNCU, 'Ürün kodu parça veritabanında yok: eşleştir'
    if cihaz is None:
        return GRI, 'Gövde parçası: uç yok'
    if not (olcum or {}).get('found'):
        return TURUNCU, 'Makro ölçümü yok: kontrol et'
    if cihaz['fark']:
        return TURUNCU, 'Eşleşmeyen pini eşleştir'
    if bos:
        return TURUNCU, 'Adı boş bağlı uç: çizimde tanımla'
    return YESIL, ''


def bos_say(r):
    F = {f['object_id']: f for f in C.fonksiyonlar(r)}
    say = collections.Counter()
    for c in r['connections']:
        for a, b in ((c['start'], c['end']), (c['end'], c['start'])):
            f = F.get(a.get('object_id')) or {}
            if a.get('kind') == 'PIN' and not a.get('pin') and C.dv(b.get('function')) != C.dv(a.get('function')) and f.get('class_name') != 'Terminal' and f.get('category') != 'Busbar':
                say[C.dv(a.get('function'))] += 1
    return say


def uret(i, cikis):
    import openpyxl
    from openpyxl.styles import Alignment, Font, PatternFill
    from openpyxl.utils import get_column_letter
    v = i.veri()
    d = i._oku('dogrulama.json') or {}
    kal = Path(d.get('kalici') or '', 'readback_current.json')
    r = C.rb_oku(kal) if d.get('kalici') and kal.exists() else v['r']
    pl = i.yazilan_plan()
    pano = i.kapsam
    cih = C.cihaz_karsilastir(r, pano, v['pr'], v['ms'])
    bos = bos_say(r)
    onceki = collections.defaultdict(set)
    for p in pl['parts']:
        onceki[(p['ce'], p['new'])].add(p['old'])
    uc_yaz = collections.defaultdict(list)
    for p in pl['pins']:
        uc_yaz[p['ce']].append('%s→%s' % (p['old'], p['new']))
    kotu = {x[0] for x in (d.get('uc') or {}).get('kotu') or []} | {x[0] for x in (d.get('parca') or {}).get('kotu') or []}
    disarida = __import__('re').compile(v['pr']['klemens_deseni'] + '|' + v['pr']['kablo_deseni'])
    gor, sat = set(), []
    for kod, fs in sorted(C.pano_kodlari(r, pano).items()):
        for k in sorted({C.dv(f['name']) for f in fs}):
            if (k, kod) in gor or disarida.search(k) or any((f.get('class_name') == 'Terminal' and f.get('category') != 'PLCTerminal') for f in fs if C.dv(f['name']) == k):
                continue
            gor.add((k, kod))
            c = cih.get(k)
            govde = '-' not in k.split('+')[-1]
            durum, yap = satir_durumu(kod, v['db'], v['ms'].get(kod), None if govde else (c or dict(fark=[], telli=[], uvp=[])), bos.get(k, 0), k in kotu, C.kapsam_disi(kod, v['pr']))
            ms = v['ms'].get(kod) or {}
            telli = (c or {}).get('telli') or []
            fark = (c or {}).get('fark') or []
            la = C.liste_adaylari(kod, v['liste'])
            ad = (la[0].get('Açıklama') if la else '') or C.urun_aciklama(ms) or ''
            sat.append((k, kod, ad, ' '.join('%s→%s' % (o, kod) for o in sorted(onceki.get((k, kod), ()))) or 'Hayır',
                        'Var' if ms.get('macro') else ('Ölçülmedi' if not ms else 'Yok'), len(telli), len(telli) - len(fark) - len((c or {}).get('kontak') or []),
                        ' '.join(uc_yaz.get(k, [])) or 'Hayır', ' '.join('%s=%s' % x for x in (c or {}).get('kontak') or []), ' '.join(fark), bos.get(k, 0) or '', durum, yap))
    sat.sort(key=lambda s: ({TURUNCU: 1, KIRMIZI: 0, YESIL: 2, GRI: 3}[s[11]], s[0], s[1]))
    wb = openpyxl.Workbook()
    wy = wb.active
    wy.title = 'Yapılacaklar'
    say = collections.Counter(s[11] for s in sat)
    ur = sum(1 for s in sat if s[12].startswith('Ürün'))
    pn = sum(1 for s in sat if s[12].startswith(('Eşleşmeyen', 'Makro')))
    ta = sum(1 for s in sat if s[12].startswith('Adı boş'))
    plan = i.plan()
    bek = len(plan['bekleyen']['tel'])
    eksik = C.eksik_kablolar(pl['wires'])
    ka = [x for x in plan.get('haric', []) if str(x.get('kaynak', '')).startswith('Köprü')]
    satirlar = [('%s %s' % (i.ad, i.bilgi['proje_adi']), ''), ('Yeşil yalnız ürün kodu ve pin adı kontrolüdür. Tel ve kesit ayrı bakılır.', ''), ('', ''),
                ('Sıra', 'İş'), ('1 Ürün kodu', '%d satır eşleşmedi. Etiket Kontrol sayfasında "Ürün kodu" yazan turuncu satırlar.' % ur),
                ('2 Pin', '%d satır eşleşmedi. Turuncu "Eşleşmeyen pin" satırları. Adı boş uç: %d satır.' % (pn, ta)),
                ('3 Klemens', 'Klemensleri yap. Program klemense dokunmadı. Klemens arası bağ: %d adet, köprü ya da tel kararı sende.' % len(ka)),
                ('4 Kesit kontrol', 'Klemens yaparken kesitlere bak. Kesiti verilmemiş tel: %d. Teller sayfasında durum sütunu.' % bek),
                ('', ''), ('Özet', ''), ('Kontrol edildi', say[YESIL]), ('Çizimci bakacak', say[TURUNCU]), ('Arıza', say[KIRMIZI]), ('Kapsam dışı', say[GRI]),
                ('Doğrulama', d.get('sonuc') or 'Yapılmadı'), ('Eksik kablo tipi', ' · '.join('%s (%d tel)' % (x['tip'], x['baglanti']) for x in eksik) or 'Yok')]
    for a, b in satirlar:
        wy.append((a, b))
    wy.column_dimensions['A'].width, wy.column_dimensions['B'].width = 20, 95
    for n in (1, 4, 10):
        wy.cell(n, 1).font = Font(bold=True, size=13 if n == 1 else 11)
    we = wb.create_sheet('Etiket Kontrol')
    we.append(BASLIK)
    for c in we[1]:
        c.font = Font(bold=True, color='FFFFFF')
        c.fill = PatternFill('solid', fgColor='17324D')
        c.alignment = Alignment(wrap_text=True, vertical='center')
    for s in sat:
        we.append(s)
        dolgu = PatternFill('solid', fgColor=RENK[s[11]])
        for c in we[we.max_row]:
            c.fill = dolgu
            if s[11] == KIRMIZI:
                c.font = Font(color='FFFFFF', bold=True)
    for j, g in enumerate((26, 30, 44, 26, 10, 8, 10, 20, 26, 22, 9, 16, 36)):
        we.column_dimensions[get_column_letter(j + 1)].width = g
    we.freeze_panes = 'C2'
    we.auto_filter.ref = 'A1:%s%d' % (get_column_letter(len(BASLIK)), we.max_row)
    wt = wb.create_sheet('Teller')
    wt.append(('Uç 1', 'Uç 2', 'Renk', 'Kesit', 'Tip', 'Kod', 'Kaynak', 'Durum'))
    for c in wt[1]:
        c.font = Font(bold=True)
    for w in pl['wires']:
        wt.append(('%s %s' % (w['ce'][0] or '', w['ce'][1] or ''), '%s %s' % (w['ce'][2] or '', w['ce'][3] or ''), w['renk'], w['kesit'], w['tip'], w['wire_code'], (w.get('kaynak') or '').split(' (')[0], 'Yazıldı' if w['wire_code'] else 'Yazıldı, kod yok'))
    for t in plan['bekleyen']['tel']:
        wt.append(('%s %s' % (t['ce'][0] or '', t['ce'][1] or ''), '%s %s' % (t['ce'][2] or '', t['ce'][3] or ''), t.get('aday_renk') or '', t.get('aday_kesit') or '', '', '', t.get('aday_devre') or '', 'Kesit bekliyor'))
    for j, g in enumerate((30, 30, 8, 7, 18, 10, 26, 16)):
        wt.column_dimensions[get_column_letter(j + 1)].width = g
    wt.freeze_panes = 'A2'
    wb.save(cikis)
    return say, sat


if __name__ == '__main__':
    import calistir as K
    a = argparse.ArgumentParser()
    a.add_argument('--is', dest='is_', required=True)
    a = a.parse_args()
    i = K.Is(a.is_)
    c = i.yol('ETIKET_KONTROL_%s.xlsx' % a.is_)
    oz, sat = uret(i, c)
    print(dict(oz), c)
    for s in sat:
        if s[11] not in (YESIL,):
            print(s[11], s[0], s[1], '|', s[12], s[9], s[8])
