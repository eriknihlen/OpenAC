using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering;

internal enum ParticleSubmissionKind
{
    Billboard,
    Mesh,
}

internal readonly record struct ParticleSubmission(
    ParticleSubmissionKind Kind,
    int DrawIndex,
    float DistanceSq,
    int Sequence);

internal readonly record struct PreparedParticleAlphaSubmission(
    RetailAlphaQueue Queue,
    RetailAlphaList List,
    IRetailAlphaDrawSource Source,
    ParticleRenderer Owner,
    ParticleSubmissionKind Kind,
    int DrawIndex,
    Matrix4x4 ViewProjection,
    bool OverrideClipmap,
    float DistanceSq,
    int Sequence)
{
    internal void Append()
    {
        int token = Owner.ReservePreparedDispatchDeferredParticle(
            Kind,
            DrawIndex,
            ViewProjection);
        bool accepted;
        try
        {
            accepted = Queue.TryAppend(List, Source, token, OverrideClipmap);
        }
        catch
        {
            Owner.RollbackPreparedDispatchDeferredParticle(token);
            throw;
        }

        if (!accepted)
            Owner.RollbackPreparedDispatchDeferredParticle(token);
    }
}

internal static class ParticleSubmissionOrdering
{
    public static void Sort(List<ParticleSubmission> submissions)
    {
        ArgumentNullException.ThrowIfNull(submissions);
        submissions.Sort(static (left, right) =>
        {
            int distance = right.DistanceSq.CompareTo(left.DistanceSq);
            return distance != 0
                ? distance
                : left.Sequence.CompareTo(right.Sequence);
        });
    }
}

internal sealed class ParticleMeshReferenceTracker : IDisposable
{
    private sealed class ReferenceState
    {
        public required uint GfxObjId { get; init; }
        public bool Desired { get; set; }
        public bool Held { get; set; }
        public bool Reconciling { get; set; }
    }

    private readonly Action<uint> _increment;
    private readonly Action<uint> _decrement;
    private readonly Dictionary<int, ReferenceState> _referencesByEmitter = new();
    private bool _disposeRequested;
    private bool _disposed;

    public ParticleMeshReferenceTracker(Action<uint> increment, Action<uint> decrement)
    {
        _increment = increment ?? throw new ArgumentNullException(nameof(increment));
        _decrement = decrement ?? throw new ArgumentNullException(nameof(decrement));
    }

    public void Register(int emitterHandle, uint gfxObjId)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (!_referencesByEmitter.TryGetValue(emitterHandle, out ReferenceState? state))
        {
            state = new ReferenceState { GfxObjId = gfxObjId };
            _referencesByEmitter.Add(emitterHandle, state);
        }
        else if (state.GfxObjId != gfxObjId)
        {
            throw new InvalidOperationException(
                $"Particle emitter {emitterHandle} is already associated with " +
                $"GfxObj 0x{state.GfxObjId:X8}, not 0x{gfxObjId:X8}.");
        }

        state.Desired = true;
        Reconcile(emitterHandle, state);
    }

    public void Release(int emitterHandle)
    {
        if (_disposed || !_referencesByEmitter.TryGetValue(emitterHandle, out ReferenceState? state))
            return;

        state.Desired = false;
        Reconcile(emitterHandle, state);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposeRequested = true;
        List<Exception>? failures = null;
        int[] emitterHandles = [.. _referencesByEmitter.Keys];
        for (int i = 0; i < emitterHandles.Length; i++)
        {
            int emitterHandle = emitterHandles[i];
            if (!_referencesByEmitter.TryGetValue(emitterHandle, out ReferenceState? state))
                continue;

            state.Desired = false;
            try
            {
                Reconcile(emitterHandle, state);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (_referencesByEmitter.Count == 0)
            _disposed = true;

        if (failures is not null)
            throw new AggregateException(
                "One or more particle mesh references failed to release.",
                failures);
    }

    private void Reconcile(int emitterHandle, ReferenceState state)
    {
        if (state.Reconciling)
            return;

        state.Reconciling = true;
        Exception? failure = null;
        try
        {
            while (state.Desired != state.Held)
            {
                if (state.Desired)
                {
                    try
                    {
                        _increment(state.GfxObjId);
                        state.Held = true;
                    }
                    catch (MeshReferenceMutationException error)
                    {
                        if (error.MutationCommitted)
                        {
                            state.Held = true;
                            failure ??= error;
                            continue;
                        }
                        failure = error;
                        break;
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }
                }
                else
                {
                    try
                    {
                        _decrement(state.GfxObjId);
                        state.Held = false;
                    }
                    catch (MeshReferenceMutationException error)
                    {
                        if (error.MutationCommitted)
                        {
                            state.Held = false;
                            failure ??= error;
                            continue;
                        }
                        failure = error;
                        break;
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }
                }
            }
        }
        finally
        {
            state.Reconciling = false;
            if (!state.Desired && !state.Held)
                _referencesByEmitter.Remove(emitterHandle);
            if (_disposeRequested && _referencesByEmitter.Count == 0)
                _disposed = true;
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
