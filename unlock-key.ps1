[CmdletBinding()]
param(
    [string]$Config
)

$ErrorActionPreference = 'Stop'
if (-not $Config) {
    $Config = Join-Path $PSScriptRoot 'config.local.json'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $arguments = @(
        '-NoExit',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-Config', ('"{0}"' -f $Config)
    )
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Normal -ArgumentList $arguments
    return
}

$configuration = Get-Content -Raw -LiteralPath $Config | ConvertFrom-Json
$keyPath = [Environment]::ExpandEnvironmentVariables([string]$configuration.ssh.identityFile)
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
    throw "SSH private key not found: $keyPath"
}

Write-Host 'Enabling Windows OpenSSH Authentication Agent...' -ForegroundColor Cyan
Set-Service -Name 'ssh-agent' -StartupType Automatic
Start-Service -Name 'ssh-agent'

Write-Host ''
Write-Host 'Enter the LOCAL private-key passphrase at the prompt below.' -ForegroundColor Yellow
Write-Host 'The server password is not needed.' -ForegroundColor Yellow
& "$env:SystemRoot\System32\OpenSSH\ssh-add.exe" $keyPath
if ($LASTEXITCODE -ne 0) {
    throw 'ssh-add failed.'
}

Write-Host ''
Write-Host 'Validating key-only server login...' -ForegroundColor Cyan
$target = "$($configuration.ssh.user)@$($configuration.ssh.host)"
& "$env:SystemRoot\System32\OpenSSH\ssh.exe" `
    -o BatchMode=yes `
    -o PasswordAuthentication=no `
    -o IdentitiesOnly=yes `
    -i $keyPath `
    -p ([string]$configuration.ssh.port) `
    $target `
    'printf CODEX_REMOTE_KEY_OK'

if ($LASTEXITCODE -ne 0) {
    throw 'The key was unlocked, but key-only server login still failed.'
}

Write-Host ''
Write-Host '[PASS] SSH key is loaded and key-only server login succeeded.' -ForegroundColor Green
Read-Host 'Press Enter to close this window'
