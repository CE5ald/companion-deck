<#
.SYNOPSIS
    The setup steps the Companion Deck installer runs after copying files, and undoes on uninstall.

.DESCRIPTION
    Every step is optional and reports plainly. A step that cannot be done (no internet, no tablet plugged
    in) is written to install-summary.txt as a PROBLEM line that the installer shows, and never stops the
    others: the program is installed either way.

      Adb       Downloads Google's platform-tools and uses it only if Google's signature checks out.
      Autostart Registers the logon task that runs the agent with the rights it needs.
      Firewall  Lets the Wi-Fi link through Windows Firewall on private networks.
      PawnIO    Installs the signed driver that lets CPU temperature and fans be read (winget).
      Tablet    Installs the Android app on a tablet that is plugged in.
#>
param(
    [Parameter(Mandatory)][string]$AppDir,
    [switch]$Adb, [switch]$Autostart, [switch]$Firewall, [switch]$PawnIO, [switch]$Tablet,
    [switch]$Remove
)

$ErrorActionPreference = 'Continue'
$TaskName = 'CompanionDeck Host'
$FirewallName = 'Companion Deck'
$Exe = Join-Path $AppDir 'CompanionDeck.Host.exe'
$Summary = Join-Path $AppDir 'install-summary.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say($t)     { $lines.Add($t) }
function Problem($t) { $lines.Add("PROBLEM: $t") }

# The person at the keyboard, not whichever administrator account approved the installer's prompt.
function Get-InteractiveUser {
    try { $u = (Get-CimInstance Win32_ComputerSystem).UserName; if ($u) { return $u } } catch { }
    return "$env:USERDOMAIN\$env:USERNAME"
}

# Only ever this install's own copy: the task, the process and the firewall rule are matched by where the
# program lives, so another copy on the same PC (a second install, a developer's build) is left alone.
function Test-Mine($path) { return [bool]($path -and $path.StartsWith($AppDir, [StringComparison]::OrdinalIgnoreCase)) }
function Get-MyTask {
    $t = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($t -and ($t.Actions | Where-Object { Test-Mine $_.Execute })) { return $t }
    return $null
}
function Stop-MyCopy {
    if (Get-MyTask) { try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { } }
    Get-Process -Name 'CompanionDeck.Host' -ErrorAction SilentlyContinue | Where-Object { Test-Mine $_.Path } |
        ForEach-Object { try { $_ | Stop-Process -Force } catch { } }
}

# ------------------------------------------------------------------ uninstall
if ($Remove) {
    Stop-MyCopy
    if (Get-MyTask) { try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue } catch { } }
    try {
        Get-NetFirewallRule -DisplayName $FirewallName -ErrorAction SilentlyContinue | Where-Object {
            (($_ | Get-NetFirewallApplicationFilter).Program) -eq $Exe } | Remove-NetFirewallRule
    } catch { }
    exit 0
}

# ------------------------------------------------------------------------ ADB
if ($Adb) {
    $pt = Join-Path $AppDir 'platform-tools'
    $zip = Join-Path $env:TEMP 'companiondeck-platform-tools.zip'
    try {
        Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' `
                          -OutFile $zip -UseBasicParsing -TimeoutSec 120
        $stage = Join-Path $env:TEMP 'companiondeck-platform-tools'
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        Expand-Archive $zip $stage -Force

        # Only Google's own build is accepted: the files must be signed by Google LLC and unaltered.
        $bad = @()
        foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') {
            $s = Get-AuthenticodeSignature (Join-Path $stage "platform-tools\$f")
            if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Google LLC') { $bad += $f }
        }
        if ($bad.Count -gt 0) {
            Problem "The download of ADB was not signed by Google ($($bad -join ', ')), so it was not used. USB connection will not work; Wi-Fi still can."
        } else {
            if (Test-Path $pt) { Remove-Item $pt -Recurse -Force }
            Move-Item (Join-Path $stage 'platform-tools') $pt
            Say 'ADB installed (downloaded from Google, signature checked).'
        }
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Problem "Could not download ADB from Google ($($_.Exception.Message)). USB connection needs it: connect to the internet and run 'Set up USB connection' from the Start menu. Wi-Fi works without it."
    }
}

