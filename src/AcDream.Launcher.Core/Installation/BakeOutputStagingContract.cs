namespace AcDream.Launcher.Core.Installation;

internal static class BakeOutputStagingContract
{
    internal const string StagingMarker = ".acdream-bake.";
    private const string Suffix = ".tmp";

    internal static string CreateStagingPath(
        string destinationPath,
        Guid transactionId)
    {
        string fullDestination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException(
                "The prepared package path has no parent directory.");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}{StagingMarker}"
                + $"{transactionId:N}{Suffix}");
    }

    internal static bool IsOwnedStagingFileName(
        string fileName,
        string destinationFileName)
    {
        string prefix = $".{destinationFileName}{StagingMarker}";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(Suffix, StringComparison.Ordinal)
            || fileName.Length != prefix.Length + 32 + Suffix.Length)
        {
            return false;
        }

        ReadOnlySpan<char> transaction = fileName.AsSpan(prefix.Length, 32);
        return Guid.TryParseExact(transaction, "N", out _);
    }

    internal static void DeleteOwnedStagingFiles(string destinationPath)
    {
        string fullDestination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string destinationFileName = Path.GetFileName(fullDestination);
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory))
            {
                if (IsOwnedStagingFileName(
                        Path.GetFileName(candidate),
                        destinationFileName))
                {
                    LauncherInstallRecordStore.TryDelete(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
