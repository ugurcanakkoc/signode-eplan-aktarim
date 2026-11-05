"""tools/yedek_is: kapılar (sentetik) ve 26SB003 regresyonu (veri varsa; EPLAN gerekmez).

Regresyon verisi depoya girmez. Yol ortam değişkeninden gelir:
  YEDEK_IS_KAYIT = 26SB003 ölçüm klasörü (olcum/, olcum2/ içerir)
  YEDEK_IS_LISTE = is-emri listesi xlsx
  YEDEK_IS_MUSTERI = profil.json ve ogrenilen.json klasörü
Yoksa regresyon atlanır.
"""
import collections
import json
import os
import shutil
import sys
from pathlib import Path

import openpyxl
import pytest

KOK = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(KOK / 'tools' / 'yedek_is'))
import calistir as K  # noqa: E402
import cekirdek as C  # noqa: E402
import cikti  # noqa: E402
import eplan  # noqa: E402

PR = dict(kablo_deseni=r'-\d+W[A-Z]', klemens_deseni=r'-\d*X[A-Z]*\d', klemens_dizi_deseni=r'\d*X[A-Z]*[\d.]*', dc24_klemens_deseni='24X',
          pe_ray=[], dis_arayuz=['-EXT'], ac_isaretleri=['-AC1'], pakete_girmeyen_onekler=['KAB.'], yer_tutucu_kodlar=['YER.001'])
STD = {'DBU-0,75-H05V-K': '100', 'DBUWH-0,75-H05V-K': '101', 'RD-1,5-H07V-K': '102', 'BK-2,5-H07V-K': '103'}


def uc(ad, x=0, y=0):
    return dict(name=ad, location=[x, y])


def fn(oid, ad, pins=(), arts=(), sinif='Function', cat='', sayfa='P/1'):
    return dict(object_id=oid, name=ad, class_name=sinif, category=cat, page=sayfa, location=[0, 0], pins=list(pins),
                articles=[dict(part_nr=a, variant='1', count=1) for a in arts], function_text='')


def bag(oid, f1, p1, f2, p2, kesit='', renk='', o1=None, o2=None):
    return dict(object_id=oid, start=dict(object_id=o1 or f1, pin=p1, index=0, kind='PIN', function=f1, page='P/1'),
                end=dict(object_id=o2 or f2, pin=p2, index=0, kind='PIN', function=f2, page='P/1'),
                symbol_refs=[], cross_section=kesit, wire_color=renk, wire_part='')


def proje():
    fs = [fn('f1', '=A+CC-1K1', [uc('L1', 0), uc('T1', 10)], ['SIE.X1']),
          fn('f2', '=A+CC-2G1:+', [uc('+', 20)], ['WEI.G1']), fn('f3', '=A+CC-2G1:+', [uc('+', 5)]),
          fn('f4', '=A+CC-3K1', [uc('A1'), uc('A2')], ['BIL.Y1']),
          fn('f5', '=A+CC-4K1', [uc('L1'), uc('Z9')], ['SIE.X1']),
          fn('f6', '+CC-X1:1', [uc('a')], ['WAGO.K'], sinif='Terminal'), fn('f7', '+CC-X1:2', [uc('a')], ['WAGO.K'], sinif='Terminal'),
          fn('f8', '=+CC', [], ['MUH.KAB1'])]
    cs = [bag('c1', '=A+CC-1K1', 'L1', '+CC-X1:1', 'a', '6', 'BK', 'f1', 'f6'), bag('c2', '=A+CC-1K1', 'T1', '+CC-X1:2', 'a', '0,75', 'BU', 'f1', 'f7'),
          bag('c3', '=A+CC-2G1:+', '+', '+CC-X1:1', 'a', '0,75', 'BUWH', 'f2', 'f6'), bag('c4', '=A+CC-2G1:+', '+', '+CC-X1:2', 'a', '', '', 'f3', 'f7'),
          bag('c5', '=A+CC-3K1', 'A1', '+CC-X1:1', 'a', '', 'BU', 'f4', 'f6'), bag('c6', '=A+CC-3K1', 'A2', '+CC-5W1', '1', '1,5', '1', 'f4', 'k1'),
          bag('c7', '=A+CC-4K1', 'Z9', '+CC-X1:2', 'a', '2,5', 'BK', 'f5', 'f7')]
    return dict(pages=[dict(functions=fs)], connections=cs)


def olcum():
    return C.olcum_birlestir(dict(parts=[
        dict(part_number='SIE.X1', found=True, part_nr='SIE.X1', device_functions=[dict(pins=['1/L1', '2/T1', '.1'])], functions=[]),
        dict(part_number='WEI.G1', found=True, part_nr='WEI.G1', device_functions=[dict(pins=['+a', '+b'])], functions=[]),
        dict(part_number='BIL.Y1', found=False), dict(part_number='WAGO.K', found=True, part_nr='WAGO.K'),
        dict(part_number='MUH.KAB1', found=True, part_nr='MUH.KAB1')]))


OGR = dict(urun_kodu=[dict(p8_kod='BIL.Y1', uvp_kod='YEN.Y1', kaynak='t', tarih='t')],
           uc=[dict(urun='WEI.G1', karar='ESLE', esleme={'+': ['+a', '+b']}, kaynak='t')], tel=[])
LISTE = [{'Kod': 'Sie.X1', 'Üretici Kodu': 'X1', 'Marka': 'Siemens', 'Açıklama': 'Şalter'}]


def plan(ogr=OGR, kabul=None):
    return C.plan_kur(proje(), '+CC', PR, ogr, 'IS1', LISTE, olcum(), set(), STD, kabul)


def test_ayni_adli_uclar_soldan_saga_atanir():
    p = plan()
    uc_satir = {(s['function_id'], s['new']) for s in p['pins']}
    assert {('f3', '+a'), ('f2', '+b')} <= uc_satir


def test_bilinmeyen_urune_ogrenilen_yayilmaz():
    p = plan()
    # SIE.X1 için öğrenilen uç kararı yok: 1K1 tek adayla çözülür, 4K1 Z9 tek adayı yok, bekler. WEI.G1 kararı yayılmaz.
    assert '=A+CC-4K1' in p['bekleyen']['uc'] and '=A+CC-1K1' not in p['bekleyen']['uc']
    assert not [s for s in p['pins'] if s['old'] == 'Z9']


def test_uc_karari_pin_basina_bilinen_yazilir_bilinmeyen_bekler():
    """Kullanıcı 05.11: bildiklerimi işaretlerim, onları güncelleriz."""
    ogr = dict(OGR, uc=[dict(urun='SIE.X1', karar='ESLE', esleme={'L1': '1/L1', 'T1': '2/T1'}, kaynak='t')])
    p = plan(ogr)
    assert {s['old'] for s in p['pins'] if s['ce'] == '=A+CC-1K1'} == {'L1', 'T1'}
    assert not [s for s in p['pins'] if s['ce'] == '=A+CC-4K1']   # 4K1'de yalnız Z9 bağlı, kararı yok
    assert '=A+CC-4K1' in p['bekleyen']['uc'] and '=A+CC-1K1' not in p['bekleyen']['uc']


def test_tek_aday_pin_otomatik_ikisi_varsa_sorulur():
    assert C.tek_aday('1', ['1/L1', '2/T1']) == '1/L1'
    assert C.tek_aday('L1', ['1/L1', '2/T1']) == '1/L1'
    assert C.tek_aday('T1_1', ['T1', 'T2']) == 'T1'
    assert C.tek_aday('1', ['1/L1', '1/L2']) == ''
    assert C.tek_aday('BU', ['ws1', 'ws2']) == ''
    assert C.tek_aday('', ['1']) == ''
    assert C.tek_aday('1', ['L+', 'M']) == ''                # 1 -> L+ yorum ister, sorulur


def test_malzeme_listesi_baska_bicim_okunur(tmp_path):
    import openpyxl as O
    wb = O.Workbook()
    ws = wb.active
    ws.title = 'CC ve CP'
    ws.append(['Baslik'])
    ws.append([])
    ws.append(['Bileşen etiketi', 'Açıklama', 'Sipariş numarası', 'Üretici', 'Yapı'])
    ws.append(['=A+CC-1K1', 'Kontaktör', '3RT2016', 'Siemens', '+CC'])
    y = tmp_path / 'l.xlsx'
    wb.save(y)
    s = cikti.liste_oku(y)
    assert s[0]['Üretici Kodu'] == '3RT2016' and s[0]['Marka'] == 'Siemens' and s[0]['Açıklama'] == 'Kontaktör' and s[0]['Kod'] == 'Siemens.3RT2016'
    assert not cikti.is_emri_bicimi(y)


def test_uc_yer_tutucu_hedef_gecerli():
    ogr = dict(OGR, uc=[dict(urun='SIE.X1', karar='ESLE', esleme={'L1': '.1', 'T1': '2/T1'}, kaynak='t')])
    assert ('L1', '.1') in {(s['old'], s['new']) for s in plan(ogr)['pins']}


def test_arti_eksi_ayni_cihazda_buyuk_kesit_kontak_bobin_haric():
    F = [fn('p', '=A+CC-7FB1', [uc('6'), uc('5')], ['L.1'], cat='DeviceEndTerminal'), fn('c', '=A+CC-1K1', [uc('A2'), uc('2')], ['S.1'], cat='Coil')]
    r = dict(pages=[dict(functions=F)], connections=[bag('a', '=A+CC-7FB1', '6', '+CC-X1:1', 'a', o1='p', o2='t'), bag('e', '=A+CC-7FB1', '5', '+CC-X1:2', 'a', o1='p', o2='t'),
                                                       bag('m', '=A+CC-1K1', '2', '+CC-X1:1', 'a', o1='c', o2='t'), bag('b', '=A+CC-1K1', 'A2', '+CC-X1:2', 'a', o1='c', o2='t')])
    w = lambda i, k, renk, kay: dict(connection_id=i, renk=renk, kesit=k, cross_section=k, kaynak=kay, tip='', wire_type='', wire_code='', kod_durum='')
    S = C.arti_eksi_esitle([w('a', '2,5', 'DBU', 'MÜŞTERİ KESİT'), w('e', '0,75', 'DBUWH', 'KURAL: DC_EKSI'), w('m', '6', 'DBU', 'MÜŞTERİ KESİT'), w('b', '0,75', 'DBUWH', 'KURAL: DC_EKSI')], r, {}, ())
    assert {x['connection_id']: x['kesit'] for x in S} == {'a': '2,5', 'e': '2,5', 'm': '6', 'b': '0,75'}
    x = C.arti_eksi_esitle([w('a', '2,5', 'DBU', 'MÜŞTERİ KESİT'), w('e', '0,75', 'DBUWH', 'MÜŞTERİ KESİT')], r, {}, ())
    assert x[1]['kesit'] == '0,75'     # müşteri kesiti değişmez


