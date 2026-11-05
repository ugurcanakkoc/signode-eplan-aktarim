"""EPLAN yedeği gelen iş: tek komut. Kurallar: KURALLAR.md (ana depoda MAIN.md "2026-11-05" maddeleri).

İlk kurulum:
  python -X utf8 tools/yedek_is/calistir.py --is <ad> --zw1 <yol> --pano +CC --liste <xlsx> [--pdf <yol>] [--edz <yol>] --adim hazirla
Sonraki adımlar yalnız --is ister:  --is <ad> --adim ai_girdi | ai_karar_al --ai-karar <json> | uygula | dogrula | rapor
                                    --is <ad> --adim karar_uygula --karar <kullanıcı json>
--adim verilmezse sırayla çalışır ve karar bekleyen satır varsa ai_girdi sonrası durur (çıkış 2).
Her adım kaldığı yerden sürer: ara sonuç diske yazılır, sonucu olan iş tekrar edilmez. Son durum <kayit>/durum.json.
Çıkış: 0 tamam, 1 durdu ya da hata, 2 karar bekliyor.
"""
import argparse
import datetime as dt
import json
import re
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cekirdek as C  # noqa: E402
import cikti  # noqa: E402
import devre  # noqa: E402
import eplan  # noqa: E402
from eplan import Dur  # noqa: E402

KOK = Path(__file__).resolve().parents[2]
STANDART = KOK / 'arsiv' / 'standartlar' / 'UVP Standartları  V1.xlsx'
PARCA_DB = Path(r'C:\Users\Public\EPLAN\Data\Ürünler\Company name\EPLAN_parts.adb\Article.eod')
DEFTER = KOK / 'output' / 'musteri' / 'signode' / 'hata_defteri.json'
ADIMLAR = ['hazirla', 'ai_girdi', 'ai_karar_al', 'uygula', 'dogrula', 'rapor', 'karar_uygula', 'olcum', 'yedek', 'teslim']


def yaz_json(yol, veri):
    yol = Path(yol)
    yol.parent.mkdir(parents=True, exist_ok=True)
    yol.write_text(json.dumps(veri, ensure_ascii=False, indent=1), encoding='utf-8')


def db_anahtarlari(yol=PARCA_DB):
    """Parça DB dizini, küçük harfli parça numaraları. EPLAN açmadan ön kapı; son söz ölçümün ve yazma raporunun.

    Ham dosya okuması: EPLAN sürümü değişirse boş dönebilir. Boşsa kapı yalnız ölçüme dayanır ve ölçülmemiş kodu reddeder.
    """
    try:
        b = Path(yol).read_bytes()
    except OSError:
        return set()
    return {m.group(1).decode('latin1') for m in re.finditer(rb'd[\x20-\x7e\x80-\xff]([a-z0-9][a-z0-9&_\-]{0,14}\.[\x20-\x7e]{1,70}?)\x00', b)}


