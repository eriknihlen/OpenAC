using System;
using System.Collections.Generic;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.Rendering.Wb;

/// <summary>
/// The exact owner is still inside a reference transition, so its requested
/// logical removal has been queued and must be retried by the live-entity
/// teardown owner after the transition unwinds.
/// </summary>
public sealed class EntityPresentationRemovalDeferredException(uint serverGuid)
    : InvalidOperationException(
        $"Live entity 0x{serverGuid:X8} presentation removal is deferred until its active reference transition completes.");

public sealed class EntitySpawnAdapter
{
    private readonly IEntityTextureLifetime _textureLifetime;
    private readonly Func<WorldEntity, AnimationSequencer> _sequencerFactory;
    private readonly IWbMeshAdapter? _meshAdapter;

    private readonly Dictionary<RuntimeEntityKey, Owner> _ownersByKey = [];

    private sealed class Owner(
        RuntimeEntityKey key,
        WorldEntity entity,
        AnimatedEntityState state,
        HashSet<ulong> meshIds)
    {
        public RuntimeEntityKey Key { get; } = key;
        public WorldEntity Entity { get; } = entity;
        public AnimatedEntityState State { get; } = state;
        public HashSet<ulong> MeshIds { get; set; } = meshIds;
        public HashSet<ulong> MeshReferencesHeld { get; } = new();
        public bool IsPresentationResident { get; set; }
        public bool TextureReleaseRequired { get; set; }
        public PresentationTransition Transition { get; set; }
        public bool RemovalPending { get; set; }

        public bool HasPresentationResources
        {
            get
            {
                if (TextureReleaseRequired)
                    return true;

                return MeshReferencesHeld.Count != 0;
            }
        }

        public bool IsFullyResident
        {
            get
            {
                if (!IsPresentationResident || !TextureReleaseRequired)
                    return false;
                return MeshReferencesHeld.SetEquals(MeshIds);
            }
        }

        public bool IsFullySuspended =>
            !IsPresentationResident && !HasPresentationResources;
    }

    private enum PresentationTransition
    {
        None,
        Resuming,
        Suspending,
        ChangingAppearance,
    }

    public EntitySpawnAdapter(
        IEntityTextureLifetime textureLifetime,
        Func<WorldEntity, AnimationSequencer> sequencerFactory,
        IWbMeshAdapter? meshAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(textureLifetime);
        ArgumentNullException.ThrowIfNull(sequencerFactory);
        _textureLifetime = textureLifetime;
        _sequencerFactory = sequencerFactory;
        _meshAdapter = meshAdapter;
    }

    public AnimatedEntityState? OnCreate(WorldEntity entity)
        => OnCreate(new RuntimeEntityKey(entity.Id, 0), entity);

    public AnimatedEntityState? OnCreate(
        RuntimeEntityKey key,
        WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Atlas-tier entities (procedural / dat-hydrated, ServerGuid == 0)
        // are handled by LandblockSpawnAdapter, not here.
        if (entity.ServerGuid == 0) return null;
        if (key.LocalEntityId == 0 || key.LocalEntityId != entity.Id)
        {
            throw new InvalidOperationException(
                "The exact Runtime projection key must match the WorldEntity local ID.");
        }

        entity.RefreshAabb();

        var sequencer = _sequencerFactory(entity);
        var state = new AnimatedEntityState(sequencer);

        state.HideParts(entity.HiddenPartsMask);
        foreach (var po in entity.PartOverrides)
            state.SetPartOverride(po.PartIndex, po.GfxObjId);

        HashSet<ulong> meshIds = _meshAdapter is null
            ? []
            : CollectMeshIds(entity.MeshRefs, entity.PartOverrides);

        var replacementOwner = new Owner(key, entity, state, meshIds);
        if (_ownersByKey.TryGetValue(key, out Owner? displacedOwner))
        {
            if (displacedOwner.RemovalPending)
            {
                throw new EntityPresentationRemovalDeferredException(entity.ServerGuid);
            }

            if (displacedOwner.Transition != PresentationTransition.None)
            {
                throw new InvalidOperationException(
                    $"Live entity 0x{entity.ServerGuid:X8} replacement was requested while its presentation transition was already in progress.");
            }

            if (displacedOwner.HasPresentationResources)
            {
                if (!SuspendPresentation(displacedOwner))
                {
                    throw new InvalidOperationException(
                        $"Live entity 0x{entity.ServerGuid:X8} replacement was requested while its presentation teardown was already in progress.");
                }
            }

            _ownersByKey[key] = replacementOwner;
        }
        else
        {
            _ownersByKey.Add(key, replacementOwner);
        }

        return state;
    }

