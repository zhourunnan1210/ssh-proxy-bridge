namespace SshProxyBridge.Core.Profiles;

public static class AppDataPaths
{
    public const string CurrentDirectoryName = "SshProxyBridge";
    public const string LegacyDirectoryName = "CodexRemoteBridge";

    public static string CurrentRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        CurrentDirectoryName);

    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LegacyDirectoryName);

    public static string ProfilesFile => Path.Combine(CurrentRoot, "profiles.json");

    public static string ProfilesDirectory => Path.Combine(CurrentRoot, "profiles");
}