class Is:
    """Bir işin kayıt klasörü ve girdileri."""

    def __init__(self, ad, kayit=None):
        self.ad = ad
        self.kayit = Path(kayit) if kayit else self._kayit_bul(ad)
        self.kayit.mkdir(parents=True, exist_ok=True)
        self.bilgi = self._oku('is.json') or {}
        self.uyarilar = []
        self._v = {}

    @staticmethod
    def _kayit_bul(ad):
        """Aynı işin klasörü varsa devam eder. is.json'u olmayan klasör başka koşunundur, ona yazılmaz."""
        kok = KOK / 'output' / 'ajan_kayit'
        for d in sorted(kok.glob('*_%s*' % ad), reverse=True):
            if (d / 'is.json').exists():
                return d
        temel = kok / ('%s_%s' % (dt.date.today().strftime('%Y%m%d'), ad))
        d, n = temel, 2
        while d.exists():
            d = temel.with_name('%s_%d' % (temel.name, n))
            n += 1
        return d

    def _oku(self, ad):
        p = self.kayit / ad
        return C.json_oku(p) if p.exists() else None

    def yol(self, *p):
        return self.kayit.joinpath(*p)

    def proje_klasoru(self, ek=''):
        return KOK / 'output' / 'p8runs' / (self.ad + ek)

    @property
    def elk(self):
        return self.proje_klasoru() / ('UVP_%s.elk' % self.bilgi['proje_adi'])

    # ---- veri
    def profil(self):
        return C.json_oku(Path(self.bilgi['musteri_kok']) / 'profil.json')

    def ogrenilen(self):
        p = Path(self.bilgi['musteri_kok']) / 'ogrenilen.json'
        return C.json_oku(p) if p.exists() else dict(urun_kodu=[], uc=[], tel=[])

    def taban_rb(self):
        return C.rb_oku(self.yol('rb', 'readback_current.json'))

    def olcum(self):
        rap = [C.json_oku(p) for p in sorted(self.yol('olcum').rglob('makro_pinleri.json'))]
        return C.olcum_birlestir(*rap)

    def liste(self):
        """İş emri listesi isteğe bağlı: yoksa kod yalnız parça DB ile karşılaştırılır."""
        return cikti.liste_oku(self.bilgi['liste']) if self.bilgi.get('liste') else []

    def pdf_uretilen(self):
        return self.yol('pdf', self.elk.stem + '.pdf')

    def pdf(self):
        """Müşteri PDF'i verildiyse o, yoksa test kopyasından EPLAN'ın ürettiği PDF. İkisi de yoksa resim olmaz."""
        if self.bilgi.get('pdf'):
            return self.bilgi['pdf']
        u = self.pdf_uretilen()
        return str(u) if u.exists() else None

    def veri(self):
        if not self._v:
            cdp = self.yol('rb', 'baglanti_cdp.json')
            if not cdp.exists():
                raise Dur('Bağlantı-CDP okuması yok: %s. Kablo damarı ayrılamaz, plan kurulmaz. --adim hazirla çalıştır.' % cdp)
            self._v = dict(cdp={C.kimlik_sonu(k): a for k, a in C.json_oku(cdp)['connections'].items()}, r=self.taban_rb(), ms=self.olcum(), pr=self.profil(), ogr=self.ogrenilen(), liste=self.liste(),
                           db=db_anahtarlari(), std=self.standart(), akim=devre.akim_tablosu(self.bilgi.get('standart') or STANDART))
        return self._v

    def standart(self):
        std = C.standart_kodlar(self.bilgi.get('standart') or STANDART)
        std.update({k['tip']: k['kod'] for k in self.ogrenilen().get('tel_kodlari', [])})
        return std

    @property
    def kapsam(self):
        """İş panosu ve profildeki ek panolar: '+CC|+CP'."""
        ek = [x for x in self.profil().get('ek_pano', []) if x != self.bilgi['pano']]
        return '|'.join([self.bilgi['pano']] + ek)

    def kabul(self, *adlar):
        """Kullanıcı 05.11: kesit ve pin kararını AI vermez. AI kararından yalnız ürün kodu plana girer."""
        k = []
        for a in adlar:
            d = self._oku(a)
            k.append(dict(d, uc=[], tel={}) if d and a == 'ai_kabul.json' else d)
        return C.kabul_birlestir(*k)

    def plan(self, kabul=None):
        v = self.veri()
        k = self.kabul('ai_kabul.json', 'kullanici_kabul.json') if kabul is None else kabul
        return C.plan_kur(v['r'], self.kapsam, v['pr'], v['ogr'], self.ad, v['liste'], v['ms'], v['db'], v['std'], k, v['cdp'], v['akim'])

    def turlar(self):
        return sorted((p for p in self.kayit.glob('duzelt_*') if p.is_dir()), key=lambda p: int(p.name.split('_')[1]))

    def yazilan_plan(self):
        bir = dict(pins=[], wires=[], parts=[])
        for t in self.turlar():
            p = t / 'plan.json'
            if p.exists() and (t / 'rapor.json').exists():
                y = C.json_oku(p)
                for k in bir:
                    bir[k] += y[k]
        return bir


# ---------------------------------------------------------------- adımlar

def readback(elk, klasor, update=False):
    param = dict(PROJECT=str(elk), OUT=str(klasor))
    if update:
        param['UPDATE'] = '1'
    return dict(action='UvpPdfToP8Readback', params=param, result=str(Path(klasor) / 'readback_current.json'))


def macros(paket, klasor):
    return dict(action='UvpPdfToP8Macros', result=str(Path(klasor) / 'makro_pinleri.json'),
                params=dict(PACKAGE=str(paket), OUT=str(klasor), TEMPLATE=eplan.sablon_bul(), ONE_PER_PAGE='1', LINKS='1'))


def paket_yaz(yol, kodlar):
    yaz_json(yol, dict(contract='uvp.pdf2p8.import-request', contract_version='1.0', pages=[dict(physical_page=1, objects=[
        dict(kind='DEVICE', device_tag='=P+Q-%d' % i, family='device_connection_point', part_number=c, pins=[]) for i, c in enumerate(kodlar)])]))


