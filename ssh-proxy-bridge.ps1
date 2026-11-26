[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('doctor', 'bootstrap-key', 'setup', 'start', 'check-direct', 'start-tunnel', 'repair', 'reload-vscode', 'install-codex', 'monitor', 'status', 'stop', 'uninstall')]
    [string]$Command = 'doctor',

    [string]$Config,

    [string]$RemoteWorkspace,

    [ValidateSet('auto', 'direct', 'proxy')]
    [string]$ApplicationRoute = 'auto',

    [ValidateSet('proxy', 'direct', 'upload')]
    [string]$ExtensionInstallRoute = 'proxy',

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
    $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:unknown'
    $script:LastSshLoginReason = 'unknown'
    $sshExe = Get-Executable 'ssh.exe'
    if (-not $sshExe) {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:ssh-command-missing'
        $script:LastSshLoginReason = 'ssh-command-missing'
        if (-not $Quiet) {
            Write-Fail 'ssh.exe was not found.'
        }
        return $false
    }
    if (-not (Test-PasswordGatewayMode $Configuration)) {
        $keyPath = Get-KeyPath $Configuration
        try {
            $keyExists = Test-Path -LiteralPath $keyPath -PathType Leaf
        }
        catch {
            $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:key-access-denied'
            $script:LastSshLoginReason = 'key-access-denied'
            if (-not $Quiet) {
                Write-Fail 'The SSH private key cannot be read by the current Windows user.'
            }
            return $false
        }
        if (-not $keyExists) {
            $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:key-missing'
            $script:LastSshLoginReason = 'key-missing'
            if (-not $Quiet) {
                Write-Fail "The SSH private key does not exist: $keyPath"
            }
            return $false
        }
    }
    $arguments = @(Get-SshOneShotArguments $Configuration)
    $arguments += @(Get-SshTargetArguments $Configuration)
    $arguments += 'printf SSH_PROXY_BRIDGE_LOGIN_OK'
    $environment = Get-SshProcessEnvironment $Configuration
    try {
        $result = Invoke-NativeProcessWithTimeout $sshExe $arguments $null 20 $environment
    }
    catch {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:check-failed'
        $script:LastSshLoginReason = 'check-failed'
        if (-not $Quiet) {
            Write-Fail "SSH login check could not run: $($_.Exception.Message)"
        }
        return $false
    }
    $ok = ($result.ExitCode -eq 0 -and ($result.Output -join '') -eq 'SSH_PROXY_BRIDGE_LOGIN_OK')
    $label = if (Test-PasswordGatewayMode $Configuration) { 'SSH password gateway login' } else { 'SSH key login' }
    $detail = $result.Output -join ' '
    if ($ok) {
        $script:LastSshLoginMarker = 'SSH_LOGIN_READY'
        $script:LastSshLoginReason = 'ready'
    }
    elseif ($result.TimedOut -or $detail -match '(?i)connection timed out|operation timed out') {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:timeout'
        $script:LastSshLoginReason = 'timeout'
    }
    elseif ($detail -match '(?i)REMOTE HOST IDENTIFICATION HAS CHANGED|host key verification failed') {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:host-key'
        $script:LastSshLoginReason = 'host-key'
    }
    elseif ($detail -match '(?i)permission denied') {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:authentication-rejected'
        $script:LastSshLoginReason = 'authentication-rejected'
    }
    elseif ($detail -match '(?i)connection refused') {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:connection-refused'
        $script:LastSshLoginReason = 'connection-refused'
    }
    elseif ($detail -match '(?i)could not resolve hostname|name or service not known') {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:dns'
        $script:LastSshLoginReason = 'dns'
    }
    else {
        $script:LastSshLoginMarker = 'SSH_LOGIN_NOT_READY:connection-failed'
        $script:LastSshLoginReason = 'connection-failed'
    }
    if (-not $Quiet) {
        if ($ok) {
            Write-Pass "$label succeeded."
        }
        elseif ($script:LastSshLoginReason -eq 'timeout') {
            Write-Fail "$label timed out after 20 seconds. The server did not finish the SSH handshake."
        }
        elseif ($script:LastSshLoginReason -eq 'host-key') {
            Write-Fail "$label was blocked because the saved server host key no longer matches."
        }
        elseif ($script:LastSshLoginReason -eq 'authentication-rejected') {
            Write-Fail "$label was rejected by the server."
        }
        elseif ($script:LastSshLoginReason -eq 'connection-refused') {
            Write-Fail "$label could not connect because the SSH port refused the connection."
        }
        elseif ($script:LastSshLoginReason -eq 'dns') {
            Write-Fail "$label could not resolve the server address."
        }
        else {
            Write-Fail "$label is not ready."
        }
    }
    return $ok
}

function Write-SshLoginMarkers {
    Write-Host "SSH login marker: $($script:LastSshLoginMarker)"
    Write-Host "SSH login reason: $($script:LastSshLoginReason)"
}

function Assert-SshLoginReady($Configuration) {
    if (Test-SshLogin $Configuration) {
        Write-SshLoginMarkers
        return
    }

    Write-SshLoginMarkers
    Write-Host 'Connection diagnosis: ssh-not-ready'
    Write-Host 'Application network marker: APPLICATION_NETWORK_SKIPPED:ssh-not-ready'
    Write-Host 'Application network reason: ssh-not-ready'
    throw 'SSH login is not ready. Check the server address, port, saved credential, and SSH initialization.'
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

function Invoke-Ssh($Configuration, [string]$RemoteCommand, [switch]$UseAlias, [switch]$ReadOnly) {
    $sshExe = Get-Executable 'ssh.exe'
    $arguments = @(Get-SshOneShotArguments $Configuration -UseAlias:$UseAlias)
    if ($ReadOnly) {
        $arguments = @('-o', 'StrictHostKeyChecking=yes', '-o', 'UpdateHostKeys=no', '-o', 'ControlPath=none') + $arguments
    }
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

function Install-RemoteProxyEnvironment(
    $Configuration,
    [ValidateSet('direct', 'proxy')]
    [string]$Route = 'proxy'
) {
    if (-not (Test-SshLogin $Configuration -Quiet)) {
        throw 'SSH login is required before remote setup.'
    }
    $hostName = [string]$Configuration.ssh.remoteProxyHost
    $port = [int]$Configuration.ssh.remoteProxyPort
    $start = '# >>> codex-remote-proxy >>>'
    $end = '# <<< codex-remote-proxy <<<'
    $block = if ($Route -eq 'direct') {
        @"
$start
unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy ALL_PROXY all_proxy
export NO_PROXY='localhost,127.0.0.1,::1'
export no_proxy="`$NO_PROXY"
export SSH_PROXY_BRIDGE_ROUTE='direct'
$end
"@
    }
    else {
        @"
$start
if timeout 1 bash -c '</dev/tcp/$hostName/$port' 2>/dev/null; then
    export HTTP_PROXY='http://$hostName`:$port'
    export HTTPS_PROXY="`$HTTP_PROXY"
    export http_proxy="`$HTTP_PROXY"
    export https_proxy="`$HTTPS_PROXY"
    export NO_PROXY='localhost,127.0.0.1,::1'
    export no_proxy="`$NO_PROXY"
    export SSH_PROXY_BRIDGE_ROUTE='proxy'
else
    unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy ALL_PROXY all_proxy
    unset SSH_PROXY_BRIDGE_ROUTE
fi
$end
"@
    }
    $script = @"
set -eu
file="`$HOME/.bashrc"
touch "`$file"
tmp=`$(mktemp)
trap 'rm -f "`$tmp"' EXIT
awk '
BEGIN { skip=0 }
{
    normalized=`$0
    sub(/\r`$/, "", normalized)
    if (normalized == "$start") { skip=1; next }
    if (normalized == "$end") { skip=0; next }
    if (skip == 0) {
        sub(/\r`$/, "", `$0)
        print
    }
}
' "`$file" > "`$tmp"
printf '\n' >> "`$tmp"
printf %s __BLOCK_BASE64__ | base64 -d >> "`$tmp"
printf '\n' >> "`$tmp"
bash -n "`$tmp"
chmod --reference="`$file" "`$tmp" 2>/dev/null || true
mv "`$tmp" "`$file"
trap - EXIT
"@
    # PowerShell here-strings use CRLF on Windows. Linux shell profiles must
    # receive LF-only content; otherwise exact marker cleanup stops being
    # idempotent and Bash can parse the managed block as an unterminated if.
    $blockLf = $block.Replace("`r", '').Trim()
    $blockBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($blockLf))
    $script = $script.Replace('__BLOCK_BASE64__', $blockBase64)
    $scriptBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $remoteCommand = "printf %s $scriptBase64 | base64 -d | bash"
    $result = Invoke-Ssh $Configuration $remoteCommand
    if ($result.ExitCode -ne 0) {
        throw "Remote environment setup failed: $($result.Output -join ' ')"
    }
    Write-Pass "Remote ~/.bashrc application network route installed: $Route."
}

