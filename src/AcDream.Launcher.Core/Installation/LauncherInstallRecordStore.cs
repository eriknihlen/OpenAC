using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core.Integrity;
using AcDream.Launcher.Core.Launching;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Installation;

public enum InstallRecordVerificationState
{
    Missing,
    Verified,
    ContentUpdateRequired,
    Invalid,
}

public sealed record InstallRecordVerification(
    InstallRecordVerificationState State,
    LauncherInstallRecord? Record,
    string Status,
    ContentMigrationPlan? RequiredContentWork = null)
{
    public bool IsVerified => State == InstallRecordVerificationState.Verified;

    public bool RequiresContentUpdate =>
        State == InstallRecordVerificationState.ContentUpdateRequired;
}

public sealed class LauncherInstallRecordStore
{
    public const uint CurrentBakeToolVersion = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ApplicationPathSet _paths;
    private readonly DatDirectoryLocator _datDirectories;
    private readonly Func<string, CancellationToken, Task<string>> _computeSha256;
    private readonly PreparedAssetVerificationCache _verificationCache;

    public LauncherInstallRecordStore(
        ApplicationPathSet paths,
        DatDirectoryLocator? datDirectories = null,
        Func<string, CancellationToken, Task<string>>? computeSha256 = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _datDirectories = datDirectories ?? new DatDirectoryLocator();
        _computeSha256 = computeSha256
            ?? ((path, cancellationToken) =>
                FileIntegrity.ComputeSha256HexAsync(path, cancellationToken));
        _verificationCache = new PreparedAssetVerificationCache(DataDirectory);
    }

    public string DataDirectory => Path.GetFullPath(_paths.DataDirectory);

    public string RecordPath => Path.Combine(DataDirectory, "install.json");

    public string PreparedAssetPath => Path.Combine(
        DataDirectory,
        "pak",
        "acdream.pak");

    public static string GetBackupPath(string preparedAssetPath) =>
        preparedAssetPath + ".previous-install";

