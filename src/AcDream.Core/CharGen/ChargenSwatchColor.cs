namespace AcDream.Core.CharGen;

public readonly record struct ChargenSwatchRgb(byte R, byte G, byte B);

public interface IChargenPaletteColorSource
{
    bool TryGetColor(uint paletteId, int index, out ChargenSwatchRgb color);
}
