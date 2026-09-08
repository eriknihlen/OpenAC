using AcDream.App.Platform;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.UI.Layout;

public sealed class RetailKeyNames
{
    public const uint KeyNameTableId = 0x2300000Au;

    public const uint MetaKeyNameTableId = 0x2300000Bu;

    public const uint DelimiterTableId = 0x23000007u;

    private readonly Func<uint, uint, string?> _resolveString;
    private readonly Func<byte, bool, string?>? _osKeyName;
    private readonly string _delimiter;

    public RetailKeyNames(
        Func<uint, uint, string?> resolveString,
        Func<byte, bool, string?>? osKeyName = null)
    {
        _resolveString = resolveString
            ?? throw new ArgumentNullException(nameof(resolveString));
        _osKeyName = osKeyName ?? PlatformKeyNameProvider.ForCurrentProcess();
        _delimiter = resolveString(
                DelimiterTableId, DatStringResolver.ComputeHash("ID_KeyDescDelimiter"))
            ?? "+";
    }

    public string Describe(KeyChord chord)
    {
        if (chord == default)
            return string.Empty;
        if (TryGetMouseSemantic(chord, out string? mouseSemantic, out int buttonNumber))
        {
            string mouseName = _resolveString(
                    KeyNameTableId,
                    DatStringResolver.ComputeHash(mouseSemantic!))
                ?? $"Mouse Button {buttonNumber}";
            return Compose(chord, mouseName);
        }
        if (!TryGetDik(chord.Key, out byte dik, out string? dikName))
            return FallbackSpelling(chord);

        return Compose(chord, LookupName(dikName!, dik, KeyNameTableId));
    }

    private string Compose(KeyChord chord, string keyName)
    {
        var composed = new System.Text.StringBuilder();
        foreach ((ModifierMask flag, Key metaKey) in MetaOrder)
        {
            if ((chord.Modifiers & flag) == 0 || IsSelfModifier(chord.Key, flag))
                continue;
            if (!TryGetDik(metaKey, out byte metaDik, out string? metaDikName))
                continue;
            composed.Append(LookupName(metaDikName!, metaDik, MetaKeyNameTableId));
            composed.Append(_delimiter);
        }

        composed.Append(keyName);
        return composed.ToString();
    }

    private static bool TryGetMouseSemantic(
        KeyChord chord,
        out string? semantic,
        out int buttonNumber)
    {
        int zeroBased = (int)chord.Key switch
        {
            -1001 => 0,
            -1002 => 1,
            -1003 => 2,
            -1004 => 3,
            -1005 => 4,
            _ => -1,
        };
        if (chord.Device != 1 || zeroBased < 0)
        {
            semantic = null;
            buttonNumber = 0;
            return false;
        }

        semantic = $"DIMOFS_BUTTON{zeroBased}";
        buttonNumber = zeroBased + 1;
        return true;
    }

    private string LookupName(string dikName, byte dik, uint tableId)
        => _resolveString(tableId, DatStringResolver.ComputeHash(dikName))
            ?? _osKeyName?.Invoke((byte)(dik & 0x7F), (dik & 0x80) != 0)
            ?? dikName["DIK_".Length..];

    private static string FallbackSpelling(KeyChord chord)
    {
        string mods = chord.Modifiers == ModifierMask.None
            ? ""
            : chord.Modifiers.ToString() + "+";
        return mods + chord.Key;
    }

    private static readonly (ModifierMask Flag, Key MetaKey)[] MetaOrder =
    {
        (ModifierMask.Shift, Key.ShiftLeft),
        (ModifierMask.Ctrl, Key.ControlLeft),
        (ModifierMask.Alt, Key.AltLeft),
        (ModifierMask.Win, Key.SuperLeft),
    };

    private static bool IsSelfModifier(Key key, ModifierMask flag)
        => flag switch
        {
            ModifierMask.Shift => key is Key.ShiftLeft or Key.ShiftRight,
            ModifierMask.Ctrl => key is Key.ControlLeft or Key.ControlRight,
            ModifierMask.Alt => key is Key.AltLeft or Key.AltRight,
            ModifierMask.Win => key is Key.SuperLeft or Key.SuperRight,
            _ => false,
        };

