using System;
using System.Linq;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public sealed class IndicatorBarController : IRetainedPanelController
{
    public const uint LayoutId = 0x21000071u;

    public const uint BurdenClassId = 0x10000001u;
    public const uint EffectsClassId = 0x10000002u;
    public const uint LinkClassId = 0x10000003u;
    public const uint MiniGameClassId = 0x10000004u;
    public const uint VitaeClassId = 0x10000006u;

    public const uint MiniGameButtonId = 0x100000F3u;
    public const uint VitaeButtonId = 0x100000F4u;
    public const uint HelpfulButtonId = 0x100000F5u;
    public const uint HarmfulButtonId = 0x100000F6u;
    public const uint BurdenButtonId = 0x100000F7u;
    public const uint LinkButtonId = 0x100000F8u;
    public const uint EndCharacterSessionButtonId = 0x100000FAu;

    public const uint PressHighlightId = 0x100000F2u;

    public const uint UnencumberedState = 14u;
    public const uint EncumberedState = 15u;
    public const uint HeavilyEncumberedState = 16u;
    public const uint ConnectionGoodState = 17u;
    public const uint ConnectionUncertainState = 18u;
    public const uint ConnectionBadState = 19u;
    public const uint ConnectionDisconnectedState = 20u;

    private const uint EncumbranceValProperty = 5u;
    private const uint EncumbranceAugProperty = 0xE6u;
    private const double LinkUpdateSeconds = 4.0;
    private const double LinkFlashSeconds = 0.75;

    private readonly IndicatorBarBindings _bindings;
    private readonly UiButton _link;
    private readonly UiButton _helpful;
    private readonly UiButton _harmful;
    private readonly UiButton _vitae;
    private readonly UiButton _burden;
    private readonly UiButton _miniGame;
    private readonly UiButton _endCharacterSession;
    private LinkSemanticState _linkState = LinkSemanticState.Good;
    private double _lastLinkUpdate;
    private double _lastLinkFlash;
    private bool _disposed;

    private enum LinkSemanticState
    {
        Good = 1,
        Uncertain = 2,
        Bad = 3,
        Disconnected = 4,
    }

    public void AttachPressHighlights(
        ElementInfo layoutRoot, Func<ElementInfo, UiElement?> build)
    {
        foreach ((UiButton button, uint id) in Indicators())
        {
            if (FindInfo(layoutRoot, id) is not { } info)
                continue;
            foreach (ElementInfo child in info.Children)
            {
                if (child.Id != PressHighlightId)
                    continue;
                if (build(child) is { } highlight)
                    button.AddChild(highlight);
            }
        }
    }

    private (UiButton Button, uint Id)[] Indicators() =>
    [
        (_link, LinkButtonId),
        (_helpful, HelpfulButtonId),
        (_harmful, HarmfulButtonId),
        (_vitae, VitaeButtonId),
        (_burden, BurdenButtonId),
        (_miniGame, MiniGameButtonId),
        (_endCharacterSession, EndCharacterSessionButtonId),
    ];

    private static ElementInfo? FindInfo(ElementInfo root, uint id)
    {
        if (root.Id == id)
            return root;
        foreach (ElementInfo child in root.Children)
            if (FindInfo(child, id) is { } found)
                return found;
        return null;
    }

    private IndicatorBarController(
        IndicatorBarBindings bindings,
        UiButton link,
        UiButton helpful,
        UiButton harmful,
        UiButton vitae,
        UiButton burden,
        UiButton miniGame,
        UiButton endCharacterSession)
    {
        _bindings = bindings;
        _link = link;
        _helpful = helpful;
        _harmful = harmful;
        _vitae = vitae;
        _burden = burden;
        _miniGame = miniGame;
        _endCharacterSession = endCharacterSession;

        _helpful.OnClick = () => bindings.TogglePanel(RetailPanelCatalog.PositiveEffects);
        _harmful.OnClick = () => bindings.TogglePanel(RetailPanelCatalog.NegativeEffects);
        _link.OnClick = () => bindings.TogglePanel(RetailPanelCatalog.LinkStatus);
        _vitae.OnClick = () => bindings.TogglePanel(RetailPanelCatalog.Vitae);
        _burden.OnClick = () => bindings.TogglePanel(RetailPanelCatalog.CharacterInformation);
        _miniGame.OnClick = () => bindings.TogglePanel(RetailPanelCatalog.MiniGame);
        _endCharacterSession.OnClick = bindings.RequestEndCharacterSession;

        bindings.Spellbook.EnchantmentsChanged += OnEnchantmentsChanged;
        bindings.Objects.ObjectAdded += OnObjectChanged;
        bindings.Objects.ObjectUpdated += OnObjectChanged;
        bindings.Objects.ObjectRemoved += OnObjectChanged;
        bindings.Objects.ObjectMoved += OnObjectMoved;
        bindings.Objects.ContainerContentsReplaced += OnContainerContentsReplaced;
        bindings.Objects.Cleared += OnObjectsCleared;

        double now = bindings.CurrentTime();
        _lastLinkUpdate = now;
        _lastLinkFlash = now;
        _link.TrySetRetailState(ConnectionGoodState);
        _miniGame.TrySetRetailState(UiButtonStateMachine.Ghosted);
        _endCharacterSession.TrySetRetailState(UiButtonStateMachine.Normal);
        UpdateEnchantments();
        UpdateBurden();
    }

    public static IndicatorBarController? Bind(
        ImportedLayout layout,
        IndicatorBarBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bindings);

        if (layout.FindElement(LinkButtonId) is not UiButton link
            || layout.FindElement(HelpfulButtonId) is not UiButton helpful
            || layout.FindElement(HarmfulButtonId) is not UiButton harmful
            || layout.FindElement(VitaeButtonId) is not UiButton vitae
            || layout.FindElement(BurdenButtonId) is not UiButton burden
            || layout.FindElement(MiniGameButtonId) is not UiButton miniGame
            || layout.FindElement(EndCharacterSessionButtonId) is not UiButton endSession)
            return null;

        return new IndicatorBarController(
            bindings, link, helpful, harmful, vitae, burden, miniGame, endSession);
    }

    public void Tick()
    {
        double now = _bindings.CurrentTime();
        if (now - _lastLinkUpdate >= LinkUpdateSeconds)
        {
            UpdateLinkState(now, _bindings.LinkStatus());
            _lastLinkUpdate = now;
        }

        if (_linkState == LinkSemanticState.Bad
            && now - _lastLinkFlash >= LinkFlashSeconds)
        {
            _link.TrySetRetailState(
                _link.ActiveRetailStateId == ConnectionUncertainState
                    ? ConnectionBadState
                    : ConnectionUncertainState);
            _lastLinkFlash = now;
        }
    }

    public void SetMiniGameActive(bool active)
        => _miniGame.TrySetRetailState(
            active ? UiButtonStateMachine.Normal : UiButtonStateMachine.Ghosted);

    private void UpdateLinkState(double now, LinkStatusSnapshot snapshot)
    {
        LinkSemanticState state = !snapshot.Connected || snapshot.SecondsSinceLastPacket >= 40d
            ? LinkSemanticState.Disconnected
            : snapshot.SecondsSinceLastPacket >= 20d
                ? LinkSemanticState.Bad
                : snapshot.SecondsSinceLastPacket >= 5d
                    ? LinkSemanticState.Uncertain
                    : LinkSemanticState.Good;
        SetLinkState(state, now);
    }

    private void SetLinkState(LinkSemanticState state, double now)
    {
        if (_linkState == state) return;
        _linkState = state;
        uint visual = state switch
        {
            LinkSemanticState.Good => ConnectionGoodState,
            LinkSemanticState.Uncertain => ConnectionUncertainState,
            LinkSemanticState.Bad => ConnectionBadState,
            LinkSemanticState.Disconnected => ConnectionDisconnectedState,
            _ => throw new InvalidOperationException($"Unknown link state {state}."),
        };
        _link.TrySetRetailState(visual);
        if (state == LinkSemanticState.Bad)
            _lastLinkFlash = now;
    }

    private void UpdateEnchantments()
    {
        _helpful.TrySetRetailState(HasMatchingEffect(beneficial: true)
            ? UiButtonStateMachine.Normal
            : UiButtonStateMachine.Ghosted);
        _harmful.TrySetRetailState(HasMatchingEffect(beneficial: false)
            ? UiButtonStateMachine.Normal
            : UiButtonStateMachine.Ghosted);

        bool hasVitae = _bindings.Spellbook.ActiveEnchantmentSnapshot.Any(record =>
            record.Bucket == 4u
            && record.StatModValue is float modifier
            && modifier < 1f);
        _vitae.TrySetRetailState(hasVitae
            ? UiButtonStateMachine.Normal
            : UiButtonStateMachine.Ghosted);
    }

    private bool HasMatchingEffect(bool beneficial)
        => _bindings.Spellbook.ActiveEnchantmentSnapshot.Any(record =>
            record.Bucket is 1u or 2u
            && _bindings.Spellbook.TryGetMetadata(
                record.SpellId, out SpellMetadata metadata)
            && metadata.IsBeneficial == beneficial);

    private void OnObjectChanged(ClientObject _) => UpdateBurden();
    private void OnObjectMoved(ClientObjectMove _) => UpdateBurden();
    private void OnContainerContentsReplaced(uint _) => UpdateBurden();
    private void OnObjectsCleared() => UpdateBurden();
    private void OnEnchantmentsChanged()
    {
        UpdateEnchantments();
        UpdateBurden();
    }

    private void UpdateBurden()
    {
        uint player = _bindings.PlayerGuid();
        ClientObject? playerObject = _bindings.Objects.Get(player);
        int strength = _bindings.Strength() ?? 10;
        int augmentation = playerObject?.Properties.GetInt(EncumbranceAugProperty) ?? 0;
        int capacity = BurdenMath.EncumbranceCapacity(strength, augmentation);
        int burden = playerObject?.Properties.Ints.TryGetValue(
                EncumbranceValProperty, out int wireBurden) == true
            ? wireBurden
            : _bindings.Objects.SumCarriedBurden(player);
        float load = BurdenMath.LoadRatio(capacity, burden);

        _burden.TrySetRetailState(load < 1f
            ? UnencumberedState
            : load < 2f ? EncumberedState : HeavilyEncumberedState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bindings.Spellbook.EnchantmentsChanged -= OnEnchantmentsChanged;
        _bindings.Objects.ObjectAdded -= OnObjectChanged;
        _bindings.Objects.ObjectUpdated -= OnObjectChanged;
        _bindings.Objects.ObjectRemoved -= OnObjectChanged;
        _bindings.Objects.ObjectMoved -= OnObjectMoved;
        _bindings.Objects.ContainerContentsReplaced -= OnContainerContentsReplaced;
        _bindings.Objects.Cleared -= OnObjectsCleared;
        _link.OnClick = null;
        _helpful.OnClick = null;
        _harmful.OnClick = null;
        _vitae.OnClick = null;
        _burden.OnClick = null;
        _miniGame.OnClick = null;
        _endCharacterSession.OnClick = null;
    }
}

public sealed record IndicatorBarBindings(
    Spellbook Spellbook,
    ClientObjectTable Objects,
    Func<uint> PlayerGuid,
    Func<int?> Strength,
    Func<LinkStatusSnapshot> LinkStatus,
    Func<double> CurrentTime,
    Action<uint> TogglePanel,
    Action RequestEndCharacterSession);
