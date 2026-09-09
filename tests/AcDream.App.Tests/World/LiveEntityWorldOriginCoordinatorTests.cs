using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.World;
using AcDream.Core.World;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityWorldOriginCoordinatorTests
{
    [Fact]
    public void LocalPlayerPosition_InitializesOnceAndReturnsResidentRecoverySet()
    {
        const uint playerGuid = 0x50000001u;
        var origin = new LiveWorldOriginState();
        origin.SetPlaceholder(10, 20);
        var world = new GpuWorldState();
        world.AddLandblock(EmptyLandblock(0x0101FFFFu));
        world.AddLandblock(EmptyLandblock(0x0202FFFFu));
        var coordinator = new LiveEntityWorldOriginCoordinator(
            origin,
            Streaming(world),
            world,
            Reveal(),
            () => playerGuid,
            new FakeSealedDungeonCellClassifier());

        LiveEntityOriginInitialization ignored = coordinator.TryInitialize(
            Spawn(0x50000002u, 0x09090001u));
        Assert.False(ignored.IsKnown);

        LiveEntityOriginInitialization initialized = coordinator.TryInitialize(
            Spawn(playerGuid, 0x03040001u));

        Assert.True(initialized.IsKnown);
        Assert.True(origin.IsKnown);
        Assert.Equal(3, origin.CenterX);
        Assert.Equal(4, origin.CenterY);
        Assert.Equal(
            [0x0101FFFFu, 0x0202FFFFu],
            initialized.AlreadyLoadedLandblocks.Order().ToArray());

        LiveEntityOriginInitialization repeated = coordinator.TryInitialize(
            Spawn(playerGuid, 0x07080001u));
        Assert.Empty(repeated.AlreadyLoadedLandblocks);
        Assert.Equal(3, origin.CenterX);
        Assert.Equal(4, origin.CenterY);
    }

    [Fact]
    public void Reset_AllowsNextSessionPlayerPositionToOwnOrigin()
    {
        const uint playerGuid = 0x50000001u;
        var origin = new LiveWorldOriginState();
        var world = new GpuWorldState();
        var coordinator = new LiveEntityWorldOriginCoordinator(
            origin,
            Streaming(world),
            world,
            Reveal(),
            () => playerGuid,
            new FakeSealedDungeonCellClassifier());

        Assert.True(coordinator.TryInitialize(Spawn(playerGuid, 0x03040001u)).IsKnown);
        origin.Reset();
        Assert.False(coordinator.IsKnown);

        Assert.True(coordinator.TryInitialize(Spawn(playerGuid, 0x07080001u)).IsKnown);
        Assert.Equal(7, origin.CenterX);
        Assert.Equal(8, origin.CenterY);
    }

    private static StreamingController Streaming(GpuWorldState world) => new(
        (_, _, _) => { },
        (_, _) => { },
        _ => Array.Empty<LandblockStreamResult>(),
        (_, _) => { },
        world,
        nearRadius: 1,
        farRadius: 2);

    private static WorldRevealCoordinator Reveal() => new(
        new RuntimeWorldTransitState(),
        () => new StreamingRevealWindow(1, 1),
        (_, _, _) => true,
        _ => true,
        (_, _) => true,
        () => true,
        (_, _) => { },
        () => { },
        _ => false);

    private static LoadedLandblock EmptyLandblock(uint id) => new(
        id,
        new DatReaderWriter.DBObjs.LandBlock(),
        Array.Empty<WorldEntity>());

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell) => new(
        Guid: guid,
        Position: new CreateObject.ServerPosition(
            cell,
            12f,
            24f,
            6f,
            1f,
            0f,
            0f,
            0f),
        SetupTableId: 0x02000001u,
        AnimPartChanges: [],
        TextureChanges: [],
        SubPalettes: [],
        BasePaletteId: null,
        ObjScale: null,
        Name: "origin fixture",
        ItemType: null,
        MotionState: null,
        MotionTableId: null);

    private sealed class FakeSealedDungeonCellClassifier
        : ISealedDungeonCellClassifier
    {
        public bool IsSealedDungeon(uint cellId) => false;
    }
}
