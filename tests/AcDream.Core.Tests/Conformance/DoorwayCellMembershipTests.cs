using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public sealed class DoorwayCellMembershipTests
{
    private readonly ITestOutputHelper _out;
    public DoorwayCellMembershipTests(ITestOutputHelper output) => _out = output;

    private const float FootRadius = 0.48f;

    private static PhysicsDataCache LoadLandblockInteriors(DatCollection dats, uint lbPrefix)
    {
        var cache = new PhysicsDataCache();
        for (uint low = 0x0100; low <= 0x01FF; low++)
        {
            try { ConformanceDats.LoadEnvCell(dats, cache, lbPrefix | low); }
            catch { }
        }
        cache.CellGraph.RegisterTerrain(lbPrefix, new TerrainSurface(new byte[81], new float[256]), Vector3.Zero);
        return cache;
    }

    [Fact]
    public void A9B3CottageGap_AtDoorway_StraddlesExitPlane_DemotesRetailFaithfully()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);

        var gap = new Vector3(184.915f, 82.464f, 116.48f);

        uint picked = CellTransit.FindCellList(cache, gap, FootRadius, 0xA9B30104u);
        _out.WriteLine($"pick(seed 0x104) at gap -> 0x{picked:X8}");

        Assert.Equal(0xA9B3003Cu, picked);
    }

    [Fact]
    public void A9B3Cottage_GapBeyondStraddleDistance_KeepsCurrCell_RetailGate()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);

        var deepGap = new Vector3(185.345f, 82.464f, 116.48f);

        uint picked = CellTransit.FindCellList(cache, deepGap, FootRadius, 0xA9B30104u);
        _out.WriteLine($"pick(seed 0x104) at deep gap -> 0x{picked:X8}");
        Assert.Equal(0xA9B30104u, picked);
    }

    [Fact]
    public void FindTransitCellsSphere_ExitPortalStraddleGate_MatchesRetail()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);

        var cell102 = cache.GetCellStruct(0xA9B30102u)!;

        var farCandidates = new List<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cell102, 0xA9B30102u, new Vector3(183.0f, 86.5f, 117.0f),
            FootRadius, farCandidates, out bool farStraddle);
        Assert.False(farStraddle);

        // (b) At the door plane (0.30 m away < 0.48 radius): straddle fires.
        var nearCandidates = new List<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cell102, 0xA9B30102u, new Vector3(185.70f, 85.5f, 117.0f),
            FootRadius, nearCandidates, out bool nearStraddle);
        Assert.True(nearStraddle);
    }

    private static void RegisterBuildings(DatCollection dats, PhysicsDataCache cache, uint lbPrefix)
    {
        var lbInfo = dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>(lbPrefix | 0xFFFEu);
        Assert.NotNull(lbInfo);
        foreach (var building in lbInfo!.Buildings)
        {
            if (building.Portals.Count == 0) continue;
            var portals = new List<BldPortalInfo>(building.Portals.Count);
            foreach (var bp in building.Portals)
                portals.Add(new BldPortalInfo(
                    otherCellId: lbPrefix | (uint)bp.OtherCellId,
                    otherPortalId: unchecked((short)bp.OtherPortalId),
                    flags: (ushort)bp.Flags));
            var transform =
                Matrix4x4.CreateFromQuaternion(building.Frame.Orientation) *
                Matrix4x4.CreateTranslation(building.Frame.Origin);
            int gridX = (int)(building.Frame.Origin.X / 24f);
            int gridY = (int)(building.Frame.Origin.Y / 24f);
            uint landcellLow = (uint)(gridX * 8 + gridY + 1);
            cache.CacheBuilding(lbPrefix | landcellLow, portals, transform);
        }
    }

    [Fact]
    public void A9B3Cottage_OutdoorSeed_TickSkippedThreshold_RecoversViaGrowingWalk()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);
        RegisterBuildings(dats, cache, 0xA9B30000u);

        var pastThreshold = new Vector3(184.30f, 82.20f, 116.48f);

        uint picked = CellTransit.FindCellList(cache, pastThreshold, FootRadius, 0xA9B3003Cu);
        _out.WriteLine($"pick(seed 0x3C) one tick past the threshold -> 0x{picked:X8}");
        Assert.Equal(0xA9B30100u, picked);
    }

    [Fact]
    public void A9B3Cottage_RunSpeedEntryReplay_NeverStrandsOutdoor()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);
        RegisterBuildings(dats, cache, 0xA9B30000u);

        for (float phase = 0f; phase < 0.16f; phase += 0.04f)
        {
            uint curr = 0xA9B3003Cu;
            for (float x = 191.5f - phase; x > 181.0f; x -= 0.16f)
            {
                float t = (191.5f - x) / 10.5f;
                var p = new Vector3(x, 82.7f - t * 0.7f, 116.48f);
                curr = CellTransit.FindCellList(cache, p, FootRadius, curr);
            }
            _out.WriteLine($"phase {phase:F2}: final curr=0x{curr & 0xFFFFu:X4}");
            Assert.True((curr & 0xFFFFu) >= 0x0100u,
                $"run-speed entry (phase {phase:F2}) stranded OUTDOOR (0x{curr:X8}) inside the cottage — the absorbing state");
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_ReplayCapturedEntryWalk()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);
        RegisterBuildings(dats, cache, 0xA9B30000u);
        _out.WriteLine($"buildings on landcells: {string.Join(", ", System.Linq.Enumerable.Select(cache.BuildingIds, b => $"0x{b:X8}"))}");

        uint curr = 0xA9B3003Cu;
        uint lastPrinted = 0;
        void Step(Vector3 p)
        {
            uint contains = 0;
            for (uint low = 0x0100; low <= 0x010F; low++)
            {
                var cand = cache.GetCellStruct(0xA9B30000u | low);
                if (cand?.CellBSP?.Root is null) continue;
                var local = Vector3.Transform(p, cand.InverseWorldTransform);
                if (BSPQuery.PointInsideCellBsp(cand.CellBSP.Root, local)) { contains = 0xA9B30000u | low; break; }
            }
            uint picked = CellTransit.FindCellList(cache, p, FootRadius, curr);
            if (picked != curr || contains != lastPrinted)
                _out.WriteLine($"  ({p.X:F2},{p.Y:F2}) contains=0x{contains & 0xFFFFu:X4} pick: 0x{curr & 0xFFFFu:X4} -> 0x{picked & 0xFFFFu:X4}");
            lastPrinted = contains;
            curr = picked;
        }

        _out.WriteLine("— leg A: west along y 82.7→82.0, x 191.5→181.0 —");
        for (float t = 0f; t <= 1f; t += 0.01f)
            Step(new Vector3(191.5f - t * 10.5f, 82.7f - t * 0.7f, 116.48f));
        _out.WriteLine($"after leg A: curr=0x{curr & 0xFFFFu:X4}");

        _out.WriteLine("— leg B: wander north (181.0,82.0) → (182.4,88.1) —");
        for (float t = 0f; t <= 1f; t += 0.01f)
            Step(new Vector3(181.0f + t * 1.4f, 82.0f + t * 6.1f, 116.48f));
        _out.WriteLine($"after leg B: curr=0x{curr & 0xFFFFu:X4}");
    }

    [Fact]
    public void A9B3Cottage_OutdoorSeed_AtThresholdGap_KeepsOutdoor()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B30000u);
        RegisterBuildings(dats, cache, 0xA9B30000u);

        var gap = new Vector3(184.915f, 82.464f, 116.48f);
        uint picked = CellTransit.FindCellList(cache, gap, FootRadius, 0xA9B3003Cu);
        _out.WriteLine($"pick(seed 0x3C) at gap -> 0x{picked:X8}");
        Assert.Equal(0xA9B3003Cu, picked);
    }

    [Fact]
    public void ThresholdCottage_AdjacentClaim_LaterallyRecovers_ViaStabGraph()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadLandblockInteriors(dats, 0xA9B40000u);

        var spawnFootCenter = new Vector3(155.390f, 11.020f, 94.0f + FootRadius);

        uint picked = CellTransit.FindCellList(cache, spawnFootCenter, FootRadius, 0xA9B40172u);
        _out.WriteLine($"pick(seed 0x172) at 0x171-interior -> 0x{picked:X8}");
        Assert.Equal(0xA9B40171u, picked);
    }
}
