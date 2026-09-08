using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RadarLayoutConformanceTests
{
    [Fact]
    public void RadarFixture_HasRetailRootAndAllStaticAssets()
    {
        var root = FixtureLoader.LoadRadarInfos();

        Assert.Equal(RadarController.RootId, root.Id);
        Assert.Equal(UiRadar.RetailClassId, root.Type);
        Assert.Equal(120f, root.Width);
        Assert.Equal(140f, root.Height);
        Assert.Equal(8, root.Children.Count);

        AssertElement(root, RadarController.CoordinateContainerId, 0, 120, 120, 18, 0x06004CC0u);
        AssertElement(root, RadarController.RadarDiscId, 0, 0, 120, 120, 0x06004CC1u);
        AssertElement(root, RadarController.NorthTokenId, 55, 1, 10, 9, 0x060011FBu);
        AssertElement(root, RadarController.EastTokenId, 110, 55, 10, 9, 0x06001938u);
        AssertElement(root, RadarController.SouthTokenId, 55, 110, 10, 9, 0x0600193Au);
        AssertElement(root, RadarController.WestTokenId, 0, 55, 10, 9, 0x0600193Cu);
        AssertElement(root, RadarController.DragButtonId, 87, 6, 27, 27, 0x060074C9u);

        var lockButton = Find(root, RadarController.LockButtonId)!;
        Assert.Equal(1u, lockButton.Type);
        Assert.Equal(0x060074B7u, lockButton.StateMedia["LockedUI"].File);
        Assert.Equal(0x060074B8u, lockButton.StateMedia["UnlockedUI"].File);
    }

    [Fact]
    public void RadarFixture_BuildsDedicatedWidgetAndPreservesStaticChildren()
    {
        var layout = FixtureLoader.LoadRadar();

        Assert.IsType<UiRadar>(layout.Root);
        Assert.Equal(8, layout.Root.Children.Count);
        var coordinates = Assert.IsType<UiText>(
            layout.FindElement(RadarController.CoordinateContainerId));
        Assert.Equal(0x06004CC0u, coordinates.BackgroundSprite);
        Assert.IsType<UiDatElement>(layout.FindElement(RadarController.RadarDiscId));
        Assert.IsType<UiButton>(layout.FindElement(RadarController.LockButtonId));
    }

    private static void AssertElement(
        ElementInfo root,
        uint id,
        float x,
        float y,
        float width,
        float height,
        uint sprite)
    {
        var element = Find(root, id)!;
        Assert.Equal(x, element.X);
        Assert.Equal(y, element.Y);
        Assert.Equal(width, element.Width);
        Assert.Equal(height, element.Height);
        Assert.Equal(sprite, element.StateMedia[""].File);
    }

    private static ElementInfo? Find(ElementInfo node, uint id)
    {
        if (node.Id == id)
            return node;
        foreach (var child in node.Children)
        {
            var found = Find(child, id);
            if (found is not null)
                return found;
        }

        return null;
    }
}
