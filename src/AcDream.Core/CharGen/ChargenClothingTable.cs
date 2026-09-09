using System.Collections.Frozen;

namespace AcDream.Core.CharGen;

public readonly record struct ChargenClothingSubPaletteRange(uint Offset, uint NumColors);

public readonly record struct ChargenClothingSubPaletteChoice(
    uint PalSetId,
    IReadOnlyList<ChargenClothingSubPaletteRange> Ranges);

public sealed record ChargenClothingPaletteTemplate(
    IReadOnlyList<ChargenClothingSubPaletteChoice> Choices)
{
    public static ChargenClothingPaletteTemplate Empty { get; } =
        new(Array.Empty<ChargenClothingSubPaletteChoice>());
}

public sealed record ChargenClothingBaseEffect(
    IReadOnlyList<ChargenAnimPartChange> PartChanges,
    IReadOnlyList<ChargenTextureChange> TextureChanges)
{
    public static ChargenClothingBaseEffect Empty { get; } = new(
        Array.Empty<ChargenAnimPartChange>(),
        Array.Empty<ChargenTextureChange>());
}

public sealed record ChargenClothingTable(
    IReadOnlyDictionary<uint, ChargenClothingBaseEffect> BaseEffectsBySetupId,
    IReadOnlyDictionary<uint, ChargenClothingPaletteTemplate> PaletteTemplatesById)
{
    public static ChargenClothingTable Empty { get; } = new(
        FrozenDictionary<uint, ChargenClothingBaseEffect>.Empty,
        FrozenDictionary<uint, ChargenClothingPaletteTemplate>.Empty);
}

public interface IChargenPalSetSource
{
    ChargenPalSet? TryGetPalSet(uint palSetId);
}

public interface IChargenClothingTableSource
{
    ChargenClothingTable? TryGetClothingTable(uint clothingTableId);
}