function Test-ManagedTunnelProcess($Configuration, $Process) {
    if (-not $Process -or $Process.ProcessName -ne 'ssh') {
        return $false
    }

    # Local proxy settings can be edited while this tunnel is running. Match
    # the remote bind and SSH target, which stay the same until it is stopped.
    try {
        $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction Stop
        if ($details) {
            $remoteBind = [regex]::Escape("$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort):")
            $forwardArgument = '{0}[^\s"]+:\d+' -f $remoteBind
            $target = [regex]::Escape([string]$Configuration.ssh.alias)
            $forwardAndTarget = '(?:^|\s)-o\s+(?:"ExitOnForwardFailure=yes"|ExitOnForwardFailure=yes)\s+-R\s+(?:"{0}"|{0})\s+(?:"{1}"|{1})\s*$' -f $forwardArgument, $target
            return $details.Name -eq 'ssh.exe' -and
                $details.CommandLine -cmatch '(?:^|\s)-N\s+-T(?:\s|$)' -and
                $details.CommandLine -cmatch $forwardAndTarget
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

function Test-RemoteDirectCodex($Configuration, [switch]$Quiet, [switch]$ReportProbeResult) {
    $script = @'
set -u
unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy ALL_PROXY all_proxy
target='https://chatgpt.com/backend-api/codex/responses'
code=''
for attempt in 1 2; do
    code=$(curl -sS -o /dev/null -w '%{http_code}' \
        --noproxy '*' --connect-timeout 6 --max-time 10 \
        -X POST -H 'content-type: application/json' --data '{}' \
        "$target" 2>/dev/null || true)
    if [ "$code" = '401' ]; then
        printf 'CODEX_DIRECT_OK:%s:attempt-%s\n' "$code" "$attempt"
        exit 0
    fi
    [ "$attempt" = '1' ] && sleep 1
done
printf 'CODEX_DIRECT_FAILED:%s\n' "${code:-none}"
exit 1
'@
    $scriptBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $command = "printf %s $scriptBase64 | base64 -d | bash"
    $result = Invoke-Ssh $Configuration $command -UseAlias:(-not $ReportProbeResult) -ReadOnly:$ReportProbeResult
    $marker = (($result.Output | Where-Object {
                $_ -match '^CODEX_DIRECT_(?:OK:401:attempt-[12]|FAILED:(?:\d{3}|none))$'
            } | Select-Object -First 1) -as [string])
    $marker = if ($marker) { $marker.Trim() } else { '' }
    if ($ReportProbeResult) {
        if (-not $marker) {
            throw "The SSH command did not return a direct Codex probe result (exit $($result.ExitCode))."
        }
        Write-Host $marker
    }
    $ok = $result.ExitCode -eq 0 -and $marker -match '^CODEX_DIRECT_OK:'
    if (-not $Quiet) {
        if ($ok) {
            Write-Pass "The server can reach the Codex endpoint directly ($marker)."
        }
        else {
            Write-Warn 'The server cannot reach the Codex endpoint directly; the Windows proxy tunnel is required.'
        }
    }
    return $ok
}

function Select-RemoteCodexRoute($Configuration) {
    if (Test-RemoteDirectCodex $Configuration) {
        return 'direct'
    }
    return 'proxy'
}

function Test-RemoteProxy($Configuration) {
    $url = "http://$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort)"
    $urlBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($url))
    $script = @'
set -u
proxy=$(printf %s __PROXY_BASE64__ | base64 -d)
codex_code=''
for attempt in 1 2; do
    codex_code=$(curl -sS -o /dev/null -w '%{http_code}' \
        --connect-timeout 6 --max-time 10 --proxy "$proxy" \
        -X POST -H 'content-type: application/json' --data '{}' \
        'https://chatgpt.com/backend-api/codex/responses' 2>/dev/null || true)
    if [ "$codex_code" = '401' ]; then
        printf 'CODEX_REMOTE_PROXY_OK:codex:%s:attempt-%s\n' "$codex_code" "$attempt"
        exit 0
    fi
    [ "$attempt" = '1' ] && sleep 1
done
printf 'CODEX_REMOTE_PROXY_FAILED:codex:%s\n' "${codex_code:-none}"
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

function Test-RemoteApplicationNetwork(
    $Configuration,
    [switch]$Quiet,
    [switch]$RequireActiveCodex,
    [switch]$SuppressMarkers
) {
    $expected = "http://$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort)"
    $expectedBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expected))
    $script = @'
set -u
expected=$(printf %s __EXPECTED_BASE64__ | base64 -d)
require_active=__REQUIRE_ACTIVE__
if ! bash -n "$HOME/.bashrc" 2>/dev/null; then
    printf 'APPLICATION_NETWORK_BASHRC_INVALID\n'
    exit 1
fi
# Read the marked result even when interactive startup writes to stdout.
shell_output=$(env -u SSH_PROXY_BRIDGE_ROUTE -u HTTPS_PROXY \
    bash -ic 'builtin printf "\n__SSH_PROXY_BRIDGE_SHELL_STATE__:%s:%s\n" "${SSH_PROXY_BRIDGE_ROUTE:-}" "${HTTPS_PROXY:-}"' \
    </dev/null 2>/dev/null || true)
shell_state=$(printf '%s\n' "$shell_output" | sed -n 's/^__SSH_PROXY_BRIDGE_SHELL_STATE__://p' | tail -n 1)
route=${shell_state%%:*}
shell_proxy=${shell_state#*:}
case "$route" in
    direct)
        if [ -n "$shell_proxy" ]; then
            printf 'APPLICATION_NETWORK_SHELL_MISMATCH:direct\n'
            exit 1
        fi
        unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy ALL_PROXY all_proxy
        direct_code=''
        for attempt in 1 2; do
            direct_code=$(curl -sS -o /dev/null -w '%{http_code}' \
                --noproxy '*' --connect-timeout 6 --max-time 10 \
                -X POST -H 'content-type: application/json' --data '{}' \
                'https://chatgpt.com/backend-api/codex/responses' 2>/dev/null || true)
            [ "$direct_code" = '401' ] && break
            [ "$attempt" = '1' ] && sleep 1
        done
        if [ "$direct_code" != '401' ]; then
            printf 'APPLICATION_NETWORK_DIRECT_FAILED:%s\n' "${direct_code:-none}"
            exit 1
        fi
        ;;
    proxy)
        if [ "$shell_proxy" != "$expected" ]; then
            printf 'APPLICATION_NETWORK_SHELL_MISMATCH:proxy\n'
            exit 1
        fi
        proxy_code=''
        for attempt in 1 2; do
            proxy_code=$(curl -sS -o /dev/null -w '%{http_code}' \
                --connect-timeout 6 --max-time 10 --proxy "$expected" \
                -X POST -H 'content-type: application/json' --data '{}' \
                'https://chatgpt.com/backend-api/codex/responses' 2>/dev/null || true)
            [ "$proxy_code" = '401' ] && break
            [ "$attempt" = '1' ] && sleep 1
        done
        if [ "$proxy_code" != '401' ]; then
            printf 'APPLICATION_NETWORK_PROXY_FAILED:%s\n' "${proxy_code:-none}"
            exit 1
        fi
        ;;
    *)
        printf 'APPLICATION_NETWORK_ROUTE_MISSING\n'
        exit 1
        ;;