def adim_hazirla(i):
    rb_var, cdp_var = i.yol('rb', 'readback_current.json').exists(), i.yol('rb', 'baglanti_cdp.json').exists()
    pdf_var = bool(i.bilgi.get('pdf')) or i.pdf_uretilen().exists()
    if not (rb_var and cdp_var and pdf_var):
        adimlar = []
        if not i.elk.exists():
            kaynak = i.bilgi.get('elk')
            if kaynak:
                # Yedek önceden EPLAN'da açıldıysa: klasörü test kopyasına al. 26SB003 bu yolla ölçüldü.
                eplan.kopya(Path(kaynak).with_suffix('.edb'), i.elk.with_suffix('.edb'))
                i.elk.write_bytes(Path(kaynak).read_bytes().replace(
                    Path(kaynak).stem.encode('utf-16-le'), i.elk.stem.encode('utf-16-le')))
            else:
                adimlar.append(dict(action='UvpPdfToP8GeriYukle', result=str(i.yol('geri_yukle.json')),
                                    params=dict(ARCHIVE=i.bilgi['zw1'], PROJECT=str(i.elk), OUT=str(i.yol('geri_yukle.json')))))
        if not rb_var:
            adimlar.append(readback(i.elk, i.yol('rb'), update=True))    # Güncellenmeden okuma 2 bağ kaçırdı (26SB002)
        if not cdp_var:
            adimlar.append(dict(action='UvpPdfToP8BagCdp', result=str(i.yol('rb', 'baglanti_cdp.json')),
                                params=dict(PROJECT=str(i.elk), OUT=str(i.yol('rb', 'baglanti_cdp.json')))))
        if not pdf_var:
            adimlar.append(dict(action='UvpPdfToP8Pdf', result=str(i.pdf_uretilen()), params=dict(PROJECT=str(i.elk), OUT=str(i.pdf_uretilen().parent))))
        eplan.calistir(adimlar, i.kayit, 'hazirla_okuma')
        if i.bilgi.get('zw1') and not i.bilgi.get('elk') and not rb_var:
            eksik = eplan.arsiv_eksik(i.bilgi['zw1'], i.elk.with_suffix('.edb'))
            if eksik:
                raise Dur('Yedek eksik açıldı: %d dosya yok (%s).' % (len(eksik), ' '.join(eksik[:5])))
        eplan.elk_surum_kapisi(i.elk)
    if not i.yol('olcum', 'makro_pinleri.json').exists():
        r, pr, ogr, liste = i.taban_rb(), i.profil(), i.ogrenilen(), i.liste()
        db = db_anahtarlari()
        adaylar = set()
        for kod in C.pano_kodlari(r, i.kapsam):
            if kod.lower() not in db and not C.kapsam_disi(kod, pr):
                adaylar |= set(C.db_adaylari(kod, db))
        kodlar = C.olcum_kodlari(r, i.kapsam, pr, ogr, adaylar)
        paket_yaz(i.yol('olcum', 'paket.json'), kodlar)
        eplan.calistir([macros(i.yol('olcum', 'paket.json'), i.yol('olcum'))], i.kayit, 'hazirla_olcum')
    v = i.veri()
    vk = veri_kapisi_yap(i, v)
    p = i.plan()
    yaz_json(i.yol('hazirla_ozet.json'), _plan_ozet(p))
    return 'Uç %d, tel %d, parça %d otomatik. Bekleyen: ürün kodu %d, uç cihazı %d, tel %d. Veri kapısı eksik: liste %d, EDZ parça %d, EDZ makro %d.' % (
        len(p['pins']), len(p['wires']), len(p['parts']), len(p['bekleyen']['urun_kodu']), len(p['bekleyen']['uc']), len(p['bekleyen']['tel']),
        len(vk['liste_eksik']), len(vk['parca_eksik']), len(vk['makro_eksik']))


def veri_kapisi_yap(i, v):
    """Liste ve EDZ'yi parça DB ve makro klasörüyle karşılaştırır. Eksik raporda listelenir, parça DB'ye yazılmaz."""
    parca, makro = eplan.edz_icerik(i.bilgi['edz']) if i.bilgi.get('edz') else ([], [])
    vk = C.veri_kapisi(v['liste'], parca, makro, v['db'], eplan.makro_yollari() if makro else set())
    yaz_json(i.yol('veri_kapisi.json'), vk)
    return vk


def _plan_ozet(p):
    return dict(uc=len(p['pins']), tel=len(p['wires']), parca=len(p['parts']), siniflar=p['siniflar'], bekleyen=dict(
        urun_kodu=p['bekleyen']['urun_kodu'], uc=p['bekleyen']['uc'], tel=[t['connection_id'] for t in p['bekleyen']['tel']]),
        atlanan=p['atlanan'], haric=p['haric'], cozulen=p['cozulen'])


