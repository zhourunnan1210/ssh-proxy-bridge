using System.Text;
using SshProxyBridge.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshProxyBridge.Core.Ssh;

public sealed class SshBootstrapService
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);

    public async Task InstallPublicKeyAsync(
        ConnectionProfile profile,
        string password,
        string publicKey,
        CancellationToken cancellationToken = default)
    {
        ValidatePinnedProfile(profile);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        using var connectionInfo = new PasswordConnectionInfo(
            profile.Ssh.Host,
            profile.Ssh.Port,
            profile.Ssh.User,
            password)
        {
            Timeout = ConnectionTimeout
        };
        using var client = new SshClient(connectionInfo);
        AttachPinnedHostKey(client, profile.Ssh.HostKeySha256!);
        await ConnectWithPinErrorAsync(client, profile.Ssh.HostKeySha256!, cancellationToken);

        try
        {
            var publicKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey));
            var script = $$"""
                set -eu
                umask 077
                mkdir -p "$HOME/.ssh"
                chmod 700 "$HOME/.ssh"
                touch "$HOME/.ssh/authorized_keys"
                chmod 600 "$HOME/.ssh/authorized_keys"
                key="$(printf '%s' '{{publicKeyBase64}}' | base64 -d)"
                if ! grep -qxF "$key" "$HOME/.ssh/authorized_keys"; then
                    printf '%s\n' "$key" >> "$HOME/.ssh/authorized_keys"
                fi
                printf '%s' SSH_PROXY_BRIDGE_KEY_OK
                """;
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r", string.Empty)));

            using var command = client.CreateCommand($"printf '%s' '{payload}' | base64 -d | bash");
            command.CommandTimeout = TimeSpan.FromSeconds(20);
            await command.ExecuteAsync(cancellationToken);

            if (command.ExitStatus != 0
                || !command.Result.Contains("SSH_PROXY_BRIDGE_KEY_OK", StringComparison.Ordinal))
            {
                var detail = string.IsNullOrWhiteSpace(command.Error)
                    ? $"remote exit={command.ExitStatus?.ToString() ?? "unknown"}"
                    : command.Error.Trim();
                throw new InvalidOperationException($"安装 SSH 公钥失败：{detail}");
            }
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }
    }

    public async Task<SshAuthenticationCapabilities> ProbeAuthenticationCapabilitiesAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidatePinnedProfile(profile);

        using var authentication = new NoneAuthenticationMethod(profile.Ssh.User);
        var connectionInfo = new ConnectionInfo(
            profile.Ssh.Host,
            profile.Ssh.Port,
            profile.Ssh.User,
            authentication)
        {
            Timeout = ConnectionTimeout
        };
        using var client = new SshClient(connectionInfo);
        AttachPinnedHostKey(client, profile.Ssh.HostKeySha256!);

        string? authenticationError = null;
        try
        {
            await ConnectWithPinErrorAsync(
                client,
                profile.Ssh.HostKeySha256!,
                cancellationToken);
        }
        catch (SshAuthenticationException exception)
        {
            // A "none" probe is expected to fail. SSH.NET records the methods
            // offered by the server before raising the authentication error.
            authenticationError = exception.Message;
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }

        return new SshAuthenticationCapabilities(
            authentication.AllowedAuthentications,
            authenticationError);
    }

    public async Task ValidateKeyOnlyLoginAsync(
        ConnectionProfile profile,
        string privateKeyPath,
        CancellationToken cancellationToken = default)
    {
        ValidatePinnedProfile(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);

        using var privateKey = new PrivateKeyFile(privateKeyPath);
        using var connectionInfo = new PrivateKeyConnectionInfo(
            profile.Ssh.Host,
            profile.Ssh.Port,
            profile.Ssh.User,
            privateKey)
        {
            Timeout = ConnectionTimeout
        };
        using var client = new SshClient(connectionInfo);
        AttachPinnedHostKey(client, profile.Ssh.HostKeySha256!);
        await ConnectWithPinErrorAsync(client, profile.Ssh.HostKeySha256!, cancellationToken);

        if (!client.IsConnected)
            throw new SshAuthenticationException("SSH Key-only 登录验证失败。");

        client.Disconnect();
    }

    public async Task<int> SelectAvailableRemotePortAsync(
        ConnectionProfile profile,
        string privateKeyPath,
        CancellationToken cancellationToken = default)
    {
        ValidatePinnedProfile(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        var candidates = RemotePortCandidateSelector.Create(
            profile.Remote.ProxyPort,
            profile.Remote.ProxyPortRangeStart,
            profile.Remote.ProxyPortRangeEnd);

        using var privateKey = new PrivateKeyFile(privateKeyPath);
        using var connectionInfo = new PrivateKeyConnectionInfo(
            profile.Ssh.Host,
            profile.Ssh.Port,
            profile.Ssh.User,
            privateKey)
        {
            Timeout = ConnectionTimeout
        };
        using var client = new SshClient(connectionInfo);
        AttachPinnedHostKey(client, profile.Ssh.HostKeySha256!);
        await ConnectWithPinErrorAsync(client, profile.Ssh.HostKeySha256!, cancellationToken);

        try
        {
            var candidateList = string.Join(' ', candidates);
            var script = $$"""
                set -eu
                is_free() {
                    port="$1"
                    if command -v python3 >/dev/null 2>&1; then
                        python3 -c 'import socket,sys; s=socket.socket(); s.bind(("127.0.0.1", int(sys.argv[1]))); s.close()' "$port"
                    elif command -v python >/dev/null 2>&1; then
                        python -c 'import socket,sys; s=socket.socket(); s.bind(("127.0.0.1", int(sys.argv[1]))); s.close()' "$port"
                    elif command -v ss >/dev/null 2>&1; then
                        test -z "$(ss -H -ltn "sport = :$port" 2>/dev/null)"
                    elif command -v timeout >/dev/null 2>&1; then
                        ! timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port"
                    else
                        ! bash -c "exec 3<>/dev/tcp/127.0.0.1/$port"
                    fi
                }

                for port in {{candidateList}}; do
                    if is_free "$port" >/dev/null 2>&1; then
                        printf 'SSH_PROXY_BRIDGE_PORT_OK:%s' "$port"
                        exit 0
                    fi
                done
                printf 'SSH_PROXY_BRIDGE_PORT_UNAVAILABLE' >&2
                exit 42
                """;
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r", string.Empty)));

            using var command = client.CreateCommand($"printf '%s' '{payload}' | base64 -d | bash");
            command.CommandTimeout = TimeSpan.FromSeconds(20);
            await command.ExecuteAsync(cancellationToken);

            var selectedPort = RemotePortCandidateSelector.ParseSelectedPort(command.Result);
            if (command.ExitStatus != 0 || selectedPort is null)
            {
                throw new InvalidOperationException(
                    $"远端代理端口范围 {profile.Remote.ProxyPortRangeStart}-{profile.Remote.ProxyPortRangeEnd} 中没有可用端口。");
            }

            return selectedPort.Value;
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }
    }

    public async Task<int> SelectAvailableRemotePortWithPasswordAsync(
        ConnectionProfile profile,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePinnedProfile(profile);
        ArgumentException.ThrowIfNullOrEmpty(password);

        using var connectionInfo = new PasswordConnectionInfo(
            profile.Ssh.Host,
            profile.Ssh.Port,
            profile.Ssh.User,
            password)
        {
            Timeout = ConnectionTimeout
        };
        using var client = new SshClient(connectionInfo);
        AttachPinnedHostKey(client, profile.Ssh.HostKeySha256!);
        await ConnectWithPinErrorAsync(client, profile.Ssh.HostKeySha256!, cancellationToken);

        return await SelectAvailableRemotePortAsync(profile, client, cancellationToken);
    }

    private static async Task<int> SelectAvailableRemotePortAsync(
        ConnectionProfile profile,
        SshClient client,
        CancellationToken cancellationToken)
    {
        var candidates = RemotePortCandidateSelector.Create(
            profile.Remote.ProxyPort,
            profile.Remote.ProxyPortRangeStart,
            profile.Remote.ProxyPortRangeEnd);

        try
        {
            var candidateList = string.Join(' ', candidates);
            var script = $$"""
                set -eu
                is_free() {
                    port="$1"
                    if command -v python3 >/dev/null 2>&1; then
                        python3 -c 'import socket,sys; s=socket.socket(); s.bind(("127.0.0.1", int(sys.argv[1]))); s.close()' "$port"
                    elif command -v python >/dev/null 2>&1; then
                        python -c 'import socket,sys; s=socket.socket(); s.bind(("127.0.0.1", int(sys.argv[1]))); s.close()' "$port"
                    elif command -v ss >/dev/null 2>&1; then
                        test -z "$(ss -H -ltn "sport = :$port" 2>/dev/null)"
                    elif command -v timeout >/dev/null 2>&1; then
                        ! timeout 1 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port"
                    else
                        ! bash -c "exec 3<>/dev/tcp/127.0.0.1/$port"
                    fi
                }

                for port in {{candidateList}}; do
                    if is_free "$port" >/dev/null 2>&1; then
                        printf 'SSH_PROXY_BRIDGE_PORT_OK:%s' "$port"
                        exit 0
                    fi
                done
                printf 'SSH_PROXY_BRIDGE_PORT_UNAVAILABLE' >&2
                exit 42
                """;
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r", string.Empty)));

            using var command = client.CreateCommand($"printf '%s' '{payload}' | base64 -d | bash");
            command.CommandTimeout = TimeSpan.FromSeconds(20);
            await command.ExecuteAsync(cancellationToken);

            var selectedPort = RemotePortCandidateSelector.ParseSelectedPort(command.Result);
            if (command.ExitStatus != 0 || selectedPort is null)
            {
                throw new InvalidOperationException(
                    $"远端代理端口范围 {profile.Remote.ProxyPortRangeStart}-{profile.Remote.ProxyPortRangeEnd} 中没有可用端口。");
            }

            return selectedPort.Value;
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }
    }

    private static void AttachPinnedHostKey(SshClient client, string expectedSha256)
    {
        client.HostKeyReceived += (_, eventArgs) =>
        {
            var actual = $"SHA256:{eventArgs.FingerPrintSHA256}";
            eventArgs.CanTrust = string.Equals(
                SshPasswordVerifier.NormalizeSha256(actual),
                SshPasswordVerifier.NormalizeSha256(expectedSha256),
                StringComparison.Ordinal);
        };
    }

    private static async Task ConnectWithPinErrorAsync(
        SshClient client,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        string? actual = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            actual = $"SHA256:{eventArgs.FingerPrintSHA256}";
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (SshConnectionException exception)
            when (actual is not null
                  && !string.Equals(
                      SshPasswordVerifier.NormalizeSha256(actual),
                      SshPasswordVerifier.NormalizeSha256(expectedSha256),
                      StringComparison.Ordinal))
        {
            throw new SshHostKeyMismatchException(expectedSha256, actual, exception);
        }
    }

    private static void ValidatePinnedProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Ssh.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Ssh.User);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Ssh.HostKeySha256);
    }
}

public sealed class SshAuthenticationCapabilities
{
    private readonly HashSet<string> _methods;

    public SshAuthenticationCapabilities(
        IEnumerable<string>? methods,
        string? authenticationError = null)
    {
        _methods = new HashSet<string>(
            methods ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (_methods.Count == 0 && !string.IsNullOrWhiteSpace(authenticationError))
        {
            var open = authenticationError.LastIndexOf('(');
            var close = authenticationError.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                foreach (var method in authenticationError[(open + 1)..close]
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _methods.Add(method);
                }
            }
        }
    }

    public IReadOnlyCollection<string> Methods => _methods;

    public bool SupportsPassword => _methods.Contains("password");

    public bool SupportsPublicKey => _methods.Contains("publickey");

    public bool RequiresPasswordGateway => SupportsPassword && !SupportsPublicKey;
}
