"""Kesitsiz telin devre sınıfı: bağlantı grafiği + kesinti oku adları + klemens numarası + PLC uçları.

Ağ: bağlantı uçları birleşir. Klemens tek düğümdür. Kontak, anahtar, sigorta kutbu geçilir; bobin ve modül durdurur.
Aynı adlı kesinti okları aynı ağdır. Ağda iki farklı sınıf varsa sonuç BELIRSIZ: yazılmaz, karar sorulur.
PE klemensi ve bara ağı geçirmez: 0V'un PE'ye bağlandığı projede (26SB002 XD1:2-XD1:PE) tüm devreler tek ağ olur. Ana kontak ucu (3/L2) AC kanıtı değildir:
24V da ana kontaktan geçer (26SB002 XD1:1.1-31QA1:3/L2).
HARICI (müşteri gerilimi): pano arayüzünde gerilim adlı uç ('24VDC - 230VAC'), ya da röle modülünün DO ucu + etiketsiz arayüz ucu. DI ucu ve yarı iletken DO HARICI değildir.
Seri (MAIN 2026-10-04): kontak ve ana kontak zinciri yazılı kesiti taşır; sigorta, bobin, PLC, kutu ve klemens zinciri keser.
Kesin sınıflar: DC_ARTI, DC_EKSI, PLC_SINYAL. AC ve PE otomatik yazılmaz.
"""
import re

GECER = re.compile(r'Contact|Switch|Overload|Fuse|Breaker')
KLEMENS = {'Terminal', 'Busbar', 'PhaseBar'}
AMPER_CIHAZ = re.compile(r'Overload|Fuse|Breaker|Motor')
OK_DESEN = [('DC_ARTI', r'^\+?24V'), ('DC_EKSI', r'^0V'), ('AC', r'^L[123]([._:]|$)'), ('AC', r'^N([._:]|$)'), ('PE', r'(^|:)(PE|GND)$')]
UC_DESEN = [('DC_ARTI', r"^(\d*L\+|\+|\+?24V(DC)?( ?\(\d+\))?|P24|Out \+'?)$"), ('DC_EKSI', r"^(\d*M|-|0V(DC)?( ?\(\d+\))?|L-|N24|Out -'?)$"),
            ('AC', r'^(\d/)?[LT][123]$|^N$|^[UVW]\d?$'), ('PE', r'^PE(\(\d+\))?$')]
KLEMENS_DESEN = [('AC', r'^\d*L[123]$|^\d*N$'), ('PE', r'^\d*PE$|^GND$')]
ETHERNET = re.compile(r'PN|^P\d|^X\d')
SERI = re.compile(r'Contact')
GERILIM = re.compile(r'\d\s*V')


def arayuz_mu(ad):
    """Cihaz etiketi olmayan pano ucu: '=GEN+CC', '=GEN+CC:24VDC - 230VAC'."""
    return bool(ad) and '-' not in ad.split(':')[0].split('+')[-1]


def _ilk(desen, ad):
    return next((s for s, d in desen if re.search(d, ad or '')), None)


