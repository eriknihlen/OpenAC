using AcDream.Core.Combat;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public sealed class CombatUiController : IRetainedPanelController
{
    public sealed record Bindings(
        Func<CharacterOptionId, bool> CurrentValue,
        Action<CharacterOptionId, bool> SetOption);

    public const uint LayoutId = 0x21000073u;
    public const uint BasicPanelId = 0x1000005Cu;
    public const uint SpellcastingPanelId = 0x10000061u;
    public const uint AdvancedPanelId = SpellcastingPanelId;
    public const uint PowerControlId = 0x1000004Fu;
    public const uint SpeedLabelId = 0x10000051u;
    public const uint PowerLabelId = 0x10000052u;
    public const uint RepeatAttacksId = 0x10000053u;
    public const uint AutoTargetId = 0x10000054u;
    public const uint KeepInViewId = 0x10000055u;
    public const uint HighButtonId = 0x10000057u;
    public const uint MediumButtonId = 0x10000058u;
    public const uint LowButtonId = 0x10000059u;

    public const uint MeleeState = 0x10000003u;
    public const uint MissileState = 0x10000004u;

    private readonly UiElement _root;
    private readonly UiElement _basicPanel;
    private readonly UiElement _spellcastingPanel;
    private readonly UiScrollbar _powerControl;
    private readonly UiButton _high;
    private readonly UiButton _medium;
    private readonly UiButton _low;
    private readonly UiButton _repeatAttacks;
    private readonly UiButton _autoTarget;
    private readonly UiButton _keepInView;
    private readonly CombatState _combat;
    private readonly RuntimeCombatAttackState _attacks;
    private readonly Bindings _bindings;
    private readonly CombatUiLabels _labels;
    private readonly UiText? _powerLabel;
    private readonly Action<bool> _setWindowVisible;
    private bool _disposed;

    private CombatUiController(
        ImportedLayout layout,
        UiElement basicPanel,
        UiElement spellcastingPanel,
        UiScrollbar powerControl,
        UiButton high,
        UiButton medium,
        UiButton low,
        UiButton repeatAttacks,
        UiButton autoTarget,
        UiButton keepInView,
        CombatState combat,
        RuntimeCombatAttackState attacks,
        Bindings bindings,
        CombatUiLabels labels,
        Action<bool> setWindowVisible)
    {
        _root = layout.Root;
        _basicPanel = basicPanel;
        _spellcastingPanel = spellcastingPanel;
        _powerControl = powerControl;
        _high = high;
        _medium = medium;
        _low = low;
        _repeatAttacks = repeatAttacks;
        _autoTarget = autoTarget;
        _keepInView = keepInView;
        _combat = combat;
        _attacks = attacks;
        _bindings = bindings;
        _labels = labels;
        _setWindowVisible = setWindowVisible;

        _basicPanel.Visible = true;
        _spellcastingPanel.Visible = false;

        _powerControl.SetScalarPosition(_attacks.DesiredPower);
        _powerControl.ScalarChanged = _attacks.SetDesiredPower;
        _powerControl.ScalarFill = () => _attacks.PowerBarLevel;

        BindAttackButton(_high, AttackHeight.High);
        BindAttackButton(_medium, AttackHeight.Medium);
        BindAttackButton(_low, AttackHeight.Low);

        _high.Label = labels.High;
        _medium.Label = labels.Medium;
        _low.Label = labels.Low;
        _repeatAttacks.Label = labels.RepeatAttacks;
        _autoTarget.Label = labels.AutoTarget;
        _keepInView.Label = labels.KeepInView;
        UiText? speedLabel = layout.FindElement(SpeedLabelId) as UiText;
        _powerLabel = layout.FindElement(PowerLabelId) as UiText;
        SetStaticText(speedLabel, labels.Speed, rightAligned: false);
        SetStaticText(_powerLabel, labels.Power, rightAligned: true);

        _repeatAttacks.OnClick = () =>
            _bindings.SetOption(CharacterOptionId.AutoRepeatAttack, _repeatAttacks.Selected);
        _autoTarget.OnClick = () =>
            _bindings.SetOption(CharacterOptionId.AutoTarget, _autoTarget.Selected);
        _keepInView.OnClick = () =>
            _bindings.SetOption(CharacterOptionId.ViewCombatTarget, _keepInView.Selected);

        _combat.CombatModeChanged += OnCombatModeChanged;
        _attacks.StateChanged += OnAttackStateChanged;
        SyncControls();
    }

    public static CombatUiController? Bind(
        ImportedLayout layout,
        CombatState combat,
        RuntimeCombatAttackState attacks,
        Bindings bindings,
        CombatUiLabels labels,
        Action<bool> setWindowVisible)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(attacks);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(setWindowVisible);

        if (layout.FindElement(BasicPanelId) is not { } basic
            || layout.FindElement(SpellcastingPanelId) is not { } spellcasting
            || layout.FindElement(PowerControlId) is not UiScrollbar power
            || layout.FindElement(HighButtonId) is not UiButton high
            || layout.FindElement(MediumButtonId) is not UiButton medium
            || layout.FindElement(LowButtonId) is not UiButton low
            || layout.FindElement(RepeatAttacksId) is not UiButton repeatAttacks
            || layout.FindElement(AutoTargetId) is not UiButton autoTarget
            || layout.FindElement(KeepInViewId) is not UiButton keepInView)
            return null;

        return new CombatUiController(
            layout, basic, spellcasting, power, high, medium, low,
            repeatAttacks, autoTarget, keepInView,
            combat, attacks, bindings, labels, setWindowVisible);
    }

    public void SyncVisibility() => OnCombatModeChanged(_combat.CurrentMode);

    private void BindAttackButton(UiButton button, AttackHeight height)
    {
        button.OnPressed = () => _attacks.PressAttack(height);
        button.OnReleased = _attacks.ReleaseAttack;
    }

    private void OnCombatModeChanged(CombatMode mode)
    {
        bool visible = mode is CombatMode.Melee or CombatMode.Missile or CombatMode.Magic;
        _basicPanel.Visible = mode is CombatMode.Melee or CombatMode.Missile;
        _spellcastingPanel.Visible = mode == CombatMode.Magic;
        if (_basicPanel is IUiDatStateful stateful)
        {
            if (mode == CombatMode.Melee)
                stateful.TrySetRetailState(MeleeState);
            else if (mode == CombatMode.Missile)
                stateful.TrySetRetailState(MissileState);
        }
        SetStaticText(
            _powerLabel,
            mode == CombatMode.Missile ? _labels.Accuracy : _labels.Power,
            rightAligned: true);
        _setWindowVisible(visible);
        SyncControls();
    }

    private void OnAttackStateChanged() => SyncControls();

    public void OnServerOptionsSeeded() => SyncControls();

    private void SyncControls()
    {
        _powerControl.SetScalarPosition(_attacks.DesiredPower);
        _high.Selected = _attacks.RequestedHeight == AttackHeight.High;
        _medium.Selected = _attacks.RequestedHeight == AttackHeight.Medium;
        _low.Selected = _attacks.RequestedHeight == AttackHeight.Low;
        _repeatAttacks.Selected = _bindings.CurrentValue(CharacterOptionId.AutoRepeatAttack);
        _autoTarget.Selected = _bindings.CurrentValue(CharacterOptionId.AutoTarget);
        _keepInView.Selected = _bindings.CurrentValue(CharacterOptionId.ViewCombatTarget);
    }

    private static void SetStaticText(UiText? text, string value, bool rightAligned)
    {
        if (text is null) return;
        text.OneLine = true;
        text.Padding = 0f;
        text.Centered = false;
        text.RightAligned = rightAligned;
        UiText.Line[] line = [new UiText.Line(value, text.DefaultColor)];
        text.LinesProvider = () => line;
    }

    public void OnShown() => SyncControls();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _combat.CombatModeChanged -= OnCombatModeChanged;
        _attacks.StateChanged -= OnAttackStateChanged;
        _powerControl.ScalarChanged = null;
        _powerControl.ScalarFill = () => null;
        _high.OnPressed = null;
        _high.OnReleased = null;
        _medium.OnPressed = null;
        _medium.OnReleased = null;
        _low.OnPressed = null;
        _low.OnReleased = null;
        _repeatAttacks.OnClick = null;
        _autoTarget.OnClick = null;
        _keepInView.OnClick = null;
    }
}

