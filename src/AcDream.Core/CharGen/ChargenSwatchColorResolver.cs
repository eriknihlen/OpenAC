namespace AcDream.Core.CharGen;

public static class ChargenSwatchColorResolver
{
    public const int HairSampleIndex = 0xd0;

    public const int SkinFamilySampleIndex = 0xb0;

    public const int EyeSampleIndex = 0x103;

    public const int ClothingSampleIndex = 0x520;

    public static bool TryGetPalSetAverageColor(
        IChargenPalSetSource palSets,
        IChargenPaletteColorSource colors,
        uint palSetId,
        int sampleIndex,
        out ChargenSwatchRgb color)
    {
        ArgumentNullException.ThrowIfNull(palSets);
        ArgumentNullException.ThrowIfNull(colors);

        color = default;
        ChargenPalSet? palSet = palSets.TryGetPalSet(palSetId);
        if (palSet is null)
            return false;

        if (palSet.PaletteIds.Count == 0)
            return true;

        int sumR = 0, sumG = 0, sumB = 0;
        foreach (uint paletteId in palSet.PaletteIds)
        {
            if (colors.TryGetColor(paletteId, sampleIndex, out ChargenSwatchRgb c))
            {
                sumR += c.R;
                sumG += c.G;
                sumB += c.B;
            }
        }

        int count = palSet.PaletteIds.Count;
        color = new ChargenSwatchRgb((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
        return true;
    }

    public static bool TryGetDirectColor(
        IChargenPaletteColorSource colors,
        uint paletteId,
        int sampleIndex,
        out ChargenSwatchRgb color)
    {
        ArgumentNullException.ThrowIfNull(colors);
        return colors.TryGetColor(paletteId, sampleIndex, out color);
    }

    public static bool TryGetClothingSwatchPalSetId(
        IChargenClothingTableSource clothingTables,
        uint clothingTableId,
        uint paletteTemplateId,
        out uint palSetId)
    {
        ArgumentNullException.ThrowIfNull(clothingTables);

        palSetId = 0;
        ChargenClothingTable? table = clothingTables.TryGetClothingTable(clothingTableId);
        if (table is null)
            return false;
        if (!table.PaletteTemplatesById.TryGetValue(paletteTemplateId, out ChargenClothingPaletteTemplate? template))
            return false;
        if (template.Choices.Count == 0)
            return false;

        palSetId = template.Choices[0].PalSetId;
        return true;
    }
}
