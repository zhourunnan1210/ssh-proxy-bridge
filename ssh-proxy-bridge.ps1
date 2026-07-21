[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('doctor', 'bootstrap-key', 'setup', 'start', 'repair', 'monitor', 'status', 'stop', 'uninstall')]
    [string]$Command = 'doctor',

    [string]$Config,

    [string]$RemoteWorkspace,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Config) {
    $Config = Join-Path $PSScriptRoot 'config.local.json'
}

# Keep the legacy configuration on its existing state directory. Application
# profiles override this after their runtime configuration has been loaded.
$script:StateDirectory = Join-Path $PSScriptRoot '.state'

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Write-Pass([string]$Message) {
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

function Write-Warn([string]$Message) {
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Write-Fail([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Expand-EnvironmentPath([string]$Path) {
    return [Environment]::ExpandEnvironmentVariables($Path)
}

function Read-Configuration([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Configuration file not found: $Path"
    }

    $value = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    foreach ($name in @('proxy', 'ssh', 'vscode')) {
        if (-not $value.PSObject.Properties[$name]) {
            throw "Configuration section is missing: $name"
        }
    }

    foreach ($name in @('host', 'port')) {
        if (-not $value.proxy.PSObject.Properties[$name]) {
            throw "Configuration value is missing: proxy.$name"
        }
    }

    foreach ($name in @('alias', 'host', 'port', 'user', 'remoteProxyHost', 'remoteProxyPort')) {
        if (-not $value.ssh.PSObject.Properties[$name]) {
            throw "Configuration value is missing: ssh.$name"
        }
    }

    $authentication = if ($value.ssh.PSObject.Properties['authentication']) {
        [string]$value.ssh.authentication
    }
    else {
        'managedKey'
    }
    if ($authentication -ine 'passwordGateway' -and -not $value.ssh.PSObject.Properties['identityFile']) {
        throw 'Configuration value is missing: ssh.identityFile'
    }

    $serialized = $value | ConvertTo-Json -Depth 10
    if ($serialized -match '(?i)"(password|passwd|pwd)"\s*:') {
        throw 'Password fields are forbidden. Use the interactive bootstrap-key command.'
    }

    return $value
}

function Get-StateDirectory {
    $path = $script:StateDirectory
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
    return $path
}

function Get-StatePath([string]$Name) {
    return Join-Path (Get-StateDirectory) $Name
}

function Get-Executable([string]$Name, [string]$Fallback = '') {
    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }
    if ($Fallback -and (Test-Path -LiteralPath $Fallback -PathType Leaf)) {
        return $Fallback
    }
    return $null
}

function Test-TcpPort([string]$HostName, [int]$Port, [int]$TimeoutMs = 1200) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync($HostName, $Port)
        if (-not $task.Wait($TimeoutMs)) {
            return $false
        }
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Get-PortListeners([int]$Port) {
    $lines = & "$env:SystemRoot\System32\netstat.exe" -ano -p tcp 2>$null
    $pattern = '^\s*TCP\s+(\S+):' + [regex]::Escape([string]$Port) + '\s+\S+\s+LISTENING\s+(\d+)\s*$'
    $items = foreach ($line in $lines) {
        if ($line -match $pattern) {
            [pscustomobject]@{
                Address = $Matches[1]
                Port = $Port
                Pid = [int]$Matches[2]
            }
        }
    }
    return @($items)
}

function Start-ProxyIfNeeded($Configuration) {
    $proxy = $Configuration.proxy
    if (Test-TcpPort -HostName ([string]$proxy.host) -Port ([int]$proxy.port)) {
        return
    }

    if (-not [bool]$proxy.autoStart) {
        throw "Proxy is not listening on $($proxy.host):$($proxy.port)."
    }

    $executable = Expand-EnvironmentPath ([string]$proxy.executablePath)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Proxy executable not found: $executable"
    }

    Write-Step "Starting proxy application: $executable"
    Start-Process -FilePath $executable | Out-Null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        if (Test-TcpPort -HostName ([string]$proxy.host) -Port ([int]$proxy.port)) {
            Write-Pass "Proxy is listening on $($proxy.host):$($proxy.port)."
            return
        }
    }
    throw "Proxy application started but port $($proxy.port) did not become ready."
}

function Invoke-CurlProbe([string]$ProxyType, [string]$ProxyHost, [int]$ProxyPort) {
    $curl = Get-Executable 'curl.exe' "$env:SystemRoot\System32\curl.exe"
    if (-not $curl) {
        return [pscustomobject]@{
            Success = $false
            Status = ''
            Detail = 'curl.exe not found'
            Target = ''
        }
    }

    # A single public connectivity endpoint can fail even when the proxy is
    # healthy. Try independent providers and accept the first expected status.
    $targets = @(
        [pscustomobject]@{
            Name = 'Google connectivity check'
            Url = 'http://connectivitycheck.gstatic.com/generate_204'
            ExpectedStatus = '204'
        },
        [pscustomobject]@{
            Name = 'Microsoft connectivity check'
            Url = 'http://www.msftconnecttest.com/connecttest.txt'
            ExpectedStatus = '200'
        },
        [pscustomobject]@{
            Name = 'Example.com'
            Url = 'http://example.com/'
            ExpectedStatus = '200'
        }
    )

    $attempts = @()
    foreach ($target in $targets) {
        $arguments = @('--silent', '--show-error', '--output', 'NUL', '--write-out', '%{http_code}', '--max-time', '12')
        if ($ProxyType -eq 'socks5') {
            $arguments += @('--socks5-hostname', "${ProxyHost}:$ProxyPort")
        }
        else {
            $arguments += @('--proxy', "http://${ProxyHost}:$ProxyPort")
        }
        $arguments += [string]$target.Url

        # ProcessStartInfo keeps curl stderr out of PowerShell's error stream.
        # Windows PowerShell 5.1 otherwise promotes native stderr to a
        # terminating NativeCommandError when ErrorActionPreference is Stop.
        $result = Invoke-NativeProcessWithTimeout $curl $arguments $null 15
        $status = @(
            $result.Output |
                ForEach-Object { ([string]$_).Trim() } |
                Where-Object { $_ -match '^\d{3}$' } |
                Select-Object -First 1
        )
        $statusText = if ($status.Count -gt 0) { [string]$status[0] } else { '' }
        $attempts += "$($target.Name):status=$(if ($statusText) { $statusText } else { 'none' }),exit=$($result.ExitCode)"
        if ($result.ExitCode -eq 0 -and $statusText -eq [string]$target.ExpectedStatus) {
            return [pscustomobject]@{
                Success = $true
                Status = $statusText
                Detail = "exit=$($result.ExitCode)"
                Target = [string]$target.Name
            }
        }
    }

    return [pscustomobject]@{
        Success = $false
        Status = ''
        Detail = ($attempts -join '; ')
        Target = ''
    }
}

function Get-KeyPath($Configuration) {
    return Expand-EnvironmentPath ([string]$Configuration.ssh.identityFile)
}

function Get-AuthenticationMode($Configuration) {
    $property = $Configuration.ssh.PSObject.Properties['authentication']
    if (-not $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return 'managedKey'
    }
    return [string]$property.Value
}

function Test-PasswordGatewayMode($Configuration) {
    return (Get-AuthenticationMode $Configuration) -ieq 'passwordGateway'
}

function Get-SshProcessEnvironment($Configuration) {
    if (-not (Test-PasswordGatewayMode $Configuration)) {
        return $null
    }

    foreach ($name in @('credentialTarget', 'askPassExecutable')) {
        if ((-not $Configuration.ssh.PSObject.Properties[$name]) -or
            [string]::IsNullOrWhiteSpace([string]$Configuration.ssh.$name)) {
            throw "Password gateway configuration is missing: ssh.$name"
        }
    }

    $askPass = Expand-EnvironmentPath ([string]$Configuration.ssh.askPassExecutable)
    if (-not (Test-Path -LiteralPath $askPass -PathType Leaf)) {
        throw "SSH AskPass helper not found: $askPass"
    }

    return @{
        SSH_ASKPASS = $askPass
        SSH_ASKPASS_REQUIRE = 'force'
        SSH_PROXY_BRIDGE_ASKPASS = '1'
        SSH_PROXY_BRIDGE_CREDENTIAL_TARGET = [string]$Configuration.ssh.credentialTarget
        DISPLAY = 'ssh-proxy-bridge:0'
    }
}

function Invoke-WithProcessEnvironment($EnvironmentVariables, [scriptblock]$Action) {
    if (-not $EnvironmentVariables) {
        return & $Action
    }

    $previous = @{}
    try {
        foreach ($name in $EnvironmentVariables.Keys) {
            $previous[$name] = [Environment]::GetEnvironmentVariable([string]$name, 'Process')
            [Environment]::SetEnvironmentVariable(
                [string]$name,
                [string]$EnvironmentVariables[$name],
                'Process')
        }
        return & $Action
    }
    finally {
        foreach ($name in $EnvironmentVariables.Keys) {
            [Environment]::SetEnvironmentVariable(
                [string]$name,
                $previous[$name],
                'Process')
        }
    }
}

function Get-SshTargetArguments($Configuration, [switch]$UseAlias) {
    if ($UseAlias) {
        return @([string]$Configuration.ssh.alias)
    }
    return @('-l', [string]$Configuration.ssh.user, [string]$Configuration.ssh.host)
}

function Get-SshBaseArguments($Configuration, [switch]$UseAlias) {
    $ssh = $Configuration.ssh
    $arguments = @(
        '-o', 'ConnectTimeout=10',
        '-o', 'ServerAliveInterval=30',
        '-o', 'ServerAliveCountMax=3'
    )
    if (Test-PasswordGatewayMode $Configuration) {
        $arguments += @(
            '-o', 'PreferredAuthentications=password',
            '-o', 'PubkeyAuthentication=no'
        )
    }
    else {
        $identity = Get-KeyPath $Configuration
        $arguments += @(
            '-o', 'IdentitiesOnly=yes',
            '-i', $identity
        )
    }
    $knownHostsProperty = $ssh.PSObject.Properties['userKnownHostsFile']
    if ($knownHostsProperty -and -not [string]::IsNullOrWhiteSpace([string]$knownHostsProperty.Value)) {
        $knownHostsPath = Expand-EnvironmentPath ([string]$knownHostsProperty.Value)
        $arguments += @(
            '-o', "UserKnownHostsFile=$knownHostsPath",
            '-o', 'StrictHostKeyChecking=yes'
        )
    }
    if (-not $UseAlias) {
        $arguments += @('-p', [string]$ssh.port)
    }
    return $arguments
}

function ConvertTo-NativeArgument([string]$Value) {
    if ($null -eq $Value -or $Value.Length -eq 0) {
        return '""'
    }
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-NativeProcessWithTimeout(
    [string]$FilePath,
    [string[]]$Arguments,
    [AllowNull()][string]$InputText,
    [int]$TimeoutSeconds,
    $EnvironmentVariables = $null
) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        $started = Invoke-WithProcessEnvironment $EnvironmentVariables {
            $process.Start()
        }
        if (-not $started) {
            throw "Could not start native process: $FilePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($null -ne $InputText) {
            $process.StandardInput.Write($InputText.Replace("`r", ''))
        }
        $process.StandardInput.Close()

        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) {
            try { $process.Kill() } catch { }
        }
        $process.WaitForExit()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $output = @()
        foreach ($text in @($stdout, $stderr)) {
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $output += @($text -split '\r?\n' | Where-Object { $_ -ne '' })
            }
        }
        if ($timedOut) {
            $output += "SSH command timed out after $TimeoutSeconds seconds."
        }

        return [pscustomobject]@{
            ExitCode = if ($timedOut) { 124 } else { $process.ExitCode }
            Output = @($output)
            TimedOut = $timedOut
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-SshOneShotArguments($Configuration, [switch]$UseAlias) {
    $arguments = @(Get-SshBaseArguments $Configuration -UseAlias:$UseAlias)
    $arguments += @('-o', 'ConnectionAttempts=1')
    if (Test-PasswordGatewayMode $Configuration) {
        $arguments += @(
            '-o', 'BatchMode=no',
            '-o', 'NumberOfPasswordPrompts=1'
        )
    }
    else {
        $arguments += @(
            '-o', 'BatchMode=yes',
            '-o', 'NumberOfPasswordPrompts=0'
        )
    }
    $arguments += @('-o', 'ClearAllForwardings=yes', '-T')
    return $arguments
}

function Test-SshLogin($Configuration, [switch]$Quiet) {
    $sshExe = Get-Executable 'ssh.exe'
    if (-not $sshExe) {
        return $false
    }
    if (-not (Test-PasswordGatewayMode $Configuration)) {
        $keyPath = Get-KeyPath $Configuration
        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            return $false
        }
    }
    $arguments = @(Get-SshOneShotArguments $Configuration)
    $arguments += @(Get-SshTargetArguments $Configuration)
    $arguments += 'printf SSH_PROXY_BRIDGE_LOGIN_OK'
    $environment = Get-SshProcessEnvironment $Configuration
    $result = Invoke-NativeProcessWithTimeout $sshExe $arguments $null 20 $environment
    $ok = ($result.ExitCode -eq 0 -and ($result.Output -join '') -eq 'SSH_PROXY_BRIDGE_LOGIN_OK')
    $label = if (Test-PasswordGatewayMode $Configuration) { 'SSH password gateway login' } else { 'SSH key login' }
    if (-not $Quiet) {
        if ($ok) {
            Write-Pass "$label succeeded."
        }
        elseif ($result.TimedOut) {
            Write-Fail "$label timed out after 20 seconds. The server did not finish the SSH handshake."
        }
        else {
            Write-Fail "$label is not ready."
        }
    }
    return $ok
}

