"""EPLAN yedeği gelen iş: tek tel, uç ve ürün kodu kararları, plan, doğrulama. Saf mantık; EPLAN açmaz, dosya yazmaz.

Kurallar MAIN.md "2026-11-05" maddeleri. Müşteriye özel her desen `profil.json`'dan, öğrenilen her karar `ogrenilen.json`'dan gelir.
Kanıtsız satır plana girmez: `bekleyen` listesine düşer, AI ya da kullanıcı karar verir.
"""
import collections
import json
import re

import devre

YER_TUTUCU_UC = re.compile(r'\.\d+')
KESIT = re.compile(r'\d+(?:,\d+)?')
DV = re.compile(r'^((?:=[^+\-:]*)?(?:\+[^\-:]*)?-[^:]*)')
PORT = re.compile(r'^$|PN|^(X\d+\s*)?P\d+R?$|^\d+-\d+$|RJ45', re.I)
ROLE = re.compile(r'röle|relais|relay', re.I)


def kimlik_sonu(k):
    """Nesne kimliğinin ilk parçası EPLAN oturumuna göre değişir (108/18/46825/0, 150/18/46825/0). Oturumlar arası yalnız son 3 parça geçerlidir."""
    if isinstance(k, str) and k.count('/') == 3:
        return k.split('/', 1)[1]
    return k


def kimlik_duzelt(x):
    """Geri okuma ya da plan içindeki tüm nesne kimliklerini kimlik sonuna çevirir. Yerinde değiştirir, tekrar çağrılabilir."""
    if isinstance(x, dict):
        for a, b in x.items():
            if a in ('object_id', 'function_id', 'connection_id'):
                x[a] = kimlik_sonu(b)
            elif isinstance(b, (dict, list)):
                kimlik_duzelt(b)
    elif isinstance(x, list):
        for b in x:
            kimlik_duzelt(b)
    return x


def rb_oku(yol):
    return kimlik_duzelt(json_oku(yol))


def json_oku(yol):
    with open(yol, encoding='utf-8-sig') as f:
        return json.load(f)


def dv(ad):
    """Cihaz etiketi: '=GEN+CC-6TB1:+' -> '=GEN+CC-6TB1'."""
    m = DV.match(ad or '')
    return m.group(1) if m else (ad or '')


def nrm(x):
    return re.sub(r'[^A-Z0-9]', '', str(x or '').upper())


def kod_govde(kod):
    return kod.split('.', 1)[1] if '.' in kod else kod


def art(a):
    return a if isinstance(a, str) else (a.get('part_nr') or a.get('part_number'))


def pano_re(pano):
    """Kapsam deseni. Birden çok pano '|' ile: '+CC|+CP' (kullanıcı 05.11: +CP kapsamda)."""
    return '(?:' + '|'.join(re.escape(x) for x in pano.split('|')) + ')'


def pano_ici(ad, pano):
    """Tam yer adı: '+CC-', '+CC:' ya da sonda '+CC'. '+CCI' başka yerdir."""
    return re.search(pano_re(pano) + r'(?=$|[-:])', ad or '') is not None


def fonksiyonlar(r):
    for p in r['pages']:
        yield from p.get('functions', [])


def urun_listesi(u):
    return [u] if isinstance(u, str) else list(u)


def urun_anahtar(parcalar):
    return ' + '.join(sorted(parcalar))


# ---------------------------------------------------------------- ölçüm

def olcum_birlestir(*raporlar):
    """Aynı kod birkaç ölçümde varsa bulunan kayıt kazanır."""
    ms = {}
    for rap in raporlar:
        for p in rap.get('parts') or []:
            if p.get('found') or p['part_number'] not in ms:
                ms[p['part_number']] = p
    return ms


def gercek_uclar(p):
    """Ölçülen ürünün gerçek uç adları. Yer tutucu (.1 .2) sayılmaz."""
    uc = set()
    for d in (p or {}).get('device_functions') or []:
        uc |= {x for x in d.get('pins') or [] if x and not YER_TUTUCU_UC.fullmatch(x)}
    for f in (p or {}).get('functions') or []:
        uc |= {q['name'] for q in f.get('pins') or [] if q.get('name') and not YER_TUTUCU_UC.fullmatch(q['name'])}
    return uc


def birebir_bulundu(ms, kod):
    """Ölçüm kodu parça numarasıyla mı buldu. Eylem tip numarasıyla da bulabilir (TYPE_NR): o eşleşme birebir değildir."""
    p = ms.get(kod) or {}
    return bool(p.get('found')) and (p.get('part_nr') or '').lower() == kod.lower()


def urun_aciklama(p):
    """Parça DB açıklaması: 'tr_TR@...;en_US@...' içinden Türkçe, yoksa ilk dil."""
    t = (p or {}).get('description') or ''
    m = re.search(r'tr_TR@([^;]*)', t) or re.search(r'@([^;]*)', t)
    return (m.group(1) if m else t).strip()


# ---------------------------------------------------------------- tek tel

def gercek_cdp(c, cdp_ad, kablo):
    """Kesit ve rengi müşterinin mi. CDP'siz bağlantıda değerler proje varsayılanıdır (26SB002: 281 tel 1,5 BK, CDP 0).

    cdp_ad None: harita yok, değerlere güvenilir (yalnız eski veri ve sentetik test).
    """
    if cdp_ad is None:
        return True
    return any(not kablo.search(x) for x in cdp_ad.get(c['object_id'], []))


def saha_cihazlari(r, pr):
    """Müşteri tarafı cihazlar: hiç ürünü yok, müşteri sayfasında çizili, bara değil (26SB002 -30SG4: harici NOT-HALT).

    Tel panoda değildir, kod yazılmaz. EPLAN da bu bağlara ürün eklemeyi reddetti (S063074).
    """
    desen = re.compile(pr.get('saha_sayfa_metni', 'Kunde'))
    musteri = {p.get('name') for p in r['pages'] if desen.search(json.dumps(p.get('geometry', ''), ensure_ascii=False))}
    cihaz = collections.defaultdict(list)
    for f in fonksiyonlar(r):
        cihaz[dv(f.get('name'))].append(f)
    return {d for d, fs in cihaz.items()
            if '-' in d.split('+')[-1].split(':')[0]
            and not any(f.get('articles') for f in fs)
            and any(f.get('page') in musteri for f in fs)
            and not any(f.get('category') == 'Busbar' or f.get('class_name') == 'Terminal' for f in fs)}


