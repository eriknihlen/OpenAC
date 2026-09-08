using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Installation;

public sealed record LauncherContentOverlay(
    string Path,
    string Sha256,
    long Size,
    uint RecipeVersion);

public sealed record LauncherContentState(
    int SchemaVersion,
    string BaseSha256,
    uint EffectiveRecipeVersion,
    LauncherContentOverlay? Overlay)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Optional overlay authority kept beside, rather than inside, install.json.
/// Old launchers safely ignore this file instead of rejecting a new field in
/// their strict install-record schema.
/// </summary>
public sealed class LauncherContentStateStore
{
    private const uint PakMagic = 0x4B504341u;
    private const uint PakFormatVersion = 2;
    private const int PakHeaderSize = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _pakDirectory;
    private readonly Func<string, CancellationToken, Task<string>> _computeSha256;

    public LauncherContentStateStore(
        ApplicationPathSet paths,
        Func<string, CancellationToken, Task<string>>? computeSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _pakDirectory = Path.Combine(
            Path.GetFullPath(paths.DataDirectory),
            "pak");
        _computeSha256 = computeSha256
            ?? ((path, cancellationToken) =>
                FileIntegrity.ComputeSha256HexAsync(path, cancellationToken));
    }

    public string StatePath => Path.Combine(_pakDirectory, "content.current.json");

    public string ClientCompatibilityPendingPath => Path.Combine(
        _pakDirectory,
        "content.client-pending");

    public string OverlayCandidatePath => Path.Combine(
        _pakDirectory,
        ".acdream-update.candidate.pak");

    public bool IsClientCompatibilityPending =>
        File.Exists(ClientCompatibilityPendingPath);