def girdi_uret(i, kabul):
    """AI'nın karar vermesi gereken satırlar. Bilgi eksikse aday listesi boş kalır."""
    v = i.veri()
    r, ms, liste, db, pano = v['r'], v['ms'], v['liste'], v['db'], i.kapsam
    p = i.plan(kabul)
    harita = cikti.pdf_sayfa_haritasi(i.pdf())
    kodlar = C.pano_kodlari(r, pano)
    kullanilan = {C.nrm(C.kod_govde(k)) for k in kodlar}

    def liste_sade(s):
        return {a: s.get(a, '') for a in ('Kod', 'Üretici Kodu', 'Marka', 'Açıklama')}

    urun_kodu = []
    esiz = [s for s in liste if C.nrm(s.get('Üretici Kodu')) not in kullanilan]
    for kod in p['bekleyen']['urun_kodu']:
        fs = kodlar[kod]
        k = p['kod'][kod]
        aday = k['liste'] or esiz
        db_ad = set(k['db'])
        for s in aday:
            db_ad |= set(C.db_adaylari(s.get('Üretici Kodu') or '', db))
        ana = next((f for f in fs if f.get('function_text')), fs[0])
        urun_kodu.append(dict(p8_kod=kod, cihazlar=sorted({C.dv(f['name']) for f in fs}), liste_adaylari=[liste_sade(s) for s in aday], liste_birebir=bool(k['liste']),
                              db_adaylari=sorted(db_ad), musteri_verisi=dict(function_text=ana.get('function_text') or '', makale=[a for a in ana.get('articles') or [] if C.art(a) == kod]),
                              pdf_sayfa=harita.get(ana.get('page'))))

    r2 = C.sanal_uygula(C.sanal_uygula(r, dict(parts=p['parts'])), dict(pins=p['pins']))
    fn = C.cihaz_haritasi(r2, pano)
    baglar = {}
    for c in r2['connections']:
        for a, b in ((c['start'], c['end']), (c['end'], c['start'])):
            if C.dv(a.get('function')) != C.dv(b.get('function')):
                baglar.setdefault((C.dv(a.get('function')), a.get('pin')), []).append(dict(karsi_uc='%s' % b.get('function'), karsi_pin=b.get('pin'), kesit=c.get('cross_section') or '', renk=c.get('wire_color') or ''))
    gruplar = {}
    for k in p['bekleyen']['uc']:
        gruplar.setdefault(C.urun_anahtar(p['cihaz'][k]['parcalar']), []).append(k)
    uc = []
    for anahtar, cihazlar in sorted(gruplar.items()):
        parcalar = anahtar.split(' + ')
        fark = sorted({u for k in cihazlar for u in p['cihaz'][k]['fark']})
        telli = sorted({u for k in cihazlar for u in p['cihaz'][k]['telli']})
        musteri = []
        for u in telli:
            ornek = next((b for k in cihazlar for b in baglar.get((k, u), [])), {})
            sayfa = next((f.get('page') for k in cihazlar for f in fn[k] if any(q.get('name') == u for q in f.get('pins') or [])), None)
            musteri.append(dict(ad=u, fark=u in fark, karsi_uc=ornek.get('karsi_uc', ''), kesit=ornek.get('kesit', ''), renk=ornek.get('renk', ''), pdf=harita.get(sayfa)))
        uc.append(dict(urun=anahtar, cihazlar=cihazlar, fark=fark, musteri_uclari=musteri, uvp_uclari=cikti.gercek_uclar_birlesik(parcalar, ms),
                       makro=[(ms.get(u) or {}).get('macro') or '' for u in parcalar], gorunumler=sorted({t for u in parcalar for t in (ms.get(u) or {}).get('representation_types') or []})))

    tel = []
    cih_parca = {k: sorted({C.art(a) for f in fs for a in f.get('articles') or []}) for k, fs in fn.items()}
    for t in p['bekleyen']['tel']:
        cihazlar = sorted({C.dv(t['ce'][0]), C.dv(t['ce'][2])})
        # Amper kaynağı: önce ağdaki sigorta/motor, yoksa telin uçlarındaki cihazlar. AC'de kesit yalnız sigorta/motor amperi kanıtlıysa önerilir.
        F = {f['object_id']: f for f in C.fonksiyonlar(r2)}
        ag_cihaz = sorted({C.dv(F[i]['name']) for i in t.get('amper_cihaz', []) if i in F})
        kaynak = [dict(cihaz=k, urun=u, aciklama=C._urun_metni(u, ms, liste).strip(), amper=devre.amper_oku(C._urun_metni(u, ms, liste)), ag=k in ag_cihaz)
                  for k in ag_cihaz + [x for x in cihazlar if x not in ag_cihaz] for u in cih_parca.get(k, [])]
        amper = max((a['amper'] for a in kaynak if a['ag'] and a['amper']), default=None)
        tel.append(dict(bag=t['connection_id'], uclar=['%s:%s' % (t['ce'][0], t['ce'][1]), '%s:%s' % (t['ce'][2], t['ce'][3])], pdf_sayfa=harita.get(t.get('sayfa')), cihaz=cihazlar,
                        aday_devre=t.get('aday_devre', ''), kanit_ag=t.get('kanit_ag', ''), aday_kesit=t.get('aday_kesit') or devre.tablodan_kesit(amper, v['akim']),
                        aday_renk=t.get('aday_renk') or devre.ADAY_RENK.get(t.get('aday_devre'), ''), aday_amper_kaynak=kaynak))
    return dict(urun_kodu=urun_kodu, uc=uc, tel=tel)


def adim_ai_girdi(i):
    g = girdi_uret(i, dict(urun_kodu={}, uc=[], tel={}))
    yaz_json(i.yol('ai_girdi.json'), g)
    return 'Karar gereken: ürün kodu %d, uç grubu %d, tel %d. Dosya %s.' % (len(g['urun_kodu']), len(g['uc']), len(g['tel']), i.yol('ai_girdi.json'))


def adim_ai_karar_al(i, yol):
    if not yol:
        raise Dur('--ai-karar verilmedi.')
    v = i.veri()
    if not i.yol('ai_girdi.json').exists():
        raise Dur('Önce --adim ai_girdi.')
    k = C.karar_kapisi(C.json_oku(yol), C.json_oku(i.yol('ai_girdi.json')), v['ms'], v['db'], v['std'])
    yaz_json(i.yol('ai_kabul.json'), k)
    return 'Kabul: ürün kodu %d, uç %d, tel %d. Red %d. Emin değil %d.' % (len(k['urun_kodu']), len(k['uc']), len(k['tel']), len(k['red']), len(k['belirsiz']))


