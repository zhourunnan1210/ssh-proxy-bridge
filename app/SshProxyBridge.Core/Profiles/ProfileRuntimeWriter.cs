using System.Text.Json;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Security;

namespace SshProxyBridge.Core.Profiles;

public sealed class ProfileRuntimeWriter
{
    private readonly string _baseDirectory;
    private readonly string _askPassExecutablePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ProfileRuntimeWriter(
        string? baseDirectory = null,
        string? askPassExecutablePath = null)
    {
        _baseDirectory = baseDirectory ?? AppDataPaths.ProfilesDirectory;
        _askPassExecutablePath = askPassExecutablePath
            ?? Path.Combine(AppContext.BaseDirectory, "SshProxyBridge.exe");
    }

    public string GetRuntimeConfigPath(Guid profileId) =>
        Path.Combine(GetProfileDirectory(profileId), "runtime.json");

    public async Task<ProfileRuntimeArtifacts> WriteAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateHostKey(profile);

        var directory = GetProfileDirectory(profile.Id);
        Directory.CreateDirectory(directory);
        var knownHostsPath = Path.Combine(directory, "known_hosts");
        var runtimeConfigPath = Path.Combine(directory, "runtime.json");

        var hostToken = profile.Ssh.Port == 22
            ? profile.Ssh.Host
            : $"[{profile.Ssh.Host}]:{profile.Ssh.Port}";
        var knownHosts =
            $"# SSH Proxy Bridge profile {profile.Id:D}{Environment.NewLine}" +
            $"{hostToken} {profile.Ssh.HostKeyAlgorithm} {profile.Ssh.HostKeyBase64}{Environment.NewLine}";
        await WriteAtomicAsync(knownHostsPath, knownHosts, cancellationToken);

        var runtime = new
        {
            name = profile.Name,
            profileId = profile.Id,
            proxy = new
            {
                host = profile.Proxy.Host,
                port = profile.Proxy.Port,
                protocol = profile.Proxy.Protocol.ToString().ToLowerInvariant(),
                processName = string.Empty,
                executablePath = profile.Proxy.ExecutablePath ?? string.Empty,
                autoStart = profile.Proxy.AutoStart
            },
            ssh = new
            {
                alias = profile.Ssh.Alias,
                host = profile.Ssh.Host,
                port = profile.Ssh.Port,
                user = profile.Ssh.User,
                authentication = profile.Ssh.Authentication switch
                {
                    AuthenticationMode.PasswordGateway => "passwordGateway",
                    AuthenticationMode.ExistingKey => "existingKey",
                    _ => "managedKey"
                },
                identityFile = profile.Ssh.IdentityFile,
                credentialTarget = profile.Ssh.Authentication == AuthenticationMode.PasswordGateway
                    ? GetCredentialTarget(profile)
                    : string.Empty,
                askPassExecutable = profile.Ssh.Authentication == AuthenticationMode.PasswordGateway
                    ? _askPassExecutablePath
                    : string.Empty,
                remoteProxyHost = profile.Remote.ProxyHost,
                remoteProxyPort = profile.Remote.ProxyPort,
                userKnownHostsFile = knownHostsPath
            },
            vscode = new
            {
                launch = profile.VsCode.Launch,
                remoteWorkspace = profile.VsCode.DefaultWorkspace,
                extensionId = profile.VsCode.ExtensionId
            }
        };
        var json = JsonSerializer.Serialize(runtime, _jsonOptions);
        await WriteAtomicAsync(runtimeConfigPath, $"{json}{Environment.NewLine}", cancellationToken);

        return new ProfileRuntimeArtifacts(runtimeConfigPath, knownHostsPath);
    }

    private string GetProfileDirectory(Guid profileId) =>
        Path.Combine(_baseDirectory, profileId.ToString("D"));

    private static string GetCredentialTarget(ConnectionProfile profile)
    {
        var target = profile.Ssh.CredentialRef
            ?? CredentialReference.SshPassword(profile.Id).TargetName;
        if (!CredentialReference.TryParseSshPassword(target, out _))
            throw new InvalidOperationException("密码网关 Profile 缺少有效的 Windows 凭据引用。");
        return target;
    }

    private static void ValidateHostKey(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Ssh.HostKeySha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Ssh.HostKeyAlgorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Ssh.HostKeyBase64);
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

public sealed record ProfileRuntimeArtifacts(
    string RuntimeConfigPath,
    string KnownHostsPath);
