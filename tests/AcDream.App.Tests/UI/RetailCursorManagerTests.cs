using AcDream.App.Rendering;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetailCursorManagerTests
{
    private static readonly UiCursorMedia WidgetA = new(0x06001001u, 1, 2);
    private static readonly UiCursorMedia WidgetB = new(0x06001002u, 3, 4);

    [Fact]
    public void InitialState_appliesGlobalDefaultThenWidgetOverride()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: false,
            previousGlobal: default,
            previousWidget: default,
            currentGlobal: RetailGlobalCursorKind.Default,
            currentWidget: WidgetA);

        Assert.Equal(new[] { RetailCursorLayer.Global, RetailCursorLayer.Widget }, plan);
    }

    [Fact]
    public void GlobalChange_reassertsDefaultWithoutReapplyingUnchangedWidget()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: true,
            previousGlobal: RetailGlobalCursorKind.Default,
            previousWidget: WidgetA,
            currentGlobal: RetailGlobalCursorKind.TargetPending,
            currentWidget: WidgetA);

        Assert.Equal(new[] { RetailCursorLayer.Global }, plan);
    }

    [Fact]
    public void LaterWidgetTransition_overridesUnchangedGlobalDefault()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: true,
            previousGlobal: RetailGlobalCursorKind.TargetPending,
            previousWidget: WidgetA,
            currentGlobal: RetailGlobalCursorKind.TargetPending,
            currentWidget: WidgetB);

        Assert.Equal(new[] { RetailCursorLayer.Widget }, plan);
    }

    [Fact]
    public void ClearingWidgetCursor_restoresGlobalDefault()
    {
        var plan = RetailCursorManager.PlanApplication(
            hasLayerState: true,
            previousGlobal: RetailGlobalCursorKind.TargetPending,
            previousWidget: WidgetA,
            currentGlobal: RetailGlobalCursorKind.TargetPending,
            currentWidget: default);

        Assert.Equal(new[] { RetailCursorLayer.Global }, plan);
    }
}
