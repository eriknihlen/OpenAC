namespace AcDream.Core.CharGen;

public readonly record struct ChargenSubPalette(uint SubPaletteId, byte Offset, byte NumColors);

public readonly record struct ChargenTextureChange(byte PartIndex, uint OldTextureId, uint NewTextureId);

public readonly record struct ChargenAnimPartChange(byte PartIndex, uint PartId);

public sealed record ChargenObjDesc(
    uint PaletteId,
    IReadOnlyList<ChargenSubPalette> SubPalettes,
    IReadOnlyList<ChargenTextureChange> TextureChanges,
    IReadOnlyList<ChargenAnimPartChange> AnimPartChanges)
{
    public static ChargenObjDesc Empty { get; } = new(
        0u,
        Array.Empty<ChargenSubPalette>(),
        Array.Empty<ChargenTextureChange>(),
        Array.Empty<ChargenAnimPartChange>());
}
