using AcDream.App.UI;
using AcDream.Core.Textures;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using Silk.NET.Core;
using Silk.NET.Input;

namespace AcDream.App.Rendering;

public sealed class RetailCursorManager
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly RetailCursorResolver _globalCursors;
    private readonly Dictionary<uint, RawImage> _imagesBySurface = new();
    private readonly HashSet<uint> _missingSurfaces = new();
    private readonly HashSet<RetailGlobalCursorKind> _reportedFallbacks = new();
    private bool _hasLayerState;
    private RetailGlobalCursorKind _lastGlobalKind;
    private UiCursorMedia _lastWidgetCursor;
    private UiCursorMedia _lastAppliedCursor;
    private StandardCursor? _lastStandardCursor;
    private GlfwCursorCache? _nativeCursors;

    public RetailCursorManager(IDatReaderWriter dats, object datLock)
    {
        _dats = dats;
        _datLock = datLock;
        _globalCursors = new RetailCursorResolver(dats, datLock);
    }

    public void AttachNativeWindow(nint glfwWindowHandle)
    {
        if (_nativeCursors is null)
        {
            _nativeCursors = GlfwCursorCache.TryCreate(glfwWindowHandle);
            if (_nativeCursors is not null)
                Console.WriteLine("[UI] cursor native cache attached.");
        }
    }

    public void Apply(IEnumerable<IMouse> mice, CursorFeedback feedback)
    {
        foreach (RetailCursorLayer layer in PlanApplication(
            _hasLayerState,
            _lastGlobalKind,
            _lastWidgetCursor,
            feedback.GlobalKind,
            feedback.Cursor))
        {
            if (layer == RetailCursorLayer.Widget
                && feedback.Cursor.IsValid
                && TryGetImage(feedback.Cursor.File, out var image))
            {
                ApplyCustom(mice, feedback.Cursor, image);
            }
            else
            {
                ApplyGlobal(mice, feedback.GlobalKind);
            }
        }

        _hasLayerState = true;
        _lastGlobalKind = feedback.GlobalKind;
        _lastWidgetCursor = feedback.Cursor;
    }

    private void ApplyGlobal(IEnumerable<IMouse> mice, RetailGlobalCursorKind kind)
    {
        if (_globalCursors.TryResolve(kind, out var globalCursor)
            && TryGetImage(globalCursor.File, out var globalImage))
        {
            ApplyCustom(mice, globalCursor, globalImage);
            return;
        }

        if (_reportedFallbacks.Add(kind))
        {
            Console.Error.WriteLine(
                $"[UI] retail cursor {kind} could not be resolved from DAT; " +
                "using the registered OS cursor adaptation.");
        }
        ApplyStandard(mice, StandardCursorFor(kind));
    }

    private void ApplyCustom(IEnumerable<IMouse> mice, UiCursorMedia cursorMedia, RawImage image)
    {
        if (_lastStandardCursor is null && _lastAppliedCursor.Equals(cursorMedia))
            return;

        if (_nativeCursors is not null && _nativeCursors.TrySetCustom(cursorMedia, image))
        {
            _lastAppliedCursor = cursorMedia;
            _lastStandardCursor = null;
            return;
        }

        foreach (var mouse in mice)
        {
            var cursor = mouse.Cursor;
            cursor.Image = image;
            cursor.HotspotX = cursorMedia.HotspotX;
            cursor.HotspotY = cursorMedia.HotspotY;
            if (cursor.Type != CursorType.Custom)
                cursor.Type = CursorType.Custom;
        }

        _lastAppliedCursor = cursorMedia;
        _lastStandardCursor = null;
    }

    private void ApplyStandard(IEnumerable<IMouse> mice, StandardCursor desired)
    {
        if (_lastStandardCursor == desired)
            return;

        if (_nativeCursors is not null && _nativeCursors.TrySetStandard(desired))
        {
            _lastAppliedCursor = default;
            _lastStandardCursor = desired;
            return;
        }

        foreach (var mouse in mice)
        {
            var cursor = mouse.Cursor;
            var standard = desired;
            if (!cursor.IsSupported(standard))
                standard = StandardCursor.Arrow;
            if (!cursor.IsSupported(standard))
                continue;

            if (cursor.Type != CursorType.Standard)
                cursor.Type = CursorType.Standard;
            if (cursor.StandardCursor != standard)
                cursor.StandardCursor = standard;
        }

        _lastAppliedCursor = default;
        _lastStandardCursor = desired;
    }

    private bool TryGetImage(uint renderSurfaceId, out RawImage image)
    {
        if (_imagesBySurface.TryGetValue(renderSurfaceId, out image))
            return true;
        if (_missingSurfaces.Contains(renderSurfaceId))
            return false;

        DecodedTexture? decoded = DecodeCursorSurface(renderSurfaceId);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0 || decoded.Rgba8.Length == 0)
        {
            _missingSurfaces.Add(renderSurfaceId);
            image = default;
            return false;
        }

        image = new RawImage(decoded.Width, decoded.Height, decoded.Rgba8);
        _imagesBySurface[renderSurfaceId] = image;
        return true;
    }

    private DecodedTexture? DecodeCursorSurface(uint renderSurfaceId)
    {
        lock (_datLock)
        {
            if (!_dats.Portal.TryGet<RenderSurface>(renderSurfaceId, out var rs)
                && !_dats.HighRes.TryGet<RenderSurface>(renderSurfaceId, out rs))
                return null;

            Palette? palette = rs.DefaultPaletteId != 0
                ? _dats.Get<Palette>(rs.DefaultPaletteId)
                : null;
            return SurfaceDecoder.DecodeRenderSurface(rs, palette);
        }
    }

    internal static IReadOnlyList<RetailCursorLayer> PlanApplication(
        bool hasLayerState,
        RetailGlobalCursorKind previousGlobal,
        UiCursorMedia previousWidget,
        RetailGlobalCursorKind currentGlobal,
        UiCursorMedia currentWidget)
    {
        bool globalChanged = !hasLayerState || previousGlobal != currentGlobal;
        bool widgetChanged = !hasLayerState || previousWidget != currentWidget;
        if (!globalChanged && !widgetChanged)
            return Array.Empty<RetailCursorLayer>();

        var result = new List<RetailCursorLayer>(2);
        if (globalChanged)
            result.Add(RetailCursorLayer.Global);

        if (widgetChanged)
        {
            RetailCursorLayer widgetResult = currentWidget.IsValid
                ? RetailCursorLayer.Widget
                : RetailCursorLayer.Global;
            if (result.Count == 0 || result[^1] != widgetResult)
                result.Add(widgetResult);
        }

        return result;
    }

    private static StandardCursor StandardCursorFor(RetailGlobalCursorKind kind)
        => kind switch
        {
            RetailGlobalCursorKind.Use or RetailGlobalCursorKind.UseFound => StandardCursor.Hand,
            RetailGlobalCursorKind.TargetPending => StandardCursor.Crosshair,
            RetailGlobalCursorKind.TargetValid => StandardCursor.ResizeAll,
            RetailGlobalCursorKind.TargetInvalid => StandardCursor.NotAllowed,
            _ => StandardCursor.Arrow,
        };
}

internal enum RetailCursorLayer
{
    Global,
    Widget,
}
