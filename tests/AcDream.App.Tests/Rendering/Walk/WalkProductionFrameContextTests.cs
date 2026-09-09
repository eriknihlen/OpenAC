using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkProductionFrameContextTests
{
    private static Matrix4x4 SimpleViewProjection() =>
        Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ)
        * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 4f / 3f, 0.1f, 1000f);

    [Fact]
    public void GetVisible_ResolvesThroughTheCommittedCellVisibilityRegistry()
    {
        var cellVisibility = new CellVisibility();
        var walkCell = new WalkCell { CellId = 0xA9B40100u };
        var loaded = new LoadedCell { CellId = 0xA9B40100u, Walk = walkCell };
        cellVisibility.CommitLandblock(0xA9B4FFFFu, new[] { loaded });
        var ctx = new WalkProductionFrameContext(
            cellVisibility, new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);

        Assert.Same(walkCell, ctx.GetVisible(0xA9B40100u));
        Assert.Null(ctx.GetVisible(0xA9B40101u));
    }

    [Fact]
    public void GetVisible_ReturnsNullWhenTheCommittedCellHasNoWalkModel()
    {
        // A hand-built LoadedCell that never went through
        // EnvCellLandblockBuildBuilder.BuildVisibilityCell (test-only
        // shortcut some existing fixtures take) — Walk stays null.
        var cellVisibility = new CellVisibility();
        var loaded = new LoadedCell { CellId = 0xA9B40100u };
        cellVisibility.CommitLandblock(0xA9B4FFFFu, new[] { loaded });
        var ctx = new WalkProductionFrameContext(
            cellVisibility, new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);

        Assert.Null(ctx.GetVisible(0xA9B40100u));
    }

    [Fact]
    public void ObjectToClipAndViewpointIn_UseTheCellsOwnTransforms()
    {
        var cell = new WalkCell
        {
            CellId = 1,
            WorldTransform = Matrix4x4.CreateTranslation(10f, 0f, 0f),
            InverseWorldTransform = Matrix4x4.CreateTranslation(-10f, 0f, 0f),
        };
        Matrix4x4 vp = SimpleViewProjection();
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), new Vector3(10f, 0f, 0f), Vector3.UnitY,
            vp, 1024f, 768f);

        Assert.Equal(cell.WorldTransform * vp, ctx.ObjectToClip(cell));
        Assert.Equal(Vector3.Zero, ctx.ViewpointIn(cell));
    }

    [Fact]
    public void ViewpointInBuilding_ResolvesThroughWalkBuildingRegistry()
    {
        var registry = new WalkBuildingRegistry();
        var building = new WalkBuilding { PositionCellId = 1 };
        Matrix4x4 world = Matrix4x4.CreateTranslation(5f, 0f, 0f);
        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        registry.Publish(0xA9B4FFFFu, new[] { new WalkBuildingFactory.Entry(building, world, inverse) });
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), registry, new Vector3(5f, 0f, 0f), Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);

        Assert.Equal(Vector3.Zero, ctx.ViewpointInBuilding(building));
    }

    [Fact]
    public void ViewpointInBuilding_ThrowsWhenTheBuildingIsNotCommitted()
    {
        // Fail loud (the PV3 post-mortem rule): a walk/registry desync must
        // never resolve to a silently-skipped building.
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);
        var unregistered = new WalkBuilding { PositionCellId = 1 };

        Assert.Throws<InvalidOperationException>(() => ctx.ViewpointInBuilding(unregistered));
    }

    [Fact]
    public void ViewerDistanceTo_MeasuresToTheBuildingsTransformedSortCenter()
    {
        var registry = new WalkBuildingRegistry();
        var building = new WalkBuilding { PositionCellId = 1, SortCenter = new Vector3(0f, 3f, 0f) };
        Matrix4x4 world = Matrix4x4.CreateTranslation(0f, 10f, 0f);
        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        registry.Publish(0xA9B4FFFFu, new[] { new WalkBuildingFactory.Entry(building, world, inverse) });
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), registry, Vector3.Zero, Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);

        Assert.Equal(13f, ctx.ViewerDistanceTo(building));
    }

    [Fact]
    public void SetupPartZeroTransformIsSharedByViewpointDistanceAndWorldPublication()
    {
        var registry = new WalkBuildingRegistry();
        var building = new WalkBuilding
        {
            PositionCellId = 1,
            SortCenter = new Vector3(0f, 1f, 0f),
            PartZeroTransform = Matrix4x4.CreateScale(2f)
                * Matrix4x4.CreateTranslation(0f, 3f, 0f),
            PartZeroScaleZ = 2f,
        };
        Matrix4x4 root = Matrix4x4.CreateTranslation(0f, 10f, 0f);
        Matrix4x4.Invert(root, out Matrix4x4 inverseRoot);
        var entry = new WalkBuildingFactory.Entry(building, root, inverseRoot);
        registry.Publish(0xA9B4FFFFu, [entry]);
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), registry, new Vector3(0f, 19f, 0f), Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);

        Assert.Equal(entry.PartZeroWorldTransform,
            building.PartZeroTransform * entry.WorldTransform);
        Assert.Equal(new Vector3(0f, 3f, 0f), ctx.ViewpointInBuilding(building));
        Assert.Equal(2f, ctx.ViewerDistanceTo(building));
    }

    [Fact]
    public void CyPlane_MatchesTheRetailNearPlaneFormula()
    {
        Vector3 forward = Vector3.UnitY;
        var eye = new Vector3(0f, 5f, 0f);
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), eye, forward,
            SimpleViewProjection(), 1024f, 768f);

        Assert.Equal(forward, ctx.CyPlane.Normal);
        Assert.Equal(-Vector3.Dot(eye, forward) - WalkProductionFrameContext.ZNear, ctx.CyPlane.D);
    }

    [Fact]
    public void Constructor_RejectsANonInvertibleViewProjection()
    {
        Assert.Throws<ArgumentException>(() => new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            default, 1024f, 768f));
    }

    [Fact]
    public void Reset_RebindsCameraValuesAndRetainsTheRayCaster()
    {
        Matrix4x4 firstProjection = SimpleViewProjection();
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            firstProjection, 1024f, 768f);
        IWalkRayCaster retainedRays = ctx.Rays;

        var eye = new Vector3(4f, 5f, 6f);
        Vector3 forward = Vector3.UnitX;
        Matrix4x4 secondProjection =
            Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitZ)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 16f / 9f, 0.1f, 500f);

        ctx.Reset(eye, forward, secondProjection, 1760f, 990f);

        Assert.Same(retainedRays, ctx.Rays);
        Assert.Equal(eye, ctx.WorldViewpoint);
        Assert.Equal(1760f, ctx.ViewportWidth);
        Assert.Equal(990f, ctx.ViewportHeight);
        Assert.Equal(forward, ctx.CyPlane.Normal);
        Assert.Equal(-Vector3.Dot(eye, forward) - WalkProductionFrameContext.ZNear, ctx.CyPlane.D);
    }

    [Fact]
    public void Constructor_and_Reset_StoreTheViewerCellIdAndWeatherGate()
    {
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f);
        Assert.Equal(0u, ctx.ViewerCellId);
        Assert.False(ctx.WeatherGateOpen);

        var ctxArmed = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), Vector3.Zero, Vector3.UnitY,
            SimpleViewProjection(), 1024f, 768f,
            viewerCellId: 0xF4180003u, weatherGateOpen: true);
        Assert.Equal(0xF4180003u, ctxArmed.ViewerCellId);
        Assert.True(ctxArmed.WeatherGateOpen);

        ctxArmed.Reset(
            Vector3.Zero, Vector3.UnitY, SimpleViewProjection(), 1024f, 768f,
            viewerCellId: 0xA9B40100u, weatherGateOpen: false);
        Assert.Equal(0xA9B40100u, ctxArmed.ViewerCellId);
        Assert.False(ctxArmed.WeatherGateOpen);
    }

    [Fact]
    public void Reset_WithBadProjectionLeavesThePreviousBindingUsable()
    {
        Matrix4x4 projection = SimpleViewProjection();
        var eye = new Vector3(1f, 2f, 3f);
        var ctx = new WalkProductionFrameContext(
            new CellVisibility(), new WalkBuildingRegistry(), eye, Vector3.UnitY,
            projection, 1024f, 768f);
        Vector3 rayBeforeFailure = ctx.Rays.RayThrough(200f, 300f);

        Assert.Throws<ArgumentException>(() =>
            ctx.Reset(new Vector3(9f), Vector3.UnitX, default, 1f, 1f));

        Assert.Equal(eye, ctx.WorldViewpoint);
        Assert.Equal(1024f, ctx.ViewportWidth);
        Assert.Equal(768f, ctx.ViewportHeight);
        Assert.Equal(Vector3.UnitY, ctx.CyPlane.Normal);
        Assert.Equal(rayBeforeFailure, ctx.Rays.RayThrough(200f, 300f));
    }
}
