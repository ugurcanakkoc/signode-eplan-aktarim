"""Zaman damgalı iş kaydı (kullanıcı 08.10): her adımın başı/sonu CSV'ye; özet neye ne kadar gittiğini verir.

Kullanım:
  python -X utf8 tools/zaman.py <kayit.csv> basla <adim> [--kim ajan|asistan|kullanici] [--not "..."]
  python -X utf8 tools/zaman.py <kayit.csv> bitir <adim> [--not "..."]
  python -X utf8 tools/zaman.py <kayit.csv> ozet
"""
import argparse
import csv
import datetime as dt
from pathlib import Path

ALAN = ['zaman', 'olay', 'adim', 'kim', 'not']


def yaz(dosya, olay, adim, kim='', notu=''):
    dosya = Path(dosya)
    yeni = not dosya.exists()
    dosya.parent.mkdir(parents=True, exist_ok=True)
    with open(dosya, 'a', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, ALAN)
        if yeni:
            w.writeheader()
        w.writerow({'zaman': dt.datetime.now().isoformat(timespec='seconds'), 'olay': olay, 'adim': adim, 'kim': kim,
                    'not': notu})


def ozet(dosya):
    """Adım başına süre (dk); kapanmamış adım 'açık'. Dönüş: [(adim, kim, dk|None, basla)]."""
    acik, satir = {}, []
    for r in csv.DictReader(open(dosya, encoding='utf-8')):
        t = dt.datetime.fromisoformat(r['zaman'])
        if r['olay'] == 'basla':
            acik[r['adim']] = (t, r['kim'])
        elif r['olay'] == 'bitir' and r['adim'] in acik:
            t0, kim = acik.pop(r['adim'])
            satir.append((r['adim'], kim, round((t - t0).total_seconds() / 60, 1), t0))
    satir += [(a, k, None, t0) for a, (t0, k) in acik.items()]
    return sorted(satir, key=lambda s: s[3])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('kayit')
    ap.add_argument('olay', choices=['basla', 'bitir', 'ozet'])
    ap.add_argument('adim', nargs='?', default='')
    ap.add_argument('--kim', default='')
    ap.add_argument('--not', dest='notu', default='')
    a = ap.parse_args()
    if a.olay == 'ozet':
        toplam = {}
        for adim, kim, dk, t0 in ozet(a.kayit):
            print('%s  %-45s %-10s %s' % (t0.strftime('%d.%m %H:%M'), adim, kim, 'açık' if dk is None else '%.1f dk' % dk))
            if dk is not None:
                toplam[kim or '-'] = toplam.get(kim or '-', 0) + dk
        print('Toplam:', ', '.join('%s %.1f dk' % kv for kv in toplam.items()))
        return
    yaz(a.kayit, a.olay, a.adim, a.kim, a.notu)
    print(dt.datetime.now().strftime('%H:%M:%S'), a.olay, a.adim)


if __name__ == '__main__':
    main()
