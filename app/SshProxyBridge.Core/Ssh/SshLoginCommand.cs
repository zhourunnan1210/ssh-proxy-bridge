using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SshProxyBridge.Core.Ssh;

public sealed record SshLoginDetails(string Host, int Port, string User, string? Password)
{
    public string SanitizedCommand
    {
        get
        {
            var hostToken = Host.Contains(':') ? $"[{Host}]" : Host;
            return Port == 22
                ? $"ssh {User}@{hostToken}"
                : FormattableString.Invariant($"ssh -p {Port} {User}@{hostToken}");
        }
    }

    public override string ToString() => SanitizedCommand;
}

public static partial class SshLoginCommand
{
    private const string PasswordLabel = @"密[\t\p{Zs}]*码";

    public static bool ContainsPasswordLabel(string text) =>
        !string.IsNullOrEmpty(text) && PasswordLabelRegex().IsMatch(text);

    public static bool TryParse(string text, [NotNullWhen(true)] out SshLoginDetails? details)
    {
        details = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var command = CommandLabelRegex().Replace(text.TrimStart(), string.Empty, 1);
        var passwordField = PasswordFieldRegex().Match(command);
        string? password = null;
        if (passwordField.Success)
        {
            password = command[(passwordField.Index + passwordField.Length)..].TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(password) || password.Any(char.IsControl))
                return false;

            command = command[..passwordField.Index];
        }

        var match = LoginCommandRegex().Match(command.Trim());
        if (!match.Success)
            return false;

        var port = 22;
        var portText = match.Groups["port"];
        if (portText.Captures.Count > 1
            || (portText.Success && (!int.TryParse(portText.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)))
        {
            return false;
        }

        var destination = match.Groups["destination"].Value;
        var separator = destination.LastIndexOf('@');
        if (separator <= 0 || separator == destination.Length - 1)
            return false;

        var user = destination[..separator];
        if (!UserRegex().IsMatch(user)
            || !TryNormalizeHost(destination[(separator + 1)..], out var host))
        {
            return false;
        }

        details = new SshLoginDetails(host, port, user, password);
        return true;
    }

    private static bool TryNormalizeHost(string token, out string host)
    {
        host = token;
        if (token.StartsWith('[') && token.EndsWith(']'))
        {
            host = token[1..^1];
            return !host.Contains('%') && IPAddress.TryParse(host, out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        if (token.Length > (token.EndsWith('.') ? 254 : 253) || !DnsHostRegex().IsMatch(token))
            return false;

        if (token.Count(character => character == '.') == 3
            && token.All(character => character == '.' || char.IsAsciiDigit(character)))
        {
            return IPAddress.TryParse(token, out var address) && address.ToString() == token;
        }

        return true;
    }

    [GeneratedRegex(@"\A登[录陆]指令[\t\p{Zs}]*[:：]\s*")]
    private static partial Regex CommandLabelRegex();

    [GeneratedRegex(PasswordLabel)]
    private static partial Regex PasswordLabelRegex();

    [GeneratedRegex(@"(?:\A|\s)" + PasswordLabel + @"[\t\p{Zs}]*[:：]\s*")]
    private static partial Regex PasswordFieldRegex();

    [GeneratedRegex("""
        \A (?i:ssh) [\t\p{Zs}]+
        (?: -p [\t\p{Zs}]* (?<port>[0-9]+) [\t\p{Zs}]+ )?
        (?<destination>\S+)
        (?: [\t\p{Zs}]+ -p [\t\p{Zs}]* (?<port>[0-9]+) )? \z
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LoginCommandRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]*(?:@[A-Za-z0-9_][A-Za-z0-9_.-]*)*\z")]
    private static partial Regex UserRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?\z")]
    private static partial Regex DnsHostRegex();
}
