namespace AcDream.Launcher.Core.Updates;

public sealed class UpdateSessionBarrier
{
    public const string LockFileName = ".update-session.lock";

    private readonly string _lockPath;

    public UpdateSessionBarrier(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _lockPath = Path.Combine(
            Path.GetFullPath(dataDirectory),
            "app",
            LockFileName);
    }

    public string LockPath => _lockPath;

    public SessionLease AcquireSession()
    {
        FileStream stream = Open(FileShare.ReadWrite, "A client update is in progress.");
        return new SessionLease(stream);
    }

    public bool TryAcquireSession(out SessionLease? lease)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_lockPath)
            ?? throw new InvalidOperationException(
                "The update/session lock path has no parent directory."));
        try
        {
            lease = new SessionLease(
                new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 1,
                    FileOptions.None));
            return true;
        }
        catch (IOException)
        {
            lease = null;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherUpdateException(
                $"The update/session lease could not be opened: {ex.Message}",
                ex);
        }
    }

    public ExclusiveLease AcquireExclusive()
    {
        FileStream stream = Open(
            FileShare.None,
            "A launcher session or another update transaction is running. "
                + "Stop every launcher session before updating.");
        return new ExclusiveLease(this, stream);
    }

    public bool TryAcquireExclusive(out ExclusiveLease? lease)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_lockPath)
            ?? throw new InvalidOperationException(
                "The update/session lock path has no parent directory."));
        try
        {
            lease = new ExclusiveLease(
                this,
                new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None));
            return true;
        }
        catch (IOException)
        {
            lease = null;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherUpdateException(
                $"The update/session lease could not be opened: {ex.Message}",
                ex);
        }
    }

    internal void RequireOwned(ExclusiveLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsHeldBy(this))
        {
            throw new LauncherUpdateException(
                "The cleanup operation does not hold this update barrier's exclusive lease.");
        }
    }

    private FileStream Open(FileShare share, string refusal)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_lockPath)
            ?? throw new InvalidOperationException(
                "The update/session lock path has no parent directory."));
        try
        {
            return new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                share,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException ex)
        {
            throw new LauncherUpdateException(refusal, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherUpdateException(
                $"The update/session lease could not be opened: {ex.Message}",
                ex);
        }
    }

    public sealed class SessionLease : IDisposable
    {
        private FileStream? _stream;

        internal SessionLease(FileStream stream) => _stream = stream;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    public sealed class ExclusiveLease : IDisposable
    {
        private readonly UpdateSessionBarrier _owner;
        private FileStream? _stream;

        internal ExclusiveLease(UpdateSessionBarrier owner, FileStream stream)
        {
            _owner = owner;
            _stream = stream;
        }

        internal bool IsHeldBy(UpdateSessionBarrier owner) =>
            ReferenceEquals(_owner, owner)
            && Volatile.Read(ref _stream) is not null;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
