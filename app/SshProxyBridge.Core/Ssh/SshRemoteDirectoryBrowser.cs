using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshProxyBridge.Core.Ssh;

public sealed class SshRemoteDirectoryBrowser
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(25);

    public async Task<RemoteDirectoryListing> ListAsync(
        string host,
        int port,
        string userName,
        string password,
        string expectedSha256,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (path != "." && !IsAbsolutePath(path))
            throw new ArgumentException("浏览路径必须是以 / 开头的 Linux 绝对路径。", nameof(path));

        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrowseTimeout);
        using var connectionInfo = new PasswordConnectionInfo(host, port, userName, password)
        {
            Timeout = RequestTimeout
        };
        using var client = new SftpClient(connectionInfo) { OperationTimeout = RequestTimeout };

        var expected = SshPasswordVerifier.NormalizeSha256(expectedSha256);
        string? fingerprint = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            fingerprint = eventArgs.FingerPrintSHA256;
            eventArgs.CanTrust = string.Equals(fingerprint, expected, StringComparison.Ordinal);
        };

        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (fingerprint is null)
                throw new SshConnectionException("SSH 已连接，但没有获得主机指纹。");

            var directory = path == "." ? client.WorkingDirectory : path;
            if (!IsAbsolutePath(directory))
                throw new InvalidOperationException("SFTP 未返回有效的 Linux 用户目录。");

            var directories = new List<string>();
            await foreach (var entry in client.ListDirectoryAsync(directory, timeout.Token).ConfigureAwait(false))
            {
                if (entry.IsDirectory && !entry.IsSymbolicLink && entry.Name is not ("." or ".."))
                    directories.Add(entry.Name);
            }

            timeout.Token.ThrowIfCancellationRequested();
            directories.Sort(StringComparer.Ordinal);
            return new RemoteDirectoryListing(directory, directories);
        }
        catch (SshConnectionException exception)
            when (fingerprint is not null && !string.Equals(fingerprint, expected, StringComparison.Ordinal))
        {
            throw new SshHostKeyMismatchException(expectedSha256, $"SHA256:{fingerprint}", exception);
        }
        catch (OperationCanceledException exception)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("浏览远程目录超时。", exception);
        }
    }

    private static bool IsAbsolutePath(string? path) =>
        path is not null && path.StartsWith('/') && !path.Contains('\0');
}

public sealed record RemoteDirectoryListing(string Path, IReadOnlyList<string> Directories);
