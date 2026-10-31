# Start-menu helper: (re)runs the USB and tablet steps of the installer, for when ADB or the tablet was not
# ready at install time. Shows what happened and waits, because it is run from a shortcut.
$app = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Post-Install.ps1') -AppDir $app -Adb:(-not (Test-Path (Join-Path $app 'platform-tools\adb.exe'))) -Tablet
Write-Host ''
Write-Host '  Companion Deck - USB and tablet setup' -ForegroundColor Cyan
Write-Host ''
$summary = Join-Path $app 'install-summary.txt'
if (Test-Path $summary) { Get-Content $summary | ForEach-Object { if ($_ -like 'PROBLEM:*') { Write-Host "  $_" -ForegroundColor Yellow } else { Write-Host "  $_" -ForegroundColor Green } } }
Write-Host ''
Read-Host '  Press Enter to close'