esac
total=0
ready=0
extension_prefix="$HOME/.vscode-server/extensions/openai.chatgpt-"
for proc in /proc/[0-9]*; do
    [ -r "$proc/cmdline" ] && [ -r "$proc/environ" ] || continue
    command_line=$(tr '\0' ' ' <"$proc/cmdline" 2>/dev/null || true)
    case "$command_line" in
        *"$extension_prefix"*) ;;
        *) continue ;;
    esac
    total=$((total + 1))
    process_route=$(tr '\0' '\n' <"$proc/environ" |
        sed -n 's/^SSH_PROXY_BRIDGE_ROUTE=//p' | head -n 1)
    process_proxy=$(tr '\0' '\n' <"$proc/environ" |
        sed -n 's/^HTTPS_PROXY=//p' | head -n 1)
    case "$route" in
        direct)
            [ "$process_route" = 'direct' ] && [ -z "$process_proxy" ] &&
                ready=$((ready + 1))
            ;;
        proxy)
            [ "$process_route" = 'proxy' ] && [ "$process_proxy" = "$expected" ] &&
                ready=$((ready + 1))
            ;;
    esac
done
if [ "$total" -eq 0 ]; then
    if [ "$require_active" = '1' ]; then
        printf 'APPLICATION_NETWORK_CODEX_NOT_RUNNING:%s\n' "$route"
        exit 1
    fi
    printf 'APPLICATION_NETWORK_READY:%s:no-active-codex\n' "$route"
    exit 0
fi
if [ "$ready" -ne "$total" ]; then
    printf 'APPLICATION_NETWORK_PROCESS_MISMATCH:%s:%s/%s\n' "$route" "$ready" "$total"
    exit 1
fi
printf 'APPLICATION_NETWORK_READY:%s:%s/%s\n' "$route" "$ready" "$total"
'@
    $script = $script.Replace('__EXPECTED_BASE64__', $expectedBase64)
    $script = $script.Replace('__REQUIRE_ACTIVE__', $(if ($RequireActiveCodex) { '1' } else { '0' }))
    $scriptBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $command = "printf %s $scriptBase64 | base64 -d | bash"
    $result = Invoke-Ssh $Configuration $command -UseAlias
    $marker = (($result.Output | Where-Object {
                $_ -match '^APPLICATION_NETWORK_'
            } | Select-Object -First 1) -as [string])
    $marker = if ($marker) { $marker.Trim() } else { '' }
    $ok = $result.ExitCode -eq 0 -and
        $marker -match '^APPLICATION_NETWORK_READY:(direct|proxy):'
    $script:LastApplicationNetworkMarker = if ($marker) { $marker } else { 'APPLICATION_NETWORK_CHECK_FAILED' }
    $script:LastApplicationNetworkReason = if ($marker -match '^APPLICATION_NETWORK_READY:') {
        'ready'
    }
    elseif ($marker -match '^APPLICATION_NETWORK_PROCESS_MISMATCH:') {
        'stale-codex-process'
    }
    elseif ($marker -match '^APPLICATION_NETWORK_CODEX_NOT_RUNNING:') {
        'codex-not-running'
    }
    elseif ($marker -eq 'APPLICATION_NETWORK_BASHRC_INVALID') {
        'bashrc-invalid'
    }
    elseif ($marker -match '^APPLICATION_NETWORK_SHELL_MISMATCH:') {
        'shell-route-mismatch'
    }
    elseif ($marker -eq 'APPLICATION_NETWORK_ROUTE_MISSING') {
        'shell-route-missing'
    }
    elseif ($marker -match '^APPLICATION_NETWORK_DIRECT_FAILED:') {
        'direct-unreachable'
    }
    elseif ($marker -match '^APPLICATION_NETWORK_PROXY_FAILED:') {
        'proxy-unreachable'
    }
    else {
        'check-failed'
    }
    $script:LastApplicationNetworkRoute = if ($ok) {
        ($marker -split ':')[1]
    }
    else {
        'not ready'
    }
    if (-not $SuppressMarkers) {
        Write-Host "Application network marker: $($script:LastApplicationNetworkMarker)"
        Write-Host "Application network reason: $($script:LastApplicationNetworkReason)"
    }
    if (-not $Quiet) {
        if ($ok) {
            if ($marker -match ':no-active-codex$') {
                Write-Pass "Remote application network route is $($script:LastApplicationNetworkRoute); no active Codex process needs validation."
            }
            else {
                Write-Pass "Remote Codex processes inherited the $($script:LastApplicationNetworkRoute) network route ($marker)."
            }
        }
        elseif ($marker -eq 'APPLICATION_NETWORK_BASHRC_INVALID') {
            Write-Fail 'Remote ~/.bashrc has a syntax error; Codex cannot inherit the managed network route.'
        }
        elseif ($marker -match '^APPLICATION_NETWORK_SHELL_MISMATCH:') {
            Write-Fail "A new remote Bash shell did not inherit the selected application network route ($marker)."
        }
        elseif ($marker -match '^APPLICATION_NETWORK_PROCESS_MISMATCH:') {
            Write-Fail "Active remote Codex processes are using a stale application network route ($marker)."
        }
        elseif ($marker -match '^APPLICATION_NETWORK_DIRECT_FAILED:') {
            Write-Fail 'The managed route is direct, but the server can no longer reach the Codex endpoint directly.'
        }
        elseif ($marker -match '^APPLICATION_NETWORK_PROXY_FAILED:') {
            Write-Fail "The managed proxy route cannot reach the Codex endpoint ($marker)."
        }
        else {
            Write-Fail "Remote application network validation failed: $($result.Output -join ' ')"
        }
    }
    return $ok
}

function Restart-RemoteVsCodeServer($Configuration) {
    if (-not $Force) {
        throw 'Restarting the remote VS Code Server requires explicit confirmation.'
    }
    $script = @'
set -u
pids=''
for proc in /proc/[0-9]*; do
    [ -r "$proc/cmdline" ] || continue
    command_line=$(tr '\0' ' ' <"$proc/cmdline" 2>/dev/null || true)
    case "$command_line" in
        *"$HOME/.vscode-server/"*"/out/server-main.js"*|*"$HOME/.vscode-server/"*"/server/bin/code-server"*)
            pids="$pids ${proc##*/}"
            ;;
    esac
done
if [ -z "${pids# }" ]; then
    printf 'VSCODE_SERVER_RESTARTED:none-running\n'
    exit 0
fi
kill -TERM $pids 2>/dev/null || true
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    alive=''
    for pid in $pids; do
        kill -0 "$pid" 2>/dev/null && alive="$alive $pid"
    done
    [ -z "${alive# }" ] && break
    sleep 1
done
if [ -n "${alive# }" ]; then
    kill -KILL $alive 2>/dev/null || true
fi
printf 'VSCODE_SERVER_RESTARTED:%s\n' "$(printf '%s' "$pids" | xargs)"
'@
    $scriptBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $command = "printf %s $scriptBase64 | base64 -d | bash"
    $result = Invoke-Ssh $Configuration $command -UseAlias
    $marker = (($result.Output | Where-Object {
                $_ -match '^VSCODE_SERVER_RESTARTED:'
            } | Select-Object -First 1) -as [string])
    if ($result.ExitCode -ne 0 -or -not $marker) {
        throw "Remote VS Code Server restart failed: $($result.Output -join ' ')"
    }
    Write-Pass "Remote VS Code Server processes were stopped safely ($($marker.Trim()))."
}

