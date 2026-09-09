using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.Core.Lighting;
using Xunit;

namespace AcDream.Core.Tests.Lighting;

public sealed class LightManagerTests
{
    private static LightSource MakePoint(Vector3 pos, float range, uint ownerId = 0, bool lit = true, uint cellId = 0)
        => new LightSource
        {
            Kind = LightKind.Point,
            WorldPosition = pos,
            RankingOrigin = pos,
            Range = range,
            IsLit = lit,
            OwnerId = ownerId,
            CellId = cellId,
        };

    private static LightSource MakeDynamic(Vector3 pos, float range, uint cellId = 0)
        => new LightSource
        {
            Kind = LightKind.Point,
            WorldPosition = pos,
            RankingOrigin = pos,
            Range = range,
            IsLit = true,
            IsDynamic = true,
            CellId = cellId,
        };

    [Fact]
    public void Register_Unregister_TracksList()
    {
        var mgr = new LightManager();
        var a = MakePoint(Vector3.Zero, 5f);
        var b = MakePoint(new Vector3(10, 0, 0), 5f);
        mgr.Register(a);
        mgr.Register(b);
        Assert.Equal(2, mgr.RegisteredCount);

        mgr.Unregister(a);
        Assert.Equal(1, mgr.RegisteredCount);
    }

    [Fact]
    public void Register_DuplicateInstance_Idempotent()
    {
        var mgr = new LightManager();
        var light = MakePoint(Vector3.Zero, 5f);
        mgr.Register(light);
        mgr.Register(light);
        Assert.Equal(1, mgr.RegisteredCount);
    }

    [Fact]
    public void Register_NondirectionalWithoutExplicitRankingOrigin_Throws()
    {
        var mgr = new LightManager();
        var light = new LightSource
        {
            Kind = LightKind.Point,
            WorldPosition = new Vector3(3f, 4f, 5f),
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => mgr.Register(light));
        Assert.Contains("ranking origin", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, mgr.RegisteredCount);
    }

    [Fact]
    public void Tick_SelectsByDistance_Top8()
    {
        var mgr = new LightManager();
        // 12 lights at varying distances, all with range 100 so none filter out.
        for (int i = 0; i < 12; i++)
            mgr.Register(MakePoint(new Vector3(i, 0, 0), 100f));

        mgr.Tick(viewerWorldPos: Vector3.Zero);

        Assert.Equal(8, mgr.ActiveCount);
        foreach (var l in mgr.Active)
        {
            Assert.NotNull(l);
            Assert.True(l!.WorldPosition.X <= 7f);
        }
    }

