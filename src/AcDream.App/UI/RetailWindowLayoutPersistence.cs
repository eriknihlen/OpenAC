using System;
using System.Collections.Generic;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.UI;

public sealed class RetailWindowLayoutPersistence : IDisposable
{
    private readonly RetailWindowManager _manager;
    private readonly SettingsStore _store;
    private readonly Func<string> _characterKey;
    private readonly Func<(int Width, int Height)> _screenSize;
    private readonly HashSet<string> _stateManagedVisibilityWindows;
    private readonly List<RetailWindowHandle> _attached = new();
    private bool _restoring;
    private bool _disposed;

    public RetailWindowLayoutPersistence(
        RetailWindowManager manager,
        SettingsStore store,
        Func<string> characterKey,
        Func<(int Width, int Height)> screenSize,
        IEnumerable<string>? stateManagedVisibilityWindows = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _characterKey = characterKey ?? throw new ArgumentNullException(nameof(characterKey));
        _screenSize = screenSize ?? throw new ArgumentNullException(nameof(screenSize));
        _stateManagedVisibilityWindows = stateManagedVisibilityWindows is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(stateManagedVisibilityWindows, StringComparer.Ordinal);

        _manager.WindowRegistered += OnWindowRegistered;
        _manager.WindowUnregistered += OnWindowUnregistered;
        foreach (RetailWindowHandle handle in manager.Windows)
            Attach(handle);
    }

    private void OnWindowRegistered(RetailWindowHandle handle) => Attach(handle);

    private void OnWindowUnregistered(RetailWindowHandle handle) => Detach(handle);

    public void RestoreAll(bool saveBack = true)
        => RestoreAllCore(
            saveBack,
            restoreVisibility: true);

    public void RestoreAfterDisplayChange()
        => RestoreAllCore(
            saveBack: false,
            restoreVisibility: false);

    private void RestoreAllCore(
        bool saveBack,
        bool restoreVisibility)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string character = _characterKey();
        if (!CanPersist(character)) return;
        var screen = ValidScreenSize();
        string resolution = ResolutionKey(screen);

