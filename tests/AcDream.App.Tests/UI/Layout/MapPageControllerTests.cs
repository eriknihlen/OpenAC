using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Ui;
using AcDream.Core.World;

namespace AcDream.App.Tests.UI.Layout;

public sealed class MapPageControllerTests
{

    [Fact]
    public void FormatDateTime_OrdinaryHour_NoAndHalfSuffix()
    {
        var calendar = new DerethDateTime.Calendar(
            119, DerethDateTime.MonthName.Frostfell, 27, DerethDateTime.HourName.Dawnsong);

        string text = MapPageController.FormatDateTime(calendar);

        Assert.Equal("Date: Frostfell 27, 119 P.Y.\nTime: Dawnsong", text);
    }

    [Fact]
    public void FormatDateTime_AndHalfHour_RewritesSuffixWithHyphens()
    {
        var calendar = new DerethDateTime.Calendar(
            10, DerethDateTime.MonthName.Morningthaw, 1, DerethDateTime.HourName.MorntideAndHalf);

        string text = MapPageController.FormatDateTime(calendar);

        Assert.Equal("Date: Morningthaw 1, 10 P.Y.\nTime: Morntide-and-Half", text);
    }

    [Theory]
    [InlineData(DerethDateTime.HourName.Darktide, "Darktide")]
    [InlineData(DerethDateTime.HourName.DarktideAndHalf, "Darktide-and-Half")]
    [InlineData(DerethDateTime.HourName.Gloaming, "Gloaming")]
    [InlineData(DerethDateTime.HourName.GloamingAndHalf, "Gloaming-and-Half")]
    [InlineData(DerethDateTime.HourName.WarmtideAndHalf, "Warmtide-and-Half")]
    public void FormatDateTime_EveryHourName_MatchesExpectedDisplayText(
        DerethDateTime.HourName hour, string expectedHourText)
    {
        var calendar = new DerethDateTime.Calendar(
            10, DerethDateTime.MonthName.Morningthaw, 1, hour);

        string text = MapPageController.FormatDateTime(calendar);

        Assert.EndsWith($"Time: {expectedHourText}", text);
    }

    // ── Town table ────────────────────────────────────────────────────────

    [Fact]
    public void MapLocations_Has53Entries()
    {
        Assert.Equal(53, MapLocations.All.Length);
    }

