"""EPLAN kapıları ve başlatma: sürüm, kilit, DLL seçimi, yedek açma denetimi, tek başlatmada eylem dizisi.

Yedek açma yöntemi (ölçüm 05.11, 26SB003 .zw1): py7zr 1.1.3 268 dosyanın 242'sini açtı ve TypeError verdi, CRC 181 dosyada tutmadı;
bsdtar 3.8.9 69 dosyada "Truncated 7-Zip file body" ile durdu. Bu yüzden .zw1 EPLAN'ın kendi `restore` eylemiyle açılır
(UvpPdfToP8GeriYukle; parametreler 2026 Eplan.EplApi.CommandLineActionsu.xml). py7zr yalnız dosya ADI listesi için kullanılır:
açılan klasör bu listeyle karşılaştırılır. EPLAN'ın açtığı 26SB003 kopyasında liste ile disk arasında fark 0 (`_` önekli 3 yedek dosyası hariç).
"""
import glob
import json
import os
import shutil
import subprocess
from pathlib import Path
from urllib.parse import unquote

KOK = Path(__file__).resolve().parents[2]
KILIT = KOK / 'output' / 'ajan_kayit' / '20261003_troester' / 'EPLAN_KILIT'
RUN_ACTION = KOK / 'eplan_addin' / 'build' / 'run_action.ps1'
KURULU = Path(os.environ.get('LOCALAPPDATA', '')) / 'UVP' / 'PdfToP8' / 'installed_dll.txt'
EPLAN_DESEN = r'C:\Program Files\EPLAN\Platform\2026*\Bin\EPLAN.exe'
SABLON_DESEN = r'C:\Users\Public\Eplan\Data\*\Company name\UVP_013.zw9'
MAKRO_KOK = Path(r'C:\Users\Public\EPLAN\Data\Makrolar\Company name')
EDZ_PARCA_DEGIL = ('.connectionpoints', '.manufacturer', '.construction', '.accessorylist')
# Bu makinede EPLAN 2026'nın .elk dosyasına yazdığı sürüm: ölçüm 05.11, 26SB003 kopyaları.
ELK_ANA_SURUM = 18
# Başlatma + eylem üst sınırı. 3 başlatmalı ölçüm: yazma 150 sn, geri okuma 180 sn, ölçüm 160 sn (26SB003 logları).
SINIR_SN = dict(UvpPdfToP8GeriYukle=300, UvpPdfToP8Readback=420, UvpPdfToP8BagCdp=120, UvpPdfToP8Pdf=420, UvpPdfToP8Macros=420, UvpPdfToP8Duzelt=420)
GEREKEN = ['UvpPdfToP8Readback', 'UvpPdfToP8Macros', 'UvpPdfToP8Duzelt', 'UvpPdfToP8GeriYukle', 'UvpPdfToP8BagCdp', 'UvpPdfToP8Pdf', 'UvpPdfToP8Dizi', 'UvpPdfToP8Yedek',
           'STALE_OLD_PART_NOT_FOUND', 'SKIPPED_CABLE_CDP']


class Dur(Exception):
    """Süreç bu noktada durur. Mesaj kullanıcıya gösterilir."""


def eplan_acik():
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq EPLAN.exe', '/NH'], capture_output=True, text=True, errors='replace')
    return 'eplan.exe' in r.stdout.lower()


def surum_kapisi(exeler=None):
    """Yalnız EPLAN 2026. run_action.ps1 aynı deseni kullanır."""
    exeler = glob.glob(EPLAN_DESEN) if exeler is None else exeler
    if not exeler:
        raise Dur('EPLAN 2026 bulunamadı. Başka sürümde çalışılmaz.')
    return exeler[0]


def elk_surum_kapisi(elk):
    """Proje EPLAN 2026'dan yeni bir sürümle yazıldıysa dur. Sürüm .elk dosyasının ikinci satırındadır (UTF-16)."""
    satir = Path(elk).read_bytes().decode('utf-16', errors='replace').split()
    ana = int(satir[1].split('.')[0]) if len(satir) > 1 and satir[1].split('.')[0].isdigit() else None
    if ana is None or ana > ELK_ANA_SURUM:
        raise Dur('Proje sürümü %s. Yalnız EPLAN 2026 (%d.x) ile çalışılır: %s' % (ana, ELK_ANA_SURUM, elk))
    return ana


