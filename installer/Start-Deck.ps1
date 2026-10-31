# Starts Companion Deck. Uses the sign-in task when there is one, so it runs with the rights it needs;
# otherwise starts the program directly.
$app = Split-Path -Parent $PSScriptRoot
$task = Get-ScheduledTask -TaskName 'CompanionDeck Host' -ErrorAction SilentlyContinue
if ($task) {
    if (-not (Get-Process -Name 'CompanionDeck.Host' -ErrorAction SilentlyContinue)) { Start-ScheduledTask -TaskName 'CompanionDeck Host' }
} elseif (-not (Get-Process -Name 'CompanionDeck.Host' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath (Join-Path $app 'CompanionDeck.Host.exe') -WorkingDirectory $app -WindowStyle Hidden
}
