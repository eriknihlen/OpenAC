using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class JumpPowerbarLayoutConformanceTests
{
    [Fact]
    public void RetailFixture_UsesExactFloatyGeometryTrackAndJumpFill()
    {
        ElementInfo info = FixtureLoader.LoadPowerbarInfos();
        ImportedLayout layout = LayoutImporter.Build(info, _ => (0u, 0, 0), null);
        var meter = Assert.IsType<UiMeter>(
            layout.FindElement(JumpPowerbarController.MeterId));

        Assert.Equal((610f, 25f), (layout.Root.Width, layout.Root.Height));
        Assert.Equal((5f, 5f, 600f, 15f),
            (meter.Left, meter.Top, meter.Width, meter.Height));
        Assert.Equal(0x06004D0Bu, meter.BackTile);
        Assert.True(meter.TrySetRetailState(JumpPowerbarController.JumpModeState));
        Assert.Equal(0x06001354u, meter.FrontTile);

        Assert.Equal((0f, 0f, 300f, 15f),
            UiMeter.ComputeFillRect(0.5f, meter.Width, meter.Height));

        Assert.True(info.TryGetEffectiveInteger(0x3Fu, out int minWidth));
        Assert.True(info.TryGetEffectiveInteger(0x3Du, out int maxWidth));
        Assert.True(info.TryGetEffectiveInteger(0x3Eu, out int minHeight));
        Assert.True(info.TryGetEffectiveInteger(0x3Cu, out int maxHeight));
        Assert.Equal((50, 810, 25, 25),
            (minWidth, maxWidth, minHeight, maxHeight));
    }
}