def _yaz_turu(i, plan_tam):
    """Yeni yazma turu. Yazılmış satırlar tekrar yazılmaz. Dönüş: mesaj."""
    turlar = i.turlar()
    if turlar and not (turlar[-1] / 'rapor.json').exists() and (turlar[-1] / 'plan.json').exists():
        n, tur = int(turlar[-1].name.split('_')[1]), turlar[-1]
    else:
        n = (int(turlar[-1].name.split('_')[1]) + 1) if turlar else 1
        tur = i.yol('duzelt_%d' % n)
    if not (tur / 'plan.json').exists():
        yeni = C.plan_farki(C.yazma_plani(plan_tam), i.yazilan_plan())
        temiz, eski = C.eskimis_satirlar(i.taban_rb(), yeni)
        if eski:
            yaz_json(i.yol('eskimis_satir_%d.json' % n), eski)
        if not any(temiz.values()):
            return 'Plan boş. Yazma adımı atlandı.' + (' Eskimiş satır %d: yazılmadı.' % len(eski) if eski else '')
        yaz_json(tur / 'plan.json', temiz)
    if (tur / 'rapor.json').exists():
        return 'Tur %d zaten yazıldı.' % n
    # Kanıt: yazmadan önce yedek klasör. Yedek ezilmez.
    if not i.elk.exists():
        raise Dur('Test kopyası yok: %s' % i.elk)
    eplan.kopya(i.proje_klasoru(), i.proje_klasoru('_once_%d' % n))
    if n == 1:
        eplan.kopya(i.proje_klasoru(), i.proje_klasoru('_taban'))
    adimlar = []
    v = i.veri()
    olculmemis = sorted({p['new'] for p in plan_tam['parts']} - set(v['ms']))
    if olculmemis:
        ek = i.yol('olcum', 'ek_%d' % n)
        paket_yaz(ek / 'paket.json', olculmemis)
        adimlar.append(macros(ek / 'paket.json', ek))
    adimlar.append(dict(action='UvpPdfToP8Duzelt', result=str(tur / 'rapor.json'),
                        params=dict(PROJECT=str(i.elk), PLAN=str(tur / 'plan.json'), OUT=str(tur / 'rapor.json'))))
    b = eplan.calistir(adimlar, i.kayit, 'uygula_%d' % n)
    if b['uyari']:
        i.uyarilar.append(b['uyari'])
    y = C.json_oku(tur / 'rapor.json')
    bad = [s for t in ('pins', 'wires', 'parts') for s in y.get(t) or [] if s.get('status') not in C.YAZILDI]
    return 'Tur %d yazıldı. Yazılamayan satır %d.' % (n, len(bad))


def adim_uygula(i):
    return _yaz_turu(i, i.plan())


def adim_dogrula(i):
    turlar = [t for t in i.turlar() if (t / 'rapor.json').exists()]
    if not turlar:
        sonuc = dict(ok=False, sonuc='KALDI', neden=['yazma yapılmadı'], plan_bos=True)
        yaz_json(i.yol('dogrulama.json'), sonuc)
        return 'Yazma yapılmadı. Doğrulanacak iş yok.'
    n = turlar[-1].name.split('_')[1]
    kal = i.yol('rb_kalici_%s' % n)
    tab = i.yol('rb_taban')
    adimlar = []
    if not (kal / 'readback_current.json').exists():
        hedef = i.proje_klasoru('_kalici_%s' % n)
        eplan.kopya(i.proje_klasoru(), hedef)
        adimlar.append(readback(hedef / i.elk.name, kal, update=True))
    if not (tab / 'readback_current.json').exists():
        adimlar.append(readback(i.proje_klasoru('_taban') / i.elk.name, tab, update=True))
    if adimlar:
        eplan.calistir(adimlar, i.kayit, 'dogrula_%s' % n)
    sonuc = C.dogrula(i.taban_rb(), C.rb_oku(tab / 'readback_current.json'), C.rb_oku(kal / 'readback_current.json'),
                      i.yazilan_plan(), [C.json_oku(t / 'rapor.json') for t in turlar])
    sonuc['kalici'] = str(kal)
    yaz_json(i.yol('dogrulama.json'), sonuc)
    if not sonuc['ok']:
        raise Dur('KALDI: ' + '; '.join(sonuc['neden']))
    return 'GEÇTİ: bağlantı farkı 0, plan dışı fark 0, satırlar %d tel, %d uç, %d parça.' % (sonuc['tel']['plan'], sonuc['uc']['plan'], sonuc['parca']['plan'])


def adim_olcum(i):
    """Kullanıcı makro ya da ürün ekledikten sonra: bulunmayan ve uçsuz ürünleri yeniden ölçer. Eski ölçüm silinmez."""
    v = i.veri()
    kodlar = sorted(k for k in C.pano_kodlari(v['r'], i.kapsam) if not C.kapsam_disi(k, v['pr']) and not (v['ms'].get(k) or {}).get('found'))
    if not kodlar:
        return 'Yeniden ölçülecek ürün yok.'
    ek = i.yol('olcum', 'ek_' + dt.datetime.now().strftime('%m%d%H%M'))
    paket_yaz(ek / 'paket.json', kodlar)
    eplan.calistir([macros(ek / 'paket.json', ek)], i.kayit, 'olcum_' + ek.name)
    i._v = {}
    return 'Yeniden ölçüldü: %d ürün. Bulunan: %d.' % (len(kodlar), sum(1 for k in kodlar if (i.veri()['ms'].get(k) or {}).get('found')))


def adim_yedek(i):
    """Yazılmış test kopyasının EPLAN yedeği. Çizimciye verilecek dosya."""
    y = i.yol('yedek.json')
    y.unlink(missing_ok=True)
    eplan.calistir([dict(action='UvpPdfToP8Yedek', result=str(y), params=dict(PROJECT=str(i.elk), OUT=str(y)))], i.kayit, 'yedek_' + dt.datetime.now().strftime('%m%d%H%M'))
    zw1 = Path(C.json_oku(y)['yedek'])
    t = i.yol('TESLIM')
    t.mkdir(exist_ok=True)
    shutil.copy(zw1, t / ('%s_%s_UVP.zw1' % (i.ad, i.bilgi['proje_adi'])))
    et = i.yol('ETIKET_KONTROL_%s.xlsx' % i.ad)
    if et.exists():
        shutil.copy(et, t)
    return 'Yedek ve Excel: %s' % t


