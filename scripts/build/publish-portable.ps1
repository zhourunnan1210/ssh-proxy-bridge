[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidatePattern('^[0-9A-Za-z.-]+$')]
    [string]$Runtime = 'win-x64',
    [string]$Version = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $root 'app\SshProxyBridge.App\SshProxyBridge.App.csproj'
$localExecutable = Join-Path $root 'SshProxyBridge.exe'
$releaseRoot = Join-Path $root 'release'
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -Raw -LiteralPath $project
    $Version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
}
if ($Version -notmatch '^[0-9A-Za-z.-]+$') {
    throw "Invalid application version in project file: $Version"
}
$packageName = "SSH-Proxy-Bridge-v$Version-$Runtime"
$publishDirectory = Join-Path $releaseRoot $packageName
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$zipChecksumPath = "$zipPath.sha256"
$artifactsDirectory = Join-Path $env:TEMP "SshProxyBridge.Publish.$PID"

function Assert-ChildPath([string]$Candidate, [string]$Parent) {
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $candidatePath.StartsWith($parentPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the expected directory: $candidatePath"
    }
}

function Compress-ArchiveWithRetry(
    [string]$Source,
    [string]$Destination,
    [int]$MaximumAttempts = 8
) {
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            Compress-Archive -Path $Source -DestinationPath $Destination -CompressionLevel Optimal -ErrorAction Stop
            return
        }
        catch {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            }
            if ($attempt -eq $MaximumAttempts) {
                throw
            }

            # A just-executed single-file app can remain locked briefly while
            # Windows finishes process cleanup or security scanning.
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

Assert-ChildPath $publishDirectory $releaseRoot
Assert-ChildPath $zipPath $releaseRoot
Assert-ChildPath $zipChecksumPath $releaseRoot
Assert-ChildPath $artifactsDirectory $env:TEMP

$runningLocalGui = Get-Process -Name 'SshProxyBridge' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $localExecutable } |
    Select-Object -First 1
if ($runningLocalGui) {
    throw 'Close the root SshProxyBridge.exe window before rebuilding the local GUI.'
}

if (-not (Test-Path -LiteralPath $releaseRoot)) {
    New-Item -ItemType Directory -Path $releaseRoot | Out-Null
}
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $zipChecksumPath) {
    Remove-Item -LiteralPath $zipChecksumPath -Force
}

try {
    & dotnet publish $project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --nologo `
        -p:PublishProfile=PortableWinX64 `
        -p:Version=$Version `
        -p:ArtifactsPath=$artifactsDirectory `
        -p:NuGetAudit=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File |
        Remove-Item -Force

    $requiredFiles = @(
        'SshProxyBridge.exe',
        'ssh-proxy-bridge.ps1',
        'USER_GUIDE.md',
        'PORTABLE_README.txt',
        'config.example.json',
        'LICENSE',
        'THIRD_PARTY_NOTICES.md'
    )
    foreach ($name in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name) -PathType Leaf)) {
            throw "Published package is missing: $name"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $publishDirectory 'config.local.json')) {
        throw 'Published package must not contain config.local.json.'
    }

    $executable = Join-Path $publishDirectory 'SshProxyBridge.exe'
    $verification = Start-Process -FilePath $executable -ArgumentList '--verify-package' `
        -WindowStyle Hidden -Wait -PassThru
    if ($verification.ExitCode -ne 0) {
        throw "Portable package self-check failed with exit code $($verification.ExitCode)."
    }

    Copy-Item -LiteralPath $executable -Destination $localExecutable -Force

    $hash = Get-FileHash -LiteralPath $executable -Algorithm SHA256
    $hashLine = "$($hash.Hash.ToLowerInvariant())  SshProxyBridge.exe"
    [IO.File]::WriteAllText(
        (Join-Path $publishDirectory 'SHA256SUMS.txt'),
        $hashLine + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Compress-ArchiveWithRetry (Join-Path $publishDirectory '*') $zipPath
    $zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    [IO.File]::WriteAllText(
        $zipChecksumPath,
        "$($zipHash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zipPath))" + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    $currentArtifacts = @(
        $publishDirectory,
        $zipPath,
        $zipChecksumPath
    ) | ForEach-Object { [IO.Path]::GetFullPath($_) }
    Get-ChildItem -LiteralPath $releaseRoot -Force |
        Where-Object {
            ($_.Name -like 'SSH-Proxy-Bridge-*' -or $_.Name -like 'CodexRemoteBridge-*') -and
            $_.FullName -notin $currentArtifacts
        } |
        ForEach-Object {
            Assert-ChildPath $_.FullName $releaseRoot
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }

    Write-Host ''
    Write-Host 'Portable package created:' -ForegroundColor Green
    Write-Host "  Folder: $publishDirectory"
    Write-Host "  ZIP:    $zipPath"
    Write-Host "  Local:  $localExecutable"
    Write-Host "  EXE SHA256: $($hash.Hash.ToLowerInvariant())"
    Write-Host "  ZIP SHA256: $($zipHash.Hash.ToLowerInvariant())"
}
finally {
    if (Test-Path -LiteralPath $artifactsDirectory) {
        Assert-ChildPath $artifactsDirectory $env:TEMP
        Remove-Item -LiteralPath $artifactsDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
