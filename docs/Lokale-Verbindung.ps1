# Optional: per Rechtsklick mit PowerShell als Administrator ausführen.
# Erlaubt nur lokale Subnetze und private Windows-Netzwerkprofile.
$ErrorActionPreference = 'Stop'
$program = Join-Path $PSScriptRoot 'RobinsShare.exe'
if (-not (Test-Path $program)) { throw 'Bitte im Installationsordner ausführen.' }
New-NetFirewallRule -DisplayName 'Robins Share - lokale Dateien' -Direction Inbound -Program $program -Protocol TCP -LocalPort 5057 -Action Allow -Profile Private -RemoteAddress LocalSubnet
New-NetFirewallRule -DisplayName 'Robins Share - lokale Erkennung' -Direction Inbound -Program $program -Protocol UDP -LocalPort 5353 -Action Allow -Profile Private -RemoteAddress LocalSubnet
Write-Host 'Lokale Übertragung in privaten Netzwerken freigegeben.'