    [Fact]
    public void MapLocations_AllNamesAreUnique()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapLocation loc in MapLocations.All)
            Assert.True(names.Add(loc.Name), $"duplicate town name: {loc.Name}");
    }

    [Fact]
    public void MapLocations_Holtburg_MatchesTheReferenceByteValues()
    {
        // s_rgLocations[0x13] (pc:977379): X=0xa4 Y=0x4d W=9 H=8.
        MapLocation holtburg = Assert.Single(MapLocations.All, l => l.Name == "Holtburg");
        Assert.Equal(0xa4, holtburg.X);
        Assert.Equal(0x4d, holtburg.Y);
        Assert.Equal(9, holtburg.Width);
        Assert.Equal(8, holtburg.Height);
    }

    [Fact]
    public void MapLocations_EveryRectIsWithinTheMapWidgetsAuthoredExtent()
    {
        foreach (MapLocation loc in MapLocations.All)
        {
            Assert.InRange(loc.X, 0, 260);
            Assert.InRange(loc.Y, 0, 260);
            Assert.InRange(loc.Width, 1, 20);
            Assert.InRange(loc.Height, 1, 20);
        }
    }


    [Theory]
    [InlineData(0.0, 0.0, 122, 128)]
    [InlineData(-100.0, 0.0, 3, 128)]
    // Far north (y very positive): pixel Y moves toward the marker area's
    // top edge (m_y0=8) — the FSUBR north-up flip means +Y in-game means
    // SMALLER pixel Y, not larger.
    [InlineData(0.0, 100.0, 122, 5)]
    [InlineData(-88.30000000000001, 62.900000000000006, 17, 51)]
    public void ComputeMarkerPosition_MatchesByteDecodedFormula_GoldenPixels(
        double x, double y, int expectedLeft, int expectedTop)
    {
        (float left, float top) = MapPageController.ComputeMarkerPosition(
            markerX0: 6, markerX1: 247, markerY0: 8, markerY1: 258,
            iconWidth: 10, iconHeight: 10, x: x, y: y);

        Assert.Equal(expectedLeft, left);
        Assert.Equal(expectedTop, top);
    }

    [Fact]
    public void ComputeMarkerPosition_ArwicCell_MatchesRadarCoordinates()
    {
        const uint cellId = 0x11CE0001u;
        Assert.True(RadarCoordinates.TryFromCell(cellId, out RadarCoordinates coords));
        Assert.Equal(-88.30000000000001, coords.X, precision: 12);
        Assert.Equal(62.900000000000006, coords.Y, precision: 12);
    }

    // ── Marker placement wiring (real fixture, no re-derivation) ────────────

    [Fact]
    public void Bind_PlayerMarker_OutdoorCell_ReproducesRadarCoordinatesPlacement()
    {
        const uint cellId = 0x11CE0001u;
        Assert.True(RadarCoordinates.TryFromCell(cellId, out _));

        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var callbacks = new MapHousePanelController.Callbacks(
            Toggle: () => { },
            Map: new MapPageController.Bindings(
                CurrentCalendar: static () => default,
                PlayerCellId: () => cellId,
                HousePosition: static () => (CreateObject.ServerPosition?)null,
                TemplateResolver: (_, e) => new UiText { Width = 10f, Height = 10f, DatElementId = e },
                IconBuilder: info => new UiText { Width = 10f, Height = 10f, DatElementId = info.Id }),
            House: new HousePageController.Bindings(Lines: static () => Array.Empty<string>()));

        MapHousePanelController? controller = MapHousePanelController.Bind(rootInfo, layout, callbacks);
        Assert.NotNull(controller);

        UiElement? playerIcon = UiElement.FindDescendant(controller!.Root, MapPageController.PlayerIconId);
        Assert.NotNull(playerIcon);
        Assert.True(playerIcon!.Visible);

        Assert.Equal(17f, playerIcon.Left);
        Assert.Equal(51f, playerIcon.Top);
    }

    [Fact]
    public void Bind_PlayerMarker_IndoorCell_HidesIconAndClearsCoordinateText()
    {
        const uint indoorCellId = 0x0012_0100u;
        Assert.False(RadarCoordinates.TryFromCell(indoorCellId, out _));

        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var callbacks = new MapHousePanelController.Callbacks(
            Toggle: () => { },
            Map: new MapPageController.Bindings(
                CurrentCalendar: static () => default,
                PlayerCellId: () => indoorCellId,
                HousePosition: static () => (CreateObject.ServerPosition?)null,
                TemplateResolver: (_, e) => new UiText { Width = 10f, Height = 10f, DatElementId = e },
                IconBuilder: info => new UiText { Width = 10f, Height = 10f, DatElementId = info.Id }),
            House: new HousePageController.Bindings(Lines: static () => Array.Empty<string>()));

        MapHousePanelController? controller = MapHousePanelController.Bind(rootInfo, layout, callbacks);
        Assert.NotNull(controller);

        UiElement? playerIcon = UiElement.FindDescendant(controller!.Root, MapPageController.PlayerIconId);
        Assert.NotNull(playerIcon);
        Assert.False(playerIcon!.Visible);
    }

    [Fact]
    public void Refresh_PlayerIconResolutionFails_CoordinateTextStaysEmptyToo()
    {
        const uint cellId = 0x11CE0001u; // outdoor: TryFromCell succeeds.
        Assert.True(RadarCoordinates.TryFromCell(cellId, out _));

        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var callbacks = new MapHousePanelController.Callbacks(
            Toggle: () => { },
            Map: new MapPageController.Bindings(
                CurrentCalendar: static () => default,
                PlayerCellId: () => cellId,
                HousePosition: static () => (CreateObject.ServerPosition?)null,
                TemplateResolver: (_, e) => new UiText { Width = 10f, Height = 10f, DatElementId = e },
                // Simulates the player icon's own build failing
                // (ResolveSwallowedIcon's null-build path, e.g. a future DAT
                // regression) while the house icon and every town-marker
                // resolution still succeed normally.
                IconBuilder: info => info.Id == MapPageController.PlayerIconId
                    ? null
                    : new UiText { Width = 10f, Height = 10f, DatElementId = info.Id }),
            House: new HousePageController.Bindings(Lines: static () => Array.Empty<string>()));

        MapHousePanelController? controller = MapHousePanelController.Bind(rootInfo, layout, callbacks);
        Assert.NotNull(controller);

        Assert.Null(
            UiElement.FindDescendant(controller!.Root, MapPageController.PlayerIconId));

        var coordinateText = Assert.IsType<UiText>(
            UiElement.FindDescendant(controller.Root, MapPageController.CoordinateTextId));
        Assert.Empty(coordinateText.LinesProvider());
    }

    [Fact]
    public void Bind_HouseMarker_NullPosition_StaysHidden()
    {
        ElementInfo rootInfo = FixtureLoader.LoadMapHouseHostInfos();
        ImportedLayout layout = FixtureLoader.LoadMapHouseHost();
        var callbacks = new MapHousePanelController.Callbacks(
            Toggle: () => { },
            Map: new MapPageController.Bindings(
                CurrentCalendar: static () => default,
                PlayerCellId: static () => 0u,
                HousePosition: static () => (CreateObject.ServerPosition?)null,
                TemplateResolver: (_, e) => new UiText { Width = 10f, Height = 10f, DatElementId = e },
                IconBuilder: info => new UiText { Width = 10f, Height = 10f, DatElementId = info.Id }),
            House: new HousePageController.Bindings(Lines: static () => Array.Empty<string>()));

        MapHousePanelController? controller = MapHousePanelController.Bind(rootInfo, layout, callbacks);
        Assert.NotNull(controller);

        UiElement? houseIcon = UiElement.FindDescendant(controller!.Root, MapPageController.HouseIconId);
        Assert.NotNull(houseIcon);
        Assert.False(houseIcon!.Visible);
    }
}