def tel_siniflari(r, pano, pr, cdp_ad):
    """Pano içi bağlantıları sınıflar. Dönüş: [(bağlantı, sınıf)]. Yalnız TEK_KESITLI ve TEK_KESITSIZ tek damarlı teldir.

    cdp_ad: bağlantı kimliği -> CDP adları, EPLAN nesnesinden (UvpPdfToP8BagCdp). Konumla eşleme kullanılmaz: gerçek teli kablo sayıyor
    (denetim B, 2 tel), kablo damarını kaçırıyordu (denetim A, 6 damar). Adında kablo deseni olan CDP'nin bağlantısı kablodur.
    """
    F = {f['object_id']: f for f in fonksiyonlar(r)}
    kablo = re.compile(pr['kablo_deseni'])
    dizi = re.compile(pano_re(pano) + '-(' + pr['klemens_dizi_deseni'] + ')')
    pano_arayuz = re.compile(r'=[A-Z]*' + pano_re(pano) + r'(:.*)?')
    saha = saha_cihazlari(r, pr)

    def st(e):
        m = dizi.search(e.get('function') or '')
        return m.group(1) if m else None

    def cat(e):
        return (F.get(e.get('object_id')) or {}).get('category') or e.get('kind')

    def sinif(c):
        s, e = c['start'], c['end']
        ss, se = st(s), st(e)
        f1, f2 = s.get('function') or '', e.get('function') or ''
        cs = c.get('cross_section') or ''
        col = c.get('wire_color') or ''
        if not gercek_cdp(c, cdp_ad, kablo):
            cs = ''    # varsayılan kesit tel sınıfını belirlemez
        if kablo.search(f1) or kablo.search(f2) or any(kablo.search(x) for x in (cdp_ad or {}).get(c['object_id'], [])):
            return 'KABLO'
        if col and re.fullmatch(r'\d+|SH|D', col):
            return 'KABLO'
        if dv(f1) in saha or dv(f2) in saha:
            return 'SAHA_CIHAZI'
        if ss and ss == se and not cs:
            # Dizi aynı, klemens farklı: köprü ya da tel. Otomatik yazılmaz, listelenir (26SB002 XD201:16-23).
            return 'KLEMENS_ARASI' if f1 != f2 and ':' in f1 and ':' in f2 else 'KLEMENS_ICI'
        if any(x in f1 or x in f2 for x in pr.get('pe_ray', [])):
            return 'PE_RAY' if not cs else 'TEK_KESITLI'
        if cs:
            return 'TEK_KESITLI'
        d1, d2 = dv(f1), dv(f2)
        # Aynı cihazda iki farklı kontak ucu makro içi değildir: gerçek köprüdür (31QA5:2/T1-6/T3).
        kopru = 'Contact' in (cat(s) or '') and 'Contact' in (cat(e) or '') and s.get('pin') and e.get('pin') and s.get('pin') != e.get('pin')
        if d1 == d2 and d1 and '-' in d1.split('+')[-1] and not kopru:
            return 'CIHAZ_ICI'
        if {cat(s), cat(e)} == {'Coil', 'ProtectiveCircuit'}:
            return 'TAKMA_VARISTOR'
        # İki PLC ucu tek başına Ethernet değildir: yalnız port uçları (P1, X150 P1, 1-8, RJ45) ya da adsız bus ucu.
        if cat(s) == 'PLCTerminal' and cat(e) == 'PLCTerminal' and PORT.search(s.get('pin') or '') and PORT.search(e.get('pin') or ''):
            return 'ETHERNET'
        if 'PN' in (s.get('pin') or '') or 'PN' in (e.get('pin') or ''):
            return 'ETHERNET'
        if pano_arayuz.fullmatch(f1) or pano_arayuz.fullmatch(f2) or any(x in f1 or x in f2 for x in pr.get('dis_arayuz', [])):
            return 'PANO_DISI_ARAYUZ'
        return 'TEK_KESITSIZ'

    def icinde(e):
        # Potansiyel tanım noktasının cihazı yok: karşı uç panodaysa tel panodadır (26SB002 GND, 7 tel).
        return e.get('kind') == 'PotentialDefinition' or pano_ici(e.get('function'), pano)

    return [(c, sinif(c)) for c in r['connections']
            if icinde(c['start']) and icinde(c['end']) and (pano_ici(c['start'].get('function'), pano) or pano_ici(c['end'].get('function'), pano))]


def standart_kodlar(xlsx):
    """UVP standart tablosu: 'DBU-0,75-H05V-K' -> stok kodu."""
    import openpyxl
    wb = openpyxl.load_workbook(xlsx, read_only=True, data_only=True)
    kod = {}
    for row in wb['NYAF Kablo Kodu ve Bilgileri'].iter_rows(values_only=True):
        v = [x for x in row if x is not None]
        for h in v:
            if re.match(r'^[A-Z]+-[\d,]+-H0', str(h)):
                kod[str(h).strip()] = str(v[0])
    wb.close()
    return kod


def tel_tipi(renk, kesit):
    return '%s-%s-%s-K' % (renk, kesit, 'H05V' if float(kesit.replace(',', '.')) <= 1 else 'H07V')


def ac_isareti(c, pr):
    """Telin kendi ucu nötr ya da AC işaretli mi. Ağ izlenmez: 24V kontaktör ana kontağından geçince yanlış AC veriyordu."""
    for e in (c['start'], c['end']):
        if (e.get('pin') or '') == 'N' or any(re.search(d, e.get('function') or '') for d in pr.get('ac_isaretleri', [])):
            return True
    return False


def tel_ogrenilen(c, ogr, is_adi):
    """Öğrenilen tel kararı yalnız kendi işinde geçerlidir: tel kararı başka projeye yayılmaz."""
    f = (c['start'].get('function') or '') + ' ' + (c['end'].get('function') or '')
    for k in ogr.get('tel', []):
        if k.get('is') == is_adi and k['bag_deseni'] in f:
            return k
    return None


def _urun_metni(u, ms, liste):
    la = liste_adaylari(u, liste)
    return ((la[0].get('Açıklama') or '') if la else '') + ' ' + urun_aciklama(ms.get(u))


def cihaz_metinleri(r, ms, liste):
    """Cihaz etiketi -> ürün açıklamaları. Önce iş emri listesi: amper ve 'röle' orada yazılı."""
    m = collections.defaultdict(list)
    for f in fonksiyonlar(r):
        for a in f.get('articles') or []:
            m[dv(f['name'])].append(_urun_metni(art(a), ms, liste))
    return m


ANALOG = re.compile(r'\b\d*\s*(?:AO|AI|AQ)\b|analog', re.I)
DIJITAL = re.compile(r'\b\d+\s*(?:DI|DO|ETOR|STOR)\b', re.I)


def analog_modul(metinler):
    """Yalnız analog modül: analog işareti var, dijital kanal yok. CPU'nun karışık kanalları ayrılamaz, bu kurala girmez."""
    return any(ANALOG.search(x) for x in metinler) and not any(DIJITAL.search(x) for x in metinler)


