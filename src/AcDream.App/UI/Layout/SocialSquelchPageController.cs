using System;
using System.Numerics;
using AcDream.Core.Social;

namespace AcDream.App.UI.Layout;

public sealed class SocialSquelchPageController
{
    private const uint ListBoxId = 0x1000053Eu;
    private const uint NameFieldId = 0x10000540u;
    private const uint RemoveButtonId = 0x10000547u;
    private const uint SquelchCharacterButtonId = 0x1000054Bu;
    private const uint SquelchAccountButtonId = 0x1000054Cu;

    public sealed record Actions(
        Action<string> SquelchCharacter,
        Action<string> SquelchAccount,
        Action<uint, string> RemoveCharacterSquelch,
        Action<string> RemoveAccountSquelch);

    private readonly UiTemplateListBox _listBox;
    private readonly SquelchState _squelch;
    private readonly Actions? _actions;
    private readonly UiField? _nameField;
    private long _lastRevision = long.MinValue;
    private (uint Guid, string Name, bool IsAccount)? _selected;

    private SocialSquelchPageController(
        UiTemplateListBox listBox,
        SquelchState squelch,
        Actions? actions,
        UiField? nameField)
    {
        _listBox = listBox;
        _squelch = squelch;
        _actions = actions;
        _nameField = nameField;
    }

    public static SocialSquelchPageController? Bind(
        UiElement pageRoot,
        SquelchState squelch,
        Func<uint, uint, UiElement?> templateResolver,
        Actions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(pageRoot);
        ArgumentNullException.ThrowIfNull(squelch);
        ArgumentNullException.ThrowIfNull(templateResolver);

        if (UiElement.FindDescendant(pageRoot, ListBoxId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[UI] SocialSquelchPageController: ListBox 0x{ListBoxId:X8} not "
                + "found — Squelch page will not populate.");
            return null;
        }
        listBox.TemplateResolver = templateResolver;

        uint scrollbarElementId = listBox.ScrollbarElementId;
        UiElement? scrollbarElement = scrollbarElementId == 0
            ? null
            : UiElement.FindDescendant(pageRoot, scrollbarElementId);
        if (scrollbarElement is UiScrollbar scrollbar)
            scrollbar.Model = listBox.Scroll;
        else
            Console.WriteLine(
                $"[UI] SocialSquelchPageController: scrollbar 0x{scrollbarElementId:X8} "
                + "not found — the Squelch list will not scroll.");

        var nameField = UiElement.FindDescendant(pageRoot, NameFieldId) as UiField;
        var controller = new SocialSquelchPageController(listBox, squelch, actions, nameField);
        controller.WireActions(pageRoot);
        controller.Refresh();
        return controller;
    }

    private void WireActions(UiElement pageRoot)
    {
        if (_actions is not { } actions) return;

        if (UiElement.FindDescendant(pageRoot, SquelchCharacterButtonId) is UiButton addCharacter)
            addCharacter.OnClick = () =>
            {
                string name = _nameField?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) return;
                actions.SquelchCharacter(name);
                _nameField?.SetText(string.Empty);
            };

        if (UiElement.FindDescendant(pageRoot, SquelchAccountButtonId) is UiButton addAccount)
            addAccount.OnClick = () =>
            {
                string name = _nameField?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) return;
                actions.SquelchAccount(name);
                _nameField?.SetText(string.Empty);
            };

        if (UiElement.FindDescendant(pageRoot, RemoveButtonId) is UiButton remove)
            remove.OnClick = () =>
            {
                if (_selected is not { } selected) return;
                if (selected.IsAccount)
                    actions.RemoveAccountSquelch(selected.Name);
                else
                    actions.RemoveCharacterSquelch(selected.Guid, selected.Name);
            };
    }

    public void Tick()
    {
        long revision = _squelch.Revision;
        if (revision == _lastRevision) return;
        Refresh();
    }

    private void Refresh()
    {
        long revision = _squelch.Revision;
        _listBox.Flush();
        SquelchDatabase database = _squelch.Snapshot();

        bool allRowsResolved = true;
        bool selectedStillPresent = false;
        foreach ((uint guid, SquelchInfo character) in database.Characters)
        {
            allRowsResolved &= AddRow(character.Name, guid, isAccount: false);
            if (_selected is { IsAccount: false } s && s.Guid == guid)
                selectedStillPresent = true;
        }
        foreach (string accountName in database.Accounts.Keys)
        {
            allRowsResolved &= AddRow(accountName, 0u, isAccount: true);
            if (_selected is { IsAccount: true } s && s.Name == accountName)
                selectedStillPresent = true;
        }
        if (!selectedStillPresent) _selected = null;

        // SF-3: only latch the revision once the rebuild actually reflects it —
        // a resolver miss must not silently swallow a future revision bump.
        if (allRowsResolved) _lastRevision = revision;
    }

    private bool AddRow(string name, uint guid, bool isAccount)
    {
        UiElement? row = _listBox.AddItemFromTemplateList(0);
        if (row is null) return false;
        if (SocialPanelRowText.FindDeepest(row) is { } text)
        {
            text.LinesProvider = () => [new UiText.Line(name, Vector4.One)];
            text.OnClick = () => _selected = (guid, name, isAccount);
        }
        return true;
    }
}
