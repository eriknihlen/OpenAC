using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

public readonly record struct RuntimeEntityKey(
    uint LocalEntityId,
    ushort Incarnation);

public sealed class RuntimeEntityRecord
{
    private readonly Queue<RetailPhysicsStateTransition> _pendingStateTransitions = new();

    internal RuntimeEntityRecord(WorldSession.EntitySpawn snapshot)
    {
        ServerGuid = snapshot.Guid;
        Snapshot = snapshot;
        RefreshDerivedState();
        PositionAuthorityVersion = snapshot.Position is null ? 0UL : 1UL;
        VectorAuthorityVersion = snapshot.Physics is null ? 0UL : 1UL;
        VelocityAuthorityVersion = snapshot.Position is null
            && snapshot.Physics is null
                ? 0UL
                : 1UL;
        MovementAuthorityVersion = 1UL;
        ObjDescAuthorityVersion = 1UL;
        FinalPhysicsState = RetailPhysicsStateTransitions.ConstructorState;
        ApplyRawPhysicsState(RawPhysicsState);
    }

    public uint ServerGuid { get; }
    public ushort Incarnation => Snapshot.InstanceSequence;
    public ushort Generation => Incarnation;
    public WorldSession.EntitySpawn Snapshot { get; internal set; }
    public uint? LocalEntityId { get; internal set; }
    public RuntimeEntityKey? Key => LocalEntityId is { } localId
        ? new RuntimeEntityKey(localId, Incarnation)
        : null;
    public uint FullCellId { get; internal set; }
    public uint CanonicalLandblockId { get; internal set; }
    public uint RawPhysicsState { get; internal set; }
    public PhysicsStateFlags FinalPhysicsState { get; internal set; }
    public ulong PhysicsOwnershipEpoch { get; private set; }
    public ulong SpatialAuthorityVersion { get; private set; }
    public ulong PlacementCommitVersion { get; private set; }
    public ulong PhysicsStateMutationVersion { get; private set; }

    public RetailObjectQuantumClock ObjectClock { get; } = new();

    public ulong ObjectClockEpoch { get; private set; }

    public bool HasPartArray { get; internal set; }
    public PhysicsBody? PhysicsBody { get; private set; }
    public bool PhysicsBodyAcquisitionInProgress { get; internal set; }
    public IRuntimeRemoteMotion? RemoteMotion { get; internal set; }
    public bool RemoteMotionBindingInProgress { get; internal set; }
    public IRuntimeProjectile? Projectile { get; internal set; }
    public bool ProjectileBindingInProgress { get; internal set; }
    public bool RequiresRemotePlacementRuntime { get; internal set; }
    public AcDream.Core.Physics.Motion.IPhysicsObjHost? PhysicsHost { get; internal set; }
    public ulong PositionAuthorityVersion { get; private set; }
    public ulong StateAuthorityVersion { get; private set; }
    public ulong VectorAuthorityVersion { get; private set; }
    public ulong VelocityAuthorityVersion { get; private set; }
    public ulong MovementAuthorityVersion { get; private set; }
    public ulong MovementCommitVersion { get; private set; } = 1UL;
    public ulong ParentCommitVersion { get; private set; }
    public ulong ObjDescAuthorityVersion { get; private set; }
    public ulong CreateIntegrationVersion { get; private set; } = 1UL;
    public bool DeleteAcceptedForTeardown { get; internal set; }

    internal bool TryDequeueStateTransition(out RetailPhysicsStateTransition transition) =>
        _pendingStateTransitions.TryDequeue(out transition);

    internal void SuspendObjectClock()
    {
        ObjectClock.Deactivate();
        ObjectClockEpoch++;
    }

    internal void ResumeObjectClock()
    {
        if (ObjectClock.Activate())
            ObjectClockEpoch++;
    }

    internal void ResetObjectClockForEnterWorld(bool isStatic)
    {
        ObjectClock.ResetForEnterWorld(isStatic);
        ObjectClockEpoch++;
    }

    internal RetailPhysicsStateTransition ApplyRawPhysicsState(uint rawState)
    {
        StateAuthorityVersion++;
        PhysicsStateMutationVersion++;
        RawPhysicsState = rawState;
        RetailPhysicsStateTransition transition = RetailPhysicsStateTransitions.Apply(
            FinalPhysicsState,
            (PhysicsStateFlags)rawState);
        FinalPhysicsState = transition.FinalState;
        if (PhysicsBody is not null)
            PhysicsBody.State = FinalPhysicsState;
        _pendingStateTransitions.Enqueue(transition);
        return transition;
    }

    internal void AdvancePositionAuthority()
    {
        PositionAuthorityVersion++;
        VelocityAuthorityVersion++;
    }

    internal void AdvanceVectorAuthority()
    {
        VectorAuthorityVersion++;
        VelocityAuthorityVersion++;
    }

    internal void AdvanceMovementAuthority()
    {
        MovementAuthorityVersion++;
        VelocityAuthorityVersion++;
    }

    internal void AdvanceMovementCommit() => MovementCommitVersion++;

    internal void AdvancePlacementCommit() => PlacementCommitVersion++;

    internal void AdvanceParentCommit() => ParentCommitVersion++;

    internal void AdvanceObjDescAuthority() => ObjDescAuthorityVersion++;

    internal void AdvanceCreateAuthority()
    {
        PositionAuthorityVersion++;
        StateAuthorityVersion++;
        VectorAuthorityVersion++;
        VelocityAuthorityVersion++;
        MovementAuthorityVersion++;
        ObjDescAuthorityVersion++;
        CreateIntegrationVersion++;
    }

    internal void SetChildNoDraw(bool noDraw)
    {
        PhysicsStateMutationVersion++;
        FinalPhysicsState = noDraw
            ? FinalPhysicsState | PhysicsStateFlags.NoDraw
            : FinalPhysicsState & ~PhysicsStateFlags.NoDraw;
        if (PhysicsBody is not null)
            PhysicsBody.State = FinalPhysicsState;
    }

    internal void SetFinalPhysicsState(PhysicsStateFlags state)
    {
        if (state == FinalPhysicsState)
            return;
        PhysicsStateMutationVersion++;
        FinalPhysicsState = state;
    }

    internal void SetPhysicsBody(PhysicsBody? body)
    {
        if (ReferenceEquals(PhysicsBody, body))
            return;
        PhysicsBody = body;
        PhysicsOwnershipEpoch++;
    }

    internal bool StopMissileAfterCollision(bool requireCurrentMissile)
    {
        const PhysicsStateFlags stopped = PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped;
        if (requireCurrentMissile
            && (FinalPhysicsState & PhysicsStateFlags.Missile) == 0)
            return false;
        PhysicsStateFlags final = FinalPhysicsState & ~stopped;
        if (final == FinalPhysicsState)
            return false;
        PhysicsStateMutationVersion++;
        FinalPhysicsState = final;
        if (PhysicsBody is not null)
            PhysicsBody.State = FinalPhysicsState;
        return true;
    }

    internal void RefreshDerivedState(bool refreshPosition = true)
    {
        if (refreshPosition && Snapshot.Position is { } position)
        {
            SetFullCell(
                position.LandblockId,
                (position.LandblockId & 0xFFFF0000u) | 0xFFFFu);
        }

        RawPhysicsState = Snapshot.Physics?.RawState
            ?? Snapshot.PhysicsState
            ?? 0u;
    }

    internal void SetFullCell(
        uint fullCellId,
        uint canonicalLandblockId)
    {
        SpatialAuthorityVersion++;
        FullCellId = fullCellId;
        CanonicalLandblockId = canonicalLandblockId;
    }
}
