using AcDream.Content.Pak;
using DatReaderWriter;
using DatReaderWriter.Options;
using GfxObj = DatReaderWriter.DBObjs.GfxObj;

namespace AcDream.Content.Tests;

[Trait("Lane", "PreparedPackage")]
public sealed class InstalledPreparedCollisionCatalogTests
{
    [Fact]
    public void InstalledPackage_FacilityHubStepsCarryRetailDrawingSphere()
    {
        const uint stairGfxObjId = 0x0100_00DEu;
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=PreparedPackage requires installed retail DATs and a validated acdream.pak; see docs/release-gate.md.");

        string packagePath = ResolvePackagePath(datDir);
        if (!File.Exists(packagePath))
            Assert.Fail("Lane=PreparedPackage requires installed retail DATs and a validated acdream.pak; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        using var source = new PakPreparedAssetSource(packagePath, adapter);
        GfxObj stair = Assert.IsType<GfxObj>(dats.Get<GfxObj>(stairGfxObjId));

        PreparedAssetReadResult prepared = source.Read(
            PreparedAssetRequest.GfxObj(stairGfxObjId));

        Assert.Equal(PreparedAssetReadStatus.Loaded, prepared.Status);
        Assert.NotNull(prepared.Data);
        Assert.NotNull(prepared.Data!.SelectionSphere);
        DatReaderWriter.Types.DrawingBSPNode root = Assert.IsType<
            DatReaderWriter.Types.DrawingBSPNode>(stair.DrawingBSP.Root);
        Assert.Equal(
            root.BoundingSphere.Origin,
            prepared.Data.SelectionSphere.Origin);
        Assert.Equal(
            root.BoundingSphere.Radius,
            prepared.Data.SelectionSphere.Radius);
    }

    [Fact]
    public void InstalledPackage_ContainsAndReadsCanonicalCollisionKeys()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=PreparedPackage requires installed retail DATs and a validated acdream.pak; see docs/release-gate.md.");

        string packagePath = ResolvePackagePath(datDir);
        if (!File.Exists(packagePath))
            Assert.Fail("Lane=PreparedPackage requires installed retail DATs and a validated acdream.pak; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        using var source =
            new PakPreparedAssetSource(packagePath, adapter);

        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            source.ReadGfxObjCollision(0x0100_0A2Bu).Status);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            source.ReadSetupCollision(0x0200_0001u).Status);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            source.ReadCellStructureCollision(0xA9B4_013Fu).Status);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            source.ReadEnvCellTopology(0xA9B4_013Fu).Status);
        Assert.Equal(4, source.CollisionStats.Loaded);
        Assert.Equal(new FileInfo(packagePath).Length, source.MappedVirtualBytes);
        Assert.InRange(source.MappedVirtualBytes, 1L, 5L * 1024 * 1024 * 1024);
    }

    private static string ResolvePackagePath(string datDir)
    {
        string? configured =
            Environment.GetEnvironmentVariable("ACDREAM_PAK_PATH");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(datDir, "acdream.pak");
    }

    private static string? ResolveDatDir()
    {
        string? configured =
            Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && Directory.Exists(configured))
        {
            return configured;
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