def mesgul_kapisi():
    if KILIT.exists():
        raise Dur('EPLAN kilidi var: %s. Başka bir koşu sürüyor olabilir. Bitince tekrar başlat.' % KILIT)
    if eplan_acik():
        raise Dur('Açık EPLAN var. Kullanıcının oturumuna dokunulmaz. EPLAN kapanınca tekrar başlat.')


def dll_iceriyor(dll, adlar):
    d = Path(dll).read_bytes()
    return [a for a in adlar if a.encode('utf-16-le') not in d]


def dll_sec(gerekli=GEREKEN, kurulu=KURULU, klasor=KOK / 'output' / 'eplan_addin_bin'):
    """Kurulu DLL'de eylemler varsa kayıtsız tek başlatma. Yoksa son derleme + kayıt: eylem başına 3 başlatma ve açık uyarı.

    Dönüş: (dll, kayıtsız mı, uyarı).
    """
    kurulu = Path(kurulu)
    if kurulu.exists():
        d = Path(kurulu.read_text(encoding='utf-8-sig').strip())
        if d.exists() and not dll_iceriyor(d, gerekli):
            return str(d), True, ''
    adaylar = sorted(Path(klasor).glob('Uvp.PdfToP8.Host*.dll'), key=os.path.getmtime, reverse=True)
    for d in adaylar:
        if not dll_iceriyor(d, gerekli):
            return str(d), False, ('UYARI: kurulu DLL\'de gerekli eylem yok. %s kayıtla çalışır: her EPLAN işi 3 başlatma. '
                                   'Başlatma sayısını düşürmek için bu DLL\'i kur.' % d.name)
    raise Dur('Gerekli eylemleri içeren DLL yok (%s). eplan_addin/build/build.bat ile derle.' % ' '.join(gerekli))


def sablon_bul():
    b = glob.glob(SABLON_DESEN)
    if not b:
        raise Dur('UVP_013.zw9 şablonu bulunamadı.')
    return b[0]


def arsiv_eksik(zw1, edb):
    """Açılan proje klasörü yedekteki dosya adlarını içeriyor mu. Dönüş: eksik adlar. `_` önekli 3 dosya yedek bilgisidir."""
    import py7zr
    with py7zr.SevenZipFile(zw1) as z:
        adlar = z.getnames()
    disk = {str(p.relative_to(edb)).replace(os.sep, '/').lower() for p in Path(edb).rglob('*') if p.is_file()}
    return sorted(a for a in adlar if not a.startswith('_') and a.lower() not in disk)


def edz_icerik(edz):
    """EDZ paketi 7z'dir. Dönüş: (parça adları, makro göreli yolları). Yalnız ad listesi okunur, dosya açılmaz."""
    import py7zr
    with py7zr.SevenZipFile(edz) as z:
        adlar = z.getnames()
    parca, makro = set(), set()
    for a in adlar:
        d = a.replace('\\', '/')
        if d.lower().endswith('.part.xml'):
            ad = unquote(d.rsplit('/', 1)[-1][:-len('.part.xml')])   # dosya adında '/' -> %2F
            if not ad.lower().endswith(EDZ_PARCA_DEGIL):
                parca.add(ad)
        elif d.lower().startswith('items/macro/') and d.lower().endswith('.ema'):
            makro.add(d[len('items/macro/'):])
    return sorted(parca), sorted(makro)


def makro_yollari(kok=MAKRO_KOK):
    return {str(p.relative_to(kok)).replace(os.sep, '/').lower() for p in Path(kok).rglob('*.[eE][mM][aA]')}


def kopya(kaynak, hedef):
    """Proje klasörü kopyası. Kilit dosyaları kopyalanmaz. Hedef varsa dokunulmaz: yedek ezilmez."""
    hedef = Path(hedef)
    if hedef.exists():
        return False
    shutil.copytree(kaynak, hedef, ignore=shutil.ignore_patterns('*.lck'))
    return True


def _ps_tirnak(s):
    return "'" + str(s).replace("'", "''") + "'"


