using System;
using System.Collections.Generic;
using System.Linq;

namespace AcDream.App.UI;

public sealed class RetailWindowManager : IDisposable
{
    private readonly UiRoot _root;
    private readonly Dictionary<string, RetailWindowHandle> _byName =
        new(StringComparer.Ordinal);
    private readonly Dictionary<UiElement, RetailWindowHandle> _byFrame = new();
    private readonly Dictionary<RetailWindowHandle, UiElement> _defaultInputs = new();
    private bool _disposed;

    internal RetailWindowManager(UiRoot root)
    {
        _root = root;
        _root.ElementVisibilityChanged += OnElementVisibilityChanged;
        _root.KeyboardFocusChanged += OnKeyboardFocusChanged;
        _root.PointerCaptureChanged += OnPointerCaptureChanged;
        _root.WindowMoved += OnWindowMoved;
        _root.WindowResized += OnWindowResized;
        _root.UiLockChanged += OnUiLockChanged;
    }

    public bool IsLocked => _root.UiLocked;
    public IReadOnlyCollection<RetailWindowHandle> Windows => _byName.Values;
    public event Action<string, bool>? WindowVisibilityChanged;

    public event Action<RetailWindowHandle>? WindowRegistered;

    internal event Action<RetailWindowHandle>? WindowRegistering;

    public event Action<RetailWindowHandle>? WindowUnregistered;

    public RetailWindowHandle Register(
        string name,
        UiElement outerFrame,
        UiElement? contentRoot = null,
        IRetainedPanelController? controller = null,
        IRetainedWindowStateController? stateController = null,
        int authoredGeometryRevision = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(outerFrame);
        authoredGeometryRevision = Math.Max(0, authoredGeometryRevision);

        if (!ReferenceEquals(outerFrame.Parent, _root))
            throw new InvalidOperationException(
                $"Retained window '{name}' must be mounted as a direct UiRoot child before registration.");

        IRetainedWindowStateController? resolvedStateController =
            stateController ?? outerFrame as IRetainedWindowStateController;

        if (_byName.TryGetValue(name, out var sameName))
        {
            if (ReferenceEquals(sameName.OuterFrame, outerFrame)
                && ReferenceEquals(sameName.ContentRoot, contentRoot ?? outerFrame)
                && ReferenceEquals(sameName.Controller, controller)
                && ReferenceEquals(sameName.StateController, resolvedStateController)
                && sameName.AuthoredGeometryRevision == authoredGeometryRevision)
                return sameName;

            Unregister(name);
        }

        if (_byFrame.TryGetValue(outerFrame, out var sameFrame))
            Unregister(sameFrame.Name);

        var handle = new RetailWindowHandle(
            this,
            name,
            outerFrame,
            contentRoot ?? outerFrame,
            controller,
            resolvedStateController,
            authoredGeometryRevision);
        _byName.Add(name, handle);
        _byFrame.Add(outerFrame, handle);
        WindowRegistering?.Invoke(handle);
        handle.NotifyInitialState();
        WindowRegistered?.Invoke(handle);
        return handle;
    }

