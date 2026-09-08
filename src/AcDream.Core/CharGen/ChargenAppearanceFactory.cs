namespace AcDream.Core.CharGen;

public sealed record ChargenAppearanceResult(
    uint SetupId,
    uint BasePaletteId,
    ChargenObjDesc ObjDesc,
    IReadOnlyList<uint> MissingPalSetIds,
    IReadOnlyList<uint> MissingClothingTableIds,
    IReadOnlyList<uint> ClothingTablesMissingBaseEffectForSetup);

public static class ChargenAppearanceFactory
{
    public const uint HumanSetupId = 0x02000001u;

    private const uint InvalidDid = 0xFFFFFFFFu;

    private const byte SkinRangeOffset = 0;
    private const byte SkinRangeNumColors = 24; // 192 / 8

    private const byte HairRangeOffset = 24;  // 192 / 8
    private const byte HairRangeNumColors = 8; // 64 / 8

    private const byte EyeRangeOffset = 32;   // 256 / 8
    private const byte EyeRangeNumColors = 8; // 64 / 8

    public static bool TryCompose(
        ChargenOptions options,
        uint heritageId,
        int genderKey,
        ChargenAppearanceSelection selection,
        IChargenPalSetSource palSets,
        IChargenClothingTableSource clothingTables,
        out ChargenAppearanceResult result,
        uint alternateSetupIdOverride = InvalidDid)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(palSets);
        ArgumentNullException.ThrowIfNull(clothingTables);

