using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Runtime;

namespace AcDream.App.UI.Layout;

public sealed class SocialFellowshipPageController
{
    private const uint ListBoxId = 0x10000279u;
    private const uint NameEntryBoxId = 0x1000026Fu;
    private const uint CreateButtonId = 0x10000274u;
    private const uint FellowshipNameTextId = 0x10000276u;
    private const uint LeaderButtonId = 0x1000027Bu;
    private const uint QuitButtonId = 0x1000027Cu;
    private const uint OpenButtonId = 0x1000027Du;
    private const uint RecruitButtonId = 0x1000027Eu;
    private const uint DismissButtonId = 0x1000027Fu;
    private const uint DisbandButtonId = 0x10000280u;
    private const uint IgnoreRequestsCheckboxId = 0x10000270u;
    private const uint AutoAcceptCheckboxId = 0x10000271u;
    private const uint ShareXpCheckboxId = 0x10000272u;
    private const uint ShareLootCheckboxId = 0x10000273u;

    private const uint RowNameBandId = 0x10000282u;
    private const uint RowNameTextId = 0x10000283u;
    private const uint RowStatsTextId = 0x10000284u;
    private const uint RowHealthMeterId = 0x10000285u;
    private const uint RowStaminaMeterId = 0x10000287u;
    private const uint RowManaMeterId = 0x10000289u;

    private const uint OptionStringTableId = 0x23000003u;
    private const uint FellowshipStringTableId = 0x23000001u;

    // The authored "level/share%" entry of the fellowship string table and
    // its two variable ids.
    private const uint StatsTemplateKey = 0x003B5A03u;
    private const uint StatsLevelVariable = 5286556u;
    private const uint StatsPercentVariable = 174673717u;

    private static readonly float[] EvenSplitPercentTable =
        [1.0f, 0.75f, 0.6f, 0.55f, 0.5f, 0.45f, 0.4f, 0.35f, 0.3111111f, 0.28f];

    private const int MaxFellowshipSize = 9;

    private static readonly Vector4 MemberNameColor = Vector4.One;

    private string? _lastFellowshipName;
    private Func<IReadOnlyList<UiText.Line>>? _fellowshipNameLinesProvider;

    public sealed record Bindings(
        Func<RuntimeFellowshipSnapshot> Snapshot,
        Func<IEnumerable<RuntimeFellowMemberSnapshot>> Members,
        Func<uint, uint, UiElement?> TemplateResolver,
        Func<string, bool, RuntimeCommandResult> Create,
        Func<uint, RuntimeCommandResult> Recruit,
        Func<uint, RuntimeCommandResult> Dismiss,
        Func<bool, RuntimeCommandResult> Quit,
        Func<uint, RuntimeCommandResult> AssignLeader,
        Func<bool, RuntimeCommandResult> SetOpen,
        Func<bool, RuntimeCommandResult> SetPanelOpen,
        SelectionState Selection,
        Func<uint> LocalPlayerGuid,
        Func<CharacterOptionId, bool> CurrentCharacterOption,
        Action<CharacterOptionId, bool> SetCharacterOption,
        Func<uint, uint, string?> ResolveString,
        Func<uint, uint, IReadOnlyDictionary<uint, string>, string?>? ResolveTemplate = null,
        Func<uint, long>? ExperienceToRaiseLevel = null);

    private readonly record struct FellowRowWidgets(
        UiDatElement? NameBand,
        UiText? Name,
        UiText? Stats,
        UiMeter? Health,
        UiMeter? Stamina,
        UiMeter? Mana);

    private readonly UiElement _notInFellowshipFrame;
    private readonly UiElement _inFellowshipFrame;
    private readonly Bindings _bindings;

    private readonly UiTemplateListBox? _listBox;
    private readonly UiField? _nameField;
    private readonly UiButton? _createButton;
    private readonly UiText? _fellowshipNameText;
    private readonly UiButton? _leaderButton;
    private readonly UiButton? _quitButton;
    private readonly UiButton? _openButton;
    private readonly UiButton? _recruitButton;
    private readonly UiButton? _dismissButton;
    private readonly UiButton? _disbandButton;
    private readonly UiButton? _ignoreRequestsCheckbox;
    private readonly UiButton? _autoAcceptCheckbox;
    private readonly UiButton? _shareXpCheckbox;
    private readonly UiButton? _shareLootCheckbox;

