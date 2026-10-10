@echo off
REM Everyday launcher for Companion Deck.
REM Builds the host if anything changed, then runs it. Close the window to stop.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Start-CompanionDeck.ps1" %*
if errorlevel 1 pause
