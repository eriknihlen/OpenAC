using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkLandscapeAssemblerTests
{
    private sealed class LandscapeCellRecorder : IWalkEventSink
    {
        public HashSet<uint> Cells { get; } = [];

        public void Emit(in WalkEvent walkEvent) { }

        public void OnLandscapeCellTurn(uint cellId) => Cells.Add(cellId);
    }

    private const uint LandblockId = 0xA9B4FFFFu;      // block (0xA9, 0xB4)
    private const uint CameraCellId = 0xA9B40001u;     // same block, outdoor landcell 1

    private static int GridIndex(int gx, int gy) => gx * WalkLandscapeAssembler.GridWidth + gy;

    private static int CenterIndex() =>
        GridIndex(WalkLandscapeAssembler.MidRadius, WalkLandscapeAssembler.MidRadius);

    [Fact]
    public void PublishBeforeSetViewer_IsVisibleOnceSetViewerRuns()
    {
        var assembler = new WalkLandscapeAssembler();

        assembler.PublishLandblock(LandblockId, maxZ: 100f, minZ: -5f, Array.Empty<WalkBuildingFactory.Entry>());
        assembler.SetViewer(CameraCellId, Vector3.Zero);

        WalkLandBlock? block = assembler.Landscape.Blocks[CenterIndex()];
        Assert.NotNull(block);
        Assert.Equal(8, block!.SideCellCount);
        Assert.Equal(100f, block.MaxZ);
        Assert.Equal(-5f, block.MinZ);
    }

    [Fact]
    public void PublishAfterSetViewer_RefreshesTheAlreadyWindowedSlotImmediately()
    {
        var assembler = new WalkLandscapeAssembler();
        assembler.SetViewer(CameraCellId, Vector3.Zero);

        assembler.PublishLandblock(LandblockId, maxZ: 42f, minZ: 3f, Array.Empty<WalkBuildingFactory.Entry>());

        Assert.Equal(42f, assembler.Landscape.Blocks[CenterIndex()]!.MaxZ);
    }

    [Fact]
    public void RingPyramid_FarBlockKeepsBuildingsInCoarseCellBuckets()
    {
        var assembler = new WalkLandscapeAssembler();
        var building = new WalkBuildingFactory.Entry(
            new WalkBuilding { PositionCellId = (LandblockId & 0xFFFF0000u) | 1u },
            Matrix4x4.Identity, Matrix4x4.Identity);
        assembler.PublishLandblock(LandblockId, 1f, 0f, new[] { building });

        const uint FarLandblockId = 0xA9B7FFFFu;
        assembler.PublishLandblock(FarLandblockId, 1f, 0f, new[] { building });
        assembler.SetViewer(CameraCellId, Vector3.Zero);

        WalkLandBlock center = assembler.Landscape.Blocks[CenterIndex()]!;
        WalkLandBlock far = assembler.Landscape.Blocks[
            GridIndex(WalkLandscapeAssembler.MidRadius, WalkLandscapeAssembler.MidRadius + 3)]!;
        Assert.Equal(8, center.SideCellCount);
        Assert.Contains(center.CellBuildings, b => b is not null);
        Assert.Equal(2, far.SideCellCount);
        Assert.All(far.CellBuildings, Assert.Null);
        Assert.Same(building.Building, Assert.Single(far.CoarseCellBuildings.SelectMany(b => b)));
    }

    [Fact]
    public void RetireLandblock_NullsTheWindowedSlot()
    {
        var assembler = new WalkLandscapeAssembler();
        assembler.PublishLandblock(LandblockId, 1f, 0f, Array.Empty<WalkBuildingFactory.Entry>());
        assembler.SetViewer(CameraCellId, Vector3.Zero);
        Assert.NotNull(assembler.Landscape.Blocks[CenterIndex()]);

        assembler.RetireLandblock(LandblockId);

        Assert.Null(assembler.Landscape.Blocks[CenterIndex()]);
    }

    [Fact]
    public void RetireLandblock_UnpublishedLandblockIsANoOp()
    {
        var assembler = new WalkLandscapeAssembler();

        assembler.RetireLandblock(LandblockId);

        Assert.Null(assembler.Landscape.Blocks[CenterIndex()]);
    }

    [Fact]
    public void SetViewer_RecentresTheWindowWhenTheCameraCrossesIntoAnotherBlock()
    {
        var assembler = new WalkLandscapeAssembler();
        assembler.PublishLandblock(LandblockId, 7f, -1f, Array.Empty<WalkBuildingFactory.Entry>());
        assembler.SetViewer(CameraCellId, Vector3.Zero);
        Assert.NotNull(assembler.Landscape.Blocks[CenterIndex()]);

        assembler.SetViewer(0xAAB40001u, Vector3.Zero);

        Assert.Null(assembler.Landscape.Blocks[CenterIndex()]);
        WalkLandBlock? shifted = assembler.Landscape.Blocks[
            GridIndex(WalkLandscapeAssembler.MidRadius - 1, WalkLandscapeAssembler.MidRadius)];
        Assert.NotNull(shifted);
        Assert.Equal(7f, shifted!.MaxZ);
    }

    [Fact]
    public void SetViewer_LandcellIndexDerivesViewerCellFromLowWord()
    {
        var assembler = new WalkLandscapeAssembler();

        // Low word 10 -> landcell index 9 -> (9/8, 9%8) = (1, 1).
        assembler.SetViewer(0xA9B4000Au, Vector3.Zero);

        Assert.Equal(1, assembler.Landscape.ViewerCellX);
        Assert.Equal(1, assembler.Landscape.ViewerCellY);
    }

    [Fact]
    public void SetViewer_OutdoorCellRecoversWholeBlockRenderCenterOffset()
    {
        var assembler = new WalkLandscapeAssembler();

        assembler.SetViewer(0xF07F0022u, new Vector3(107.31238f, 222.21594f, 0f));

        Assert.Equal(4, assembler.Landscape.ViewerCellX);
        Assert.Equal(1, assembler.Landscape.ViewerCellY);
        Assert.Equal(0f, assembler.Landscape.ViewerWorldOriginX);
        Assert.Equal(192f, assembler.Landscape.ViewerWorldOriginY);
    }

    [Fact]
    public void SetViewer_InteriorCameraDerivesViewerCellFromOrigin()
    {
        var assembler = new WalkLandscapeAssembler();

        assembler.SetViewer(0xA9B40105u, new Vector3(50f, 74f, 0f));

        Assert.Equal(2, assembler.Landscape.ViewerCellX);   // floor(50 / 24) = 2
        Assert.Equal(3, assembler.Landscape.ViewerCellY);   // floor(74 / 24) = 3
        Assert.Equal(0f, assembler.Landscape.ViewerWorldOriginX);
        Assert.Equal(0f, assembler.Landscape.ViewerWorldOriginY);
    }

    [Fact]
    public void SetViewer_InteriorCameraUsesRenderCenterBlockAndPositiveLocalCell()
    {
        var assembler = new WalkLandscapeAssembler();

        assembler.SetViewer(0xA9B40105u, new Vector3(-10f, 222f, 0f));

        Assert.Equal(-192f, assembler.Landscape.ViewerWorldOriginX);
        Assert.Equal(192f, assembler.Landscape.ViewerWorldOriginY);
        Assert.Equal(7, assembler.Landscape.ViewerCellX);
        Assert.Equal(1, assembler.Landscape.ViewerCellY);
    }

    [Fact]
    public void SetViewer_SameBlockRepeatCallDoesNotClearAlreadyPublishedSlots()
    {
        var assembler = new WalkLandscapeAssembler();
        assembler.PublishLandblock(LandblockId, 1f, 0f, Array.Empty<WalkBuildingFactory.Entry>());
        assembler.SetViewer(CameraCellId, Vector3.Zero);

        assembler.SetViewer(CameraCellId, new Vector3(5f, 5f, 0f));

        Assert.NotNull(assembler.Landscape.Blocks[CenterIndex()]);
    }

    [Fact]
    public void TuskerIsland_RenderCenterOffset_DoesNotCullTheViewerLandblock()
    {
        const uint tuskerLandblock = 0xF07FFFFFu;
        const uint tuskerCell = 0xF07F0022u;
        var eye = new Vector3(107.31238f, 222.21594f, 16.352213f);
        var forward = Vector3.Normalize(new Vector3(-0.66922873f, 0.6854568f, -0.286918f));
        var viewProjection = new Matrix4x4(
            0.79605f, -0.39642528f, -0.66922873f, -0.6692153f,
            0.7772036f, 0.4060382f, 0.6854568f, 0.6854431f,
            0f, 1.8946906f, -0.286918f, -0.28691226f,
            -258.13306f, -78.669205f, -75.91117f, -75.809654f);

        var assembler = new WalkLandscapeAssembler();
        assembler.PublishLandblock(
            tuskerLandblock,
            maxZ: 220f,
            minZ: -10f,
            Array.Empty<WalkBuildingFactory.Entry>());
        assembler.SetViewer(tuskerCell, eye);

        var context = new WalkProductionFrameContext(
            new CellVisibility(),
            new WalkBuildingRegistry(),
            eye,
            forward,
            viewProjection,
            1760f,
            990f);
        var recorder = new LandscapeCellRecorder();

        new RetailFrameWalk().WalkFrame(
            tuskerCell,
            cameraCell: null,
            assembler.Landscape,
            context,
            recorder);

        Assert.Contains(
            recorder.Cells,
            cellId => (cellId & 0xFFFF0000u) == (tuskerLandblock & 0xFFFF0000u));
    }
}
