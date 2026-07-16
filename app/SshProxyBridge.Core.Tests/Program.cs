using SshProxyBridge.Core.Diagnostics;
using SshProxyBridge.Core.Models;
using SshProxyBridge.Core.Profiles;
using SshProxyBridge.Core.Security;
using SshProxyBridge.Core.Ssh;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Profile validation accepts a valid draft", TestValidProfile),
    ("Profile validation rejects invalid fields", TestInvalidProfile),
    ("Profile store round-trips without secrets", TestProfileStoreRoundTrip),
    ("Credential target is stable and profile-scoped", TestCredentialTarget),
    ("Legacy application data migration is additive and idempotent", TestLegacyDataMigration),
    ("SSH SHA256 fingerprint normalization is strict and stable", TestFingerprintNormalization),
    ("Proxy status parsing accepts variable spacing without accepting not-ready", TestProxyStatusParsing),
    ("Remote port candidates prefer the requested port and avoid duplicates", TestRemotePortCandidates),
    ("Remote port marker parsing is strict", TestRemotePortMarker),
    ("One-click ED25519 key generation is idempotent", TestSshKeyGeneration),
    ("Runtime config and known_hosts contain no password", TestRuntimeArtifacts),
    ("Profile cleanup removes only owned local artifacts", TestProfileCleanup),
    ("Profile cleanup preserves an unowned private key", TestProfileCleanupPreservesUnownedKey),
    ("Profile cleanup preserves malformed SSH config", TestProfileCleanupPreservesMalformedConfig),
    ("Legacy Windows credential migrates without deleting the old target", TestLegacyCredentialMigration),
    ("Windows Credential Manager round-trips a disposable test secret", TestCredentialRoundTrip)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;

static Task TestValidProfile()
{
    var profile = CreateValidProfile();
    Assert(ProfileValidator.Validate(profile).Count == 0, "Valid profile was rejected.");
    return Task.CompletedTask;
}

static Task TestInvalidProfile()
{
    var profile = CreateValidProfile();
    profile.Ssh.Port = 70000;
    profile.Ssh.Alias = "contains spaces";
    profile.VsCode.DefaultWorkspace = "relative/path";

    var errors = ProfileValidator.Validate(profile);
    Assert(errors.Count >= 3, "Expected port, alias and workspace errors.");
    return Task.CompletedTask;
}

