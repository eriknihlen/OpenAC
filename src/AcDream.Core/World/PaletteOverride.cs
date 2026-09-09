namespace AcDream.Core.World;

public sealed record PaletteOverride(
    uint BasePaletteId,
    IReadOnlyList<PaletteOverride.SubPaletteRange> SubPalettes)
{
    public readonly record struct SubPaletteRange(
        uint SubPaletteId,
        byte Offset,
        byte Length);
}
