using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.Lighting;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Rendering;

[Trait("Lane", "InstalledDat")]
public class TownNetworkFountainRoomLightInspectionTests
{
    private readonly ITestOutputHelper _out;
    public TownNetworkFountainRoomLightInspectionTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveDatDir()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(datDir) ? datDir : null;
    }

    [Theory]
    [InlineData(0x00070144u)]   // the fountain room — the user's original repro spot
    [InlineData(0x00070156u)]
    [InlineData(0x00070164u)]
    [InlineData(0x00070132u)]
    [InlineData(0x00070133u)]
    [InlineData(0x00070143u)]
    [InlineData(0x00070145u)]
    [InlineData(0x00070146u)]
    [InlineData(0x00070155u)]
    [InlineData(0x00070157u)]
    [Trait("Purpose", "Diagnostic")]
    public void StaticObjects_SetupLightsCount_Dump(uint cellId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var envCell = dats.Get<EnvCell>(cellId);
        if (envCell is null)
        {
            _out.WriteLine($"=== 0x{cellId:X8} NOT FOUND in dat ===");
            return;
        }

        _out.WriteLine($"=== 0x{cellId:X8} Env=0x{envCell.EnvironmentId:X4} struct={envCell.CellStructure} " +
                       $"pos=({envCell.Position.Origin.X:F2},{envCell.Position.Origin.Y:F2},{envCell.Position.Origin.Z:F2}) " +
                       $"StaticObjects={envCell.StaticObjects.Count} ===");

        int totalLights = 0;
        foreach (var so in envCell.StaticObjects)
        {
            if ((so.Id & 0xFF000000u) == 0x02000000u)
            {
                var setup = dats.Get<Setup>(so.Id);
                int lightCount = setup?.Lights.Count ?? -1;
                totalLights += Math.Max(lightCount, 0);
                _out.WriteLine($"    SETUP id=0x{so.Id:X8} at ({so.Frame.Origin.X:F2},{so.Frame.Origin.Y:F2},{so.Frame.Origin.Z:F2}) Lights={lightCount}");
            }
            else
            {
                _out.WriteLine($"    id=0x{so.Id:X8} at ({so.Frame.Origin.X:F2},{so.Frame.Origin.Y:F2},{so.Frame.Origin.Z:F2}) (not a Setup — no Lights dict)");
            }
        }
        _out.WriteLine($"  TOTAL dat-authored Lights in cell 0x{cellId:X8} StaticObjects: {totalLights}");
    }

    [Fact]
    public void CeilingFixtureSetup_MeshFlattenSurvivorCount_Dump()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var boundedDats = new DatCollectionAdapter(dats);

        const uint setupId = 0x02000365u;
        var setup = dats.Get<Setup>(setupId);
        Assert.NotNull(setup);

        _out.WriteLine($"=== Setup 0x{setupId:X8}: Parts={setup!.Parts.Count} PlacementFrames={setup.PlacementFrames.Count} Lights={setup.Lights.Count} ===");
        foreach (var kvp in setup.Lights)
            _out.WriteLine($"    light[{kvp.Key}] Color=({kvp.Value.Color?.Red},{kvp.Value.Color?.Green},{kvp.Value.Color?.Blue}) Intensity={kvp.Value.Intensity} Falloff={kvp.Value.Falloff} ConeAngle={kvp.Value.ConeAngle} LocalOrigin=({kvp.Value.ViewSpaceLocation?.Origin.X:R},{kvp.Value.ViewSpaceLocation?.Origin.Y:R},{kvp.Value.ViewSpaceLocation?.Origin.Z:R})");

        var flat = AcDream.Core.Meshing.SetupMesh.Flatten(setup);
        _out.WriteLine($"  SetupMesh.Flatten -> {flat.Count} MeshRefs");

        int survivors = 0, markerSkipped = 0, gfxNull = 0;
        foreach (var mr in flat)
        {
            if (AcDream.Core.Meshing.GfxObjDegradeResolver.IsRuntimeHiddenMarker(boundedDats, mr.GfxObjId))
            {
                markerSkipped++;
                _out.WriteLine($"    part gfx=0x{mr.GfxObjId:X8} -> MARKER (skipped)");
                continue;
            }
            var gfx = dats.Get<GfxObj>(mr.GfxObjId);
            if (gfx is null)
            {
                gfxNull++;
                _out.WriteLine($"    part gfx=0x{mr.GfxObjId:X8} -> GFXOBJ-NULL (dropped)");
                continue;
            }
            survivors++;
            _out.WriteLine($"    part gfx=0x{mr.GfxObjId:X8} -> survives (Polygons={gfx.Polygons.Count})");
        }
        _out.WriteLine($"  meshRefs survivor count = {survivors} (markerSkipped={markerSkipped} gfxNull={gfxNull}) " +
                       $"=> GameWindow.cs:7324 would {(survivors == 0 ? "DROP" : "KEEP")} this entity");
    }

    [Fact]
    public void TownCeilingFixture_AuthoredTypeKeyAndRootRankOrigin_ArePinnedFromInstalledDat()
    {
        var datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint cellId = 0x00070144u;
        const uint setupId = 0x02000365u;
        EnvCell cell = Assert.IsType<EnvCell>(dats.Get<EnvCell>(cellId));
        var fixture = Assert.Single(cell.StaticObjects, entry => entry.Id == setupId);
        Setup setup = Assert.IsType<Setup>(dats.Get<Setup>(setupId));
        var authored = Assert.Single(setup.Lights);

        Assert.Equal(0, authored.Key);
        Assert.Equal(0xCDCDCDCDu, BitConverter.SingleToUInt32Bits(authored.Value.ConeAngle));
        Assert.Equal(0.000759337f, authored.Value.ViewSpaceLocation.Origin.X);
        Assert.Equal(0.00675148f, authored.Value.ViewSpaceLocation.Origin.Y);
        Assert.Equal(0.0277f, authored.Value.ViewSpaceLocation.Origin.Z);

        Vector3 root = new(
            fixture.Frame.Origin.X,
            fixture.Frame.Origin.Y,
            fixture.Frame.Origin.Z);
        Quaternion rotation = new(
            fixture.Frame.Orientation.X,
            fixture.Frame.Orientation.Y,
            fixture.Frame.Orientation.Z,
            fixture.Frame.Orientation.W);
        Assert.Equal(new Vector3(69.875f, -69.916f, 5.005f), root);
        Assert.Equal(new Quaternion(0f, 0f, -0.94372f, 0.330745f), rotation);
        LightSource light = Assert.Single(LightInfoLoader.Load(
            setup,
            ownerId: 0x4000712Fu,
            entityPosition: root,
            entityRotation: rotation,
            isDynamic: false,
            cellId: cellId));

        Assert.True(light.HasRankingOrigin);
        Assert.Equal(root, light.RankingOrigin);
        Assert.Equal(LightKind.Point, light.Kind);
        Assert.NotEqual(root, light.WorldPosition);
        var localFrame = authored.Value.ViewSpaceLocation;
        var localOffset = new Vector3(
            localFrame.Origin.X,
            localFrame.Origin.Y,
            localFrame.Origin.Z);
        var localRotation = new Quaternion(
            localFrame.Orientation.X,
            localFrame.Orientation.Y,
            localFrame.Orientation.Z,
            localFrame.Orientation.W);
        Matrix4x4 expectedWorld = (Matrix4x4.CreateFromQuaternion(localRotation)
            * Matrix4x4.CreateTranslation(localOffset))
            * (Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(root));
        Assert.Equal(expectedWorld.Translation, light.WorldPosition);
    }

    [Theory]
    [InlineData(0x02000AA3u)]
    [InlineData(0x02001967u)]
    [InlineData(0x020018C5u)]
    public void FountainAndCandleSetup_DefaultScriptAndMeshSurvivorCount_Dump(uint setupId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var boundedDats = new DatCollectionAdapter(dats);

        var setup = dats.Get<Setup>(setupId);
        Assert.NotNull(setup);

        _out.WriteLine($"=== Setup 0x{setupId:X8}: Parts={setup!.Parts.Count} DefaultScript.DataId=0x{setup.DefaultScript.DataId:X8} Lights={setup.Lights.Count} ===");

        var flat = AcDream.Core.Meshing.SetupMesh.Flatten(setup);
        int survivors = 0;
        foreach (var mr in flat)
        {
            if (AcDream.Core.Meshing.GfxObjDegradeResolver.IsRuntimeHiddenMarker(boundedDats, mr.GfxObjId)) continue;
            if (dats.Get<GfxObj>(mr.GfxObjId) is null) continue;
            survivors++;
        }
        bool wouldBeKeptByCurrentFix = AcDream.Core.Meshing.EntityHydrationRules.ShouldKeepEntity(survivors, setup.Lights.Count);
        _out.WriteLine($"  flattened={flat.Count} meshSurvivors={survivors} " +
                       $"hasDefaultScript={setup.DefaultScript.DataId != 0} " +
                       $"=> current ShouldKeepEntity(mesh,lights) = {wouldBeKeptByCurrentFix}");
    }
}
