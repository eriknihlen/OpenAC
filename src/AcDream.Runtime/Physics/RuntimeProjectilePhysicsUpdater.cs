using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

internal sealed class RuntimeProjectilePhysicsCommit
{
    internal required RuntimeProjectilePhysicsUpdater Owner { get; init; }
    internal required RuntimeEntityRecord Record { get; init; }
    internal required RuntimeProjectile Projectile { get; init; }
    internal required ProjectileQuantumPreparation Preparation { get; init; }
    internal required ulong PredictionAuthorityVersion { get; init; }
    internal required ulong ObjectClockEpoch { get; init; }
    internal required Func<bool>? ExternalOwnerValid { get; init; }
    internal bool Completed { get; set; }
}

internal sealed class RuntimeProjectilePhysicsUpdater
{
    private readonly RuntimePhysicsState _physics;
    private readonly ProjectilePhysicsStepper _stepper;

    internal RuntimeProjectilePhysicsUpdater(RuntimePhysicsState physics)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _stepper = new ProjectilePhysicsStepper(physics.Engine);
    }

    internal bool TryBegin(
        RuntimeEntityRecord record,
        float quantum,
        ulong objectClockEpoch,
        Func<bool>? externalOwnerValid,
        out RuntimeProjectilePhysicsCommit commit)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Projectile is not RuntimeProjectile projectile
            || !IsSpatialCurrent(
                record,
                projectile,
                projectile.PredictionAuthorityVersion,
                objectClockEpoch,
                externalOwnerValid)
            || (record.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0
            || record.FullCellId == 0)
        {
            commit = null!;
            return false;
        }

        projectile.Body.State = record.FinalPhysicsState;
        bool isParented = record.Snapshot.ParentGuid is not null
            || record.Snapshot.Physics?.Parent is not null;
        ProjectileQuantumPreparation preparation = _stepper.BeginQuantum(
            projectile.Body,
            quantum,
            record.FullCellId,
            projectile.CollisionSphere,
            isParented);
        if (!preparation.Simulated
            || !IsSpatialCurrent(
                record,
                projectile,
                projectile.PredictionAuthorityVersion,
                objectClockEpoch,
                externalOwnerValid))
        {
            commit = null!;
            return false;
        }

        commit = new RuntimeProjectilePhysicsCommit
        {
            Owner = this,
            Record = record,
            Projectile = projectile,
            Preparation = preparation,
            PredictionAuthorityVersion =
                projectile.PredictionAuthorityVersion,
            ObjectClockEpoch = objectClockEpoch,
            ExternalOwnerValid = externalOwnerValid,
        };
        return true;
    }

    internal bool Complete(
        RuntimeProjectilePhysicsCommit commit,
        int liveCenterX,
        int liveCenterY,
        Func<RuntimePhysicsFrameSnapshot, bool> acknowledgeProjection)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(acknowledgeProjection);
        if (!ReferenceEquals(commit.Owner, this))
        {
            throw new InvalidOperationException(
                "A projectile-physics commit belongs to another Runtime owner.");
        }
        if (commit.Completed)
        {
            throw new InvalidOperationException(
                "A projectile-physics commit has already completed.");
        }
        commit.Completed = true;

        if (!IsSpatialCurrent(
                commit.Record,
                commit.Projectile,
                commit.PredictionAuthorityVersion,
                commit.ObjectClockEpoch,
                commit.ExternalOwnerValid))
        {
            return false;
        }

        PhysicsBody body = commit.Projectile.Body;
        ProjectileAdvanceResult result = _stepper.CompleteQuantum(
            body,
            commit.Preparation,
            commit.Projectile.CollisionSphere,
            commit.Record.LocalEntityId ?? 0u,
            designatedTargetId: 0u);
        if (!result.Simulated
            || !IsSpatialCurrent(
                commit.Record,
                commit.Projectile,
                commit.PredictionAuthorityVersion,
                commit.ObjectClockEpoch,
                commit.ExternalOwnerValid))
        {
            return false;
        }

        uint resolvedCellId = result.CellId != 0
            ? result.CellId
            : commit.Record.FullCellId;
        body.SnapToCell(
            resolvedCellId,
            body.Position,
            CellLocalFromWorld(
                body.Position,
                resolvedCellId,
                liveCenterX,
                liveCenterY));

        var snapshot = new RuntimePhysicsFrameSnapshot(
            body.Position,
            body.Orientation,
            resolvedCellId);
        if (!_physics.CommitProjectileCell(
                commit.Record,
                commit.Projectile,
                commit.PredictionAuthorityVersion,
                resolvedCellId,
                commit.ExternalOwnerValid)
            || !IsIdentityCurrent(
                commit.Record,
                commit.Projectile,
                commit.PredictionAuthorityVersion,
                commit.ExternalOwnerValid)
            || !acknowledgeProjection(snapshot)
            || !IsIdentityCurrent(
                commit.Record,
                commit.Projectile,
                commit.PredictionAuthorityVersion,
                commit.ExternalOwnerValid))
        {
            return false;
        }

        if (_physics.IsSpatialProjectile(
                commit.Record,
                commit.Projectile)
            && (commit.Record.FinalPhysicsState
                & PhysicsStateFlags.Hidden) == 0)
        {
            ShadowPositionSynchronizer.Sync(
                _physics.Engine.ShadowObjects,
                commit.Record.LocalEntityId ?? 0u,
                body.Position,
                body.Orientation,
                commit.Record.FullCellId,
                liveCenterX,
                liveCenterY);
        }
        else
        {
            _physics.Engine.ShadowObjects.Suspend(
                commit.Record.LocalEntityId ?? 0u);
        }

        return IsIdentityCurrent(
            commit.Record,
            commit.Projectile,
            commit.PredictionAuthorityVersion,
            commit.ExternalOwnerValid);
    }

    internal bool ApplyAuthoritativeVector(
        RuntimeEntityRecord record,
        ulong expectedVectorAuthorityVersion,
        ulong expectedVelocityAuthorityVersion,
        Vector3 velocity,
        Vector3 angularVelocity,
        double currentTime,
        Func<bool>? externalOwnerValid = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!TryGetCurrent(
                record,
                externalOwnerValid,
                out RuntimeProjectile projectile)
            || record.VectorAuthorityVersion
                != expectedVectorAuthorityVersion
            || record.VelocityAuthorityVersion
                != expectedVelocityAuthorityVersion)
        {
            return false;
        }

        if ((record.FinalPhysicsState & PhysicsStateFlags.Missile) == 0
            && record.RemoteMotion is not null)
        {
            return false;
        }
        if (!IsFinite(velocity)
            || !IsFinite(angularVelocity)
            || !double.IsFinite(currentTime))
        {
            return true;
        }

        projectile.InvalidatePrediction();
        _ = _physics.TryCommitAuthoritativeVector(
            record,
            projectile.Body,
            velocity,
            angularVelocity,
            currentTime,
            externalOwnerValid);
        return true;
    }

    internal bool ApplyAuthoritativeState(
        RuntimeEntityRecord record,
        ulong expectedStateAuthorityVersion,
        PhysicsStateFlags state,
        double effectiveClock,
        int liveCenterX,
        int liveCenterY,
        Func<bool>? externalOwnerValid = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!TryGetCurrent(
                record,
                externalOwnerValid,
                out RuntimeProjectile projectile)
            || record.StateAuthorityVersion
                != expectedStateAuthorityVersion)
        {
            return false;
        }

        projectile.InvalidatePrediction();
        PhysicsBody body = projectile.Body;
        if (!double.IsFinite(effectiveClock))
        {
            effectiveClock = double.IsFinite(body.LastUpdateTime)
                ? body.LastUpdateTime
                : 0d;
        }
        bool wasMissile =
            (body.State & PhysicsStateFlags.Missile) != 0;
        body.State = state;
        if ((state & PhysicsStateFlags.Missile) != 0 && !wasMissile)
        {
            body.LastUpdateTime = effectiveClock;
            if (record.FullCellId != 0)
            {
                body.SnapToCell(
                    record.FullCellId,
                    body.Position,
                    CellLocalFromWorld(
                        body.Position,
                        record.FullCellId,
                        liveCenterX,
                        liveCenterY));
            }
        }
        return true;
    }


    private bool IsSpatialCurrent(
        RuntimeEntityRecord record,
        RuntimeProjectile projectile,
        ulong predictionAuthorityVersion,
        ulong objectClockEpoch,
        Func<bool>? externalOwnerValid) =>
        _physics.IsSpatialProjectile(record, projectile)
        && record.ObjectClockEpoch == objectClockEpoch
        && IsIdentityCurrent(
            record,
            projectile,
            predictionAuthorityVersion,
            externalOwnerValid);

    private bool IsIdentityCurrent(
        RuntimeEntityRecord record,
        RuntimeProjectile projectile,
        ulong predictionAuthorityVersion,
        Func<bool>? externalOwnerValid) =>
        _physics.Entities.IsCurrent(record)
        && ReferenceEquals(record.Projectile, projectile)
        && ReferenceEquals(record.PhysicsBody, projectile.Body)
        && projectile.PredictionAuthorityVersion
            == predictionAuthorityVersion
        && (externalOwnerValid?.Invoke() ?? true);

    private bool TryGetCurrent(
        RuntimeEntityRecord record,
        Func<bool>? externalOwnerValid,
        out RuntimeProjectile projectile)
    {
        if (_physics.Entities.IsCurrent(record)
            && record.Projectile is RuntimeProjectile current
            && ReferenceEquals(record.PhysicsBody, current.Body)
            && (externalOwnerValid?.Invoke() ?? true))
        {
            projectile = current;
            return true;
        }

        projectile = null!;
        return false;
    }

    private static Vector3 CellLocalFromWorld(
        Vector3 worldPosition,
        uint cellId,
        int liveCenterX,
        int liveCenterY)
    {
        int landblockX = (int)((cellId >> 24) & 0xFFu);
        int landblockY = (int)((cellId >> 16) & 0xFFu);
        return worldPosition - new Vector3(
            (landblockX - liveCenterX) * 192f,
            (landblockY - liveCenterY) * 192f,
            0f);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);
}