    /// <param name="forceFullVerification">Hash the package even when a
    /// previous full hash of the same bytes is remembered. Install, update,
    /// and any explicit "verify my files" request pass true; ordinary startup
    /// passes false.</param>
    public async Task<InstallRecordVerification> LoadAndVerifyAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false,
        IProgress<string>? progress = null)
    {
        await using InstallerTransactionLease lease =
            await InstallerTransactionLease.AcquireAsync(
                    DataDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        return await LoadAndVerifyUnderLeaseAsync(
                cancellationToken,
                forceFullVerification,
                progress)
            .ConfigureAwait(false);
    }

    internal async Task<InstallRecordVerification> LoadAndVerifyUnderLeaseAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false,
        IProgress<string>? progress = null)
    {
        if (!File.Exists(RecordPath))
        {
            return new InstallRecordVerification(
                InstallRecordVerificationState.Missing,
                null,
                "Client content is not installed. Complete the first-run setup.");
        }

        LauncherInstallRecord? record;
        try
        {
            await using FileStream stream = new(
                RecordPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            using JsonDocument document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement version)
                || !version.TryGetInt32(out _))
            {
                return Invalid(
                    "The install record must contain an explicit integer version.");
            }

            record = root.Deserialize<LauncherInstallRecord>(SerializerOptions);
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
            return Invalid($"The install record could not be read: {ex.Message}");
        }

        if (record is null)
        {
            return Invalid("The install record is empty.");
        }

        string? contractError = ValidateRecordContract(
            record,
            requireCanonicalSerializedPaths: true,
            requireCurrentRecipe: false);
        if (contractError is not null)
        {
            return Invalid(contractError);
        }

        if (record.BakeToolVersion > CurrentBakeToolVersion)
        {
            return Invalid(
                $"Prepared content recipe {record.BakeToolVersion} is newer than "
                + $"this launcher's recipe {CurrentBakeToolVersion}. Update the launcher.");
        }

        if (record.BakeToolVersion < CurrentBakeToolVersion)
        {
            if (!File.Exists(record.PreparedAssetPath))
            {
                return Invalid("The prepared package is missing.");
            }

            if (new FileInfo(record.PreparedAssetPath).Length
                != record.PreparedAssetSize)
            {
                return Invalid("The prepared package size changed.");
            }

            ContentMigrationPlan plan;
            try
            {
                plan = ContentMigrationCatalog.Resolve(
                    record.BakeToolVersion,
                    CurrentBakeToolVersion);
            }
            catch (InvalidOperationException ex)
            {
                return Invalid(ex.Message);
            }

            return new InstallRecordVerification(
                InstallRecordVerificationState.ContentUpdateRequired,
                record,
                $"World data update required: {plan.Reason}.",
                plan);
        }

        string backupPath = GetBackupPath(record.PreparedAssetPath);
        FileVerification current = await VerifyFileAsync(
                record.PreparedAssetPath,
                record,
                cancellationToken,
                allowCachedResult: !forceFullVerification,
                progress)
            .ConfigureAwait(false);
        if (current.IsValid)
        {
            TryDelete(backupPath);
            return Verified(record);
        }

        FileVerification backup = await VerifyFileAsync(
                backupPath,
                record,
                cancellationToken,
                allowCachedResult: false,
                progress)
            .ConfigureAwait(false);
        if (backup.IsValid)
        {
            try
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(record.PreparedAssetPath)
                    ?? throw new InvalidOperationException(
                        "The prepared asset path has no parent directory."));
                File.Move(backupPath, record.PreparedAssetPath, overwrite: true);
                return Verified(record, "Recovered and verified the previous client content.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Invalid(
                    $"The previous verified package could not be restored: {ex.Message}");
            }
        }

        return Invalid(current.Status);
    }

    public async Task SaveAtomicallyAsync(
        LauncherInstallRecord record,
        CancellationToken cancellationToken = default)
    {
        await using InstallerTransactionLease lease =
            await InstallerTransactionLease.AcquireAsync(
                    DataDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        await SaveAtomicallyUnderLeaseAsync(record, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task SaveAtomicallyUnderLeaseAsync(
        LauncherInstallRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        LauncherInstallRecord normalized = NormalizeForSave(record);
        string? contractError = ValidateRecordContract(
            normalized,
            requireCanonicalSerializedPaths: true,
            requireCurrentRecipe: true);
        if (contractError is not null)
        {
            throw new InvalidDataException(contractError);
        }

        string directory = Path.GetDirectoryName(RecordPath)
            ?? throw new InvalidOperationException(
                "The install record has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(RecordPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        normalized,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, RecordPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal void RememberVerifiedPackage(LauncherInstallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            var file = new FileInfo(record.PreparedAssetPath);
            if (file.Exists && file.Length == record.PreparedAssetSize)
            {
                _verificationCache.Write(
                    record.PreparedAssetPath,
                    file.Length,
                    file.LastWriteTimeUtc,
                    record.PreparedAssetSha256);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
        }
    }

    private string? ValidateRecordContract(
        LauncherInstallRecord record,
        bool requireCanonicalSerializedPaths,
        bool requireCurrentRecipe)
    {
        if (record.Version != LauncherInstallRecord.CurrentRecordVersion)
        {
            return $"Install record version {record.Version} is not supported.";
        }

        if (!record.HasIntegrityMetadata)
        {
            return "The install record is missing SHA-256, size, or bake-tool metadata.";
        }

        if (requireCurrentRecipe
            && record.BakeToolVersion != CurrentBakeToolVersion)
        {
            return $"Bake tool version {record.BakeToolVersion} is not supported; "
                + $"version {CurrentBakeToolVersion} is required.";
        }

        if (!IsSha256(record.PreparedAssetSha256))
        {
            return "The install record contains an invalid SHA-256 digest.";
        }

        if (string.IsNullOrWhiteSpace(record.PreparedAssetPath))
        {
            return "The prepared asset path is missing.";
        }

        if (string.IsNullOrWhiteSpace(record.DatDirectory))
        {
            return "The DAT directory path is missing.";
        }

        string canonicalPreparedPath = Path.GetFullPath(PreparedAssetPath);
        if (requireCanonicalSerializedPaths
            && !Path.IsPathFullyQualified(record.PreparedAssetPath))
        {
            return "The prepared asset path must be absolute.";
        }

        string recordedPreparedPath;
        try
        {
            recordedPreparedPath = Path.GetFullPath(record.PreparedAssetPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return $"The prepared asset path is invalid: {ex.Message}";
        }

        if (!PathsEqual(recordedPreparedPath, canonicalPreparedPath))
        {
            return "The install record does not point to the launcher's canonical "
                + "DataDirectory/pak/acdream.pak path.";
        }

        if (requireCanonicalSerializedPaths
            && !CanonicalSpellingEquals(
                record.PreparedAssetPath,
                recordedPreparedPath,
                trimEndingSeparator: false))
        {
            return "The prepared asset path is not canonical.";
        }

        if (requireCanonicalSerializedPaths
            && !Path.IsPathFullyQualified(record.DatDirectory))
        {
            return "The DAT directory path must be absolute.";
        }

        DatDirectoryValidation datValidation =
            _datDirectories.Validate(record.DatDirectory);
        if (!datValidation.IsValid)
        {
            return datValidation.Message
                + FormatMissing(datValidation.MissingFileNames);
        }

        return requireCanonicalSerializedPaths
               && !CanonicalSpellingEquals(
                   record.DatDirectory,
                   datValidation.Directory,
                   trimEndingSeparator: true)
            ? "The DAT directory path is not canonical."
            : null;
    }

    private LauncherInstallRecord NormalizeForSave(LauncherInstallRecord record)
    {
        if (record.Version != LauncherInstallRecord.CurrentRecordVersion)
        {
            throw new InvalidDataException(
                $"Install record version {record.Version} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(record.PreparedAssetPath))
        {
            throw new InvalidDataException("The prepared asset path is missing.");
        }

        DatDirectoryValidation datValidation =
            _datDirectories.Validate(record.DatDirectory);
        if (!datValidation.IsValid)
        {
            throw new InvalidDataException(
                datValidation.Message
                + FormatMissing(datValidation.MissingFileNames));
        }

        string recordedPreparedPath;
        try
        {
            recordedPreparedPath = Path.GetFullPath(record.PreparedAssetPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            throw new InvalidDataException(
                $"The prepared asset path is invalid: {ex.Message}",
                ex);
        }

        if (!PathsEqual(recordedPreparedPath, PreparedAssetPath))
        {
            throw new InvalidDataException(
                "The install record does not point to the launcher's canonical "
                + "DataDirectory/pak/acdream.pak path.");
        }

        return record with
        {
            Version = LauncherInstallRecord.CurrentRecordVersion,
            DatDirectory = datValidation.Directory,
            PreparedAssetPath = Path.GetFullPath(PreparedAssetPath),
        };
    }

    private async Task<FileVerification> VerifyFileAsync(
        string path,
        LauncherInstallRecord record,
        CancellationToken cancellationToken,
        bool allowCachedResult,
        IProgress<string>? progress)
    {
        if (!File.Exists(path))
        {
            return new FileVerification(false, "The prepared package is missing.");
        }

        try
        {
            var file = new FileInfo(path);
            long length = file.Length;
            if (length != record.PreparedAssetSize)
            {
                return new FileVerification(
                    false,
                    $"The prepared package size changed (expected "
                        + $"{record.PreparedAssetSize}, found {length}).");
            }

            DateTime lastWriteUtc = file.LastWriteTimeUtc;
            if (allowCachedResult
                && PreparedAssetVerificationCache.Satisfies(
                    _verificationCache.TryRead(),
                    path,
                    length,
                    lastWriteUtc,
                    record.PreparedAssetSha256))
            {
                return new FileVerification(true, "Client content verified.");
            }

            progress?.Report(
                allowCachedResult
                    ? "The verification cache is missing or changed. Reading the "
                        + "whole world-data pak once; this can take around 30 seconds."
                    : "Reading the whole world-data pak for explicit verification; "
                        + "this can take around 30 seconds.");
            string sha256 = await _computeSha256(path, cancellationToken)
                .ConfigureAwait(false);
            if (!FileIntegrity.Matches(sha256, record.PreparedAssetSha256))
            {
                _verificationCache.Invalidate();
                return new FileVerification(
                    false,
                    "The prepared package SHA-256 does not match the install record.");
            }

            DateTime hashedWriteUtc = new FileInfo(path).LastWriteTimeUtc;
            if (hashedWriteUtc == lastWriteUtc)
            {
                _verificationCache.Write(path, length, lastWriteUtc, sha256);
            }

            return new FileVerification(true, "Client content verified.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileVerification(
                false,
                $"The prepared package could not be verified: {ex.Message}");
        }
    }

    private static InstallRecordVerification Verified(
        LauncherInstallRecord record,
        string status = "Client content SHA-256, size, and bake-tool version verified.") =>
        new(InstallRecordVerificationState.Verified, record, status);

    private static InstallRecordVerification Invalid(string status) =>
        new(InstallRecordVerificationState.Invalid, null, status);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string FormatMissing(IReadOnlyList<string> missing) =>
        missing.Count == 0
            ? string.Empty
            : " Missing: " + string.Join(", ", missing) + ".";

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool CanonicalSpellingEquals(
        string serialized,
        string canonical,
        bool trimEndingSeparator)
    {
        string candidate = trimEndingSeparator
            ? Path.TrimEndingDirectorySeparator(serialized)
            : serialized;
        return string.Equals(
            candidate,
            canonical,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record FileVerification(bool IsValid, string Status);
}
