# UVP PDF → EPLAN P8 — kurulum (bir kez). EPLAN KAPALIYKEN sağ tık > "PowerShell ile çalıştır".
#
# Yaptığı iş:
#   1. Motor klasörünü (analyzer_v3 içeren proje klasörü) ve Python'u bulur; Python sürümünü ve paketleri sınar.
#   2. Kullanıcı ayarını yazar: %LOCALAPPDATA%\UVP\PdfToP8\uvp_pdf2p8.json
#      (EPLAN eklentiyi kendi gölge klasörüne kopyaladığı için ayar DLL'in yanında değil burada durur.)
#   3. Daha önce bu betikle kurulmuş BAŞKA bir DLL varsa önce onun kaydını geri alır (çift kayıt kalmaz).
#   4. DLL'i EPLAN'a kaydeder (gizli EPLAN başlatması) ve KAYDI DOĞRULAR: ikinci gizli başlatmada eklentinin
#      UvpPdfToP8Version eylemi kimliğini yazmalıdır. Yazmazsa "KAYIT DOĞRULANAMADI" ile çıkar (çıkış kodu 1).
# Lisans ayarlarına, açık EPLAN oturumuna ve mevcut projelere dokunmaz.
#
#   kur.ps1 [-Dll <Uvp.PdfToP8.Host_*.dll>] [-Python <python.exe>] [-Root <proje>] [-Run <çalışma>]
param(
  [string]$Dll = '',
  [string]$Python = '',
  [string]$Root = '',
  [string]$Run = '',
  [string]$Template = '',        # boşsa: mevcut ayardaki şablon, o da yoksa UVP antedi (MAIN: UVP_013.zw9)
  [string]$Variant = 'Pro Panel',
  [int]$WaitSec = 120
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Dll) { $dllItem = Get-Item -LiteralPath $Dll }
else { $dllItem = Get-ChildItem -LiteralPath $here -Filter 'Uvp.PdfToP8.Host_*.dll' | Select-Object -First 1 }
if (-not $dllItem) { throw "Eklenti DLL'i bulunamadı (klasör: $here). -Dll verin." }

if (-not $Root) {
  $d = Get-Item -LiteralPath $here
  while ($d -and -not (Test-Path -LiteralPath (Join-Path $d.FullName 'analyzer_v3\server.py'))) { $d = $d.Parent }
  if (-not $d) { throw 'Motor klasörü (analyzer_v3) bulunamadı. -Root "<proje klasörü>" verin.' }
  $Root = $d.FullName
}
if (-not $Python) {
  $cmd = Get-Command python.exe -All -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch 'WindowsApps' } | Select-Object -First 1
  if ($cmd) { $Python = $cmd.Source }
}
if (-not $Python -or -not (Test-Path -LiteralPath $Python)) { throw 'Python bulunamadı. -Python "C:\...\python.exe" verin.' }
$version = & $Python -c "import sys; print('%d.%d' % sys.version_info[:2])"
if ($LASTEXITCODE -ne 0 -or [version]$version -lt [version]'3.12') { throw "Python 3.12 veya üstü gerekir (bulunan: $version, $Python)." }
& $Python -c "import pdfplumber, pypdfium2, PIL; print('Python paketleri: tamam')"
if ($LASTEXITCODE -ne 0) { throw "Python paketleri eksik: analyzer_v3\requirements.txt" }
if (-not $Run) { $Run = Join-Path $Root 'output\pilots\E122\20260917_pilot_demo_v01' }
if (-not (Test-Path -LiteralPath (Join-Path $Run 'manifest.json'))) { Write-Output "Uyarı: çalışma bulunamadı ($Run); panel varsayılan çalışmayı açar." }
if (-not $Template) {
  # 08.10 hata: varsayılan IEC_bas001 her kurulumda UVP antedini eziyordu. Önce mevcut ayar korunur.
  $old = Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\uvp_pdf2p8.json'
  if (Test-Path -LiteralPath $old) { $Template = [string]((Get-Content -LiteralPath $old -Raw -Encoding UTF8 | ConvertFrom-Json).template) }
  if (-not $Template -or $Template -match 'IEC_bas001') { $Template = Join-Path $env:PUBLIC ('Eplan\Data\' + [char]0x015E + 'ablonlar\Company name\UVP_013.zw9') }
}
Write-Output "Proje şablonu: $Template"
if (-not (Test-Path -LiteralPath $Template)) { throw "EPLAN proje şablonu yok: $Template" }

$cfgDir = Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfg = [ordered]@{ root = $Root; python = $Python; run = $Run; port = 8791; template = $Template }
[System.IO.File]::WriteAllText((Join-Path $cfgDir 'uvp_pdf2p8.json'), ($cfg | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "Ayar yazıldı: $(Join-Path $cfgDir 'uvp_pdf2p8.json')"

if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) {
  Write-Output "EPLAN açık: kayıt yapılmadı. EPLAN'ı kapatıp bu betiği yeniden çalıştırın."
  exit 1
}
$exe = Get-ChildItem 'C:\Program Files\EPLAN\Platform\2026*\Bin\EPLAN.exe' | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'EPLAN 2026 bulunamadı.' }

function Invoke-Eplan([string]$Single, [string]$Expect) {
  # Kayıt başlatması kendiliğinden kapanmaz: beklenen dosya oluşunca veya süre dolunca YALNIZ bu süreç kapatılır.
  $p = Start-Process -FilePath $exe -ArgumentList "/Variant:`"$Variant`" /Quiet:1 /NoSplash /NoLoadWorkspace /Frame:0 $Single" -WindowStyle Hidden -PassThru
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $WaitSec) {
    if ($Expect -and (Test-Path -LiteralPath $Expect)) { Start-Sleep -Seconds 3; break }
    Start-Sleep -Seconds 3
  }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 6 }
}

$installed = Join-Path $cfgDir 'installed_dll.txt'
if (Test-Path -LiteralPath $installed) {
  $previous = (Get-Content -LiteralPath $installed -Raw).Trim()
  if ($previous -and $previous -ne $dllItem.FullName -and (Test-Path -LiteralPath $previous)) {
    Write-Output "Önceki kayıt geri alınıyor: $previous"
    Invoke-Eplan "EplApiModuleAction /unregister:`"$previous`"" ''
  }
}
Invoke-Eplan "EplApiModuleAction /register:`"$($dllItem.FullName)`"" ''

$probe = Join-Path $env:TEMP ("uvp_kur_dogrulama_{0}.json" -f [guid]::NewGuid().ToString('N'))
Invoke-Eplan "UvpPdfToP8Version /OUT:`"$probe`"" $probe
if (-not (Test-Path -LiteralPath $probe)) {
  Write-Output "KAYIT DOĞRULANAMADI: EPLAN eklentinin kimlik eylemini çalıştırmadı ($($dllItem.Name))."
  Write-Output "Lisans/API izni, EPLAN günlüğü veya DLL yolu kontrol edilmeli. Kurulum TAMAMLANMADI."
  exit 1
}
$identity = Get-Content -LiteralPath $probe -Raw -Encoding UTF8 | ConvertFrom-Json
Remove-Item -LiteralPath $probe
[System.IO.File]::WriteAllText($installed, $dllItem.FullName, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "Kuruldu ve doğrulandı: $($identity.assembly) (EPLAN $($identity.eplan_file_version))."
Write-Output "EPLAN'ı açın → şerit UVP sekmesi veya Genişletmeler → API → 'UVP PDF → P8'; ikisi de yoksa 'Komutu ara' kutusuna UVP yazın."
