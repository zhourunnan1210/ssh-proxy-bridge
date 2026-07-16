using System.Text.Json;
using System.Text.Json.Serialization;
using SshProxyBridge.Core.Models;

namespace SshProxyBridge.Core.Profiles;

public sealed class ProfileStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ProfileStore(string? filePath = null)
    {
        _filePath = filePath ?? AppDataPaths.ProfilesFile;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }

    public string FilePath => _filePath;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return Array.Empty<ConnectionProfile>();

        await using var stream = File.OpenRead(_filePath);
        var document = await JsonSerializer.DeserializeAsync<ProfileDocument>(
            stream, _jsonOptions, cancellationToken);

        if (document is null || document.SchemaVersion != 1)
            throw new InvalidDataException("不支持的服务器配置文件版本。");

        return document.Profiles;
    }

    public async Task UpsertAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count > 0)
            throw new ProfileValidationException(errors);

        var profiles = (await LoadAsync(cancellationToken)).ToList();
        var existingIndex = profiles.FindIndex(item => item.Id == profile.Id);
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        if (existingIndex >= 0)
            profiles[existingIndex] = profile;
        else
            profiles.Add(profile);

        await WriteAsync(new ProfileDocument { Profiles = profiles }, cancellationToken);
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profiles = (await LoadAsync(cancellationToken))
            .Where(item => item.Id != profileId)
            .ToList();

        await WriteAsync(new ProfileDocument { Profiles = profiles }, cancellationToken);
    }

    private async Task WriteAsync(ProfileDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Profile 路径缺少父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class ProfileDocument
    {
        public int SchemaVersion { get; set; } = 1;

        public List<ConnectionProfile> Profiles { get; set; } = [];
    }
}

public sealed class ProfileValidationException : Exception
{
    public ProfileValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
