using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.World;

public sealed class RuntimePlacementPresentationSinkTests
{
    private const uint SourceCell = 0x01010001u;
    private const uint DestinationCell = 0x01020001u;
    private const uint Guid = 0x7000A101u;

    [Fact]
    public void Place_ReframesAndRebucketsExactSidecarWithoutMutatingRuntimePhysics()
    {
        Fixture fixture = Fixture.Create(twoLandblocks: true);
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);

        record.FullCellId = DestinationCell;
        record.CanonicalLandblockId =
            (DestinationCell & 0xFFFF0000u) | 0xFFFFu;
        record.Canonical.AdvancePlacementCommit();
        RuntimePlacementProjectionSnapshot place = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Place,
            new Vector3(44f, 55f, 66f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f));

        RuntimeOwnershipSnapshot before = RuntimeOwnershipSnapshot.Capture(
            fixture.Runtime,
            record);
        int genericVisibilityCount = 0;
        fixture.Runtime.ProjectionVisibilityChanged += (_, _) =>
            genericVisibilityCount++;

        Assert.True(fixture.Sink.TryApply(in place));

        Assert.Equal(place.WorldPosition, entity.Position);
        Assert.Equal(place.Orientation, entity.Rotation);
        Assert.Equal(DestinationCell, entity.ParentCellId);
        Assert.True(record.IsSpatiallyProjected);
        Assert.True(record.IsSpatiallyVisible);
        Assert.Contains(record, fixture.Runtime.VisibleRecords);
        Assert.True(fixture.Spatial.IsLiveEntityProjectionResident(
            record.ProjectionKey!.Value));
        Assert.Equal(before, RuntimeOwnershipSnapshot.Capture(
            fixture.Runtime,
            record));
        Assert.Equal(0, genericVisibilityCount);
        Assert.Equal(
            place.WorldPosition,
            Assert.Single(fixture.WorldState.Entities).Position);
        Assert.Equal((record, true), Assert.Single(fixture.Visibility));
        Assert.Equal(
            new LocalPlayerShadowState.Snapshot(
                place.WorldPosition,
                place.Orientation,
                DestinationCell),
            fixture.LocalShadow.Current);

        fixture.Spatial.RemoveLandblock(SourceCell | 0xFFFFu);
        Assert.True(fixture.Spatial.IsLiveEntityProjectionResident(
            record.ProjectionKey.Value));
    }

    [Fact]
    public void Withdraw_RemovesOnlyPresentationAndRetainsLogicalRuntimeOwnership()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        RuntimePlacementProjectionSnapshot withdraw = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Withdraw,
            record.WorldEntity!.Position,
            record.WorldEntity.Rotation);
        RuntimeOwnershipSnapshot before = RuntimeOwnershipSnapshot.Capture(
            fixture.Runtime,
            record);
        int genericVisibilityCount = 0;
        fixture.Runtime.ProjectionVisibilityChanged += (_, _) =>
            genericVisibilityCount++;

        Assert.True(fixture.Sink.TryApply(in withdraw));

        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord current));
        Assert.Same(record, current);
        Assert.NotNull(record.WorldEntity);
        Assert.True(record.ResourcesRegistered);
        Assert.False(record.IsSpatiallyProjected);
        Assert.False(record.IsSpatiallyVisible);
        Assert.DoesNotContain(record, fixture.Runtime.VisibleRecords);
        Assert.False(fixture.Spatial.IsLiveEntityProjectionResident(
            record.ProjectionKey!.Value));
        Assert.Equal(before, RuntimeOwnershipSnapshot.Capture(
            fixture.Runtime,
            record));
        Assert.Equal(0, genericVisibilityCount);
        Assert.Empty(fixture.WorldState.Entities);
        Assert.Equal(0, fixture.EffectPoses.Count);
        Assert.Null(fixture.LocalShadow.Current);
        Assert.Equal((record, false), Assert.Single(fixture.Visibility));
        Assert.Equal(Guid, Assert.Single(fixture.ClearedSelection));
    }

    [Fact]
    public void WithdrawalRestored_ReinstatesEveryPresentationRegistrationTheWithdrawalRemoved()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);

        AcDream.Plugin.Abstractions.WorldEntitySnapshot expectedSnapshot =
            Assert.Single(fixture.WorldState.Entities);
        LocalPlayerShadowState.Snapshot? expectedShadow =
            fixture.LocalShadow.Current;
        Assert.NotNull(expectedShadow);
        Assert.Equal(
            expectedSnapshot,
            Assert.Single(CurrentEventMembership(fixture.WorldEvents)));
        Assert.Equal(1, fixture.EffectPoses.Count);
        Vector3 posedPosition = entity.Position;
        Quaternion posedRotation = entity.Rotation;

        RuntimePlacementProjectionSnapshot withdraw = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Withdraw,
            posedPosition,
            posedRotation);
        Assert.True(fixture.Sink.TryApply(in withdraw));
        Assert.Empty(fixture.WorldState.Entities);
        Assert.Empty(CurrentEventMembership(fixture.WorldEvents));
        Assert.Equal(0, fixture.EffectPoses.Count);

        RuntimeOwnershipSnapshot beforeRestore =
            RuntimeOwnershipSnapshot.Capture(fixture.Runtime, record);
        RuntimePlacementProjectionSnapshot restored = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.WithdrawalRestored,
            // Deliberately NOT the entity's pose: the receipt is the exact
            // inverse of a withdrawal, which never moved the sidecar, so the
            // restoration must not move it either.
            new Vector3(-777f, -777f, -777f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.25f));

        Assert.True(fixture.Sink.TryApply(in restored));

        // Graphical projection - what the world render and the radar read.
        Assert.True(
            record.IsSpatiallyProjected,
            "restored park left the entity unprojected");
        Assert.True(
            record.IsSpatiallyVisible,
            "restored park left the entity invisible");
        Assert.Contains(record, fixture.Runtime.VisibleRecords);
        Assert.True(
            fixture.Spatial.IsLiveEntityProjectionResident(
                record.ProjectionKey!.Value),
            "restored park left the entity out of its draw bucket");
        Assert.Equal(SourceCell, entity.ParentCellId);

        // Sink-owned registrations - restored EXACTLY, not defaulted.
        Assert.Equal(
            expectedSnapshot,
            Assert.Single(fixture.WorldState.Entities));
        Assert.Equal(
            expectedSnapshot,
            Assert.Single(CurrentEventMembership(fixture.WorldEvents)));
        Assert.Equal(1, fixture.EffectPoses.Count);
        Assert.True(fixture.EffectPoses.TryGetRootPose(
            entity.Id,
            out Matrix4x4 restoredPose));
        Assert.Equal(
            Matrix4x4.CreateFromQuaternion(posedRotation)
                * Matrix4x4.CreateTranslation(posedPosition),
            restoredPose);
        Assert.Equal(expectedShadow, fixture.LocalShadow.Current);
        Assert.Equal(
            [(record, false), (record, true)],
            fixture.Visibility);

        // The sidecar pose is untouched by the restoration.
        Assert.Equal(posedPosition, entity.Position);
        Assert.Equal(posedRotation, entity.Rotation);

        Assert.Equal(
            beforeRestore,
            RuntimeOwnershipSnapshot.Capture(fixture.Runtime, record));

        Assert.True(fixture.Sink.TryApply(in restored));
        Assert.Equal(
            expectedSnapshot,
            Assert.Single(fixture.WorldState.Entities));
        Assert.Equal(1, fixture.EffectPoses.Count);
        Assert.True(record.IsSpatiallyVisible);
    }

    [Fact]
    public void WithdrawalRestored_IsAcknowledgedEvenWhenTheProjectionIsGone()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        RuntimePlacementProjectionSnapshot restored = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.WithdrawalRestored,
            Vector3.Zero,
            Quaternion.Identity) with
        {
            Token = Placement(
                fixture,
                record,
                RuntimePlacementProjectionKind.WithdrawalRestored,
                Vector3.Zero,
                Quaternion.Identity).Token with
            {
                SessionLifetimeVersion =
                    fixture.Runtime.SessionLifetimeVersion + 99UL,
            },
        };

        Assert.True(fixture.Sink.TryApply(in restored));
        Assert.Empty(fixture.Visibility);
    }

    private static List<AcDream.Plugin.Abstractions.WorldEntitySnapshot>
        CurrentEventMembership(WorldEvents events)
    {
        var replayed = new List<AcDream.Plugin.Abstractions.WorldEntitySnapshot>();
        void Handler(AcDream.Plugin.Abstractions.WorldEntitySnapshot snapshot) =>
            replayed.Add(snapshot);
        events.EntitySpawned += Handler;
        events.EntitySpawned -= Handler;
        return replayed;
    }

    [Fact]
    public void Discard_IsAckOnlyNoOpEvenWhenTokenIsStaleOrSidecarIsGone()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = record.WorldEntity!;
        RuntimePlacementProjectionSnapshot discard = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Discard,
            new Vector3(900f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f)) with
        {
            Token = Placement(fixture, record,
                RuntimePlacementProjectionKind.Place,
                Vector3.Zero,
                Quaternion.Identity).Token with
            {
                SessionLifetimeVersion = ulong.MaxValue,
                PositionAuthorityVersion = ulong.MaxValue,
                ExactCellId = 0xDEAD0001u,
            },
        };
        Vector3 priorPosition = entity.Position;
        Quaternion priorRotation = entity.Rotation;
        bool priorVisible = record.IsSpatiallyVisible;

        Assert.True(fixture.Sink.TryApply(in discard));

        Assert.Equal(priorPosition, entity.Position);
        Assert.Equal(priorRotation, entity.Rotation);
        Assert.Equal(priorVisible, record.IsSpatiallyVisible);
    }

    [Fact]
    public void ExecutorCompleted_IsAckOnlyNoOpEvenWhenTokenIsStaleOrSidecarIsGone()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = record.WorldEntity!;
        RuntimePlacementProjectionSnapshot completion = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.ExecutorCompleted,
            new Vector3(900f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f)) with
        {
            Token = Placement(fixture, record,
                RuntimePlacementProjectionKind.Place,
                Vector3.Zero,
                Quaternion.Identity).Token with
            {
                SessionLifetimeVersion = ulong.MaxValue,
                PositionAuthorityVersion = ulong.MaxValue,
                ExactCellId = 0xDEAD0001u,
            },
        };
        Vector3 priorPosition = entity.Position;
        Quaternion priorRotation = entity.Rotation;
        bool priorVisible = record.IsSpatiallyVisible;

        Assert.True(fixture.Sink.TryApply(in completion));

        Assert.Equal(priorPosition, entity.Position);
        Assert.Equal(priorRotation, entity.Rotation);
        Assert.Equal(priorVisible, record.IsSpatiallyVisible);
    }

    [Fact]
    public void ExecutorCompleted_PoseOnlySupersessionDoesNotSnapSidecarBack()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = record.WorldEntity!;
        var body = new PhysicsBody
        {
            Position = entity.Position,
            Orientation = entity.Rotation,
        };
        record.Canonical.SetPhysicsBody(body);
        RuntimePlacementProjectionSnapshot stale = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.ExecutorCompleted,
            body.Position,
            body.Orientation);

        var currentPosition = body.Position + new Vector3(25f, 10f, 3f);
        Quaternion currentOrientation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            0.75f);
        body.Position = currentPosition;
        body.Orientation = currentOrientation;
        entity.SetPosition(currentPosition);
        entity.Rotation = currentOrientation;

        Assert.True(fixture.Sink.TryApply(in stale));

        Assert.Equal(currentPosition, entity.Position);
        Assert.Equal(currentOrientation, entity.Rotation);
        Assert.Equal(stale.Token.ExactCellId, record.FullCellId);
        Assert.Equal(
            stale.Token.PlacementCommitVersion,
            record.Canonical.PlacementCommitVersion);
    }

    [Fact]
    public void Place_RejectsStaleCanonicalVersionsWithoutChangingSidecar()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = record.WorldEntity!;
        RuntimePlacementProjectionSnapshot stale = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Place,
            new Vector3(90f, 91f, 92f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f));
        record.Canonical.AdvancePlacementCommit();
        Vector3 priorPosition = entity.Position;
        Quaternion priorRotation = entity.Rotation;

        Assert.False(fixture.Sink.TryApply(in stale));

        Assert.Equal(priorPosition, entity.Position);
        Assert.Equal(priorRotation, entity.Rotation);
        Assert.True(record.IsSpatiallyVisible);
    }

    [Fact]
    public void Place_WithoutMaterializedSidecarRemainsPendingForRetry()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRegistrationResult registration = fixture.Runtime.RegisterLiveEntity(
            Spawn(Guid, 1, SourceCell));
        RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(
            registration.Canonical);
        RuntimeEntityKey missingKey = new(0x60000042u, canonical.Incarnation);
        var token = new RuntimePlacementProjectionToken(
            Sequence: 1,
            Revision: 1,
            Entity: missingKey,
            PositionAuthorityVersion: canonical.PositionAuthorityVersion,
            SpatialAuthorityVersion: canonical.SpatialAuthorityVersion,
            PlacementCommitVersion: canonical.PlacementCommitVersion,
            SessionLifetimeVersion: fixture.Runtime.SessionLifetimeVersion,
            ExactCellId: canonical.FullCellId,
            CollisionGeneration: 1,
            Portal: default);
        var place = new RuntimePlacementProjectionSnapshot(
            token,
            RuntimePlacementProjectionKind.Place,
            new Vector3(1f, 2f, 3f),
            Quaternion.Identity,
            Vector3.Zero,
            InContact: false,
            OnWalkable: false);

        Assert.False(fixture.Sink.TryApply(in place));
        Assert.Empty(fixture.Spatial.Entities);
    }

    [Fact]
    public void Place_WithoutLoadedDestinationBackendRemainsPendingAtPriorProjection()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = record.WorldEntity!;
        Vector3 priorPosition = entity.Position;
        record.FullCellId = DestinationCell;
        record.CanonicalLandblockId =
            (DestinationCell & 0xFFFF0000u) | 0xFFFFu;
        record.Canonical.AdvancePlacementCommit();
        RuntimePlacementProjectionSnapshot place = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Place,
            new Vector3(70f, 71f, 72f),
            Quaternion.Identity);

        Assert.False(fixture.Sink.TryApply(in place));

        Assert.Equal(priorPosition, entity.Position);
        Assert.True(record.IsSpatiallyVisible);
        Assert.True(fixture.Spatial.IsLiveEntityProjectionResident(
            record.ProjectionKey!.Value));
        Assert.Empty(fixture.Visibility);
    }

    [Fact]
    public void Place_UsesExactIncarnationAndCannotMutateSameGuidReplacement()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord old = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        RuntimePlacementProjectionSnapshot stale = Placement(
            fixture,
            old,
            RuntimePlacementProjectionKind.Place,
            new Vector3(88f, 77f, 66f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.25f));

        LiveEntityRecord replacement = fixture.Materialize(
            Spawn(Guid, 2, SourceCell));
        WorldEntity replacementEntity = replacement.WorldEntity!;
        Vector3 priorPosition = replacementEntity.Position;
        Quaternion priorRotation = replacementEntity.Rotation;

        Assert.False(fixture.Sink.TryApply(in stale));

        Assert.Equal(priorPosition, replacementEntity.Position);
        Assert.Equal(priorRotation, replacementEntity.Rotation);
        Assert.True(replacement.IsSpatiallyVisible);
    }

    [Fact]
    public void PortalPlace_StaleTransitHostOrSequenceIsAcknowledgedAndIgnored()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        RuntimePlacementProjectionSnapshot ordinary = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Place,
            new Vector3(21f, 22f, 23f),
            Quaternion.Identity);
        RuntimePortalPlacementAuthority authority = fixture.BeginPortal(
            SourceCell,
            teleportSequence: 9);
        RuntimePlacementProjectionSnapshot current = ordinary with
        {
            Token = ordinary.Token with { Portal = authority },
        };

        Assert.True(fixture.Sink.TryApply(in current));
        Assert.Equal(current.WorldPosition, record.WorldEntity!.Position);

        Assert.True(fixture.Transit.BeginHostProjectionSupersession(
            authority.Projection));
        RuntimePlacementProjectionSnapshot superseded = current with
        {
            WorldPosition = new Vector3(80f, 81f, 82f),
        };
        Assert.True(fixture.Sink.TryApply(in superseded));
        Assert.Equal(current.WorldPosition, record.WorldEntity.Position);

        RuntimePlacementProjectionSnapshot wrongSequence = current with
        {
            Token = current.Token with
            {
                Portal = authority with { TeleportSequence = 10 },
            },
            WorldPosition = new Vector3(90f, 91f, 92f),
        };
        Assert.True(fixture.Sink.TryApply(in wrongSequence));
        Assert.Equal(current.WorldPosition, record.WorldEntity.Position);
    }

    [Fact]
    public void PlaceAndWithdraw_DoNotMutateRemoteBodyOrRuntimeOwnership()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        RemoteMotion remote = fixture.Runtime.GetOrCreateRemoteMotionRuntime(Guid);
        remote.Body.Position = new Vector3(4f, 5f, 6f);
        remote.Body.Orientation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitY,
            0.4f);
        remote.Body.State = PhysicsStateFlags.Gravity
            | PhysicsStateFlags.ReportCollisions;
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact;
        remote.Body.InWorld = true;
        remote.Body.LastUpdateTime = 42.5;
        PhysicsBodySnapshot bodyBefore = PhysicsBodySnapshot.Capture(remote.Body);
        RuntimePhysicsOwnershipSnapshot runtimeBefore =
            fixture.Runtime.Physics.CaptureOwnership();
        RuntimePlacementProjectionSnapshot place = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Place,
            new Vector3(30f, 31f, 32f),
            Quaternion.Identity);

        Assert.True(fixture.Sink.TryApply(in place));
        Assert.Equal(bodyBefore, PhysicsBodySnapshot.Capture(remote.Body));
        Assert.Equal(runtimeBefore, fixture.Runtime.Physics.CaptureOwnership());

        RuntimePlacementProjectionSnapshot withdraw = place with
        {
            Kind = RuntimePlacementProjectionKind.Withdraw,
        };
        Assert.True(fixture.Sink.TryApply(in withdraw));
        Assert.Equal(bodyBefore, PhysicsBodySnapshot.Capture(remote.Body));
        Assert.Equal(runtimeBefore, fixture.Runtime.Physics.CaptureOwnership());
    }

    [Fact]
    public void Withdraw_DoesNotMutateProjectileBodyShadowOrWorksetOwnership()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        PhysicsBody body = fixture.Runtime.GetOrCreatePhysicsBody(
            Guid,
            _ => new PhysicsBody());
        body.Position = new Vector3(7f, 8f, 9f);
        body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f);
        body.State = PhysicsStateFlags.Missile
            | PhysicsStateFlags.ReportCollisions;
        body.TransientState = TransientStateFlags.Active;
        body.InWorld = true;
        body.LastUpdateTime = 99.25;
        fixture.Runtime.BindProjectileRuntime(
            Guid,
            body,
            new ProjectileCollisionSphere(Vector3.Zero, 0.25f));
        PhysicsBodySnapshot bodyBefore = PhysicsBodySnapshot.Capture(body);
        RuntimePhysicsOwnershipSnapshot runtimeBefore =
            fixture.Runtime.Physics.CaptureOwnership();
        RuntimePlacementProjectionSnapshot withdraw = Placement(
            fixture,
            record,
            RuntimePlacementProjectionKind.Withdraw,
            record.WorldEntity!.Position,
            record.WorldEntity.Rotation);

        Assert.True(fixture.Sink.TryApply(in withdraw));

        Assert.Equal(bodyBefore, PhysicsBodySnapshot.Capture(body));
        Assert.Equal(runtimeBefore, fixture.Runtime.Physics.CaptureOwnership());
        Assert.Same(body, record.ProjectileRuntime!.Body);
    }

    [Fact]
    public void FailedPresentationTail_RetriesSameReceiptIdempotently()
    {
        using var fixture = SubscriptionFixture.Create();
        fixture.VisibilityFailuresRemaining = 1;
        using var subscription = new RuntimePlacementProjectionSubscription(
            fixture.Lifetime.Placements,
            () => fixture.Generation,
            fixture.Sink);

        Assert.True(fixture.Lifetime.Physics.SetPosition.Cancel(
            fixture.Record.Canonical,
            publishWithdrawal: true));

        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.Equal(1, fixture.Lifetime.Events.DispatchFailureCount);
        Assert.IsType<InvalidOperationException>(
            fixture.Lifetime.Events.LastDispatchFailure);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);

        Assert.True(subscription.RetryPending());

        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.False(fixture.Record.IsSpatiallyProjected);
        Assert.Empty(fixture.WorldState.Entities);
        Assert.Equal(0, fixture.EffectPoses.Count);
        Assert.Equal(2, fixture.Visibility.Count);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);
    }

    [Fact]
    public void GraphicalRoute_ReentrantInitialDrainTeardownLeavesHeadForReplacement()
    {
        using var fixture = SubscriptionFixture.Create();
        Assert.True(fixture.Lifetime.Physics.SetPosition.Cancel(
            fixture.Record.Canonical,
            publishWithdrawal: true));
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);

        RuntimeGenerationToken generation = fixture.Generation;
        var retries = new RuntimePlacementProjectionRetrySlot(
            () => generation);
        var firstEvents = new RecordingEventRoute();
        GraphicalSessionEventRoute? first = null;
        int applications = 0;
        var sink = new DelegatePlacementSink(
            (in RuntimePlacementProjectionSnapshot projection) =>
        {
            applications++;
            bool applied = fixture.Sink.TryApply(in projection);
            if (applications == 1)
                first!.Dispose();
            return applied;
        });
        first = new GraphicalSessionEventRoute(
            firstEvents,
            () => new RuntimePlacementProjectionSubscription(
                fixture.Lifetime.Placements,
                () => generation,
                sink,
                retryPendingOnSubscribe: false),
            () => generation,
            retries);

        first.Attach();

        Assert.Equal(1, applications);
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.Equal(0, retries.BindingCount);
        Assert.Equal(0, fixture.Lifetime.Events.PlacementSubscriberCount);
        Assert.Equal(1, firstEvents.DisposeCount);

        var replacementEvents = new RecordingEventRoute();
        var replacement = new GraphicalSessionEventRoute(
            replacementEvents,
            () => new RuntimePlacementProjectionSubscription(
                fixture.Lifetime.Placements,
                () => generation,
                sink,
                retryPendingOnSubscribe: false),
            () => generation,
            retries);
        replacement.Attach();

        Assert.Equal(2, applications);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.Equal(1, retries.BindingCount);
        Assert.Equal(1, fixture.Lifetime.Events.PlacementSubscriberCount);

        replacement.Dispose();

        Assert.Equal(0, retries.BindingCount);
        Assert.Equal(0, fixture.Lifetime.Events.PlacementSubscriberCount);
        Assert.Equal(1, replacementEvents.DisposeCount);
    }

    [Fact]
    public void RetrySlot_InvokesOnlyExactCurrentGenerationBinding()
    {
        RuntimeGenerationToken generation = new(7UL);
        var retries = new RuntimePlacementProjectionRetrySlot(
            () => generation);
        int firstCalls = 0;
        IDisposable first = retries.BindOwned(
            generation,
            () =>
            {
                firstCalls++;
                return true;
            });

        retries.RetryPending();
        generation = new RuntimeGenerationToken(8UL);
        retries.RetryPending();
        first.Dispose();

        int replacementCalls = 0;
        using IDisposable replacement = retries.BindOwned(
            generation,
            () =>
            {
                replacementCalls++;
                return true;
            });
        first.Dispose();
        retries.RetryPending();

        Assert.Equal(1, firstCalls);
        Assert.Equal(1, replacementCalls);
        Assert.Equal(1, retries.BindingCount);
    }

    private static RuntimePlacementProjectionSnapshot Placement(
        Fixture fixture,
        LiveEntityRecord record,
        RuntimePlacementProjectionKind kind,
        Vector3 position,
        Quaternion orientation)
    {
        RuntimeEntityRecord canonical = record.Canonical;
        var token = new RuntimePlacementProjectionToken(
            Sequence: 1,
            Revision: 1,
            Entity: record.ProjectionKey!.Value,
            PositionAuthorityVersion: canonical.PositionAuthorityVersion,
            SpatialAuthorityVersion: canonical.SpatialAuthorityVersion,
            PlacementCommitVersion: canonical.PlacementCommitVersion,
            SessionLifetimeVersion: fixture.Runtime.SessionLifetimeVersion,
            ExactCellId: canonical.FullCellId,
            CollisionGeneration: 1,
            Portal: default);
        return new RuntimePlacementProjectionSnapshot(
            token,
            kind,
            position,
            orientation,
            CellLocalPosition: position,
            InContact: false,
            OnWalkable: false);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
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
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
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
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
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
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private readonly record struct RuntimeOwnershipSnapshot(
        ulong PositionAuthorityVersion,
        ulong SpatialAuthorityVersion,
        ulong PlacementCommitVersion,
        ulong ObjectClockEpoch,
        bool ObjectClockIsActive,
        int SpatialRootCount,
        int SpatialRemoteCount,
        int SpatialProjectileCount,
        RuntimePhysicsOwnershipSnapshot Physics)
    {
        internal static RuntimeOwnershipSnapshot Capture(
            LiveEntityRuntime runtime,
            LiveEntityRecord record) => new(
                record.Canonical.PositionAuthorityVersion,
                record.Canonical.SpatialAuthorityVersion,
                record.Canonical.PlacementCommitVersion,
                record.ObjectClockEpoch,
                record.ObjectClock.IsActive,
                runtime.SpatialRootObjectCount,
                runtime.SpatialRemoteMotionRuntimeCount,
                runtime.SpatialProjectileRuntimeCount,
                runtime.Physics.CaptureOwnership());
    }

    private readonly record struct PhysicsBodySnapshot(
        Vector3 Position,
        Quaternion Orientation,
        PhysicsStateFlags State,
        TransientStateFlags TransientState,
        bool InWorld,
        double LastUpdateTime)
    {
        internal static PhysicsBodySnapshot Capture(PhysicsBody body) => new(
            body.Position,
            body.Orientation,
            body.State,
            body.TransientState,
            body.InWorld,
            body.LastUpdateTime);
    }

    private sealed class Fixture
    {
        private Fixture(
            GpuWorldState spatial,
            LiveEntityRuntime runtime,
            RuntimeWorldTransitState transit,
            WorldGameState worldState,
            WorldEvents worldEvents,
            EntityEffectPoseRegistry effectPoses,
            LocalPlayerShadowState localShadow,
            LocalPlayerShadowSynchronizer synchronizer)
        {
            Spatial = spatial;
            Runtime = runtime;
            Transit = transit;
            WorldState = worldState;
            WorldEvents = worldEvents;
            EffectPoses = effectPoses;
            LocalShadow = localShadow;
            Synchronizer = synchronizer;
            Sink = new RuntimePlacementPresentationSink(
                runtime,
                transit,
                worldState,
                worldEvents,
                effectPoses,
                synchronizer,
                () => Guid,
                ClearedSelection.Add,
                [
                    (record, visible) =>
                    {
                        Visibility.Add((record, visible));
                        if (VisibilityFailuresRemaining > 0)
                        {
                            VisibilityFailuresRemaining--;
                            throw new InvalidOperationException(
                                "fixture presentation failure");
                        }
                    },
                ]);
        }

        internal GpuWorldState Spatial { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal RuntimeWorldTransitState Transit { get; }
        internal WorldGameState WorldState { get; }
        internal WorldEvents WorldEvents { get; }
        internal EntityEffectPoseRegistry EffectPoses { get; }
        internal LocalPlayerShadowState LocalShadow { get; }
        internal LocalPlayerShadowSynchronizer Synchronizer { get; }
        internal List<(LiveEntityRecord Record, bool Visible)> Visibility { get; } = [];
        internal List<uint> ClearedSelection { get; } = [];
        internal int VisibilityFailuresRemaining { get; set; }
        internal RuntimePlacementPresentationSink Sink { get; }

        internal static Fixture Create(bool twoLandblocks = false)
        {
            var physics = new PhysicsEngine { DataCache = new PhysicsDataCache() };
            physics.AddLandblock(
                SourceCell & 0xFFFF0000u,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            if (twoLandblocks)
            {
                physics.AddLandblock(
                    DestinationCell & 0xFFFF0000u,
                    new TerrainSurface(new byte[81], new float[256]),
                    Array.Empty<CellSurface>(),
                    Array.Empty<PortalPlane>(),
                    worldOffsetX: 192f,
                    worldOffsetY: 0f);
            }
            var spatial = new GpuWorldState();
            spatial.AddLandblock(EmptyLandblock(SourceCell | 0xFFFFu));
            if (twoLandblocks)
                spatial.AddLandblock(EmptyLandblock(DestinationCell | 0xFFFFu));
            var resources = new RecordingResources();
            LiveEntityRuntime runtime = LiveEntityRuntimeFixture.Create(
                spatial,
                resources,
                physics);
            var identity = new LocalPlayerIdentityState { ServerGuid = Guid };
            var origin = new LiveWorldOriginState();
            origin.SetPlaceholder(0, 0);
            var localShadow = new LocalPlayerShadowState();
            var synchronizer = new LocalPlayerShadowSynchronizer(
                physics,
                runtime,
                identity,
                origin,
                localShadow);
            return new Fixture(
                spatial,
                runtime,
                new RuntimeWorldTransitState(),
                new WorldGameState(),
                new WorldEvents(),
                new EntityEffectPoseRegistry(),
                localShadow,
                synchronizer);
        }

        internal LiveEntityRecord Materialize(WorldSession.EntitySpawn spawn)
        {
            LiveEntityRecord record = Runtime.RegisterAndMaterializeProjection(spawn);
            Assert.False(Runtime.HasActiveInitialCreateResidence(
                record.Canonical));
            Assert.True(record.ResourcesRegistered);
            WorldEntity entity = record.WorldEntity!;
            var snapshot = new AcDream.Plugin.Abstractions.WorldEntitySnapshot(
                entity.Id,
                entity.SourceGfxObjOrSetupId,
                entity.Position,
                entity.Rotation);
            WorldState.Add(snapshot);
            WorldEvents.UpsertCurrent(snapshot);
            EffectPoses.PublishMeshRefs(entity);
            if (record.ServerGuid == Guid)
            {
                LocalShadow.Set(
                    entity.Position,
                    entity.Rotation,
                    record.FullCellId);
            }
            return record;
        }

        internal RuntimePortalPlacementAuthority BeginPortal(
            uint cell,
            ushort teleportSequence)
        {
            Assert.True(Transit.TryQueueTeleportStart(teleportSequence));
            Assert.True(Transit.ActivateQueuedTeleport());
            Assert.True(Transit.OfferTeleportDestination(
                new RuntimeTeleportDestination(
                    Guid,
                    InstanceSequence: 1,
                    PositionSequence: 1,
                    TeleportSequence: teleportSequence,
                    ForcePositionSequence: 1,
                    new Position(
                        cell,
                        new Vector3(1f, 2f, 3f),
                        Quaternion.Identity)),
                teleportTimestampAdvanced: true));
            Assert.True(Transit.TryBeginPortalReveal(
                teleportSequence,
                cell,
                out long generation));
            Assert.True(Transit.TryRegisterHostProjection(
                generation,
                cell,
                out RuntimeWorldHostProjectionToken host));
            return new RuntimePortalPlacementAuthority(
                true,
                generation,
                teleportSequence,
                host);
        }

        private static LoadedLandblock EmptyLandblock(uint canonicalId) =>
            new(canonicalId, new LandBlock(), Array.Empty<WorldEntity>());
    }

    private sealed class SubscriptionFixture : IDisposable
    {
        private SubscriptionFixture(
            RuntimeEntityObjectLifetime lifetime,
            LiveEntityRuntime runtime,
            LiveEntityRecord record,
            RuntimePlacementPresentationSink sink,
            WorldGameState worldState,
            EntityEffectPoseRegistry effectPoses,
            List<(LiveEntityRecord Record, bool Visible)> visibility)
        {
            Lifetime = lifetime;
            Runtime = runtime;
            Record = record;
            Sink = sink;
            WorldState = worldState;
            EffectPoses = effectPoses;
            Visibility = visibility;
        }

        internal RuntimeGenerationToken Generation { get; } = new(7UL);
        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal LiveEntityRecord Record { get; }
        internal RuntimePlacementPresentationSink Sink { get; }
        internal WorldGameState WorldState { get; }
        internal EntityEffectPoseRegistry EffectPoses { get; }
        internal List<(LiveEntityRecord Record, bool Visible)> Visibility { get; }
        internal int VisibilityFailuresRemaining { get; set; }

        internal static SubscriptionFixture Create()
        {
            PhysicsEngine engine = FlatEngine();
            var lifetime = new RuntimeEntityObjectLifetime(engine);
            RuntimeGenerationToken generation = new(7UL);
            lifetime.BindEventContext(() => generation, static () => 11UL);
            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                SourceCell | 0xFFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            var runtime = new LiveEntityRuntime(
                spatial,
                new RecordingResources(),
                lifetime);
            LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(
                Spawn(Guid, 1, SourceCell));
            PhysicsBody body = runtime.GetOrCreatePhysicsBody(
                Guid,
                _ => new PhysicsBody
                {
                    Position = new Vector3(10f, 10f, 5f),
                    Orientation = Quaternion.Identity,
                    LastUpdateTime = 1d,
                    State = PhysicsStateFlags.ReportCollisions,
                    TransientState = TransientStateFlags.Active,
                });
            body.SnapToCell(
                SourceCell,
                body.Position,
                body.Position);

            var worldState = new WorldGameState();
            var worldEvents = new WorldEvents();
            var effectPoses = new EntityEffectPoseRegistry();
            var localShadow = new LocalPlayerShadowState();
            var localShadowIdentity = new LocalPlayerIdentityState { ServerGuid = Guid };
            var localShadowOrigin = new LiveWorldOriginState();
            localShadowOrigin.SetPlaceholder(0, 0);
            var synchronizer = new LocalPlayerShadowSynchronizer(
                engine,
                runtime,
                localShadowIdentity,
                localShadowOrigin,
                localShadow);
            WorldEntity entity = record.WorldEntity!;
            var snapshot = new AcDream.Plugin.Abstractions.WorldEntitySnapshot(
                entity.Id,
                entity.SourceGfxObjOrSetupId,
                entity.Position,
                entity.Rotation);
            worldState.Add(snapshot);
            worldEvents.UpsertCurrent(snapshot);
            effectPoses.PublishMeshRefs(entity);
            localShadow.Set(entity.Position, entity.Rotation, record.FullCellId);
            var visibility = new List<(LiveEntityRecord Record, bool Visible)>();
            SubscriptionFixture? fixture = null;
            var sink = new RuntimePlacementPresentationSink(
                runtime,
                new RuntimeWorldTransitState(),
                worldState,
                worldEvents,
                effectPoses,
                synchronizer,
                () => Guid,
                _ => { },
                [
                    (candidate, visible) =>
                    {
                        visibility.Add((candidate, visible));
                        if (fixture!.VisibilityFailuresRemaining > 0)
                        {
                            fixture.VisibilityFailuresRemaining--;
                            throw new InvalidOperationException(
                                "fixture presentation failure");
                        }
                    },
                ]);
            fixture = new SubscriptionFixture(
                lifetime,
                runtime,
                record,
                sink,
                worldState,
                effectPoses,
                visibility);
            return fixture;
        }

        public void Dispose()
        {
            Runtime.Clear();
            Lifetime.Dispose();
        }

        private static PhysicsEngine FlatEngine()
        {
            var engine = new PhysicsEngine
            {
                DataCache = new PhysicsDataCache(),
            };
            engine.AddLandblock(
                SourceCell & 0xFFFF0000u,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            return engine;
        }
    }

    private sealed class DelegatePlacementSink(
        PlacementApply apply) : IRuntimePlacementProjectionSink
    {
        public bool TryApply(
            in RuntimePlacementProjectionSnapshot projection) =>
            apply(in projection);
    }

    private delegate bool PlacementApply(
        in RuntimePlacementProjectionSnapshot projection);

    private sealed class RecordingEventRoute : ILiveSessionEventRouting
    {
        public int AttachCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Attach() => AttachCount++;

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }
}
