using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class WorldRevealDerivedWindowIntegrationTests
{
    private const int CenterX = 0x40;
    private const int CenterY = 0x40;
    private const uint DestinationCell = (uint)CenterX << 24
        | (uint)CenterY << 16
        | 0x0021u;

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    public void OutdoorReveal_HoldsUntilTheWholeDerivedWindowIsPublished(
        int nearRadius,
        int farRadius)
    {
        GpuWorldState world = CreateWorld();
        var physics = new PhysicsEngine();
        var transit = new RuntimeWorldTransitState();
        var streaming = new RecordingReservations();
        StreamingController controller = CreateController(
            world,
            nearRadius,
            farRadius);
        WorldRevealCoordinator coordinator = CreateCoordinator(
            transit,
            controller,
            physics,
            streaming);

        long generation = coordinator.BeginLogin(DestinationCell);

        // The reservation opens on the same square the gate measures.
        Assert.Equal(
            (generation, DestinationCell, farRadius),
            Assert.Single(streaming.Begins));

        for (int radius = 0; radius <= nearRadius; radius++)
            PublishRing(world, physics, radius, LandblockStreamTier.Near);
        Assert.False(coordinator.Evaluate(DestinationCell).IsReady);

        // Fill the outer rings with Far-tier terrain publication, one ring at
        // a time, so the hold is proven to track the outer boundary and not
        // just the first missing member.
        for (int radius = nearRadius + 1; radius < farRadius; radius++)
        {
            PublishRing(world, physics, radius, LandblockStreamTier.Far);
            Assert.False(coordinator.Evaluate(DestinationCell).IsReady);
        }

        PublishRing(world, physics, farRadius, LandblockStreamTier.Far);
        WorldRevealReadinessSnapshot ready =
            coordinator.Evaluate(DestinationCell);

        Assert.True(ready.IsReady);
        Assert.Equal(farRadius, ready.RequiredRenderRadius);
        Assert.Equal(nearRadius, ready.RequiredNearRadius);
        // The loosened Runtime shape invariant accepted the derived radius.
        Assert.Equal(0, transit.Snapshot.InvariantFailureCount);
        Assert.True(transit.Snapshot.IsReady);
    }

    [Fact]
    public void LoginReveal_UsesTheSameWidenedGateAsPortalArrival()
    {
        const int nearRadius = 1;
        const int farRadius = 3;
        GpuWorldState world = CreateWorld();
        var physics = new PhysicsEngine();
        var transit = new RuntimeWorldTransitState();
        StreamingController controller = CreateController(
            world,
            nearRadius,
            farRadius);
        WorldRevealCoordinator coordinator = CreateCoordinator(
            transit,
            controller,
            physics,
            streaming: null);

        coordinator.BeginLogin(DestinationCell);
        for (int radius = 0; radius < farRadius; radius++)
        {
            PublishRing(
                world,
                physics,
                radius,
                radius <= nearRadius
                    ? LandblockStreamTier.Near
                    : LandblockStreamTier.Far);
        }

        Assert.False(coordinator.Evaluate(DestinationCell).IsReady);

        PublishRing(world, physics, farRadius, LandblockStreamTier.Far);

        Assert.True(coordinator.Evaluate(DestinationCell).IsReady);
        Assert.Equal(0, transit.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void IndoorReveal_IgnoresTheStreamingWindowEntirely()
    {
        const uint indoorCell = (uint)CenterX << 24
            | (uint)CenterY << 16
            | 0x0100u;
        GpuWorldState world = CreateWorld();
        var physics = new PhysicsEngine();
        var transit = new RuntimeWorldTransitState();
        StreamingController controller = CreateController(world, 2, 6);
        WorldRevealCoordinator coordinator = CreateCoordinator(
            transit,
            controller,
            physics,
            streaming: null);

        coordinator.BeginLogin(indoorCell);
        Assert.False(coordinator.Evaluate(indoorCell).IsReady);

        // Only the destination's own landblock, at Near tier.
        PublishRing(world, physics, 0, LandblockStreamTier.Near);
        WorldRevealReadinessSnapshot snapshot = coordinator.Evaluate(indoorCell);

        Assert.True(snapshot.IsRenderNeighborhoodReady);
        Assert.Equal(0, snapshot.RequiredRenderRadius);
        Assert.Equal(0, transit.Snapshot.InvariantFailureCount);
    }

    [Fact]
    public void OutdoorReveal_SurvivesAnOuterRingDemoteDuringTheHold()
    {
        const int nearRadius = 1;
        const int farRadius = 3;
        var meshes = new RecordingMeshAdapter();
        var world = new GpuWorldState(new LandblockSpawnAdapter(meshes));
        var physics = new PhysicsEngine();
        var transit = new RuntimeWorldTransitState();
        var pipeline = new LandblockPresentationPipeline(
            publishBeforeSpatialCommit: (_, _) => { },
            world);
        StreamingController controller = CreateController(
            world,
            nearRadius,
            farRadius);
        WorldRevealCoordinator coordinator = CreateCoordinator(
            transit,
            controller,
            physics,
            streaming: null);

        coordinator.BeginLogin(DestinationCell);
        for (int radius = 0; radius <= farRadius; radius++)
            PublishRing(world, physics, radius, LandblockStreamTier.Near, withEntity: true);

        Assert.True(coordinator.Evaluate(DestinationCell).IsReady);

        uint demoted = ((uint)(CenterX + farRadius) << 24)
            | ((uint)(CenterY - farRadius) << 16)
            | 0xFFFFu;
        pipeline.BeginNearLayerRetirement(demoted);

        Assert.True(world.IsLoaded(demoted));
        Assert.False(world.IsNearTier(demoted));
        Assert.True(coordinator.Evaluate(DestinationCell).IsReady);
        Assert.Equal(0, transit.Snapshot.InvariantFailureCount);
    }

    private sealed class RecordingMeshAdapter : IWbMeshAdapter
    {
        public Dictionary<ulong, int> ReferenceCounts { get; } = new();

        public void IncrementRefCount(ulong id) =>
            ReferenceCounts[id] = ReferenceCounts.GetValueOrDefault(id) + 1;

        public void DecrementRefCount(ulong id)
        {
            int next = ReferenceCounts.GetValueOrDefault(id) - 1;
            if (next <= 0)
                ReferenceCounts.Remove(id);
            else
                ReferenceCounts[id] = next;
        }

        public bool IsRenderDataReady(ulong id) => true;
    }

    private sealed class RecordingReservations : IWorldRevealStreamingScheduler
    {
        public List<(long Generation, uint Cell, int Radius)> Begins { get; } = [];
        public List<long> Ends { get; } = [];

        public void BeginDestinationReservation(
            long revealGeneration,
            uint destinationCell,
            int requiredRenderRadius) =>
            Begins.Add((revealGeneration, destinationCell, requiredRenderRadius));

        public void EndDestinationReservation(long revealGeneration) =>
            Ends.Add(revealGeneration);
    }

    private static WorldRevealCoordinator CreateCoordinator(
        RuntimeWorldTransitState transit,
        StreamingController controller,
        PhysicsEngine physics,
        IWorldRevealStreamingScheduler? streaming) =>
        new(
            transit,
            () => new StreamingRevealWindow(
                controller.NearRadius,
                controller.FarRadius),
            controller.IsRenderNeighborhoodResident,
            physics.IsSpawnCellReady,
            physics.IsNeighborhoodTerrainResident,
            () => true,
            (_, _) => { },
            () => { },
            _ => false,
            streaming: streaming);

    private static StreamingController CreateController(
        GpuWorldState state,
        int nearRadius,
        int farRadius) =>
        new(
            (_, _, _) => { },
            (_, _) => { },
            _ => Array.Empty<LandblockStreamResult>(),
            (_, _) => { },
            state,
            nearRadius: nearRadius,
            farRadius: farRadius);

    private static GpuWorldState CreateWorld() =>
        new(new LandblockSpawnAdapter(new RecordingMeshAdapter()));

    private static void PublishRing(
        GpuWorldState world,
        PhysicsEngine physics,
        int radius,
        LandblockStreamTier tier,
        bool withEntity = false)
    {
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                continue;

            uint id = ((uint)(CenterX + dx) << 24)
                | ((uint)(CenterY + dy) << 16)
                | 0xFFFFu;
            WorldEntity[] entities = withEntity
                ?
                [
                    new WorldEntity
                    {
                        Id = 1,
                        ServerGuid = 0,
                        SourceGfxObjOrSetupId = 0x01000010u,
                        Position = System.Numerics.Vector3.Zero,
                        Rotation = System.Numerics.Quaternion.Identity,
                        MeshRefs =
                        [
                            new MeshRef(
                                0x01000010u,
                                System.Numerics.Matrix4x4.Identity),
                        ],
                    },
                ]
                : Array.Empty<WorldEntity>();
            world.AddLandblock(
                new LoadedLandblock(id, new LandBlock(), entities),
                tier: tier);
            physics.AddLandblock(
                id,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
        }
    }
}
