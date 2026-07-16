using System.Text.RegularExpressions;

namespace SshProxyBridge.Core.Ssh;

public static partial class RemotePortCandidateSelector
{
    public static IReadOnlyList<int> Create(int preferred, int rangeStart, int rangeEnd)
    {
        ValidatePort(preferred, nameof(preferred));
        ValidatePort(rangeStart, nameof(rangeStart));
        ValidatePort(rangeEnd, nameof(rangeEnd));
        if (rangeStart > rangeEnd)
            throw new ArgumentException("远端代理端口范围起点不能大于终点。");
        if (rangeEnd - rangeStart > 1000)
            throw new ArgumentException("远端代理端口范围最多包含 1001 个端口。");

        var candidates = new List<int>(rangeEnd - rangeStart + 2) { preferred };
        for (var port = rangeStart; port <= rangeEnd; port++)
        {
            if (port != preferred)
                candidates.Add(port);
        }

        return candidates;
    }

    public static int? ParseSelectedPort(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = SelectedPortRegex().Match(output);
        return match.Success
               && int.TryParse(match.Groups[1].Value, out var port)
               && port is >= 1 and <= 65535
            ? port
            : null;
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    [GeneratedRegex(@"SSH_PROXY_BRIDGE_PORT_OK:(\d{1,5})")]
    private static partial Regex SelectedPortRegex();
}
