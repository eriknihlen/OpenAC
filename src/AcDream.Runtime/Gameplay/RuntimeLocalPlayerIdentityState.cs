namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeLocalPlayerIdentityOwnershipSnapshot(
    bool IsDisposed,
    uint ServerGuid,
    long Revision)
{
    public bool IsConverged => IsDisposed && ServerGuid == 0u;
}

public sealed class RuntimeLocalPlayerIdentityState : IDisposable
{
    private uint _serverGuid;
    private long _revision;
    private bool _disposed;

    public uint ServerGuid
    {
        get => _serverGuid;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_serverGuid == value)
                return;
            _serverGuid = value;
            Interlocked.Increment(ref _revision);
        }
    }

    public long Revision => Interlocked.Read(ref _revision);
    public bool IsDisposed => _disposed;

    public void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ServerGuid = 0u;
    }

    public RuntimeLocalPlayerIdentityOwnershipSnapshot CaptureOwnership() =>
        new(_disposed, _serverGuid, Revision);

    public void Dispose()
    {
        if (_disposed)
            return;
        _serverGuid = 0u;
        Interlocked.Increment(ref _revision);
        _disposed = true;
    }
}