def _baslat(dll, kayitsiz, satir, sonuc, sure, log):
    komut = '& %s -Dll %s -Action %s -Result %s -TimeoutSec %d%s' % (
        _ps_tirnak(RUN_ACTION), _ps_tirnak(dll), _ps_tirnak(satir), _ps_tirnak(sonuc), sure, ' -NoRegister' if kayitsiz else '')
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', komut],
                       capture_output=True, text=True, errors='replace', timeout=sure + 300)
    with open(log, 'a', encoding='utf-8') as f:
        f.write('--- %s\n%s\n%s\n' % (satir, r.stdout, r.stderr))
    if r.returncode != 0 or not Path(sonuc).exists():
        ayrinti = ''
        if Path(sonuc).exists():
            d = json.loads(Path(sonuc).read_text(encoding='utf-8-sig'))
            kalan = [s for s in d.get('steps', []) if not s.get('dogru', True) or not s.get('result_exists')]
            ayrinti = ' %s %s' % (d.get('error') or '', json.dumps(kalan, ensure_ascii=False)[:400])
        raise Dur('EPLAN adımı bitmedi (çıkış %d).%s Ayrıntı: %s' % (r.returncode, ayrinti, log))


def sonuc_kapisi(adimlar):
    """Her adımın sonucu var ve ok=false değil. Dosyanın varlığı başarı değildir: eylem hatayı da JSON'a yazar."""
    for a in adimlar:
        y = Path(a['result'])
        if not y.exists():
            raise Dur('EPLAN adımı sonuç üretmedi: %s (%s)' % (a['action'], y))
        if y.suffix.lower() == '.json':
            d = json.loads(y.read_text(encoding='utf-8-sig'))
            if d.get('ok') is False:
                raise Dur('EPLAN adımı başarısız: %s: %s (%s)' % (a['action'], d.get('error'), y))


def eylem_satiri(ad, param):
    return ad + ''.join(' /%s:"%s"' % (k, v) for k, v in param.items())


def calistir(adimlar, kayit, etiket):
    """Adımları EPLAN'da çalıştırır. Adım: dict(action, params, result). Kilit alır, bitince siler.

    Önceki deneme kaldıysa aynı etiketi körlemesine tekrarlamaz: `hata_<etiket>.json` silinmeden başlamaz.
    Dizi eylemi varsa hepsi tek başlatmadadır. Dönüş: başlatma sayısı ve uyarı.
    """
    kayit = Path(kayit)
    hata = kayit / ('hata_%s.json' % etiket)
    if hata.exists():
        raise Dur('Bu adım önceki denemede kaldı: %s. Nedeni çöz, dosyayı sil, sonra tekrar başlat.' % hata)
    surum_kapisi()
    mesgul_kapisi()
    dll, kayitsiz, uyari = dll_sec()
    log = kayit / 'eplan.log'
    try:
        KILIT.parent.mkdir(parents=True, exist_ok=True)
        with open(KILIT, 'x', encoding='utf-8') as f:
            f.write('yedek_is %s pid=%d' % (etiket, os.getpid()))
    except FileExistsError as e:
        raise Dur('EPLAN kilidi az önce alındı: %s' % KILIT) from e
    try:
        for a in adimlar:
            Path(a['result']).parent.mkdir(parents=True, exist_ok=True)
        dizi = kayit / ('dizi_%s.json' % etiket)
        rapor = kayit / ('dizi_%s_rapor.json' % etiket)
        dizi.write_text(json.dumps(dict(steps=adimlar), ensure_ascii=False), encoding='utf-8')
        sure = sum(SINIR_SN.get(a['action'], 420) for a in adimlar)
        _baslat(dll, kayitsiz, eylem_satiri('UvpPdfToP8Dizi', dict(PLAN=dizi, OUT=rapor)), rapor, sure, log)
        sonuc_kapisi(adimlar)
    except Exception as e:
        hata.write_text(json.dumps(dict(etiket=etiket, hata=str(e)), ensure_ascii=False), encoding='utf-8')
        raise
    finally:
        KILIT.unlink(missing_ok=True)
    return dict(baslatma=1 if kayitsiz else 3, uyari=uyari)
