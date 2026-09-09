namespace AcDream.App.Rendering.Wb;

public sealed class MeshReferenceMutationException : Exception
{
    public MeshReferenceMutationException(
        string message,
        bool mutationCommitted,
        Exception innerException)
        : base(message, innerException)
    {
        MutationCommitted = mutationCommitted;
    }

    public bool MutationCommitted { get; }
}

public interface IWbMeshAdapter
{
    void IncrementRefCount(ulong id);

    void DecrementRefCount(ulong id);

    /// <summary>
    /// Pins render data whose CPU preparation is owned by a specialized
    /// pipeline (for example synthetic EnvCell geometry). Unlike ordinary
    /// registration, production implementations must not start a generic
    /// GfxObj decode for this id.
    /// </summary>
    void PinPreparedRenderData(ulong id) => IncrementRefCount(id);

    bool IsRenderDataReady(ulong id) => true;
}