function Get-RemoteCodexAuthenticationScript {
    return @'
set +x
probe_codex_auth() {
    local auth_root=${CODEX_HOME:-$HOME/.codex} auth_file mode proc command_line
    local deadline=$((SECONDS + 5))
    auth_file="$auth_root/auth.json"
    if [ -f "$auth_file" ] && [ -r "$auth_file" ] &&
        grep -Eq '"(access_token|id_token|OPENAI_API_KEY)"[[:space:]]*:[[:space:]]*"[^"[:space:]]' "$auth_file" 2>/dev/null; then
        mode=$(sed -n 's/.*"auth_mode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
            "$auth_file" 2>/dev/null | head -n 1)
        case "$mode" in
            chatgpt|apikey) printf 'CODEX_AUTH_READY:%s\n' "$mode" ;;
            *) printf 'CODEX_AUTH_READY:file\n' ;;
        esac
        return
    fi
    for proc in /proc/[0-9]*; do
        if [ "$SECONDS" -ge "$deadline" ]; then printf 'CODEX_AUTH_UNKNOWN\n'; return; fi
        [ -O "$proc" ] && [ -r "$proc/cmdline" ] && [ -r "$proc/environ" ] || continue
        command_line=$(tr '\0' ' ' 2>/dev/null <"$proc/cmdline" || true)
        case "$command_line" in
            */openai.chatgpt-*) ;;
            *) continue ;;
        esac
        if tr '\0' '\n' 2>/dev/null <"$proc/environ" |
            grep -Eq '^(OPENAI_API_KEY|CODEX_API_KEY)=.+'; then
            printf 'CODEX_AUTH_READY:process-env\n'
            return
        fi
    done
    if { { [ -e "$auth_file" ] || [ -L "$auth_file" ]; } && { [ ! -f "$auth_file" ] || [ ! -r "$auth_file" ]; }; } ||
        { [ -d "$auth_root" ] && [ ! -x "$auth_root" ]; }; then
        printf 'CODEX_AUTH_UNKNOWN\n'
    else
        printf 'CODEX_AUTH_SIGN_IN_REQUIRED\n'
    fi
}
codex_sign_in_state() {
    case "$1" in
        CODEX_AUTH_READY:*) printf 'signed-in' ;;
        CODEX_AUTH_SIGN_IN_REQUIRED) printf 'sign-in-required' ;;
        *) printf 'unknown' ;;
    esac
}
'@
}

