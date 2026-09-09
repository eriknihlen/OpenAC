using AcDream.App.Combat;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Combat;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CombatUiControllerTests
{
    private static (uint, int, int) NoTex(uint _) => (0u, 0, 0);

    private sealed class FakeOptionBindings
    {
        public Dictionary<CharacterOptionId, bool> Values { get; } = new();

        public CombatUiController.Bindings ToBindings() => new(
            CurrentValue: id => Values.TryGetValue(id, out bool v) && v,
            SetOption: (id, value) => Values[id] = value);
    }

    [Fact]
    public void CombatMode_ShowsPhysicalAndMagicPages_AndSelectsMediumByDefault()
    {
        double now = 0d;
        var combat = new CombatState();
        using var attacks = CreateAttacks(combat, () => now, []);
        var (layout, basic, spellcasting, power, high, medium, low) = BuildLayout();
        var visibility = new List<bool>();
        var options = new FakeOptionBindings();
        using var controller = CombatUiController.Bind(
            layout, combat, attacks, options.ToBindings(),
            Labels, visibility.Add)!;

        controller.SyncVisibility();
        combat.SetCombatMode(CombatMode.Melee);
        combat.SetCombatMode(CombatMode.Magic);

        Assert.Equal([false, true, true], visibility);
        Assert.False(basic.Visible);
        Assert.True(spellcasting.Visible);
        Assert.False(high.Selected);
        Assert.True(medium.Selected);
        Assert.False(low.Selected);
        Assert.Equal(RuntimeCombatAttackState.InitialDesiredPower, power.ScalarPosition);
    }

    [Fact]
    public void AuthoredHeightButton_PressChargesAndReleaseSendsThroughSharedController()
    {
        double now = 4d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var attacks = CreateAttacks(combat, () => now, sent);
        var (layout, basic, advanced, power, high, _, _) = BuildLayout();
        var options = new FakeOptionBindings();
        using var controller = CombatUiController.Bind(
            layout, combat, attacks, options.ToBindings(),
            Labels, _ => { })!;
        combat.SetCombatMode(CombatMode.Melee);

        high.OnEvent(new UiEvent(0, high, UiEventType.MouseDown, Data1: 2, Data2: 2));
        now = 4.5d;
        Assert.Equal(0.5f, power.ScalarFill()!.Value, 3);
        high.OnEvent(new UiEvent(0, high, UiEventType.MouseUp, Data1: 2, Data2: 2));

        var attack = Assert.Single(sent);
        Assert.Equal(AttackHeight.High, attack.Height);
        Assert.Equal(0.5f, attack.Power, 3);
        Assert.True(basic.Visible);
        Assert.False(advanced.Visible);
    }

    [Fact]
    public void AuthoredOptionCheckbox_TogglesPersistedGameplayValue()
    {
        var combat = new CombatState();
        using var attacks = CreateAttacks(combat, () => 0d, []);
        var (layout, _, _, _, _, _, _) = BuildLayout();
        var options = new FakeOptionBindings();
        options.Values[CharacterOptionId.AutoTarget] = true;
        using var controller = CombatUiController.Bind(
            layout, combat, attacks, options.ToBindings(),
            Labels, _ => { })!;
        var autoTarget = Assert.IsType<UiButton>(layout.FindElement(CombatUiController.AutoTargetId));

        autoTarget.OnEvent(new UiEvent(0, autoTarget, UiEventType.MouseDown, Data1: 3, Data2: 3));
        autoTarget.OnEvent(new UiEvent(0, autoTarget, UiEventType.MouseUp, Data1: 3, Data2: 3));
        autoTarget.OnEvent(new UiEvent(0, autoTarget, UiEventType.Click, Data1: 3, Data2: 3));

        Assert.False(options.Values[CharacterOptionId.AutoTarget]);
    }

    [Fact]
    public void AuthoredPowerLabels_KeepGrayTrackAtSides_AndUseRetailAlignment()
    {
        var combat = new CombatState();
        using var attacks = CreateAttacks(combat, () => 0d, []);
        var (layout, _, _, power, _, _, _) = BuildLayout();
        var options = new FakeOptionBindings();
        using var controller = CombatUiController.Bind(
            layout, combat, attacks, options.ToBindings(),
            Labels, _ => { })!;

        var speed = Assert.IsType<UiText>(layout.FindElement(CombatUiController.SpeedLabelId));
        var powerLabel = Assert.IsType<UiText>(layout.FindElement(CombatUiController.PowerLabelId));
        Assert.False(speed.Centered);
        Assert.False(speed.RightAligned);
        Assert.False(powerLabel.Centered);
        Assert.True(powerLabel.RightAligned);
    }

    [Fact]
    public void PowerLabel_SwitchesToAccuracyInMissileMode_AndBack()
    {
        var combat = new CombatState();
        using var attacks = CreateAttacks(combat, () => 0d, []);
        var (layout, _, _, _, _, _, _) = BuildLayout();
        var options = new FakeOptionBindings();
        using var controller = CombatUiController.Bind(
            layout, combat, attacks, options.ToBindings(),
            Labels, _ => { })!;
        var powerLabel = Assert.IsType<UiText>(
            layout.FindElement(CombatUiController.PowerLabelId));

        Assert.Equal("Power", powerLabel.LinesProvider!()[0].Text);

        combat.SetCombatMode(CombatMode.Missile);
        Assert.Equal("Accuracy", powerLabel.LinesProvider!()[0].Text);

        combat.SetCombatMode(CombatMode.Melee);
        Assert.Equal("Power", powerLabel.LinesProvider!()[0].Text);
    }

    [Fact]
    public void ImportedFixture_ReflowUpdatesCenteredDarkRange()
    {
        ElementInfo info = FixtureLoader.LoadCombatInfos();
        ImportedLayout layout = LayoutImporter.Build(info, NoTex, datFont: null);
        var combat = new CombatState();
        using var attacks = CreateAttacks(combat, () => 0d, []);
        var options = new FakeOptionBindings();
        using var controller = CombatUiController.Bind(
            layout, combat, attacks, options.ToBindings(),
            Labels, _ => { })!;

        ApplyAnchors(layout.Root);

        var power = Assert.IsType<UiScrollbar>(
            layout.FindElement(CombatUiController.PowerControlId));
        Assert.Equal((50f, 407f), power.ScalarRangeRect());
    }

    private static readonly CombatUiLabels Labels = new(
        "Speed", "Power", "Accuracy", "Repeat Attacks", "Auto Target",
        "Keep in View", "High", "Medium", "Low");

    private static RuntimeCombatAttackState CreateAttacks(
        CombatState combat,
        Func<double> now,
        List<(AttackHeight Height, float Power)> sent)
        => new(
            combat,
            () => true,
            (height, power) =>
            {
                sent.Add((height, power));
                return true;
            },
            autoRepeatAttack: () => false,
            now: now);

    private static (
        ImportedLayout Layout,
        UiElement Basic,
        UiElement Advanced,
        UiScrollbar Power,
        UiButton High,
        UiButton Medium,
        UiButton Low) BuildLayout()
    {
        var root = new UiPanel { Width = 610, Height = 90 };
        var basic = new UiPanel();
        var advanced = new UiPanel();
        var power = new UiScrollbar
        {
            Left = 13,
            Top = 14,
            Width = 507,
            Height = 14,
            Horizontal = true,
        };
        var speedLabel = new UiText { Left = 17, Top = 14, Width = 100, Height = 15 };
        var powerLabel = new UiText { Left = 416, Top = 14, Width = 100, Height = 15 };
        UiButton Button(uint id) => new(
            new ElementInfo { Id = id, Type = 1, Width = 72, Height = 19 }, NoTex)
        {
            Width = 72,
            Height = 19,
        };
        UiButton ToggleButton(uint id)
        {
            var info = new ElementInfo { Id = id, Type = 0x10000035u, Width = 100, Height = 14 };
            var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
            state.Properties.Values[0x0Bu] = new UiPropertyValue
                { Kind = UiPropertyKind.Bool, BoolValue = true };
            info.States[UiStateInfo.DirectStateId] = state;
            return new UiButton(info, NoTex) { Width = 100, Height = 14 };
        }
        var high = Button(CombatUiController.HighButtonId);
        var medium = Button(CombatUiController.MediumButtonId);
        var low = Button(CombatUiController.LowButtonId);
        var repeat = ToggleButton(CombatUiController.RepeatAttacksId);
        var autoTarget = ToggleButton(CombatUiController.AutoTargetId);
        var keepInView = ToggleButton(CombatUiController.KeepInViewId);
        var byId = new Dictionary<uint, UiElement>
        {
            [CombatUiController.BasicPanelId] = basic,
            [CombatUiController.AdvancedPanelId] = advanced,
            [CombatUiController.PowerControlId] = power,
            [CombatUiController.SpeedLabelId] = speedLabel,
            [CombatUiController.PowerLabelId] = powerLabel,
            [CombatUiController.HighButtonId] = high,
            [CombatUiController.MediumButtonId] = medium,
            [CombatUiController.LowButtonId] = low,
            [CombatUiController.RepeatAttacksId] = repeat,
            [CombatUiController.AutoTargetId] = autoTarget,
            [CombatUiController.KeepInViewId] = keepInView,
        };
        return (new ImportedLayout(root, byId), basic, advanced, power, high, medium, low);
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
