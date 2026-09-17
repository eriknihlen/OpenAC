namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A key a plugin can bind a hotkey to. Names mirror the graphical host's
/// own keyboard-input enum so a chord reads the same way to a plugin author
/// as it does in the client's own keybinds file, without this assembly
/// depending on that input layer.
/// </summary>
public enum PluginKey
{
    /// <summary>No key; also the key a chord carries when none was set.</summary>
    Unknown = 0,

    /// <summary>The A key.</summary>
    A,
    /// <summary>The B key.</summary>
    B,
    /// <summary>The C key.</summary>
    C,
    /// <summary>The D key.</summary>
    D,
    /// <summary>The E key.</summary>
    E,
    /// <summary>The F key.</summary>
    F,
    /// <summary>The G key.</summary>
    G,
    /// <summary>The H key.</summary>
    H,
    /// <summary>The I key.</summary>
    I,
    /// <summary>The J key.</summary>
    J,
    /// <summary>The K key.</summary>
    K,
    /// <summary>The L key.</summary>
    L,
    /// <summary>The M key.</summary>
    M,
    /// <summary>The N key.</summary>
    N,
    /// <summary>The O key.</summary>
    O,
    /// <summary>The P key.</summary>
    P,
    /// <summary>The Q key.</summary>
    Q,
    /// <summary>The R key.</summary>
    R,
    /// <summary>The S key.</summary>
    S,
    /// <summary>The T key.</summary>
    T,
    /// <summary>The U key.</summary>
    U,
    /// <summary>The V key.</summary>
    V,
    /// <summary>The W key.</summary>
    W,
    /// <summary>The X key.</summary>
    X,
    /// <summary>The Y key.</summary>
    Y,
    /// <summary>The Z key.</summary>
    Z,

    /// <summary>The 0 key on the number row.</summary>
    Number0,
    /// <summary>The 1 key on the number row.</summary>
    Number1,
    /// <summary>The 2 key on the number row.</summary>
    Number2,
    /// <summary>The 3 key on the number row.</summary>
    Number3,
    /// <summary>The 4 key on the number row.</summary>
    Number4,
    /// <summary>The 5 key on the number row.</summary>
    Number5,
    /// <summary>The 6 key on the number row.</summary>
    Number6,
    /// <summary>The 7 key on the number row.</summary>
    Number7,
    /// <summary>The 8 key on the number row.</summary>
    Number8,
    /// <summary>The 9 key on the number row.</summary>
    Number9,

    /// <summary>The F1 function key.</summary>
    F1,
    /// <summary>The F2 function key.</summary>
    F2,
    /// <summary>The F3 function key.</summary>
    F3,
    /// <summary>The F4 function key.</summary>
    F4,
    /// <summary>The F5 function key.</summary>
    F5,
    /// <summary>The F6 function key.</summary>
    F6,
    /// <summary>The F7 function key.</summary>
    F7,
    /// <summary>The F8 function key.</summary>
    F8,
    /// <summary>The F9 function key.</summary>
    F9,
    /// <summary>The F10 function key.</summary>
    F10,
    /// <summary>The F11 function key.</summary>
    F11,
    /// <summary>The F12 function key.</summary>
    F12,

    /// <summary>The space bar.</summary>
    Space,

    /// <summary>The main Enter/Return key.</summary>
    Enter,

    /// <summary>The Escape key.</summary>
    Escape,

    /// <summary>The Tab key.</summary>
    Tab,

    /// <summary>The Backspace key.</summary>
    Backspace,

    /// <summary>The Delete key.</summary>
    Delete,

    /// <summary>The Insert key.</summary>
    Insert,

    /// <summary>The Home key.</summary>
    Home,

    /// <summary>The End key.</summary>
    End,

    /// <summary>The Page Up key.</summary>
    PageUp,

    /// <summary>The Page Down key.</summary>
    PageDown,

    /// <summary>The up arrow key.</summary>
    Up,

    /// <summary>The down arrow key.</summary>
    Down,

    /// <summary>The left arrow key.</summary>
    Left,

    /// <summary>The right arrow key.</summary>
    Right,

    /// <summary>The minus/hyphen key on the number row.</summary>
    Minus,
    /// <summary>The equals key on the number row.</summary>
    Equal,
    /// <summary>The left square bracket key.</summary>
    LeftBracket,
    /// <summary>The right square bracket key.</summary>
    RightBracket,
    /// <summary>The backslash key.</summary>
    BackSlash,
    /// <summary>The semicolon key.</summary>
    Semicolon,
    /// <summary>The apostrophe/quote key.</summary>
    Apostrophe,
    /// <summary>The comma key.</summary>
    Comma,
    /// <summary>The period key.</summary>
    Period,
    /// <summary>The forward slash key.</summary>
    Slash,

