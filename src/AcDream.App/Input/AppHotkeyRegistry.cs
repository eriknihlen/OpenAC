using System.Linq;
using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Input;

// Plugin-owned keyboard hotkeys for the graphical host. Binds lazily: a
// plugin may register before the keyboard source and client key bindings
// exist yet (plugin loading and input-dispatcher composition are not
// strictly ordered), so every registration is replayed once Bind() runs.
// User overrides live in a plugin-scoped sibling file next to the client's
// own keybinds.json rather than inside it, so this feature cannot corrupt
// the client's own binding schema.
public sealed class AppHotkeyRegistry : IHotkeyRegistry
{
    private readonly object _gate = new();
    private readonly string? _overridesFilePath;
    private readonly Dictionary<string, PluginKeyChord> _overrides;
    private readonly List<Entry> _entries = [];
    private IKeyboardSource? _keyboard;
    private KeyBindings? _clientBindings;
    private InputDispatcher? _dispatcher;
    private bool _keyboardHooked;

    public AppHotkeyRegistry(string? overridesFilePath)
    {
        _overridesFilePath = overridesFilePath;
        _overrides = LoadOverrides(overridesFilePath);
    }

    /// <summary>
    /// Wires the registry to the live keyboard source and the client's own
    /// bindings (for collision detection) and the dispatcher (for the
    /// active-scope chat-focus check). Every hotkey registered before this
    /// call is resolved and armed now.
    /// </summary>
    public void Bind(
        IKeyboardSource keyboard,
        KeyBindings clientBindings,
        InputDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(clientBindings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        Entry[] pending;
        lock (_gate)
        {
            _keyboard = keyboard;
            _clientBindings = clientBindings;
            _dispatcher = dispatcher;
            if (!_keyboardHooked)
            {
                _keyboardHooked = true;
                keyboard.KeyDown += OnKeyDown;
            }
            pending = _entries.ToArray();
        }
        foreach (Entry entry in pending)
            Resolve(entry);
    }

    // A second Register call for an id that already has a live registration
    // replaces it -- the old entry is revoked (disposed) first so its chord
    // frees up before the new one resolves, exactly as if the caller had
    // disposed the old handle themselves.
    public IPluginHotkeyRegistration Register(
        string id,
        string displayName,
        PluginKeyChord defaultChord,
        Action handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(handler);

        var entry = new Entry(id, displayName, defaultChord, handler);
        bool alreadyBound;
        lock (_gate)
        {
            Entry? existing = _entries.Find(e => !e.Revoked && e.Id == id);
            if (existing is not null)
                existing.Revoked = true;
            _entries.RemoveAll(e => e.Revoked);
            _entries.Add(entry);
            alreadyBound = _keyboard is not null;
        }
        if (alreadyBound)
        {
            Resolve(entry);
            ReResolveUnboundEntries(entry);
        }
        return new Registration(this, entry);
    }

    private void Resolve(Entry entry)
    {
        KeyBindings? clientBindings;
        lock (_gate)
            clientBindings = _clientBindings;
        if (clientBindings is null)
            return;

        PluginKeyChord effective;
        lock (_gate)
        {
            effective = _overrides.TryGetValue(entry.Id, out PluginKeyChord stored)
                ? stored
                : entry.DefaultChord;
        }

        Key? silkKey = MapKey(effective.Key);
        bool bound;
        lock (_gate)
        {
            bound = silkKey is not null
                && !CollidesWithClient(clientBindings, effective, silkKey.Value)
                && !CollidesWithAnotherPlugin(entry, silkKey.Value, effective);
            entry.EffectiveChord = effective;
            entry.SilkKey = silkKey;
            entry.Bound = bound;
        }
    }

    // A plugin chord that matches another plugin's already-bound chord is
    // refused rather than silently stealing the earlier registration --
    // first-come, first-bound, the same rule CollidesWithClient applies
    // against the client's own bindings. Only entries currently marked
    // Bound count as live occupants of a chord; a revoked or already-
    // unbound entry does not block anything.
    private bool CollidesWithAnotherPlugin(
        Entry entry, Key silkKey, PluginKeyChord chord)
    {
        foreach (Entry other in _entries)
        {
            if (ReferenceEquals(other, entry) || other.Revoked || !other.Bound)
                continue;
            if (other.SilkKey != silkKey) continue;
            if (other.EffectiveChord.Ctrl != chord.Ctrl) continue;
            if (other.EffectiveChord.Alt != chord.Alt) continue;
            if (other.EffectiveChord.Shift != chord.Shift) continue;
            return true;
        }
        return false;
    }

    private static bool CollidesWithClient(
        KeyBindings clientBindings, PluginKeyChord chord, Key silkKey)
    {
        ModifierMask mods = ModifierMask.None;
        if (chord.Ctrl) mods |= ModifierMask.Ctrl;
        if (chord.Alt) mods |= ModifierMask.Alt;
        if (chord.Shift) mods |= ModifierMask.Shift;
        var keyChord = new KeyChord(silkKey, mods);
        return clientBindings.Find(keyChord, ActivationType.Press) is not null
            || clientBindings.Find(keyChord, ActivationType.Release) is not null
            || clientBindings.Find(keyChord, ActivationType.Hold) is not null;
    }


    // Not routed through InputDispatcher's action/scope machinery -- a
    // dynamic per-plugin action space large enough for that would be a much
    // bigger change (see the plugin-api.md Hotkeys note for the recorded
    // deviation). This still honours the two conditions that matter most:
    // a rebind capture in progress (BeginCapture) and a modal scope
    // (Dialog/EditField, not just Chat) both suppress every hotkey, the
    // same way the dispatcher itself would refuse to route a client
    // action into a text field or a capture-in-progress rebind screen.
    private void OnKeyDown(Key key, ModifierMask modifiers)
    {
        Entry[] snapshot;
        InputDispatcher? dispatcher;
        lock (_gate)
        {
            snapshot = _entries.ToArray();
            dispatcher = _dispatcher;
        }
        if (dispatcher is not null && dispatcher.IsCapturing)
            return;
        InputScope? activeScope = dispatcher?.ActiveScope;
        bool chatFocused = activeScope == InputScope.Chat;
        bool modalScope = activeScope is InputScope.Dialog or InputScope.EditField;
        if (modalScope)
            return;

        foreach (Entry entry in snapshot)
        {
            bool bound;
            Key? silkKey;
            PluginKeyChord chord;
            Action handler;
            lock (_gate)
            {
                if (entry.Revoked) continue;
                bound = entry.Bound;
                silkKey = entry.SilkKey;
                chord = entry.EffectiveChord;
                handler = entry.Handler;
            }
            if (!bound || silkKey != key) continue;

            bool modsMatch =
                ((modifiers & ModifierMask.Ctrl) != 0) == chord.Ctrl
                && ((modifiers & ModifierMask.Alt) != 0) == chord.Alt
                && ((modifiers & ModifierMask.Shift) != 0) == chord.Shift;
            if (!modsMatch) continue;

            if (chatFocused && !chord.Ctrl && !chord.Alt) continue;

            try { handler(); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }


    private void Revoke(Entry entry)
    {
        lock (_gate)
        {
            entry.Revoked = true;
            _entries.Remove(entry);
        }
        ReResolveUnboundEntries();
    }

    // Called after any chord-freeing mutation (a revoke, a same-id replace,
    // an override change) so a plugin previously refused for a collision
    // binds the moment the chord it wanted is free again -- otherwise it
    // would stay refused until its own next Register/Bind call, which may
    // never come.
    private void ReResolveUnboundEntries(Entry? justResolved = null)
    {
        Entry[] snapshot;
        lock (_gate)
        {
            if (_keyboard is null) return;
            snapshot = _entries
                .Where(e => !e.Revoked && !e.Bound && !ReferenceEquals(e, justResolved))
                .ToArray();
        }
        foreach (Entry e in snapshot)
            Resolve(e);
    }

    /// <summary>
    /// Stores a user override for a plugin-scoped hotkey id, persists it to
    /// disk, and re-resolves every live registration for it. Called by
    /// Registration.Rebind -- there is no in-client rebind UI yet, but the
    /// storage and re-resolve path is real.
    /// </summary>
    public void SetOverride(string scopedId, PluginKeyChord chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedId);
        Entry[] matching;
        Dictionary<string, PluginKeyChord> snapshot;
        lock (_gate)
        {
            _overrides[scopedId] = chord;
            matching = _entries.Where(e => e.Id == scopedId).ToArray();
            snapshot = new Dictionary<string, PluginKeyChord>(_overrides, StringComparer.Ordinal);
        }
        SaveOverrides(_overridesFilePath, snapshot);
        foreach (Entry entry in matching)
            Resolve(entry);
        ReResolveUnboundEntries();
    }

    private static Dictionary<string, PluginKeyChord> LoadOverrides(string? path)
    {
        var result = new Dictionary<string, PluginKeyChord>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return result;
        try
        {
            using FileStream stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<Dictionary<string, StoredChord>>(stream);
            if (raw is null) return result;
            foreach ((string id, StoredChord stored) in raw)
            {
                if (Enum.TryParse(stored.Key, out PluginKey key))
                {
                    result[id] = new PluginKeyChord(
                        key, stored.Ctrl, stored.Alt, stored.Shift);
                }
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private static void SaveOverrides(
        string? path, Dictionary<string, PluginKeyChord> overrides)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var raw = new Dictionary<string, StoredChord>(StringComparer.Ordinal);
            foreach ((string id, PluginKeyChord chord) in overrides)
            {
                raw[id] = new StoredChord(
                    chord.Key.ToString(), chord.Ctrl, chord.Alt, chord.Shift);
            }
            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, raw, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private readonly record struct StoredChord(string Key, bool Ctrl, bool Alt, bool Shift);

    private static Key? MapKey(PluginKey key) => key switch
    {
        PluginKey.A => Key.A, PluginKey.B => Key.B, PluginKey.C => Key.C,
        PluginKey.D => Key.D, PluginKey.E => Key.E, PluginKey.F => Key.F,
        PluginKey.G => Key.G, PluginKey.H => Key.H, PluginKey.I => Key.I,
        PluginKey.J => Key.J, PluginKey.K => Key.K, PluginKey.L => Key.L,
        PluginKey.M => Key.M, PluginKey.N => Key.N, PluginKey.O => Key.O,
        PluginKey.P => Key.P, PluginKey.Q => Key.Q, PluginKey.R => Key.R,
        PluginKey.S => Key.S, PluginKey.T => Key.T, PluginKey.U => Key.U,
        PluginKey.V => Key.V, PluginKey.W => Key.W, PluginKey.X => Key.X,
        PluginKey.Y => Key.Y, PluginKey.Z => Key.Z,
        PluginKey.Number0 => Key.Number0, PluginKey.Number1 => Key.Number1,
        PluginKey.Number2 => Key.Number2, PluginKey.Number3 => Key.Number3,
        PluginKey.Number4 => Key.Number4, PluginKey.Number5 => Key.Number5,
        PluginKey.Number6 => Key.Number6, PluginKey.Number7 => Key.Number7,
        PluginKey.Number8 => Key.Number8, PluginKey.Number9 => Key.Number9,
        PluginKey.F1 => Key.F1, PluginKey.F2 => Key.F2, PluginKey.F3 => Key.F3,
        PluginKey.F4 => Key.F4, PluginKey.F5 => Key.F5, PluginKey.F6 => Key.F6,
        PluginKey.F7 => Key.F7, PluginKey.F8 => Key.F8, PluginKey.F9 => Key.F9,
        PluginKey.F10 => Key.F10, PluginKey.F11 => Key.F11, PluginKey.F12 => Key.F12,
        PluginKey.Space => Key.Space,
        PluginKey.Enter => Key.Enter,
        PluginKey.Escape => Key.Escape,
        PluginKey.Tab => Key.Tab,
        PluginKey.Backspace => Key.Backspace,
        PluginKey.Delete => Key.Delete,
        PluginKey.Insert => Key.Insert,
        PluginKey.Home => Key.Home,
        PluginKey.End => Key.End,
        PluginKey.PageUp => Key.PageUp,
        PluginKey.PageDown => Key.PageDown,
        PluginKey.Up => Key.Up,
        PluginKey.Down => Key.Down,
        PluginKey.Left => Key.Left,
        PluginKey.Right => Key.Right,
        PluginKey.Minus => Key.Minus,
        PluginKey.Equal => Key.Equal,
        PluginKey.LeftBracket => Key.LeftBracket,
        PluginKey.RightBracket => Key.RightBracket,
        PluginKey.BackSlash => Key.BackSlash,
        PluginKey.Semicolon => Key.Semicolon,
        PluginKey.Apostrophe => Key.Apostrophe,
        PluginKey.Comma => Key.Comma,
        PluginKey.Period => Key.Period,
        PluginKey.Slash => Key.Slash,
        PluginKey.Numpad0 => Key.Keypad0, PluginKey.Numpad1 => Key.Keypad1,
        PluginKey.Numpad2 => Key.Keypad2, PluginKey.Numpad3 => Key.Keypad3,
        PluginKey.Numpad4 => Key.Keypad4, PluginKey.Numpad5 => Key.Keypad5,
        PluginKey.Numpad6 => Key.Keypad6, PluginKey.Numpad7 => Key.Keypad7,
        PluginKey.Numpad8 => Key.Keypad8, PluginKey.Numpad9 => Key.Keypad9,
        PluginKey.NumpadDecimal => Key.KeypadDecimal,
        PluginKey.NumpadDivide => Key.KeypadDivide,
        PluginKey.NumpadMultiply => Key.KeypadMultiply,
        PluginKey.NumpadSubtract => Key.KeypadSubtract,
        PluginKey.NumpadAdd => Key.KeypadAdd,
        PluginKey.NumpadEnter => Key.KeypadEnter,
        PluginKey.Grave => Key.GraveAccent,
        PluginKey.PrintScreen => Key.PrintScreen,
        PluginKey.Pause => Key.Pause,
        _ => null,
    };

    private sealed class Entry(
        string id,
        string displayName,
        PluginKeyChord defaultChord,
        Action handler)
    {
        internal string Id { get; } = id;
        internal string DisplayName { get; } = displayName;
        internal PluginKeyChord DefaultChord { get; } = defaultChord;
        internal Action Handler { get; } = handler;
        internal PluginKeyChord EffectiveChord { get; set; } = defaultChord;
        internal Key? SilkKey { get; set; }
        internal bool Bound { get; set; }
        internal bool Revoked { get; set; }
    }

    private sealed class Registration(AppHotkeyRegistry owner, Entry entry)
        : IPluginHotkeyRegistration
    {
        private bool _disposed;

        public bool IsBound
        {
            get { lock (owner._gate) return !entry.Revoked && entry.Bound; }
        }

        public PluginKeyChord EffectiveChord
        {
            get { lock (owner._gate) return entry.EffectiveChord; }
        }

        public void Rebind(PluginKeyChord chord)
        {
            if (_disposed) return;
            owner.SetOverride(entry.Id, chord);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Revoke(entry);
        }
    }
}
