<#
.SYNOPSIS
    Builds the two downloads for a release: the Windows installer and the signed Android APK.

.DESCRIPTION
    Writes installer\out\CompanionDeck-Setup-<version>.exe, installer\out\CompanionDeck-<version>.apk and a
    SHA256SUMS.txt beside them. These are what a GitHub Release carries, so people can install without
    building anything.

    Needs: the .NET 8 SDK, a JDK 17 and the Android SDK (as for Setup.cmd), and Inno Setup 6
    (winget install JRSoftware.InnoSetup). The APK is signed with the maintainer's key when its
    keystore.properties is found (COMPANIONDECK_KEYSTORE_PROPS, or %USERPROFILE%\.companiondeck-signing);
    otherwise it is debug-signed, which installs but cannot be updated in place by official releases.
#>
param([string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\scripts\Deck.Common.ps1"
$root = Get-DeckRoot
$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $PSScriptRoot 'stage'

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed. Run: winget install JRSoftware.InnoSetup' }

Remove-Item $out, $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out, $stage, "$stage\scripts" -Force | Out-Null

Write-Host '1/4  PC program (self-contained, so the user needs no .NET)...' -ForegroundColor Cyan
& dotnet publish "$root\host\CompanionDeck.Host\CompanionDeck.Host.csproj" -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false -o "$stage\pub" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'host publish failed' }

Write-Host '2/4  Android app...' -ForegroundColor Cyan
$jdk = Find-Jdk
$env:JAVA_HOME = $jdk
$defaultProps = Join-Path $env:USERPROFILE '.companiondeck-signing\keystore.properties'
if (-not $env:COMPANIONDECK_KEYSTORE_PROPS -and (Test-Path $defaultProps)) { $env:COMPANIONDECK_KEYSTORE_PROPS = $defaultProps }
Set-LocalProperties -SdkPath (Find-AndroidSdk) | Out-Null
Push-Location "$root\tablet"
try { & .\gradlew.bat :app:assembleRelease --console=plain -q; if ($LASTEXITCODE -ne 0) { throw 'tablet build failed' } } finally { Pop-Location }
Copy-Item "$root\tablet\app\build\outputs\apk\release\app-release.apk" "$stage\CompanionDeck.apk"

Write-Host '3/4  Installer...' -ForegroundColor Cyan
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md') { Copy-Item "$root\$f" $stage }
foreach ($f in 'Post-Install.ps1', 'Start-Deck.ps1', 'Stop-Deck.ps1', 'Tablet-Setup.ps1') { Copy-Item "$PSScriptRoot\$f" "$stage\scripts" }
& $iscc "/DAppVersion=$Version" "/DSourceDir=$stage" "/O$out" "$PSScriptRoot\CompanionDeck.iss" /Q
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Copy-Item "$stage\CompanionDeck.apk" "$out\CompanionDeck-$Version.apk"

Write-Host '4/4  Checksums...' -ForegroundColor Cyan
Get-ChildItem $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" } |
    Set-Content "$out\SHA256SUMS.txt" -Encoding ascii
Remove-Item $stage -Recurse -Force

Get-ChildItem $out | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}} | Format-Table -AutoSize