    /// <summary>The 0 key on the numeric keypad.</summary>
    Numpad0,
    /// <summary>The 1 key on the numeric keypad.</summary>
    Numpad1,
    /// <summary>The 2 key on the numeric keypad.</summary>
    Numpad2,
    /// <summary>The 3 key on the numeric keypad.</summary>
    Numpad3,
    /// <summary>The 4 key on the numeric keypad.</summary>
    Numpad4,
    /// <summary>The 5 key on the numeric keypad.</summary>
    Numpad5,
    /// <summary>The 6 key on the numeric keypad.</summary>
    Numpad6,
    /// <summary>The 7 key on the numeric keypad.</summary>
    Numpad7,
    /// <summary>The 8 key on the numeric keypad.</summary>
    Numpad8,
    /// <summary>The 9 key on the numeric keypad.</summary>
    Numpad9,
    /// <summary>The decimal point key on the numeric keypad.</summary>
    NumpadDecimal,
    /// <summary>The divide key on the numeric keypad.</summary>
    NumpadDivide,
    /// <summary>The multiply key on the numeric keypad.</summary>
    NumpadMultiply,
    /// <summary>The minus key on the numeric keypad.</summary>
    NumpadSubtract,
    /// <summary>The plus key on the numeric keypad.</summary>
    NumpadAdd,
    /// <summary>The Enter key on the numeric keypad.</summary>
    NumpadEnter,
    /// <summary>The backtick/tilde key left of the number row.</summary>
    Grave,
    /// <summary>The Print Screen key.</summary>
    PrintScreen,
    /// <summary>The Pause/Break key.</summary>
    Pause,
}

/// <summary>
/// A key plus the modifiers required to trigger it. Ctrl/Alt/Shift are
/// side-independent: either physical key of a held pair satisfies the
/// chord.
/// </summary>
/// <param name="Key">The key that triggers the hotkey.</param>
/// <param name="Ctrl">Whether Ctrl must be held.</param>
/// <param name="Alt">Whether Alt must be held.</param>
/// <param name="Shift">Whether Shift must be held.</param>
public readonly record struct PluginKeyChord(
    PluginKey Key,
    bool Ctrl = false,
    bool Alt = false,
    bool Shift = false);

/// <summary>A live hotkey registration returned by <see cref="IHotkeyRegistry.Register"/>.</summary>
public interface IPluginHotkeyRegistration : IDisposable
{
    /// <summary>
    /// False when the requested chord collided with an already-bound client
    /// action or another plugin's hotkey and the registration was refused;
    /// the handler will never fire.
    /// </summary>
    bool IsBound { get; }

    /// <summary>
    /// The chord actually bound: the caller's default unless a stored user
    /// override replaced it at registration time.
    /// </summary>
    PluginKeyChord EffectiveChord { get; }

    /// <summary>
    /// Rebinds this registration to a new chord, persisting it as a user
    /// override under this hotkey's scoped id so it survives across
    /// sessions. Re-resolves immediately if the host's keyboard/dispatcher
    /// are already up; a host with nothing to bind to (headless) treats
    /// this as a no-op and IsBound stays false.
    /// </summary>
    void Rebind(PluginKeyChord chord);
}

/// <summary>A registration handle from a host with nothing to bind (headless).</summary>
public sealed class NoOpHotkeyRegistration : IPluginHotkeyRegistration
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpHotkeyRegistration Instance { get; } = new();

    private NoOpHotkeyRegistration()
    {
    }

    /// <inheritdoc/>
    public bool IsBound => false;

    /// <inheritdoc/>
    public PluginKeyChord EffectiveChord => default;

    /// <inheritdoc/>
    public void Rebind(PluginKeyChord chord) { }

    /// <summary>Does nothing; there is no binding to revoke.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// Binds plugin-owned keyboard shortcuts. A host with no keyboard hands out
/// the inert registry, where a registration is accepted but never fires.
/// </summary>
public interface IHotkeyRegistry
{
    /// <summary>
    /// Registers a plugin-owned hotkey. <paramref name="id"/> identifies the
    /// binding for persistence (scoped by plugin, so two plugins may each
    /// use the same id); <paramref name="displayName"/> is shown in the
    /// rebind UI. A stored user override for this id replaces
    /// <paramref name="defaultChord"/> when present. The handler does not
    /// fire while the chat bar has keyboard focus unless the chord includes
    /// Ctrl or Alt. Disposing the result revokes the binding; the host also
    /// revokes every hotkey a plugin registered when that plugin unloads.
    /// </summary>
    IPluginHotkeyRegistration Register(
        string id,
        string displayName,
        PluginKeyChord defaultChord,
        Action handler) =>
        NoOpHotkeyRegistration.Instance;
}

/// <summary>
/// The hotkey registry a host with no keyboard hands out: it checks the
/// arguments and returns an unbound registration.
/// </summary>
public sealed class NoOpHotkeyRegistry : IHotkeyRegistry
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpHotkeyRegistry Instance { get; } = new();

    private NoOpHotkeyRegistry()
    {
    }

    /// <inheritdoc/>
    public IPluginHotkeyRegistration Register(
        string id,
        string displayName,
        PluginKeyChord defaultChord,
        Action handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(handler);
        return NoOpHotkeyRegistration.Instance;
    }
}
