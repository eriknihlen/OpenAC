using System.Collections.Generic;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Wb;

public sealed class LandblockSpawnAdapter
{
    private sealed class ReferenceRegistration
    {
        public bool Desired;
        public bool Held;
    }

    private sealed class LandblockRegistration
    {
        public bool WantsLoaded;
        public Dictionary<ulong, ReferenceRegistration> Ordinary { get; } = new();
        public Dictionary<ulong, ReferenceRegistration> Prepared { get; } = new();
    }

    private readonly IWbMeshAdapter _adapter;
    private readonly Dictionary<uint, LandblockRegistration> _registrations = new();

    public LandblockSpawnAdapter(IWbMeshAdapter adapter)
    {
        System.ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
    }

    public void OnLandblockLoaded(
        LoadedLandblock landblock,
        IEnumerable<ulong>? additionalReadinessIds = null,
        IEnumerable<ulong>? additionalOrdinaryIds = null,
        bool replaceExisting = false)
    {
        System.ArgumentNullException.ThrowIfNull(landblock);

        var unique = new HashSet<ulong>();
        foreach (var entity in landblock.Entities)
        {
            // Atlas-tier filter: server-spawned entities (ServerGuid != 0)
            // belong to the per-instance path and are NOT registered with WB.
            if (entity.ServerGuid != 0) continue;

            foreach (var meshRef in entity.MeshRefs)
                unique.Add((ulong)meshRef.GfxObjId);
        }
        if (additionalOrdinaryIds is not null)
            unique.UnionWith(additionalOrdinaryIds.Where(static id => id != 0));

        HashSet<ulong>? preparedIds = additionalReadinessIds is null
            ? null
            : new HashSet<ulong>(additionalReadinessIds);

        if (!_registrations.TryGetValue(landblock.LandblockId, out var registration))
        {
            registration = new LandblockRegistration { WantsLoaded = true };
            _registrations.Add(landblock.LandblockId, registration);
        }
        else if (!registration.WantsLoaded)
        {
            // This is a new load edge that arrived while a preceding unload
            // still had unfinished releases. The new snapshot replaces the old
            // desired set. Any still-held overlap remains acquired; obsolete
            // residual references are released by Reconcile below.
            MarkAllUndesired(registration.Ordinary);
            MarkAllUndesired(registration.Prepared);
            registration.WantsLoaded = true;
        }
        else if (replaceExisting)
        {
            MarkAllUndesired(registration.Ordinary);
            MarkAllUndesired(registration.Prepared);
        }

        MarkDesired(registration.Ordinary, unique);
        if (preparedIds is not null)
            MarkDesired(registration.Prepared, preparedIds);

        List<Exception>? failures = null;
        ReleaseUndesired(registration.Ordinary, ref failures);
        ReleaseUndesired(registration.Prepared, ref failures);
        PruneReleasedUndesired(registration.Ordinary);
        PruneReleasedUndesired(registration.Prepared);
        AcquireDesired(registration.Ordinary, prepared: false, ref failures);
        AcquireDesired(registration.Prepared, prepared: true, ref failures);
        ThrowFailures(
            failures,
            $"Landblock 0x{landblock.LandblockId:X8} mesh-reference acquisition did not fully converge.");
    }

    public bool IsLandblockRenderReady(uint landblockId)
    {
        if (!_registrations.TryGetValue(landblockId, out var registration)
            || !registration.WantsLoaded)
            return false;

        foreach (var pair in registration.Ordinary)
            if (!pair.Value.Desired
                || !pair.Value.Held
                || !_adapter.IsRenderDataReady(pair.Key))
                return false;
        foreach (var pair in registration.Prepared)
            if (!pair.Value.Desired
                || !pair.Value.Held
                || !_adapter.IsRenderDataReady(pair.Key))
                return false;
        return true;
    }

    public void OnLandblockUnloaded(uint landblockId)
    {
        if (!_registrations.TryGetValue(landblockId, out var registration))
            return;

        registration.WantsLoaded = false;
        MarkAllUndesired(registration.Ordinary);
        MarkAllUndesired(registration.Prepared);

        List<Exception>? failures = null;
        ReleaseUndesired(registration.Ordinary, ref failures);
        ReleaseUndesired(registration.Prepared, ref failures);
        PruneReleasedUndesired(registration.Ordinary);
        PruneReleasedUndesired(registration.Prepared);

        if (registration.Ordinary.Count == 0 && registration.Prepared.Count == 0)
            _registrations.Remove(landblockId);

        ThrowFailures(
            failures,
            $"Landblock 0x{landblockId:X8} mesh-reference release did not fully converge.");
    }

    private static void MarkDesired(
        Dictionary<ulong, ReferenceRegistration> registrations,
        IEnumerable<ulong> ids)
    {
        foreach (ulong id in ids)
        {
            if (!registrations.TryGetValue(id, out var reference))
            {
                reference = new ReferenceRegistration();
                registrations.Add(id, reference);
            }

            reference.Desired = true;
        }
    }

    private static void MarkAllUndesired(
        Dictionary<ulong, ReferenceRegistration> registrations)
    {
        foreach (var reference in registrations.Values)
            reference.Desired = false;
    }

    private void AcquireDesired(
        Dictionary<ulong, ReferenceRegistration> registrations,
        bool prepared,
        ref List<Exception>? failures)
    {
        foreach (var pair in registrations)
        {
            ReferenceRegistration reference = pair.Value;
            if (!reference.Desired || reference.Held)
                continue;

            try
            {
                if (prepared)
                    _adapter.PinPreparedRenderData(pair.Key);
                else
                    _adapter.IncrementRefCount(pair.Key);
                reference.Held = true;
            }
            catch (Exception error)
            {
                if (error is MeshReferenceMutationException { MutationCommitted: true })
                    reference.Held = true;
                (failures ??= new List<Exception>()).Add(error);
            }
        }
    }

    private void ReleaseUndesired(
        Dictionary<ulong, ReferenceRegistration> registrations,
        ref List<Exception>? failures)
    {
        foreach (var pair in registrations)
        {
            ReferenceRegistration reference = pair.Value;
            if (reference.Desired || !reference.Held)
                continue;

            try
            {
                _adapter.DecrementRefCount(pair.Key);
                reference.Held = false;
            }
            catch (Exception error)
            {
                if (error is MeshReferenceMutationException { MutationCommitted: true })
                    reference.Held = false;
                (failures ??= new List<Exception>()).Add(error);
            }
        }
    }

    private static void PruneReleasedUndesired(
        Dictionary<ulong, ReferenceRegistration> registrations)
    {
        List<ulong>? released = null;
        foreach (var pair in registrations)
        {
            if (!pair.Value.Desired && !pair.Value.Held)
                (released ??= new List<ulong>()).Add(pair.Key);
        }

        if (released is null)
            return;
        foreach (ulong id in released)
            registrations.Remove(id);
    }

    private static void ThrowFailures(List<Exception>? failures, string message)
    {
        if (failures is null)
            return;
        if (failures.Count == 1)
            throw failures[0];
        throw new AggregateException(message, failures);
    }
}
