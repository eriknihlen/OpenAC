using AcDream.Core.Net;

namespace AcDream.Runtime.Session;

public enum RuntimeConnectionStatus
{
    Inactive,
    Connecting,
    CheckingData,
    Ready,
    Unsupported,
    Failed,
}

public readonly record struct RuntimeConnectionSnapshot(
    RuntimeConnectionStatus Status,
    float ConnectionProgress,
    float UpdateProgress,
    string? Error = null);

public interface IRuntimeConnectionView
{
    RuntimeConnectionSnapshot Snapshot { get; }
}

public sealed class RuntimeConnectionState : IRuntimeConnectionView
{
    private readonly object _gate = new();
    private RuntimeConnectionSnapshot _snapshot;

    public static IRuntimeConnectionView Inactive { get; } = new RuntimeConnectionState();

    public RuntimeConnectionSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    internal void Reset()
    {
        lock (_gate) _snapshot = default;
    }

    internal void Apply(ConnectionProgress progress)
    {
        lock (_gate)
        {
            RuntimeConnectionStatus status = progress.Phase switch
            {
                ConnectionPhase.Connecting => RuntimeConnectionStatus.Connecting,
                ConnectionPhase.CheckingData => RuntimeConnectionStatus.CheckingData,
                ConnectionPhase.Ready => RuntimeConnectionStatus.Ready,
                ConnectionPhase.Unsupported => RuntimeConnectionStatus.Unsupported,
                ConnectionPhase.Failed => RuntimeConnectionStatus.Failed,
                _ => RuntimeConnectionStatus.Inactive,
            };
            _snapshot = new(status,
                status is RuntimeConnectionStatus.CheckingData or RuntimeConnectionStatus.Ready
                    or RuntimeConnectionStatus.Unsupported ? 1 : _snapshot.ConnectionProgress,
                status == RuntimeConnectionStatus.Ready ? 1 : 0,
                progress.Error);
        }
    }

    internal void Fail(Exception error)
    {
        Apply(new(error is UnsupportedDataUpdateException
            ? ConnectionPhase.Unsupported : ConnectionPhase.Failed, error.Message));
    }
}
