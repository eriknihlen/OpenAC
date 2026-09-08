using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed partial class RuntimeCollisionPrefixQuiescenceTests
{
    [Fact]
    public void EmptyPrefixPermissionCompletesOnFirstAdmissibleCall()
    {
        using var fixture = new Fixture();
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.True(fixture.TryAcquire(token, out var permission));
        Assert.Equal(token, permission.Quiescence);
        Assert.True(permission.Withdrawals.IsDefaultOrEmpty);
        Assert.True(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(permission));

        Assert.True(fixture.Lifetime.Physics.CancelCollisionPrefixQuiescence(
            token,
            successorGeneration: 2UL,
            successorReady: false));
        RuntimePhysicsOwnershipSnapshot ownership =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.PendingCollisionPrefixProjectionCount);
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebtFreeRuntimeRetirementCompletesOnFirstAdmissibleCall(
        bool demote)
    {
        const uint canonical = PrefixP | 0xFFFFu;
        const uint indoorCell = PrefixP | 0x0100u;
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        var indoorSurface = new CellSurface(
            indoorCell,
            new Dictionary<ushort, Vector3>
            {
                [0] = new(0f, 0f, 9f),
                [1] = new(8f, 0f, 9f),
                [2] = new(0f, 8f, 9f),
            },
            [new List<short> { 0, 1, 2 }]);
        engine.AddLandblock(
            canonical,
            new TerrainSurface(new byte[81], new float[256]),
            [indoorSurface],
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        engine.DataCache.CellGraph.Add(new EnvCell(
            indoorCell,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<CellPortal>(),
            Array.Empty<uint>(),
            false,
            null));
        using var fixture = new Fixture(engine: engine);

        Assert.True(engine.IsLandblockTerrainResident(canonical));
        Assert.Equal(1, engine.LandblockCount);
        Assert.Same(indoorSurface, Assert.Single(
            engine.CollisionWorld.Current.Landblocks[canonical].Cells));
        Assert.NotNull(engine.DataCache.CellGraph.GetVisible(indoorCell));
        Assert.Equal(9f, indoorSurface.SampleFloorZ(1f, 1f));

        RuntimeCollisionMutationResult result = demote
            ? fixture.Lifetime.Physics.DemoteCollisionToTerrain(PrefixP)
            : fixture.Lifetime.Physics.WithdrawCollision(PrefixP);

        Assert.True(result.Completed);
        Assert.Equal(PrefixP | 0xFFFFu, result.LandblockId);
        Assert.Equal(2UL, result.Generation);
        Assert.True(result.WasResident);
        Assert.Equal(demote, result.Ready);
        Assert.Equal(demote, engine.IsLandblockTerrainResident(canonical));
        Assert.Equal(demote ? 1 : 0, engine.LandblockCount);
        if (demote)
        {
            Assert.Empty(
                engine.CollisionWorld.Current.Landblocks[canonical].Cells);
        }
        else
        {
            Assert.False(engine.CollisionWorld.Current.Landblocks
                .ContainsKey(canonical));
        }
        Assert.Null(engine.DataCache.CellGraph.GetVisible(indoorCell));
        RuntimePhysicsOwnershipSnapshot ownership =
            fixture.Lifetime.Physics.CaptureOwnership();
        Assert.Equal(0, ownership.CollisionPrefixQuiescenceCount);
        Assert.Equal(0, ownership.PendingCollisionPrefixProjectionCount);
        Assert.Equal(0, ownership.CollisionPrefixMutationCount);
    }

    [Fact]
    public void SynchronousWithdrawAckCanAcquireWithoutSyntheticRetry()
    {
        using var fixture = new Fixture(bindGeneration: true);
        _ = fixture.Add(
            0x70003101u,
            1,
            CellP,
            new Vector3(10f, 40f, 7f));
        var sink = new RecordingSink(static _ => true);
        using RuntimePlacementProjectionSubscription subscription =
            fixture.Subscribe(sink);
        RuntimeCollisionPrefixQuiescenceToken token = fixture.Begin(2UL);

        Assert.True(fixture.TryAcquire(token, out var permission));

        RuntimePlacementProjectionToken withdrawal = Assert.Single(
            permission.Withdrawals);
        Assert.Equal(withdrawal, Assert.Single(sink.Applied).Token);
        Assert.Equal(0, fixture.Lifetime.Placements.PendingCount);
        Assert.True(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(permission));
    }

    [Fact]
    public void ParkCallbackSupersessionCannotIssueDetachedPermission()
    {
        using var fixture = new Fixture();
        _ = fixture.Add(
            0x70003102u,
            1,
            CellP,
            new Vector3(10f, 41f, 7f));
        RuntimeCollisionPrefixQuiescenceToken first = fixture.Begin(2UL);
        RuntimeCollisionPrefixQuiescenceToken successor = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind
                is not RuntimePlacementProjectionKind.Withdraw)
            {
                return;
            }

            Assert.True(fixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(delta.Placement.Token));
            successor = fixture.Begin(3UL);
        });
        using IDisposable subscription =
            fixture.Lifetime.Events.SubscribePlacement(observer);

        Assert.False(fixture.TryAcquire(first, out var stale));
        Assert.False(stale.IsValid);
        Assert.True(successor.IsValid);
        Assert.False(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(stale));
        Assert.True(fixture.TryAcquire(successor, out var permission));
        Assert.Equal(successor, permission.Quiescence);
        Assert.Single(permission.Withdrawals);
    }

    [Fact]
    public void AdditionalParkCallbackSupersessionCannotIssueDetachedPermission()
    {
        using var fixture = new Fixture();
        RuntimeCollisionPrefixQuiescenceToken first = fixture.Begin(2UL);
        Assert.True(fixture.TryAcquire(first, out var original));
        Assert.True(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(original));

        _ = fixture.Add(
            0x70003104u,
            1,
            CellP,
            new Vector3(10f, 43f, 7f));
        Assert.False(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(original));

        RuntimeCollisionPrefixQuiescenceToken successor = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind
                is not RuntimePlacementProjectionKind.Withdraw)
            {
                return;
            }

            Assert.True(fixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(delta.Placement.Token));
            successor = fixture.Begin(3UL);
        });
        using IDisposable subscription =
            fixture.Lifetime.Events.SubscribePlacement(observer);

        Assert.False(fixture.TryAcquire(first, out var stale));
        Assert.False(stale.IsValid);
        Assert.True(successor.IsValid);
        Assert.False(fixture.Lifetime.Physics
            .IsCollisionPrefixMutationPermissionCurrent(stale));
        Assert.True(fixture.TryAcquire(successor, out var permission));
        Assert.Equal(successor, permission.Quiescence);
        Assert.Single(permission.Withdrawals);
    }

    [Fact]
    public void AlreadyParkedUnpreparedCancellationSupersessionCannotIssueDetachedPermission()
    {
        using var fixture = new Fixture();
        RuntimeEntityRecord record = fixture.Add(
            0x70003103u,
            1,
            CellP,
            new Vector3(10f, 42f, 7f));
        RuntimeCollisionPrefixQuiescenceToken first = fixture.Begin(2UL);

        Assert.False(fixture.TryAcquire(first, out _));
        Assert.True(fixture.Lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot withdrawal));
        RuntimeEntityPlacementToken retained = fixture.Lifetime.Physics
            .SetPosition.BeginAuthoredPlacement(
                record,
                record.PositionAuthorityVersion,
                RuntimeSetPositionOperationKind.RemoteAuthoritative);
        Assert.True(retained.IsValid);

        RuntimeCollisionPrefixQuiescenceToken successor = default;
        var observer = new PlacementObserver(delta =>
        {
            if (delta.Placement.Kind
                is not RuntimePlacementProjectionKind.Discard)
            {
                return;
            }

            Assert.Equal(withdrawal.Token.Sequence,
                delta.Placement.Token.Sequence);
            Assert.True(delta.Placement.Token.Revision
                > withdrawal.Token.Revision);
            Assert.True(fixture.Lifetime.Physics.SetPosition
                .AcknowledgeProjection(delta.Placement.Token));
            successor = fixture.Begin(3UL);
        });
        using IDisposable subscription =
            fixture.Lifetime.Events.SubscribePlacement(observer);

        Assert.False(fixture.TryAcquire(first, out var stale));
        Assert.False(stale.IsValid);
        Assert.False(fixture.Lifetime.Physics.SetPosition
            .IsPlacementCurrent(retained));
        Assert.True(successor.IsValid);
        Assert.True(fixture.TryAcquire(successor, out var permission));
        Assert.Equal(successor, permission.Quiescence);
        Assert.Single(permission.Withdrawals);
        Assert.Equal(withdrawal.Token.Sequence,
            permission.Withdrawals[0].Sequence);
    }
}