def test_analog_modul_yalniz_analog_kanalli():
    assert C.analog_modul(['SIGNAL BOARD SB 1232, 1 AO'])
    assert not C.analog_modul(['CPU 1214C, DC/DC/DC, 14DI/10DO/2AI'])
    assert not C.analog_modul(['SM 1223, 16DI/16DO'])


def test_tel_kurallari():
    p = plan()
    t = {w['connection_id']: w for w in p['wires']}
    assert t['c2']['color'] == 'DBU' and t['c2']['wire_code'] == '100'
    assert t['c3']['color'] == 'DBUWH' and t['c3']['wire_code'] == '101'
    assert t['c5']['cross_section'] == '0,75' and t['c5']['color'] == 'DBU'   # kesitsiz + 24V rengi
    assert t['c1']['wire_code'] == '' and t['c1']['wire_type'] == 'H07V-K'    # standartta yok: liste
    assert 'c6' not in t                                                      # kablo damarı
    assert [b['connection_id'] for b in p['bekleyen']['tel']] == ['c4']       # kesitsiz, kanıt yok: yazılmaz


def test_kablo_siniflamasi_konumdan_yapilmaz_cdp_adindan_yapilir():
    r = proje()
    f = fn('cd', '+CC-5WD1', sinif='ConnectionDefinitionPoint')
    f['location'] = [10, 10]
    r['pages'][0]['functions'].append(f)
    r['connections'][1]['symbol_refs'] = [dict(class_name='SymbolReference', location=[10, 10])]
    assert dict((c['object_id'], k) for c, k in C.tel_siniflari(r, '+CC', PR, dict(c2=['=+'])))['c2'] == 'TEK_KESITLI'   # yakın CDP kablo yapmaz
    assert dict((c['object_id'], k) for c, k in C.tel_siniflari(r, '+CC', PR, dict(c2=['+CC-5WD1'])))['c2'] == 'KABLO'
    p = C.plan_kur(r, '+CC', PR, OGR, 'IS1', LISTE, olcum(), set(), STD, cdp_ad=dict(c2=['+CC-5WD1']))
    assert 'c2' not in {w['connection_id'] for w in p['wires']}


def test_pano_alt_konumu_ayni_pano_sayilmaz():
    assert C.pano_ici('=A+CC-1', '+CC') and C.pano_ici('=+CC', '+CC') and C.pano_ici('=G+CC:2', '+CC')
    assert not C.pano_ici('=A+CCI-1', '+CC') and not C.pano_ici('=A+CC1-1', '+CC') and not C.pano_ici('=A+CC.1-1', '+CC')


def test_ac_isaretli_tel_renklenmez():
    r = proje()
    r['connections'].append(bag('c9', '=A+CC-AC1', 'x', '+CC-X1:1', 'a', '0,75', 'BU'))
    p = C.plan_kur(r, '+CC', PR, OGR, 'IS1', LISTE, olcum(), set(), STD)
    assert [w for w in p['wires'] if w['connection_id'] == 'c9'][0]['color'] == ''


def test_tel_ogrenilen_baska_ise_yayilmaz():
    ogr = dict(OGR, tel=[dict(**{'is': 'IS2'}, bag_deseni='-2G1', karar='YAZ', kesit='0,75', renk='DBU', kaynak='t')])
    assert [b['connection_id'] for b in plan(ogr)['bekleyen']['tel']] == ['c4']
    ogr['tel'][0]['is'] = 'IS1'
    assert plan(ogr)['bekleyen']['tel'] == []


def test_kod_kapisi():
    g = dict(urun_kodu=[dict(p8_kod='A.1')], uc=[dict(urun='U.1', fark=['x'], musteri_uclari=[dict(ad='x')], uvp_uclari=['y', 'z'])], tel=[dict(bag='c1')])
    ms = dict(olcum())
    ka = dict(urun_kodu=[dict(p8_kod='A.1', uvp_kod='YOK.1', karar='DEGISTIR', kanit='k'), dict(p8_kod='B.1', uvp_kod='SIE.X1', karar='DEGISTIR', kanit='k')],
              uc=[dict(urun='U.1', esleme={'x': 'q'}, karar='ESLE', kanit='k')], tel=[dict(bag='c1', kesit='9', renk='DBU', karar='YAZ', kanit='k')])
    s = C.karar_kapisi(ka, g, ms, set(), STD)
    assert not s['urun_kodu'] and not s['uc'] and not s['tel'] and len(s['red']) == 4
    ka = dict(urun_kodu=[dict(p8_kod='A.1', uvp_kod='SIE.X1', karar='DEGISTIR', kanit='k')], uc=[dict(urun='U.1', esleme={'x': 'y'}, karar='ESLE', kanit='k')],
              tel=[dict(bag='c1', kesit='0.75', renk='DBU', karar='YAZ', kanit='k')])
    s = C.karar_kapisi(ka, g, ms, set(), STD)
    assert s['urun_kodu']['A.1']['uvp_kod'] == 'SIE.X1' and s['uc'][0]['esleme'] == {'x': 'y'} and s['tel']['c1']['kesit'] == '0,75' and not s['red']
    # Kanıtsız karar, ölçümde bulunamayan kod ve tip no eşleşmesi kabul edilmez.
    ka = dict(urun_kodu=[dict(p8_kod='A.1', uvp_kod='SIE.X1', karar='DEGISTIR')])
    assert C.karar_kapisi(ka, g, ms, set(), STD)['red']
    ka = dict(urun_kodu=[dict(p8_kod='A.1', uvp_kod='BIL.Y1', karar='DEGISTIR', kanit='k')])
    assert C.karar_kapisi(ka, g, ms, set(), STD)['red']
    ms2 = dict(ms, **{'T.1': dict(part_number='T.1', found=True, part_nr='SIE.BASKA')})
    ka = dict(urun_kodu=[dict(p8_kod='A.1', uvp_kod='T.1', karar='DEGISTIR', kanit='k')])
    assert C.karar_kapisi(ka, g, ms2, set(), STD)['red']


def test_tip_no_ile_bulunan_kod_ayni_sayilmaz():
    ms = dict(olcum(), **{'SIE.X1': dict(part_number='SIE.X1', found=True, part_nr='SIE.BASKA')})
    assert C.kod_kararlari(proje(), '+CC', PR, dict(urun_kodu=[]), [], ms, set(), {})['SIE.X1']['durum'] == 'BEKLIYOR'


def test_liste_birebir_kod_otomatik_degisir_emin_degilse_bekler():
    r = proje()
    r['pages'][0]['functions'][3]['articles'] = [dict(part_nr='XX.Y1')]
    liste = [{'Kod': 'Bil.Y1', 'Üretici Kodu': 'Y-1', 'Marka': 'Bilgi', 'Açıklama': ''}]
    ms = dict(olcum(), **{'bil.y1': dict(part_number='bil.y1', found=True, part_nr='BIL.Y1')})
    k = C.kod_kararlari(r, '+CC', PR, dict(urun_kodu=[]), liste, ms, {'bil.y1'}, {})['XX.Y1']
    assert k['durum'] == 'DEGISTIR' and k['yeni'] == 'BIL.Y1'
    liste[0]['Marka'] = 'Baska'   # marka tutmuyor
    assert C.kod_kararlari(r, '+CC', PR, dict(urun_kodu=[]), liste, ms, {'bil.y1'}, {})['XX.Y1']['durum'] == 'BEKLIYOR'


def test_eskimis_satir_yazilmaz():
    r = proje()
    yp = dict(pins=[dict(function_id='f1', old='YOK', new='a')], wires=[dict(connection_id='c1', old_color='RD', old_cross_section='6')],
              parts=[dict(function_id='f1', old='SIE.X1', new='B')])
    temiz, eski = C.eskimis_satirlar(r, yp)
    assert temiz['parts'] and not temiz['pins'] and not temiz['wires'] and len(eski) == 2


def _son_durum():
    r0 = proje()
    p = plan()
    yp = C.yazma_plani(p)
    r1 = C.sanal_uygula(r0, yp)
    t = {w['connection_id']: w for w in yp['wires']}
    for c in r1['connections']:
        w = t.get(c['object_id'])
        if w:
            c['wire_color'] = w['color'] or w['old_color']
            c['cross_section'] = w['cross_section'] or w['old_cross_section']
            c['wire_part'] = 'LAPP.' + w['wire_code'] if w['wire_code'] else ''
    return r0, r1, yp


def test_dogrula_gecer():
    r0, r1, yp = _son_durum()
    assert C.dogrula(r0, json.loads(json.dumps(r0)), r1, yp, [dict(ok=True, wires=[dict(status='WRITTEN')])])['ok']