    public bool SetPresentationResident(WorldEntity entity, bool resident)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!TryFindOwner(entity, out _, out Owner owner)
            || owner.RemovalPending
            || (resident ? owner.IsFullyResident : owner.IsFullySuspended))
        {
            return false;
        }

        return resident
            ? ResumePresentation(owner)
            : SuspendPresentation(owner);
    }

    public bool OnAppearanceChanged(
        WorldEntity entity,
        IReadOnlyList<MeshRef> meshRefs,
        IReadOnlyList<PartOverride> partOverrides,
        Action publishAppearance,
        Action? afterPublication = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(meshRefs);
        ArgumentNullException.ThrowIfNull(partOverrides);
        ArgumentNullException.ThrowIfNull(publishAppearance);

        if (!TryFindOwner(entity, out _, out Owner owner)
            || owner.RemovalPending
            || owner.Transition != PresentationTransition.None)
        {
            return false;
        }

        HashSet<ulong> nextMeshIds = _meshAdapter is null
            ? []
            : CollectMeshIds(meshRefs, partOverrides);
        owner.Transition = PresentationTransition.ChangingAppearance;
        var acquiredThisTransition = new List<ulong>();
        bool meshSetPublished = false;

        try
        {
            if (owner.IsPresentationResident && _meshAdapter is not null)
                AcquireMissingMeshReferences(owner, nextMeshIds, acquiredThisTransition);

            publishAppearance();

            owner.MeshIds = nextMeshIds;
            meshSetPublished = true;
            afterPublication?.Invoke();

            List<Exception>? releaseFailures = owner.IsPresentationResident
                ? ReleaseMeshReferencesOutside(owner, nextMeshIds)
                : ReleaseAllMeshReferences(owner);
            if (releaseFailures is not null)
            {
                throw new AggregateException(
                    $"Live entity 0x{owner.Entity.ServerGuid:X8} appearance mesh retirement failed.",
                    releaseFailures);
            }

            return true;
        }
        catch (Exception acquireOrPublicationFailure) when (!meshSetPublished)
        {
            // Acquisition failed before publication. Preserve the prior exact
            // mesh set and undo only references acquired by this transition.
            List<Exception>? rollbackFailures = RollBackAcquiredMeshReferences(
                owner,
                acquiredThisTransition);
            if (rollbackFailures is null)
                throw;

            rollbackFailures.Insert(0, acquireOrPublicationFailure);
            throw new AggregateException(
                $"Live entity 0x{owner.Entity.ServerGuid:X8} appearance publication and mesh rollback failed.",
                rollbackFailures);
        }
        finally
        {
            owner.Transition = PresentationTransition.None;
        }
    }

    public void OnRemove(uint serverGuid)
    {
        foreach ((RuntimeEntityKey key, Owner owner) in _ownersByKey)
        {
            if (owner.Entity.ServerGuid == serverGuid)
            {
                _ = TryRemove(key, owner.Entity);
                return;
            }
        }
    }

    private bool TryRemove(
        RuntimeEntityKey key,
        WorldEntity expectedEntity)
    {
        if (!_ownersByKey.TryGetValue(key, out Owner? owner)
            || !ReferenceEquals(owner.Entity, expectedEntity))
        {
            return false;
        }

        owner.RemovalPending = true;
        if (owner.Transition != PresentationTransition.None)
            throw new EntityPresentationRemovalDeferredException(
                owner.Entity.ServerGuid);

        if (owner.HasPresentationResources && !SuspendPresentation(owner))
            return false;

        if (_ownersByKey.TryGetValue(key, out Owner? current)
            && ReferenceEquals(current, owner))
        {
            _ownersByKey.Remove(key);
            return true;
        }


        return false;
    }

    public bool OnRemove(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return TryFindOwner(entity, out RuntimeEntityKey key, out _)
            && TryRemove(key, entity);
    }

    public AnimatedEntityState? GetState(uint serverGuid)
    {
        foreach (Owner owner in _ownersByKey.Values)
        {
            if (owner.Entity.ServerGuid == serverGuid)
                return owner.State;
        }

        return null;
    }

    private bool TryFindOwner(
        WorldEntity entity,
        out RuntimeEntityKey key,
        out Owner owner)
    {
        foreach ((RuntimeEntityKey candidateKey, Owner candidate) in _ownersByKey)
        {
            if (ReferenceEquals(candidate.Entity, entity))
            {
                key = candidateKey;
                owner = candidate;
                return true;
            }
        }

        key = default;
        owner = null!;
        return false;
    }

    private bool ResumePresentation(Owner owner)
    {
        if (owner.Transition != PresentationTransition.None)
            return false;

        owner.Transition = PresentationTransition.Resuming;
        var acquiredThisTransition = new List<ulong>();
        bool desiredMeshSetAcquired = false;
        try
        {
            if (_meshAdapter is not null)
                AcquireMissingMeshReferences(owner, owner.MeshIds, acquiredThisTransition);
            desiredMeshSetAcquired = true;

            owner.TextureReleaseRequired = true;
            owner.IsPresentationResident = true;

            List<Exception>? releaseFailures = ReleaseMeshReferencesOutside(
                owner,
                owner.MeshIds);
            if (releaseFailures is not null)
            {
                throw new AggregateException(
                    $"Live entity 0x{owner.Entity.ServerGuid:X8} presentation mesh reconciliation failed.",
                    releaseFailures);
            }

            return true;
        }
        catch (Exception acquireFailure) when (!desiredMeshSetAcquired)
        {
            List<Exception>? rollbackFailures = RollBackAcquiredMeshReferences(
                owner,
                acquiredThisTransition);
            if (rollbackFailures is null)
                throw;

            rollbackFailures.Insert(0, acquireFailure);
            throw new AggregateException(
                $"Live entity 0x{owner.Entity.ServerGuid:X8} presentation resume and rollback failed.",
                rollbackFailures);
        }
        finally
        {
            owner.Transition = PresentationTransition.None;
        }
    }

    private bool SuspendPresentation(Owner owner)
    {
        if (owner.Transition != PresentationTransition.None)
            return false;

        owner.Transition = PresentationTransition.Suspending;

        List<Exception>? failures = null;
        if (owner.TextureReleaseRequired)
        {
            try
            {
                _textureLifetime.ReleaseOwner(owner.Entity.Id);
                owner.TextureReleaseRequired = false;
            }
            catch (Exception error)
            {
                (failures ??= new List<Exception>()).Add(error);
            }
        }

        List<Exception>? meshFailures = ReleaseAllMeshReferences(owner);
        if (meshFailures is not null)
            (failures ??= new List<Exception>()).AddRange(meshFailures);

        if (failures is not null)
        {
            owner.Transition = PresentationTransition.None;
            throw new AggregateException(
                $"Live entity 0x{owner.Entity.ServerGuid:X8} presentation suspension failed.",
                failures);
        }

        owner.IsPresentationResident = false;
        owner.Transition = PresentationTransition.None;
        return true;
    }

    private static HashSet<ulong> CollectMeshIds(
        IReadOnlyList<MeshRef> meshRefs,
        IReadOnlyList<PartOverride> partOverrides)
    {
        var unique = new HashSet<ulong>();
        for (int i = 0; i < meshRefs.Count; i++)
            unique.Add(meshRefs[i].GfxObjId);
        for (int i = 0; i < partOverrides.Count; i++)
            unique.Add(partOverrides[i].GfxObjId);
        return unique;
    }

    private void AcquireMissingMeshReferences(
        Owner owner,
        HashSet<ulong> desiredMeshIds,
        List<ulong> acquiredThisTransition)
    {
        if (_meshAdapter is null)
            return;

        foreach (ulong meshId in desiredMeshIds)
        {
            if (owner.MeshReferencesHeld.Contains(meshId))
                continue;

            try
            {
                _meshAdapter.IncrementRefCount(meshId);
                owner.MeshReferencesHeld.Add(meshId);
                acquiredThisTransition.Add(meshId);
            }
            catch (MeshReferenceMutationException error)
            {
                if (error.MutationCommitted)
                {
                    owner.MeshReferencesHeld.Add(meshId);
                    acquiredThisTransition.Add(meshId);
                }

                throw;
            }
        }
    }

    private List<Exception>? RollBackAcquiredMeshReferences(
        Owner owner,
        List<ulong> acquiredThisTransition)
    {
        if (_meshAdapter is null)
            return null;

        List<Exception>? failures = null;
        for (int i = acquiredThisTransition.Count - 1; i >= 0; i--)
        {
            ulong meshId = acquiredThisTransition[i];
            if (!owner.MeshReferencesHeld.Contains(meshId))
                continue;

            try
            {
                _meshAdapter.DecrementRefCount(meshId);
                owner.MeshReferencesHeld.Remove(meshId);
            }
            catch (Exception error)
            {
                if (error is MeshReferenceMutationException { MutationCommitted: true })
                    owner.MeshReferencesHeld.Remove(meshId);
                (failures ??= new List<Exception>()).Add(error);
            }
        }

        return failures;
    }

    private List<Exception>? ReleaseMeshReferencesOutside(
        Owner owner,
        HashSet<ulong> desiredMeshIds)
    {
        if (_meshAdapter is null || owner.MeshReferencesHeld.Count == 0)
            return null;

        List<Exception>? failures = null;
        ulong[] heldSnapshot = [.. owner.MeshReferencesHeld];
        foreach (ulong meshId in heldSnapshot)
        {
            if (desiredMeshIds.Contains(meshId))
                continue;

            try
            {
                _meshAdapter.DecrementRefCount(meshId);
                owner.MeshReferencesHeld.Remove(meshId);
            }
            catch (Exception error)
            {
                if (error is MeshReferenceMutationException { MutationCommitted: true })
                    owner.MeshReferencesHeld.Remove(meshId);
                (failures ??= new List<Exception>()).Add(error);
            }
        }

        return failures;
    }

    private List<Exception>? ReleaseAllMeshReferences(Owner owner)
    {
        if (_meshAdapter is null || owner.MeshReferencesHeld.Count == 0)
            return null;

        return ReleaseMeshReferencesOutside(owner, []);
    }
}
