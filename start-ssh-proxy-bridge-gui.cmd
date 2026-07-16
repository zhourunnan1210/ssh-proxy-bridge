@echo off
setlocal
set "ROOT=%~dp0"
set "PROJECT=%ROOT%app\SshProxyBridge.App\SshProxyBridge.App.csproj"
set "APP=%ROOT%app\SshProxyBridge.App\bin\Release\net8.0-windows\SshProxyBridge.exe"
set "ASSETS=%ROOT%app\SshProxyBridge.App\obj\project.assets.json"

powershell.exe -NoLogo -NoProfile -NonInteractive -Command "if (Get-Process -Name 'SshProxyBridge' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if not errorlevel 1 (
  echo SSH Proxy Bridge is already running.
  exit /b 0
)

if not exist "%ASSETS%" (
  dotnet restore "%PROJECT%" --nologo
  if errorlevel 1 (
    echo.
    echo Failed to restore SSH Proxy Bridge dependencies.
    pause
    exit /b 1
  )
)

dotnet build "%PROJECT%" --configuration Release --nologo --no-restore
if errorlevel 1 (
  echo.
  echo Failed to build SSH Proxy Bridge.
  pause
  exit /b 1
)

start "" "%APP%"
endlocal
