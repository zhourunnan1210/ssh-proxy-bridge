[CmdletBinding()]
param([string]$ScriptPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ScriptPath) { $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1' }
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0].Message }
foreach ($name in @('Get-RemoteCodexHomeScript', 'Write-RemoteCodexHomeStatus')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if (-not $definition) { throw "Missing Codex home function $name." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$bash = if ($env:OS -eq 'Windows_NT') { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' }
    else { (Get-Command bash -CommandType Application).Path }
if (-not [IO.File]::Exists($bash)) { throw 'Bash is required for the offline Codex home fixtures.' }
$source = (Get-RemoteCodexHomeScript).Replace('for proc in /proc/[0-9]*;', 'for proc in "$HOME"/fixture-proc/[0-9]*;')
if ($source -ceq (Get-RemoteCodexHomeScript)) { throw 'Cannot isolate the Codex process scan from the live system.' }

$bootstrap = @'
set -eu
fixture_home=$1
if [ -x /usr/bin/cygpath ]; then fixture_home=$(/usr/bin/cygpath -u "$fixture_home"); fi
export HOME="$fixture_home"
unset CODEX_HOME SHELL
'@
$cases = @'
expect_home() {
    local mode=$1 expected=$2 actual
    actual=$(probe_codex_home "$mode")
    if [ "$actual" != "CODEX_HOME_STATUS:$expected" ]; then
        printf 'Expected %s, got %s\n' "$expected" "$actual" >&2
        exit 1
    fi
}
mkdir() {
    [ "$#" -eq 4 ] && [ "$1" = -m ] && [ "$2" = 700 ] && [ "$3" = -- ] && [ "$4" = "$HOME/.codex" ] || return 1
    if [ -x /usr/bin/cygpath ]; then command mkdir -- "$4"; else command mkdir "$@"; fi
}
expect_home inspect missing:default
[ ! -e "$HOME/.codex" ]
expect_home prepare created:default
[ -d "$HOME/.codex" ]
printf 'config fixture\n' >"$HOME/.codex/config.toml"
expect_home prepare ready:default
[ "$(<"$HOME/.codex/config.toml")" = 'config fixture' ]
mv -- "$HOME/.codex" "$HOME/preexisting custom"

export CODEX_HOME="$HOME/missing custom"
expect_home prepare missing:custom
[ ! -e "$CODEX_HOME" ] && [ ! -e "$HOME/.codex" ]
printf 'keep this file\n' >"$CODEX_HOME"
expect_home prepare unavailable:custom
[ "$(<"$CODEX_HOME")" = 'keep this file' ]
export CODEX_HOME="$HOME/preexisting custom"
expect_home inspect ready:custom
export CODEX_HOME="$HOME/.codex"$'\n'
expect_home prepare unknown:unknown
[ ! -e "$HOME/.codex" ]
unset CODEX_HOME

export SHELL="$HOME/fixture-login"
cat >"$SHELL" <<'LOGIN_FIXTURE'
#!/usr/bin/env bash
[ "$1" = -l ] && [ "$2" = -i ] && [ "$3" = -c ] || exit 2
printf 'Startup banner without a trailing newline'
export CODEX_HOME="$HOME/preexisting custom"
exec bash --noprofile --norc -c "$4"
LOGIN_FIXTURE
chmod +x "$SHELL"
expect_home prepare ready:custom
[ ! -e "$HOME/.codex" ]

proc_fixture="$HOME/fixture-proc"
command mkdir -p -- "$proc_fixture/100" "$proc_fixture/200"
printf '/fixture/openai.chatgpt-1.2.3/bin/codex\0app-server-helper\0' >"$proc_fixture/100/cmdline"
printf 'HOME=%s\0CODEX_HOME=%s\0' "$HOME" "$HOME/missing-process-home" >"$proc_fixture/100/environ"
expect_home inspect ready:custom
printf '/fixture/openai.chatgpt-1.2.3/bin/codex\0app-server\0' >"$proc_fixture/100/cmdline"
expect_home prepare missing:custom
[ ! -e "$HOME/missing-process-home" ] && [ ! -e "$HOME/.codex" ]

printf '/fixture/openai.chatgpt-1.2.3/bin/codex\0app-server\0' >"$proc_fixture/200/cmdline"
printf 'HOME=%s\0CODEX_HOME=%s\0' "$HOME" "$HOME/preexisting custom" >"$proc_fixture/200/environ"
expect_home prepare unknown:unknown
[ ! -e "$HOME/.codex" ]
'@

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
$fixture = Join-Path $temp "codex-home-fixture-$([Guid]::NewGuid().ToString('N'))"
try {
    $null = [IO.Directory]::CreateDirectory($fixture)
    $payload = ($bootstrap + "`n" + $source + "`n" + $cases).Replace("`r", '')
    $PSNativeCommandUseErrorActionPreference = $false
    ($payload + "`n# EOF") | & $bash --noprofile --norc -s -- $fixture
    if ($LASTEXITCODE -ne 0) { throw "Codex home Bash fixtures failed (exit code $LASTEXITCODE)." }
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $temp -and [IO.Directory]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$markers = & {
    Write-RemoteCodexHomeStatus @('CODEX_HOME_STATUS:created:default')
    Write-RemoteCodexHomeStatus @('CODEX_HOME_STATUS:ready:default') -PreserveCreated
} 6>&1 | ForEach-Object { $_.ToString() }
if (@($markers).Count -ne 1 -or $markers -cne 'CODEX_HOME_STATUS:created:default') {
    throw 'The final inspection replaced the home creation marker.'
}
Write-Host 'PASS  Codex home inspection, safe preparation, login shell and active process checks.'
