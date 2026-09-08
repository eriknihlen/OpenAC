using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class ControlsIniTests
{
    [Fact]
    public void Parse_ReadsSectionTokens()
    {
        var ini = ControlsIni.Parse(
            "[title]\nheight=19\ncolor=#FFFFFFFF\nfont=font://Verdana-10-bold\n" +
            "[body]\nbgcolor=#00000000\ncolor_border=#FF4F657D\n");

        Assert.Equal("19", ini.Get("title", "height"));
        Assert.Equal("font://Verdana-10-bold", ini.Get("title", "font"));
        Assert.Null(ini.Get("title", "missing"));
        Assert.Null(ini.Get("nosuch", "height"));
    }

    [Fact]
    public void TryColor_ParsesAlphaFirstHex()
    {
        var ini = ControlsIni.Parse("[body]\ncolor_border=#FF4F657D\n");
        Assert.True(ini.TryColor("body", "color_border", out Vector4 c));
        Assert.Equal(0xFF / 255f, c.W, 5); // alpha
        Assert.Equal(0x4F / 255f, c.X, 5); // red
        Assert.Equal(0x65 / 255f, c.Y, 5); // green
        Assert.Equal(0x7D / 255f, c.Z, 5); // blue
    }

    [Fact]
    public void Load_MissingFileReturnsEmptyNotThrow()
    {
        var ini = ControlsIni.Load(@"Z:\does\not\exist\controls.ini");
        Assert.Null(ini.Get("title", "height")); // empty, no throw
    }
}