function Get-RemoteCodexExtensionProbeScript {
    return @'
probe_codex_extension() {
    local proc arg settings data_dir extensions_dir server_script path parent node_exe
    local i unreadable=0
    local -a directories=() args=()
    for proc in /proc/[0-9]*; do
        [ -O "$proc" ] && [ -r "$proc/cmdline" ] || continue
        args=()
        mapfile -d '' -t args 2>/dev/null <"$proc/cmdline" || continue
        data_dir=''; extensions_dir=''; server_script=''
        for ((i=0; i<${#args[@]}; i++)); do
            arg=${args[i]}
            case "$arg" in
                */out/server-main.js) server_script=$arg ;;
                --extensions-dir=*) extensions_dir=${arg#*=} ;;
                --extensions-dir) extensions_dir=${args[i+1]:-} ;;
                --server-data-dir=*) data_dir=${arg#*=} ;;
                --server-data-dir) data_dir=${args[i+1]:-} ;;
            esac
        done
        [ -n "$server_script" ] || continue
        if [ -z "$extensions_dir" ] && [ -r "$proc/environ" ]; then
            while IFS= read -r -d '' settings; do
                case "$settings" in
                    VSCODE_EXTENSIONS=*) extensions_dir=${settings#*=} ;;
                    VSCODE_AGENT_FOLDER=*) [ -n "$data_dir" ] || data_dir=${settings#*=} ;;
                esac
            done 2>/dev/null <"$proc/environ"
        fi
        if [ -z "$data_dir" ]; then
            case "$server_script" in
                */cli/servers/*/server/out/server-main.js) data_dir=${server_script%%/cli/servers/*} ;;
                */bin/*/out/server-main.js) data_dir=${server_script%%/bin/*} ;;
            esac
        fi
        if [ -n "$extensions_dir" ]; then
            directories+=("$extensions_dir")
        elif [ -n "$data_dir" ]; then
            directories+=("$data_dir/extensions")
        fi
        path="${server_script%/out/server-main.js}/node"
        if [ -x "$path" ]; then node_exe=$path; fi
    done
    if [ "${#directories[@]}" -eq 0 ]; then
        if [ -n "${VSCODE_EXTENSIONS:-}" ]; then
            directories+=("$VSCODE_EXTENSIONS")
        elif [ -n "${VSCODE_AGENT_FOLDER:-}" ]; then
            directories+=("$VSCODE_AGENT_FOLDER/extensions")
        else
            directories+=("$HOME/.vscode-server/extensions" "$HOME/.vscode-server-insiders/extensions")
        fi
    fi

    if [ -z "${node_exe:-}" ]; then
        for parent in "${VSCODE_AGENT_FOLDER:-$HOME/.vscode-server}" "$HOME/.vscode-server-insiders"; do
            for path in "$parent"/cli/servers/{Stable,Insiders}-*/server/node "$parent"/bin/*/node; do
                if [ -x "$path" ]; then node_exe=$path; break 2; fi
            done
        done
    fi
    if [ -z "${node_exe:-}" ]; then node_exe=$(command -v node || true); fi
    if [ -n "$node_exe" ]; then
        env -u NODE_OPTIONS -u NODE_PATH "$node_exe" - "${directories[@]}" 2>/dev/null <<'CODEX_EXTENSION_NODE'
const fs = require('fs');
const path = require('path');
let uncertain = false;
for (const directory of process.argv.slice(2)) {
    if (!path.isAbsolute(directory)) { uncertain = true; continue; }
    let contents;
    try { contents = fs.readdirSync(directory); } catch (error) {
        if (error.code !== 'ENOENT') uncertain = true;
        else {
            try { if (fs.lstatSync(directory).isSymbolicLink()) uncertain = true; }
            catch (failure) { if (failure.code !== 'ENOENT') uncertain = true; }
        }
        continue;
    }
    let registered = null;
    const locations = new Set(contents.filter(name => /^openai\.chatgpt-[0-9]/i.test(name)));
    try {
        if (contents.includes('extensions.json')) {
            const registry = JSON.parse(fs.readFileSync(path.join(directory, 'extensions.json'), 'utf8').replace(/^\uFEFF/, ''));
            if (!Array.isArray(registry)) throw new Error('Invalid registry');
            registered = registry.filter(entry => entry && entry.identifier && String(entry.identifier.id).toLowerCase() === 'openai.chatgpt');
            for (const entry of registered) {
                if (typeof entry.relativeLocation === 'string' && !['.', '..'].includes(entry.relativeLocation) &&
                    path.basename(entry.relativeLocation) === entry.relativeLocation)
                    locations.add(entry.relativeLocation);
            }
            if (registered.length === 0) continue;
        }
    } catch { uncertain = true; }
    for (const name of locations) {
        try {
            const manifest = JSON.parse(fs.readFileSync(path.join(directory, name, 'package.json'), 'utf8').replace(/^\uFEFF/, ''));
            if (`${manifest.publisher}.${manifest.name}`.toLowerCase() === 'openai.chatgpt') {
                console.log('installed');
                process.exit(0);
            }
        } catch { uncertain = true; }
    }
    if (registered && registered.length) uncertain = true;
}
console.log(uncertain ? 'unknown' : 'not-installed');
CODEX_EXTENSION_NODE
        return
    fi
    for path in "${directories[@]}"; do
        if [ -d "$path" ] || [ -L "$path" ] || [[ "$path" != /* ]]; then unreadable=1; fi
        parent=${path%/*}
        [ -d "$parent" ] && [ ! -x "$parent" ] && unreadable=1
    done
    if [ "$unreadable" -eq 0 ]; then printf 'not-installed\n'; else printf 'unknown\n'; fi
}
'@
}

function Write-RemoteCodexExtensionStatus($Output, [string]$Fallback = 'unknown:unknown') {
    $line = $Output | Where-Object {
        $_ -cmatch '^CODEX_EXTENSION_STATUS:(installed|not-installed|unknown):(signed-in|sign-in-required|unknown)$'
    } | Select-Object -First 1
    if ($line) { Write-Host $line }
    else { Write-Host "CODEX_EXTENSION_STATUS:$Fallback" }
}

function Test-RemoteCodexAuthentication($Configuration, [switch]$Quiet, [switch]$IncludeExtensionStatus) {
    $script = "set -u`n" + (Get-RemoteCodexAuthenticationScript) + "`n"
    if ($IncludeExtensionStatus) {
        $script:LastRemoteCodexAuthenticationState = 'unknown'
        $script += (Get-RemoteCodexExtensionProbeScript) + "`n"
    }
    $script += 'auth_marker=$(probe_codex_auth); printf "%s\n" "$auth_marker"' + "`n"
    if ($IncludeExtensionStatus) {
        $script += @'
extension_status=$(probe_codex_extension || true)
case "$extension_status" in installed|not-installed) ;; *) extension_status=unknown ;; esac
sign_in=$(codex_sign_in_state "$auth_marker")
printf 'CODEX_EXTENSION_STATUS:%s:%s\n' "$extension_status" "$sign_in"
'@ + "`n"
    }
    $script += 'case "$auth_marker" in CODEX_AUTH_READY:*) exit 0 ;; *) exit 1 ;; esac'
    $scriptBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($script.Replace("`r", '')))
    $command = "printf %s $scriptBase64 | base64 -d | bash"
    try {
        $result = Invoke-Ssh $Configuration $command -UseAlias
    }
    catch {
        if (-not $IncludeExtensionStatus) { throw }
        Write-RemoteCodexExtensionStatus $null
        return $false
    }
    if ($IncludeExtensionStatus) {
        $statusOutput = if ($result.ExitCode -in @(0, 1)) { $result.Output } else { @() }
        Write-RemoteCodexExtensionStatus $statusOutput
    }
    $marker = (($result.Output | Where-Object {
                $_ -match '^CODEX_AUTH_'
            } | Select-Object -First 1) -as [string])
    $marker = if ($marker) { $marker.Trim() } else { '' }
    $ok = $result.ExitCode -eq 0 -and $marker -match '^CODEX_AUTH_READY:'
    if ($IncludeExtensionStatus) {
        if ($ok) { $script:LastRemoteCodexAuthenticationState = 'ready' }
        elseif ($result.ExitCode -eq 1 -and $marker -ceq 'CODEX_AUTH_SIGN_IN_REQUIRED') {
            $script:LastRemoteCodexAuthenticationState = 'sign-in required'
        }
    }
    if (-not $Quiet) {
        if ($ok) {
            Write-Pass "Remote Codex authentication is configured ($marker)."
        }
        else {
            Write-Fail 'Remote Codex authentication is missing. Open Codex in the remote VS Code window and sign in with ChatGPT.'
        }
    }
    return $ok
}

function Get-VsCodeArguments($Configuration) {
    $alias = [string]$Configuration.ssh.alias
    $workspace = [string]$Configuration.vscode.remoteWorkspace
    if ($workspace.StartsWith('/')) {
        $encodedPath = ($workspace -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
        $authority = 'ssh-remote+' + [Uri]::EscapeDataString($alias)
        # Chromium can drop bare URI arguments before VS Code receives them.
        return @('--new-window', "--folder-uri=vscode-remote://$authority$encodedPath")
    }

    $arguments = @('--new-window', '--remote', "ssh-remote+$alias")
    if ($workspace) {
        $arguments += $workspace
    }
    return $arguments
}

function Get-VsCodeCliScript([string]$CodeRoot, [string]$CodeCommand) {
    [string]$launcher = Get-Content -LiteralPath $CodeCommand -Raw -ErrorAction SilentlyContinue
    # Recent installers put the CLI under the active build directory named in code.cmd.
    $launcherPattern = '(?m)^[ \t]*"%~dp0\.\.\\Code\.exe"[ \t]+"%~dp0\.\.\\((?:[0-9a-f]{7,64}\\)?resources\\app\\out\\cli\.js)"[ \t]+%\*[ \t]*\r?$'
    if ($launcher -notmatch $launcherPattern) {
        return
    }

    $path = Join-Path $CodeRoot $Matches[1]
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        return $path
    }
}

function Start-VsCode($Configuration) {
    if (-not [bool]$Configuration.vscode.launch) {
        return
    }
    $code = Get-Executable 'code.cmd'
    if (-not $code) {
        throw 'VS Code command (code.cmd) was not found.'
    }
    $arguments = @(Get-VsCodeArguments $Configuration)
    $environment = Get-SshProcessEnvironment $Configuration
    # Invoke the official CLI through the executable to preserve URI escapes.
    $codeRoot = Split-Path -Parent (Split-Path -Parent $code)
    $codeExecutable = Join-Path $codeRoot 'Code.exe'
    $codeCli = Get-VsCodeCliScript $codeRoot $code
    if ($codeCli -and (Test-Path -LiteralPath $codeExecutable -PathType Leaf)) {
        $code = $codeExecutable
        $arguments = @($codeCli) + $arguments
        if (-not $environment) { $environment = @{} }
        $environment['ELECTRON_RUN_AS_NODE'] = '1'
        $environment['VSCODE_DEV'] = $null
    }
    $argumentLine = ($arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
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
    Invoke-WithProcessEnvironment $environment {
        Start-Process -FilePath $code -ArgumentList $argumentLine -WindowStyle Hidden `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
    }
    Write-Pass 'VS Code Remote-SSH launch requested.'
}

function Get-CodexExtensionProxyUrl([string]$HostName, [int]$Port, [string]$Scheme = 'http') {
    if ([Uri]::CheckHostName($HostName) -eq [UriHostNameType]::Unknown -or $Port -lt 1 -or $Port -gt 65535) {
        throw 'The configured proxy host or port is invalid.'
    }
    return [UriBuilder]::new($Scheme, $HostName, $Port).Uri.GetLeftPart([UriPartial]::Authority)
}

function Get-CodexExtensionServerScript {
    $commit = ''
    $launcher = Get-Executable 'code.cmd'
    if ($launcher) {
        $root = Split-Path -Parent (Split-Path -Parent $launcher)
        $cli = Get-VsCodeCliScript $root $launcher
        if ($cli) {
            $productPath = Join-Path (Split-Path -Parent (Split-Path -Parent $cli)) 'product.json'
            try {
                $product = Get-Content -Raw -LiteralPath $productPath | ConvertFrom-Json
                if ($product.PSObject.Properties['commit'] -and $product.commit -match '^[a-f0-9]{40}$') {
                    $commit = [string]$product.commit
                }
            }
            catch { }
        }
    }

    $source = @'
set -euo pipefail
fail() { printf 'CODEX_EXTENSION_ERROR:%s\n' "$1"; exit 1; }
root=${VSCODE_AGENT_FOLDER:-$HOME/.vscode-server}
commit='__LOCAL_COMMIT__'
case "$(uname -s):$(uname -m)" in
    Linux:x86_64|Linux:amd64) target=linux-x64 ;;
    Linux:aarch64|Linux:arm64) target=linux-arm64 ;;
    *) fail unsupported-platform ;;
esac
editor=''
candidates=0
for candidate in "$root"/cli/servers/{Stable,Insiders}-*/server/bin/code-server "$root"/bin/*/bin/code-server; do
    [ -x "$candidate" ] || continue
    candidates=$((candidates + 1))
    editor=$candidate
    if [ -n "$commit" ]; then
        case "$candidate" in
            */Stable-"$commit"/server/bin/code-server|*/Insiders-"$commit"/server/bin/code-server|*/bin/"$commit"/bin/code-server)
                candidates=1
                break
                ;;
        esac
    fi
done
[ "$candidates" -gt 0 ] || fail no-editor
[ "$candidates" -eq 1 ] || fail ambiguous-editor
extensions=${VSCODE_EXTENSIONS:-$root/extensions}
unset HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy NO_PROXY no_proxy VSCODE_SERVER_PATCHELF_PATH
install_codex() {
    "$editor" --force-disable-user-env --server-data-dir "$root" --extensions-dir "$extensions" --install-extension "$@" </dev/null || fail cli-install
    "$editor" --force-disable-user-env --server-data-dir "$root" --extensions-dir "$extensions" --list-extensions </dev/null |
        grep -Fx 'openai.chatgpt' >/dev/null || fail cli-verification
}
'@
    return $source.Replace('__LOCAL_COMMIT__', $commit)
}