def test_dogrula_urun_reddi_kodsuz_gecer_ama_renk_kesit_ister():
    r = dict(pages=[dict(functions=[])], connections=[bag('c', '=A+CC-1K1', 'A1', '+CC-X1:1', 'a', '1,5', 'BK')])
    s = bag('c', '=A+CC-1K1', 'A1', '+CC-X1:1', 'a', '0,75', 'DBU')
    w = dict(connection_id='c', ce=['=A+CC-1K1', 'A1', '+CC-X1:1', 'a'], color='DBU', old_color='BK', cross_section='0,75', old_cross_section='1,5', wire_code='100', wire_type='H05V-K')
    son = dict(pages=[dict(functions=[])], connections=[s])
    yp = dict(wires=[w], pins=[], parts=[])
    assert C.dogrula(r, r, son, yp, [dict(wires=[dict(connection_id='c', status='WRITTEN_ARTICLE_REFUSED')])])['ok']
    assert not C.dogrula(r, r, son, yp, [dict(wires=[dict(connection_id='c', status='WRITTEN')])])['ok']
    assert not C.dogrula(r, r, r, yp, [dict(wires=[dict(connection_id='c', status='WRITTEN_ARTICLE_REFUSED')])])['ok']


def test_dogrula_cdp_yan_urunu_yeni_fonksiyon_sayilmaz():
    r0, r1, yp = _son_durum()
    r1['pages'][0]['functions'].append(fn('cdpX', '=+', sinif='ConnectionDefinitionPoint'))
    assert C.dogrula(r0, json.loads(json.dumps(r0)), r1, yp, [dict(ok=True)])['ok']


@pytest.mark.parametrize('bozuk', ['bag', 'yeni', 'silinen', 'plandisi', 'stale', 'kablo', 'satir', 'parca'])
def test_dogrula_kaldi(bozuk):
    r0, r1, yp = _son_durum()
    taban = json.loads(json.dumps(r0))
    rap = [dict(ok=True)]
    fs = r1['pages'][0]['functions']
    if bozuk == 'bag':
        r1['connections'].pop()
    elif bozuk == 'yeni':
        fs.append(fn('fX', '=A+CC-9Z9', [], ['LUE.1']))
    elif bozuk == 'silinen':
        fs.pop(0)
    elif bozuk == 'plandisi':
        [c for c in r1['connections'] if c['object_id'] == 'c6'][0]['wire_color'] = 'RD'
    elif bozuk == 'stale':
        rap = [dict(ok=True, wires=[dict(status='STALE')])]
    elif bozuk == 'kablo':
        rap = [dict(ok=True, wires=[dict(status='SKIPPED_CABLE_CDP')])]
    elif bozuk == 'satir':
        [c for c in r1['connections'] if c['object_id'] == 'c2'][0]['wire_color'] = 'BU'
    elif bozuk == 'parca':
        fs[1]['articles'] = [dict(part_nr='KURCALANDI')]
    d = C.dogrula(r0, taban, r1, yp, rap)
    assert not d['ok'] and d['sonuc'] == 'KALDI' and d['neden']
    if bozuk == 'yeni':
        assert d['yeni_fonksiyon'] == ['=A+CC-9Z9']


def test_plan_farki_yazilani_cikarir():
    yp = C.yazma_plani(plan())
    fark = C.plan_farki(yp, dict(pins=yp['pins'][:1], wires=yp['wires'], parts=[]))
    assert len(fark['pins']) == len(yp['pins']) - 1 and not fark['wires'] and fark['parts'] == yp['parts']


def test_surum_kapisi(tmp_path):
    with pytest.raises(eplan.Dur):
        eplan.surum_kapisi([])
    elk = tmp_path / 'a.elk'
    elk.write_bytes('a.edb\r\n19.0\r\n'.encode('utf-16'))
    with pytest.raises(eplan.Dur):
        eplan.elk_surum_kapisi(elk)
    elk.write_bytes('a.edb\r\n18.0\r\n'.encode('utf-16'))
    assert eplan.elk_surum_kapisi(elk) == 18


def test_mesgul_kapisi(tmp_path, monkeypatch):
    k = tmp_path / 'KILIT'
    monkeypatch.setattr(eplan, 'KILIT', k)
    monkeypatch.setattr(eplan, 'eplan_acik', lambda: True)
    with pytest.raises(eplan.Dur, match='Açık EPLAN'):
        eplan.mesgul_kapisi()
    monkeypatch.setattr(eplan, 'eplan_acik', lambda: False)
    eplan.mesgul_kapisi()
    k.write_text('x')
    with pytest.raises(eplan.Dur, match='kilidi'):
        eplan.mesgul_kapisi()


def test_dll_secimi(tmp_path):
    ad = ['UvpA', 'UvpB']
    tam = tmp_path / 'Uvp.PdfToP8.Host_t.dll'
    tam.write_bytes(b'x'.join(a.encode('utf-16-le') for a in ad))
    eksik = tmp_path / 'Uvp.PdfToP8.Host_e.dll'
    eksik.write_bytes(ad[0].encode('utf-16-le'))
    kur = tmp_path / 'installed.txt'
    kur.write_text(str(tam))
    assert eplan.dll_sec(ad, kur, tmp_path) == (str(tam), True, '')
    kur.write_text(str(eksik))
    dll, kayitsiz, uyari = eplan.dll_sec(ad, kur, tmp_path)
    assert dll == str(tam) and not kayitsiz and 'UYARI' in uyari
    tam.unlink()
    with pytest.raises(eplan.Dur):
        eplan.dll_sec(ad, kur, tmp_path)


def test_adim_sonucu_ok_false_ise_durur(tmp_path):
    (tmp_path / 'a.json').write_text(json.dumps(dict(ok=False, error='yedek yok')), encoding='utf-8')
    (tmp_path / 'b.pdf').write_bytes(b'x')
    with pytest.raises(eplan.Dur, match='yedek yok'):
        eplan.sonuc_kapisi([dict(action='X', result=str(tmp_path / 'b.pdf')), dict(action='Y', result=str(tmp_path / 'a.json'))])


def test_kaldi_denemesi_tekrarlanmaz(tmp_path, monkeypatch):
    (tmp_path / 'hata_x.json').write_text('{}')
    with pytest.raises(eplan.Dur, match='önceki denemede'):
        eplan.calistir([], tmp_path, 'x')


def _liste_dosyasi(yol):
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.append(['Kod', 'Üretici Kodu', 'Marka', 'Açıklama', 'Miktar', 'Pano'])
    ws.append(['Sie.X1', 'X1', 'Siemens', 'Şalter', 1, '+CC'])
    ws.append(['Yan.9', '9', 'Yanlis', 'Mekanik', 1, '+CC'])
    ws.append(['Muh.Kab1', 'KAB1', 'Muh', 'Muhafaza', 1, '+CC'])
    wb.save(yol)


def test_renkli_liste_renk_ve_kaldi(tmp_path):
    g, c = tmp_path / 'l.xlsx', tmp_path / 'c.xlsx'
    _liste_dosyasi(g)
    r0, r1, yp = _son_durum()
    p = plan()
    oz = cikti.renkli_liste(g, c, r1, '+CC', PR, olcum(), yp, OGR, dict(ok=True))
    assert oz['kaldi'] is None and oz['kontrol_disi'] == 1 and oz['turuncu'] == 1 and oz['yesil'] == 1   # =+CC üstündeki parça kaybolmaz
    ws = openpyxl.load_workbook(c).worksheets[0]
    assert ws.cell(row=2, column=1).fill.fgColor.rgb.endswith('ED7D31')
    assert ws.cell(row=4, column=1).fill.fgColor.rgb.endswith('A9D08E')
    cikti.renkli_liste(g, c, r1, '+CC', PR, olcum(), yp, OGR, dict(ok=False, neden=['bağlantı farkı']))
    assert 'KALDI' in openpyxl.load_workbook(c).worksheets[0].cell(row=1, column=1).value
    assert p


def test_karar_html_veriden_uretilir(tmp_path):
    g = dict(uc=[dict(urun='SIE.X1', cihazlar=['=A+CC-1K1'], fark=['L1'], musteri_uclari=[dict(ad='L1', pdf=None)], uvp_uclari=['1/L1'])],
             urun_kodu=[dict(p8_kod='ZZ.1', cihazlar=['=A+CC-3K1'], db_adaylari=[], pdf_sayfa=None)], tel=[dict(bag='c4', uclar=['a:1', 'b:2'], cihaz=['=A+CC-2G1'], aday_amper_kaynak=[], pdf=None)])
    S = cikti.sorular(g, LISTE, olcum(), dict(red=[dict(tur='uc', anahtar='SIE.X1', neden='uvp ucu ölçümde yok: Q')], belirsiz=[]), None)
    assert [s['tur'] for s in S] == ['uc', 'urun_kodu', 'tel'] and S[0]['baslik'] == 'Şalter' and 'reddedildi' in S[0]['neden']
    h = tmp_path / 'k.html'
    cikti.karar_html(h, 'IS1', '+CC', S, [])
    t = h.read_text(encoding='utf-8')
    assert '__VERI__' not in t and '__PROJE__' not in t and 'http' not in t.split('<script>')[0].replace('http-equiv', '')


def test_kullanici_karari_cevrilir():
    k = K.kullanici_cevir(dict(kararlar=[dict(tur='uc', anahtar='U', karar='ESLE', esleme={'a': 'b', 'c': ''}), dict(tur='tel', anahtar='c', karar='YAZ', esleme=dict(kesit='1,5', renk='RD')),
                                         dict(tur='urun_kodu', anahtar='P', karar='ELLE')]))
    assert k['uc'][0]['esleme'] == {'a': 'b'} and k['tel'][0]['renk'] == 'RD' and k['urun_kodu'][0]['karar'] == 'ELLE'


def test_lapp_kodu_db_de_yoksa_yazilmaz_listelenir():
    db = {'lapp.100', 'sie.x1'}
    p = C.plan_kur(proje(), '+CC', PR, OGR, 'IS1', LISTE, olcum(), db, STD)
    t = {w['connection_id']: w for w in p['wires']}
    assert t['c2']['wire_code'] == '100'
    assert t['c3']['wire_code'] == '' and t['c3']['kod_durum'] == 'DB_YOK: 101'
    e = {x['tip']: x for x in C.eksik_kablolar(p['wires'])}
    assert e['DBUWH-0,75-H05V-K']['durum'] == 'DB_YOK: 101' and e['BK-6-H07V-K']['durum'] == 'STANDARTTA_YOK' and e['BK-6-H07V-K']['baglanti'] == 1
    assert e['BK-6-H07V-K']['ornek'] == ['=A+CC-1K1:L1 - +CC-X1:1:a']


