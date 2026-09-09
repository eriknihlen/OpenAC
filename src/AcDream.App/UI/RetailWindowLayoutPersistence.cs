using System;
using System.Collections.Generic;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.UI;

public sealed class RetailWindowLayoutPersistence : IDisposable
{
    private readonly RetailWindowManager _manager;
    private readonly SettingsStore? _store;
    private readonly Func<string> _characterKey;
    private readonly Func<(int Width, int Height)> _screenSize;
    private readonly HashSet<string> _stateManagedVisibilityWindows;
    private readonly List<RetailWindowHandle> _attached = new();
    private readonly Dictionary<string, UiWindowLayout> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiWindowPlacement> _placements = new(StringComparer.Ordinal);
    private string? _restoredCharacter;
    private bool _restoring;
    private bool _disposed;
    private bool _gameplayActive = true;

    public void SetGameplayActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gameplayActive = active;
    }

    public RetailWindowLayoutPersistence(
        RetailWindowManager manager,
        SettingsStore? store,
        Func<string> characterKey,
        Func<(int Width, int Height)> screenSize,
        IEnumerable<string>? stateManagedVisibilityWindows = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _store = store;
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

    public void ResetToDefaults()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var screen = ValidScreenSize();
        _restoring = true;
        try
        {
            foreach (var handle in _attached)
            {
                var layout = WindowPlacementGeometry.Default(handle.Name, _defaults[handle.Name],
                    screen.Width, screen.Height, _defaults);
                var placement = new UiWindowPlacement(layout, screen.Width, screen.Height);
                _placements[handle.Name] = placement;
                ApplyPlacement(handle, placement, screen, restoreVisibility: false);
            }
        }
        finally { _restoring = false; }
    }

    public void ReflowToScreen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_gameplayActive) return;
        var screen = ValidScreenSize();
        _restoring = true;
        try
        {
            foreach (var handle in _attached)
            {
                var placement = WithCurrentState(handle, _placements[handle.Name]);
                ApplyPlacement(handle, placement, screen, restoreVisibility: false);
            }
        }
        finally { _restoring = false; }
    }

    private void RestoreAllCore(
        bool saveBack,
        bool restoreVisibility)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_gameplayActive) return;
        string character = _characterKey();
        if (!CanPersist(character)) return;
        var screen = ValidScreenSize();
        _restoredCharacter = character;

        _restoring = true;
        try
        {
            foreach (RetailWindowHandle handle in _attached.ToArray())
            {
                RestoreHandle(handle, character, screen, saveBack, restoreVisibility);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void RestoreHandle(RetailWindowHandle handle, string character,
        (int Width, int Height) screen, bool saveBack, bool restoreVisibility)
    {
        UiWindowLayout fallback = WindowPlacementGeometry.Default(handle.Name, _defaults[handle.Name],
            screen.Width, screen.Height, _defaults);
        var placement = _store?.LoadWindowPlacement(character, ResolutionKey(screen), handle.Name, fallback)
            ?? new UiWindowPlacement(fallback, screen.Width, screen.Height);
        placement = placement with
        {
            Layout = MigrateAuthoredGeometry(placement.Layout, AuthoredGeometry(handle, fallback)),
        };
        _placements[handle.Name] = placement;
        ApplyPlacement(handle, placement, screen,
            restoreVisibility && !_stateManagedVisibilityWindows.Contains(handle.Name));
        placement = WithCurrentState(handle, placement);
        _placements[handle.Name] = placement;
        if (saveBack)
            _store?.SaveWindowPlacement(character, handle.Name, placement);
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
        if (!_gameplayActive) return;
        string character = _characterKey();
        if (!CanPersist(character)) return;
        foreach (RetailWindowHandle handle in _attached.ToArray())
            SavePlacement(character, handle);
    }

    public void SaveNamed(string profileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profileName);
        if (!_gameplayActive) return;
        foreach (RetailWindowHandle handle in _attached.ToArray())
            _store?.SaveNamedWindowLayout(profileName, handle.Name, Capture(handle));
    }

    public void RestoreNamed(string profileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profileName);
        if (!_gameplayActive) return;
        var screen = ValidScreenSize();

        _restoring = true;
        try
        {
            foreach (RetailWindowHandle handle in _attached.ToArray())
            {
                UiWindowLayout? saved = _store?.LoadNamedWindowLayout(
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
                _placements[handle.Name] = new UiWindowPlacement(Capture(handle), screen.Width, screen.Height);
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
        _defaults[handle.Name] = Capture(handle);
        var screen = ValidScreenSize();
        _placements[handle.Name] = new UiWindowPlacement(Capture(handle), screen.Width, screen.Height);
        handle.Moved += OnGeometryChanged;
        handle.Resized += OnGeometryChanged;
        handle.StateChanged += OnChanged;
        if (!_stateManagedVisibilityWindows.Contains(handle.Name))
        {
            handle.Shown += OnChanged;
            handle.Hidden += OnChanged;
        }
        if (_gameplayActive && _restoredCharacter is { } character && character == _characterKey())
        {
            _restoring = true;
            try { RestoreHandle(handle, character, screen, saveBack: true, restoreVisibility: true); }
            finally { _restoring = false; }
        }
    }

    private void Detach(RetailWindowHandle handle)
    {
        if (!_attached.Remove(handle))
            return;
        _defaults.Remove(handle.Name);
        _placements.Remove(handle.Name);
        handle.Moved -= OnGeometryChanged;
        handle.Resized -= OnGeometryChanged;
        handle.StateChanged -= OnChanged;
        if (!_stateManagedVisibilityWindows.Contains(handle.Name))
        {
            handle.Shown -= OnChanged;
            handle.Hidden -= OnChanged;
        }
    }

    private void OnGeometryChanged(RetailWindowHandle handle)
    {
        if (!_gameplayActive || _restoring || _disposed) return;
        var screen = ValidScreenSize();
        _placements[handle.Name] = new UiWindowPlacement(Capture(handle), screen.Width, screen.Height);
        OnChanged(handle);
    }

    private static UiWindowPlacement WithCurrentState(RetailWindowHandle handle, UiWindowPlacement placement)
    {
        var current = Capture(handle);
        return placement with
        {
            Layout = placement.Layout with
            {
                Visible = current.Visible,
                Collapsed = current.Collapsed,
                Maximized = current.Maximized,
            },
        };
    }

    private void SavePlacement(string character, RetailWindowHandle handle)
    {
        var placement = WithCurrentState(handle, _placements[handle.Name]);
        _placements[handle.Name] = placement;
        _store?.SaveWindowPlacement(character, handle.Name, placement);
    }

    private void OnChanged(RetailWindowHandle handle)
    {
        if (!_gameplayActive || _restoring || _disposed) return;
        string character = _characterKey();
        if (!CanPersist(character)) return;

        try
        {
            SavePlacement(character, handle);
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

    private static void ApplyPlacement(RetailWindowHandle handle, UiWindowPlacement placement,
        (int Width, int Height) screen, bool restoreVisibility)
    {
        var frame = handle.OuterFrame;
        float width = frame.ResizeX
            ? ClampDimension(placement.Layout.Width, frame.Width, frame.MinWidth, frame.MaxWidth, screen.Width)
            : frame.Width;
        float height = frame.ResizeY
            ? ClampDimension(placement.Layout.Height, frame.Height, frame.MinHeight, frame.MaxHeight, screen.Height)
            : frame.Height;
        var layout = WindowPlacementGeometry.Project(placement, screen.Width, screen.Height, width, height);
        Apply(handle, layout, screen, restoreVisibility);
    }

    private static void Apply(
        RetailWindowHandle handle,
        UiWindowLayout layout,
        (int Width, int Height) screen,
        bool restoreVisibility)
    {
        UiElement frame = handle.OuterFrame;
        float width = ClampDimension(layout.Width, frame.Width, frame.MinWidth, frame.MaxWidth, screen.Width);
        float height = ClampDimension(layout.Height, frame.Height, frame.MinHeight, frame.MaxHeight, screen.Height);
        bool constrainResize = frame.ConstrainResizeToParent;
        frame.ConstrainResizeToParent = false;
        try { handle.ResizeTo(width, height); }
        finally { frame.ConstrainResizeToParent = constrainResize; }

        float maxX = MathF.Max(0f, screen.Width - handle.Width);
        float maxY = MathF.Max(0f, screen.Height - handle.Height);
        float x = Math.Clamp(FiniteOr(layout.X, handle.Left), 0f, maxX);
        float y = Math.Clamp(FiniteOr(layout.Y, handle.Top), 0f, maxY);
        handle.MoveTo(x, y);

        handle.StateController?.RestoreWindowState(new RetainedWindowState(
            Collapsed: layout.Collapsed,
            Maximized: layout.Maximized,
            PersistedTop: y,
            PersistedHeight: handle.Height,
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
