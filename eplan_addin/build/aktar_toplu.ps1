# Toplu aktarim: TEK gizli EPLAN baslatmasinda birden cok sayfa paketi (eylem UvpPdfToP8ImportBatch).
# Sayfa basina soguk baslatma (~30-50 sn) yerine bir kez (olcum 02.10). ASCII.
#
# Klasor duzeni (tam12/aktar2.ps1 ile ayni): <Run>\<sN>\package.json, <Run>\mapping.json; makbuz <Run>\<sN>\receipt.json.
# Toplu sonuc, liste, geri okuma (guncel\) ve PDF (pdf\) yeni <Run>\toplu_<zaman>\ klasorune yazilir.
# Eklenti ImportBatch iceren DLL ile kalici kurulu olmali (kur.ps1) -> -NoRegister. Kurulu degilse -Register (+2 baslatma).
#
# Kullanim: aktar_toplu.ps1 -Run <kosu> -Folders s4,s5 [-Target <output\p8runs\...\UVP_*.elk>] -Dll <dll>
#                           [-Template <zw9>] [-NoReadback] [-NoPdf] [-Register] [-AckRisk]
# Onay kapisi (MAIN 03.10): kisa devre / yanlis bag riskli kirmizi bayrakli sayfa (CUSTOMER_SHORT_CIRCUIT, SHORT_CIRCUIT,
# POTENTIAL_NAMES_DISAGREE, AUTOCONNECT_RISK, SAME_TERMINAL_TWICE) VARSAYILAN olarak YAZILMAZ (REFUSED_RISK_NOT_ACKNOWLEDGED).
# -AckRisk yalniz test projelerinde, bayragi bilerek yazmak icin verilir.
param(
  [Parameter(Mandatory)] [string]$Run,
  [Parameter(Mandatory)] [string[]]$Folders,
  [string]$Target = '',
  [Parameter(Mandatory)] [string]$Dll,
  [string]$Template = 'C:\ProgramData\EPLAN\O_Data\Electric P8 Data\2026.0.3\Templates\EPLAN\IEC_bas001.zw9',
  [string]$OnFailure = 'KEEP_PARTIAL',
  [switch]$NoReadback,
  [switch]$NoPdf,
  [switch]$Register,
  [switch]$AckRisk,
  [int]$SecPerPage = 120
)
$ErrorActionPreference = 'Stop'
$Run = (Resolve-Path -LiteralPath $Run).Path
if ($Target) { $Target = (Resolve-Path -LiteralPath $Target).Path }
if (-not (Test-Path -LiteralPath "$Run\mapping.json")) { throw "mapping.json yok: $Run" }
$items = @()
foreach ($f in @($Folders | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
  $s = Join-Path $Run $f
  if (Test-Path -LiteralPath "$s\receipt.json") { Write-Output "$f : receipt.json var, atlandi"; continue }
  if (-not (Test-Path -LiteralPath "$s\package.json")) { Write-Output "$f : package.json yok, atlandi"; continue }
  $items += [ordered]@{ package = "$s\package.json"; mapping = "$Run\mapping.json"; out = $s }
}
if ($items.Count -eq 0) { throw 'Aktarilacak klasor yok.' }

$batch = Join-Path $Run ('toplu_' + (Get-Date -Format yyyyMMdd_HHmmss))
New-Item -ItemType Directory -Path $batch | Out-Null
$list = [ordered]@{ target = $Target; template = $Template; on_failure = $OnFailure; ack_risk = [bool]$AckRisk; items = [object[]]$items }
[IO.File]::WriteAllText("$batch\liste.json", (ConvertTo-Json -InputObject $list -Depth 5), (New-Object Text.UTF8Encoding $false))
$result = "$batch\toplu_sonuc.json"
$act = 'UvpPdfToP8ImportBatch /LIST:"' + "$batch\liste.json" + '" /RESULT:"' + $result + '"'
if (-not $NoReadback) { $act += ' /READBACK:1' }
if (-not $NoPdf) { $act += ' /PDF:"' + "$batch\pdf" + '"' }

$sw = [Diagnostics.Stopwatch]::StartNew()
try {
  & (Join-Path $PSScriptRoot 'run_action.ps1') -Dll $Dll -Action $act -Result $result `
      -TimeoutSec (600 + $SecPerPage * $items.Count) -NoRegister:(-not $Register)
} catch { Write-Output "run_action: $_" }
if (-not (Test-Path -LiteralPath $result)) { throw "Toplu sonuc yazilmadi: $result (EPLAN log / ilerleme.log inceleyin)" }

$r = Get-Content -LiteralPath $result -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($i in $r.items) {
  Write-Output ('{0,-6} {1,-24} created={2} rejected={3} route={4} {5}s {6}' -f (Split-Path $i.out -Leaf), $i.state,
    $i.created, $i.rejected, $i.route_errors, $i.seconds, $i.error)
}
Write-Output "Proje     : $($r.target_project)"
if ($r.readback) { Write-Output "Geri okuma: ok=$($r.readback.ok) $($r.readback.seconds)s $($r.readback.out) $($r.readback.error)" }
if ($r.pdf) { Write-Output "PDF       : ok=$($r.pdf.ok) $($r.pdf.pdf) $($r.pdf.error)" }
if ($r.error) { Write-Output "TOPLU HATA: $($r.error)" }
Write-Output ("Sure      : eylem {0} sn, betik {1} sn ({2} oge)" -f $r.seconds, [int]$sw.Elapsed.TotalSeconds, $items.Count)
Write-Output "Sonuc     : $result"
