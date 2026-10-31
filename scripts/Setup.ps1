<#
.SYNOPSIS
    One-time setup for Companion Deck: checks what this PC is missing, builds both sides,
    and installs the tablet app.

.DESCRIPTION
    Run this once after cloning. It never installs anything without asking, and it never
    fails the whole run because one optional piece is absent - the host alone is useful,
    and the tablet app can be sideloaded by hand if the Android tools are not wanted.

.PARAMETER SkipTablet
    Build only the PC side.

.PARAMETER Yes
    Accept every prompt. For unattended use.
#>
[CmdletBinding()]
param(
    [switch]$SkipTablet,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Deck.Common.ps1"

function Ask([string]$q, [bool]$default = $true) {
    if ($Yes) { return $true }
    return Confirm-Step -Question $q -Default $default
}

<#
    Offers to install a missing tool with winget. Installing software is the user's
    decision, so this always asks and always shows the exact command first.
#>
function Offer-Winget {
    param([string]$Label, [string]$PackageId)

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Step "Install $Label yourself, then run this again."
        return $false
    }
    Write-Step "This can be installed with:  winget install --id $PackageId"
    if (-not (Ask "Install $Label now?")) { return $false }

    & winget install --id $PackageId --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { Write-Bad "winget could not install $Label."; return $false }

    # winget adds to PATH for new processes, not this one.
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('PATH', 'User')
    return $true
}

Write-Head 'Companion Deck - setup'
Write-Host "  $env:COMPUTERNAME  /  Windows $([Environment]::OSVersion.Version)" -ForegroundColor DarkGray

# ------------------------------------------------------------- Windows check

$build = [Environment]::OSVersion.Version.Build
if ($build -lt 17763) {
    Write-Bad "Windows 10 version 1809 (build 17763) or newer is required; this is build $build."
    Write-Step 'Older builds are not supported by .NET 8 and the Windows APIs this uses.'
    exit 1
}
if ($build -lt 19041) {
    Write-Miss "Windows build $build predates 2004: it should run, but the now-playing media card needs 2004 or newer and is untested here."
}
Write-Ok "Windows build $build"

# ---------------------------------------------------------------- PC side

Write-Head 'PC side'

if (Test-DotNetSdk) {
    Write-Ok ".NET SDK found"
} else {
    Write-Miss ".NET 8 SDK is needed to build the host."
    if (-not (Offer-Winget -Label '.NET 8 SDK' -PackageId 'Microsoft.DotNet.SDK.8')) {
        Write-Bad 'Cannot build the host without it. Stopping.'
        exit 1
    }
    if (-not (Test-DotNetSdk)) {
        Write-Bad 'Still cannot see a .NET 8 SDK. Open a new terminal and run setup again.'
        exit 1
    }
    Write-Ok '.NET SDK installed'
}

Stop-DeckHost | Out-Null

Write-Step 'Building the host...'
$hostDir = Join-Path (Get-DeckRoot) 'host\CompanionDeck.Host'
Push-Location $hostDir
try {
    & dotnet build -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
} finally { Pop-Location }

$exe = Get-HostExe
if (-not $exe) { Write-Bad 'Built, but CompanionDeck.Host.exe was not found.'; exit 1 }
Write-Ok "Host built: $($exe.FullName)"

# --------------------------------------------------------------- tablet side

