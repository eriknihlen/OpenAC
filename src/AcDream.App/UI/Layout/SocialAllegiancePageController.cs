using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;

namespace AcDream.App.UI.Layout;

public sealed class SocialAllegiancePageController
{
    private const uint SelfNameTextId = 0x10000251u;
    private const uint SelfFollowersTextId = 0x10000252u;
    private const uint SelfRankTextId = 0x10000253u;
    private const uint MonarchFieldId = 0x10000255u;
    private const uint MonarchLabelTextId = 0x10000256u;
    private const uint MonarchNameTextId = 0x10000257u;
    private const uint MonarchFollowersTextId = 0x10000258u;
    private const uint PatronFieldId = 0x1000025Au;
    private const uint PatronNameTextId = 0x1000025Cu;
    private const uint VassalListBoxId = 0x10000260u;
    private const uint IgnoreRequestsCheckboxId = 0x10000262u;
    private const uint SwearButtonId = 0x10000263u;
    private const uint BreakButtonId = 0x10000264u;
    private const uint KickButtonId = 0x10000265u;

    private const uint MonarchIsPatronSubBlockId = 0x10000490u;

    private const uint ExperiencePassedUpTextId = 0x10000492u;

    // ── Row-template element ids (same doc — the 3-part vassal row) ──
    private const uint RowNameTextId = 0x10000268u;
    private const uint RowExperiencePassedUpTextId = 0x10000269u;
    private const uint RowOfflineMarkerId = 0x100004AAu;

    private const uint StringTableId = 0x23000001u;
    private const uint OptionStringTableId = 0x23000003u;

    private static readonly Vector4 TextColor = Vector4.One;

    private static readonly IReadOnlyList<UiText.Line> BlankLine =
        [new UiText.Line(" ", TextColor)];
    private static readonly Func<IReadOnlyList<UiText.Line>> BlankLineProvider = () => BlankLine;

    private const string BlankSentinel = " blank ";

    public sealed record Bindings(
        Func<RuntimeAllegianceSnapshot> Snapshot,
        Func<RuntimeAllegianceMemberSnapshot?> Monarch,
        Func<uint, RuntimeAllegianceMemberSnapshot?> Patron,
        Func<uint, RuntimeAllegianceMemberSnapshot?> Member,
        Func<uint, IEnumerable<RuntimeAllegianceMemberSnapshot>> Vassals,
        Func<uint, RuntimeCommandResult> Swear,
        Func<uint, RuntimeCommandResult> Break,
        Func<uint, RuntimeCommandResult> Kick,
        Func<bool, RuntimeCommandResult> SetUpdateSubscription,
        AcDream.Core.Selection.SelectionState Selection,
        Func<uint> LocalPlayerGuid,
        Func<CharacterOptionId, bool> CurrentCharacterOption,
        Action<CharacterOptionId, bool> SetCharacterOption,
        Func<uint, uint, UiElement?> TemplateResolver,
        Func<uint, uint, string?> ResolveString,
        Func<uint, string?> ResolveWorldObjectName,
        Func<string, Action<bool>, uint> ShowConfirmation,
        Func<string, string, string?>? ResolvePlayerTemplate = null,
        Func<uint, uint, IReadOnlyDictionary<uint, string>, string?>? ResolveTemplate = null);

    /// <summary>
    /// The authored "experience passed up" entry every passed-up number on
    /// this page goes through — the monarch-is-patron block, the patron
    /// block and each vassal row alike.
    /// </summary>
    private static readonly uint ExperiencePassedUpTemplateKey =
        DatStringResolver.ComputeHash("ID_Allegiance_VassalExperiencePassedUp");

    /// <summary>The entry's one variable.</summary>
    private static readonly uint ValueVariable = DatStringResolver.ComputeHash("VALUE");

    private readonly record struct VassalRowWidgets(
        UiText? Name,
        UiText? ExperiencePassedUp,
        UiElement? OfflineMarker);

    private readonly Bindings _bindings;

    private readonly UiText? _selfName;
    private readonly UiText? _selfFollowers;
    private readonly UiText? _selfRank;

