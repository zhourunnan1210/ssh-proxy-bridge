using System.Text.RegularExpressions;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Security;

namespace SshProxyBridge.Core.Profiles;

public sealed class ProfileCleanupService
{
    private readonly ICredentialStore _credentialStore;
    private readonly string _profileBaseDirectory;
    private readonly string _sshConfigPath;
    private readonly string _managedSshDirectory;

    public ProfileCleanupService(
        ICredentialStore credentialStore,
        string? profileBaseDirectory = null,
        string? sshConfigPath = null,
        string? managedSshDirectory = null)
    {
        _credentialStore = credentialStore;
        _profileBaseDirectory = profileBaseDirectory ?? AppDataPaths.ProfilesDirectory;
        _managedSshDirectory = managedSshDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh");
        _sshConfigPath = sshConfigPath ?? Path.Combine(_managedSshDirectory, "config");
    }

    public async Task<ProfileCleanupResult> CleanupAsync(
        ConnectionProfile profile,
        ProfileCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var completed = new List<string>();
        var warnings = new List<string>();

        if (options.DeleteCredential)
        {
            await RunStepAsync(
                "Windows 凭据已删除",
                () => _credentialStore.DeleteAsync(
                    CredentialReference.SshPassword(profile.Id),
                    cancellationToken),
                completed,
                warnings);
        }

        if (options.DeleteSshConfigBlock)
        {
            await RunStepAsync(
                "SSH Config 托管块已删除",
                () => RemoveSshConfigBlockAsync(profile.Ssh.Alias, cancellationToken),
                completed,
                warnings);
        }

        if (options.DeleteRuntimeFiles)
        {
            await RunStepAsync(
                "Profile 运行文件已删除",
                () => DeleteRuntimeDirectoryAsync(profile.Id),
                completed,
                warnings);
        }

        if (options.DeletePrivateKey)
        {
            await RunStepAsync(
                "专用 SSH Key 已删除",
                () => DeleteOwnedPrivateKeyAsync(profile, cancellationToken),
                completed,
                warnings);
        }

        return new ProfileCleanupResult(completed, warnings);
    }

    private async Task RemoveSshConfigBlockAsync(
        string alias,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_sshConfigPath))
            return;

        var content = await File.ReadAllTextAsync(_sshConfigPath, cancellationToken);
        var start = $"# >>> codex-remote-proxy:{alias} >>>";
        var end = $"# <<< codex-remote-proxy:{alias} <<<";
        var hasStart = content.Contains(start, StringComparison.Ordinal);
        var hasEnd = content.Contains(end, StringComparison.Ordinal);
        if (!hasStart && !hasEnd)
            return;
        if (!hasStart || !hasEnd)
            throw new InvalidDataException("SSH Config 托管块标记不完整，已拒绝自动修改。请手动检查文件。");

        var pattern = $"(?ms)^{Regex.Escape(start)}\\r?\\n.*?^{Regex.Escape(end)}(?:\\r?\\n)?";
        var updated = Regex.Replace(content, pattern, string.Empty, RegexOptions.CultureInvariant);
        if (updated == content)
            throw new InvalidDataException("未能精确定位 SSH Config 托管块，已拒绝自动修改。");

        await WriteAtomicAsync(_sshConfigPath, updated, cancellationToken);
    }

    private Task DeleteRuntimeDirectoryAsync(Guid profileId)
    {
        var basePath = Path.GetFullPath(_profileBaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var profilePath = Path.GetFullPath(Path.Combine(basePath, profileId.ToString("D")));
        var expectedParent = Path.GetDirectoryName(profilePath);
        if (!string.Equals(expectedParent, basePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(profilePath), profileId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Profile 运行目录未通过安全边界检查。");
        }

        if (Directory.Exists(profilePath))
            Directory.Delete(profilePath, recursive: true);
        return Task.CompletedTask;
    }

    private async Task DeleteOwnedPrivateKeyAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var privateKeyPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(profile.Ssh.IdentityFile));
        var sshDirectory = Path.GetFullPath(_managedSshDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = Path.GetRelativePath(sshDirectory, privateKeyPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(Path.DirectorySeparatorChar)
            || !Path.GetFileName(privateKeyPath).StartsWith("id_ed25519_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("私钥不在应用允许管理的 .ssh 安全范围内，已保留该文件。");
        }

        var publicKeyPath = $"{privateKeyPath}.pub";
        if (!File.Exists(publicKeyPath))
            throw new InvalidOperationException("缺少配套公钥，无法确认私钥归属，已保留该文件。");

        var publicKey = await File.ReadAllTextAsync(publicKeyPath, cancellationToken);
        var currentMarker = $"ssh-proxy-bridge:{profile.Id:D}";
        var legacyMarker = $"codex-remote-bridge:{profile.Id:D}";
        if (!publicKey.Contains(currentMarker, StringComparison.Ordinal)
            && !publicKey.Contains(legacyMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("公钥缺少当前 Profile 的托管标记，已保留该 Key。");

        if (File.Exists(privateKeyPath))
            File.Delete(privateKeyPath);
        File.Delete(publicKeyPath);
    }

    private static async Task RunStepAsync(
        string successMessage,
        Func<Task> action,
        ICollection<string> completed,
        ICollection<string> warnings)
    {
        try
        {
            await action();
            completed.Add(successMessage);
        }
        catch (Exception exception)
        {
            warnings.Add($"{successMessage.Replace("已删除", "删除失败")}：{exception.Message}");
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

public sealed record ProfileCleanupOptions(
    bool DeleteCredential,
    bool DeleteSshConfigBlock,
    bool DeleteRuntimeFiles,
    bool DeletePrivateKey);

public sealed record ProfileCleanupResult(
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> Warnings);