def test_veri_kapisi():
    liste = [{'Kod': 'Sie.X1', 'Üretici Kodu': 'X-1', 'Açıklama': 'a'}, {'Kod': 'Yok.1', 'Üretici Kodu': 'Q9', 'Açıklama': 'b'}]
    vk = C.veri_kapisi(liste, ['SIE.X1', 'CON@.5 - 6', 'YOK.2', 'BAS@.7'], ['Sie/a.ema', 'Sie/b.ema', 'c.EMA'], {'sie.x1', 'con.5 - 6'}, {'sie/a.ema', 'baska/b.ema'})
    assert [x['kod'] for x in vk['liste_eksik']] == ['Yok.1']
    assert vk['parca_eksik'] == ['YOK.2', 'BAS@.7'] and vk['at_oneki_db_de_duz'] == ['CON@.5 - 6']
    assert {x['makro']: x['durum'] for x in vk['makro_eksik']} == {'Sie/b.ema': 'ad var, klasör farklı', 'c.EMA': 'yok'}


def test_edz_parca_adlari_suzulur(tmp_path):
    import py7zr
    for ad in ('CON@.5 - 6.part.xml', 'A.1.connectionpoints.part.xml', 'A.1.manufacturer.part.xml', 'A.1.part.xml'):
        (tmp_path / ad).write_text('x')
    (tmp_path / 'm.ema').write_text('x')
    z = tmp_path / 'p.edz'
    with py7zr.SevenZipFile(z, 'w') as a:
        for ad in ('CON@.5 - 6.part.xml', 'A.1.connectionpoints.part.xml', 'A.1.manufacturer.part.xml', 'A.1.part.xml'):
            a.write(tmp_path / ad, 'items/partxml/' + ad)
        a.write(tmp_path / 'm.ema', 'items/macro/Sie/m.ema')
    assert eplan.edz_icerik(z) == (['A.1', 'CON@.5 - 6'], ['Sie/m.ema'])


def test_kimlik_oturumdan_bagimsiz():
    assert C.kimlik_sonu('108/18/46825/0') == '18/46825/0' == C.kimlik_sonu('18/46825/0')
    r = proje()
    r['connections'][0]['object_id'] = '108/18/1/0'
    r['connections'][0]['start']['object_id'] = '108/17/2/0'
    C.kimlik_duzelt(r)
    assert r['connections'][0]['object_id'] == '18/1/0' and r['connections'][0]['start']['object_id'] == '17/2/0'
    # Harita başka oturumdan: yine eşleşir.
    r2 = proje()
    r2['connections'][1]['object_id'] = '108/18/9/0'
    p = C.plan_kur(r2, '+CC', PR, OGR, 'IS1', LISTE, olcum(), set(), STD, cdp_ad={'150/18/9/0': ['+CC-5WD1']})
    assert '18/9/0' not in {w['connection_id'] for w in p['wires']}


def _varsayilanli():
    """CDP'siz bağlantılar proje varsayılanı 1,5 BK taşır."""
    fs = [fn('s', '=A+CC-1TB1:Out +', [uc('Out +')], sinif='Function', cat='DeviceEndTerminal'), fn('m', '=A+CC-1TB1:Out -', [uc('Out -')], cat='DeviceEndTerminal'),
          fn('k', '=A+CC-1QA1', [uc('13'), uc('14')], cat='NoContact'), fn('b', '=A+CC-2QA1', [uc('A1'), uc('A2')], cat='Coil'),
          fn('p', '=A+CC-3KE1:DI0', [uc('DI0')], cat='PLCTerminal'), fn('f', '=A+CC-4FC1', [uc('1'), uc('2')], cat='Overload'), fn('w', '=A+CC-4MA1', [uc('U')], cat='Motor'),
          fn('t', '+CC-X1:L1', [uc('a')], sinif='Terminal', cat='Terminal'), fn('q', '=A+CC-5KF1:9', [uc('9')], cat='DeviceEndTerminal'), fn('q2', '=A+CC-5KF2:9', [uc('9')], cat='DeviceEndTerminal')]
    cs = [bag('a1', '=A+CC-1TB1:Out +', 'Out +', '=A+CC-1QA1', '13', '1,5', 'BK', 's', 'k'),
          bag('a2', '=A+CC-1QA1', '14', '=A+CC-2QA1', 'A1', '1,5', 'BK', 'k', 'b'),
          bag('a3', '=A+CC-2QA1', 'A2', '=A+CC-1TB1:Out -', 'Out -', '1,5', 'BK', 'b', 'm'),
          bag('a4', '=A+CC-3KE1:DI0', 'DI0', '=A+CC-5KF1:9', '9', '1,5', 'BK', 'p', 'q'),
          bag('a5', '+CC-X1:L1', 'a', '=A+CC-4FC1', '1', '1,5', 'BK', 't', 'f'),
          bag('a6', '=A+CC-4FC1', '2', '=A+CC-4MA1', 'U', '1,5', 'BK', 'f', 'w'),
          bag('a7', '=A+CC-5KF2:9', '9', '=A+CC-5KF1:9', '9', '6', 'BU', 'q2', 'q')]
    return dict(pages=[dict(functions=fs)], connections=cs, interruption_points=[])


def test_cdp_yoksa_varsayilan_kesit_musterinin_degildir():
    p = C.plan_kur(_varsayilanli(), '+CC', PR, dict(urun_kodu=[], uc=[], tel=[]), 'IS1', [], {}, set(), STD, cdp_ad=dict(a7=['=+']))
    t = {w['connection_id']: w for w in p['wires']}
    # Kontak geçilir, bobin durdurur: a2 artı, a3 eksi.
    assert t['a1']['cross_section'] == '0,75' and t['a1']['color'] == 'DBU' and t['a1']['old_color'] == 'BK' and t['a1']['old_cross_section'] == '1,5'
    assert t['a2']['color'] == 'DBU' and t['a3']['color'] == 'DBUWH' and t['a3']['wire_code'] == '101'
    assert t['a4']['color'] == 'DBU' and 'PLC_SINYAL' in t['a4']['kaynak']
    # Gerçek CDP: müşteri kesiti kalır.
    assert t['a7']['cross_section'] == '' and t['a7']['color'] == 'DBU'
    # AC: otomatik yazılmaz, sigorta üzerinden aynı ağ.
    b = {x['connection_id']: x for x in p['bekleyen']['tel']}
    assert set(b) == {'a5', 'a6'} and b['a5']['aday_devre'] == 'AC' and b['a6']['aday_devre'] == 'AC' and b['a5']['amper_cihaz'] == ['f', 'w']


def test_ag_iki_sinif_belirsiz_kesinti_oku_adi_sinif_verir():
    import devre
    r = _varsayilanli()
    r['connections'].append(bag('a8', '=A+CC-4FC1', '2', '=A+CC-1QA1', '14', '1,5', 'BK', 'f', 'k'))
    assert devre.Ag(r, PR).sinif(r['connections'][1])[0] == 'BELIRSIZ'
    r = _varsayilanli()
    r['interruption_points'] = [dict(object_id='i1', name='-0V_1A'), dict(object_id='i2', name='-0V_1A')]
    r['connections'][3]['sub_connections'] = [dict(start=dict(kind='InterruptionPoint', object_id='i1'), end=dict(kind='PIN'))]
    d, kanit, _a = devre.Ag(r, PR).sinif(r['connections'][3])
    assert d == 'DC_EKSI' and '0V_1A' in kanit
    assert devre.tablodan_kesit(6.3, [(10, '1,5'), (16, '2,5')]) == '1,5' and devre.tablodan_kesit(None, [(10, '1,5')]) == ''
    assert devre.amper_oku('LEISTUNGSSCHALTER FEDERZUGANSCHL. 6,3A') == 6.3


def _plan_devre(r, cdp, ms=None, liste=(), akim=()):
    return C.plan_kur(r, '+CC', PR, dict(urun_kodu=[], uc=[], tel=[]), 'IS1', list(liste), ms or {}, set(), STD, cdp_ad=cdp, akim=akim)


def _kisa(fs, cs):
    return dict(pages=[dict(functions=fs)], connections=cs, interruption_points=[])


def test_musteri_sayfasindaki_urunsuz_cihaz_saha_sayilir():
    """26SB002 -30SG4: harici NOT-HALT. Yazılmaz. Ürünlü cihaz ve bara (GND) aynı sayfada olsa da tel kalır."""
    fs = [fn('a', '=G+CC-9SG4', [uc('11')], sayfa='K/1', cat='Switch'), fn('b', '=G+CC-9SG4', [uc('12')], sayfa='P/2', cat='NcContact'),
          fn('c', '=G+CC-1K1', [uc('A1')], ['SIE.X1'], sayfa='K/1'), fn('d', '=G+CC-GND', [uc('')], sayfa='K/1', cat='Busbar'),
          fn('e', '=G+CC-2K1', [uc('A1')], sayfa='P/2', cat='Coil'), fn('t', '=G+CC-X1:1', [uc('')], ['WAGO.K'], sinif='Terminal', sayfa='K/1')]
    r = dict(pages=[dict(name='K/1', geometry=dict(items=[dict(text='Kunde')]), functions=fs[:4] + fs[5:]), dict(name='P/2', functions=fs[4:5] + fs[1:2])],
             connections=[bag('s', '=G+CC-9SG4', '12', '=G+CC-X1:1', '', o1='b', o2='t'), bag('u', '=G+CC-1K1', 'A1', '=G+CC-X1:1', '', o1='c', o2='t'),
                          bag('g', '=G+CC-GND', '', '=G+CC-X1:1', '', o1='d', o2='t'), bag('o', '=G+CC-2K1', 'A1', '=G+CC-X1:1', '', o1='e', o2='t')])
    assert C.saha_cihazlari(r, PR) == {'=G+CC-9SG4'}
    S = {c['object_id']: s for c, s in C.tel_siniflari(r, '+CC', PR, {})}
    assert S['s'] == 'SAHA_CIHAZI' and 'SAHA_CIHAZI' not in (S['u'], S['g'], S['o'])


