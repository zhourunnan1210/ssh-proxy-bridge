@echo off
setlocal
set "ROOT=%~dp0"
set "APP=%ROOT%SshProxyBridge.exe"

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$app='%APP%'; if (-not (Test-Path -LiteralPath $app)) { exit 1 }; $appTime=(Get-Item -LiteralPath $app).LastWriteTimeUtc; $latest=Get-ChildItem -LiteralPath '%ROOT%app\SshProxyBridge.App','%ROOT%app\SshProxyBridge.Core' -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1; if ($latest -and $latest.LastWriteTimeUtc -gt $appTime) { exit 1 }; exit 0"
set "GUI_CURRENT=%ERRORLEVEL%"

if not "%GUI_CURRENT%"=="0" (
  echo The local GUI is missing or older than the source. Building the current version...
  powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$app=[IO.Path]::GetFullPath('%APP%'); Get-Process -Name 'SshProxyBridge' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app } | Stop-Process -Force"
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\build\publish-portable.ps1"
  if errorlevel 1 (
    echo.
    echo Failed to build the current SSH Proxy Bridge GUI.
    pause
    exit /b 1
  )
)

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$app=[IO.Path]::GetFullPath('%APP%'); $running=Get-Process -Name 'SshProxyBridge' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app }; if ($running) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
  echo SSH Proxy Bridge is already running.
  exit /b 0
)

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$app=[IO.Path]::GetFullPath('%APP%'); Get-Process -Name 'SshProxyBridge' -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $app } | Stop-Process -Force"

start "" "%APP%"
endlocal
