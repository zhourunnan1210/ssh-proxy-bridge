using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshProxyBridge.Core.Ssh;

public sealed class SshPasswordVerifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    public async Task<HostKeyObservation> ObserveHostKeyAsync(
        string host,
        int port,
        string userName,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(host, port, userName);

        using var authentication = new NoneAuthenticationMethod(userName);
        var connectionInfo = new ConnectionInfo(host, port, userName, authentication)
        {
            Timeout = DefaultTimeout
        };
        using var client = new SshClient(connectionInfo);

        HostKeyObservation? observation = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            observation = CreateObservation(eventArgs);
            eventArgs.CanTrust = false;
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (Exception) when (observation is not null)
        {
            // Rejecting the key intentionally terminates the probe before authentication.
        }

        return observation
            ?? throw new SshConnectionException("服务器没有提供可验证的 SSH 主机密钥。");
    }

    public async Task<SshAuthenticationResult> AuthenticatePasswordAsync(
        string host,
        int port,
        string userName,
        string password,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(host, port, userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var normalizedExpected = NormalizeSha256(expectedSha256);
        using var connectionInfo = new PasswordConnectionInfo(host, port, userName, password)
        {
            Timeout = DefaultTimeout
        };
        using var client = new SshClient(connectionInfo);

        HostKeyObservation? observation = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            observation = CreateObservation(eventArgs);
            eventArgs.CanTrust = string.Equals(
                NormalizeSha256(observation.Sha256),
                normalizedExpected,
                StringComparison.Ordinal);
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (SshConnectionException exception)
            when (observation is not null
                  && !string.Equals(
                      NormalizeSha256(observation.Sha256),
                      normalizedExpected,
                      StringComparison.Ordinal))
        {
            throw new SshHostKeyMismatchException(expectedSha256, observation.Sha256, exception);
        }
        try
        {
            if (observation is null)
                throw new SshConnectionException("SSH 已连接，但没有获得主机指纹。");

            return new SshAuthenticationResult(observation, connectionInfo.ServerVersion ?? string.Empty);
        }
        finally
        {
            if (client.IsConnected)
                client.Disconnect();
        }
    }

    public static string NormalizeSha256(string fingerprint)
    {
        var trimmed = fingerprint.Trim();
        return trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[7..]
            : trimmed;
    }

    private static HostKeyObservation CreateObservation(HostKeyEventArgs eventArgs) =>
        new(
            eventArgs.HostKeyName,
            $"SHA256:{eventArgs.FingerPrintSHA256}",
            eventArgs.KeyLength,
            Convert.ToBase64String(eventArgs.HostKey));

    private static void ValidateEndpoint(string host, int port, string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
    }
}

public sealed record HostKeyObservation(
    string Algorithm,
    string Sha256,
    int KeyLength,
    string KeyBase64);

public sealed record SshAuthenticationResult(
    HostKeyObservation HostKey,
    string ServerVersion);

public sealed class SshHostKeyMismatchException : Exception
{
    public SshHostKeyMismatchException(string expected, string actual, Exception innerException)
        : base("SSH 主机指纹在确认后发生变化，连接已中止。", innerException)
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }

    public string Actual { get; }
}
