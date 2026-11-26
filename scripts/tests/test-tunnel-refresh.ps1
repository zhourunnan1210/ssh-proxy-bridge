[CmdletBinding()]
param([string]$ScriptPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ScriptPath) {
    $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1'
}

$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw "Could not parse bridge script: $($errors[0].Message)" }

foreach ($name in @('Get-ManagedTunnelSnapshot', 'Write-ManagedTunnelStatus', 'Disable-ManagedTunnel',
        'Restore-ManagedApplicationRoute', 'Write-ApplicationNetworkStatus', 'Invoke-ConnectionStatus', 'Repair-Tunnel')) {
    $node = $ast.Find({
        param($candidate)
        $candidate -is [Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq $name
    }, $false)
    if (-not $node) { throw "Missing bridge function $name." }
    $definition = $node.Extent.Text
    if ($name -eq 'Repair-Tunnel') { $definition = $definition.Replace('function Repair-Tunnel', 'function Invoke-ActualTunnelRepair') }
    . ([scriptblock]::Create($definition))
}

$configuration = [pscustomobject]@{ proxy = [pscustomobject]@{ host = 'localhost'; port = 7897 } }
$routeIntentPath = Join-Path ([IO.Path]::GetTempPath()) ('BridgeTestRoute-' + [guid]::NewGuid().ToString('N'))

function Get-StatePath([string]$Name) {
    if ($Name -ne 'application-route.intent') { throw "Unexpected state read: $Name" }
    return $routeIntentPath
}

function Get-TunnelIntent { $script:intent }
function Set-TunnelIntent([string]$Intent) {
    $script:actions.Add("intent=$Intent")
    $script:intent = $Intent
}
function Get-TunnelProcess($Configuration) { if ($script:tunnel) { [pscustomobject]@{ Id = $script:tunnelPid } } }
function Get-MonitorProcess { if ($script:monitor) { [pscustomobject]@{ Id = 1912 } } }
function Start-Monitor($Configuration) {
    $script:actions.Add('start-monitor')
    $script:monitor = $true
    Get-MonitorProcess
}
function Stop-Monitor {
    $script:actions.Add('stop-monitor')
    $script:monitor = $false
}
function Stop-Tunnel($Configuration) {
    $script:actions.Add('stop-tunnel')
    $script:tunnel = $false
}
function Start-Tunnel($Configuration) {
    $script:actions.Add('start-tunnel')
    $script:tunnel = $true
    $script:tunnelPid++
    Get-TunnelProcess $Configuration
}
function Enter-RepairLock { return ,([IO.MemoryStream]::new()) }
function Start-ProxyIfNeeded($Configuration) { $script:actions.Add('ensure-local-proxy') }
function Repair-Tunnel($Configuration, [switch]$RespectTunnelIntent) {
    if (-not $RespectTunnelIntent) { throw 'Automatic repair ignored tunnel intent.' }
    $script:actions.Add('repair-tunnel')
    $script:tunnel = $true
    Get-TunnelProcess $Configuration
}

function Test-PasswordGatewayMode($Configuration) { return $false }
function Test-SshLogin($Configuration, [switch]$Quiet) { return $true }
function Test-TcpPort { return $true }
function Get-RemoteTunnelListenerState($Configuration) { return $script:listenerState }
function Get-TunnelFailureSummary { return 'fixture tunnel is stopped' }
function Write-MonitorEvent([string]$Message, [string]$Level) { }
function Write-Warn([string]$Message) { }
function Write-Pass([string]$Message) { }
function Write-SshLoginMarkers { }
function Test-RemoteCodexAuthentication($Configuration, [switch]$Quiet, [switch]$IncludeExtensionStatus) {
    $script:LastRemoteCodexAuthenticationState = 'ready'
    return $true
}
function Test-RemoteDirectCodex($Configuration, [switch]$Quiet) {
    $script:actions.Add('verify-direct')
    return $true
}
function Test-RemoteProxy($Configuration) {
    $script:actions.Add('verify-proxy')
    return $script:tunnel -and $script:proxyHealthy
}
function Install-RemoteProxyEnvironment($Configuration, [string]$Route) {
    $script:actions.Add("install=$Route")
}
function Test-RemoteApplicationNetwork($Configuration, [switch]$Quiet, [switch]$SuppressMarkers) {
    if (-not $SuppressMarkers) { throw 'An intermediate diagnosis would leak into the final status.' }
    $marker = $script:networkChecks.Dequeue()
    $script:LastManagedProxyGuarded = $marker.EndsWith('|guarded', [StringComparison]::Ordinal)
    if ($script:LastManagedProxyGuarded) { $marker = $marker.Substring(0, $marker.Length - '|guarded'.Length) }
    $script:LastApplicationNetworkMarker = $marker
    $script:LastCodexAccountAccessMarker = 'CODEX_ACCOUNT_ACCESS:unknown:unknown:none'
    if ($marker -match '^APPLICATION_NETWORK_READY:(direct|proxy):') {
        $script:LastApplicationNetworkRoute = $Matches[1]
        $script:LastApplicationNetworkReason = 'ready'
        return $true
    }
    $script:LastApplicationNetworkRoute = 'not ready'
    $script:LastApplicationNetworkReason = if ($marker -eq 'APPLICATION_NETWORK_ROUTE_MISSING') {
        'shell-route-missing'
    } else {
        'shell-route-mismatch'
    }
    return $false
}

function Initialize-Scenario([string]$Intent, [bool]$Tunnel, [bool]$Monitor, [string[]]$Checks) {
    $script:intent = $Intent
    $script:tunnel = $Tunnel
    $script:tunnelPid = 1911
    $script:monitor = $Monitor
    $script:proxyHealthy = $true
    $script:listenerState = 'unknown'
    $script:actions = [Collections.Generic.List[string]]::new()
    $script:networkChecks = [Collections.Generic.Queue[string]]::new()
    foreach ($check in $Checks) { $script:networkChecks.Enqueue($check) }
}

function Assert-Refresh([string]$TunnelState, [string]$Result, [string[]]$Actions) {
    $output = @(Invoke-ConnectionStatus $configuration -RepairWhenReady 6>&1)
    $expected = @('REFRESH_FINAL_STATUS', "Tunnel state: $TunnelState", "Refresh result: $Result")
    foreach ($line in $expected) {
        if (@($output | Where-Object { "$_" -ceq $line }).Count -ne 1) {
            throw "Expected exactly one '$line'. Refresh wrote: $($output -join ' | ')"
        }
    }
    if (@($output | Where-Object { "$_" -match '^Application network marker:' }).Count -ne 1) {
        throw 'Refresh must report one final application network marker.'
    }
    if ($output[-1] -ne 0 -or $script:networkChecks.Count -ne 0 -or
            ($script:actions -join ',') -ne ($Actions -join ',')) {
        throw "Unexpected actions. Expected [$($Actions -join ',')]; ran [$($script:actions -join ',')]."
    }
}

try {
    [IO.File]::WriteAllText($routeIntentPath, 'proxy')
    Initialize-Scenario 'disabled' $false $false @('APPLICATION_NETWORK_ROUTE_MISSING')
    Assert-Refresh 'stopped' 'checked' @()

    Initialize-Scenario 'enabled' $false $false @('APPLICATION_NETWORK_ROUTE_MISSING', 'APPLICATION_NETWORK_READY:proxy:1/1')
    Assert-Refresh 'running' 'repaired' @('ensure-local-proxy', 'repair-tunnel', 'start-monitor')

    Initialize-Scenario 'enabled' $true $true @('APPLICATION_NETWORK_READY:direct:1/1')
    Assert-Refresh 'running' 'checked' @()

    Initialize-Scenario 'enabled' $true $true @('APPLICATION_NETWORK_READY:proxy:1/1|guarded', 'APPLICATION_NETWORK_READY:proxy:1/1')
    Assert-Refresh 'running' 'repaired' @('install=proxy')

    Initialize-Scenario 'enabled' $true $true @('APPLICATION_NETWORK_READY:proxy:1/1')
    Assert-Refresh 'running' 'checked' @()

    Initialize-Scenario 'disabled' $false $false @('APPLICATION_NETWORK_SHELL_MISMATCH:direct', 'APPLICATION_NETWORK_READY:direct:1/1')
    Assert-Refresh 'stopped' 'repaired' @('verify-direct', 'install=direct')

    Initialize-Scenario 'enabled' $true $true @()
    Disable-ManagedTunnel $configuration
    if ($script:intent -ne 'disabled' -or $script:tunnel -or $script:monitor -or
            ($script:actions -join ',') -ne 'intent=disabled,stop-monitor,intent=disabled,stop-tunnel') {
        throw 'Disabling did not stop the managed monitor and tunnel in order.'
    }

    foreach ($listener in @('listening', 'unknown', 'missing')) {
        Initialize-Scenario 'enabled' $true $true @()
        $script:proxyHealthy = $false
        $script:listenerState = $listener
        $forward = Invoke-ActualTunnelRepair $configuration -RespectTunnelIntent
        $expectedActions = if ($listener -eq 'missing') { 'verify-proxy,stop-tunnel,start-tunnel,verify-proxy' } else { 'verify-proxy' }
        $expectedPid = if ($listener -eq 'missing') { 1912 } else { 1911 }
        if ($forward.Id -ne $expectedPid -or -not $script:tunnel -or ($script:actions -join ',') -ne $expectedActions) {
            throw "Unexpected repair behavior when the upstream probe fails and the remote listener is $listener."
        }
    }
}
finally {
    Remove-Item -LiteralPath $routeIntentPath -Force -ErrorAction SilentlyContinue
}

Write-Host 'PASS  Refresh preserves a stopped tunnel and a healthy direct route, and restores a lost enabled tunnel.'
Write-Host 'PASS  Refresh reports one final status and restores only the known application route.'
Write-Host 'PASS  Old proxy guard is migrated once and current healthy routing is preserved.'
Write-Host 'PASS  Explicit stop records disabled intent and stops the managed monitor and tunnel.'
Write-Host 'PASS  Repair keeps live forwarding processes when only upstream validation fails.'