function Invoke-CodexExtensionSsh($Configuration, [string]$Source, [int]$TimeoutSeconds, [string]$UploadPath = '') {
    $ssh = Get-Executable 'ssh.exe'
    if (-not $ssh) { throw 'OpenSSH client (ssh.exe) was not found.' }
    # Pass only a fixed Bash program and encoded, validated values. Its stdin stays available for VSIX bytes.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Source.Replace("`r", '')))
    $command = 'bash -c "$(printf %s ' + $encoded + ' | base64 -d)"'
    $arguments = @('-o', 'StrictHostKeyChecking=yes', '-o', 'UpdateHostKeys=no', '-o', 'ControlPath=none',
        '-o', 'ControlMaster=no', '-o', 'ForwardAgent=no') + @(Get-SshOneShotArguments $Configuration)
    $arguments += @(Get-SshTargetArguments $Configuration)
    $arguments += $command
    $environment = Get-SshProcessEnvironment $Configuration
    if (-not $UploadPath) {
        return Invoke-NativeProcessWithTimeout $ssh $arguments $null $TimeoutSeconds $environment
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $ssh
    $process.StartInfo.Arguments = ($arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $inputFile = [IO.File]::OpenRead($UploadPath)
    try {
        if (-not (Invoke-WithProcessEnvironment $environment { $process.Start() })) {
            throw 'Could not start the SSH upload.'
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $copy = $inputFile.CopyToAsync($process.StandardInput.BaseStream, 262144)
        if (-not $copy.Wait($TimeoutSeconds * 1000)) {
            throw 'Uploading the Codex VSIX over SSH timed out.'
        }
        $process.StandardInput.Close()
        Write-Host 'CODEX_EXTENSION_PROGRESS:remote-install'
        $remaining = [int][Math]::Max(0, ($TimeoutSeconds * 1000) - $watch.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) {
            throw 'The remote Codex installation timed out.'
        }
        $output = $stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = @($output -split '\r?\n'); TimedOut = $false }
    }
    finally {
        $inputFile.Dispose()
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
        $process.Dispose()
    }
}

function Assert-CodexExtensionSshResult($Result) {
    if ($Result.ExitCode -eq 0) { return }
    if ($Result.TimedOut) { throw 'The remote Codex operation timed out.' }
    $reason = $Result.Output | Where-Object { $_ -match '^CODEX_EXTENSION_ERROR:[a-z-]+$' } | Select-Object -First 1
    switch ($reason) {
        'CODEX_EXTENSION_ERROR:no-editor' { throw 'The remote VS Code Server is missing. Connect once in VS Code, then retry.' }
        'CODEX_EXTENSION_ERROR:ambiguous-editor' { throw 'Multiple remote VS Code Server builds were found, with none matching the local VS Code build. Connect once with this VS Code client, then retry.' }
        'CODEX_EXTENSION_ERROR:unsupported-platform' { throw 'The Codex installer requires a Linux x64 or arm64 SSH server.' }
        'CODEX_EXTENSION_ERROR:proxy-unavailable' { throw 'The existing remote proxy cannot reach the VS Code Marketplace. Check the proxy tunnel or choose Windows download and SSH upload.' }
        'CODEX_EXTENSION_ERROR:archive-integrity' { throw 'The uploaded Codex VSIX was incomplete or its checksum did not match.' }
        'CODEX_EXTENSION_ERROR:cli-install' { throw 'VS Code Server rejected the Codex installation. Check VS Code Server compatibility and Marketplace connectivity, or try another install route.' }
        'CODEX_EXTENSION_ERROR:cli-verification' { throw 'VS Code Server did not list Codex after installation.' }
        default { throw 'The remote Codex operation failed. Check SSH authentication, the host key, and the selected installation route.' }
    }
}

function Get-CodexExtensionGalleryUrl([string]$Curl, [string[]]$NetworkArguments, [string]$Target) {
    $query = @'
{"filters":[{"criteria":[{"filterType":7,"value":"openai.chatgpt"},{"filterType":8,"value":"Microsoft.VisualStudio.Code"}],"pageSize":1,"pageNumber":1}],"assetTypes":[],"flags":145}
'@
    # IncludeVersions (1), IncludeVersionProperties (16), IncludeAssetUri (128).
    $queryUrl = 'https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery'
    $response = Invoke-NativeProcessWithTimeout $Curl ($NetworkArguments + @('--max-time', '40',
        '--max-filesize', '16777216', '--header', 'Content-Type: application/json', '--header',
        'Accept: application/json;api-version=7.2-preview.1', '--data-binary', $query.Trim(), $queryUrl)) $null 45
    if ($response.ExitCode -ne 0) { throw 'Windows could not query Codex package metadata from the Marketplace.' }

    $metadata = ($response.Output -join "`n") | ConvertFrom-Json
    $edition = $metadata.results | ForEach-Object { $_.extensions } | Where-Object {
        $_.publisher.publisherName -ieq 'openai' -and $_.extensionName -ieq 'chatgpt'
    } | Select-Object -First 1
    if (-not $edition) { throw 'The Marketplace did not return the OpenAI Codex extension.' }
    $version = $edition.versions | Where-Object {
        $properties = $_.PSObject.Properties['properties']
        $prerelease = $properties -and ($properties.Value | Where-Object {
            $_.key -eq 'Microsoft.VisualStudio.Code.PreRelease' -and $_.value -eq 'true'
        })
        -not $prerelease -and $_.PSObject.Properties['targetPlatform'] -and $_.targetPlatform -ceq $Target -and
        $_.version -match '^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$' -and $_.PSObject.Properties['fallbackAssetUri']
    } | Sort-Object -Property @{Expression = { [version]$_.version }; Descending = $true } | Select-Object -First 1
    if (-not $version) { throw 'The Marketplace did not return a release Codex VSIX for the remote Linux architecture.' }

    [Uri]$asset = $null
    if (-not [Uri]::TryCreate([string]$version.fallbackAssetUri, [UriKind]::Absolute, [ref]$asset) -or
        $asset.Scheme -ne 'https' -or $asset.Port -ne 443 -or $asset.UserInfo -or $asset.Query -or $asset.Fragment -or
        $asset.Host -notin @('marketplace.visualstudio.com', 'openai.gallery.vsassets.io', 'openai.gallerycdn.vsassets.io')) {
        throw 'The Marketplace reported an unexpected Codex package URL.'
    }
    return $asset.AbsoluteUri.TrimEnd('/') + "/Microsoft.VisualStudio.Services.VSIXPackage?redirect=true&targetPlatform=$Target"
}

function Save-CodexExtensionVsix($Configuration, [string]$Target, [string]$Path) {
    if ($Target -cnotin @('linux-x64', 'linux-arm64')) { throw 'Unsupported Codex package architecture.' }
    $curl = Get-Executable 'curl.exe' "$env:SystemRoot\System32\curl.exe"
    if (-not $curl) { throw 'Windows curl.exe is required to download the Marketplace VSIX.' }
    $networkArguments = @('--silent', '--show-error', '--fail', '--proto', '=https', '--connect-timeout', '15')
    if (Test-TcpPort ([string]$Configuration.proxy.host) ([int]$Configuration.proxy.port)) {
        $protocol = $Configuration.proxy.PSObject.Properties['protocol']
        $scheme = if ($protocol -and $protocol.Value -eq 'socks5') { 'socks5h' } else { 'http' }
        $proxy = Get-CodexExtensionProxyUrl ([string]$Configuration.proxy.host) ([int]$Configuration.proxy.port) $scheme
        $networkArguments += @('--noproxy', '', '--proxy', $proxy)
    }
    $url = Get-CodexExtensionGalleryUrl $curl $networkArguments $Target
    $arguments = $networkArguments + @('--location', '--max-redirs', '5', '--proto-redir', '=https',
        '--max-time', '720', '--max-filesize', '1073741824', '--output', $Path, $url)
    $result = Invoke-NativeProcessWithTimeout $curl $arguments $null 730
    if ($result.ExitCode -ne 0) { throw 'Windows could not download the official Codex VSIX from the Marketplace.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('extension.vsixmanifest')
        if (-not $entry -or $entry.Length -gt 65536) { throw 'The downloaded file does not contain a valid VSIX manifest.' }
        $stream = $entry.Open()
        try {
            $manifest = [Xml.XmlDocument]::new()
            $manifest.XmlResolver = $null
            $manifest.Load($stream)
        }
        finally { $stream.Dispose() }
        $identity = $manifest.SelectSingleNode("/*[local-name()='PackageManifest']/*[local-name()='Metadata']/*[local-name()='Identity']")
        if (-not $identity -or $identity.GetAttribute('Publisher') -cne 'openai' -or
            $identity.GetAttribute('Id') -cne 'chatgpt' -or $identity.GetAttribute('TargetPlatform') -cne $Target) {
            throw 'The Marketplace package does not match the expected OpenAI Codex extension and Linux architecture.'
        }
        $packageEntry = $archive.GetEntry('extension/package.json')
        if (-not $packageEntry -or $packageEntry.Length -gt 1048576) { throw 'The Codex VSIX package manifest is missing or invalid.' }
        $reader = [IO.StreamReader]::new($packageEntry.Open())
        try { $package = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        $dependencies = $package.PSObject.Properties['extensionDependencies']
        if ($dependencies -and $dependencies.Value) {
            throw 'This Codex VSIX requires other extensions. Use the Marketplace install route to resolve its dependencies.'
        }
    }
    finally { $archive.Dispose() }
}

function Install-CodexExtension($Configuration, [string]$Route) {
    Write-Host 'CODEX_EXTENSION_PROGRESS:checking'
    $serverScript = (Get-CodexExtensionServerScript) + "`n"
    $completionScript = "`n" + (Get-RemoteCodexAuthenticationScript) + "`n" + @'
auth_marker=$(probe_codex_auth || true)
sign_in=$(codex_sign_in_state "$auth_marker")
printf 'CODEX_EXTENSION_STATUS:installed:%s\n' "$sign_in"
'@
    if ($Route -eq 'upload') {
        $check = Invoke-CodexExtensionSsh $Configuration ($serverScript + 'printf "CODEX_EXTENSION_TARGET:%s\n" "$target"') 35
        Assert-CodexExtensionSshResult $check
        $targetLine = $check.Output | Where-Object { $_ -match '^CODEX_EXTENSION_TARGET:linux-(x64|arm64)$' } | Select-Object -First 1
        if (-not $targetLine) { throw 'The remote Codex package architecture could not be verified.' }
        $target = $targetLine.Substring('CODEX_EXTENSION_TARGET:'.Length)

        $path = [IO.Path]::GetTempFileName()
        try {
            Write-Host 'CODEX_EXTENSION_PROGRESS:windows-download'
            Save-CodexExtensionVsix $Configuration $target $path
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $upload = @'
[ "$target" = '__TARGET__' ] || fail unsupported-platform
umask 077
temporary=$(mktemp -d "${TMPDIR:-/tmp}/ssh-proxy-bridge-codex.XXXXXXXX")
archive="$temporary/openai.chatgpt.vsix"
trap 'rm -f -- "$archive"; rmdir -- "$temporary"' EXIT
trap 'exit 130' HUP INT TERM
cat >"$archive" || fail archive-integrity
digest=$(sha256sum -- "$archive") || fail archive-integrity
[ "${digest%% *}" = '__HASH__' ] || fail archive-integrity
install_codex "$archive" --do-not-include-pack-dependencies
'@
            $upload = $upload.Replace('__TARGET__', $target).Replace('__HASH__', $hash)
            Write-Host 'CODEX_EXTENSION_PROGRESS:ssh-upload'
            $result = Invoke-CodexExtensionSsh $Configuration ($serverScript + $upload + $completionScript) 1880 $path
            Assert-CodexExtensionSshResult $result
        }
        finally { [IO.File]::Delete($path) }
    }
    else {
        if ($Route -eq 'proxy') {
            $proxy = Get-CodexExtensionProxyUrl ([string]$Configuration.ssh.remoteProxyHost) ([int]$Configuration.ssh.remoteProxyPort)
            $proxyBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($proxy))
            $proxySetup = @'
proxy=$(printf %s __PROXY_BASE64__ | base64 -d)
curl -fsSL --proto '=https' --proto-redir '=https' --noproxy '' --proxy "$proxy" \
    --connect-timeout 5 --max-time 15 -o /dev/null 'https://marketplace.visualstudio.com/items?itemName=openai.chatgpt' || fail proxy-unavailable
export HTTP_PROXY="$proxy" HTTPS_PROXY="$proxy" http_proxy="$proxy" https_proxy="$proxy"
'@
            $serverScript += $proxySetup.Replace('__PROXY_BASE64__', $proxyBase64) + "`n"
        }
        Write-Host 'CODEX_EXTENSION_PROGRESS:remote-download-install'
        $result = Invoke-CodexExtensionSsh $Configuration ($serverScript + 'install_codex openai.chatgpt' + $completionScript) 2400
        Assert-CodexExtensionSshResult $result
    }
    Write-RemoteCodexExtensionStatus $result.Output 'installed:unknown'
    Write-Host 'CODEX_EXTENSION_PROGRESS:complete'
}

function Invoke-Doctor($Configuration) {
    $failures = 0
    $localProxyBlockingFailures = 0
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
        Write-Warn "No TCP listener was found on port $($Configuration.proxy.port). This is only blocking if the server needs the proxy fallback."
        $localProxyBlockingFailures++
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
        Write-Warn "HTTP proxy probe failed ($($http.Detail)). This is only blocking if the server needs the proxy fallback."
        $localProxyBlockingFailures++
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

    Write-Step 'Selecting the server application network route'
    $selectedRoute = Select-RemoteCodexRoute $Configuration
    if ($selectedRoute -eq 'direct') {
        Write-Pass 'Selected route: server direct connection. The Windows proxy tunnel is optional.'
    }
    else {
        Write-Warn 'Selected route: Windows proxy tunnel fallback.'
        $failures += $localProxyBlockingFailures
    }

    Write-Step 'Checking managed reverse tunnel'
    $tunnel = Get-TunnelProcess $Configuration
    if ($selectedRoute -eq 'proxy') {
        if ($tunnel) {
            Write-Pass "Managed SSH tunnel is running (PID $($tunnel.Id))."
            if (-not (Test-RemoteProxy $Configuration)) {
                $failures++
            }
        }
        else {
            Write-Fail 'The server needs the Windows proxy, but the managed SSH tunnel is not running.'
            $failures++
        }
    }
    elseif ($tunnel) {
        Write-Pass "Managed SSH tunnel is also running (PID $($tunnel.Id)), but the direct route does not depend on it."
    }
    else {
        Write-Pass 'Managed SSH tunnel is not required for the selected direct route.'
    }

    if (-not (Test-RemoteApplicationNetwork $Configuration)) {
        $failures++
    }
    if (-not (Test-RemoteCodexAuthentication $Configuration)) {
        $failures++
    }

    $monitor = Get-MonitorProcess
    if ($selectedRoute -eq 'proxy') {
        if ($monitor) {
            Write-Pass "Automatic tunnel repair is running (PID $($monitor.Id))."
        }
        else {
            Write-Warn 'Automatic tunnel repair is not running. Use start or repair to enable it.'
            $failures++
        }
    }
    elseif ($monitor) {
        Write-Pass "Automatic tunnel repair is available (PID $($monitor.Id)), but is not required by the direct route."
    }
    else {
        Write-Pass 'Automatic tunnel repair is not required for the selected direct route.'
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
        'install-codex' {
            Install-CodexExtension $configuration $ExtensionInstallRoute
        }
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
            Assert-SshLoginReady $configuration
            $selectedRoute = Select-RemoteCodexRoute $configuration
            Install-RemoteProxyEnvironment $configuration $selectedRoute
            Write-Pass "Setup is complete. Selected application route: $selectedRoute."
        }
        'check-direct' {
            if (-not (Test-RemoteDirectCodex $configuration -Quiet -ReportProbeResult)) {
                exit 2
            }
        }
        'start-tunnel' {
            Start-ProxyIfNeeded $configuration
            $existing = Get-TunnelProcess $configuration
            if ($existing) {
                Write-Pass "SSH tunnel is already running (PID $($existing.Id))."
                Write-Host 'Tunnel start result: existing'
                break
            }

            Install-SshConfig $configuration
            $null = Start-Tunnel $configuration
            if (Test-RemoteProxy $configuration) {
                $null = Start-Monitor $configuration
                Write-Host 'Tunnel start result: ready'
            }
            else {
                Write-Warn 'The SSH tunnel was left running. Automatic repair was not started because the Codex proxy probe failed.'
                Write-Host 'Tunnel start result: unverified'
            }
        }
        'start' {
            if ($ApplicationRoute -eq 'direct' -and -not (Test-RemoteDirectCodex $configuration -Quiet -ReportProbeResult)) {
                Write-Host 'Start result: proxy-choice-required'
                exit 3
            }
            Install-SshConfig $configuration
            Assert-SshLoginReady $configuration
            $selectedRoute = if ($ApplicationRoute -eq 'auto') {
                Select-RemoteCodexRoute $configuration
            }
            else {
                $ApplicationRoute
            }
            if ($selectedRoute -eq 'direct') {
                Install-RemoteProxyEnvironment $configuration 'direct'
                Write-Pass 'Server direct route selected; the Windows proxy tunnel is not required.'
            }
            else {
                Start-ProxyIfNeeded $configuration
                $null = Start-Tunnel $configuration
                if (-not (Test-RemoteProxy $configuration)) {
                    throw 'The SSH tunnel is running, but remote proxy validation failed.'
                }
                $null = Start-Monitor $configuration
                Install-RemoteProxyEnvironment $configuration 'proxy'
                Write-Pass 'Windows proxy tunnel fallback selected.'
            }
            Start-VsCode $configuration
            $applicationNetworkOk = Test-RemoteApplicationNetwork $configuration -Quiet
            Write-Host "Application network: $(if ($applicationNetworkOk) { $script:LastApplicationNetworkRoute } else { 'not ready' })"
            $codexAuthenticationOk = Test-RemoteCodexAuthentication $configuration -Quiet
            Write-Host "Codex authentication: $(if ($codexAuthenticationOk) { 'ready' } else { 'sign-in required' })"
            if (-not $applicationNetworkOk) {
                if ($script:LastApplicationNetworkReason -eq 'stale-codex-process') {
                    Write-Host 'Start result: reload-vscode-required'
                    Write-Warn 'The route is configured, but active Codex processes still use the previous route. Reload the remote VS Code session to apply it.'
                    exit 2
                }
                Write-Host 'Start result: failed'
                Write-Fail "The selected application route is not ready ($($script:LastApplicationNetworkReason))."
                exit 1
            }
            Write-Host 'Start result: ready'
            Write-Pass "Codex remote network workflow started (route: $selectedRoute)."
        }
        'repair' {
            Install-SshConfig $configuration
            Assert-SshLoginReady $configuration
            $selectedRoute = Select-RemoteCodexRoute $configuration
            if ($selectedRoute -eq 'direct') {
                Install-RemoteProxyEnvironment $configuration 'direct'
                Write-Pass 'The server direct route is healthy; tunnel repair was not required.'
            }
            else {
                Start-ProxyIfNeeded $configuration
                $null = Repair-Tunnel $configuration
                $null = Start-Monitor $configuration
                Install-RemoteProxyEnvironment $configuration 'proxy'
                Write-Pass 'Tunnel repair and automatic monitoring are ready.'
            }
            $applicationNetworkOk = Test-RemoteApplicationNetwork $configuration -Quiet
            Write-Host "Application network: $(if ($applicationNetworkOk) { $script:LastApplicationNetworkRoute } else { 'not ready' })"
            $codexAuthenticationOk = Test-RemoteCodexAuthentication $configuration -Quiet
            Write-Host "Codex authentication: $(if ($codexAuthenticationOk) { 'ready' } else { 'sign-in required' })"
            if (-not $applicationNetworkOk) {
                if ($script:LastApplicationNetworkReason -eq 'stale-codex-process') {
                    Write-Host 'Repair result: reload-vscode-required'
                    Write-Warn 'The tunnel and route are ready, but active Codex processes still use the previous route. Reload the remote VS Code session to finish repair.'
                    exit 2
                }
                Write-Host 'Repair result: failed'
                Write-Fail "Application network repair did not complete ($($script:LastApplicationNetworkReason))."
                exit 1
            }
            Write-Host 'Repair result: ready'
            Write-Pass "Application network route repaired (route: $selectedRoute)."
        }
        'reload-vscode' {
            Install-SshConfig $configuration
            Assert-SshLoginReady $configuration
            Restart-RemoteVsCodeServer $configuration
            Start-VsCode $configuration
            Write-Host 'Reload result: waiting-for-codex'
            $applicationNetworkOk = $false
            for ($attempt = 1; $attempt -le 12; $attempt++) {
                Start-Sleep -Seconds 5
                $applicationNetworkOk = Test-RemoteApplicationNetwork `
                    $configuration -Quiet -RequireActiveCodex -SuppressMarkers
                if ($applicationNetworkOk) {
                    break
                }
            }
            Write-Host "Application network marker: $($script:LastApplicationNetworkMarker)"
            Write-Host "Application network reason: $($script:LastApplicationNetworkReason)"
            Write-Host "Application network: $(if ($applicationNetworkOk) { $script:LastApplicationNetworkRoute } else { 'not ready' })"
            $codexAuthenticationOk = Test-RemoteCodexAuthentication $configuration -Quiet
            Write-Host "Codex authentication: $(if ($codexAuthenticationOk) { 'ready' } else { 'sign-in required' })"
            if (-not $applicationNetworkOk) {
                Write-Host 'Reload result: failed'
                Write-Fail "VS Code reopened, but Codex did not adopt the selected route ($($script:LastApplicationNetworkReason))."
                exit 1
            }
            Write-Host 'Reload result: ready'
            Write-Pass "Remote VS Code reloaded and Codex adopted the $($script:LastApplicationNetworkRoute) route."
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
            $sshLoginOk = $false
            $sshLoginError = ''
            try {
                $sshLoginOk = Test-SshLogin $configuration -Quiet
            }
            catch {
                $sshLoginError = $_.Exception.Message
            }
            Write-SshLoginMarkers
            Write-Host "$loginLabel`: $(if ($sshLoginOk) { 'ready' } else { 'not ready' })"

            if (-not $sshLoginOk) {
                Write-Host 'Connection diagnosis: ssh-not-ready'
                Write-Host 'Application network marker: APPLICATION_NETWORK_SKIPPED:ssh-not-ready'
                Write-Host 'Application network reason: ssh-not-ready'
                Write-Host 'Application network: unknown'
                Write-Host 'Codex authentication: unknown'
                Write-RemoteCodexExtensionStatus $null
                if ($sshLoginError) {
                    Write-Fail "SSH login check failed: $sshLoginError"
                }
                else {
                    Write-Fail 'SSH login is not ready. Check the server address, port, saved credential, and SSH key initialization.'
                }
                exit 1
            }

            $null = Test-RemoteCodexAuthentication $configuration -Quiet -IncludeExtensionStatus
            $applicationNetworkOk = Test-RemoteApplicationNetwork $configuration -Quiet
            Write-Host "Application network: $(if ($applicationNetworkOk) { $script:LastApplicationNetworkRoute } else { 'not ready' })"
            Write-Host "Codex authentication: $script:LastRemoteCodexAuthenticationState"
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
