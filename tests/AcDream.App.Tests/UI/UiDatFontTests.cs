using System.Collections.Generic;
using AcDream.App.UI;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI;

public class UiDatFontTests
{
    private static FontCharDesc Glyph(
        ushort unicode, byte width,
        sbyte before = 0, sbyte after = 0,
        ushort offsetX = 0, ushort offsetY = 0, byte height = 16, sbyte vBefore = 0)
        => new()
        {
            Unicode = unicode,
            Width = width,
            Height = height,
            OffsetX = offsetX,
            OffsetY = offsetY,
            HorizontalOffsetBefore = before,
            HorizontalOffsetAfter = after,
            VerticalOffsetBefore = vBefore,
        };

    [Fact]
    public void GlyphAdvance_SumsBeforeWidthAfter()
    {
        var g = Glyph('A', width: 8, before: 1, after: 2);
        Assert.Equal(11f, UiDatFont.GlyphAdvance(g));
    }

    [Fact]
    public void GlyphAdvance_HandlesNegativeBearings()
    {
        // Kerned glyph: a negative left-bearing pulls it leftward; the advance
        // still nets out to before + width + after.
        var g = Glyph('j', width: 4, before: -1, after: 0);
        Assert.Equal(3f, UiDatFont.GlyphAdvance(g));
    }

    [Fact]
    public void MeasureWidth_SumsEachGlyphAdvance()
    {
        var table = new Dictionary<char, FontCharDesc>
        {
            ['2'] = Glyph('2', width: 7, before: 1, after: 1),  // advance 9
            ['9'] = Glyph('9', width: 7, before: 1, after: 1),  // advance 9
            ['1'] = Glyph('1', width: 3, before: 2, after: 1),  // advance 6
            ['/'] = Glyph('/', width: 4, before: 0, after: 1),  // advance 5
        };
        FontCharDesc? Lookup(char c) => table.TryGetValue(c, out var g) ? g : null;

        // "291/291" = 9 + 9 + 6 + 5 + 9 + 9 + 6 = 53
        Assert.Equal(53f, UiDatFont.MeasureWidth("291/291", Lookup));
    }

    [Fact]
    public void MeasureWidth_SkipsCharactersNotInFont()
    {
        var table = new Dictionary<char, FontCharDesc>
        {
            ['5'] = Glyph('5', width: 6, before: 1, after: 1),  // advance 8
        };
        FontCharDesc? Lookup(char c) => table.TryGetValue(c, out var g) ? g : null;

        Assert.Equal(16f, UiDatFont.MeasureWidth("5X5", Lookup));
    }

    [Fact]
    public void MeasureWidth_EmptyOrNullIsZero()
    {
        FontCharDesc? Lookup(char c) => null;
        Assert.Equal(0f, UiDatFont.MeasureWidth("", Lookup));
        Assert.Equal(0f, UiDatFont.MeasureWidth(null, Lookup));
    }

    [Fact]
    public void InstanceMeasureWidth_ReusesGlyphTableWithoutAllocating()
    {
        var glyphs = new Dictionary<char, FontCharDesc>
        {
            ['A'] = Glyph('A', width: 8, before: 1, after: 2),
            ['B'] = Glyph('B', width: 7, before: 1, after: 1),
        };
        var font = new UiDatFont(
            fgTex: 0,
            fgW: 0,
            fgH: 0,
            bgTex: 0,
            bgW: 0,
            bgH: 0,
            lineHeight: 16f,
            baselineOffset: 12f,
            glyphs);
        const string Text = "ABBA";
        float expected = font.MeasureWidth(Text);
        float actual = 0f;

        ZeroAllocationProbe.AssertAllocatesNothing(
            "UiDatFont.MeasureWidth",
            () => actual = font.MeasureWidth(Text));

        Assert.Equal(expected, actual);
    }
}
