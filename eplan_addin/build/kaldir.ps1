# UVP PDF → EPLAN P8 — kaldırma. EPLAN KAPALIYKEN çalıştırın.
# kur.ps1'in KAYDETTİĞİ DLL'in (installed_dll.txt) kaydını geri alır; yoksa -Dll veya bu klasördeki DLL.
# Kullanıcı ayarını, panel durumunu, motor günlüklerini ve panelin WebView2 profilini siler.
# Oluşturulmuş test projelerine, koşu kanıtlarına, PDF'e ve işaret/çalışma klasörlerine DOKUNMAZ.
param([string]$Dll = '', [string]$Variant = 'Pro Panel', [int]$WaitSec = 120)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cfgDir = Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8'
$installed = Join-Path $cfgDir 'installed_dll.txt'
if (-not $Dll -and (Test-Path -LiteralPath $installed)) { $Dll = (Get-Content -LiteralPath $installed -Raw).Trim() }
if (-not $Dll) { $item = Get-ChildItem -LiteralPath $here -Filter 'Uvp.PdfToP8.Host_*.dll' | Select-Object -First 1; if ($item) { $Dll = $item.FullName } }
if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) { throw "EPLAN açık: önce kapatın." }
$exe = Get-ChildItem 'C:\Program Files\EPLAN\Platform\2026*\Bin\EPLAN.exe' | Select-Object -First 1 -ExpandProperty FullName
if ($Dll -and $exe) {
  $p = Start-Process -FilePath $exe -ArgumentList "/Variant:`"$Variant`" /Quiet:1 /NoSplash /NoLoadWorkspace /Frame:0 EplApiModuleAction /unregister:`"$Dll`"" -WindowStyle Hidden -PassThru
  if (-not $p.WaitForExit($WaitSec * 1000)) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 6 }
  Write-Output "Eklenti kaydı geri alma isteği verildi: $Dll (EPLAN açılınca şeritte komut olmamalı; varsa bildirin)."
} else {
  Write-Output 'Kaydı geri alınacak DLL bulunamadı; yalnız kullanıcı dosyaları temizleniyor.'
}
foreach ($name in 'uvp_pdf2p8.json', 'panel_state.json', 'installed_dll.txt') {
  $f = Join-Path $cfgDir $name
  if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f; Write-Output "Silindi: $f" }
}
foreach ($pattern in 'motor_*.log') {
  Get-ChildItem -LiteralPath $cfgDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName; Write-Output "Silindi: $($_.FullName)" }
}
$web = Join-Path $cfgDir 'WebView2'
if (Test-Path -LiteralPath $web) { Remove-Item -LiteralPath $web -Recurse -Force; Write-Output "Silindi: $web" }
Write-Output 'Kaldırıldı. Test projeleri ve kanıtlar yerinde bırakıldı.'
