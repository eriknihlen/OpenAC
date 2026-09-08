using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailFpsControllerTests
{
    [Fact]
    public void AuthoredSmartBoxDisplay_FormatsUpdatesAndTogglesLikeRetail()
    {
        double fps = 59.994;
        double degrade = 0.875;
        bool visible = false;
        ImportedLayout layout = FixtureLoader.LoadFpsDisplay();
        ElementInfo info = FixtureLoader.LoadFpsDisplayInfos();

        Assert.Equal(0x4000001Au, info.FontDid);
        Assert.Equal(128, info.Width);
        Assert.Equal(36, info.Height);
        Assert.Equal(1u, info.Left);
        Assert.Equal(1u, info.Top);

        var controller = RetailFpsController.Bind(
            layout, () => fps, () => degrade, () => visible)!;

        UiText display = controller.Display;
        Assert.Equal(RetailFpsController.DisplayElementId, display.ElementId);
        Assert.Equal(128f, display.Width);
        Assert.Equal(36f, display.Height);
        Assert.Equal(1f, display.Left);
        Assert.Equal(1f, display.Top);
        Assert.False(display.Visible);

        Assert.Equal(
            ["FPS: 59.99", "DEG: 0.88"],
            display.LinesProvider().Select(line => line.Text));

        fps = 144.0;
        degrade = 1.0;
        visible = true;
        controller.Tick();

        Assert.True(display.Visible);
        Assert.Equal(
            ["FPS: 144.00", "DEG: 1.00"],
            display.LinesProvider().Select(line => line.Text));
    }

    [Fact]
    public void Bind_RejectsLayoutWithoutAuthoredTextElement()
    {
        var root = new UiPanel();
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>());

        Assert.Null(RetailFpsController.Bind(layout, () => 60, () => 1, () => true));
    }
}
