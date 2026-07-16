[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('doctor', 'bootstrap-key', 'setup', 'start', 'status', 'stop', 'uninstall')]
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

    foreach ($name in @('alias', 'host', 'port', 'user', 'identityFile', 'remoteProxyHost', 'remoteProxyPort')) {
        if (-not $value.ssh.PSObject.Properties[$name]) {
            throw "Configuration value is missing: ssh.$name"
        }
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
        return [pscustomobject]@{ Success = $false; Status = ''; Detail = 'curl.exe not found' }
    }

    $arguments = @('--silent', '--show-error', '--output', 'NUL', '--write-out', '%{http_code}', '--max-time', '12')
    if ($ProxyType -eq 'socks5') {
        $arguments += @('--socks5-hostname', "${ProxyHost}:$ProxyPort")
    }
    else {
        $arguments += @('--proxy', "http://${ProxyHost}:$ProxyPort")
    }
    $arguments += 'http://cp.cloudflare.com/generate_204'

    $output = & $curl @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $status = (($output | Select-Object -Last 1) -as [string]).Trim()
    return [pscustomobject]@{
        Success = ($exitCode -eq 0 -and $status -eq '204')
        Status = $status
        Detail = "exit=$exitCode"
    }
}

function Get-KeyPath($Configuration) {
    return Expand-EnvironmentPath ([string]$Configuration.ssh.identityFile)
}

