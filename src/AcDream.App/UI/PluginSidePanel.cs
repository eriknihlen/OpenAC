using System.Numerics;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.UI;

public sealed class PluginSidePanel : UiPanel, IDisposable, IRetainedWindowStateController, IRetainedPanelController
{
    private const float OuterPadding = 4f;
    private const float ButtonExtent = 28f;
    private const float ButtonGap = 4f;
    private const float DefaultTop = 116f;

    private const float GripHeight = 12f;

    internal float ExpandedGripBandHeight =>
        _font is { } f ? MathF.Max(GripHeight, f.LineHeight + 2f) : GripHeight;

    private const float ToggleWidth = 16f;

    private const float CollapsedWidth = ToggleWidth + OuterPadding * 2f;

    private static readonly Vector4 ToggleGlyphColor = new(0.86f, 0.72f, 0.32f, 1f);

    private readonly RetailWindowManager _windows;
    private readonly Func<uint, (uint tex, int width, int height)> _resolve;
    private readonly UiDatFont? _font;
    private readonly Dictionary<RetailWindowHandle, ShelfEntry> _entries = [];
    private readonly ShelfGripPanel _grip;
    private readonly UiSimpleButton _toggle;
    private bool _disposed;
    private float _lastLayoutHeight = -1f;

    private bool _collapsed;

    private bool _requestedVisible = true;

    private bool _userPositioned;

    private bool _initialDockApplied;

    private float _dockLeft;
    private float _dockTop;

    private RetailWindowHandle? _handle;

    public PluginSidePanel(
        RetailWindowManager windows,
        Func<uint, (uint tex, int width, int height)> resolve,
        UiDatFont? font)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _font = font;

        Width = ButtonExtent + OuterPadding * 2f;
        Height = ExpandedGripBandHeight + OuterPadding * 2f;
        Top = DefaultTop;
        Anchors = AnchorEdges.None;
        Draggable = false;
        ConstrainDragToParent = true;
        Resizable = false;
        // Nit 10: the shelf's Width/Height are entirely derived (Reflow), so a
        // restored layout's saved dimensions must never stomp them via ResizeTo.
        ResizeX = false;
        ResizeY = false;
        BackgroundColor = new Vector4(0f, 0f, 0f, 0.88f);
        BorderColor = new Vector4(0.62f, 0.48f, 0.16f, 1f);
        BorderThickness = 1f;
        Visible = false;

        _grip = new ShelfGripPanel
        {
            WindowMoveHandle = true,
            BackgroundColor = Vector4.Zero,
            BorderColor = Vector4.Zero,
            Anchors = AnchorEdges.None,
        };
        _toggle = new UiSimpleButton
        {
            BackgroundColor = Vector4.Zero,
            BorderColor = Vector4.Zero,
            TextColor = ToggleGlyphColor,
            DatFont = _font,
            Outline = true,
            TextSource = () => _collapsed ? "<" : ">",
            Anchors = AnchorEdges.None,
        };
        _toggle.Click += ToggleCollapsed;
        AddChild(_grip);
        AddChild(_toggle);
        LayoutChrome();