    private readonly UiElement _monarchField;
    private readonly UiText? _monarchLabel;
    private readonly UiText? _monarchName;
    private readonly UiText? _monarchFollowers;
    private readonly UiElement? _monarchIsPatronSubBlock;
    private readonly UiText? _monarchExperiencePassedUp;

    private readonly UiElement _patronField;
    private readonly UiText? _patronName;
    private readonly UiText? _patronExperiencePassedUp;

    private readonly UiTemplateListBox? _vassalListBox;
    private readonly UiButton? _ignoreRequestsCheckbox;
    private readonly UiButton? _swearButton;
    private readonly UiButton? _breakButton;
    private readonly UiButton? _kickButton;

    private readonly string? _monarchLabelCaption;
    private readonly string? _patronSlashMonarchLabelCaption;

    private readonly Dictionary<uint, VassalRowWidgets> _rows = new();
    private readonly HashSet<uint> _vassalGuids = new();

    private uint _selectedVassalGuid;
    private long _lastRosterRevision = long.MinValue;

    private bool _subscribed;

    private string? _lastSelfName;
    private string? _lastSelfFollowers;
    private string? _lastSelfRank;
    private string? _lastMonarchName;
    private string? _lastMonarchFollowers;
    private string? _lastMonarchExperiencePassedUp;
    private string? _lastPatronName;
    private string? _lastPatronExperiencePassedUp;

    private SocialAllegiancePageController(
        Bindings bindings,
        UiText? selfName,
        UiText? selfFollowers,
        UiText? selfRank,
        UiElement monarchField,
        UiText? monarchLabel,
        UiText? monarchName,
        UiText? monarchFollowers,
        UiElement? monarchIsPatronSubBlock,
        UiText? monarchExperiencePassedUp,
        UiElement patronField,
        UiText? patronName,
        UiText? patronExperiencePassedUp,
        UiTemplateListBox? vassalListBox,
        UiButton? ignoreRequestsCheckbox,
        UiButton? swearButton,
        UiButton? breakButton,
        UiButton? kickButton,
        string? monarchLabelCaption,
        string? patronSlashMonarchLabelCaption)
    {
        _bindings = bindings;
        _selfName = selfName;
        _selfFollowers = selfFollowers;
        _selfRank = selfRank;
        _monarchField = monarchField;
        _monarchLabel = monarchLabel;
        _monarchName = monarchName;
        _monarchFollowers = monarchFollowers;
        _monarchIsPatronSubBlock = monarchIsPatronSubBlock;
        _monarchExperiencePassedUp = monarchExperiencePassedUp;
        _patronField = patronField;
        _patronName = patronName;
        _patronExperiencePassedUp = patronExperiencePassedUp;
        _vassalListBox = vassalListBox;
        _ignoreRequestsCheckbox = ignoreRequestsCheckbox;
        _swearButton = swearButton;
        _breakButton = breakButton;
        _kickButton = kickButton;
        _monarchLabelCaption = monarchLabelCaption;
        _patronSlashMonarchLabelCaption = patronSlashMonarchLabelCaption;
    }

