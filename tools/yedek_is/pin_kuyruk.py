"""Tek ekranda bekleyen işler: tüm Signode projelerinden eşleşmeyen pin, ürün kodu ve kesit. Yerel sunucu, dış erişim yok.

Başlat:   python -X utf8 tools/yedek_is/pin_kuyruk.py        Aç: http://127.0.0.1:8766
Kart kaydedilince karar hemen kalıcı olur ve kart kuyruktan düşer. Yeni proje okunduğunda pinleri kendiliğinden gelir.
Pin ve ürün kodu kararı ogrenilen.json dosyasına girer: sonraki işlerde sorulmaz. Kesit kararı yalnız o projede geçerlidir.
Adı boş bağlı uç eşleştirilmez, "Tanımsız bağlantı" listesine düşer. Sonra EPLAN'a yazmak için: calistir.py --is <ad> --adim uygula, dogrula, rapor.
"""
import argparse
import collections
import datetime as dt
import json
import re
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import calistir as K  # noqa: E402
import cekirdek as C  # noqa: E402

PORT = 8766
MUSTERI = K.KOK / 'output' / 'musteri' / 'signode'
BILINMEYEN = MUSTERI / 'pin_bilinmeyen.json'


def makro_uclari(parcalar, ms):
    """Ürün başına makro uçları, yer tutucu dahil. Dönüş: [(uç, ürün)]."""
    sat, gor = [], set()
    for u in parcalar:
        p = ms.get(u) or {}
        adlar = list(C.gercek_uclar(p)) + [q for d in p.get('device_functions') or [] for q in d.get('pins') or []]
        for a in sorted(set(adlar), key=str):
            if a and a.strip('.') and (a, u) not in gor:
                gor.add((a, u))
                sat.append((a, u))
    return sat


def oneri(uc, hedefler):
    """Şema adı tek makro ucuna uyuyorsa öneri: 1 -> 1/L1, T1_1 -> T1. Birden çok aday varsa öneri yok."""
    if re.fullmatch(r'\d+', uc or ''):
        ad = [h for h in hedefler if h.startswith(uc + '/')]
        return ad[0] if len(ad) == 1 else ''
    kok = re.sub(r'_\d+$', '', uc or '')
    return kok if kok != uc and kok in hedefler else ''


def oneriler(uclar, hedefler, dolu=()):
    """Yalnız boş makro ucu önerilir, bir uç iki kez önerilmez. Kesin değildir, kullanıcı değiştirir."""
    bos = [h for h in hedefler if h not in set(dolu)]
    sonuc = {}
    for u in uclar:
        h = oneri(u, bos)
        if h:
            sonuc[u] = h
            bos.remove(h)
    if set(uclar) - set(sonuc) == {'1', '2'} and {'L+', 'M'} <= set(bos):
        sonuc.update({'1': 'L+', '2': 'M'})
    return sonuc


def kayitli_esleme(urunler, uc, ogr):
    for k in ogr.get('uc', []):
        if not set(C.urun_listesi(k['urun'])) <= set(urunler):
            continue
        if k.get('karar') == 'ESLE' and uc in (k.get('esleme') or {}):
            return 'ESLE', k['esleme'][uc]
        if k.get('karar') == 'MUSTERI_KALSIN' and uc in (k.get('uclar') or []):
            return 'MUSTERI_KALSIN', uc
    return None


def kayda_cevir(kararlar, ogr, proje, tarih):
    """Pin kararları -> ogrenilen.json. Anahtar: hedef ürün + müşteri pini, başka takımda da geçerli olsun. Yeni karar eskisini ezer."""
    uc = [dict(k, esleme=dict(k.get('esleme') or {}), uclar=list(k.get('uclar') or [])) for k in ogr.get('uc', [])]
    es = collections.defaultdict(dict)
    for s in kararlar:
        if s.get('karar') == 'ESLE' and s.get('hedef') and s.get('uc'):
            es[C.urun_anahtar([s['hedef_urun']] if s.get('hedef_urun') else s['urun'])][s['uc']] = s['hedef']
    for k in uc:
        for u in es.get(C.urun_anahtar(C.urun_listesi(k['urun'])), ()):
            k['esleme'].pop(u, None)
    uc = [k for k in uc if k['esleme'] or k['uclar']]
    kaynak = '%s kullanıcı kararı %s' % (proje, tarih)
    for a, e in sorted(es.items()):
        uc.append(dict(urun=a.split(' + '), karar='ESLE', esleme=e, uclar=[], kaynak=kaynak, tarih=tarih))
    return dict(ogr, uc=uc)


