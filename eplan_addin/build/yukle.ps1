# yukle.ps1 — UVP eklentisi kurulumu + serit scripti. EPLAN KAPALIYKEN sag tik > "PowerShell ile calistir".
#   1) Eklenti DLL'ini kurar (kur.ps1; -Dll verilmezse installed_dll.txt'teki ya da en yeni Host_*.dll)
#   2) Serit scriptini (UVP_Araclar_Serit.cs) EPLAN'in bulacagi yere kopyalar ve klasoru acar.
#   3) Scriptin EPLAN'a yuklenmesi (bir kez, kalici): EPLAN'i acin > "Script" sekmesi > "Yukle" (Load) > bu dosyayi secin.
#      (13.10 olcumu: gizli EPLAN'da RegisterScript sonucu dogrulanamadi; GUI'den yukleme EPLAN'in standart yoludur.)
param([string]$Dll = '', [switch]$SadeceScript)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Get-Item $here).Parent.Parent.FullName
$script = Join-Path $root 'eplan_addin\scripts\UVP_Araclar_Serit.cs'
if (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) { throw 'EPLAN acik. Once kapatin.' }
if (-not (Test-Path $script)) { throw "Script yok: $script" }
$kayit = Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\installed_dll.txt'
if (-not $Dll) {
  if (Test-Path $kayit) { $Dll = (Get-Content $kayit -Raw).Trim() }
  else { $Dll = (Get-ChildItem (Join-Path $root 'output\eplan_addin_bin\Uvp.PdfToP8.Host_*.dll') | Sort-Object LastWriteTime | Select-Object -Last 1).FullName }
}
if (-not $SadeceScript) {
  Write-Host "1) Eklenti kuruluyor: $Dll"
  & (Join-Path $here 'kur.ps1') -Dll $Dll -Root $root
}
$hedef = Join-Path $env:LOCALAPPDATA 'UVP\PdfToP8\scripts'
New-Item -ItemType Directory -Force -Path $hedef | Out-Null
Copy-Item $script (Join-Path $hedef 'UVP_Araclar_Serit.cs') -Force
Write-Host "2) Serit scripti kopyalandi: $hedef\UVP_Araclar_Serit.cs"
Write-Host '3) SON ADIM (bir kez): EPLAN''i acin > "Script" sekmesi > "Yukle" > bu dosyayi secin. EPLAN scripti hatirlar;'
Write-Host '   seritte "UVP Araclar" sekmesi (PDF > P8 / Cizim / Kontrol / Duzenle / Teshis) gelir.'
Start-Process explorer.exe $hedef