    public static SocialAllegiancePageController? Bind(UiElement pageRoot, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(pageRoot);
        ArgumentNullException.ThrowIfNull(bindings);

        if (UiElement.FindDescendant(pageRoot, MonarchFieldId) is not { } monarchField
            || UiElement.FindDescendant(pageRoot, PatronFieldId) is not { } patronField)
        {
            Console.WriteLine(
                "[UI] SocialAllegiancePageController: monarch/patron field "
                + $"containers (0x{MonarchFieldId:X8}/0x{PatronFieldId:X8}) not found — "
                + "allegiance page will not present.");
            return null;
        }

        UiElement? monarchIsPatronSubBlock =
            UiElement.FindDescendant(monarchField, MonarchIsPatronSubBlockId);
        UiText? monarchExperiencePassedUp = monarchIsPatronSubBlock is null
            ? null
            : UiElement.FindDescendant(monarchIsPatronSubBlock, ExperiencePassedUpTextId) as UiText;
        UiText? patronExperiencePassedUp =
            UiElement.FindDescendant(patronField, ExperiencePassedUpTextId) as UiText;

        UiText? selfName = UiElement.FindDescendant(pageRoot, SelfNameTextId) as UiText;
        UiText? selfFollowers = UiElement.FindDescendant(pageRoot, SelfFollowersTextId) as UiText;
        UiText? selfRank = UiElement.FindDescendant(pageRoot, SelfRankTextId) as UiText;
        UiText? monarchLabel = UiElement.FindDescendant(monarchField, MonarchLabelTextId) as UiText;
        UiText? monarchName = UiElement.FindDescendant(monarchField, MonarchNameTextId) as UiText;
        UiText? monarchFollowers = UiElement.FindDescendant(monarchField, MonarchFollowersTextId) as UiText;
        UiText? patronName = UiElement.FindDescendant(patronField, PatronNameTextId) as UiText;

        UiTemplateListBox? vassalListBox =
            UiElement.FindDescendant(pageRoot, VassalListBoxId) as UiTemplateListBox;
        if (vassalListBox is null)
            Console.WriteLine(
                $"[UI] SocialAllegiancePageController: ListBox 0x{VassalListBoxId:X8} not "
                + "found — the vassal list will not populate.");
        else
        {
            vassalListBox.TemplateResolver = bindings.TemplateResolver;
            uint scrollbarElementId = vassalListBox.ScrollbarElementId;
            UiElement? scrollbarElement = scrollbarElementId == 0
                ? null
                : UiElement.FindDescendant(pageRoot, scrollbarElementId);
            if (scrollbarElement is UiScrollbar scrollbar)
                scrollbar.Model = vassalListBox.Scroll;
            else
                Console.WriteLine(
                    $"[UI] SocialAllegiancePageController: scrollbar 0x{scrollbarElementId:X8} "
                    + "not found — the vassal list will not scroll.");
        }

        UiButton? ignoreRequestsCheckbox =
            UiElement.FindDescendant(pageRoot, IgnoreRequestsCheckboxId) as UiButton;
        UiButton? swearButton = UiElement.FindDescendant(pageRoot, SwearButtonId) as UiButton;
        UiButton? breakButton = UiElement.FindDescendant(pageRoot, BreakButtonId) as UiButton;
        UiButton? kickButton = UiElement.FindDescendant(pageRoot, KickButtonId) as UiButton;

        string? monarchLabelCaption = bindings.ResolveString(
            StringTableId, DatStringResolver.ComputeHash("ID_Allegiance_MonarchLabel"));
        string? patronSlashMonarchLabelCaption = bindings.ResolveString(
            StringTableId, DatStringResolver.ComputeHash("ID_Allegiance_PatronSlashMonarchLabel"));

        var controller = new SocialAllegiancePageController(
            bindings,
            selfName, selfFollowers, selfRank,
            monarchField, monarchLabel, monarchName, monarchFollowers,
            monarchIsPatronSubBlock, monarchExperiencePassedUp,
            patronField, patronName, patronExperiencePassedUp,
            vassalListBox, ignoreRequestsCheckbox, swearButton, breakButton, kickButton,
            monarchLabelCaption, patronSlashMonarchLabelCaption);

        controller.WireButtons();
        controller.WireCheckbox();
        controller.Tick();

        controller.SetPageVisible(true);

        return controller;
    }

    private void WireButtons()
    {
        if (_swearButton is not null)
            _swearButton.OnClick = OnSwearClick;
        if (_breakButton is not null)
            _breakButton.OnClick = OnBreakClick;
        if (_kickButton is not null)
            _kickButton.OnClick = OnKickClick;
    }