def bos_uclar(r, cih):
    F = {f['object_id']: f for f in C.fonksiyonlar(r)}
    bos = collections.defaultdict(list)
    for c in r['connections']:
        for a, b in ((c['start'], c['end']), (c['end'], c['start'])):
            k = C.dv(a.get('function'))
            f = F.get(a.get('object_id')) or {}
            if k in cih and cih[k]['parcalar'] and a.get('kind') == 'PIN' and not a.get('pin') and C.dv(b.get('function')) != k \
                    and f.get('class_name') != 'Terminal' and f.get('category') != 'Busbar':
                bos[k].append(dict(karsi='%s %s' % (b.get('function') or 'Potansiyel', b.get('pin') or ''), sayfa=(a.get('page') or '').replace('&EFS1', '')))
    return bos


def ad_bul(kod, ms, liste):
    la = C.liste_adaylari(kod, liste)
    return (la[0].get('Açıklama') if la else '') or C.urun_aciklama(ms.get(kod)) or kod


def is_verisi(i, ogr):
    """Bir projenin bekleyenleri."""
    i._v = i._v or {}
    v = i.veri()
    v['ogr'] = ogr
    p = i.plan()
    r = C.sanal_uygula(v['r'], dict(parts=p['parts']))
    cih = C.cihaz_karsilastir(r, i.kapsam, v['pr'], v['ms'])
    bos = bos_uclar(r, cih)
    pinler, tanimsiz, makro_yok = [], [], []
    for k, c in sorted(cih.items()):
        if not c['parcalar']:
            continue
        for b in bos.get(k, []):
            tanimsiz.append(dict(cihaz=k, urun=c['parcalar'], karsi=b['karsi'], sayfa=b['sayfa'], **{'is': i.ad}))
        if c['fark']:
            hedef = makro_uclari(c['parcalar'], v['ms'])
            if not hedef:
                makro_yok.append(dict(cihaz=k, urun=c['parcalar'], uclar=c['fark'], **{'is': i.ad}))
            pinler.append(dict(cihaz=k, parcalar=c['parcalar'], fark=[u for u in c['fark'] if not kayitli_esleme(c['parcalar'], u, ogr)], telli=c['telli'], hedef=hedef,
                               aciklama=[ad_bul(u, v['ms'], v['liste']) for u in c['parcalar']], **{'is': i.ad}))
    kodlar = C.pano_kodlari(v['r'], i.kapsam)
    urun = [dict(p8_kod=kod, cihazlar=sorted({C.dv(f['name']) for f in kodlar[kod]}), aday=sorted(set(p['kod'][kod].get('db') or [])),
                 liste=[s.get('Kod', '') for s in p['kod'][kod].get('liste') or []], **{'is': i.ad}) for kod in p['bekleyen']['urun_kodu']]
    tel = [dict(bag=t['connection_id'], uclar=['%s %s' % (t['ce'][0] or 'Potansiyel', t['ce'][1] or ''), '%s %s' % (t['ce'][2] or 'Potansiyel', t['ce'][3] or '')],
                sayfa=(t.get('sayfa') or '').replace('&EFS1', ''), devre=t.get('aday_devre') or '', renk=t.get('aday_renk') or t.get('renk') or '', kesit=t.get('aday_kesit') or '', **{'is': i.ad})
           for t in p['bekleyen']['tel']]
    return pinler, urun, tel, tanimsiz, makro_yok


DEVRE_AD = dict(PE='Toprak', HARICI='Harici gerilim', DC_ARTI='24V artı', DC_EKSI='0V', BELIRSIZ='Devre belirsiz', AC='AC')


