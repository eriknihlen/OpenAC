using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Physics;

internal readonly record struct RuntimePhysicsFrameSnapshot(
    Vector3 Position,
    Quaternion Orientation,
    uint FullCellId);

internal sealed class RuntimeOrdinaryPhysicsCommit
{
    internal required RuntimeOrdinaryPhysicsUpdater Owner { get; init; }
    internal required RuntimeEntityRecord Record { get; init; }
    internal required PhysicsBody Body { get; init; }
    internal required ulong ObjectClockEpoch { get; init; }
    internal required bool FrameChanged { get; init; }
    internal required Func<bool>? ExternalOwnerValid { get; init; }
    internal bool Completed { get; set; }
    internal RuntimePhysicsFrameSnapshot Snapshot { get; init; }
}

internal sealed class RuntimeOrdinaryPhysicsUpdater
{
    private readonly RuntimePhysicsState _physics;

    internal RuntimeOrdinaryPhysicsUpdater(RuntimePhysicsState physics)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
    }

    internal bool TryBegin(
        RuntimeEntityRecord record,
        Frame rootFrame,
        float objectScale,
        float quantum,
        float radius,
        float height,
        ulong objectClockEpoch,
        AnimationSequencer? sequencer,
        Action<uint, AnimationSequencer> captureAnimationHooks,
        Func<bool>? externalOwnerValid,
        out RuntimeOrdinaryPhysicsCommit commit,
        System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>
            sphereList = default,
        float sphereScale = 1f,
        float stepUpHeight = 0.4f,
        float stepDownHeight = 0.4f,
        ObjectInfoState moverPvpState = ObjectInfoState.None)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rootFrame);
        ArgumentNullException.ThrowIfNull(captureAnimationHooks);
        if (record.PhysicsBody is not { } body
            || !IsCurrent(
                record,
                body,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            return false;
        }

        body.State = record.FinalPhysicsState;
        Vector3 priorPosition = body.Position;
        Quaternion priorOrientation = body.Orientation;
        bool previousContact = body.InContact;
        bool previousOnWalkable = body.OnWalkable;

        Vector3 candidatePosition = priorPosition;
        if (body.OnWalkable && rootFrame.Origin != Vector3.Zero)
        {
            candidatePosition += Vector3.Transform(
                rootFrame.Origin * objectScale,
                priorOrientation);
        }

        Quaternion candidateOrientation = priorOrientation;
        if (!rootFrame.Orientation.IsIdentity)
        {
            candidateOrientation = FrameOps.SetRotate(
                candidatePosition,
                priorOrientation,
                priorOrientation * rootFrame.Orientation);
        }

        body.SetFrameInCurrentCell(candidatePosition, candidateOrientation);
        body.calc_acceleration();
        body.UpdatePhysicsInternal(quantum);
        body.SetFrameInCurrentCell(body.Position, body.Orientation);

        if (sequencer is not null)
        {
            uint localId = record.LocalEntityId
                ?? throw new InvalidOperationException(
                    $"Runtime entity 0x{record.ServerGuid:X8}/{record.Incarnation} has no local identity.");
            captureAnimationHooks(localId, sequencer);
        }
        if (!IsCurrent(
                record,
                body,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            return false;
        }

        Vector3 integratedPosition = body.Position;
        uint sourceCellId = record.FullCellId;
        uint resolvedCellId = sourceCellId;
        uint movingEntityId = record.LocalEntityId ?? 0u;
        bool hasSweepShape = !sphereList.IsDefaultOrEmpty || radius >= 0.05f;

        // An object only moves through a sweep of its collision shape. With
        // nothing to sweep, or when the sweep is refused, it keeps its
        // origin and only its orientation advances; its velocity keeps
        // integrating, so it stays put rather than sinking through the
        // world (the sign hung on a wall carries no collision spheres).
        void HoldOrigin(bool deactivateOnWalkable)
        {
            body.SetFrameInCurrentCell(priorPosition, body.Orientation);
            body.CachedVelocity = Vector3.Zero;
            if (deactivateOnWalkable && body.OnWalkable)
                body.TransientState &= ~TransientStateFlags.Active;
        }

        if (integratedPosition != priorPosition
            && sourceCellId != 0
            && hasSweepShape
            && _physics.Engine.LandblockCount > 0)
        {
            ResolveResult resolved = _physics.Engine.ResolveWithTransition(
                priorPosition,
                integratedPosition,
                sourceCellId,
                radius,
                height,
                stepUpHeight: stepUpHeight,
                stepDownHeight: stepDownHeight,
                isOnGround: previousOnWalkable,
                body: body,
                moverFlags: (IsPlayerGuid(record.ServerGuid)
                    ? ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide
                    : ObjectInfoState.EdgeSlide)
                    | moverPvpState,
                movingEntityId: movingEntityId,
                sphereList: sphereList,
                sphereScale: sphereScale);

            if (resolved.Ok)
            {
                resolvedCellId = resolved.CellId != 0
                    ? resolved.CellId
                    : sourceCellId;
                body.CommitTransitionPosition(
                    resolvedCellId,
                    resolved.Position);
                PhysicsObjUpdate.CommitSetPositionTransition(
                    body,
                    resolved.InContact,
                    resolved.OnWalkable,
                    resolved.CollisionNormalValid,
                    resolved.CollisionNormal,
                    previousContact,
                    previousOnWalkable);
                body.CachedVelocity = quantum > 0f
                    ? (body.Position - priorPosition) / quantum
                    : Vector3.Zero;
            }
            else
            {
                HoldOrigin(deactivateOnWalkable: false);
            }
        }
        else if (integratedPosition != priorPosition && !hasSweepShape)
        {
            HoldOrigin(deactivateOnWalkable: true);
        }
        else
        {
            body.CachedVelocity = Vector3.Zero;
        }
        bool frameChanged = body.Position != priorPosition
            || body.Orientation != priorOrientation;

        if (!IsCurrent(
                record,
                body,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            return false;
        }

        commit = new RuntimeOrdinaryPhysicsCommit
        {
            Owner = this,
            Record = record,
            Body = body,
            ObjectClockEpoch = objectClockEpoch,
            FrameChanged = frameChanged,
            ExternalOwnerValid = externalOwnerValid,
            Snapshot = new RuntimePhysicsFrameSnapshot(
                body.Position,
                body.Orientation,
                resolvedCellId),
        };
        return true;
    }

    internal bool Complete(
        RuntimeOrdinaryPhysicsCommit commit,
        int liveCenterX,
        int liveCenterY,
        Func<RuntimePhysicsFrameSnapshot, bool> acknowledgeProjection)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(acknowledgeProjection);
        if (!ReferenceEquals(commit.Owner, this))
        {
            throw new InvalidOperationException(
                "An ordinary-physics commit belongs to another Runtime owner.");
        }
        if (commit.Completed)
        {
            throw new InvalidOperationException(
                "An ordinary-physics commit has already completed.");
        }
        commit.Completed = true;

        if (!IsCurrent(
                commit.Record,
                commit.Body,
                commit.ObjectClockEpoch,
                commit.ExternalOwnerValid)
            || !_physics.CommitOrdinaryCell(
                commit.Record,
                commit.Body,
                commit.ObjectClockEpoch,
                commit.Snapshot.FullCellId,
                commit.ExternalOwnerValid)
            || !acknowledgeProjection(commit.Snapshot)
            || !IsCurrent(
                commit.Record,
                commit.Body,
                commit.ObjectClockEpoch,
                commit.ExternalOwnerValid))
        {
            return false;
        }

        if (commit.FrameChanged
            && commit.Record.FullCellId != 0)
        {
            ShadowPositionSynchronizer.Sync(
                _physics.Engine.ShadowObjects,
                commit.Record.LocalEntityId ?? 0u,
                commit.Body.Position,
                commit.Body.Orientation,
                commit.Record.FullCellId,
                liveCenterX,
                liveCenterY);
        }

        return IsCurrent(
            commit.Record,
            commit.Body,
            commit.ObjectClockEpoch,
            commit.ExternalOwnerValid);
    }

    private bool IsCurrent(
        RuntimeEntityRecord record,
        PhysicsBody body,
        ulong objectClockEpoch,
        Func<bool>? externalOwnerValid) =>
        _physics.IsSpatialRoot(record)
        && record.ObjectClockEpoch == objectClockEpoch
        && ReferenceEquals(record.PhysicsBody, body)
        && record.RemoteMotion is null
        && (externalOwnerValid?.Invoke() ?? true);

    private static bool IsPlayerGuid(uint guid) =>
        (guid & 0xFF000000u) == 0x50000000u;
}
