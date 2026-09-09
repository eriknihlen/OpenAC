namespace AcDream.Launcher.Core.Installation;

internal sealed class InstallerTransactionLease : IAsyncDisposable
{
    internal const string LockFileName = ".install.lock";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly FileStream _stream;

    private InstallerTransactionLease(FileStream stream)
    {
        _stream = stream;
    }

    internal static string GetLockPath(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), LockFileName);

    internal static async ValueTask<InstallerTransactionLease> AcquireAsync(
        string dataDirectory,
        CancellationToken cancellationToken = default,
        Action? contentionObserved = null)
    {
        string lockPath = GetLockPath(dataDirectory);
        Directory.CreateDirectory(
            Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException(
                "The installer lock path has no parent directory."));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return new InstallerTransactionLease(stream);
            }
            catch (IOException)
            {
                contentionObserved?.Invoke();
                await Task.Delay(RetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
