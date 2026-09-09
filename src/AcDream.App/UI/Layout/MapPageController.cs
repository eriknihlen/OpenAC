using System;
using System.Collections.Generic;
using AcDream.Core.Net.Messages;
using AcDream.Core.Ui;
using AcDream.Core.World;

namespace AcDream.App.UI.Layout;

public sealed class MapPageController
{
    public const uint DateTimeTextId = 0x100001EBu;
    public const uint MapWidgetId = 0x100001ECu;
    public const uint PlayerIconId = 0x100001EDu;
    public const uint HouseIconId = 0x100001EEu;
    public const uint CoordinateTextId = 0x100001EFu;

    private const uint MarkerAreaX0Attr = 0x1000004Eu;
    private const uint MarkerAreaX1Attr = 0x1000004Fu;
    private const uint MarkerAreaY0Attr = 0x10000050u;
    private const uint MarkerAreaY1Attr = 0x10000051u;
    private const uint HotspotTemplateElementAttr = 0x47u;
    private const uint HotspotTemplateLayoutAttr = 0x48u;

    public const double RefreshIntervalSeconds = 5.0;

    public sealed record Bindings(
        Func<DerethDateTime.Calendar> CurrentCalendar,
        Func<uint> PlayerCellId,
        Func<CreateObject.ServerPosition?> HousePosition,
        Func<uint, uint, UiElement?> TemplateResolver,
        Func<ElementInfo, UiElement?> IconBuilder,
        Func<uint, uint, ElementInfo?>? TemplateInfoResolver = null);

    private readonly UiElement? _dateTimeText;
    private readonly UiElement? _map;
    private readonly UiElement? _playerIcon;
    private readonly UiElement? _houseIcon;
    private readonly UiElement? _coordinateText;
    private readonly Bindings _bindings;
    private readonly int _markerX0, _markerX1, _markerY0, _markerY1;

    private double _nextUpdateSeconds;
    private string? _lastDateTimeText;
    private string? _lastCoordinateText;

    private MapPageController(
        UiElement? dateTimeText,
        UiElement map,
        UiElement? playerIcon,
        UiElement? houseIcon,
        UiElement? coordinateText,
        (int X0, int X1, int Y0, int Y1) markerArea,
        Bindings bindings)
    {
        _dateTimeText = dateTimeText;
        _map = map;
        _playerIcon = playerIcon;
        _houseIcon = houseIcon;
        _coordinateText = coordinateText;
        _bindings = bindings;
        (_markerX0, _markerX1, _markerY0, _markerY1) = markerArea;
    }

    public static MapPageController? Bind(UiElement page, ElementInfo pageInfo, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(pageInfo);
        ArgumentNullException.ThrowIfNull(bindings);

        UiElement? map = UiElement.FindDescendant(page, MapWidgetId);
        if (map is null)
        {
            Console.WriteLine($"[UI] Map tab: m_pMap 0x{MapWidgetId:X8} not found — Map tab will not populate.");
            return null;
        }

        ElementInfo? mapInfo = FindInfo(pageInfo, MapWidgetId);
        var markerArea = (X0: 0, X1: 0, Y0: 0, Y1: 0);
        if (mapInfo is not null)
        {
            int x0 = mapInfo.TryGetEffectiveProperty(MarkerAreaX0Attr, out var vx0) ? vx0.IntegerValue : 0;
            int x1 = mapInfo.TryGetEffectiveProperty(MarkerAreaX1Attr, out var vx1) ? vx1.IntegerValue : 0;
            int y0 = mapInfo.TryGetEffectiveProperty(MarkerAreaY0Attr, out var vy0) ? vy0.IntegerValue : 0;
            int y1 = mapInfo.TryGetEffectiveProperty(MarkerAreaY1Attr, out var vy1) ? vy1.IntegerValue : 0;
            markerArea = (x0, x1, y0, y1);
        }

        UiElement? playerIcon = ResolveSwallowedIcon(map, mapInfo, bindings.IconBuilder, PlayerIconId);
        UiElement? houseIcon = ResolveSwallowedIcon(map, mapInfo, bindings.IconBuilder, HouseIconId);

        var controller = new MapPageController(
            UiElement.FindDescendant(page, DateTimeTextId),
            map,
            playerIcon,
            houseIcon,
            UiElement.FindDescendant(page, CoordinateTextId),
            markerArea,
            bindings);

        controller.BuildTownMarkers(mapInfo, bindings.TemplateResolver);

        if (controller._dateTimeText is UiText dateTimeText)
            dateTimeText.LinesProvider = () => ToLines(controller._lastDateTimeText, dateTimeText.DefaultColor);
        if (controller._coordinateText is UiText coordinateText)
            coordinateText.LinesProvider = () => ToLines(controller._lastCoordinateText, coordinateText.DefaultColor);

        // Immediate first refresh rather than waiting out the first 5 s tick.
        controller.Refresh();
        controller._nextUpdateSeconds = RefreshIntervalSeconds;
        return controller;
    }

    private static UiElement? ResolveSwallowedIcon(
        UiElement map, ElementInfo? mapInfo, Func<ElementInfo, UiElement?> iconBuilder, uint iconElementId)
    {
        UiElement? existing = UiElement.FindDescendant(map, iconElementId);
        if (existing is not null)
            return PrepareIcon(existing);

        ElementInfo? iconInfo = mapInfo is null ? null : FindInfo(mapInfo, iconElementId);
        if (iconInfo is null)
        {
            Console.WriteLine(
                $"[UI] Map tab: icon 0x{iconElementId:X8} not authored under m_pMap's resolved "
                + "info tree — it will not be shown.");
            return null;
        }

        UiElement? icon = iconBuilder(iconInfo);
        if (icon is null)
        {
            Console.WriteLine(
                $"[UI] Map tab: icon 0x{iconElementId:X8} did not build — it will not be shown.");
            return null;
        }
        map.AddChild(PrepareIcon(icon));
        return icon;
    }

