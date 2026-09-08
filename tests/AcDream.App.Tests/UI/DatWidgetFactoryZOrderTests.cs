using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI;

public class DatWidgetFactoryZOrderTests
{
    private static (uint, int, int) NoChrome(uint id) => (0u, 0, 0);

    [Fact]
    public void ZOrder_HigherZLevel_DrawsBehind()
    {
        var backdrop = new ElementInfo { Type = 3, ReadOrder = 4, ZLevel = 100 };
        var panel    = new ElementInfo { Type = 3, ReadOrder = 1, ZLevel = 0 };

        var b = DatWidgetFactory.Create(backdrop, NoChrome, null);
        var p = DatWidgetFactory.Create(panel, NoChrome, null);

        Assert.True(b!.ZOrder < p!.ZOrder, $"backdrop ZOrder {b.ZOrder} should be < panel ZOrder {p.ZOrder}");
    }

    [Fact]
    public void ZOrder_AllZeroZLevel_EqualsReadOrder()
    {
        // Regression guard: a vitals-style window (every element ZLevel 0) keeps ZOrder == ReadOrder.
        var e = new ElementInfo { Type = 3, ReadOrder = 5, ZLevel = 0 };
        var w = DatWidgetFactory.Create(e, NoChrome, null);
        Assert.Equal(5, w!.ZOrder);
    }

    [Fact]
    public void ZOrder_SameZLevel_OrderedByReadOrder()
    {
        // Within one ZLevel, ReadOrder is the tiebreaker (higher ReadOrder draws on top).
        var lo = new ElementInfo { Type = 3, ReadOrder = 2, ZLevel = 2 };
        var hi = new ElementInfo { Type = 3, ReadOrder = 7, ZLevel = 2 };
        var l = DatWidgetFactory.Create(lo, NoChrome, null);
        var h = DatWidgetFactory.Create(hi, NoChrome, null);
        Assert.True(l!.ZOrder < h!.ZOrder);
    }
}
