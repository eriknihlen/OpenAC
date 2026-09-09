using System;
using System.Collections.Generic;
using AcDream.App.UI.Layout;

namespace AcDream.App.UI;

public sealed class UiTabPanel : UiDatElement, IUiChildrenAttachedListener
{
    public const uint RetailTypeId = 8u;

    private readonly IReadOnlyList<UiTabTableEntry> _tabs;
    private readonly List<UiTabTableEntry> _unresolved = new();
    private bool _behaviorActive;

    public UiTabPanel(
        ElementInfo info,
        Func<uint, (uint tex, int w, int h)> resolve,
        IReadOnlyList<UiTabTableEntry> tabs)
        : base(info, resolve)
    {
        _tabs = tabs;
    }

    public IReadOnlyList<UiTabTableEntry> Tabs => _tabs;

    public uint ActivePageElementId { get; private set; }

    public event Action<uint, uint>? ActivePageChanged;

    public bool BehaviorActive => _behaviorActive;

    public IReadOnlyList<UiTabTableEntry> UnresolvedEntries => _unresolved;

    void IUiChildrenAttachedListener.OnChildrenAttached()
    {
    }

    public void ActivateTabBehavior()
    {
        if (_behaviorActive) return;
        _behaviorActive = true;

        _unresolved.Clear();
        UiTabTableEntry? defaultEntry = null;
        foreach (UiTabTableEntry entry in _tabs)
        {
            UiElement? button = FindDescendant(this, entry.ButtonElementId);
            UiElement? page = FindDescendant(this, entry.PageElementId);
            if (button is null || page is null)
            {
                _unresolved.Add(entry);
                Console.WriteLine(
                    $"[UI] UiTabPanel 0x{Info.Id:X8}: tab entry button=0x{entry.ButtonElementId:X8} "
                    + $"page=0x{entry.PageElementId:X8} did not resolve against the built subtree "
                    + $"(button {(button is null ? "MISSING" : "ok")}, page {(page is null ? "MISSING" : "ok")}).");
            }

            uint pageId = entry.PageElementId;
            RetailTabBinding.SetClick(button, () => SwitchTo(pageId));

            if (entry.IsDefault)
                defaultEntry = entry;
        }

        if (defaultEntry is { } def)
            SwitchTo(def.PageElementId);
    }

    public void SwitchTo(uint pageElementId)
    {
        if (ActivePageElementId == pageElementId) return;
        uint previousPageElementId = ActivePageElementId;

        foreach (UiTabTableEntry entry in _tabs)
        {
            bool active = entry.PageElementId == pageElementId;
            UiElement? page = FindDescendant(this, entry.PageElementId);
            if (page is not null) page.Visible = active;

            UiElement? button = FindDescendant(this, entry.ButtonElementId);
            RetailTabBinding.SetOpen(button, active);
        }

        ActivePageElementId = pageElementId;
        ActivePageChanged?.Invoke(previousPageElementId, pageElementId);
    }
}
