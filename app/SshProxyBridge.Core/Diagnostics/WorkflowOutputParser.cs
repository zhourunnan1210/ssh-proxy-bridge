using System.Text.RegularExpressions;

namespace SshProxyBridge.Core.Diagnostics;

public static partial class WorkflowOutputParser
{
    public static bool? ParseProxyReadiness(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var status = ProxyStatusLineRegex().Match(output);
        if (status.Success)
            return !status.Groups[1].Value.StartsWith("not", StringComparison.OrdinalIgnoreCase);

        if (output.Contains("HTTP proxy probe succeeded", StringComparison.OrdinalIgnoreCase)
            || output.Contains("HTTP proxy probe returned 204", StringComparison.OrdinalIgnoreCase)
            || output.Contains("SOCKS5 proxy probe succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return null;
    }

    [GeneratedRegex(@"^[ \t]*Proxy:[ \t]+(running|ready|not[ -]ready)[ \t]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ProxyStatusLineRegex();
}
