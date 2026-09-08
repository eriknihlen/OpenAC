using System;

namespace AcDream.App.UI.Layout;

public sealed class MapHousePanelController : IRetainedPanelController
{
    public const uint HostLayoutId = 0x2100006Eu;
    public const uint SlotElementId = 0x1000018Cu;

    private const uint MapButtonId = 0x100001F3u;
    private const uint MapPageId = 0x100001F6u;
    private const uint HouseButtonId = 0x100001F4u;
    private const uint HousePageId = 0x100001F7u;
    private const uint CloseButtonId = 0x100001F5u;

    public sealed record Callbacks(
        Action Toggle,
        MapPageController.Bindings Map,
        HousePageController.Bindings House);

    private readonly UiTabPanel _tabPanel;
    private readonly MapPageController? _map;
    private readonly HousePageController? _house;
    private readonly Action<uint, uint> _onActivePageChanged;
    private bool _visible;
    private bool _disposed;

    public UiElement Root => _tabPanel;
    public UiTabPanel TabPanel => _tabPanel;

    private MapHousePanelController(
        UiTabPanel tabPanel, MapPageController? map, HousePageController? house)
    {
        _tabPanel = tabPanel;
        _map = map;
        _house = house;

        _onActivePageChanged = (_, _) => FireHouseShownIfActive();
        _tabPanel.ActivePageChanged += _onActivePageChanged;
    }

    public static MapHousePanelController? Bind(
        ElementInfo rootInfo, ImportedLayout layout, Callbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(rootInfo);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(callbacks);

        if (layout.Root is not UiTabPanel tabPanel)
        {
            Console.WriteLine(
                "[D.2b] MapHousePanelController.Bind: root did not build as UiTabPanel "
                + $"(actual type {layout.Root.GetType().Name}) — Map/House panel will not open.");
            return null;
        }

        if (layout.FindElement(CloseButtonId) is UiButton close)
            close.OnClick = callbacks.Toggle;
        else
            Console.WriteLine(
                $"[D.2b] MapHousePanelController: close button 0x{CloseButtonId:X8} not found.");

        UiElement? mapPage = UiElement.FindDescendant(tabPanel, MapPageId);
        UiElement? housePage = UiElement.FindDescendant(tabPanel, HousePageId);

        MapPageController? map = null;
        if (mapPage is not null)
        {
            ElementInfo? mapPageInfo = FindInfo(rootInfo, MapPageId);
            map = mapPageInfo is null
                ? null
                : MapPageController.Bind(mapPage, mapPageInfo, callbacks.Map);
        }
        HousePageController? house = housePage is null
            ? null
            : HousePageController.Bind(housePage, callbacks.House);

        if (mapPage is null)
            Console.WriteLine($"[D.2b] MapHousePanelController: Map page 0x{MapPageId:X8} not found.");
        if (housePage is null)
            Console.WriteLine($"[D.2b] MapHousePanelController: House page 0x{HousePageId:X8} not found.");

        return new MapHousePanelController(tabPanel, map, house);
    }

    public void ActivateTabs() => _tabPanel.ActivateTabBehavior();

    public bool IsShowingHouse => _tabPanel.ActivePageElementId == HousePageId;

    public bool IsShowingMap => _tabPanel.ActivePageElementId == MapPageId;

    public void ShowMap() => _tabPanel.SwitchTo(MapPageId);

    public void ShowHouse() => _tabPanel.SwitchTo(HousePageId);

    public void OnShown()
    {
        _visible = true;
        FireHouseShownIfActive();
    }

    public void OnHidden() => _visible = false;

    private void FireHouseShownIfActive()
    {
        if (_visible && IsShowingHouse)
            _house?.OnShown();
    }

    public void Tick(double deltaSeconds)
    {
        if (_disposed) return;
        _map?.Tick(deltaSeconds);
        _house?.Tick();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tabPanel.ActivePageChanged -= _onActivePageChanged;
    }

    private static ElementInfo? FindInfo(ElementInfo info, uint id)
    {
        if (info.Id == id) return info;
        foreach (ElementInfo child in info.Children)
        {
            ElementInfo? found = FindInfo(child, id);
            if (found is not null) return found;
        }
        return null;
    }
}
