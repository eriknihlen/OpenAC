using System;
using System.Collections.Generic;
using AcDream.App.UI;
using Silk.NET.Core;
using Silk.NET.GLFW;
using Silk.NET.Input;

namespace AcDream.App.Rendering;

internal sealed unsafe class GlfwCursorCache : IDisposable
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly Dictionary<UiCursorMedia, nint> _customCursors = new();
    private readonly Dictionary<StandardCursor, nint> _standardCursors = new();
    private bool _disposed;

    private GlfwCursorCache(Glfw glfw, WindowHandle* window)
    {
        _glfw = glfw;
        _window = window;
    }

    public static GlfwCursorCache? TryCreate(nint glfwWindowHandle)
        => glfwWindowHandle == 0
            ? null
            : new GlfwCursorCache(Glfw.GetApi(), (WindowHandle*)glfwWindowHandle);

    public bool TrySetCustom(UiCursorMedia media, RawImage image)
    {
        if (_disposed)
            return false;

        if (!_customCursors.TryGetValue(media, out nint cursor))
        {
            cursor = CreateCustomCursor(media, image);
            _customCursors[media] = cursor;
        }

        if (cursor == 0)
            return false;

        _glfw.SetCursor(_window, (Cursor*)cursor);
        return true;
    }

    public bool TrySetStandard(StandardCursor desired)
    {
        if (_disposed)
            return false;

        if (desired == StandardCursor.Arrow)
        {
            _glfw.SetCursor(_window, null);
            return true;
        }

        CursorShape shape;
        switch (desired)
        {
            case StandardCursor.Hand: shape = CursorShape.Hand; break;
            case StandardCursor.Crosshair: shape = CursorShape.Crosshair; break;
            case StandardCursor.IBeam: shape = CursorShape.IBeam; break;
            default: return false;
        }

        if (!_standardCursors.TryGetValue(desired, out nint cursor))
        {
            cursor = (nint)_glfw.CreateStandardCursor(shape);
            _standardCursors[desired] = cursor;
        }

        if (cursor == 0)
            return false;

        _glfw.SetCursor(_window, (Cursor*)cursor);
        return true;
    }

    private nint CreateCustomCursor(UiCursorMedia media, RawImage image)
    {
        fixed (byte* pixels = image.Pixels.Span)
        {
            var glfwImage = new Image
            {
                Width = image.Width,
                Height = image.Height,
                Pixels = pixels,
            };
            return (nint)_glfw.CreateCursor(&glfwImage, media.HotspotX, media.HotspotY);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _glfw.SetCursor(_window, null);
        foreach (nint cursor in _customCursors.Values)
        {
            if (cursor != 0)
                _glfw.DestroyCursor((Cursor*)cursor);
        }
        foreach (nint cursor in _standardCursors.Values)
        {
            if (cursor != 0)
                _glfw.DestroyCursor((Cursor*)cursor);
        }
        _customCursors.Clear();
        _standardCursors.Clear();
    }
}
