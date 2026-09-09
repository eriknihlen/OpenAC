using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class CellTransitTests
{
    [Fact]
    public void DeepInteriorSphere_NoStraddle_AddsNoOutdoorCells()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var cache = new PhysicsDataCache();
        HydrateCell(cache, datDir, 0xA9B4013Fu);
        HydrateCell(cache, datDir, 0xA9B40150u);

        Assert.NotNull(cache.GetCellStruct(0xA9B4013Fu));
        Assert.NotNull(cache.GetCellStruct(0xA9B40150u));

        var sphereWorld = new Vector3(132.5935f, 16.350428f, 94.48f);
        const float sphereRadius = 0.48f;
        const uint startCellId = 0xA9B4013Fu;

        _ = CellTransit.FindCellSet(
            cache,
            sphereWorld,
            sphereRadius,
            startCellId,
            out var cellSet);

        Assert.DoesNotContain(cellSet, c => (c & 0xFFFFu) < 0x0100u);
        Assert.Contains(startCellId, cellSet);
    }

    [Fact]
    public void AlcoveSphere_StraddlesExitPortal_ReachesDoorOutdoorCell()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var cache = new PhysicsDataCache();
        HydrateCell(cache, datDir, 0xA9B40150u);

        var sphereWorld = new Vector3(132.4014f, 16.761757f, 94.48f);
        const float sphereRadius = 0.48f;

        _ = CellTransit.FindCellSet(
            cache,
            sphereWorld,
            sphereRadius,
            0xA9B40150u,
            out var cellSet);

        Assert.True(
            cellSet.Contains(0xA9B40029u),
            $"Straddle-gated outside-add: the alcove sphere straddles " +
            $"0xA9B40150's exit portal, so the door's outdoor cell " +
            $"0xA9B40029 must enter the set (the straddle branch of " +
            $"CEnvCell::find_transit_cells). " +
            $"Actual: {string.Join(",", cellSet.Select(c => $"0x{c:X8}"))}");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string? ResolveDatDir()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(datDir) ? datDir : null;
    }

    private static void HydrateCell(PhysicsDataCache cache, string datDir, uint cellId)
    {
        const uint EnvCellPrefix = 0x0D000000u;
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var envCell = dats.Get<EnvCell>(cellId);
        if (envCell is null)
            throw new InvalidOperationException(
                $"Cell 0x{cellId:X8} missing from dat at {datDir}.");

        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(
            EnvCellPrefix | envCell.EnvironmentId);
        if (environment is null)
            throw new InvalidOperationException(
                $"Environment 0x{EnvCellPrefix | envCell.EnvironmentId:X8} missing.");

        if (!environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct))
            throw new InvalidOperationException(
                $"CellStructure {envCell.CellStructure} missing in environment.");

        var worldTransform =
            Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation) *
            Matrix4x4.CreateTranslation(envCell.Position.Origin);

        cache.CacheCellStruct(cellId, envCell, cellStruct, worldTransform);
    }
}