function Ensure-SshKey($Configuration) {
    $sshKeygen = Get-Executable 'ssh-keygen.exe'
    if (-not $sshKeygen) {
        throw 'ssh-keygen.exe was not found.'
    }
    $keyPath = Get-KeyPath $Configuration
    if (Test-Path -LiteralPath $keyPath -PathType Leaf) {
        Write-Pass "SSH key already exists: $keyPath"
        return
    }

    $directory = Split-Path -Parent $keyPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }

    Write-Step 'Generating an Ed25519 key. Enter a local key passphrase when prompted.'
    & $sshKeygen -t ed25519 -a 64 -f $keyPath -C 'codex-remote-proxy'
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $keyPath)) {
        throw 'SSH key generation failed.'
    }
}

function Install-PublicKeyInteractively($Configuration) {
    Ensure-SshKey $Configuration
    $keyPath = Get-KeyPath $Configuration
    $publicKeyPath = "$keyPath.pub"
    $publicKey = (Get-Content -Raw -LiteralPath $publicKeyPath).Trim()
    if (-not $publicKey) {
        throw 'The SSH public key is empty.'
    }

    $publicKeyBytes = [Text.Encoding]::UTF8.GetBytes($publicKey + "`n")
    $publicKeyBase64 = [Convert]::ToBase64String($publicKeyBytes)
    $remoteCommand = "umask 077; mkdir -p ~/.ssh; chmod 700 ~/.ssh; touch ~/.ssh/authorized_keys; printf %s $publicKeyBase64 | base64 -d >> ~/.ssh/authorized_keys; chmod 600 ~/.ssh/authorized_keys"
    $arguments = @(
        '-o', 'ConnectTimeout=15',
        '-p', [string]$Configuration.ssh.port,
        "$($Configuration.ssh.user)@$($Configuration.ssh.host)",
        $remoteCommand
    )

    Write-Step 'Installing the public key. Type the server password at the ssh.exe prompt.'
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & (Get-Executable 'ssh.exe') @arguments
        $installExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($installExitCode -ne 0) {
        throw 'Public key installation failed.'
    }
    if (-not (Test-SshLogin $Configuration)) {
        throw 'The public key was copied, but key-only login validation failed.'
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Set-ManagedBlock([string]$Content, [string]$StartMarker, [string]$EndMarker, [string]$Block) {
    $pattern = '(?ms)^' + [regex]::Escape($StartMarker) + '.*?^' + [regex]::Escape($EndMarker) + '\s*'
    $clean = [regex]::Replace($Content, $pattern, '').TrimEnd()
    if ($clean) {
        return "$clean`r`n`r`n$Block`r`n"
    }
    return "$Block`r`n"
}

function Remove-ManagedBlock([string]$Content, [string]$StartMarker, [string]$EndMarker) {
    $pattern = '(?ms)^' + [regex]::Escape($StartMarker) + '.*?^' + [regex]::Escape($EndMarker) + '\s*'
    return ([regex]::Replace($Content, $pattern, '').TrimEnd() + "`r`n")
}

function Install-SshConfig($Configuration) {
    $sshDirectory = Join-Path $env:USERPROFILE '.ssh'
    $configPath = Join-Path $sshDirectory 'config'
    if (-not (Test-Path -LiteralPath $sshDirectory)) {
        New-Item -ItemType Directory -Path $sshDirectory | Out-Null
    }
    $content = if (Test-Path -LiteralPath $configPath) { Get-Content -Raw -LiteralPath $configPath } else { '' }
    $alias = [string]$Configuration.ssh.alias
    $start = "# >>> codex-remote-proxy:$alias >>>"
    $end = "# <<< codex-remote-proxy:$alias <<<"
    $authenticationDirectives = if (Test-PasswordGatewayMode $Configuration) {
@"
    PreferredAuthentications password
    PubkeyAuthentication no
"@
    }
    else {
        $identity = (Get-KeyPath $Configuration).Replace('\', '/')
@"
    IdentityFile "$identity"
    IdentitiesOnly yes
"@
    }
    $knownHostsDirectives = ''
    $knownHostsProperty = $Configuration.ssh.PSObject.Properties['userKnownHostsFile']
    if ($knownHostsProperty -and -not [string]::IsNullOrWhiteSpace([string]$knownHostsProperty.Value)) {
        $knownHostsPath = (Expand-EnvironmentPath ([string]$knownHostsProperty.Value)).Replace('\', '/')
        $knownHostsDirectives = @"
    UserKnownHostsFile "$knownHostsPath"
    StrictHostKeyChecking yes
"@
    }
    $block = @"
$start
Host $alias
    HostName $($Configuration.ssh.host)
    User $($Configuration.ssh.user)
    Port $($Configuration.ssh.port)
$authenticationDirectives
$knownHostsDirectives
    ServerAliveInterval 30
    ServerAliveCountMax 3
$end
"@
    $updated = Set-ManagedBlock $content $start $end $block.Trim()
    Write-Utf8NoBom $configPath $updated
    Write-Pass "SSH alias installed: $alias"
}

function Invoke-Ssh($Configuration, [string]$RemoteCommand, [switch]$UseAlias) {
    $sshExe = Get-Executable 'ssh.exe'
    $arguments = @(Get-SshOneShotArguments $Configuration -UseAlias:$UseAlias)
    $arguments += @(Get-SshTargetArguments $Configuration -UseAlias:$UseAlias)
    $arguments += $RemoteCommand
    $environment = Get-SshProcessEnvironment $Configuration
    return Invoke-NativeProcessWithTimeout $sshExe $arguments $null 35 $environment
}

function Invoke-SshWithInput($Configuration, [string]$RemoteCommand, [string]$InputText, [switch]$UseAlias) {
    $sshExe = Get-Executable 'ssh.exe'
    $arguments = @(Get-SshOneShotArguments $Configuration -UseAlias:$UseAlias)
    $arguments += @(Get-SshTargetArguments $Configuration -UseAlias:$UseAlias)
    $arguments += $RemoteCommand
    $environment = Get-SshProcessEnvironment $Configuration
    return Invoke-NativeProcessWithTimeout $sshExe $arguments $InputText 35 $environment
}

function Install-RemoteProxyEnvironment($Configuration) {
    if (-not (Test-SshLogin $Configuration -Quiet)) {
        throw 'SSH login is required before remote setup.'
    }
    $hostName = [string]$Configuration.ssh.remoteProxyHost
    $port = [int]$Configuration.ssh.remoteProxyPort
    $start = '# >>> codex-remote-proxy >>>'
    $end = '# <<< codex-remote-proxy <<<'
    $block = @"
$start
if timeout 1 bash -c '</dev/tcp/$hostName/$port' 2>/dev/null; then
    export HTTP_PROXY='http://$hostName`:$port'
    export HTTPS_PROXY="`$HTTP_PROXY"
    export http_proxy="`$HTTP_PROXY"
    export https_proxy="`$HTTPS_PROXY"
    export NO_PROXY='localhost,127.0.0.1,::1'
    export no_proxy="`$NO_PROXY"
else
    unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy ALL_PROXY all_proxy
fi
$end
"@
    $script = @"
set -eu
file="`$HOME/.bashrc"
touch "`$file"
tmp=`$(mktemp)
awk '
BEGIN { skip=0 }
`$0 == "$start" { skip=1; next }
`$0 == "$end" { skip=0; next }
skip == 0 { print }
' "`$file" > "`$tmp"
printf '\n' >> "`$tmp"
printf %s __BLOCK_BASE64__ | base64 -d >> "`$tmp"
printf '\n' >> "`$tmp"
mv "`$tmp" "`$file"
"@
    $blockBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($block.Trim()))
    $script = $script.Replace('__BLOCK_BASE64__', $blockBase64)
    $scriptBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $remoteCommand = "printf %s $scriptBase64 | base64 -d | bash"
    $result = Invoke-Ssh $Configuration $remoteCommand
    if ($result.ExitCode -ne 0) {
        throw "Remote environment setup failed: $($result.Output -join ' ')"
    }
    Write-Pass 'Remote ~/.bashrc proxy environment block installed.'
}

function Test-ManagedTunnelProcess($Configuration, $Process) {
    if (-not $Process -or $Process.ProcessName -ne 'ssh') {
        return $false
    }

    # When available, verify the exact reverse-forward and SSH alias. Some
    # restricted Windows sessions cannot read Win32_Process.CommandLine, so
    # process name/path remain the safe fallback for an app-owned PID file.
    try {
        $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction Stop
        if ($details) {
            $remoteSpec = "$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort):$($Configuration.proxy.host):$($Configuration.proxy.port)"
            $expectedAlias = [string]$Configuration.ssh.alias
            return $details.Name -eq 'ssh.exe' -and
                $details.CommandLine -match [regex]::Escape($remoteSpec) -and
                $details.CommandLine -match [regex]::Escape($expectedAlias)
        }
    }
    catch {
        # Fall back below. Get-Process still proves that the saved PID belongs
        # to an ssh.exe process owned by the current Windows session.
    }

    try {
        $path = [string]$Process.Path
        return -not $path -or [IO.Path]::GetFileName($path) -eq 'ssh.exe'
    }
    catch {
        return $true
    }
}

function Get-TunnelProcess($Configuration) {
    $pidPath = Get-StatePath 'tunnel.pid'
    if (-not (Test-Path -LiteralPath $pidPath)) {
        return $null
    }
    $savedPid = 0
    if (-not [int]::TryParse((Get-Content -Raw -LiteralPath $pidPath).Trim(), [ref]$savedPid)) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        return $null
    }
    $process = Get-Process -Id $savedPid -ErrorAction SilentlyContinue
    if (-not (Test-ManagedTunnelProcess $Configuration $process)) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        return $null
    }
    return $process
}

function Stop-Tunnel($Configuration) {
    $process = Get-TunnelProcess $Configuration
    $pidPath = Get-StatePath 'tunnel.pid'
    if (-not $process) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        Write-Warn 'No managed SSH tunnel is running.'
        return
    }

    if (-not (Test-ManagedTunnelProcess $Configuration $process)) {
        throw "Refusing to stop PID $($process.Id): it does not match the managed tunnel."
    }
    Stop-Process -Id $process.Id -Force
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    Write-Pass "Stopped SSH tunnel PID $($process.Id)."
}

