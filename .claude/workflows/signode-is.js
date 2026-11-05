export const meta = {
  name: 'signode-is',
  description: 'Signode EPLAN yedeği: oku, ölç, test kopyasına yaz, doğrula, tek denetçi, rapor. Pin ve kesit kararını kullanıcı pin kuyruğunda verir.',
  whenToUse: 'Kullanıcı /signode-is ile klasör verdiğinde. EPLAN kapalı olmalı.',
  phases: [
    { title: 'Çalıştır', detail: 'hazırla, yaz, doğrula, rapor: Python akışı' },
    { title: 'Denetle', detail: 'bağımsız denetçi', model: 'opus' },
    { title: 'Bitir', detail: 'alarm' },
  ],
}

// Kullanıcı 05.11: pin ve kesit kararını AI vermez. AI yalnız yazım sonrası bağımsız denetim yapar.
const { is: IS, zw1, pdf, liste, edz, pano } = args
const parca = [['--zw1', zw1], ['--pano', pano || '+CC'], ['--liste', liste], ['--pdf', pdf], ['--edz', edz]].filter(([, v]) => v).map(([a, v]) => `${a} "${v}"`).join(' ')
const KOM = `python -X utf8 tools/yedek_is/calistir.py --is ${IS} ${parca}`
const ADIM = { type: 'object', properties: { cikis: { type: 'number' }, ozet: { type: 'string' }, kayit: { type: 'string' } }, required: ['cikis', 'ozet', 'kayit'] }

phase('Çalıştır')
const c = await agent(`Depo kökünde şu komutu aynen çalıştır, zaman aşımı 600000 ms. Başka iş yapma, kodu değiştirme.\n${KOM}\nBitince son satırları ve çıkış kodunu al. kayit: durum.json içindeki kayit alanı (output/ajan_kayit altında ${IS} klasörü).`, { label: 'akış', phase: 'Çalıştır', schema: ADIM, model: 'sonnet', effort: 'low' })
if (!c || c.cikis !== 0) {
  await agent('Şu komutu çalıştır, başka iş yapma: powershell -NoProfile -ExecutionPolicy Bypass -File tools/bip.ps1 -Count 6', { label: 'alarm', phase: 'Bitir', model: 'sonnet', effort: 'low' })
  return { is: IS, durum: 'DURDU', sonuc: c }
}

phase('Denetle')
const D = await agent(`Bağımsız denetçi. Proje ${IS}, kayıt ${c.kayit}. Amacın hata bulmak; önceki sonuçları doğru varsayma, kendi betiğini yaz. Kod veya proje değiştirme, EPLAN açma. Raporu ${c.kayit}\\denetim.md dosyasına yaz.
Veri: rb_taban ve rb_kalici_* geri okumaları, duzelt_*/plan.json ve rapor.json, PDF. Nesne kimliğinde son 3 parça eşlenir.
Bak: bağlantı uç çiftleri, plan dışı değişiklik, yeni nesne, kablo damarına ve klemense yazım, atlanan gerçek tek tel, 24V/0V/analog ayrımı, kod-standart uyumu. En az 6 PDF sayfasında 30 teli göz ile kontrol et.
Bulgu: nesne kimliği, tekrar üretim, etki, en küçük düzeltme. Kanıtlanmayanı risk diye ayır.`, {
  label: 'denetim', phase: 'Denetle', model: 'opus',
  schema: { type: 'object', properties: { gecti: { type: 'boolean' }, bulgular: { type: 'array', items: { type: 'string' } }, riskler: { type: 'array', items: { type: 'string' } } }, required: ['gecti', 'bulgular'] },
})

phase('Bitir')
await agent('Şu komutu çalıştır, başka iş yapma: powershell -NoProfile -ExecutionPolicy Bypass -File tools/bip.ps1 -Count 8', { label: 'alarm', phase: 'Bitir', model: 'sonnet', effort: 'low' })
return { is: IS, kayit: c.kayit, ozet: c.ozet, denetim: D }
