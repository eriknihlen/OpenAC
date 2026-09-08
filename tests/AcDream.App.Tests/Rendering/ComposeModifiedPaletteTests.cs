using AcDream.App.Rendering;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

public sealed class ComposeModifiedPaletteTests
{
    private const int PaletteSize = 32;

    private static Palette Ramp(byte channel)
    {
        var palette = new Palette();
        for (int i = 0; i < PaletteSize; i++)
        {
            palette.Colors.Add(new ColorARGB
            {
                Alpha = 0xFF,
                Red = channel,
                Green = 0,
                Blue = (byte)i,
            });
        }

        return palette;
    }

    private static void AssertEntry(Palette palette, int index, byte channel, int sourceIndex)
    {
        ColorARGB color = palette.Colors[index];
        Assert.Equal(channel, color.Red);
        Assert.Equal((byte)sourceIndex, color.Blue);
    }

    [Fact]
    public void OverlappingRangesApplyInOrderAtEightColorGranularity()
    {
        Palette basePalette = Ramp(0x10);
        Palette first = Ramp(0x20);
        Palette second = Ramp(0x30);

        var ranges = new[]
        {
            new PaletteOverride.SubPaletteRange(1, Offset: 1, Length: 1),
            new PaletteOverride.SubPaletteRange(2, Offset: 1, Length: 2),
        };

        Palette composed = TextureCache.ComposeModifiedPalette(
            basePalette,
            ranges,
            id => id == 1 ? first : id == 2 ? second : null);

        Assert.Equal(PaletteSize, composed.Colors.Count);
        for (int i = 0; i < 8; i++)
            AssertEntry(composed, i, channel: 0x10, sourceIndex: i);
        for (int i = 8; i < 24; i++)
            AssertEntry(composed, i, channel: 0x30, sourceIndex: i);
        for (int i = 24; i < PaletteSize; i++)
            AssertEntry(composed, i, channel: 0x10, sourceIndex: i);

        for (int i = 0; i < PaletteSize; i++)
            AssertEntry(basePalette, i, channel: 0x10, sourceIndex: i);
    }

    [Fact]
    public void ZeroLengthByteMeansTwoThousandFortyEightColors()
    {
        Palette basePalette = Ramp(0x10);
        Palette whole = Ramp(0x20);

        var ranges = new[] { new PaletteOverride.SubPaletteRange(1, Offset: 0, Length: 0) };

        Palette composed = TextureCache.ComposeModifiedPalette(
            basePalette, ranges, _ => whole);

        for (int i = 0; i < PaletteSize; i++)
            AssertEntry(composed, i, channel: 0x20, sourceIndex: i);
    }

    [Fact]
    public void RangeIsClampedToTheShorterOfDestinationAndSource()
    {
        Palette basePalette = Ramp(0x10);
        var shortSource = new Palette();
        for (int i = 0; i < 12; i++)
        {
            shortSource.Colors.Add(new ColorARGB
            {
                Alpha = 0xFF,
                Red = 0x20,
                Green = 0,
                Blue = (byte)i,
            });
        }

        var ranges = new[] { new PaletteOverride.SubPaletteRange(1, Offset: 1, Length: 2) };

        Palette composed = TextureCache.ComposeModifiedPalette(
            basePalette, ranges, _ => shortSource);

        for (int i = 8; i < 12; i++)
            AssertEntry(composed, i, channel: 0x20, sourceIndex: i);
        for (int i = 12; i < PaletteSize; i++)
            AssertEntry(composed, i, channel: 0x10, sourceIndex: i);
    }

    [Fact]
    public void UnresolvableSubPaletteSkipsOnlyItsOwnRange()
    {
        Palette basePalette = Ramp(0x10);
        Palette resolved = Ramp(0x20);

        var ranges = new[]
        {
            new PaletteOverride.SubPaletteRange(1, Offset: 0, Length: 1),  // missing
            new PaletteOverride.SubPaletteRange(2, Offset: 1, Length: 1),  // present
        };

        Palette composed = TextureCache.ComposeModifiedPalette(
            basePalette, ranges, id => id == 2 ? resolved : null);

        for (int i = 0; i < 8; i++)
            AssertEntry(composed, i, channel: 0x10, sourceIndex: i);
        for (int i = 8; i < 16; i++)
            AssertEntry(composed, i, channel: 0x20, sourceIndex: i);
    }

    [Fact]
    public void EmptyRangeListReturnsACopyOfTheBase()
    {
        Palette basePalette = Ramp(0x10);

        Palette composed = TextureCache.ComposeModifiedPalette(
            basePalette,
            Array.Empty<PaletteOverride.SubPaletteRange>(),
            _ => null);

        Assert.NotSame(basePalette, composed);
        Assert.Equal(PaletteSize, composed.Colors.Count);
        for (int i = 0; i < PaletteSize; i++)
            AssertEntry(composed, i, channel: 0x10, sourceIndex: i);
    }
}