function Get-TunnelFailureSummary {
    $stderr = Get-StatePath 'tunnel.stderr.log'
    if (-not (Test-Path -LiteralPath $stderr -PathType Leaf)) {
        return 'The managed SSH process is not running.'
    }

    $summary = ((Get-Content -LiteralPath $stderr -Tail 5 -ErrorAction SilentlyContinue) -join ' ').Trim()
    if ([string]::IsNullOrWhiteSpace($summary)) {
        return 'The managed SSH process exited without an error message.'
    }
    if ($summary.Length -gt 400) {
        $summary = $summary.Substring(0, 400)
    }
    return $summary
}

function Write-MonitorEvent([string]$Message, [string]$Level = 'INFO') {
    $path = Get-StatePath 'monitor.events.log'
    try {
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -gt 1MB) {
            $previous = Get-StatePath 'monitor.events.previous.log'
            Move-Item -LiteralPath $path -Destination $previous -Force
        }
        $safe = ($Message -replace '[\r\n]+', ' ').Trim()
        $safe = $safe -replace '(?i)(password|passwd|pwd)\s*[:=]\s*\S+', '$1=<redacted>'
        Add-Content -LiteralPath $path -Encoding UTF8 -Value (
            '{0} [{1}] {2}' -f ([DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz')), $Level, $safe)
    }
    catch {
        # Monitoring must never take down a healthy tunnel because a diagnostic
        # log could not be rotated or written.
    }
}

function Test-ManagedMonitorProcess($Process) {
    if (-not $Process -or $Process.ProcessName -notin @('powershell', 'pwsh')) {
        return $false
    }

    try {
        $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction Stop
        if ($details) {
            $scriptPath = [IO.Path]::GetFullPath($PSCommandPath)
            $configPath = (Resolve-Path -LiteralPath $Config).Path
            return $details.CommandLine -match [regex]::Escape($scriptPath) -and
                $details.CommandLine -match '(?i)(?:^|\s)monitor(?:\s|$)' -and
                $details.CommandLine -match [regex]::Escape($configPath)
        }
    }
    catch {
        # Fall back to the process name for restricted Windows sessions. The
        # PID file lives in this profile's private runtime directory.
    }

    return $true
}

function Get-MonitorProcess {
    $pidPath = Get-StatePath 'monitor.pid'
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
        return $null
    }

    $savedPid = 0
    if (-not [int]::TryParse((Get-Content -Raw -LiteralPath $pidPath).Trim(), [ref]$savedPid)) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        return $null
    }

    $process = Get-Process -Id $savedPid -ErrorAction SilentlyContinue
    if (-not (Test-ManagedMonitorProcess $process)) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        return $null
    }
    return $process
}

