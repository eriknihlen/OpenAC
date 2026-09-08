using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

public sealed class InboundPhysicsStateController
{
    private readonly Dictionary<uint, PhysicsTimestampGate> _gates = new();
    private readonly Dictionary<uint, WorldSession.EntitySpawn> _snapshots = new();

    public IReadOnlyDictionary<uint, WorldSession.EntitySpawn> Snapshots => _snapshots;

    public bool TryGetSnapshot(uint guid, out WorldSession.EntitySpawn spawn) =>
        _snapshots.TryGetValue(guid, out spawn);

    internal bool TryGetAcceptedTimestamps(
        uint guid,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (_gates.TryGetValue(guid, out PhysicsTimestampGate? gate))
        {
            timestamps = Current(gate);
            return true;
        }
        timestamps = default;
        return false;
    }

    public CreateObjectTimestampDisposition PreviewCreateDisposition(
        WorldSession.EntitySpawn incoming) =>
        _gates.TryGetValue(incoming.Guid, out PhysicsTimestampGate? gate)
            ? gate.PreviewCreateObject(incoming.InstanceSequence)
            : CreateObjectTimestampDisposition.InitialGeneration;

    public InboundCreateResult AcceptCreate(WorldSession.EntitySpawn incoming) =>
        AcceptCreateCore(incoming, deferSameGenerationWeenieDescription: false);

    internal InboundCreateResult AcceptCreateDeferredSameGeneration(
        WorldSession.EntitySpawn incoming) =>
        AcceptCreateCore(incoming, deferSameGenerationWeenieDescription: true);

    private InboundCreateResult AcceptCreateCore(
        WorldSession.EntitySpawn incoming,
        bool deferSameGenerationWeenieDescription)
    {
        if (!_gates.TryGetValue(incoming.Guid, out PhysicsTimestampGate? gate))
        {
            gate = new PhysicsTimestampGate();
            _gates.Add(incoming.Guid, gate);
        }

        PhysicsTimestamps timestamps = SpawnTimestamps(incoming);
        CreateObjectTimestampDisposition disposition = gate.SeedForCreateObject(
            timestamps.Position,
            timestamps.Movement,
            timestamps.State,
            timestamps.Vector,
            timestamps.Teleport,
            timestamps.ServerControlledMove,
            timestamps.ForcePosition,
            timestamps.ObjDesc,
            timestamps.Instance);

        if (disposition is CreateObjectTimestampDisposition.StaleGeneration)
            return new InboundCreateResult(disposition, default, null, Current(gate));

        if (disposition is not CreateObjectTimestampDisposition.ExistingGeneration
            || !_snapshots.TryGetValue(incoming.Guid, out WorldSession.EntitySpawn retained))
        {
            _snapshots[incoming.Guid] = incoming;
            return new InboundCreateResult(disposition, incoming, null, Current(gate));
        }

        WorldSession.EntitySpawn merged = deferSameGenerationWeenieDescription
            ? retained
            : MergeUntimestampedCreate(retained, incoming);
        if (!deferSameGenerationWeenieDescription)
            _snapshots[incoming.Guid] = merged;
        return new InboundCreateResult(
            disposition,
            merged,
            incoming.Physics is null ? null : BuildSameGenerationEvents(incoming),
            Current(gate));
    }

    public bool TryDelete(DeleteObject.Parsed delete, bool isLocalPlayer)
    {
        if (!_gates.TryGetValue(delete.Guid, out PhysicsTimestampGate? gate))
            return false;

        if (!gate.TryAcceptDeleteEvent(delete.InstanceSequence, isLocalPlayer))
            return false;

        _gates.Remove(delete.Guid);
        _snapshots.Remove(delete.Guid);
        return true;
    }