def adim_teslim(i):
    """Çizimciye gönderilecek zip: yedek, Excel, kısa not. Doğrulama geçmeden üretilmez."""
    import collections
    import zipfile

    import openpyxl
    d = i._oku('dogrulama.json') or {}
    if not d.get('ok'):
        raise Dur('Doğrulama geçmeden teslim paketi üretilmez.')
    t = i.yol('TESLIM')
    zw1 = t / ('%s_%s_UVP.zw1' % (i.ad, i.bilgi['proje_adi']))
    et = i.yol('ETIKET_KONTROL_%s.xlsx' % i.ad)
    if not (zw1.exists() and et.exists()):
        raise Dur('Önce --adim rapor ve --adim yedek çalıştır.')
    wb = openpyxl.load_workbook(et, read_only=True, data_only=True)
    say = collections.Counter(r[0] for r in wb['Çizimci Listesi'].iter_rows(min_row=2, values_only=True) if r[0])
    durum = collections.Counter(r[11] for r in wb['Etiket Kontrol'].iter_rows(min_row=2, values_only=True) if r[0])
    ka = sum(1 for r in wb['Klemens Arası'].iter_rows(min_row=2, values_only=True) if r[0])
    wb.close()
    b = d.get('baglanti') or {}
    satir = ['%s %s' % (i.ad, i.bilgi['proje_adi']), '',
             'Dosyalar', '- %s: EPLAN yedeği. Programın yaptığı değişiklikler içinde.' % zw1.name, '- %s: kontrol ve yapılacaklar.' % et.name, '',
             'Sıra', '1. Ürün kodu: Çizimci Listesi, Tür = Ürün kodu ve Makro.', '2. Pin: Çizimci Listesi, Tür = Pin ve Tanımsız pin.', '3. Klemens: program klemense dokunmadı. Klemens Arası sayfasına bak.', '4. Kesit: klemens yaparken kontrol et. Çizimci Listesi, Tür = Kesit ve Tel kodu.', '',
             'Excel renkleri', '- Yeşil: ürün kodu ve pin adı kontrol edildi. Tel ve kesit bu kontrole dahil değil.', '- Koyu turuncu: çizimci bakacak.', '- Gri: kapsam dışı.', '',
             'Sayılar', '- Kontrol edildi: %d. Çizimci bakacak: %d.' % (durum['Kontrol edildi'], durum['Çizimci bakacak'])]
    satir += ['- %s: %d' % (k, say[k]) for k in ('Ürün kodu', 'Makro', 'Pin', 'Tanımsız pin', 'Tel kodu', 'Kesit') if say[k]]
    satir += ['- Klemens arası bağ: %d' % ka, '- Yazılan: %d tel, %d pin, %d ürün kodu. Bağlantı farkı: %s eksik, %s fazla.' % ((d.get('tel') or {}).get('plan', 0), (d.get('uc') or {}).get('plan', 0), (d.get('parca') or {}).get('plan', 0), b.get('eksik'), b.get('fazla'))]
    hedef = KOK / 'output' / 'teslim' / ('%s_%s.zip' % (i.ad, i.bilgi['proje_adi']))
    hedef.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(hedef, 'w', zipfile.ZIP_DEFLATED) as z:
        z.write(zw1, zw1.name, compress_type=zipfile.ZIP_STORED)
        z.write(et, et.name)
        z.writestr('OKU_BENI.txt', (chr(13) + chr(10)).join(satir).encode('utf-8-sig'))
    with zipfile.ZipFile(hedef) as z:
        if z.testzip() is not None:
            raise Dur('Zip bozuk: %s' % hedef)
    return 'Teslim paketi: %s' % hedef


def _ogrenilen_satirlar(p):
    s = [('Ürün kodu', k, v.get('yeni') + ' / ' + v.get('kanit', '')) for k, v in sorted(p['kod'].items()) if v.get('kaynak') == 'ÖĞRENİLEN']
    s += [('Uç', k, v) for k, v in sorted(p['cozulen'].items()) if str(v).startswith('ÖĞRENİLEN')]
    return s


def _urun_reddi(i):
    """EPLAN'ın ürün kabul etmediği tel satırları: renk ve kesit yazılı, kod yok."""
    pl = {w['connection_id']: w for w in i.yazilan_plan()['wires']}
    return [pl[s['connection_id']] for t in i.turlar() if (t / 'rapor.json').exists() for s in C.json_oku(t / 'rapor.json').get('wires') or []
            if s['status'] == 'WRITTEN_ARTICLE_REFUSED' and s['connection_id'] in pl]