function Stop-Monitor {
    $process = Get-MonitorProcess
    $pidPath = Get-StatePath 'monitor.pid'
    if (-not $process) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        Write-Warn 'Automatic tunnel repair is not running.'
        return
    }

    Stop-Process -Id $process.Id -Force
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    Write-MonitorEvent "Automatic repair monitor stopped (PID $($process.Id))."
    Write-Pass "Stopped automatic repair monitor PID $($process.Id)."
}

function Enter-RepairLock([int]$TimeoutSeconds = 15) {
    $path = Get-StatePath 'repair.lock'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            return [IO.File]::Open(
                $path,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
        }
        catch [IO.IOException] {
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw 'Another tunnel repair operation is still running.'
}

function Repair-Tunnel($Configuration) {
    $repairLock = Enter-RepairLock
    try {
        if (-not (Test-TcpPort ([string]$Configuration.proxy.host) ([int]$Configuration.proxy.port) 1500)) {
            throw "The Windows proxy is not listening at $($Configuration.proxy.host):$($Configuration.proxy.port)."
        }

        $existing = Get-TunnelProcess $Configuration
        if ($existing) {
            if (Test-RemoteProxy $Configuration) {
                Write-MonitorEvent "Tunnel health check passed (PID $($existing.Id))."
                Write-Pass "The managed tunnel is healthy (PID $($existing.Id)); no rebuild was needed."
                return $existing
            }

            Write-MonitorEvent (
                "Tunnel process PID $($existing.Id) was alive but the remote proxy probe failed; rebuilding it.") 'WARN'
            Stop-Tunnel $Configuration
        }
        else {
            Write-MonitorEvent ("Tunnel was not running. Last SSH message: $(Get-TunnelFailureSummary)") 'WARN'
        }

        if (-not (Test-SshLogin $Configuration -Quiet)) {
            throw 'SSH login is not ready; the tunnel cannot be rebuilt yet.'
        }

        $process = Start-Tunnel $Configuration
        if (-not (Test-RemoteProxy $Configuration)) {
            Stop-Tunnel $Configuration
            throw 'The rebuilt SSH tunnel did not pass the remote proxy probe.'
        }

        Write-MonitorEvent "Tunnel repair succeeded with PID $($process.Id)."
        Write-Pass "Tunnel repair succeeded (PID $($process.Id))."
        return $process
    }
    catch {
        Write-MonitorEvent $_.Exception.Message 'ERROR'
        throw
    }
    finally {
        $repairLock.Dispose()
    }
}

function Start-Monitor($Configuration) {
    $existing = Get-MonitorProcess
    if ($existing) {
        Write-Pass "Automatic tunnel repair is already running (PID $($existing.Id))."
        return $existing
    }

    $powerShell = Get-Executable 'powershell.exe'
    if (-not $powerShell) {
        throw 'powershell.exe was not found; automatic tunnel repair cannot start.'
    }

    $scriptPath = [IO.Path]::GetFullPath($PSCommandPath)
    $configPath = (Resolve-Path -LiteralPath $Config).Path
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $scriptPath,
        'monitor', '-Config', $configPath
    )
    $argumentLine = ($arguments | ForEach-Object { ConvertTo-NativeArgument ([string]$_) }) -join ' '
    $stdout = Get-StatePath 'monitor.stdout.log'
    $stderr = Get-StatePath 'monitor.stderr.log'
    $process = Start-Process -FilePath $powerShell -ArgumentList $argumentLine `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Start-Sleep -Seconds 1
    if ($process.HasExited) {
        $detail = if (Test-Path -LiteralPath $stderr) { Get-Content -Raw -LiteralPath $stderr } else { '' }
        throw "Automatic tunnel repair monitor exited immediately: $detail"
    }

    Write-Utf8NoBom (Get-StatePath 'monitor.pid') ([string]$process.Id)
    Write-MonitorEvent "Automatic repair monitor started (PID $($process.Id))."
    Write-Pass "Automatic tunnel repair started (PID $($process.Id))."
    return $process
}

function Get-MonitorRetryDelay([int]$FailureCount) {
    $delays = @(2, 5, 10, 30, 60, 300)
    return $delays[[Math]::Min([Math]::Max($FailureCount - 1, 0), $delays.Count - 1)]
}

function Invoke-TunnelMonitor($Configuration) {
    $other = Get-MonitorProcess
    if ($other -and $other.Id -ne $PID) {
        Write-Warn "Another automatic repair monitor is already running (PID $($other.Id))."
        return
    }

    Write-Utf8NoBom (Get-StatePath 'monitor.pid') ([string]$PID)
    Write-MonitorEvent "Automatic repair monitor entered its health loop (PID $PID)."
    $failureCount = 0
    $nextRemoteProbe = [DateTime]::MinValue
    try {
        while ($true) {
            try {
                $tunnel = Get-TunnelProcess $Configuration
                if (-not $tunnel -or [DateTime]::UtcNow -ge $nextRemoteProbe) {
                    $null = Repair-Tunnel $Configuration
                    $nextRemoteProbe = [DateTime]::UtcNow.AddSeconds(60)
                }
                $failureCount = 0
                Start-Sleep -Seconds 20
            }
            catch {
                $failureCount++
                $delay = Get-MonitorRetryDelay $failureCount
                Write-MonitorEvent (
                    "Repair attempt $failureCount failed; retrying in $delay seconds. $($_.Exception.Message)") 'WARN'
                Start-Sleep -Seconds $delay
            }
        }
    }
    finally {
        $pidPath = Get-StatePath 'monitor.pid'
        if ((Test-Path -LiteralPath $pidPath) -and
            (Get-Content -Raw -LiteralPath $pidPath).Trim() -eq [string]$PID) {
            Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-Tunnel($Configuration) {
    $existing = Get-TunnelProcess $Configuration
    if ($existing) {
        Write-Pass "SSH tunnel is already running (PID $($existing.Id))."
        return $existing
    }

    if (-not (Test-SshLogin $Configuration -Quiet)) {
        throw 'SSH login is not ready. Complete SSH initialization first.'
    }
    $sshExe = Get-Executable 'ssh.exe'
    $remoteSpec = "$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort):$($Configuration.proxy.host):$($Configuration.proxy.port)"
    $arguments = Get-SshBaseArguments $Configuration -UseAlias
    $arguments += @('-N', '-T')
    if (Test-PasswordGatewayMode $Configuration) {
        $arguments += @('-o', 'BatchMode=no', '-o', 'NumberOfPasswordPrompts=1')
    }
    else {
        $arguments += @('-o', 'BatchMode=yes', '-o', 'NumberOfPasswordPrompts=0')
    }
    $arguments += @(
        '-o', 'ExitOnForwardFailure=yes',
        '-R', $remoteSpec,
        [string]$Configuration.ssh.alias
    )
    $state = Get-StateDirectory
    $stdout = Join-Path $state 'tunnel.stdout.log'
    $stderr = Join-Path $state 'tunnel.stderr.log'
    $environment = Get-SshProcessEnvironment $Configuration
    $process = Invoke-WithProcessEnvironment $environment {
        Start-Process -FilePath $sshExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    }
    Start-Sleep -Seconds 2
    if ($process.HasExited) {
        $detail = if (Test-Path -LiteralPath $stderr) { Get-Content -Raw -LiteralPath $stderr } else { 'no error output' }
        throw "SSH tunnel exited immediately: $detail"
    }
    Write-Utf8NoBom (Get-StatePath 'tunnel.pid') ([string]$process.Id)
    Write-Pass "SSH reverse tunnel started (PID $($process.Id))."
    return $process
}

function Test-RemoteProxy($Configuration) {
    $url = "http://$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort)"
    $urlBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($url))
    $script = @'
set -u
proxy=$(printf %s __PROXY_BASE64__ | base64 -d)
probe() {
    name="$1"
    target="$2"
    expected="$3"
    code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 --proxy "$proxy" "$target" 2>/dev/null || true)
    if [ "$code" = "$expected" ]; then
        printf 'CODEX_REMOTE_PROXY_OK:%s:%s\n' "$name" "$code"
        exit 0
    fi
    printf 'PROBE_FAILED:%s:%s\n' "$name" "${code:-none}"
}
probe gstatic http://connectivitycheck.gstatic.com/generate_204 204
probe microsoft http://www.msftconnecttest.com/connecttest.txt 200
probe example http://example.com/ 200
exit 1
'@
    $script = $script.Replace('__PROXY_BASE64__', $urlBase64)
    $scriptBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $command = "printf %s $scriptBase64 | base64 -d | bash"
    $result = Invoke-Ssh $Configuration $command -UseAlias
    $ok = ($result.ExitCode -eq 0 -and ($result.Output -join '') -match 'CODEX_REMOTE_PROXY_OK')
    if ($ok) {
        $marker = (($result.Output | Where-Object { $_ -match 'CODEX_REMOTE_PROXY_OK' } | Select-Object -First 1) -as [string]).Trim()
        Write-Pass "The server can reach the internet through the Windows proxy tunnel ($marker)."
    }
    else { Write-Fail "Remote proxy validation failed: $($result.Output -join ' ')" }
    return $ok
}

function Start-VsCode($Configuration) {
    if (-not [bool]$Configuration.vscode.launch) {
        return
    }
    $code = Get-Executable 'code.cmd'
    if (-not $code) {
        throw 'VS Code command (code.cmd) was not found.'
    }
    $arguments = @('--new-window', '--remote', "ssh-remote+$($Configuration.ssh.alias)")
    $workspace = [string]$Configuration.vscode.remoteWorkspace
    if ($workspace) {
        $arguments += $workspace
    }
    # A newly started VS Code instance can outlive this PowerShell process. Keep
    # it away from the GUI-owned stdout/stderr pipes so the GUI can observe that
    # the workflow itself has completed instead of waiting for VS Code to exit.
    $state = Get-StateDirectory
    Get-ChildItem -LiteralPath $state -Filter 'vscode.*.log' -File -ErrorAction SilentlyContinue |
        Where-Object LastWriteTimeUtc -lt ([DateTime]::UtcNow.AddDays(-7)) |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $launchId = '{0}.{1}' -f ([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')), $PID
    $stdout = Join-Path $state "vscode.$launchId.stdout.log"
    $stderr = Join-Path $state "vscode.$launchId.stderr.log"
    $environment = Get-SshProcessEnvironment $Configuration
    Invoke-WithProcessEnvironment $environment {
        Start-Process -FilePath $code -ArgumentList $arguments -WindowStyle Hidden `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
    }
    Write-Pass 'VS Code Remote-SSH launch requested.'
}

