# Madde 3 EPLAN deneyleri (09.10). ASCII. Plan ve yorum tablosu:
#   output\ajan_kayit\20261009_10madde\duzelt_K_csharp_deney_xref\PLAN.md
# (1) UvpPdfToP8XrefProbe: kesinti oku capraz referansi UVP_013 projesinde 0/18, IEC_bas001'de 18/18. KOPYA projelerde
#     okur + tek degisiklik (ayar / UpdateProjectStructureFromSettings / IsStarSource) + yeniden okur.
# (2) UvpPdfToP8IdentProbe: sayfa basina ayri E280 dugumu. Yeni test projelerinde (kollar A,B,C,K) yapi tanimlayicilari.
#
# KURALLAR: kullanici EPLAN'i aciksa DURUR. Kilit output\ajan_kayit\20261003_troester\EPLAN_KILIT (atomik New-Item,
# doluysa 60 sn bekle, 5 deneme), bitince silinir. run_action.ps1 HER ZAMAN -NoRegister: kurulu eklenti
# (%LOCALAPPDATA%\UVP\PdfToP8\installed_dll.txt) deney eylemlerini icermiyorsa DURUR (DLL kurmak bu betigin isi degil).
# Yalniz kopyalar (output\p8runs\madde3_xref_<zaman>\) ve yeni UVP_IDENT_ test projeleri yazilir.
#
# Kullanim: deney_xref.ps1 [-Only xref|ident] [-Out <kayit klasoru>]
param([string]$Only = '', [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stamp = Get-Date -Format yyyyMMdd_HHmmss
if (-not $Out) { $Out = Join-Path $root "output\ajan_kayit\20261009_10madde\duzelt_K_csharp_deney_xref\kosu_$stamp" }
$work = Join-Path $root "output\p8runs\madde3_xref_$stamp"
$lock = Join-Path $root 'output\ajan_kayit\20261003_troester\EPLAN_KILIT'
$runAction = Join-Path $PSScriptRoot 'run_action.ps1'

if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) { throw 'EPLAN acik (kullanici oturumu): DUR. EPLAN kapaliyken tekrar.' }
$dll = (Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\installed_dll.txt') -Raw).Trim()
$latin = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
foreach ($a in 'UvpPdfToP8XrefProbe', 'UvpPdfToP8IdentProbe') {
  $utf16 = ($a.ToCharArray() | ForEach-Object { [string]$_ + [char]0 }) -join ''
  if (-not $latin.Contains($utf16)) {
    throw "Kurulu eklentide $a yok ($dll). Once deney DLL'i kurulmali (orkestra/kullanici karari): kur.ps1 -Dll output\eplan_addin_bin\Uvp.PdfToP8.Host_deney.dll; bitince kur.ps1 -Dll $dll ile geri."
  }
}

$locked = $false
for ($i = 0; $i -lt 5 -and -not $locked; $i++) {
  try { New-Item -ItemType File -Path $lock -ErrorAction Stop | Out-Null; $locked = $true }
  catch { Write-Output "Kilit dolu, 60 sn: $lock"; Start-Sleep -Seconds 60 }
}
if (-not $locked) { throw "Kilit alinamadi: $lock" }

function Copy-Project([string]$src, [string]$dst) {
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  Copy-Item -LiteralPath "$src.elk" -Destination $dst
  Copy-Item -LiteralPath "$src.edb" -Destination $dst -Recurse
  return Join-Path $dst ((Split-Path $src -Leaf) + '.elk')
}

try {
  New-Item -ItemType Directory -Force -Path $Out, $work | Out-Null
  Write-Output "Kayit: $Out"
  Write-Output "Kopyalar: $work"
  if ($Only -ne 'ident') {
    $ok = Join-Path $root 'output\p8runs\20261006_pac_e210\s13\UVP_PDF2P8_20261006_133549'    # IEC_bas001, xref 18/18
    $bad = Join-Path $root 'output\p8runs\20261006_pac2_e210\s13\UVP_PDF2P8_20261006_134037'  # UVP_013, xref 0/18
    $chains = 'EsCrossReference.Interruptionpoint.DisplayDestinationAtChains'
    $runs = @(
      @{ id = 'ok_chains1';       src = $ok;  args = "/MUTATE:set /SETTING:$chains /VALUE:1" },
      @{ id = 'bozuk_chains0';    src = $bad; args = "/MUTATE:set /SETTING:$chains /VALUE:0" },
      @{ id = 'bozuk_updstruct';  src = $bad; args = '/MUTATE:updstruct' },
      @{ id = 'bozuk_starsource'; src = $bad; args = '/MUTATE:starsource' })
    foreach ($r in $runs) {
      $elk = Copy-Project $r.src (Join-Path $work $r.id)
      $res = Join-Path $Out "xref_$($r.id).json"
      $act = 'UvpPdfToP8XrefProbe /PROJECT:"' + $elk + '" /OUT:"' + $res + '" ' + $r.args
      try { & $runAction -Dll $dll -Action $act -Result $res -TimeoutSec 600 -NoRegister } catch { Write-Output "$($r.id): $_" }
    }
  }
  if ($Only -ne 'xref') {
    # Sablon yolu Turkce harf iceriyor (Sablonlar); ASCII kopya komut satiri ayrismasini devre disi birakir.
    $cfg = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\uvp_pdf2p8.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $tpl = Join-Path $work 'sablon\UVP_013.zw9'
    New-Item -ItemType Directory -Force -Path (Split-Path $tpl) | Out-Null
    Copy-Item -LiteralPath $cfg.template -Destination $tpl
    $tplK = 'C:\ProgramData\EPLAN\O_Data\Electric P8 Data\2026.0.3\Templates\EPLAN\IEC_bas001.zw9'
    $identDir = Join-Path $work 'ident'
    $res = Join-Path $identDir 'ident_probe.json'
    $act = 'UvpPdfToP8IdentProbe /OUT:"' + $identDir + '" /TEMPLATE:"' + $tpl + '" /TEMPLATE_K:"' + $tplK + '" /ARMS:P,Q,R'
    try { & $runAction -Dll $dll -Action $act -Result $res -TimeoutSec 1200 -NoRegister } catch { Write-Output "ident: $_" }
    if (Test-Path -LiteralPath $res) { Copy-Item -LiteralPath $res -Destination $Out }
  }

  # Ozet + ayar farki (OK vs BOZUK, degisiklikten ONCEKI doku).
  $lines = @()
  foreach ($f in Get-ChildItem -LiteralPath $Out -Filter 'xref_*.json') {
    $j = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $row = "{0,-24} ok={1} once {2}/{3}" -f $f.BaseName, $j.ok, $j.before.xref_filled, $j.before.pair_ends
    if ($j.after) { $row += " sonra {0}/{1} kapat-ac {2}/{3}" -f $j.after.xref_filled, $j.after.pair_ends, $j.after_reopen.xref_filled, $j.after_reopen.pair_ends }
    $row += " motor(once)=$($j.before.verify_found_partner) | $($j.mutation) $($j.error)"
    $lines += $row
  }
  $ident = Join-Path $Out 'ident_probe.json'
  if (Test-Path -LiteralPath $ident) {
    $j = Get-Content -LiteralPath $ident -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($arm in $j.arms) {
      $lines += "ident $($arm.arm) ok=$($arm.ok) yapi='$($arm.page_structure)' xref $($arm.xref.xref_filled)/$($arm.xref.pair_ends) guncelle-sonra $($arm.xref_after_update.xref_filled) kapat-ac $($arm.xref_after_reopen.xref_filled) $($arm.error)"
      foreach ($s in $arm.steps) { $lines += "   {0,-32} E280={1} 280={2}" -f $s.label, $s.locations.counts.'Location:E280', $s.locations.counts.'Plant:280' }
    }
  }
  $okJson = Join-Path $Out 'xref_ok_chains1.json'
  $badJson = Join-Path $Out 'xref_bozuk_chains0.json'
  if ((Test-Path -LiteralPath $okJson) -and (Test-Path -LiteralPath $badJson)) {
    $sa = (Get-Content -LiteralPath $okJson -Raw -Encoding UTF8 | ConvertFrom-Json).settings
    $sb = (Get-Content -LiteralPath $badJson -Raw -Encoding UTF8 | ConvertFrom-Json).settings
    $keys = @($sa.PSObject.Properties.Name) + @($sb.PSObject.Properties.Name) | Sort-Object -Unique
    $diff = foreach ($k in $keys) { $va = [string]$sa.$k; $vb = [string]$sb.$k; if ($va -ne $vb) { "$k`tOK=$va`tBOZUK=$vb" } }
    [IO.File]::WriteAllLines((Join-Path $Out 'ayar_farki.tsv'), [string[]]@($diff), (New-Object Text.UTF8Encoding $false))
    $lines += "ayar farki: $(@($diff).Count) satir -> ayar_farki.tsv"
  }
  [IO.File]::WriteAllLines((Join-Path $Out 'ozet.txt'), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
  $lines | ForEach-Object { Write-Output $_ }
}
finally {
  Remove-Item -LiteralPath $lock -ErrorAction SilentlyContinue
  Write-Output "Kilit silindi: $lock"
}