static async Task TestProfileStoreRoundTrip()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.Tests.{Guid.NewGuid():N}");
    var filePath = Path.Combine(directory, "profiles.json");

    try
    {
        var store = new ProfileStore(filePath);
        var profile = CreateValidProfile();
        await store.UpsertAsync(profile);

        var loaded = await store.LoadAsync();
        Assert(loaded.Count == 1, "Expected one profile after save.");
        Assert(loaded[0].Id == profile.Id, "Profile ID changed during serialization.");
        Assert(loaded[0].Proxy.Protocol == ProxyProtocol.Auto, "Enum did not round-trip.");

        var json = await File.ReadAllTextAsync(filePath);
        Assert(!json.Contains("\"secret\"", StringComparison.OrdinalIgnoreCase),
            "Profile schema contains a secret field.");
        Assert(!json.Contains("\"password\"", StringComparison.OrdinalIgnoreCase),
            "Profile schema contains a password field.");

        await store.DeleteAsync(profile.Id);
        Assert((await store.LoadAsync()).Count == 0, "Profile delete failed.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static Task TestCredentialTarget()
{
    var profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    var reference = CredentialReference.SshPassword(profileId);
    Assert(
        reference.TargetName == "SshProxyBridge:ssh-password:11111111-2222-3333-4444-555555555555",
        "Unexpected credential target.");
    Assert(reference.TryGetLegacyEquivalent(out var legacyReference),
        "New credential target did not expose a legacy equivalent.");
    Assert(
        legacyReference.TargetName == "CodexRemoteBridge:ssh-password:11111111-2222-3333-4444-555555555555",
        "Unexpected legacy credential target.");
    return Task.CompletedTask;
}

static async Task TestLegacyDataMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.MigrationTests.{Guid.NewGuid():N}");
    var legacyRoot = Path.Combine(directory, "legacy");
    var currentRoot = Path.Combine(directory, "current");

    try
    {
        var legacyState = Path.Combine(legacyRoot, "profiles", Guid.NewGuid().ToString("D"), "state");
        Directory.CreateDirectory(legacyState);
        Directory.CreateDirectory(currentRoot);
        await File.WriteAllTextAsync(Path.Combine(legacyRoot, "profiles.json"), "{\"schemaVersion\":1,\"profiles\":[]}");
        await File.WriteAllTextAsync(Path.Combine(legacyState, "tunnel.pid"), "12345");
        await File.WriteAllTextAsync(Path.Combine(legacyState, "tunnel.stdout.log"), "skip active log");
        await File.WriteAllTextAsync(Path.Combine(currentRoot, "keep.txt"), "keep current");

        var first = LegacyDataMigrator.MigrateIfNeeded(legacyRoot, currentRoot);
        var second = LegacyDataMigrator.MigrateIfNeeded(legacyRoot, currentRoot);

        Assert(first.Migrated && first.CopiedFiles == 2, "Expected stable legacy files to be copied once.");
        Assert(second.CopiedFiles == 0, "Repeated migration overwrote existing files.");
        Assert(File.Exists(Path.Combine(legacyRoot, "profiles.json")), "Legacy data was removed.");
        Assert(File.Exists(Path.Combine(currentRoot, "profiles.json")), "Profile data was not migrated.");
        Assert(File.Exists(Path.Combine(currentRoot, Path.GetRelativePath(legacyRoot, Path.Combine(legacyState, "tunnel.pid")))),
            "Tunnel PID was not migrated.");
        Assert(!File.Exists(Path.Combine(currentRoot, Path.GetRelativePath(legacyRoot, Path.Combine(legacyState, "tunnel.stdout.log")))),
            "Active log file should not be migrated.");
        Assert(await File.ReadAllTextAsync(Path.Combine(currentRoot, "keep.txt")) == "keep current",
            "Existing current data was modified.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static Task TestFingerprintNormalization()
{
    const string value = "abcDEF123";
    Assert(SshPasswordVerifier.NormalizeSha256($"SHA256:{value}") == value,
        "SHA256 prefix was not removed.");
    Assert(SshPasswordVerifier.NormalizeSha256($"  {value}  ") == value,
        "Fingerprint whitespace was not normalized.");
    return Task.CompletedTask;
}

static Task TestProxyStatusParsing()
{
    Assert(WorkflowOutputParser.IsProxyReady("status", "Proxy:  running\nTunnel: running"),
        "Double-spaced running status was not recognized.");
    Assert(WorkflowOutputParser.IsProxyReady("status", "Proxy:\trunning"),
        "Tab-separated running status was not recognized.");
    Assert(!WorkflowOutputParser.IsProxyReady("status", "Proxy:  not ready"),
        "Not-ready proxy status was incorrectly accepted.");
    Assert(WorkflowOutputParser.IsProxyReady("doctor", "[PASS] HTTP proxy probe returned 204."),
        "Successful diagnostic proxy probe was not recognized.");
    return Task.CompletedTask;
}

static Task TestRemotePortCandidates()
{
    var candidates = RemotePortCandidateSelector.Create(17899, 17897, 17900);
    Assert(candidates.SequenceEqual([17899, 17897, 17898, 17900]),
        "Unexpected remote port candidate order.");
    Assert(candidates.Distinct().Count() == candidates.Count,
        "Remote port candidates contain duplicates.");
    return Task.CompletedTask;
}

static Task TestRemotePortMarker()
{
    Assert(RemotePortCandidateSelector.ParseSelectedPort("SSH_PROXY_BRIDGE_PORT_OK:17898") == 17898,
        "Valid remote port marker was not parsed.");
    Assert(RemotePortCandidateSelector.ParseSelectedPort("PORT_OK:17898") is null,
        "Untrusted remote output was accepted.");
    Assert(RemotePortCandidateSelector.ParseSelectedPort("SSH_PROXY_BRIDGE_PORT_OK:70000") is null,
        "Out-of-range remote port marker was accepted.");
    return Task.CompletedTask;
}

static async Task TestSshKeyGeneration()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.KeyTests.{Guid.NewGuid():N}");
    var privateKeyPath = Path.Combine(directory, "id_ed25519_test");

    try
    {
        var manager = new SshKeyManager();
        var profileId = Guid.NewGuid();
        var first = await manager.EnsureOneClickKeyAsync(privateKeyPath, profileId);
        var second = await manager.EnsureOneClickKeyAsync(privateKeyPath, profileId);

        Assert(File.Exists(first.PrivateKeyPath), "Private key was not created.");
        Assert(File.Exists(first.PublicKeyPath), "Public key was not created.");
        Assert(first.PublicKey.StartsWith("ssh-ed25519 ", StringComparison.Ordinal),
            "Unexpected public key format.");
        Assert(first.PublicKey.Contains($"ssh-proxy-bridge:{profileId:D}", StringComparison.Ordinal),
            "New SSH key does not contain the current ownership marker.");
        Assert(first.PublicKey == second.PublicKey, "Repeated initialization replaced the key.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static async Task TestRuntimeArtifacts()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.RuntimeTests.{Guid.NewGuid():N}");

    try
    {
        var profile = CreateValidProfile();
        profile.Ssh.Port = 14240;
        profile.Ssh.HostKeySha256 = "SHA256:test-fingerprint";
        profile.Ssh.HostKeyAlgorithm = "ssh-ed25519";
        profile.Ssh.HostKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAITestKeyMaterial";

        var writer = new ProfileRuntimeWriter(directory);
        var artifacts = await writer.WriteAsync(profile);
        var runtimeJson = await File.ReadAllTextAsync(artifacts.RuntimeConfigPath);
        var knownHosts = await File.ReadAllTextAsync(artifacts.KnownHostsPath);

        Assert(!runtimeJson.Contains("\"password\"", StringComparison.OrdinalIgnoreCase),
            "Runtime config contains a password field.");
        Assert(runtimeJson.Contains("userKnownHostsFile", StringComparison.Ordinal),
            "Runtime config does not reference managed known_hosts.");
        Assert(knownHosts.Contains("[203.0.113.10]:14240 ssh-ed25519", StringComparison.Ordinal),
            "known_hosts does not use the correct non-default port token.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static async Task TestProfileCleanup()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.CleanupTests.{Guid.NewGuid():N}");
    var profileBase = Path.Combine(directory, "profiles");
    var sshDirectory = Path.Combine(directory, ".ssh");
    var sshConfigPath = Path.Combine(sshDirectory, "config");
    Directory.CreateDirectory(sshDirectory);

    try
    {
        var profile = CreateValidProfile();
        profile.Ssh.IdentityFile = Path.Combine(sshDirectory, $"id_ed25519_{profile.Ssh.Alias}");
        await File.WriteAllTextAsync(profile.Ssh.IdentityFile, "disposable private key");
        await File.WriteAllTextAsync(
            $"{profile.Ssh.IdentityFile}.pub",
            $"ssh-ed25519 TEST codex-remote-bridge:{profile.Id:D}");

        var runtimeDirectory = Path.Combine(profileBase, profile.Id.ToString("D"));
        Directory.CreateDirectory(runtimeDirectory);
        await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, "runtime.json"), "{}");

        var start = $"# >>> codex-remote-proxy:{profile.Ssh.Alias} >>>";
        var end = $"# <<< codex-remote-proxy:{profile.Ssh.Alias} <<<";
        await File.WriteAllTextAsync(
            sshConfigPath,
            $"Host keep-me{Environment.NewLine}    HostName example.test{Environment.NewLine}{start}{Environment.NewLine}Host {profile.Ssh.Alias}{Environment.NewLine}{end}{Environment.NewLine}");

        var credentialStore = new FakeCredentialStore();
        await credentialStore.SaveAsync(
            CredentialReference.SshPassword(profile.Id),
            profile.Ssh.User,
            "temporary-secret");
        var service = new ProfileCleanupService(
            credentialStore,
            profileBase,
            sshConfigPath,
            sshDirectory);
        var result = await service.CleanupAsync(
            profile,
            new ProfileCleanupOptions(true, true, true, true));

        Assert(result.Warnings.Count == 0, "Owned cleanup produced a warning.");
        Assert(!Directory.Exists(runtimeDirectory), "Runtime directory was not deleted.");
        Assert(!File.Exists(profile.Ssh.IdentityFile), "Owned private key was not deleted.");
        Assert(!File.Exists($"{profile.Ssh.IdentityFile}.pub"), "Owned public key was not deleted.");
        Assert(await credentialStore.ReadAsync(CredentialReference.SshPassword(profile.Id)) is null,
            "Profile credential was not deleted.");
        var config = await File.ReadAllTextAsync(sshConfigPath);
        Assert(config.Contains("Host keep-me", StringComparison.Ordinal),
            "Unrelated SSH config content was removed.");
        Assert(!config.Contains(start, StringComparison.Ordinal),
            "Managed SSH config block was not removed.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static async Task TestProfileCleanupPreservesUnownedKey()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.KeyCleanupTests.{Guid.NewGuid():N}");
    var sshDirectory = Path.Combine(directory, ".ssh");
    Directory.CreateDirectory(sshDirectory);

    try
    {
        var profile = CreateValidProfile();
        profile.Ssh.IdentityFile = Path.Combine(sshDirectory, $"id_ed25519_{profile.Ssh.Alias}");
        await File.WriteAllTextAsync(profile.Ssh.IdentityFile, "must remain");
        await File.WriteAllTextAsync($"{profile.Ssh.IdentityFile}.pub", "ssh-ed25519 TEST someone-else");

        var service = new ProfileCleanupService(
            new FakeCredentialStore(),
            Path.Combine(directory, "profiles"),
            Path.Combine(sshDirectory, "config"),
            sshDirectory);
        var result = await service.CleanupAsync(
            profile,
            new ProfileCleanupOptions(false, false, false, true));

        Assert(result.Warnings.Count == 1, "Unowned key deletion was not refused.");
        Assert(File.Exists(profile.Ssh.IdentityFile), "Unowned private key was deleted.");
        Assert(File.Exists($"{profile.Ssh.IdentityFile}.pub"), "Unowned public key was deleted.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static async Task TestProfileCleanupPreservesMalformedConfig()
{
    var directory = Path.Combine(Path.GetTempPath(), $"SshProxyBridge.ConfigCleanupTests.{Guid.NewGuid():N}");
    var sshDirectory = Path.Combine(directory, ".ssh");
    var sshConfigPath = Path.Combine(sshDirectory, "config");
    Directory.CreateDirectory(sshDirectory);

    try
    {
        var profile = CreateValidProfile();
        var malformed = $"Host keep-me{Environment.NewLine}# >>> codex-remote-proxy:{profile.Ssh.Alias} >>>{Environment.NewLine}";
        await File.WriteAllTextAsync(sshConfigPath, malformed);
        var service = new ProfileCleanupService(
            new FakeCredentialStore(),
            Path.Combine(directory, "profiles"),
            sshConfigPath,
            sshDirectory);

        var result = await service.CleanupAsync(
            profile,
            new ProfileCleanupOptions(false, true, false, false));

        Assert(result.Warnings.Count == 1, "Malformed SSH config did not produce a warning.");
        Assert(await File.ReadAllTextAsync(sshConfigPath) == malformed,
            "Malformed SSH config was modified.");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static async Task TestCredentialRoundTrip()
{
    var store = new WindowsCredentialStore();
    var reference = new CredentialReference($"SshProxyBridge:test:{Guid.NewGuid():D}");
    var secret = $"temporary-{Guid.NewGuid():N}";

    try
    {
        await store.SaveAsync(reference, "test-user", secret);
        var loaded = await store.ReadAsync(reference);
        Assert(loaded is not null, "Credential was not found after save.");
        Assert(loaded!.UserName == "test-user", "Credential user name changed.");
        Assert(loaded.Secret == secret, "Credential secret changed.");
    }
    finally
    {
        await store.DeleteAsync(reference);
    }

    Assert(await store.ReadAsync(reference) is null, "Disposable test credential was not deleted.");
}

static async Task TestLegacyCredentialMigration()
{
    var store = new WindowsCredentialStore();
    var profileId = Guid.NewGuid();
    var currentReference = CredentialReference.SshPassword(profileId);
    var legacyReference = CredentialReference.LegacySshPassword(profileId);
    var secret = $"temporary-legacy-{Guid.NewGuid():N}";

    try
    {
        await store.SaveAsync(legacyReference, "legacy-test-user", secret);
        var migrated = await store.ReadAsync(currentReference);

        Assert(migrated is not null && migrated.Secret == secret,
            "Legacy credential was not returned through the current target.");
        Assert(await store.ReadAsync(currentReference) is not null,
            "Current credential target was not created.");
        Assert(await store.ReadAsync(legacyReference) is not null,
            "Legacy credential was deleted during migration.");

        await store.DeleteAsync(currentReference);
        Assert(await store.ReadAsync(currentReference) is null,
            "Current credential target was not deleted.");
        Assert(await store.ReadAsync(legacyReference) is null,
            "Legacy credential target was not deleted during explicit cleanup.");
    }
    finally
    {
        await store.DeleteAsync(currentReference);
        await store.DeleteAsync(legacyReference);
    }
}

static ConnectionProfile CreateValidProfile()
{
    var id = Guid.NewGuid();
    var alias = ProfileValidator.CreateSshAlias("GPU Server", "203.0.113.10", id);

    return new ConnectionProfile
    {
        Id = id,
        Name = "GPU Server",
        Proxy = new ProxyProfile
        {
            Host = "127.0.0.1",
            Port = 7897,
            Protocol = ProxyProtocol.Auto
        },
        Ssh = new SshProfile
        {
            Alias = alias,
            Host = "203.0.113.10",
            Port = 22,
            User = "developer",
            IdentityFile = $"%USERPROFILE%\\.ssh\\id_ed25519_{alias}",
            CredentialRef = CredentialReference.SshPassword(id).TargetName
        },
        Remote = new RemoteProfile
        {
            ProxyPort = 17897
        },
        VsCode = new VsCodeProfile
        {
            DefaultWorkspace = "/workspace"
        }
    };
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, StoredCredential> _credentials = new(StringComparer.Ordinal);

    public Task SaveAsync(
        CredentialReference reference,
        string userName,
        string secret,
        CancellationToken cancellationToken = default)
    {
        _credentials[reference.TargetName] = new StoredCredential(userName, secret);
        return Task.CompletedTask;
    }

    public Task<StoredCredential?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        _credentials.TryGetValue(reference.TargetName, out var credential);
        return Task.FromResult(credential);
    }

    public Task DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        _credentials.Remove(reference.TargetName);
        return Task.CompletedTask;
    }
}
