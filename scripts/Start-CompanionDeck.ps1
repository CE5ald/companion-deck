<#
.SYNOPSIS
    Starts the Companion Deck host.

.DESCRIPTION
    Builds the host if any source file is newer than the binary, then runs it. The host
    waits for the tablet; plugging in the USB cable is all that is needed after that - it
    detects the device, starts the tablet app, and connects.

    Run Setup.cmd first on a fresh clone.

.PARAMETER Tablet
    Also rebuild and reinstall the tablet app before starting. Needs the Android SDK.

.PARAMETER Rebuild
    Force a rebuild of the host even if a binary already exists.
#>
[CmdletBinding()]
param(
    [switch]$Tablet,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Deck.Common.ps1"

$root = Get-DeckRoot
$hostDir = Join-Path $root 'host\CompanionDeck.Host'

Write-Head 'Companion Deck'

if (-not (Test-DotNetSdk)) {
    Write-Bad 'No .NET 8 SDK. Run Setup.cmd first.'
    exit 1
}

# If the elevated task is installed it owns the agent, and it also holds the binary open.
$taskOwns = Stop-DeckHost

# ---------------------------------------------------------------- host build

$exe = Get-HostExe
$needsBuild = $Rebuild -or -not $exe
if (-not $needsBuild) {
    $newest = Get-ChildItem -Path $hostDir -Recurse -Include *.cs, *.csproj -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest -and $newest.LastWriteTime -gt $exe.LastWriteTime) { $needsBuild = $true }
}

if ($needsBuild) {
    Write-Step 'Building host...'
    Push-Location $hostDir
    try {
        & dotnet build -c Debug -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
    } finally { Pop-Location }
    $exe = Get-HostExe
}

if (-not $exe) { throw 'Could not find CompanionDeck.Host.exe after building.' }

# -------------------------------------------------------------- tablet build

if ($Tablet) {
    $jdk = Find-Jdk
    $sdk = Find-AndroidSdk
    if (-not $jdk) { Write-Bad 'No JDK 17+. Run Setup.cmd.'; exit 1 }
    if (-not $sdk) { Write-Bad 'No Android SDK. Run Setup.cmd.'; exit 1 }

    Write-Step 'Building and installing the tablet app...'
    Set-LocalProperties -SdkPath $sdk | Out-Null
    $env:JAVA_HOME = $jdk

    Push-Location (Join-Path $root 'tablet')
    try {
        & .\gradlew.bat :app:assembleDebug --console=plain -q
        if ($LASTEXITCODE -ne 0) { throw 'Tablet build failed.' }

        $adb = Find-Adb
        if ($adb) { & $adb install -r 'app\build\outputs\apk\debug\app-debug.apk' }
        else { Write-Miss 'No adb; the app was built but not installed.' }
    } finally { Pop-Location }
}

# ------------------------------------------------------------------- run

Write-Host ''

if ($taskOwns) {
    # Hand control back to the elevated task rather than running a second, unelevated copy
    # that would fight it for the tablet.
    Start-ScheduledTask -TaskName (Get-DeckTaskName)
    Start-Sleep -Seconds 2
    if (Get-Process CompanionDeck.Host -ErrorAction SilentlyContinue) {
        Write-Ok 'Elevated agent restarted. Plug the tablet in and it will connect by itself.'
        Write-Host "  Log: $env:LOCALAPPDATA\CompanionDeck\host.log" -ForegroundColor DarkGray
    } else {
        Write-Miss 'The task did not start. Check Task Scheduler.'
    }
    Write-Host ''
    exit 0
}

Write-Ok 'Host starting. Plug the tablet in over USB and it will connect by itself.'
Write-Host '  Press Ctrl+C to stop.' -ForegroundColor DarkGray
Write-Host ''

& $exe.FullName @args