def adim_rapor(i):
    v = i.veri()
    kabul = i.kabul('ai_kabul.json', 'kullanici_kabul.json')
    p = i.plan(kabul)
    dog = i._oku('dogrulama.json')
    if dog and dog.get('kalici') and Path(dog['kalici'], 'readback_current.json').exists():
        r_son = C.rb_oku(Path(dog['kalici'], 'readback_current.json'))
        yazilan = i.yazilan_plan()
        gorunen = dict(pins=yazilan['pins'], parts=yazilan['parts'], wires=yazilan['wires'])
    else:
        r_son = C.sanal_uygula(C.sanal_uygula(v['r'], dict(parts=p['parts'])), dict(pins=p['pins']))
        gorunen = C.yazma_plani(p)
        dog = dog or dict(ok=False, neden=['doğrulama yapılmadı'])
    eksik = C.eksik_kablolar(gorunen['wires'])
    oz = cikti.renkli_liste(i.bilgi['liste'], i.yol('IS_EMRI_KONTROL.xlsx'), r_son, i.kapsam, v['pr'], v['ms'], gorunen, v['ogr'], dog, eksik) if i.bilgi.get('liste') and cikti.is_emri_bicimi(i.bilgi['liste']) else None
    g = girdi_uret(i, kabul)
    S = cikti.sorular(g, v['liste'], v['ms'], kabul, i.pdf())
    bilgi = [[k, 'Müşteride ürün kodu yok. Karşılaştırılacak ürün yok.'] for k, c in sorted(p['cihaz'].items()) if c['fark'] and not c['parcalar']]
    cikti.karar_html(i.yol('KARAR.html'), i.ad, i.bilgi['pano'], S, bilgi)
    sureler = []
    try:
        import zaman
        sureler = [(a, d if d is not None else 'açık') for a, _k, d, _t in zaman.ozet(i.yol('ZAMAN.csv'))]
    except (ImportError, FileNotFoundError):
        pass
    sonuc = cikti.durum_raporu(i.yol('RAPOR.html'), i.ad, i.bilgi['pano'], p, dog, oz, kabul, sureler, i.uyarilar, _ogrenilen_satirlar(p), eksik, i._oku('veri_kapisi.json'),
                                C.json_oku(DEFTER) if DEFTER.exists() else [],
                                _urun_reddi(i))
    yaz_json(i.yol('rapor_ozet.json'), dict(sonuc=sonuc, liste=oz, soru=len(S)))
    import etiket_liste
    et = i.yol('ETIKET_KONTROL_%s.xlsx' % i.ad)
    say, _ = etiket_liste.uret(i, et)
    return '%s. Yeşil %d, turuncu %d, kırmızı %d, gri %d. %s' % (sonuc, say[etiket_liste.YESIL], say[etiket_liste.TURUNCU], say[etiket_liste.KIRMIZI], say[etiket_liste.GRI], et)


def kullanici_cevir(karar):
    """Karar sayfasının JSON'u -> kapı biçimi. Kanıt: kullanıcının kendi seçimi."""
    k = dict(urun_kodu=[], uc=[], tel=[])
    for s in karar.get('kararlar', []):
        ek = dict(karar=s['karar'], kanit='kullanıcı kararı', kaynak='KULLANICI')
        e = s.get('esleme') or {}
        if s['tur'] == 'uc':
            k['uc'].append(dict(ek, urun=s['anahtar'], esleme={a: b for a, b in e.items() if b}))
        elif s['tur'] == 'urun_kodu':
            k['urun_kodu'].append(dict(ek, p8_kod=s['anahtar'], uvp_kod=e.get('uvp_kod', '')))
        elif s['tur'] == 'tel':
            k['tel'].append(dict(ek, bag=s['anahtar'], kesit=e.get('kesit', ''), renk=e.get('renk', '')))
    return k


def ogrenilene_ekle(i, kabul):
    """Doğrulaması geçen kullanıcı kararı öğrenilir: ürün kodu ve uç. Tel kararı yalnız bu işte geçerli kalır."""
    p = Path(i.bilgi['musteri_kok']) / 'ogrenilen.json'
    o = i.ogrenilen()
    tarih, kaynak = dt.date.today().isoformat(), '%s kullanıcı kararı' % i.ad
    for kod, s in kabul['urun_kodu'].items():
        o['urun_kodu'] = [x for x in o['urun_kodu'] if x['p8_kod'] != kod] + [dict(p8_kod=kod, uvp_kod=s['uvp_kod'], kaynak=kaynak, tarih=tarih)]
    for s in kabul['uc']:
        o['uc'] = [x for x in o['uc'] if C.urun_anahtar(C.urun_listesi(x['urun'])) != C.urun_anahtar(s['urun'])] + [dict(s, kaynak=kaynak, tarih=tarih)]
    yaz_json(p, o)


def adim_karar_uygula(i, yol):
    if not yol:
        raise Dur('--karar verilmedi.')
    v = i.veri()
    ai = i.kabul('ai_kabul.json')
    g = girdi_uret(i, ai)
    k = C.karar_kapisi(kullanici_cevir(C.json_oku(yol)), g, v['ms'], v['db'], v['std'])
    yaz_json(i.yol('kullanici_kabul.json'), k)
    m1 = _yaz_turu(i, i.plan())
    m2 = adim_dogrula(i)   # KALDI ise Dur: öğrenilmez.
    ogrenilene_ekle(i, k)
    return 'Kullanıcı kabul: ürün kodu %d, uç %d, tel %d, red %d. %s %s' % (len(k['urun_kodu']), len(k['uc']), len(k['tel']), len(k['red']), m1, m2)


# ---------------------------------------------------------------- akış

