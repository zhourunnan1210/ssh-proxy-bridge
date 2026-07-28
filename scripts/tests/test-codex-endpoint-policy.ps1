[CmdletBinding()]
param(
    [string]$ScriptPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '..\..\ssh-proxy-bridge.ps1'
}
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ScriptPath))

$required = @(
    'if [ "$code" = ''401'' ]; then',
    'if [ "$codex_code" = ''401'' ]; then',
    'if [ "$direct_code" != ''401'' ]; then',
    'if [ "$proxy_code" != ''401'' ]; then',
    'APPLICATION_NETWORK_PROXY_FAILED:'
)
foreach ($fragment in $required) {
    if ($source.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Codex endpoint policy is missing the required fragment: $fragment"
    }
}

$forbidden = @(
    '400|401',
    '2??|3??|4??',
    'probe gstatic',
    'probe microsoft',
    'probe example'
)
foreach ($fragment in $forbidden) {
    if ($source.IndexOf($fragment, [StringComparison]::Ordinal) -ge 0) {
        throw "Codex endpoint policy still accepts an overly broad fallback: $fragment"
    }
}

$retryCount = [regex]::Matches(
    $source,
    [regex]::Escape('for attempt in 1 2; do')).Count
if ($retryCount -lt 4) {
    throw "Codex endpoint probes must confirm failures with a short retry (found $retryCount retry loops)."
}

Write-Host 'PASS  Codex endpoint probes accept only the expected 401 response.'
Write-Host 'PASS  Proxy application status performs an explicit Codex endpoint probe.'
Write-Host 'PASS  Direct and proxy probes retry once before declaring a route unavailable.'
