namespace SshProxyBridge.Core.Profiles;

public static class LegacyDataMigrator
{
    public static LegacyDataMigrationResult MigrateIfNeeded(
        string? legacyRoot = null,
        string? currentRoot = null)
    {
        legacyRoot = Path.GetFullPath(legacyRoot ?? AppDataPaths.LegacyRoot);
        currentRoot = Path.GetFullPath(currentRoot ?? AppDataPaths.CurrentRoot);

        if (!Directory.Exists(legacyRoot)
            || string.Equals(legacyRoot, currentRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new LegacyDataMigrationResult(false, 0, []);
        }

        var copiedFiles = 0;
        var warnings = new List<string>();
        Directory.CreateDirectory(currentRoot);

        foreach (var sourcePath in Directory.EnumerateFiles(
                     legacyRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(legacyRoot, sourcePath);
            if (!IsSafeRelativePath(relativePath)
                || relativePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(currentRoot, relativePath));
            if (!IsChildPath(destinationPath, currentRoot) || File.Exists(destinationPath))
                continue;

            try
            {
                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("迁移目标缺少父目录。");
                Directory.CreateDirectory(destinationDirectory);

                var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(sourcePath, temporaryPath, overwrite: false);
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                    copiedFiles++;
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"{relativePath}: {exception.Message}");
            }
        }

        return new LegacyDataMigrationResult(copiedFiles > 0, copiedFiles, warnings);
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Equals("..", StringComparison.Ordinal)
        && !path.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsChildPath(string path, string parent)
    {
        var normalizedParent = parent.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record LegacyDataMigrationResult(
    bool Migrated,
    int CopiedFiles,
    IReadOnlyList<string> Warnings);
