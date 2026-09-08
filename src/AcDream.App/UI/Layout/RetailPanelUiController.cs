using System;
using System.Collections.Generic;

namespace AcDream.App.UI.Layout;

public sealed class RetailPanelUiController : IDisposable
{
    public const uint RestorePreviousPropertyId = 0x10000049u;

    private readonly Func<string, bool> _isVisible;
    private readonly Func<string, bool> _show;
    private readonly Func<string, bool> _hide;
    private readonly Dictionary<uint, PanelEntry> _byPanel = new();
    private readonly Dictionary<string, uint> _byWindow = new(StringComparer.Ordinal);
    private uint? _activePanel;
    private uint? _deferredPanel;
    private bool _applying;
    private bool _synchronizingGeometry;
    private PanelGeometry? _mainPanelGeometry;
    private bool _disposed;

    public RetailPanelUiController(
        Func<string, bool> isVisible,
        Func<string, bool> show,
        Func<string, bool> hide)
    {
        _isVisible = isVisible ?? throw new ArgumentNullException(nameof(isVisible));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
    }

    public uint? ActivePanelId => _activePanel;

    public void RegisterMainPanel(
        uint panelId,
        string windowName,
        RetailWindowHandle window,
        bool restorePrevious = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!string.Equals(windowName, window.Name, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Panel window name '{windowName}' does not match handle '{window.Name}'.",
                nameof(windowName));

        RegisterCore(
            panelId,
            windowName,
            restorePrevious,
            window,
            sharesMainPanelGeometry: true);
    }

    public void Register(uint panelId, string windowName, bool restorePrevious = false)
        => RegisterCore(
            panelId,
            windowName,
            restorePrevious,
            window: null,
            sharesMainPanelGeometry: false);

    private void RegisterCore(
        uint panelId,
        string windowName,
        bool restorePrevious,
        RetailWindowHandle? window,
        bool sharesMainPanelGeometry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (panelId == 0) throw new ArgumentOutOfRangeException(nameof(panelId));
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName);
        if (_byPanel.ContainsKey(panelId) || _byWindow.ContainsKey(windowName))
            throw new InvalidOperationException(
                $"Panel {panelId} or retained window '{windowName}' is already registered.");

        var entry = new PanelEntry(
            windowName,
            restorePrevious,
            window,
            sharesMainPanelGeometry);
        _byPanel.Add(panelId, entry);
        _byWindow.Add(windowName, panelId);
        if (window is not null)
        {
            window.Moved += OnWindowMoved;
            window.Resized += OnWindowResized;
        }