    public bool TryApplyObjDesc(
        ObjDescEvent.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn old)
            || !gate.TryAcceptObjDescEvent(update.InstanceSequence, update.ObjDescSequence))
        {
            accepted = default;
            return false;
        }

        accepted = ApplyAcceptedObjDesc(old, update);
        _snapshots[update.Guid] = accepted;
        return true;
    }

    internal bool ApplyAcceptedObjDescSnapshot(
        uint guid,
        ObjDescEvent.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedObjDesc(old, update);
        _snapshots[guid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedObjDesc(
        WorldSession.EntitySpawn old,
        ObjDescEvent.Parsed update)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Timestamps = desc.Timestamps with { ObjDesc = update.ObjDescSequence },
            };

        return old with
        {
            AnimPartChanges = update.ModelData.AnimPartChanges,
            TextureChanges = update.ModelData.TextureChanges,
            SubPalettes = update.ModelData.SubPalettes,
            BasePaletteId = update.ModelData.BasePaletteId,
            Physics = physics,
        };
    }

    public bool TryApplyPickup(
        PickupEvent.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn old)
            || !gate.TryAcceptPositionChannelEvent(
                update.InstanceSequence, update.PositionSequence))
        {
            accepted = default;
            return false;
        }

        accepted = ApplyAcceptedPickup(old, update);
        _snapshots[update.Guid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedPickup(
        WorldSession.EntitySpawn old,
        PickupEvent.Parsed update) =>
        ApplyUnparentedPosition(old, null, update.PositionSequence);

    internal bool ApplyAcceptedPickupSnapshot(
        uint guid,
        PickupEvent.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedPickup(old, update);
        _snapshots[guid] = accepted;
        return true;
    }

    public bool TryApplyCreateParent(
        CreateParentUpdate update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!TryGet(update.ChildGuid, out PhysicsTimestampGate? childGate, out WorldSession.EntitySpawn child)
            || !childGate.TryAcceptPositionChannelEvent(
                update.ChildInstanceSequence, update.ChildPositionSequence))
        {
            accepted = default;
            return false;
        }

        accepted = ApplyAcceptedCreateParent(child, update);
        _snapshots[update.ChildGuid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedCreateParent(
        WorldSession.EntitySpawn child,
        CreateParentUpdate update) =>
        ApplyPositionTimestampOnly(child, update.ChildPositionSequence);

    internal bool ApplyAcceptedCreateParentSnapshot(
        uint guid,
        CreateParentUpdate update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedCreateParent(old, update);
        _snapshots[guid] = accepted;
        return true;
    }

    public bool TryApplyParent(
        ParentEvent.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_gates.TryGetValue(update.ParentGuid, out PhysicsTimestampGate? parentGate)
            || !parentGate.IsCurrentInstance(update.ParentInstanceSequence)
            || !TryGet(update.ChildGuid, out PhysicsTimestampGate? childGate, out WorldSession.EntitySpawn child)
            || !childGate.TryAcceptPositionChannelEvent(
                childGate.InstanceTimestamp, update.ChildPositionSequence))
        {
            accepted = default;
            return false;
        }

        accepted = ApplyAcceptedParent(child, update);
        _snapshots[update.ChildGuid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedParent(
        WorldSession.EntitySpawn child,
        ParentEvent.Parsed update) =>
        ApplyPositionTimestampOnly(child, update.ChildPositionSequence);

    internal bool ApplyAcceptedParentSnapshot(
        uint guid,
        ParentEvent.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedParent(old, update);
        _snapshots[guid] = accepted;
        return true;
    }

    public bool TryCommitParent(
        uint childGuid,
        uint parentGuid,
        uint parentLocation,
        uint placementId,
        ushort positionSequence,
        out WorldSession.EntitySpawn accepted)
    {
        if (!TryGet(childGuid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn child)
            || gate.PositionTimestamp != positionSequence
            || child.PositionSequence != positionSequence)
        {
            accepted = default;
            return false;
        }

        accepted = ApplyParent(
            child,
            parentGuid,
            parentLocation,
            placementId,
            positionSequence);
        _snapshots[childGuid] = accepted;
        return true;
    }

    public bool TryApplyMotion(
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            timestamps = default;
            return false;
        }

        bool applyPayload = gate.TryAcceptMovementEvent(
            update.InstanceSequence,
            update.MovementSequence,
            update.ServerControlSequence);
        timestamps = Current(gate);

        WorldSession.EntitySpawn stamped = ApplyAcceptedMotion(
            old,
            gate.MovementTimestamp,
            gate.ServerControlledMoveTimestamp,
            update,
            retainPayload: false);
        _snapshots[update.Guid] = stamped;

        if (!applyPayload)
        {
            accepted = default;
            return false;
        }

        if (!retainPayload)
        {
            accepted = stamped;
            return true;
        }

        accepted = ApplyAcceptedMotion(
            stamped,
            gate.MovementTimestamp,
            gate.ServerControlledMoveTimestamp,
            update,
            retainPayload: true);
        _snapshots[update.Guid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedMotion(
        WorldSession.EntitySpawn old,
        ushort movementSequence,
        ushort acceptedServerControlledMove,
        WorldSession.EntityMotionUpdate update,
        bool retainPayload)
    {
        PhysicsSpawnData? stampedPhysics = old.Physics;
        if (stampedPhysics is { } stampedDesc)
            stampedPhysics = stampedDesc with
            {
                Timestamps = stampedDesc.Timestamps with
                {
                    Movement = movementSequence,
                    ServerControlledMove = acceptedServerControlledMove,
                },
            };
        WorldSession.EntitySpawn stamped = old with
        {
            MovementSequence = movementSequence,
            ServerControlSequence = acceptedServerControlledMove,
            Physics = stampedPhysics,
        };
        if (!retainPayload)
            return stamped;

        PhysicsSpawnData? physics = stamped.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Movement = new PhysicsMovementData(
                    ReadOnlyMemory<byte>.Empty,
                    update.MotionState,
                    update.IsAutonomous),
            };

        return stamped with
        {
            MotionState = update.MotionState,
            Physics = physics,
        };
    }

    internal bool ApplyAcceptedMotionSnapshot(
        uint guid,
        ushort movementSequence,
        ushort acceptedServerControlledMove,
        WorldSession.EntityMotionUpdate update,
        bool retainPayload,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedMotion(
            old,
            movementSequence,
            acceptedServerControlledMove,
            update,
            retainPayload);
        _snapshots[guid] = accepted;
        return true;
    }

    public bool TryApplyVector(
        VectorUpdate.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn old)
            || !gate.TryAcceptVectorEvent(update.InstanceSequence, update.VectorSequence))
        {
            accepted = default;
            return false;
        }

        accepted = ApplyAcceptedVector(old, update);
        _snapshots[update.Guid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedVector(
        WorldSession.EntitySpawn old,
        VectorUpdate.Parsed update)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Velocity = update.Velocity,
                AngularVelocity = update.Omega,
                Timestamps = desc.Timestamps with { Vector = update.VectorSequence },
            };

        return old with { Physics = physics };
    }

    internal bool ApplyAcceptedVectorSnapshot(
        uint guid,
        VectorUpdate.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedVector(old, update);
        _snapshots[guid] = accepted;
        return true;
    }

    public bool TryApplyState(
        SetState.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn old)
            || !gate.TryAcceptStateEvent(update.InstanceSequence, update.StateSequence))
        {
            accepted = default;
            return false;
        }

        accepted = ApplyAcceptedState(old, update);
        _snapshots[update.Guid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedState(
        WorldSession.EntitySpawn old,
        SetState.Parsed update)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                RawState = update.PhysicsState,
                Timestamps = desc.Timestamps with { State = update.StateSequence },
            };

        return old with
        {
            PhysicsState = update.PhysicsState,
            Physics = physics,
        };
    }

    internal bool ApplyAcceptedStateSnapshot(
        uint guid,
        SetState.Parsed update,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedState(old, update);
        _snapshots[guid] = accepted;
        return true;
    }

    public bool TryApplyPosition(
        WorldSession.EntityPositionUpdate update,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        out PositionTimestampDisposition disposition,
        out WorldSession.EntitySpawn accepted,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out WorldSession.EntitySpawn old))
        {
            disposition = PositionTimestampDisposition.Rejected;
            accepted = default;
            timestamps = default;
            return false;
        }

        ushort previousTeleport = gate.TeleportTimestamp;
        bool advancesTeleport = PhysicsTimestampGate.IsNewer(
            previousTeleport,
            update.TeleportSequence);
        disposition = gate.TryAcceptPositionEvent(
            update.InstanceSequence,
            update.PositionSequence,
            update.TeleportSequence,
            update.ForcePositionSequence,
            isLocalPlayer);
        timestamps = Current(
            gate,
            teleportAdvanced: disposition is PositionTimestampDisposition.Apply
                && advancesTeleport,
            previousTeleport: previousTeleport);
        bool hasAnimations =
            (old.MotionTableId ?? old.Physics?.MotionTableId)
                is { } motionTableId
            && motionTableId != 0u;
        RuntimeAcceptedPositionPrePlacementFlags prePlacement =
            RuntimeAuthoritativePositionRouteClassifier.DerivePrePlacementFlags(
                disposition,
                hasAnimations);
        accepted = ApplyAcceptedPosition(
            old,
            update,
            disposition,
            timestamps,
            isLocalPlayer,
            forcePositionRotation,
            currentLocalVelocity,
            installPlacementFrame: prePlacement.ApplyPlacementFrameBeforeRouting,
            clearParent: prePlacement.UnparentBeforeRouting);
        _snapshots[update.Guid] = accepted;
        return true;
    }

    internal bool ApplyAcceptedPositionSnapshot(
        uint guid,
        WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        AcceptedPhysicsTimestamps timestamps,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        bool installPlacementFrame,
        bool clearParent,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        accepted = ApplyAcceptedPosition(
            old,
            update,
            disposition,
            timestamps,
            isLocalPlayer,
            forcePositionRotation,
            currentLocalVelocity,
            installPlacementFrame,
            clearParent);
        _snapshots[guid] = accepted;
        return true;
    }

    internal bool ApplyAcceptedPositionExecutionRejectedSnapshot(
        uint guid,
        ushort acceptedPositionSequence,
        AcceptedPhysicsTimestamps timestamps,
        out WorldSession.EntitySpawn accepted)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn old))
        {
            accepted = default;
            return false;
        }
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Timestamps = desc.Timestamps with
                {
                    Position = acceptedPositionSequence,
                    Teleport = timestamps.Teleport,
                    ForcePosition = timestamps.ForcePosition,
                },
            };
        accepted = old with
        {
            PositionSequence = acceptedPositionSequence,
            Physics = physics,
        };
        _snapshots[guid] = accepted;
        return true;
    }

    internal static WorldSession.EntitySpawn ApplyAcceptedPosition(
        WorldSession.EntitySpawn old,
        WorldSession.EntityPositionUpdate update,
        PositionTimestampDisposition disposition,
        AcceptedPhysicsTimestamps timestamps,
        bool isLocalPlayer,
        System.Numerics.Quaternion? forcePositionRotation,
        System.Numerics.Vector3? currentLocalVelocity,
        bool installPlacementFrame,
        bool clearParent)
    {
        if (disposition is PositionTimestampDisposition.Rejected)
            return ApplyAcceptedPositionTimestampOnly(old, timestamps);

        CreateObject.ServerPosition appliedPosition = update.Position;
        if (disposition is PositionTimestampDisposition.ForcePosition
            && forcePositionRotation is { } preserved)
        {
            appliedPosition = update.Position with
            {
                RotationW = preserved.W,
                RotationX = preserved.X,
                RotationY = preserved.Y,
                RotationZ = preserved.Z,
            };
        }

        uint? appliedPlacement = installPlacementFrame
            ? (disposition is PositionTimestampDisposition.Apply
                ? update.PlacementId ?? 0u
                : old.PlacementId)
            : old.PlacementId;

        System.Numerics.Vector3? appliedVelocity = disposition switch
        {
            // ForcePosition returns immediately after BlipPlayer and retains
            // the local physics object's velocity.
            PositionTimestampDisposition.ForcePosition =>
                currentLocalVelocity ?? old.Physics?.Velocity,

            PositionTimestampDisposition.Apply when isLocalPlayer =>
                timestamps.TeleportAdvanced
                    ? System.Numerics.Vector3.Zero
                    : currentLocalVelocity ?? old.Physics?.Velocity,

            PositionTimestampDisposition.Apply =>
                update.Velocity ?? System.Numerics.Vector3.Zero,

            _ => old.Physics?.Velocity,
        };
        uint? parentGuid = clearParent ? null : old.ParentGuid;
        uint? parentLocation = clearParent ? null : old.ParentLocation;
        PhysicsAttachment? physicsParent = clearParent ? null : old.Physics?.Parent;
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Position = appliedPosition,
                AnimationFrame = appliedPlacement,
                Velocity = appliedVelocity,
                Parent = physicsParent,
                Timestamps = desc.Timestamps with
                {
                    Position = update.PositionSequence,
                    Teleport = timestamps.Teleport,
                    ForcePosition = timestamps.ForcePosition,
                },
            };

        return old with
        {
            Position = appliedPosition,
            PositionSequence = update.PositionSequence,
            ParentGuid = parentGuid,
            ParentLocation = parentLocation,
            PlacementId = appliedPlacement,
            Physics = physics,
        };
    }

    private static WorldSession.EntitySpawn ApplyAcceptedPositionTimestampOnly(
        WorldSession.EntitySpawn old,
        AcceptedPhysicsTimestamps timestamps)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Timestamps = desc.Timestamps with
                {
                    ForcePosition = timestamps.ForcePosition,
                },
            };
        return old with { Physics = physics };
    }

    internal bool TryAcceptDeferredPosition(
        WorldSession.EntityPositionUpdate update,
        bool isLocalPlayer,
        out PositionTimestampDisposition disposition,
        out AcceptedPhysicsTimestamps timestamps,
        out bool hasTimestampMutation)
    {
        if (!TryGet(
                update.Guid,
                out PhysicsTimestampGate? gate,
                out WorldSession.EntitySpawn old))
        {
            disposition = PositionTimestampDisposition.Rejected;
            timestamps = default;
            hasTimestampMutation = false;
            return false;
        }

        ushort previousPosition = gate.PositionTimestamp;
        ushort previousTeleport = gate.TeleportTimestamp;
        ushort previousForcePosition = gate.ForcePositionTimestamp;
        bool advancesTeleport = PhysicsTimestampGate.IsNewer(
            previousTeleport,
            update.TeleportSequence);
        disposition = gate.TryAcceptPositionEvent(
            update.InstanceSequence,
            update.PositionSequence,
            update.TeleportSequence,
            update.ForcePositionSequence,
            isLocalPlayer);
        timestamps = Current(
            gate,
            teleportAdvanced: disposition is PositionTimestampDisposition.Apply
                && advancesTeleport,
            previousTeleport: previousTeleport);
        hasTimestampMutation = previousPosition != gate.PositionTimestamp
            || previousTeleport != gate.TeleportTimestamp
            || previousForcePosition != gate.ForcePositionTimestamp;
        return true;
    }

    internal bool TryAcceptDeferredObjDesc(
        ObjDescEvent.Parsed update,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out _)
            || !gate.TryAcceptObjDescEvent(
                update.InstanceSequence,
                update.ObjDescSequence))
        {
            timestamps = default;
            return false;
        }
        timestamps = Current(gate);
        return true;
    }

    internal bool TryAcceptDeferredPickup(
        PickupEvent.Parsed update,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out _)
            || !gate.TryAcceptPositionChannelEvent(
                update.InstanceSequence,
                update.PositionSequence))
        {
            timestamps = default;
            return false;
        }
        timestamps = Current(gate);
        return true;
    }

    internal bool TryAcceptDeferredCreateParent(
        CreateParentUpdate update,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.ChildGuid, out PhysicsTimestampGate? gate, out _)
            || !gate.TryAcceptPositionChannelEvent(
                update.ChildInstanceSequence,
                update.ChildPositionSequence))
        {
            timestamps = default;
            return false;
        }
        timestamps = Current(gate);
        return true;
    }

    internal bool TryAcceptDeferredParent(
        ParentEvent.Parsed update,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!_gates.TryGetValue(
                update.ParentGuid,
                out PhysicsTimestampGate? parentGate)
            || !parentGate.IsCurrentInstance(update.ParentInstanceSequence)
            || !TryGet(
                update.ChildGuid,
                out PhysicsTimestampGate? childGate,
                out _)
            || !childGate.TryAcceptPositionChannelEvent(
                childGate.InstanceTimestamp,
                update.ChildPositionSequence))
        {
            timestamps = default;
            return false;
        }
        timestamps = Current(childGate);
        return true;
    }

    internal bool TryAcceptDeferredMotion(
        WorldSession.EntityMotionUpdate update,
        out AcceptedPhysicsTimestamps timestamps,
        out bool hasTimestampMutation)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out _))
        {
            timestamps = default;
            hasTimestampMutation = false;
            return false;
        }

        ushort previousMovement = gate.MovementTimestamp;
        ushort previousServerControl = gate.ServerControlledMoveTimestamp;
        bool accepted = gate.TryAcceptMovementEvent(
            update.InstanceSequence,
            update.MovementSequence,
            update.ServerControlSequence);
        timestamps = Current(gate);
        hasTimestampMutation = previousMovement != gate.MovementTimestamp
            || previousServerControl != gate.ServerControlledMoveTimestamp;
        return accepted;
    }

    internal bool TryAcceptDeferredState(
        SetState.Parsed update,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out _)
            || !gate.TryAcceptStateEvent(
                update.InstanceSequence,
                update.StateSequence))
        {
            timestamps = default;
            return false;
        }
        timestamps = Current(gate);
        return true;
    }

    internal bool TryAcceptDeferredVector(
        VectorUpdate.Parsed update,
        out AcceptedPhysicsTimestamps timestamps)
    {
        if (!TryGet(update.Guid, out PhysicsTimestampGate? gate, out _)
            || !gate.TryAcceptVectorEvent(
                update.InstanceSequence,
                update.VectorSequence))
        {
            timestamps = default;
            return false;
        }
        timestamps = Current(gate);
        return true;
    }

    public bool IsFreshTeleportStart(uint localPlayerGuid, ushort teleportSequence) =>
        _gates.TryGetValue(localPlayerGuid, out PhysicsTimestampGate? gate)
        && gate.IsFreshTeleportStart(teleportSequence);

    public void Clear()
    {
        _gates.Clear();
        _snapshots.Clear();
    }

    private bool TryGet(
        uint guid,
        out PhysicsTimestampGate gate,
        out WorldSession.EntitySpawn spawn)
    {
        if (_gates.TryGetValue(guid, out gate!)
            && _snapshots.TryGetValue(guid, out spawn))
            return true;

        gate = null!;
        spawn = default;
        return false;
    }

    private static PhysicsTimestamps SpawnTimestamps(WorldSession.EntitySpawn spawn) =>
        spawn.Physics?.Timestamps
        ?? new PhysicsTimestamps(
            spawn.PositionSequence,
            spawn.MovementSequence,
            0,
            0,
            0,
            spawn.ServerControlSequence,
            0,
            0,
            spawn.InstanceSequence);

    private static AcceptedPhysicsTimestamps Current(
        PhysicsTimestampGate gate,
        bool teleportAdvanced = false,
        ushort? previousTeleport = null) => new(
        gate.InstanceTimestamp,
        gate.ServerControlledMoveTimestamp,
        gate.TeleportTimestamp,
        gate.ForcePositionTimestamp,
        teleportAdvanced,
        PreviousTeleport: previousTeleport ?? gate.TeleportTimestamp);

    private static WorldSession.EntitySpawn MergeUntimestampedCreate(
        WorldSession.EntitySpawn retained,
        WorldSession.EntitySpawn incoming) =>
        incoming with
        {
            Position = retained.Position,
            SetupTableId = retained.SetupTableId,
            AnimPartChanges = retained.AnimPartChanges,
            TextureChanges = retained.TextureChanges,
            SubPalettes = retained.SubPalettes,
            BasePaletteId = retained.BasePaletteId,
            ObjScale = retained.ObjScale,
            MotionState = retained.MotionState,
            MotionTableId = retained.MotionTableId,
            PhysicsState = retained.PhysicsState,
            Friction = retained.Friction,
            Elasticity = retained.Elasticity,
            InstanceSequence = retained.InstanceSequence,
            MovementSequence = retained.MovementSequence,
            ServerControlSequence = retained.ServerControlSequence,
            PositionSequence = retained.PositionSequence,
            ParentGuid = retained.ParentGuid,
            ParentLocation = retained.ParentLocation,
            PlacementId = retained.PlacementId,
            Physics = retained.Physics,
        };

    internal bool ApplyAcceptedWeenieDescriptionSnapshot(
        uint guid,
        WorldSession.EntitySpawn incoming,
        out WorldSession.EntitySpawn merged)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn retained))
        {
            merged = default;
            return false;
        }
        merged = MergeUntimestampedCreate(retained, incoming);
        _snapshots[guid] = merged;
        return true;
    }

    internal bool TryRefreshObjectDescriptionFlags(
        uint guid,
        uint bitfield,
        out WorldSession.EntitySpawn merged)
    {
        if (!_snapshots.TryGetValue(guid, out WorldSession.EntitySpawn retained))
        {
            merged = default;
            return false;
        }
        if (retained.ObjectDescriptionFlags == bitfield)
        {
            merged = retained;
            return false;
        }
        merged = retained with { ObjectDescriptionFlags = bitfield };
        _snapshots[guid] = merged;
        return true;
    }

    private static SameGenerationCreateObjectEvents BuildSameGenerationEvents(
        WorldSession.EntitySpawn incoming)
    {
        PhysicsSpawnData physics = incoming.Physics!.Value;
        PhysicsTimestamps ts = physics.Timestamps;

        CreateParentUpdate? parent = physics.Parent is { } attachment
            ? new CreateParentUpdate(
                incoming.Guid,
                attachment.Guid,
                attachment.LocationId,
                physics.AnimationFrame ?? 0u,
                ts.Instance,
                ts.Position)
            : null;
        WorldSession.EntityPositionUpdate? position = parent is null
            && physics.Position is { } p
            && p.LandblockId != 0
                ? new WorldSession.EntityPositionUpdate(
                    incoming.Guid,
                    p,
                    physics.Velocity,
                    physics.AnimationFrame,
                    IsGrounded: true,
                    ts.Instance,
                    ts.Position,
                    ts.Teleport,
                    ts.ForcePosition)
                : null;
        PickupEvent.Parsed? pickup = parent is null && position is null
            ? new PickupEvent.Parsed(incoming.Guid, ts.Instance, ts.Position)
            : null;
        WorldSession.EntityMotionUpdate? movement =
            physics.Movement is { } movementData
            && !movementData.RawData.IsEmpty
            && movementData.MotionState is { } motionState
                ? new WorldSession.EntityMotionUpdate(
                    incoming.Guid,
                    motionState,
                    ts.Instance,
                    ts.Movement,
                    ts.ServerControlledMove,
                    movementData.IsAutonomous is true)
                : null;

        return new SameGenerationCreateObjectEvents(
            physics,
            new ObjDescEvent.Parsed(
                incoming.Guid,
                new CreateObject.ModelData(
                    incoming.BasePaletteId,
                    incoming.SubPalettes,
                    incoming.TextureChanges,
                    incoming.AnimPartChanges),
                ts.Instance,
                ts.ObjDesc),
            parent,
            position,
            pickup,
            movement,
            new SetState.Parsed(incoming.Guid, physics.RawState, ts.Instance, ts.State),
            new VectorUpdate.Parsed(
                incoming.Guid,
                physics.Velocity ?? System.Numerics.Vector3.Zero,
                physics.AngularVelocity ?? System.Numerics.Vector3.Zero,
                ts.Instance,
                ts.Vector));
    }

    private static WorldSession.EntitySpawn ApplyUnparentedPosition(
        WorldSession.EntitySpawn old,
        CreateObject.ServerPosition? position,
        ushort positionSequence)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Position = position,
                Parent = null,
                Timestamps = desc.Timestamps with { Position = positionSequence },
            };

        return old with
        {
            Position = position,
            ParentGuid = null,
            ParentLocation = null,
            PositionSequence = positionSequence,
            Physics = physics,
        };
    }

    private static WorldSession.EntitySpawn ApplyPositionTimestampOnly(
        WorldSession.EntitySpawn old,
        ushort positionSequence)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
        {
            physics = desc with
            {
                Timestamps = desc.Timestamps with { Position = positionSequence },
            };
        }
        return old with
        {
            PositionSequence = positionSequence,
            Physics = physics,
        };
    }

    private static WorldSession.EntitySpawn ApplyParent(
        WorldSession.EntitySpawn old,
        uint parentGuid,
        uint parentLocation,
        uint placementId,
        ushort positionSequence)
    {
        PhysicsSpawnData? physics = old.Physics;
        if (physics is { } desc)
            physics = desc with
            {
                Position = null,
                Parent = new PhysicsAttachment(parentGuid, parentLocation),
                AnimationFrame = placementId,
                Timestamps = desc.Timestamps with { Position = positionSequence },
            };

        return old with
        {
            Position = null,
            ParentGuid = parentGuid,
            ParentLocation = parentLocation,
            PlacementId = placementId,
            PositionSequence = positionSequence,
            Physics = physics,
        };
    }
}

public readonly record struct AcceptedPhysicsTimestamps(
    ushort Instance,
    ushort ServerControlledMove,
    ushort Teleport,
    ushort ForcePosition,
    bool TeleportAdvanced = false,
    uint? PreMergeCommittedCellId = null,
    ushort PreviousTeleport = 0);

public readonly record struct CreateParentUpdate(
    uint ChildGuid,
    uint ParentGuid,
    uint ParentLocation,
    uint PlacementId,
    ushort ChildInstanceSequence,
    ushort ChildPositionSequence);

public readonly record struct SameGenerationCreateObjectEvents(
    PhysicsSpawnData Description,
    ObjDescEvent.Parsed Appearance,
    CreateParentUpdate? Parent,
    WorldSession.EntityPositionUpdate? Position,
    PickupEvent.Parsed? Pickup,
    WorldSession.EntityMotionUpdate? Movement,
    SetState.Parsed State,
    VectorUpdate.Parsed Vector);

public readonly record struct InboundCreateResult(
    CreateObjectTimestampDisposition Disposition,
    WorldSession.EntitySpawn Snapshot,
    SameGenerationCreateObjectEvents? SameGenerationEvents,
    AcceptedPhysicsTimestamps Timestamps);
