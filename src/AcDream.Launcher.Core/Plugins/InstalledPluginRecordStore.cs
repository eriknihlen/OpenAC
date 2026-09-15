using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Whether a plugin's repo appeared in the curated list (L-308) at install time.</summary>
public enum PluginInstallSource
{
    Listed,
    Unlisted,
}

/// <summary>The target of an in-flight swap (pipeline step 6), written before the folder move so
/// recovery can reconcile a crash between the move and the record write (L-309).</summary>
public sealed record PendingPluginInstall(string Version, string Tag, string ZipSha256);

/// <summary>One launcher-managed plugin. <see cref="Version"/>, <see cref="Tag"/> and
/// <see cref="ZipSha256"/> are null only while <see cref="Pending"/> names a first install that has
/// not yet completed its swap.</summary>
public sealed record InstalledPluginRecord(
    string Id,
    string Repo,
    PluginInstallSource Source,
    string? Version,
    string? Tag,
    string? ZipSha256,
    DateTimeOffset InstalledAt,
    PendingPluginInstall? Pending)
{
    /// <summary>Tolerates a record written before L-313 dropped the acknowledgement checkbox; the
    /// launcher never sets it, and <see cref="InstalledPluginRecordStore.Load"/> clears it so a
    /// save never carries an old value forward.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? WarningAcceptedAt { get; init; }
}

/// <summary><c>DataDirectory/app/plugins-installed.json</c>: the record of every plugin the launcher
/// itself installed (L-309). Load/save follow <c>LauncherProfileStore</c>'s temp-file-and-rename,
/// owner-only idiom.</summary>
public sealed class InstalledPluginRecordStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const UnixFileMode OwnerOnlyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };

    public InstalledPluginRecordStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public static InstalledPluginRecordStore ForApplicationPaths(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new InstalledPluginRecordStore(
            Path.Combine(paths.DataDirectory, "app", "plugins-installed.json"));
    }

    public string FilePath { get; }

    public List<InstalledPluginRecord> Records { get; private set; } = [];

    public bool Load()
    {
        DeleteStaleTempFile(FilePath + ".tmp");

        if (!File.Exists(FilePath))
        {
            Records = [];
            return false;
        }

        EnsureExistingFilePermissions();

        Document? document;
        using (FileStream stream = File.OpenRead(FilePath))
        {
            try
            {
                document = JsonSerializer.Deserialize<Document>(stream, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new LauncherUpdateException(
                    $"'{FilePath}' is not a valid installed-plugin record.",
                    ex);
            }
        }

        if (document is null)
        {
            throw new LauncherUpdateException($"'{FilePath}' is empty.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new LauncherUpdateException(
                $"Unsupported installed-plugin record version {document.SchemaVersion}; "
                + $"expected {CurrentSchemaVersion}.");
        }

        List<InstalledPluginRecord> records = document.Plugins ?? [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledPluginRecord record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(record.Repo))
            {
                throw new LauncherUpdateException(
                    $"'{FilePath}' has an installed-plugin record with a missing id or repo.");
            }

            if (!ids.Add(record.Id))
            {
                throw new LauncherUpdateException(
                    $"'{FilePath}' has more than one record for '{record.Id}'.");
            }
        }

        Records = records.ConvertAll(record => record with { WarningAcceptedAt = null });
        return true;
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = FilePath + ".tmp";
        DeleteStaleTempFile(tempPath);
        try
        {
            var document = new Document
            {
                SchemaVersion = CurrentSchemaVersion,
                Plugins = Records,
            };
            using (FileStream stream = CreateTempFile(tempPath))
            {
                if (LauncherOperatingSystem.IsUnix)
                {
                    File.SetUnixFileMode(tempPath, OwnerOnlyFileMode);
                }

                JsonSerializer.Serialize(stream, document, SerializerOptions);
            }

            if (LauncherOperatingSystem.IsUnix
                && File.GetUnixFileMode(tempPath) != OwnerOnlyFileMode)
            {
                throw new IOException(
                    "The installed-plugin record temp file could not be secured to mode 0600.");
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            DeleteStaleTempFile(tempPath);
            throw;
        }
    }

    public InstalledPluginRecord? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Records.Find(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureExistingFilePermissions()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(FilePath);
            if (mode != OwnerOnlyFileMode)
            {
                File.SetUnixFileMode(FilePath, OwnerOnlyFileMode);
                mode = File.GetUnixFileMode(FilePath);
            }

            if (mode != OwnerOnlyFileMode)
            {
                throw new IOException($"Mode remained {mode} after normalization.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherUpdateException(
                $"'{FilePath}' could not be secured to owner-only mode 0600.",
                ex);
        }
    }

    private static FileStream CreateTempFile(string tempPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (LauncherOperatingSystem.IsUnix)
        {
            options.UnixCreateMode = OwnerOnlyFileMode;
        }

        return new FileStream(tempPath, options);
    }

    private static void DeleteStaleTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
        }
    }

    private sealed class Document
    {
        public int SchemaVersion { get; set; }

        public List<InstalledPluginRecord>? Plugins { get; set; }
    }
}