    private void WireCheckbox()
    {
        if (_ignoreRequestsCheckbox is null) return;

        const string labelKey = "ID_PlayerOption_IgnoreAllegianceRequests";
        string? label = _bindings.ResolveString(OptionStringTableId, DatStringResolver.ComputeHash(labelKey));
        if (label is not null)
            _ignoreRequestsCheckbox.Label = label;
        else
            Console.WriteLine(
                $"[UI] SocialAllegiancePageController: label '{labelKey}' did not resolve — "
                + "checkbox renders with no caption rather than invented English.");

        string? tooltip = _bindings.ResolveString(
            OptionStringTableId, DatStringResolver.ComputeHash(labelKey + "_Help"));
        if (tooltip is not null)
            _ignoreRequestsCheckbox.TooltipText = tooltip;

        _ignoreRequestsCheckbox.SuppressSelfToggle = true;
        _ignoreRequestsCheckbox.OnClick = () =>
        {
            bool next = !_bindings.CurrentCharacterOption(CharacterOptionId.IgnoreAllegianceRequests);
            _bindings.SetCharacterOption(CharacterOptionId.IgnoreAllegianceRequests, next);
        };
    }

    private void OnSwearClick()
    {
        if (_bindings.Selection.SelectedObjectId is not { } targetGuid) return;
        string? name = _bindings.ResolveWorldObjectName(targetGuid);
        if (string.IsNullOrEmpty(name)) return;

        string message = _bindings.ResolvePlayerTemplate?.Invoke(
            "ID_Allegiance_SwearConfirmation", name) ?? name;
        _bindings.ShowConfirmation(message, accepted =>
        {
            if (accepted) _bindings.Swear(targetGuid);
        });
    }

    private void OnBreakClick()
    {
        uint selfGuid = _bindings.LocalPlayerGuid();
        if (_bindings.Patron(selfGuid) is not { } patron) return;

        string message = _bindings.ResolvePlayerTemplate?.Invoke(
            "ID_Allegiance_BreakConfirmation", patron.Name) ?? patron.Name;
        _bindings.ShowConfirmation(message, accepted =>
        {
            if (accepted) _bindings.Break(patron.CharacterId);
        });
    }

    private void OnKickClick()
    {
        if (_selectedVassalGuid == 0u) return;
        if (_bindings.Member(_selectedVassalGuid) is not { } vassal) return;

        uint vassalGuid = _selectedVassalGuid;
        string message = _bindings.ResolvePlayerTemplate?.Invoke(
            "ID_Allegiance_KickConfirmation", vassal.Name) ?? vassal.Name;
        _bindings.ShowConfirmation(message, accepted =>
        {
            if (accepted) _bindings.Kick(vassalGuid);
        });
    }

    public void Tick()
    {
        RuntimeAllegianceSnapshot snapshot = _bindings.Snapshot();
        uint selfGuid = _bindings.LocalPlayerGuid();
        RuntimeAllegianceMemberSnapshot? monarch = snapshot.HasProfile ? _bindings.Monarch() : null;
        RuntimeAllegianceMemberSnapshot? patron = snapshot.HasProfile ? _bindings.Patron(selfGuid) : null;

        RefreshSelfBlock(snapshot);
        RefreshMonarchBlock(snapshot, monarch, patron);
        RefreshPatronBlock(snapshot, monarch, patron);

        if (snapshot.Revision != _lastRosterRevision)
        {
            _lastRosterRevision = snapshot.Revision;
            RefreshRoster(selfGuid);
        }

        RefreshCheckboxSelection();
        RefreshButtonStates(snapshot, selfGuid, patron);
    }

    private void RefreshSelfBlock(RuntimeAllegianceSnapshot snapshot)
    {
        SetLine(_selfName, ref _lastSelfName, snapshot.AllegianceName, TextColor);
        SetLine(_selfFollowers, ref _lastSelfFollowers, $"Followers: {snapshot.TotalVassals}", TextColor);
        SetLine(_selfRank, ref _lastSelfRank, $"Rank: [{snapshot.Rank}]", TextColor);
    }

