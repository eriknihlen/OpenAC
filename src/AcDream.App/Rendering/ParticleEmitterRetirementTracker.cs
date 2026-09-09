using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering;

/// <summary>
/// Durable, per-emitter teardown ledger. Emitter death removes simulation
/// state first, so renderer-owned resources must retain their own retryable
/// obligations instead of relying on the death event being raised again.
/// </summary>
internal sealed class ParticleEmitterRetirementTracker
{
    private sealed class RetirementState
    {
        public bool MeshReleased;
        public bool GfxInfoRemoved;
        public bool TextureOwnerReleased;
        public bool FailureReported;
    }

    private readonly Action<int> _releaseMesh;
    private readonly Action<int> _removeGfxInfo;
    private readonly Action<int> _releaseTextureOwner;
    private readonly Action<Exception>? _reportFailure;
    private readonly Dictionary<int, RetirementState> _pending = [];
    private readonly HashSet<int> _advancingHandles = [];

    public ParticleEmitterRetirementTracker(
        Action<int> releaseMesh,
        Action<int> removeGfxInfo,
        Action<int> releaseTextureOwner,
        Action<Exception>? reportFailure = null)
    {
        _releaseMesh = releaseMesh ?? throw new ArgumentNullException(nameof(releaseMesh));
        _removeGfxInfo = removeGfxInfo ?? throw new ArgumentNullException(nameof(removeGfxInfo));
        _releaseTextureOwner = releaseTextureOwner ?? throw new ArgumentNullException(nameof(releaseTextureOwner));
        _reportFailure = reportFailure;
    }

    internal int PendingCount => _pending.Count;

    public void BeginRetirement(int emitterHandle)
    {
        if (_advancingHandles.Contains(emitterHandle))
            return;
        if (!_pending.TryGetValue(emitterHandle, out RetirementState? state))
        {
            state = new RetirementState();
            _pending.Add(emitterHandle, state);
        }
        Advance(emitterHandle, state);
    }

    public void RetryPending()
    {
        if (_pending.Count == 0)
            return;

        int[] handles = [.. _pending.Keys];
        for (int i = 0; i < handles.Length; i++)
        {
            if (_pending.TryGetValue(handles[i], out RetirementState? state))
                Advance(handles[i], state);
        }
    }

    public void CompleteOrThrow()
    {
        RetryPending();
        if (_pending.Count != 0)
            throw new InvalidOperationException(
                $"{_pending.Count} particle-emitter teardown obligation(s) remain incomplete.");
    }

    private void Advance(int emitterHandle, RetirementState state)
    {
        if (!_advancingHandles.Add(emitterHandle))
            return;

        List<Exception>? failures = null;
        try
        {
            if (!state.MeshReleased)
            {
                try
                {
                    _releaseMesh(emitterHandle);
                    state.MeshReleased = true;
                }
                catch (Exception error)
                {
                    if (error is MeshReferenceMutationException { MutationCommitted: true })
                        state.MeshReleased = true;
                    (failures ??= []).Add(error);
                }
            }

            if (!state.GfxInfoRemoved)
            {
                try
                {
                    _removeGfxInfo(emitterHandle);
                    state.GfxInfoRemoved = true;
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }

            if (!state.TextureOwnerReleased)
            {
                try
                {
                    _releaseTextureOwner(emitterHandle);
                    state.TextureOwnerReleased = true;
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }

            if (state.MeshReleased && state.GfxInfoRemoved && state.TextureOwnerReleased)
                _pending.Remove(emitterHandle);

            if (failures is not null && !state.FailureReported)
            {
                state.FailureReported = true;
                _reportFailure?.Invoke(new AggregateException(
                    $"Particle emitter {emitterHandle} teardown will be retried.",
                    failures));
            }
        }
        finally
        {
            _advancingHandles.Remove(emitterHandle);
        }
    }
}
