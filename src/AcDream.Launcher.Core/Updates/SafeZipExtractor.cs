using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AcDream.Launcher.Core.Updates;

public sealed record SafeZipExtractionLimits(
    int MaximumEntries = 20_000,
    long MaximumEntryBytes = 2L * 1024 * 1024 * 1024,
    long MaximumTotalBytes = 8L * 1024 * 1024 * 1024,
    double MaximumCompressionRatio = 200,
    int MaximumRelativePathLength = 512);

public sealed record ExtractedFileRecord(
    string Path,
    string Sha256,
    long Size,
    int UnixMode);

public sealed class SafeZipExtractor
{
    private const int BufferSize = 128 * 1024;
    private const int UnixTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixPermissionMask = 0x1FF;
    private readonly SafeZipExtractionLimits _limits;

    public SafeZipExtractor(SafeZipExtractionLimits? limits = null)
    {
        _limits = limits ?? new SafeZipExtractionLimits();
        if (_limits.MaximumEntries <= 0
            || _limits.MaximumEntryBytes <= 0
            || _limits.MaximumTotalBytes <= 0
            || _limits.MaximumCompressionRatio <= 0
            || _limits.MaximumRelativePathLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "ZIP extraction limits must all be positive.");
        }
    }

    public async Task<IReadOnlyList<ExtractedFileRecord>> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string archive = Path.GetFullPath(archivePath);
        string destination = Path.GetFullPath(destinationDirectory);

        if (Directory.Exists(destination)
            && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new LauncherUpdateException(
                "The ZIP extraction destination must be empty.");
        }

        try
        {
            await using var stream = new FileStream(
                archive,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            IReadOnlyList<ValidatedEntry> entries = ValidateArchive(zip);

            Directory.CreateDirectory(destination);
            RejectReparsePoint(destination, "extraction root");
            foreach (string directory in entries
                         .SelectMany(entry => ParentPaths(entry.RelativePath))
                         .Concat(entries.Where(entry => entry.IsDirectory)
                             .Select(entry => entry.RelativePath))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path.Count(character => character == '/'))
                         .ThenBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directoryPath = ResolveContained(destination, directory);
                Directory.CreateDirectory(directoryPath);
                RejectReparsePoint(directoryPath, $"directory '{directory}'");
            }

            var files = new List<ExtractedFileRecord>();
            long actualTotal = 0;
            foreach (ValidatedEntry entry in entries.Where(entry => !entry.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string outputPath = ResolveContained(destination, entry.RelativePath);
                EnsureParentsAreDirectories(destination, entry.RelativePath);
                await using Stream input = entry.Entry.Open();
                await using var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous
                    | FileOptions.SequentialScan
                    | FileOptions.WriteThrough);
                using IncrementalHash hash = IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long actualEntry = 0;
                try
                {
                    while (true)
                    {
                        int read = await input.ReadAsync(
                                buffer.AsMemory(0, BufferSize),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        actualEntry = checked(actualEntry + read);
                        actualTotal = checked(actualTotal + read);
                        if (actualEntry > entry.Entry.Length
                            || actualEntry > _limits.MaximumEntryBytes
                            || actualTotal > _limits.MaximumTotalBytes)
                        {
                            throw new LauncherUpdateException(
                                $"ZIP entry '{entry.RelativePath}' exceeded its declared limits.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                if (actualEntry != entry.Entry.Length)
                {
                    throw new LauncherUpdateException(
                        $"ZIP entry '{entry.RelativePath}' length changed while extracting.");
                }

                int unixMode = entry.UnixMode & UnixPermissionMask;
                if (OperatingSystem.IsLinux() && unixMode != 0)
                {
                    File.SetUnixFileMode(outputPath, (UnixFileMode)unixMode);
                }

                files.Add(new ExtractedFileRecord(
                    entry.RelativePath,
                    Convert.ToHexStringLower(hash.GetHashAndReset()),
                    actualEntry,
                    unixMode));
            }

            files.Sort((left, right) => string.Compare(
                left.Path,
                right.Path,
                StringComparison.Ordinal));
            return files;
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(destination);
            throw;
        }
        catch (LauncherUpdateException)
        {
            TryDeleteDirectory(destination);
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or NotSupportedException
                                   or CryptographicException)
        {
            TryDeleteDirectory(destination);
            throw new LauncherUpdateException(
                $"The release ZIP could not be extracted safely: {ex.Message}",
                ex);
        }
    }

    private IReadOnlyList<ValidatedEntry> ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count == 0 || zip.Entries.Count > _limits.MaximumEntries)
        {
            throw new LauncherUpdateException(
                $"ZIP entry count {zip.Entries.Count} is outside the allowed range.");
        }

        var result = new List<ValidatedEntry>(zip.Entries.Count);
        var explicitEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, PathNode>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        long totalCompressed = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string relative = NormalizeEntryPath(entry.FullName);
            if (!explicitEntries.Add(relative))
            {
                throw new LauncherUpdateException(
                    $"ZIP contains a duplicate/case-colliding entry '{relative}'.");
            }

            int unixAttributes = entry.ExternalAttributes >> 16;
            int unixType = unixAttributes & UnixTypeMask;
            bool trailingDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
            bool isDirectory = trailingDirectory || unixType == UnixDirectory;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || unixType is not (0 or UnixRegularFile or UnixDirectory)
                || (unixType == UnixDirectory && !trailingDirectory)
                || (isDirectory && (entry.Length != 0 || entry.CompressedLength != 0)))
            {
                throw new LauncherUpdateException(
                    $"ZIP entry '{relative}' is a symlink, reparse point, or unsupported type.");
            }

            AddPathNodes(nodes, relative, isDirectory);
            if (!isDirectory)
            {
                if (entry.Length < 0
                    || entry.CompressedLength < 0
                    || entry.Length > _limits.MaximumEntryBytes)
                {
                    throw new LauncherUpdateException(
                        $"ZIP entry '{relative}' exceeds the per-file limit.");
                }

                totalLength = checked(totalLength + entry.Length);
                totalCompressed = checked(totalCompressed + entry.CompressedLength);
                if (totalLength > _limits.MaximumTotalBytes
                    || IsRatioExceeded(entry.Length, entry.CompressedLength))
                {
                    throw new LauncherUpdateException(
                        $"ZIP entry '{relative}' exceeds extraction size/ratio limits.");
                }
            }

            result.Add(new ValidatedEntry(entry, relative, isDirectory, unixAttributes));
        }

        if (totalLength > 0
            && (totalCompressed == 0 || IsRatioExceeded(totalLength, totalCompressed)))
        {
            throw new LauncherUpdateException(
                "ZIP aggregate compression ratio exceeds the allowed limit.");
        }

        return result;
    }

    private string NormalizeEntryPath(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.IndexOf('\0') >= 0
            || name.Contains(':', StringComparison.Ordinal))
        {
            throw new LauncherUpdateException("ZIP contains an empty, NUL, or ADS path.");
        }

        string normalized = name.Replace('\\', '/');
        bool directory = normalized.EndsWith("/", StringComparison.Ordinal);
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0
            || normalized.Length > _limits.MaximumRelativePathLength
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized))
        {
            throw new LauncherUpdateException($"ZIP path '{name}' is rooted or too long.");
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Any(character =>
                    char.IsControl(character)
                    || character is '<' or '>' or '"' or '|' or '?' or '*')
                || PortablePathRules.IsWindowsDeviceName(segment))
            {
                throw new LauncherUpdateException(
                    $"ZIP path '{name}' contains an unsafe segment.");
            }
        }

        return string.Join('/', segments) + (directory ? "/" : string.Empty);
    }

    private static void AddPathNodes(
        Dictionary<string, PathNode> nodes,
        string relative,
        bool isDirectory)
    {
        string path = relative.TrimEnd('/');
        string[] segments = path.Split('/');
        string current = string.Empty;
        for (int index = 0; index < segments.Length; index++)
        {
            current = current.Length == 0
                ? segments[index]
                : current + "/" + segments[index];
            bool nodeIsDirectory = index < segments.Length - 1 || isDirectory;
            if (nodes.TryGetValue(current, out PathNode? existing))
            {
                if (!string.Equals(existing.Spelling, current, StringComparison.Ordinal)
                    || (!existing.IsDirectory || !nodeIsDirectory))
                {
                    throw new LauncherUpdateException(
                        $"ZIP path '{relative}' collides with '{existing.Spelling}'.");
                }

                continue;
            }

            nodes.Add(current, new PathNode(current, nodeIsDirectory));
        }
    }

    private bool IsRatioExceeded(long expanded, long compressed) =>
        expanded > 0
        && (compressed <= 0 || expanded / (double)compressed > _limits.MaximumCompressionRatio);

    private static IEnumerable<string> ParentPaths(string relative)
    {
        string path = relative.TrimEnd('/');
        int slash = path.IndexOf('/');
        while (slash >= 0)
        {
            yield return path[..slash];
            slash = path.IndexOf('/', slash + 1);
        }
    }

    private static string ResolveContained(string root, string relative)
    {
        string path = Path.GetFullPath(
            Path.Combine(root, relative.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new LauncherUpdateException(
                $"ZIP path '{relative}' escaped the extraction directory.");
        }

        return path;
    }

    private static void EnsureParentsAreDirectories(string root, string relative)
    {
        foreach (string parent in ParentPaths(relative))
        {
            string path = ResolveContained(root, parent);
            if (!Directory.Exists(path))
            {
                throw new LauncherUpdateException(
                    $"ZIP parent '{parent}' is not a directory.");
            }

            RejectReparsePoint(path, $"directory '{parent}'");
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new LauncherUpdateException(
                $"The {description} is a reparse point.");
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                DeleteDirectoryWithoutFollowingReparsePoints(path);
            }
        }
        catch
        {
            // The exact random staging name is reclaimed under the update lease.
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string directory)
    {
        FileAttributes rootAttributes = File.GetAttributes(directory);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteReparsePoint(directory);
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                DeleteReparsePoint(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    private static void DeleteReparsePoint(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException)
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private sealed record PathNode(string Spelling, bool IsDirectory);

    private sealed record ValidatedEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory,
        int UnixMode);
}