    public static int ComputeAuthoredGeometryRevision(
        float width, float height, float minWidth, float minHeight, bool resizable)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + BitConverter.SingleToInt32Bits(width);
            hash = (hash * 31) + BitConverter.SingleToInt32Bits(height);
            hash = (hash * 31) + BitConverter.SingleToInt32Bits(minWidth);
            hash = (hash * 31) + BitConverter.SingleToInt32Bits(minHeight);
            hash = (hash * 31) + (resizable ? 1 : 0);
            return hash & 0x7FFFFFFF;
        }
    }

    public bool TryGet(string name, out RetailWindowHandle handle)
        => _byName.TryGetValue(name, out handle!);

    public void AttachController(string name, IRetainedPanelController controller)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(controller);
        if (!_byName.TryGetValue(name, out var handle))
            throw new KeyNotFoundException($"No retained window named '{name}' is registered.");
        handle.AttachController(controller);
    }

    internal bool TryGet(UiElement outerFrame, out RetailWindowHandle handle)
        => _byFrame.TryGetValue(outerFrame, out handle!);

    public bool Show(string name)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        handle.OuterFrame.Visible = true;
        BringToFront(handle.OuterFrame);
        return true;
    }

    public bool Hide(string name)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        handle.OuterFrame.Visible = false;
        return true;
    }

    public bool Toggle(string name)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        if (handle.OuterFrame.Visible)
        {
            Hide(name);
            return false;
        }

        Show(name);
        return true;
    }

    public bool Close(string name)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        Hide(name);
        handle.NotifyClosed();
        return true;
    }

    public bool IsVisible(string name)
        => _byName.TryGetValue(name, out var handle) && handle.OuterFrame.Visible;

    public bool MoveTo(string name, float left, float top)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        var frame = handle.OuterFrame;
        if (frame.ConstrainDragToParent && frame.Parent is { } parent)
        {
            left = Math.Clamp(left, 0f, Math.Max(0f, parent.Width - frame.Width));
            top = Math.Clamp(top, 0f, Math.Max(0f, parent.Height - frame.Height));
        }
        if (frame.Left == left && frame.Top == top) return true;
        frame.Left = left;
        frame.Top = top;
        frame.ResetAnchorCapture();
        _root.NotifyWindowMoved(frame);
        return true;
    }

    public bool ResizeTo(string name, float width, float height)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        var frame = handle.OuterFrame;
        if (!frame.ResizeX) width = frame.Width;
        if (!frame.ResizeY) height = frame.Height;
        float maxWidth = frame.MaxWidth;
        float maxHeight = frame.MaxHeight;
        if (frame.ConstrainResizeToParent && frame.Parent is { } parent)
        {
            maxWidth = MathF.Min(maxWidth, parent.Width - frame.Left);
            maxHeight = MathF.Min(maxHeight, parent.Height - frame.Top);
        }
        width = Math.Clamp(width, frame.MinWidth, MathF.Max(frame.MinWidth, maxWidth));
        height = Math.Clamp(height, frame.MinHeight, MathF.Max(frame.MinHeight, maxHeight));
        if (frame.Width == width && frame.Height == height) return true;
        frame.Width = width;
        frame.Height = height;
        // Same rebase as MoveTo: keep the intentional resize from being undone
        // by the per-frame anchor layout.
        frame.ResetAnchorCapture();
        _root.NotifyWindowResized(frame);
        return true;
    }

    public bool SetOpacity(string name, float opacity)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;
        handle.OuterFrame.Opacity = Math.Clamp(opacity, 0f, 1f);
        return true;
    }

    public void SetLocked(bool locked) => _root.UiLocked = locked;

    public bool Unregister(string name)
    {
        if (!_byName.TryGetValue(name, out var handle)) return false;

        if (handle.OuterFrame.Visible)
            handle.OuterFrame.Visible = false;
        else
            _root.ClearSubtreeOwnership(handle.OuterFrame);

        _byName.Remove(name);
        _byFrame.Remove(handle.OuterFrame);
        _defaultInputs.Remove(handle);
        handle.NotifyClosed();
        handle.DisposeController();
        WindowUnregistered?.Invoke(handle);
        return true;
    }

    public void BringToFront(UiElement window)
    {
        int top = window.ZOrder;
        foreach (var child in _root.Children)
            if (!ReferenceEquals(child, window))
                top = Math.Max(top, child.ZOrder + 1);
        window.ZOrder = top;
    }

    internal void PrepareToHide(UiElement subtree)
    {
        if (!_byFrame.TryGetValue(subtree, out var handle)) return;
        if (_root.DefaultTextInput is { } input && IsWithin(input, subtree))
            _defaultInputs[handle] = input;
    }

    internal void OnSubtreeRemoving(UiElement subtree)
    {
        foreach (var handle in _byName.Values
            .Where(handle => IsWithin(handle.OuterFrame, subtree))
            .ToArray())
        {
            handle.NotifyVisibility(false);
            _byName.Remove(handle.Name);
            _byFrame.Remove(handle.OuterFrame);
            _defaultInputs.Remove(handle);
            handle.NotifyClosed();
            handle.DisposeController();
            WindowUnregistered?.Invoke(handle);
        }
    }

    private void OnElementVisibilityChanged(UiElement element, bool visible)
    {
        if (!_byFrame.TryGetValue(element, out var handle)) return;

        if (visible
            && _root.DefaultTextInput is null
            && _defaultInputs.TryGetValue(handle, out var input)
            && IsWithin(input, handle.OuterFrame))
        {
            _root.DefaultTextInput = input;
        }

        handle.NotifyVisibility(visible);
        WindowVisibilityChanged?.Invoke(handle.Name, visible);
    }

    private void OnKeyboardFocusChanged(UiElement? oldFocus, UiElement? newFocus)
    {
        foreach (var handle in _byName.Values.ToArray())
        {
            bool hadFocus = IsWithin(oldFocus, handle.OuterFrame);
            bool hasFocus = IsWithin(newFocus, handle.OuterFrame);
            if (hadFocus || hasFocus)
                handle.NotifyDescendantFocusChanged(hasFocus ? newFocus : null);
        }
    }

    private void OnPointerCaptureChanged(UiElement? oldCapture, UiElement? newCapture)
    {
        foreach (var handle in _byName.Values.ToArray())
        {
            bool hadCapture = IsWithin(oldCapture, handle.OuterFrame);
            bool hasCapture = IsWithin(newCapture, handle.OuterFrame);
            if (hadCapture || hasCapture)
                handle.NotifyDescendantCaptureChanged(hasCapture ? newCapture : null);
        }
    }

    private void OnWindowMoved(string name, UiElement window)
    {
        if (_byName.TryGetValue(name, out var handle)
            && ReferenceEquals(handle.OuterFrame, window))
            handle.NotifyMoved();
    }

    private void OnWindowResized(string name, UiElement window)
    {
        if (_byName.TryGetValue(name, out var handle)
            && ReferenceEquals(handle.OuterFrame, window))
            handle.NotifyResized();
    }

    private void OnUiLockChanged(bool locked)
    {
        foreach (var handle in _byName.Values.ToArray())
            handle.NotifyLockChanged(locked);
    }

    private static bool IsWithin(UiElement? element, UiElement subtree)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, subtree)) return true;
            element = element.Parent;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (string name in _byName.Keys.ToArray())
            Unregister(name);

        _root.ElementVisibilityChanged -= OnElementVisibilityChanged;
        _root.KeyboardFocusChanged -= OnKeyboardFocusChanged;
        _root.PointerCaptureChanged -= OnPointerCaptureChanged;
        _root.WindowMoved -= OnWindowMoved;
        _root.WindowResized -= OnWindowResized;
        _root.UiLockChanged -= OnUiLockChanged;
    }
}