public sealed record CombatUiLabels(
    string Speed,
    string Power,
    string Accuracy,
    string RepeatAttacks,
    string AutoTarget,
    string KeepInView,
    string High,
    string Medium,
    string Low)
{
    private const uint UiStringTable = 0x23000001u;

    public static CombatUiLabels Resolve(ElementInfo root, DatStringResolver strings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(strings);

        return new CombatUiLabels(
            ElementString(CombatUiController.SpeedLabelId, UiStateInfo.DirectStateId, "Speed"),
            ElementString(CombatUiController.PowerLabelId, CombatUiController.MeleeState, "Power"),
            ElementString(CombatUiController.PowerLabelId, CombatUiController.MissileState, "Accuracy"),
            RuntimeString("ID_CombatPanelOption_AutoRepeatAttack", "Repeat Attacks"),
            RuntimeString("ID_CombatPanelOption_AutoTarget", "Auto Target"),
            RuntimeString("ID_CombatPanelOption_ViewCombatTarget", "Keep in View"),
            ElementString(CombatUiController.HighButtonId, UiStateInfo.DirectStateId, "High"),
            ElementString(CombatUiController.MediumButtonId, UiStateInfo.DirectStateId, "Medium"),
            ElementString(CombatUiController.LowButtonId, UiStateInfo.DirectStateId, "Low"));

        string RuntimeString(string id, string fallback)
            => strings.Resolve(UiStringTable, DatStringResolver.ComputeHash(id)) ?? fallback;

        string ElementString(uint elementId, uint stateId, string fallback)
        {
            ElementInfo? element = Find(root, elementId);
            if (element is null
                || !element.TryGetEffectiveProperty(0x17u, out var property, stateId)
                || property.Kind != UiPropertyKind.StringInfo)
                return fallback;
            return strings.Resolve(property.StringInfoValue) ?? fallback;
        }
    }

    private static ElementInfo? Find(ElementInfo element, uint id)
    {
        if (element.Id == id) return element;
        foreach (ElementInfo child in element.Children)
            if (Find(child, id) is { } found)
                return found;
        return null;
    }
}