def _tel_kodu(renk, kesit, std, db):
    ad = tel_tipi(renk, kesit) if KESIT.fullmatch(kesit) and renk else ''
    kod = std.get(ad, '') if ad else ''
    durum = 'STANDARTTA_YOK' if not kod else ''
    # LAPP.<kod> parça DB'de yoksa kod yazılmaz, satır eksik kablo listesine düşer. DB okunamadıysa (boş küme) yazma raporu karar verir.
    if kod and db and 'lapp.' + kod.lower() not in db and kod.lower() not in db:
        kod, durum = '', 'DB_YOK: ' + kod
    return ad, kod, durum


def arti_eksi_esitle(satir, r, std, db):
    """Aynı cihazın besleme ucunda eksi tel artı telin en büyük kesitine çıkar (kullanıcı 05.11; ölçüm 7FB1:5 2,5).

    Yalnız kurallı eksi tel değişir: müşteri kesiti değişmez. Kontak ve bobin girmez: ana kontak 6, bobin A2 0,75 kalır.
    """
    F = {f['object_id']: f for f in fonksiyonlar(r)}
    B = {c['object_id']: c for c in r['connections']}

    def cihazlar(w):
        for e in (B[w['connection_id']]['start'], B[w['connection_id']]['end']):
            f = F.get(e.get('object_id')) or {}
            if e.get('function') and f and not re.search('Contact|Coil', f.get('category') or '') and (f.get('class_name') != 'Terminal' or f.get('category') == 'PLCTerminal'):
                yield dv(e['function'])

    def sayi(k):
        return float(k.replace(',', '.'))
    arti = collections.defaultdict(list)
    for w in satir:
        if w['renk'] == 'DBU' and w['connection_id'] in B:
            for d in cihazlar(w):
                arti[d].append(w['kesit'])
    for w in satir:
        if w['renk'] != 'DBUWH' or w['connection_id'] not in B or not w['kaynak'].startswith('KURAL'):
            continue
        en = max((k for d in cihazlar(w) for k in arti.get(d, ())), key=sayi, default='')
        if en and sayi(en) > sayi(w['kesit']):
            ad, kod, durum = _tel_kodu('DBUWH', en, std, db)
            w.update(cross_section=en, kesit=en, tip=ad, wire_type=ad.split('-', 2)[2] if ad else '', wire_code=kod, kod_durum=durum, kaynak='KURAL: artı ile aynı kesit (%s)' % en)
    return satir


def tel_plani(r, pano, pr, std, ogr, is_adi, kabul_tel, cdp_ad, db=(), ms=None, liste=(), akim=()):
    """Tek damarlı tellerin yazma satırları. Dönüş: (satırlar, bekleyen, hariç, sınıf sayıları).

    Kesitli: gerçek CDP'si var. CDP yoksa kesit ve renk müşterinin değildir: tel kesitsizdir, değer karar ya da devre sınıfından gelir.
    Eski değer planda kalır: yazma anında kapı olarak denetlenir.
    """
    ag = None
    kablo = re.compile(pr['kablo_deseni'])
    satir, bekleyen, haric, sayi = [], [], [], collections.Counter()
    for c, k in tel_siniflari(r, pano, pr, cdp_ad):
        sayi[k] += 1
        if k == 'KLEMENS_ARASI':
            haric.append(dict(connection_id=c['object_id'], ce=[c['start'].get('function'), c['start'].get('pin'), c['end'].get('function'), c['end'].get('pin')],
                              kaynak='Köprü ya da tel: karar sende'))
        if k not in ('TEK_KESITLI', 'TEK_KESITSIZ'):
            continue
        s, e = c['start'], c['end']
        ce = [s.get('function'), s.get('pin'), e.get('function'), e.get('pin')]
        og = tel_ogrenilen(c, ogr, is_adi)
        if og and og.get('karar') == 'HARIC':
            haric.append(dict(connection_id=c['object_id'], ce=ce, kaynak=og.get('kaynak', '')))
            continue
        kesitli = gercek_cdp(c, cdp_ad, kablo) and bool(c.get('cross_section'))
        ham_cs, ham_col = c.get('cross_section') or '', c.get('wire_color') or ''
        cs = ham_cs.replace('.', ',') if kesitli else ''
        col = ham_col if gercek_cdp(c, cdp_ad, kablo) else ''
        ucol, kaynak, notu = col, 'MÜŞTERİ KESİT', ''
        if col in ('BU', 'BUWH'):
            if ac_isareti(c, pr):
                notu = 'AC işareti: renk kontrol'
            else:
                ucol = 'D' + col
        if not cs:
            if c['object_id'] in kabul_tel:
                k2 = kabul_tel[c['object_id']]
                sec = (k2['kesit'], k2['renk'], k2.get('kaynak', 'KARAR'))
            elif og and og.get('karar') == 'YAZ':
                sec = (og['kesit'], og['renk'], 'ÖĞRENİLEN: ' + og.get('kaynak', ''))
            else:
                if ag is None:
                    metin = cihaz_metinleri(r, ms or {}, liste)
                    ag = devre.Ag(r, pr, {k for k, v in metin.items() if any(ROLE.search(x) for x in v)})
                    F = {f['object_id']: f for f in fonksiyonlar(r)}
                    seri = devre.Seri(r, lambda q: gercek_cdp(q, cdp_ad, kablo), lambda q: any(kablo.search(x) for x in (cdp_ad or {}).get(q['object_id'], [])))
                    amper_bul = lambda i: next((a for a in map(devre.amper_oku, metin.get(dv((F.get(i) or {}).get('name')), [])) if a), None)
                sinif, kanit_ag, amper = ag.sinif(c)
                seri_ks, seri_kanit = seri.kesit(c, amper_bul, akim) if sinif not in ('PE', 'AC') else (None, '')
                sec, aday_ks = None, ''
                renk_k = (devre.KESIN.get(sinif) or (None, None))[1] or devre.ADAY_RENK.get(sinif)
                analog = sinif == 'PLC_SINYAL' and any(analog_modul(metin.get(dv(x), [])) for x in (s.get('function'), e.get('function')))
                if analog:
                    sec = ('0,75', 'WH', 'KURAL: analog sinyal (kullanıcı 05.11)')
                elif sinif in devre.KESIT_CIZIMCI:
                    pass    # renk belli, kesiti çizimci verir (kullanıcı 05.11)
                elif seri_ks == '':
                    kanit_ag = (seri_kanit + '; ' + kanit_ag).strip('; ')    # iki kesit, kanıt yetmiyor: sor
                elif seri_ks and renk_k and not ac_isareti(c, pr):
                    sec = (seri_ks, renk_k, 'KURAL: %s, %s' % (sinif, seri_kanit))
                elif seri_ks:
                    aday_ks, kanit_ag = seri_ks, (seri_kanit + '; ' + kanit_ag).strip('; ')
                elif col in ('BU', 'BUWH') and sinif not in ('AC', 'PE'):
                    sec = ('0,75', 'D' + col, 'KURAL: müşteri rengi, kesit yok')
                elif sinif in devre.KESIN and not ac_isareti(c, pr):
                    sec = devre.KESIN[sinif] + ('KURAL: %s (%s)' % (sinif, kanit_ag),)
                if not sec:
                    bekleyen.append(dict(connection_id=c['object_id'], ce=ce, renk=col, sayfa=s.get('page'), aday_devre=sinif, kanit_ag=kanit_ag, amper_cihaz=amper,
                                         aday_kesit=aday_ks, aday_renk=renk_k or ''))
                    continue
            cs, ucol, kaynak = sec
        mx = re.fullmatch(r'(\d+)x(\d+(?:,\d+)?)', cs)
        if mx:
            notu = (notu + ' ' + mx.group(1) + ' paralel tel').strip()
            cs = mx.group(2)
        ad, kod, kod_durum = _tel_kodu(ucol, cs, std, db)
        satir.append(dict(connection_id=c['object_id'], old_color=ham_col, old_cross_section=ham_cs,
                          color=ucol if ucol != ham_col or not kesitli else '', cross_section=cs if not kesitli else '',
                          wire_type=ad.split('-', 2)[2] if ad else '', wire_code=kod, ce=ce, tip=ad, kaynak=kaynak, not_=notu, kod_durum=kod_durum,
                          kesit=cs, renk=ucol))
    return arti_eksi_esitle(satir, r, std, db), bekleyen, haric, sayi