def veri(isler):
    ogr = C.json_oku(MUSTERI / 'ogrenilen.json') if (MUSTERI / 'ogrenilen.json').exists() else {}
    bilinmeyen = set(C.json_oku(BILINMEYEN)) if BILINMEYEN.exists() else set()
    gr, urun, tel, tanimsiz, makro_yok = {}, [], [], [], []
    hata = []
    for i in isler:
        try:
            pin, u, t, ta, my = is_verisi(i, ogr)
        except Exception as e:  # noqa: BLE001  bir projenin hatası kuyruğu durdurmaz
            hata.append('%s: %s' % (i.ad, e))
            continue
        urun += u
        tel += t
        tanimsiz += ta
        makro_yok += my
        for x in pin:
            g = gr.setdefault(C.urun_anahtar(x['parcalar']), dict(parcalar=x['parcalar'], aciklama=x['aciklama'], hedef=x['hedef'], cihazlar=[], fark=set(), telli=set()))
            g['cihazlar'].append('%s %s' % (x['is'], x['cihaz']))
            g['fark'] |= set(x['fark'])
            g['telli'] |= set(x['telli'])
    kart = []
    for a, g in gr.items():
        if not g['fark']:
            continue
        sira = sorted(g['fark'], key=lambda s: (len(s), s))
        adlar = sorted(h[0] for h in g['hedef'])
        dolu = sorted((g['telli'] - g['fark']) & set(adlar))
        onr = oneriler(sira, adlar, dolu)
        kart.append(dict(anahtar=a, parcalar=g['parcalar'], aciklama=g['aciklama'], cihazlar=sorted(g['cihazlar']), ayni=sorted(g['telli'] - g['fark'], key=lambda s: (len(s), s)),
                         hedef=[dict(uc=h[0], urun=h[1], dolu=h[0] in dolu) for h in g['hedef']],
                         uclar=[dict(uc=u, oneri=onr.get(u, ''), bilinmiyor='%s|%s' % (a, u) in bilinmeyen) for u in sira]))
    kart.sort(key=lambda k: (all(u['bilinmiyor'] for u in k['uclar']), -len(k['cihazlar']), k['anahtar']))
    kg = collections.defaultdict(list)
    for t in tel:
        kg[(t['devre'], t['renk'], t['kesit'])].append(t)
    kesit = [dict(devre=DEVRE_AD.get(d, d or 'Devre belirsiz'), renk=r, kesit=k, teller=v) for (d, r, k), v in sorted(kg.items(), key=lambda x: -len(x[1]))]
    return dict(pin=kart, urun=urun, kesit=kesit, tanimsiz=tanimsiz, makro_yok=makro_yok, isler=[i.ad for i in isler], hata=hata)


def isler_bul():
    sonuc = []
    for d in sorted((K.KOK / 'output' / 'ajan_kayit').glob('*/is.json')):
        if (d.parent / 'rb' / 'readback_current.json').exists() and (d.parent / 'rb' / 'baglanti_cdp.json').exists():
            ad = C.json_oku(d).get('ad')
            if ad:
                sonuc.append(K.Is(ad, d.parent))
    return sonuc


def karar_yaz(j, isler):
    """Kart kararını diske yazar. Dönüş: mesaj."""
    tarih = dt.date.today().isoformat()
    if j['tur'] == 'pin':
        o = C.json_oku(MUSTERI / 'ogrenilen.json')
        K.yaz_json(MUSTERI / 'ogrenilen.json', kayda_cevir(j['kararlar'], o, 'Pin kuyruğu', tarih))
        b = set(C.json_oku(BILINMEYEN)) if BILINMEYEN.exists() else set()
        for s in j['kararlar']:
            a = '%s|%s' % (j['anahtar'], s['uc'])
            (b.add if s['karar'] == 'BILMIYORUM' else b.discard)(a)
        K.yaz_json(BILINMEYEN, sorted(b))
    elif j['tur'] == 'urun':
        o = C.json_oku(MUSTERI / 'ogrenilen.json')
        o['urun_kodu'] = [x for x in o.get('urun_kodu', []) if x['p8_kod'] != j['p8_kod']] + [dict(p8_kod=j['p8_kod'], uvp_kod=j['uvp_kod'], kaynak='Kullanıcı kararı', tarih=tarih)]
        K.yaz_json(MUSTERI / 'ogrenilen.json', o)
    elif j['tur'] == 'kesit':
        gr = collections.defaultdict(list)
        for t in j['teller']:
            gr[t['is']].append(t['bag'])
        for i in isler:
            if i.ad in gr:
                k = i._oku('kullanici_kabul.json') or dict(urun_kodu={}, uc=[], tel={}, red=[], belirsiz=[])
                for b in gr[i.ad]:
                    k['tel'][C.kimlik_sonu(b)] = dict(bag=b, kesit=j['kesit'], renk=j['renk'], karar='YAZ', kanit='çizimci kararı', kaynak='KULLANICI')
                K.yaz_json(i.yol('kullanici_kabul.json'), k)
    else:
        raise ValueError('Bilinmeyen tür')


