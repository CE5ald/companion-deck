<#
    Shared tool discovery for the Companion Deck scripts.

    Nothing here assumes a path. Every build tool is looked for in the places its own
    installer actually puts it, in the order of most-likely-correct, because the one thing
    a machine other than the author's is guaranteed to have is a different layout.

    Dot-source it:  . "$PSScriptRoot\Deck.Common.ps1"
#>

Set-StrictMode -Version Latest

$script:DeckRoot = Split-Path -Parent $PSScriptRoot
$script:DeckTaskName = 'CompanionDeck Host'

function Get-DeckRoot { $script:DeckRoot }
function Get-DeckTaskName { $script:DeckTaskName }

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Ok($msg) { Write-Host "  [ok] $msg" -ForegroundColor Green }
function Write-Miss($msg) { Write-Host "  [--] $msg" -ForegroundColor Yellow }
function Write-Bad($msg) { Write-Host "  [!!] $msg" -ForegroundColor Red }
function Write-Head($msg) { Write-Host ''; Write-Host "  $msg" -ForegroundColor Cyan; Write-Host '  ---------------------------------------------' -ForegroundColor DarkGray }

<#
    First existing path from a list, or $null. Used instead of a chain of Test-Path ifs so
    adding a new install location is one line.
#>
function Get-FirstPath {
    param([string[]]$Candidates)
    foreach ($c in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        try { if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path } } catch { }
    }
    return $null
}

function Find-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return Get-FirstPath @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    )
}

<#
    True when an SDK new enough to build the host is present. The runtime alone is not
    enough to build from source, which is why this asks for SDKs specifically.
#>
function Test-DotNetSdk {
    $dotnet = Find-DotNet
    if (-not $dotnet) { return $false }
    try {
        $sdks = & $dotnet --list-sdks 2>$null
        foreach ($line in $sdks) {
            if ($line -match '^(\d+)\.') { if ([int]$matches[1] -ge 8) { return $true } }
        }
    } catch { }
    return $false
}

function Find-Adb {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $roots = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT,
               "$env:LOCALAPPDATA\Android\Sdk",
               "$env:ProgramFiles\Android\Android Studio\platform-tools",
               "$env:USERPROFILE\AppData\Local\Android\Sdk") | Where-Object { $_ }

    $candidates = @()
    foreach ($r in $roots) {
        $candidates += (Join-Path $r 'platform-tools\adb.exe')
        $candidates += (Join-Path $r 'adb.exe')
    }
    # winget's Google.PlatformTools lands in its own package root.
    $candidates += "$env:LOCALAPPDATA\Microsoft\WinGet\Links\adb.exe"
    return Get-FirstPath $candidates
}

# The JDK range this Gradle can run on. Gradle refuses to start on a JDK newer than it
# knows about, with the opaque "Unsupported class file major version NN", so a JDK has to
# be version-checked rather than merely found. Android Studio now bundles JDK 25, which is
# exactly the trap: it is the most obvious JDK on the machine and Gradle cannot use it.
$script:JdkMin = 17
$script:JdkMax = 23

<#
    Major Java version of a JDK directory, or 0 if it is not one.
    Reads the release file first - it is a cheap text read - and only falls back to running
    java -version, which costs a process launch per candidate.
#>
function Get-JdkMajor {
    param([Parameter(Mandatory)][string]$JdkHome)

    if (-not (Test-Path -LiteralPath (Join-Path $JdkHome 'bin\java.exe'))) { return 0 }

    $release = Join-Path $JdkHome 'release'
    if (Test-Path -LiteralPath $release) {
        $line = Select-String -LiteralPath $release -Pattern '^JAVA_VERSION="?([0-9]+)' -ErrorAction SilentlyContinue |
                Select-Object -First 1
        if ($line) { return [int]$line.Matches[0].Groups[1].Value }
    }

    try {
        $out = & (Join-Path $JdkHome 'bin\java.exe') -version 2>&1 | Out-String
        if ($out -match 'version "(?:1\.)?([0-9]+)') { return [int]$matches[1] }
    } catch { }
    return 0
}

<#
    A JDK this Gradle can actually build with.

    Prefers the lowest usable major version rather than the newest: the project compiles to
    Java 17 bytecode, and a lower JDK is the one least likely to trip a toolchain warning.
    Android Studio's bundled runtime is checked but wins nothing for being bundled.
