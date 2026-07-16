@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ssh-proxy-bridge.ps1" status
echo.
pause
