using AcDream.App.UI;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiTextDatFontTests
{
    private static FontCharDesc Glyph(char c) => new()
    {
        Unicode = c, HorizontalOffsetBefore = 2, Width = 6, HorizontalOffsetAfter = 2,
        OffsetX = 0, OffsetY = 0, Height = 12, VerticalOffsetBefore = 0,
    };

    [Fact]
    public void CharIndexAt_UsesDatGlyphAdvance()
    {
        float Adv(char c) => UiDatFont.GlyphAdvance(Glyph(c));
        Assert.Equal(0, UiText.CharIndexAt("abc", Adv, 4f));
        Assert.Equal(1, UiText.CharIndexAt("abc", Adv, 12f));
        Assert.Equal(3, UiText.CharIndexAt("abc", Adv, 100f));
    }

    [Fact]
    public void GlyphAdvance_MatchesRetailFormula()
    {
        Assert.Equal(10f, UiDatFont.GlyphAdvance(Glyph('x')));
    }
}
