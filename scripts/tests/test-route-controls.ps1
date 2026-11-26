[CmdletBinding()]
param(
    [string]$ScriptPath = '',
    [string]$Fixture = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ScriptPath) {
    $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1'
}
$ScriptPath = [IO.Path]::GetFullPath($ScriptPath)

if ($Fixture) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw $errors[0].Message }
    $dispatch = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.SwitchStatementAst] -and
            $node.Condition.Extent.Text -eq '$Command'
    }, $false)
    if (-not $dispatch) { throw 'Bridge command dispatcher was not found.' }

    foreach ($name in @('Enable-ManagedTunnel', 'Get-ManagedTunnelSnapshot', 'Write-ManagedTunnelStatus',
            'Write-ApplicationNetworkStatus')) {
        $node = $ast.Find({
            param($candidate)
            $candidate -is [Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq $name
        }, $false)
        if (-not $node) { throw "Missing bridge function $name." }
        . ([scriptblock]::Create($node.Extent.Text))
    }

    function Test-RemoteDirectCodex($Configuration, [switch]$Quiet, [switch]$ReportProbeResult) {
        if (-not ($Quiet -and $ReportProbeResult)) { throw 'Direct probe should use the read-only reporting path.' }
        Write-Host 'direct-probe'
        return $Fixture -notin @('check-fail', 'start-direct-fail')
    }
    function Start-ProxyIfNeeded($Configuration) { Write-Host 'local-proxy' }
    $script:intent = 'unspecified'
    $script:tunnelStarted = $false
    $script:monitorStarted = $false
    function Get-TunnelIntent { return $script:intent }
    function Set-TunnelIntent([string]$Intent) { $script:intent = $Intent }
    function Enter-RepairLock { return ,([IO.MemoryStream]::new()) }
    function Get-TunnelProcess($Configuration) {
        if ($Fixture -eq 'tunnel-existing' -or $script:tunnelStarted) { return [pscustomobject]@{ Id = 1234 } }
        return $null
    }
    function Get-MonitorProcess { if ($script:monitorStarted) { return [pscustomobject]@{ Id = 5678 } } }
    function Install-SshConfig($Configuration) { Write-Host 'ssh-config' }
    function Assert-SshLoginReady($Configuration) { Write-Host 'ssh-ready' }
    function Select-RemoteCodexRoute($Configuration) { Write-Host 'auto-select'; return 'proxy' }
    function Start-Tunnel($Configuration) {
        $script:tunnelStarted = $true
        Write-Host 'tunnel-start'
    }
    function Stop-Tunnel($Configuration) { throw 'Route controls must not stop the tunnel.' }
    function Start-Monitor($Configuration) {
        $script:monitorStarted = $true
        Write-Host 'monitor-start'
    }
    function Test-RemoteProxy($Configuration) {
        Write-Host 'probe-proxy'
        return $Fixture -ne 'tunnel-new-unverified'
    }
    function Install-RemoteProxyEnvironment($Configuration, [string]$Route) {
        $script:installedRoute = $Route
        Write-Host "remote-env:$Route"
    }
    $script:codexHomePrepared = $false
    $script:vscodeStarted = $false
    $script:vscodeRouteChecked = $false
    function Start-VsCode($Configuration) {
        if (-not $script:codexHomePrepared) { throw 'VS Code launched before preparing the default Codex home.' }
        $script:vscodeStarted = $true
        Write-Host 'vscode-start'
    }
    function Test-RemoteApplicationNetwork($Configuration, [switch]$Quiet, [switch]$SuppressMarkers) {
        $script:vscodeRouteChecked = $script:vscodeStarted
        $script:LastApplicationNetworkRoute = $script:installedRoute
        $script:LastApplicationNetworkReason = 'ready'
        $script:LastApplicationNetworkMarker = "APPLICATION_NETWORK_READY:$($script:installedRoute):no-active-codex"
        $script:LastCodexAccountAccessMarker = 'CODEX_ACCOUNT_ACCESS:unknown:unknown:none'
        return $true
    }
    function Test-RemoteCodexAuthentication($Configuration, [switch]$Quiet, [switch]$IncludeExtensionStatus,
        [switch]$PrepareCodexHome, [switch]$HomePreparationOnly, [switch]$PreserveCreatedCodexHome) {
        if ($PrepareCodexHome) {
            if ($Command -ne 'start' -or -not $HomePreparationOnly -or $script:vscodeStarted) {
                throw 'Codex home preparation should occur independently before VS Code launches.'
            }
            $script:codexHomePrepared = $true
        }
        elseif ($Command -eq 'start') {
            if (-not ($script:vscodeRouteChecked -and $IncludeExtensionStatus -and $PreserveCreatedCodexHome)) {
                throw 'The final auth check must follow the VS Code launch and route check and preserve the home marker.'
            }
            Write-Host 'vscode-final-auth'
        }
        $script:LastRemoteCodexAuthenticationState = 'ready'
        return $true
    }
    function Write-Pass([string]$Message) { Write-Host $Message }
    function Write-Warn([string]$Message) { Write-Host $Message }

    if ($Fixture -eq 'check-read-only-ssh') {
        $directProbe = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Test-RemoteDirectCodex'
        }, $false)
        if (-not $directProbe) { throw 'Direct probe function was not found.' }
        . ([scriptblock]::Create($directProbe.Extent.Text))
        function Invoke-Ssh($Configuration, [string]$RemoteCommand, [switch]$UseAlias, [switch]$ReadOnly) {
            if ($UseAlias -or -not $ReadOnly) { throw 'Expected read-only SSH to the configured endpoint.' }
            return [pscustomobject]@{ ExitCode = 0; Output = @('CODEX_DIRECT_OK:401:attempt-1') }
        }
    }

    $configuration = [pscustomobject]@{}
    $Command = if ($Fixture.StartsWith('check-')) { 'check-direct' }
        elseif ($Fixture.StartsWith('tunnel-')) { 'start-tunnel' }
        else { 'start' }
    $ApplicationRoute = if ($Fixture.StartsWith('start-direct-')) { 'direct' }
        elseif ($Fixture -eq 'start-proxy') { 'proxy' }
        else { 'auto' }

    & ([scriptblock]::Create($dispatch.Extent.Text))
    exit 0
}

