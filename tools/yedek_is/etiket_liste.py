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
    if cihaz == 'yerlesim':
        return GRI, 'Yerleşim ya da aksesuar: bağlı pin yok'
    if not (olcum or {}).get('found'):
        return TURUNCU, 'Makro ölçümü yok: kontrol et'
    if cihaz['fark']:
        return TURUNCU, 'Eşleşmeyen pini eşleştir'
    if bos:
        return TURUNCU, 'Adı boş bağlı uç: çizimde tanımla'
    return YESIL, ''


LISTE_BASLIK = ('Tür', 'Sayfa', 'Etiket', 'Ürün kodu', 'Problem')
TUR_RENK = {'Ürün kodu': 'F4B183', 'Makro': 'F4B183', 'Pin': 'FFE699', 'Tanımsız pin': 'FFE699', 'Tel kodu': 'BDD7EE', 'Kesit': 'DDEBF7'}
TUR_SIRA = ['Ürün kodu', 'Makro', 'Pin', 'Tanımsız pin', 'Tel kodu', 'Kesit']


def sayfa_adi(a):
    return (a or '').lstrip('&')


def cizimci_listesi(i, r, plan, pl, cih, v, sat):
    """Programın çözemediği her kalem: sayfa, etiket, ürün kodu, problem. Çizimci sayfaya gidip düzeltir. Sıra: ürün, makro, pin, tel, kesit."""
    F = {f['object_id']: f for f in C.fonksiyonlar(r)}
    B = {C.kimlik_sonu(c['object_id']): c for c in r['connections']}
    sayfa_uc, sayfa_cihaz, bos = collections.defaultdict(set), collections.defaultdict(set), collections.defaultdict(list)
    for f in C.fonksiyonlar(r):
        sayfa_cihaz[C.dv(f.get('name'))].add(sayfa_adi(f.get('page')))
    for c in r['connections']:
        for a, b in ((c['start'], c['end']), (c['end'], c['start'])):
            k = C.dv(a.get('function'))
            if a.get('kind') != 'PIN' or not k or C.dv(b.get('function')) == k:
                continue
            if a.get('pin'):
                sayfa_uc[(k, a['pin'])].add(sayfa_adi(a.get('page')))
            elif (F.get(a.get('object_id')) or {}).get('class_name') != 'Terminal' and (F.get(a.get('object_id')) or {}).get('category') != 'Busbar':
                bos[k].append((sayfa_adi(a.get('page')), '%s %s' % (b.get('function') or 'Potansiyel', b.get('pin') or '')))
    L = []
    gor = set()
    for kod, fs in sorted(C.pano_kodlari(r, i.kapsam).items()):
        if kod.lower() in v['db'] or kod in v['pr'].get('yer_tutucu_kodlar', []):
            continue
        aday = C.db_adaylari(kod, v['db'])
        for k in sorted({C.dv(f['name']) for f in fs}):
            L.append(('Ürün kodu', ' '.join(sorted(sayfa_cihaz.get(k, []))[:3]), k, kod, 'Ürün kodu UVP parça veritabanında yok. Doğru kodu seç.' + (' Aday: ' + ' '.join(aday[:3]) if aday else '')))
    for s_ in sat:
        k, kod, yap = s_[0], s_[1], s_[12]
        if yap.startswith('Makro ölçümü') and kod.lower() in v['db']:
            L.append(('Makro', ' '.join(sorted(sayfa_cihaz.get(k, []))[:3]), k, kod, 'Makro ölçülemedi. Ürün ya da makro eksik.'))
    for k, c in sorted(cih.items()):
        if not c['parcalar']:
            continue
        hedef = sorted({h for u in c['parcalar'] for d in (v['ms'].get(u) or {}).get('device_functions') or [] for h in d.get('pins') or [] if h and h.strip('.')} | set(c['uvp']))
        bos_h = [h for h in hedef if h not in c['telli']]
        kod = ' + '.join(c['parcalar'])
        for u in c['fark']:
            if (k, u) in gor:
                continue
            gor.add((k, u))
            sy = ' '.join(sorted(sayfa_uc.get((k, u), []))[:3])
            if not hedef:
                L.append(('Makro', sy, k, kod, 'Bağlı pin %s var ama makroda pin yok.' % u))
            else:
                L.append(('Pin', sy, k, kod, 'Müşteri pini %s makroda yok. Boş makro pinleri: %s' % (u, ' '.join(bos_h[:14]) or 'yok')))
        for sy, karsi in sorted(set(bos.get(k, []))):
            L.append(('Tanımsız pin', sy, k, kod, 'Pinin adı yok. Bağlı olduğu yer: %s' % karsi))
    red = {x['connection_id'] for t in i.turlar() if (t / 'rapor.json').exists() for x in C.json_oku(t / 'rapor.json').get('wires') or [] if x['status'] == 'WRITTEN_ARTICLE_REFUSED'}
    for w in pl['wires']:
        c = B.get(C.kimlik_sonu(w['connection_id'])) or {}
        sy = sayfa_adi((c.get('start') or {}).get('page') or (c.get('end') or {}).get('page'))
        et = '%s %s ↔ %s %s' % (w['ce'][0] or 'Potansiyel', w['ce'][1] or '', w['ce'][2] or 'Potansiyel', w['ce'][3] or '')
        if w['connection_id'] in red:
            L.append(('Tel kodu', sy, et, w['wire_code'], 'EPLAN kablo ürününü kabul etmedi. Renk ve kesit yazılı. Kodu elle ekle: %s' % w['tip']))
        elif not w['wire_code']:
            L.append(('Tel kodu', sy, et, '', 'Standartta kablo kodu yok: %s. Renk ve kesit yazılı.' % w['tip']))
    kablo = __import__('re').compile(v['pr']['kablo_deseni'])
    planli = {C.kimlik_sonu(w['connection_id']) for w in pl['wires']} | {C.kimlik_sonu(t['connection_id']) for t in plan['bekleyen']['tel']}
    for c in r['connections']:
        f1, f2 = c['start'].get('function') or '', c['end'].get('function') or ''
        if C.kimlik_sonu(c['object_id']) in planli or not c.get('cross_section') or not C.gercek_cdp(c, v['cdp'], kablo):
            continue
        ic = [C.pano_ici(x, i.kapsam) for x in (f1, f2)]
        if sum(ic) == 1 and not any(kablo.search(x) for x in (f1, f2) + tuple(v['cdp'].get(C.kimlik_sonu(c['object_id']), []))):
            L.append(('Tel kodu', sayfa_adi(c['start'].get('page') or c['end'].get('page')), '%s %s ↔ %s %s' % (f1, c['start'].get('pin') or '', f2 or 'Potansiyel', c['end'].get('pin') or ''), '',
                      'Pano dışına giden tel, program yazmadı: %s %s. Sahaya gidiyorsa işlem yok, panodaysa kodu ekle.' % (c.get('wire_color') or '', c.get('cross_section'))))
    for t in plan['bekleyen']['tel']:
        et = '%s %s ↔ %s %s' % (t['ce'][0] or 'Potansiyel', t['ce'][1] or '', t['ce'][2] or 'Potansiyel', t['ce'][3] or '')
        ad = {'PE': 'Toprak', 'HARICI': 'Harici gerilim', 'DC_ARTI': '24V artı', 'BELIRSIZ': 'Devre belirsiz'}.get(t.get('aday_devre'), t.get('aday_devre') or '')
        L.append(('Kesit', sayfa_adi(t.get('sayfa')), et, '', 'Kesit okunamadı, yazılmadı. %s%s. Kesit ve rengi ver.' % (ad, ', önerilen renk ' + t['aday_renk'] if t.get('aday_renk') else '')))
    return sorted(L, key=lambda x: (TUR_SIRA.index(x[0]), x[1], x[2], x[4]))


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
            alt = [x for x in cih.values() if x.get('ust') == k] if c else []
            if alt:    # Alt etiketin bağlı pinleri üst cihazın satırındadır.
                c = dict(c, telli=c['telli'] + [u for x in alt for u in x['telli']], fark=c['fark'] + [u for x in alt for u in x['fark']], kontak=c['kontak'] + [u for x in alt for u in x['kontak']])
            govde = '-' not in k.split('+')[-1]
            durum, yap = satir_durumu(kod, v['db'], v['ms'].get(kod), None if govde else (c or 'yerlesim'), bos.get(k, 0), k in kotu, C.kapsam_disi(kod, v['pr']))
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
    _l = collections.Counter(x[0] for x in cizimci_listesi(i, r, i.plan(), pl, cih, v, sat))
    ur = _l['Ürün kodu']
    pn = _l['Pin'] + _l['Makro']
    ta = _l['Tanımsız pin']
    plan = i.plan()
    bek = len(plan['bekleyen']['tel'])
    eksik = C.eksik_kablolar(pl['wires'])
    ka = [x for x in plan.get('haric', []) if str(x.get('kaynak', '')).startswith('Köprü')]
    satirlar = [('%s %s' % (i.ad, i.bilgi['proje_adi']), ''), ('Yeşil yalnız ürün kodu ve pin adı kontrolüdür. Tel ve kesit ayrı bakılır.', ''), ('', ''),
                ('Sıra', 'İş'), ('1 Ürün kodu', '%d kod veritabanında yok. Çizimci Listesi sayfasında Tür = Ürün kodu.' % ur),
                ('2 Pin', '%d pin eşleşmedi. Çizimci Listesi sayfasında Tür = Pin ve Makro. Adı boş bağlı uç: %d, Tür = Tanımsız pin.' % (pn, ta)),
                ('3 Klemens', 'Klemensleri yap. Program klemense dokunmadı. Klemens arası bağ: %d adet, Klemens Arası sayfasında.' % len(ka)),
                ('4 Kesit kontrol', 'Klemens yaparken kesitlere bak. Kesiti verilmemiş tel: %d. Çizimci Listesi sayfasında Tür = Kesit.' % bek),
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
    wx = wb.create_sheet('Çizimci Listesi', 1)
    wx.append(LISTE_BASLIK)
    for c_ in wx[1]:
        c_.font = Font(bold=True, color='FFFFFF')
        c_.fill = PatternFill('solid', fgColor='17324D')
    liste = cizimci_listesi(i, r, plan, pl, cih, v, sat)
    for x_ in liste:
        wx.append(x_)
        dolgu = PatternFill('solid', fgColor=TUR_RENK.get(x_[0], 'FFFFFF'))
        wx.cell(wx.max_row, 1).fill = dolgu
    for j, g in enumerate((16, 26, 34, 30, 74)):
        wx.column_dimensions[get_column_letter(j + 1)].width = g
    wx.freeze_panes = 'A2'
    wx.auto_filter.ref = 'A1:E%d' % max(wx.max_row, 1)
    wk = wb.create_sheet('Klemens Arası')
    wk.append(('Sayfa', 'Klemens 1', 'Klemens 2', 'Yapılacak'))
    for c_ in wk[1]:
        c_.font = Font(bold=True)
    B_ = {c['object_id']: c for c in r['connections']}
    for x_ in ka:
        c0 = B_.get(x_['connection_id']) or next((c for k_, c in B_.items() if C.kimlik_sonu(k_) == C.kimlik_sonu(x_['connection_id'])), {})
        wk.append((sayfa_adi((c0.get('start') or {}).get('page') or (c0.get('end') or {}).get('page')), x_['ce'][0], x_['ce'][2], 'Köprü ya da tel: klemens yaparken karar ver'))
    for j, g in enumerate((26, 26, 26, 44)):
        wk.column_dimensions[get_column_letter(j + 1)].width = g
    wt = wb.create_sheet('Teller')
    wt.append(('Uç 1', 'Uç 2', 'Renk', 'Kesit', 'Tip', 'Kod', 'Kaynak', 'Durum', 'Not'))
    for c in wt[1]:
        c.font = Font(bold=True)
    for w in pl['wires']:
        wt.append(('%s %s' % (w['ce'][0] or '', w['ce'][1] or ''), '%s %s' % (w['ce'][2] or '', w['ce'][3] or ''), w['renk'], w['kesit'], w['tip'], w['wire_code'], (w.get('kaynak') or '').split(' (')[0], 'Yazıldı' if w['wire_code'] else 'Yazıldı, kod yok', w.get('not_') or ''))
    for t in plan['bekleyen']['tel']:
        wt.append(('%s %s' % (t['ce'][0] or '', t['ce'][1] or ''), '%s %s' % (t['ce'][2] or '', t['ce'][3] or ''), t.get('aday_renk') or '', t.get('aday_kesit') or '', '', '', t.get('aday_devre') or '', 'Kesit bekliyor'))
    for j, g in enumerate((30, 30, 8, 7, 18, 10, 26, 16)):
        wt.column_dimensions[get_column_letter(j + 1)].width = g
    wt.freeze_panes = 'A2'
    import cikti
    cikti.metin_yap(wb)
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
