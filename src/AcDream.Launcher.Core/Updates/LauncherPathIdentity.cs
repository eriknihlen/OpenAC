namespace AcDream.Launcher.Core.Updates;

internal static class LauncherPathIdentity
{
    public static bool Equals(string left, string right) =>
        string.Equals(
            Normalize(left),
            Normalize(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsMacOS())
        {
            return fullPath;
        }

        string root = Path.GetPathRoot(fullPath)
            ?? throw new LauncherUpdateException("The launcher path has no root.");
        string current = root;
        string relative = fullPath[root.Length..];
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(current, segment);
            try
            {
                FileAttributes attributes = File.GetAttributes(candidate);
                FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(candidate)
                    : new FileInfo(candidate);
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            }
            catch (IOException)
            {
                current = candidate;
            }
            catch (UnauthorizedAccessException)
            {
                current = candidate;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
