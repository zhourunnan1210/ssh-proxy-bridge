[CmdletBinding()]
param([string]$ScriptPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ScriptPath) { $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1' }
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0].Message }
foreach ($functionName in @('Test-RemoteApplicationNetwork', 'Test-RemoteDirectCodex')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    }, $false)
    if (-not $definition) { throw "Missing network probe $functionName." }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$bash = if ($env:OS -eq 'Windows_NT') { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' }
    else { (Get-Command bash -CommandType Application).Path }
if (-not [IO.File]::Exists($bash)) { throw 'Bash is required for the offline Codex account access fixture.' }

function Invoke-Ssh($Configuration, [string]$Command, [switch]$UseAlias, [switch]$ReadOnly) {
    if ($script:CheckExplicitDirect -and (-not $ReadOnly -or $UseAlias)) {
        throw 'An explicit direct check did not use read-only SSH.'
    }
    if ($Command -notmatch '^printf %s ([A-Za-z0-9+/=]+) \| base64 -d \| bash$') { throw 'Unexpected SSH probe command.' }
    $payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    $payload = $payload.Replace('/proc/[0-9]*', '"$HOME/fixture-proc"/[0-9]*')
    $setup = @'
set -e
fixture_home=$1
if [ -x /usr/bin/cygpath ]; then fixture_home=$(/usr/bin/cygpath -u "$fixture_home"); fi
export HOME="$fixture_home" HISTFILE=/dev/null PATH="$fixture_home/bin:/usr/bin:/bin"
export FIXTURE_ACCOUNT_CODE=$2 FIXTURE_ACCOUNT_ROUTE=$3
chmod +x "$HOME/bin/curl"
[ "$(command -v curl)" = "$HOME/bin/curl" ]
set +e
'@
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @((($setup + "`n" + $payload).Replace("`r", '') + "`n# EOF") |
        & $bash --noprofile --norc -s -- $fixture $script:AccountCode $script:AccountRoute)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Assert-Access([string]$Access, [string]$NetworkMarker, [bool]$ExpectedReady) {
    $output = @(Test-RemoteApplicationNetwork $configuration -Quiet 6>&1 | ForEach-Object { $_.ToString() })
    $printed = @($output | Where-Object { $_.StartsWith('CODEX_ACCOUNT_ACCESS:') })
    if ($printed.Count -ne 1 -or $printed[0] -cne "CODEX_ACCOUNT_ACCESS:$Access" -or
        $script:LastApplicationNetworkMarker -cne $NetworkMarker -or $output[-1] -ne [string]$ExpectedReady) {
        throw "Expected $Access and $NetworkMarker, got $($printed -join ',') and $script:LastApplicationNetworkMarker."
    }
}

function Write-ProcessFixture([string]$PidName, [string[]]$Variables) {
    $directory = Join-Path $fixture "fixture-proc/$PidName"
    $null = [IO.Directory]::CreateDirectory($directory)
    $command = "/fixture/custom extensions/openai.chatgpt-1.2.3/bin/linux-x86_64/codex$([char]0)app-server$([char]0)"
    [IO.File]::WriteAllBytes((Join-Path $directory 'cmdline'), [Text.Encoding]::UTF8.GetBytes($command))
    $environment = ($Variables -join [char]0) + [char]0
    [IO.File]::WriteAllBytes((Join-Path $directory 'environ'), [Text.Encoding]::UTF8.GetBytes($environment))
}

function Assert-ExplicitDirect([string]$ExpectedPrimary, [string]$ExpectedAccount, [bool]$ExpectedReady) {
    $output = @(Test-RemoteDirectCodex $configuration -Quiet -ReportProbeResult 6>&1 |
        ForEach-Object { $_.ToString() })
    if ($ExpectedPrimary -cnotin $output -or $ExpectedAccount -cnotin $output -or
        $output[-1] -ne [string]$ExpectedReady) {
        throw "The explicit direct check did not report $ExpectedPrimary and $ExpectedAccount."
    }
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) "codex-account-fixture-$([Guid]::NewGuid().ToString('N'))"
$configuration = [pscustomobject]@{ ssh = [pscustomobject]@{ remoteProxyHost = '127.0.0.1'; remoteProxyPort = 17897 } }
$script:CheckExplicitDirect = $false
$fakeCurl = @'
#!/usr/bin/env bash
clean_config=0
[ "$1" = '-q' ] && clean_config=1
route=direct
bypass=unset
target=''
while [ "$#" -gt 0 ]; do
    case "$1" in
        --proxy) route=$2; shift ;;
        --noproxy) bypass=$2; shift ;;
        https://*) target=$1 ;;
    esac
    shift