        if (sharesMainPanelGeometry && _mainPanelGeometry is { } geometry)
            ApplyWindowGeometry(entry, geometry);
        if (_isVisible(windowName))
            ObserveWindowVisibility(windowName, visible: true);
    }

    public void Register(uint panelId, string windowName, ElementInfo authoredRoot)
    {
        ArgumentNullException.ThrowIfNull(authoredRoot);
        Register(
            panelId,
            windowName,
            authoredRoot.TryGetEffectiveBool(
                RestorePreviousPropertyId,
                out bool restorePrevious)
                && restorePrevious);
    }

    public bool IsPanelVisible(uint panelId)
        => _byPanel.TryGetValue(panelId, out PanelEntry entry)
           && _isVisible(entry.WindowName);

    public bool TogglePanel(uint panelId)
    {
        bool visible = !IsPanelVisible(panelId);
        return SetPanelVisibility(panelId, visible) && visible;
    }

    public bool SetPanelVisibility(uint panelId, bool visible)
    {
        if (!_byPanel.TryGetValue(panelId, out PanelEntry requested)) return false;

        _applying = true;
        try
        {
            if (visible)
            {
                if (_activePanel == panelId)
                {
                    PrepareMainPanelGeometry(requested);
                    return _show(requested.WindowName);
                }

                uint? previousId = _activePanel;
                PanelEntry? previous = previousId is uint id
                    && _byPanel.TryGetValue(id, out PanelEntry found)
                    && _isVisible(found.WindowName)
                        ? found
                        : null;

                if (previous is { } previousEntry)
                    CaptureMainPanelGeometry(previousEntry, synchronizeSiblings: true);

                _deferredPanel = requested.RestorePrevious
                    && previous is { RestorePrevious: false }
                        ? previousId
                        : null;
                _activePanel = panelId;
                if (previous is not null)
                    _hide(previous.Value.WindowName);
                PrepareMainPanelGeometry(requested);
                return _show(requested.WindowName);
            }

            if (_activePanel != panelId)
            {
                if (_deferredPanel == panelId) _deferredPanel = null;
                return _hide(requested.WindowName);
            }

            CaptureMainPanelGeometry(requested, synchronizeSiblings: true);
            bool hidden = _hide(requested.WindowName);
            _activePanel = null;
            if (_deferredPanel is not uint deferredId
                || !_byPanel.TryGetValue(deferredId, out PanelEntry deferred))
                return hidden;

            _deferredPanel = null;
            _activePanel = deferredId;
            PrepareMainPanelGeometry(deferred);
            return _show(deferred.WindowName) || hidden;
        }
        finally
        {
            _applying = false;
        }
    }

    public void ObserveWindowVisibility(string windowName, bool visible)
    {
        if (_applying || !_byWindow.TryGetValue(windowName, out uint panelId)) return;
        SetPanelVisibility(panelId, visible);
    }

    private void OnWindowMoved(RetailWindowHandle window)
    {
        if (_disposed || _synchronizingGeometry) return;

        foreach (PanelEntry entry in _byPanel.Values)
        {
            if (!entry.SharesMainPanelGeometry
                || !ReferenceEquals(entry.Window, window))
                continue;

            CaptureAndSynchronizeMainPanelGeometry(window);
            return;
        }
    }

    private void OnWindowResized(RetailWindowHandle window)
    {
        if (_disposed || _synchronizingGeometry) return;

        foreach (PanelEntry entry in _byPanel.Values)
        {
            if (!entry.SharesMainPanelGeometry
                || !ReferenceEquals(entry.Window, window))
                continue;

            CaptureAndSynchronizeMainPanelGeometry(window);
            return;
        }
    }

    private void CaptureAndSynchronizeMainPanelGeometry(RetailWindowHandle source)
    {
        _mainPanelGeometry = new PanelGeometry(
            source.Left,
            source.Top,
            source.Width,
            source.Height);
        SynchronizeMainPanelSiblings(source);
    }

    private void CaptureMainPanelGeometry(
        PanelEntry entry,
        bool synchronizeSiblings)
    {
        if (!entry.SharesMainPanelGeometry || entry.Window is not { } window)
            return;

        _mainPanelGeometry = new PanelGeometry(
            window.Left,
            window.Top,
            window.Width,
            window.Height);
        if (synchronizeSiblings)
            SynchronizeMainPanelSiblings(window);
    }

    private void PrepareMainPanelGeometry(PanelEntry entry)
    {
        if (!entry.SharesMainPanelGeometry || entry.Window is not { } window)
            return;

        if (_mainPanelGeometry is not { } geometry)
        {
            _mainPanelGeometry = new PanelGeometry(
                window.Left,
                window.Top,
                window.Width,
                window.Height);
            SynchronizeMainPanelSiblings(window);
            return;
        }

        ApplyWindowGeometry(entry, geometry);
    }

    private void SynchronizeMainPanelSiblings(RetailWindowHandle source)
    {
        if (_mainPanelGeometry is not { } geometry) return;

        _synchronizingGeometry = true;
        try
        {
            foreach (PanelEntry sibling in _byPanel.Values)
            {
                if (!sibling.SharesMainPanelGeometry
                    || sibling.Window is not { } window
                    || ReferenceEquals(window, source))
                    continue;
                ApplyWindowGeometry(sibling, geometry);
            }
        }
        finally
        {
            _synchronizingGeometry = false;
        }
    }

    private static void ApplyWindowGeometry(PanelEntry entry, PanelGeometry geometry)
    {
        if (entry.Window is not { } window) return;

        if (window.Left != geometry.Left || window.Top != geometry.Top)
            window.MoveTo(geometry.Left, geometry.Top);
        if (window.Width != geometry.Width || window.Height != geometry.Height)
            window.ResizeTo(geometry.Width, geometry.Height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (PanelEntry entry in _byPanel.Values)
            if (entry.Window is { } window)
            {
                window.Moved -= OnWindowMoved;
                window.Resized -= OnWindowResized;
            }
    }

    private readonly record struct PanelGeometry(
        float Left,
        float Top,
        float Width,
        float Height);

    private readonly record struct PanelEntry(
        string WindowName,
        bool RestorePrevious,
        RetailWindowHandle? Window,
        bool SharesMainPanelGeometry);
}
