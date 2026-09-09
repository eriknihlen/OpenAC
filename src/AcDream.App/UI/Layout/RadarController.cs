using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Core.Ui;

namespace AcDream.App.UI.Layout;

public sealed class RadarController : IRetainedPanelController
{
    public const uint LayoutId = 0x21000074u;
    public const int RadarPixelRadius = 50;
    public const uint RootId = 0x100006D3u;
    public const uint CoordinateContainerId = 0x1000003Eu;
    public const uint RadarDiscId = 0x1000003Fu;
    public const uint NorthTokenId = 0x10000040u;
    public const uint EastTokenId = 0x10000041u;
    public const uint SouthTokenId = 0x10000042u;
    public const uint WestTokenId = 0x10000043u;
    public const uint LockButtonId = 0x10000619u;
    public const uint DragButtonId = 0x100006A3u;

    private static readonly Vector4 CoordinateColor = Vector4.One;

    private readonly UiRadar _radar;
    private readonly UiElement? _coordinateContainer;
    private readonly UiText? _coordinateText;
    private readonly UiButton? _lockButton;
    private readonly UiElement? _dragButton;
    private readonly Action<bool>? _setUiLocked;
    private readonly CompassToken[] _tokens;
    private float _lastHeading = float.NaN;
    private string? _lastCoordinates;
    private bool _coordinatesInitialized;
    private bool? _lastUiLocked;
    private bool _disposed;

    private RadarController(
        ImportedLayout layout,
        Func<UiRadarSnapshot> snapshotProvider,
        Action<uint>? selectObject,
        Action<uint?>? hoveredObjectChanged,
        Action<bool>? setUiLocked,
        UiDatFont? datFont)
    {
        _radar = layout.Root as UiRadar
            ?? throw new ArgumentException(
                $"Layout root must be {nameof(UiRadar)} (retail class 0x{UiRadar.RetailClassId:X8}).",
                nameof(layout));

        _radar.Center = ResolveCenter(layout);
        _radar.SnapshotProvider = () =>
        {
            var snapshot = snapshotProvider() ?? UiRadarSnapshot.Empty;
            ApplyPresentation(snapshot);
            return snapshot;
        };
        _radar.SelectObject = selectObject;
        _radar.HoveredObjectChanged = hoveredObjectChanged;
        _setUiLocked = setUiLocked;

        _coordinateContainer = layout.FindElement(CoordinateContainerId);
        _coordinateText = CreateCoordinateText(_coordinateContainer, datFont);
        _lockButton = layout.FindElement(LockButtonId) as UiButton;
        _dragButton = layout.FindElement(DragButtonId);

        if (_lockButton is not null)
        {
            // LockedUI/UnlockedUI are persistent semantic faces. The button's
            // authored, media-less Normal_pressed state must not replace them
            // during pointer traffic after the global lock transition.
            _lockButton.ControllerOwnsVisualState = true;
            if (_setUiLocked is not null)
                _lockButton.OnClick = () => _setUiLocked(!(_lastUiLocked ?? false));
        }

        _tokens =
        [
            CreateToken(layout.FindElement(NorthTokenId), RadarCompassPoint.North, _radar.Center),
            CreateToken(layout.FindElement(EastTokenId), RadarCompassPoint.East, _radar.Center),
            CreateToken(layout.FindElement(SouthTokenId), RadarCompassPoint.South, _radar.Center),
            CreateToken(layout.FindElement(WestTokenId), RadarCompassPoint.West, _radar.Center),
        ];

        if (_dragButton is not null)
            _dragButton.ClickThrough = false;

        _radar.Draggable = true;
        _radar.ResizeX = false;
        _radar.ResizeY = false;
        _radar.Refresh();
    }

    public static RadarController Bind(
        ImportedLayout layout,
        Func<UiRadarSnapshot> snapshotProvider,
        Action<uint>? selectObject = null,
        Action<uint?>? hoveredObjectChanged = null,
        Action<bool>? setUiLocked = null,
        UiDatFont? datFont = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        return new RadarController(
            layout,
            snapshotProvider,
            selectObject,
            hoveredObjectChanged,
            setUiLocked,
            datFont);
    }