        _windows.WindowUnregistered += OnWindowUnregistered;
        _windows.WindowRegistered += OnWindowRegistered;
    }

    /// <summary>Number of live plugin-window entries, exposed for gates.</summary>
    public int EntryCount => _entries.Count;

    /// <summary>
    /// Adds one manifest-scoped plugin window and its minimize affordance.
    /// Duplicate handles are idempotent.
    /// </summary>
    public void Add(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        RetailWindowHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handle);
        if (_entries.ContainsKey(handle))
            return;

        handle.OuterFrame.ConstrainDragToParent = true;
        handle.OuterFrame.ConstrainResizeToParent = true;
        KeepWindowReachable(handle);

        var button = new PluginShelfButton(
            descriptor,
            owner.DisplayName,
            handle,
            _resolve,
            _font)
        {
            Width = ButtonExtent,
            Height = ButtonExtent,
        };
        button.Click += () =>
        {
            if (handle.IsVisible)
                handle.Hide();
            else
                handle.Show();
        };

        var minimize = new PluginMinimizeButton(handle, _font)
        {
            Left = MathF.Max(8f, handle.OuterFrame.Width - 23f),
            Top = 3f,
            Width = 18f,
            Height = 17f,
            Anchors = AnchorEdges.Top | AnchorEdges.Right,
        };
        handle.OuterFrame.AddChild(minimize);

        _entries.Add(handle, new ShelfEntry(button, minimize));
        AddChild(button);
        Reflow();
    }

    public void Show()
    {
        _requestedVisible = true;
        if (_collapsed)
            _collapsed = false;
        Reflow();
        _handle?.NotifyStateChanged();
    }

    /// <summary>Hide the shelf. Preserves entries/positions; never disables a
    /// plugin or touches any plugin window's own visibility.</summary>
    public void Hide()
    {
        _requestedVisible = false;
        Reflow();
        _handle?.NotifyStateChanged();
    }

    protected override void OnTick(double deltaSeconds)
    {
        base.OnTick(deltaSeconds);

        _grip.Opacity = _windows.IsLocked ? 0.5f : 1f;

        if (Parent is { } parent)
        {
            float availableHeight = MathF.Max(
                ButtonExtent + OuterPadding * 2f,
                parent.Height - Top - ExpandedGripBandHeight - OuterPadding);
            if (MathF.Abs(availableHeight - _lastLayoutHeight) > 0.5f)
            {
                _lastLayoutHeight = availableHeight;
                Reflow(availableHeight);
            }

            if (!_initialDockApplied && !_userPositioned && parent.Width > 0f)
            {
                float dockLeft = MathF.Max(0f, parent.Width - Width - OuterPadding);
                float dockTop = Top;
                _dockLeft = dockLeft;
                _dockTop = dockTop;
                _initialDockApplied = true;
                if (_handle is { } handle)
                    handle.MoveTo(dockLeft, dockTop);
                else
                    Left = dockLeft;
            }
        }

        foreach (RetailWindowHandle handle in _entries.Keys)
            KeepWindowReachable(handle);

    }

    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        Reflow();
        _handle?.NotifyStateChanged();
    }

    private void OnWindowRegistered(RetailWindowHandle handle)
    {
        if (!ReferenceEquals(handle.OuterFrame, this)) return;
        _handle = handle;
        handle.Moved += OnHandleMoved;
        _windows.WindowRegistered -= OnWindowRegistered;
    }

    private void OnHandleMoved(RetailWindowHandle _)
    {
        if (Parent is { } parent)
        {
            float currentDockLeft = MathF.Max(0f, parent.Width - Width - OuterPadding);
            float reachabilityClampOfPriorDock = Math.Clamp(
                _dockLeft, 0f, MathF.Max(0f, parent.Width - Width));
            bool stillDocked = Top == _dockTop
                && (Left == currentDockLeft || Left == reachabilityClampOfPriorDock);
            if (stillDocked)
            {
                _dockLeft = Left;
                _dockTop = Top;
                return;
            }
        }

        if (Left != _dockLeft || Top != _dockTop)
            _userPositioned = true;
    }

    // ── IRetainedPanelController: separates "has entries" (availability) from
    // the user's own show/hide request — see ApplyVisibility. ────────────────

    void IRetainedPanelController.OnShown() => _requestedVisible = true;

    void IRetainedPanelController.OnHidden()
    {
        // Hidden because EntryCount hit zero is temporary (availability gate);
        // hidden while entries remain is a real user hide.
        if (_entries.Count > 0)
            _requestedVisible = false;
    }


    public RetainedWindowState CaptureWindowState() =>
        new(Collapsed: _collapsed, RequestedVisible: _requestedVisible);

    public void RestoreWindowState(RetainedWindowState state)
    {
        _collapsed = state.Collapsed;
        if (state.RequestedVisible is { } requestedVisible)
            _requestedVisible = requestedVisible;
        Reflow();
    }

    private void OnWindowUnregistered(RetailWindowHandle handle)
    {
        if (!_entries.Remove(handle, out ShelfEntry entry))
            return;

        RemoveChild(entry.Button);
        if (ReferenceEquals(entry.Minimize.Parent, handle.OuterFrame))
            handle.OuterFrame.RemoveChild(entry.Minimize);
        entry.Button.DisposeSubscriptions();
        Reflow();
    }

    private static void KeepWindowReachable(RetailWindowHandle handle)
    {
        if (handle.OuterFrame.Parent is not { } parent
            || parent.Width <= 0f
            || parent.Height <= 0f)
        {
            return;
        }

        float left = Math.Clamp(
            handle.Left,
            0f,
            MathF.Max(0f, parent.Width - handle.Width));
        float top = Math.Clamp(
            handle.Top,
            0f,
            MathF.Max(0f, parent.Height - handle.Height));
        if (left != handle.Left || top != handle.Top)
            handle.MoveTo(left, top);
    }

    private void LayoutChrome()
    {
        float bandHeight = _collapsed ? ButtonExtent : ExpandedGripBandHeight;
        _grip.Left = 0f;
        _grip.Top = 0f;
        _grip.Width = MathF.Max(0f, Width - ToggleWidth);
        _grip.Height = bandHeight;

        _toggle.Left = Width - ToggleWidth;
        _toggle.Top = 0f;
        _toggle.Width = ToggleWidth;
        _toggle.Height = bandHeight;
    }

    private void Reflow(float? maximumHeight = null)
    {
        float effectiveHeight = maximumHeight
            ?? (_lastLayoutHeight >= 0f ? _lastLayoutHeight : float.PositiveInfinity);
        float oldWidth = Width;

        int maximumRows = float.IsPositiveInfinity(effectiveHeight)
            ? Math.Max(1, _entries.Count)
            : Math.Max(
                1,
                (int)MathF.Floor(
                    (effectiveHeight - OuterPadding * 2f + ButtonGap)
                    / (ButtonExtent + ButtonGap)));
        int index = 0;
        foreach (ShelfEntry entry in _entries.Values)
        {
            int column = index / maximumRows;
            int row = index % maximumRows;
            entry.Button.Left = OuterPadding
                + column * (ButtonExtent + ButtonGap);
            entry.Button.Top = ExpandedGripBandHeight + OuterPadding
                + row * (ButtonExtent + ButtonGap);
            entry.Button.Visible = !_collapsed;
            index++;
        }

        int rows = Math.Min(index, maximumRows);
        int columns = index == 0 ? 1 : (index + maximumRows - 1) / maximumRows;

        if (_collapsed)
        {
            Width = CollapsedWidth;
            Height = ButtonExtent;
        }
        else
        {
            Width = OuterPadding * 2f
                + columns * ButtonExtent
                + Math.Max(0, columns - 1) * ButtonGap;
            Height = ExpandedGripBandHeight
                + OuterPadding * 2f
                + rows * ButtonExtent
                + Math.Max(0, rows - 1) * ButtonGap;
        }

        if (_initialDockApplied && !_userPositioned)
            Left += oldWidth - Width;

        LayoutChrome();
        ApplyVisibility();

        if (_initialDockApplied && !_userPositioned)
        {
            _dockLeft = Left;
            _dockTop = Top;
        }
    }

    private void ApplyVisibility()
    {
        Visible = _requestedVisible && _entries.Count > 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _windows.WindowUnregistered -= OnWindowUnregistered;
        _windows.WindowRegistered -= OnWindowRegistered;
        if (_handle is { } ownHandle)
            ownHandle.Moved -= OnHandleMoved;

        foreach ((RetailWindowHandle handle, ShelfEntry entry) in _entries)
        {
            entry.Button.DisposeSubscriptions();
            if (ReferenceEquals(entry.Minimize.Parent, handle.OuterFrame))
                handle.OuterFrame.RemoveChild(entry.Minimize);
        }
        _entries.Clear();
        Visible = false;
    }

    private readonly record struct ShelfEntry(
        PluginShelfButton Button,
        PluginMinimizeButton Minimize);

    private sealed class ShelfGripPanel : UiPanel
    {
        private static readonly Vector4 DashColor = new(0.62f, 0.48f, 0.16f, 1f);

        protected override void OnDraw(UiRenderContext ctx)
        {
            base.OnDraw(ctx);

            const float dashWidth = 5f;
            const float dashGap = 4f;
            float totalDashWidth = dashWidth * 3f + dashGap * 2f;
            float dashX = MathF.Max(2f, (Width - totalDashWidth) * 0.5f);
            float dashY = Height * 0.5f - 1f;
            for (int i = 0; i < 3; i++)
                ctx.DrawFill(dashX + i * (dashWidth + dashGap), dashY, dashWidth, 2f, DashColor);
        }
    }

    internal sealed class PluginShelfButton : UiSimpleButton
    {
        private static readonly Vector4 HiddenBackground =
            new(0.025f, 0.025f, 0.02f, 0.96f);
        private static readonly Vector4 VisibleBackground =
            new(0.09f, 0.19f, 0.055f, 0.96f);
        private static readonly Vector4 HiddenBorder =
            new(0.48f, 0.38f, 0.14f, 1f);
        private static readonly Vector4 VisibleBorder =
            new(0.76f, 0.64f, 0.25f, 1f);

        private readonly RetailWindowHandle _handle;
        private readonly Func<uint, (uint tex, int width, int height)> _resolve;
        private readonly uint _iconSurfaceId;
        private readonly string _tooltip;
        private readonly string _initialsFallback;

        private bool _iconResolveAttempted;
        private bool _iconAvailable;

        internal PluginShelfButton(
            PluginPanelDescriptor descriptor,
            string ownerDisplayName,
            RetailWindowHandle handle,
            Func<uint, (uint tex, int width, int height)> resolve,
            UiDatFont? font)
        {
            _handle = handle;
            _resolve = resolve;
            _iconSurfaceId = PluginIcons.Normalize(descriptor.IconSurfaceId);
            _tooltip = string.Equals(descriptor.Title, ownerDisplayName,
                    StringComparison.Ordinal)
                ? descriptor.Title
                : $"{ownerDisplayName} — {descriptor.Title}";
            _initialsFallback = Initials(descriptor.IconText, descriptor.Title);
            Text = _iconSurfaceId == 0 ? _initialsFallback : string.Empty;
            DatFont = font;
            Outline = true;
            BorderThickness = 1f;
            Anchors = AnchorEdges.None;
            _handle.Shown += OnVisibilityChanged;
            _handle.Hidden += OnVisibilityChanged;
            RefreshPresentation();
        }

        public override string? GetTooltipText() => _tooltip;

        protected override void OnTick(double deltaSeconds)
        {
            base.OnTick(deltaSeconds);
            RefreshPresentation();
        }

        protected override void OnDraw(UiRenderContext ctx)
        {
            if (!_iconResolveAttempted && _iconSurfaceId != 0)
            {
                _iconResolveAttempted = true;
                (uint tex, int w, int h) = _resolve(_iconSurfaceId);
                _iconAvailable = tex != 0 && w > 0 && h > 0;
                if (!_iconAvailable)
                    Text = _initialsFallback;
            }

            base.OnDraw(ctx);

            if (_iconSurfaceId == 0 || !_iconAvailable)
                return;

            (uint texture, int width, int height) = _resolve(_iconSurfaceId);
            if (texture == 0 || width <= 0 || height <= 0)
                return;
            float extent = MathF.Min(Width - 6f, Height - 6f);
            ctx.DrawSprite(
                texture,
                (Width - extent) * 0.5f,
                (Height - extent) * 0.5f,
                extent,
                extent,
                0f,
                0f,
                1f,
                1f,
                Vector4.One);
        }

        internal void DisposeSubscriptions()
        {
            _handle.Shown -= OnVisibilityChanged;
            _handle.Hidden -= OnVisibilityChanged;
        }

        private void OnVisibilityChanged(RetailWindowHandle _) =>
            RefreshPresentation();

        private void RefreshPresentation()
        {
            BackgroundColor = _handle.IsVisible
                ? VisibleBackground
                : HiddenBackground;
            BorderColor = _handle.IsVisible ? VisibleBorder : HiddenBorder;
        }

        private static string Initials(string? requested, string title)
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return requested.Trim()[..Math.Min(3, requested.Trim().Length)];

            string[] words = title.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
                return "?";
            if (words.Length == 1)
                return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
            return string.Concat(words.Take(2).Select(static word =>
                char.ToUpperInvariant(word[0])));
        }
    }

    private sealed class PluginMinimizeButton : UiSimpleButton
    {
        private readonly RetailWindowHandle _handle;

        internal PluginMinimizeButton(RetailWindowHandle handle, UiDatFont? font)
        {
            _handle = handle;
            Text = "–";
            DatFont = font;
            Outline = true;
            BackgroundColor = new Vector4(0.02f, 0.02f, 0.015f, 0.94f);
            BorderColor = new Vector4(0.58f, 0.46f, 0.17f, 1f);
            BorderThickness = 1f;
            Click += () => _handle.Hide();
        }

        public override string? GetTooltipText() => "Minimize to plugin sidepanel";
    }
}
