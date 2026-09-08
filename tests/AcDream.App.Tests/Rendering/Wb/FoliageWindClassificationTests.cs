using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class FoliageWindClassificationTests
{
    private const uint ProceduralSceneryEntityId = 0x80010203u; // bit 31 set
    private const uint OrdinaryEntityId = 0x00010203u;

    [Fact]
    public void ProceduralSceneryCutoutSubsetGetsTheCutoutFlag()
    {
        uint flags = FoliageWindClassification.Classify(
            ProceduralSceneryEntityId,
            isExcluded: false,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);

        Assert.Equal(FoliageWindClassification.CutoutFoliageFlag, flags);
    }

    [Fact]
    public void ProceduralSceneryOpaqueSubsetGetsTheTrunkFlagOnlyWhenTheMeshOwnsACutoutSubset()
    {
        uint withCutoutSibling = FoliageWindClassification.Classify(
            ProceduralSceneryEntityId,
            isExcluded: false,
            TranslucencyKind.Opaque,
            meshHasCutoutSubset: true);
        uint withoutCutoutSibling = FoliageWindClassification.Classify(
            ProceduralSceneryEntityId,
            isExcluded: false,
            TranslucencyKind.Opaque,
            meshHasCutoutSubset: false);

        Assert.Equal(FoliageWindClassification.TrunkFlag, withCutoutSibling);
        Assert.Equal(0u, withoutCutoutSibling); // a rock, not a tree trunk
    }

    [Fact]
    public void NonProceduralSceneryEntityGetsNeitherBitRegardlessOfMaterial()
    {
        uint cutout = FoliageWindClassification.Classify(
            OrdinaryEntityId,
            isExcluded: false,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);
        uint opaqueWithCutoutSibling = FoliageWindClassification.Classify(
            OrdinaryEntityId,
            isExcluded: false,
            TranslucencyKind.Opaque,
            meshHasCutoutSubset: true);

        Assert.Equal(0u, cutout);
        Assert.Equal(0u, opaqueWithCutoutSibling);
    }

    [Fact]
    public void AnExcludedObjectIdGetsNeitherBitEvenWhenOtherwiseQualifying()
    {
        uint cutout = FoliageWindClassification.Classify(
            ProceduralSceneryEntityId,
            isExcluded: true,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);
        uint trunk = FoliageWindClassification.Classify(
            ProceduralSceneryEntityId,
            isExcluded: true,
            TranslucencyKind.Opaque,
            meshHasCutoutSubset: true);

        Assert.Equal(0u, cutout);
        Assert.Equal(0u, trunk);
    }

    [Theory]
    [InlineData(TranslucencyKind.AlphaBlend)]
    [InlineData(TranslucencyKind.Additive)]
    [InlineData(TranslucencyKind.InvAlpha)]
    public void OtherMaterialKindsOnProceduralSceneryGetNeitherBit(TranslucencyKind translucency)
    {
        uint flags = FoliageWindClassification.Classify(
            ProceduralSceneryEntityId,
            isExcluded: false,
            translucency,
            meshHasCutoutSubset: true);

        Assert.Equal(0u, flags);
    }

    [Theory]
    [InlineData(0x80000000u, true)]     // top nibble 0x8 exactly
    [InlineData(0x8FFFFFFFu, true)]     // top nibble 0x8, every other bit set
    [InlineData(0x7FFFFFFFu, false)]
    [InlineData(0x00000000u, false)]
    [InlineData(0xFFFFFFFFu, false)]    // top nibble 0xF — bit 31 set, NOT procedural scenery
    [InlineData(0xC0010203u, false)]    // LandblockStaticEntityIdAllocator (top nibble 0xC) — fence/gate/building shell
    [InlineData(0xDA11D012u, false)]
    [InlineData(0xFFFFFF01u, false)]    // PortalTunnelPresentation synthetic id
    public void IsProceduralSceneryDecodesTheFullTopNibbleNotJustBit31(uint entityId, bool expected)
    {
        Assert.Equal(expected, FoliageWindClassification.IsProceduralScenery(entityId));
    }

    [Fact]
    public void GroupKeysWithDifferentFoliageFlagsAreNeverEqualEvenWithIdenticalGeometry()
    {
        const uint proceduralSceneryTreeId = 0x80010203u; // top nibble 0x8
        const uint landblockStaticFenceId = 0xC0010203u;  // top nibble 0xC

        uint sceneryFlags = FoliageWindClassification.Classify(
            proceduralSceneryTreeId,
            isExcluded: false,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);
        uint landblockStaticFlags = FoliageWindClassification.Classify(
            landblockStaticFenceId,
            isExcluded: false,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);

        Assert.Equal(FoliageWindClassification.CutoutFoliageFlag, sceneryFlags); // 0x2
        Assert.Equal(0u, landblockStaticFlags);                                   // 0x0

        var sceneryKey = new GroupKey(
            FirstIndex: 100,
            BaseVertex: 5,
            IndexCount: 12,
            TextureSlot: new AcDream.App.Rendering.Gpu.GpuTextureSlot(42),
            TextureLayer: 0,
            Translucency: TranslucencyKind.ClipMap,
            MaterialState: RetailSetSurfaceMaterialState.Opaque,
            CullMode: DatReaderWriter.Enums.CullMode.CounterClockwise,
            FoliageFlags: sceneryFlags);
        var landblockStaticKey = sceneryKey with { FoliageFlags = landblockStaticFlags };

        Assert.NotEqual(sceneryKey, landblockStaticKey);
    }

    [Theory]
    [InlineData(new[] { false, false }, false)]
    [InlineData(new[] { true, false }, true)]
    [InlineData(new[] { false, true }, true)]
    [InlineData(new[] { true, true }, true)]
    public void ComputeEntityHasCutoutSubsetOrsAcrossEveryPart(bool[] partHasCutout, bool expected)
    {
        bool result = FoliageWindClassification.ComputeEntityHasCutoutSubset(
            partHasCutout,
            context: 0,
            static (_, value) => value);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComputeEntityHasCutoutSubsetIsFalseForAnEmptyPartList()
    {
        Assert.False(
            FoliageWindClassification.ComputeEntityHasCutoutSubset(
                Array.Empty<bool>(),
                context: 0,
                static (_, value) => value));
    }

    [Fact]
    public void TwoPartSetupGivesTheOpaqueTrunkPartTheTrunkFlagWhenAnotherPartHasCutout()
    {
        const uint sceneryEntityId = 0x80010203u;
        bool[] partAHasCutoutSubset = [false];
        bool[] setupPartsHasCutoutSubset = [false, true]; // part A (trunk), part B (leaves)

        bool partAOwnHasCutoutSubset = FoliageWindClassification.ComputeEntityHasCutoutSubset(
            partAHasCutoutSubset,
            context: 0,
            static (_, value) => value);
        bool entityScopedHasCutoutSubset = FoliageWindClassification.ComputeEntityHasCutoutSubset(
            setupPartsHasCutoutSubset,
            context: 0,
            static (_, value) => value);

        uint flagsFromPartOwnValue = FoliageWindClassification.Classify(
            sceneryEntityId,
            isExcluded: false,
            TranslucencyKind.Opaque,
            partAOwnHasCutoutSubset);
        uint flagsFromEntityScopedValue = FoliageWindClassification.Classify(
            sceneryEntityId,
            isExcluded: false,
            TranslucencyKind.Opaque,
            entityScopedHasCutoutSubset);

        Assert.Equal(0u, flagsFromPartOwnValue); // the pre-fix bug: no flag at all
        Assert.Equal(FoliageWindClassification.TrunkFlag, flagsFromEntityScopedValue);
    }
}