if ($SkipTablet) {
    Write-Head 'Tablet side skipped'
} else {
    Write-Head 'Tablet side'

    $sdk = Find-AndroidSdk
    $jdk = Find-Jdk

    # Two separate needs, and conflating them is what makes this confusing: building the
    # app wants a full SDK plus a JDK, while merely installing a built APK wants nothing
    # but adb. A machine with only adb can still get the app on the tablet.
    if (-not $sdk) {
        Write-Miss 'No Android SDK found, so the app cannot be built here.'
        Write-Step 'For that you need Android Studio, or the command-line tools, and then'
        Write-Step 'to accept the SDK licences once. See the README.'
    }

    if (-not $jdk) {
        Write-Miss 'No JDK 17 or newer found; Gradle needs one to build the app.'
        if (Offer-Winget -Label 'OpenJDK 17' -PackageId 'Microsoft.OpenJDK.17') { $jdk = Find-Jdk }
    }

    if (-not (Find-Adb)) {
        Write-Miss 'No adb, so nothing can be pushed to the tablet.'
        Write-Step 'This is a small download and is all that is needed to install the app.'
        Offer-Winget -Label 'Android platform tools' -PackageId 'Google.PlatformTools' | Out-Null
    }

    $apk = Join-Path (Get-DeckRoot) 'tablet\app\build\outputs\apk\release\app-release.apk'

    if ($jdk -and $sdk) {
        Write-Ok "JDK: $jdk"
        Write-Ok "Android SDK: $sdk"
        Set-LocalProperties -SdkPath $sdk | Out-Null

        Write-Step 'Building the tablet app (first run downloads Gradle, so it is slow)...'
        $env:JAVA_HOME = $jdk
        Push-Location (Join-Path (Get-DeckRoot) 'tablet')
        try {
            & .\gradlew.bat :app:assembleRelease --console=plain -q
            if ($LASTEXITCODE -ne 0) { throw 'Tablet build failed.' }
        } finally { Pop-Location }
        Write-Ok 'Tablet app built'
    } else {
        Write-Miss 'Skipping the tablet build - the tools above are missing.'
    }

    # ------------------------------------------------------------- install

    $adb = Find-Adb
    if ($adb -and (Test-Path $apk)) {
        $devices = @(& $adb devices | Select-Object -Skip 1 |
                     Where-Object { $_ -match '\sdevice$' })
        if ($devices.Count -eq 0) {
            Write-Miss 'No tablet detected over USB.'
            Write-Step 'Turn on Developer options and USB debugging on the tablet, plug it in,'
            Write-Step 'accept the "Allow USB debugging?" prompt, then run setup again.'
            Write-Step "Or copy this file to the tablet and tap it:  $apk"
        } else {
            Write-Step "Installing on $($devices.Count) device(s)..."
            & $adb install -r $apk
            if ($LASTEXITCODE -eq 0) { Write-Ok 'Tablet app installed' }
            else { Write-Miss "adb install failed. Copy the APK across by hand instead: $apk" }
        }
    } elseif (Test-Path $apk) {
        Write-Miss 'No adb, so the app cannot be pushed.'
        Write-Step "Copy this to the tablet and tap it:  $apk"
    }
}

# ------------------------------------------------------------- capabilities

Write-Head 'What this PC can do'
& $exe.FullName --selftest

# ---------------------------------------------------------------- elevation

Write-Head 'Optional: run elevated at logon'
Write-Host '  Display brightness and laptop fan/temperature data are refused by Windows' -ForegroundColor DarkGray
Write-Host '  to an ordinary process. Installing a scheduled task makes those work and' -ForegroundColor DarkGray
Write-Host '  removes the UAC prompt for good.' -ForegroundColor DarkGray
Write-Host ''

if (Ask 'Install the elevated task? (needs one admin prompt)' $false) {
    $installer = Join-Path $PSScriptRoot 'Install-Elevated.ps1'
    if (Test-Elevated) {
        & $installer
    } else {
        Start-Process -FilePath 'powershell' -Verb RunAs -Wait -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$installer`"")
    }
} else {
    Write-Step 'Skipped. Run scripts\Install-Elevated.ps1 later if you change your mind.'
}

# -------------------------------------------------------------------- done

Write-Head 'Setup complete'
Write-Host '  Start it with Deck.cmd, then plug the tablet in over USB.' -ForegroundColor Green
Write-Host "  Log: $env:LOCALAPPDATA\CompanionDeck\host.log" -ForegroundColor DarkGray
Write-Host ''