    private readonly Dictionary<uint, FellowRowWidgets> _rows = new();

    private readonly HashSet<uint> _memberGuids = new();

    private uint _selectedFellowGuid;
    private long _lastRosterRevision = long.MinValue;
    private bool? _lastOpenState;
    private bool _pageVisible;

    private readonly string? _openCaption;
    private readonly string? _closeCaption;

    private SocialFellowshipPageController(
        UiElement notInFellowshipFrame,
        UiElement inFellowshipFrame,
        Bindings bindings,
        UiTemplateListBox? listBox,
        UiField? nameField,
        UiButton? createButton,
        UiText? fellowshipNameText,
        UiButton? leaderButton,
        UiButton? quitButton,
        UiButton? openButton,
        UiButton? recruitButton,
        UiButton? dismissButton,
        UiButton? disbandButton,
        UiButton? ignoreRequestsCheckbox,
        UiButton? autoAcceptCheckbox,
        UiButton? shareXpCheckbox,
        UiButton? shareLootCheckbox,
        string? openCaption,
        string? closeCaption)
    {
        _notInFellowshipFrame = notInFellowshipFrame;
        _inFellowshipFrame = inFellowshipFrame;
        _bindings = bindings;
        _listBox = listBox;
        _nameField = nameField;
        _createButton = createButton;
        _fellowshipNameText = fellowshipNameText;
        _leaderButton = leaderButton;
        _quitButton = quitButton;
        _openButton = openButton;
        _recruitButton = recruitButton;
        _dismissButton = dismissButton;
        _disbandButton = disbandButton;
        _ignoreRequestsCheckbox = ignoreRequestsCheckbox;
        _autoAcceptCheckbox = autoAcceptCheckbox;
        _shareXpCheckbox = shareXpCheckbox;
        _shareLootCheckbox = shareLootCheckbox;
        _openCaption = openCaption;
        _closeCaption = closeCaption;
    }

