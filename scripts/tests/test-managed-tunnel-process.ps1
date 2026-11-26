[CmdletBinding()]
param([string]$ScriptPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
    $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1'
}

$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
    throw "Could not parse the bridge script: $($errors[0].Message)"
}
$matcher = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Test-ManagedTunnelProcess'
}, $false)
if (-not $matcher) {
    throw 'The managed tunnel matcher was not found.'
}
. ([scriptblock]::Create($matcher.Extent.Text))

function Get-CimInstance {
    [CmdletBinding()]
    param([string]$ClassName, [string]$Filter)

    if ($ClassName -ne 'Win32_Process' -or $Filter -ne 'ProcessId=1234') {
        throw "Unexpected test process lookup: $ClassName $Filter"
    }
    return [pscustomobject]@{ Name = 'ssh.exe'; CommandLine = $script:CimCommandLine }
}

function Assert-TunnelMatch([string]$CommandLine, [bool]$Expected, [string]$Description) {
    $script:CimCommandLine = $CommandLine
    $actual = Test-ManagedTunnelProcess $configuration $process
    if ($actual -ne $Expected) {
        throw "${Description}: expected $Expected, got $actual."
    }
}

$configuration = [pscustomobject]@{
    proxy = [pscustomobject]@{ host = '127.0.0.1'; port = 7897 }
    ssh = [pscustomobject]@{ alias = 'proxy.example'; remoteProxyHost = '127.0.0.1'; remoteProxyPort = 17897 }
}
$process = [pscustomobject]@{ Id = 1234; ProcessName = 'ssh' }
$ssh = '"C:\Windows\System32\OpenSSH\ssh.exe" -o ConnectTimeout=10'
$managed = "$ssh -N -T -o BatchMode=yes -o NumberOfPasswordPrompts=0 -o ExitOnForwardFailure=yes"
$running = "$managed -R 127.0.0.1:17897:127.0.0.1:7897 proxy.example"

Assert-TunnelMatch $running $true 'The original tunnel should be recognized'
$configuration.proxy.host = 'localhost'
$configuration.proxy.port = 8888
Assert-TunnelMatch $running $true 'Saving local proxy changes should preserve the running tunnel identity'
Assert-TunnelMatch "$managed -R 127.0.0.1:17897:localhost:8888 proxy.example" $true 'A reconnected tunnel should be recognized'
Assert-TunnelMatch ($running.Replace('127.0.0.1:17897:127.0.0.1:7897 proxy.example', '"127.0.0.1:17897:127.0.0.1:7897" "proxy.example"')) $true 'Quoted forwarding and target arguments should be recognized'
Assert-TunnelMatch ($running.Replace('proxy.example', 'prefix-proxy.example')) $false 'An alias suffix should not match'
Assert-TunnelMatch ($running.Replace('proxy.example', 'proxy-example')) $false 'Alias punctuation should be literal'
Assert-TunnelMatch ($running.Replace('17897', '17898')) $false 'A different remote port should not match'
Assert-TunnelMatch ($running.Replace('127.0.0.1:17897', '0.0.0.0:17897')) $false 'A different remote bind should not match'
Assert-TunnelMatch "$ssh -R 127.0.0.1:17897:127.0.0.1:7897 proxy.example" $false 'An interactive SSH process should not match'
Assert-TunnelMatch "$running uptime" $false 'A tunnel with a trailing remote command should not match'

Write-Host 'PASS  A running managed tunnel remains identifiable after a local proxy edit.'
Write-Host 'PASS  Unrelated SSH processes do not match the managed tunnel.'
