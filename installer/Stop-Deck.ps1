# Stops Companion Deck. The sign-in task is ended through the scheduler, which an ordinary user may do
# even though the program itself runs with higher rights.
if (Get-ScheduledTask -TaskName 'CompanionDeck Host' -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName 'CompanionDeck Host' }
Get-Process -Name 'CompanionDeck.Host' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