    public void MarkClientCompatibilityPending()
    {
        Directory.CreateDirectory(_pakDirectory);
        string temporaryPath = ClientCompatibilityPendingPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            LauncherInstallRecordStore.CurrentBakeToolVersion.ToString(
                CultureInfo.InvariantCulture));
        File.Move(
            temporaryPath,
            ClientCompatibilityPendingPath,
            overwrite: true);
    }

    public void ClearClientCompatibilityPending()
    {
        LauncherInstallRecordStore.TryDelete(ClientCompatibilityPendingPath);
        LauncherInstallRecordStore.TryDelete(
            ClientCompatibilityPendingPath + ".tmp");
    }

    public string GetOverlayPath(LauncherContentOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        string? error = ValidateOverlayFileName(overlay.Path);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }

        return Path.Combine(_pakDirectory, overlay.Path);
    }

    public async Task<(LauncherContentState? State, string? Error)> LoadAsync(
        LauncherInstallRecord baseRecord,
        bool forceFullVerification = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseRecord);
        if (!File.Exists(StatePath))
        {
            return (null, null);
        }

        LauncherContentState? state;
        try
        {
            await using FileStream stream = new(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            state = await JsonSerializer.DeserializeAsync<LauncherContentState>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException)
        {
            return (null, $"The prepared-content update record could not be read: {ex.Message}");
        }

        string? contractError = ValidateContract(baseRecord, state);
        if (contractError is not null)
        {
            return (null, contractError);
        }

        LauncherContentOverlay overlay = state!.Overlay!;
        string overlayPath = GetOverlayPath(overlay);
        if (!File.Exists(baseRecord.PreparedAssetPath))
        {
            return (null, "The base prepared package is missing.");
        }

        if (new FileInfo(baseRecord.PreparedAssetPath).Length
            != baseRecord.PreparedAssetSize)
        {
            return (null, "The base prepared package size changed.");
        }

        if (!File.Exists(overlayPath))
        {
            return (null, "The prepared-content overlay is missing.");
        }

        if (new FileInfo(overlayPath).Length != overlay.Size)
        {
            return (null, "The prepared-content overlay size changed.");
        }

        try
        {
            PakIdentity baseIdentity = ReadPakIdentity(baseRecord.PreparedAssetPath);
            PakIdentity overlayIdentity = ReadPakIdentity(overlayPath);
            if (baseIdentity.FormatVersion != PakFormatVersion
                || overlayIdentity.FormatVersion != PakFormatVersion)
            {
                return (null, "The base or overlay pak format is not supported.");
            }

            if (baseIdentity.RecipeVersion != baseRecord.BakeToolVersion
                || overlayIdentity.RecipeVersion != overlay.RecipeVersion)
            {
                return (null, "The base or overlay content recipe does not match its record.");
            }

            if (!baseIdentity.SameDatSet(overlayIdentity))
            {
                return (null, "The prepared-content overlay was built from a different DAT set.");
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            return (null, $"The prepared-content package header is invalid: {ex.Message}");
        }

        if (forceFullVerification)
        {
            string baseSha = await _computeSha256(
                    baseRecord.PreparedAssetPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!FileIntegrity.Matches(baseSha, baseRecord.PreparedAssetSha256))
            {
                return (null, "The base prepared package SHA-256 does not match its record.");
            }

            string overlaySha = await _computeSha256(overlayPath, cancellationToken)
                .ConfigureAwait(false);
            if (!FileIntegrity.Matches(overlaySha, overlay.Sha256))
            {
                return (null, "The prepared-content overlay SHA-256 does not match its record.");
            }
        }

        return (state, null);
    }

    public async Task SaveAtomicallyAsync(
        LauncherInstallRecord baseRecord,
        LauncherContentState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseRecord);
        ArgumentNullException.ThrowIfNull(state);
        string? error = ValidateContract(baseRecord, state);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }

        string overlayPath = GetOverlayPath(state.Overlay!);
        string? candidateError = ValidateCandidate(
            baseRecord,
            overlayPath,
            state.Overlay!);
        if (candidateError is not null)
        {
            throw new InvalidDataException(candidateError);
        }

        Directory.CreateDirectory(_pakDirectory);
        string temporaryPath = StatePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        state,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            LauncherInstallRecordStore.TryDelete(temporaryPath);
        }
    }

    public void Delete() => LauncherInstallRecordStore.TryDelete(StatePath);

    public string? ValidateCandidate(
        LauncherInstallRecord baseRecord,
        string overlayPath,
        LauncherContentOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(baseRecord);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);
        ArgumentNullException.ThrowIfNull(overlay);
        if (!File.Exists(baseRecord.PreparedAssetPath))
        {
            return "The base prepared package is missing.";
        }

        if (!File.Exists(overlayPath)
            || new FileInfo(overlayPath).Length != overlay.Size)
        {
            return "The prepared-content overlay candidate size changed.";
        }

        try
        {
            PakIdentity baseIdentity = ReadPakIdentity(baseRecord.PreparedAssetPath);
            PakIdentity overlayIdentity = ReadPakIdentity(overlayPath);
            if (baseIdentity.FormatVersion != PakFormatVersion
                || overlayIdentity.FormatVersion != PakFormatVersion
                || baseIdentity.RecipeVersion != baseRecord.BakeToolVersion
                || overlayIdentity.RecipeVersion != overlay.RecipeVersion
                || !baseIdentity.SameDatSet(overlayIdentity))
            {
                return "The prepared-content overlay candidate header does not "
                    + "match the base pak and requested recipe.";
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            return $"The prepared-content overlay candidate is invalid: {ex.Message}";
        }

        return null;
    }

    private static string? ValidateContract(
        LauncherInstallRecord baseRecord,
        LauncherContentState? state)
    {
        if (state is null)
        {
            return "The prepared-content update record is empty.";
        }

        if (state.SchemaVersion != LauncherContentState.CurrentSchemaVersion)
        {
            return $"Prepared-content record version {state.SchemaVersion} is not supported.";
        }

        if (!IsSha256(state.BaseSha256)
            || !FileIntegrity.Matches(state.BaseSha256, baseRecord.PreparedAssetSha256))
        {
            return "The prepared-content update record does not match the installed base pak.";
        }

        if (state.Overlay is null)
        {
            return "The prepared-content update record is missing its overlay.";
        }

        if (state.EffectiveRecipeVersion != state.Overlay.RecipeVersion
            || state.EffectiveRecipeVersion <= baseRecord.BakeToolVersion)
        {
            return "The prepared-content overlay recipe is not a newer effective recipe.";
        }

        if (!IsSha256(state.Overlay.Sha256) || state.Overlay.Size <= 0)
        {
            return "The prepared-content overlay is missing valid integrity metadata.";
        }

        return ValidateOverlayFileName(state.Overlay.Path);
    }

    private static string? ValidateOverlayFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathFullyQualified(path)
            || path.Contains('/')
            || path.Contains('\\')
            || !string.Equals(path, Path.GetFileName(path), StringComparison.Ordinal)
            || path is "." or ".."
            || !path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            return "The prepared-content overlay path must be one pak filename beneath the pak directory.";
        }

        return null;
    }

    private static PakIdentity ReadPakIdentity(string path)
    {
        Span<byte> header = stackalloc byte[PakHeaderSize];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.ReadExactly(header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]);
        if (magic != PakMagic)
        {
            throw new InvalidDataException("pak magic does not match ACPK");
        }

        return new PakIdentity(
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]));
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private readonly record struct PakIdentity(
        uint FormatVersion,
        uint PortalIteration,
        uint CellIteration,
        uint HighResIteration,
        uint LanguageIteration,
        uint RecipeVersion)
    {
        public bool SameDatSet(PakIdentity other) =>
            PortalIteration == other.PortalIteration
            && CellIteration == other.CellIteration
            && HighResIteration == other.HighResIteration
            && LanguageIteration == other.LanguageIteration;
    }
}
