namespace AcDream.Core.Net;

public enum ConnectionPhase
{
    Inactive,
    Connecting,
    CheckingData,
    Ready,
    Unsupported,
    Failed,
}

public readonly record struct ConnectionProgress(ConnectionPhase Phase, string? Error = null);

public sealed class UnsupportedDataUpdateException : NotSupportedException
{
    public UnsupportedDataUpdateException()
        : base("This server requires a game-data update. OpenAC does not support downloading DAT updates yet. Install the server's required data files before connecting.")
    {
    }
}
