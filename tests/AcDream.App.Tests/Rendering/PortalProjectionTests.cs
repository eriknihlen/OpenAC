using System;
using System.Buffers;
using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class PortalProjectionTests
{
    private static Matrix4x4 ViewProj()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.0f, 0.1f, 1000f);
        return view * proj;
    }

    [Fact]
    public void Project_QuadInFront_ProducesNdcInsideViewport()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, -5), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, -5),
        };
        var r = PortalProjection.ProjectToNdc(poly, Matrix4x4.Identity, ViewProj());
        Assert.True(r.Length >= 3);
        foreach (var v in r)
        {
            Assert.InRange(v.X, -1.001f, 1.001f);
            Assert.InRange(v.Y, -1.001f, 1.001f);
        }
    }

    [Fact]
    public void Project_QuadFullyBehind_ReturnsEmpty()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, 5), new Vector3(1, -1, 5), new Vector3(1, 1, 5), new Vector3(-1, 1, 5),
        };
        var r = PortalProjection.ProjectToNdc(poly, Matrix4x4.Identity, ViewProj());
        Assert.True(r.Length < 3);
    }

    [Fact]
    public void Project_QuadStraddlingCamera_ClipsWithoutInversion()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, 2), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, 2),
        };
        var r = PortalProjection.ProjectToNdc(poly, Matrix4x4.Identity, ViewProj());
        Assert.True(r.Length >= 3);
        foreach (var v in r)
        {
            Assert.InRange(v.X, -50f, 50f); // bounded — no inversion blow-up
            Assert.InRange(v.Y, -50f, 50f);
        }
    }

    [Fact]
    public void Project_QuadStraddlingCamera_DownstreamIntersectionIsValidOnScreen()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, 2), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, 2),
        };
        var projected = PortalProjection.ProjectToNdc(poly, Matrix4x4.Identity, ViewProj());
        Assert.True(projected.Length >= 3);

        var viewport = CellView.FullScreen().Polygons[0].Vertices;
        var onScreen = ScreenPolygonClip.Intersect(projected, viewport);

        Assert.True(onScreen.Length >= 3); // a non-empty visible region survives
        foreach (var v in onScreen)
        {
            Assert.InRange(v.X, -1.001f, 1.001f);
            Assert.InRange(v.Y, -1.001f, 1.001f);
        }
    }

    [Fact]
    public void Project_QuadStraddlingCamera_NdcStaysWithinScreen()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, 2), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, 2),
        };
        var r = PortalProjection.ProjectToNdc(poly, Matrix4x4.Identity, ViewProj());
        Assert.True(r.Length >= 3);
        foreach (var v in r)
        {
            Assert.InRange(v.X, -1.001f, 1.001f); // bounded to the screen — no off-screen explosion
            Assert.InRange(v.Y, -1.001f, 1.001f);
        }
    }

    [Fact]
    public void Project_CloseDoorway_NdcStaysWithinScreen_AndCoversScreen()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 1.0f, 5000f);
        var viewProj = view * proj;

        // A 2 m x 2 m doorway 0.28 m in front of the eye, facing it.
        var doorway = new[]
        {
            new Vector3(-1f, -1f, -0.28f), new Vector3(1f, -1f, -0.28f),
            new Vector3(1f,  1f, -0.28f),  new Vector3(-1f, 1f, -0.28f),
        };
        var projected = PortalProjection.ProjectToNdc(doorway, Matrix4x4.Identity, viewProj);
        Assert.True(projected.Length >= 3);
        foreach (var v in projected)
        {
            Assert.InRange(v.X, -1.001f, 1.001f);
            Assert.InRange(v.Y, -1.001f, 1.001f);
        }

        var viewport = CellView.FullScreen().Polygons[0].Vertices;
        var onScreen = ScreenPolygonClip.Intersect(projected, viewport);
        Assert.True(onScreen.Length >= 3, "a doorway the eye looks through must cover the screen, not collapse to the void");
    }

    [Fact]
    public void Project_PortalEyeIsAlmostTouching_StaysVisibleOnScreen()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 1.0f, 5000f);
        var viewProj = view * proj;

        // A 2 m x 2 m doorway 0.1 m in front of the eye, facing it.
        var doorway = new[]
        {
            new Vector3(-1f, -1f, -0.1f), new Vector3(1f, -1f, -0.1f),
            new Vector3(1f,  1f, -0.1f),  new Vector3(-1f, 1f, -0.1f),
        };

        var projected = PortalProjection.ProjectToNdc(doorway, Matrix4x4.Identity, viewProj);
        Assert.True(projected.Length >= 3,
            "a doorway 0.1 m from the eye must still project (was clipped to empty -> void)");

        var viewport = CellView.FullScreen().Polygons[0].Vertices;
        var onScreen = ScreenPolygonClip.Intersect(projected, viewport);
        Assert.True(onScreen.Length >= 3,
            "the cell behind a doorway you're standing in must stay visible (the void bug)");
    }


    private static Vector2[] FullScreenCcw() => new[]
    {
        new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f),
    };

    [Fact]
    public void ProjectToClip_QuadInFront_KeepsVertsWithPositiveW()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, -5), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, -5),
        };
        var clip = PortalProjection.ProjectToClip(poly, Matrix4x4.Identity, ViewProj());
        Assert.True(clip.Length >= 3);
        foreach (var v in clip)
            Assert.True(v.W > 0f, $"an in-front portal vertex must keep w>0 (homogeneous), got w={v.W}");
    }

    [Fact]
    public void ProjectToClip_QuadFullyBehind_ReturnsEmpty()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, 5), new Vector3(1, -1, 5), new Vector3(1, 1, 5), new Vector3(-1, 1, 5),
        };
        Assert.True(PortalProjection.ProjectToClip(poly, Matrix4x4.Identity, ViewProj()).Length < 3);
    }

    [Fact]
    public void ClipToRegion_OnScreenQuad_ReturnsBoundedNdc()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, -5), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, -5),
        };
        var clip = PortalProjection.ProjectToClip(poly, Matrix4x4.Identity, ViewProj());
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length >= 3);
        foreach (var v in ndc) { Assert.InRange(v.X, -1.001f, 1.001f); Assert.InRange(v.Y, -1.001f, 1.001f); }
    }

    [Fact]
    public void ClipToRegion_FullyOffScreen_ReturnsEmpty_NotSliver()
    {
        var poly = new[]
        {
            new Vector3(3, -1, -5), new Vector3(5, -1, -5), new Vector3(5, 1, -5), new Vector3(3, 1, -5),
        };
        var clip = PortalProjection.ProjectToClip(poly, Matrix4x4.Identity, ViewProj());
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length < 3, $"fully off-screen portal must clip to empty, got {ndc.Length} verts (sliver)");
    }

    [Fact]
    public void ClipToRegion_PartlyOffScreen_ClipsToBoundedNonEmpty()
    {
        var poly = new[]
        {
            new Vector3(0, -1, -5), new Vector3(4, -1, -5), new Vector3(4, 1, -5), new Vector3(0, 1, -5),
        };
        var clip = PortalProjection.ProjectToClip(poly, Matrix4x4.Identity, ViewProj());
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length >= 3, "a partly-on-screen portal must produce a non-empty clipped region");
        foreach (var v in ndc) { Assert.InRange(v.X, -1.001f, 1.001f); Assert.InRange(v.Y, -1.001f, 1.001f); }
    }

    [Fact]
    public void ClipToRegion_DoorwayEyeLooksThrough_CoversScreen_WithoutFallback()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 1.0f, 5000f);
        var viewProj = view * proj;
        var doorway = new[]
        {
            new Vector3(-1f, -1f, -0.28f), new Vector3(1f, -1f, -0.28f),
            new Vector3(1f,  1f, -0.28f),  new Vector3(-1f, 1f, -0.28f),
        };
        var clip = PortalProjection.ProjectToClip(doorway, Matrix4x4.Identity, viewProj);
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length >= 3, "a doorway the eye looks through must cover the screen, not collapse to the void");
        foreach (var v in ndc) { Assert.InRange(v.X, -1.001f, 1.001f); Assert.InRange(v.Y, -1.001f, 1.001f); }
    }

    [Fact]
    public void ClipToRegion_StraddlingEye_OnScreenBounded_NoBlowup()
    {
        var poly = new[]
        {
            new Vector3(-1, -1, 2), new Vector3(1, -1, -5), new Vector3(1, 1, -5), new Vector3(-1, 1, 2),
        };
        var clip = PortalProjection.ProjectToClip(poly, Matrix4x4.Identity, ViewProj());
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length >= 3);
        foreach (var v in ndc) { Assert.InRange(v.X, -1.001f, 1.001f); Assert.InRange(v.Y, -1.001f, 1.001f); }
    }

    private static float AbsArea(Vector2[] p)
    {
        if (p == null || p.Length < 3) return 0f;
        float a2 = 0f;
        for (int i = 0; i < p.Length; i++) { var u = p[i]; var w = p[(i + 1) % p.Length]; a2 += u.X * w.Y - w.X * u.Y; }
        return MathF.Abs(a2) * 0.5f;
    }

    [Fact]
    public void ClipToRegion_SubjectFullyInsideRegion_ReturnsSubjectNotRegion()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1.0f, 0.1f, 1000f);
        var vp = view * proj;
        var narrow = new[] { new Vector3(-0.3f, -0.9f, -3f), new Vector3(0.3f, -0.9f, -3f), new Vector3(0.3f, 0.9f, -3f), new Vector3(-0.3f, 0.9f, -3f) };
        var wide = new[] { new Vector3(-0.9f, -0.9f, -3f), new Vector3(0.9f, -0.9f, -3f), new Vector3(0.9f, 0.9f, -3f), new Vector3(-0.9f, 0.9f, -3f) };

        var narrowClip = PortalProjection.ProjectToClip(narrow, Matrix4x4.Identity, vp);
        var wideRegion = PortalProjection.ClipToRegion(PortalProjection.ProjectToClip(wide, Matrix4x4.Identity, vp), FullScreenCcw());

        var clipped = PortalProjection.ClipToRegion(narrowClip, wideRegion);
        float narrowArea = AbsArea(PortalProjection.ClipToRegion(narrowClip, FullScreenCcw()));
        float wideArea = AbsArea(wideRegion);
        float clippedArea = AbsArea(clipped);
        Assert.True(clippedArea <= narrowArea + 1e-3f,
            $"subject∩region must be the narrow subject (area {narrowArea}), not the wide region (area {wideArea}); got {clippedArea}");
    }

    [Fact]
    public void ClipToRegion_AgainstSubRegion_TightensToIntersection()
    {
        var wide = new[]
        {
            new Vector3(-2, -2, -5), new Vector3(2, -2, -5), new Vector3(2, 2, -5), new Vector3(-2, 2, -5),
        };
        var clip = PortalProjection.ProjectToClip(wide, Matrix4x4.Identity, ViewProj());
        var narrow = new[]
        {
            new Vector2(-0.3f, -0.3f), new Vector2(0.3f, -0.3f), new Vector2(0.3f, 0.3f), new Vector2(-0.3f, 0.3f),
        };
        var ndc = PortalProjection.ClipToRegion(clip, narrow);
        Assert.True(ndc.Length >= 3);
        foreach (var v in ndc) { Assert.InRange(v.X, -0.301f, 0.301f); Assert.InRange(v.Y, -0.301f, 0.301f); }
    }


    [Fact]
    public void ProjectToClip_EyeCrossingPortal_BoundaryVertsLandAtWZero()
    {
        var opening = new[]
        {
            new Vector3(-1f, -0.005f, -1.5f), new Vector3(1f, -0.005f, -1.5f),
            new Vector3(1f, -0.005f, 0.5f),   new Vector3(-1f, -0.005f, 0.5f),
        };
        var clip = PortalProjection.ProjectToClip(opening, Matrix4x4.Identity, ViewProj());
        Assert.True(clip.Length >= 3, "an eye-crossing portal must keep its forward half");
        int atZero = 0;
        foreach (var v in clip)
        {
            Assert.True(v.W >= 0f, $"no survivor may sit behind the eye plane, got w={v.W}");
            if (v.W == 0f) atZero++;
        }
        Assert.True(atZero >= 2, $"the two eye-plane crossings must land at exactly w==0, got {atZero}");
    }

    [Fact]
    public void ClipToRegion_EyeCrossingFloorOpening_YieldsHalfRegionNotSliver()
    {
        var opening = new[]
        {
            new Vector3(-1f, -0.005f, -1.5f), new Vector3(1f, -0.005f, -1.5f),
            new Vector3(1f, -0.005f, 0.5f),   new Vector3(-1f, -0.005f, 0.5f),
        };
        var clip = PortalProjection.ProjectToClip(opening, Matrix4x4.Identity, ViewProj());
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length >= 3, "the crossing frame must produce a region, not empty (the climb strobe)");
        foreach (var v in ndc)
        {
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y), $"region verts must be finite, got ({v.X},{v.Y})");
            Assert.InRange(v.X, -1.001f, 1.001f);
            Assert.InRange(v.Y, -1.001f, 1.001f);
        }
        float area = AbsArea(ndc);
        Assert.True(area > 1.5f,
            $"the region must approximate the lower half-screen (area ~2.0 of 4.0), got {area} (sliver = the strobe bug)");
    }

    [Fact]
    public void EyeInPortalPlane_GazeAlongPlane_DegenerateViewPropagates()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 1.0f, 5000f);
        var vp = view * proj;
        var opening = new[]
        {
            new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f),
            new Vector3(1f, 0f, -4f),  new Vector3(-1f, 0f, -4f),
        };
        var clip = PortalProjection.ProjectToClip(opening, Matrix4x4.Identity, vp);
        Assert.True(clip.Length >= 3, "the in-plane opening's forward part must survive the W clip");
        var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
        Assert.True(ndc.Length >= 3, "the edge-on opening must yield its (zero-area) collinear region");

        var cellView = new CellView();
        Assert.True(cellView.Add(new ViewPolygon(ndc)),
            "a zero-area collinear view must be ACCEPTED (retail propagates degenerate views; " +
            "rejecting it drops the cell chain at the knife edge)");
        Assert.False(cellView.Add(new ViewPolygon(ndc)),
            "a re-emitted degenerate view must dedup via its segment key");
    }

    [Fact]
    public void ClipToRegion_NeverReturnsNonFiniteVerts()
    {
        var degenerate = new[]
        {
            new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, -2f),
        };
        var clip = PortalProjection.ProjectToClip(degenerate, Matrix4x4.Identity, ViewProj());
        if (clip.Length >= 3)
        {
            var ndc = PortalProjection.ClipToRegion(clip, FullScreenCcw());
            foreach (var v in ndc)
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y),
                    $"non-finite NDC vert leaked from the divide: ({v.X},{v.Y})");
        }
    }

    [Fact]
    public void ProjectToClipLease_ReusesPooledWorkWithoutResultArrays()
    {
        var opening = new[]
        {
            new Vector3(-1f, -1f, -3f),
            new Vector3(1f, -1f, -3f),
            new Vector3(1f, 1f, -3f),
            new Vector3(-1f, 1f, -3f),
        };
        Matrix4x4 viewProjection = ViewProj();

        using (PortalProjection.ClipPolygonLease warm =
               PortalProjection.ProjectToClipLease(
                   opening,
                   Matrix4x4.Identity,
                   viewProjection))
        {
            Assert.Equal(4, warm.Count);
        }

        for (int i = 0; i < 2_000; i++)
        {
            using PortalProjection.ClipPolygonLease lease =
                PortalProjection.ProjectToClipLease(
                    opening,
                    Matrix4x4.Identity,
                    viewProjection);
            _ = lease.Count;
        }

        int totalVertices = 0;
        long minimumAllocated = long.MaxValue;
        for (int sample = 0; sample < 5; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
            {
                using PortalProjection.ClipPolygonLease lease =
                    PortalProjection.ProjectToClipLease(
                        opening,
                        Matrix4x4.Identity,
                        viewProjection);
                totalVertices += lease.Count;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }

        Assert.Equal(20_000, totalVertices);
        Assert.True(
            minimumAllocated <= 1_024,
            $"best warmed pooled-projection batch allocated {minimumAllocated:N0} bytes");
    }

    [Fact]
    public void ClipToRegion_FrameOwnedStore_ReusesExactResultArray()
    {
        var opening = new[]
        {
            new Vector3(-1f, -1f, -5f),
            new Vector3(1f, -1f, -5f),
            new Vector3(1f, 1f, -5f),
            new Vector3(-1f, 1f, -5f),
        };
        Vector4[] subject = PortalProjection.ProjectToClip(
            opening, Matrix4x4.Identity, ViewProj());
        Vector2[] region = FullScreenCcw();
        Vector2[] expected = PortalProjection.ClipToRegion(subject, region);
        var store = new PortalPolygonVertexStore();

        Vector2[] first = PortalProjection.ClipToRegion(
            subject.AsSpan(), region, store);
        Vector2[] firstSnapshot = (Vector2[])first.Clone();
        int allocationHighWater = store.AllocationCount;

        store.ResetUsage();
        Vector2[] second = PortalProjection.ClipToRegion(
            subject.AsSpan(), region, store);

        Assert.Same(first, second);
        Assert.Equal(allocationHighWater, store.AllocationCount);
        Assert.Equal(expected, firstSnapshot);
        Assert.Equal(expected, second);

        for (int i = 0; i < 2_000; i++)
        {
            store.ResetUsage();
            _ = PortalProjection.ClipToRegion(subject.AsSpan(), region, store);
        }

        float checksum = 0f;
        long minimumAllocated = long.MaxValue;
        for (int sample = 0; sample < 5; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
            {
                store.ResetUsage();
                Vector2[] reused = PortalProjection.ClipToRegion(
                    subject.AsSpan(), region, store);
                checksum += reused[0].X;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }

        Assert.True(float.IsFinite(checksum));
        Assert.True(minimumAllocated <= 4_096,
            $"best warmed frame-owned clip batch allocated {minimumAllocated:N0} bytes");
    }

    [Fact]
    public void ProjectToNdc_SecondRentFailure_ReturnsFirstBuffer()
    {
        var pool = new ThrowOnSecondRentPool<Vector4>();
        Vector3[] opening =
        [
            new(-1f, -1f, -5f),
            new(1f, -1f, -5f),
            new(1f, 1f, -5f),
            new(-1f, 1f, -5f),
        ];

        Assert.Throws<InjectedRentException>(() =>
            PortalProjection.ProjectToNdc(
                opening,
                Matrix4x4.Identity,
                ViewProj(),
                pool));

        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void ProjectToClipLease_SecondRentFailure_ReturnsFirstBuffer()
    {
        var pool = new ThrowOnSecondRentPool<Vector4>();
        Vector3[] opening =
        [
            new(-1f, -1f, -5f),
            new(1f, -1f, -5f),
            new(1f, 1f, -5f),
            new(-1f, 1f, -5f),
        ];

        try
        {
            using PortalProjection.ClipPolygonLease _ =
                PortalProjection.ProjectToClipLease(
                    opening,
                    Matrix4x4.Identity,
                    ViewProj(),
                    pool);
            Assert.Fail("The injected second-rent failure was not observed.");
        }
        catch (InjectedRentException)
        {
            // Expected: ownership of the first rent must already be reconciled.
        }

        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void ClipToRegion_SecondRentFailure_ReturnsFirstBuffer()
    {
        var pool = new ThrowOnSecondRentPool<Vector4>();
        Vector4[] subject =
        [
            new(-1f, -1f, 0f, 1f),
            new(1f, -1f, 0f, 1f),
            new(1f, 1f, 0f, 1f),
            new(-1f, 1f, 0f, 1f),
        ];

        Assert.Throws<InjectedRentException>(() =>
            PortalProjection.ClipToRegion(
                subject.AsSpan(),
                FullScreenCcw(),
                new PortalPolygonVertexStore(),
                pool));

        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void ClipPolygonLease_AccessAfterDispose_FailsFast()
    {
        var pool = new ThrowOnSecondRentPool<Vector4>(throwOnRent: int.MaxValue);
        Vector3[] opening =
        [
            new(-1f, -1f, -5f),
            new(1f, -1f, -5f),
            new(1f, 1f, -5f),
            new(-1f, 1f, -5f),
        ];
        PortalProjection.ClipPolygonLease lease =
            PortalProjection.ProjectToClipLease(
                opening,
                Matrix4x4.Identity,
                ViewProj(),
                pool);

        lease.Dispose();
        Assert.Equal(0, pool.OutstandingCount);

        try
        {
            _ = lease.Count;
            Assert.Fail("A disposed pooled lease exposed its returned buffer.");
        }
        catch (ObjectDisposedException)
        {
            // Expected.
        }

        try
        {
            _ = lease.Span.Length;
            Assert.Fail("A disposed pooled lease exposed its returned span.");
        }
        catch (ObjectDisposedException)
        {
            // Expected.
        }

        // Same-instance disposal is idempotent and must not return buffers twice.
        lease.Dispose();
        Assert.Equal(0, pool.OutstandingCount);
    }

    private sealed class InjectedRentException : Exception { }

    private sealed class ThrowOnSecondRentPool<T>(int throwOnRent = 2) : ArrayPool<T>
    {
        private int _rentCount;

        internal int OutstandingCount { get; private set; }

        public override T[] Rent(int minimumLength)
        {
            _rentCount++;
            if (_rentCount == throwOnRent)
                throw new InjectedRentException();
            OutstandingCount++;
            return new T[Math.Max(1, minimumLength)];
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            Assert.NotNull(array);
            Assert.True(OutstandingCount > 0, "A pooled array was returned twice.");
            OutstandingCount--;
        }
    }
}