#>
function Find-Jdk {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += $env:JAVA_HOME }
    $candidates += @(
        "$env:ProgramFiles\Android\Android Studio\jbr",
        "$env:LOCALAPPDATA\Programs\Android Studio\jbr",
        "$env:ProgramFiles\Android\Android Studio\jre"
    )

    foreach ($pattern in @("$env:ProgramFiles\Java\jdk*", "$env:ProgramFiles\Eclipse Adoptium\jdk*",
                           "$env:ProgramFiles\Microsoft\jdk*", "$env:ProgramFiles\Zulu\zulu*",
                           "$env:ProgramFiles\Amazon Corretto\jdk*", "$env:ProgramFiles\BellSoft\*")) {
        foreach ($f in (Get-ChildItem -Path $pattern -Directory -ErrorAction SilentlyContinue)) {
            $candidates += $f.FullName
        }
    }

    $cmd = Get-Command java -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += (Split-Path -Parent (Split-Path -Parent $cmd.Source)) }

    $usable = @()
    foreach ($c in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        $major = Get-JdkMajor -JdkHome $c
        if ($major -ge $script:JdkMin -and $major -le $script:JdkMax) {
            $usable += [pscustomobject]@{ Path = $c; Major = $major }
        }
    }

    if ($usable.Count -eq 0) { return $null }
    return ($usable | Sort-Object Major, Path | Select-Object -First 1).Path
}

<#
    Explains a JDK failure, which otherwise surfaces as a Gradle stack trace. Separate from
    Find-Jdk so the message can name the version that was rejected and why.
#>
function Get-JdkProblem {
    $newest = 0
    foreach ($c in @($env:JAVA_HOME, "$env:ProgramFiles\Android\Android Studio\jbr") | Where-Object { $_ }) {
        $m = Get-JdkMajor -JdkHome $c
        if ($m -gt $newest) { $newest = $m }
    }
    if ($newest -gt $script:JdkMax) {
        return "Found Java $newest, but this Gradle supports up to Java $script:JdkMax. " +
               "A JDK $script:JdkMin install is needed alongside it."
    }
    return "No JDK between $script:JdkMin and $script:JdkMax was found."
}

<#
    A *full* Android SDK, meaning one Gradle can compile against.

    Deliberately not derived from wherever adb happens to live: the standalone
    platform-tools package is enough to install an APK and contains no platforms or
    build-tools at all, so treating its folder as an SDK root makes Gradle fail with a
    confusing "SDK platform not found" instead of an honest "no SDK".
#>
function Find-AndroidSdk {
    $roots = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT,
               "$env:LOCALAPPDATA\Android\Sdk",
               "$env:ProgramFiles\Android\Android Studio\Sdk",
               "$env:USERPROFILE\Android\Sdk") | Where-Object { $_ }

    foreach ($r in $roots) {
        if (Test-Path -LiteralPath (Join-Path $r 'platforms')) {
            return (Resolve-Path -LiteralPath $r).Path
        }
    }
    return $null
}

function Get-HostExe {
    $dir = Join-Path (Get-DeckRoot) 'host\CompanionDeck.Host'
    return Get-ChildItem -Path $dir -Recurse -Filter 'CompanionDeck.Host.exe' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

<#
    Stops whatever copy of the agent is running, including one owned by the elevated
    scheduled task, so a build can replace the binary. An unelevated shell cannot kill an
    elevated process, which is why the task is stopped through the scheduler instead.
#>
function Stop-DeckHost {
    $task = Get-ScheduledTask -TaskName (Get-DeckTaskName) -ErrorAction SilentlyContinue
    if ($task) {
        Stop-ScheduledTask -TaskName (Get-DeckTaskName) -ErrorAction SilentlyContinue
        $waited = 0
        while ((Get-Process CompanionDeck.Host -ErrorAction SilentlyContinue) -and $waited -lt 20) {
            Start-Sleep -Milliseconds 300; $waited++
        }
        return $true
    }
    Get-Process CompanionDeck.Host -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    return $false
}

<#
    Asks a yes/no question. Returns $Default when there is no console to ask on, so the
    scripts stay usable from CI or a non-interactive shell.
#>
function Confirm-Step {
    param([string]$Question, [bool]$Default = $true)
    if ([Console]::IsInputRedirected) { return $Default }
    $hint = if ($Default) { 'Y/n' } else { 'y/N' }
    while ($true) {
        Write-Host "  $Question [$hint] " -ForegroundColor White -NoNewline
        $a = Read-Host
        if ([string]::IsNullOrWhiteSpace($a)) { return $Default }
        switch ($a.Trim().ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
        }
    }
}

<#
    Writes tablet\local.properties, which the Android Gradle plugin needs in order to find
    the SDK. It is deliberately gitignored - it is a machine-local path - so every fresh
    clone has to have it generated.
#>
function Set-LocalProperties {
    param([Parameter(Mandatory)][string]$SdkPath)
    $file = Join-Path (Get-DeckRoot) 'tablet\local.properties'
    $escaped = $SdkPath -replace '\\', '\\\\' -replace ':', '\:'
    Set-Content -LiteralPath $file -Value "sdk.dir=$escaped" -Encoding ASCII
    return $file
}
