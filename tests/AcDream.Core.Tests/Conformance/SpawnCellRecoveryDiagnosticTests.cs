using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public sealed class SpawnCellRecoveryDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public SpawnCellRecoveryDiagnosticTests(ITestOutputHelper output) => _out = output;

    private const float FootRadius = 0.48f;
    private const uint PoisonedClaim = 0xA9B40162u;
    private const uint ActualCell    = 0xA9B40171u;

    private static readonly Vector3 Spawn = new(156.53314f, 11.775104f, 96.5f);

    [Fact]
    public void SpawnPosition_IsInside0171_NotInside0162_PickRecoversFromGoodSeed()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();

        var containing = new List<uint>();
        int loaded = 0;
        for (uint low = 0x0100; low <= 0x01FF; low++)
        {
            uint cellId = 0xA9B40000u | low;
            EnvCell cell;
            try { cell = ConformanceDats.LoadEnvCell(dats, cache, cellId); }
            catch { continue; }
            loaded++;
            if (cell.PointInCell(Spawn + new Vector3(0, 0, FootRadius)))
            {
                containing.Add(cellId);
                _out.WriteLine($"cell 0x{cellId:X8} CONTAINS spawn footCenter");
            }
        }
        Assert.True(loaded > 0, "expected Holtburg interior cells to load");

        // The poisoned-pair facts: position inside 0x171, NOT inside 0x162.
        Assert.Contains(ActualCell, containing);
        Assert.DoesNotContain(PoisonedClaim, containing);

        var footCenter = Spawn + new Vector3(0, 0, FootRadius);

        // Production pick with the GOOD seed keeps the player indoor.
        uint pickGood = CellTransit.FindCellList(cache, footCenter, FootRadius, ActualCell);
        _out.WriteLine($"FindCellList(seed=0x{ActualCell:X8}) -> 0x{pickGood:X8}");
        Assert.Equal(ActualCell, pickGood);

        uint pickBad = CellTransit.FindCellList(cache, footCenter, FootRadius, PoisonedClaim);
        _out.WriteLine($"FindCellList(seed=0x{PoisonedClaim:X8}) -> 0x{pickBad:X8}");
        Assert.Equal(PoisonedClaim, pickBad);
    }

    [Fact]
    public void SeenOutside_IsPopulated_ForGroundFloorInteriors()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();

        ConformanceDats.LoadEnvCell(dats, cache, ActualCell);
        ConformanceDats.LoadEnvCell(dats, cache, PoisonedClaim);

        var room = cache.GetCellStruct(ActualCell);
        Assert.NotNull(room);
        Assert.True(room!.SeenOutside, "0xA9B40171 should be seen_outside");
        _out.WriteLine($"0x{ActualCell:X8}.SeenOutside={room.SeenOutside}");

        var inn = cache.GetCellStruct(PoisonedClaim);
        Assert.NotNull(inn);
        _out.WriteLine($"0x{PoisonedClaim:X8}.SeenOutside={inn!.SeenOutside}");
    }

    [Fact]
    public void AdjacentRoomClaim_CorrectsToContainingRoom_ViaStabList()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();
        for (uint low = 0x0100; low <= 0x01FF; low++)
        {
            try { ConformanceDats.LoadEnvCell(dats, cache, 0xA9B40000u | low); }
            catch { }
        }

        var spawnFootCenter = new Vector3(155.390f, 11.020f, 94.0f + FootRadius);

        uint corrected = CellTransit.FindVisibleChildCell(
            cache, 0xA9B40172u, spawnFootCenter, useStabList: true);
        _out.WriteLine($"FindVisibleChildCell(claim 0x172, stab) -> 0x{corrected:X8}");
        Assert.Equal(ActualCell, corrected);
    }

    [Fact]
    public void PoisonedClaim_IsADifferentBuilding_55mAway()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var claim = dats.Get<DatReaderWriter.DBObjs.EnvCell>(PoisonedClaim);
        var actual = dats.Get<DatReaderWriter.DBObjs.EnvCell>(ActualCell);
        Assert.NotNull(claim);
        Assert.NotNull(actual);

        float dist = Vector3.Distance(claim!.Position.Origin, actual!.Position.Origin);
        _out.WriteLine(
            $"claim origin=({claim.Position.Origin.X:F1},{claim.Position.Origin.Y:F1}) " +
            $"actual origin=({actual.Position.Origin.X:F1},{actual.Position.Origin.Y:F1}) dist={dist:F1} m");
        Assert.True(dist > 20f, $"expected cross-building distance, got {dist:F1} m");
    }
}
