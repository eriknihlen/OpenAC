using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkBuildingPortalTests
{
    private sealed class RecordingSink : WalkBuildingPortals.IWalkPortalPassSink
    {
        public readonly List<WalkPolygon> Punches = new();
        public readonly List<uint[]> DrawCells = new();

        public void OnPunch(WalkPolygon polygon) => Punches.Add(polygon);

        public void OnDrawCells(WalkPView pview)
            => DrawCells.Add(pview.CellDrawList.Select(c => c.CellId).ToArray());
    }

    private sealed class Caster : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY)
            => new(screenX, screenY, 100f);
    }

    private sealed class TestContext : IWalkFrameContext, IWalkBuildingFrameContext
    {
        public readonly Dictionary<uint, WalkCell> Cells = new();
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

        public Vector3 ViewpointInBuilding(WalkBuilding building) => Vector3.Zero;
        public float ViewerDistanceTo(WalkBuilding building) => 0f;
        public IWalkFrameContext CellContext => this;

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

    private static WalkPolygon Quad(float z, float half = 0.5f, bool facingViewer = true) => new()
    {
        Vertices =
        [
            new Vector3(-half, -half, z), new Vector3(half, -half, z),
            new Vector3(half, half, z), new Vector3(-half, half, z),
        ],
        Plane = new WalkPlane(new Vector3(0, 0, facingViewer ? 1f : -1f), facingViewer ? -z : z),
    };

    private static WalkBspNode PortalNode(WalkPlane plane, params WalkPortalRef[] portals)
        => new() { SplittingPlane = plane, InPortals = portals };

    // ---- the BSP portal-only walk ----

    [Fact]
    public void Bsp_walk_emits_the_far_side_first()
    {
        var farPortal = new WalkPortalRef { PortalIndex = 0, Polygon = Quad(-4f) };
        var nearPortal = new WalkPortalRef { PortalIndex = 1, Polygon = Quad(-2f) };
        var root = new WalkBspNode
        {
            SplittingPlane = new WalkPlane(new Vector3(1, 0, 0), 0f),
            NegNode = PortalNode(new WalkPlane(new Vector3(0, 0, 1), 100f), farPortal),
            PosNode = PortalNode(new WalkPlane(new Vector3(0, 0, 1), 100f), nearPortal),
        };
        var emitted = new List<int>();

        WalkBuildingPortals.BuildDrawPortalsOnly(
            root, 1, new Vector3(5, 0, 0), (p, _) => emitted.Add(p.PortalIndex));
        Assert.Equal(new[] { 0, 1 }, emitted);

        emitted.Clear();
        WalkBuildingPortals.BuildDrawPortalsOnly(
            root, 1, new Vector3(-5, 0, 0), (p, _) => emitted.Add(p.PortalIndex));
        Assert.Equal(new[] { 1, 0 }, emitted);
    }

    [Fact]
    public void In_plane_portal_node_emits_nothing()
    {
        var portal = new WalkPortalRef { PortalIndex = 0, Polygon = Quad(-2f) };
        // Viewer exactly on the node's splitting plane (|d| <= epsilon).
        WalkBspNode root = PortalNode(new WalkPlane(new Vector3(1, 0, 0), 0f), portal);
        var emitted = new List<int>();

        WalkBuildingPortals.BuildDrawPortalsOnly(
            root, 1, Vector3.Zero, (p, _) => emitted.Add(p.PortalIndex));

        Assert.Empty(emitted);
    }


    private static (TestContext ctx, WalkBuilding building, WalkCell interior, WalkPortalRef portalRef)
        BuildLookInFixture(int portalSide = 0)
    {
        var ctx = new TestContext();
        var interior = new WalkCell
        {
            CellId = 0x104,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0xFFFFFFFF, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[interior.CellId] = interior;
        var building = new WalkBuilding
        {
            PositionCellId = 0xA9B4000Fu,
            Portals =
            [
                new WalkBldPortal
                {
                    PortalSide = portalSide, OtherCellId = 0x104, OtherPortalId = 0,
                    StabList = [0x104u],
                },
            ],
        };
        var portalRef = new WalkPortalRef { PortalIndex = 0, Polygon = Quad(-2f) };
        return (ctx, building, interior, portalRef);
    }

    [Fact]
    public void Pass_one_punches_and_appends_the_view_without_flooding()
    {
        (TestContext ctx, WalkBuilding building, WalkCell interior, WalkPortalRef portalRef)
            = BuildLookInFixture();
        var pview = new WalkPView();
        var sink = new RecordingSink();

        bool ok = WalkBuildingPortals.DrawPortal(pview, building, portalRef, 1, ctx, sink);

        Assert.True(ok);
        Assert.Single(sink.Punches);
        Assert.Empty(sink.DrawCells);
        Assert.Empty(pview.CellDrawList);
        Assert.Equal(0, interior.NumView);   // stab views popped back
    }

    [Fact]
    public void Pass_two_floods_the_interior_and_emits_the_draw_cells_event()
    {
        (TestContext ctx, WalkBuilding building, WalkCell interior, WalkPortalRef portalRef)
            = BuildLookInFixture();
        var pview = new WalkPView();
        var sink = new RecordingSink();

        bool ok = WalkBuildingPortals.DrawPortal(pview, building, portalRef, 2, ctx, sink);

        Assert.True(ok);
        Assert.Empty(sink.Punches);          // pass 2 never draws the poly
        Assert.Single(sink.DrawCells);
        Assert.Equal(new[] { 0x104u }, sink.DrawCells[0]);
    }

    [Fact]
    public void Wrong_viewer_side_rejects_the_look_in()
    {
        (TestContext ctx, WalkBuilding building, WalkCell interior, WalkPortalRef portalRef)
            = BuildLookInFixture(portalSide: 1);
        var pview = new WalkPView();
        var sink = new RecordingSink();

        Assert.False(WalkBuildingPortals.DrawPortal(pview, building, portalRef, 1, ctx, sink));
        Assert.Empty(sink.Punches);
        Assert.Empty(sink.DrawCells);
    }

    [Fact]
    public void Unloaded_destination_skips_the_punch_silently()
    {
        (TestContext ctx, WalkBuilding building, WalkCell interior, WalkPortalRef portalRef)
            = BuildLookInFixture();
        ctx.Cells.Remove(0x104);   // destination not Visible

        var pview = new WalkPView();
        var sink = new RecordingSink();

        Assert.False(WalkBuildingPortals.DrawPortal(pview, building, portalRef, 1, ctx, sink));
        Assert.Empty(sink.Punches);   // no fallback seal on the outdoor path
    }
}