        _restoring = true;
        try
        {
            foreach (RetailWindowHandle handle in _attached.ToArray())
            {
                UiWindowLayout fallback = Capture(handle);
                UiWindowLayout? saved = _store.LoadWindowLayout(
                    character, resolution, handle.Name, fallback);
                if (saved is not { } layout) continue;
                layout = MigrateAuthoredGeometry(
                    layout,
                    AuthoredGeometry(handle, fallback));

                Apply(
                    handle,
                    layout,
                    screen,
                    restoreVisibility:
                        restoreVisibility
                        && !_stateManagedVisibilityWindows.Contains(handle.Name));
                if (saveBack)
                    _store.SaveWindowLayout(character, resolution, handle.Name, Capture(handle));
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    public void ClampAllToScreen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var screen = ValidScreenSize();
        _restoring = true;
        try
        {
            foreach (RetailWindowHandle handle in _attached.ToArray())
            {
                float maxX = MathF.Max(0f, screen.Width - handle.Width);
                float maxY = MathF.Max(0f, screen.Height - handle.Height);
                float x = Math.Clamp(handle.Left, 0f, maxX);
                float y = Math.Clamp(handle.Top, 0f, maxY);
                if (x != handle.Left || y != handle.Top)
                    handle.MoveTo(x, y);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    public void SaveAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string character = _characterKey();
        if (!CanPersist(character)) return;
        var screen = ValidScreenSize();
        string resolution = ResolutionKey(screen);
        foreach (RetailWindowHandle handle in _attached.ToArray())
            _store.SaveWindowLayout(character, resolution, handle.Name, Capture(handle));
    }

    public void SaveNamed(string profileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profileName);
        foreach (RetailWindowHandle handle in _attached.ToArray())
            _store.SaveNamedWindowLayout(profileName, handle.Name, Capture(handle));
    }

    public void RestoreNamed(string profileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profileName);
        var screen = ValidScreenSize();

        _restoring = true;
        try
        {
            foreach (RetailWindowHandle handle in _attached.ToArray())
            {
                UiWindowLayout? saved = _store.LoadNamedWindowLayout(
                    profileName, handle.Name, Capture(handle));
                if (saved is not { } layout) continue;
                UiWindowLayout fallback = Capture(handle);
                layout = MigrateAuthoredGeometry(
                    layout,
                    AuthoredGeometry(handle, fallback));
                Apply(
                    handle,
                    layout,
                    screen,
                    restoreVisibility: !_stateManagedVisibilityWindows.Contains(handle.Name));
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void Attach(RetailWindowHandle handle)
    {
        if (_attached.Contains(handle))
            return;
        _attached.Add(handle);
        handle.Moved += OnChanged;
        handle.Resized += OnChanged;
        handle.StateChanged += OnChanged;
        if (!_stateManagedVisibilityWindows.Contains(handle.Name))
        {
            handle.Shown += OnChanged;
            handle.Hidden += OnChanged;
        }
    }

    private void Detach(RetailWindowHandle handle)
    {
        if (!_attached.Remove(handle))
            return;
        handle.Moved -= OnChanged;
        handle.Resized -= OnChanged;
        handle.StateChanged -= OnChanged;
        if (!_stateManagedVisibilityWindows.Contains(handle.Name))
        {
            handle.Shown -= OnChanged;
            handle.Hidden -= OnChanged;
        }
    }

    private void OnChanged(RetailWindowHandle handle)
    {
        if (_restoring || _disposed) return;
        string character = _characterKey();
        if (!CanPersist(character)) return;

        try
        {
            var screen = ValidScreenSize();
            _store.SaveWindowLayout(
                character,
                ResolutionKey(screen),
                handle.Name,
                Capture(handle));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"settings: window layout save failed [{handle.Name}]: {ex.Message}");
        }
    }

    private static UiWindowLayout Capture(RetailWindowHandle handle)
    {
        RetainedWindowState state = handle.StateController?.CaptureWindowState() ?? default;
        return new UiWindowLayout(
            handle.Left,
            state.PersistedTop ?? handle.Top,
            handle.Width,
            state.PersistedHeight ?? handle.Height,
            state.RequestedVisible ?? handle.IsVisible,
            state.Collapsed,
            state.Maximized,
            handle.AuthoredGeometryRevision);
    }

    private static UiWindowLayout MigrateAuthoredGeometry(
        UiWindowLayout saved,
        UiWindowLayout authored)
    {
        if (saved.AuthoredGeometryRevision == authored.AuthoredGeometryRevision)
            return saved;

        return saved with
        {
            Width = authored.Width,
            Height = authored.Height,
            AuthoredGeometryRevision = authored.AuthoredGeometryRevision,
        };
    }

    private static UiWindowLayout AuthoredGeometry(
        RetailWindowHandle handle,
        UiWindowLayout current) => current with
    {
        Width = handle.AuthoredWidth,
        Height = handle.AuthoredHeight,
        AuthoredGeometryRevision = handle.AuthoredGeometryRevision,
    };

    private static void Apply(
        RetailWindowHandle handle,
        UiWindowLayout layout,
        (int Width, int Height) screen,
        bool restoreVisibility)
    {
        UiElement frame = handle.OuterFrame;
        float width = ClampDimension(layout.Width, frame.Width, frame.MinWidth, frame.MaxWidth, screen.Width);
        float height = ClampDimension(layout.Height, frame.Height, frame.MinHeight, frame.MaxHeight, screen.Height);
        handle.ResizeTo(width, height);

        float maxX = MathF.Max(0f, screen.Width - handle.Width);
        float maxY = MathF.Max(0f, screen.Height - handle.Height);
        float x = Math.Clamp(FiniteOr(layout.X, handle.Left), 0f, maxX);
        float y = Math.Clamp(FiniteOr(layout.Y, handle.Top), 0f, maxY);
        handle.MoveTo(x, y);

        handle.StateController?.RestoreWindowState(new RetainedWindowState(
            Collapsed: layout.Collapsed,
            Maximized: layout.Maximized,
            RequestedVisible: layout.Visible));

        if (restoreVisibility)
        {
            if (layout.Visible) handle.Show();
            else handle.Hide();
        }
    }

    private (int Width, int Height) ValidScreenSize()
    {
        var screen = _screenSize();
        return (Math.Max(1, screen.Width), Math.Max(1, screen.Height));
    }

    private static float ClampDimension(
        float saved,
        float current,
        float minimum,
        float maximum,
        int screenExtent)
    {
        float value = FiniteOr(saved, current);
        float upper = MathF.Max(minimum, MathF.Min(maximum, screenExtent));
        return Math.Clamp(value, minimum, upper);
    }

    private static float FiniteOr(float value, float fallback)
        => float.IsFinite(value) ? value : fallback;

    private static string ResolutionKey((int Width, int Height) screen)
        => $"{screen.Width}x{screen.Height}";

    private static bool CanPersist(string key)
        => !string.IsNullOrWhiteSpace(key)
           && !string.Equals(key, "default", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.WindowRegistered -= OnWindowRegistered;
        _manager.WindowUnregistered -= OnWindowUnregistered;
        foreach (RetailWindowHandle handle in _attached.ToArray())
            Detach(handle);
        _attached.Clear();
    }
}
