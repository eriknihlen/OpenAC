using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core.Integrity;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Updates;

public sealed record InstalledFileRecord(
    string Path,
    string Sha256,
    long Size,
    int UnixMode);

public sealed record ClientVersionRecord(
    int SchemaVersion,
    string Version,
    string Rid,
    string ArchiveSha256,
    long ArchiveSize,
    IReadOnlyList<InstalledFileRecord> Files)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ClientActivationPointer(
    int SchemaVersion,
    string CurrentVersion,
    string? PreviousVersion)
{
    public const int CurrentSchemaVersion = 1;
}

public enum ClientVersionState
{
    Missing,
    Verified,
    Invalid,
}

public sealed record ClientVersionResolution(
    ClientVersionState State,
    string Status,
    LauncherVersion? Version,
    string? Directory,
    string? PreviousVersion,
    ClientVersionRecord? Record)
{
    public bool IsVerified => State == ClientVersionState.Verified;
}

/// <summary>
/// Strict installed-version and activation-pointer authority. LA9's DAT/pak
/// record is intentionally not represented here.
/// </summary>
public sealed class ClientVersionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task<string>> _computeSha256;
    private ClientVersionResolution _cached = new(
        ClientVersionState.Missing,
        "No versioned client is installed. Check for updates to install one.",
        null,
        null,
        null,
        null);

    public ClientVersionStore(
        ApplicationPathSet paths,
        Func<string, CancellationToken, Task<string>>? computeSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        AppDirectory = Path.Combine(Path.GetFullPath(paths.DataDirectory), "app");
        CurrentPointerPath = Path.Combine(AppDirectory, "current.json");
        PreviousPointerPath = Path.Combine(AppDirectory, "current.previous.json");
        Barrier = new UpdateSessionBarrier(paths.DataDirectory);
        _computeSha256 = computeSha256
            ?? ((path, token) => FileIntegrity.ComputeSha256HexAsync(path, token));
    }

    public string AppDirectory { get; }

    public string CurrentPointerPath { get; }

    public string PreviousPointerPath { get; }

    public UpdateSessionBarrier Barrier { get; }

    public ClientVersionResolution CachedResolution
    {
        get
        {
            lock (_gate)
            {
                return _cached;
            }
        }
    }

    public string GetVersionDirectory(LauncherVersion version) =>
        Path.Combine(AppDirectory, version.Value);

    public static string GetMetadataPath(string versionDirectory) =>
        Path.Combine(Path.GetFullPath(versionDirectory), "install.json");

    public async Task<ClientVersionResolution> LoadAndRecoverAsync(
        string rid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using UpdateSessionBarrier.ExclusiveLease lease = Barrier.AcquireExclusive();
            return await LoadAndRecoverUnderLeaseAsync(rid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LauncherUpdateException ex) when (ex.InnerException is IOException)
        {
            // Another launcher may legitimately hold a shared session lease.
            // Pointer publication is atomic and old versions are retained, so
            // a read-only verification remains safe; mutation/recovery waits
            // for the next startup without active sessions.
            return await LoadCurrentReadOnlyAsync(rid, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<ClientVersionResolution> LoadCurrentReadOnlyAsync(
        string rid,
        CancellationToken cancellationToken = default)
    {
        RequireRid(rid);
        PointerRead current = await ReadPointerAsync(CurrentPointerPath, cancellationToken)
            .ConfigureAwait(false);
        ClientVersionResolution resolution = current.Pointer is null
            ? (!File.Exists(CurrentPointerPath)
                ? new ClientVersionResolution(
                    ClientVersionState.Missing,
                    "No versioned client is installed. Check for updates to install one.",
                    null,
                    null,
                    null,
                    null)
                : Invalid(current.Error ?? "The client activation pointer is invalid."))
            : await ResolvePointerAsync(current.Pointer, rid, cancellationToken)
                .ConfigureAwait(false);
        SetCached(resolution);
        return resolution;
    }

    internal async Task<ClientVersionResolution> LoadAndRecoverUnderLeaseAsync(
        string rid,
        CancellationToken cancellationToken = default)
    {
        RequireRid(rid);
        Directory.CreateDirectory(AppDirectory);
        CleanupOwnedResidue();

        PointerRead current = await ReadPointerAsync(CurrentPointerPath, cancellationToken)
            .ConfigureAwait(false);
        if (current.Pointer is not null)
        {
            ClientVersionResolution resolution = await ResolvePointerAsync(
                    current.Pointer,
                    rid,
                    cancellationToken)
                .ConfigureAwait(false);
            SetCached(resolution);
            return resolution;
        }

        PointerRead previous = await ReadPointerAsync(PreviousPointerPath, cancellationToken)
            .ConfigureAwait(false);
        if (previous.Pointer is not null)
        {
            ClientVersionResolution recovered = await ResolvePointerAsync(
                    previous.Pointer,
                    rid,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovered.IsVerified)
            {
                await WritePointerFileAsync(
                        CurrentPointerPath,
                        previous.Pointer,
                        cancellationToken)
                    .ConfigureAwait(false);
                recovered = recovered with
                {
                    Status = "Recovered the last valid client activation pointer.",
                };
                SetCached(recovered);
                return recovered;
            }
        }

        ClientVersionResolution missingOrInvalid =
            !File.Exists(CurrentPointerPath) && !File.Exists(PreviousPointerPath)
                ? new ClientVersionResolution(
                    ClientVersionState.Missing,
                    "No versioned client is installed. Check for updates to install one.",
                    null,
                    null,
                    null,
                    null)
                : new ClientVersionResolution(
                    ClientVersionState.Invalid,
                    current.Error
                        ?? previous.Error
                        ?? "No valid client activation pointer could be recovered.",
                    null,
                    null,
                    null,
                    null);
        SetCached(missingOrInvalid);
        return missingOrInvalid;
    }

    internal async Task<ClientVersionResolution> PromoteAndActivateUnderLeaseAsync(
        string stagingDirectory,
        LauncherVersion version,
        string rid,
        ReleaseArtifact artifact,
        IReadOnlyList<ExtractedFileRecord> extractedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(extractedFiles);
        RequireRid(rid);

        string staging = Path.GetFullPath(stagingDirectory);
        RequireOwnedStagingPath(staging);
        ValidateRequiredExecutables(extractedFiles, rid, launcherPayload: false);
        if (extractedFiles.Any(file => string.Equals(
                file.Path,
                "install.json",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new LauncherUpdateException(
                "The client ZIP may not provide the launcher's install.json record.");
        }

        var record = new ClientVersionRecord(
            ClientVersionRecord.CurrentSchemaVersion,
            version.Value,
            rid,
            artifact.Sha256.ToLowerInvariant(),
            artifact.Size,
            extractedFiles
                .Select(file => new InstalledFileRecord(
                    file.Path,
                    file.Sha256,
                    file.Size,
                    file.UnixMode))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToArray());
        ValidateRecord(record, version, rid);
        await AtomicJsonFile.WriteAsync(
                GetMetadataPath(staging),
                record,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        ClientVersionResolution staged = await VerifyVersionDirectoryAsync(
                staging,
                version,
                rid,
                cancellationToken)
            .ConfigureAwait(false);
        if (!staged.IsVerified)
        {
            throw new LauncherUpdateException(staged.Status);
        }

        ClientActivationPointer? oldPointer = (await ReadPointerAsync(
                CurrentPointerPath,
                cancellationToken)
            .ConfigureAwait(false)).Pointer;
        string target = GetVersionDirectory(version);
        if (Directory.Exists(target))
        {
            ClientVersionResolution existing = await VerifyVersionDirectoryAsync(
                    target,
                    version,
                    rid,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing.IsVerified
                && existing.Record is not null
                && string.Equals(
                    existing.Record.ArchiveSha256,
                    artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                && existing.Record.ArchiveSize == artifact.Size)
            {
                SafeZipExtractor.TryDeleteDirectory(staging);
            }
            else
            {
                if (oldPointer is not null
                    && string.Equals(
                        oldPointer.CurrentVersion,
                        version.Value,
                        StringComparison.Ordinal))
                {
                    throw new LauncherUpdateException(
                        "The active client version is corrupt and cannot be replaced in place. "
                        + "Roll back before repairing it.");
                }

                string quarantine = Path.Combine(
                    AppDirectory,
                    $".client-corrupt-{Guid.NewGuid():N}");
                Directory.Move(target, quarantine);
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    Directory.Move(quarantine, target);
                    throw;
                }

                SafeZipExtractor.TryDeleteDirectory(quarantine);
            }
        }
        else
        {
            Directory.Move(staging, target);
        }

        string? previousVersion = oldPointer is null
            || string.Equals(
                oldPointer.CurrentVersion,
                version.Value,
                StringComparison.Ordinal)
                ? oldPointer?.PreviousVersion
                : oldPointer.CurrentVersion;
        var pointer = new ClientActivationPointer(
            ClientActivationPointer.CurrentSchemaVersion,
            version.Value,
            previousVersion);
        await SavePointerAsync(pointer, cancellationToken).ConfigureAwait(false);
        ClientVersionResolution resolution = await ResolvePointerAsync(
                pointer,
                rid,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsVerified)
        {
            throw new LauncherUpdateException(resolution.Status);
        }

        SetCached(resolution);
        return resolution;
    }

    public async Task<ClientVersionResolution> RollbackAsync(
        string rid,
        CancellationToken cancellationToken = default)
    {
        using UpdateSessionBarrier.ExclusiveLease lease = Barrier.AcquireExclusive();
        PointerRead read = await ReadPointerAsync(CurrentPointerPath, cancellationToken)
            .ConfigureAwait(false);
        ClientActivationPointer pointer = read.Pointer
            ?? throw new LauncherUpdateException(
                read.Error ?? "There is no active client version to roll back.");
        if (string.IsNullOrEmpty(pointer.PreviousVersion))
        {
            throw new LauncherUpdateException(
                "There is no previous client version available for rollback.");
        }

        LauncherVersion previous = LauncherVersion.Parse(pointer.PreviousVersion);
        ClientVersionResolution verified = await VerifyVersionDirectoryAsync(
                GetVersionDirectory(previous),
                previous,
                rid,
                cancellationToken)
            .ConfigureAwait(false);
        if (!verified.IsVerified)
        {
            throw new LauncherUpdateException(
                $"The previous client version cannot be activated: {verified.Status}");
        }

        var swapped = new ClientActivationPointer(
            ClientActivationPointer.CurrentSchemaVersion,
            previous.Value,
            pointer.CurrentVersion);
        await SavePointerAsync(swapped, cancellationToken).ConfigureAwait(false);
        ClientVersionResolution resolution = await ResolvePointerAsync(
                swapped,
                rid,
                cancellationToken)
            .ConfigureAwait(false);
        SetCached(resolution);
        return resolution;
    }

    internal string CreateClientStagingDirectory(Guid transactionId)
    {
        Directory.CreateDirectory(AppDirectory);
        return Path.Combine(AppDirectory, $".client-staging-{transactionId:N}");
    }

    internal static void ValidateRequiredExecutables(
        IReadOnlyList<ExtractedFileRecord> files,
        string rid,
        bool launcherPayload)
    {
        string suffix = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        string[] required = launcherPayload
            ? ["acdream-launcher" + suffix]
            : ["AcDream.App" + suffix, "acdream-headless" + suffix];
        foreach (string path in required)
        {
            ExtractedFileRecord? file = files.SingleOrDefault(candidate =>
                string.Equals(candidate.Path, path, StringComparison.Ordinal));
            if (file is null)
            {
                throw new LauncherUpdateException(
                    $"The release ZIP is missing required root executable '{path}'.");
            }

            if (rid.StartsWith("linux-", StringComparison.Ordinal)
                && (file.UnixMode & (int)UnixFileMode.UserExecute) == 0)
            {
                throw new LauncherUpdateException(
                    $"The Linux release executable '{path}' lacks owner execute permission.");
            }
        }
    }

    private async Task<ClientVersionResolution> ResolvePointerAsync(
        ClientActivationPointer pointer,
        string rid,
        CancellationToken cancellationToken)
    {
        string? error = ValidatePointer(pointer);
        if (error is not null)
        {
            return Invalid(error);
        }

        LauncherVersion version = LauncherVersion.Parse(pointer.CurrentVersion);
        ClientVersionResolution resolution = await VerifyVersionDirectoryAsync(
                GetVersionDirectory(version),
                version,
                rid,
                cancellationToken)
            .ConfigureAwait(false);
        return resolution.IsVerified
            ? resolution with { PreviousVersion = pointer.PreviousVersion }
            : resolution;
    }

    private async Task<ClientVersionResolution> VerifyVersionDirectoryAsync(
        string directory,
        LauncherVersion version,
        string rid,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return Invalid($"Client version {version} directory is missing.");
        }

        try
        {
            RejectReparseTree(directory);
            ClientVersionRecord? record = await ReadStrictAsync<ClientVersionRecord>(
                    GetMetadataPath(directory),
                    cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Invalid($"Client version {version} install.json is missing.");
            }

            string? contractError = ValidateRecord(record, version, rid);
            if (contractError is not null)
            {
                return Invalid(contractError);
            }

            string[] actualFiles = Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => NormalizeRelative(directory, path))
                .Where(path => !string.Equals(
                    path,
                    "install.json",
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            string[] recordedFiles = record.Files
                .Select(file => file.Path)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (!actualFiles.SequenceEqual(recordedFiles, StringComparer.Ordinal))
            {
                return Invalid(
                    $"Client version {version} contains missing or unrecorded files.");
            }

            foreach (InstalledFileRecord file in record.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ResolveContained(directory, file.Path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Size)
                {
                    return Invalid(
                        $"Client version {version} file '{file.Path}' size is corrupt.");
                }

                string sha256 = await _computeSha256(path, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        sha256,
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid(
                        $"Client version {version} file '{file.Path}' SHA-256 is corrupt.");
                }

                if (OperatingSystem.IsLinux()
                    && ((int)File.GetUnixFileMode(path) & 0x1FF) != file.UnixMode)
                {
                    return Invalid(
                        $"Client version {version} file '{file.Path}' mode is corrupt.");
                }
            }

            return new ClientVersionResolution(
                ClientVersionState.Verified,
                $"Client version {version} verified.",
                version,
                directory,
                null,
                record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException
                                   or FormatException
                                   or LauncherUpdateException)
        {
            return Invalid(
                $"Client version {version} could not be verified: {ex.Message}");
        }
    }

    private async Task SavePointerAsync(
        ClientActivationPointer pointer,
        CancellationToken cancellationToken)
    {
        string? error = ValidatePointer(pointer);
        if (error is not null)
        {
            throw new LauncherUpdateException(error);
        }

        if (File.Exists(CurrentPointerPath))
        {
            byte[] previous = await File.ReadAllBytesAsync(
                    CurrentPointerPath,
                    cancellationToken)
                .ConfigureAwait(false);
            PointerRead validPrevious = ParsePointer(previous);
            if (validPrevious.Pointer is not null)
            {
                await AtomicJsonFile.WriteBytesAsync(
                        PreviousPointerPath,
                        previous,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await WritePointerFileAsync(CurrentPointerPath, pointer, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task WritePointerFileAsync(
        string path,
        ClientActivationPointer pointer,
        CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(path, pointer, SerializerOptions, cancellationToken);

    private static async Task<PointerRead> ReadPointerAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new PointerRead(null, null);
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return ParsePointer(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PointerRead(null, $"Client pointer could not be read: {ex.Message}");
        }
    }

    private static PointerRead ParsePointer(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            ClientActivationPointer? pointer = ParseStrict<ClientActivationPointer>(bytes.Span);
            string? error = pointer is null
                ? "Client pointer is empty."
                : ValidatePointer(pointer);
            return error is null
                ? new PointerRead(pointer, null)
                : new PointerRead(null, error);
        }
        catch (Exception ex) when (ex is JsonException
                                   or LauncherUpdateException
                                   or FormatException)
        {
            return new PointerRead(null, $"Client pointer is invalid: {ex.Message}");
        }
    }

    private static string? ValidatePointer(ClientActivationPointer pointer)
    {
        if (pointer.SchemaVersion != ClientActivationPointer.CurrentSchemaVersion)
        {
            return $"Client pointer schema version {pointer.SchemaVersion} is not supported.";
        }

        if (!LauncherVersion.TryParse(pointer.CurrentVersion, out _))
        {
            return "Client pointer currentVersion is invalid.";
        }

        if (pointer.PreviousVersion is not null
            && (!LauncherVersion.TryParse(pointer.PreviousVersion, out _)
                || string.Equals(
                    pointer.PreviousVersion,
                    pointer.CurrentVersion,
                    StringComparison.Ordinal)))
        {
            return "Client pointer previousVersion is invalid.";
        }

        return null;
    }

    private static string? ValidateRecord(
        ClientVersionRecord record,
        LauncherVersion version,
        string rid)
    {
        if (record.SchemaVersion != ClientVersionRecord.CurrentSchemaVersion)
        {
            return $"Client install schema version {record.SchemaVersion} is not supported.";
        }

        if (!string.Equals(record.Version, version.Value, StringComparison.Ordinal)
            || !LauncherVersion.TryParse(record.Version, out _))
        {
            return "Client install version does not match its directory.";
        }

        if (!string.Equals(record.Rid, rid, StringComparison.Ordinal)
            || !LauncherRuntimeIdentity.IsValidRid(record.Rid))
        {
            return $"Client install RID does not match '{rid}'.";
        }

        if (!ReleaseManifestClient.IsSha256(record.ArchiveSha256)
            || record.ArchiveSize <= 0
            || record.ArchiveSize > ReleaseManifestClient.MaximumArtifactBytes)
        {
            return "Client install archive metadata is invalid.";
        }

        if (record.Files is null || record.Files.Count == 0)
        {
            return "Client install file list is empty.";
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? prior = null;
        foreach (InstalledFileRecord file in record.Files)
        {
            if (!IsNormalizedRelative(file.Path)
                || !paths.Add(file.Path)
                || !ReleaseManifestClient.IsSha256(file.Sha256)
                || file.Size < 0
                || file.UnixMode is < 0 or > 0x1FF
                || (prior is not null
                    && string.Compare(prior, file.Path, StringComparison.Ordinal) >= 0))
            {
                return "Client install file metadata is invalid, duplicated, or unsorted.";
            }

            prior = file.Path;
        }

        string suffix = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        foreach (string required in new[]
                 {
                     "AcDream.App" + suffix,
                     "acdream-headless" + suffix,
                 })
        {
            if (!paths.Contains(required))
            {
                return $"Client install is missing '{required}'.";
            }
        }

        return null;
    }

    private static async Task<T?> ReadStrictAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return ParseStrict<T>(bytes);
    }

    internal static T? ParseStrict<T>(
        ReadOnlySpan<byte> bytes,
        JsonSerializerOptions? serializerOptions = null)
    {
        using JsonDocument document = JsonDocument.Parse(
            bytes.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        RejectDuplicateProperties(document.RootElement, "$" );
        return document.RootElement.Deserialize<T>(
            serializerOptions ?? SerializerOptions);
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new LauncherUpdateException(
                        $"Duplicate JSON property '{path}.{property.Name}' is not allowed.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private void CleanupOwnedResidue()
    {
        foreach (string path in Directory.EnumerateDirectories(
                     AppDirectory,
                     ".client-staging-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (HasCanonicalGuidName(
                    Path.GetFileName(path),
                    ".client-staging-",
                    string.Empty))
            {
                SafeZipExtractor.TryDeleteDirectory(path);
            }
        }

        foreach (string path in Directory.EnumerateDirectories(
                     AppDirectory,
                     ".client-corrupt-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (HasCanonicalGuidName(
                    Path.GetFileName(path),
                    ".client-corrupt-",
                    string.Empty))
            {
                SafeZipExtractor.TryDeleteDirectory(path);
            }
        }

        foreach (string path in Directory.EnumerateFiles(
                     AppDirectory,
                     ".client-download-*.zip",
                     SearchOption.TopDirectoryOnly))
        {
            if (HasCanonicalGuidName(
                    Path.GetFileName(path),
                    ".client-download-",
                    ".zip"))
            {
                VerifiedArtifactDownloader.TryDelete(path);
            }
        }

        foreach (string path in Directory.EnumerateFiles(
                     AppDirectory,
                     ".current*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            string[] parts = fileName.Split('.');
            if (parts.Length >= 4
                && string.Equals(parts[^1], "tmp", StringComparison.Ordinal)
                && Guid.TryParseExact(parts[^2], "N", out Guid parsed)
                && string.Equals(
                    parsed.ToString("N"),
                    parts[^2],
                    StringComparison.Ordinal))
            {
                VerifiedArtifactDownloader.TryDelete(path);
            }
        }
    }

    private static bool HasCanonicalGuidName(
        string fileName,
        string prefix,
        string suffix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || fileName.Length != prefix.Length + 32 + suffix.Length)
        {
            return false;
        }

        string value = fileName.Substring(prefix.Length, 32);
        return Guid.TryParseExact(value, "N", out Guid parsed)
            && string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);
    }

    private void RequireOwnedStagingPath(string path)
    {
        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        string fileName = Path.GetFileName(path);
        if (!PathsEqual(parent, AppDirectory)
            || !HasCanonicalGuidName(
                fileName,
                ".client-staging-",
                string.Empty))
        {
            throw new LauncherUpdateException(
                "The client extraction path is not an owned LA10 staging directory.");
        }
    }

    internal static void RejectReparseTree(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LauncherUpdateException("The client version directory is a reparse point.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new LauncherUpdateException(
                        $"Client install path '{NormalizeRelative(root, path)}' is a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
            }
        }
    }

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    internal static bool IsNormalizedRelative(string? path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length > 512
            || path.IndexOf('\0') >= 0
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            return false;
        }

        string[] parts = path.Split('/');
        return parts.All(part =>
            part.Length > 0
            && part is not ("." or "..")
            && !part.EndsWith(' ')
            && !part.EndsWith('.')
            && !part.Any(character =>
                char.IsControl(character)
                || character is '<' or '>' or '"' or '|' or '?' or '*')
            && !PortablePathRules.IsWindowsDeviceName(part));
    }

    internal static string ResolveContained(string root, string relative)
    {
        if (!IsNormalizedRelative(relative))
        {
            throw new LauncherUpdateException($"Unsafe relative path '{relative}'.");
        }

        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(
            Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new LauncherUpdateException($"Path '{relative}' escaped its root.");
        }

        return path;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void RequireRid(string rid)
    {
        if (!LauncherRuntimeIdentity.IsValidRid(rid))
        {
            throw new ArgumentException("RID is invalid.", nameof(rid));
        }
    }

    private void SetCached(ClientVersionResolution resolution)
    {
        lock (_gate)
        {
            _cached = resolution;
        }
    }

    private static ClientVersionResolution Invalid(string status) =>
        new(ClientVersionState.Invalid, status, null, null, null, null);

    private sealed record PointerRead(ClientActivationPointer? Pointer, string? Error);
}
