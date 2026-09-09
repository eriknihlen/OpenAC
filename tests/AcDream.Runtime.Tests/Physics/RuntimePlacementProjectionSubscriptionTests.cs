using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimePlacementProjectionSubscriptionTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;

    [Fact]
    public void SuccessfulSinkAcknowledgesExactPlaceSynchronously()
    {
        using var fixture = new Fixture();
        var sink = new RecordingSink();
        using var subscription = fixture.Subscribe(sink);

        RuntimeSetPositionOutcome outcome = fixture.Place(
            fixture.First,
            new Vector3(12f, 18f, 7f));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        RuntimePlacementProjectionSnapshot only = Assert.Single(sink.Applied);
        Assert.Equal(RuntimePlacementProjectionKind.Place, only.Kind);
        Assert.Equal(outcome.Projection, only.Token);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);
    }

    [Fact]
    public void RejectedSinkRetainsHeadAndRetryAppliesItExactly()
    {
        using var fixture = new Fixture();
        bool ready = false;
        var sink = new RecordingSink(_ => ready);
        using var subscription = fixture.Subscribe(sink);
        RuntimeSetPositionOutcome outcome = fixture.Place(
            fixture.First,
            new Vector3(13f, 18f, 7f));

        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.Single(sink.Applied);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);

        ready = true;
        Assert.True(subscription.RetryPending());

        Assert.Equal(2, sink.Applied.Count);
        Assert.All(sink.Applied,
            projection => Assert.Equal(outcome.Projection, projection.Token));
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
    }

    [Fact]
    public void ThrowingSinkLeavesReceiptRetryableAndRecordsDispatchFailure()
    {
        using var fixture = new Fixture();
        bool fail = true;
        var sink = new RecordingSink(_ =>
        {
            if (fail)
                throw new InvalidOperationException("host unavailable");
            return true;
        });
        using var subscription = fixture.Subscribe(sink);
        _ = fixture.Place(fixture.First, new Vector3(14f, 18f, 7f));

        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.Equal(1, fixture.Lifetime.Events.DispatchFailureCount);
        Assert.IsType<InvalidOperationException>(
            fixture.Lifetime.Events.LastDispatchFailure);

        fail = false;
        Assert.True(subscription.RetryPending());

        Assert.Equal(2, sink.Applied.Count);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
    }

    [Fact]
    public void LaterReceiptCannotProjectBeforeRejectedFifoHead()
    {
        using var fixture = new Fixture(secondEntity: true);
        bool ready = false;
        RuntimeEntityKey firstKey = fixture.First.Key!.Value;
        var sink = new RecordingSink(projection =>
            ready || projection.Token.Entity != firstKey);
        using var subscription = fixture.Subscribe(sink);

        _ = fixture.Place(fixture.First, new Vector3(15f, 18f, 7f));
        _ = fixture.Place(fixture.Second!, new Vector3(16f, 18f, 7f));

        RuntimePlacementProjectionSnapshot initial = Assert.Single(sink.Applied);
        Assert.Equal(firstKey, initial.Token.Entity);
        Assert.Equal(2, fixture.Lifetime.Placements.PendingCount);

        ready = true;
        Assert.True(subscription.RetryPending());

        Assert.Equal(
            [firstKey, firstKey, fixture.Second!.Key!.Value],
            sink.Applied.Select(item => item.Token.Entity).ToArray());
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
    }

    [Fact]
    public void ReentrantReplacementProjectsRevisedDiscardBeforeAcknowledging()
    {
        using var fixture = new Fixture();
        RuntimeEntityPlacementToken replacement = default;
        var sink = new RecordingSink(projection =>
        {
            if (projection.Kind is RuntimePlacementProjectionKind.Place)
            {
                replacement = fixture.Lifetime.Physics.SetPosition
                    .BeginAcceptedPlacement(
                        fixture.First,
                        fixture.First.PositionAuthorityVersion,
                        RuntimeSetPositionOperationKind.RemoteAuthoritative);
            }
            return true;
        });
        using var subscription = fixture.Subscribe(sink);

        RuntimeSetPositionOutcome original = fixture.Place(
            fixture.First,
            new Vector3(17f, 18f, 7f));

        Assert.True(replacement.IsValid);
        Assert.Equal(2, sink.Applied.Count);
        Assert.Equal(RuntimePlacementProjectionKind.Place,
            sink.Applied[0].Kind);
        Assert.Equal(RuntimePlacementProjectionKind.Discard,
            sink.Applied[1].Kind);
        Assert.Equal(original.Projection.Sequence,
            sink.Applied[1].Token.Sequence);
        Assert.True(sink.Applied[1].Token.Revision
            > original.Projection.Revision);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);

        fixture.Lifetime.Physics.SetPosition.Forget(
            fixture.First,
            releasePreparedMover: true);
    }

    [Fact]
    public void ConstructorDrainsReceiptsCreatedBeforeSubscriptionInFifoOrder()
    {
        using var fixture = new Fixture(secondEntity: true);
        RuntimeSetPositionOutcome first = fixture.Place(
            fixture.First,
            new Vector3(18f, 18f, 7f));
        RuntimeSetPositionOutcome second = fixture.Place(
            fixture.Second!,
            new Vector3(19f, 18f, 7f));
        Assert.Equal(2, fixture.Lifetime.Placements.PendingCount);
        var sink = new RecordingSink();

        using var subscription = fixture.Subscribe(sink);

        Assert.Equal(
            [first.Projection, second.Projection],
            sink.Applied.Select(item => item.Token).ToArray());
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);
    }

    [Fact]
    public void SubscribeWithoutDrainPublishesOnlyAfterExplicitRetry()
    {
        using var fixture = new Fixture();
        RuntimeSetPositionOutcome pending = fixture.Place(
            fixture.First,
            new Vector3(18.5f, 18f, 7f));
        var sink = new RecordingSink();

        using var subscription = fixture.Subscribe(
            sink,
            retryPendingOnSubscribe: false);

        Assert.Empty(sink.Applied);
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);

        Assert.True(subscription.RetryPending());

        Assert.Equal(pending.Projection, Assert.Single(sink.Applied).Token);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
    }

    [Fact]
    public void SyntheticWithdrawalUsesTheSameExactSinkAndAckPath()
    {
        using var fixture = new Fixture();
        var sink = new RecordingSink();
        using var subscription = fixture.Subscribe(sink);

        Assert.True(fixture.Lifetime.Physics.SetPosition.Cancel(
            fixture.First,
            publishWithdrawal: true));

        RuntimePlacementProjectionSnapshot withdrawal = Assert.Single(
            sink.Applied);
        Assert.Equal(RuntimePlacementProjectionKind.Withdraw, withdrawal.Kind);
        Assert.Equal(fixture.First.Key, withdrawal.Token.Entity);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);
    }

    [Fact]
    public void DisposeUnsubscribesWithoutConsumingPendingRuntimeReceipt()
    {
        using var fixture = new Fixture();
        var sink = new RecordingSink(_ => false);
        var subscription = fixture.Subscribe(sink);
        _ = fixture.Place(fixture.First, new Vector3(18f, 18f, 7f));
        Assert.Equal(1, fixture.Lifetime.Events.PlacementSubscriberCount);

        subscription.Dispose();

        Assert.Equal(0, fixture.Lifetime.Events.PlacementSubscriberCount);
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.Throws<ObjectDisposedException>(() =>
            subscription.RetryPending());

        var replacementSink = new RecordingSink();
        using var replacement = fixture.Subscribe(replacementSink);
        Assert.Single(replacementSink.Applied);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
    }

    [Fact]
    public void ReentrantSinkDisposalNeverAcknowledgesAfterHostTeardown()
    {
        using var fixture = new Fixture();
        RuntimePlacementProjectionSubscription? subscription = null;
        var sink = new RecordingSink(_ =>
        {
            subscription!.Dispose();
            return true;
        });
        subscription = fixture.Subscribe(sink);

        _ = fixture.Place(fixture.First, new Vector3(19f, 18f, 7f));

        Assert.Single(sink.Applied);
        Assert.Equal(0, fixture.Lifetime.Events.PlacementSubscriberCount);
        Assert.Equal(1, fixture.Lifetime.Placements.PendingCount);
        Assert.False(subscription.HasAppliedReceiptAwaitingAcknowledgement);

        var replacementSink = new RecordingSink();
        using var replacement = fixture.Subscribe(replacementSink);
        Assert.Single(replacementSink.Applied);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RuntimeGenerationToken _generation = new(7UL);

        internal Fixture(bool secondEntity = false)
        {
            Lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
            Lifetime.BindEventContext(
                () => _generation,
                static () => 11UL);
            First = CreateRecord(Lifetime, 0x70002001u, 1);
            AttachBody(Lifetime, First, new Vector3(10f, 20f, 7f));
            if (secondEntity)
            {
                Second = CreateRecord(Lifetime, 0x70002002u, 1);
                AttachBody(Lifetime, Second, new Vector3(11f, 20f, 7f));
            }
        }

        internal RuntimeEntityObjectLifetime Lifetime { get; }
        internal RuntimeEntityRecord First { get; }
        internal RuntimeEntityRecord? Second { get; }

        internal RuntimePlacementProjectionSubscription Subscribe(
            IRuntimePlacementProjectionSink sink,
            bool retryPendingOnSubscribe = true) => new(
                Lifetime.Placements,
                () => _generation,
                sink,
                retryPendingOnSubscribe);

        internal RuntimeSetPositionOutcome Place(
            RuntimeEntityRecord record,
            Vector3 position) => Lifetime.Physics.SetPosition.Apply(
                record,
                record.PositionAuthorityVersion,
                Command(position));

        public void Dispose() => Lifetime.Dispose();
    }

    private sealed class RecordingSink(
        Func<RuntimePlacementProjectionSnapshot, bool>? apply = null)
        : IRuntimePlacementProjectionSink
    {
        internal List<RuntimePlacementProjectionSnapshot> Applied { get; } = [];

        public bool TryApply(
            in RuntimePlacementProjectionSnapshot projection)
        {
            Applied.Add(projection);
            return apply?.Invoke(projection) ?? true;
        }
    }

    private static RuntimeSetPositionCommand Command(Vector3 position) => new(
        new PhysicsSetPositionRequest(
            position,
            Quaternion.Identity,
            Cell,
            position,
            ImmutableArray<FlatCollisionSphere>.Empty,
            Scale: 1f,
            StepUpHeight: 0.4f,
            StepDownHeight: 0.4f,
            Flags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide),
        RuntimeSetPositionOperationKind.RemoteAuthoritative,
        GameTime: 10d,
        ExpectedVelocityAuthorityVersion: 0UL);

    private static RuntimeEntityRecord CreateRecord(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        ushort incarnation)
    {
        RuntimeEntityRecord record = lifetime.RegisterEntity(
            Spawn(guid, incarnation)).Canonical!;
        lifetime.Entities.SetFinalPhysicsState(
            record,
            PhysicsStateFlags.Gravity);
        return record;
    }

    private static void AttachBody(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record,
        Vector3 position)
    {
        lifetime.Entities.SetFullCell(record, Cell, Landblock | 0xFFFFu);
        var body = new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(Cell, position, position);
        lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
    }

    private static PhysicsEngine FlatEngine()
    {
        var engine = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
    {
        var position = new CreateObject.ServerPosition(
            Cell, 10f, 20f, 7f, 1f, 0f, 0f, 0f);
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
            RawState: (uint)PhysicsStateFlags.Gravity,
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
            "placement-projection-fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.Gravity,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