def eksik_kablolar(wires):
    """Standart kodu bulunamayan tek teller, kesit + renk + tip başına. Kod önerilmez, kullanıcı verir."""
    g = {}
    for w in wires:
        if not w['wire_code']:
            k = (w.get('kesit', ''), w.get('renk', ''), w.get('tip', ''), w.get('kod_durum', ''))
            g.setdefault(k, []).append(w)
    return [dict(kesit=k[0], renk=k[1], tip=k[2], durum=k[3], baglanti=len(v), ornek=['%s:%s - %s:%s' % tuple(x['ce']) for x in v[:3]]) for k, v in sorted(g.items())]


def veri_kapisi(liste, edz_parca, edz_makro, db, makro_yol):
    """Ana veri kapısı: listedeki kod ve EDZ parçası parça DB'de, EDZ makrosu makro klasöründe var mı. Parça DB'ye yazmaz.

    edz_parca: EDZ içindeki parça adları. EDZ '@' önekini dosya adında tutabilir: 'CON@.x' DB'de 'CON.x' olarak da aranır.
    makro_yol: makro klasöründeki göreli yollar, küçük harfli. Dönüş: eksik listeleri ve sayılar.
    """
    liste_eksik = []
    for s in liste:
        if not (s.get('Kod', '').lower() in db or db_adaylari(s.get('Üretici Kodu') or '', db)):
            liste_eksik.append(dict(kod=s.get('Kod', ''), uretici_kodu=s.get('Üretici Kodu', ''), aciklama=s.get('Açıklama', '')))
    parca_eksik, at_notu = [], []
    for a in edz_parca:
        b = a.strip().lower()
        duz = b.replace('@.', '.')
        if b in db:
            continue
        if duz in db:
            at_notu.append(a)
        else:
            parca_eksik.append(a)
    adlar = {y.rsplit('/', 1)[-1] for y in makro_yol}
    makro_eksik = []
    for m in edz_makro:
        y = m.replace('\\', '/').lower()
        if y not in makro_yol:
            makro_eksik.append(dict(makro=m, durum='ad var, klasör farklı' if y.rsplit('/', 1)[-1] in adlar else 'yok'))
    return dict(liste_satir=len(liste), edz_parca=len(edz_parca), edz_makro=len(edz_makro), liste_eksik=liste_eksik,
                parca_eksik=parca_eksik, at_oneki_db_de_duz=at_notu, makro_eksik=makro_eksik, db_okundu=bool(db))


# ---------------------------------------------------------------- cihaz ve uç

def cihaz_haritasi(r, pano):
    fn = collections.defaultdict(list)
    for f in fonksiyonlar(r):
        if re.search(pano_re(pano) + '-', f['name']):
            fn[dv(f['name'])].append(f)
    return fn


def telli_uclar(r):
    """Cihaz -> başka cihaza bağlı uç adları. Makro içi bağlantı tel sayılmaz."""
    bag = collections.defaultdict(set)
    for c in r['connections']:
        a, b = c['start'], c['end']
        if dv(a.get('function')) != dv(b.get('function')):
            for e in (a, b):
                if e.get('pin'):
                    bag[dv(e.get('function'))].add(e['pin'])
    return bag


def kontak_kurali(fark, parcalar, ms):
    """Yardımcı kontak: son rakam işlevdir (1-2 NC, 3-4 NO; IEC 60947, üretici belgeleri), ilk rakam sıra numarasıdır.

    Makro yalnız yer tutucu verir (.1 .2 .3 .4). Şema numarası yazılmaz, korunur: 63 ucu .3 ile uyumludur (kullanıcı 05.11).
    İşlev sayısı makroyu aşarsa uç eşleşmemiş kalır. Dönüş: [(uç, yer tutucu)].
    """
    yer = collections.defaultdict(list)
    for u in parcalar:
        for d in (ms.get(u) or {}).get('device_functions') or []:
            for q in d.get('pins') or []:
                m = re.fullmatch(r"\.([1-4])'?", q or '')
                if m:
                    yer[m.group(1)].append(q)
    sonuc = []
    for uc in sorted(fark, key=lambda s: (len(s), s)):
        if re.fullmatch(r'\d[1-4]', uc or '') and yer.get(uc[1]):
            sonuc.append((uc, yer[uc[1]].pop(0)))
    return sonuc


def cihaz_karsilastir(r, pano, pr, ms):
    """Yalnız BAĞLI uçlar karşılaştırılır; aynı addaki uç aynıdır. Klemens ve kablo kapsam dışı."""
    disarida = re.compile(pr['klemens_deseni'] + '|' + pr['kablo_deseni'])
    fn = cihaz_haritasi(r, pano)
    bag = telli_uclar(r)
    cih = {}
    for k, fs in fn.items():
        if disarida.search(k):
            continue
        parcalar = sorted({art(a) for f in fs for a in f.get('articles') or []})
        uc = {q['name'] for f in fs for q in f.get('pins') or [] if q.get('name')}
        telli = sorted(uc & bag.get(k, set()))
        gercek, sablon, sema = set(), False, False
        for u in parcalar:
            p = ms.get(u) or {}
            gercek |= gercek_uclar(p)
            sablon = sablon or any(any(d.get('pins') or []) for d in p.get('device_functions') or [])
            sema = sema or bool(p.get('has_multiline'))
        fark = sorted(set(telli) - gercek)
        kontak = kontak_kurali(fark, parcalar, ms)
        fark = sorted(set(fark) - {x[0] for x in kontak})
        cih[k] = dict(parcalar=parcalar, telli=telli, fark=fark, uvp=sorted(gercek), sablon=sablon, sema=sema, kontak=kontak)
    return cih


