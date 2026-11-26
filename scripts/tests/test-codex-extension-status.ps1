[CmdletBinding()]
param([string]$ScriptPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ScriptPath) { $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1' }
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0].Message }
foreach ($functionName in @('Get-RemoteCodexAuthenticationScript', 'Get-RemoteCodexExtensionProbeScript',
    'Get-RemoteCodexHomeScript', 'Write-RemoteCodexHomeStatus',
    'Write-RemoteCodexExtensionStatus', 'Test-RemoteCodexAuthentication')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    }, $false)
    if (-not $definition) { throw "Missing Codex status function $functionName." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$bash = if ($env:OS -eq 'Windows_NT') { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' }
    else { (Get-Command bash -CommandType Application).Path }
if (-not [IO.File]::Exists($bash)) { throw 'Bash is required for the offline Codex status fixtures.' }

function Invoke-Ssh($Configuration, [string]$Command, [switch]$UseAlias) {
    if ($script:FailSsh) { return [pscustomobject]@{ ExitCode = 255; Output = @('Fixture SSH failure') } }
    if ($Command -notmatch '^printf %s ([A-Za-z0-9+/=]+) \| base64 -d \| bash$') { throw 'Unexpected SSH command format.' }
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    $bootstrap = @'
fixture_home=$1
if [ -x /usr/bin/cygpath ]; then fixture_home=$(/usr/bin/cygpath -u "$fixture_home"); fi
export HOME="$fixture_home"
unset SHELL CODEX_HOME VSCODE_AGENT_FOLDER VSCODE_EXTENSIONS
case "$2" in
    custom) export VSCODE_EXTENSIONS="$HOME/custom extensions" ;;
esac
command -v node >/dev/null || exit 98
'@
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @((($bootstrap + "`n" + $payload).Replace("`r", '') + "`n# EOF") | & $bash --noprofile --norc -s -- $fixture $script:LocationMode)
    if ($LASTEXITCODE -eq 98) { throw 'Node is required for these offline VS Code registry tests.' }
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Assert-Status([string]$State, [string]$LegacyAuth) {
    $output = @(Test-RemoteCodexAuthentication $null -Quiet -IncludeExtensionStatus 6>&1 |
        ForEach-Object { $_.ToString() })
    $markers = @($output | Where-Object { $_.StartsWith('CODEX_EXTENSION_STATUS:') })
    if ($markers.Count -ne 1 -or $markers[0] -cne "CODEX_EXTENSION_STATUS:$State" -or
        $script:LastRemoteCodexAuthenticationState -cne $LegacyAuth -or
        ($output -join ' ') -match 'fixture-private-token') {
        throw "Expected $State / $LegacyAuth, got $($markers -join ',') / $script:LastRemoteCodexAuthenticationState."
    }
}

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
$fixture = Join-Path $temp "codex-status-fixture-$([Guid]::NewGuid().ToString('N'))"
$extensions = Join-Path $fixture '.vscode-server/extensions'
$extension = Join-Path $extensions 'openai.chatgpt-1.2.3'
$authDirectory = Join-Path $fixture '.codex'
$auth = Join-Path $authDirectory 'auth.json'
$registry = Join-Path $extensions 'extensions.json'
$script:FailSsh = $false
$script:LocationMode = 'default'
try {
    $null = [IO.Directory]::CreateDirectory($extension)
    $null = [IO.Directory]::CreateDirectory($authDirectory)
    [IO.File]::WriteAllText((Join-Path $extension 'package.json'), '{"publisher":"openai","name":"chatgpt"}')
    Assert-Status 'installed:sign-in-required' 'sign-in required'

    [IO.File]::WriteAllText($registry, '[{"identifier":{"id":"openai.chatgpt"},"relativeLocation":"openai.chatgpt-1.2.3"}]')
    [IO.File]::WriteAllText($auth, '{"auth_mode":"chatgpt","OPENAI_API_KEY":null}')
    Assert-Status 'installed:sign-in-required' 'sign-in required'
    [IO.File]::WriteAllText($auth, '{"auth_mode":"chatgpt","tokens":{"access_token":"fixture-private-token"}}')
    Assert-Status 'installed:signed-in' 'ready'
    [IO.File]::Delete($auth)
    $null = [IO.Directory]::CreateDirectory($auth)
    Assert-Status 'installed:unknown' 'unknown'

    $script:LocationMode = 'custom'
    $custom = Join-Path $fixture 'custom extensions'
    $null = [IO.Directory]::CreateDirectory($custom)
    [IO.File]::WriteAllText((Join-Path $custom 'extensions.json'), '[{"identifier":{"id":"openai.chatgpt-preview"}}]')
    Assert-Status 'not-installed:unknown' 'unknown'
    [IO.File]::WriteAllText((Join-Path $custom 'extensions.json'), '[{"identifier":{"id":"openai.chatgpt"},"relativeLocation":"missing-payload"}]')
    Assert-Status 'unknown:unknown' 'unknown'

    $script:FailSsh = $true
    Assert-Status 'unknown:unknown' 'unknown'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (([IO.Path]::GetDirectoryName($resolved) -eq $temp) -and [IO.Directory]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Host 'PASS  Codex extension status detects installed, sign-in, missing and unknown states without emitting credential values.'