    private void RefreshMonarchBlock(
        RuntimeAllegianceSnapshot snapshot,
        RuntimeAllegianceMemberSnapshot? monarch,
        RuntimeAllegianceMemberSnapshot? patron)
    {
        bool hasMonarch = monarch is { } m && m.CharacterId != _bindings.LocalPlayerGuid();
        _monarchField.Visible = hasMonarch;

        if (!hasMonarch)
        {
            SetProvider(_monarchName, ref _lastMonarchName, BlankSentinel, BlankLineProvider);
            SetProvider(_monarchFollowers, ref _lastMonarchFollowers, BlankSentinel, BlankLineProvider);
            if (_monarchIsPatronSubBlock is not null) _monarchIsPatronSubBlock.Visible = false;
            return;
        }

        RuntimeAllegianceMemberSnapshot monarchData = monarch!.Value;
        SetLine(_monarchName, ref _lastMonarchName, monarchData.Name, TextColor);
        SetLine(
            _monarchFollowers,
            ref _lastMonarchFollowers,
            $"Followers: {(snapshot.TotalMembers >= 1u ? snapshot.TotalMembers - 1u : 0u)}",
            TextColor);
        _monarchField.Enabled = monarchData.IsLoggedIn;

        bool patronIsMonarch = patron is { } p && p.CharacterId == monarchData.CharacterId;
        string? label = patronIsMonarch ? _patronSlashMonarchLabelCaption : _monarchLabelCaption;
        if (_monarchLabel is not null && label is not null)
            _monarchLabel.LinesProvider = () => [new UiText.Line(label, TextColor)];

        if (_monarchIsPatronSubBlock is not null)
            _monarchIsPatronSubBlock.Visible = patronIsMonarch;

        if (patronIsMonarch)
        {
            uint tithed = _bindings.Member(_bindings.LocalPlayerGuid())?.CpTithed ?? 0u;
            SetLine(
                _monarchExperiencePassedUp,
                ref _lastMonarchExperiencePassedUp,
                ExperiencePassedUpText(tithed),
                TextColor);
        }
    }

    /// <summary>
    /// The passed-up experience as the page shows it: the number with its
    /// digits grouped (1,500,000 — the game's number text always groups),
    /// placed into the authored entry when the string table has it.
    /// </summary>
    private string ExperiencePassedUpText(uint tithed)
    {
        string value = tithed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return _bindings.ResolveTemplate?.Invoke(
                StringTableId,
                ExperiencePassedUpTemplateKey,
                new Dictionary<uint, string> { [ValueVariable] = value })
            ?? value;
    }

    private void RefreshPatronBlock(
        RuntimeAllegianceSnapshot snapshot,
        RuntimeAllegianceMemberSnapshot? monarch,
        RuntimeAllegianceMemberSnapshot? patron)
    {
        bool hasPatron = patron is { } p
            && (monarch is not { } m || p.CharacterId != m.CharacterId);
        _patronField.Visible = hasPatron;

        if (!hasPatron)
        {
            SetProvider(_patronName, ref _lastPatronName, BlankSentinel, BlankLineProvider);
            return;
        }

        RuntimeAllegianceMemberSnapshot patronData = patron!.Value;
        SetLine(_patronName, ref _lastPatronName, patronData.Name, TextColor);
        _patronField.Enabled = patronData.IsLoggedIn;

        uint tithed = _bindings.Member(_bindings.LocalPlayerGuid())?.CpTithed ?? 0u;
        SetLine(
            _patronExperiencePassedUp,
            ref _lastPatronExperiencePassedUp,
            ExperiencePassedUpText(tithed),
            TextColor);
    }

    private void RefreshCheckboxSelection()
    {
        if (_ignoreRequestsCheckbox is null) return;
        _ignoreRequestsCheckbox.Selected =
            _bindings.CurrentCharacterOption(CharacterOptionId.IgnoreAllegianceRequests);
    }

    private void RefreshButtonStates(
        RuntimeAllegianceSnapshot snapshot,
        uint selfGuid,
        RuntimeAllegianceMemberSnapshot? patron)
    {
        if (_swearButton is not null)
        {
            uint? targetGuid = _bindings.Selection.SelectedObjectId;
            bool targetValid = targetGuid is { } id
                && id != selfGuid
                && _bindings.Member(id) is null;
            _swearButton.Enabled = patron is null && targetValid;
        }

        if (_breakButton is not null)
            _breakButton.Enabled = patron is not null;

        if (_kickButton is not null)
            _kickButton.Enabled = _selectedVassalGuid != 0u;
    }

