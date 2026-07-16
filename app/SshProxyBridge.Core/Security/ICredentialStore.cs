namespace SshProxyBridge.Core.Security;

public interface ICredentialStore
{
    Task SaveAsync(
        CredentialReference reference,
        string userName,
        string secret,
        CancellationToken cancellationToken = default);

    Task<StoredCredential?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default);
}

public readonly record struct CredentialReference(string TargetName)
{
    private const string CurrentPrefix = "SshProxyBridge:ssh-password:";
    private const string LegacyPrefix = "CodexRemoteBridge:ssh-password:";

    public static CredentialReference SshPassword(Guid profileId) =>
        new($"{CurrentPrefix}{profileId:D}");

    public static CredentialReference LegacySshPassword(Guid profileId) =>
        new($"{LegacyPrefix}{profileId:D}");

    public bool TryGetLegacyEquivalent(out CredentialReference legacyReference)
    {
        if (TargetName.StartsWith(CurrentPrefix, StringComparison.Ordinal)
            && Guid.TryParse(TargetName[CurrentPrefix.Length..], out var profileId))
        {
            legacyReference = LegacySshPassword(profileId);
            return true;
        }

        legacyReference = default;
        return false;
    }
}

public sealed record StoredCredential(string UserName, string Secret);
