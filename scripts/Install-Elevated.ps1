<#
.SYNOPSIS
    Registers Companion Deck to start elevated at logon.

.DESCRIPTION
    Three capabilities are refused by Windows to a normal-privilege process:

      - display brightness  (WmiSetBrightness requires elevation)
      - CPU temperature     (the ACPI thermal zone refuses an ordinary caller)
      - fan speeds and thermal mode  (same interface)

    This installs a Scheduled Task that runs the agent with highest privileges at logon.
    A task is used rather than a Windows service because the agent needs the interactive
    desktop session: it synthesises keystrokes for quick actions and reads the media
    session of the logged-in user, neither of which a session-0 service can do.

    Run this ONCE, from an elevated PowerShell. Afterwards the agent starts automatically
    and never prompts for UAC again.

.PARAMETER Uninstall
    Removes the task instead of creating it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Install-Elevated.ps1
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$TaskName = 'CompanionDeck Host'

# ---------------------------------------------------------------- elevation

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host ''
    Write-Host '  This installer must run elevated.' -ForegroundColor Yellow
    Write-Host '  Right-click PowerShell -> Run as administrator, then run:' -ForegroundColor Gray
    Write-Host ''
    Write-Host "      powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor White
    Write-Host ''
    exit 1
}

# ---------------------------------------------------------------- uninstall

if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "  Removed scheduled task '$TaskName'." -ForegroundColor Green
    } else {
        Write-Host "  No task named '$TaskName' was registered." -ForegroundColor Gray
    }
    exit 0
}

# ------------------------------------------------------------------ locate

$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path (Join-Path $root 'host\CompanionDeck.Host') -Recurse -Filter 'CompanionDeck.Host.exe' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $exe) {
    throw "CompanionDeck.Host.exe not found. Build it first with Start-CompanionDeck.cmd."
}

Write-Host ''
Write-Host '  Companion Deck - elevated install' -ForegroundColor Cyan
Write-Host "  Agent: $($exe.FullName)" -ForegroundColor DarkGray
Write-Host ''

# ---------------------------------------------------------------- register

$action = New-ScheduledTaskAction -Execute $exe.FullName -WorkingDirectory $exe.DirectoryName
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

# Highest privileges, in the user's interactive session.
$principalDef = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
                                           -LogonType Interactive -RunLevel Highest

# The agent is long-running and must never be killed for taking too long, throttled as a
# background task, or stopped when the laptop switches to battery.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host '  Replaced the existing task.' -ForegroundColor DarkGray
}

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
                       -Principal $principalDef -Settings $settings `
                       -Description 'Runs the Companion Deck host elevated so it can control display brightness and read fan and thermal data.' | Out-Null

Write-Host "  Registered '$TaskName' (runs at logon, highest privileges)." -ForegroundColor Green

# ------------------------------------------------------------------- start

Get-Process -Name 'CompanionDeck.Host' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3

$running = Get-Process -Name 'CompanionDeck.Host' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host '  Agent is running elevated. Brightness, CPU temperature and fan speeds' -ForegroundColor Green
    Write-Host '  will appear on the tablet once it reconnects.' -ForegroundColor Green
} else {
    Write-Host '  Task registered, but the agent did not start. Check Task Scheduler.' -ForegroundColor Yellow
}
Write-Host ''
Write-Host '  To undo:  powershell -ExecutionPolicy Bypass -File scripts\Install-Elevated.ps1 -Uninstall' -ForegroundColor DarkGray
Write-Host ''
