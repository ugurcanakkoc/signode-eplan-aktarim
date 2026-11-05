# "Baglantilari yeniden kur" EPLAN deneyi (09.10, gorev D). ASCII.
# Gece projesinin KOPYASINDA Blatt 7 (=280+E280&EFS1/7): -7K29'u API ile (0,-40) mm tasi -> UvpPdfToP8Rebuild -> olc.
#   1. Snapshot (once)   : kopyanin sayfa PNG'leri (gorsel karsilastirma tabani)
#   2. Rebuild /MOVE     : tasima + yeniden kurma + olcum (rebuild.json: plan_counts, check, applied, moved)
#   3. Rebuild (2. basis): ayni sayfa, tasimasiz -> beklenen: hepsi KEEP, 0 yazma (tekrar basmak bozmaz)
#   4. Snapshot (sonra)  : kirpim icin PNG
# KURALLAR: kullanici EPLAN'i aciksa DURUR. Kilit output\ajan_kayit\20261003_troester\EPLAN_KILIT (atomik New-Item,
# doluysa 60 sn bekle, 5 deneme), bitince silinir. run_action.ps1 HER ZAMAN -NoRegister: kurulu eklenti
# (%LOCALAPPDATA%\UVP\PdfToP8\installed_dll.txt) UvpPdfToP8Rebuild icermiyorsa DURUR (DLL kurmak bu betigin isi degil:
# kur.ps1 -Dll output\eplan_addin_bin\Uvp.PdfToP8.Host_rebuild.dll). Yalniz kopya yazilir: output\p8runs\rebuild_deney_<zaman>\.
#
# Kullanim: deney_rebuild.ps1 [-Move '-7K29,0,-40'] [-Page '=280+E280&EFS1/7'] [-Out <kayit klasoru>] [-NoSnapshot]
param(
  [string]$Move = '-7K29,0,-40',
  [string]$Page = '=280+E280&EFS1/7',
  [string]$Source = '',
  [string]$Out = '',
  [switch]$NoSnapshot
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = Get-Date -Format yyyyMMdd_HHmmss
if (-not $Source) { $Source = Join-Path $root 'output\p8runs\20261009_gece_e280\s5\UVP_PDF2P8_20261008_230158' }
if (-not $Out) { $Out = Join-Path $root "output\ajan_kayit\20261009_sabah\D_telleri_yeniden_kur\deney_$stamp" }
$work = Join-Path $root "output\p8runs\rebuild_deney_$stamp"
$lock = Join-Path $root 'output\ajan_kayit\20261003_troester\EPLAN_KILIT'
$runAction = Join-Path $PSScriptRoot 'run_action.ps1'

if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) { throw 'EPLAN acik (kullanici oturumu): DUR. EPLAN kapaliyken tekrar.' }
$dll = (Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\installed_dll.txt') -Raw).Trim()
$latin = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
$utf16 = ('UvpPdfToP8Rebuild'.ToCharArray() | ForEach-Object { [string]$_ + [char]0 }) -join ''
if (-not $latin.Contains($utf16)) {
  throw "Kurulu eklentide UvpPdfToP8Rebuild yok ($dll). Once kur.ps1 -Dll output\eplan_addin_bin\Uvp.PdfToP8.Host_rebuild.dll (orkestra karari); bitince eski DLL'e geri."
}
foreach ($ext in '.elk', '.uvp_ledger.json') {
  if (-not (Test-Path -LiteralPath "$Source$ext")) { throw "Kaynak proje eksik: $Source$ext" }
}

$locked = $false
for ($i = 0; $i -lt 5 -and -not $locked; $i++) {
  try { New-Item -ItemType File -Path $lock -ErrorAction Stop | Out-Null; $locked = $true }
  catch { Write-Output "Kilit dolu, 60 sn: $lock"; Start-Sleep -Seconds 60 }
}
if (-not $locked) { throw "Kilit alinamadi: $lock" }

try {
  New-Item -ItemType Directory -Force -Path $Out, $work | Out-Null
  $name = Split-Path $Source -Leaf
  Copy-Item -LiteralPath "$Source.elk" -Destination $work
  Copy-Item -LiteralPath "$Source.edb" -Destination $work -Recurse
  Copy-Item -LiteralPath "$Source.uvp_ledger.json" -Destination $work     # Python sayfanin makbuz/paketini defterden bulur
  $elk = Join-Path $work "$name.elk"
  Write-Output "Kopya: $elk"
  Write-Output "Kayit: $Out"
  $steps = @()
  if (-not $NoSnapshot) {
    $steps += @{ id = 'snap_once'; act = 'UvpPdfToP8Snapshot /PROJECT:"' + $elk + '" /OUT:"' + (Join-Path $Out 'snap_once') + '"'; res = (Join-Path $Out 'snap_once\snapshot.json') }
  }
  # Sayfa adindaki '/' komut satirinda ters bolu olarak verilir (eylem geri cevirir; '/' degerde ayirici sayilabilir).
  $pageArg = $Page.Replace('/', '\')
  $steps += @{ id = 'rebuild_move'; act = 'UvpPdfToP8Rebuild /PROJECT:"' + $elk + '" /PAGE:"' + $pageArg + '" /OUT:"' + (Join-Path $Out 'rebuild_move') + '" /MOVE:"' + $Move + '"'; res = (Join-Path $Out 'rebuild_move\rebuild.json') }
  $steps += @{ id = 'rebuild_again'; act = 'UvpPdfToP8Rebuild /PROJECT:"' + $elk + '" /PAGE:"' + $pageArg + '" /OUT:"' + (Join-Path $Out 'rebuild_again') + '"'; res = (Join-Path $Out 'rebuild_again\rebuild.json') }
  if (-not $NoSnapshot) {
    $steps += @{ id = 'snap_sonra'; act = 'UvpPdfToP8Snapshot /PROJECT:"' + $elk + '" /OUT:"' + (Join-Path $Out 'snap_sonra') + '"'; res = (Join-Path $Out 'snap_sonra\snapshot.json') }
  }
  foreach ($s in $steps) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    try { & $runAction -Dll $dll -Action $s.act -Result $s.res -TimeoutSec 900 -NoRegister } catch { Write-Output "$($s.id): $_" }
    Write-Output ("{0}: {1} sn" -f $s.id, [int]$t.Elapsed.TotalSeconds)
  }
  # Ozet + kirpim (Python: ayni olcu dosyalarindan; uydurma yok, dosya yoksa 'YOK' yazar).
  $py = (Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\uvp_pdf2p8.json') -Raw -Encoding UTF8 | ConvertFrom-Json).python
  $code = @'
import json, sys
from pathlib import Path
out, page = Path(sys.argv[1]), sys.argv[2]
lines = []
def load(p):
    p = out / p
    return json.loads(p.read_text(encoding='utf-8')) if p.exists() else None
for step in ('rebuild_move', 'rebuild_again'):
    r = load(step + '/rebuild.json')
    if r is None:
        lines.append('%s: YOK (rebuild.json yazilmadi)' % step); continue
    c, k = r.get('check') or {}, r.get('plan_counts') or {}
    pairs = c.get('pairs') or {}
    lines.append('%s: ok=%s undone=%s refused=%s error=%s' % (step, r.get('ok'), r.get('undone'), r.get('refused'), r.get('error')))
    lines.append('  plan: %s (python %s sn)' % (json.dumps(k, ensure_ascii=False), r.get('plan_seconds')))
    lines.append('  uygulanan: %s' % json.dumps({x: y for x, y in (r.get('applied') or {}).items() if x != 'marks'}, ensure_ascii=False))
    lines.append('  bag %s/%s birlesik; yanlis birlesme %s (yeni %s); beklenmeyen uc %s; kose %s; T %s; Connection %s'
                 % (c.get('links_connected'), c.get('links'), len(c.get('wrong_joins') or []), len(c.get('wrong_joins_new') or []),
                    len(c.get('unexpected_pins') or []), c.get('corners_on_page'), c.get('tees_on_page'), c.get('connections_on_page')))
    lines.append('  uc ciftleri referans %s / sonra %s; kayip %s; yeni %s' % (pairs.get('reference'), pairs.get('after'),
                 pairs.get('lost'), pairs.get('gained')))
    for row in c.get('not_connected') or []:
        lines.append('  BIRLESMEDI: %s -> %s (%s)' % (row['a'], row['b'], row['status']))
    for m in r.get('moved') or []:
        lines.append('  tasindi: %s %s -> %s' % (m['name'], m['from'], m['at']))
safe = ''.join('_' if ch in '\\/:*?"<>|' else ch for ch in page)
try:
    from PIL import Image
    for snap in ('snap_once', 'snap_sonra'):
        png = out / snap / (safe + '.png')
        if not png.exists():
            lines.append('%s: PNG YOK (%s)' % (snap, png.name)); continue
        im = Image.open(png)
        sx, sy = im.width / 420.0, im.height / 297.0          # A3 yatay, mm -> px; y asagi
        box = (int(300 * sx), int((297 - 300) * sy), int(390 * sx), int((297 - 110) * sy))
        im.crop((max(box[0], 0), max(box[1], 0), min(box[2], im.width), min(box[3], im.height))).save(out / ('kirpim_%s.png' % snap))
        lines.append('%s: kirpim_%s.png (x 300-390, y 110-300 mm)' % (snap, snap))
except Exception as ex:
    lines.append('kirpim yapilamadi: %s' % ex)
(out / 'ozet.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')
print('\n'.join(lines))
'@
  $tmp = Join-Path $Out 'ozet_yaz.py'
  [IO.File]::WriteAllText($tmp, $code, (New-Object Text.UTF8Encoding $false))
  & $py -X utf8 $tmp $Out $Page
}
finally {
  Remove-Item -LiteralPath $lock -ErrorAction SilentlyContinue
  Write-Output "Kilit silindi: $lock"
}
