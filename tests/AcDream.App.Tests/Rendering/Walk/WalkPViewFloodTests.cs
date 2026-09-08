using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkPViewFloodTests
{
    private sealed class TestContext : IWalkFrameContext
    {
        private sealed class Caster : IWalkRayCaster
        {
            public Vector3 RayThrough(float screenX, float screenY)
                => new(screenX, screenY, 100f);
        }

        public readonly Dictionary<uint, WalkCell> Cells = new();
        private readonly Matrix4x4 _viewProj;

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
    }

    private static WalkPolygon Quad(float z, float half = 0.5f, bool facingViewer = true) => new()
    {
        Vertices =
        [
            new Vector3(-half, -half, z), new Vector3(half, -half, z),
            new Vector3(half, half, z), new Vector3(-half, half, z),
        ],
        // Plane through the quad: for z=-2 facing +z, N=(0,0,1), D=2 (eye at
        // origin sits on the POSITIVE side: d = +2).
        Plane = new WalkPlane(new Vector3(0, 0, facingViewer ? 1f : -1f), facingViewer ? -z : z),
    };

    private static WalkCell Cell(
        TestContext ctx, uint id, params (WalkCellPortal Portal, WalkPolygon Polygon)[] portals)
    {
        var cell = new WalkCell
        {
            CellId = id,
            Portals = portals.Select(p => p.Portal).ToArray(),
            PortalPolygons = portals.Select(p => p.Polygon).ToArray(),
        };
        cell.PushView();          // add_views/stab-list stand-in: one pushed slot
        ctx.Cells[id] = cell;
        return cell;
    }

    private static WalkPView SeedAndFlood(TestContext ctx, WalkCell seed)
    {
        var pview = new WalkPView();
        WalkCopyView.AppendFullViewportQuad(
            seed.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        pview.ConstructView(seed, 0xFFFF, ctx);
        return pview;
    }

    [Fact]
    public void Flood_traverses_a_look_through_portal_into_the_neighbor()
    {
        var ctx = new TestContext();
        // Eye on the positive side of the portal plane (d=+2 → side 0);
        // PortalSide=0 → side == PortalSide → an OPENING (look-through).
        WalkCell seed = Cell(ctx, 0x100,
            (new WalkCellPortal { OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));
        WalkCell neighbor = Cell(ctx, 0x101,
            (new WalkCellPortal { OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0 },
             Quad(-2f)));

        WalkPView pview = SeedAndFlood(ctx, seed);

        Assert.Equal(new[] { 0x100u, 0x101u }, pview.CellDrawList.Select(c => c.CellId));
        Assert.Equal(1, neighbor.TopView.ViewCount);
        Assert.True(neighbor.TopView.CellViewDone);       // and the neighbor was processed
    }

    [Fact]
    public void Facing_portal_is_not_traversed_but_feeds_the_distance_key()
    {
        var ctx = new TestContext();
        WalkCell seed = Cell(ctx, 0x100,
            (new WalkCellPortal { OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0 },
             Quad(-2f)));
        Cell(ctx, 0x101,
            (new WalkCellPortal { OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));

        WalkPView pview = SeedAndFlood(ctx, seed);

        Assert.Equal(new[] { 0x100u }, pview.CellDrawList.Select(c => c.CellId));
        // max_indist = squared distance to the farthest facing-portal vertex:
        // (±0.5, ±0.5, −2) from the origin → 0.25 + 0.25 + 4.
        Assert.Equal(4.5f, seed.TopView.MaxInDistSquared, 3);
    }

    [Fact]
    public void Exit_portal_raises_the_outside_view_only_when_landscape_is_drawn()
    {
        var ctx = new TestContext();
        WalkCell seed = Cell(ctx, 0x100,
            (new WalkCellPortal { OtherCellId = 0xFFFFFFFF, PolygonIndex = 0, PortalSide = 0, OtherPortalId = -1 },
             Quad(-2f)));

        WalkPView pview = SeedAndFlood(ctx, seed);
        Assert.Equal(1, pview.OutsideView.ViewCount);

        // draw_landscape == 0 discards exit views entirely.
        seed.PopView();
        seed.PushView();
        var noLandscape = new WalkPView { DrawLandscape = false };
        WalkCopyView.AppendFullViewportQuad(
            seed.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        noLandscape.ConstructView(seed, 0xFFFF, ctx);
        Assert.Equal(0, noLandscape.OutsideView.ViewCount);
    }

    [Fact]
    public void Flood_never_walks_back_through_the_entry_portal()
    {
        var ctx = new TestContext();
        WalkCell a = Cell(ctx, 0x100,
            (new WalkCellPortal { OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));
        WalkCell b = Cell(ctx, 0x101,
            (new WalkCellPortal { OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));

        WalkPView pview = SeedAndFlood(ctx, a);

        Assert.Equal(2, pview.CellDrawList.Count);
        Assert.Equal(2, pview.CellDrawList.Select(c => c.CellId).Distinct().Count());
    }

    [Fact]
    public void Deeper_chain_floods_in_nearest_first_pop_order()
    {
        var ctx = new TestContext();
        // seed → mid (portal at z=-2) → far (portal at z=-4): the draw list
        // appends in pop order (nearest first), so the end-first draw walk
        // is far-to-near.
        WalkCell seed = Cell(ctx, 0x100,
            (new WalkCellPortal { OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));
        WalkCell mid = Cell(ctx, 0x101,
            (new WalkCellPortal { OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0 },
             Quad(-2f)),
            (new WalkCellPortal { OtherCellId = 0x102, PolygonIndex = 1, PortalSide = 0, OtherPortalId = 0 },
             Quad(-4f, half: 0.4f)));
        WalkCell far = Cell(ctx, 0x102,
            (new WalkCellPortal { OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 1 },
             Quad(-4f, half: 0.4f)));

        WalkPView pview = SeedAndFlood(ctx, seed);

        Assert.Equal(
            new[] { 0x100u, 0x101u, 0x102u },
            pview.CellDrawList.Select(c => c.CellId));
        Assert.Equal(1, far.TopView.ViewCount);
    }


    [Fact]
    public void ConstructView_CalledTwiceOnTheSameInstance_ResetsCellDrawListAndOutsideViewEachTime()
    {
        var ctx = new TestContext();
        WalkCell first = Cell(ctx, 0x200,
            (new WalkCellPortal { OtherCellId = 0xFFFFFFFF, PolygonIndex = 0, PortalSide = 0, OtherPortalId = -1 },
             Quad(-2f)));
        WalkCell second = Cell(ctx, 0x300,
            (new WalkCellPortal { OtherCellId = 0x301, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));
        Cell(ctx, 0x301,
            (new WalkCellPortal { OtherCellId = 0x300, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0 },
             Quad(-2f)));

        var pview = new WalkPView();
        WalkCopyView.AppendFullViewportQuad(
            first.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        pview.ConstructView(first, 0xFFFF, ctx);
        Assert.Equal(new[] { 0x200u }, pview.CellDrawList.Select(c => c.CellId));
        Assert.Equal(1, pview.OutsideView.ViewCount);
        int timestampAfterFirst = WalkPView.MasterTimestampForDiagnostics;

        WalkCopyView.AppendFullViewportQuad(
            second.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        pview.ConstructView(second, 0xFFFF, ctx);

        Assert.Equal(new[] { 0x300u, 0x301u }, pview.CellDrawList.Select(c => c.CellId));
        Assert.Equal(0, pview.OutsideView.ViewCount);
        Assert.True(WalkPView.MasterTimestampForDiagnostics > timestampAfterFirst);
    }

    [Fact]
    public void Unloaded_neighbor_is_silently_skipped()
    {
        var ctx = new TestContext();
        WalkCell seed = Cell(ctx, 0x100,
            (new WalkCellPortal { OtherCellId = 0x0DEAD, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0 },
             Quad(-2f)));

        WalkPView pview = SeedAndFlood(ctx, seed);

        Assert.Equal(new[] { 0x100u }, pview.CellDrawList.Select(c => c.CellId));
    }
}
