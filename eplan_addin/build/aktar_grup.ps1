# Grup aktarimi (tools/toplu_aktar.py cagirir). Gruplar ';' ile, grup ici kosular ',' ile ayrilir: her grubun ilk
# kosusu yeni UVP_ projesi kurar, sonrakiler ayni projeye eklenir. DLL KURMA YOK. Kilit protokolu (EPLAN_KILIT).
# Gizli EPLAN BelowNormal. Kullanicinin EPLAN'ina dokunulmaz: acik EPLAN varsa bekler. ASCII.
param([Parameter(Mandatory)] [string]$Log, [Parameter(Mandatory)] [string]$Groups)
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lock = "$root\output\ajan_kayit\20261003_troester\EPLAN_KILIT"
$tpl = (Get-ChildItem 'C:\Users\Public\Eplan\Data\*\Company name\UVP_013.zw9' | Select-Object -First 1).FullName
function L($m) {
  $line = (Get-Date -Format 'HH:mm:ss') + ' ' + $m
  for ($k = 0; $k -lt 20; $k++) { try { Add-Content -Path $Log -Value $line -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 250 } }
}
L "START tpl=$tpl groups=$Groups"
$me = "toplu_aktar " + (Get-Date -Format s) + " pid=$PID"
$got = $false
for ($i = 0; $i -lt 240 -and -not $got; $i++) {
  $busy = (Get-Process -Name EPLAN -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $lock)
  if (-not $busy) {
    try { New-Item -ItemType File -Path $lock -Value $me -ErrorAction Stop | Out-Null } catch { L 'kilit alinamadi (atomik)'; Start-Sleep -Seconds 60; continue }
    Start-Sleep -Seconds 2
    $got = ((Get-Content -LiteralPath $lock -Raw).Trim() -eq $me) -and -not (Get-Process -Name EPLAN -ErrorAction SilentlyContinue)
    if ($got) { break }
    if ((Get-Content -LiteralPath $lock -Raw -ErrorAction SilentlyContinue).Trim() -eq $me) { Remove-Item -LiteralPath $lock -Force }
  }
  L ("bekle: EPLAN=" + [bool](Get-Process -Name EPLAN -ErrorAction SilentlyContinue) + " kilit=" + (Test-Path -LiteralPath $lock))
  Start-Sleep -Seconds 60
}
if (-not $got) { L 'KILIT ALINAMADI - DUR'; L 'DONE'; exit 3 }
L ("kilit alindi: " + (Get-Content -LiteralPath $lock))
$Dll = (Get-Content -LiteralPath "$env:LOCALAPPDATA\UVP\PdfToP8\installed_dll.txt" -Raw).Trim()
L ("kurulu DLL: " + $Dll)
$prio = Start-Job -ScriptBlock {
  param($LogPath)
  while (-not ((Test-Path $LogPath) -and (Select-String -Path $LogPath -Pattern ' DONE' -Quiet))) {
    Get-CimInstance Win32_Process -Filter "Name='EPLAN.exe'" | Where-Object { $_.CommandLine -match '/Quiet:1' } | ForEach-Object {
      $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
      if ($p -and $p.PriorityClass -ne 'BelowNormal' -and $p.PriorityClass -ne 'Idle') { $p.PriorityClass = 'BelowNormal' }
    }
    Start-Sleep -Seconds 3
  }
} -ArgumentList $Log
try {
  foreach ($grp in ($Groups -split ';' | Where-Object { $_ })) {
    $Target = ''
    foreach ($Run in ($grp -split ',' | Where-Object { $_ })) {
      L ("aktar " + $Run + " target=" + $Target)
      $args2 = @{ Run = $Run; Folders = 's1'; Dll = $Dll; Template = $tpl; AckRisk = $true; SecPerPage = 60 }
      if ($Target) { $args2.Target = $Target }
      $res = & "$root\eplan_addin\build\aktar_toplu.ps1" @args2 *>&1
      foreach ($x in $res) { L ("  " + $x) }
      if (-not $Target) { $e = Get-ChildItem "$Run\s1\*.elk" | Select-Object -First 1; if ($e) { $Target = $e.FullName; L ("hedef proje: " + $Target) } }
    }
  }
}
catch { L ("  HATA " + $_) }
finally {
  # Yalniz gizli (/Quiet:1) EPLAN 3 dk icinde kapanmazsa durdurulur; kullanicinin EPLAN'ina dokunulmaz.
  for ($w = 0; $w -lt 36 -and (Get-Process -Name EPLAN -ErrorAction SilentlyContinue); $w++) { Start-Sleep -Seconds 5 }
  Get-CimInstance Win32_Process -Filter "Name='EPLAN.exe'" | Where-Object { $_.CommandLine -match '/Quiet:1' } | ForEach-Object { L ('gizli EPLAN kapanmadi: Stop ' + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  if ((Get-Content -LiteralPath $lock -Raw -ErrorAction SilentlyContinue).Trim() -eq $me) { Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue }
  L ("kilit silindi: " + (-not (Test-Path -LiteralPath $lock)))
  L 'DONE'
  Start-Sleep -Seconds 4
  Stop-Job $prio -ErrorAction SilentlyContinue; Remove-Job $prio -Force -ErrorAction SilentlyContinue
}
