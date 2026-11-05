# Test/prova başlatıcısı: EPLAN'ı KENDİ oturumu olarak açar, eklentiyi kaydeder ve paneli açar.
# Normal kullanıcı bunu kullanmaz (EPLAN şeridi: UVP sekmesi / Extensions > API / "Komutu ara: UVP" → "UVP PDF → P8"). Bu betik yalnız
# otomatik uçtan uca deneme ve sunum provası içindir.
#
#   pilot_eplan.ps1 -Dll <dll> [-CdpPort 9333] [-PanelArgs '/PYTHON:"C:\yok\python.exe"'] [-Visible]
#   pilot_eplan.ps1 -Stop -Pid <EPLAN pid>      # yalnız bu betiğin açtığı EPLAN'ı ve panel motorunu kapatır
#
# Kurallar: açık bir EPLAN varsa BAŞLAMAZ (kullanıcı oturumuna dokunulmaz). -CdpPort verilirse WebView2
# yerel hata ayıklama portu açılır (yalnız 127.0.0.1); panel sayfası tarayıcı sürücüsüyle sınanabilir.
param(
  [string]$Dll,
  [int]$CdpPort = 0,
  [string]$PanelArgs = '',
  [string]$Variant = 'Pro Panel',
  [switch]$Visible,
  [switch]$Stop,
  [int]$EplanPid = 0,
  [int]$EnginePort = 8791
)
$ErrorActionPreference = 'Stop'
$exe = Get-ChildItem 'C:\Program Files\EPLAN\Platform\2026*\Bin\EPLAN.exe' | Select-Object -First 1 -ExpandProperty FullName

function Stop-Engine {
  # Panel motoru (python -m analyzer_v3.server --port N): EPLAN zorla kapatılınca pencere kapanış olayı
  # çalışmaz; yalnız bu porttaki motor kapatılır.
  Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -match 'analyzer_v3\.server' -and $_.CommandLine -match "--port $EnginePort" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Output "motor kapatıldı: $($_.ProcessId)" }
}

if ($Stop) {
  if ($EplanPid) {
    $p = Get-Process -Id $EplanPid -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq 'EPLAN') { Stop-Process -Id $EplanPid -Force; Write-Output "EPLAN kapatıldı: $EplanPid" }
  }
  Stop-Engine
  return
}

if (-not $Dll -or -not (Test-Path -LiteralPath $Dll)) { throw 'DLL bulunamadı.' }
if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) { throw 'Açık bir EPLAN var; kullanıcının oturumuna dokunulmaz.' }

$reg = Start-Process -FilePath $exe -ArgumentList "/Variant:`"$Variant`" /Quiet:1 /NoSplash /NoLoadWorkspace /Frame:0 EplApiModuleAction /register:`"$Dll`"" -WindowStyle Hidden -PassThru
if (-not $reg.WaitForExit(60000)) { Stop-Process -Id $reg.Id -Force; Start-Sleep -Seconds 4 }
Write-Output 'eklenti kaydedildi'

if ($CdpPort) {
  # Uzaktan hata ayıklama kapısı yalnız TEST derlemesinde vardır (build.bat <çıktı> <ad> test).
  $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Dll))
  $needle = [System.Text.Encoding]::Unicode.GetBytes('UVP_PDF2P8_CDP_PORT')
  if (([System.Text.Encoding]::Unicode.GetString($bytes)).IndexOf('UVP_PDF2P8_CDP_PORT') -lt 0) { throw '-CdpPort için TEST derlemesi gerekir: build.bat <çıktı> <ad> test' }
  $env:UVP_PDF2P8_CDP_PORT = "$CdpPort"
}
$frame = if ($Visible) { '' } else { '/Quiet:1 /Frame:0 ' }
$style = if ($Visible) { 'Normal' } else { 'Minimized' }
$p = Start-Process -FilePath $exe -ArgumentList "/Variant:`"$Variant`" /NoSplash /NoLoadWorkspace $frame UvpPdfToP8Panel $PanelArgs" -WindowStyle $style -PassThru
Write-Output "EPLAN_PID=$($p.Id)"