def sanal_uygula(r, plan):
    """Planı geri okumanın kopyasına uygular: yazmadan önce sonucu görmek için. Girdi değişmez."""
    r = json.loads(json.dumps(r))
    pin = {(p['function_id'], p['old']): p['new'] for p in plan.get('pins', [])}
    parca = collections.defaultdict(dict)
    for p in plan.get('parts', []):
        parca[p['function_id']][p['old']] = p['new']
    for f in fonksiyonlar(r):
        for q in f.get('pins') or []:
            q['name'] = pin.get((f['object_id'], q.get('name')), q.get('name'))
        for a in f.get('articles') or []:
            if isinstance(a, dict) and art(a) in parca.get(f['object_id'], {}):
                a['part_nr'] = parca[f['object_id']][art(a)]
    for c in r['connections']:
        for e in (c['start'], c['end']):
            e['pin'] = pin.get((e.get('object_id'), e.get('pin')), e.get('pin'))
    return r


def _uc_hedef(h):
    return [h] if isinstance(h, str) else list(h)


def uc_plani(r, pano, pr, ms, kararlar):
    """Uç yazma satırları. Karar: {urun, esleme:{müşteri: uvp|[uvp,...]}, karar, kaynak}.

    Aynı adlı uçlar soldan sağa sıralanır ve hedefe sırayla atanır. Cihazın bağlı farkı kararın uç kümesini aşarsa karar o cihaza
    uygulanmaz: ürün + uç kümesi eşleşmesi. Hedef uç ölçümde yoksa satır yazılmaz.
    Dönüş: (satırlar, çözülen {cihaz: kaynak}, atlanan).
    """
    fn = cihaz_haritasi(r, pano)
    cih = cihaz_karsilastir(r, pano, pr, ms)
    satir, cozulen, atlanan = [], {}, []
    for k in sorted(cih):
        c = cih[k]
        if not c['fark'] or not c['parcalar']:
            continue
        esleme, kalsin, kaynak = {}, set(), ''
        for kar in kararlar:
            if not set(urun_listesi(kar['urun'])) <= set(c['parcalar']):
                continue
            if kar.get('karar') == 'MUSTERI_KALSIN':
                kalsin |= set(kar.get('uclar') or c['fark'])
                kaynak = kar.get('kaynak', '')
            elif kar.get('karar') == 'ESLE':
                esleme.update(kar.get('esleme') or {})
                kaynak = kar.get('kaynak', '')
        makro = set(c['uvp']) | {q for u in c['parcalar'] for d in (ms.get(u) or {}).get('device_functions') or [] for q in d.get('pins') or []}
        cozuldu = set(c['fark']) & kalsin
        for musteri in sorted((set(c['fark']) & set(esleme)) - kalsin):
            h = _uc_hedef(esleme[musteri])
            yok = sorted(set(h) - makro)
            if yok:
                atlanan.append(dict(cihaz=k, neden='hedef uç ölçümde yok', uc=yok, urun=c['parcalar']))
                continue
            es = sorted((q['location'][0], q['location'][1], f['object_id'])
                        for f in fn[k] for q in f.get('pins') or [] if q.get('name') == musteri)
            if len(es) > len(h):
                atlanan.append(dict(cihaz=k, neden='uç sayısı hedeften fazla', uc=musteri, urun=c['parcalar']))
                continue
            satir += [dict(function_id=fid, old=musteri, new=t, ce=k, urun=urun_anahtar(c['parcalar'])) for (_x, _y, fid), t in zip(es, h)]
            cozuldu.add(musteri)
        if cozuldu and cozuldu >= set(c['fark']):
            cozulen[k] = kaynak
    return satir, cozulen, atlanan


# ---------------------------------------------------------------- ürün kodu

def pano_kodlari(r, pano):
    """Pano fonksiyonlarındaki müşteri kodları: kod -> [fonksiyon]."""
    kod = collections.defaultdict(list)
    for f in fonksiyonlar(r):
        if pano_ici(f['name'], pano):
            for a in f.get('articles') or []:
                kod[art(a)].append(f)
    return kod


def kapsam_disi(kod, pr):
    return kod in pr.get('yer_tutucu_kodlar', []) or kod.startswith(tuple(pr.get('pakete_girmeyen_onekler', [])))


def olcum_kodlari(r, pano, pr, ogr, ek=()):
    """Ölçülecek kodlar: kablo/yer tutucu ve değişecek eski kodlar pakete girmez, yeni kodlar girer."""
    ogren = {k['p8_kod']: k['uvp_kod'] for k in ogr.get('urun_kodu', [])}
    kodlar = set(pano_kodlari(r, pano))
    yeni = {ogren[k] for k in kodlar if k in ogren}
    sade = {k for k in kodlar if k not in ogren and not kapsam_disi(k, pr)}
    return sorted(sade | yeni | set(ek))


def marka_uyar(kod, marka):
    """Kod öneki markanın başı mı: 'RIT' ~ 'Rittal'. Tek başına kanıt değildir."""
    on = nrm(kod.split('.', 1)[0]) if '.' in kod else ''
    return len(on) >= 2 and nrm(marka).startswith(on[:3])


def db_adaylari(kod, db):
    g = nrm(kod_govde(kod))
    return sorted(k for k in db if nrm(kod_govde(k)) == g) if g else []


def liste_adaylari(kod, liste):
    g = nrm(kod_govde(kod))
    return [s for s in liste if nrm(s['Üretici Kodu']) == g] if g else []


