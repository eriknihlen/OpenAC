using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CombatLayoutConformanceTests
{
    [Theory]
    [InlineData("ID_CombatPanelOption_AutoRepeatAttack", 0x0718F89Bu)]
    [InlineData("ID_CombatPanelOption_AutoTarget", 0x0FB7B944u)]
    [InlineData("ID_CombatPanelOption_ViewCombatTarget", 0x0936C254u)]
    public void RetailStringHash_MatchesTheReferenceGolden(string value, uint expected)
        => Assert.Equal(expected, DatStringResolver.ComputeHash(value));

    [Fact]
    public void RetailFixture_UsesAuthoredScrollbarRolesAndCheckboxWidgets()
    {
        ElementInfo info = FixtureLoader.LoadCombatInfos();
        ImportedLayout layout = LayoutImporter.Build(
            info,
            _ => (0u, 0, 0),
            datFont: null,
            stringResolve: _ => "localized");
        ApplyAnchors(layout.Root);

        var power = Assert.IsType<UiScrollbar>(
            layout.FindElement(CombatUiController.PowerControlId));
        Assert.Equal(0x060074CAu, power.TrackSprite);
        Assert.Equal(0x06001923u, power.ThumbSprite);
        Assert.Equal(0x06001200u, power.ScalarFillSprite);
        Assert.Equal(0x0600715Eu, power.ScalarRangeSprite);
        Assert.NotNull(power.ScalarRangeLayoutPolicy);
        Assert.False(power.ScalarFillFromRight);

        var speed = Assert.IsType<UiText>(
            layout.FindElement(CombatUiController.SpeedLabelId));
        var powerLabel = Assert.IsType<UiText>(
            layout.FindElement(CombatUiController.PowerLabelId));
        Assert.Equal(
            (8f, 507f, 12f, 100f, 411f, 100f),
            (power.Left, power.Width, speed.Left, speed.Width,
                powerLabel.Left, powerLabel.Width));
        Assert.Equal((50f, 407f), power.ScalarRangeRect());

        var repeat = Assert.IsType<UiButton>(
            layout.FindElement(CombatUiController.RepeatAttacksId));
        Assert.Equal(13f, repeat.FaceWidth);
        Assert.Equal(13f, repeat.FaceHeight);
        Assert.Equal(17f, repeat.LabelOffsetX);
        Assert.True(repeat.ToggleBehavior);

        var high = Assert.IsType<UiButton>(
            layout.FindElement(CombatUiController.HighButtonId));
        Assert.Equal("localized", high.Label);
        Assert.IsType<UiText>(layout.FindElement(CombatUiController.SpeedLabelId));
    }

    private static void ApplyAnchors(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyAnchors(child);
        }
    }
}