def defter_ekle(i, adim, hata):
    """Sistemin yakaladığı hata kalıcı deftere girer. Aynı hata iki kez yazılmaz. Sentetik test kaydı deftere girmez."""
    if KOK not in Path(i.kayit).parents:
        return
    d = C.json_oku(DEFTER) if DEFTER.exists() else []
    if not any((x.get('is'), x.get('adim'), x.get('hata')) == (i.ad, adim, hata) for x in d):
        d.append({'no': len(d) + 1, 'tarih': dt.date.today().isoformat(), 'is': i.ad, 'adim': adim, 'hata': hata, 'yakalayan': 'akış kapısı', 'kok_neden': '', 'duzeltme': '', 'test': '', 'durum': 'AÇIK'})
        yaz_json(DEFTER, d)


def tek_adim(i, ad, a):
    """Adımı çalıştırır, durum.json ve ZAMAN yazar. Dönüş: başarılı mı."""
    try:
        import zaman
    except ImportError:
        zaman = None
    if zaman:
        zaman.yaz(i.yol('ZAMAN.csv'), 'basla', ad, 'ajan')
    ok, ozet, hata = True, '', ''
    try:
        if ad == 'ai_karar_al':
            ozet = adim_ai_karar_al(i, a.ai_karar)
        elif ad == 'karar_uygula':
            ozet = adim_karar_uygula(i, a.karar)
        else:
            ozet = globals()['adim_' + ad](i)
    except Dur as e:
        ok, hata = False, str(e)
    except Exception as e:  # noqa: BLE001  durum.json makine okur: her hata dosyada görünür
        ok, hata = False, '%s: %s' % (type(e).__name__, e)
    if zaman:
        zaman.yaz(i.yol('ZAMAN.csv'), 'bitir', ad, 'ajan', (ozet or hata)[:120])
    if not ok:
        defter_ekle(i, ad, hata)
    yaz_json(i.yol('durum.json'), dict(adim=ad, ok=ok, ozet=ozet, hata=hata, uyari=i.uyarilar, kayit=str(i.kayit)))
    print(('TAMAM ' if ok else 'DURDU ') + ad + ': ' + (ozet or hata))
    return ok


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--is', dest='ad', required=True)
    ap.add_argument('--zw1')
    ap.add_argument('--elk', help='Yedek önceden EPLAN arayüzünde açıldıysa proje .elk yolu; test kopyasına alınır.')
    ap.add_argument('--pano')
    ap.add_argument('--liste')
    ap.add_argument('--pdf', help='Müşteri PDF. Yoksa test kopyasından EPLAN ile üretilir.')
    ap.add_argument('--edz', help='EDZ paketi. Parça ve makro adları veri kapısında denetlenir.')
    ap.add_argument('--musteri', default='signode')
    ap.add_argument('--kayit', help='Kayıt klasörü. Yoksa output/ajan_kayit/<tarih>_<is>.')
    ap.add_argument('--ai-karar', dest='ai_karar')
    ap.add_argument('--karar')
    ap.add_argument('--adim', choices=ADIMLAR)
    a = ap.parse_args(argv)
    i = Is(a.ad, a.kayit)
    if not i.bilgi:
        if not (a.pano and (a.zw1 or a.elk)):
            msg = 'İlk adım için --pano ve --zw1 (ya da --elk) gerekli.'
            yaz_json(i.yol('durum.json'), dict(adim=a.adim or 'hazirla', ok=False, ozet='', hata=msg, uyari=[], kayit=str(i.kayit)))
            print('DURDU: ' + msg)
            return 1
        kaynak = Path(a.zw1 or a.elk)
        i.bilgi = dict(ad=a.ad, zw1=a.zw1 and str(Path(a.zw1).resolve()), elk=a.elk and str(Path(a.elk).resolve()), pano=a.pano, liste=a.liste and str(Path(a.liste).resolve()),
                       pdf=a.pdf and str(Path(a.pdf).resolve()), edz=a.edz and str(Path(a.edz).resolve()), proje_adi=kaynak.stem, musteri_kok=str(KOK / 'output' / 'musteri' / a.musteri))
        if not (Path(i.bilgi['musteri_kok']) / 'profil.json').exists():
            msg = 'Müşteri profili yok: %s. Örnek: tools/yedek_is/profil_ornek.json' % i.bilgi['musteri_kok']
            yaz_json(i.yol('durum.json'), dict(adim=a.adim or 'hazirla', ok=False, ozet='', hata=msg, uyari=[], kayit=str(i.kayit)))
            print('DURDU: ' + msg)
            return 1
        yaz_json(i.yol('is.json'), i.bilgi)
    if a.adim:
        return 0 if tek_adim(i, a.adim, a) else 1
    # Tek komut akışı.
    if a.karar:
        return 0 if tek_adim(i, 'karar_uygula', a) and tek_adim(i, 'rapor', a) else 1
    if not tek_adim(i, 'hazirla', a) or not tek_adim(i, 'ai_girdi', a):
        return 1
    if a.ai_karar:
        if not tek_adim(i, 'ai_karar_al', a):
            return 1
    # Kullanıcı 05.11: pin ve kesit kararını AI vermez. Bekleyenler yazmayı durdurmaz, pin_kuyruk.py ekranına düşer.
    for ad in ('uygula', 'dogrula'):
        if not tek_adim(i, ad, a):
            tek_adim(i, 'rapor', a)   # KALDI raporu ve listesi yine üretilir.
            return 1
    return 0 if tek_adim(i, 'rapor', a) else 1


if __name__ == '__main__':
    sys.exit(main())