class Ag:
    """Devre ağı. Düğüm: klemens için fonksiyon, diğerleri için fonksiyon + uç."""

    def __init__(self, r, pr, role_cihaz=()):
        self.F = {f['object_id']: f for p in r['pages'] for f in p.get('functions', [])}
        self.ata = {}
        self.tohum = {}
        ip = {p['object_id']: p for p in r.get('interruption_points') or []}
        dc = re.compile(pr['dc24_klemens_deseni'])
        for c in r['connections']:
            a, b = self.dugum(c['start']), self.dugum(c['end'])
            if self.pe_klemens(c['start']) == self.pe_klemens(c['end']):
                self.bir(a, b)
            # Yalnız alt bağlantının kesinti noktası: symbol_refs konum yakınlığıdır, başka ağın okunu da getirir.
            ids = []
            for s in c.get('sub_connections') or []:
                ids += [e.get('object_id') for e in (s.get('start') or {}, s.get('end') or {}) if e.get('kind') == 'InterruptionPoint']
            for i in ids:
                p = ip.get(i)
                if p:
                    self.bir(a, ('ok', p['name']))
                    self._tohum(('ok', p['name']), _ilk(OK_DESEN, p['name'].rsplit('-', 1)[-1]), 'ok ' + p['name'])
            for ad in c.get('potentials') or []:
                self._tohum(a, _ilk(OK_DESEN, ad), 'potansiyel ' + ad)
            for e, d in ((c['start'], a), (c['end'], b)):
                if not e.get('pin') and arayuz_mu((self.F.get(e.get('object_id')) or {}).get('name')):
                    self._tohum(d, 'ARAYUZ', 'arayüz ' + (e.get('function') or ''))
        for f in self.F.values():
            kat, ad = f.get('category') or '', f['name']
            if kat in KLEMENS:
                d = ('f', ad)
                self._tohum(d, _ilk(KLEMENS_DESEN, ad.rsplit(':', 1)[-1]) if ':' in ad else _ilk(OK_DESEN, ad.rsplit('-', 1)[-1]), 'klemens ' + ad)
                if dc.search(ad):
                    self._tohum(d, 'DC', 'klemens ' + ad)
                continue
            uclar = [q.get('name') for q in f.get('pins') or [] if q.get('name')]
            if GECER.search(kat):
                ciftler = zip(uclar, uclar[1:]) if len(uclar) % 2 else zip(uclar[::2], uclar[1::2])   # değişken kontak 3 uç
                for u, v in ciftler:
                    self.bir(('p', f['object_id'], u), ('p', f['object_id'], v))
                continue
            if kat == 'Coil':
                continue
            for u in uclar:
                d, s = ('p', f['object_id'], u), _ilk(UC_DESEN, u)
                if not s and kat == 'PLCTerminal' and not ETHERNET.search(u):
                    s = 'PLC'
                    if ad.split(':')[0] in role_cihaz and ', DO' in (f.get('definition_name') or ''):
                        self._tohum(d, 'ROLE_DO', 'röle DO %s:%s' % (ad, u))
                if not s and arayuz_mu(ad) and GERILIM.search(u):
                    s = 'HARICI'
                self._tohum(d, s, 'uç %s:%s' % (ad, u))
        self.gruplar = {}
        for d in list(self.ata) + list(self.tohum):
            self.gruplar.setdefault(self.kok(d), []).append(d)

    def dugum(self, e):
        """Klemens adıyla tek düğümdür (görünümden bağımsız). Cihaz ucu nesne + uç: aynı adlı PLC uçları farklı kanal olabilir."""
        f = self.F.get(e.get('object_id')) or {}
        if f.get('category') in KLEMENS:
            return 'f', f['name']
        # Adsız uç (varistör, 2 uç da ''): iki taraf birleşmesin, uç sırası ayırır.
        return 'p', e.get('object_id'), e.get('pin') or '#%s' % e.get('index')

    def pe_klemens(self, e):
        f = self.F.get(e.get('object_id')) or {}
        if f.get('category') not in KLEMENS:
            return False
        if f.get('category') == 'Busbar':
            return True    # toprak barası: GND, WE1, PE
        ad = f['name']
        return (_ilk(KLEMENS_DESEN, ad.rsplit(':', 1)[-1]) if ':' in ad else _ilk(OK_DESEN, ad.rsplit('-', 1)[-1])) == 'PE'

    def kok(self, d):
        self.ata.setdefault(d, d)
        while self.ata[d] != d:
            self.ata[d] = self.ata[self.ata[d]]
            d = self.ata[d]
        return d

    def bir(self, a, b):
        self.ata[self.kok(a)] = self.kok(b)

    def _tohum(self, d, sinif, kanit):
        if sinif:
            self.tohum.setdefault(d, []).append((sinif, kanit))

    def sinif(self, c):
        """Dönüş: (devre, kanıt metni, ağdaki amper cihazları)."""
        if self.pe_klemens(c['start']) or self.pe_klemens(c['end']):
            ad = c['start'] if self.pe_klemens(c['start']) else c['end']
            return 'PE', 'klemens ' + (ad.get('function') or ''), []
        dugumler = self.gruplar.get(self.kok(self.dugum(c['start'])), [])
        kanit = [t for d in dugumler for t in self.tohum.get(d, [])]
        s = {x for x, _k in kanit}
        guclu = s & {'DC_ARTI', 'DC_EKSI', 'AC', 'PE', 'HARICI'}
        if not guclu and {'ROLE_DO', 'ARAYUZ'} <= s:
            guclu = {'HARICI'}
        if len(guclu) > 1:
            devre = 'BELIRSIZ'
        elif guclu:
            devre = next(iter(guclu))
        elif 'PLC' in s:
            devre = 'PLC_SINYAL'
        else:
            devre = 'BELIRSIZ'
        oz = [k for x, k in kanit if x in guclu] or [k for _x, k in kanit]
        amper = sorted({d[1] for d in dugumler if d[0] == 'p' and AMPER_CIHAZ.search((self.F.get(d[1]) or {}).get('category') or '')})
        return devre, '; '.join(sorted(set(oz))[:5]), amper


KESIN = dict(DC_ARTI=('0,75', 'DBU'), DC_EKSI=('0,75', 'DBUWH'), PLC_SINYAL=('0,75', 'DBU'))
ADAY_RENK = dict(PE='GNYE', HARICI='OG')
# Kullanıcı 05.11: bu devrelerde kesiti program seçmez, çizimci verir. Renk öneridir.
KESIT_CIZIMCI = {'PE', 'HARICI'}