    public static SocialFellowshipPageController? Bind(UiElement pageRoot, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(pageRoot);
        ArgumentNullException.ThrowIfNull(bindings);

        if (UiElement.FindDescendant(pageRoot, 0x1000026Bu) is not { } notIn
            || UiElement.FindDescendant(pageRoot, 0x10000275u) is not { } inFellowship)
        {
            Console.WriteLine(
                "[UI] SocialFellowshipPageController: empty/full frame pair "
                + "(0x1000026B/0x10000275) not found — fellowship page will not "
                + "swap its empty state.");
            return null;
        }

        UiTemplateListBox? listBox = UiElement.FindDescendant(pageRoot, ListBoxId) as UiTemplateListBox;
        if (listBox is null)
            Console.WriteLine(
                $"[UI] SocialFellowshipPageController: ListBox 0x{ListBoxId:X8} not "
                + "found — the fellowship roster will not populate.");
        else
        {
            listBox.TemplateResolver = bindings.TemplateResolver;
            uint scrollbarElementId = listBox.ScrollbarElementId;
            UiElement? scrollbarElement = scrollbarElementId == 0
                ? null
                : UiElement.FindDescendant(pageRoot, scrollbarElementId);
            if (scrollbarElement is UiScrollbar scrollbar)
                scrollbar.Model = listBox.Scroll;
            else
                Console.WriteLine(
                    $"[UI] SocialFellowshipPageController: scrollbar 0x{scrollbarElementId:X8} "
                    + "not found — the fellowship roster will not scroll.");
        }

        UiField? nameField = UiElement.FindDescendant(pageRoot, NameEntryBoxId) as UiField;
        if (nameField is null)
            Console.WriteLine(
                $"[UI] SocialFellowshipPageController: name-entry field 0x{NameEntryBoxId:X8} "
                + "not found (or not authored Editable) — Create will not read a typed name.");

        UiButton? createButton = UiElement.FindDescendant(pageRoot, CreateButtonId) as UiButton;
        UiText? fellowshipNameText = UiElement.FindDescendant(pageRoot, FellowshipNameTextId) as UiText;
        UiButton? leaderButton = UiElement.FindDescendant(pageRoot, LeaderButtonId) as UiButton;
        UiButton? quitButton = UiElement.FindDescendant(pageRoot, QuitButtonId) as UiButton;
        UiButton? openButton = UiElement.FindDescendant(pageRoot, OpenButtonId) as UiButton;
        UiButton? recruitButton = UiElement.FindDescendant(pageRoot, RecruitButtonId) as UiButton;
        UiButton? dismissButton = UiElement.FindDescendant(pageRoot, DismissButtonId) as UiButton;
        UiButton? disbandButton = UiElement.FindDescendant(pageRoot, DisbandButtonId) as UiButton;
        UiButton? ignoreRequestsCheckbox = UiElement.FindDescendant(pageRoot, IgnoreRequestsCheckboxId) as UiButton;
        UiButton? autoAcceptCheckbox = UiElement.FindDescendant(pageRoot, AutoAcceptCheckboxId) as UiButton;
        UiButton? shareXpCheckbox = UiElement.FindDescendant(pageRoot, ShareXpCheckboxId) as UiButton;
        UiButton? shareLootCheckbox = UiElement.FindDescendant(pageRoot, ShareLootCheckboxId) as UiButton;

        string? openCaption = bindings.ResolveString(
            FellowshipStringTableId, DatStringResolver.ComputeHash("ID_Fellowship_OpenFellowshipButtonText"));
        string? closeCaption = bindings.ResolveString(
            FellowshipStringTableId, DatStringResolver.ComputeHash("ID_Fellowship_CloseFellowshipButtonText"));
        if (openCaption is null || closeCaption is null)
            Console.WriteLine(
                "[UI] SocialFellowshipPageController: Open/Close Fellowship button caption(s) "
                + "did not resolve — the button keeps its imported caption rather than "
                + "invented English.");

        var controller = new SocialFellowshipPageController(
            notIn,
            inFellowship,
            bindings,
            listBox,
            nameField,
            createButton,
            fellowshipNameText,
            leaderButton,
            quitButton,
            openButton,
            recruitButton,
            dismissButton,
            disbandButton,
            ignoreRequestsCheckbox,
            autoAcceptCheckbox,
            shareXpCheckbox,
            shareLootCheckbox,
            openCaption,
            closeCaption);

        controller.WireButtons();
        controller.WireCheckboxes();
        controller.Tick();
        return controller;
    }

    private void WireButtons()
    {
        if (_createButton is not null)
            _createButton.OnClick = () =>
            {
                string name = _nameField?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) return;
                bool shareXp = _bindings.CurrentCharacterOption(CharacterOptionId.FellowshipShareXP);
                _bindings.Create(name, shareXp);
            };

        if (_recruitButton is not null)
            _recruitButton.OnClick = () =>
            {
                if (_bindings.Selection.SelectedObjectId is { } targetGuid)
                    _bindings.Recruit(targetGuid);
            };

        if (_dismissButton is not null)
            _dismissButton.OnClick = () =>
            {
                if (_selectedFellowGuid != 0u) _bindings.Dismiss(_selectedFellowGuid);
            };

        if (_leaderButton is not null)
            _leaderButton.OnClick = () =>
            {
                if (_selectedFellowGuid != 0u) _bindings.AssignLeader(_selectedFellowGuid);
            };

        if (_quitButton is not null)
            _quitButton.OnClick = () => _bindings.Quit(false);
        if (_disbandButton is not null)
            _disbandButton.OnClick = () => _bindings.Quit(true);

