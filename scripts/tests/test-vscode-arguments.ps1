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
foreach ($functionName in @('Get-VsCodeArguments', 'Get-VsCodeCliScript')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $false)
    if (-not $definition) {
        throw "The VS Code function $functionName was not found."
    }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$configuration = [pscustomobject]@{
    ssh = [pscustomobject]@{ alias = 'gpu.example-01' }
    vscode = [pscustomobject]@{ remoteWorkspace = '' }
}

function Assert-VsCodeArguments([string]$Workspace, [string[]]$Expected, [string]$Description) {
    $configuration.vscode.remoteWorkspace = $Workspace
    $actual = @(Get-VsCodeArguments $configuration)
    if ($actual.Count -ne $Expected.Count) {
        throw "${Description}: expected $($Expected.Count) arguments, got $($actual.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actual[$index] -cne $Expected[$index]) {
            throw "${Description}: argument $index expected '$($Expected[$index])', got '$($actual[$index])'."
        }
    }
    Write-Host "PASS  $Description."
}

$unicodeDirectory = -join ([char]0x9879, [char]0x76ee)
$workspace = '/home/.research/v1.2/{0} A/#fragment?query%+&=' -f $unicodeDirectory
$encodedFolder = 'vscode-remote://ssh-remote+gpu.example-01/home/.research/v1.2/%E9%A1%B9%E7%9B%AE%20A/%23fragment%3Fquery%25%2B%26%3D'
Assert-VsCodeArguments $workspace @('--new-window', "--folder-uri=$encodedFolder") `
    'Absolute Linux paths preserve slashes and dots and encode spaces, Unicode and URI delimiters'
Assert-VsCodeArguments '/' @('--new-window', '--folder-uri=vscode-remote://ssh-remote+gpu.example-01/') `
    'The remote filesystem root stays an explicit folder URI'
Assert-VsCodeArguments '' @('--new-window', '--remote', 'ssh-remote+gpu.example-01') `
    'An empty legacy workspace opens the remote without a folder'
Assert-VsCodeArguments 'legacy/old project' @('--new-window', '--remote', 'ssh-remote+gpu.example-01', 'legacy/old project') `
    'A relative legacy workspace stays a single positional argument'

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/'))
$fixtureRoot = Join-Path $tempRoot "VS Code launcher fixture $([Guid]::NewGuid().ToString('N'))"
$codeCommand = Join-Path $fixtureRoot 'bin/code.cmd'
$classicCli = Join-Path $fixtureRoot 'resources/app/out/cli.js'
$buildVersion = '123456abcd'
$versionedCli = Join-Path $fixtureRoot "$buildVersion/resources/app/out/cli.js"
try {
    foreach ($file in @($codeCommand, $classicCli, $versionedCli)) {
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
    }
    [IO.File]::WriteAllText($classicCli, '')
    [IO.File]::WriteAllText($versionedCli, '')

    [IO.File]::WriteAllText($codeCommand, '"%~dp0..\Code.exe" "%~dp0..\resources\app\out\cli.js" %*')
    if ((Get-VsCodeCliScript $fixtureRoot $codeCommand) -cne $classicCli) {
        throw 'A classic VS Code wrapper did not resolve to the installed CLI.'
    }
    Write-Host 'PASS  The classic launcher resolves its standard CLI.'

    $launcherLine = '"%~dp0..\Code.exe" "%~dp0..\{0}\resources\app\out\cli.js" %*'
    [IO.File]::WriteAllText($codeCommand, ($launcherLine -f $buildVersion))
    if ((Get-VsCodeCliScript $fixtureRoot $codeCommand) -cne $versionedCli) {
        throw 'A versioned wrapper did not select its CLI with a stale classic CLI present.'
    }
    Write-Host 'PASS  The versioned launcher resolves the exact build named in the wrapper.'

    [IO.File]::WriteAllText($codeCommand, ($launcherLine -f '8888888888'))
    if ($null -ne (Get-VsCodeCliScript $fixtureRoot $codeCommand)) {
        throw 'A launch line pointing at a missing CLI selected another build.'
    }
    [IO.File]::WriteAllText($codeCommand, '@echo An unsupported wrapper format.')
    if ($null -ne (Get-VsCodeCliScript $fixtureRoot $codeCommand)) {
        throw 'A malformed wrapper selected an unrelated CLI.'
    }
    [IO.File]::Delete($codeCommand)
    if ($null -ne (Get-VsCodeCliScript $fixtureRoot $codeCommand)) {
        throw 'A missing wrapper selected an unrelated CLI.'
    }
    Write-Host 'PASS  A missing CLI or missing or malformed wrapper does not select another build.'
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (([IO.Path]::GetDirectoryName($resolvedFixture) -eq $tempRoot) -and
        [IO.Directory]::Exists($resolvedFixture)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
