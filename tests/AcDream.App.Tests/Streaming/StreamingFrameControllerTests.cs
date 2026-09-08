using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using AcDream.Core.World.Cells;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class StreamingFrameControllerTests
{
    [Fact]
    public void LivePreLogin_AdvancesOriginConvergenceButSuppressesStreaming()
    {
        var fixture = new Fixture(liveMode: true);

        fixture.Controller.Tick();

        Assert.Equal(1, fixture.Convergence.Advances);
        Assert.Empty(fixture.Streaming.Calls);
        Assert.Equal(0, fixture.Dungeon.Captures);
        Assert.Empty(fixture.Rescues.Calls);
    }

    [Fact]
    public void LiveInWorldWithoutAuthoritativeOrigin_RemainsGated()
    {
        var fixture = new Fixture(liveMode: true);
        fixture.Session.IsInWorld = true;

        fixture.Controller.Tick();

        Assert.Equal(1, fixture.Convergence.Advances);
        Assert.Empty(fixture.Streaming.Calls);
    }

    [Fact]
    public void ChaseLifetimeAfterEntry_AllowsConvergenceWithNoCurrentSession()
    {
        var fixture = new Fixture(liveMode: true);
        fixture.Mode.ChaseModeEverEntered = true;

        fixture.Controller.Tick();

        Assert.Equal([(10, 20, false)], fixture.Streaming.Calls);
    }

    [Fact]
    public void ConvergenceCompletesBeforeObserverSelection()
    {
        var fixture = new Fixture();
        fixture.Convergence.OnAdvance = () => fixture.Origin.Recenter(30, 40);

        fixture.Controller.Tick();

        Assert.Equal([(30, 40, false)], fixture.Streaming.Calls);
    }

    [Fact]
    public void PendingConvergence_StillTicksBackendBeforeRescueSoBackendOwnsItsGate()
    {
        var calls = new List<string>();
        var fixture = new Fixture();
        fixture.Convergence.AdvanceResult = false;
        fixture.Streaming.OnTick = (_, _, _) => calls.Add("backend");
        fixture.Rescues.OnRebucket = (_, _) => calls.Add("rescues");

        fixture.Controller.Tick();

        Assert.Equal(["backend", "rescues"], calls);
        Assert.Equal([(10, 20, false)], fixture.Streaming.Calls);
        Assert.Equal([(10, 20)], fixture.Rescues.Calls);
    }

    [Fact]
    public void LivePrePlayerMode_UsesAuthoritativePlayerLandblock()
    {
        var fixture = new Fixture(liveMode: true, initializeOrigin: true);
        fixture.Session.IsInWorld = true;
        fixture.PlayerLandblock.LastKnownLandblockId = 0xA1B20001u;
        fixture.Offline.Position = new Vector3(9000f, 9000f, 0f);

        fixture.Controller.Tick();

        Assert.Equal([(0xA1, 0xB2, false)], fixture.Streaming.Calls);
        Assert.Equal(0, fixture.Offline.Reads);
    }

    [Fact]
    public void LivePrePlayerModeWithoutPositionHint_StaysOnAcceptedOrigin()
    {
        var fixture = new Fixture(liveMode: true, initializeOrigin: true);
        fixture.Session.IsInWorld = true;
        fixture.Offline.Position = new Vector3(9000f, 9000f, 0f);

        fixture.Controller.Tick();

        Assert.Equal([(10, 20, false)], fixture.Streaming.Calls);
        Assert.Equal(0, fixture.Offline.Reads);
    }

    [Fact]
    public void PlayerModeWithoutController_InWorldStillUsesAuthoritativePlayerHint()
    {
        var fixture = new Fixture(liveMode: true, initializeOrigin: true);
        fixture.Mode.IsPlayerMode = true;
        fixture.Session.IsInWorld = true;
        fixture.PlayerLandblock.LastKnownLandblockId = 0xC95B0001u;

        fixture.Controller.Tick();

        Assert.Equal([(0xC9, 0x5B, false)], fixture.Streaming.Calls);
        Assert.Equal(0, fixture.Offline.Reads);
    }

    [Fact]
    public void PlayerModeWithoutController_OfflineFallsBackToFlyObserver()
    {
        var fixture = new Fixture();
        fixture.Mode.IsPlayerMode = true;
        fixture.Offline.Position = new Vector3(192f, 384f, 0f);

        fixture.Controller.Tick();

        Assert.Equal([(11, 22, false)], fixture.Streaming.Calls);
        Assert.Equal(1, fixture.Offline.Reads);
    }

    [Fact]
    public void PlayerMode_UsesPhysicsResolvedPositionIncludingNegativeFloor()
    {
        var fixture = new Fixture(initializeOrigin: true);
        fixture.Mode.IsPlayerMode = true;
        var player = new PlayerMovementController(new PhysicsEngine());
        player.SeedPlacementForTest(
            new Vector3(383f, -0.01f, 0f),
            0x0A140001u,
            new Vector3(1f, 1f, 0f));
        fixture.Player.Controller = player;

        fixture.Controller.Tick();

        Assert.Equal([(11, 19, false)], fixture.Streaming.Calls);
    }

    [Fact]
    public void PortalSpace_PinsDestinationOriginAndSuppressesFrozenSourceDungeon()
    {
        var fixture = new Fixture(initializeOrigin: true);
        fixture.Mode.IsPlayerMode = true;
        var player = new PlayerMovementController(new PhysicsEngine())
        {
            State = PlayerState.PortalSpace,
        };
        player.SeedPlacementForTest(
            new Vector3(1000f, -1000f, 0f),
            0x0A140001u,
            new Vector3(1f, 1f, 0f));
        fixture.Player.Controller = player;
        fixture.Dungeon.Snapshot = new StreamingDungeonCellSnapshot(
            0xAABB0001u,
            IsSealedDungeon: true);

        fixture.Controller.Tick();

        Assert.Equal([(10, 20, false)], fixture.Streaming.Calls);
    }

    [Fact]
    public void SealedDungeon_PinsObserverToCurrentCellsOwnLandblock()
    {
        var fixture = new Fixture(initializeOrigin: true);
        fixture.Mode.IsPlayerMode = true;
        var player = new PlayerMovementController(new PhysicsEngine());
        player.SeedPlacementForTest(
            new Vector3(1000f, -1000f, 0f),
            0x0A140001u,
            new Vector3(1f, 1f, 0f));
        fixture.Player.Controller = player;
        fixture.Dungeon.Snapshot = new StreamingDungeonCellSnapshot(
            0xAABB0001u,
            IsSealedDungeon: true);

        fixture.Controller.Tick();

        Assert.Equal([(0xAA, 0xBB, true)], fixture.Streaming.Calls);
    }

    [Fact]
    public void OfflineMode_UsesFlyCameraRelativeToPlaceholderOrigin()
    {
        var fixture = new Fixture();
        fixture.Offline.Position = new Vector3(384f, -0.01f, 0f);

        fixture.Controller.Tick();

        Assert.Equal([(12, 19, false)], fixture.Streaming.Calls);
        Assert.Equal(1, fixture.Offline.Reads);
    }

    [Fact]
    public void StreamingBackendTickPrecedesRescueRebucketAtSameObserver()
    {
        var calls = new List<string>();
        var fixture = new Fixture();
        fixture.Streaming.OnTick = (_, _, _) => calls.Add("streaming");
        fixture.Rescues.OnRebucket = (_, _) => calls.Add("rescues");

        fixture.Controller.Tick();

        Assert.Equal(["streaming", "rescues"], calls);
        Assert.Equal([(10, 20)], fixture.Rescues.Calls);
    }

    [Fact]
    public void PhysicsDungeonCellSource_MapsMissingOutdoorAndSealedCells()
    {
        var physics = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        var source = new PhysicsStreamingDungeonCellSource(physics);

        Assert.Equal(default, source.Capture());

        var heights = new byte[81];
        var heightTable = new float[256];
        physics.DataCache.CellGraph.RegisterTerrain(
            0xAABB0000u,
            new TerrainSurface(heights, heightTable),
            Vector3.Zero);
        physics.UpdatePlayerCurrCell(0xAABB0001u);

        Assert.Equal(
            new StreamingDungeonCellSnapshot(0xAABB0001u, IsSealedDungeon: false),
            source.Capture());

        var outsideVisibleCell = new AcDream.Core.World.Cells.EnvCell(
            0xCCDD0100u,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<CellPortal>(),
            Array.Empty<uint>(),
            seenOutside: true,
            containmentBsp: null);
        physics.DataCache.CellGraph.Add(outsideVisibleCell);
        physics.UpdatePlayerCurrCell(outsideVisibleCell.Id);

        Assert.Equal(
            new StreamingDungeonCellSnapshot(
                outsideVisibleCell.Id,
                IsSealedDungeon: false),
            source.Capture());

        var sealedCell = new AcDream.Core.World.Cells.EnvCell(
            0xCCDD0101u,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<CellPortal>(),
            Array.Empty<uint>(),
            seenOutside: false,
            containmentBsp: null);
        physics.DataCache.CellGraph.Add(sealedCell);
        physics.UpdatePlayerCurrCell(sealedCell.Id);

        Assert.Equal(
            new StreamingDungeonCellSnapshot(sealedCell.Id, IsSealedDungeon: true),
            source.Capture());
    }

    [Fact]
    public void LiveProjectionRescueRebucketter_RebucketsIntoExactObserverLandblock()
    {
        const uint guid = 0x50000031u;
        const uint sourceLandblock = 0x0101FFFFu;
        const uint sourceCell = 0x01010001u;
        var state = new GpuWorldState();
        state.AddLandblock(EmptyLandblock(sourceLandblock));
        var runtime = Runtime(state);
        runtime.RegisterLiveEntity(Spawn(guid, sourceCell));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            sourceCell,
            id => Entity(id, guid))!;
        state.MarkPersistent(guid);
        state.RemoveLandblock(sourceLandblock);
        Assert.Equal(1, state.PendingRescueCount);

        new LiveProjectionRescueRebucketter(state, runtime).RebucketAll(0xAA, 0xBB);

        Assert.True(runtime.TryGetRecord(guid, out LiveEntityRecord record));
        Assert.Same(entity, record.WorldEntity);
        Assert.Equal(0xAABBFFFFu, record.CanonicalLandblockId);
        Assert.Equal(0, state.PendingRescueCount);
        Assert.Equal(1, state.PendingLiveEntityCount);
    }

    [Fact]
    public void LiveProjectionRescueRebucketter_IgnoresEmptyAndDeletedRescues()
    {
        const uint guid = 0x50000032u;
        const uint sourceLandblock = 0x0101FFFFu;
        const uint sourceCell = 0x01010001u;
        var state = new GpuWorldState();
        var runtime = Runtime(state);
        var adapter = new LiveProjectionRescueRebucketter(state, runtime);

        adapter.RebucketAll(0xAA, 0xBB);
        Assert.Equal(0, state.PendingLiveEntityCount);

        state.AddLandblock(EmptyLandblock(sourceLandblock));
        runtime.RegisterLiveEntity(Spawn(guid, sourceCell));
        runtime.MaterializeLiveEntity(guid, sourceCell, id => Entity(id, guid));
        state.MarkPersistent(guid);
        state.RemoveLandblock(sourceLandblock);
        Assert.Equal(1, state.PendingRescueCount);
        runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false);

        adapter.RebucketAll(0xAA, 0xBB);

        Assert.Equal(0, state.PendingRescueCount);
        Assert.Equal(0, state.PendingLiveEntityCount);
        Assert.Empty(state.Entities);
    }

    [Fact]
    public void CompletedStreamingPublicationMakesInboundProjectionResident()
    {
        const uint landblock = 0x0A14FFFFu;
        const uint cell = 0x0A140001u;
        const uint guid = 0x70000041u;
        var state = new GpuWorldState();
        var outbox = new Queue<LandblockStreamResult>();
        outbox.Enqueue(new LandblockStreamResult.Loaded(
            landblock,
            LandblockStreamTier.Near,
            EmptyLandblock(landblock),
            new LandblockMeshData(Array.Empty<TerrainVertex>(), Array.Empty<uint>())));
        var streaming = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: max => Drain(outbox, max),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0);
        var fixture = new Fixture(streamingBackend: streaming);

        for (int frame = 0; frame < 32 && !state.IsLoaded(landblock); frame++)
            fixture.Controller.Tick();
        Assert.True(state.IsLoaded(landblock));

        var runtime = Runtime(state);
        runtime.RegisterLiveEntity(Spawn(guid, cell));
        WorldEntity entity = runtime.MaterializeLiveEntity(
            guid,
            cell,
            id => Entity(id, guid))!;

        Assert.Same(entity, Assert.Single(state.Entities));
        Assert.Equal(0, state.PendingLiveEntityCount);
    }

    [Fact]
    public void SessionResetClosesChaseLifetimeOriginAndPlayerHintBeforeNextTick()
    {
        var fixture = new Fixture(liveMode: true, initializeOrigin: true);
        fixture.Mode.IsPlayerMode = true;
        fixture.Mode.ChaseModeEverEntered = true;
        fixture.Session.IsInWorld = true;
        fixture.PlayerLandblock.LastKnownLandblockId = 0xAABB0001u;

        fixture.Mode.ResetSession();
        fixture.Origin.Reset();
        fixture.Session.IsInWorld = false;
        fixture.PlayerLandblock.LastKnownLandblockId = null;
        fixture.Controller.Tick();

        Assert.Equal(1, fixture.Convergence.Advances);
        Assert.Empty(fixture.Streaming.Calls);
        Assert.Empty(fixture.Rescues.Calls);
        Assert.False(fixture.Mode.IsPlayerMode);
        Assert.False(fixture.Mode.ChaseModeEverEntered);
        Assert.False(fixture.Origin.IsKnown);
    }

    private sealed class Fixture
    {
        internal Fixture(
            bool liveMode = false,
            bool initializeOrigin = false,
            IStreamingFrameBackend? streamingBackend = null)
        {
            Origin.SetPlaceholder(10, 20);
            if (initializeOrigin)
                Assert.True(Origin.TryInitialize(10, 20));
            Controller = new StreamingFrameController(
                liveMode,
                Mode,
                Player,
                Session,
                Origin,
                PlayerLandblock,
                Offline,
                Dungeon,
                Convergence,
                streamingBackend ?? Streaming,
                Rescues);
        }

        internal LocalPlayerModeState Mode { get; } = new();
        internal RuntimeLocalPlayerMovementState Player { get; } = new();
        internal SessionSource Session { get; } = new();
        internal LiveWorldOriginState Origin { get; } = new();
        internal PlayerLandblockSource PlayerLandblock { get; } = new();
        internal OfflineObserverSource Offline { get; } = new();
        internal DungeonCellSource Dungeon { get; } = new();
        internal OriginConvergence Convergence { get; } = new();
        internal StreamingBackend Streaming { get; } = new();
        internal RescueRebucketter Rescues { get; } = new();
        internal StreamingFrameController Controller { get; }
    }

    private sealed class SessionSource : ILiveInWorldSource
    {
        public bool IsInWorld { get; set; }
    }

    private sealed class PlayerLandblockSource : ILocalPlayerLandblockSource
    {
        public uint? LastKnownLandblockId { get; set; }
    }

    private sealed class OfflineObserverSource : IOfflineStreamingObserverSource
    {
        private Vector3 _position;
        internal int Reads { get; private set; }
        public Vector3 Position
        {
            get
            {
                Reads++;
                return _position;
            }
            set => _position = value;
        }
    }

    private sealed class DungeonCellSource : IStreamingDungeonCellSource
    {
        internal int Captures { get; private set; }
        internal StreamingDungeonCellSnapshot Snapshot { get; set; }
        public StreamingDungeonCellSnapshot Capture()
        {
            Captures++;
            return Snapshot;
        }
    }

    private sealed class OriginConvergence : IStreamingOriginConvergence
    {
        internal int Advances { get; private set; }
        internal Action? OnAdvance { get; set; }
        internal bool AdvanceResult { get; set; } = true;
        public bool Advance()
        {
            Advances++;
            OnAdvance?.Invoke();
            return AdvanceResult;
        }
    }

    private sealed class StreamingBackend : IStreamingFrameBackend
    {
        internal List<(int X, int Y, bool Dungeon)> Calls { get; } = [];
        internal Action<int, int, bool>? OnTick { get; set; }
        public void Tick(int observerCx, int observerCy, bool insideDungeon = false)
        {
            Calls.Add((observerCx, observerCy, insideDungeon));
            OnTick?.Invoke(observerCx, observerCy, insideDungeon);
        }
    }

    private sealed class RescueRebucketter : ILiveProjectionRescueRebucketter
    {
        internal List<(int X, int Y)> Calls { get; } = [];
        internal Action<int, int>? OnRebucket { get; set; }
        public void RebucketAll(int observerCx, int observerCy)
        {
            Calls.Add((observerCx, observerCy));
            OnRebucket?.Invoke(observerCx, observerCy);
        }
    }

    private static LiveEntityRuntime Runtime(GpuWorldState state) =>
        LiveEntityRuntimeFixture.Create(
            state,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));

    private static LoadedLandblock EmptyLandblock(uint landblock) =>
        new(landblock, new LandBlock(), Array.Empty<WorldEntity>());

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "streaming frame fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1).WithConsistentPhysics();
    }

    private static IReadOnlyList<LandblockStreamResult> Drain(
        Queue<LandblockStreamResult> queue,
        int max)
    {
        var drained = new List<LandblockStreamResult>(max);
        while (drained.Count < max && queue.TryDequeue(out LandblockStreamResult? result))
            drained.Add(result);
        return drained;
    }
}
