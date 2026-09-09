using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering;

internal sealed class SyntheticEntityMeshReferenceOwner : IDisposable
{
    private sealed class ReferenceState(ulong gfxObjId)
    {
        public ulong GfxObjId { get; } = gfxObjId;
        public bool Held { get; set; }
    }

    private readonly IWbMeshAdapter _meshAdapter;
    private readonly ReferenceState[] _references;
    private bool _desired;
    private bool _disposeRequested;
    private bool _disposed;
    private bool _reconciling;
    private bool _reconcileAgain;

    public SyntheticEntityMeshReferenceOwner(
        IWbMeshAdapter meshAdapter,
        IEnumerable<ulong> gfxObjIds)
    {
        _meshAdapter = meshAdapter ?? throw new ArgumentNullException(nameof(meshAdapter));
        ArgumentNullException.ThrowIfNull(gfxObjIds);
        _references = gfxObjIds
            .Where(static id => id != 0u)
            .Distinct()
            .Select(static id => new ReferenceState(id))
            .ToArray();
    }

    public bool IsDisposed => _disposed;

    public void Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        _desired = true;
        try
        {
            Reconcile();
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
        }
        catch (Exception acquisitionFailure)
        {
            _desired = false;
            try
            {
                Reconcile();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Synthetic entity mesh acquisition failed and its rollback did not fully converge.",
                    acquisitionFailure,
                    rollbackFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(acquisitionFailure)
                .Throw();
            throw new InvalidOperationException("Unreachable exception dispatch path.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposeRequested = true;
        _desired = false;
        if (_reconciling)
        {
            _reconcileAgain = true;
            return;
        }

        Reconcile();
    }

    private void Reconcile()
    {
        if (_reconciling)
        {
            _reconcileAgain = true;
            return;
        }

        _reconciling = true;
        List<Exception>? failures = null;
        try
        {
            do
            {
                _reconcileAgain = false;
                for (int i = 0; i < _references.Length; i++)
                {
                    ReferenceState reference = _references[i];
                    bool targetHeld = _desired;
                    if (reference.Held == targetHeld)
                        continue;

                    try
                    {
                        if (targetHeld)
                            _meshAdapter.IncrementRefCount(reference.GfxObjId);
                        else
                            _meshAdapter.DecrementRefCount(reference.GfxObjId);
                        reference.Held = targetHeld;
                    }
                    catch (Exception error)
                    {
                        if (error is MeshReferenceMutationException
                            {
                                MutationCommitted: true,
                            })
                        {
                            reference.Held = targetHeld;
                        }

                        string operation = targetHeld ? "acquisition" : "release";
                        (failures ??= []).Add(new InvalidOperationException(
                            $"Synthetic entity mesh 0x{reference.GfxObjId:X10} reference {operation} failed.",
                            error));
                    }
                }
            }
            while (_reconcileAgain);
        }
        finally
        {
            _reconciling = false;
            if (_disposeRequested && _references.All(static reference => !reference.Held))
                _disposed = true;
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more synthetic entity mesh references failed to reconcile.",
                failures);
    }
}
