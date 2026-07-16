[CmdletBinding()]
param(
    [string]$Config
)

if (-not $Config) {
    $Config = Join-Path $PSScriptRoot 'config.local.json'
}

& (Join-Path $PSScriptRoot 'ssh-proxy-bridge.ps1') doctor -Config $Config
