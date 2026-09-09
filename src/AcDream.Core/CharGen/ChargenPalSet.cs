namespace AcDream.Core.CharGen;

public sealed record ChargenPalSet(IReadOnlyList<uint> PaletteIds)
{
    public static ChargenPalSet Empty { get; } = new(Array.Empty<uint>());
}
