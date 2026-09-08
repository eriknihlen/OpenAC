namespace AcDream.App.Streaming;

public sealed class StreamingMutationException : Exception
{
    public StreamingMutationException(
        string message,
        bool mutationCommitted,
        Exception? innerException = null)
        : base(message, innerException)
    {
        MutationCommitted = mutationCommitted;
    }

    public bool MutationCommitted { get; }
}
