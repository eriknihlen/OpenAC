using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Checks a hand-unzipped plugin folder against every install rule that needs no GitHub
/// release (L-318): links and reparse points, regular files only, the extractor's path rules, the
/// extraction limits, the content policy, <see cref="LauncherPluginManifest.ValidateForInstall"/>,
/// and the icon rules.</summary>
public static class DirectInstallCheck
{
    private static readonly EnumerationOptions WalkOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    /// <summary>The first failing rule's player-facing reason, or null when the folder passes.</summary>
    public static string? Refusal(string directory, LauncherPluginManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(manifest);

        try
        {
            return Walk(directory, manifest);
        }
        catch (LauncherUpdateException ex)
        {
            return ex.Message;
        }
        catch (LauncherPluginManifestException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The plugin folder could not be read.";
        }
    }

    private static string? Walk(string directory, LauncherPluginManifest manifest)
    {
        if (IsReparsePoint(directory))
        {
            return "The plugin folder is a link, which the launcher will not follow.";
        }

        SafeZipExtractionLimits limits = PluginInstaller.ContractLimits.Extraction;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ExtractedFileRecord>();
        var pending = new Queue<string>();
        pending.Enqueue(string.Empty);
        int fileCount = 0;
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            string relativeDirectory = pending.Dequeue();
            string absoluteDirectory = relativeDirectory.Length == 0
                ? directory
                : Path.Combine(directory, relativeDirectory);

            foreach (FileSystemInfo entry in new DirectoryInfo(absoluteDirectory)
                         .EnumerateFileSystemInfos("*", WalkOptions))
            {
                if (IsIgnored(entry.Name))
                {
                    continue;
                }

                string relativePath = relativeDirectory.Length == 0
                    ? entry.Name
                    : $"{relativeDirectory}/{entry.Name}";

                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return $"'{relativePath}' is a link, which the launcher will not follow.";
                }

                string? nameIssue = ValidateSegment(entry.Name, relativePath);
                if (nameIssue is not null)
                {
                    return nameIssue;
                }

                if (relativePath.Length > limits.MaximumRelativePathLength)
                {
                    return $"'{relativePath}' is longer than "
                        + $"{limits.MaximumRelativePathLength} characters.";
                }

                if (!seen.Add(relativePath))
                {
                    return $"'{relativePath}' collides with another entry that differs only in case.";
                }

                if (!OperatingSystem.IsWindows() && !UnixEntryKind.IsRegularOrDirectory(entry.FullName))
                {
                    return $"'{relativePath}' is not a regular file or folder.";
                }

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(relativePath);
                    continue;
                }

                if (++fileCount > limits.MaximumEntries)
                {
                    return $"The plugin folder has more than {limits.MaximumEntries} files.";
                }

                long length = ((FileInfo)entry).Length;
                if (length > limits.MaximumEntryBytes)
                {
                    return $"'{relativePath}' is larger than "
                        + $"{limits.MaximumEntryBytes / (1024 * 1024)} MiB.";
                }

                totalBytes += length;
                if (totalBytes > limits.MaximumTotalBytes)
                {
                    return $"The plugin folder is larger than "
                        + $"{limits.MaximumTotalBytes / (1024 * 1024)} MiB.";
                }

                files.Add(new ExtractedFileRecord(relativePath, string.Empty, length, 0));
            }
        }

        PluginContentPolicy.Validate(files, manifest.EntryDll);
        manifest.ValidateForInstall();

        string iconPath = Path.Combine(directory, LauncherPluginIcon.FileName);
        if (seen.Contains(LauncherPluginIcon.FileName) && File.Exists(iconPath))
        {
            byte[] iconBytes = ReadAtMost(iconPath, LauncherPluginIcon.MaximumBytes + 1);
            LauncherPluginIcon.Validate(iconBytes);
        }

        return null;
    }

    private static bool IsIgnored(string name) =>
        string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("._", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateSegment(string segment, string relativePath)
    {
        if (segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || segment.Any(character =>
                char.IsControl(character)
                || character is '<' or '>' or '"' or '|' or '?' or '*' or ':')
            || PortablePathRules.IsWindowsDeviceName(segment))
        {
            return $"'{relativePath}' has a name the launcher will not accept.";
        }

        return null;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static byte[] ReadAtMost(string path, int maximumBytes)
    {
        using FileStream stream = File.OpenRead(path);
        var buffer = new byte[maximumBytes];
        int totalRead = 0;
        int read;
        while (totalRead < buffer.Length
            && (read = stream.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
        {
            totalRead += read;
        }

        return buffer[..totalRead];
    }
}
