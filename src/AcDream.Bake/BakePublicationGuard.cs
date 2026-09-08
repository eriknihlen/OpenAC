using AcDream.Platform;

namespace AcDream.Bake;

internal static class BakePublicationGuard
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    internal static IDisposable? AcquireIfRequested(
        string outputPath,
        CancellationToken cancellationToken)
    {
        string? nonce = Environment.GetEnvironmentVariable(
            BakePublicationGuardPaths.NonceEnvironmentVariable);
        if (nonce is null)
        {
            return null;
        }

        if (!BakePublicationGuardPaths.IsValidNonce(nonce))
        {
            throw new InvalidOperationException(
                "The launcher bake publication nonce is invalid.");
        }

        string lockPath = BakePublicationGuardPaths.GetPublishLockPath(
            outputPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException(
                "The bake publication lock has no parent directory."));

        FileStream? lease = null;
        while (lease is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lease = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.None);
            }
            catch (IOException)
            {
                cancellationToken.WaitHandle.WaitOne(RetryDelay);
            }
        }

        try
        {
            string authorizationPath =
                BakePublicationGuardPaths.GetAuthorizationPath(outputPath);
            string authorized = File.Exists(authorizationPath)
                ? File.ReadAllText(authorizationPath)
                : string.Empty;
            if (!string.Equals(authorized, nonce, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "This bake process is no longer authorized to publish its output.");
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

}