        if (_openButton is not null)
            _openButton.OnClick = () =>
            {
                RuntimeFellowshipSnapshot snapshot = _bindings.Snapshot();
                bool newOpenState = !snapshot.IsOpen;
                _bindings.SetOpen(newOpenState);

                _lastOpenState = newOpenState;
                string? label = newOpenState ? _closeCaption : _openCaption;
                if (label is not null)
                    _openButton.Label = label;
            };
    }

    private void WireCheckboxes()
    {
        BindCheckbox(_ignoreRequestsCheckbox, CharacterOptionId.IgnoreFellowshipRequests, "IgnoreFellowshipRequests");
        BindCheckbox(_autoAcceptCheckbox, CharacterOptionId.FellowshipAutoAcceptRequests, "FellowshipAutoAcceptRequests");
        BindCheckbox(_shareXpCheckbox, CharacterOptionId.FellowshipShareXP, "FellowshipShareXP");
        BindCheckbox(_shareLootCheckbox, CharacterOptionId.FellowshipShareLoot, "FellowshipShareLoot");
    }

    private void BindCheckbox(UiButton? checkbox, CharacterOptionId id, string retailName)
    {
        if (checkbox is null) return;

        string labelKey = $"ID_PlayerOption_{retailName}";
        string? label = _bindings.ResolveString(OptionStringTableId, DatStringResolver.ComputeHash(labelKey));
        if (label is not null)
            checkbox.Label = label;
        else
            Console.WriteLine(
                $"[UI] SocialFellowshipPageController: label '{labelKey}' did not resolve — "
                + "checkbox renders with no caption rather than invented English.");

        string? tooltip = _bindings.ResolveString(
            OptionStringTableId, DatStringResolver.ComputeHash(labelKey + "_Help"));
        if (tooltip is not null)
            checkbox.TooltipText = tooltip;

        checkbox.SuppressSelfToggle = true;
        checkbox.OnClick = () =>
        {
            bool next = !_bindings.CurrentCharacterOption(id);
            _bindings.SetCharacterOption(id, next);
        };
    }

    public void Tick()
    {
        RuntimeFellowshipSnapshot snapshot = _bindings.Snapshot();
        bool inFellowship = snapshot.IsInFellowship;
        _notInFellowshipFrame.Visible = !inFellowship;
        _inFellowshipFrame.Visible = inFellowship;

        RefreshCheckboxSelections();
        RefreshCreateButtonState();

        if (!inFellowship)
        {
            if (_rows.Count != 0)
            {
                _listBox?.Flush();
                _rows.Clear();
            }
            _memberGuids.Clear();
            _selectedFellowGuid = 0u;
            _lastRosterRevision = long.MinValue;
            _lastOpenState = null;
            _lastFellowshipName = null;
            _fellowshipNameLinesProvider = null;
            return;
        }

        RefreshFellowshipName(snapshot.Name);

        if (snapshot.Revision != _lastRosterRevision)
        {
            _lastRosterRevision = snapshot.Revision;
            RefreshRoster(snapshot);
        }

        SyncSelectionFromWorld();

        RefreshButtonStates(snapshot);
        RefreshOpenCaption(snapshot);
    }

    public void SetPageVisible(bool visible)
    {
        if (_pageVisible == visible) return;
        if (_bindings.SetPanelOpen(visible).Status == RuntimeCommandStatus.Accepted)
            _pageVisible = visible;
    }

    public void ResetPageVisibleLatch() => _pageVisible = false;

    private void RefreshCreateButtonState()
    {
        if (_createButton is null) return;
        string name = _nameField?.Text ?? string.Empty;
        _createButton.Enabled = !string.IsNullOrWhiteSpace(name);
    }

    private void RefreshCheckboxSelections()
    {
        if (_ignoreRequestsCheckbox is not null)
            _ignoreRequestsCheckbox.Selected =
                _bindings.CurrentCharacterOption(CharacterOptionId.IgnoreFellowshipRequests);
        if (_autoAcceptCheckbox is not null)
            _autoAcceptCheckbox.Selected =
                _bindings.CurrentCharacterOption(CharacterOptionId.FellowshipAutoAcceptRequests);
        if (_shareXpCheckbox is not null)
            _shareXpCheckbox.Selected =
                _bindings.CurrentCharacterOption(CharacterOptionId.FellowshipShareXP);
        if (_shareLootCheckbox is not null)
            _shareLootCheckbox.Selected =
                _bindings.CurrentCharacterOption(CharacterOptionId.FellowshipShareLoot);
    }

    private void RefreshFellowshipName(string name)
    {
        if (_fellowshipNameText is null) return;
        if (_fellowshipNameLinesProvider is not null && _lastFellowshipName == name) return;

        _lastFellowshipName = name;
        _fellowshipNameLinesProvider = () => [new UiText.Line(name, MemberNameColor)];
        _fellowshipNameText.LinesProvider = _fellowshipNameLinesProvider;
    }

    private void RefreshOpenCaption(RuntimeFellowshipSnapshot snapshot)
    {
        if (_openButton is null) return;
        if (_lastOpenState == snapshot.IsOpen) return;
        _lastOpenState = snapshot.IsOpen;

        string? label = snapshot.IsOpen ? _closeCaption : _openCaption;
        if (label is not null)
            _openButton.Label = label;
    }

    private void RefreshButtonStates(RuntimeFellowshipSnapshot snapshot)
    {
        uint selfGuid = _bindings.LocalPlayerGuid();
        bool isLeader = snapshot.LeaderGuid != 0u && snapshot.LeaderGuid == selfGuid;
        bool hasSelection = _selectedFellowGuid != 0u;
        bool selectedIsSelf = hasSelection && _selectedFellowGuid == selfGuid;

        if (_quitButton is not null) _quitButton.Enabled = true; // always, while in a fellowship
        if (_disbandButton is not null) _disbandButton.Enabled = isLeader;
        if (_openButton is not null) _openButton.Enabled = isLeader;
        if (_leaderButton is not null) _leaderButton.Enabled = isLeader && hasSelection && !selectedIsSelf;
        if (_dismissButton is not null) _dismissButton.Enabled = isLeader && hasSelection && !selectedIsSelf;

        if (_recruitButton is not null)
        {
            uint? targetGuid = _bindings.Selection.SelectedObjectId;
            bool targetValid = targetGuid is { } id && id != selfGuid && !_memberGuids.Contains(id);
            bool notFull = snapshot.MemberCount < MaxFellowshipSize;
            _recruitButton.Enabled = targetValid && notFull && (isLeader || snapshot.IsOpen);
        }
    }

    private void RefreshRoster(RuntimeFellowshipSnapshot snapshot)
    {
        if (_listBox is null) return;

        var members = new List<RuntimeFellowMemberSnapshot>(_bindings.Members());

        bool membershipChanged = members.Count != _memberGuids.Count;
        if (!membershipChanged)
        {
            foreach (RuntimeFellowMemberSnapshot member in members)
            {
                if (_memberGuids.Contains(member.Guid)) continue;
                membershipChanged = true;
                break;
            }
        }

        if (membershipChanged)
            RebuildRoster(members, snapshot);
        else
            foreach (RuntimeFellowMemberSnapshot member in members)
                UpdateRow(member, snapshot, members);

        if (_selectedFellowGuid != 0u && !_memberGuids.Contains(_selectedFellowGuid))
            _selectedFellowGuid = 0u;
    }

    private void RebuildRoster(List<RuntimeFellowMemberSnapshot> members, RuntimeFellowshipSnapshot snapshot)
    {
        _listBox!.FlushPreservingScroll();
        _rows.Clear();

        _memberGuids.Clear();
        foreach (RuntimeFellowMemberSnapshot member in members)
            _memberGuids.Add(member.Guid);

        foreach (RuntimeFellowMemberSnapshot member in members)
        {
            UiElement? row = _listBox.AddItemFromTemplateList(0);
            if (row is null)
            {
                Console.WriteLine(
                    "[UI] SocialFellowshipPageController: fellow row template did not "
                    + $"build for guid 0x{member.Guid:X8}.");
                continue;
            }

            var widgets = new FellowRowWidgets(
                UiElement.FindDescendant(row, RowNameBandId) as UiDatElement,
                UiElement.FindDescendant(row, RowNameTextId) as UiText,
                UiElement.FindDescendant(row, RowStatsTextId) as UiText,
                UiElement.FindDescendant(row, RowHealthMeterId) as UiMeter,
                UiElement.FindDescendant(row, RowStaminaMeterId) as UiMeter,
                UiElement.FindDescendant(row, RowManaMeterId) as UiMeter);
            _rows[member.Guid] = widgets;

            if (widgets.Name is { } nameText)
            {
                uint guid = member.Guid;
                nameText.OnClick = () => SelectFellow(guid);
            }
            if (widgets.Stats is { } statsText)
            {
                uint guid = member.Guid;
                statsText.OnClick = () => SelectFellow(guid);
                KeepClearOfScrollbar(row, statsText);
            }
        }

        foreach (RuntimeFellowMemberSnapshot member in members)
            UpdateRow(member, snapshot, members);
    }

    // The authored row is wider than the list, so its right-justified text
    // ends under the scrollbar; the text keeps the list's right edge instead.
    private void KeepClearOfScrollbar(UiElement row, UiText statsText)
    {
        float overlap = row.Width - (_listBox?.Width ?? row.Width);
        if (overlap > 0f && statsText.Width > overlap)
            statsText.Width -= overlap;
    }

    private void UpdateRow(
        RuntimeFellowMemberSnapshot member,
        RuntimeFellowshipSnapshot snapshot,
        IReadOnlyList<RuntimeFellowMemberSnapshot> members)
    {
        if (!_rows.TryGetValue(member.Guid, out FellowRowWidgets widgets)) return;

        if (widgets.Name is { } nameText)
        {
            string name = member.Name;
            nameText.LinesProvider = () => [new UiText.Line(name, MemberNameColor)];
        }

        if (widgets.NameBand is { } nameBand)
            nameBand.ActiveState = _selectedFellowGuid == member.Guid ? "Highlight" : "";

        if (widgets.Stats is { } statsText)
        {
            string text = FormatStatsText(member, snapshot, members);
            statsText.LinesProvider = () => [new UiText.Line(text, MemberNameColor)];
        }

        SetVitals(widgets.Health, member.CurrentHealth, member.MaxHealth);
        SetVitals(widgets.Stamina, member.CurrentStamina, member.MaxStamina);
        SetVitals(widgets.Mana, member.CurrentMana, member.MaxMana);
    }

    // "level/share%": no sharing shows 0; an even split uses the share
    // table; otherwise each fellow's share is the experience their next
    // level costs, over the sum of everyone's.
    private string FormatStatsText(
        RuntimeFellowMemberSnapshot member,
        RuntimeFellowshipSnapshot snapshot,
        IReadOnlyList<RuntimeFellowMemberSnapshot> members)
    {
        double share = 0d;
        if (snapshot.ShareXp)
        {
            if (snapshot.EvenXpSplit)
                share = EvenSplitPercent(snapshot.MemberCount);
            else if (_bindings.ExperienceToRaiseLevel is { } toRaise)
            {
                double sum = 0d;
                foreach (RuntimeFellowMemberSnapshot fellow in members)
                    sum += toRaise(fellow.Level);
                if (sum > 0d)
                    share = toRaise(member.Level) / sum;
            }
        }
        int percent = (int)(share * 100.0);

        string? authored = _bindings.ResolveTemplate?.Invoke(
            FellowshipStringTableId,
            StatsTemplateKey,
            new Dictionary<uint, string>
            {
                [StatsLevelVariable] = member.Level.ToString(),
                [StatsPercentVariable] = percent.ToString(),
            });
        return authored ?? $"{member.Level}/{percent}%";
    }

    private static float EvenSplitPercent(int memberCount) =>
        memberCount is >= 1 and <= 10 ? EvenSplitPercentTable[memberCount - 1] : 0f;

    private static void SetVitals(UiMeter? meter, uint current, uint max)
    {
        if (meter is null) return;
        meter.Fill = () => max > 0u ? (float)current / max : 0f;
        meter.Label = () => $"{current}/{max}";
    }

    private void SelectFellow(uint guid)
    {
        SetSelectedFellow(guid);
        _bindings.Selection.Select(guid, SelectionChangeSource.Social);
    }

    private void SyncSelectionFromWorld()
    {
        if (_bindings.Selection.SelectedObjectId is { } id && _memberGuids.Contains(id))
            SetSelectedFellow(id);
    }

    private void SetSelectedFellow(uint guid)
    {
        if (_selectedFellowGuid == guid) return;
        _selectedFellowGuid = guid;

        if (_rows.Count == 0) return;
        RuntimeFellowshipSnapshot snapshot = _bindings.Snapshot();
        List<RuntimeFellowMemberSnapshot> members = _bindings.Members().ToList();
        foreach (RuntimeFellowMemberSnapshot member in members)
            UpdateRow(member, snapshot, members);
    }
}