function Invoke-Doctor($Configuration) {
    $failures = 0
    Write-Step 'Checking local commands'
    $commands = @('ssh.exe', 'code.cmd', 'curl.exe')
    if (-not (Test-PasswordGatewayMode $Configuration)) {
        $commands += 'ssh-keygen.exe'
    }
    foreach ($name in $commands) {
        $path = Get-Executable $name
        if ($path) { Write-Pass "$name -> $path" } else { Write-Fail "$name was not found."; $failures++ }
    }

    Write-Step 'Checking local proxy'
    $listeners = @(Get-PortListeners ([int]$Configuration.proxy.port))
    if ($listeners.Count -eq 0) {
        Write-Fail "No TCP listener was found on port $($Configuration.proxy.port)."
        $failures++
    }
    else {
        Write-Pass "Proxy port $($Configuration.proxy.port) is listening."
        $publicBind = $listeners | Where-Object Address -In @('0.0.0.0', '[::]', '::')
        if ($publicBind) {
            Write-Warn 'The proxy listens on all interfaces. Prefer loopback-only binding unless LAN access is intentional.'
        }
        foreach ($listener in $listeners) {
            $process = Get-Process -Id $listener.Pid -ErrorAction SilentlyContinue
            Write-Host "       $($listener.Address):$($listener.Port) PID=$($listener.Pid) process=$($process.ProcessName)"
        }
    }

    $http = Invoke-CurlProbe 'http' ([string]$Configuration.proxy.host) ([int]$Configuration.proxy.port)
    $socks = Invoke-CurlProbe 'socks5' ([string]$Configuration.proxy.host) ([int]$Configuration.proxy.port)
    if ($http.Success) {
        if ($http.Status -eq '204') {
            # Preserve the original marker so an already-running GUI from an
            # older release can still recognize the successful diagnosis.
            Write-Pass "HTTP proxy probe returned 204 via $($http.Target)."
        }
        else {
            Write-Pass "HTTP proxy probe succeeded via $($http.Target) (status $($http.Status))."
        }
    }
    else {
        Write-Fail "HTTP proxy probe failed ($($http.Detail))."
        $failures++
    }
    if ($socks.Success) {
        Write-Pass "SOCKS5 proxy probe succeeded via $($socks.Target) (status $($socks.Status))."
    }
    else {
        Write-Warn "SOCKS5 probe failed ($($socks.Detail))."
    }

    Write-Step 'Checking SSH target'
    if (Test-TcpPort ([string]$Configuration.ssh.host) ([int]$Configuration.ssh.port) 5000) {
        Write-Pass "SSH endpoint is reachable at $($Configuration.ssh.host):$($Configuration.ssh.port)."
    }
    else {
        Write-Fail "SSH endpoint is not reachable at $($Configuration.ssh.host):$($Configuration.ssh.port)."
        $failures++
    }
    if (Test-PasswordGatewayMode $Configuration) {
        Write-Pass 'SSH authentication mode: password gateway with Windows Credential Manager.'
        if (-not (Test-SshLogin $Configuration)) { $failures++ }
    }
    else {
        $keyPath = Get-KeyPath $Configuration
        if (Test-Path -LiteralPath $keyPath -PathType Leaf) {
            Write-Pass "SSH private key exists: $keyPath"
            if (-not (Test-SshLogin $Configuration)) { $failures++ }
        }
        else {
            Write-Warn "SSH key has not been created yet: $keyPath"
        }
    }

    Write-Step 'Checking managed reverse tunnel'
    $tunnel = Get-TunnelProcess $Configuration
    if ($tunnel) {
        Write-Pass "Managed SSH tunnel is running (PID $($tunnel.Id))."
        if (-not (Test-RemoteProxy $Configuration)) {
            $failures++
        }
    }
    else {
        Write-Warn 'Managed SSH tunnel is not running. Use start to establish it.'
    }

    $monitor = Get-MonitorProcess
    if ($monitor) {
        Write-Pass "Automatic tunnel repair is running (PID $($monitor.Id))."
    }
    else {
        Write-Warn 'Automatic tunnel repair is not running. Use start or repair to enable it.'
    }
    $monitorLog = Get-StatePath 'monitor.events.log'
    if (Test-Path -LiteralPath $monitorLog -PathType Leaf) {
        Write-Host '       Recent repair events:'
        Get-Content -LiteralPath $monitorLog -Tail 8 -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Host "       $_" }
    }

    Write-Step 'Checking VS Code extensions'
    $extensionRoot = Join-Path $env:USERPROFILE '.vscode\extensions'
    $codex = Get-ChildItem -LiteralPath $extensionRoot -Directory -ErrorAction SilentlyContinue | Where-Object Name -Like "$($Configuration.vscode.extensionId)-*" | Select-Object -First 1
    $remoteSsh = Get-ChildItem -LiteralPath $extensionRoot -Directory -ErrorAction SilentlyContinue | Where-Object Name -Like 'ms-vscode-remote.remote-ssh-*' | Select-Object -First 1
    if ($codex) { Write-Pass "Codex extension installed: $($codex.Name)" } else { Write-Fail 'Codex extension is not installed locally.'; $failures++ }
    if ($remoteSsh) { Write-Pass "Remote-SSH extension installed: $($remoteSsh.Name)" } else { Write-Fail 'Remote-SSH extension is not installed.'; $failures++ }

    Write-Host ''
    if ($failures -eq 0) {
        Write-Pass 'Doctor completed without failures.'
        return 0
    }
    Write-Warn "Doctor completed with $failures blocking item(s)."
    return 1
}