# ------------------------------------------------------------------ autostart
if ($Autostart) {
    try {
        $user = Get-InteractiveUser
        $action = New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $AppDir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -DontStopOnIdleEnd `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
            -MultipleInstances IgnoreNew -StartWhenAvailable
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false }
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
            -Description 'Runs Companion Deck at sign-in with the rights it needs for temperatures, fans, brightness and frame rates.' | Out-Null
        Say "Companion Deck will start when $user signs in."
    } catch {
        Problem "Could not set up start at sign-in ($($_.Exception.Message)). Start it from the Start menu instead."
    }
}

# ------------------------------------------------------------------- firewall
if ($Firewall) {
    try {
        Get-NetFirewallRule -DisplayName $FirewallName -ErrorAction SilentlyContinue | Where-Object {
            (($_ | Get-NetFirewallApplicationFilter).Program) -eq $Exe } | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $FirewallName -Direction Inbound -Program $Exe -Action Allow -Profile Private `
            -Description 'Lets a tablet connect to Companion Deck over Wi-Fi when the cable is not in.' | Out-Null
        Say 'Wi-Fi link allowed through Windows Firewall (private networks only).'
    } catch {
        Problem "Could not add the firewall rule ($($_.Exception.Message)). Wi-Fi may ask for permission the first time."
    }
}

# --------------------------------------------------------------------- PawnIO
if ($PawnIO) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $r = & winget install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements 2>&1
        if ($LASTEXITCODE -eq 0 -or ($r -match 'already installed')) { Say 'PawnIO driver installed.' }
        else { Problem 'The PawnIO driver could not be installed. CPU temperature and fans will say what is missing; run: winget install namazso.PawnIO' }
    } else {
        Problem 'winget is not available, so the PawnIO driver was not installed. Get it from https://pawnio.eu/ if you want CPU temperature and fans.'
    }
}

# --------------------------------------------------------------- tablet install
if ($Tablet) {
    $adbExe = Join-Path $AppDir 'platform-tools\adb.exe'
    $apk = Join-Path $AppDir 'CompanionDeck.apk'
    if (-not (Test-Path $adbExe)) {
        Problem 'The tablet app was not installed because ADB is missing. Run "Install app on tablet" from the Start menu once it is.'
    } else {
        & $adbExe start-server 2>&1 | Out-Null
        $devs = @(& $adbExe devices 2>&1 | Select-String '\tdevice$')
        $unauth = @(& $adbExe devices 2>&1 | Select-String '\tunauthorized$')
        if ($devs.Count -eq 0 -and $unauth.Count -gt 0) {
            Problem 'A tablet is connected but has not allowed this PC. On the tablet, tick "Always allow" in the USB debugging prompt, then run "Install app on tablet" from the Start menu.'
        } elseif ($devs.Count -eq 0) {
            Problem 'No tablet was found, so the app was not installed on it. Plug it in with USB debugging on, then run "Install app on tablet" from the Start menu. (Or copy CompanionDeck.apk to the tablet by hand.)'
        } else {
            $out = & $adbExe install -r $apk 2>&1 | Out-String
            if ($out -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
                # An older build signed with a different key. Same app, so replace it; it pairs again over the cable.
                & $adbExe uninstall com.companiondeck 2>&1 | Out-Null
                $out = & $adbExe install $apk 2>&1 | Out-String
            }
            if ($out -match 'Success') {
                Say 'The Android app is installed on your tablet.'
                & $adbExe shell am start -n com.companiondeck/.MainActivity 2>&1 | Out-Null
            } else {
                Problem "The tablet app could not be installed: $($out.Trim())"
            }
        }
    }
}

$lines | Set-Content -Path $Summary -Encoding UTF8
exit 0
