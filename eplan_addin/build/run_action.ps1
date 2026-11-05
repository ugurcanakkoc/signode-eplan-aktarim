# EPLAN P8'yi gizli başlatır, add-in'i kaydeder, verilen action'ı çalıştırır, add-in kaydını
# geri alır. Yalnız KENDİ başlattığı EPLAN sürecini bekler/kapatır; açık bir EPLAN varsa başlamaz.
#
# ÖLÇÜM (2026-09-16, bu makinede EPLAN 2026.0.3 / Pro Panel):
#   * Komut satırında eylem ZİNCİRİ çalışmıyor. EPLAN switch'lerden sonraki İLK belirteci eylem adı
#     sayıyor, kalan her şeyi o eylemin parametresi yapıyor: 'Auto EplApiModuleAction …' denemesinde
#     log'a "'Auto' eylemi bulunamadı" (S085021) düştü ve zincirin geri kalanı hiç çalışmadı.
#     Bu yüzden her başlatmada TEK eylem verilir; kayıt / eylem / kayıt-silme ayrı başlatmalardır.
#   * EPLAN eylemden sonra kendiliğinden kapanmıyor ('exit' eylem zinciri içinde işe yaramadı).
#     Bu yüzden beklenen sonuç dosyası oluşana kadar beklenir, sonra YALNIZ bizim süreç kapatılır.
#   * Add-in kaydı EPLAN ayarlarında kalıcıdır; bu yüzden ayrı başlatmada yapılan kayıt sonraki
#     başlatmada geçerlidir. Koşu sonunda kayıt geri alınır (-KeepRegistration ile korunur).
#
# Kullanım: run_action.ps1 -Dll <Uvp.PdfToP8.Host.dll> -Action 'UvpPdfToP8Probe /OUT:"..."' -Result <beklenen dosya>
param(
  [Parameter(Mandatory)] [string]$Dll,
  [Parameter(Mandatory)] [string]$Action,
  [Parameter(Mandatory)] [string]$Result,
  [int]$TimeoutSec = 600,
  [int]$RegisterSec = 60,
  # Bu makinede kurulu varyant 'Pro Panel' (kullanıcı kısayolu). 'Electric P8' klasörü kurulu değil.
  [string]$Variant = 'Pro Panel',
  [switch]$KeepRegistration,
  # Eklenti kur.ps1 ile kalıcı kuruluysa kayıt/kayıt silme başlatmaları atlanır (sayfa başına 2 soğuk EPLAN
  # başlatması ≈ 100 sn; eylemin kendisi 2-12 sn — ölçüm 02.10). Kurulu eklentiyi silmez.
  [switch]$NoRegister
)
$ErrorActionPreference = 'Stop'
$exe = Get-ChildItem 'C:\Program Files\EPLAN\Platform\2026*\Bin\EPLAN.exe' | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'EPLAN.exe bulunamadı.' }
if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) { throw 'Açık bir EPLAN var; kullanıcının oturumuna dokunulmaz. Kapatınca tekrar deneyin.' }
if (Test-Path -LiteralPath $Result) { throw 'Sonuç dosyası zaten var; korunuyor. Yeni bir çıktı yolu seçin.' }

function Invoke-EplanAction {
  param([string]$Single, [string]$Expect, [int]$Wait)
  $argline = "/Variant:`"$Variant`" /Quiet:1 /NoSplash /NoLoadWorkspace /Frame:0 $Single"
  Write-Output "EYLEM: $Single"
  $p = Start-Process -FilePath $exe -ArgumentList $argline -WindowStyle Hidden -PassThru
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $done = $false
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $Wait) {
    if ($Expect -and (Test-Path -LiteralPath $Expect)) { Start-Sleep -Seconds 5; $done = $true; break }
    Start-Sleep -Seconds 3
  }
  $state = if ($p.HasExited) { "EXITED code=$($p.ExitCode)" } elseif ($done) { 'SONUÇ YAZILDI' } else { 'SÜRE DOLDU' }
  Write-Output ("  {0} ({1} sn)" -f $state, [int]$sw.Elapsed.TotalSeconds)
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 4 }
  return $done
}

if (-not $NoRegister) { Invoke-EplanAction -Single "EplApiModuleAction /register:`"$Dll`"" -Expect '' -Wait $RegisterSec | Out-Null }
# Kayıt doğrulaması ayrı başlatma gerektirir; burada sonraki eylemin sonuç dosyası kaydın kanıtıdır (yoksa aşağıda hata).
$ok = Invoke-EplanAction -Single $Action -Expect $Result -Wait $TimeoutSec
if (-not $KeepRegistration -and -not $NoRegister) {
  Invoke-EplanAction -Single "EplApiModuleAction /unregister:`"$Dll`"" -Expect '' -Wait $RegisterSec | Out-Null
}
if (-not (Test-Path -LiteralPath $Result)) { throw 'EPLAN beklenen sonuç dosyasını üretmedi.' }
$resultText = Get-Content -LiteralPath $Result -Raw -Encoding UTF8
$resultData = $resultText | ConvertFrom-Json
if ($resultData.ok -eq $false) { throw "EPLAN eylemi başarısız (ok=false): $Result — hata ve reddedilenleri inceleyin." }
Write-Output "SONUÇ: $Result"
