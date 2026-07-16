@echo off
setlocal
if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ssh-proxy-bridge.ps1" start
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ssh-proxy-bridge.ps1" start -RemoteWorkspace "%~1"
)
if errorlevel 1 (
  echo.
  echo SSH Proxy Bridge failed to start.
  pause
  exit /b 1
)