done
case "$target" in
    https://chatgpt.com/backend-api/codex/responses) printf '401' ;;
    https://chatgpt.com/backend-api/wham/accounts/check)
        : >"$HOME/account-called"
        if [ "$clean_config" = '1' ] && [ "$route" = "$FIXTURE_ACCOUNT_ROUTE" ] &&
            { { [ "$route" = direct ] && [ "$bypass" = '*' ]; } || { [ "$route" != direct ] && [ -z "$bypass" ]; }; }; then
            printf '%s' "$FIXTURE_ACCOUNT_CODE"
        else
            printf '599'
        fi
        ;;
    *) printf '000'; exit 1 ;;
esac
'@
try {
    $null = [IO.Directory]::CreateDirectory((Join-Path $fixture 'bin'))
    [IO.File]::WriteAllText((Join-Path $fixture 'bin/curl'), $fakeCurl.Replace("`r", '') + "`n")
    $bashrc = Join-Path $fixture '.bashrc'
    $script:AccountCode = '401'
    $script:AccountRoute = 'direct'
    [IO.File]::WriteAllText($bashrc, "export SSH_PROXY_BRIDGE_ROUTE=direct`n")
    Assert-Access 'reachable:direct:401:shell' 'APPLICATION_NETWORK_READY:direct:no-active-codex' $true

    $script:AccountCode = '403'
    $script:AccountRoute = 'http://127.0.0.1:17897'
    [IO.File]::WriteAllText($bashrc, "export SSH_PROXY_BRIDGE_ROUTE=proxy HTTPS_PROXY=$script:AccountRoute`n")
    Assert-Access 'unreachable:proxy:403:shell' 'APPLICATION_NETWORK_READY:proxy:no-active-codex' $true
    $script:AccountCode = '000'
    Assert-Access 'unreachable:proxy:none:shell' 'APPLICATION_NETWORK_READY:proxy:no-active-codex' $true

    [IO.File]::WriteAllText($bashrc, "# No route configured`n")
    Assert-Access 'unknown:unknown:none:unknown' 'APPLICATION_NETWORK_ROUTE_MISSING' $false
    $script:AccountCode = '401'
    $script:AccountRoute = 'http://127.0.0.1:17901'
    Write-ProcessFixture '1234' @('SSH_PROXY_BRIDGE_ROUTE=proxy', "HTTPS_PROXY=$script:AccountRoute")
    Assert-Access 'reachable:proxy:401:process' 'APPLICATION_NETWORK_ROUTE_MISSING' $false

    Write-ProcessFixture '5678' @('SSH_PROXY_BRIDGE_ROUTE=proxy', "HTTPS_PROXY=$script:AccountRoute", 'NO_PROXY=chatgpt.com')
    Assert-Access 'unknown:unknown:none:process' 'APPLICATION_NETWORK_ROUTE_MISSING' $false

    $script:CheckExplicitDirect = $true
    $script:AccountRoute = 'direct'
    $script:AccountCode = '403'
    Assert-ExplicitDirect 'CODEX_DIRECT_FAILED:403' 'CODEX_DIRECT_ACCOUNT:unreachable:403' $false
    $script:AccountCode = '401'
    Assert-ExplicitDirect 'CODEX_DIRECT_OK:401:attempt-1' 'CODEX_DIRECT_ACCOUNT:reachable:401' $true

    $script:CheckExplicitDirect = $false
    $accountCall = Join-Path $fixture 'account-called'
    [IO.File]::Delete($accountCall)
    $script:AccountCode = '403'
    if (-not (Test-RemoteDirectCodex $configuration -Quiet) -or [IO.File]::Exists($accountCall)) {
        throw 'Automatic route selection unexpectedly probed or gated on the account endpoint.'
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
    if (([IO.Path]::GetDirectoryName($resolved) -eq $temp) -and [IO.Directory]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Host 'PASS  Codex account reachability is independent of response API or login readiness and uses the observed process route.'
Write-Host 'PASS  Explicit direct checks require both endpoints without changing automatic route selection.'