    [Fact]
    public void Tick_SelectsByDistance_RegardlessOfViewerRange()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(new Vector3(20, 0, 0), range: 5f));  // viewer outside the torch's range

        mgr.Tick(viewerWorldPos: Vector3.Zero);

        Assert.Equal(1, mgr.ActiveCount);
    }

    [Fact]
    public void Tick_IncludesNearbyLight()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(new Vector3(5, 0, 0), range: 5f));

        mgr.Tick(viewerWorldPos: Vector3.Zero);
        Assert.Equal(1, mgr.ActiveCount);
    }

    [Fact]
    public void Tick_SunSlot0_PreservedAcrossTicks()
    {
        var mgr = new LightManager();
        var sun = new LightSource { Kind = LightKind.Directional, WorldForward = -Vector3.UnitZ };
        mgr.Sun = sun;

        mgr.Register(MakePoint(Vector3.Zero, 100f));
        mgr.Tick(Vector3.Zero);

        Assert.Equal(2, mgr.ActiveCount);
        Assert.Same(sun, mgr.Active[0]);
    }

    [Fact]
    public void Tick_UnlitLight_Excluded()
    {
        var mgr = new LightManager();
        var light = MakePoint(Vector3.Zero, 100f, lit: false);
        mgr.Register(light);

        mgr.Tick(Vector3.Zero);
        Assert.Equal(0, mgr.ActiveCount);

        // Toggle lit: should now appear.
        light.IsLit = true;
        mgr.Tick(Vector3.Zero);
        Assert.Equal(1, mgr.ActiveCount);
    }

    [Fact]
    public void UnregisterByOwner_RemovesAttachedLights()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(Vector3.Zero, 5f, ownerId: 42));
        mgr.Register(MakePoint(new Vector3(1, 0, 0), 5f, ownerId: 42));
        mgr.Register(MakePoint(new Vector3(2, 0, 0), 5f, ownerId: 99));

        mgr.UnregisterByOwner(42);
        Assert.Equal(1, mgr.RegisteredCount);
    }

    [Fact]
    public void DistSq_UpdatedEachTick()
    {
        var mgr = new LightManager();
        var light = MakePoint(new Vector3(3, 0, 4), 10f);   // dist 5
        mgr.Register(light);

        mgr.Tick(Vector3.Zero);
        Assert.Equal(25f, light.DistSq, 2);

        mgr.Tick(new Vector3(3, 0, 0));   // same x, same y, z diff 4
        Assert.Equal(16f, light.DistSq, 2);
    }

    // ── Fix B: per-object selection (minimize_object_lighting) ────────────────

    [Fact]
    public void BuildPointLightSnapshot_ExcludesDirectionalAndUnlit()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(new Vector3(1, 0, 0), 5f));               // in
        mgr.Register(MakePoint(new Vector3(2, 0, 0), 5f, lit: false));   // unlit → out
        mgr.Register(new LightSource { Kind = LightKind.Directional });  // sun → out

        mgr.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Single(mgr.PointSnapshot);
        Assert.Equal(1f, mgr.PointSnapshot[0].WorldPosition.X, 3);
    }

    [Fact]
    public void BuildPointLightSnapshot_UnderCap_SortsByRootDistance()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(new Vector3(100, 0, 0), 5f));  // far
        mgr.Register(MakePoint(new Vector3(1, 0, 0), 5f));    // near

        mgr.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(2, mgr.PointSnapshot.Count);
        Assert.Equal(1f, mgr.PointSnapshot[0].WorldPosition.X, 3);
        Assert.Equal(100f, mgr.PointSnapshot[1].WorldPosition.X, 3);
    }

    [Fact]
    public void BuildPointLightSnapshot_RanksRootBeforeAuthoredOffsetFinalPosition()
    {
        var manager = new LightManager();
        LightSource nearRootFarFinal = MakePoint(new Vector3(100f, 0f, 0f), 20f, ownerId: 1);
        nearRootFarFinal.RankingOrigin = new Vector3(1f, 0f, 0f);
        LightSource farRootNearFinal = MakePoint(new Vector3(2f, 0f, 0f), 20f, ownerId: 2);
        farRootNearFinal.RankingOrigin = new Vector3(50f, 0f, 0f);
        manager.Register(farRootNearFinal);
        manager.Register(nearRootFarFinal);

        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(new[] { nearRootFarFinal, farRootNearFinal }, manager.PointSnapshot);
        Assert.Equal(1f, manager.PointSnapshot[0].DistSq);
    }

    [Fact]
    public void BuildPointLightSnapshot_StrictForwardInsertion_PreservesEqualAndNaNOrder()
    {
        var manager = new LightManager();
        LightSource nan = MakePoint(Vector3.Zero, 20f, ownerId: 1);
        nan.RankingOrigin = new Vector3(float.NaN, 0f, 0f);
        LightSource equalA = MakePoint(new Vector3(10f, 0f, 0f), 20f, ownerId: 2);
        equalA.RankingOrigin = new Vector3(2f, 0f, 0f);
        LightSource equalB = MakePoint(new Vector3(20f, 0f, 0f), 20f, ownerId: 3);
        equalB.RankingOrigin = new Vector3(-2f, 0f, 0f);
        manager.Register(nan);
        manager.Register(equalA);
        manager.Register(equalB);

        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(new[] { nan, equalA, equalB }, manager.PointSnapshot);
    }

    [Fact]
    public void BuildPointLightSnapshot_StrictForwardInsertion_AdvancesPastNaN()
    {
        var manager = new LightManager();
        LightSource far = MakePoint(new Vector3(10f, 0f, 0f), 20f, ownerId: 1);
        LightSource nan = MakePoint(Vector3.Zero, 20f, ownerId: 2);
        nan.RankingOrigin = new Vector3(float.NaN, 0f, 0f);
        LightSource near = MakePoint(Vector3.One, 20f, ownerId: 3);
        manager.Register(far);
        manager.Register(nan);
        manager.Register(near);

        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(new[] { near, far, nan }, manager.PointSnapshot);
        Assert.Equal(new[] { 3f, 100f }, manager.PointSnapshot.Take(2).Select(light => light.DistSq));
        Assert.True(float.IsNaN(manager.PointSnapshot[2].DistSq));
    }

    [Fact]
    public void BuildPointLightSnapshot_SpotUsesZeroRank_PointUsesRootDistance()
    {
        var manager = new LightManager();
        LightSource point = MakePoint(new Vector3(1f, 0f, 0f), 20f, ownerId: 1);
        LightSource spot = MakePoint(new Vector3(100f, 0f, 0f), 20f, ownerId: 2);
        spot.Kind = LightKind.Spot;
        manager.Register(point);
        manager.Register(spot);

        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(new[] { spot, point }, manager.PointSnapshot);
        Assert.Equal(0f, spot.DistSq);
    }

    [Fact]
    public void BuildPointLightSnapshot_IndependentSevenAndFortyProducts_DoNotCrossEvict()
    {
        var manager = new LightManager();
        var dynamics = new List<LightSource>();
        var statics = new List<LightSource>();
        for (int i = 0; i < 10; i++)
        {
            LightSource light = MakeDynamic(new Vector3(100f + i, 0f, 0f), 10f);
            light.OwnerId = checked((uint)(100 + i));
            dynamics.Add(light);
            manager.Register(light);
        }
        for (int i = 0; i < 50; i++)
        {
            LightSource light = MakePoint(new Vector3(i, 0f, 0f), 10f, checked((uint)(200 + i)));
            statics.Add(light);
            manager.Register(light);
        }

        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(LightManager.MaxGlobalLights, manager.PointSnapshot.Count);
        Assert.Equal(dynamics.Take(7), manager.PointSnapshot.Take(7));
        Assert.Equal(statics.Take(40), manager.PointSnapshot.Skip(7));
    }

    [Fact]
    public void BuildPointLightSnapshot_ViewerWinsEqualRootTieThoughRegisteredLast()
    {
        var manager = new LightManager();
        LightSource ordinary = MakeDynamic(new Vector3(0f, 0f, 2f), 15f);
        ordinary.RankingOrigin = Vector3.Zero;
        manager.Register(ordinary);
        manager.UpdateViewerLight(Vector3.Zero);

        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(2, manager.PointSnapshot.Count);
        Assert.NotSame(ordinary, manager.PointSnapshot[0]);
        Assert.Equal(new Vector3(0f, 0f, 2f), manager.PointSnapshot[0].WorldPosition);
        Assert.Equal(Vector3.Zero, manager.PointSnapshot[0].RankingOrigin);
        Assert.Same(ordinary, manager.PointSnapshot[1]);
    }

    [Fact]
    public void BuildPointLightSnapshot_ClearRemovesRetainedProducts()
    {
        var manager = new LightManager();
        manager.Register(MakePoint(Vector3.One, 5f));
        manager.BuildPointLightSnapshot(Vector3.Zero);
        Assert.NotEmpty(manager.PointSnapshot);

        manager.Clear();
        Assert.Empty(manager.PointSnapshot);
        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Empty(manager.PointSnapshot);
    }


    [Fact]
    public void PointSnapshot_ResidentCollection_CellTagDoesNotFilter()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(new Vector3(1, 0, 0), 5f, cellId: 0xAAAA0101u));  // "visible" room
        mgr.Register(MakePoint(new Vector3(2, 0, 0), 5f, cellId: 0xAAAA0102u));  // under-room
        mgr.Register(MakePoint(new Vector3(3, 0, 0), 5f, cellId: 0u));

        mgr.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(3, mgr.PointSnapshot.Count);
    }

    [Fact]
    public void PointSnapshot_OverCap_DynamicsNeverEvictedByNearerStatics()
    {
        var mgr = new LightManager();
        // More statics than the cap, ALL nearer the player than every dynamic.
        for (int i = 0; i < LightManager.MaxGlobalLights + 20; i++)
            mgr.Register(MakePoint(new Vector3(i * 0.01f, 0, 0), 5f, ownerId: (uint)(i + 1)));
        var dyns = new LightSource[7];
        for (int i = 0; i < dyns.Length; i++)
        {
            dyns[i] = MakeDynamic(new Vector3(50f + i, 0, 0), range: 9f);
            mgr.Register(dyns[i]);
        }

        mgr.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(LightManager.MaxGlobalLights, mgr.PointSnapshot.Count);
        foreach (var d in dyns)
            Assert.Contains(d, mgr.PointSnapshot);
    }

    [Fact]
    public void PointSnapshot_OverCap_KeepsNearestThePlayer()
    {
        var mgr = new LightManager();
        for (int i = 0; i < LightManager.MaxGlobalLights + 50; i++)
            mgr.Register(MakePoint(new Vector3(200f + i * 0.05f, 0, 0), 5f, ownerId: (uint)(i + 1)));
        var torch = MakePoint(new Vector3(2f, 0, 0), range: 15f, ownerId: 0xF00Du);
        mgr.Register(torch);

        mgr.BuildPointLightSnapshot(playerWorldPos: Vector3.Zero);

        Assert.Contains(torch, mgr.PointSnapshot);
    }

    [Fact]
    public void PointSnapshot_OverCap_EqualDistancesPreserveLegacyOrderExactly()
    {
        var manager = new LightManager();
        var registered = new List<LightSource>();
        for (int index = 0;
             index < LightManager.MaxGlobalLights + 9;
             index++)
        {
            LightSource light = MakePoint(
                new Vector3(3f, 4f, 0f),
                range: 10f,
                ownerId: checked((uint)index + 1));
            registered.Add(light);
            manager.Register(light);
        }

        LightSource[] expected = FullSortOracle(
            registered,
            Vector3.Zero);
        manager.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(expected, manager.PointSnapshot);

        manager.BuildPointLightSnapshot(Vector3.Zero);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 25; iteration++)
            manager.BuildPointLightSnapshot(Vector3.Zero);
        Assert.Equal(
            0,
            GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PointSnapshot_BoundedSelectorMatchesCompleteSortRandomized()
    {
        var random = new Random(0x54D1B0);
        for (int scenario = 0; scenario < 64; scenario++)
        {
            var manager = new LightManager();
            var registered = new List<LightSource>();
            int count = 180 + random.Next(420);
            for (int index = 0; index < count; index++)
            {
                var position = new Vector3(
                    random.Next(-12, 13),
                    random.Next(-12, 13),
                    random.Next(-3, 4));
                LightSource light = MakePoint(
                    position,
                    range: 20f,
                    ownerId: checked((uint)index + 1),
                    lit: random.Next(13) != 0,
                    cellId: random.Next(5) == 0
                        ? 0u
                        : checked((uint)(0xAAAA0100 + random.Next(1, 4))));
                light.IsDynamic = random.Next(7) == 0;
                if (random.Next(17) == 0)
                    light.Kind = LightKind.Directional;
                registered.Add(light);
                manager.Register(light);
            }

            Vector3 player = new(
                random.Next(-4, 5),
                random.Next(-4, 5),
                random.Next(-2, 3));
            LightSource[] expected = FullSortOracle(
                registered,
                player);

            manager.BuildPointLightSnapshot(player);

            Assert.Equal(expected, manager.PointSnapshot);
        }
    }

    [Fact]
    public void PointSnapshot_TownNetworkScale463_MatchesCompleteSort()
    {
        var manager = new LightManager();
        var registered = new List<LightSource>(463);
        const uint fountainRoom = 0x00070144u;
        const uint corridor = 0x00070145u;
        for (int index = 0; index < 463; index++)
        {
            LightSource light = MakePoint(
                new Vector3(
                    index + 0.125f,
                    index * 0.001f,
                    index * 0.0001f),
                range: 15f,
                ownerId: checked((uint)index + 1),
                cellId: index % 3 == 0
                    ? fountainRoom
                    : corridor);
            light.IsDynamic = index % 61 == 0;
            registered.Add(light);
            manager.Register(light);
        }
        Vector3 player = new(4.25f, -1.5f, 0.7f);
        LightSource[] expected = FullSortOracle(
            registered,
            player);

        manager.BuildPointLightSnapshot(player);

        Assert.Equal(LightManager.MaxGlobalLights, manager.PointSnapshot.Count);
        Assert.Equal(expected, manager.PointSnapshot);
    }

    [Fact]
    public void PointSnapshot_WarmedOverflowPathAllocatesZero()
    {
        var manager = new LightManager();
        for (int index = 0; index < 463; index++)
        {
            LightSource light = MakePoint(
                new Vector3(
                    index + 0.125f,
                    index * 0.001f,
                    index * 0.0001f),
                range: 15f,
                ownerId: checked((uint)index + 1));
            light.IsDynamic = index % 53 == 0;
            manager.Register(light);
        }
        _ = MeasurePointSnapshotAllocations(manager, 256);
        long allocated = MeasurePointSnapshotAllocations(manager, 100);

        Assert.Equal(0, allocated);
    }

    // Keep setup and assertion work outside the measured method's optimization boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePointSnapshotAllocations(LightManager manager, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < iterations; iteration++)
            manager.BuildPointLightSnapshot(Vector3.Zero);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void BuildPointLightSnapshot_UsesAllResidentLights()
    {
        var mgr = new LightManager();
        mgr.Register(MakePoint(new Vector3(1, 0, 0), 5f, cellId: 0xAAAAu));
        mgr.Register(MakePoint(new Vector3(2, 0, 0), 5f, cellId: 0xBBBBu));

        mgr.BuildPointLightSnapshot(Vector3.Zero);

        Assert.Equal(2, mgr.PointSnapshot.Count);
    }

    [Fact]
    public void SelectForObject_EmptySnapshot_ReturnsZero()
    {
        Span<int> idx = stackalloc int[8];
        int n = LightManager.SelectForObject(System.Array.Empty<LightSource>(), Vector3.Zero, 1f, idx);
        Assert.Equal(0, n);
    }

    [Fact]
    public void SelectForObject_InRange_Selected()
    {
        var snapshot = new[] { MakePoint(new Vector3(3, 0, 0), range: 5f) };  // dist 3 < range 5
        Span<int> idx = stackalloc int[8];
        int n = LightManager.SelectForObject(snapshot, Vector3.Zero, radius: 0f, idx);
        Assert.Equal(1, n);
        Assert.Equal(0, idx[0]);
    }

    [Fact]
    public void SelectForObject_OutOfRange_Excluded()
    {
        // dist 10, range 5, radius 0 → 10 >= 5 → excluded.
        var snapshot = new[] { MakePoint(new Vector3(10, 0, 0), range: 5f) };
        Span<int> idx = stackalloc int[8];
        int n = LightManager.SelectForObject(snapshot, Vector3.Zero, radius: 0f, idx);
        Assert.Equal(0, n);
    }

    [Fact]
    public void SelectForObject_ObjectRadiusExtendsReach()
    {
        var snapshot = new[] { MakePoint(new Vector3(7, 0, 0), range: 5f) };
        Span<int> idx = stackalloc int[8];

        Assert.Equal(0, LightManager.SelectForObject(snapshot, Vector3.Zero, radius: 0f, idx));
        Assert.Equal(1, LightManager.SelectForObject(snapshot, Vector3.Zero, radius: 3f, idx));
    }

    [Fact]
    public void SelectForObject_MoreThan8_KeepsNearest8()
    {
        var snapshot = new LightSource[10];
        for (int i = 0; i < 10; i++)
            snapshot[i] = MakePoint(new Vector3(i + 1, 0, 0), range: 100f);  // dist i+1, all in range

        Span<int> idx = stackalloc int[8];
        int n = LightManager.SelectForObject(snapshot, Vector3.Zero, radius: 0f, idx);

        Assert.Equal(8, n);
        // Nearest-first: index 0 (dist 1) … index 7 (dist 8). The two farthest
        // (indices 8,9 / dist 9,10) are evicted.
        for (int k = 0; k < 8; k++)
            Assert.Equal(k, idx[k]);
    }

    [Fact]
    public void SelectForObject_CameraIndependent_DependsOnlyOnObjectCentre()
    {
        var snapshot = new[]
        {
            MakePoint(new Vector3(2, 0, 0), range: 10f),
            MakePoint(new Vector3(20, 0, 0), range: 10f),
        };
        Span<int> a = stackalloc int[8];
        Span<int> b = stackalloc int[8];
        int na = LightManager.SelectForObject(snapshot, Vector3.Zero, 1f, a);
        int nb = LightManager.SelectForObject(snapshot, Vector3.Zero, 1f, b);

        Assert.Equal(1, na);
        Assert.Equal(na, nb);
        Assert.Equal(a[0], b[0]);
    }


    [Fact]
    public void SelectForCell_AppliesAllDynamicLights_EvenOutOfReach()
    {
        var snapshot = new[]
        {
            MakePoint(new Vector3(1, 0, 0), range: 5f),      // 0: static, reaches
            MakeDynamic(new Vector3(100, 0, 0), range: 5f),  // 1: dynamic, FAR (out of reach)
            MakeDynamic(new Vector3(2, 0, 0), range: 5f),    // 2: dynamic, near
            MakePoint(new Vector3(50, 0, 0), range: 5f),     // 3: static, far (out of reach)
        };
        Span<int> sel = stackalloc int[LightManager.MaxLightsPerEnvCell];
        int n = LightManager.SelectForCell(snapshot, sel);

        bool d1 = false, d2 = false, s0 = false, s3 = false;
        for (int i = 0; i < n; i++)
        {
            if (sel[i] == 1) d1 = true;
            if (sel[i] == 2) d2 = true;
            if (sel[i] == 0) s0 = true;
            if (sel[i] == 3) s3 = true;
        }
        Assert.True(d1, "the FAR dynamic light must still be applied — retail enables all dynamics");
        Assert.True(d2, "the near dynamic light is applied");
        Assert.True(s0, "the near static light reaches the cell → selected");
        Assert.True(s3, "the complete retained static product is supplied; the shader applies range");
    }

    [Fact]
    public void SelectForCell_SameDynamicSet_ForCellsFarApart_NoFlap()
    {
        var snapshot = new[]
        {
            MakeDynamic(new Vector3(0, 0, 0), range: 5f),
            MakeDynamic(new Vector3(100, 0, 0), range: 5f),
        };
        Span<int> a = stackalloc int[LightManager.MaxLightsPerEnvCell];
        Span<int> b = stackalloc int[LightManager.MaxLightsPerEnvCell];
        int na = LightManager.SelectForCell(snapshot, a);
        int nb = LightManager.SelectForCell(snapshot, b);

        Assert.Equal(2, na);
        Assert.Equal(2, nb);
    }

    [Fact]
    public void SelectForCell_CarriesAllSevenDynamicsAndFortyStatics_WhileObjectStaysEight()
    {
        var snapshot = new List<LightSource>();
        for (int i = 0; i < LightManager.MaxDynamicPointLights; i++)
            snapshot.Add(MakeDynamic(new Vector3(i, 0f, 0f), 100f));
        for (int i = 0; i < LightManager.MaxStaticPointLights; i++)
            snapshot.Add(MakePoint(new Vector3(i + 10f, 0f, 0f), 100f));

        Span<int> cell = stackalloc int[LightManager.MaxLightsPerEnvCell];
        int cellCount = LightManager.SelectForCell(snapshot, cell);
        Span<int> obj = stackalloc int[LightManager.MaxLightsPerEnvCell];
        int objectCount = LightManager.SelectForObject(snapshot, Vector3.Zero, 100f, obj);

        Assert.Equal(47, cellCount);
        for (int index = 0; index < cellCount; index++)
            Assert.Equal(index, cell[index]);
        Assert.Equal(8, objectCount);
    }

    [Fact]
    public void PointSnapshot_HubScaleLightCount_ObjectSelectionIsCameraInvariant()
    {
        var mgr = new LightManager();

        const uint farRoom = 0xAAAA0102u;
        for (int i = 0; i < 400; i++)
            mgr.Register(MakePoint(new Vector3(200f + i * 0.05f, 0, 0), range: 5f, ownerId: (uint)(i + 1), cellId: farRoom));

        // The target torch: beside the player, in the player's room.
        const uint playerRoom = 0xAAAA0101u;
        var torch = MakePoint(new Vector3(2f, 0, 0), range: 15f, ownerId: 0xF00DF00Du, cellId: playerRoom);
        mgr.Register(torch);

        Span<int> sel = stackalloc int[LightManager.MaxLightsPerObject];

        mgr.BuildPointLightSnapshot(playerWorldPos: Vector3.Zero);
        int n1 = LightManager.SelectForObject(mgr.PointSnapshot, new Vector3(0f, 0, 0), 6f, sel);
        bool torchSelected1 = SelectedContains(mgr.PointSnapshot, sel, n1, torch);

        mgr.BuildPointLightSnapshot(playerWorldPos: Vector3.Zero);
        int n2 = LightManager.SelectForObject(mgr.PointSnapshot, new Vector3(0f, 0, 0), 6f, sel);
        bool torchSelected2 = SelectedContains(mgr.PointSnapshot, sel, n2, torch);

        Assert.True(torchSelected1,
            "an in-range light beside the player was evicted from the pool — " +
            "per-cell lighting would pop");
        Assert.True(torchSelected2, "consecutive same-player builds must select identically");
        Assert.Equal(LightManager.MaxStaticPointLights, mgr.PointSnapshot.Count);

        static bool SelectedContains(
            System.Collections.Generic.IReadOnlyList<LightSource> snapshot,
            Span<int> indices, int count, LightSource target)
        {
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(snapshot[indices[i]], target)) return true;
            return false;
        }
    }

    private static LightSource[] FullSortOracle(
        IReadOnlyList<LightSource> registered,
        Vector3 player)
    {
        var ranked = new List<OracleRank>();
        for (int index = 0; index < registered.Count; index++)
        {
            LightSource light = registered[index];
            if (!light.IsLit || light.Kind == LightKind.Directional)
                continue;
            ranked.Add(new OracleRank(
                light,
                light.Kind == LightKind.Point
                    ? Vector3.DistanceSquared(light.RankingOrigin, player)
                    : 0f));
        }

        var dynamics = ranked.Where(static item => item.Light.IsDynamic).ToList();
        var statics = ranked.Where(static item => !item.Light.IsDynamic).ToList();
        StableRetailInsertion(dynamics, LightManager.MaxDynamicPointLights);
        StableRetailInsertion(statics, LightManager.MaxStaticPointLights);
        return dynamics.Concat(statics).Select(static item => item.Light).ToArray();

        static void StableRetailInsertion(List<OracleRank> values, int cap)
        {
            var selected = new List<OracleRank>(cap);
            foreach (OracleRank value in values)
            {
                int index = 0;
                while (index < selected.Count && !(value.DistanceSq < selected[index].DistanceSq))
                    index++;
                if (index >= cap)
                    continue;
                selected.Insert(index, value);
                if (selected.Count > cap)
                    selected.RemoveAt(cap);
            }
            values.Clear();
            values.AddRange(selected);
        }
    }

    private readonly record struct OracleRank(
        LightSource Light,
        float DistanceSq);
}
