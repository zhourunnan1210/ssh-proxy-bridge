using System.Text.RegularExpressions;

namespace SshProxyBridge.Core.Diagnostics;

public static partial class WorkflowOutputParser
{
    public static bool IsProxyReady(string command, string output)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);

        return string.Equals(command, "start", StringComparison.OrdinalIgnoreCase)
               || string.Equals(command, "repair", StringComparison.OrdinalIgnoreCase)
               || ProxyReadyLineRegex().IsMatch(output)
               || output.Contains(
                   "HTTP proxy probe succeeded",
                   StringComparison.OrdinalIgnoreCase)
               || output.Contains(
                   "HTTP proxy probe returned 204",
                   StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\s*Proxy:\s+(?:running|ready)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ProxyReadyLineRegex();
}
