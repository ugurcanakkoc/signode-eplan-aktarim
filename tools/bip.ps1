# Usage: bip.ps1 [-Count 15]
# Sound-only alert (no window): alternating beeps, about 1 second apart. User 24.10: "sadece ses, ekrana bir şey çıkartma".
param([int]$Count = 15)
for ($i = 0; $i -lt $Count; $i++) {
  [console]::Beep(1400, 250); [console]::Beep(900, 250)
  Start-Sleep -Milliseconds 500
}
