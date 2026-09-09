using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World.Cells;
using System.Numerics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimePhysicsStateTests
{
    [Fact]
    public void EntityLifetimeConstructsOneCanonicalProductionPhysicsWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();

        Assert.Same(
            lifetime.Physics.DataCache,
            lifetime.Physics.Engine.DataCache);
        Assert.True(lifetime.Physics.CaptureOwnership().OwnsProductionDataCache);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership().LandblockCount);
        Assert.False(lifetime.Physics.CaptureOwnership().IsDisposed);
    }

    [Fact]
    public void ConcurrentRuntimeLifetimesDoNotShareMutablePhysicsState()
    {
        using var first = new RuntimeEntityObjectLifetime();
        using var second = new RuntimeEntityObjectLifetime();

        Assert.NotSame(first.Physics, second.Physics);
        Assert.NotSame(first.Physics.Engine, second.Physics.Engine);
        Assert.NotSame(first.Physics.DataCache, second.Physics.DataCache);
        Assert.NotSame(
            first.Physics.Engine.ShadowObjects,
            second.Physics.Engine.ShadowObjects);
        Assert.NotSame(
            first.Physics.DataCache.CellGraph,
            second.Physics.DataCache.CellGraph);
    }

    [Fact]
    public void PhysicsOwnerDisposesWithItsEntityLifetime()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var physics = lifetime.Physics;

        lifetime.Dispose();

        Assert.True(physics.CaptureOwnership().IsDisposed);
    }

    [Fact]
    public void ResolveObjectTableHostDelegatesToTheBoundResolverAndFallsBackToInstalledHosts()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000001u, 1));
        EntityPhysicsHost installed = MinimalHost(record.ServerGuid);
        lifetime.Physics.InstallPhysicsHost(record, installed);

        const uint uninstalledGuid = 0x7C95B01Cu;
        // Unbound fallback: installed hosts resolve, anything else is null.
        Assert.Same(
            installed,
            lifetime.Physics.ResolveObjectTableHost(record.ServerGuid));
        Assert.Null(lifetime.Physics.ResolveObjectTableHost(uninstalledGuid));

        EntityPhysicsHost lazyMinimal = MinimalHost(uninstalledGuid);
        var resolvedGuids = new List<uint>();
        lifetime.Physics.BindObjectTableHostResolver(guid =>
        {
            resolvedGuids.Add(guid);
            return guid == uninstalledGuid ? lazyMinimal : null;
        });
        Assert.Same(
            lazyMinimal,
            lifetime.Physics.ResolveObjectTableHost(uninstalledGuid));
        Assert.Null(lifetime.Physics.ResolveObjectTableHost(0x70000002u));
        Assert.Equal(
            new[] { uninstalledGuid, 0x70000002u },
            resolvedGuids);

        lifetime.Physics.BindObjectTableHostResolver(null);
        Assert.Null(lifetime.Physics.ResolveObjectTableHost(uninstalledGuid));
        Assert.Same(
            installed,
            lifetime.Physics.ResolveObjectTableHost(record.ServerGuid));

        lifetime.Physics.BindObjectTableHostResolver(_ => lazyMinimal);
        lifetime.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => lifetime.Physics.ResolveObjectTableHost(uninstalledGuid));
    }

    private static EntityPhysicsHost MinimalHost(uint guid) => new(
        guid,
        getPosition: static () => new AcDream.Core.Physics.Position(
            0u, Vector3.Zero, Quaternion.Identity),
        getVelocity: static () => Vector3.Zero,
        getRadius: static () => 0f,
        inContact: static () => true,
        minterpMaxSpeed: static () => null,
        curTime: static () => 0d,
        physicsTimerTime: static () => 0d,
        getObjectA: static _ => null,
        handleUpdateTarget: static _ => { },
        interruptCurrentMovement: static () => { });

    [Fact]
    public void CanonicalRecordAndPhysicsOwnerOwnRemoteComponentAndWorksets()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000001u, 1));
        var remote = new TestRemoteMotion();
        lifetime.Entities.SetPhysicsBody(record, remote.Body);
        lifetime.Entities.SetRemoteMotion(record, remote);

        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);

        Assert.Same(remote, record.RemoteMotion);
        Assert.Same(remote.Body, record.PhysicsBody);
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        Assert.True(lifetime.Physics.IsSpatialRemote(record, remote));
        Assert.Equal(1, lifetime.Physics.SpatialRootCount);
        Assert.Equal(1, lifetime.Physics.SpatialRemoteCount);

        var roots = new List<RuntimeEntityRecord>();
        var remotes = new List<RuntimeEntityRecord>();
        lifetime.Physics.CopySpatialRootsTo(roots);
        lifetime.Physics.CopySpatialRemotesTo(remotes);
        Assert.Same(record, Assert.Single(roots));
        Assert.Same(record, Assert.Single(remotes));

        lifetime.Entities.SetRemoteMotion(record, null);
        lifetime.Physics.RefreshRemoteComponent(record);
        Assert.True(lifetime.Physics.IsSpatialRoot(record));
        Assert.Equal(0, lifetime.Physics.SpatialRemoteCount);

        lifetime.Physics.RemoveSpatialProjection(record);
        Assert.Equal(0, lifetime.Physics.SpatialRootCount);
    }

    [Fact]
    public void CanonicalRecordAndPhysicsOwnerOwnProjectileComponentAndWorkset()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000031u, 1));
        lifetime.Entities.SetFullCell(record, 0x01010001u, 0x0101FFFFu);
        lifetime.Entities.SetFinalPhysicsState(
            record,
            PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 20f, 5f),
            Orientation = Quaternion.Identity,
            InWorld = true,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(
            record.FullCellId,
            body.Position,
            body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        var sphere = new ProjectileCollisionSphere(
            new Vector3(0.1f, 0f, 0f),
            0.25f);

        IRuntimeProjectile projectile = lifetime.Physics.BindProjectile(
            record,
            body,
            sphere);
        lifetime.Physics.AcknowledgeSpatialProjection(
            record,
            spatial: true);

        Assert.Same(projectile, record.Projectile);
        Assert.Same(body, projectile.Body);
        Assert.Equal(sphere, projectile.CollisionSphere);
        Assert.True(
            lifetime.Physics.IsSpatialProjectile(record, projectile));
        Assert.Equal(1, lifetime.Physics.SpatialProjectileCount);
        Assert.Equal(
            1,
            lifetime.Physics.CaptureOwnership().SpatialProjectileCount);

        Assert.Same(
            projectile,
            lifetime.Physics.BindProjectile(record, body, sphere));
        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Physics.BindProjectile(
                record,
                new PhysicsBody(),
                sphere));

        lifetime.Physics.RemoveSpatialProjection(record);
        Assert.Equal(0, lifetime.Physics.SpatialProjectileCount);
        Assert.Same(projectile, record.Projectile);

        Assert.True(lifetime.Physics.ClearProjectile(record));
        Assert.Null(record.Projectile);
    }

    [Fact]
    public void ProjectileAuthoritativeMutationInvalidatesSplitPrediction()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000032u, 1));
        lifetime.Entities.SetFullCell(record, 0x01010001u, 0x0101FFFFu);
        lifetime.Entities.SetFinalPhysicsState(
            record,
            PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 20f, 5f),
            Orientation = Quaternion.Identity,
            InWorld = true,
            TransientState = TransientStateFlags.Active,
        };
        body.set_velocity(new Vector3(10f, 0f, 0f));
        body.SnapToCell(
            record.FullCellId,
            body.Position,
            body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        IRuntimeProjectile projectile = lifetime.Physics.BindProjectile(
            record,
            body,
            new ProjectileCollisionSphere(Vector3.Zero, 0.25f));
        lifetime.Physics.AcknowledgeSpatialProjection(
            record,
            spatial: true);
        var updater =
            new RuntimeProjectilePhysicsUpdater(lifetime.Physics);

        Assert.True(updater.TryBegin(
            record,
            quantum: 0.05f,
            record.ObjectClockEpoch,
            externalOwnerValid: null,
            out RuntimeProjectilePhysicsCommit commit));

        lifetime.Entities.AdvanceVectorAuthority(record);
        Vector3 correction = new(7f, 8f, 9f);
        Assert.True(updater.ApplyAuthoritativeVector(
            record,
            record.VectorAuthorityVersion,
            record.VelocityAuthorityVersion,
            correction,
            Vector3.UnitZ,
            currentTime: 1.0));

        Assert.True(
            projectile.PredictionAuthorityVersion
            > commit.PredictionAuthorityVersion);
        Assert.False(updater.Complete(
            commit,
            liveCenterX: 1,
            liveCenterY: 1,
            _ => true));
        Assert.Equal(correction, body.Velocity);
        Assert.Equal(Vector3.UnitZ, body.Omega);
    }

    [Fact]
    public void ProjectileQuantumPublishesOneImmutableRuntimeFrame()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000033u, 1));
        lifetime.Entities.SetFullCell(record, 0x01010001u, 0x0101FFFFu);
        lifetime.Entities.SetFinalPhysicsState(
            record,
            PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Missile
            | PhysicsStateFlags.PathClipped);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 20f, 5f),
            Orientation = Quaternion.Identity,
            InWorld = true,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(
            record.FullCellId,
            body.Position,
            body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        lifetime.Physics.BindProjectile(
            record,
            body,
            new ProjectileCollisionSphere(Vector3.Zero, 0.25f));
        lifetime.Physics.AcknowledgeSpatialProjection(
            record,
            spatial: true);
        var updater =
            new RuntimeProjectilePhysicsUpdater(lifetime.Physics);

        Assert.True(updater.TryBegin(
            record,
            quantum: 0.05f,
            record.ObjectClockEpoch,
            externalOwnerValid: null,
            out RuntimeProjectilePhysicsCommit commit));
        int acknowledgements = 0;
        RuntimePhysicsFrameSnapshot published = default;

        Assert.True(updater.Complete(
            commit,
            liveCenterX: 1,
            liveCenterY: 1,
            snapshot =>
            {
                published = snapshot;
                acknowledgements++;
                return true;
            }));

        Assert.Equal(1, acknowledgements);
        Assert.Equal(body.Position, published.Position);
        Assert.Equal(body.Orientation, published.Orientation);
        Assert.Equal(record.FullCellId, published.FullCellId);
        Assert.Throws<InvalidOperationException>(() =>
            updater.Complete(
                commit,
                liveCenterX: 1,
                liveCenterY: 1,
                _ => true));
    }

    [Fact]
    public void EqualLocalKeysInConcurrentRuntimesDoNotShareWorksets()
    {
        using var first = new RuntimeEntityObjectLifetime();
        using var second = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord firstRecord =
            first.Entities.AddActive(Spawn(0x70000002u, 1));
        RuntimeEntityRecord secondRecord =
            second.Entities.AddActive(Spawn(0x70000003u, 1));
        Assert.Equal(firstRecord.Key, secondRecord.Key);

        first.Physics.AcknowledgeSpatialProjection(firstRecord, spatial: true);

        Assert.True(first.Physics.IsSpatialRoot(firstRecord));
        Assert.False(second.Physics.IsSpatialRoot(secondRecord));
        Assert.Equal(1, first.Physics.SpatialRootCount);
        Assert.Equal(0, second.Physics.SpatialRootCount);
    }

    [Fact]
    public void CollisionAdmissionIsExactRuntimeScopedAndWithdrawalIsTyped()
    {
        using var first = new RuntimeEntityObjectLifetime();
        using var second = new RuntimeEntityObjectLifetime();
        RuntimeCollisionAdmission admission =
            first.Physics.BeginCollisionAdmission(0xA9B4FFFFu);
        RuntimeCollisionAdmission newer =
            first.Physics.BeginCollisionAdmission(0xA9B4FFFFu);

        Assert.Throws<InvalidOperationException>(() =>
            first.Physics.PrepareCollisionGeneration(admission));
        Assert.Throws<InvalidOperationException>(() =>
            second.Physics.PrepareCollisionGeneration(newer));

        using PreparedLandblockCollisionGeneration prepared =
            first.Physics.PrepareCollisionGeneration(newer);
        first.Physics.StageCollisionAssets(
            newer,
            prepared,
            CollisionAssets(0xA9B4FFFFu));
        RuntimeCollisionGenerationCommit commit =
            CommitPrepared(first.Physics, newer, prepared);
        RuntimeCollisionAcknowledgement completed = commit.Acknowledgement;

        Assert.True(commit.Committed);
        Assert.True(completed.WasResident);
        Assert.Equal(1, first.Physics.Engine.LandblockCount);
        Assert.Equal(
            0,
            first.Physics.CaptureOwnership().CollisionAdmissionCount);
        Assert.Throws<InvalidOperationException>(() =>
            CommitPrepared(first.Physics, newer, prepared));

        RuntimeCollisionAcknowledgement withdrawn =
            CompleteWithdrawal(first.Physics, 0xA9B4FFFFu)
                .Acknowledgement;
        Assert.True(withdrawn.WasResident);
        Assert.True(withdrawn.Generation > completed.Generation);
        Assert.Equal(0, first.Physics.Engine.LandblockCount);
    }

    [Fact]
    public void CollisionGenerationKeepsPreviousWorldVisibleUntilOneCommitNotification()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        RuntimeCollisionAdmission firstAdmission =
            physics.BeginCollisionAdmission(0xA9B4FFFFu);
        using (PreparedLandblockCollisionGeneration first =
               physics.PrepareCollisionGeneration(firstAdmission))
        {
            physics.StageCollisionAssets(
                firstAdmission,
                first,
                CollisionAssets(0xA9B4FFFFu, terrainHeight: 10f));
            Assert.True(CommitPrepared(physics,
                firstAdmission,
                first).Committed);
        }

        int notifications = 0;
        physics.CollisionGenerationCommitted += _ => notifications++;
        RuntimeCollisionAdmission replacementAdmission =
            physics.BeginCollisionAdmission(0xA9B4FFFFu);
        using PreparedLandblockCollisionGeneration replacement =
            physics.PrepareCollisionGeneration(replacementAdmission);
        physics.StageCollisionAssets(
            replacementAdmission,
            replacement,
            CollisionAssets(0xA9B4FFFFu, terrainHeight: 25f));

        Assert.Equal(10f, physics.Engine.SampleTerrainZ(1f, 1f));
        Assert.Equal(0, notifications);

        RuntimeCollisionGenerationCommit committed =
            CommitPrepared(physics,
                replacementAdmission,
                replacement);

        Assert.True(committed.Committed);
        Assert.Equal(25f, physics.Engine.SampleTerrainZ(1f, 1f));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void CollisionGenerationRejectsStaleReplacementWithoutMutatingActiveWorld()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        RuntimeCollisionAdmission stale =
            physics.BeginCollisionAdmission(0xA9B4FFFFu);
        using PreparedLandblockCollisionGeneration preparedStale =
            physics.PrepareCollisionGeneration(stale);
        physics.StageCollisionAssets(
            stale,
            preparedStale,
            CollisionAssets(0xA9B4FFFFu, terrainHeight: 10f));

        RuntimeCollisionAdmission current =
            physics.BeginCollisionAdmission(0xA9B4FFFFu);
        Assert.Throws<InvalidOperationException>(() =>
            CommitPrepared(physics, stale, preparedStale));
        physics.CancelCollisionGeneration(stale, preparedStale);
        Assert.Equal(
            1,
            physics.CaptureOwnership().CollisionAdmissionCount);
        Assert.False(physics.Engine.IsLandblockTerrainResident(0xA9B4FFFFu));

        using PreparedLandblockCollisionGeneration preparedCurrent =
            physics.PrepareCollisionGeneration(current);
        physics.StageCollisionAssets(
            current,
            preparedCurrent,
            CollisionAssets(0xA9B4FFFFu, terrainHeight: 20f));
        Assert.True(CommitPrepared(physics,
            current,
            preparedCurrent).Committed);
        Assert.Equal(20f, physics.Engine.SampleTerrainZ(1f, 1f));
    }

    [Fact]
    public void ArmedWithdrawnOwnerStateChangeWritesThroughSealedGeneration()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(0x0101FFFFu);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(0x0101FFFFu, terrainHeight: 5f));
            Assert.True(CommitPrepared(physics,
                initialAdmission,
                initial).Committed);
        }
        physics.Engine.ShadowObjects.Register(
            42u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            0x0101FFFFu,
            seedCellId: 0x01010001u,
            isStatic: false);
        physics.Engine.ShadowObjects.RemoveLandblock(0x0101FFFFu);
        Assert.Equal(0, physics.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(1, physics.Engine.ShadowObjects.RetainedRegistrationCount);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(0x0101FFFFu);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(0x0101FFFFu, terrainHeight: 15f));
        uint owner = Assert.Single(SealPrepared(physics, admission, prepared));
        physics.Engine.ShadowObjects.UpdatePhysicsState(owner, 0x14u);

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.Equal(15f, physics.Engine.SampleTerrainZ(1f, 1f));
        ShadowEntry restored = Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(owner, restored.EntityId);
        Assert.Equal(0x14u, restored.State);
    }

    [Fact]
    public void DenseReplacementSealsOneWorkUnitPerStepAndActivatesWithPayloadBoundedAllocation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint landblockId = 0x0101FFFFu;
        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(landblockId);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(landblockId, terrainHeight: 5f));
            Assert.True(CommitPrepared(
                physics,
                initialAdmission,
                initial).Committed);
        }

        const int ownerCount = 256;
        for (uint index = 0; index < ownerCount; index++)
        {
            physics.Engine.ShadowObjects.Register(
                entityId: 1000u + index,
                gfxObjId: 0x01000001u,
                worldPos: new Vector3(
                    8f + (index % 16u) * 0.25f,
                    8f + (index / 16u) * 0.25f,
                    0f),
                rotation: Quaternion.Identity,
                radius: 0.5f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId,
                seedCellId: 0x01010001u,
                isStatic: false);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(landblockId);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(landblockId, terrainHeight: 15f));

        int captureSteps = 0;
        RuntimeCollisionOwnerCaptureStep capture;
        do
        {
            capture = physics.AdvanceCollisionRetainedOwnerCapture(
                admission,
                prepared);
            captureSteps++;
        }
        while (!capture.Completed);
        Assert.True(captureSteps >= ownerCount);
        Assert.Equal(ownerCount, prepared.RetainedOwnerIds.Count);
        foreach (uint ownerId in prepared.RetainedOwnerIds)
        {
            physics.RefreshCollisionRetainedOwner(
                admission,
                prepared,
                ownerId);
        }

        int sealSteps = 0;
        int workUnits = 0;
        RuntimeCollisionSealStep seal;
        do
        {
            seal = physics.AdvanceCollisionGenerationSeal(
                admission,
                prepared);
            sealSteps++;
            Assert.InRange(seal.WorkUnits, 0, 1);
            workUnits += seal.WorkUnits;
        }
        while (!seal.Completed);
        Assert.False(seal.Restarted);
        Assert.True(sealSteps > ownerCount);
        Assert.True(workUnits > ownerCount);

        Assert.Equal(5f, physics.Engine.SampleTerrainZ(1f, 1f));
        Assert.Equal(ownerCount,
            physics.Engine.ShadowObjects.TotalRegistered);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RuntimeCollisionGenerationCommit commit =
            physics.CommitCollisionGeneration(admission, prepared);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(commit.Committed);
        Assert.InRange(allocated, 0L, 4L * 1024L * 1024L);
        Assert.Equal(15f, physics.Engine.SampleTerrainZ(1f, 1f));
        Assert.Equal(ownerCount, physics.Engine.ShadowObjects.TotalRegistered);
    }

    [Fact]
    public void UnrelatedOwnerMutationEveryStepCannotRestartTargetCaptureOrSeal()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(target, terrainHeight: 5f));
            Assert.True(CommitPrepared(
                physics,
                initialAdmission,
                initial).Committed);
        }

        physics.Engine.ShadowObjects.Register(
            42u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: false);
        physics.Engine.ShadowObjects.Register(
            99u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            0x0303FFFFu,
            seedCellId: 0x03030001u,
            isStatic: false);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 15f));

        int steps = 0;
        RuntimeCollisionOwnerCaptureStep capture;
        do
        {
            physics.Engine.ShadowObjects.UpdatePhysicsState(
                99u,
                (uint)(steps + 1));
            capture = physics.AdvanceCollisionRetainedOwnerCapture(
                admission,
                prepared);
            Assert.False(capture.Restarted);
            Assert.True(++steps < 512);
        }
        while (!capture.Completed);
        Assert.Equal(42u, Assert.Single(prepared.RetainedOwnerIds));
        physics.RefreshCollisionRetainedOwner(admission, prepared, 42u);

        RuntimeCollisionSealStep seal;
        do
        {
            physics.Engine.ShadowObjects.UpdatePhysicsState(
                99u,
                (uint)(steps + 1));
            seal = physics.AdvanceCollisionGenerationSeal(
                admission,
                prepared);
            Assert.False(seal.Restarted);
            Assert.InRange(seal.WorkUnits, 0, 1);
            Assert.True(++steps < 256);
        }
        while (!seal.Completed);

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.Equal(15f, physics.Engine.SampleTerrainZ(1f, 1f));
    }

    [Fact]
    public void TwoUnrelatedOwnersMovingEverySealStepCannotStarveActivation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        foreach (uint ownerId in new[] { 99u, 100u })
        {
            physics.Engine.ShadowObjects.Register(
                ownerId,
                0x01000001u,
                new Vector3(10f + ownerId - 99u, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                0x0303FFFFu,
                seedCellId: 0x03030001u,
                isStatic: false);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 15f));

        int steps = 0;
        RuntimeCollisionOwnerCaptureStep capture;
        do
        {
            foreach (uint ownerId in new[] { 99u, 100u })
            {
                physics.Engine.ShadowObjects.UpdatePhysicsState(
                    ownerId,
                    (uint)(steps + ownerId));
            }
            capture = physics.AdvanceCollisionRetainedOwnerCapture(
                admission,
                prepared);
            Assert.False(capture.Restarted);
            Assert.True(++steps < 512);
        }
        while (!capture.Completed);

        while (true)
        {
            foreach (uint ownerId in new[] { 99u, 100u })
            {
                physics.Engine.ShadowObjects.UpdatePosition(
                    ownerId,
                    new Vector3(10f + ownerId - 99u + steps, 10f, 0f),
                    Quaternion.Identity,
                    0f,
                    0f,
                    0x0303FFFFu,
                    seedCellId: 0x03030001u);
                physics.Engine.ShadowObjects.UpdatePhysicsState(
                    ownerId,
                    (uint)(steps + ownerId));
            }

            RuntimeCollisionSealStep seal =
                physics.AdvanceCollisionGenerationSeal(admission, prepared);
            Assert.False(seal.Restarted);
            Assert.InRange(seal.WorkUnits, 0, 1);
            Assert.True(++steps < 512);
            if (seal.Completed)
                break;
        }

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        ShadowEntry[] entries = physics.Engine.ShadowObjects
            .AllEntriesForDebug()
            .OrderBy(entry => entry.EntityId)
            .ToArray();
        Assert.Equal(new[] { 99u, 100u }, entries.Select(entry => entry.EntityId));
        foreach (ShadowEntry entry in entries)
        {
            Assert.Equal((uint)(steps - 1) + entry.EntityId, entry.State);
            Assert.Equal(
                10f + entry.EntityId - 99u + steps - 1,
                entry.Position.X);
        }
    }

    [Fact]
    public void TwoRelevantOwnersMovingEverySealStepConvergeThroughWriteThrough()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(target, terrainHeight: 5f));
            Assert.True(CommitPrepared(
                physics,
                initialAdmission,
                initial).Committed);
        }

        foreach (uint ownerId in new[] { 42u, 43u })
        {
            physics.Engine.ShadowObjects.Register(
                ownerId,
                0x01000001u,
                new Vector3(10f + ownerId - 42u, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u,
                isStatic: false);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 15f));
        while (!physics.AdvanceCollisionRetainedOwnerCapture(
                   admission,
                   prepared).Completed)
        {
        }
        foreach (uint ownerId in prepared.RetainedOwnerIds)
            physics.RefreshCollisionRetainedOwner(admission, prepared, ownerId);

        int step = 0;
        RuntimeCollisionSealStep seal;
        do
        {
            step++;
            physics.Engine.ShadowObjects.UpdatePosition(
                42u,
                new Vector3(10f + step * 0.01f, 10f, 0f),
                Quaternion.Identity,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u);
            physics.Engine.ShadowObjects.UpdatePosition(
                43u,
                new Vector3(11f + step * 0.01f, 10f, 0f),
                Quaternion.Identity,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u);
            seal = physics.AdvanceCollisionGenerationSeal(
                admission,
                prepared);
            Assert.False(seal.Restarted);
            Assert.InRange(seal.WorkUnits, 0, 1);
            Assert.True(step < 256);
        }
        while (!seal.Completed);

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        ShadowEntry[] entries = physics.Engine.ShadowObjects
            .AllEntriesForDebug()
            .OrderBy(entry => entry.EntityId)
            .ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(10f + step * 0.01f, entries[0].Position.X, 4);
        Assert.Equal(11f + step * 0.01f, entries[1].Position.X, 4);
    }

    [Fact]
    public void FirstLoadWithNewStaticBucketActivatesWithPayloadBoundedAllocation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 12f));
        prepared.Engine.ShadowObjects.Register(
            500u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: true);
        _ = SealPrepared(physics, admission, prepared);

        PhysicsDataCache cacheFacade = physics.DataCache;
        CellGraph graphFacade = physics.DataCache.CellGraph;
        ShadowObjectRegistry shadowFacade = physics.Engine.ShadowObjects;
        int notifications = 0;
        physics.CollisionGenerationCommitted += _ => notifications++;
        Assert.False(physics.Engine.IsLandblockTerrainResident(target));
        Assert.Empty(physics.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(0, notifications);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RuntimeCollisionGenerationCommit commit =
            physics.CommitCollisionGeneration(admission, prepared);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(commit.Committed);
        Assert.InRange(allocated, 0L, 1L * 1024L * 1024L);
        Assert.Equal(1, notifications);
        Assert.Same(cacheFacade, physics.DataCache);
        Assert.Same(graphFacade, physics.DataCache.CellGraph);
        Assert.Same(shadowFacade, physics.Engine.ShadowObjects);
        Assert.Equal(12f, physics.Engine.SampleTerrainZ(1f, 1f));
        Assert.Equal(500u, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).EntityId);
    }

    [Fact]
    public void CollisionSealWorkIsIndependentOfResidentWorldSize()
    {
        Assert.Equal(
            MeasureSealWorkUnits(residentLandblocks: 32),
            MeasureSealWorkUnits(residentLandblocks: 256));
    }

    private static int MeasureSealWorkUnits(int residentLandblocks)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        for (int index = 0; index < residentLandblocks; index++)
        {
            uint prefix = (uint)(0x10 + (index % 128)) << 24
                | (uint)(0x01 + (index / 128)) << 16;
            uint landblockId = prefix | 0xFFFFu;
            RuntimeLandblockCollisionAssets assets = CollisionAssets(
                landblockId,
                terrainHeight: index);
            physics.Engine.AddLandblock(
                assets.LandblockId,
                assets.Terrain,
                assets.CellSurfaces,
                assets.PortalPlanes,
                assets.WorldOffsetX,
                assets.WorldOffsetY);
            AddSyntheticCell(physics.DataCache, prefix | 0x0100u);
            physics.DataCache.RegisterBuildingForTest(
                prefix | 1u,
                SyntheticBuilding(Matrix4x4.Identity));
            physics.Engine.ShadowObjects.Register(
                (uint)(10_000 + index),
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                landblockId,
                seedCellId: prefix | 1u,
                isStatic: false);
        }

        const uint target = 0x0901FFFFu;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 21f));
        AddSyntheticCell(prepared.DataCache, 0x09010100u);
        prepared.DataCache.RegisterBuildingForTest(
            0x09010001u,
            SyntheticBuilding(Matrix4x4.Identity));

        while (!physics.AdvanceCollisionRetainedOwnerCapture(
                   admission,
                   prepared).Completed)
        {
        }
        foreach (uint ownerId in prepared.RetainedOwnerIds)
            physics.RefreshCollisionRetainedOwner(admission, prepared, ownerId);

        int workUnits = 0;
        RuntimeCollisionSealStep seal;
        do
        {
            seal = physics.AdvanceCollisionGenerationSeal(admission, prepared);
            Assert.False(seal.Restarted);
            Assert.InRange(seal.WorkUnits, 0, 1);
            workUnits += seal.WorkUnits;
        }
        while (!seal.Completed);
        physics.CancelCollisionGeneration(admission, prepared);
        return workUnits;
    }

    [Fact]
    public void CollisionPreparationCostIsIndependentOfResidentWorldSize()
    {
        (int prepared32, int seal32) =
            MeasurePreparationAndSealWork(residentLandblocks: 32);
        (int prepared256, int seal256) =
            MeasurePreparationAndSealWork(residentLandblocks: 256);
        Assert.Equal(prepared32, prepared256);
        Assert.Equal(seal32, seal256);
    }

    private static (int PreparationAdvances, int SealWorkUnits)
        MeasurePreparationAndSealWork(int residentLandblocks)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        for (int index = 0; index < residentLandblocks; index++)
        {
            uint prefix = (uint)(0x10 + (index % 128)) << 24
                | (uint)(0x01 + (index / 128)) << 16;
            uint landblockId = prefix | 0xFFFFu;
            RuntimeLandblockCollisionAssets assets = CollisionAssets(
                landblockId,
                terrainHeight: index);
            physics.Engine.AddLandblock(
                assets.LandblockId,
                assets.Terrain,
                assets.CellSurfaces,
                assets.PortalPlanes,
                assets.WorldOffsetX,
                assets.WorldOffsetY);
            AddSyntheticCell(physics.DataCache, prefix | 0x0100u);
            physics.DataCache.RegisterBuildingForTest(
                prefix | 1u,
                SyntheticBuilding(Matrix4x4.Identity));
            physics.Engine.ShadowObjects.Register(
                (uint)(20_000 + index),
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                landblockId,
                seedCellId: prefix | 1u,
                isStatic: false);
        }

        const uint target = 0x0901FFFFu;
        RuntimeCollisionAdmission warmAdmission =
            physics.BeginCollisionAdmission(0x0A01FFFFu);
        PreparedLandblockCollisionGeneration warm =
            physics.PrepareCollisionGeneration(warmAdmission);
        physics.CancelCollisionGeneration(warmAdmission, warm);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        long admissionAllocation =
            GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(admissionAllocation, 1L, 128L * 1024L);

        int preparationAdvances = 0;
        RuntimeCollisionPreparationStep step;
        do
        {
            step = physics.AdvanceCollisionGenerationPreparation(
                admission,
                prepared);
            Assert.InRange(step.WorkUnits, 0, 1);
            Assert.True(++preparationAdvances < 10_000);
        }
        while (!step.Completed);

        Assert.Equal(0, prepared.Engine.LandblockCount);

        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 33f));
        AddSyntheticCell(prepared.DataCache, 0x09010100u);
        prepared.DataCache.RegisterBuildingForTest(
            0x09010001u,
            SyntheticBuilding(Matrix4x4.Identity));

        while (!physics.AdvanceCollisionRetainedOwnerCapture(
                   admission,
                   prepared).Completed)
        {
        }
        foreach (uint ownerId in prepared.RetainedOwnerIds)
            physics.RefreshCollisionRetainedOwner(admission, prepared, ownerId);

        int sealWorkUnits = 0;
        RuntimeCollisionSealStep seal;
        do
        {
            seal = physics.AdvanceCollisionGenerationSeal(admission, prepared);
            Assert.False(seal.Restarted);
            Assert.InRange(seal.WorkUnits, 0, 1);
            sealWorkUnits += seal.WorkUnits;
        }
        while (!seal.Completed);

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.True(physics.Engine.IsLandblockTerrainResident(target));
        return (preparationAdvances, sealWorkUnits);
    }

    [Fact]
    public void CommitTimeRefloodMatchesPrecomputedReflood()
    {

        AssertCommitTimeRefloodMatchesOracle(
            targetLandblock: 0x0000FFFFu,
            targetOrigin: new Vector3(0f, 0f, 0f),
            neighborLandblock: 0x0001FFFFu,
            neighborOrigin: new Vector3(0f, 192f, 0f),
            targetEnvCells: Array.Empty<uint>(),
            owners:
            [
                new RefloodOwnerSpec(
                    800u,
                    new Vector3(10f, 193f, 0f),
                    Radius: 4f,
                    SeedCellId: 0x00010001u,
                    IsStatic: false,
                    Staged: false),
                new RefloodOwnerSpec(
                    801u,
                    new Vector3(20f, 195f, 0f),
                    Radius: 6f,
                    SeedCellId: 0x00010001u,
                    IsStatic: true,
                    Staged: false),
                new RefloodOwnerSpec(
                    802u,
                    new Vector3(30f, 20f, 0f),
                    Radius: 5f,
                    SeedCellId: 0x00000009u,
                    IsStatic: true,
                    Staged: true),
            ]);

        // EnvCell-heavy target: authored indoor statics seeded in target
        // EnvCells plus a retained seam dynamic.
        AssertCommitTimeRefloodMatchesOracle(
            targetLandblock: 0x0301FFFFu,
            targetOrigin: new Vector3(576f, 192f, 0f),
            neighborLandblock: 0x0302FFFFu,
            neighborOrigin: new Vector3(576f, 384f, 0f),
            targetEnvCells: [0x03010100u, 0x03010101u, 0x03010102u],
            owners:
            [
                new RefloodOwnerSpec(
                    810u,
                    new Vector3(600f, 385f, 0f),
                    Radius: 3f,
                    SeedCellId: 0x03020001u,
                    IsStatic: false,
                    Staged: false),
                new RefloodOwnerSpec(
                    811u,
                    new Vector3(580f, 200f, 0f),
                    Radius: 1.5f,
                    SeedCellId: 0x03010100u,
                    IsStatic: true,
                    Staged: true),
                new RefloodOwnerSpec(
                    812u,
                    new Vector3(590f, 210f, 0f),
                    Radius: 1.5f,
                    SeedCellId: 0x03010101u,
                    IsStatic: true,
                    Staged: true),
            ]);

        // Scenery-dense target: sixteen authored outdoor statics across the
        // block plus retained seam owners.
        var sceneryOwners = new List<RefloodOwnerSpec>
        {
            new(
                820u,
                new Vector3(970f, 1153f, 0f),
                Radius: 4f,
                SeedCellId: 0x05060001u,
                IsStatic: false,
                Staged: false),
            new(
                821u,
                new Vector3(1100f, 1150f, 0f),
                Radius: 5f,
                SeedCellId: 0x05060031u,
                IsStatic: true,
                Staged: false),
        };
        for (int index = 0; index < 16; index++)
        {
            float localX = 12f + (index % 4) * 48f;
            float localY = 12f + (index / 4) * 48f;
            uint low = (uint)(
                ((int)(localX / 24f) * 8) + (int)(localY / 24f) + 1);
            sceneryOwners.Add(new RefloodOwnerSpec(
                (uint)(830 + index),
                new Vector3(960f + localX, 960f + localY, 0f),
                Radius: 3f,
                SeedCellId: 0x05050000u | low,
                IsStatic: true,
                Staged: true));
        }
        AssertCommitTimeRefloodMatchesOracle(
            targetLandblock: 0x0505FFFFu,
            targetOrigin: new Vector3(960f, 960f, 0f),
            neighborLandblock: 0x0506FFFFu,
            neighborOrigin: new Vector3(960f, 1152f, 0f),
            targetEnvCells: Array.Empty<uint>(),
            owners: [.. sceneryOwners]);
    }

    private sealed record RefloodOwnerSpec(
        uint OwnerId,
        Vector3 Position,
        float Radius,
        uint SeedCellId,
        bool IsStatic,
        bool Staged);

    private static void AssertCommitTimeRefloodMatchesOracle(
        uint targetLandblock,
        Vector3 targetOrigin,
        uint neighborLandblock,
        Vector3 neighborOrigin,
        uint[] targetEnvCells,
        RefloodOwnerSpec[] owners)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;

        RuntimeCollisionAdmission neighborAdmission =
            physics.BeginCollisionAdmission(neighborLandblock);
        using (PreparedLandblockCollisionGeneration neighborPrepared =
               physics.PrepareCollisionGeneration(neighborAdmission))
        {
            physics.StageCollisionAssets(
                neighborAdmission,
                neighborPrepared,
                CollisionAssetsAt(neighborLandblock, 2f, neighborOrigin));
            Assert.True(CommitPrepared(
                physics,
                neighborAdmission,
                neighborPrepared).Committed);
        }
        foreach (RefloodOwnerSpec owner in owners)
        {
            if (owner.Staged)
                continue;
            physics.Engine.ShadowObjects.Register(
                owner.OwnerId,
                0x01000001u,
                owner.Position,
                Quaternion.Identity,
                owner.Radius,
                0f,
                0f,
                neighborLandblock,
                seedCellId: owner.SeedCellId,
                isStatic: owner.IsStatic);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(targetLandblock);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssetsAt(targetLandblock, 1f, targetOrigin));
        foreach (uint envCellId in targetEnvCells)
            AddSyntheticCell(prepared.DataCache, envCellId);
        foreach (RefloodOwnerSpec owner in owners)
        {
            if (!owner.Staged)
                continue;
            prepared.Engine.ShadowObjects.Register(
                owner.OwnerId,
                0x01000001u,
                owner.Position,
                Quaternion.Identity,
                owner.Radius,
                0f,
                0f,
                targetLandblock,
                seedCellId: owner.SeedCellId,
                isStatic: owner.IsStatic);
        }
        Assert.True(CommitPrepared(physics, admission, prepared).Committed);

        var oracleCache = new PhysicsDataCache();
        var oracleEngine = new PhysicsEngine { DataCache = oracleCache };
        RuntimeLandblockCollisionAssets neighborAssets =
            CollisionAssetsAt(neighborLandblock, 2f, neighborOrigin);
        oracleEngine.AddLandblock(
            neighborAssets.LandblockId,
            neighborAssets.Terrain,
            neighborAssets.CellSurfaces,
            neighborAssets.PortalPlanes,
            neighborAssets.WorldOffsetX,
            neighborAssets.WorldOffsetY);
        RuntimeLandblockCollisionAssets targetAssets =
            CollisionAssetsAt(targetLandblock, 1f, targetOrigin);
        oracleEngine.AddLandblock(
            targetAssets.LandblockId,
            targetAssets.Terrain,
            targetAssets.CellSurfaces,
            targetAssets.PortalPlanes,
            targetAssets.WorldOffsetX,
            targetAssets.WorldOffsetY);
        foreach (uint envCellId in targetEnvCells)
            AddSyntheticCell(oracleCache, envCellId);
        foreach (RefloodOwnerSpec owner in owners)
        {
            oracleEngine.ShadowObjects.Register(
                owner.OwnerId,
                0x01000001u,
                owner.Position,
                Quaternion.Identity,
                owner.Radius,
                0f,
                0f,
                owner.Staged ? targetLandblock : neighborLandblock,
                seedCellId: owner.SeedCellId,
                isStatic: owner.IsStatic);
        }

        foreach (RefloodOwnerSpec owner in owners)
        {
            Assert.Equal(
                oracleEngine.ShadowObjects.HasLogicalOwner(owner.OwnerId),
                physics.Engine.ShadowObjects.HasLogicalOwner(owner.OwnerId));
        }
        var candidateCells = new List<uint>();
        for (uint low = 1u; low <= 0x40u; low++)
        {
            candidateCells.Add((targetLandblock & 0xFFFF0000u) | low);
            candidateCells.Add((neighborLandblock & 0xFFFF0000u) | low);
        }
        candidateCells.AddRange(targetEnvCells);
        foreach (uint cellId in candidateCells)
        {
            ShadowEntry[] actual = physics.Engine.ShadowObjects
                .GetObjectsInCell(cellId)
                .OrderBy(entry => entry.EntityId)
                .ThenBy(entry => entry.GfxObjId)
                .ToArray();
            ShadowEntry[] expected = oracleEngine.ShadowObjects
                .GetObjectsInCell(cellId)
                .OrderBy(entry => entry.EntityId)
                .ThenBy(entry => entry.GfxObjId)
                .ToArray();
            Assert.Equal(expected, actual);
        }
    }

    private static RuntimeLandblockCollisionAssets CollisionAssetsAt(
        uint landblockId,
        float terrainHeight,
        Vector3 origin)
    {
        var heights = new byte[81];
        var table = new float[256];
        table[0] = terrainHeight;
        return new RuntimeLandblockCollisionAssets(
            landblockId,
            new TerrainSurface(heights, table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            origin.X,
            origin.Y,
            0u);
    }

    [Fact]
    public void CommitAppliesOneLandblockDeltaInASingleCall()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint neighbor = 0x0202FFFFu;
        const uint neighborCellId = 0x02020100u;
        const uint target = 0x0101FFFFu;
        const uint targetCellId = 0x01010100u;
        const uint targetBuildingId = 0x01010001u;

        RuntimeCollisionAdmission neighborAdmission =
            physics.BeginCollisionAdmission(neighbor);
        using (PreparedLandblockCollisionGeneration neighborPrepared =
               physics.PrepareCollisionGeneration(neighborAdmission))
        {
            physics.StageCollisionAssets(
                neighborAdmission,
                neighborPrepared,
                CollisionAssets(neighbor, terrainHeight: 3f));
            AddSyntheticCell(neighborPrepared.DataCache, neighborCellId);
            Assert.True(CommitPrepared(
                physics,
                neighborAdmission,
                neighborPrepared).Committed);
        }
        physics.Engine.ShadowObjects.Register(
            700u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            neighbor,
            seedCellId: 0x02020001u,
            isStatic: false);
        physics.Engine.ShadowObjects.Register(
            699u,
            0x01000001u,
            new Vector3(11f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: true);
        CellPhysics? neighborCell =
            physics.DataCache.GetCellStruct(neighborCellId);
        Assert.NotNull(neighborCell);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 9f));
        AddSyntheticCell(prepared.DataCache, targetCellId);
        prepared.DataCache.RegisterBuildingForTest(
            targetBuildingId,
            SyntheticBuilding(Matrix4x4.Identity));
        prepared.Engine.ShadowObjects.Register(
            701u,
            0x01000001u,
            new Vector3(12f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: true);
        _ = SealPrepared(physics, admission, prepared);

        RuntimeCollisionGenerationCommit commit = default;
        for (int poll = 0; poll < 10_000 && !commit.EngineCommitted; poll++)
        {
            Assert.Null(physics.DataCache.GetCellStruct(targetCellId));
            Assert.Null(physics.DataCache.GetBuilding(targetBuildingId));
            Assert.False(physics.Engine.IsLandblockTerrainResident(target));
            commit = physics.CommitCollisionGeneration(admission, prepared);
            if (!commit.EngineCommitted && !commit.Completed)
                _ = SealPrepared(physics, admission, prepared);
        }

        Assert.True(commit.EngineCommitted);
        Assert.True(physics.Engine.IsLandblockTerrainResident(target));
        Assert.NotNull(physics.DataCache.GetCellStruct(targetCellId));
        Assert.NotNull(physics.DataCache.GetBuilding(targetBuildingId));
        Assert.True(physics.DataCache.CellGraph.Contains(targetCellId));
        uint[] owners = physics.Engine.ShadowObjects
            .AllEntriesForDebug()
            .Select(entry => entry.EntityId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        Assert.Equal(new[] { 700u, 701u }, owners);
        Assert.False(physics.Engine.ShadowObjects.HasLogicalOwner(699u));
        Assert.Same(neighborCell, physics.DataCache.GetCellStruct(neighborCellId));
        Assert.Throws<ObjectDisposedException>(
            () => _ = prepared.Engine.LandblockCount);

        if (!commit.Completed)
        {
            Assert.True(CompleteSealedCommit(
                physics,
                admission,
                prepared).Committed);
        }
    }

    [Fact]
    public void ChangedEnvCellsBuildingsAndStaticBucketActivateWithPayloadBoundedAllocation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint oldCell = 0x01010100u;
        const uint newCell = 0x01010101u;
        const uint oldBuilding = 0x01010001u;
        const uint newBuilding = 0x01010002u;

        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(target, terrainHeight: 5f));
            AddSyntheticCell(initial.DataCache, oldCell);
            initial.DataCache.RegisterBuildingForTest(
                oldBuilding,
                SyntheticBuilding(Matrix4x4.Identity));
            initial.Engine.ShadowObjects.Register(
                600u,
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u,
                isStatic: true);
            Assert.True(CommitPrepared(
                physics,
                initialAdmission,
                initial).Committed);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        prepared.DataCache.RemoveCellsForLandblock(target);
        prepared.DataCache.RemoveBuildingsForLandblock(target);
        prepared.DataCache.CellGraph.RemoveEnvCellsForLandblock(target);
        prepared.Engine.ShadowObjects.DeregisterStaticOwnersForLandblock(target);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 15f));
        AddSyntheticCell(prepared.DataCache, newCell);
        prepared.DataCache.RegisterBuildingForTest(
            newBuilding,
            SyntheticBuilding(Matrix4x4.CreateTranslation(1f, 0f, 0f)));
        prepared.Engine.ShadowObjects.Register(
            601u,
            0x01000001u,
            new Vector3(11f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010002u,
            isStatic: true);
        _ = SealPrepared(physics, admission, prepared);

        PhysicsDataCache cacheFacade = physics.DataCache;
        CellGraph graphFacade = physics.DataCache.CellGraph;
        ShadowObjectRegistry shadowFacade = physics.Engine.ShadowObjects;
        Assert.Equal(5f, physics.Engine.SampleTerrainZ(1f, 1f));
        Assert.NotNull(physics.DataCache.GetCellStruct(oldCell));
        Assert.Null(physics.DataCache.GetCellStruct(newCell));
        Assert.NotNull(physics.DataCache.GetBuilding(oldBuilding));
        Assert.Null(physics.DataCache.GetBuilding(newBuilding));
        Assert.Equal(600u, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).EntityId);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RuntimeCollisionGenerationCommit commit =
            physics.CommitCollisionGeneration(admission, prepared);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(commit.Committed);
        Assert.InRange(allocated, 0L, 1L * 1024L * 1024L);
        Assert.Same(cacheFacade, physics.DataCache);
        Assert.Same(graphFacade, physics.DataCache.CellGraph);
        Assert.Same(shadowFacade, physics.Engine.ShadowObjects);
        Assert.Null(physics.DataCache.GetCellStruct(oldCell));
        Assert.NotNull(physics.DataCache.GetCellStruct(newCell));
        Assert.Null(physics.DataCache.GetBuilding(oldBuilding));
        Assert.NotNull(physics.DataCache.GetBuilding(newBuilding));
        Assert.Equal(601u, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).EntityId);
    }

    [Fact]
    public void ConcurrentPreparedLandblocksRebaseBeforeSecondActivation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint firstLandblock = 0x0101FFFFu;
        const uint secondLandblock = 0x0202FFFFu;
        RuntimeCollisionAdmission firstAdmission =
            physics.BeginCollisionAdmission(firstLandblock);
        RuntimeCollisionAdmission secondAdmission =
            physics.BeginCollisionAdmission(secondLandblock);
        using PreparedLandblockCollisionGeneration first =
            physics.PrepareCollisionGeneration(firstAdmission);
        using PreparedLandblockCollisionGeneration second =
            physics.PrepareCollisionGeneration(secondAdmission);
        physics.StageCollisionAssets(
            firstAdmission,
            first,
            CollisionAssets(firstLandblock, terrainHeight: 5f));
        physics.StageCollisionAssets(
            secondAdmission,
            second,
            CollisionAssets(secondLandblock, terrainHeight: 15f));
        first.Engine.ShadowObjects.Register(
            700u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            firstLandblock,
            seedCellId: 0x01010001u,
            isStatic: true);
        _ = SealPrepared(physics, firstAdmission, first);
        _ = SealPrepared(physics, secondAdmission, second);

        Assert.False(physics.Engine.IsLandblockTerrainResident(firstLandblock));
        Assert.False(physics.Engine.IsLandblockTerrainResident(secondLandblock));
        Assert.Empty(physics.Engine.ShadowObjects.AllEntriesForDebug());
        _ = GC.GetAllocatedBytesForCurrentThread();
        long firstBefore = GC.GetAllocatedBytesForCurrentThread();
        RuntimeCollisionGenerationCommit firstCommit =
            physics.CommitCollisionGeneration(firstAdmission, first);
        long firstAllocated =
            GC.GetAllocatedBytesForCurrentThread() - firstBefore;
        Assert.True(firstCommit.Committed);
        Assert.InRange(firstAllocated, 0L, 1L * 1024L * 1024L);
        Assert.True(second.IsSealed);
        Assert.True(physics.Engine.IsLandblockTerrainResident(firstLandblock));
        Assert.False(physics.Engine.IsLandblockTerrainResident(secondLandblock));

        _ = SealPrepared(physics, secondAdmission, second);
        Assert.True(physics.Engine.IsLandblockTerrainResident(firstLandblock));
        Assert.False(physics.Engine.IsLandblockTerrainResident(secondLandblock));
        Assert.Equal(700u, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).EntityId);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long secondBefore = GC.GetAllocatedBytesForCurrentThread();
        RuntimeCollisionGenerationCommit secondCommit =
            physics.CommitCollisionGeneration(secondAdmission, second);
        long secondAllocated =
            GC.GetAllocatedBytesForCurrentThread() - secondBefore;
        Assert.True(secondCommit.Committed);
        Assert.InRange(secondAllocated, 0L, 1L * 1024L * 1024L);
        Assert.Equal(2, physics.Engine.LandblockCount);
        Assert.True(physics.Engine.IsLandblockTerrainResident(firstLandblock));
        Assert.True(physics.Engine.IsLandblockTerrainResident(secondLandblock));
        Assert.Equal(700u, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).EntityId);
    }

    [Fact]
    public void PostCommitOwnerMutationWinsOverQueuedPeerRebase()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint firstLandblock = 0x0101FFFFu;
        const uint secondLandblock = 0x0202FFFFu;
        RuntimeCollisionAdmission firstAdmission =
            physics.BeginCollisionAdmission(firstLandblock);
        using PreparedLandblockCollisionGeneration first =
            physics.PrepareCollisionGeneration(firstAdmission);
        physics.StageCollisionAssets(
            firstAdmission,
            first,
            CollisionAssets(firstLandblock));
        first.Engine.ShadowObjects.Register(
            701u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            firstLandblock,
            seedCellId: 0x01010001u,
            isStatic: false);

        RuntimeCollisionAdmission secondAdmission =
            physics.BeginCollisionAdmission(secondLandblock);
        using PreparedLandblockCollisionGeneration second =
            physics.PrepareCollisionGeneration(secondAdmission);
        physics.StageCollisionAssets(
            secondAdmission,
            second,
            CollisionAssets(secondLandblock));
        _ = SealPrepared(physics, firstAdmission, first);
        _ = SealPrepared(physics, secondAdmission, second);

        Assert.True(CompleteSealedCommit(
            physics,
            firstAdmission,
            first).Committed);
        physics.Engine.ShadowObjects.UpdatePosition(
            701u,
            new Vector3(12f, 10f, 0f),
            Quaternion.Identity,
            0f,
            0f,
            firstLandblock,
            seedCellId: 0x01010001u);

        _ = SealPrepared(physics, secondAdmission, second);
        Assert.True(CompleteSealedCommit(
            physics,
            secondAdmission,
            second).Committed);
        Assert.Equal(12f, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).Position.X);
    }

    [Fact]
    public void ConcurrentSeamStaticRefloodsAgainstLaterTopology()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint north = 0xA9B4FFFFu;
        const uint south = 0xA9B3FFFFu;
        foreach ((uint landblock, float offsetY) in new[]
                 {
                     (north, 0f),
                     (south, -192f),
                 })
        {
            RuntimeCollisionAdmission seed =
                physics.BeginCollisionAdmission(landblock);
            using PreparedLandblockCollisionGeneration initial =
                physics.PrepareCollisionGeneration(seed);
            physics.StageCollisionAssets(
                seed,
                initial,
                CollisionAssets(landblock) with
                {
                    WorldOffsetY = offsetY,
                });
            Assert.True(CommitPrepared(physics, seed, initial).Committed);
        }

        RuntimeCollisionAdmission northAdmission =
            physics.BeginCollisionAdmission(north);
        using PreparedLandblockCollisionGeneration northPrepared =
            physics.PrepareCollisionGeneration(northAdmission);
        physics.StageCollisionAssets(
            northAdmission,
            northPrepared,
            CollisionAssets(north));
        northPrepared.Engine.ShadowObjects.Register(
            702u,
            0x01000001u,
            new Vector3(150f, 0.2f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            north,
            seedCellId: 0xA9B40031u,
            isStatic: true);
        Assert.True(northPrepared.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            702u,
            south));

        RuntimeCollisionAdmission southAdmission =
            physics.BeginCollisionAdmission(south);
        using PreparedLandblockCollisionGeneration southPrepared =
            physics.PrepareCollisionGeneration(southAdmission);
        physics.StageCollisionAssets(
            southAdmission,
            southPrepared,
            CollisionAssets(south) with
            {
                WorldOffsetY = -192f,
            });
        _ = SealPrepared(physics, northAdmission, northPrepared);
        _ = SealPrepared(physics, southAdmission, southPrepared);

        Assert.True(CompleteSealedCommit(
            physics,
            northAdmission,
            northPrepared).Committed);
        _ = SealPrepared(physics, southAdmission, southPrepared);
        Assert.True(CompleteSealedCommit(
            physics,
            southAdmission,
            southPrepared).Committed);
        Assert.True(physics.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            702u,
            south));
    }

    [Fact]
    public void CancelledOlderPreparationNeverLeaksIntoLaterDraft()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint cancelledLandblock = 0x0101FFFFu;
        const uint survivingLandblock = 0x0202FFFFu;
        RuntimeCollisionAdmission cancelledAdmission =
            physics.BeginCollisionAdmission(cancelledLandblock);
        PreparedLandblockCollisionGeneration cancelled =
            physics.PrepareCollisionGeneration(cancelledAdmission);
        physics.StageCollisionAssets(
            cancelledAdmission,
            cancelled,
            CollisionAssets(cancelledLandblock, terrainHeight: 11f));
        RuntimeCollisionAdmission survivingAdmission =
            physics.BeginCollisionAdmission(survivingLandblock);
        using PreparedLandblockCollisionGeneration surviving =
            physics.PrepareCollisionGeneration(survivingAdmission);
        physics.StageCollisionAssets(
            survivingAdmission,
            surviving,
            CollisionAssets(survivingLandblock, terrainHeight: 22f));
        _ = SealPrepared(physics, cancelledAdmission, cancelled);

        physics.CancelCollisionGeneration(cancelledAdmission, cancelled);
        Assert.True(CommitPrepared(
            physics,
            survivingAdmission,
            surviving).Committed);

        Assert.False(physics.Engine.IsLandblockTerrainResident(
            cancelledLandblock));
        Assert.True(physics.Engine.IsLandblockTerrainResident(
            survivingLandblock));
    }

    [Fact]
    public void OutgoingTargetStaticMutationCannotResurrectOmittedOwner()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(target));
            initial.Engine.ShadowObjects.Register(
                500u,
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u,
                isStatic: true);
            Assert.True(CommitPrepared(
                physics,
                initialAdmission,
                initial).Committed);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        prepared.Engine.ShadowObjects.DeregisterStaticOwnersForLandblock(target);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 3f));
        physics.Engine.ShadowObjects.UpdatePhysicsState(500u, 0x44u);

        Assert.True(CommitPrepared(physics, admission, prepared).Committed);
        Assert.Empty(physics.Engine.ShadowObjects.AllEntriesForDebug());
    }

    [Fact]
    public void SamePrefixCurrentCellMoveAfterSealRebindsToNewRoot()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint firstCell = 0x01010100u;
        const uint secondCell = 0x01010101u;
        RuntimeCollisionAdmission initialAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration initial =
               physics.PrepareCollisionGeneration(initialAdmission))
        {
            physics.StageCollisionAssets(
                initialAdmission,
                initial,
                CollisionAssets(target));
            AddSyntheticCell(initial.DataCache, firstCell);
            AddSyntheticCell(initial.DataCache, secondCell);
            Assert.True(CommitPrepared(
                physics,
                initialAdmission,
                initial).Committed);
        }
        physics.Engine.UpdatePlayerCurrCell(firstCell);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        prepared.DataCache.RemoveCellsForLandblock(target);
        prepared.DataCache.CellGraph.RemoveEnvCellsForLandblock(target);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 5f));
        AddSyntheticCell(prepared.DataCache, firstCell);
        AddSyntheticCell(prepared.DataCache, secondCell);
        _ = SealPrepared(physics, admission, prepared);

        physics.Engine.UpdatePlayerCurrCell(secondCell);
        ObjCell oldRootCell = physics.DataCache.CellGraph.CurrCell!;
        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);

        ObjCell? rebound = physics.DataCache.CellGraph.GetVisible(secondCell);
        Assert.NotNull(rebound);
        Assert.NotSame(oldRootCell, rebound);
        Assert.Same(rebound, physics.DataCache.CellGraph.CurrCell);
    }

    [Fact]
    public void ArmedOwnerLeavingTargetRemainsLiveInItsNewPrefix()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint destination = 0x0202FFFFu;
        foreach (uint landblock in new[] { target, destination })
        {
            RuntimeCollisionAdmission seed =
                physics.BeginCollisionAdmission(landblock);
            using PreparedLandblockCollisionGeneration initial =
                physics.PrepareCollisionGeneration(seed);
            physics.StageCollisionAssets(
                seed,
                initial,
                CollisionAssets(landblock, terrainHeight: 5f));
            Assert.True(CommitPrepared(physics, seed, initial).Committed);
        }

        physics.Engine.ShadowObjects.Register(
            42u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: false);
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 9f));
        _ = SealPrepared(physics, admission, prepared);

        physics.Engine.ShadowObjects.UpdatePosition(
            42u,
            new Vector3(11f, 10f, 0f),
            Quaternion.Identity,
            0f,
            0f,
            destination,
            seedCellId: 0x02020001u);

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.False(physics.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            42u,
            target));
        Assert.True(physics.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            42u,
            destination));
        Assert.Equal(11f, Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug()).Position.X);
    }

    [Fact]
    public void ArmedOwnerIdReuseOutsideTargetKeepsNewIncarnationRows()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint destination = 0x0202FFFFu;
        foreach (uint landblock in new[] { target, destination })
        {
            RuntimeCollisionAdmission seed =
                physics.BeginCollisionAdmission(landblock);
            using PreparedLandblockCollisionGeneration initial =
                physics.PrepareCollisionGeneration(seed);
            physics.StageCollisionAssets(
                seed,
                initial,
                CollisionAssets(landblock));
            Assert.True(CommitPrepared(physics, seed, initial).Committed);
        }
        physics.Engine.ShadowObjects.Register(
            42u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: false);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 8f));
        _ = SealPrepared(physics, admission, prepared);

        physics.Engine.ShadowObjects.Deregister(42u);
        physics.Engine.ShadowObjects.Register(
            42u,
            0x01000002u,
            new Vector3(12f, 10f, 0f),
            Quaternion.Identity,
            0.75f,
            0f,
            0f,
            destination,
            seedCellId: 0x02020001u,
            isStatic: false);

        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        ShadowEntry entry = Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug());
        Assert.Equal(0x01000002u, entry.GfxObjId);
        Assert.Equal(12f, entry.Position.X);
        Assert.True(physics.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            42u,
            destination));
    }

    [Fact]
    public void PrefixOwnerSlotsReuseTombstonesUnderGuidChurn()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration prepared =
               physics.PrepareCollisionGeneration(admission))
        {
            physics.StageCollisionAssets(
                admission,
                prepared,
                CollisionAssets(target));
            Assert.True(CommitPrepared(
                physics,
                admission,
                prepared).Committed);
        }

        for (uint ownerId = 1u; ownerId <= 2_000u; ownerId++)
        {
            physics.Engine.ShadowObjects.Register(
                ownerId,
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u,
                isStatic: false);
            physics.Engine.ShadowObjects.Deregister(ownerId);
        }

        Assert.Equal(0, physics.Engine.ShadowObjects
            .PrefixOwnerSlotCapacityForDiagnostics(target));
        Assert.Equal(0, physics.Engine.ShadowObjects
            .OwnerVersionCountForDiagnostics);
    }

    [Fact]
    public void CommittedPreparationRevokesItsStagingCollisionRoot()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(0x0101FFFFu);
        PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(0x0101FFFFu));
        Assert.True(CommitPrepared(physics, admission, prepared).Committed);

        Assert.Throws<ObjectDisposedException>(
            () => _ = prepared.Engine.LandblockCount);
        prepared.Dispose();
    }

    [Fact]
    public void UnrelatedDemotionAndWithdrawalCannotBeResurrectedByDraft()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint demoted = 0x0101FFFFu;
        const uint withdrawn = 0x0202FFFFu;
        const uint replacement = 0x0303FFFFu;

        RuntimeCollisionAdmission first =
            physics.BeginCollisionAdmission(demoted);
        using (PreparedLandblockCollisionGeneration preparedFirst =
               physics.PrepareCollisionGeneration(first))
        {
            physics.StageCollisionAssets(
                first,
                preparedFirst,
                CollisionAssets(demoted));
            AddSyntheticCell(preparedFirst.DataCache, 0x01010100u);
            Assert.True(CommitPrepared(
                physics,
                first,
                preparedFirst).Committed);
        }
        RuntimeCollisionAdmission second =
            physics.BeginCollisionAdmission(withdrawn);
        using (PreparedLandblockCollisionGeneration preparedSecond =
               physics.PrepareCollisionGeneration(second))
        {
            physics.StageCollisionAssets(
                second,
                preparedSecond,
                CollisionAssets(withdrawn));
            Assert.True(CommitPrepared(
                physics,
                second,
                preparedSecond).Committed);
        }

        RuntimeCollisionAdmission third =
            physics.BeginCollisionAdmission(replacement);
        using PreparedLandblockCollisionGeneration preparedThird =
            physics.PrepareCollisionGeneration(third);
        physics.StageCollisionAssets(
            third,
            preparedThird,
            CollisionAssets(replacement));

        Assert.True(CompleteDemotion(physics, demoted).WasResident);
        Assert.True(CompleteWithdrawal(physics, withdrawn).WasResident);
        Assert.True(CommitPrepared(
            physics,
            third,
            preparedThird).Committed);

        Assert.True(physics.Engine.IsLandblockTerrainResident(demoted));
        Assert.False(physics.DataCache.CellGraph.Contains(0x01010100u));
        Assert.False(physics.Engine.IsLandblockTerrainResident(withdrawn));
        Assert.True(physics.Engine.IsLandblockTerrainResident(replacement));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PendingOrActivePeerRebaseCannotResurrectRetiredLandblock(
        bool withdraw,
        bool beginRebase)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint retired = 0x0101FFFFu;
        const uint survivor = 0x0202FFFFu;
        const uint retiredCell = 0x01010100u;

        RuntimeCollisionAdmission retiredAdmission =
            physics.BeginCollisionAdmission(retired);
        using PreparedLandblockCollisionGeneration retiredPrepared =
            physics.PrepareCollisionGeneration(retiredAdmission);
        physics.StageCollisionAssets(
            retiredAdmission,
            retiredPrepared,
            CollisionAssets(retired, terrainHeight: 7f));
        AddSyntheticCell(retiredPrepared.DataCache, retiredCell);

        RuntimeCollisionAdmission survivorAdmission =
            physics.BeginCollisionAdmission(survivor);
        using PreparedLandblockCollisionGeneration survivorPrepared =
            physics.PrepareCollisionGeneration(survivorAdmission);
        physics.StageCollisionAssets(
            survivorAdmission,
            survivorPrepared,
            CollisionAssets(survivor, terrainHeight: 9f));
        while (!physics.AdvanceCollisionGenerationPreparation(
                   survivorAdmission,
                   survivorPrepared).Completed)
        {
        }

        _ = SealPrepared(physics, retiredAdmission, retiredPrepared);
        Assert.True(CompleteSealedCommit(
            physics,
            retiredAdmission,
            retiredPrepared).Committed);
        if (beginRebase)
        {
            for (int step = 0; step < 12; step++)
            {
                RuntimeCollisionSealStep started =
                    physics.AdvanceCollisionGenerationSeal(
                    survivorAdmission,
                    survivorPrepared);
                Assert.False(started.Completed);
                Assert.InRange(started.WorkUnits, 0, 1);
            }
        }

        if (withdraw)
            Assert.True(CompleteWithdrawal(physics, retired).WasResident);
        else
            Assert.True(CompleteDemotion(physics, retired).WasResident);

        Assert.True(CommitPrepared(
            physics,
            survivorAdmission,
            survivorPrepared).Committed);
        Assert.Equal(!withdraw, physics.Engine.IsLandblockTerrainResident(retired));
        Assert.Null(physics.DataCache.GetCellStruct(retiredCell));
        Assert.False(physics.DataCache.CellGraph.Contains(retiredCell));
        Assert.True(physics.Engine.IsLandblockTerrainResident(survivor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OutgoingStaticDeletionCannotEraseSameIdAuthoredReplacement(
        int deletionPhase)
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint ownerId = 812u;

        RuntimeCollisionAdmission seedAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration seed =
               physics.PrepareCollisionGeneration(seedAdmission))
        {
            physics.StageCollisionAssets(
                seedAdmission,
                seed,
                CollisionAssets(target));
            seed.Engine.ShadowObjects.Register(
                ownerId,
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u,
                isStatic: true);
            Assert.True(CommitPrepared(
                physics,
                seedAdmission,
                seed).Committed);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 4f));
        prepared.Engine.ShadowObjects.Register(
            ownerId,
            0x01000002u,
            new Vector3(13f, 10f, 0f),
            Quaternion.Identity,
            0.75f,
            0f,
            0f,
            target,
            state: 0x55u,
            seedCellId: 0x01010002u,
            isStatic: true);

        if (deletionPhase == 1)
        {
            _ = physics.AdvanceCollisionGenerationPreparation(
                admission,
                prepared);
        }
        else if (deletionPhase == 2)
        {
            while (!physics.AdvanceCollisionGenerationPreparation(
                       admission,
                       prepared).Completed)
            {
            }
        }
        physics.Engine.ShadowObjects.Deregister(ownerId);

        Assert.True(CommitPrepared(physics, admission, prepared).Committed);
        ShadowEntry authored = Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == ownerId);
        Assert.Equal(0x01000002u, authored.GfxObjId);
        Assert.Equal(13f, authored.Position.X);
        Assert.Equal(0x55u, authored.State);
    }

    [Fact]
    public void UnrelatedOwnerEnteringTargetAfterJournalScanWritesThroughExactly()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint outside = 0x0202FFFFu;
        const uint ownerId = 43_000u;

        physics.Engine.ShadowObjects.Register(
            ownerId,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            outside,
            seedCellId: 0x02020001u,
            isStatic: false);
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target));
        physics.Engine.ShadowObjects.UpdatePhysicsState(ownerId, 1u);
        _ = SealPrepared(physics, admission, prepared);

        physics.Engine.ShadowObjects.UpdatePosition(
            ownerId,
            new Vector3(12f, 10f, 0f),
            Quaternion.Identity,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u);
        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.True(physics.Engine.ShadowObjects.HasOwnerRowsInLandblock(
            ownerId,
            target));
    }

    [Fact]
    public void LateSamePrefixMutationOfScannedUnrelatedOwnerWritesThroughExactly()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint outside = 0x0202FFFFu;
        const uint ownerId = 43_500u;

        physics.Engine.ShadowObjects.Register(
            ownerId,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            outside,
            state: 1u,
            seedCellId: 0x02020001u,
            isStatic: false);
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target));
        physics.Engine.ShadowObjects.UpdatePhysicsState(ownerId, 2u);
        _ = SealPrepared(physics, admission, prepared);

        physics.Engine.ShadowObjects.UpdatePosition(
            ownerId,
            new Vector3(14f, 10f, 0f),
            Quaternion.Identity,
            0f,
            0f,
            outside,
            seedCellId: 0x02020001u);
        physics.Engine.ShadowObjects.UpdatePhysicsState(ownerId, 0x55u);
        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        ShadowEntry entry = Assert.Single(
            physics.Engine.ShadowObjects.AllEntriesForDebug(),
            item => item.EntityId == ownerId);
        Assert.Equal(14f, entry.Position.X);
        Assert.Equal(0x55u, entry.State);
    }

    [Fact]
    public void EmptyOwnerPrefixContainersAreReclaimedAcrossUniquePrefixes()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        for (uint index = 1u; index <= 2_000u; index++)
        {
            uint x = index & 0xFFu;
            uint y = (index >> 8) & 0xFFu;
            uint prefix = (x << 24) | (y << 16);
            uint ownerId = 20_000u + index;
            physics.Engine.ShadowObjects.Register(
                ownerId,
                0x01000001u,
                new Vector3(10f, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                prefix | 0xFFFFu,
                seedCellId: prefix | 1u,
                isStatic: false);
            physics.Engine.ShadowObjects.Deregister(ownerId);
        }

        Assert.Equal(0, physics.Engine.ShadowObjects
            .PrefixOwnerContainerCountForDiagnostics);
        Assert.Equal(0, physics.Engine.ShadowObjects
            .OwnerVersionCountForDiagnostics);
    }

    [Fact]
    public void SealCursorRetainsCapturedSlotListsWhenPrefixContainerIsReclaimed()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        const uint ownerId = 41_000u;

        RuntimeCollisionAdmission seedAdmission =
            physics.BeginCollisionAdmission(target);
        using (PreparedLandblockCollisionGeneration seed =
               physics.PrepareCollisionGeneration(seedAdmission))
        {
            physics.StageCollisionAssets(
                seedAdmission,
                seed,
                CollisionAssets(target));
            Assert.True(CommitPrepared(
                physics,
                seedAdmission,
                seed).Committed);
        }
        physics.Engine.ShadowObjects.Register(
            ownerId,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            target,
            seedCellId: 0x01010001u,
            isStatic: false);

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target, terrainHeight: 2f));
        while (!physics.AdvanceCollisionRetainedOwnerCapture(
                   admission,
                   prepared).Completed)
        {
        }
        physics.RefreshCollisionRetainedOwner(admission, prepared, ownerId);
        RuntimeCollisionSealStep started;
        do
        {
            started = physics.AdvanceCollisionGenerationSeal(
                admission,
                prepared);
            Assert.False(started.Completed);
        }
        while (started.WorkUnits == 0);
        Assert.Equal(1, started.WorkUnits);

        physics.Engine.ShadowObjects.Deregister(ownerId);
        Assert.Equal(0, physics.Engine.ShadowObjects
            .PrefixOwnerContainerCountForDiagnostics);

        RuntimeCollisionSealStep seal;
        do
        {
            seal = physics.AdvanceCollisionGenerationSeal(
                admission,
                prepared);
            Assert.InRange(seal.WorkUnits, 0, 1);
        }
        while (!seal.Completed);
        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.Empty(physics.Engine.ShadowObjects.AllEntriesForDebug());
    }

    [Fact]
    public void LiveCurrentCellIsNotRolledBackByTopologyActivation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint first = 0x0101FFFFu;
        const uint second = 0x0202FFFFu;
        foreach (uint landblock in new[] { first, second })
        {
            RuntimeCollisionAdmission seed =
                physics.BeginCollisionAdmission(landblock);
            using PreparedLandblockCollisionGeneration initial =
                physics.PrepareCollisionGeneration(seed);
            physics.StageCollisionAssets(
                seed,
                initial,
                CollisionAssets(landblock));
            Assert.True(CommitPrepared(physics, seed, initial).Committed);
        }

        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(first);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(first, terrainHeight: 7f));
        physics.Engine.UpdatePlayerCurrCell(0x02020001u);
        Assert.Equal(0x02020001u, physics.DataCache.CellGraph.CurrCell?.Id);

        Assert.True(CommitPrepared(physics, admission, prepared).Committed);
        Assert.Equal(0x02020001u, physics.DataCache.CellGraph.CurrCell?.Id);
    }

    [Fact]
    public void SpawnAndDeleteDuringStagingAreBothGenerationGated()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(0x0101FFFFu);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(0x0101FFFFu, terrainHeight: 12f));
        Assert.Empty(SealPrepared(physics, admission, prepared));

        physics.Engine.ShadowObjects.Register(
            77u,
            0x01000001u,
            new Vector3(10f, 10f, 0f),
            Quaternion.Identity,
            0.5f,
            0f,
            0f,
            0x0101FFFFu,
            seedCellId: 0x01010001u,
            isStatic: false);
        physics.Engine.ShadowObjects.Deregister(77u);
        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.Empty(physics.Engine.ShadowObjects.AllEntriesForDebug());
    }

    [Fact]
    public void TwoPostSealOwnersWriteThroughBeforeActivation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimePhysicsState physics = lifetime.Physics;
        const uint target = 0x0101FFFFu;
        RuntimeCollisionAdmission admission =
            physics.BeginCollisionAdmission(target);
        using PreparedLandblockCollisionGeneration prepared =
            physics.PrepareCollisionGeneration(admission);
        physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(target));
        Assert.Empty(SealPrepared(physics, admission, prepared));

        foreach (uint ownerId in new[] { 71u, 72u })
        {
            physics.Engine.ShadowObjects.Register(
                ownerId,
                0x01000001u,
                new Vector3(10f + ownerId - 71u, 10f, 0f),
                Quaternion.Identity,
                0.5f,
                0f,
                0f,
                target,
                seedCellId: 0x01010001u,
                isStatic: false);
        }
        Assert.True(CompleteSealedCommit(
            physics,
            admission,
            prepared).Committed);
        Assert.Equal(
            new[] { 71u, 72u },
            physics.Engine.ShadowObjects.AllEntriesForDebug()
                .Select(entry => entry.EntityId)
                .Order()
                .ToArray());
    }

    [Fact]
    public void TerminalDisposalClearsLandblocksShadowsAndWorksets()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000041u, 1));
        lifetime.Physics.AcknowledgeSpatialProjection(
            record,
            spatial: true);

        RuntimeCollisionAdmission admission =
            lifetime.Physics.BeginCollisionAdmission(0x0101FFFFu);
        using PreparedLandblockCollisionGeneration prepared =
            lifetime.Physics.PrepareCollisionGeneration(admission);
        lifetime.Physics.StageCollisionAssets(
            admission,
            prepared,
            CollisionAssets(0x0101FFFFu));
        _ = CommitPrepared(lifetime.Physics, admission, prepared);
        lifetime.Physics.Engine.ShadowObjects.Register(
            entityId: record.LocalEntityId!.Value,
            gfxObjId: 0x01000001u,
            worldPos: new Vector3(10f, 20f, 5f),
            rotation: Quaternion.Identity,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x0101FFFFu,
            seedCellId: 0x01010001u,
            isStatic: false);

        Assert.False(lifetime.Physics.CaptureOwnership().IsConverged);
        Assert.True(
            lifetime.Physics.Engine.ShadowObjects
                .RetainedRegistrationCount > 0);

        lifetime.Dispose();

        RuntimePhysicsOwnershipSnapshot retired =
            lifetime.Physics.CaptureOwnership();
        Assert.True(retired.IsConverged);
        Assert.Equal(0, retired.LandblockCount);
        Assert.Equal(0, retired.RetainedShadowRegistrationCount);
        Assert.Equal(0, retired.SpatialRootCount);
    }

    [Fact]
    public void RemoteBindingOwnsCanonicalBodyCellAndTypedCellPublication()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000021u, 1));
        var remote = new RemoteMotion();
        var commits = new List<RuntimePhysicsCellCommit>();
        lifetime.Physics.CellCommitted += commits.Add;
        lifetime.Physics.SetRemoteMotion(record, remote);
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);

        remote.CellId = 0x02020001u;

        Assert.Same(remote, record.RemoteMotion);
        Assert.Same(remote.Body, record.PhysicsBody);
        Assert.Equal(0x02020001u, record.FullCellId);
        RuntimePhysicsCellCommit commit = Assert.Single(commits);
        Assert.Same(record, commit.Record);
        Assert.Equal(0x0101FFFFu, commit.PreviousFullCellId);
        Assert.Equal(0x02020001u, commit.FullCellId);
        Assert.True(lifetime.Physics.IsSpatialRemote(record, remote));

        Assert.True(lifetime.Physics.ClearRemoteMotion(record));
        Assert.Null(record.RemoteMotion);
        Assert.Equal(0, lifetime.Physics.SpatialRemoteCount);
    }

    [Fact]
    public void BuiltInRemoteConstructionInstallsCreateVectorsOnlyOnce()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = lifetime.Entities.AddActive(
            Spawn(
                0x70000042u,
                1,
                velocity: new Vector3(40f, 0f, 0f),
                angularVelocity: new Vector3(0f, 0f, 2f)));

        RemoteMotion remote =
            lifetime.Physics.GetOrCreateRemoteMotion(record);

        Assert.Equal(new Vector3(40f, 0f, 0f), remote.Body.Velocity);
        Assert.Equal(new Vector3(0f, 0f, 2f), remote.Body.Omega);
        remote.Body.set_velocity(new Vector3(8f, 0f, 0f));
        remote.Body.Omega = new Vector3(0f, 0f, 3f);

        Assert.Same(
            remote,
            lifetime.Physics.GetOrCreateRemoteMotion(record));
        Assert.Equal(new Vector3(8f, 0f, 0f), remote.Body.Velocity);
        Assert.Equal(new Vector3(0f, 0f, 3f), remote.Body.Omega);
    }

    [Fact]
    public void BuiltInMovementActivationUsesExactRuntimeIncarnation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord ordinary =
            lifetime.Entities.AddActive(Spawn(0x70000043u, 1));
        RemoteMotion ordinaryRemote =
            lifetime.Physics.GetOrCreateRemoteMotion(ordinary);
        ordinary.ObjectClock.Deactivate();
        ordinaryRemote.Body.TransientState &=
            ~TransientStateFlags.Active;

        ordinaryRemote.Movement.ActivatePhysicsObject!();

        Assert.True(ordinary.ObjectClock.IsActive);
        Assert.True(ordinaryRemote.Body.IsActive);

        RuntimeEntityRecord staticRecord =
            lifetime.Entities.AddActive(Spawn(0x70000044u, 1));
        lifetime.Entities.SetFinalPhysicsState(
            staticRecord,
            PhysicsStateFlags.Static);
        RemoteMotion staticRemote =
            lifetime.Physics.GetOrCreateRemoteMotion(staticRecord);
        staticRecord.ObjectClock.Deactivate();
        staticRemote.Body.TransientState &=
            ~TransientStateFlags.Active;

        staticRemote.Movement.ActivatePhysicsObject!();

        Assert.False(staticRecord.ObjectClock.IsActive);
        Assert.False(staticRemote.Body.IsActive);
    }

    [Fact]
    public void RemotePhysicsTickUsesCanonicalRuntimeAndPublishesPoseSnapshot()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000022u, 1));
        var remote = new RemoteMotion();
        remote.Body.Position = new Vector3(10f, 20f, 5f);
        remote.Body.Orientation = Quaternion.Identity;
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        lifetime.Physics.SetRemoteMotion(record, remote);
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        var updater = new RuntimeRemotePhysicsUpdater(lifetime.Physics);
        RuntimeRemotePhysicsSnapshot snapshot = default;
        int publishes = 0;

        Assert.True(updater.Tick(
            record,
            remote,
            objectScale: 1f,
            sequencer: null,
            dt: 0.1f,
            objectClockEpoch: record.ObjectClockEpoch,
            new MotionDeltaFrame
            {
                Origin = Vector3.UnitX,
                Orientation = Quaternion.Identity,
            },
            radius: 0.48f,
            height: 1.835f,
            liveCenterX: 1,
            liveCenterY: 1,
            acknowledgeProjection: value =>
            {
                snapshot = value;
                publishes++;
                return true;
            }));

        Assert.Equal(1, publishes);
        Assert.True(snapshot.Position.X > 10f);
        Assert.Equal(remote.Body.Position, snapshot.Position);
        Assert.Equal(record.FullCellId, snapshot.FullCellId);
    }

    [Fact]
    public void RemoteVelocityStalenessUsesInjectedRuntimeClock()
    {
        var time = new ManualTimeProvider(
            DateTimeOffset.UnixEpoch.AddSeconds(100d));
        using var lifetime = new RuntimeEntityObjectLifetime(
            timeProvider: time);
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000025u, 1));
        var remote = new RemoteMotion
        {
            HasServerVelocity = true,
            LastServerPosTime = 99d,
            ServerVelocity = Vector3.UnitX,
        };
        remote.Body.Position = new Vector3(10f, 20f, 5f);
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        lifetime.Physics.SetRemoteMotion(record, remote);
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        var updater = new RuntimeRemotePhysicsUpdater(lifetime.Physics);
        Vector3? cycle = null;

        Assert.True(updater.Tick(
            record,
            remote,
            objectScale: 1f,
            sequencer: null,
            dt: 0.1f,
            objectClockEpoch: record.ObjectClockEpoch,
            new MotionDeltaFrame
            {
                Orientation = Quaternion.Identity,
            },
            radius: 0.48f,
            height: 1.835f,
            liveCenterX: 1,
            liveCenterY: 1,
            applyStaleVelocityCycle: value => cycle = value));

        Assert.False(remote.HasServerVelocity);
        Assert.Equal(Vector3.Zero, remote.ServerVelocity);
        Assert.Equal(Vector3.Zero, cycle);
    }

    [Fact]
    public void RemotePhysicsTickPushesIsFullyConstrainedFromTheArmedLeash()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000027u, 1));
        var remote = new RemoteMotion();
        remote.Body.Position = new Vector3(10f, 20f, 5f);
        remote.Body.Orientation = Quaternion.Identity;
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        lifetime.Physics.SetRemoteMotion(record, remote);
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);

        EntityPhysicsHost host = new(
            record.ServerGuid,
            getPosition: () => new Position(
                record.FullCellId, remote.Body.Position, remote.Body.Orientation),
            getVelocity: () => remote.Body.Velocity,
            getRadius: () => 0.48f,
            inContact: () => remote.Body.InContact,
            minterpMaxSpeed: () => null,
            curTime: () => 0d,
            physicsTimerTime: () => 0d,
            getObjectA: _ => null,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });
        lifetime.Physics.InstallOrRebindPhysicsHost(record, host);
        remote.MarkFullPhysicsHostBound();
        Assert.Same(host, remote.Host);

        host.PositionManager.ConstrainTo(host.Position, startDistance: 1f, maxDistance: 2f);
        Assert.False(remote.Body.IsFullyConstrained); // stub default, not yet pushed

        var updater = new RuntimeRemotePhysicsUpdater(lifetime.Physics);
        Assert.True(updater.Tick(
            record,
            remote,
            objectScale: 1f,
            sequencer: null,
            dt: 0.1f,
            objectClockEpoch: record.ObjectClockEpoch,
            new MotionDeltaFrame
            {
                Origin = new Vector3(10f, 0f, 0f), // one huge tick, past max
                Orientation = Quaternion.Identity,
            },
            radius: 0.48f,
            height: 1.835f,
            liveCenterX: 1,
            liveCenterY: 1));

        Assert.True(host.PositionManager.IsFullyConstrained());
        Assert.True(remote.Body.IsFullyConstrained);
    }

    [Fact]
    public void PhysicsBodyAcquisitionIsCanonicalAndRejectsGuidReuse()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord first =
            lifetime.Entities.AddActive(Spawn(0x70000023u, 1));
        var body = new PhysicsBody();

        Assert.Same(
            body,
            lifetime.Physics.GetOrCreatePhysicsBody(first, _ => body));
        Assert.Same(
            body,
            lifetime.Physics.GetOrCreatePhysicsBody(
                first,
                _ => throw new InvalidOperationException(
                    "The factory must not replay.")));

        RuntimeEntityRecord stale =
            lifetime.Entities.AddActive(Spawn(0x70000024u, 1));
        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Physics.GetOrCreatePhysicsBody(
                stale,
                _ =>
                {
                    Assert.True(lifetime.Entities.RemoveActive(stale));
                    lifetime.Entities.AddActive(Spawn(0x70000024u, 2));
                    return new PhysicsBody();
                }));
        Assert.Null(stale.PhysicsBody);
        Assert.False(stale.PhysicsBodyAcquisitionInProgress);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public void PhysicsHostIdentityAndLookupBelongToExactRuntimeIncarnation()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000025u, 1));
        EntityPhysicsHost initial = Host(record.ServerGuid, 1f);
        EntityPhysicsHost replacement = Host(record.ServerGuid, 2f);

        Assert.Same(
            initial,
            lifetime.Physics.InstallOrRebindPhysicsHost(record, initial));
        Assert.True(
            lifetime.Physics.TryGetPhysicsHost(
                record.ServerGuid,
                out IPhysicsObjHost resolved));
        Assert.Same(initial, resolved);
        Assert.Same(
            initial,
            lifetime.Physics.InstallOrRebindPhysicsHost(record, replacement));
        Assert.Equal(2f, initial.Position.Frame.Origin.X);

        Assert.True(lifetime.Entities.RemoveActive(record));
        RuntimeEntityRecord next =
            lifetime.Entities.AddActive(Spawn(record.ServerGuid, 2));
        Assert.False(
            lifetime.Physics.TryGetPhysicsHost(
                record.ServerGuid,
                out _));
        Assert.Null(next.PhysicsHost);
    }

    [Fact]
    public void OrdinaryCellCommitIsCanonicalBeforePublication()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record =
            lifetime.Entities.AddActive(Spawn(0x70000026u, 1));
        var body = new PhysicsBody();
        lifetime.Entities.SetPhysicsBody(record, body);
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        RuntimePhysicsCellCommit observed = default;
        lifetime.Physics.CellCommitted += value =>
        {
            Assert.Equal(value.FullCellId, value.Record.FullCellId);
            observed = value;
        };

        Assert.True(lifetime.Physics.CommitOrdinaryCell(
            record,
            body,
            record.ObjectClockEpoch,
            0x02020001u,
            externalOwnerValid: null));

        Assert.Equal(0x02020001u, record.FullCellId);
        Assert.Same(record, observed.Record);
        Assert.Equal(record.SpatialAuthorityVersion, observed.SpatialAuthorityVersion);
    }

    private static RuntimeCollisionGenerationCommit CommitPrepared(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        _ = SealPrepared(physics, admission, prepared);
        return CompleteSealedCommit(physics, admission, prepared);
    }

    private static RuntimeCollisionGenerationCommit CompleteSealedCommit(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        for (int poll = 0; poll < 10_000; poll++)
        {
            RuntimeCollisionGenerationCommit commit =
                physics.CommitCollisionGeneration(admission, prepared);
            if (commit.Committed)
                return commit;
            if (!commit.EngineCommitted)
                _ = SealPrepared(physics, admission, prepared);
        }
        throw new InvalidOperationException(
            "Collision generation did not complete its Runtime mutation transaction.");
    }

    private static RuntimeCollisionMutationResult CompleteDemotion(
        RuntimePhysicsState physics,
        uint landblockId)
    {
        for (int poll = 0; poll < 10_000; poll++)
        {
            RuntimeCollisionMutationResult result =
                physics.DemoteCollisionToTerrain(landblockId);
            if (result.Completed)
                return result;
        }
        throw new InvalidOperationException(
            "Collision demotion did not complete its Runtime mutation transaction.");
    }

    private static RuntimeCollisionMutationResult CompleteWithdrawal(
        RuntimePhysicsState physics,
        uint landblockId)
    {
        for (int poll = 0; poll < 10_000; poll++)
        {
            RuntimeCollisionMutationResult result =
                physics.WithdrawCollision(landblockId);
            if (result.Completed)
                return result;
        }
        throw new InvalidOperationException(
            "Collision withdrawal did not complete its Runtime mutation transaction.");
    }

    private static uint[] SealPrepared(
        RuntimePhysicsState physics,
        RuntimeCollisionAdmission admission,
        PreparedLandblockCollisionGeneration prepared)
    {
        while (true)
        {
            while (!physics.AdvanceCollisionRetainedOwnerCapture(
                       admission,
                       prepared).Completed)
            {
            }
            foreach (uint ownerId in prepared.RetainedOwnerIds)
            {
                physics.RefreshCollisionRetainedOwner(
                    admission,
                    prepared,
                    ownerId);
            }
            RuntimeCollisionSealStep seal;
            do
            {
                seal = physics.AdvanceCollisionGenerationSeal(
                    admission,
                    prepared);
                Assert.InRange(seal.WorkUnits, 0, 1);
            }
            while (!seal.Completed && !seal.Restarted);
            if (seal.Completed)
                break;
        }
        return [.. prepared.RetainedOwnerIds];
    }

    private static void AddSyntheticCell(
        PhysicsDataCache cache,
        uint cellId)
    {
        cache.RegisterCellStructForTest(
            cellId,
            new CellPhysics
            {
                Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            });
        cache.CellGraph.Add(new EnvCell(
            cellId,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<CellPortal>(),
            Array.Empty<uint>(),
            seenOutside: false,
            containmentBsp: null));
    }

    private static BuildingPhysics SyntheticBuilding(Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out Matrix4x4 inverse);
        return new BuildingPhysics
        {
            WorldTransform = transform,
            InverseWorldTransform = inverse,
            Portals = Array.Empty<BldPortalInfo>(),
        };
    }

    private static RuntimeLandblockCollisionAssets CollisionAssets(
        uint landblockId,
        float terrainHeight = 0f)
    {
        var heights = new byte[81];
        var table = new float[256];
        table[0] = terrainHeight;
        return new RuntimeLandblockCollisionAssets(
            landblockId,
            new TerrainSurface(heights, table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f,
            0u);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        Vector3? velocity = null,
        Vector3? angularVelocity = null)
    {
        var position = new CreateObject.ServerPosition(
            0x0101FFFFu,
            10f,
            20f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: 0x408u,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: velocity,
            Acceleration: null,
            AngularVelocity: angularVelocity,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: 0x408u,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static EntityPhysicsHost Host(uint guid, float x) =>
        new(
            guid,
            getPosition: () => new Position(
                0x0101FFFFu,
                new Vector3(x, 0f, 0f),
                Quaternion.Identity),
            getVelocity: () => Vector3.Zero,
            getRadius: () => 0.48f,
            inContact: () => true,
            minterpMaxSpeed: () => null,
            curTime: () => 0d,
            physicsTimerTime: () => 0d,
            getObjectA: _ => null,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });

    private sealed class TestRemoteMotion : IRuntimeRemoteMotion
    {
        public PhysicsBody Body { get; } = new();
    }
}
