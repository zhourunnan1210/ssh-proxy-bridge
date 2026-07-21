using System.IO;
using System.Text;
using System.Windows;
using SshProxyBridge.Core.Profiles;
using SshProxyBridge.Core.Security;

namespace SshProxyBridge.App;

public partial class App : Application
{
    private static readonly string[] PortablePackageFiles =
    [
        "ssh-proxy-bridge.ps1",
        "USER_GUIDE.md",
        "PORTABLE_README.txt",
        "config.example.json",
        "LICENSE",
        "THIRD_PARTY_NOTICES.md"
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SSH_PROXY_BRIDGE_ASKPASS"),
                "1",
                StringComparison.Ordinal))
        {
            Shutdown(WriteAskPassResponse() ? 0 : 3);
            return;
        }

        if (e.Args.Any(argument =>
                string.Equals(argument, "--verify-package", StringComparison.OrdinalIgnoreCase)))
        {
            var valid = PortablePackageFiles.All(fileName =>
                File.Exists(Path.Combine(AppContext.BaseDirectory, fileName)));
            Shutdown(valid ? 0 : 2);
            return;
        }

        try
        {
            LegacyMigration = LegacyDataMigrator.MigrateIfNeeded();
        }
        catch (Exception exception)
        {
            // Migration is deliberately non-destructive. If an unusual file-system
            // condition prevents copying, the legacy directory remains untouched.
            LegacyMigration = new LegacyDataMigrationResult(
                false,
                0,
                [$"旧版本数据未能自动复制：{exception.Message}"]);
        }

        base.OnStartup(e);
    }

    private static bool WriteAskPassResponse()
    {
        var target = Environment.GetEnvironmentVariable(
            "SSH_PROXY_BRIDGE_CREDENTIAL_TARGET");
        if (!CredentialReference.TryParseSshPassword(target, out var reference))
            return false;

        try
        {
            var credential = new WindowsCredentialStore()
                .ReadAsync(reference)
                .GetAwaiter()
                .GetResult();
            if (credential is null)
                return false;

            using var writer = new StreamWriter(
                Console.OpenStandardOutput(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: false);
            writer.Write(credential.Secret);
            writer.Flush();
            credential = null;
            return true;
        }
        catch
        {
            // AskPass must never show a second UI or print diagnostic details:
            // OpenSSH treats any stdout text as the password response.
            return false;
        }
    }

    internal static LegacyDataMigrationResult? LegacyMigration { get; private set; }
}