def test_potansiyelden_cikan_tel_ve_klemens_arasi_sinifi():
    """Denetçi bulguları: GND tanım noktası teli atlanıyordu, aynı dizide iki klemens KLEMENS_ICI sayılıyordu, ' - ' metni cihaz tiresi sanılıyordu."""
    fs = [fn('a', '=G+CC:24VDC - 230VAC', sayfa='K/1'), fn('b', '=G+CC-1K1', [uc('A1')], ['SIE.X1'], sayfa='P/2'),
          fn('t1', '=G+CC-X1:1', [uc('')], ['W'], sinif='Terminal'), fn('t2', '=G+CC-X1:5', [uc('')], ['W'], sinif='Terminal')]
    p = bag('p', '=G+CC-1K1', 'A1', '', '', o1='b')
    p['end'] = dict(object_id='pd', pin=None, index=0, kind='PotentialDefinition')
    d = bag('d', '=Z+XX-1K1', 'A1', '', '', o1='x')
    d['end'] = dict(object_id='pd', pin=None, index=0, kind='PotentialDefinition')
    r = dict(pages=[dict(name='K/1', geometry=dict(t='Kunde'), functions=fs[:1]), dict(name='P/2', functions=fs[1:])],
             connections=[p, d, bag('k', '=G+CC-X1:1', '', '=G+CC-X1:5', '', o1='t1', o2='t2'), bag('i', '=G+CC-X1:1', '', '=G+CC-X1:1', '', o1='t1', o2='t1')])
    assert C.saha_cihazlari(r, PR) == set()
    S = {c['object_id']: s for c, s in C.tel_siniflari(r, '+CC', PR, {})}
    assert S == {'p': 'TEK_KESITSIZ', 'k': 'KLEMENS_ARASI', 'i': 'KLEMENS_ICI'}


def test_dogrula_urun_sirasi_degisirse_kalir():
    r = dict(pages=[dict(functions=[fn('f', '=A+CC-1K1', [], ['ESKI', 'HELU'])])], connections=[])
    bozuk = dict(pages=[dict(functions=[fn('f', '=A+CC-1K1', [], ['HELU', 'YENI'])])], connections=[])
    dogru = dict(pages=[dict(functions=[fn('f', '=A+CC-1K1', [], ['YENI', 'HELU'])])], connections=[])
    yp = dict(wires=[], pins=[], parts=[dict(function_id='f', old='ESKI', new='YENI', ce='=A+CC-1K1')])
    assert C.dogrula(r, r, dogru, yp, [])['ok']
    assert C.dogrula(r, r, bozuk, yp, [])['plan_disi'] == {'parca_sirasi': 1}


def test_etiket_listesi_renkleri():
    import etiket_liste as E
    db = {'sie.x-1'}
    tam = dict(fark=[], uvp=['A1'], telli=['A1'])
    olc = dict(found=True)

    def d(kod='SIE.X-1', o=olc, c=tam, bos=0, kotu=False):
        return E.satir_durumu(kod, db, o, c, bos, kotu)[0]
    assert d() == E.YESIL
    assert d(c=dict(fark=[], uvp=['A1'], telli=[])) == E.YESIL
    assert d(kod='ABC.Y') == E.TURUNCU        # kod DB'de yok
    assert d(o=None) == E.TURUNCU             # ölçüm yok
    assert d(c=dict(fark=['Z'], uvp=['A1'], telli=['Z'])) == E.TURUNCU
    assert d(bos=1) == E.TURUNCU
    assert d(c=None) == E.GRI
    assert d(kotu=True) == E.KIRMIZI


def test_kontak_kurali_son_rakam_islev_sayi_asilirsa_kalir():
    ms = {'B.1NC': dict(device_functions=[dict(pins=['.1', '.2'])]), 'B.2NC': dict(device_functions=[dict(pins=['.1', '.2']), dict(pins=[".1'", ".2'"])]), 'B.NO': dict(device_functions=[dict(pins=['.3', '.4'])])}
    assert C.kontak_kurali(['63', '64'], ['B.NO'], ms) == [('63', '.3'), ('64', '.4')]
    assert C.kontak_kurali(['21', '22', '31', '32', '61', '62'], ['B.1NC', 'B.2NC'], ms) == [('21', '.1'), ('22', '.2'), ('31', '.1'), ('32', '.2'), ('61', ".1'"), ('62', ".2'")]
    assert C.kontak_kurali(['21', '22'], ['B.NO'], ms) == []                     # NC uç NO modüle uymaz
    assert C.kontak_kurali(['13', '23'], ['B.NO'], ms) == [('13', '.3')]         # ikinci NO için yer yok
    assert C.kontak_kurali(['L1', '101'], ['B.NO'], ms) == []


def test_pin_karari_urun_basina_kalici_tekrar_sorulmaz():
    import pin_kuyruk as P
    k = [dict(urun=['SIE.A', 'SIE.Z'], hedef_urun='SIE.A', uc='13', karar='ESLE', hedef='.3'), dict(urun=['SIE.A'], uc='63', karar='BILMIYORUM', hedef=''), dict(urun=['SIE.B'], uc='', karar='ESLE', hedef='P1')]
    o = P.kayda_cevir(k, dict(urun_kodu=[], uc=[], tel=[]), 'IS', 't')
    assert [(x['urun'], x['esleme']) for x in o['uc']] == [(['SIE.A'], {'13': '.3'})]
    assert P.kayitli_esleme(['SIE.A', 'SIE.Q'], '13', o) == ('ESLE', '.3')       # başka takımda da sorulmaz
    assert P.kayitli_esleme(['SIE.A'], '63', o) is None                          # bilmiyorum kaydedilmez
    assert P.kayitli_esleme(['SIE.Q'], '13', o) is None                          # başka ürüne yayılmaz
    o2 = P.kayda_cevir([dict(urun=['SIE.A'], hedef_urun='SIE.A', uc='13', karar='ESLE', hedef='.4')], o, 'IS2', 't')
    assert P.kayitli_esleme(['SIE.A'], '13', o2) == ('ESLE', '.4')               # yeni karar eskisini ezer


def test_pin_onerisi_yalniz_bos_uca_ve_bir_kez():
    import pin_kuyruk as P
    assert P.oneriler(['1', '2'], ['1/L1', '2/T1']) == {'1': '1/L1', '2': '2/T1'}
    assert P.oneriler(['T1_1'], ['T1', 'T2']) == {'T1_1': 'T1'}
    assert P.oneriler(['1', '2'], ['L+', 'M', 'P1']) == {'1': 'L+', '2': 'M'}
    assert P.oneriler(['1'], ['1/L1'], dolu=['1/L1']) == {}                      # dolu uç önerilmez
    assert P.oneriler(['BU'], ['ws1', 'ws2']) == {}


def test_dogrula_bir_fonksiyonda_iki_kod_degisimi_sira_hatasi_degil():
    r = dict(pages=[dict(functions=[fn('f', '=A+CC-1K1', [], ['ESKI1', 'ESKI2'])])], connections=[])
    son = dict(pages=[dict(functions=[fn('f', '=A+CC-1K1', [], ['YENI1', 'YENI2'])])], connections=[])
    yp = dict(wires=[], pins=[], parts=[dict(function_id='f', old='ESKI1', new='YENI1', ce='x'), dict(function_id='f', old='ESKI2', new='YENI2', ce='x')])
    assert C.dogrula(r, r, son, yp, [])['ok']


def test_alt_etiket_urunu_ustten_alir_yanlis_yesil_olmaz():
    fs = [fn('u', '=A+CC-15RA7', [], ['SIE.R']), fn('a', '=A+CC-15RA7-RA1', [uc('B+', 0)]), fn('t', '=A+CC-X1:1', [uc('a')], sinif='Terminal')]
    r = dict(pages=[dict(functions=fs)], connections=[bag('c', '=A+CC-15RA7-RA1', 'B+', '=A+CC-X1:1', 'a', o1='a', o2='t')])
    ms = {'SIE.R': dict(found=True, device_functions=[dict(pins=['RB1', 'RB2'])])}
    c = C.cihaz_karsilastir(r, '+CC', PR, ms)
    assert c['=A+CC-15RA7-RA1']['parcalar'] == ['SIE.R'] and c['=A+CC-15RA7-RA1']['fark'] == ['B+'] and c['=A+CC-15RA7-RA1']['ust'] == '=A+CC-15RA7'


def test_esittir_ile_baslayan_etiket_excelde_metin(tmp_path):
    import openpyxl as O
    wb = O.Workbook()
    wb.active.append(['=GEN+CC-1K1'])
    cikti.metin_yap(wb)
    y = tmp_path / 'x.xlsx'
    wb.save(y)
    assert O.load_workbook(y, data_only=True).active['A1'].value == '=GEN+CC-1K1'


