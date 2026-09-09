using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenAppearanceFactoryTests
{
    private const uint HeritageId = 1u;
    private const int GenderKey = 1;
    private const uint BodySetupId = 0x0200_0001u;
    private const uint AlternateBodySetupId = 0x0200_00FFu;

    private const uint BasePaletteId = 0x0400_0001u;
    private const uint SkinPalSetId = 0x0F00_0001u;
    private const uint HairColorPalSetId = 0x0F00_0002u;
    private const uint EyeColorPaletteId = 0x0400_0099u; // direct palette id, no PalSet indirection.

    private const uint HeadgearClothingTableId = 0x1900_0001u;
    private const uint TrousersClothingTableId = 0x1900_0002u;
    private const uint ShirtClothingTableId = 0x1900_0003u;
    private const uint FootwearClothingTableId = 0x1900_0004u;

    private static ChargenObjDesc MakeObjDesc(uint tag) => new(
        0u,
        [],
        [new ChargenTextureChange((byte)tag, 0x0500_0000u + tag, 0x0500_1000u + tag)],
        [new ChargenAnimPartChange((byte)tag, 0x0100_0000u + tag)]);

    private static ChargenGenderOptions MakeGender(uint alternateHairSetup = 0u, bool baldHairStyle = false) => new(
        GenderKey: GenderKey,
        Name: "Male",
        Scale: 100u,
        SetupId: BodySetupId,
        SoundTableId: 0x0900_0001u,
        IconId: 0x0600_0001u,
        BasePaletteId: BasePaletteId,
        SkinPalSetId: SkinPalSetId,
        PhysicsTableId: 0x0D00_0001u,
        MotionTableId: 0x0900_0002u,
        CombatTableId: 0x0000_0001u,
        BaseObjDesc: MakeObjDesc(0),
        HairColors: [HairColorPalSetId],
        HairStyles:
        [
            new ChargenHairStyle(0x0600_0002u, baldHairStyle, alternateHairSetup, MakeObjDesc(1)),
        ],
        EyeColors: [EyeColorPaletteId],
        EyeStrips:
        [
            new ChargenEyeStrip(0x0600_0003u, 0x0600_0004u, MakeObjDesc(2), MakeObjDesc(20)),
        ],
        NoseStrips: [new ChargenFaceStrip(0x0600_0005u, MakeObjDesc(3))],
        MouthStrips: [new ChargenFaceStrip(0x0600_0006u, MakeObjDesc(4))],
        Headgears: [new ChargenGearOption("Cap", HeadgearClothingTableId, 0x3000_0001u)],
        Shirts: [new ChargenGearOption("Shirt", ShirtClothingTableId, 0x3000_0002u)],
        Pants: [new ChargenGearOption("Pants", TrousersClothingTableId, 0x3000_0003u)],
        Footwear: [new ChargenGearOption("Boots", FootwearClothingTableId, 0x3000_0004u)],
        ClothingColors: [7u]);

    private static ChargenOptions MakeOptions(ChargenGenderOptions gender)
    {
        var heritage = new ChargenHeritageOptions(
            HeritageId, "Test", 0x0600_0001u, BodySetupId, BodySetupId,
            180u, 100u, [0], [],
            new Dictionary<uint, ChargenSkillCost>(), [],
            new Dictionary<int, ChargenGenderOptions> { [GenderKey] = gender });
        return new ChargenOptions(
            [],
            new Dictionary<uint, ChargenHeritageOptions> { [HeritageId] = heritage },
            new Dictionary<uint, ChargenSkillCost>());
    }

    private static ChargenClothingTable MakeClothingTable(uint clothingTableId, uint palSetId, uint bodySetupId)
    {
        var partChanges = new[] { new ChargenAnimPartChange(5, 0x0100_5000u + clothingTableId) };
        var textureChanges = new[] { new ChargenTextureChange(5, 0x0500_5000u, 0x0500_6000u) };
        var baseEffects = new Dictionary<uint, ChargenClothingBaseEffect>
        {
            [bodySetupId] = new ChargenClothingBaseEffect(partChanges, textureChanges),
        };
        var choice = new ChargenClothingSubPaletteChoice(
            palSetId, [new ChargenClothingSubPaletteRange(80u, 16u)]);
        var templates = new Dictionary<uint, ChargenClothingPaletteTemplate>
        {
            [7u] = new ChargenClothingPaletteTemplate([choice]),
        };
        return new ChargenClothingTable(baseEffects, templates);
    }

    private sealed class FakePalSetSource : IChargenPalSetSource
    {
        private readonly Dictionary<uint, ChargenPalSet> _sets = new();
        public void Add(uint id, params uint[] paletteIds) => _sets[id] = new ChargenPalSet(paletteIds);
        public ChargenPalSet? TryGetPalSet(uint palSetId) => _sets.TryGetValue(palSetId, out var s) ? s : null;
    }

    private sealed class FakeClothingTableSource : IChargenClothingTableSource
    {
        private readonly Dictionary<uint, ChargenClothingTable> _tables = new();
        public void Add(uint id, ChargenClothingTable table) => _tables[id] = table;
        public ChargenClothingTable? TryGetClothingTable(uint clothingTableId) =>
            _tables.TryGetValue(clothingTableId, out var t) ? t : null;
    }

    private static (FakePalSetSource pal, FakeClothingTableSource clothing) MakeSources(uint bodySetupId = BodySetupId)
    {
        var pal = new FakePalSetSource();
        pal.Add(SkinPalSetId, 0x0400_0010u, 0x0400_0011u, 0x0400_0012u);
        pal.Add(HairColorPalSetId, 0x0400_0020u, 0x0400_0021u);
        var clothingDyePalSetId = 0x0F00_0003u;
        pal.Add(clothingDyePalSetId, 0x0400_0030u, 0x0400_0031u);

        var clothing = new FakeClothingTableSource();
        clothing.Add(HeadgearClothingTableId, MakeClothingTable(HeadgearClothingTableId, clothingDyePalSetId, bodySetupId));
        clothing.Add(TrousersClothingTableId, MakeClothingTable(TrousersClothingTableId, clothingDyePalSetId, bodySetupId));
        clothing.Add(ShirtClothingTableId, MakeClothingTable(ShirtClothingTableId, clothingDyePalSetId, bodySetupId));
        clothing.Add(FootwearClothingTableId, MakeClothingTable(FootwearClothingTableId, clothingDyePalSetId, bodySetupId));
        return (pal, clothing);
    }

    [Fact]
    public void TryCompose_ReturnsFalse_WhenHeritageIsUnknown()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, heritageId: 999u, GenderKey, ChargenAppearanceSelection.Default,
            pal, clothing, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryCompose_ReturnsFalse_WhenGenderIsUnknown()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, genderKey: 999, ChargenAppearanceSelection.Default,
            pal, clothing, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryCompose_DefaultSelection_ResolvesBodySetupAndUnconditionalSkinSubpalette()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, ChargenAppearanceSelection.Default,
            pal, clothing, out ChargenAppearanceResult result);

        Assert.True(ok);
        Assert.Equal(BodySetupId, result.SetupId);
        Assert.Equal(BasePaletteId, result.BasePaletteId);
        Assert.Empty(result.MissingPalSetIds);
        Assert.Empty(result.MissingClothingTableIds);

        // UnsetShade (-1.0) is out of [0,1], so GetPaletteIndex returns -1 and
        // the skin block is skipped for THIS test's default selection — the
        // "unconditional" behavior is that the block always RUNS (always
        // attempts the PalSet lookup), not that it always emits an entry.
        Assert.DoesNotContain(result.ObjDesc.SubPalettes, sp => sp.Offset == 0);
        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 0);
    }

    [Fact]
    public void TryCompose_SkinShadeSelected_EmitsSkinSubpaletteAtPackedOffsetZeroCountTwentyFour()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { SkinShade = 0.5 };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        ChargenSubPalette skin = Assert.Single(result.ObjDesc.SubPalettes, sp => sp.Offset == 0 && sp.NumColors == 24);
        Assert.Equal(0x0400_0011u, skin.SubPaletteId); // index 1 of 3 at shade 0.5.
    }

    [Fact]
    public void TryCompose_HairColorSelected_EmitsHairSubpaletteAtPackedOffsetTwentyFourCountEight()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairColor = 0u, HairShade = 1.0 };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        ChargenSubPalette hair = Assert.Single(result.ObjDesc.SubPalettes, sp => sp.Offset == 24 && sp.NumColors == 8);
        Assert.Equal(0x0400_0021u, hair.SubPaletteId); // last of the two at shade 1.0.
    }

    [Fact]
    public void TryCompose_EyeColorSelected_UsesRawPaletteIdDirectlyNoShadeIndirection()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { EyeColor = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        ChargenSubPalette eye = Assert.Single(result.ObjDesc.SubPalettes, sp => sp.Offset == 32 && sp.NumColors == 8);
        Assert.Equal(EyeColorPaletteId, eye.SubPaletteId);
    }

    [Fact]
    public void TryCompose_HairStyleSelected_AppendsHairObjDescAfterBase()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Equal(0u, (uint)result.ObjDesc.AnimPartChanges[0].PartIndex); // base first.
        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 1); // hair style second.
    }

    [Fact]
    public void TryCompose_HairStyleWithAlternateSetup_OverridesBodySetupId()
    {
        ChargenOptions options = MakeOptions(MakeGender(alternateHairSetup: AlternateBodySetupId));
        var (pal, clothing) = MakeSources(bodySetupId: AlternateBodySetupId);
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Equal(AlternateBodySetupId, result.SetupId);
    }

    [Fact]
    public void TryCompose_BothSetupSourcesZero_FallsBackToHumanSetupId()
    {
        ChargenGenderOptions gender = MakeGender() with { SetupId = 0u };
        ChargenOptions options = MakeOptions(gender);
        var (pal, clothing) = MakeSources(bodySetupId: 0u);

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, ChargenAppearanceSelection.Default,
            pal, clothing, out ChargenAppearanceResult result);

        Assert.Equal(ChargenAppearanceFactory.HumanSetupId, result.SetupId);
    }

    [Fact]
    public void TryCompose_HairStyleAlternateSetupIsInvalidDid_IsTreatedAsUnsetNotAdopted()
    {
        ChargenOptions options = MakeOptions(MakeGender(alternateHairSetup: 0xFFFFFFFFu));
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Equal(BodySetupId, result.SetupId); // gender.SetupId, NOT the INVALID_DID sentinel.
    }

    [Fact]
    public void TryCompose_GenderSetupIdIsInvalidDid_FallsBackToHumanSetupId()
    {
        ChargenGenderOptions gender = MakeGender() with { SetupId = 0xFFFFFFFFu };
        ChargenOptions options = MakeOptions(gender);
        var (pal, clothing) = MakeSources(bodySetupId: 0xFFFFFFFFu);

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, ChargenAppearanceSelection.Default,
            pal, clothing, out ChargenAppearanceResult result);

        Assert.Equal(ChargenAppearanceFactory.HumanSetupId, result.SetupId);
    }

    [Fact]
    public void TryCompose_AlternateSetupIdOverride_WinsOverHairStyleAlternateSetup()
    {
        const uint hairStyleSetup = 0x0200_00AAu;
        const uint pageLevelOverride = 0x0200_00BBu;
        ChargenOptions options = MakeOptions(MakeGender(alternateHairSetup: hairStyleSetup));
        var (pal, clothing) = MakeSources(bodySetupId: pageLevelOverride);
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result,
            alternateSetupIdOverride: pageLevelOverride);

        Assert.Equal(pageLevelOverride, result.SetupId);
    }

    [Fact]
    public void TryCompose_AlternateSetupIdOverride_WinsOverPlainGenderSetupId()
    {
        const uint pageLevelOverride = 0x0200_00CCu;
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources(bodySetupId: pageLevelOverride);

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, ChargenAppearanceSelection.Default, pal, clothing,
            out ChargenAppearanceResult result,
            alternateSetupIdOverride: pageLevelOverride);

        Assert.Equal(pageLevelOverride, result.SetupId);
    }

    [Fact]
    public void TryCompose_NoAlternateSetupIdOverrideSupplied_ResolvesAsBefore()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, ChargenAppearanceSelection.Default, pal, clothing,
            out ChargenAppearanceResult result);

        Assert.Equal(BodySetupId, result.SetupId);
    }

    [Fact]
    public void TryCompose_AlternateSetupIdOverrideIsInvalidDid_IsTreatedAsNoOverride()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, ChargenAppearanceSelection.Default, pal, clothing,
            out ChargenAppearanceResult result,
            alternateSetupIdOverride: 0xFFFFFFFFu);

        Assert.Equal(BodySetupId, result.SetupId);
    }

    [Fact]
    public void TryCompose_EyeStripSelected_UsesNonBaldObjDesc_WhenHairStyleIsNotBald()
    {
        ChargenOptions options = MakeOptions(MakeGender(baldHairStyle: false));
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 0u, EyesStrip = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        // tag 2 = non-bald eye ObjDesc, tag 20 = bald eye ObjDesc.
        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartId == 0x0100_0002u);
        Assert.DoesNotContain(result.ObjDesc.AnimPartChanges, c => c.PartId == 0x0100_0014u);
    }

    [Fact]
    public void TryCompose_EyeStripSelected_UsesBaldObjDesc_WhenHairStyleIsBald()
    {
        ChargenOptions options = MakeOptions(MakeGender(baldHairStyle: true));
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 0u, EyesStrip = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartId == 0x0100_0014u); // tag 20, bald.
        Assert.DoesNotContain(result.ObjDesc.AnimPartChanges, c => c.PartId == 0x0100_0002u); // tag 2, non-bald.
    }

    [Fact]
    public void TryCompose_NoseAndMouthStripsSelected_AppendBothObjDescs()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { NoseStrip = 0u, MouthStrip = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 3); // nose tag.
        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 4); // mouth tag.
    }

    [Fact]
    public void TryCompose_AllFourClothingSlotsSelected_AppearInRetailOrderHeadgearTrousersShirtFootwear()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with
        {
            HeadgearStyle = 0u,
            TrousersStyle = 0u,
            ShirtStyle = 0u,
            FootwearStyle = 0u,
        };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        uint[] expectedPartIds =
        [
            0x0100_0000u, // base body tag.
            0x0100_5000u + HeadgearClothingTableId,
            0x0100_5000u + TrousersClothingTableId,
            0x0100_5000u + ShirtClothingTableId,
            0x0100_5000u + FootwearClothingTableId,
        ];
        Assert.Equal(expectedPartIds, result.ObjDesc.AnimPartChanges.Select(c => c.PartId).ToArray());
    }

    [Fact]
    public void TryCompose_ClothingSlotWithColor_EmitsPartTextureAndDyeSubpalette()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with
        {
            HeadgearStyle = 0u,
            HeadgearColor = 0u,
            HeadgearShade = 0.0,
        };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartId == 0x0100_5000u + HeadgearClothingTableId);
        Assert.Contains(result.ObjDesc.TextureChanges, c => c.PartIndex == 5 && c.NewTextureId == 0x0500_6000u);
        // Real range (80, 16) packed by /8 => (10, 2).
        Assert.Contains(result.ObjDesc.SubPalettes, sp => sp.Offset == 10 && sp.NumColors == 2);
    }

    [Fact]
    public void TryCompose_ClothingSlotWithoutColor_SkipsDyeSubpaletteButKeepsPartTextureChanges()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HeadgearStyle = 0u };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Contains(result.ObjDesc.AnimPartChanges, c => c.PartId == 0x0100_5000u + HeadgearClothingTableId);
        Assert.DoesNotContain(result.ObjDesc.SubPalettes, sp => sp.Offset == 10 && sp.NumColors == 2);
    }

    [Fact]
    public void TryCompose_PalSetMissingMidLoop_AbortsRemainingChoicesInThatGarment()
    {
        const uint missingPalSetId = 0x0F00_00AAu;
        const uint presentPalSetId = 0x0F00_00BBu;

        var firstChoice = new ChargenClothingSubPaletteChoice(
            missingPalSetId, [new ChargenClothingSubPaletteRange(80u, 16u)]);
        var secondChoice = new ChargenClothingSubPaletteChoice(
            presentPalSetId, [new ChargenClothingSubPaletteRange(160u, 8u)]);
        var baseEffects = new Dictionary<uint, ChargenClothingBaseEffect>
        {
            [BodySetupId] = ChargenClothingBaseEffect.Empty,
        };
        var templates = new Dictionary<uint, ChargenClothingPaletteTemplate>
        {
            [7u] = new ChargenClothingPaletteTemplate([firstChoice, secondChoice]),
        };
        var table = new ChargenClothingTable(baseEffects, templates);

        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        clothing.Add(HeadgearClothingTableId, table);
        pal.Add(presentPalSetId, 0x0400_0055u); // deliberately NOT adding missingPalSetId.

        var selection = ChargenAppearanceSelection.Default with
        {
            HeadgearStyle = 0u,
            HeadgearColor = 0u,
            HeadgearShade = 0.0,
        };

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.True(ok);
        Assert.Contains(missingPalSetId, result.MissingPalSetIds);
        Assert.DoesNotContain(result.ObjDesc.SubPalettes, sp => sp.Offset == 20 && sp.NumColors == 1);
        Assert.DoesNotContain(result.ObjDesc.SubPalettes, sp => sp.Offset == 10 && sp.NumColors == 2);
    }

    [Fact]
    public void TryCompose_ClothingRangeNumColorsIsWholePaletteSentinel_PacksToZeroExplicitly()
    {
        var choice = new ChargenClothingSubPaletteChoice(
            0x0F00_0003u, [new ChargenClothingSubPaletteRange(0u, 2048u)]);
        var baseEffects = new Dictionary<uint, ChargenClothingBaseEffect>
        {
            [BodySetupId] = ChargenClothingBaseEffect.Empty,
        };
        var table = new ChargenClothingTable(
            baseEffects,
            new Dictionary<uint, ChargenClothingPaletteTemplate> { [7u] = new([choice]) });

        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        clothing.Add(HeadgearClothingTableId, table);

        var selection = ChargenAppearanceSelection.Default with
        {
            HeadgearStyle = 0u,
            HeadgearColor = 0u,
            HeadgearShade = 0.0,
        };

        ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.Contains(result.ObjDesc.SubPalettes, sp => sp.Offset == 0 && sp.NumColors == 0);
    }

    [Fact]
    public void TryCompose_ClothingRangeDoesNotFitThePackedByteConvention_Throws()
    {
        var choice = new ChargenClothingSubPaletteChoice(
            0x0F00_0003u, [new ChargenClothingSubPaletteRange(0u, 2041u)]); // not a multiple of 8, not 2048.
        var baseEffects = new Dictionary<uint, ChargenClothingBaseEffect>
        {
            [BodySetupId] = ChargenClothingBaseEffect.Empty,
        };
        var table = new ChargenClothingTable(
            baseEffects,
            new Dictionary<uint, ChargenClothingPaletteTemplate> { [7u] = new([choice]) });

        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        clothing.Add(HeadgearClothingTableId, table);

        var selection = ChargenAppearanceSelection.Default with
        {
            HeadgearStyle = 0u,
            HeadgearColor = 0u,
            HeadgearShade = 0.0,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChargenAppearanceFactory.TryCompose(
                options, HeritageId, GenderKey, selection, pal, clothing, out _));
    }

    [Fact]
    public void TryCompose_UnknownClothingTableId_IsRecordedAsMissingAndSkipped()
    {
        ChargenGenderOptions gender = MakeGender();
        gender = gender with
        {
            Headgears = [new ChargenGearOption("Missing", 0x1900_00FFu, 0x3000_0099u)],
        };
        ChargenOptions options = MakeOptions(gender);
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HeadgearStyle = 0u };

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.True(ok);
        Assert.Contains(0x1900_00FFu, result.MissingClothingTableIds);
        Assert.DoesNotContain(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 5);
    }

    [Fact]
    public void TryCompose_UnknownHairColorPalSetId_IsRecordedAsMissingAndSkipped()
    {
        ChargenGenderOptions gender = MakeGender() with { HairColors = [0x0F00_00FFu] };
        ChargenOptions options = MakeOptions(gender);
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairColor = 0u, HairShade = 0.5 };

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.True(ok);
        Assert.Contains(0x0F00_00FFu, result.MissingPalSetIds);
        Assert.DoesNotContain(result.ObjDesc.SubPalettes, sp => sp.Offset == 24);
    }

    [Fact]
    public void TryCompose_BodySetupAbsentFromClothingBaseEffects_IsRecordedButDoesNotThrow()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources(bodySetupId: 0x0200_DEADu); // different from the resolved body setup.
        var selection = ChargenAppearanceSelection.Default with { HeadgearStyle = 0u };

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.True(ok);
        Assert.Contains(HeadgearClothingTableId, result.ClothingTablesMissingBaseEffectForSetup);
        Assert.DoesNotContain(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 5);
    }

    [Fact]
    public void TryCompose_OutOfRangeStyleIndex_IsTreatedAsUnselected()
    {
        ChargenOptions options = MakeOptions(MakeGender());
        var (pal, clothing) = MakeSources();
        var selection = ChargenAppearanceSelection.Default with { HairStyle = 999u, EyesStrip = 999u };

        bool ok = ChargenAppearanceFactory.TryCompose(
            options, HeritageId, GenderKey, selection, pal, clothing, out ChargenAppearanceResult result);

        Assert.True(ok);
        Assert.DoesNotContain(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 1);
        Assert.DoesNotContain(result.ObjDesc.AnimPartChanges, c => c.PartIndex == 2);
    }
}