def kod_kararlari(r, pano, pr, ogr, liste, ms, db, kabul):
    """Her müşteri kodu için karar. Sıra: kabul edilen karar, öğrenilen, DB'de aynı, kapsam dışı, liste+DB birebir, bekleyen.

    Dönüş: {kod: {durum, yeni, varyant, kanit, kaynak}}. Yalnız DEGISTIR plan satırı üretir.
    """
    ogren = {k['p8_kod']: k for k in ogr.get('urun_kodu', [])}
    sonuc = {}
    for kod in pano_kodlari(r, pano):
        if kod in kabul:
            k = kabul[kod]
            sonuc[kod] = dict(durum='DEGISTIR', yeni=k['uvp_kod'], varyant=k.get('varyant', ''), kanit=k.get('kanit', ''), kaynak=k.get('kaynak', 'KARAR'))
        elif kod in ogren:
            k = ogren[kod]
            sonuc[kod] = dict(durum='DEGISTIR', yeni=k['uvp_kod'], varyant=k.get('varyant', ''), kanit=k.get('kaynak', ''), kaynak='ÖĞRENİLEN',
                              sart=k.get('sart_ayni_cihazda', ''))
        elif birebir_bulundu(ms, kod):
            sonuc[kod] = dict(durum='AYNI')
        elif kapsam_disi(kod, pr):
            sonuc[kod] = dict(durum='KAPSAM_DISI')
        else:
            la, da = liste_adaylari(kod, liste), db_adaylari(kod, db)
            olc = ms.get(da[0]) or {} if len(da) == 1 else {}
            if len(la) == 1 and len(da) == 1 and birebir_bulundu(ms, da[0]) and marka_uyar(da[0], la[0]['Marka']):
                yeni = olc.get('part_nr') or da[0]
                sonuc[kod] = dict(durum='DEGISTIR', yeni=yeni, varyant='', kaynak='LİSTE+DB BİREBİR', kanit=la[0]['Kod'] + ' = ' + yeni)
            else:
                sonuc[kod] = dict(durum='BEKLIYOR', liste=la, db=da)
    return sonuc


def parca_plani(r, pano, kararlar):
    satir, atlanan = [], []
    fn = cihaz_haritasi(r, pano)
    for f in fonksiyonlar(r):
        if not pano_ici(f['name'], pano):
            continue
        goruldu = set()
        for a in f.get('articles') or []:
            pn = art(a)
            k = kararlar.get(pn) or {}
            if k.get('durum') != 'DEGISTIR' or pn in goruldu:
                continue
            goruldu.add(pn)
            if k.get('sart'):
                ayni = {art(b) for g in fn.get(dv(f['name']), [f]) for b in g.get('articles') or []}
                if k['sart'] not in ayni:
                    atlanan.append(dict(ce=f['name'], old=pn, neden='öğrenilen şart yok: ' + k['sart']))
                    continue
            satir.append(dict(function_id=f['object_id'], old=pn, new=k['yeni'], variant=k.get('varyant', ''), ce=f['name']))
    return satir, atlanan


# ---------------------------------------------------------------- AI ve kullanıcı kararı kapısı

def karar_kapisi(karar, girdi, ms, db, std):
    """AI ya da kullanıcı kararını doğrular. Kapıdan geçmeyen satır plana girmez ve nedeniyle rapora yazılır.

    Kapılar: uvp_kod parça DB'de birebir, uvp ucu ölçümde, tel kodu standartta, kanıt dolu. Girdide olmayan satır reddedilir.
    Dönüş: dict(urun_kodu={p8: satır}, uc=[satır], tel={bağ: satır}, red=[...], belirsiz=[...]).
    """
    sonuc = dict(urun_kodu={}, uc=[], tel={}, red=[], belirsiz=[])

    def red(tur, anahtar, neden):
        sonuc['red'].append(dict(tur=tur, anahtar=anahtar, neden=neden))

    g_kod = {s['p8_kod'] for s in girdi.get('urun_kodu', [])}
    for s in karar.get('urun_kodu', []):
        p8, k = s.get('p8_kod'), s.get('karar')
        if p8 not in g_kod:
            red('urun_kodu', p8, 'girdide yok')
        elif k == 'DEGISTIR':
            u = s.get('uvp_kod') or ''
            m = ms.get(u)
            if m is not None and not birebir_bulundu(ms, u):
                red('urun_kodu', p8, 'uvp_kod ölçümde birebir bulunamadı: ' + u)
            elif m is None and u.lower() not in db:
                red('urun_kodu', p8, "uvp_kod parça DB'de birebir yok: " + u)
            elif not s.get('kanit'):
                red('urun_kodu', p8, 'kanıt yok')
            else:
                sonuc['urun_kodu'][p8] = dict(uvp_kod=u, varyant=s.get('varyant', ''), kanit=s['kanit'], kaynak=s.get('kaynak', 'AI'))
        elif k in ('BULUNAMADI', 'EMIN_DEGIL', 'ELLE'):
            sonuc['belirsiz'].append(dict(tur='urun_kodu', anahtar=p8, karar=k, kanit=s.get('kanit', '')))
        else:
            red('urun_kodu', p8, 'geçersiz karar: %s' % k)

    g_uc = {s['urun']: s for s in girdi.get('uc', [])}
    for s in karar.get('uc', []):
        u, k = s.get('urun'), s.get('karar')
        g = g_uc.get(u)
        if not g:
            red('uc', u, 'girdide yok')
        elif k == 'ESLE':
            hedef = [h for t in (s.get('esleme') or {}).values() for h in _uc_hedef(t)]
            yok = sorted(set(hedef) - set(g['uvp_uclari']))
            bilinmez = sorted(set(s.get('esleme') or {}) - {x['ad'] for x in g['musteri_uclari']})
            if not hedef:
                red('uc', u, 'eşleme boş')
            elif yok:
                red('uc', u, 'uvp ucu ölçümde yok: ' + ' '.join(yok))
            elif bilinmez:
                red('uc', u, 'müşteri ucu cihazda yok: ' + ' '.join(bilinmez))
            elif len(hedef) != len(set(hedef)):
                red('uc', u, 'aynı uvp ucu iki kez hedef')
            elif not s.get('kanit'):
                red('uc', u, 'kanıt yok')
            else:
                sonuc['uc'].append(dict(urun=u.split(' + '), esleme=s['esleme'], karar='ESLE', kanit=s['kanit'], kaynak=s.get('kaynak', 'AI')))
        elif k == 'MUSTERI_KALSIN':
            if not s.get('kanit'):
                red('uc', u, 'kanıt yok')
            else:
                sonuc['uc'].append(dict(urun=u.split(' + '), karar='MUSTERI_KALSIN', uclar=g.get('fark', []), kanit=s['kanit'], kaynak=s.get('kaynak', 'AI')))
        elif k in ('EMIN_DEGIL', 'ELLE', 'MAKRO_BEKLE'):
            sonuc['belirsiz'].append(dict(tur='uc', anahtar=u, karar=k, kanit=s.get('kanit', '')))
        else:
            red('uc', u, 'geçersiz karar: %s' % k)

    g_tel = {kimlik_sonu(s['bag']) for s in girdi.get('tel', [])}
    for s in karar.get('tel', []):
        b, k = kimlik_sonu(s.get('bag')), s.get('karar')
        if b not in g_tel:
            red('tel', b, 'girdide yok')
        elif k == 'YAZ':
            cs, renk = str(s.get('kesit') or '').replace('.', ','), s.get('renk') or ''
            if not KESIT.fullmatch(cs) or not renk:
                red('tel', b, 'kesit ya da renk geçersiz')
            elif tel_tipi(renk, cs) not in std:
                red('tel', b, 'tel kodu standartta yok: ' + tel_tipi(renk, cs))
            elif not s.get('kanit'):
                red('tel', b, 'kanıt yok')
            else:
                sonuc['tel'][b] = dict(kesit=cs, renk=renk, kanit=s['kanit'], kaynak=s.get('kaynak', 'AI'))
        elif k in ('EMIN_DEGIL', 'ELLE'):
            sonuc['belirsiz'].append(dict(tur='tel', anahtar=b, karar=k, kanit=s.get('kanit', '')))
        else:
            red('tel', b, 'geçersiz karar: %s' % k)
    return sonuc


