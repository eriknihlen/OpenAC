using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Installation;

internal sealed record PreparedAssetVerificationEntry
{
    public required int Version { get; init; }

    public required string Path { get; init; }

    public required long Size { get; init; }

    public required long LastWriteUtcTicks { get; init; }

    public required string Sha256 { get; init; }
}

internal sealed class PreparedAssetVerificationCache
{
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public PreparedAssetVerificationCache(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(dataDirectory),
            "install.verification.json");
    }

    public string CachePath => _path;

    public PreparedAssetVerificationEntry? TryRead()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(_path);
            PreparedAssetVerificationEntry? entry =
                JsonSerializer.Deserialize<PreparedAssetVerificationEntry>(
                    stream,
                    SerializerOptions);
            if (entry is null
                || entry.Version != CurrentVersion
                || string.IsNullOrWhiteSpace(entry.Path)
                || string.IsNullOrWhiteSpace(entry.Sha256)
                || entry.Size <= 0)
            {
                return null;
            }

            return entry;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return null;
        }
    }

    public void Write(string path, long size, DateTime lastWriteUtc, string sha256)
    {
        var entry = new PreparedAssetVerificationEntry
        {
            Version = CurrentVersion,
            Path = System.IO.Path.GetFullPath(path),
            Size = size,
            LastWriteUtcTicks = lastWriteUtc.Ticks,
            Sha256 = sha256,
        };

        string temporaryPath = _path + ".tmp";
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(entry, SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            TryDelete(temporaryPath);
        }
    }

    public void Invalidate() => TryDelete(_path);

    public static bool Satisfies(
        PreparedAssetVerificationEntry? entry,
        string path,
        long size,
        DateTime lastWriteUtc,
        string recordedSha256) =>
        entry is not null
        && string.Equals(
            entry.Path,
            System.IO.Path.GetFullPath(path),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
        && entry.Size == size
        && entry.LastWriteUtcTicks == lastWriteUtc.Ticks
        && Integrity.FileIntegrity.Matches(entry.Sha256, recordedSha256);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
        }
    }
}
