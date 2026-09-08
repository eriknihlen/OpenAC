using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class ScrollbarSkinLiveDatTests
{
    private static string DatDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    [InstalledDatFact]
    public void VerticalBaseSkin_DesignatesUpArrowAsIncrement_WithThreeStateMedia()
    {
        using var dats = new DatCollection(
            DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100003Eu);
        Assert.NotNull(tree);

        ElementInfo bar = Assert.Single(Flatten(tree!), e => e.Id == 0x10000455u);
        Assert.True(bar.TryGetEffectiveProperty(0x77u, out UiPropertyValue inc));
        Assert.True(bar.TryGetEffectiveProperty(0x78u, out UiPropertyValue dec));
        Assert.Equal(0x10000072u, inc.UnsignedValue); // increment = UP arrow (authored Y=32)
        Assert.Equal(0x10000071u, dec.UnsignedValue); // decrement = DOWN arrow (authored Y=0)

        ElementInfo up = Assert.Single(bar.Children, c => c.Id == 0x10000072u);
        Assert.Equal(RetailScrollbarChrome.UpNormal, up.StateMedia["Normal"].File);
        Assert.Equal(RetailScrollbarChrome.UpRollover, up.StateMedia["Normal_rollover"].File);
        Assert.Equal(RetailScrollbarChrome.UpPressed, up.StateMedia["Normal_pressed"].File);
        ElementInfo down = Assert.Single(bar.Children, c => c.Id == 0x10000071u);
        Assert.Equal(RetailScrollbarChrome.DownNormal, down.StateMedia["Normal"].File);
        Assert.Equal(RetailScrollbarChrome.DownRollover, down.StateMedia["Normal_rollover"].File);
        Assert.Equal(RetailScrollbarChrome.DownPressed, down.StateMedia["Normal_pressed"].File);

        ElementInfo thumb = Assert.Single(bar.Children, c => c.Id == 1u);
        ElementInfo mid = Assert.Single(thumb.Children, c => c.Id == 0x10000365u);
        Assert.Equal(RetailScrollbarChrome.ThumbMidNormal, mid.StateMedia["Normal"].File);
        Assert.Equal(RetailScrollbarChrome.ThumbMidRollover, mid.StateMedia["Normal_rollover"].File);
        Assert.Equal(RetailScrollbarChrome.ThumbMidPressed, mid.StateMedia["Normal_pressed"].File);
    }

    private static IEnumerable<ElementInfo> Flatten(ElementInfo e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }
}