    private void ApplyPresentation(UiRadarSnapshot snapshot)
    {
        if (!snapshot.PlayerHeadingDegrees.Equals(_lastHeading))
        {
            for (int i = 0; i < _tokens.Length; i++)
            {
                ref readonly var token = ref _tokens[i];
                if (token.Element is null)
                    continue;

                var p = RetailRadar.GetCompassTokenTopLeft(
                    snapshot.PlayerHeadingDegrees,
                    token.Point,
                    _radar.Center,
                    token.Magnitude,
                    new Vector2(token.Element.Width, token.Element.Height));
                token.Element.Left = p.X;
                token.Element.Top = p.Y;
            }

            _lastHeading = snapshot.PlayerHeadingDegrees;
        }

        if (!_coordinatesInitialized
            || !string.Equals(snapshot.CoordinatesText, _lastCoordinates, StringComparison.Ordinal))
        {
            _coordinatesInitialized = true;
            _lastCoordinates = snapshot.CoordinatesText;
            bool visible = !string.IsNullOrEmpty(snapshot.CoordinatesText);
            if (_coordinateContainer is not null)
                _coordinateContainer.Visible = visible;
            if (_coordinateText is not null)
                _coordinateText.Visible = visible;
        }

        if (_lastUiLocked != snapshot.UiLocked)
        {
            _lastUiLocked = snapshot.UiLocked;
            _radar.Draggable = !snapshot.UiLocked;
            if (_dragButton is not null)
                _dragButton.Visible = !snapshot.UiLocked;
            if (_lockButton is not null)
                _lockButton.TrySetRetailState(
                    snapshot.UiLocked ? RetailUiStateIds.LockedUi : RetailUiStateIds.UnlockedUi);
        }
    }

    private static Vector2 ResolveCenter(ImportedLayout layout)
    {
        if (layout.FindElement(RadarDiscId) is { } disc)
            return new Vector2(disc.Left + disc.Width * 0.5f, disc.Top + disc.Height * 0.5f);

        return new Vector2(layout.Root.Width * 0.5f, MathF.Min(120f, layout.Root.Height) * 0.5f);
    }

    private static CompassToken CreateToken(UiElement? element, RadarCompassPoint point, Vector2 center)
    {
        if (element is null)
            return new CompassToken(null, 0f, point);

        var initialCenter = new Vector2(
            element.Left + element.Width * 0.5f,
            element.Top + element.Height * 0.5f);
        return new CompassToken(element, Vector2.Distance(initialCenter, center), point);
    }

    private UiText? CreateCoordinateText(UiElement? container, UiDatFont? datFont)
    {
        if (container is null)
            return null;

        if (container is UiText importedText)
        {
            importedText.Centered = true;
            importedText.OneLine = true;
            importedText.Padding = 0f;
            importedText.DatFont = datFont ?? importedText.DatFont;
            importedText.ClickThrough = true;
            importedText.AcceptsFocus = false;
            importedText.IsEditControl = false;
            importedText.CapturesPointerDrag = false;
            importedText.LinesProvider = CoordinateLines;
            return importedText;
        }

        var text = new UiText
        {
            Name = "gmRadarUI.Coordinates",
            Left = 0f,
            Top = 0f,
            Width = container.Width,
            Height = container.Height,
            Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Right | AnchorEdges.Bottom,
            Centered = true,
            OneLine = true,
            Padding = 0f,
            DatFont = datFont,
            ClickThrough = true,
            AcceptsFocus = false,
            IsEditControl = false,
            CapturesPointerDrag = false,
            ZOrder = int.MaxValue,
            LinesProvider = CoordinateLines,
        };
        container.AddChild(text);
        return text;
    }

    private IReadOnlyList<UiText.Line> CoordinateLines()
        => string.IsNullOrEmpty(_lastCoordinates)
            ? Array.Empty<UiText.Line>()
            : new[] { new UiText.Line(_lastCoordinates, CoordinateColor) };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _radar.SnapshotProvider = null;
        _radar.SelectObject = null;
        _radar.HoveredObjectChanged = null;
        if (_lockButton is not null)
            _lockButton.OnClick = null;
    }

    private readonly record struct CompassToken(
        UiElement? Element,
        float Magnitude,
        RadarCompassPoint Point);
}
