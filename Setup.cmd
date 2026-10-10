@echo off
REM One-time setup for Companion Deck. Double-click this.
REM Checks what is missing, builds the PC host and the tablet app, installs the app.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Setup.ps1" %*
echo.
pause