    private static UiElement PrepareIcon(UiElement icon)
    {
        icon.Anchors = AnchorEdges.None;
        icon.Visible = false;
        return icon;
    }

    private static IReadOnlyList<UiText.Line> ToLines(string? text, System.Numerics.Vector4 color)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<UiText.Line>();
        string[] parts = text.Split('\n');
        var lines = new UiText.Line[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            lines[i] = new UiText.Line(parts[i], color);
        return lines;
    }

    private void BuildTownMarkers(ElementInfo? mapInfo, Func<uint, uint, UiElement?> templateResolver)
    {
        if (mapInfo is null) return;
        if (!mapInfo.TryGetEffectiveProperty(HotspotTemplateElementAttr, out var templateElement)) return;
        if (!mapInfo.TryGetEffectiveProperty(HotspotTemplateLayoutAttr, out var templateLayout)) return;
        if (templateLayout.UnsignedValue == 0) return;

        ElementInfo? templateInfo = _bindings.TemplateInfoResolver?.Invoke(
            (uint)templateLayout.UnsignedValue, (uint)templateElement.UnsignedValue);

        foreach (MapLocation loc in MapLocations.All)
        {
            UiElement? marker = templateResolver(
                (uint)templateLayout.UnsignedValue, (uint)templateElement.UnsignedValue);
            if (marker is null) continue;

            marker.Left = loc.X;
            marker.Top = loc.Y;
            marker.Width = loc.Width;
            marker.Height = loc.Height;
            if (marker is UiButton markerButton)
                markerButton.TooltipText = loc.Name;
            else
                Console.WriteLine(
                    $"[UI] Map tab: town marker '{loc.Name}' template "
                    + $"resolved to {marker.GetType().Name}, not UiButton — "
                    + "TooltipText cannot be set, marker will show no tooltip.");

            if (templateInfo is not null && marker is UiButton stateHost)
            {
                foreach (ElementInfo childInfo in templateInfo.Children)
                    if (_bindings.IconBuilder(childInfo) is { } highlight)
                        marker.AddChild(highlight);
                stateHost.TrySetRetailState(UiButtonStateMachine.Normal);
            }
            _map!.AddChild(marker);
        }
    }

    public void Tick(double deltaSeconds)
    {
        _nextUpdateSeconds -= deltaSeconds;
        if (_nextUpdateSeconds > 0) return;
        _nextUpdateSeconds = RefreshIntervalSeconds;
        Refresh();
    }

    private void Refresh()
    {
        RefreshDateTime();
        RefreshCoordinatesAndPlayerMarker();
        RefreshHouseMarker();
    }

    private void RefreshDateTime()
    {
        if (_dateTimeText is null) return;
        DerethDateTime.Calendar calendar = _bindings.CurrentCalendar();
        string text = FormatDateTime(calendar);
        _lastDateTimeText = text;
    }

    internal static string FormatDateTime(DerethDateTime.Calendar calendar) =>
        $"Date: {calendar.Month} {calendar.Day}, {calendar.Year} P.Y.\nTime: {FormatHourName(calendar.Hour)}";

    private static string FormatHourName(DerethDateTime.HourName hour)
    {
        string name = hour.ToString();
        const string suffix = "AndHalf";
        return name.EndsWith(suffix, StringComparison.Ordinal)
            ? string.Concat(name.AsSpan(0, name.Length - suffix.Length), "-and-Half")
            : name;
    }

    private void RefreshCoordinatesAndPlayerMarker()
    {
        if (_coordinateText is null || _playerIcon is null) return;

        bool outside = RadarCoordinates.TryFromCell(_bindings.PlayerCellId(), out RadarCoordinates coords);
        if (outside)
        {
            _lastCoordinateText = coords.CombinedText;
            PlaceMarker(_playerIcon, coords.X, coords.Y);
        }
        else
        {
            _lastCoordinateText = string.Empty;
            _playerIcon.Visible = false;
        }
    }

    private void RefreshHouseMarker()
    {
        if (_houseIcon is null) return;

        CreateObject.ServerPosition? housePosition = _bindings.HousePosition();
        if (housePosition is null)
        {
            _houseIcon.Visible = false;
            return;
        }

        if (!RadarCoordinates.TryFromCell(housePosition.Value.LandblockId, out RadarCoordinates coords))
        {
            _houseIcon.Visible = false;
            return;
        }

        PlaceMarker(_houseIcon, coords.X, coords.Y);
    }

    private void PlaceMarker(UiElement? icon, double x, double y)
    {
        if (icon is null) return;

        (float left, float top) = ComputeMarkerPosition(
            _markerX0, _markerX1, _markerY0, _markerY1,
            (int)icon.Width, (int)icon.Height, x, y);
        icon.Left = left;
        icon.Top = top;
        icon.Visible = true;
    }

    internal static (float Left, float Top) ComputeMarkerPosition(
        int markerX0, int markerX1, int markerY0, int markerY1,
        int iconWidth, int iconHeight, double x, double y)
    {
        int halfWidth = iconWidth / 2;
        int halfHeight = iconHeight / 2;
        int extentX = markerX1 - markerX0 + 1;
        int extentY = markerY1 - markerY0 + 1;

        int xOffset = (int)(extentX * (x * 10.0 + 1024.0) * (-1.0 / 2048.0));
        int yOffset = (int)(extentY * (2047.0 - (y * 10.0 + 1024.0)) * (-1.0 / 2048.0));

        return (markerX0 - halfWidth - xOffset, markerY0 - halfHeight - yOffset);
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