class Sunucu(BaseHTTPRequestHandler):
    isler = []

    def _yanit(self, kod, govde, tip='application/json; charset=utf-8'):
        b = govde.encode('utf-8')
        self.send_response(kod)
        self.send_header('Content-Type', tip)
        self.send_header('Content-Length', str(len(b)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(b)

    def _yerel(self):
        # Başka site yerel sunucuya yazamasın.
        return self.headers.get('Host', '') in ('127.0.0.1:%d' % PORT, 'localhost:%d' % PORT) and self.headers.get('Origin', 'http://%s' % self.headers.get('Host')) == 'http://%s' % self.headers.get('Host')

    def do_GET(self):  # noqa: N802
        if not self._yerel():
            return self._yanit(403, '{}')
        if self.path == '/veri':
            return self._yanit(200, json.dumps(veri(isler_bul()), ensure_ascii=False))
        if self.path == '/':
            return self._yanit(200, SAYFA, 'text/html; charset=utf-8')
        return self._yanit(404, '{}')

    def do_POST(self):  # noqa: N802
        if not self._yerel() or self.path != '/karar':
            return self._yanit(403, '{}')
        j = json.loads(self.rfile.read(int(self.headers.get('Content-Length', 0))).decode('utf-8'))
        karar_yaz(j, isler_bul())
        return self._yanit(200, '{"ok":true}')

    def log_message(self, *a):
        pass


SAYFA = r'''<!doctype html><html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Signode bekleyenler</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#eef0f2;color:#17212b;font:16px/1.4 "Segoe UI",system-ui,sans-serif}
header{position:sticky;top:0;z-index:5;background:#17324d;color:#fff}.ust{max-width:1000px;margin:auto;padding:10px 18px;display:flex;gap:18px;align-items:center;flex-wrap:wrap}
h1{font-size:18px;margin:0;font-weight:600}header a{color:#fff;text-decoration:none;border-bottom:2px solid transparent;padding:2px 0}header a:hover{border-color:#fff}#sag{margin-left:auto;font-size:14px;color:#c9d6e2}
main{max-width:1000px;margin:auto;padding:14px 18px 60px}section>h2{font-size:17px;margin:20px 0 8px}
.kart{background:#fff;border-left:6px solid #e07b00;margin-bottom:12px;padding:12px 18px}.kart.bil{border-left-color:#9aa5af}
.kod{font:700 17px Consolas,monospace}.alt{color:#52606d;font-size:14px;margin:2px 0 10px}
.iki{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin-bottom:10px}.bas{font-size:13px;color:#52606d;font-weight:600;margin-bottom:3px}
.pin{display:inline-block;font:600 15px Consolas,monospace;padding:2px 7px;margin:0 4px 4px 0;background:#e6edf3;color:#17324d}.pin.soru{background:#fbe4cd;color:#7a3e00}.pin.dolu{background:#f2f3f4;color:#9aa5af;font-weight:400}
.ur{font:13px Consolas,monospace;color:#52606d;margin:4px 0 2px}table{border-collapse:collapse}td{padding:3px 10px 3px 0;vertical-align:middle}td.m{font:700 18px Consolas,monospace;min-width:70px}
select,input[type=text]{font:600 15px Consolas,monospace;padding:6px 8px;min-width:190px;border:1px solid #9aa5af;background:#fff}select.bil{background:#fbe4cd}
button{font:inherit;font-weight:600;padding:7px 18px;background:#0f4c81;color:#fff;border:0;cursor:pointer;margin-top:8px}.onr{color:#1b6e3c;font-size:13px;font-weight:600}
.goster{font-size:13.5px;color:#52606d;margin-left:14px;cursor:pointer}details{font-size:13.5px;color:#52606d;margin-top:6px}details div{font-family:Consolas,monospace;font-size:13px}
.tablo{background:#fff;width:100%}.tablo td,.tablo th{padding:6px 10px;border-bottom:1px solid #e1e5e9;text-align:left;font-size:14.5px}.tablo th{background:#f0f3f5}.tablo td{font-family:Consolas,monospace;font-size:13.5px}.yok{color:#52606d;background:#fff;padding:10px 18px}
@media(max-width:700px){.iki{grid-template-columns:1fr}}
</style></head><body><header><div class="ust"><h1>Signode bekleyenler</h1><a href="#pin" id="npin"></a><a href="#urun" id="nurun"></a><a href="#kesit" id="nkesit"></a><a href="#tanimsiz" id="ntan"></a><span id="sag"></span></div></header>
<main><section id="pin"><h2>Eşleşmeyen pinler</h2><div id="lpin"></div></section><section id="urun"><h2>Ürün kodu</h2><div id="lurun"></div></section><section id="kesit"><h2>Kesit</h2><div id="lkesit"></div></section><section id="tanimsiz"><h2>Tanımsız bağlantılı ürünler</h2><div id="ltan"></div></section></main>
<script>
const el=(t,c,x)=>{const e=document.createElement(t);if(c)e.className=c;if(x!==undefined)e.textContent=x;return e};const kisa=s=>s.split('.').slice(1).join('.')||s;
let kirli=false,son='';const yolla=j=>fetch('/karar',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(j)}).then(r=>{if(!r.ok)throw 0;kirli=false;yukle(true)}).catch(()=>alert('Kaydedilemedi. Sunucu açık mı?'));
function pinKart(k){const a=el('div','kart'+(k.uclar.every(u=>u.bilinmiyor)?' bil':''));a.append(el('div','kod',k.parcalar.map(kisa).join('  +  ')),el('div','alt',k.cihazlar.join('  ·  ')));
const iki=el('div','iki'),s=el('div'),g=el('div');s.append(el('div','bas','Müşteri pinleri (bağlı)'));k.ayni.forEach(p=>s.append(el('span','pin dolu',p)));k.uclar.forEach(u=>s.append(el('span','pin soru',u.uc)));
g.append(el('div','bas','Bizim makro pinleri'));const ur={};k.hedef.forEach(h=>(ur[h.urun]=ur[h.urun]||[]).push(h));Object.keys(ur).forEach(u=>{if(Object.keys(ur).length>1)g.append(el('div','ur',kisa(u)));ur[u].forEach(h=>g.append(el('span','pin'+(h.dolu?' dolu':''),h.uc)))});if(!k.hedef.length)g.append(el('span','onr','Makroda pin yok'));iki.append(s,g);a.append(iki);
const t=el('table'),S=[];let doluGoster=false;const deger=h=>h.uc+'|'+h.urun;const sec={};k.uclar.forEach(u=>{if(u.bilinmiyor)sec[u.uc]='B';else if(u.oneri){const al=new Set(Object.values(sec)),h=k.hedef.find(x=>x.uc===u.oneri&&!x.dolu&&!al.has(deger(x)));if(h)sec[u.uc]=deger(h)}});
const doldur=()=>S.forEach(({u,q,o})=>{const d=sec[u.uc]||'',dig=new Set(Object.entries(sec).filter(([x])=>x!==u.uc).map(x=>x[1]));q.textContent='';q.append(Object.assign(el('option','','seç'),{value:''}));k.hedef.forEach(h=>{const v=deger(h);if(dig.has(v)||(h.dolu&&!doluGoster&&v!==d))return;q.append(Object.assign(el('option','',h.uc+(Object.keys(ur).length>1?'  · '+kisa(h.urun):'')+(h.dolu?'  (dolu)':'')),{value:v}))});q.append(Object.assign(el('option','','Bilmiyorum'),{value:'B'}));q.value=d;q.classList.toggle('bil',d==='B');o.textContent=u.oneri&&d.startsWith(u.oneri+'|')?'önerim':''});
k.uclar.forEach(u=>{const r=el('tr'),q=el('select'),o=el('td','onr');q.onchange=()=>{sec[u.uc]=q.value;kirli=true;doldur()};const c=el('td');c.append(q);r.append(el('td','m',u.uc),el('td','','→'),c,o);t.append(r);S.push({u,q,o})});a.append(t);doldur();
const b=el('button','','Kaydet');b.onclick=()=>{const kr=k.uclar.filter(u=>sec[u.uc]).map(u=>{const v=sec[u.uc];return v==='B'?{uc:u.uc,karar:'BILMIYORUM',urun:k.parcalar}:{uc:u.uc,karar:'ESLE',hedef:v.split('|')[0],hedef_urun:v.split('|')[1],urun:k.parcalar}});if(kr.length)yolla({tur:'pin',anahtar:k.anahtar,kararlar:kr})};a.append(b);
if(k.hedef.some(h=>h.dolu)){const l=el('label','goster'),c=el('input');c.type='checkbox';c.onchange=()=>{doluGoster=c.checked;doldur()};l.append(c,document.createTextNode(' Dolu pinleri de göster'));a.append(l)}return a}
function urunKart(u){const a=el('div','kart');a.append(el('div','kod',u.p8_kod),el('div','alt',u.is+'  ·  '+u.cihazlar.join('  ')));const i=el('input');i.type='text';i.placeholder='UVP ürün kodu';i.setAttribute('list','a'+u.p8_kod);const d=el('datalist');d.id='a'+u.p8_kod;u.aday.concat(u.liste).forEach(x=>d.append(Object.assign(el('option'),{value:x})));i.oninput=()=>kirli=true;const b=el('button','','Kaydet');b.style.marginLeft='8px';b.onclick=()=>{if(i.value.trim())yolla({tur:'urun',p8_kod:u.p8_kod,uvp_kod:i.value.trim()})};a.append(i,d,b);return a}
function kesitKart(g){const a=el('div','kart');a.append(el('div','kod',g.devre+'  ·  '+g.teller.length+' tel'));const rn=el('select'),ks=el('select');['','DBU','DBUWH','BK','OG','GNYE','RD','WH'].forEach(x=>rn.append(Object.assign(el('option','',x||'renk'),{value:x})));rn.value=g.renk;['','0,5','0,75','1','1,5','2,5','4','6','10','16'].forEach(x=>ks.append(Object.assign(el('option','',x||'kesit'),{value:x})));ks.value=g.kesit;rn.onchange=ks.onchange=()=>kirli=true;const b=el('button','','Kaydet');b.style.marginLeft='8px';b.onclick=()=>{if(rn.value&&ks.value)yolla({tur:'kesit',renk:rn.value,kesit:ks.value,teller:g.teller})};
const de=el('details');de.append(el('summary','','Telleri göster'));g.teller.forEach(t=>de.append(el('div','',t.is+'  '+t.sayfa+'  '+t.uclar.join('  ↔  '))));a.append(rn,document.createTextNode(' '),ks,b,de);return a}
function ciz(d){const P=document.getElementById('lpin'),U=document.getElementById('lurun'),K=document.getElementById('lkesit'),T=document.getElementById('ltan');[P,U,K,T].forEach(x=>x.textContent='');const yok=(e,x)=>e.append(el('div','yok',x));
d.pin.length?d.pin.forEach(k=>P.append(pinKart(k))):yok(P,'Bekleyen pin yok');d.urun.length?d.urun.forEach(k=>U.append(urunKart(k))):yok(U,'Bekleyen ürün kodu yok');d.kesit.length?d.kesit.forEach(k=>K.append(kesitKart(k))):yok(K,'Bekleyen kesit yok');
if(d.tanimsiz.length){const t=el('table','tablo'),h=el('tr');['Proje','Etiket','Ürün','Bağlı olduğu yer','Sayfa'].forEach(x=>h.append(el('th','',x)));t.append(h);d.tanimsiz.forEach(x=>{const r=el('tr');[x.is,x.cihaz,x.urun.map(kisa).join(' + '),x.karsi,x.sayfa].forEach(y=>r.append(el('td','',y)));t.append(r)});T.append(t)}else yok(T,'Yok');
document.getElementById('npin').textContent='Pin '+d.pin.reduce((n,k)=>n+k.uclar.length,0);document.getElementById('nurun').textContent='Ürün kodu '+d.urun.length;document.getElementById('nkesit').textContent='Kesit '+d.kesit.reduce((n,k)=>n+k.teller.length,0);document.getElementById('ntan').textContent='Tanımsız '+d.tanimsiz.length;document.getElementById('sag').textContent=d.isler.join(' · ')+(d.hata.length?'  HATA: '+d.hata.join('; '):'')}
function yukle(z){if(kirli&&!z)return;fetch('/veri').then(r=>r.text()).then(t=>{if(t!==son&&!(kirli&&!z)){son=t;ciz(JSON.parse(t))}}).catch(()=>{document.getElementById('sag').textContent='Sunucu kapalı'})}
yukle(true);setInterval(yukle,20000);
</script></body></html>'''


if __name__ == '__main__':
    a = argparse.ArgumentParser(description=__doc__)
    a.add_argument('--al', help='Eski pin sayfasından inen JSON kararını kaydeder.')
    a = a.parse_args()
    if a.al:
        j = C.json_oku(a.al)
        K.yaz_json(MUSTERI / 'ogrenilen.json', kayda_cevir(j['kararlar'], C.json_oku(MUSTERI / 'ogrenilen.json'), j.get('proje', ''), dt.date.today().isoformat()))
        print(dict(collections.Counter(s['karar'] for s in j['kararlar'])))
    else:
        print('http://127.0.0.1:%d' % PORT, flush=True)
        HTTPServer(('127.0.0.1', PORT), Sunucu).serve_forever()