        result = default!;
        if (!options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage)
            || !heritage.GendersByKey.TryGetValue(genderKey, out ChargenGenderOptions? gender))
        {
            return false;
        }

        var missingPalSets = new List<uint>();
        var missingClothingTables = new List<uint>();
        var absentBaseEffects = new List<uint>();

        // ── 1. body Setup id ────────────────────────────────────────────
        uint setupId = gender.SetupId;
        ChargenHairStyle? hairStyle = null;
        if (selection.HairStyle != ChargenAppearanceSelection.Unset
            && selection.HairStyle < (uint)gender.HairStyles.Count)
        {
            hairStyle = gender.HairStyles[(int)selection.HairStyle];
            if (hairStyle.AlternateSetup != 0 && hairStyle.AlternateSetup != InvalidDid)
                setupId = hairStyle.AlternateSetup;
        }

        if (alternateSetupIdOverride != InvalidDid)
            setupId = alternateSetupIdOverride;

        if (setupId == 0 || setupId == InvalidDid)
            setupId = HumanSetupId;

        var subPalettes = new List<ChargenSubPalette>();
        var textureChanges = new List<ChargenTextureChange>();
        var animPartChanges = new List<ChargenAnimPartChange>();

        Append(gender.BaseObjDesc, subPalettes, textureChanges, animPartChanges);
        if (hairStyle is not null)
            Append(hairStyle.ObjDesc, subPalettes, textureChanges, animPartChanges);

        ComposeClothingSlot(
            gender.Headgears, selection.HeadgearStyle,
            gender.ClothingColors, selection.HeadgearColor, selection.HeadgearShade,
            setupId, clothingTables, palSets,
            subPalettes, textureChanges, animPartChanges,
            missingClothingTables, missingPalSets, absentBaseEffects);
        ComposeClothingSlot(
            gender.Pants, selection.TrousersStyle,
            gender.ClothingColors, selection.TrousersColor, selection.TrousersShade,
            setupId, clothingTables, palSets,
            subPalettes, textureChanges, animPartChanges,
            missingClothingTables, missingPalSets, absentBaseEffects);
        ComposeClothingSlot(
            gender.Shirts, selection.ShirtStyle,
            gender.ClothingColors, selection.ShirtColor, selection.ShirtShade,
            setupId, clothingTables, palSets,
            subPalettes, textureChanges, animPartChanges,
            missingClothingTables, missingPalSets, absentBaseEffects);
        ComposeClothingSlot(
            gender.Footwear, selection.FootwearStyle,
            gender.ClothingColors, selection.FootwearColor, selection.FootwearShade,
            setupId, clothingTables, palSets,
            subPalettes, textureChanges, animPartChanges,
            missingClothingTables, missingPalSets, absentBaseEffects);

        if (selection.EyesStrip != ChargenAppearanceSelection.Unset
            && selection.EyesStrip < (uint)gender.EyeStrips.Count)
        {
            ChargenEyeStrip strip = gender.EyeStrips[(int)selection.EyesStrip];
            bool bald = hairStyle?.Bald == true;
            Append(bald ? strip.BaldObjDesc : strip.ObjDesc, subPalettes, textureChanges, animPartChanges);
        }
        if (selection.NoseStrip != ChargenAppearanceSelection.Unset
            && selection.NoseStrip < (uint)gender.NoseStrips.Count)
        {
            Append(gender.NoseStrips[(int)selection.NoseStrip].ObjDesc, subPalettes, textureChanges, animPartChanges);
        }
        if (selection.MouthStrip != ChargenAppearanceSelection.Unset
            && selection.MouthStrip < (uint)gender.MouthStrips.Count)
        {
            Append(gender.MouthStrips[(int)selection.MouthStrip].ObjDesc, subPalettes, textureChanges, animPartChanges);
        }

        ChargenPalSet? skinPalSet = palSets.TryGetPalSet(gender.SkinPalSetId);
        if (skinPalSet is null)
        {
            missingPalSets.Add(gender.SkinPalSetId);
        }
        else
        {
            int skinIndex = ChargenPalSetMath.GetPaletteIndex(skinPalSet.PaletteIds.Count, selection.SkinShade);
            if (skinIndex >= 0)
            {
                subPalettes.Add(new ChargenSubPalette(
                    skinPalSet.PaletteIds[skinIndex], SkinRangeOffset, SkinRangeNumColors));
            }
        }

        if (selection.HairColor != ChargenAppearanceSelection.Unset
            && selection.HairColor < (uint)gender.HairColors.Count)
        {
            uint hairPalSetId = gender.HairColors[(int)selection.HairColor];
            ChargenPalSet? hairPalSet = palSets.TryGetPalSet(hairPalSetId);
            if (hairPalSet is null)
            {
                missingPalSets.Add(hairPalSetId);
            }
            else
            {
                int hairIndex = ChargenPalSetMath.GetPaletteIndex(hairPalSet.PaletteIds.Count, selection.HairShade);
                if (hairIndex >= 0)
                {
                    subPalettes.Add(new ChargenSubPalette(
                        hairPalSet.PaletteIds[hairIndex], HairRangeOffset, HairRangeNumColors));
                }
            }
        }

        if (selection.EyeColor != ChargenAppearanceSelection.Unset
            && selection.EyeColor < (uint)gender.EyeColors.Count)
        {
            uint eyePaletteId = gender.EyeColors[(int)selection.EyeColor];
            subPalettes.Add(new ChargenSubPalette(eyePaletteId, EyeRangeOffset, EyeRangeNumColors));
        }

        var objDesc = new ChargenObjDesc(
            gender.BasePaletteId,
            subPalettes.AsReadOnly(),
            textureChanges.AsReadOnly(),
            animPartChanges.AsReadOnly());

        result = new ChargenAppearanceResult(
            setupId,
            gender.BasePaletteId,
            objDesc,
            missingPalSets.AsReadOnly(),
            missingClothingTables.AsReadOnly(),
            absentBaseEffects.AsReadOnly());
        return true;
    }

    private static void Append(
        ChargenObjDesc source,
        List<ChargenSubPalette> subPalettes,
        List<ChargenTextureChange> textureChanges,
        List<ChargenAnimPartChange> animPartChanges)
    {
        subPalettes.AddRange(source.SubPalettes);
        textureChanges.AddRange(source.TextureChanges);
        animPartChanges.AddRange(source.AnimPartChanges);
    }

    private static void ComposeClothingSlot(
        IReadOnlyList<ChargenGearOption> gearOptions,
        uint styleIndex,
        IReadOnlyList<uint> clothingColors,
        uint colorIndex,
        double shade,
        uint bodySetupId,
        IChargenClothingTableSource clothingTables,
        IChargenPalSetSource palSets,
        List<ChargenSubPalette> subPalettes,
        List<ChargenTextureChange> textureChanges,
        List<ChargenAnimPartChange> animPartChanges,
        List<uint> missingClothingTables,
        List<uint> missingPalSets,
        List<uint> absentBaseEffects)
    {
        if (styleIndex == ChargenAppearanceSelection.Unset || styleIndex >= (uint)gearOptions.Count)
            return;

        ChargenGearOption gear = gearOptions[(int)styleIndex];
        ChargenClothingTable? table = clothingTables.TryGetClothingTable(gear.ClothingTableId);
        if (table is null)
        {
            missingClothingTables.Add(gear.ClothingTableId);
            return;
        }

        if (table.BaseEffectsBySetupId.TryGetValue(bodySetupId, out ChargenClothingBaseEffect? baseEffect))
        {
            animPartChanges.AddRange(baseEffect.PartChanges);
            textureChanges.AddRange(baseEffect.TextureChanges);
        }
        else
        {
            absentBaseEffects.Add(gear.ClothingTableId);
        }

        if (colorIndex == ChargenAppearanceSelection.Unset || colorIndex >= (uint)clothingColors.Count)
            return;

        uint paletteTemplateId = clothingColors[(int)colorIndex];
        if (!table.PaletteTemplatesById.TryGetValue(paletteTemplateId, out ChargenClothingPaletteTemplate? template))
            return;

        foreach (ChargenClothingSubPaletteChoice choice in template.Choices)
        {
            ChargenPalSet? palSet = palSets.TryGetPalSet(choice.PalSetId);
            if (palSet is null)
            {
                missingPalSets.Add(choice.PalSetId);
                break;
            }

            int index = ChargenPalSetMath.GetPaletteIndex(palSet.PaletteIds.Count, shade);
            if (index < 0)
                continue;

            uint paletteId = palSet.PaletteIds[index];
            foreach (ChargenClothingSubPaletteRange range in choice.Ranges)
            {
                subPalettes.Add(new ChargenSubPalette(
                    paletteId,
                    PackOffset(range.Offset),
                    PackNumColors(range.NumColors)));
            }
        }
    }

    private static byte PackOffset(uint realOffset)
    {
        if (realOffset % 8u != 0 || realOffset > 2040u)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realOffset),
                realOffset,
                "Clothing subpalette range offset does not fit the packed *8 byte "
                + "convention (expected a multiple of 8 in [0, 2040]).");
        }
        return (byte)(realOffset / 8u);
    }

    private static byte PackNumColors(uint realNumColors)
    {
        if (realNumColors == 2048u)
            return 0;
        if (realNumColors % 8u != 0 || realNumColors > 2040u)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realNumColors),
                realNumColors,
                "Clothing subpalette range color count does not fit the packed *8 byte "
                + "convention (expected a multiple of 8 in [0, 2040], or exactly 2048 "
                + "for the whole-palette sentinel).");
        }
        return (byte)(realNumColors / 8u);
    }
}
