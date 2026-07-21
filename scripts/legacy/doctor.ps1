[CmdletBinding()]
param(
    [string]$Config
)

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Config) {
    $Config = Join-Path $root 'config.local.json'
}

& (Join-Path $root 'ssh-proxy-bridge.ps1') doctor -Config $Config
