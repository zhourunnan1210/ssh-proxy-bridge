using System.IO;
using System.Windows;
using SshProxyBridge.Core.Profiles;

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

    internal static LegacyDataMigrationResult? LegacyMigration { get; private set; }
}
