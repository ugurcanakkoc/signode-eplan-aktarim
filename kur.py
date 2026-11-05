"""Kurulum: eksikleri söyler, profili yerleştirir, eklentiyi derler.

python -X utf8 kur.py                  kurulum
python -X utf8 kur.py --paylas         yerel pin kararlarını ornek/ içine kopyalar
python -X utf8 kur.py --yeni-kararlar  ornek/ogrenilen.json dosyasını yerele alır, eski dosyayı yedekler
"""
import argparse
import glob
import shutil
import subprocess
import sys
from datetime import datetime
from pathlib import Path

KOK = Path(__file__).resolve().parent
YEREL = KOK / 'output' / 'musteri' / 'signode'
KONTROL = [
    ('EPLAN 2026', r'C:\Program Files\EPLAN\Platform\2026*\Bin\EPLAN.exe'),
    ('Parça veritabanı', r'C:\Users\Public\EPLAN\Data\*\Company name\EPLAN_parts.adb\Article.eod'),
    ('Şablon UVP_013.zw9', r'C:\Users\Public\Eplan\Data\*\Company name\UVP_013.zw9'),
    ('Makro klasörü', r'C:\Users\Public\EPLAN\Data\Makrolar\Company name'),
    ('UVP standart Excel', str(KOK / 'arsiv' / 'standartlar' / 'UVP Standartları  V1.xlsx')),
]


def kur():
    eksik = 0
    for ad, yol in KONTROL:
        var = bool(glob.glob(yol))
        print(('TAMAM  ' if var else 'EKSİK  ') + ad + ('' if var else ': ' + yol))
        eksik += not var
    YEREL.mkdir(parents=True, exist_ok=True)
    for kaynak, hedef in (('profil_signode.json', 'profil.json'), ('ogrenilen.json', 'ogrenilen.json')):
        if not (YEREL / hedef).exists():
            shutil.copy(KOK / 'ornek' / kaynak, YEREL / hedef)
            print('Kopyalandı: ' + hedef)
    r = subprocess.run(['cmd', '/c', str(KOK / 'eplan_addin' / 'build' / 'build.bat'), str(KOK / 'output' / 'eplan_addin_bin'), 'Uvp.PdfToP8.Host_signode'],
                       capture_output=True, text=True, errors='replace')
    print('Eklenti derlendi.' if r.returncode == 0 else 'DERLEME KALDI:\n' + r.stdout[-800:])
    eksik += r.returncode != 0
    print('Kurulum tamam.' if not eksik else 'Eksik: %d. Tamamlayıp tekrar çalıştır.' % eksik)
    return 1 if eksik else 0


def main():
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    a.add_argument('--paylas', action='store_true')
    a.add_argument('--yeni-kararlar', action='store_true')
    a = a.parse_args()
    if a.paylas:
        shutil.copy(YEREL / 'ogrenilen.json', KOK / 'ornek' / 'ogrenilen.json')
        print('ornek/ogrenilen.json güncellendi. Dosyayı commit et.')
        return 0
    if a.yeni_kararlar:
        if (YEREL / 'ogrenilen.json').exists():
            shutil.copy(YEREL / 'ogrenilen.json', YEREL / ('ogrenilen_%s.json' % datetime.now().strftime('%Y%m%d_%H%M')))
        shutil.copy(KOK / 'ornek' / 'ogrenilen.json', YEREL / 'ogrenilen.json')
        print('Yerel kararlar güncellendi. Eski dosya yedeklendi.')
        return 0
    return kur()


if __name__ == '__main__':
    sys.exit(main())
