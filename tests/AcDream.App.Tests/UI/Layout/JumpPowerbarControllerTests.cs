using AcDream.App.Input;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class JumpPowerbarControllerTests
{
    private const uint JumpFill = 0x06001354u;

    [Fact]
    public void MovementCharge_ShowsUpdatesAndHidesAuthoredJumpMeter()
    {
        JumpChargeSnapshot state = default;
        ImportedLayout layout = BuildLayout();
        var visibility = new List<bool>();
        using var controller = JumpPowerbarController.Bind(
            layout, () => state, visibility.Add)!;
        var meter = Assert.IsType<UiMeter>(layout.FindElement(JumpPowerbarController.MeterId));

        Assert.Equal([false], visibility);
        Assert.Equal(0f, meter.Fill());
        Assert.Equal(JumpPowerbarController.JumpModeState, meter.ActiveRetailStateId);
        Assert.Equal(JumpFill, meter.FrontTile);

        state = new JumpChargeSnapshot(true, 0.35f);
        controller.Tick();
        Assert.Equal([false, true], visibility);
        Assert.Equal(0.35f, meter.Fill());

        state = new JumpChargeSnapshot(true, 1.4f);
        controller.Tick();
        Assert.Equal([false, true], visibility);
        Assert.Equal(1f, meter.Fill());

        state = default;
        controller.Tick();
        Assert.Equal([false, true, false], visibility);
        Assert.Equal(0f, meter.Fill());
    }

    [Fact]
    public void Bind_RejectsLayoutWithoutJumpModeMeter()
    {
        var root = new UiPanel();
        var meter = new UiMeter { ElementId = JumpPowerbarController.MeterId };
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [JumpPowerbarController.MeterId] = meter,
        });

        Assert.Null(JumpPowerbarController.Bind(layout, () => default, _ => { }));
    }

    private static ImportedLayout BuildLayout()
    {
        var root = new UiPanel { Width = 610, Height = 25 };
        var meter = new UiMeter
        {
            ElementId = JumpPowerbarController.MeterId,
            Left = 5,
            Top = 5,
            Width = 600,
            Height = 15,
        };
        meter.ConfigureStateFill(JumpPowerbarController.JumpModeState, JumpFill);
        root.AddChild(meter);
        return new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [JumpPowerbarController.MeterId] = meter,
        });
    }
}
