namespace SshProxyBridge.Core.Models;

public sealed class ConnectionProfile
{
    public int SchemaVersion { get; set; } = 1;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public ProfileStatus Status { get; set; } = ProfileStatus.Draft;

    public ProxyProfile Proxy { get; set; } = new();

    public SshProfile Ssh { get; set; } = new();

    public RemoteProfile Remote { get; set; } = new();

    public VsCodeProfile VsCode { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProxyProfile
{
    public string Adapter { get; set; } = "generic";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 7897;

    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Auto;

    public string? ExecutablePath { get; set; }

    public bool AutoStart { get; set; } = true;
}

public sealed class SshProfile
{
    public string Alias { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    public AuthenticationMode Authentication { get; set; } = AuthenticationMode.ManagedKey;

    public KeyProtectionMode KeyProtection { get; set; } = KeyProtectionMode.OneClick;

    public string IdentityFile { get; set; } = string.Empty;

    public string? HostKeySha256 { get; set; }

    public string? HostKeyAlgorithm { get; set; }

    public string? HostKeyBase64 { get; set; }

    public string? CredentialRef { get; set; }

    public bool HasStoredCredential { get; set; }
}

public sealed class RemoteProfile
{
    public string ProxyHost { get; set; } = "127.0.0.1";

    public int ProxyPort { get; set; } = 17897;

    public int ProxyPortRangeStart { get; set; } = 17897;

    public int ProxyPortRangeEnd { get; set; } = 17997;

    public string Shell { get; set; } = "auto";

    public int ManagedConfigVersion { get; set; } = 1;
}

public sealed class VsCodeProfile
{
    public string Channel { get; set; } = "stable";

    public bool Launch { get; set; } = true;

    public string DefaultWorkspace { get; set; } = "/";

    public string ExtensionId { get; set; } = "openai.chatgpt";
}

public enum ProfileStatus
{
    Draft,
    PasswordVerified,
    Ready
}

public enum ProxyProtocol
{
    Auto,
    Http,
    Socks5,
    Mixed
}

public enum AuthenticationMode
{
    ManagedKey,
    ExistingKey,
    PasswordGateway
}

public enum KeyProtectionMode
{
    OneClick,
    PassphraseProtected
}
