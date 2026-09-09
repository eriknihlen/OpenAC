using AcDream.Platform;

namespace AcDream.Launcher.Core.Installation;

internal static class BakePublicationGuardContract
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    internal static async ValueTask<PublicationLease> AcquireAsync(
        string outputPath,
        CancellationToken cancellationToken = default,
        Action? contentionObserved = null)
    {
        string lockPath = BakePublicationGuardPaths.GetPublishLockPath(
            outputPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException(
                "The bake publication lock has no parent directory."));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new PublicationLease(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.None));
            }
            catch (IOException)
            {
                contentionObserved?.Invoke();
                await Task.Delay(RetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static void Authorize(
        string outputPath,
        string nonce,
        PublicationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!BakePublicationGuardPaths.IsValidNonce(nonce))
        {
            throw new ArgumentException(
                "The bake publication nonce must be a lowercase GUID in N format.",
                nameof(nonce));
        }

        string authorizationPath =
            BakePublicationGuardPaths.GetAuthorizationPath(outputPath);
        using var stream = new FileStream(
            authorizationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(nonce);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    internal static void Invalidate(
        string outputPath,
        PublicationLease lease,
        string? onlyIfNonceMatches = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        string authorizationPath =
            BakePublicationGuardPaths.GetAuthorizationPath(outputPath);
        if (!File.Exists(authorizationPath))
        {
            return;
        }

        if (onlyIfNonceMatches is not null)
        {
            string current = File.ReadAllText(authorizationPath);

            if (!string.Equals(
                    current,
                    onlyIfNonceMatches,
                    StringComparison.Ordinal))
            {
                return;
            }
        }

        File.Delete(authorizationPath);
    }

    internal sealed class PublicationLease : IAsyncDisposable
    {
        private readonly FileStream _stream;

        internal PublicationLease(FileStream stream)
        {
            _stream = stream;
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