def test_cizimci_listesi_her_kalem_sayfa_etiket_problem():
    import etiket_liste as E

    class I:
        kapsam = '+CC'

        def turlar(self):
            return []
    fs = [fn('f', '=A+CC-1K1', [uc('Z9', 0)], ['SIE.X'], sayfa='&EFS1=A/1'), fn('g', '=A+CC-2K1', [uc('', 0)], ['SIE.X'], sayfa='&EFS1=A/2'), fn('t', '=A+CC-X1:1', [uc('a')], sinif='Terminal')]
    b1, b2 = bag('c', '=A+CC-1K1', 'Z9', '=A+CC-X1:1', 'a', o1='f', o2='t'), bag('d', '=A+CC-2K1', '', '=A+CC-X1:1', 'a', o1='g', o2='t')
    for b_ in (b1,):
        b_['start']['page'] = '&EFS1=A/1'
    b2['start']['page'] = '&EFS1=A/2'
    r = dict(pages=[dict(functions=fs)], connections=[b1, b2])
    v = dict(ms={'SIE.X': dict(found=True, device_functions=[dict(pins=['A1'])])}, db={'sie.x'}, pr=PR, cdp={})
    ms = v['ms']
    cih = C.cihaz_karsilastir(r, '+CC', PR, ms)
    sat = [('=A+CC-1K1', 'SIE.X', '', '', '', 0, 0, '', '', 'Z9', '', '', 'Eşleşmeyen pini eşleştir')]
    w = dict(connection_id='c', ce=['=A+CC-1K1', 'Z9', '=A+CC-X1:1', 'a'], wire_code='', tip='DBUWH-6-H07V-K')
    t = dict(connection_id='d', ce=['=A+CC-2K1', '', '=A+CC-X1:1', 'a'], sayfa='&EFS1=A/2', aday_devre='PE', aday_renk='GNYE')
    L = E.cizimci_listesi(I(), r, dict(bekleyen=dict(tel=[t])), dict(wires=[w]), cih, v, sat)
    turler = {x[0]: x for x in L}
    assert turler['Pin'][1:3] == ('EFS1=A/1', '=A+CC-1K1') and 'Z9' in turler['Pin'][4]
    assert turler['Tanımsız pin'][1:3] == ('EFS1=A/2', '=A+CC-2K1')
    assert 'DBUWH-6' in turler['Tel kodu'][4] and 'GNYE' in turler['Kesit'][4]


def test_dogrula_eksik_okunan_plani_durdurur():
    a = bag('a', '=A+CC-1K1', 'A1', '+CC-X1:1', 'a')
    b = bag('b', '=A+CC-1K1', 'A2', '+CC-X1:2', 'a')
    r0, tam = dict(pages=[dict(functions=[])], connections=[a]), dict(pages=[dict(functions=[])], connections=[a, b])
    assert any('eksik okumadan' in n for n in C.dogrula(r0, tam, tam, dict(wires=[], pins=[], parts=[]), [])['neden'])


def test_hata_deftere_bir_kez_girer_test_kaydi_girmez(tmp_path, monkeypatch):
    d = tmp_path / 'defter.json'
    monkeypatch.setattr(K, 'DEFTER', d)
    monkeypatch.setattr(K, 'KOK', tmp_path)

    class I:
        ad = 'IS'
        kayit = tmp_path / 'kayit'
    K.defter_ekle(I, 'dogrula', 'KALDI: x')
    K.defter_ekle(I, 'dogrula', 'KALDI: x')
    K.defter_ekle(I, 'dogrula', 'KALDI: y')
    v = json.loads(d.read_text(encoding='utf-8'))
    assert [(x['no'], x['hata'], x['durum']) for x in v] == [(1, 'KALDI: x', 'AÇIK'), (2, 'KALDI: y', 'AÇIK')]
    I.kayit = Path('C:/baska')
    K.defter_ekle(I, 'dogrula', 'KALDI: z')
    assert len(json.loads(d.read_text(encoding='utf-8'))) == 2


def test_ethernet_yalniz_port_ucu():
    fs = [fn('a', '=A+CC-1KE1:20', [uc('20')], cat='PLCTerminal', sinif='Terminal'), fn('b', '=A+CC-2KE1:A1', [uc('A1')], cat='PLCTerminal', sinif='Terminal'),
          fn('c', '=A+CC-1KE1:P1', [uc('P1')], cat='PLCTerminal', sinif='Terminal'), fn('d', '=A+CC-2KE1:X1 P2', [uc('X1 P2')], cat='PLCTerminal', sinif='Terminal'),
          fn('e', '=A+CC-3KE1', [uc('1-8')], cat='PLCTerminal', sinif='Terminal')]
    cs = [bag('s', '=A+CC-1KE1:20', '20', '=A+CC-2KE1:A1', 'A1', '1,5', 'BK', 'a', 'b'), bag('p', '=A+CC-1KE1:P1', 'P1', '=A+CC-2KE1:X1 P2', 'X1 P2', '1,5', 'BK', 'c', 'd'),
          bag('q', '=A+CC-2KE1:X1 P2', 'X1 P2', '=A+CC-3KE1', '1-8', '1,5', 'BK', 'd', 'e')]
    k = {c['object_id']: x for c, x in C.tel_siniflari(_kisa(fs, cs), '+CC', PR, {})}
    assert k['s'] == 'TEK_KESITSIZ' and k['p'] == 'ETHERNET' and k['q'] == 'ETHERNET'
    p = _plan_devre(_kisa(fs, cs), {})
    assert {w['connection_id']: (w['renk'], w['kesit']) for w in p['wires']} == {'s': ('DBU', '0,75')}


def test_cihaz_ici_farkli_kontak_ucu_kopru_ayni_ad_degil():
    fs = [fn('a', '=A+CC-1QA1', [uc('1/L1'), uc('2/T1')], cat='NoContact'), fn('b', '=A+CC-1QA1', [uc('5/L3'), uc('6/T3')], cat='NoContact'),
          fn('d', '=A+CC-2TA1:U', [uc('U')], cat='DeviceEndTerminal'), fn('d2', '=A+CC-2TA1:U', [uc('U')], cat='DeviceEndTerminal'),
          fn('v', '=A+CC-3QA1', [uc('1')], cat='ProtectiveCircuit'), fn('w', '=A+CC-3QA1', [uc('A1')], cat='Coil')]
    cs = [bag('k', '=A+CC-1QA1', '2/T1', '=A+CC-1QA1', '6/T3', '1,5', 'BK', 'a', 'b'), bag('u', '=A+CC-2TA1:U', 'U', '=A+CC-2TA1:U', 'U', '1,5', 'BK', 'd', 'd2'),
          bag('m', '=A+CC-3QA1', '1', '=A+CC-3QA1', 'A1', '1,5', 'BK', 'v', 'w')]
    k = {c['object_id']: x for c, x in C.tel_siniflari(_kisa(fs, cs), '+CC', PR, {})}
    assert k['k'] == 'TEK_KESITSIZ' and k['u'] == 'CIHAZ_ICI' and k['m'] == 'CIHAZ_ICI'


def _seri_devre(yazili=('6',)):
    fs = [fn('f', '=A+CC-1FC1', [uc('1'), uc('2')], cat='Overload', arts=['SIE.S1']), fn('o', '=A+CC-1TB1:Out +', [uc('Out +')], cat='DeviceEndTerminal'),
          fn('k1', '=A+CC-2QA1', [uc('1/L1'), uc('2/T1')], cat='NoContact'), fn('k2', '=A+CC-3QA1', [uc('1/L1'), uc('2/T1')], cat='NoContact'),
          fn('k3', '=A+CC-4QA1', [uc('1/L1'), uc('2/T1')], cat='NoContact'), fn('t', '+CC-X1:1', [uc('a')], sinif='Terminal', cat='Terminal'),
          fn('m', '=A+CC-5QA1', [uc('A1'), uc('A2')], cat='Coil'), fn('t2', '+CC-X1:2', [uc('a')], sinif='Terminal', cat='Terminal'),
          fn('s2', '=A+CC-5FC1', [uc('1'), uc('2')], cat='Overload')]
    cs = [bag('b0', '=A+CC-1TB1:Out +', 'Out +', '=A+CC-1FC1', '1', '6', 'BU', 'o', 'f'),
          bag('b1', '=A+CC-1FC1', '2', '=A+CC-2QA1', '2/T1', yazili[0], 'BU', 'f', 'k1'),
          bag('z1', '=A+CC-2QA1', '1/L1', '=A+CC-3QA1', '1/L1', '1,5', 'BK', 'k1', 'k2'),
          bag('z2', '=A+CC-3QA1', '2/T1', '+CC-X1:1', 'a', '1,5', 'BK', 'k2', 't'),
          # Klemens zinciri keser: arkasındaki tel kesit almaz.
          bag('k0', '+CC-X1:1', 'a', '=A+CC-4QA1', '1/L1', '1,5', 'BK', 't', 'k3'),
          # Bobin zincire girmez.
          bag('y1', '+CC-X1:1', 'a', '=A+CC-5QA1', 'A1', '1,5', 'BK', 't', 'm')]
    if len(yazili) > 1:
        cs.append(bag('b2', '=A+CC-3QA1', '1/L1', '=A+CC-4QA1', '2/T1', yazili[1], 'BU', 'k2', 'k3'))
    return _kisa(fs, cs), {k: ['=+'] for k in ('b0', 'b1', 'b2')}


def test_seri_kesit_kontaktan_gecer_klemens_keser():
    r, cdp = _seri_devre()
    t = {w['connection_id']: w for w in _plan_devre(r, cdp)['wires']}
    assert (t['z1']['renk'], t['z1']['kesit']) == ('DBU', '6') and t['z2']['kesit'] == '6'
    assert t['k0']['kesit'] == '0,75' and t['y1']['kesit'] == '0,75'


def test_seri_ayni_sigorta_cikisindaki_kollar_tek_zincir_sigorta_gecilmez():
    r, cdp = _seri_devre()
    r['connections'].append(bag('b3', '=A+CC-1FC1', '2', '=A+CC-4QA1', '2/T1', '2,5', 'BU', 'f', 'k3'))
    cdp['b3'] = ['=+']
    assert 'z1' in {b['connection_id'] for b in _plan_devre(r, cdp)['bekleyen']['tel']}   # 6 ve 2,5 aynı düğümde
    # Sigortanın giriş tarafındaki b0 (6) çıkış zincirine geçmez.
    r, cdp = _seri_devre(('2,5',))
    assert {w['connection_id']: w['kesit'] for w in _plan_devre(r, cdp)['wires']}['z1'] == '2,5'


