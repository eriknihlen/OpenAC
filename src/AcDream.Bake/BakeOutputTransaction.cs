using AcDream.Content.Pak;

namespace AcDream.Bake;

public static class BakeOutputTransaction
{
    internal const string StagingMarker = ".acdream-bake.";

    public static TResult WriteValidateAndPublish<TResult>(
        string destinationPath,
        Func<string, TResult> writeTemporary,
        Action<string, TResult> validateTemporary,
        CancellationToken cancellationToken = default)
        => WriteValidateAndPublish(
            destinationPath,
            writeTemporary,
            validateTemporary,
            beforePublicationLock: null,
            beforePromotion: null,
            cancellationToken);

    internal static TResult WriteValidateAndPublish<TResult>(
        string destinationPath,
        Func<string, TResult> writeTemporary,
        Action<string, TResult> validateTemporary,
        Action? beforePublicationLock,
        Action? beforePromotion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writeTemporary);
        ArgumentNullException.ThrowIfNull(validateTemporary);

        string fullDestination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("destination has no parent directory");
        Directory.CreateDirectory(directory);

        string temporaryPath = CreateStagingPath(fullDestination, Guid.NewGuid());

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TResult result = writeTemporary(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            validateTemporary(temporaryPath, result);
            cancellationToken.ThrowIfCancellationRequested();
            beforePublicationLock?.Invoke();
            using IDisposable? publication =
                BakePublicationGuard.AcquireIfRequested(
                    fullDestination,
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            beforePromotion?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(temporaryPath, fullDestination, overwrite: true);

            return result;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original exception. An adjacent .tmp is
                // recognizable and never mistaken for a published pak.
            }
        }
    }

    internal static string CreateStagingPath(string destinationPath, Guid transactionId)
    {
        string fullDestination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException(
                "destination has no parent directory");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}{StagingMarker}"
                + $"{transactionId:N}.tmp");
    }
}
