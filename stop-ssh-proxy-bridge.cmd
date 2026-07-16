@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ssh-proxy-bridge.ps1" stop
if errorlevel 1 (
  echo.
  echo Failed to stop the managed SSH tunnel.
  pause
  exit /b 1
)
