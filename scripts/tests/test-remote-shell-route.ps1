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
foreach ($name in @('Test-RemoteApplicationNetwork', 'Install-RemoteProxyEnvironment')) {
    $function = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if (-not $function) { throw "Bridge function $name was not found." }
    . ([scriptblock]::Create($function.Extent.Text))
}

function Invoke-FixtureBash([string]$Payload) {
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
exec 2>&1
'@
    $inputScript = $setup.Replace("`r", '') + "`n" + $payload + "`n# End of test input"
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @($inputScript | & $BashPath --noprofile --norc -s -- $fixtureHome)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Invoke-Ssh($Configuration, [string]$Command, [switch]$UseAlias) {
    if ($Command -notmatch '^printf %s ([A-Za-z0-9+/=]+) \| base64 -d \| bash$') {
        throw 'Unexpected command from the remote application network probe.'
    }
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    return Invoke-FixtureBash $payload
}

function Assert-NetworkProbe([string]$Bashrc, [string]$Marker, [bool]$ExpectedReady) {
    [IO.File]::WriteAllText($bashrcPath, $Bashrc.Replace("`r", '') + "`n")
    $ready = Test-RemoteApplicationNetwork $configuration -Quiet -SuppressMarkers
    if ($ready -ne $ExpectedReady -or $script:LastApplicationNetworkMarker -ne $Marker) {
        throw "Expected $Marker (ready=$ExpectedReady), got $($script:LastApplicationNetworkMarker) (ready=$ready)."
    }
}

function Test-SshLogin($Configuration, [switch]$Quiet) { return $true }
function Get-StatePath([string]$Name) { return Join-Path $fixtureHome $Name }
function Write-Utf8NoBom([string]$Path, [string]$Content) { [IO.File]::WriteAllText($Path, $Content) }
function Write-Pass([string]$Message) { }

$configuration = [pscustomobject]@{
    ssh = [pscustomobject]@{ remoteProxyHost = '127.0.0.1'; remoteProxyPort = 17897 }
}
$fixtureHome = Join-Path ([IO.Path]::GetTempPath()) "ssh-proxy-bridge-shell-$([Guid]::NewGuid().ToString('N'))"
$fixtureBin = Join-Path $fixtureHome 'bin'
$dotFiles = Join-Path $fixtureHome 'dotfiles'
$linkTarget = Join-Path $dotFiles 'init shell.sh'
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

    $legacyProxy = @'
timeout() { return 0; }
# user settings outside the managed block
export CUSTOM_SETTING=retained
# >>> codex-remote-proxy >>>
if timeout 1 bash -c '</dev/tcp/127.0.0.1/17897' 2>/dev/null; then
    export HTTPS_PROXY=http://127.0.0.1:17897
    export SSH_PROXY_BRIDGE_ROUTE=proxy
fi
# <<< codex-remote-proxy <<<
'@
    Assert-NetworkProbe $legacyProxy 'APPLICATION_NETWORK_READY:proxy:no-active-codex' $true
    if (-not $script:LastManagedProxyGuarded) { throw 'Old managed proxy guard was not detected.' }

    Install-RemoteProxyEnvironment $configuration 'proxy'
    $updatedProxy = [IO.File]::ReadAllText($bashrcPath).Replace('timeout() { return 0; }', 'timeout() { return 1; }')
    if (-not $updatedProxy.Contains('export CUSTOM_SETTING=retained') -or
            -not $updatedProxy.Contains('export ALL_PROXY="$HTTP_PROXY"') -or $updatedProxy.Contains('/dev/tcp/')) {
        throw 'Proxy migration did not preserve user settings and persist the loopback proxy.'
    }
    Assert-NetworkProbe $updatedProxy 'APPLICATION_NETWORK_READY:proxy:no-active-codex' $true
    if ($script:LastManagedProxyGuarded) { throw 'Migrated proxy configuration was mistaken for the legacy guard.' }
    Write-Host 'PASS  The proxy route survives a down listener at shell startup and the migration preserves user settings.'

    $start = '# >>> codex-remote-proxy >>>'
    $end = '# <<< codex-remote-proxy <<<'
    foreach ($markers in @(@($start), @($end), @($start, $start, $end, $end))) {
        $broken = (@('export USER_BEFORE=keep') + $markers + @('export USER_AFTER=keep', '')) -join "`n"
        [IO.File]::WriteAllText($bashrcPath, $broken)
        $failure = ''
        try { Install-RemoteProxyEnvironment $configuration 'proxy' }
        catch { $failure = $_.Exception.Message }
        if (-not $failure.Contains('Invalid managed proxy markers') -or [IO.File]::ReadAllText($bashrcPath) -cne $broken) {
            throw 'Invalid managed markers were accepted or the original user file changed.'
        }
    }
    Write-Host 'PASS  Unbalanced and nested managed markers are rejected without changing the original file.'

    [IO.File]::WriteAllText($bashrcPath, 'export CUSTOM_SETTING=retained' + "`n")
    $previousMsys = $env:MSYS
    try {
        if ($env:OS -eq 'Windows_NT') { $env:MSYS = 'winsymlinks:sys' }
        $linked = Invoke-FixtureBash @'
set -e
mkdir -- "$HOME/dotfiles"
mv -- "$HOME/.bashrc" "$HOME/dotfiles/init shell.sh"
ln -s -- 'dotfiles/init shell.sh' "$HOME/.bashrc"
[ -L "$HOME/.bashrc" ]
'@
    }
    finally { $env:MSYS = $previousMsys }
    if ($linked.ExitCode -ne 0) { throw "Could not create shell symlink fixture: $($linked.Output -join ' ')" }

    Install-RemoteProxyEnvironment $configuration 'direct'
    $preserved = Invoke-FixtureBash @'
[ -L "$HOME/.bashrc" ] &&
    [ "$(readlink -- "$HOME/.bashrc")" = 'dotfiles/init shell.sh' ] &&
    grep -Fqx 'export CUSTOM_SETTING=retained' "$HOME/dotfiles/init shell.sh" &&
    grep -Fqx "export SSH_PROXY_BRIDGE_ROUTE='direct'" "$HOME/dotfiles/init shell.sh"
'@
    if ($preserved.ExitCode -ne 0) { throw 'The symlink or the target user settings were replaced.' }
    Write-Host 'PASS  Installing updates an existing regular symlink target while preserving the link and user content.'
}
finally {
    Remove-Item -LiteralPath $curlPath, $bashrcPath, $linkTarget, (Join-Path $fixtureHome 'application-route.intent') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $dotFiles -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixtureBin -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixtureHome -ErrorAction SilentlyContinue
}
exit 0
