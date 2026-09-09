using System;

namespace AcDream.App.UI;

public sealed class RetailWindowHandle
{
    private readonly RetailWindowManager _owner;
    private bool _notifiedVisible;
    private bool _closedSinceShown;
    private bool _disposed;

    internal RetailWindowHandle(
        RetailWindowManager owner,
        string name,
        UiElement outerFrame,
        UiElement contentRoot,
        IRetainedPanelController? controller,
        IRetainedWindowStateController? stateController,
        int authoredGeometryRevision)
    {
        _owner = owner;
        Name = name;
        OuterFrame = outerFrame;
        ContentRoot = contentRoot;
        Controller = controller;
        StateController = stateController;
        AuthoredGeometryRevision = Math.Max(0, authoredGeometryRevision);
        AuthoredWidth = outerFrame.Width;
        AuthoredHeight = outerFrame.Height;
        _notifiedVisible = outerFrame.Visible;
    }

    public string Name { get; }
    public UiElement OuterFrame { get; }
    public UiElement ContentRoot { get; }
    public IRetainedPanelController? Controller { get; private set; }
    public IRetainedWindowStateController? StateController { get; }
    public int AuthoredGeometryRevision { get; }
    public float AuthoredWidth { get; }
    public float AuthoredHeight { get; }
    public bool IsRegistered => !_disposed;
    public bool IsVisible => OuterFrame.Visible;
    public bool IsLocked => _owner.IsLocked;
    public float Left => OuterFrame.Left;
    public float Top => OuterFrame.Top;
    public float Width => OuterFrame.Width;
    public float Height => OuterFrame.Height;
    public float Opacity => OuterFrame.Opacity;

    public event Action<RetailWindowHandle>? Shown;
    public event Action<RetailWindowHandle>? Hidden;
    public event Action<RetailWindowHandle>? Moved;
    public event Action<RetailWindowHandle>? Resized;
    public event Action<RetailWindowHandle>? Closed;

    internal event Action<RetailWindowHandle>? StateChanged;
    public event Action<RetailWindowHandle, bool>? LockChanged;
    public event Action<RetailWindowHandle, UiElement?>? DescendantFocusChanged;
    public event Action<RetailWindowHandle, UiElement?>? DescendantCaptureChanged;

    public bool Show() => IsRegistered && _owner.Show(Name);
    public bool Hide() => IsRegistered && _owner.Hide(Name);
    public bool Close() => IsRegistered && _owner.Close(Name);
    public bool MoveTo(float left, float top)
        => IsRegistered && _owner.MoveTo(Name, left, top);
    public bool ResizeTo(float width, float height)
        => IsRegistered && _owner.ResizeTo(Name, width, height);
    public bool SetOpacity(float opacity)
        => IsRegistered && _owner.SetOpacity(Name, opacity);

    internal void NotifyInitialState()
    {
        if (_notifiedVisible)
            Controller?.OnShown();
    }

    internal void AttachController(IRetainedPanelController controller)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(controller);
        if (ReferenceEquals(Controller, controller)) return;
        if (Controller is not null)
            throw new InvalidOperationException($"Retained window '{Name}' already owns a controller.");

        Controller = controller;
        if (_notifiedVisible)
            Controller.OnShown();
    }

    internal void NotifyVisibility(bool visible)
    {
        if (_notifiedVisible == visible) return;
        _notifiedVisible = visible;

        if (visible)
        {
            _closedSinceShown = false;
            Controller?.OnShown();
            Shown?.Invoke(this);
        }
        else
        {
            Controller?.OnHidden();
            Hidden?.Invoke(this);
        }
    }

    internal void NotifyMoved() => Moved?.Invoke(this);
    internal void NotifyResized() => Resized?.Invoke(this);

    internal void NotifyStateChanged() => StateChanged?.Invoke(this);

    internal void NotifyClosed()
    {
        if (_closedSinceShown) return;
        _closedSinceShown = true;
        Closed?.Invoke(this);
    }

    internal void NotifyLockChanged(bool locked) => LockChanged?.Invoke(this, locked);

    internal void NotifyDescendantFocusChanged(UiElement? focusedDescendant)
    {
        Controller?.OnDescendantFocusChanged(focusedDescendant);
        DescendantFocusChanged?.Invoke(this, focusedDescendant);
    }

    internal void NotifyDescendantCaptureChanged(UiElement? capturedDescendant)
        => DescendantCaptureChanged?.Invoke(this, capturedDescendant);

    internal void DisposeController()
    {
        if (_disposed) return;
        _disposed = true;
        Controller?.Dispose();
    }
}
