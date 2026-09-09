using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Walk;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class ClipFrameLayoutTests
{
    private static float ReadFloat(System.ReadOnlySpan<byte> b, int offset)
        => System.BitConverter.ToSingle(b.Slice(offset, 4));

    private static uint ReadUInt(System.ReadOnlySpan<byte> b, int offset)
        => System.BitConverter.ToUInt32(b.Slice(offset, 4));

    private static int ReadInt(System.ReadOnlySpan<byte> b, int offset)
        => System.BitConverter.ToInt32(b.Slice(offset, 4));

    [Fact]
    public void LayoutConstants_MatchShaderStruct()
    {
        Assert.Equal(144, ClipFrame.CellClipStrideBytes);
        Assert.Equal(16, ClipFrame.CellClipPlanesOffset);
        Assert.Equal(8, ClipFrame.MaxPlanes);
        Assert.Equal(144, ClipFrame.TerrainUboBytes);
        Assert.Equal(2u, GpuBindingModel.StorageClipRegions);
        Assert.Equal(2u, ClipFrame.TerrainClipUboBinding);
    }

    [Fact]
    public void NoClip_HasExactlyOneSlot_AllZeros_Count0()
    {
        var frame = ClipFrame.NoClip();
        Assert.Equal(1, frame.SlotCount);

        var bytes = frame.RegionBytesForTest;
        Assert.Equal(ClipFrame.CellClipStrideBytes, bytes.Length); // 144 — exactly one slot

        Assert.Equal(0u, ReadUInt(bytes, 0));
        foreach (var b in bytes)
            Assert.Equal(0, b);
    }

    [Fact]
    public void AppendSlot_WritesCountAndPlanes_AtStd430Offsets()
    {
        var frame = ClipFrame.NoClip();

        // Three distinct planes so each lands at a verifiable offset.
        var p0 = new Vector4(1f, 0f, 0f, 0.5f);
        var p1 = new Vector4(0f, 1f, 0f, 0.25f);
        var p2 = new Vector4(-1f, 0f, 0f, -0.75f);

        int slot = frame.AppendSlot(new[] { p0, p1, p2 });
        Assert.Equal(1, slot);
        Assert.Equal(2, frame.SlotCount);

        var bytes = frame.RegionBytesForTest;
        Assert.Equal(2 * ClipFrame.CellClipStrideBytes, bytes.Length); // two slots now

        int baseOff = slot * ClipFrame.CellClipStrideBytes; // 144

        Assert.Equal(3u, ReadUInt(bytes, baseOff + 0));
        Assert.Equal(0u, ReadUInt(bytes, baseOff + 4));
        Assert.Equal(0u, ReadUInt(bytes, baseOff + 8));
        Assert.Equal(0u, ReadUInt(bytes, baseOff + 12));

        // planes[0..2] at offset 16, 32, 48 (vec4 stride 16).
        AssertPlaneAt(bytes, baseOff + 16, p0);
        AssertPlaneAt(bytes, baseOff + 32, p1);
        AssertPlaneAt(bytes, baseOff + 48, p2);

        Assert.Equal(0u, ReadUInt(bytes, 0));
    }

    [Fact]
    public void GetSlotPlanes_BorrowsTheExactPackedClipRegion()
    {
        using ClipFrame frame = ClipFrame.NoClip();
        Vector4[] expected =
        [
            new(1f, 2f, 3f, 4f),
            new(-5f, 6f, -7f, 8f),
        ];

        int slot = frame.AppendSlot(expected);

        ReadOnlySpan<Vector4> actual = frame.GetSlotPlanes(checked((uint)slot));
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected[0], actual[0]);
        Assert.Equal(expected[1], actual[1]);
        Assert.Equal(0, frame.GetSlotPlanes(0).Length);
    }

    [Fact]
    public void AppendSlot_EmptyPlaneList_PacksNoClipSlot_Count0()
    {
        var frame = ClipFrame.NoClip();
        int slot = frame.AppendSlot(System.ReadOnlySpan<Vector4>.Empty);
        Assert.Equal(1, slot);

        var bytes = frame.RegionBytesForTest;
        Assert.Equal(0u, ReadUInt(bytes, slot * ClipFrame.CellClipStrideBytes));
    }

    [Fact]
    public void AppendSlot_ClampsToEightPlanes()
    {
        var frame = ClipFrame.NoClip();
        var planes = new Vector4[12];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = new Vector4(i, 0f, 0f, 0f);

        int slot = frame.AppendSlot(planes);
        var bytes = frame.RegionBytesForTest;
        Assert.Equal((uint)ClipFrame.MaxPlanes, ReadUInt(bytes, slot * ClipFrame.CellClipStrideBytes));
    }

    [Fact]
    public void AppendSlot_FromClipPlaneSet_AxisAlignedSquare_PacksFourPlanes()
    {
        var cv = new CellView();
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f),
            new Vector2(0.5f, 0.5f),   new Vector2(-0.5f, 0.5f),
        }));
        var cps = ClipPlaneSet.From(cv);
        Assert.Equal(4, cps.Count);

        var frame = ClipFrame.NoClip();
        int slot = frame.AppendSlot(cps);

        var bytes = frame.RegionBytesForTest;
        int baseOff = slot * ClipFrame.CellClipStrideBytes;
        Assert.Equal(4u, ReadUInt(bytes, baseOff + 0));

        for (int i = 0; i < 4; i++)
            AssertPlaneAt(bytes, baseOff + ClipFrame.CellClipPlanesOffset + i * 16, cps.Planes[i]);
    }


    private static void AssertPlaneAt(System.ReadOnlySpan<byte> bytes, int offset, Vector4 expected)
    {
        Assert.Equal(expected.X, ReadFloat(bytes, offset + 0), 6);
        Assert.Equal(expected.Y, ReadFloat(bytes, offset + 4), 6);
        Assert.Equal(expected.Z, ReadFloat(bytes, offset + 8), 6);
        Assert.Equal(expected.W, ReadFloat(bytes, offset + 12), 6);
    }


    [Fact]
    public void ClipViewSlicePlanes_PunchFanPath_EqualsCpuViewPolygonEdgePlanes_ForASyntheticView()
    {
        const float ViewportWidth = 640f, ViewportHeight = 480f;
        Vector2[] verts =
        [
            new(0f, 0.6f), new(-0.6f, -0.4f), new(0.5f, -0.5f), new(0.7f, 0.2f),
        ];

        // Pixel-space points (origin top-left, +Y down) that ReassembleOutsideViewFromWalk's
        // own inverse transform (px = (ndc+1)*W/2, py = (1-ndc)*H/2) maps back to `verts`.
        var pixelPoints = new WalkScreenPoint[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            float px = (verts[i].X + 1f) * ViewportWidth / 2f;
            float py = (1f - verts[i].Y) * ViewportHeight / 2f;
            pixelPoints[i] = new WalkScreenPoint(px, py, 0f, 1f);
        }

        var walkView = new WalkPortalView();
        Assert.True(WalkCopyView.Append(
            walkView, pixelPoints, new SyntheticRayCaster(), Vector3.Zero));

        ClipFrameAssembly assembly = ClipFrameAssembler.BeginWalkFrame(
            ClipFrame.NoClip(), outdoorRoot: false);
        ClipFrameAssembler.ReassembleOutsideViewFromWalk(
            assembly, walkView, ViewportWidth, ViewportHeight);

        ClipViewSlice slice = Assert.Single(assembly.OutsideViewSlices);
        Assert.True(slice.Planes.Length >= 3);

        AssertEveryEdgeMidpointLiesOnSomeGpuPlane(verts, slice.Planes);
    }

    private sealed class SyntheticRayCaster : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY) =>
            Vector3.Normalize(new Vector3(screenX, screenY, 1000f));
    }

    private static void AssertEveryEdgeMidpointLiesOnSomeGpuPlane(
        Vector2[] verts, ReadOnlySpan<Vector4> gpuPlanes)
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
}
