using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshProxyBridge.Core.Models;

namespace SshProxyBridge.Core.Profiles;

public sealed class LegacyProfileStore(string? appDataRoot = null)
{
    private readonly string _removedProfilesDirectory = Path.Combine(
        appDataRoot ?? AppDataPaths.CurrentRoot, "removed-legacy-profiles");

    public async Task<ConnectionProfile?> LoadAsync(string configPath)
    {
        if (!File.Exists(configPath))
            return null;

        var config = await ReadConfigAsync(configPath);
        var profile = ReadProfile(config);
        return File.Exists(GetRemovalPath(profile.Id)) ? null : profile;
    }

    public async Task SaveEditsAsync(string configPath, ConnectionProfile editedProfile)
    {
        var config = await ReadConfigAsync(configPath);
        if (ReadProfile(config).Id != editedProfile.Id || File.Exists(GetRemovalPath(editedProfile.Id)))
            throw new InvalidOperationException("脚本配置中的服务器已更改，请重新打开应用后再编辑。");

        config["name"] = editedProfile.Name;
        var proxy = GetSection(config, "proxy");
        proxy["host"] = editedProfile.Proxy.Host;
        proxy["port"] = editedProfile.Proxy.Port;
        proxy["protocol"] = editedProfile.Proxy.Protocol.ToString().ToLowerInvariant();
        proxy["executablePath"] = editedProfile.Proxy.ExecutablePath ?? string.Empty;
        proxy["autoStart"] = editedProfile.Proxy.AutoStart;
        GetSection(config, "vscode")["remoteWorkspace"] = editedProfile.VsCode.DefaultWorkspace;

        var json = config.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true
        });
        var temporaryPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Windows PowerShell reads files without a BOM using the system code page.
            await File.WriteAllTextAsync(temporaryPath, json + Environment.NewLine, new UTF8Encoding(true));
            File.Replace(temporaryPath, configPath, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public Task RemoveFromListAsync(Guid profileId)
    {
        Directory.CreateDirectory(_removedProfilesDirectory);
        return File.WriteAllTextAsync(GetRemovalPath(profileId), string.Empty);
    }

    private string GetRemovalPath(Guid profileId) =>
        Path.Combine(_removedProfilesDirectory, profileId.ToString("D"));

    private static async Task<JsonObject> ReadConfigAsync(string configPath) =>
        JsonNode.Parse(await File.ReadAllTextAsync(configPath)) as JsonObject
        ?? throw new InvalidDataException("脚本配置必须是 JSON 对象。");

    private static ConnectionProfile ReadProfile(JsonObject config)
    {
        var ssh = GetSection(config, "ssh");
        var proxy = GetSection(config, "proxy");
        var vscode = GetSection(config, "vscode");
        var alias = GetValue(ssh, "alias", "current-server");
        var host = GetValue(ssh, "host", "unknown");
        var port = GetValue(ssh, "port", 22);
        var user = GetValue(ssh, "user", "user");
        var identity = JsonSerializer.Serialize(new { alias, host, port, user });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        var protocolName = GetValue(proxy, "protocol", "auto");
        var protocol = Enum.TryParse<ProxyProtocol>(protocolName, ignoreCase: true, out var parsedProtocol)
            && Enum.IsDefined(parsedProtocol)
            ? parsedProtocol : ProxyProtocol.Auto;

        return new ConnectionProfile
        {
            Id = new Guid(hash.AsSpan(0, 16)),
            Name = GetValue(config, "name", alias),
            Status = ProfileStatus.Ready,
            Proxy = new ProxyProfile
            {
                Host = GetValue(proxy, "host", "127.0.0.1"),
                Port = GetValue(proxy, "port", 7897),
                Protocol = protocol,
                ExecutablePath = GetValue(proxy, "executablePath", string.Empty),
                AutoStart = GetValue(proxy, "autoStart", true)
            },
            Ssh = new SshProfile
            {
                Alias = alias,
                Host = host,
                Port = port,
                User = user,
                IdentityFile = GetValue(ssh, "identityFile", string.Empty)
            },
            Remote = new RemoteProfile
            {
                ProxyPort = GetValue(ssh, "remoteProxyPort", 17897)
            },
            VsCode = new VsCodeProfile
            {
                DefaultWorkspace = GetValue(vscode, "remoteWorkspace", "/")
            }
        };
    }

    private static JsonObject GetSection(JsonObject config, string name) =>
        config[name] as JsonObject ?? throw new InvalidDataException($"脚本配置缺少 {name} 段。");

    private static T GetValue<T>(JsonObject section, string name, T fallback) =>
        section[name] is JsonValue value && value.TryGetValue<T>(out var result) ? result : fallback;
}