function Remove-SshConfig($Configuration) {
    $configPath = Join-Path $env:USERPROFILE '.ssh\config'
    if (-not (Test-Path -LiteralPath $configPath)) { return }
    $alias = [string]$Configuration.ssh.alias
    $start = "# >>> codex-remote-proxy:$alias >>>"
    $end = "# <<< codex-remote-proxy:$alias <<<"
    $content = Get-Content -Raw -LiteralPath $configPath
    Write-Utf8NoBom $configPath (Remove-ManagedBlock $content $start $end)
    Write-Pass "Removed managed SSH alias: $alias"
}

$configuration = Read-Configuration $Config
$profileIdProperty = $configuration.PSObject.Properties['profileId']
if ($profileIdProperty -and -not [string]::IsNullOrWhiteSpace([string]$profileIdProperty.Value)) {
    $resolvedConfigPath = (Resolve-Path -LiteralPath $Config).Path
    $script:StateDirectory = Join-Path (Split-Path -Parent $resolvedConfigPath) 'state'
}
if ($RemoteWorkspace) {
    $configuration.vscode.remoteWorkspace = $RemoteWorkspace
}

try {
    switch ($Command) {
        'doctor' {
            $doctorExitCode = Invoke-Doctor $configuration
            if ($doctorExitCode -ne 0) {
                throw 'Doctor found one or more blocking items.'
            }
        }
        'bootstrap-key' {
            Install-PublicKeyInteractively $configuration
            Install-SshConfig $configuration
            Write-Pass 'SSH bootstrap is complete.'
        }
        'setup' {
            Install-SshConfig $configuration
            if (-not (Test-SshLogin $configuration)) {
                throw 'Complete SSH initialization in SSH Proxy Bridge first.'
            }
            Install-RemoteProxyEnvironment $configuration
            Write-Pass 'Setup is complete.'
        }
        'start' {
            Start-ProxyIfNeeded $configuration
            Install-SshConfig $configuration
            if (-not (Test-SshLogin $configuration)) {
                throw 'Complete SSH initialization in SSH Proxy Bridge first.'
            }
            $null = Start-Tunnel $configuration
            if (-not (Test-RemoteProxy $configuration)) {
                throw 'The SSH tunnel is running, but remote proxy validation failed.'
            }
            $null = Start-Monitor $configuration
            Install-RemoteProxyEnvironment $configuration
            Start-VsCode $configuration
            Write-Pass 'Codex remote proxy workflow started.'
        }
        'repair' {
            Start-ProxyIfNeeded $configuration
            Install-SshConfig $configuration
            $null = Repair-Tunnel $configuration
            $null = Start-Monitor $configuration
            Write-Pass 'Tunnel repair and automatic monitoring are ready.'
        }
        'monitor' {
            Invoke-TunnelMonitor $configuration
        }
        'status' {
            $proxyOk = Test-TcpPort ([string]$configuration.proxy.host) ([int]$configuration.proxy.port)
            $tunnel = Get-TunnelProcess $configuration
            $monitor = Get-MonitorProcess
            Write-Host "Proxy:  $(if ($proxyOk) { 'running' } else { 'not ready' })"
            Write-Host "Tunnel: $(if ($tunnel) { "running (PID $($tunnel.Id))" } else { 'stopped' })"
            Write-Host "Auto repair: $(if ($monitor) { "running (PID $($monitor.Id))" } else { 'stopped' })"
            $loginLabel = if (Test-PasswordGatewayMode $configuration) { 'SSH password gateway login' } else { 'SSH key login' }
            Write-Host "$loginLabel`: $(if (Test-SshLogin $configuration -Quiet) { 'ready' } else { 'not ready' })"
        }
        'stop' {
            Stop-Monitor
            Stop-Tunnel $configuration
        }
        'uninstall' {
            if (-not $Force) {
                $answer = Read-Host 'Remove managed SSH and remote proxy configuration? Type YES to continue'
                if ($answer -ne 'YES') { Write-Warn 'Uninstall cancelled.'; return }
            }
            Stop-Monitor
            Stop-Tunnel $configuration
            Remove-SshConfig $configuration
            if (Test-SshLogin $configuration -Quiet) {
                $start = '# >>> codex-remote-proxy >>>'
                $end = '# <<< codex-remote-proxy <<<'
                $script = @"
set -eu
file="`$HOME/.bashrc"
test -f "`$file" || exit 0
tmp=`$(mktemp)
awk '
BEGIN { skip=0 }
`$0 == "$start" { skip=1; next }
`$0 == "$end" { skip=0; next }
skip == 0 { print }
' "`$file" > "`$tmp"
mv "`$tmp" "`$file"
"@
                $scriptBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
                $remoteCommand = "printf %s $scriptBase64 | base64 -d | bash"
                $result = Invoke-Ssh $configuration $remoteCommand
                if ($result.ExitCode -eq 0) { Write-Pass 'Removed remote proxy environment block.' }
                else { Write-Warn 'Could not remove the remote proxy environment block.' }
            }
            if (Test-PasswordGatewayMode $configuration) {
                Write-Pass 'Uninstall completed. The saved Windows credential was preserved.'
            }
            else {
                Write-Pass 'Uninstall completed. The SSH private key was preserved.'
            }
        }
    }
}
catch {
    Write-Fail $_.Exception.Message
    throw
}
