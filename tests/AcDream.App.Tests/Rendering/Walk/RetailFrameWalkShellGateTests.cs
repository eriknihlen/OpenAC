using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

/// <summary>A building's shell mesh and the meshes of the cells behind its
/// doorways are prepared by independent asynchronous queues. These pin the rule
/// that the walk draws a building only when the level it selected can actually
/// be drawn — shell and look-in together, or neither.</summary>
public sealed class RetailFrameWalkShellGateTests
{
    private sealed class Recorder : IWalkEventSink
    {
        public readonly List<uint> InteriorCells = new();
        public readonly List<WalkPolygon> Punches = new();
        public int BuildingTurns;
        public int ShellTurns;

        public void OnBuildingTurn(WalkBuilding building) => BuildingTurns++;

        public void OnBuildingShellTurn(
            WalkBuilding building, WalkBuildingSelection selection) => ShellTurns++;

        public void OnPunchGeometry(
            WalkBuilding building, WalkPolygon polygon, int activeViewIndex)
            => Punches.Add(polygon);

        public void Emit(in WalkEvent walkEvent)
        {
            if (walkEvent.Kind == WalkEventKind.DrawCells)
                InteriorCells.AddRange(walkEvent.Cells);
        }
    }

    private sealed class Caster : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY)
            => new(screenX, screenY, 100f);
    }

    private sealed class TestContext : IWalkFrameContext, IRetailFrameWalkContext
    {
        public readonly Dictionary<uint, WalkCell> Cells = new();
        public readonly HashSet<uint> ResidentShells = new();
        private readonly Matrix4x4 _viewProj;
        private static readonly Vector2[] RootQuad =
        [
            new(0, 480), new(640, 480), new(640, 0), new(0, 0),
        ];

        public TestContext()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1f, 0.1f, 1000f);
            _viewProj = view * proj;
        }

        public Vector3 ViewpointIn(WalkCell cell) => Vector3.Zero;
        public Matrix4x4 ObjectToClip(WalkCell cell) => _viewProj;
        public WalkCell? GetVisible(uint cellId) => Cells.GetValueOrDefault(cellId);
        public IWalkRayCaster Rays { get; } = new Caster();
        public Vector3 WorldViewpoint => Vector3.Zero;
        public float ViewportWidth => 640f;
        public float ViewportHeight => 480f;
        public WalkPlane CyPlane => new(new Vector3(0, 0, 1), 0f);
        public IWalkFrameContext CellContext => this;
        public Vector3 ViewpointInBuilding(WalkBuilding building) => Vector3.Zero;
        public float ViewerDistanceTo(WalkBuilding building) => 0f;
        public void SetActiveView(WalkPortalView views, int index) { }

        public bool IsBuildingShellDrawable(uint gfxObjId)
            => ResidentShells.Contains(gfxObjId);

        public int ClipBuildingPolygon(
            WalkBuilding building, WalkPolygon polygon, int side, Span<WalkScreenPoint> output)
        {
            Span<WalkScreenPoint> projected = stackalloc WalkScreenPoint[polygon.Vertices.Length];
            for (int i = 0; i < polygon.Vertices.Length; i++)
                projected[i] = WalkScreenClip.TransformToScreen(
                    polygon.Vertices[i], _viewProj, ViewportWidth, ViewportHeight);
            if (side != 0)
                projected.Reverse();
            return WalkScreenClip.ClipAgainstView(projected, RootQuad, output);
        }
    }

    private const uint ShellGfxObjId = 0x0100ABCDu;
    private const uint InteriorCellId = 0xF5180104u;

    private static WalkPolygon Quad(float z) => new()
    {
        Vertices =
        [
            new Vector3(-0.5f, -0.5f, z), new Vector3(0.5f, -0.5f, z),
            new Vector3(0.5f, 0.5f, z), new Vector3(-0.5f, 0.5f, z),
        ],
        Plane = new WalkPlane(new Vector3(0, 0, 1f), -z),
    };

    /// <summary>A building with one doorway into one loaded interior cell, and
    /// a full-viewport active view to walk it through.</summary>
    private static (TestContext Ctx, WalkBuilding Building, WalkPortalView Views) Fixture()
    {
        var ctx = new TestContext();
        var interior = new WalkCell
        {
            CellId = InteriorCellId,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0xFFFFFFFF, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[interior.CellId] = interior;

        var portalRef = new WalkPortalRef { PortalIndex = 0, Polygon = Quad(-2f) };
        var building = new WalkBuilding
        {
            PositionCellId = 0xF518002Eu,
            GfxObjId = ShellGfxObjId,
            Portals =
            [
                new WalkBldPortal
                {
                    PortalSide = 0, OtherCellId = InteriorCellId, OtherPortalId = 0,
                    StabList = [InteriorCellId],
                },
            ],
            DrawingBsp = new WalkBspNode
            {
                SplittingPlane = new WalkPlane(new Vector3(0, 0, 1f), 100f),
                InPortals = [portalRef],
            },
        };

        var views = new WalkPortalView();
        views.ResetForPush();
        WalkCopyView.AppendFullViewportQuad(views, ctx.Rays, ctx.WorldViewpoint, 640f, 480f);
        return (ctx, building, views);
    }

    [Fact]
    public void A_building_whose_shell_is_not_yet_drawable_draws_nothing_at_all()
    {
        (TestContext ctx, WalkBuilding building, WalkPortalView views) = Fixture();
        var recorder = new Recorder();

        new RetailFrameWalk().DrawBuilding(building, views, ctx, recorder);

        Assert.Empty(recorder.InteriorCells);
        Assert.Empty(recorder.Punches);
        Assert.Equal(0, recorder.BuildingTurns);
        Assert.Equal(0, recorder.ShellTurns);
    }

    [Fact]
    public void The_same_building_draws_shell_and_interior_once_its_shell_is_drawable()
    {
        (TestContext ctx, WalkBuilding building, WalkPortalView views) = Fixture();
        ctx.ResidentShells.Add(ShellGfxObjId);
        var recorder = new Recorder();

        new RetailFrameWalk().DrawBuilding(building, views, ctx, recorder);

        Assert.Equal(new[] { InteriorCellId }, recorder.InteriorCells);
        Assert.Single(recorder.Punches);
        Assert.Equal(1, recorder.BuildingTurns);
        Assert.Equal(1, recorder.ShellTurns);
    }

    [Fact]
    public void An_interior_that_is_ready_first_waits_for_the_shell_rather_than_drawing_alone()
    {
        (TestContext ctx, WalkBuilding building, WalkPortalView views) = Fixture();
        var walk = new RetailFrameWalk();

        // Frame 1: the interior cell is loaded and its doorway is walkable, but
        // the shell level has not finished preparing.
        Assert.NotNull(ctx.GetVisible(InteriorCellId));
        var beforeShell = new Recorder();
        walk.DrawBuilding(building, views, ctx, beforeShell);
        Assert.Empty(beforeShell.InteriorCells);

        // Frame 2: the shell arrives; the whole building enters on one frame.
        ctx.ResidentShells.Add(ShellGfxObjId);
        var afterShell = new Recorder();
        walk.DrawBuilding(building, views, ctx, afterShell);
        Assert.Equal(new[] { InteriorCellId }, afterShell.InteriorCells);
        Assert.Equal(1, afterShell.ShellTurns);
    }

    [Fact]
    public void A_degraded_out_level_is_still_refused_before_the_residency_question()
    {
        (TestContext ctx, WalkBuilding building, WalkPortalView views) = Fixture();
        building.GfxObjId = 0u;
        ctx.ResidentShells.Add(0u);
        var recorder = new Recorder();

        new RetailFrameWalk().DrawBuilding(building, views, ctx, recorder);

        Assert.Equal(0, recorder.BuildingTurns);
        Assert.Empty(recorder.InteriorCells);
    }

    // ── the production answer behind IsBuildingShellDrawable ────────────────

    private static void SetRuntimeHiddenMarkers(WbMeshAdapter adapter, params uint[] markerIds)
    {
        FieldInfo field = typeof(WbMeshAdapter).GetField(
            "_runtimeHiddenMarker", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "WbMeshAdapter._runtimeHiddenMarker field not found — test relies on this exact name.");
        var markers = new HashSet<uint>(markerIds);
        field.SetValue(adapter, (Func<uint, bool>)markers.Contains);
    }

    private static IReadOnlyCollection<ulong> PreparationRequests(WbDrawDispatcher dispatcher)
    {
        FieldInfo field = typeof(WbDrawDispatcher).GetField(
            "_missRequested", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "WbDrawDispatcher._missRequested field not found — test relies on this exact name.");
        return (HashSet<ulong>)field.GetValue(dispatcher)!;
    }

    [Fact]
    public void A_never_drawn_marker_level_is_refused_and_never_asked_for()
    {
        using var fx = new WalkFrameDriverTests.DispatcherFixture();
        const uint markerId = 0x010001ECu;
        SetRuntimeHiddenMarkers(fx.MeshAdapter, markerId);

        Assert.False(fx.Dispatcher.IsShellDrawable(markerId));
        Assert.False(fx.Dispatcher.IsShellDrawable(markerId));

        // A level that never draws at any distance is the same state as a level
        // authored blank: nothing to wait for, so nothing is requested.
        Assert.Empty(PreparationRequests(fx.Dispatcher));
    }

    [Fact]
    public void A_shell_that_is_merely_absent_is_refused_and_requested_once()
    {
        using var fx = new WalkFrameDriverTests.DispatcherFixture();
        const uint absentId = 0x0100BEEFu;

        Assert.False(fx.Dispatcher.IsShellDrawable(absentId));
        Assert.False(fx.Dispatcher.IsShellDrawable(absentId));

        Assert.Equal(new ulong[] { absentId }, PreparationRequests(fx.Dispatcher));
    }

    [Fact]
    public void A_blank_level_is_refused_without_consulting_the_mesh_layer()
    {
        using var fx = new WalkFrameDriverTests.DispatcherFixture();

        Assert.False(fx.Dispatcher.IsShellDrawable(0u));

        Assert.Empty(PreparationRequests(fx.Dispatcher));
    }

    [Fact]
    public void A_prepared_shell_is_drawable_and_asks_for_nothing()
    {
        using var fx = new WalkFrameDriverTests.DispatcherFixture();
        const uint preparedId = 0x0100ABCDu;
        InjectRenderData(fx.Manager, preparedId, new ObjectRenderData());

        Assert.True(fx.Dispatcher.IsShellDrawable(preparedId));

        Assert.Empty(PreparationRequests(fx.Dispatcher));
    }

    private static void InjectRenderData(
        ObjectMeshManager manager, ulong id, ObjectRenderData data)
    {
        FieldInfo field = typeof(ObjectMeshManager).GetField(
            "_renderData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "ObjectMeshManager._renderData field not found — test relies on this exact name.");
        var dict = (ConcurrentDictionary<ulong, ObjectRenderData>)field.GetValue(manager)!;
        dict[id] = data;
    }
}
