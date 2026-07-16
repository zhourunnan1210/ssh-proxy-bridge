using System.Text.RegularExpressions;
using SshProxyBridge.Core.Models;

namespace SshProxyBridge.Core.Profiles;

public static partial class ProfileValidator
{
    public static IReadOnlyList<string> Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<string>();

        Required(profile.Name, "服务器名称", errors);
        Required(profile.Ssh.Host, "SSH 地址", errors);
        Required(profile.Ssh.User, "SSH 用户", errors);
        Required(profile.Ssh.Alias, "SSH 别名", errors);
        Required(profile.Proxy.Host, "代理地址", errors);
        Required(profile.Ssh.IdentityFile, "SSH 私钥路径", errors);

        Port(profile.Ssh.Port, "SSH 端口", errors);
        Port(profile.Proxy.Port, "本机代理端口", errors);
        Port(profile.Remote.ProxyPort, "远程代理端口", errors);
        Port(profile.Remote.ProxyPortRangeStart, "远程代理端口范围起点", errors);
        Port(profile.Remote.ProxyPortRangeEnd, "远程代理端口范围终点", errors);

        if (!string.IsNullOrWhiteSpace(profile.Ssh.Alias)
            && !SshAliasRegex().IsMatch(profile.Ssh.Alias))
        {
            errors.Add("SSH 别名只能包含英文字母、数字、点、下划线和连字符。");
        }

        if (string.IsNullOrWhiteSpace(profile.VsCode.DefaultWorkspace)
            || !profile.VsCode.DefaultWorkspace.StartsWith('/'))
        {
            errors.Add("远程项目目录必须是以 / 开头的 Linux 绝对路径。");
        }

        if (profile.Remote.ProxyPortRangeStart > profile.Remote.ProxyPortRangeEnd)
        {
            errors.Add("远程代理端口范围起点不能大于终点。");
        }

        if (profile.Remote.ProxyPortRangeEnd - profile.Remote.ProxyPortRangeStart > 1000)
        {
            errors.Add("远程代理端口范围最多包含 1001 个端口。");
        }

        return errors;
    }

    public static string CreateSshAlias(string name, string host, Guid profileId)
    {
        var source = string.IsNullOrWhiteSpace(name) ? host : name;
        var normalized = NonAliasCharacterRegex().Replace(source.Trim().ToLowerInvariant(), "-")
            .Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "server";

        if (normalized.Length > 32)
            normalized = normalized[..32].TrimEnd('-');

        return $"codex-{normalized}-{profileId.ToString("N")[..6]}";
    }

    private static void Required(string? value, string displayName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{displayName}不能为空。");
    }

    private static void Port(int value, string displayName, ICollection<string> errors)
    {
        if (value is < 1 or > 65535)
            errors.Add($"{displayName}必须在 1 到 65535 之间。");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SshAliasRegex();

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex NonAliasCharacterRegex();
}