def kabul_birlestir(*kabuller):
    """Sonraki kabul öncekini ezer. Aynı ürün için son uç kararı geçerlidir."""
    bir = dict(urun_kodu={}, uc=[], tel={}, red=[], belirsiz=[])
    for k in kabuller:
        if not k:
            continue
        bir['urun_kodu'].update(k.get('urun_kodu', {}))
        bir['tel'].update(k.get('tel', {}))
        yeni = {urun_anahtar(u['urun']) for u in k.get('uc', [])}
        bir['uc'] = [u for u in bir['uc'] if urun_anahtar(u['urun']) not in yeni] + list(k.get('uc', []))
        bir['red'] += k.get('red', [])
        bir['belirsiz'] += k.get('belirsiz', [])
    return bir


# ---------------------------------------------------------------- plan

def plan_kur(r, pano, pr, ogr, is_adi, liste, ms, db, std, kabul=None, cdp_ad=None, akim=()):
    """Tüm kararlardan tek yazma planı. Girdi değişmez. Bekleyen satırlar planda yoktur."""
    kabul = kabul or dict(urun_kodu={}, uc=[], tel={})
    r = kimlik_duzelt(json.loads(json.dumps(r)))
    if cdp_ad is not None:
        cdp_ad = {kimlik_sonu(k): v for k, v in cdp_ad.items()}
    kabul = dict(kabul, tel={kimlik_sonu(k): v for k, v in kabul['tel'].items()})
    kk = kod_kararlari(r, pano, pr, ogr, liste, ms, db, kabul['urun_kodu'])
    parts, p_atlanan = parca_plani(r, pano, kk)
    wires, t_bekleyen, t_haric, siniflar = tel_plani(r, pano, pr, std, ogr, is_adi, kabul['tel'], cdp_ad, db, ms, liste, akim)
    # Uç kararı yeni kodlu duruma göre verilir: ölçümde yeni kodun uçları geçerlidir.
    r2 = sanal_uygula(r, dict(parts=parts))
    uc_og = [dict(k, kaynak='ÖĞRENİLEN: ' + k.get('kaynak', '')) for k in ogr.get('uc', [])]
    pins, cozulen, u_atlanan = uc_plani(r2, pano, pr, ms, uc_og + list(kabul['uc']))
    cih = cihaz_karsilastir(sanal_uygula(r2, dict(pins=pins)), pano, pr, ms)
    return dict(pins=pins, wires=wires, parts=parts, kod=kk, cihaz=cih, cozulen=cozulen, siniflar=dict(siniflar),
                bekleyen=dict(urun_kodu=sorted(k for k, v in kk.items() if v['durum'] == 'BEKLIYOR'),
                              uc=sorted(k for k, c in cih.items() if c['fark'] and c['parcalar'] and k not in cozulen), tel=t_bekleyen),
                atlanan=dict(parca=p_atlanan, uc=u_atlanan), haric=t_haric)


def yazma_plani(plan):
    return {k: plan[k] for k in ('pins', 'wires', 'parts')}


def eskimis_satirlar(r, yp):
    """Plan satırı tabanla uyuşmuyorsa yazılmaz. Dönüş: (temiz plan, eskimiş satırlar)."""
    r = kimlik_duzelt(json.loads(json.dumps(r)))
    F = {f['object_id']: f for f in fonksiyonlar(r)}
    C = {c['object_id']: c for c in r['connections']}
    temiz, eski = dict(pins=[], wires=[], parts=[]), []
    for s in yp.get('pins', []):
        f = F.get(s['function_id'])
        if f and any(q.get('name') == s['old'] for q in f.get('pins') or []):
            temiz['pins'].append(s)
        else:
            eski.append(dict(tur='uc', satir=s))
    for s in yp.get('wires', []):
        c = C.get(s['connection_id'])
        if c and (c.get('wire_color') or '') == s['old_color'] and (c.get('cross_section') or '') == s['old_cross_section']:
            temiz['wires'].append(s)
        else:
            eski.append(dict(tur='tel', satir=s))
    for s in yp.get('parts', []):
        f = F.get(s['function_id'])
        if f and any(art(a) == s['old'] for a in f.get('articles') or []):
            temiz['parts'].append(s)
        else:
            eski.append(dict(tur='parca', satir=s))
    return temiz, eski


def plan_farki(yeni, yazilan):
    """Karar turu: daha önce yazılan satırları çıkarır."""
    def anah(tur, s):
        if tur == 'wires':
            return s['connection_id'], s['color'], s['cross_section'], s['wire_code']
        return s['function_id'], s['old'], s['new']
    return {t: [s for s in yeni[t] if anah(t, s) not in {anah(t, x) for x in yazilan.get(t, [])}] for t in ('pins', 'wires', 'parts')}


# ---------------------------------------------------------------- doğrulama

def bag_anahtar(c):
    a, b = c['start'], c['end']
    return tuple(sorted([(a.get('object_id') or '', a.get('index')), (b.get('object_id') or '', b.get('index'))], key=str))


YAZILDI = ('WRITTEN', 'WRITTEN_PART_NOT_IN_DB', 'WRITTEN_ARTICLE_REFUSED')


