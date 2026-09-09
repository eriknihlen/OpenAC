using System;
using System.Numerics;
using AcDream.Core.Social;

namespace AcDream.App.UI.Layout;

public sealed class SocialFriendsPageController
{
    private const uint ListBoxId = 0x10000517u;
    private const uint AddButtonId = 0x10000514u;
    private const uint RemoveButtonId = 0x10000515u;
    private const uint AppearOfflineCheckboxId = 0x1000052Cu;
    private const uint NameFieldId = 0x1000051Bu;

    public sealed record Actions(
        Action<string> AddFriend,
        Action<uint> RemoveFriend,
        Func<bool> CurrentAppearOffline,
        Action<bool> SetAppearOffline);

    private readonly UiTemplateListBox _listBox;
    private readonly FriendsState _friends;
    private readonly Actions? _actions;
    private readonly UiField? _nameField;
    private readonly UiButton? _appearOfflineCheckbox;
    private long _lastRevision = long.MinValue;
    private uint _selectedFriendGuid;

    private SocialFriendsPageController(
        UiTemplateListBox listBox,
        FriendsState friends,
        Actions? actions,
        UiField? nameField,
        UiButton? appearOfflineCheckbox)
    {
        _listBox = listBox;
        _friends = friends;
        _actions = actions;
        _nameField = nameField;
        _appearOfflineCheckbox = appearOfflineCheckbox;
    }

    public static SocialFriendsPageController? Bind(
        UiElement pageRoot,
        FriendsState friends,
        Func<uint, uint, UiElement?> templateResolver,
        Actions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(pageRoot);
        ArgumentNullException.ThrowIfNull(friends);
        ArgumentNullException.ThrowIfNull(templateResolver);

        if (UiElement.FindDescendant(pageRoot, ListBoxId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[UI] SocialFriendsPageController: ListBox 0x{ListBoxId:X8} not "
                + "found — Friends page will not populate.");
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
                $"[UI] SocialFriendsPageController: scrollbar 0x{scrollbarElementId:X8} "
                + "not found — the Friends list will not scroll.");

        var nameField = UiElement.FindDescendant(pageRoot, NameFieldId) as UiField;
        var appearOffline = UiElement.FindDescendant(pageRoot, AppearOfflineCheckboxId) as UiButton;
        var controller = new SocialFriendsPageController(
            listBox, friends, actions, nameField, appearOffline);
        controller.WireActions(pageRoot);
        controller.Refresh();
        return controller;
    }

    private void WireActions(UiElement pageRoot)
    {
        if (_actions is not { } actions) return;

        if (UiElement.FindDescendant(pageRoot, AddButtonId) is UiButton add)
            add.OnClick = () =>
            {
                string name = _nameField?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) return;
                actions.AddFriend(name);
                _nameField?.SetText(string.Empty);
            };

        if (UiElement.FindDescendant(pageRoot, RemoveButtonId) is UiButton remove)
            remove.OnClick = () =>
            {
                if (_selectedFriendGuid != 0u)
                    actions.RemoveFriend(_selectedFriendGuid);
            };

        if (_appearOfflineCheckbox is { } checkbox)
        {
            checkbox.SuppressSelfToggle = true;
            checkbox.OnClick = () =>
                actions.SetAppearOffline(!actions.CurrentAppearOffline());
        }
    }

    public void Tick()
    {
        if (_actions is { } actions && _appearOfflineCheckbox is { } checkbox)
            checkbox.Selected = actions.CurrentAppearOffline();

        long revision = _friends.Revision;
        if (revision == _lastRevision) return;
        Refresh();
    }

    private const uint RowNameTextId = 0x1000051Au;
    private const uint OnlineStateId = 0x10000054u;
    private const uint OfflineStateId = 0x10000055u;

    private void Refresh()
    {
        long revision = _friends.Revision;
        _listBox.Flush();
        bool allRowsResolved = true;
        bool selectedStillPresent = false;
        foreach (FriendEntry friend in _friends.Snapshot())
        {
            UiElement? row = _listBox.AddItemFromTemplateList(0);
            if (row is null) { allRowsResolved = false; continue; }
            if (friend.Id == _selectedFriendGuid) selectedStillPresent = true;
            if (UiElement.FindDescendant(row, RowNameTextId) is UiText nameText)
            {
                string name = friend.Name;
                uint guid = friend.Id;
                nameText.LinesProvider = () => [new UiText.Line(name, Vector4.One)];
                nameText.OnClick = () => _selectedFriendGuid = guid;
                nameText.TrySetRetailState(
                    friend.Online ? OnlineStateId : OfflineStateId);
            }
            else
            {
                allRowsResolved = false;
            }
        }
        if (!selectedStillPresent) _selectedFriendGuid = 0u;
        // SF-3: only latch the revision once the rebuild actually reflects it —
        // a resolver miss must not silently swallow a future revision bump.
        if (allRowsResolved) _lastRevision = revision;
    }
}
