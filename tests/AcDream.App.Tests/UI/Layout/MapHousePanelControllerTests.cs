using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Core.World;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class MapHousePanelControllerTests
{
    private const uint MapNoteSkinRootId = 0x10000398u;
    private const uint MapNoteSkinLayoutId = 0x21000041u;

    private static UiElement? FakeHotspotTemplate(uint layoutId, uint elementId)
        => new UiButton(MapNoteTemplateInfo(), static _ => (0u, 0, 0))
        {
            Width = 10f,
            Height = 10f,
            DatElementId = elementId,
            AuthoredTooltipRootElementId = MapNoteSkinRootId,
            AuthoredTooltipLayoutDid = MapNoteSkinLayoutId,
            AuthoredTooltipDelaySeconds = 0f,   // authored P0x50 = 0.0
            AuthoredTooltipEnabled = true,      // authored P0x4B
        };

    /// <summary>The template's authored state shape (raw-DAT-dumped):
    /// media-less <c>Normal</c>/<c>Normal_rollover</c> descriptors, both
    /// <c>PassToChildren=true</c>, plus <c>P0x13</c> RolloverEnabled.</summary>
    private static ElementInfo MapNoteTemplateInfo()
    {
        var info = new ElementInfo { Type = 1, Width = 10, Height = 10 };
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x13u] = new UiPropertyValue
        { Kind = UiPropertyKind.Bool, BoolValue = true };
        info.States[UiStateInfo.DirectStateId] = direct;
        info.States[1u] = new UiStateInfo { Id = 1u, Name = "Normal", PassToChildren = true };
        info.States[2u] = new UiStateInfo { Id = 2u, Name = "Normal_rollover", PassToChildren = true };
        return info;
    }

    private static ElementInfo HighlightChildInfo()
    {
        var info = new ElementInfo { Id = 0x100001F1u, Type = 3, Width = 10, Height = 10 };
        var normal = new UiStateInfo { Id = 1u, Name = "Normal" };
        normal.Properties.Values[0x3Bu] = new UiPropertyValue
        { Kind = UiPropertyKind.Bool, BoolValue = true };
        var rollover = new UiStateInfo { Id = 2u, Name = "Normal_rollover" };
        rollover.Properties.Values[0x3Bu] = new UiPropertyValue
        { Kind = UiPropertyKind.Bool, BoolValue = false };
        info.States[1u] = normal;
        info.States[2u] = rollover;
        return info;
    }

    private static ElementInfo? FakeHotspotTemplateInfo(uint layoutId, uint elementId)
    {
        ElementInfo info = MapNoteTemplateInfo();
        info.Children.Add(HighlightChildInfo());
        return info;
    }

    private static UiElement? FakeIconBuilder(ElementInfo info)
        => info.Type == 3
            ? new UiDatElement(info, static _ => (0u, 0, 0))
            {
                Width = 10f,
                Height = 10f,
                DatElementId = info.Id,
            }
            : new UiButton(new ElementInfo(), static _ => (0u, 0, 0))
            {
                Width = 10f,
                Height = 10f,
                DatElementId = info.Id,
            };

    private static UiElement? FakeHouseRowTemplate(uint layoutId, uint elementId)
        => new UiText
        {
            Width = 280f,
            Height = 28f,
            DefaultColor = new Vector4(0.1f, 0.1f, 0.1f, 1f),
            FontColorPalette =
            [
                new Vector4(1f, 1f, 1f, 1f),
                new Vector4(0f, 1f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
            ],
        };

    private static MapHousePanelController.Callbacks MakeCallbacks(
        List<string>? calls = null,
        Func<DerethDateTime.Calendar>? currentCalendar = null,
        Func<uint>? playerCellId = null,
        Func<CreateObject.ServerPosition?>? housePosition = null,
        Func<IReadOnlyList<string>>? houseLines = null,
        Func<uint, uint, ElementInfo?>? templateInfoResolver = null,
        Func<IReadOnlyList<HousePanelLine>>? housePanelLines = null)
    {
        calls ??= new List<string>();
        return new MapHousePanelController.Callbacks(
            Toggle: () => calls.Add("toggle"),
            Map: new MapPageController.Bindings(
                CurrentCalendar: currentCalendar ?? (static () => default),
                PlayerCellId: playerCellId ?? (static () => 0u),
                HousePosition: housePosition ?? (static () => null),
                TemplateResolver: FakeHotspotTemplate,
                IconBuilder: FakeIconBuilder,
                TemplateInfoResolver: templateInfoResolver),
            House: new HousePageController.Bindings(
                Lines: houseLines ?? (static () => Array.Empty<string>()),
                OnShown: () => calls.Add("house-shown"),
                TemplateResolver: FakeHouseRowTemplate,
                PanelLines: housePanelLines));
    }

    [Fact]
    public void Bind_RootBuildsAsUiTabPanel()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();

        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo, layout, MakeCallbacks());

        Assert.NotNull(controller);
        Assert.IsType<UiTabPanel>(controller!.Root);
    }

    /// <summary>Pins the authored tab table exactly as read from the live
    /// DATs (<c>MapHousePanelSlotProbeTests</c>): two entries, Map is the
    /// sole default.</summary>
    [Fact]
    public void TabTable_MatchesLiveDatPairing_MapIsDefault()
    {
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var tabs = Assert.IsType<UiTabPanel>(layout.Root);

        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Contains(tabs.Tabs, e =>
            e.ButtonElementId == 0x100001F3u && e.PageElementId == 0x100001F6u && e.IsDefault);
        Assert.Contains(tabs.Tabs, e =>
            e.ButtonElementId == 0x100001F4u && e.PageElementId == 0x100001F7u && !e.IsDefault);
        Assert.Single(tabs.Tabs, e => e.IsDefault);
    }

    [Fact]
    public void Bind_Succeeds_AndActivateTabs_SelectsMapByDefault()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo, layout, MakeCallbacks());

        Assert.NotNull(controller);
        controller!.ActivateTabs();

        Assert.Empty(controller.TabPanel.UnresolvedEntries);
        Assert.False(controller.IsShowingHouse);
    }

    [Fact]
    public void SwitchToHouse_FiresOnShown_OnlyWhenPanelIsVisible()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var calls = new List<string>();
        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo, layout, MakeCallbacks(calls));
        Assert.NotNull(controller);
        controller!.ActivateTabs();

        // Not visible yet: switching tabs must not fire OnShown.
        controller.TabPanel.SwitchTo(0x100001F7u);
        Assert.DoesNotContain("house-shown", calls);

        controller.OnShown();
        Assert.Contains("house-shown", calls);
    }

    [Fact]
    public void ShowMap_UsesTheSameAuthoredTabStateAsAClick()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        MapHousePanelController controller = MapHousePanelController.Bind(
            rootInfo, layout, MakeCallbacks())!;
        controller.ActivateTabs();
        controller.TabPanel.SwitchTo(0x100001F7u); // House

        controller.ShowMap();

        Assert.True(controller.IsShowingMap);
        Assert.False(controller.IsShowingHouse);
    }

    [Fact]
    public void CloseButton_InvokesToggle()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var calls = new List<string>();
        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo, layout, MakeCallbacks(calls));
        Assert.NotNull(controller);

        UiElement? close = layout.FindElement(0x100001F5u);
        Assert.IsType<UiButton>(close);
        ((UiButton)close!).OnClick?.Invoke();

        Assert.Contains("toggle", calls);
    }

    [Fact]
    public void Bind_BuildsAll53TownHotspots_UnderTheMapWidget()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo, layout, MakeCallbacks());
        Assert.NotNull(controller);

        UiElement? map = UiElement.FindDescendant(controller!.Root, MapPageController.MapWidgetId);
        Assert.NotNull(map);
        var townMarkers = map!.Children
            .Where(c => c.DatElementId != MapPageController.PlayerIconId
                && c.DatElementId != MapPageController.HouseIconId)
            .ToList();
        Assert.Equal(55, map.Children.Count);
        Assert.Equal(53, townMarkers.Count);
        Assert.All(townMarkers, c => Assert.Contains(
            MapLocations.All, loc => loc.Width == c.Width && loc.Height == c.Height));
        Assert.All(townMarkers, c => Assert.Equal(MapNoteSkinRootId, c.AuthoredTooltipRootElementId));
        Assert.All(townMarkers, c => Assert.Equal(MapNoteSkinLayoutId, c.AuthoredTooltipLayoutDid));
        Assert.All(townMarkers, c => Assert.Equal(0f, c.AuthoredTooltipDelaySeconds));
        Assert.All(townMarkers, c => Assert.IsType<UiButton>(c));
        Assert.All(townMarkers, c => Assert.False(string.IsNullOrEmpty(((UiButton)c).TooltipText)));
        Assert.Contains(townMarkers, c => ((UiButton)c).TooltipText == "Holtburg");
    }

    [Fact]
    public void TownMarkers_RolloverHighlight_StartsHidden_ShowsOnHover()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        MapHousePanelController? controller = MapHousePanelController.Bind(
            rootInfo, layout, MakeCallbacks(templateInfoResolver: FakeHotspotTemplateInfo));
        Assert.NotNull(controller);

        UiElement? map = UiElement.FindDescendant(controller!.Root, MapPageController.MapWidgetId);
        Assert.NotNull(map);
        var markers = map!.Children
            .Where(c => c.DatElementId != MapPageController.PlayerIconId
                && c.DatElementId != MapPageController.HouseIconId)
            .OfType<UiButton>()
            .ToList();
        Assert.Equal(53, markers.Count);

        foreach (UiButton marker in markers)
        {
            UiElement highlight = Assert.Single(marker.Children);
            Assert.False(highlight.Visible);   // Normal: P0x3B=true
        }

        UiButton hovered = markers[0];
        UiElement hoveredHighlight = hovered.Children[0];
        hovered.OnEvent(new UiEvent(0, hovered, UiEventType.HoverEnter));
        Assert.True(hoveredHighlight.Visible); // Normal_rollover: P0x3B=false

        hovered.OnEvent(new UiEvent(0, hovered, UiEventType.HoverLeave));
        Assert.False(hoveredHighlight.Visible);
    }

    [Fact]
    public void Bind_HouseListBoxStartsEmpty_MatchingRetailPostInit()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo, layout, MakeCallbacks());
        Assert.NotNull(controller);

        UiElement? box = UiElement.FindDescendant(controller!.Root, HousePageController.TextBoxId);
        var listBox = Assert.IsType<UiTemplateListBox>(box);
        Assert.Equal(0, listBox.ContentHeight);
    }

    [Fact]
    public void Tick_RendersHouseLinesIntoTheAuthoredRowTemplate()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        string[] lines = ["You may buy another house immediately."];
        MapHousePanelController? controller = MapHousePanelController.Bind(
            rootInfo, layout, MakeCallbacks(houseLines: () => lines));
        Assert.NotNull(controller);

        controller!.Tick(0.016);

        UiElement? box = UiElement.FindDescendant(controller.Root, HousePageController.TextBoxId);
        var listBox = Assert.IsType<UiTemplateListBox>(box);
        UiScrollablePanel viewport = Assert.IsType<UiScrollablePanel>(
            listBox.ViewportForTest);
        Assert.Single(viewport.Children);
        var row = Assert.IsType<UiText>(viewport.Children[0]);
        Assert.Equal(
            "You may buy another house immediately.",
            Assert.Single(row.LinesProvider()).Text);
    }

    [Fact]
    public void Tick_UsesRetailHousePanelColorAsAuthoredPaletteIndex()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        HousePanelLine[] lines =
        [
            new("paid", HousePanelTextColor.RentPaid),
            new("unpaid", HousePanelTextColor.RentNotPaid),
        ];
        MapHousePanelController? controller = MapHousePanelController.Bind(
            rootInfo,
            layout,
            MakeCallbacks(housePanelLines: () => lines));
        Assert.NotNull(controller);

        controller!.Tick(0.016);

        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(controller.Root, HousePageController.TextBoxId));
        UiScrollablePanel viewport = Assert.IsType<UiScrollablePanel>(
            listBox.ViewportForTest);
        Assert.Equal(2, viewport.Children.Count);
        Assert.Equal(
            new Vector4(0f, 1f, 0f, 1f),
            Assert.Single(Assert.IsType<UiText>(viewport.Children[0]).LinesProvider()).Color);
        Assert.Equal(
            new Vector4(1f, 0f, 0f, 1f),
            Assert.Single(Assert.IsType<UiText>(viewport.Children[1]).LinesProvider()).Color);
    }
}
