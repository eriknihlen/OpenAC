using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class UiViewportFactoryTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    [Fact]
    public void Factory_builds_UiViewport_for_dat_type_0xD()
    {
        var info = new ElementInfo { Type = 0xD, Width = 200, Height = 300 };
        var widget = DatWidgetFactory.Create(info, NoTex, null);
        var viewport = Assert.IsType<UiViewport>(widget);
        Assert.True(viewport.ConsumesDatChildren);
    }

    [Fact]
    public void UiViewport_rect_set_from_ElementInfo()
    {
        var info = new ElementInfo { Type = 0xD, X = 10, Y = 20, Width = 180, Height = 240 };
        var widget = DatWidgetFactory.Create(info, NoTex, null)!;
        Assert.Equal(10f,  widget.Left);
        Assert.Equal(20f,  widget.Top);
        Assert.Equal(180f, widget.Width);
        Assert.Equal(240f, widget.Height);
    }
}
