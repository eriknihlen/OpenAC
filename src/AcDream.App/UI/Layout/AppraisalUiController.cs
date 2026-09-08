using System.Globalization;
using System.Text;
using AcDream.App.Spells;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Core.Spells;

namespace AcDream.App.UI.Layout;

public sealed class AppraisalUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100006Bu;
    public const uint RootId = 0x100005F2u;
    public const uint CloseId = 0x100005F3u;
    public const uint TitleId = 0x1000012Du;
    public const uint ItemPanelId = 0x1000012Eu;
    public const uint ItemTextId = 0x1000013Cu;
    public const uint ItemScrollbarId = 0x1000013Du;
    public const uint InscriptionBackgroundId = 0x10000137u;
    public const uint InscriptionTextId = 0x1000013Eu;
    public const uint SignatureTextId = 0x1000013Fu;
    public const uint InscriptionScrollbarId = 0x1000046Eu;
    public const uint CreaturePanelId = 0x10000140u;
    public const uint CreatureViewportId = 0x10000148u;
    public const uint CreatureStatsListId = 0x10000149u;
    public const uint CreatureExtraListId = 0x10000335u;
    public const uint CreatureDisplayNameId = 0x1000014Eu;
    public const uint CreatureLevelValueId = 0x1000014Cu;
    public const uint SpellPanelId = 0x10000153u;
    public const uint SpellSchoolTextId = 0x1000015Eu;
    public const uint SpellIconId = 0x1000015Fu;
    public const uint SpellManaTextId = 0x10000160u;
    public const uint SpellDurationTextId = 0x10000161u;
    public const uint SpellRangeTextId = 0x10000162u;
    public const uint SpellDisplayTextId = 0x10000163u;
    public const uint SpellFormulaListId = 0x1000032Du;

    private const uint TemplateStringProperty = 5u;
    private const uint CharacterMarkerIntProperty = 0x105u;
    private const uint DisplayedNameStringProperty = 0x34u;
    private const double CreatureRefreshSeconds = 0.75;

    private readonly ImportedLayout _layout;
    private readonly ClientObjectTable _objects;
    private readonly ItemInteractionController _interaction;
    private readonly SelectionState _selection;
    private readonly CombatState _combat;
    private readonly Spellbook _spellbook;
    private readonly Func<string> _playerName;
    private readonly Action<uint, string> _sendSetInscription;
    private readonly Action<string> _systemMessage;
    private readonly Action _show;
    private readonly Action _closeWindow;
    private readonly UiElement _itemPanel;
    private readonly UiElement _creaturePanel;
    private readonly UiElement _spellPanel;
    private readonly UiText _title;
    private readonly UiText _itemText;
    private readonly UiText? _inscriptionText;
    private readonly UiField? _inscriptionField;
    private readonly UiText? _signature;
    private readonly UiDatElement? _inscriptionBackground;
    private readonly UiButton? _close;
    private readonly CreatureAppraisalLayeredList? _creatureStats;
    private readonly CreatureAppraisalLayeredList? _creatureExtra;
    private readonly CreatureAppraisalRowTemplateFactory? _creatureRowTemplates;
    private readonly CreatureDisplayNameResolver _creatureNames;
    private readonly RetailAppraisalNameResolver _itemNames;
    private readonly Func<uint, uint> _resolveSpellIcon;
    private readonly Func<uint, uint> _resolveComponentIcon;
    private readonly Func<uint, IReadOnlyList<SpellExamineComponent>> _spellComponents;
    private readonly Func<MagicSchool, uint> _magicSkill;
    private readonly Func<uint, string?> _resolveCharacterTitle;
    private readonly Func<int> _localFactionBits;
    private readonly SpellExamineComponentTemplateFactory? _spellComponentTemplates;
    private readonly UiText _spellSchool;
    private readonly UiText _spellMana;
    private readonly UiText _spellDuration;
    private readonly UiText _spellRange;
    private readonly UiText _spellDisplay;
    private readonly UiElement _spellIconHost;
    private readonly UiElement _spellFormulaHost;
    private readonly UiTextureElement _spellIcon;
    private readonly List<UiElement> _spellFormulaCells = [];
    private readonly Dictionary<UiText, UiTextLayoutCache<string>>
        _textLayouts = new();
    private UiTextLayoutCache<ItemAppraisalReport> _itemTextLayout = null!;
    private readonly float _spellFormulaOriginX;
    private readonly float _spellFormulaCellWidth;
    private AppraisalView _activeView;
    private uint _itemObjectId;
    private uint _creatureObjectId;
    private uint _characterObjectId;
    private string _titleValue = string.Empty;
    private ItemAppraisalReport _itemReport = ItemAppraisalReport.Empty;
    private string _inscriptionValue = string.Empty;
    private string _signatureValue = string.Empty;
    private string _scribeName = string.Empty;
    private string _oldInscription = string.Empty;
    private bool _presentationInscribable;
    private uint _spellId;
    private bool _windowVisible;
    private double _refreshElapsed;
    private bool _disposed;

    private AppraisalUiController(
        ImportedLayout layout,
        ClientObjectTable objects,
        ItemInteractionController interaction,
        SelectionState selection,
        CombatState combat,
        Spellbook spellbook,
        Func<string> playerName,
        Action<uint, string> sendSetInscription,
        Action<string> systemMessage,
        Action show,
        Action close,
        UiElement itemPanel,
        UiElement creaturePanel,
        UiElement spellPanel,
        UiText title,
        UiText itemText,
        CreatureAppraisalRowTemplateFactory? creatureRowTemplates,
        CreatureDisplayNameResolver? creatureNames,
        RetailAppraisalNameResolver? itemNames,
        Func<uint, uint>? resolveSpellIcon,
        Func<uint, uint>? resolveComponentIcon,
        Func<uint, IReadOnlyList<SpellExamineComponent>>? spellComponents,
        Func<MagicSchool, uint>? magicSkill,
        SpellExamineComponentTemplateFactory? spellComponentTemplates,
        Func<uint, string?>? resolveCharacterTitle,
        Func<int>? localFactionBits)
    {
        _layout = layout;
        _objects = objects;
        _interaction = interaction;
        _selection = selection;
        _combat = combat;
        _spellbook = spellbook;
        _playerName = playerName;
        _sendSetInscription = sendSetInscription;
        _systemMessage = systemMessage;
        _show = show;
        _closeWindow = close;
        _itemPanel = itemPanel;
        _creaturePanel = creaturePanel;
        _spellPanel = spellPanel;
        _title = title;
        _itemText = itemText;
        _creatureRowTemplates = creatureRowTemplates;
        _creatureNames = creatureNames
            ?? new CreatureDisplayNameResolver(
                new Dictionary<uint, string>());
        _itemNames = itemNames ?? RetailAppraisalNameResolver.Empty;
        _resolveSpellIcon = resolveSpellIcon ?? (_ => 0u);
        _resolveComponentIcon = resolveComponentIcon ?? (_ => 0u);
        _spellComponents = spellComponents ?? (_ => []);
        _magicSkill = magicSkill ?? (_ => 0u);
        _resolveCharacterTitle = resolveCharacterTitle ?? (_ => null);
        _localFactionBits = localFactionBits ?? (() => 0);
        _spellComponentTemplates = spellComponentTemplates;
        _spellSchool = (UiText)layout.FindElement(SpellSchoolTextId)!;
        _spellMana = (UiText)layout.FindElement(SpellManaTextId)!;
        _spellDuration = (UiText)layout.FindElement(SpellDurationTextId)!;
        _spellRange = (UiText)layout.FindElement(SpellRangeTextId)!;
        _spellDisplay = (UiText)layout.FindElement(SpellDisplayTextId)!;
        _spellIconHost = layout.FindElement(SpellIconId)!;
        _spellFormulaHost = layout.FindElement(SpellFormulaListId)!;
        _spellFormulaOriginX = _spellFormulaHost.Left;
        _spellFormulaCellWidth = _spellFormulaHost.Width;
        _inscriptionText = layout.FindElement(InscriptionTextId) as UiText;
        _inscriptionField = layout.FindElement(InscriptionTextId) as UiField;
        _signature = layout.FindElement(SignatureTextId) as UiText;
        _inscriptionBackground =
            layout.FindElement(InscriptionBackgroundId) as UiDatElement;
        _close = layout.FindElement(CloseId) as UiButton;

        BindTextSource(
            _title,
            () => _titleValue,
            static (target, value) =>
                [new UiText.Line(value, target.DefaultColor)]);
        _itemText.VerticalJustify = VJustify.Top;
        ConfigureScrollableItemText(_itemText, ItemScrollbarId);
        if (_inscriptionText is not null)
            ConfigureScrollableText(
                _inscriptionText,
                InscriptionScrollbarId,
                () => _inscriptionValue);
        if (_inscriptionField is not null)
        {
            _inscriptionField.SetText(string.Empty);
            _inscriptionField.ClearOnSubmit = false;
            _inscriptionField.RecordHistory = false;
            _inscriptionField.Editable = false;
            _inscriptionField.Selectable = false;
            _inscriptionField.OnFocusGained = HandleInscriptionGainingFocus;
            _inscriptionField.OnFocusLost = HandleInscriptionLosingFocus;
            _inscriptionField.OnReadOnlyClick = ReportInscriptionUnavailable;
            if (layout.FindElement(InscriptionScrollbarId) is UiScrollbar scrollbar)
                scrollbar.Model = _inscriptionField.Scroll;
        }
        if (_signature is not null)
        {
            BindTextSource(
                _signature,
                () => _signatureValue,
                static (target, value) =>
                    [new UiText.Line(value, target.DefaultColor)]);
        }
        if (_close is not null)
            _close.OnClick = close;
        if (_inscriptionBackground is not null)
        {
            _inscriptionBackground.OnClick = ReportInscriptionUnavailable;
            _inscriptionBackground.ClickThrough = false;
        }

        if (_spellIconHost is UiDatElement spellIconDat)
            spellIconDat.MediaVisible = false;
        _spellIcon = new UiTextureElement
        {
            Width = _spellIconHost.Width,
            Height = _spellIconHost.Height,
            Anchors = AnchorEdges.Left | AnchorEdges.Top
                | AnchorEdges.Right | AnchorEdges.Bottom,
        };
        _spellIconHost.AddChild(_spellIcon);
        ConfigureSpellText(_spellSchool);
        ConfigureSpellText(_spellMana);
        ConfigureSpellText(_spellDuration);
        ConfigureSpellText(_spellRange);
        ConfigureSpellText(_spellDisplay, wrap: true);

        if (creatureRowTemplates is not null
            && layout.FindElement(CreatureStatsListId) is { } statsHost
            && layout.FindElement(CreatureExtraListId) is { } extraHost
            && layout.FindElement(CreatureViewportId) is UiViewport viewport)
        {
            _creatureStats = CreatureAppraisalLayeredList.Create(
                _creaturePanel,
                statsHost,
                viewport,
                creatureRowTemplates,
                backgroundZOrder: viewport.ZOrder - 2);
            _creatureExtra = CreatureAppraisalLayeredList.Create(
                _creaturePanel,
                extraHost,
                viewport,
                creatureRowTemplates,
                backgroundZOrder: viewport.ZOrder - 1);
        }

        _selection.Changed += HandleSelectionChanged;
        _objects.ObjectAdded += HandleSpellComponentObjectChanged;
        _objects.ObjectMoved += HandleSpellComponentObjectMoved;
        _objects.ObjectRemoved += HandleSpellComponentObjectChanged;
        _objects.StackSizeUpdated += HandleSpellComponentObjectChanged;
        _objects.Cleared += HandleSpellComponentObjectsCleared;
        SetActiveView(AppraisalView.Item);
    }

    public AppraisalView ActiveView => _activeView;
    public uint CurrentObjectId => _interaction.CurrentAppraisalId;

    public static AppraisalUiController? Bind(
        ImportedLayout layout,
        ClientObjectTable objects,
        ItemInteractionController interaction,
        SelectionState selection,
        CombatState combat,
        Spellbook spellbook,
        Func<string> playerName,
        Action<uint, string> sendSetInscription,
        Action<string> systemMessage,
        Action show,
        Action close,
        CreatureAppraisalRowTemplateFactory? creatureRowTemplates = null,
        CreatureDisplayNameResolver? creatureNames = null,
        RetailAppraisalNameResolver? itemNames = null,
        Func<uint, uint>? resolveSpellIcon = null,
        Func<uint, uint>? resolveComponentIcon = null,
        Func<uint, IReadOnlyList<SpellExamineComponent>>? spellComponents = null,
        Func<MagicSchool, uint>? magicSkill = null,
        SpellExamineComponentTemplateFactory? spellComponentTemplates = null,
        Func<uint, string?>? resolveCharacterTitle = null,
        Func<int>? localFactionBits = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(spellbook);
        ArgumentNullException.ThrowIfNull(playerName);
        ArgumentNullException.ThrowIfNull(sendSetInscription);
        ArgumentNullException.ThrowIfNull(systemMessage);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(close);

        if (layout.FindElement(ItemPanelId) is not { } itemPanel
            || layout.FindElement(CreaturePanelId) is not { } creaturePanel
            || layout.FindElement(SpellPanelId) is not { } spellPanel
            || layout.FindElement(TitleId) is not UiText title
            || layout.FindElement(ItemTextId) is not UiText itemText
            || layout.FindElement(SpellSchoolTextId) is not UiText
            || layout.FindElement(SpellManaTextId) is not UiText
            || layout.FindElement(SpellDurationTextId) is not UiText
            || layout.FindElement(SpellRangeTextId) is not UiText
            || layout.FindElement(SpellDisplayTextId) is not UiText
            || layout.FindElement(SpellIconId) is null
            || layout.FindElement(SpellFormulaListId) is null)
            return null;

        return new AppraisalUiController(
            layout,
            objects,
            interaction,
            selection,
            combat,
            spellbook,
            playerName,
            sendSetInscription,
            systemMessage,
            show,
            close,
            itemPanel,
            creaturePanel,
            spellPanel,
            title,
            itemText,
            creatureRowTemplates,
            creatureNames,
            itemNames,
            resolveSpellIcon,
            resolveComponentIcon,
            spellComponents,
            magicSkill,
            spellComponentTemplates,
            resolveCharacterTitle,
            localFactionBits);
    }

    public bool ExamineSpell(uint spellId)
    {
        if (spellId == 0u
            || !_spellbook.TryGetMetadata(spellId, out SpellMetadata metadata))
            return false;

        _interaction.CancelObjectAppraisalForSpell();
        _spellId = spellId;
        _titleValue = metadata.Name;
        SetSpellText(_spellSchool, $"School: {metadata.School}");

        string mana = metadata.ManaCost > 0
            ? metadata.ManaCost.ToString(CultureInfo.InvariantCulture)
            : "???";
        if (metadata.ManaModifier > 0u)
            mana += $" + {metadata.ManaModifier.ToString(CultureInfo.InvariantCulture)} per target";
        SetSpellText(_spellMana, $"Mana: {mana}");

        string duration = string.Empty;
        if (metadata.Duration > 0f && metadata.Duration != -1f)
        {
            uint displayed = metadata.Duration >= 60f
                ? (uint)(metadata.Duration / 60f)
                : (uint)metadata.Duration;
            duration = metadata.Duration >= 60f
                ? $"Duration: {displayed.ToString(CultureInfo.InvariantCulture)} min."
                : $"Duration: {displayed.ToString(CultureInfo.InvariantCulture)} sec.";
        }
        SetSpellText(_spellDuration, duration);

        float range = MathF.Min(
            metadata.BaseRangeConstant
            + metadata.BaseRangeModifier * _magicSkill(metadata.SchoolId),
            75f);
        SetSpellText(
            _spellRange,
            range > 0f
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "Range: {0:F1} yds.",
                    range / 0.9144f)
                : string.Empty);

        IReadOnlyList<SpellExamineComponent> components =
            _spellComponents(spellId);
        SetSpellText(_spellDisplay, BuildSpellDisplay(metadata, components));
        _spellDisplay.Scroll.SetScrollY(0);
        _spellIcon.Texture = _resolveSpellIcon(spellId);
        RebuildSpellFormula(components);

        SetActiveView(AppraisalView.Spell);
        _refreshElapsed = 0;
        _show();
        return true;
    }

    public bool Apply(AppraiseInfoParser.Parsed appraisal)
    {
        ItemInteractionController.AppraisalResponseAcceptance acceptance =
            _interaction.AcceptAppraisalResponse(appraisal.Guid);
        if (!acceptance.Accepted)
            return false;

        ClientObject? obj = _objects.Get(appraisal.Guid);
        if (obj is null)
            return false;

        _titleValue = BuildTitle(obj, appraisal.Properties);
        AppraisalView view = SelectView(appraisal);
        bool newlySelected = view switch
        {
            AppraisalView.Item => _itemObjectId != appraisal.Guid,
            AppraisalView.Creature => _creatureObjectId != appraisal.Guid,
            AppraisalView.Character => _characterObjectId != appraisal.Guid,
            _ => false,
        };

        switch (view)
        {
            case AppraisalView.Item:
                _itemObjectId = appraisal.Guid;
                ApplyItem(obj, appraisal, newlySelected);
                break;
            case AppraisalView.Creature:
                _creatureObjectId = appraisal.Guid;
                ApplyCreature(obj, appraisal, character: false, newlySelected);
                break;
            case AppraisalView.Character:
                _characterObjectId = appraisal.Guid;
                ApplyCreature(obj, appraisal, character: true, newlySelected);
                break;
        }

        SetActiveView(view);
        _refreshElapsed = 0;
        if (acceptance.FirstResponse)
            _show();
        return true;
    }

    public void Tick(double deltaSeconds)
    {
        if (!_windowVisible
            || _activeView is not (AppraisalView.Creature or AppraisalView.Character)
            || _combat.CurrentMode == CombatMode.NonCombat
            || CurrentObjectId == 0)
        {
            _refreshElapsed = 0;
            return;
        }

        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            return;
        _refreshElapsed += deltaSeconds;
        if (_refreshElapsed < CreatureRefreshSeconds)
            return;
        _refreshElapsed %= CreatureRefreshSeconds;
        _interaction.RefreshCurrentAppraisal();
    }

    public void ResetSession()
    {
        _titleValue = string.Empty;
        _itemReport = ItemAppraisalReport.Empty;
        _inscriptionValue = string.Empty;
        _inscriptionField?.SetText(string.Empty);
        if (_inscriptionField is not null)
        {
            _inscriptionField.Editable = false;
            _inscriptionField.Selectable = false;
            _inscriptionField.Visible = false;
            _inscriptionField.Scroll.SetScrollY(0);
        }
        _signatureValue = string.Empty;
        if (_signature is not null)
            _signature.Visible = false;
        _scribeName = string.Empty;
        _oldInscription = string.Empty;
        _presentationInscribable = false;
        _itemObjectId = 0;
        _creatureObjectId = 0;
        _characterObjectId = 0;
        _spellId = 0u;
        _refreshElapsed = 0;
        _spellIcon.Texture = 0u;
        SetSpellText(_spellSchool, string.Empty);
        SetSpellText(_spellMana, string.Empty);
        SetSpellText(_spellDuration, string.Empty);
        SetSpellText(_spellRange, string.Empty);
        SetSpellText(_spellDisplay, string.Empty);
        ClearSpellFormula();
        SetActiveView(AppraisalView.Item);
        ClearCreatureText();
    }

    private void ApplyItem(
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal,
        bool newlySelected)
    {
        _itemReport = ItemAppraisalTextFormatter.BuildReport(
            obj,
            appraisal,
            ResolveSpell,
            _itemNames);
        SetInscription(obj, appraisal);
        if (newlySelected)
        {
            _itemText.Scroll.SetScrollY(0);
            _inscriptionText?.Scroll.SetScrollY(0);
            _inscriptionField?.Scroll.SetScrollY(0);
        }
    }

    private void SetInscription(
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal)
    {
        _oldInscription = string.Empty;
        _scribeName = string.Empty;
        _inscriptionValue = string.Empty;
        _signatureValue = string.Empty;

        var publicFlags =
            (PublicWeenieFlags)(obj.PublicWeenieBitfield ?? 0u);
        bool publicInscribable =
            (publicFlags & PublicWeenieFlags.Inscribable) != 0;
        _presentationInscribable = obj.IsHook
            ? appraisal.HookProfile is { } hook
              && (hook.Flags & 0x1u) != 0
            : publicInscribable;

        if (!_presentationInscribable)
        {
            if (_inscriptionField is not null)
            {
                _inscriptionField.SetText(string.Empty);
                _inscriptionField.Editable = false;
                _inscriptionField.Selectable = false;
                _inscriptionField.Visible = false;
            }
            if (_inscriptionText is not null)
                _inscriptionText.Visible = false;
            if (_signature is not null)
                _signature.Visible = false;
            if (_inscriptionBackground is not null)
                _inscriptionBackground.ClickThrough = false;
            return;
        }

        _scribeName = GetString(appraisal.Properties, 8u);
        _oldInscription = GetString(appraisal.Properties, 7u);
        if (string.IsNullOrEmpty(_scribeName))
        {
            _inscriptionValue = "<Inscribe here>";
        }
        else if (!string.IsNullOrEmpty(_oldInscription))
        {
            _inscriptionValue = _oldInscription;
            _signatureValue = $"--{_scribeName}";
        }

        if (_inscriptionField is not null)
        {
            _inscriptionField.SetText(_inscriptionValue);
            _inscriptionField.Visible = true;
        }
        if (_inscriptionText is not null)
            _inscriptionText.Visible = true;
        if (_signature is not null)
            _signature.Visible = true;

        bool editable = publicInscribable
            && _interaction.IsOwnedByPlayer(obj.ObjectId)
            && (string.IsNullOrEmpty(_scribeName)
                || string.Equals(
                    _scribeName,
                    _playerName(),
                    StringComparison.OrdinalIgnoreCase));
        if (_inscriptionField is not null)
        {
            _inscriptionField.Editable = editable;
            _inscriptionField.Selectable = editable;
        }
        if (_inscriptionBackground is not null)
            _inscriptionBackground.ClickThrough = editable;
    }

    private void HandleInscriptionGainingFocus()
    {
        if (_inscriptionField is not { Editable: true } field)
            return;
        if (string.IsNullOrEmpty(_scribeName))
        {
            field.SetText(string.Empty);
            _inscriptionValue = string.Empty;
        }
        _signatureValue = $"--{_playerName()}";
    }

    private void HandleInscriptionLosingFocus(string text)
    {
        if (_itemObjectId == 0 || !_presentationInscribable)
            return;

        bool skipEmptyUnscribed = string.IsNullOrEmpty(text)
                                  && string.IsNullOrEmpty(_scribeName);
        if (!skipEmptyUnscribed
            && !string.Equals(
                text,
                _oldInscription,
                StringComparison.Ordinal))
        {
            _sendSetInscription(_itemObjectId, text);
        }

        if (string.IsNullOrEmpty(text))
        {
            _inscriptionValue = "<Inscribe here>";
            _signatureValue = string.Empty;
            _scribeName = string.Empty;
            _inscriptionField?.SetText(_inscriptionValue);
        }
        else
        {
            _inscriptionValue = text;
            _scribeName = _playerName();
            _signatureValue = $"--{_scribeName}";
        }
        _oldInscription = text;
    }

    private void ReportInscriptionUnavailable()
    {
        if (_inscriptionField?.Editable == true)
            return;
        if (!string.IsNullOrEmpty(_scribeName)
            && !string.Equals(
                _scribeName,
                _playerName(),
                StringComparison.OrdinalIgnoreCase))
        {
            _systemMessage($"Only {_scribeName} can change the inscription");
            return;
        }
        if (_itemObjectId == 0
            || !_interaction.IsOwnedByPlayer(_itemObjectId))
        {
            _systemMessage("Item must be in your inventory to inscribe.");
            return;
        }
        _systemMessage("This item is not inscribable.");
    }

    private void ApplyCreature(
        ClientObject obj,
        AppraiseInfoParser.Parsed appraisal,
        bool character,
        bool newlySelected)
    {
        ClearCreatureText();
        PropertyBundle p = appraisal.Properties;
        int level = GetInt(p, 25u);
        SetText(
            CreatureLevelValueId,
            level > 0
                ? level.ToString(CultureInfo.InvariantCulture)
                : "???");

        if (character)
        {
            SetText(0x10000150u, BuildCharacterHeritageDisplay(p));
            SetText(0x10000151u, BuildCharacterTitleDisplay(p));
            SetText(0x10000152u, BuildPlayerKillerDisplay(obj));
            SetText(0x1000053Au, BuildAllegianceDisplay(p));
            _titleValue = BuildCharacterTitleBarName(obj, p);
        }
        else
        {
            SetText(
                CreatureDisplayNameId,
                _creatureNames.Resolve(GetInt(p, 2u)));
        }

        RebuildCreatureStats(appraisal, character);

        if (newlySelected)
            ResetCreatureScroll();
    }

    private string BuildCharacterHeritageDisplay(PropertyBundle p)
    {
        int gender = GetInt(p, CharacterIdentityText.GenderPropertyId);
        int heritageGroup = GetInt(p, CharacterIdentityText.HeritageGroupPropertyId);
        string creatureFallback = heritageGroup == 0
            ? _creatureNames.Resolve(GetInt(p, 2u))
            : string.Empty;
        return CharacterIdentityText.GenderHeritageDisplay(
            gender, heritageGroup, creatureFallback);
    }

    private string BuildCharacterTitleDisplay(PropertyBundle p)
    {
        if (p.Ints.TryGetValue(CharacterMarkerIntProperty, out int titleId)
            && titleId != 0
            && _resolveCharacterTitle(unchecked((uint)titleId)) is { Length: > 0 } resolved)
        {
            return resolved;
        }
        return GetString(p, TemplateStringProperty);
    }

    private static string BuildPlayerKillerDisplay(ClientObject obj)
    {
        var bitfield = (PublicWeenieFlags)(obj.PublicWeenieBitfield ?? 0u);
        if ((bitfield & PublicWeenieFlags.PlayerKiller) != 0)
            return "Player Killer";
        if ((bitfield & PublicWeenieFlags.PlayerKillerLite) != 0)
            return "Player Killer Lite";
        return "Non-Player Killer";
    }

    private static string BuildAllegianceDisplay(PropertyBundle p)
        => GetInt(p, 30u) >= 1 ? GetString(p, 47u) : string.Empty;

    private string BuildCharacterTitleBarName(ClientObject obj, PropertyBundle p)
    {
        int rank = GetInt(p, AllegianceRankTitleTable.AllegianceRankPropertyId);
        int heritageGroup = GetInt(p, CharacterIdentityText.HeritageGroupPropertyId);
        int gender = GetInt(p, CharacterIdentityText.GenderPropertyId);
        string name = _itemNames.ResolveAppropriateName(obj);
        return AllegianceRankTitleTable.ComposeFullName(rank, heritageGroup, gender, name);
    }

    private void RebuildCreatureStats(
        AppraiseInfoParser.Parsed appraisal,
        bool character)
    {
        if (_creatureStats is null || _creatureRowTemplates is null)
            return;

        _creatureStats.Rebuild(
            appraisal.CreatureProfile is { } profile
                ? CreatureAppraisalRows.Build(profile, appraisal.Success)
                : Array.Empty<CreatureAppraisalRow>());
        _creatureExtra?.Rebuild(
            CreatureAppraisalRows.BuildExtra(
                appraisal.Properties,
                appraisal.ArmorLevels,
                character,
                character ? _localFactionBits() : 0));
    }

    private void ConfigureScrollableText(
        UiText text,
        uint scrollbarId,
        Func<string> value)
    {
        text.PreserveEndOnLayout = false;
        text.WheelScrollEnabled = true;
        text.ClickThrough = false;
        BindTextSource(
            text,
            value,
            static (target, content) =>
                IndicatorDetailText.Shape(target, content));
        if (_layout.FindElement(scrollbarId) is UiScrollbar scrollbar)
            scrollbar.Model = text.Scroll;
    }

    private void BindTextSource(
        UiText text,
        Func<string> source,
        Func<UiText, string, IReadOnlyList<UiText.Line>> shape)
    {
        var cache = new UiTextLayoutCache<string>(
            text,
            shape,
            source,
            StringComparer.Ordinal);
        _textLayouts.Add(text, cache);
        text.LinesProvider = cache.Provider;
    }

    private UiTextLayoutCache<string> GetTextLayout(UiText text)
    {
        if (_textLayouts.TryGetValue(
                text,
                out UiTextLayoutCache<string>? cache))
        {
            return cache;
        }

        cache = new UiTextLayoutCache<string>(
            text,
            static (target, value) =>
                IndicatorDetailText.Shape(target, value),
            string.Empty,
            StringComparer.Ordinal);
        _textLayouts.Add(text, cache);
        text.LinesProvider = cache.Provider;
        return cache;
    }

    private void ConfigureScrollableItemText(
        UiText text,
        uint scrollbarId)
    {
        text.PreserveEndOnLayout = false;
        text.WheelScrollEnabled = true;
        text.ClickThrough = false;
        _itemTextLayout = new UiTextLayoutCache<ItemAppraisalReport>(
            text,
            static (target, report) =>
                ItemAppraisalTextLayout.Shape(target, report),
            () => _itemReport,
            ReferenceEqualityComparer.Instance);
        text.LinesProvider = _itemTextLayout.Provider;
        if (_layout.FindElement(scrollbarId) is UiScrollbar scrollbar)
            scrollbar.Model = text.Scroll;
    }

    private static void ConfigureSpellText(UiText text, bool wrap = false)
    {
        text.PreserveEndOnLayout = false;
        text.ClickThrough = true;
        if (wrap)
        {
            text.OneLine = false;
            text.VerticalJustify = VJustify.Top;
        }
    }

    private void SetSpellText(UiText text, string value)
        => GetTextLayout(text).SetValue(value);

    private static string BuildSpellDisplay(
        SpellMetadata metadata,
        IReadOnlyList<SpellExamineComponent> components)
    {
        var display = new StringBuilder(metadata.Description);
        if (components.Count == 0)
            return display.ToString();

        display.Append("\nCOMPONENTS:");
        foreach (SpellExamineComponent component in components)
            display.Append("\n     ").Append(component.Descriptor.Name);
        return display.ToString();
    }

    private void RebuildSpellFormula(
        IReadOnlyList<SpellExamineComponent> components)
    {
        ClearSpellFormula();
        if (_spellComponentTemplates is null || components.Count == 0)
        {
            _spellFormulaHost.Width = _spellFormulaCellWidth;
            _spellFormulaHost.Left = _spellFormulaOriginX;
            return;
        }

        float cellWidth = _spellFormulaCellWidth;
        _spellFormulaHost.Left =
            _spellFormulaOriginX - cellWidth * 0.5f * (components.Count - 1);
        _spellFormulaHost.Width = cellWidth * components.Count;
        _spellFormulaHost.ResetAnchorCapture();
        for (int i = 0; i < components.Count; i++)
        {
            SpellExamineComponent component = components[i];
            UiElement cell = _spellComponentTemplates.Create(
                _resolveComponentIcon(component.Descriptor.IconId),
                component.Owned);
            cell.LayoutPolicy = null;
            cell.Anchors = AnchorEdges.Left | AnchorEdges.Top;
            cell.Left = i * cellWidth;
            cell.Top = 0f;
            _spellFormulaHost.AddChild(cell);
            _spellFormulaCells.Add(cell);
        }
    }

    private void ClearSpellFormula()
    {
        foreach (UiElement cell in _spellFormulaCells)
            _spellFormulaHost.RemoveChild(cell);
        _spellFormulaCells.Clear();
        _spellFormulaHost.Left = _spellFormulaOriginX;
        _spellFormulaHost.Width = _spellFormulaCellWidth;
        _spellFormulaHost.ResetAnchorCapture();
    }

    private void SetText(uint elementId, string value, bool scrollable = false)
    {
        if (_layout.FindElement(elementId) is not UiText text)
            return;
        text.PreserveEndOnLayout = false;
        text.WheelScrollEnabled = scrollable;
        text.ClickThrough = !scrollable;
        GetTextLayout(text).SetValue(value);
        if (scrollable)
        {
            UiScrollbar? scrollbar = Descendants(text.Parent)
                .OfType<UiScrollbar>()
                .FirstOrDefault(candidate => candidate.Visible);
            if (scrollbar is not null)
                scrollbar.Model = text.Scroll;
        }
    }

    private void ClearCreatureText()
    {
        foreach (uint id in new uint[]
        {
            CreatureLevelValueId, CreatureDisplayNameId,
            0x10000150u, 0x10000151u, 0x10000152u, 0x1000053Au,
        })
            if (_layout.FindElement(id) is UiText text)
                GetTextLayout(text).SetValue(string.Empty);
        _creatureStats?.Flush();
        _creatureExtra?.Flush();
    }

    private void ResetCreatureScroll()
    {
        _creatureStats?.ResetScroll();
        _creatureExtra?.ResetScroll();
        foreach (UiText text in Descendants(_creaturePanel).OfType<UiText>())
            text.Scroll.SetScrollY(0);
    }

    private void SetActiveView(AppraisalView view)
    {
        _activeView = view;
        _itemPanel.Visible = view == AppraisalView.Item;
        _creaturePanel.Visible = view is AppraisalView.Creature or AppraisalView.Character;
        _spellPanel.Visible = view == AppraisalView.Spell;

        UiElement? creatureInfo = _layout.FindElement(0x1000014Du);
        UiElement? characterInfo = _layout.FindElement(0x1000014Fu);
        if (creatureInfo is not null)
            creatureInfo.Visible = view == AppraisalView.Creature;
        if (characterInfo is not null)
            characterInfo.Visible = view == AppraisalView.Character;
    }

    private SpellMetadata? ResolveSpell(uint spellId)
        => _spellbook.TryGetMetadata(spellId & 0x7FFF_FFFFu, out SpellMetadata metadata)
            ? metadata
            : null;

    private static AppraisalView SelectView(AppraiseInfoParser.Parsed appraisal)
    {
        if (appraisal.CreatureProfile is null)
            return AppraisalView.Item;
        return appraisal.Properties.Strings.ContainsKey(TemplateStringProperty)
               || appraisal.Properties.Ints.ContainsKey(CharacterMarkerIntProperty)
            ? AppraisalView.Character
            : AppraisalView.Creature;
    }

    private string BuildTitle(ClientObject obj, PropertyBundle properties)
    {
        string name = GetString(properties, DisplayedNameStringProperty);
        if (string.IsNullOrWhiteSpace(name))
            name = _itemNames.ResolveAppropriateName(obj);
        return obj.StackSize > 1
            ? $"{obj.StackSize.ToString(CultureInfo.InvariantCulture)} {name}"
            : name;
    }

    private static int GetInt(PropertyBundle properties, uint id)
        => properties.Ints.TryGetValue(id, out int value) ? value : 0;

    private static string GetString(PropertyBundle properties, uint id)
        => properties.Strings.TryGetValue(id, out string? value) ? value : string.Empty;

    private static IEnumerable<UiElement> Descendants(UiElement? root)
    {
        if (root is null)
            yield break;
        foreach (UiElement child in root.Children)
        {
            yield return child;
            foreach (UiElement descendant in Descendants(child))
                yield return descendant;
        }
    }

    public void OnShown()
    {
        _windowVisible = true;
    }

    public void OnHidden() => _windowVisible = false;

    private void HandleSelectionChanged(SelectionTransition transition)
    {
        if (!_windowVisible || _disposed)
            return;

        if (transition.SelectedObjectId is uint objectId && objectId != 0u)
        {
            _interaction.ExamineSelectedOrEnterMode(objectId);
            return;
        }

        _closeWindow();
    }

    private void HandleSpellComponentObjectChanged(ClientObject _)
        => RefreshSpellComponents();

    private void HandleSpellComponentObjectMoved(ClientObjectMove _)
        => RefreshSpellComponents();

    private void HandleSpellComponentObjectsCleared()
        => RefreshSpellComponents();

    private void RefreshSpellComponents()
    {
        if (_activeView != AppraisalView.Spell
            || _spellId == 0u
            || !_spellbook.TryGetMetadata(_spellId, out SpellMetadata metadata))
            return;

        IReadOnlyList<SpellExamineComponent> components =
            _spellComponents(_spellId);
        SetSpellText(_spellDisplay, BuildSpellDisplay(metadata, components));
        RebuildSpellFormula(components);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _selection.Changed -= HandleSelectionChanged;
        _objects.ObjectAdded -= HandleSpellComponentObjectChanged;
        _objects.ObjectMoved -= HandleSpellComponentObjectMoved;
        _objects.ObjectRemoved -= HandleSpellComponentObjectChanged;
        _objects.StackSizeUpdated -= HandleSpellComponentObjectChanged;
        _objects.Cleared -= HandleSpellComponentObjectsCleared;
        if (_close is not null)
            _close.OnClick = null;
        if (_inscriptionField is not null)
        {
            _inscriptionField.OnFocusGained = null;
            _inscriptionField.OnFocusLost = null;
            _inscriptionField.OnReadOnlyClick = null;
        }
        if (_inscriptionBackground is not null)
            _inscriptionBackground.OnClick = null;
        ClearSpellFormula();
        _spellIconHost.RemoveChild(_spellIcon);
        foreach (UiScrollbar scrollbar in Descendants(_layout.Root).OfType<UiScrollbar>())
            if (ReferenceEquals(scrollbar.Model, _itemText.Scroll)
                || (_inscriptionText is not null
                    && ReferenceEquals(scrollbar.Model, _inscriptionText.Scroll))
                || (_inscriptionField is not null
                    && ReferenceEquals(scrollbar.Model, _inscriptionField.Scroll)))
                scrollbar.Model = null;
    }
}

public enum AppraisalView
{
    Item,
    Creature,
    Character,
    Spell,
}