$hostExecutable = (Get-Process -Id $PID).Path
$cases = @(
    @{ Name = 'check-pass'; Code = 0; Required = @('direct-probe'); Forbidden = @('ssh-config', 'local-proxy', 'remote-env:', 'vscode-start') }
    @{ Name = 'check-fail'; Code = 2; Required = @('direct-probe'); Forbidden = @('ssh-config', 'local-proxy', 'remote-env:', 'vscode-start') }
    @{ Name = 'check-read-only-ssh'; Code = 0; Required = @('CODEX_DIRECT_OK:401:attempt-1'); Forbidden = @('ssh-config', 'local-proxy', 'remote-env:', 'vscode-start') }
    @{ Name = 'start-direct-fail'; Code = 3; Required = @('direct-probe', 'proxy-choice-required'); Forbidden = @('ssh-config', 'local-proxy', 'remote-env:', 'vscode-start') }
    @{ Name = 'start-direct-pass'; Code = 0; Required = @('direct-probe', 'remote-env:direct', 'vscode-start', 'vscode-final-auth'); Forbidden = @('auto-select', 'local-proxy', 'tunnel-start') }
    @{ Name = 'start-proxy'; Code = 0; Required = @('local-proxy', 'tunnel-start', 'monitor-start', 'remote-env:proxy', 'vscode-start', 'vscode-final-auth'); Forbidden = @('auto-select', 'direct-probe') }
    @{ Name = 'start-auto'; Code = 0; Required = @('auto-select', 'remote-env:proxy', 'vscode-start', 'vscode-final-auth'); Forbidden = @('direct-probe') }
    @{ Name = 'tunnel-existing'; Code = 0; Required = @('Tunnel start result: existing', 'Tunnel state: running', 'remote-env:proxy'); Forbidden = @('ssh-config', 'tunnel-start', 'vscode-start') }
    @{ Name = 'tunnel-new-verified'; Code = 0; Required = @('tunnel-start', 'monitor-start', 'remote-env:proxy', 'Application network: proxy', 'Tunnel start result: ready'); Forbidden = @('vscode-start') }
    @{ Name = 'tunnel-new-unverified'; Code = 0; Required = @('tunnel-start', 'Tunnel state: running', 'Tunnel start result: unverified'); Forbidden = @('monitor-start', 'remote-env:', 'vscode-start') }
)

foreach ($case in $cases) {
    $output = (& $hostExecutable -NoLogo -NoProfile -NonInteractive -File $PSCommandPath `
        -ScriptPath $ScriptPath -Fixture $case.Name) -join "`n"
    if ($LASTEXITCODE -ne $case.Code) {
        throw "Wrong exit code for $($case.Name): expected $($case.Code), got $LASTEXITCODE. $output"
    }
    foreach ($fragment in $case.Required) {
        if (-not $output.Contains($fragment)) { throw "$($case.Name) did not report '$fragment'. $output" }
    }
    foreach ($fragment in $case.Forbidden) {
        if ($output.Contains($fragment)) { throw "$($case.Name) unexpectedly reported '$fragment'. $output" }
    }
    Write-Host "PASS  $($case.Name)"
}