def dogrula(r0, taban, r1, yp, raporlar):
    """Kapı: bağlantı farkı 0, plan dışı kesit/renk/kod farkı 0, plan satırları %100 doğru, yazma satırları WRITTEN.

    r0: yazma öncesi geri okuma (kimlikler), taban: dokunulmamış kopyanın yeniden üretimi, r1: yazılan kopyanın yeniden üretimi.
    """
    r0, taban, r1, yp = (kimlik_duzelt(json.loads(json.dumps(x))) for x in (r0, taban, r1, yp))
    neden = []
    durum = collections.Counter()
    for rap in raporlar:
        if rap.get('ok') is False:
            neden.append('yazma raporu ok=false: %s' % rap.get('error'))
        for t in ('pins', 'wires', 'parts'):
            for s in rap.get(t) or []:
                durum[s['status']] += 1
    kotu = {k: v for k, v in durum.items() if k not in YAZILDI}
    urun_reddi = {s['connection_id'] for rap in raporlar for s in rap.get('wires') or [] if s['status'] == 'WRITTEN_ARTICLE_REFUSED'}
    if kotu:
        neden.append('yazılamayan satır: %s' % kotu)
    t0 = collections.Counter(bag_anahtar(c) for c in taban['connections'])
    okunan = collections.Counter(bag_anahtar(c) for c in r0['connections'])
    if okunan != t0:
        neden.append('plan eksik okumadan kuruldu: bağlantı güncellenmeden okuma %d, güncel %d' % (sum(okunan.values()), sum(t0.values())))
    t1 = collections.Counter(bag_anahtar(c) for c in r1['connections'])
    eksik, fazla = sum((t0 - t1).values()), sum((t1 - t0).values())
    if eksik or fazla:
        neden.append('bağlantı farkı: eksik %d, fazla %d' % (eksik, fazla))
    ix = collections.defaultdict(list)
    for c in r1['connections']:
        ix[bag_anahtar(c)].append(c)
    once = {c['object_id']: c for c in r0['connections']}
    tel_ok, tel_kotu = 0, []
    for w in yp.get('wires', []):
        cs = ix.get(bag_anahtar(once[w['connection_id']])) if w['connection_id'] in once else None
        if not cs:
            tel_kotu.append((w['ce'], 'bağlantı yok'))
            continue
        c = cs[0]
        if (c.get('wire_color') or '') == (w['color'] or w['old_color']) and \
                (c.get('cross_section') or '') == (w['cross_section'] or w['old_cross_section']) and \
                (not w['wire_code'] or w['connection_id'] in urun_reddi or w['wire_code'] in (c.get('wire_part') or '')):
            tel_ok += 1
        else:
            tel_kotu.append((w['ce'], c.get('wire_color'), c.get('cross_section'), c.get('wire_part')))
    F1 = {f['object_id']: f for f in fonksiyonlar(r1)}
    uc_ok, uc_kotu = 0, []
    for p in yp.get('pins', []):
        f = F1.get(p['function_id'])
        if f and any(q.get('name') == p['new'] for q in f.get('pins') or []):
            uc_ok += 1
        else:
            uc_kotu.append((p['ce'], p['old'], p['new']))
    parca_ok, parca_kotu = 0, []
    for p in yp.get('parts', []):
        f = F1.get(p['function_id'])
        ad = {art(a).lower() for a in (f or {}).get('articles') or []}
        if p['new'].lower() in ad and p['old'].lower() not in ad:
            parca_ok += 1
        else:
            parca_kotu.append((p['ce'], p['old'], p['new']))
    for ad, kotu_s, n in (('tel', tel_kotu, len(yp.get('wires', []))), ('uç', uc_kotu, len(yp.get('pins', []))), ('parça', parca_kotu, len(yp.get('parts', [])))):
        if kotu_s:
            neden.append('%s doğrulama: %d/%d kaldı' % (ad, len(kotu_s), n))
    # Plan dışı: plan satırı olmayan bağlantı ve fonksiyonlar tabanla aynı olmalı.
    pid = {bag_anahtar(once[w['connection_id']]) for w in yp.get('wires', []) if w['connection_id'] in once}
    pf = {p['function_id'] for p in yp.get('parts', [])}
    plan_disi = collections.Counter()
    d = {}
    for c in taban['connections']:
        d.setdefault(bag_anahtar(c), c)
    for k, c in d.items():
        if k in pid or k not in ix:
            continue
        q = ix[k][0]
        for alan in ('cross_section', 'wire_color', 'wire_part'):
            if (q.get(alan) or '') != (c.get(alan) or ''):
                plan_disi[alan] += 1
    F0 = {f['object_id']: f for f in fonksiyonlar(taban)}
    # Sıra da korunur: müşteri listesi sırası değişmez (denetim 26SB002: 17/79495/0).
    for p in yp.get('parts', []):
        f, g = F0.get(p['function_id']), F1.get(p['function_id'])
        if f and g and [p['new'] if art(a).lower() == p['old'].lower() else art(a) for a in f.get('articles') or []] != [art(a) for a in g.get('articles') or []]:
            plan_disi['parca_sirasi'] += 1
    for fid, f in F0.items():
        g = F1.get(fid)
        # CDP'deki parça tel kodudur, tel satırında doğrulanır.
        if g and fid not in pf and f['class_name'] != 'ConnectionDefinitionPoint' and                 {art(a) for a in f.get('articles') or []} != {art(a) for a in g.get('articles') or []}:
            plan_disi['parca'] += 1
    if plan_disi:
        neden.append('plan dışı fark: %s' % dict(plan_disi))
    # Plan fonksiyon eklemez ve silmez. Yeni nesne depo adedini bozar: =...-5T61 gibi (denetim B).
    # CDP sayılmaz: CDP'siz tele yazınca EPLAN CDP üretir (ölçüm 26SB003: 4 CDP, tel yazımının yan ürünü).
    yeni_f = sorted({dv(F1[i]['name']) for i in set(F1) - set(F0) if F1[i]['class_name'] != 'ConnectionDefinitionPoint'})
    silinen_f = sorted({dv(F0[i]['name']) for i in set(F0) - set(F1) if F0[i]['class_name'] != 'ConnectionDefinitionPoint'})
    if yeni_f:
        neden.append('plan dışı yeni fonksiyon: ' + ' '.join(yeni_f))
    if silinen_f:
        neden.append('silinen fonksiyon: ' + ' '.join(silinen_f))
    return dict(ok=not neden, sonuc='GEÇTİ' if not neden else 'KALDI', neden=neden, yazma_durum=dict(durum),
                baglanti=dict(taban=len(taban['connections']), yeni=len(r1['connections']), eksik=eksik, fazla=fazla),
                tel=dict(dogru=tel_ok, plan=len(yp.get('wires', [])), kotu=tel_kotu[:20], urun_reddi=len(urun_reddi)),
                uc=dict(dogru=uc_ok, plan=len(yp.get('pins', [])), kotu=uc_kotu[:20]),
                parca=dict(dogru=parca_ok, plan=len(yp.get('parts', [])), kotu=parca_kotu[:20]),
                plan_disi=dict(plan_disi), yeni_fonksiyon=yeni_f, silinen_fonksiyon=silinen_f,
                yeni_cdp=sum(F1[i]['class_name'] == 'ConnectionDefinitionPoint' for i in set(F1) - set(F0)))
