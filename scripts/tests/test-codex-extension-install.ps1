[CmdletBinding()]
param([string]$ScriptPath = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ScriptPath) { $ScriptPath = Join-Path $PSScriptRoot '../../ssh-proxy-bridge.ps1' }
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($ScriptPath), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0].Message }
foreach ($name in @('Get-CodexExtensionProxyUrl', 'Get-CodexExtensionServerScript', 'Get-CodexExtensionGalleryUrl', 'Save-CodexExtensionVsix',
    'Assert-CodexExtensionSshResult', 'Install-CodexExtension')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if (-not $definition) { throw "Missing installer function $name." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Get-Executable([string]$Name, [string]$Fallback = '') {
    if ($Name -eq 'curl.exe') { return 'fixture-curl' }
}

function Test-TcpPort { return $false }

function Invoke-NativeProcessWithTimeout([string]$FilePath, [string[]]$Arguments, [string]$InputText, [int]$TimeoutSeconds) {
    if ($FilePath -ne 'fixture-curl') { throw 'The Windows installer selected an unexpected downloader.' }
    if ($Arguments[-1] -ceq 'https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery') {
        $query = $Arguments[([Array]::IndexOf($Arguments, '--data-binary') + 1)] | ConvertFrom-Json
        if ($query.filters[0].criteria[0].value -cne 'openai.chatgpt') { throw 'The Marketplace query requested another extension.' }
        $metadata = @'
{"results":[{"extensions":[{"extensionName":"chatgpt","publisher":{"publisherName":"openai"},"versions":[
  {"version":"2.1.0","targetPlatform":"linux-x64","properties":[{"key":"Microsoft.VisualStudio.Code.PreRelease","value":"true"}],"fallbackAssetUri":"https://openai.gallery.vsassets.io/prerelease"},
  {"version":"2.0.0","targetPlatform":"linux-arm64","fallbackAssetUri":"https://openai.gallery.vsassets.io/another-platform"},
  {"version":"1.0.0","targetPlatform":"linux-x64","fallbackAssetUri":"https://openai.gallery.vsassets.io/older"},
  {"version":"1.1.0","targetPlatform":"linux-x64","fallbackAssetUri":"https://openai.gallery.vsassets.io/_apis/public/gallery/publisher/openai/extension/chatgpt/1.1.0/assetbyname"}
]}]}]}
'@
        return [pscustomobject]@{ ExitCode = 0; Output = @($metadata) }
    }
    if ($Arguments[-1] -cne
        'https://openai.gallery.vsassets.io/_apis/public/gallery/publisher/openai/extension/chatgpt/1.1.0/assetbyname/Microsoft.VisualStudio.Services.VSIXPackage?redirect=true&targetPlatform=linux-x64' -or
        $Arguments[([Array]::IndexOf($Arguments, '--proto-redir') + 1)] -cne '=https') {
        throw 'The Windows installer did not request the expected official HTTPS Linux x64 package.'
    }
    $path = $Arguments[([Array]::IndexOf($Arguments, '--output') + 1)]
    $script:TemporaryVsix = $path
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.File]::Delete($path)
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{
            'extension.vsixmanifest' = "<PackageManifest><Metadata><Identity Publisher='openai' Id='chatgpt' TargetPlatform='$script:FixtureTarget' /></Metadata></PackageManifest>"
            'extension/package.json' = '{"publisher":"openai","name":"chatgpt","extensionPack":["openai.codex-audio"]}'
        }
        foreach ($name in $entries.Keys) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open())
            try { $writer.Write($entries[$name]) }
            finally { $writer.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return [pscustomobject]@{ ExitCode = 0 }
}

function Invoke-CodexExtensionSsh($Configuration, [string]$Source, [int]$TimeoutSeconds, [string]$UploadPath = '') {
    $script:RemoteSources += $Source
    $output = @()
    if ($Source -match 'CODEX_EXTENSION_TARGET:%s') {
        $output += 'CODEX_EXTENSION_TARGET:linux-x64'
    }
    if ($UploadPath) {
        $hash = (Get-FileHash -LiteralPath $UploadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $Source.Contains($hash) -or -not $Source.Contains('cat >"$archive"') -or
            -not $Source.Contains('--do-not-include-pack-dependencies')) {
            throw 'The upload script did not stage, verify and install the VSIX without network dependencies.'
        }
        Write-Host 'CODEX_EXTENSION_PROGRESS:remote-install'
    }
    return [pscustomobject]@{ ExitCode = 0; Output = $output; TimedOut = $false }
}

function Assert-Route([string]$Route, [string]$ExpectedPhases) {
    $script:RemoteSources = @()
    $markers = @(Install-CodexExtension $configuration $Route 6>&1 |
        ForEach-Object { $_.ToString().Replace('CODEX_EXTENSION_PROGRESS:', '') })
    $actual = $markers -join ','
    if ($actual -cne $ExpectedPhases) { throw "Unexpected progress phases for ${Route}: $actual." }
}

$configuration = [pscustomobject]@{
    proxy = [pscustomobject]@{ host = '127.0.0.1'; port = 7897 }
    ssh = [pscustomobject]@{ remoteProxyHost = '127.0.0.1'; remoteProxyPort = 17897 }
}
$script:FixtureTarget = 'linux-x64'
$script:TemporaryVsix = ''
Assert-Route 'proxy' 'checking,remote-download-install,complete'
if ($script:RemoteSources.Count -ne 1 -or $script:RemoteSources[0] -notmatch '--proxy "\$proxy"') {
    throw 'The proxy installation did not use the configured reverse proxy.'
}
Assert-Route 'direct' 'checking,remote-download-install,complete'
if ($script:RemoteSources.Count -ne 1 -or $script:RemoteSources[0] -match '--proxy "\$proxy"') {
    throw 'A direct installation still used the configured proxy.'
}
if ($script:RemoteSources[0] -notmatch '--force-disable-user-env[^\r\n]+--install-extension') {
    throw 'VS Code can reload a login-shell proxy and override the selected route.'
}
Assert-Route 'upload' 'checking,windows-download,ssh-upload,remote-install,complete'
if ($script:RemoteSources.Count -ne 2 -or [IO.File]::Exists($script:TemporaryVsix)) {
    throw 'The upload route did not finish and remove its Windows staging file.'
}

$script:FixtureTarget = 'linux-arm64'
try {
    Install-CodexExtension $configuration 'upload' 6>$null
    throw 'A package for the wrong Linux architecture was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'expected OpenAI Codex extension and Linux architecture') { throw }
    if ([IO.File]::Exists($script:TemporaryVsix)) { throw 'A rejected package was not removed.' }
}
Write-Host 'PASS  Direct and proxy routes use the remote VS Code CLI and report progress.'
Write-Host 'PASS  Windows upload verifies the official Linux VSIX and removes its staging file.'