class Seri:
    """Kontak zincirleri. Düğüm: kontak fonksiyonu + uç. Zinciri yalnız kontak kutbu ve iki kontak arası tel kurar."""

    def __init__(self, r, gercek, kablo_mu):
        self.F = {f['object_id']: f for p in r['pages'] for f in p.get('functions', [])}
        self.ata, self.yazili, self.giris = {}, {}, {}
        for f in self.F.values():
            if SERI.search(f.get('category') or ''):
                u = [q.get('name') for q in f.get('pins') or [] if q.get('name')]
                for x, y in (zip(u, u[1:]) if len(u) % 2 else zip(u[::2], u[1::2])):
                    self.bir(('p', f['object_id'], x), ('p', f['object_id'], y))
        for c in r['connections']:
            if not kablo_mu(c):
                a, b = self.dugum(c['start']), self.dugum(c['end'])
                if a and b:
                    self.bir(a, b)
                # Sigorta içinden geçilmez, ama aynı çıkış ucuna bağlı iki kontak kolu aynı düğümdedir (7FC5:2 -> 31QA5:2/T1 ve 4/T2).
                for x, e in ((a, c['end']), (b, c['start'])):
                    kat = (self.F.get(e.get('object_id')) or {}).get('category') or ''
                    if x and e.get('pin') and AMPER_CIHAZ.search(kat) and not SERI.search(kat):
                        self.bir(x, ('s', e['object_id'], e['pin']))
        for c in r['connections']:
            if kablo_mu(c):
                continue
            for e, o in ((c['start'], c['end']), (c['end'], c['start'])):
                d = self.dugum(e)
                if not d:
                    continue
                k = self.kok(d)
                if gercek(c) and c.get('cross_section'):
                    self.yazili.setdefault(k, {}).setdefault(c['cross_section'].replace('.', ','), []).append(c['object_id'])
                kat = (self.F.get(o.get('object_id')) or {}).get('category') or ''
                if AMPER_CIHAZ.search(kat) and not SERI.search(kat):
                    self.giris.setdefault(k, set()).add(o['object_id'])

    def dugum(self, e):
        f = self.F.get(e.get('object_id')) or {}
        return ('p', e['object_id'], e.get('pin')) if SERI.search(f.get('category') or '') and e.get('pin') else None

    def kok(self, d):
        self.ata.setdefault(d, d)
        while self.ata[d] != d:
            self.ata[d] = self.ata[self.ata[d]]
            d = self.ata[d]
        return d

    def bir(self, a, b):
        self.ata[self.kok(a)] = self.kok(b)

    def kesit(self, c, amper_bul, tablo):
        """Dönüş: (kesit, kanıt). Zincir yoksa (None, ''). İki yazılı değer ve amper kanıtı yoksa ('', kanıt): otomatik yazılmaz.

        İki yazılı değerde tablo yalnız öneridir, karar kullanıcıya gider.
        """
        for e in (c['start'], c['end']):
            d = self.dugum(e)
            if not d:
                continue
            k = self.kok(d)
            y = self.yazili.get(k)
            if not y:
                continue
            if len(y) == 1:
                ks = next(iter(y))
                return ks, 'seri kesit %s (%s)' % (ks, y[ks][0])
            amper = {amper_bul(i) for i in self.giris.get(k, ())} - {None}
            ad = tablodan_kesit(next(iter(amper)), tablo) if len(amper) == 1 else ''
            metin = 'seri zincirde iki kesit: %s' % ' '.join(sorted(y))
            # Kullanıcı kuralı: müşteri değeri farklı iki kesit birleşirse otomatik yazılmaz; tablo yalnız öneri olur (MAIN: kontaktan sonra küçülmez).
            return '', ('%s; öneri %s A -> %s (UVP tablosu)' % (metin, next(iter(amper)), ad)) if ad else metin
        return None, ''


def amper_oku(aciklama):
    m = re.search(r'(\d+(?:[.,]\d+)?)\s*A\b', aciklama or '')
    return float(m.group(1).replace(',', '.')) if m else None


def akim_tablosu(xlsx):
    """UVP 'Kablo Kesiti Belirleme': [(amper, kesit)]."""
    import openpyxl
    wb = openpyxl.load_workbook(xlsx, read_only=True, data_only=True)
    t = []
    for row in wb['Kablolama Standartları'].iter_rows(values_only=True):
        h = [str(x) for x in row if x is not None]
        if len(h) >= 2:
            a, k = re.fullmatch(r'(\d+) A', h[0]), re.fullmatch(r'([\d,]+) mm²', h[1])
            if a and k:
                t.append((int(a.group(1)), k.group(1)))
    wb.close()
    return sorted(t)


def tablodan_kesit(amper, tablo):
    return next((k for a, k in tablo if a >= amper), '') if amper else ''