def test_seri_iki_kesit_otomatik_yazilmaz():
    r, cdp = _seri_devre(('6', '2,5'))
    p = _plan_devre(r, cdp)
    assert {'z1', 'z2'} <= {b['connection_id'] for b in p['bekleyen']['tel']}
    b = [x for x in p['bekleyen']['tel'] if x['connection_id'] == 'z1'][0]
    assert 'iki kesit' in b['kanit_ag'] and b['aday_devre'] == 'DC_ARTI'
    liste = [{'Kod': 'Sie.S1', 'Üretici Kodu': 'S1', 'Marka': 'Siemens', 'Açıklama': 'Otomatik sigorta 230/400V 10kA, C, 20A'}]
    p2 = _plan_devre(r, cdp, liste=liste, akim=[(10, '1,5'), (20, '2,5')])
    assert 'z1' not in {w['connection_id'] for w in p2['wires']}    # kullanıcı kuralı: iki kesitte otomatik yazılmaz
    b2 = [x for x in p2['bekleyen']['tel'] if x['connection_id'] == 'z1'][0]
    assert 'öneri' in b2['kanit_ag'] and '2,5' in b2['kanit_ag']


def _harici(pin='24VDC - 230VAC', tip='DO', role=True):
    tanim = 'en_US@PLC connection point, %s;' % tip
    fs = [fn('p', '=A+CC-1KE1:7', [uc('7')], cat='PLCTerminal', sinif='Terminal', arts=['SIE.R1']), fn('x', '+CC-X1:5', [uc('a')], sinif='Terminal', cat='Terminal'),
          fn('a', '=A+CC' + (':' + pin if pin else ''), [uc(pin)], cat='DeviceEndTerminal')]
    fs[0]['definition_name'] = tanim
    cs = [bag('t', '=A+CC-1KE1:7', '7', '+CC-X1:5', 'a', '1,5', 'BK', 'p', 'x'), bag('g', '=A+CC' + (':' + pin if pin else ''), pin, '+CC-X1:5', 'a', '1,5', 'BK', 'a', 'x')]
    liste = [{'Kod': 'Sie.R1', 'Üretici Kodu': 'R1', 'Marka': 'Siemens', 'Açıklama': 'SM 1223 DO 16x röle' if role else 'SM 1223 DO transistor'}]
    return _kisa(fs, cs), liste


def test_musteri_gerilimi_harici_og():
    # Harici gerilim: renk OG önerilir, kesiti çizimci verir (kullanıcı 05.11). Program yazmaz.
    for r, liste in (_harici(), _harici(pin='')):   # arayüzde gerilim adı; etiketsiz arayüz + röle DO
        p = _plan_devre(r, {}, liste=liste)
        assert 't' not in {w['connection_id'] for w in p['wires']}
        t = {x['connection_id']: x for x in p['bekleyen']['tel']}['t']
        assert t['aday_devre'] == 'HARICI' and t['aday_renk'] == 'OG' and not t['aday_kesit']
    r, liste = _harici(pin='', role=False)   # yarı iletken DO: PLC sinyali
    assert {w['connection_id']: w['renk'] for w in _plan_devre(r, {}, liste=liste)['wires']}['t'] == 'DBU'
    r, liste = _harici(pin='', tip='DI')    # röle modülünün DI ucu: PLC sinyali
    assert {w['connection_id']: w['renk'] for w in _plan_devre(r, {}, liste=liste)['wires']}['t'] == 'DBU'


def test_cli_eksik_girdide_durum_yazar(tmp_path):
    assert K.main(['--is', 'X', '--kayit', str(tmp_path / 'k'), '--adim', 'hazirla']) == 1
    d = json.loads((tmp_path / 'k' / 'durum.json').read_text(encoding='utf-8'))
    assert d['ok'] is False and d['adim'] == 'hazirla' and d['hata']


# ---------------------------------------------------------------- 26SB003 regresyonu

KAYIT = os.environ.get('YEDEK_IS_KAYIT')
LISTE_XLSX = os.environ.get('YEDEK_IS_LISTE')
MUSTERI = os.environ.get('YEDEK_IS_MUSTERI')
veri_var = bool(KAYIT and LISTE_XLSX and MUSTERI and Path(KAYIT, 'olcum', 'rb', 'readback_current.json').exists() and K.STANDART.exists())
gerekli = pytest.mark.skipif(not veri_var, reason='YEDEK_IS_KAYIT/LISTE/MUSTERI ya da standart dosyası yok')


@pytest.fixture(scope='module')
def is26(tmp_path_factory):
    """Geçmiş 26SB003 ölçümlerini yeni kayıt klasörüne ekler. EPLAN adımları sonucu hazır bulduğu için açılmaz."""
    d = tmp_path_factory.mktemp('is26')
    A = Path(KAYIT)
    (d / 'rb').mkdir()
    shutil.copy(A / 'olcum/rb/readback_current.json', d / 'rb')
    # Geçmiş koşuda bağlantı-CDP okuması yok. Harita denetim A'nın temiz planından: eski planda olup temiz planda olmayan tel kablo damarıdır.
    temiz = {w['connection_id'] for w in C.json_oku(A / 'son/plan.json')['wires']}
    damar = {w['connection_id']: ['-1WD0'] for w in C.json_oku(A / 'olcum/duzelt_plan.json')['wires'] if w['connection_id'] not in temiz}
    assert len(damar) == 6
    # Müşteri kesitli teller gerçek CDP taşır (yazma raporu cdp_count > 0). Kesitsiz 4 tel CDP'siz: harita onları içermez.
    damar.update({w['connection_id']: ['=+'] for w in C.json_oku(A / 'son/plan.json')['wires'] if w['old_cross_section']})
    (d / 'rb/baglanti_cdp.json').write_text(json.dumps(dict(ok=True, connections=damar)), encoding='utf-8')
    # Regresyon yalnız +CC ölçümüyle kuruldu: ek pano bu veride ölçülmedi.
    m = d / 'musteri'
    m.mkdir()
    pr_ = C.json_oku(Path(MUSTERI) / 'profil.json')
    pr_.pop('ek_pano', None)
    (m / 'profil.json').write_text(json.dumps(pr_, ensure_ascii=False), encoding='utf-8')
    shutil.copy(Path(MUSTERI) / 'ogrenilen.json', m)
    for ad in ('c_hep', 'c_yeni'):
        (d / 'olcum' / ad).mkdir(parents=True)
        shutil.copy(A / 'olcum2' / ad / 'makro_pinleri.json', d / 'olcum' / ad)
    # EPLAN adımı burada asla başlamaz: sonuçlar hazır, kalan olursa test düşer.
    def eplan_yasak(*a, **k):
        raise AssertionError('test EPLAN başlatmaya çalıştı: %s' % (a[2] if len(a) > 2 else a,))
    eplan.calistir = eplan_yasak
    # PDF yok: hazirla üretmeye kalkmasın.
    import fitz
    (d / 'pdf').mkdir()
    bos = fitz.open()
    bos.new_page()
    bos.save(d / 'pdf' / 'UVP_SNCT260121.pdf')
    bos.close()
    # hazirla ölçümü bitmiş görsün.
    shutil.copy(A / 'olcum2/c_hep/makro_pinleri.json', d / 'olcum/makro_pinleri.json')
    json.dump(dict(ad='26SB003', zw1='', elk='', pano='+CC', liste=LISTE_XLSX, pdf=None, proje_adi='SNCT260121', musteri_kok=str(m)),
              open(d / 'is.json', 'w', encoding='utf-8'))
    assert K.main(['--is', '26SB003', '--kayit', str(d), '--adim', 'hazirla']) == 0
    assert K.main(['--is', '26SB003', '--kayit', str(d), '--adim', 'ai_girdi']) == 0
    return d


def _plan26(d):
    return K.Is('26SB003', d).plan(dict(urun_kodu={}, uc=[], tel={}))


def _coklu(L, ks):
    return collections.Counter(tuple(str(C.kimlik_sonu(x.get(k))) for k in ks) for x in L)


@gerekli
def test_26sb003_plan_ayni(is26):
    A = Path(KAYIT)
    p = _plan26(is26)
    eski, kod = C.json_oku(A / 'olcum/duzelt_plan.json'), C.json_oku(A / 'olcum2/kod_plan.json')
    uk, pk = ('function_id', 'old', 'new'), ('function_id', 'old', 'new', 'variant')
    assert _coklu(p['pins'], uk) == _coklu(eski['pins'], uk) and len(p['pins']) == 16
    assert _coklu(p['parts'], pk) == _coklu(kod['parts'], pk) and len(p['parts']) == 15
    tk = ('connection_id', 'old_color', 'old_cross_section', 'color', 'cross_section', 'wire_type', 'wire_code')
    # Eski plan 519: denetim A 6 kablo damarı çıkardı, denetim B 2 gerçek tel ekledi. Referans temiz plan.
    temiz = C.json_oku(A / 'son/plan.json')
    assert len(p['wires']) == 515
    # Tek bilinçli fark: temiz plan DBUWH-4 için 4520923 yazıyor. Bu kod standartta ve parça DB'de yok: yeni kod yazmaz, eksik kablo listesine düşürür.
    yeni_kod, temiz_kod = _coklu(p['wires'], tk) - _coklu(temiz['wires'], tk), _coklu(temiz['wires'], tk) - _coklu(p['wires'], tk)
    assert {k[0] for k in yeni_kod} == {k[0] for k in temiz_kod} == {'18/72359/0', '18/72360/0'}
    assert all(k[-1] == '' for k in yeni_kod) and all(k[-1] == '4520923' for k in temiz_kod)
    assert [(x['tip'], x['baglanti']) for x in C.eksik_kablolar(p['wires'])] == [('DBUWH-4-H07V-K', 2)]
    fazla = _coklu(p['wires'], tk) - _coklu(eski['wires'], tk)
    # Eski plana göre yeni: 2 gerçek tel (denetim B) ve DBU-6 teline öğrenilen kod.
    assert {k[0] for k in fazla} == {'18/74233/0', '18/74235/0', '18/72356/0'}
    assert all(k[3:] == ('DBU', '', 'H05V-K', '4510142') for k in fazla if k[0] != '18/72356/0')
    assert [k[-1] for k in fazla if k[0] == '18/72356/0'] == ['4520144']
    assert _coklu(p['parts'], pk) == _coklu(temiz['parts'], pk) and _coklu(p['pins'], uk) == _coklu(temiz['pins'], uk)


