using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Walk;
using AcDream.App.Tests.Rendering.Gpu;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class WalkOutsideViewReassemblyTests
{
    private const float W = 1024f, H = 720f;

    private sealed class StubRays : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY) =>
            Vector3.Normalize(new Vector3(screenX - W / 2f, screenY - H / 2f, 1000f));
    }

    private static WalkPortalView WalkViewOfPixelQuads(params Vector2[][] pixelQuads)
    {
        var view = new WalkPortalView();
        var rays = new StubRays();
        foreach (Vector2[] quad in pixelQuads)
        {
            var pts = new WalkScreenPoint[quad.Length];
            for (int i = 0; i < quad.Length; i++)
                pts[i] = new WalkScreenPoint(quad[i].X, quad[i].Y, 0f, 1f);
            Assert.True(WalkCopyView.Append(view, pts, rays, Vector3.Zero));
        }
        return view;
    }

    private static ClipFrameAssembly BeginAssembly()
        => ClipFrameAssembler.BeginWalkFrame(ClipFrame.NoClip(), outdoorRoot: true);

    [Fact]
    public void PixelDoorway_MapsToExpectedNdcAabbAndPlanes()
    {
        ClipFrameAssembly asm = BeginAssembly();
        // Pixel quad x∈[256,512], y∈[180,360] → NDC x∈[-0.5,0], y∈[0,0.5]
        // (yNdc = 1 − 2·py/H flips the axis: py=180 → +0.5, py=360 → 0).
        WalkPortalView walkView = WalkViewOfPixelQuads(new[]
        {
            new Vector2(256f, 180f), new Vector2(512f, 180f),
            new Vector2(512f, 360f), new Vector2(256f, 360f),
        });

        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, walkView, W, H);

        ClipViewSlice slice = Assert.Single(asm.OutsideViewSlices);
        Assert.True(asm.OutdoorVisible);
        Assert.Equal(4, asm.OutsidePlaneCount);
        Assert.Equal(slice.Slot, asm.OutdoorSlot);
        Assert.NotEqual(0, slice.Slot);
        Assert.Equal(-0.5f, slice.NdcAabb.X, 3);
        Assert.Equal(0f, slice.NdcAabb.Y, 3);
        Assert.Equal(0f, slice.NdcAabb.Z, 3);
        Assert.Equal(0.5f, slice.NdcAabb.W, 3);
        Assert.Equal(slice.NdcAabb, asm.OutsideViewNdcAabb);

        Vector2[] ndcCorners =
        {
            new(-0.5f, 0f), new(0f, 0f), new(0f, 0.5f), new(-0.5f, 0.5f),
        };
        foreach (Vector2 corner in ndcCorners)
        {
            foreach (Vector4 plane in slice.Planes)
                Assert.True(plane.X * corner.X + plane.Y * corner.Y + plane.W >= -1e-4f);
        }
        bool outsideFails = false;
        foreach (Vector4 plane in slice.Planes)
            outsideFails |= plane.X * 0.9f + plane.Y * -0.9f + plane.W < 0f;
        Assert.True(outsideFails);
    }

    [Fact]
    public void FullViewportWalkQuad_CoversFullNdc()
    {
        ClipFrameAssembly asm = BeginAssembly();
        var walkView = new WalkPortalView();
        Assert.True(WalkCopyView.AppendFullViewportQuad(
            walkView, new StubRays(), Vector3.Zero, W, H));

        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, walkView, W, H);

        ClipViewSlice slice = Assert.Single(asm.OutsideViewSlices);
        Assert.Equal(new Vector4(-1f, -1f, 1f, 1f), slice.NdcAabb);
    }

    [Fact]
    public void EmptyWalkView_YieldsSkipMode()
    {
        ClipFrameAssembly asm = BeginAssembly();
        Assert.True(asm.OutdoorVisible);

        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, new WalkPortalView(), W, H);

        Assert.Empty(asm.OutsideViewSlices);
        Assert.False(asm.OutdoorVisible);
        Assert.False(asm.HasOutsideView);
        Assert.Equal(0, asm.OutsidePlaneCount);
        Assert.Equal(0, asm.OutdoorSlot);
    }

    [Fact]
    public void FirstViewCollapses_SecondSurvives_SlicesStayIndexAligned()
    {
        ClipFrameAssembly asm = BeginAssembly();

        var walkView = new WalkPortalView();
        walkView.View.Vertices.Add(new WalkViewVertex { Point = new Vector2(100f, 100f) });
        walkView.View.Vertices.Add(new WalkViewVertex { Point = new Vector2(200f, 100f) });
        walkView.View.Vertices.Add(new WalkViewVertex { Point = new Vector2(300f, 100f) });
        walkView.View.Polys.Add(new WalkViewPoly(3, 0, 100f, 300f, 100f, 100f));

        // View 1: a normal quad.
        int secondBase = walkView.View.Vertices.Count;
        Vector2[] secondQuad =
        [
            new(600f, 400f), new(900f, 400f), new(900f, 650f), new(600f, 650f),
        ];
        foreach (Vector2 pixel in secondQuad)
            walkView.View.Vertices.Add(new WalkViewVertex { Point = pixel });
        walkView.View.Polys.Add(new WalkViewPoly(4, secondBase, 600f, 900f, 400f, 650f));

        walkView.ViewCount = 2;

        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, walkView, W, H);

        Assert.Equal(2, asm.OutsideViewSlices.Length);

        ClipViewSlice collapsed = asm.OutsideViewSlices[0];
        Assert.True(collapsed.NothingVisible);
        Assert.Equal(0, collapsed.Slot);
        Assert.Empty(collapsed.Planes);
        Assert.Equal(default, collapsed.NdcAabb);

        ClipViewSlice survivor = asm.OutsideViewSlices[1];
        Assert.False(survivor.NothingVisible);
        var expectedNdcVerts = new Vector2[secondQuad.Length];
        for (int i = 0; i < secondQuad.Length; i++)
        {
            expectedNdcVerts[i] = new Vector2(
                secondQuad[i].X / W * 2f - 1f,
                1f - secondQuad[i].Y / H * 2f);
        }
        AssertEveryEdgeMidpointLiesOnSomeGpuPlane(expectedNdcVerts, survivor.Planes);
    }

    [Fact]
    public void PunchLeaf_UsesIndexAlignedSlice_DrawsNothingForCollapsed_ThrowsOutOfRange()
    {
        ClipFrameAssembly asm = BeginAssembly();
        var walkView = new WalkPortalView();
        walkView.View.Vertices.Add(new WalkViewVertex { Point = new Vector2(100f, 100f) });
        walkView.View.Vertices.Add(new WalkViewVertex { Point = new Vector2(200f, 100f) });
        walkView.View.Vertices.Add(new WalkViewVertex { Point = new Vector2(300f, 100f) });
        walkView.View.Polys.Add(new WalkViewPoly(3, 0, 100f, 300f, 100f, 100f));
        int secondBase = walkView.View.Vertices.Count;
        Vector2[] secondQuad =
        [
            new(600f, 400f), new(900f, 400f), new(900f, 650f), new(600f, 650f),
        ];
        foreach (Vector2 pixel in secondQuad)
            walkView.View.Vertices.Add(new WalkViewVertex { Point = pixel });
        walkView.View.Polys.Add(new WalkViewPoly(4, secondBase, 600f, 900f, 400f, 650f));
        walkView.ViewCount = 2;
        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, walkView, W, H);
        Assert.Equal(2, asm.OutsideViewSlices.Length);

        using var device = new RecordingGpuDevice();
        var frames = new GpuDeviceFrameLifetime(device);
        var scope = new VulkanWorldPassScope(sampleCount: 1);
        using var portalDepthMask = new PortalDepthMaskRenderer(device, frames, scope);

        var executor = (RetailPViewPassExecutor)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RetailPViewPassExecutor));
        typeof(RetailPViewPassExecutor)
            .GetField("_portalDepthMask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(executor, portalDepthMask);

        var worldPolygon = new WalkPolygon
        {
            Vertices = new[]
            {
                new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(0f, 1f, 0f),
            },
        };

        RetailPViewFrameInput frame = new RetailPViewFrameInput().Reset(
            rootCell: null!,
            nearbyBuildingCells: null,
            viewerEyePos: Vector3.Zero,
            viewProjection: Matrix4x4.Identity,
            cells: null!,
            camera: null!,
            cameraWorldPosition: Vector3.Zero,
            frustum: null,
            playerLandblockId: null,
            animatedEntityIds: null,
            renderCenterLbX: 0,
            renderCenterLbY: 0,
            renderRadius: 0,
            landblockEntries: Array.Empty<(uint, Vector3, Vector3, IReadOnlyList<AcDream.Core.World.WorldEntity>, IReadOnlyDictionary<uint, AcDream.Core.World.WorldEntity>?)>(),
            renderSky: false,
            renderWeather: false,
            dayFraction: 0f,
            activeDayGroup: null,
            skyKeyframe: default,
            environOverrideActive: false,
            viewerCellId: 0,
            playerCellId: 0,
            playerViewPosition: Vector3.Zero,
            cameraView: Matrix4x4.Identity,
            cameraCellResolution: default);

        void RunDraw(int activeViewIndex)
        {
            frames.BeginFrame();
            portalDepthMask.BeginFrame(frameSlot: 0);
            using (IGpuPassEncoder pass = frames.CurrentFrame!.BeginPass(
                       GpuPassDescription.BackbufferClear(
                           "s3-f1-punch-leaf", Vector4.Zero, sampleCount: 1)))
            using (scope.Publish(pass))
            {
                executor.DrawWalkPunchFan(frame, asm, worldPolygon, activeViewIndex);
            }
            frames.EndFrame();
        }

        int drawsBefore = device.Calls.OfType<GpuRecordedDraw>().Count();
        RunDraw(0);
        int drawsAfterCollapsed = device.Calls.OfType<GpuRecordedDraw>().Count();
        Assert.Equal(drawsBefore, drawsAfterCollapsed);

        // activeViewIndex = 1: the surviving view. Submits exactly one fan draw.
        RunDraw(1);
        int drawsAfterSurvivor = device.Calls.OfType<GpuRecordedDraw>().Count();
        Assert.Equal(drawsAfterCollapsed + 1, drawsAfterSurvivor);

        // activeViewIndex = 2: out of range. Fails loud instead of drawing unclipped.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            frames.BeginFrame();
            portalDepthMask.BeginFrame(frameSlot: 0);
            using (IGpuPassEncoder pass = frames.CurrentFrame!.BeginPass(
                       GpuPassDescription.BackbufferClear(
                           "s3-f1-punch-leaf-oob", Vector4.Zero, sampleCount: 1)))
            using (scope.Publish(pass))
            {
                executor.DrawWalkPunchFan(frame, asm, worldPolygon, 2);
            }
        });
        Assert.Contains("activeViewIndex", ex.Message);
    }

    private static void AssertEveryEdgeMidpointLiesOnSomeGpuPlane(
        Vector2[] verts, System.ReadOnlySpan<Vector4> gpuPlanes)
    {
        const float eps = 1e-4f;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector2 a = verts[i];
            Vector2 b = verts[(i + 1) % verts.Length];
            Vector2 mid = (a + b) / 2f;
            var clip = new Vector4(mid.X, mid.Y, 0f, 1f);

            float minAbsDistance = float.PositiveInfinity;
            foreach (Vector4 plane in gpuPlanes)
            {
                float distance = Vector4.Dot(plane, clip);
                Assert.True(
                    distance >= -eps,
                    $"edge {i} midpoint ({mid.X},{mid.Y}) must be inside-or-on every "
                    + $"GPU plane; plane {plane} gave distance {distance}");
                minAbsDistance = MathF.Min(minAbsDistance, MathF.Abs(distance));
            }
            Assert.True(
                minAbsDistance < eps,
                $"edge {i} midpoint ({mid.X},{mid.Y}) should lie ~on its OWN GPU plane; "
                + $"the closest plane was only {minAbsDistance} away");
        }
    }

    [Fact]
    public void NineVertexOutsideView_PunchSliceHasZeroPlanes_DrawsUnclipped_NotNothingVisible()
    {
        ClipFrameAssembly asm = BeginAssembly();

        const int n = 9;
        var verts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float angle = i * MathF.Tau / n;
            verts[i] = new Vector2(0.6f * MathF.Cos(angle), 0.6f * MathF.Sin(angle));
        }
        var pixelPoints = new WalkScreenPoint[n];
        for (int i = 0; i < n; i++)
        {
            pixelPoints[i] = new WalkScreenPoint(
                (verts[i].X + 1f) * W / 2f, (1f - verts[i].Y) * H / 2f, 0f, 1f);
        }
        var walkView = new WalkPortalView();
        Assert.True(WalkCopyView.Append(walkView, pixelPoints, new StubRays(), Vector3.Zero));
        Assert.Equal(n, walkView.View.Polys[0].VertexCount);

        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, walkView, W, H);

        ClipViewSlice slice = Assert.Single(asm.OutsideViewSlices);
        Assert.False(slice.NothingVisible);
        Assert.Equal(0, slice.Slot);
        Assert.Empty(slice.Planes);
    }

    [Fact]
    public void TwoWalkViews_ProduceTwoSlicesWithDistinctSlots()
    {
        ClipFrameAssembly asm = BeginAssembly();
        WalkPortalView walkView = WalkViewOfPixelQuads(
            new[]
            {
                new Vector2(100f, 100f), new Vector2(300f, 100f),
                new Vector2(300f, 300f), new Vector2(100f, 300f),
            },
            new[]
            {
                new Vector2(600f, 400f), new Vector2(900f, 400f),
                new Vector2(900f, 650f), new Vector2(600f, 650f),
            });

        ClipFrameAssembler.ReassembleOutsideViewFromWalk(asm, walkView, W, H);

        Assert.Equal(2, asm.OutsideViewSlices.Length);
        Assert.NotEqual(asm.OutsideViewSlices[0].Slot, asm.OutsideViewSlices[1].Slot);
        // The union AABB spans both doorways.
        Assert.True(asm.OutsideViewNdcAabb.X < asm.OutsideViewSlices[0].NdcAabb.Z);
        Assert.True(asm.OutsideViewNdcAabb.Z >= asm.OutsideViewSlices[1].NdcAabb.X);
    }
}
