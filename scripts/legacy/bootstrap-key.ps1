[CmdletBinding()]
param(
    [string]$Config
)

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Config) {
    $Config = Join-Path $root 'config.local.json'
}

Write-Host 'This step opens an interactive SSH password prompt.' -ForegroundColor Yellow
Write-Host 'The password is sent only to ssh.exe and is never stored by this project.' -ForegroundColor Yellow
& (Join-Path $root 'ssh-proxy-bridge.ps1') bootstrap-key -Config $Config
Write-Host ''
Write-Host 'Bootstrap finished. You can close this window and return to SSH Proxy Bridge.' -ForegroundColor Green
