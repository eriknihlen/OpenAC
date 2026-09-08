using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class VitalsSideBySideControllerTests
{
    [Fact]
    public void FirstTick_AppliesTheCurrentBit_BothDirections()
    {
        var (root, stacked, side) = Windows();
        bool bit = false;
        var controller = new VitalsSideBySideController(
            root, () => bit, WindowNames.Vitals, WindowNames.SideVitals);

        controller.Tick();
        Assert.True(stacked.Visible);
        Assert.False(side.Visible);
        Assert.False(controller.Applied!.Value);
    }

    [Fact]
    public void FirstTick_WithBitSet_ShowsTheSideRow()
    {
        var (root, stacked, side) = Windows();
        var controller = new VitalsSideBySideController(
            root, () => true, WindowNames.Vitals, WindowNames.SideVitals);

        controller.Tick();
        Assert.False(stacked.Visible);
        Assert.True(side.Visible);
    }

    [Fact]
    public void BitEdge_SwapsLive_BothDirections()
    {
        var (root, stacked, side) = Windows();
        bool bit = false;
        var controller = new VitalsSideBySideController(
            root, () => bit, WindowNames.Vitals, WindowNames.SideVitals);
        controller.Tick();

        bit = true;
        controller.Tick();
        Assert.False(stacked.Visible);
        Assert.True(side.Visible);

        bit = false;
        controller.Tick();
        Assert.True(stacked.Visible);
        Assert.False(side.Visible);
    }

    [Fact]
    public void SteadyBit_DoesNotReassertVisibility()
    {
        var (root, stacked, side) = Windows();
        var controller = new VitalsSideBySideController(
            root, () => false, WindowNames.Vitals, WindowNames.SideVitals);
        controller.Tick();

        // Simulate an out-of-band hide (e.g. a future toggle surface).
        stacked.Visible = false;
        controller.Tick();
        Assert.False(stacked.Visible);
    }

    private static (UiRoot Root, UiPanel Stacked, UiPanel Side) Windows()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var stacked = new UiPanel { Width = 160, Height = 58 };
        var side = new UiPanel { Width = 460, Height = 26, Visible = false };
        root.AddChild(stacked);
        root.AddChild(side);
        root.RegisterWindow(WindowNames.Vitals, stacked, stacked, null);
        root.RegisterWindow(WindowNames.SideVitals, side, side, null);
        return (root, stacked, side);
    }
}
