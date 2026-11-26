[CmdletBinding()]
param(
    [string]$ScriptPath = '',
    [string]$BashPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1'
}
if ([string]::IsNullOrWhiteSpace($BashPath)) {
    $BashPath = if ($env:OS -eq 'Windows_NT') {
        Join-Path $env:ProgramFiles 'Git/bin/bash.exe'
    }
    else {
        (Get-Command bash -CommandType Application).Path
    }
}
if (-not [IO.File]::Exists($BashPath)) {
    throw 'Bash was not found. Specify the full path with -BashPath.'
}

$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
    throw "Could not parse the bridge script: $($errors[0].Message)"
}
$probe = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Test-RemoteApplicationNetwork'
}, $false)
if (-not $probe) {
    throw 'The remote application network probe was not found.'
}
. ([scriptblock]::Create($probe.Extent.Text))

function Invoke-Ssh($Configuration, [string]$Command, [switch]$UseAlias) {
    if ($Command -notmatch '^printf %s ([A-Za-z0-9+/=]+) \| base64 -d \| bash$') {
        throw 'Unexpected command from the remote application network probe.'
    }
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    $setup = @'
set -e
fixture_home=$1
if [ -x /usr/bin/cygpath ]; then
    fixture_home=$(/usr/bin/cygpath -u "$fixture_home")
fi
export HOME="$fixture_home" HISTFILE=/dev/null
export PATH="$HOME/bin:/usr/bin:/bin"
export SSH_PROXY_BRIDGE_ROUTE=proxy HTTPS_PROXY=http://127.0.0.1:17897
chmod +x "$HOME/bin/curl"
[ "$(command -v curl)" = "$HOME/bin/curl" ]
set +e
'@
    $inputScript = $setup.Replace("`r", '') + "`n" + $payload + "`n# End of test input"
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @($inputScript | & $BashPath --noprofile --norc -s -- $fixtureHome)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Assert-NetworkProbe([string]$Bashrc, [string]$Marker, [bool]$ExpectedReady) {
    [IO.File]::WriteAllText($bashrcPath, $Bashrc.Replace("`r", '') + "`n")
    $ready = Test-RemoteApplicationNetwork $configuration -Quiet -SuppressMarkers
    if ($ready -ne $ExpectedReady -or $script:LastApplicationNetworkMarker -ne $Marker) {
        throw "Expected $Marker (ready=$ExpectedReady), got $($script:LastApplicationNetworkMarker) (ready=$ready)."
    }
}

$configuration = [pscustomobject]@{
    ssh = [pscustomobject]@{ remoteProxyHost = '127.0.0.1'; remoteProxyPort = 17897 }
}
$fixtureHome = Join-Path ([IO.Path]::GetTempPath()) "ssh-proxy-bridge-shell-$([Guid]::NewGuid().ToString('N'))"
$fixtureBin = Join-Path $fixtureHome 'bin'
$bashrcPath = Join-Path $fixtureHome '.bashrc'
$curlPath = Join-Path $fixtureBin 'curl'
$fakeCurl = @'
#!/usr/bin/env bash
for argument in "$@"; do
    if [ "$argument" = 'https://chatgpt.com/backend-api/codex/responses' ]; then
        printf '401'
        exit 0
    fi
done
printf '000'
exit 1
'@
try {
    $null = [IO.Directory]::CreateDirectory($fixtureBin)
    [IO.File]::WriteAllText($curlPath, $fakeCurl.Replace("`r", '') + "`n")

    $chattyBashrc = @'
printf 'Startup text __SSH_PROXY_BRIDGE_SHELL_STATE__:direct:\nExtra startup text'
export SSH_PROXY_BRIDGE_ROUTE=proxy
export HTTPS_PROXY=http://127.0.0.1:17897
'@
    Assert-NetworkProbe $chattyBashrc 'APPLICATION_NETWORK_READY:proxy:no-active-codex' $true
    Write-Host 'PASS  Startup output does not obscure the proxy route or truncate the proxy URL.'

    $directBashrc = @'
printf 'Startup text without a newline'
export SSH_PROXY_BRIDGE_ROUTE=direct
'@
    Assert-NetworkProbe $directBashrc 'APPLICATION_NETWORK_READY:direct:no-active-codex' $true
    Write-Host 'PASS  Startup output does not obscure the direct route with an empty proxy URL.'

    Assert-NetworkProbe "printf 'No route configured\n'" 'APPLICATION_NETWORK_ROUTE_MISSING' $false
    Write-Host 'PASS  A route inherited from the outer process does not pass shell validation.'

    $mismatchedBashrc = $chattyBashrc.Replace(':17897', ':17898')
    Assert-NetworkProbe $mismatchedBashrc 'APPLICATION_NETWORK_SHELL_MISMATCH:proxy' $false
    Write-Host 'PASS  A mismatched proxy URL still fails shell validation.'
}
finally {
    Remove-Item -LiteralPath $curlPath, $bashrcPath -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixtureBin -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixtureHome -ErrorAction SilentlyContinue
}
exit 0