    private void RefreshRoster(uint selfGuid)
    {
        if (_vassalListBox is null) return;

        var vassals = new List<RuntimeAllegianceMemberSnapshot>(_bindings.Vassals(selfGuid));

        bool membershipChanged = vassals.Count != _vassalGuids.Count;
        if (!membershipChanged)
        {
            foreach (RuntimeAllegianceMemberSnapshot vassal in vassals)
            {
                if (_vassalGuids.Contains(vassal.CharacterId)) continue;
                membershipChanged = true;
                break;
            }
        }

        if (membershipChanged)
            RebuildRoster(vassals);
        else
            foreach (RuntimeAllegianceMemberSnapshot vassal in vassals)
                UpdateRow(vassal);

        if (_selectedVassalGuid != 0u && !_vassalGuids.Contains(_selectedVassalGuid))
            _selectedVassalGuid = 0u;
    }

    private void RebuildRoster(List<RuntimeAllegianceMemberSnapshot> vassals)
    {
        _vassalListBox!.FlushPreservingScroll();
        _rows.Clear();

        _vassalGuids.Clear();
        foreach (RuntimeAllegianceMemberSnapshot vassal in vassals)
            _vassalGuids.Add(vassal.CharacterId);

        foreach (RuntimeAllegianceMemberSnapshot vassal in vassals)
        {
            UiElement? row = _vassalListBox.AddItemFromTemplateList(0);
            if (row is null)
            {
                Console.WriteLine(
                    "[UI] SocialAllegiancePageController: vassal row template did not "
                    + $"build for guid 0x{vassal.CharacterId:X8}.");
                continue;
            }

            var widgets = new VassalRowWidgets(
                UiElement.FindDescendant(row, RowNameTextId) as UiText,
                UiElement.FindDescendant(row, RowExperiencePassedUpTextId) as UiText,
                UiElement.FindDescendant(row, RowOfflineMarkerId));
            _rows[vassal.CharacterId] = widgets;

            if (widgets.Name is { } nameText)
            {
                uint guid = vassal.CharacterId;
                nameText.OnClick = () => SelectVassal(guid);
            }
        }

        foreach (RuntimeAllegianceMemberSnapshot vassal in vassals)
            UpdateRow(vassal);
    }

    private void UpdateRow(RuntimeAllegianceMemberSnapshot vassal)
    {
        if (!_rows.TryGetValue(vassal.CharacterId, out VassalRowWidgets widgets)) return;

        if (widgets.Name is { } nameText)
        {
            string name = vassal.Name;
            nameText.LinesProvider = () => [new UiText.Line(name, TextColor)];
        }

        if (widgets.ExperiencePassedUp is { } xpText)
        {
            string tithed = ExperiencePassedUpText(vassal.CpTithed);
            xpText.LinesProvider = () => [new UiText.Line(tithed, TextColor)];
        }

        if (widgets.OfflineMarker is not null)
            widgets.OfflineMarker.Visible = !vassal.IsLoggedIn;
    }

    private void SelectVassal(uint guid)
    {
        if (_selectedVassalGuid == guid) return;
        _selectedVassalGuid = guid;
    }

    public void SetPageVisible(bool visible)
    {
        if (_subscribed == visible) return;
        if (_bindings.SetUpdateSubscription(visible).Status == RuntimeCommandStatus.Accepted)
            _subscribed = visible;
    }

    public void ResetPageVisibleLatch() => _subscribed = false;

    public void RedeclareAfterWorldEntry()
    {
        if (_bindings.SetUpdateSubscription(true).Status == RuntimeCommandStatus.Accepted)
            _subscribed = true;
    }

    private static void SetLine(UiText? text, ref string? lastValue, string newValue, Vector4 color)
    {
        if (text is null) return;
        if (lastValue == newValue) return;
        lastValue = newValue;
        text.LinesProvider = () => [new UiText.Line(newValue, color)];
    }

    private static void SetProvider(
        UiText? text,
        ref string? lastValue,
        string? sentinelValue,
        Func<IReadOnlyList<UiText.Line>> provider)
    {
        if (text is null) return;
        if (lastValue == sentinelValue) return;
        lastValue = sentinelValue;
        text.LinesProvider = provider;
    }
}