@gerekli
def test_26sb003_kesin_kararlar_ai_ya_gitmez(is26):
    g = C.json_oku(is26 / 'ai_girdi.json')
    assert g['urun_kodu'] == [] and g['tel'] == []
    urunler = {s['urun'] for s in g['uc']}
    assert not any('3LD3354' in u or 'WEI.14781' in u for u in urunler)
    assert sum(len(s['cihazlar']) for s in g['uc']) == 25   # kontak ve tek aday çözdü; alt etiket ve X adlı cihazlar eklendi
    assert all(s['uvp_uclari'] is not None and s['musteri_uclari'] for s in g['uc'])


@gerekli
def test_26sb003_sanal_ve_gercek_sonuc_ayni(is26):
    A = Path(KAYIT)
    p = _plan26(is26)
    v = K.Is('26SB003', is26).veri()
    rk = C.json_oku(A / 'olcum2/k_rb/readback_current.json')
    cg = C.cihaz_karsilastir(rk, '+CC', v['pr'], v['ms'])
    for c in (p['cihaz'], cg):
        assert sum(1 for x in c.values() if x['fark']) == 26  # alt etiket ve X adlı cihazlar karşılaştırılır
        assert sum(1 for x in c.values() if x['parcalar'] and x['telli'] and not x['fark']) == 113  # X adlı cihazlar da karşılaştırılır


@gerekli
def test_26sb003_olcum_paketi_ayni(is26):
    A = Path(KAYIT)
    v = K.Is('26SB003', is26).veri()
    hep = {o['part_number'] for o in C.json_oku(A / 'olcum2/paket_hep.json')['pages'][0]['objects']}
    assert set(C.olcum_kodlari(v['r'], '+CC', v['pr'], v['ogr'])) == hep


@gerekli
def test_26sb003_gecmis_yazim_kaldi_der(is26):
    """Geçmiş veri bu kapıdan GEÇMEMELİ: denetim B'nin bulduğu 2 fazla nesne ve 3 plan farkı."""
    A = Path(KAYIT)
    i = K.Is('26SB003', is26)
    p = _plan26(is26)
    d = C.dogrula(i.taban_rb(), C.json_oku(A / 'olcum/s_taban/readback_current.json'), C.json_oku(A / 'olcum2/k_rb/readback_current.json'), C.yazma_plani(p),
                  [C.json_oku(A / 'olcum/s_duz/rapor.json'), C.json_oku(A / 'olcum2/kod_rapor.json')])
    assert not d['ok'] and d['yeni_fonksiyon'] == ['=GEN+CC-5T61', '=GEN+CC-U1']
    assert d['baglanti']['eksik'] == 0 and d['baglanti']['fazla'] == 0
    assert d['uc']['dogru'] == 16 and d['parca']['dogru'] == 15
    assert d['tel']['dogru'] == 512 and d['tel']['plan'] == 515     # 2 yeni tel ve DBU-6 kodu geçmiş koşuda yazılmadı
    # 6 kablo damarına geçmişte kod yazılmış; eski DLL 3 cihazda ürün sırasını değiştirmiş (yeni kapı bunu görür).
    assert d['plan_disi']['wire_part'] == 6


@gerekli
def test_26sb003_renkli_liste(is26, tmp_path):
    A = Path(KAYIT)
    i = K.Is('26SB003', is26)
    v = i.veri()
    p = _plan26(is26)
    c = tmp_path / 'liste.xlsx'
    rk = C.json_oku(A / 'olcum2/k_rb/readback_current.json')
    oz = cikti.renkli_liste(LISTE_XLSX, c, rk, '+CC', v['pr'], v['ms'], C.yazma_plani(p), v['ogr'], dict(ok=True))
    assert (oz['yesil'], oz['turuncu'], oz['kontrol_disi']) == (83, 5, 11)
    assert oz['say']['Güncellendi'] == 10
    assert oz['cihaz_fark'] == 26 and oz['cihaz_ayni'] == 113
    ws = openpyxl.load_workbook(c).worksheets[0]
    sut = [x.value for x in ws[1]].index('Kontrol')
    rows = list(ws.iter_rows(min_row=2))
    turuncu = {str(r[1].value) for r in rows if r[sut].value.startswith('Eşleşmedi')}
    assert '15038' in turuncu and 'GWADQ420X80-75' not in turuncu and len(turuncu) == 5  # GWADQ tek adayla çözülür
    guncel = {str(r[1].value) for r in rows if r[sut].value == 'Güncellendi'}
    assert len(guncel) == 10


@gerekli
def test_26sb003_cli_tam_akis_kaldi_listesi(is26):
    """uygula, dogrula, rapor: geçmiş yazma raporları ve geri okumalarla. KALDI rapora ve liste başlığına geçer."""
    A = Path(KAYIT)
    d = is26
    p = _plan26(d)
    (d / 'duzelt_1').mkdir()
    (d / 'duzelt_1/plan.json').write_text(json.dumps(C.yazma_plani(p), ensure_ascii=False), encoding='utf-8')
    rap = dict(ok=True, pins=C.json_oku(A / 'olcum/s_duz/rapor.json')['pins'], wires=C.json_oku(A / 'olcum/s_duz/rapor.json')['wires'], parts=C.json_oku(A / 'olcum2/kod_rapor.json')['parts'])
    (d / 'duzelt_1/rapor.json').write_text(json.dumps(rap, ensure_ascii=False), encoding='utf-8')
    (d / 'rb_kalici_1').mkdir()
    shutil.copy(A / 'olcum2/k_rb/readback_current.json', d / 'rb_kalici_1')
    (d / 'rb_taban').mkdir()
    shutil.copy(A / 'olcum/s_taban/readback_current.json', d / 'rb_taban')
    assert K.main(['--is', '26SB003', '--kayit', str(d), '--adim', 'uygula']) == 0   # tur yazılmış: EPLAN açılmaz
    assert K.main(['--is', '26SB003', '--kayit', str(d), '--adim', 'dogrula']) == 1
    assert json.loads((d / 'durum.json').read_text(encoding='utf-8'))['ok'] is False
    assert C.json_oku(d / 'dogrulama.json')['sonuc'] == 'KALDI'
    assert K.main(['--is', '26SB003', '--kayit', str(d), '--adim', 'rapor']) == 0
    ws = openpyxl.load_workbook(d / 'IS_EMRI_KONTROL.xlsx').worksheets[0]
    assert 'KALDI' in ws.cell(row=1, column=1).value
    rap = (d / 'RAPOR.html').read_text(encoding='utf-8')
    assert 'KALDI' in rap and 'Eksik kablolar' in rap and 'DBUWH-4-H07V-K' in rap
    assert 'Eksik kablolar' in openpyxl.load_workbook(d / 'IS_EMRI_KONTROL.xlsx').sheetnames
    h = (d / 'KARAR.html').read_text(encoding='utf-8')
    assert '__VERI__' not in h and '3RT2916-1BB00' in h
    assert (d / 'ZAMAN.csv').exists()


@gerekli
def test_26sb003_ai_karar_kapisi(is26):
    i = K.Is('26SB003', is26)
    v = i.veri()
    g = C.json_oku(is26 / 'ai_girdi.json')
    u = next(s for s in g['uc'] if s['uvp_uclari'])
    ka = dict(uc=[dict(urun=u['urun'], esleme={u['fark'][0]: 'UYDURMA'}, karar='ESLE', kanit='k'), dict(urun='OLMAYAN', karar='MUSTERI_KALSIN', kanit='k')],
              urun_kodu=[dict(p8_kod='OLMAYAN.1', uvp_kod='X', karar='DEGISTIR', kanit='k')])
    s = C.karar_kapisi(ka, g, v['ms'], v['db'], v['std'])
    assert not s['uc'] and len(s['red']) == 3


@gerekli
def test_26sb003_arsiv_listesi_eplan_kopyasi_ile_ayni():
    zw1 = KOK / '26SB003/Kundendaten/SNCT260121.zw1'
    edb = KOK / 'output/p8runs/20261105_26sb003_once/UVP_SNCT260121.edb'
    if not (zw1.exists() and edb.exists()):
        pytest.skip('yedek ya da açılmış kopya yok')
    assert eplan.arsiv_eksik(zw1, edb) == []


def test_dll_gereken_eylemleri_tasir():
    """Son derleme varsa sürecin istediği her eylem içinde olmalı."""
    adaylar = sorted((KOK / 'output/eplan_addin_bin').glob('Uvp.PdfToP8.Host*.dll'), key=os.path.getmtime)
    if not adaylar:
        pytest.skip('derleme yok')
    d = adaylar[-1]
    assert eplan.dll_iceriyor(d, eplan.GEREKEN) == []


K002 = os.environ.get('YEDEK_IS_26SB002')


@pytest.mark.skipif(not (K002 and Path(K002, 'rb', 'baglanti_cdp.json').exists() and K.STANDART.exists()), reason='YEDEK_IS_26SB002 yok')
def test_26sb002_ornekleme_bulgulari():
    i = K.Is('26SB002', K002)
    p = i.plan(dict(urun_kodu={}, uc=[], tel={}))
    t = {w['connection_id']: w for w in p['wires']}
    bek = {x['connection_id'] for x in p['bekleyen']['tel']}
    for b in ('66471', '66464', '66462', '66463', '66459'):
        assert '18/%s/0' % b in bek and '18/%s/0' % b not in t    # harici: kesiti çizimci verir
    assert t['18/66103/0']['kesit'] == '2,5' and t['18/66902/0']['renk'] == 'WH'    # artı-eksi ve analog kuralı
    for b in ('64958', '64959', '64960', '64961', '64962', '64963', '69702', '64953'):
        assert '18/%s/0' % b in bek and '18/%s/0' % b not in t    # 6 ve 2,5 aynı düğümde: kullanıcıya gider
    for b in ('66484', '66485', '63275', '63276', '64094'):
        assert t['18/%s/0' % b]['renk'] == 'DBU' and t['18/%s/0' % b]['kesit'] == '0,75'