function Get-SshBaseArguments($Configuration, [switch]$UseAlias) {
    $ssh = $Configuration.ssh
    $identity = Get-KeyPath $Configuration
    $arguments = @(
        '-o', 'ConnectTimeout=10',
        '-o', 'ServerAliveInterval=30',
        '-o', 'ServerAliveCountMax=3',
        '-o', 'IdentitiesOnly=yes',
        '-i', $identity
    )
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
    [int]$TimeoutSeconds
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
        if (-not $process.Start()) {
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
    $arguments += @(
        '-o', 'ConnectionAttempts=1',
        '-o', 'BatchMode=yes',
        '-o', 'NumberOfPasswordPrompts=0',
        '-o', 'ClearAllForwardings=yes',
        '-T'
    )
    return $arguments
}

function Test-KeyLogin($Configuration, [switch]$Quiet) {
    $sshExe = Get-Executable 'ssh.exe'
    $keyPath = Get-KeyPath $Configuration
    if (-not $sshExe -or -not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        return $false
    }
    $arguments = @(Get-SshOneShotArguments $Configuration)
    $arguments += @(
        "$($Configuration.ssh.user)@$($Configuration.ssh.host)",
        'printf CODEX_REMOTE_KEY_OK'
    )
    $result = Invoke-NativeProcessWithTimeout $sshExe $arguments $null 20
    $ok = ($result.ExitCode -eq 0 -and ($result.Output -join '') -eq 'CODEX_REMOTE_KEY_OK')
    if (-not $Quiet) {
        if ($ok) {
            Write-Pass 'SSH key login succeeded.'
        }
        elseif ($result.TimedOut) {
            Write-Fail 'SSH key login timed out after 20 seconds. The server did not finish the SSH handshake.'
        }
        else {
            Write-Fail 'SSH key login is not ready.'
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
    if (-not (Test-KeyLogin $Configuration)) {
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
    $identity = (Get-KeyPath $Configuration).Replace('\', '/')
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
    IdentityFile "$identity"
    IdentitiesOnly yes
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
    $target = if ($UseAlias) { [string]$Configuration.ssh.alias } else { "$($Configuration.ssh.user)@$($Configuration.ssh.host)" }
    $arguments += @($target, $RemoteCommand)
    return Invoke-NativeProcessWithTimeout $sshExe $arguments $null 35
}

function Invoke-SshWithInput($Configuration, [string]$RemoteCommand, [string]$InputText, [switch]$UseAlias) {
    $sshExe = Get-Executable 'ssh.exe'
    $arguments = @(Get-SshOneShotArguments $Configuration -UseAlias:$UseAlias)
    $target = if ($UseAlias) { [string]$Configuration.ssh.alias } else { "$($Configuration.ssh.user)@$($Configuration.ssh.host)" }
    $arguments += @($target, $RemoteCommand)
    return Invoke-NativeProcessWithTimeout $sshExe $arguments $InputText 35
}

function Install-RemoteProxyEnvironment($Configuration) {
    if (-not (Test-KeyLogin $Configuration -Quiet)) {
        throw 'SSH key login is required before remote setup.'
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

function Get-TunnelProcess {
    $pidPath = Get-StatePath 'tunnel.pid'
    if (-not (Test-Path -LiteralPath $pidPath)) {
        return $null
    }
    $savedPid = 0
    if (-not [int]::TryParse((Get-Content -Raw -LiteralPath $pidPath).Trim(), [ref]$savedPid)) {
        return $null
    }
    return Get-Process -Id $savedPid -ErrorAction SilentlyContinue
}

function Stop-Tunnel($Configuration) {
    $process = Get-TunnelProcess
    $pidPath = Get-StatePath 'tunnel.pid'
    if (-not $process) {
        Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
        Write-Warn 'No managed SSH tunnel is running.'
        return
    }

    $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction SilentlyContinue
    $expectedPort = [string]$Configuration.ssh.remoteProxyPort
    $expectedAlias = [string]$Configuration.ssh.alias
    $isManagedTunnel = $details -and
        $details.Name -eq 'ssh.exe' -and
        $details.CommandLine -match [regex]::Escape($expectedPort) -and
        $details.CommandLine -match [regex]::Escape($expectedAlias)
    if (-not $isManagedTunnel) {
        throw "Refusing to stop PID $($process.Id): it does not match the managed tunnel."
    }
    Stop-Process -Id $process.Id -Force
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    Write-Pass "Stopped SSH tunnel PID $($process.Id)."
}

function Start-Tunnel($Configuration) {
    $existing = Get-TunnelProcess
    if ($existing) {
        Write-Pass "SSH tunnel is already running (PID $($existing.Id))."
        return $existing
    }

    if (-not (Test-KeyLogin $Configuration -Quiet)) {
        throw 'SSH key login is not ready. Run bootstrap-key first.'
    }
    $sshExe = Get-Executable 'ssh.exe'
    $remoteSpec = "$($Configuration.ssh.remoteProxyHost):$($Configuration.ssh.remoteProxyPort):$($Configuration.proxy.host):$($Configuration.proxy.port)"
    $arguments = Get-SshBaseArguments $Configuration -UseAlias
    $arguments += @(
        '-N', '-T',
        '-o', 'BatchMode=yes',
        '-o', 'ExitOnForwardFailure=yes',
        '-R', $remoteSpec,
        [string]$Configuration.ssh.alias
    )
    $state = Get-StateDirectory
    $stdout = Join-Path $state 'tunnel.stdout.log'
    $stderr = Join-Path $state 'tunnel.stderr.log'
    $process = Start-Process -FilePath $sshExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
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
    $command = 'code=$(curl -sS -o /dev/null -w ''%{http_code}'' --max-time 15 --proxy ''' + $url + ''' http://cp.cloudflare.com/generate_204); test "$code" = 204 && printf CODEX_REMOTE_PROXY_OK'
    $result = Invoke-Ssh $Configuration $command -UseAlias
    $ok = ($result.ExitCode -eq 0 -and ($result.Output -join '') -match 'CODEX_REMOTE_PROXY_OK')
    if ($ok) { Write-Pass 'The server can reach the internet through the Windows proxy tunnel.' }
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
    Start-Process -FilePath $code -ArgumentList $arguments -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
    Write-Pass 'VS Code Remote-SSH launch requested.'
}

function Invoke-Doctor($Configuration) {
    $failures = 0
    Write-Step 'Checking local commands'
    foreach ($name in @('ssh.exe', 'ssh-keygen.exe', 'code.cmd', 'curl.exe')) {
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
    if ($http.Success) { Write-Pass 'HTTP proxy probe returned 204.' } else { Write-Fail "HTTP proxy probe failed ($($http.Detail), status=$($http.Status))."; $failures++ }
    if ($socks.Success) { Write-Pass 'SOCKS5 proxy probe returned 204.' } else { Write-Warn "SOCKS5 probe failed ($($socks.Detail), status=$($socks.Status))." }

    Write-Step 'Checking SSH target'
    if (Test-TcpPort ([string]$Configuration.ssh.host) ([int]$Configuration.ssh.port) 5000) {
        Write-Pass "SSH endpoint is reachable at $($Configuration.ssh.host):$($Configuration.ssh.port)."
    }
    else {
        Write-Fail "SSH endpoint is not reachable at $($Configuration.ssh.host):$($Configuration.ssh.port)."
        $failures++
    }
    $keyPath = Get-KeyPath $Configuration
    if (Test-Path -LiteralPath $keyPath -PathType Leaf) {
        Write-Pass "SSH private key exists: $keyPath"
        if (-not (Test-KeyLogin $Configuration)) { $failures++ }
    }
    else {
        Write-Warn "SSH key has not been created yet: $keyPath"
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
            if (-not (Test-KeyLogin $configuration)) {
                throw 'Run bootstrap-key first from an interactive PowerShell terminal.'
            }
            Install-RemoteProxyEnvironment $configuration
            Write-Pass 'Setup is complete.'
        }
        'start' {
            Start-ProxyIfNeeded $configuration
            Install-SshConfig $configuration
            if (-not (Test-KeyLogin $configuration)) {
                throw 'Run bootstrap-key first from an interactive PowerShell terminal.'
            }
            $null = Start-Tunnel $configuration
            if (-not (Test-RemoteProxy $configuration)) {
                throw 'The SSH tunnel is running, but remote proxy validation failed.'
            }
            Install-RemoteProxyEnvironment $configuration
            Start-VsCode $configuration
            Write-Pass 'Codex remote proxy workflow started.'
        }
        'status' {
            $proxyOk = Test-TcpPort ([string]$configuration.proxy.host) ([int]$configuration.proxy.port)
            $tunnel = Get-TunnelProcess
            Write-Host "Proxy:  $(if ($proxyOk) { 'running' } else { 'not ready' })"
            Write-Host "Tunnel: $(if ($tunnel) { "running (PID $($tunnel.Id))" } else { 'stopped' })"
            Write-Host "SSH key login: $(if (Test-KeyLogin $configuration -Quiet) { 'ready' } else { 'not ready' })"
        }
        'stop' {
            Stop-Tunnel $configuration
        }
        'uninstall' {
            if (-not $Force) {
                $answer = Read-Host 'Remove managed SSH and remote proxy configuration? Type YES to continue'
                if ($answer -ne 'YES') { Write-Warn 'Uninstall cancelled.'; return }
            }
            Stop-Tunnel $configuration
            Remove-SshConfig $configuration
            if (Test-KeyLogin $configuration -Quiet) {
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
            Write-Pass 'Uninstall completed. The SSH private key was preserved.'
        }
    }
}
catch {
    Write-Fail $_.Exception.Message
    throw
}
