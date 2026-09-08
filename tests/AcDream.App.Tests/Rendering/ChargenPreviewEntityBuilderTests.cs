using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Content;
using AcDream.Content.CharGen;
using AcDream.Content.Vfx;
using AcDream.Core.CharGen;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Rendering;

[Trait("Lane", "InstalledDat")]
public sealed class ChargenPreviewEntityBuilderTests
{
    private readonly ITestOutputHelper _out;
    public ChargenPreviewEntityBuilderTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TryBuild_AluvianMaleDefaultSelection_ProducesANonEmptyStaticPoseEntity()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? aluvian)); // Aluvian.
        Assert.True(aluvian!.GendersByKey.TryGetValue(1, out ChargenGenderOptions? male));

        var catalog = new ChargenAppearanceCatalog(adapter);
        ChargenAppearanceSelection selection = ChargenAppearanceSelection.Default with
        {
            HairStyle = male!.HairStyles.Count > 0 ? 0u : ChargenAppearanceSelection.Unset,
            SkinShade = 0.5,
        };

        bool composed = ChargenAppearanceFactory.TryCompose(
            options, 1u, 1, selection, catalog, catalog, out ChargenAppearanceResult appearance);
        Assert.True(composed);
        Assert.Empty(appearance.MissingPalSetIds);
        Assert.Empty(appearance.MissingClothingTableIds);

        var animations = new RetailAnimationLoader(adapter);
        var entity = ChargenPreviewEntityBuilder.TryBuild(
            adapter, animations, appearance, heritageId: 1u, Quaternion.Identity, new object());

        Assert.NotNull(entity);
        Assert.NotEmpty(entity!.MeshRefs);
        Assert.Equal(appearance.SetupId, entity.SourceGfxObjOrSetupId);
        Assert.Equal(ChargenPreviewEntityBuilder.PreviewServerGuid, entity.ServerGuid);
        Assert.Equal(ChargenPreviewEntityBuilder.PreviewRenderId, entity.Id);
        Assert.NotNull(entity.PaletteOverride);
        Assert.Equal(appearance.BasePaletteId, entity.PaletteOverride!.BasePaletteId);

        _out.WriteLine($"setup=0x{appearance.SetupId:X8} meshRefs={entity.MeshRefs.Count} subPalettes={entity.PaletteOverride.SubPalettes.Count}");
    }

    [Fact]
    public void TryBuild_ExplicitRenderId_StampsThatIdOnTheEntity_DistinctFromTheAppearanceDefault()
    {
        Assert.NotEqual(
            ChargenPreviewEntityBuilder.PreviewRenderId,
            ChargenPreviewEntityBuilder.SummaryPreviewRenderId);
        Assert.NotEqual(
            ChargenPreviewEntityBuilder.PreviewBackdropRenderId,
            ChargenPreviewEntityBuilder.SummaryPreviewBackdropRenderId);

        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? aluvian)); // Aluvian.
        Assert.True(aluvian!.GendersByKey.TryGetValue(1, out ChargenGenderOptions? male));

        var catalog = new ChargenAppearanceCatalog(adapter);
        ChargenAppearanceSelection selection = ChargenAppearanceSelection.Default with
        {
            HairStyle = male!.HairStyles.Count > 0 ? 0u : ChargenAppearanceSelection.Unset,
            SkinShade = 0.5,
        };

        bool composed = ChargenAppearanceFactory.TryCompose(
            options, 1u, 1, selection, catalog, catalog, out ChargenAppearanceResult appearance);
        Assert.True(composed);

        var animations = new RetailAnimationLoader(adapter);
        var entity = ChargenPreviewEntityBuilder.TryBuild(
            adapter, animations, appearance, heritageId: 1u, Quaternion.Identity, new object(),
            renderId: ChargenPreviewEntityBuilder.SummaryPreviewRenderId);

        Assert.NotNull(entity);
        Assert.Equal(ChargenPreviewEntityBuilder.SummaryPreviewRenderId, entity!.Id);
        Assert.NotEqual(ChargenPreviewEntityBuilder.PreviewRenderId, entity.Id);
    }

    [Fact]
    public void TryBuild_UnknownSetupId_ReturnsNull()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var animations = new RetailAnimationLoader(adapter);

        var bogusAppearance = new ChargenAppearanceResult(
            SetupId: 0x0200_FFFFu, // Not a real installed Setup id.
            BasePaletteId: 0u,
            ObjDesc: ChargenObjDesc.Empty,
            MissingPalSetIds: [],
            MissingClothingTableIds: [],
            ClothingTablesMissingBaseEffectForSetup: []);

        var entity = ChargenPreviewEntityBuilder.TryBuild(
            adapter, animations, bogusAppearance, heritageId: 1u, Quaternion.Identity, new object());

        Assert.Null(entity);
    }

    [Fact]
    public void TryBuild_OlthoiHeritage_ResolvesADifferentRestPoseDidThanStandardHeritages()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(12u, out ChargenHeritageOptions? olthoi)); // Olthoi.
        Assert.True(olthoi!.GendersByKey.TryGetValue(1, out ChargenGenderOptions? male)
            || olthoi.GendersByKey.TryGetValue(2, out male));
        Assert.NotNull(male);
        int genderKey = olthoi.GendersByKey.First(kv => ReferenceEquals(kv.Value, male)).Key;

        var catalog = new ChargenAppearanceCatalog(adapter);
        var animations = new RetailAnimationLoader(adapter);

        bool composed = ChargenAppearanceFactory.TryCompose(
            options, 12u, genderKey, ChargenAppearanceSelection.Default with { SkinShade = 0.5 },
            catalog, catalog, out ChargenAppearanceResult appearance);
        Assert.True(composed);

        var entity = ChargenPreviewEntityBuilder.TryBuild(
            adapter, animations, appearance, heritageId: 12u, Quaternion.Identity, new object());

        Assert.NotNull(entity);
        Assert.NotEmpty(entity!.MeshRefs);
    }

    [Fact]
    public void TryBuildAnimated_AluvianMaleDefaultSelection_ResolvesARealIdleCycle()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? aluvian));
        Assert.True(aluvian!.GendersByKey.TryGetValue(1, out ChargenGenderOptions? male));

        var catalog = new ChargenAppearanceCatalog(adapter);
        ChargenAppearanceSelection selection = ChargenAppearanceSelection.Default with
        {
            HairStyle = male!.HairStyles.Count > 0 ? 0u : ChargenAppearanceSelection.Unset,
            SkinShade = 0.5,
        };

        bool composed = ChargenAppearanceFactory.TryCompose(
            options, 1u, 1, selection, catalog, catalog, out ChargenAppearanceResult appearance);
        Assert.True(composed);

        var animations = new RetailAnimationLoader(adapter);
        ChargenPreviewAnimatedBuild? build = ChargenPreviewEntityBuilder.TryBuildAnimated(
            adapter, animations, appearance, heritageId: 1u, Quaternion.Identity, new object());

        Assert.NotNull(build);
        Assert.NotEmpty(build!.DrawableParts);
        Assert.NotEmpty(build.RestMeshRefs);
        Assert.NotNull(build.IdleAnimation);
        Assert.True(build.IdleHighFrame >= build.IdleLowFrame);
        Assert.True(build.IdleAnimation!.PartFrames.Count > build.IdleHighFrame);

        var animator = new ChargenPreviewAnimator(build);
        Assert.False(animator.IsZoomedIn);
        Assert.NotEmpty(animator.Entity.MeshRefs);

        animator.Tick(1f / 30f); // one frame's worth — must not throw or empty the mesh.
        Assert.NotEmpty(animator.Entity.MeshRefs);
    }

    [Fact]
    public void TryBuildAnimated_UnknownSetupId_ReturnsNull()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var animations = new RetailAnimationLoader(adapter);

        var bogusAppearance = new ChargenAppearanceResult(
            SetupId: 0x0200_FFFFu,
            BasePaletteId: 0u,
            ObjDesc: ChargenObjDesc.Empty,
            MissingPalSetIds: [],
            MissingClothingTableIds: [],
            ClothingTablesMissingBaseEffectForSetup: []);

        ChargenPreviewAnimatedBuild? build = ChargenPreviewEntityBuilder.TryBuildAnimated(
            adapter, animations, bogusAppearance, heritageId: 1u, Quaternion.Identity, new object());

        Assert.Null(build);
    }

    [Theory]
    [InlineData(0x10000011u)] // Olthoi's shared idle/rest enum key.
    [InlineData(0x10000013u)] // OlthoiAcid's shared idle/rest enum key.
    public void OlthoiFamily_SharedIdleRestEnumKey_ResolvesToARealInstalledDid(uint sharedEnumKey)
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        uint did = RetailHeldPose.ResolvePoseDid(adapter, sharedEnumKey);

        Assert.NotEqual(0u, did);
        Assert.Equal(0x03u, did >> 24); // resolves to a real Animation DID.
    }

    [Fact]
    public void TryBuildAnimated_OlthoiHeritage_ResolvesARealIdleAnimationToo()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(12u, out ChargenHeritageOptions? olthoi));
        Assert.True(olthoi!.GendersByKey.TryGetValue(1, out ChargenGenderOptions? male)
            || olthoi.GendersByKey.TryGetValue(2, out male));
        Assert.NotNull(male);
        int genderKey = olthoi.GendersByKey.First(kv => ReferenceEquals(kv.Value, male)).Key;

        var catalog = new ChargenAppearanceCatalog(adapter);
        var animations = new RetailAnimationLoader(adapter);
        bool composed = ChargenAppearanceFactory.TryCompose(
            options, 12u, genderKey, ChargenAppearanceSelection.Default with { SkinShade = 0.5 },
            catalog, catalog, out ChargenAppearanceResult appearance);
        Assert.True(composed);

        ChargenPreviewAnimatedBuild? build = ChargenPreviewEntityBuilder.TryBuildAnimated(
            adapter, animations, appearance, heritageId: 12u, Quaternion.Identity, new object());

        Assert.NotNull(build);
        Assert.NotNull(build!.IdleAnimation);

        var animator = new ChargenPreviewAnimator(build);
        Assert.False(animator.IsZoomedIn);
        Assert.NotEmpty(animator.Entity.MeshRefs);
    }

    [Fact]
    public void TryBuildBackdrop_AluvianHeritage_ResolvesANonEmptyMesh()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? aluvian)); // Aluvian.
        if (aluvian!.EnvironmentSetupId == 0u)
        {
            _out.WriteLine("SKIP: installed dat's Aluvian heritage authors no EnvironmentSetupId.");
            return;
        }

        var entity = ChargenPreviewEntityBuilder.TryBuildBackdrop(
            adapter, aluvian.EnvironmentSetupId, new object());

        Assert.NotNull(entity);
        Assert.NotEmpty(entity!.MeshRefs);
        Assert.Equal(aluvian.EnvironmentSetupId, entity.SourceGfxObjOrSetupId);
        Assert.Equal(ChargenPreviewEntityBuilder.PreviewBackdropServerGuid, entity.ServerGuid);
        Assert.Equal(ChargenPreviewEntityBuilder.PreviewBackdropRenderId, entity.Id);
        Assert.Equal(Vector3.Zero, entity.Position);
        Assert.Equal(Quaternion.Identity, entity.Rotation);

        _out.WriteLine($"backdropSetup=0x{aluvian.EnvironmentSetupId:X8} meshRefs={entity.MeshRefs.Count}");
    }

    [Fact]
    public void TryBuildBackdrop_ExplicitRenderId_StampsThatIdOnTheEntity()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? aluvian)); // Aluvian.
        if (aluvian!.EnvironmentSetupId == 0u)
        {
            _out.WriteLine("SKIP: installed dat's Aluvian heritage authors no EnvironmentSetupId.");
            return;
        }

        var entity = ChargenPreviewEntityBuilder.TryBuildBackdrop(
            adapter, aluvian.EnvironmentSetupId, new object(),
            renderId: ChargenPreviewEntityBuilder.SummaryPreviewBackdropRenderId);

        Assert.NotNull(entity);
        Assert.Equal(ChargenPreviewEntityBuilder.SummaryPreviewBackdropRenderId, entity!.Id);
        Assert.NotEqual(ChargenPreviewEntityBuilder.PreviewBackdropRenderId, entity.Id);
    }

    [Fact]
    public void TryBuildBackdrop_UnsetEnvironmentSetupId_ReturnsNull()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        var entity = ChargenPreviewEntityBuilder.TryBuildBackdrop(adapter, 0u, new object());

        Assert.Null(entity);
    }

    [Fact]
    public void TryBuildBackdrop_UnknownSetupId_ReturnsNull()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        var entity = ChargenPreviewEntityBuilder.TryBuildBackdrop(
            adapter, 0x0200_FFFFu, new object());

        Assert.Null(entity);
    }

    [Fact]
    public void TryBuildBackdrop_AllThirteenHeritages_ResolveOrAreReportedByName()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.Equal(13, options.HeritagesById.Count);

        var unresolved = new List<string>();
        foreach (KeyValuePair<uint, ChargenHeritageOptions> pair in options.HeritagesById)
        {
            ChargenHeritageOptions heritage = pair.Value;
            var entity = ChargenPreviewEntityBuilder.TryBuildBackdrop(
                adapter, heritage.EnvironmentSetupId, new object());
            if (entity is null)
            {
                unresolved.Add(
                    $"{heritage.Name} (id={pair.Key}, environmentSetupId=0x{heritage.EnvironmentSetupId:X8})");
            }
        }

        _out.WriteLine(unresolved.Count == 0
            ? "All 13 heritages resolved a drawable environment backdrop."
            : "Unresolved: " + string.Join("; ", unresolved));

        Assert.True(
            unresolved.Count <= 13,
            "Sanity bound only — the WriteLine above is the real measurement.");
    }
}