    private static bool TryGetDik(Key key, out byte dik, out string? name)
    {
        (dik, name) = key switch
        {
            Key.Escape => ((byte)0x01, "DIK_ESCAPE"),
            Key.Number1 => ((byte)0x02, "DIK_1"),
            Key.Number2 => ((byte)0x03, "DIK_2"),
            Key.Number3 => ((byte)0x04, "DIK_3"),
            Key.Number4 => ((byte)0x05, "DIK_4"),
            Key.Number5 => ((byte)0x06, "DIK_5"),
            Key.Number6 => ((byte)0x07, "DIK_6"),
            Key.Number7 => ((byte)0x08, "DIK_7"),
            Key.Number8 => ((byte)0x09, "DIK_8"),
            Key.Number9 => ((byte)0x0A, "DIK_9"),
            Key.Number0 => ((byte)0x0B, "DIK_0"),
            Key.Minus => ((byte)0x0C, "DIK_MINUS"),
            Key.Equal => ((byte)0x0D, "DIK_EQUALS"),
            Key.Backspace => ((byte)0x0E, "DIK_BACK"),
            Key.Tab => ((byte)0x0F, "DIK_TAB"),
            Key.Q => ((byte)0x10, "DIK_Q"),
            Key.W => ((byte)0x11, "DIK_W"),
            Key.E => ((byte)0x12, "DIK_E"),
            Key.R => ((byte)0x13, "DIK_R"),
            Key.T => ((byte)0x14, "DIK_T"),
            Key.Y => ((byte)0x15, "DIK_Y"),
            Key.U => ((byte)0x16, "DIK_U"),
            Key.I => ((byte)0x17, "DIK_I"),
            Key.O => ((byte)0x18, "DIK_O"),
            Key.P => ((byte)0x19, "DIK_P"),
            Key.LeftBracket => ((byte)0x1A, "DIK_LBRACKET"),
            Key.RightBracket => ((byte)0x1B, "DIK_RBRACKET"),
            Key.Enter => ((byte)0x1C, "DIK_RETURN"),
            Key.ControlLeft => ((byte)0x1D, "DIK_LCONTROL"),
            Key.A => ((byte)0x1E, "DIK_A"),
            Key.S => ((byte)0x1F, "DIK_S"),
            Key.D => ((byte)0x20, "DIK_D"),
            Key.F => ((byte)0x21, "DIK_F"),
            Key.G => ((byte)0x22, "DIK_G"),
            Key.H => ((byte)0x23, "DIK_H"),
            Key.J => ((byte)0x24, "DIK_J"),
            Key.K => ((byte)0x25, "DIK_K"),
            Key.L => ((byte)0x26, "DIK_L"),
            Key.Semicolon => ((byte)0x27, "DIK_SEMICOLON"),
            Key.Apostrophe => ((byte)0x28, "DIK_APOSTROPHE"),
            Key.GraveAccent => ((byte)0x29, "DIK_GRAVE"),
            Key.ShiftLeft => ((byte)0x2A, "DIK_LSHIFT"),
            Key.BackSlash => ((byte)0x2B, "DIK_BACKSLASH"),
            Key.Z => ((byte)0x2C, "DIK_Z"),
            Key.X => ((byte)0x2D, "DIK_X"),
            Key.C => ((byte)0x2E, "DIK_C"),
            Key.V => ((byte)0x2F, "DIK_V"),
            Key.B => ((byte)0x30, "DIK_B"),
            Key.N => ((byte)0x31, "DIK_N"),
            Key.M => ((byte)0x32, "DIK_M"),
            Key.Comma => ((byte)0x33, "DIK_COMMA"),
            Key.Period => ((byte)0x34, "DIK_PERIOD"),
            Key.Slash => ((byte)0x35, "DIK_SLASH"),
            Key.ShiftRight => ((byte)0x36, "DIK_RSHIFT"),
            Key.KeypadMultiply => ((byte)0x37, "DIK_MULTIPLY"),
            Key.AltLeft => ((byte)0x38, "DIK_LMENU"),
            Key.Space => ((byte)0x39, "DIK_SPACE"),
            Key.CapsLock => ((byte)0x3A, "DIK_CAPITAL"),
            Key.F1 => ((byte)0x3B, "DIK_F1"),
            Key.F2 => ((byte)0x3C, "DIK_F2"),
            Key.F3 => ((byte)0x3D, "DIK_F3"),
            Key.F4 => ((byte)0x3E, "DIK_F4"),
            Key.F5 => ((byte)0x3F, "DIK_F5"),
            Key.F6 => ((byte)0x40, "DIK_F6"),
            Key.F7 => ((byte)0x41, "DIK_F7"),
            Key.F8 => ((byte)0x42, "DIK_F8"),
            Key.F9 => ((byte)0x43, "DIK_F9"),
            Key.F10 => ((byte)0x44, "DIK_F10"),
            Key.NumLock => ((byte)0x45, "DIK_NUMLOCK"),
            Key.ScrollLock => ((byte)0x46, "DIK_SCROLL"),
            Key.Keypad7 => ((byte)0x47, "DIK_NUMPAD7"),
            Key.Keypad8 => ((byte)0x48, "DIK_NUMPAD8"),
            Key.Keypad9 => ((byte)0x49, "DIK_NUMPAD9"),
            Key.KeypadSubtract => ((byte)0x4A, "DIK_SUBTRACT"),
            Key.Keypad4 => ((byte)0x4B, "DIK_NUMPAD4"),
            Key.Keypad5 => ((byte)0x4C, "DIK_NUMPAD5"),
            Key.Keypad6 => ((byte)0x4D, "DIK_NUMPAD6"),
            Key.KeypadAdd => ((byte)0x4E, "DIK_ADD"),
            Key.Keypad1 => ((byte)0x4F, "DIK_NUMPAD1"),
            Key.Keypad2 => ((byte)0x50, "DIK_NUMPAD2"),
            Key.Keypad3 => ((byte)0x51, "DIK_NUMPAD3"),
            Key.Keypad0 => ((byte)0x52, "DIK_NUMPAD0"),
            Key.KeypadDecimal => ((byte)0x53, "DIK_DECIMAL"),
            Key.F11 => ((byte)0x57, "DIK_F11"),
            Key.F12 => ((byte)0x58, "DIK_F12"),
            Key.F13 => ((byte)0x64, "DIK_F13"),
            Key.F14 => ((byte)0x65, "DIK_F14"),
            Key.F15 => ((byte)0x66, "DIK_F15"),
            Key.KeypadEnter => ((byte)0x9C, "DIK_NUMPADENTER"),
            Key.ControlRight => ((byte)0x9D, "DIK_RCONTROL"),
            Key.KeypadDivide => ((byte)0xB5, "DIK_DIVIDE"),
            Key.PrintScreen => ((byte)0xB7, "DIK_SYSRQ"),
            Key.AltRight => ((byte)0xB8, "DIK_RMENU"),
            Key.Pause => ((byte)0xC5, "DIK_PAUSE"),
            Key.Home => ((byte)0xC7, "DIK_HOME"),
            Key.Up => ((byte)0xC8, "DIK_UP"),
            Key.PageUp => ((byte)0xC9, "DIK_PRIOR"),
            Key.Left => ((byte)0xCB, "DIK_LEFT"),
            Key.Right => ((byte)0xCD, "DIK_RIGHT"),
            Key.End => ((byte)0xCF, "DIK_END"),
            Key.Down => ((byte)0xD0, "DIK_DOWN"),
            Key.PageDown => ((byte)0xD1, "DIK_NEXT"),
            Key.Insert => ((byte)0xD2, "DIK_INSERT"),
            Key.Delete => ((byte)0xD3, "DIK_DELETE"),
            Key.SuperLeft => ((byte)0xDB, "DIK_LWIN"),
            Key.SuperRight => ((byte)0xDC, "DIK_RWIN"),
            Key.Menu => ((byte)0xDD, "DIK_APPS"),
            _ => ((byte)0, null),
        };
        return name is not null;
    }
}
