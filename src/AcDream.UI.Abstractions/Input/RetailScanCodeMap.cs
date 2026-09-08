using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Input;

public static class RetailScanCodeMap
{
    public static ModifierMask ToModifierMask(uint retailModifier)
    {
        var mask = ModifierMask.None;
        if ((retailModifier & 0x80000000u) != 0) mask |= ModifierMask.Shift;
        if ((retailModifier & 0x40000000u) != 0) mask |= ModifierMask.Ctrl;
        if ((retailModifier & 0x20000000u) != 0) mask |= ModifierMask.Alt;
        return mask;
    }

    public static Key? ToSilkKey(uint scan, uint device)
    {
        if (device == 1)
        {
            return scan switch
            {
                0x0C => InputDispatcher.MouseButtonToKey(MouseButton.Left),
                0x0D => InputDispatcher.MouseButtonToKey(MouseButton.Right),
                0x0E => InputDispatcher.MouseButtonToKey(MouseButton.Middle),
                0x0F => InputDispatcher.MouseButtonToKey(MouseButton.Button4),
                _ => null,
            };
        }
        if (device != 0) return null;
        return scan switch
        {
            0x01 => Key.Escape,
            0x02 => Key.Number1,
            0x03 => Key.Number2,
            0x04 => Key.Number3,
            0x05 => Key.Number4,
            0x06 => Key.Number5,
            0x07 => Key.Number6,
            0x08 => Key.Number7,
            0x09 => Key.Number8,
            0x0A => Key.Number9,
            0x0B => Key.Number0,
            0x0C => Key.Minus,
            0x0D => Key.Equal,
            0x0E => Key.Backspace,
            0x0F => Key.Tab,
            0x10 => Key.Q,
            0x11 => Key.W,
            0x12 => Key.E,
            0x13 => Key.R,
            0x14 => Key.T,
            0x15 => Key.Y,
            0x16 => Key.U,
            0x17 => Key.I,
            0x18 => Key.O,
            0x19 => Key.P,
            0x1A => Key.LeftBracket,
            0x1B => Key.RightBracket,
            0x1C => Key.Enter,
            0x1D => Key.ControlLeft,
            0x1E => Key.A,
            0x1F => Key.S,
            0x20 => Key.D,
            0x21 => Key.F,
            0x22 => Key.G,
            0x23 => Key.H,
            0x24 => Key.J,
            0x25 => Key.K,
            0x26 => Key.L,
            0x27 => Key.Semicolon,
            0x28 => Key.Apostrophe,
            0x29 => Key.GraveAccent,
            0x2A => Key.ShiftLeft,
            0x2B => Key.BackSlash,
            0x2C => Key.Z,
            0x2D => Key.X,
            0x2E => Key.C,
            0x2F => Key.V,
            0x30 => Key.B,
            0x31 => Key.N,
            0x32 => Key.M,
            0x33 => Key.Comma,
            0x34 => Key.Period,
            0x35 => Key.Slash,
            0x36 => Key.ShiftRight,
            0x37 => Key.KeypadMultiply,
            0x38 => Key.AltLeft,
            0x39 => Key.Space,
            0x3A => Key.CapsLock,
            0x3B => Key.F1,
            0x3C => Key.F2,
            0x3D => Key.F3,
            0x3E => Key.F4,
            0x3F => Key.F5,
            0x40 => Key.F6,
            0x41 => Key.F7,
            0x42 => Key.F8,
            0x43 => Key.F9,
            0x44 => Key.F10,
            0x45 => Key.NumLock,
            0x46 => Key.ScrollLock,
            0x47 => Key.Keypad7,
            0x48 => Key.Keypad8,
            0x49 => Key.Keypad9,
            0x4A => Key.KeypadSubtract,
            0x4B => Key.Keypad4,
            0x4C => Key.Keypad5,
            0x4D => Key.Keypad6,
            0x4E => Key.KeypadAdd,
            0x4F => Key.Keypad1,
            0x50 => Key.Keypad2,
            0x51 => Key.Keypad3,
            0x52 => Key.Keypad0,
            0x53 => Key.KeypadDecimal,
            0x57 => Key.F11,
            0x58 => Key.F12,
            0x64 => Key.F13,
            0x65 => Key.F14,
            0x66 => Key.F15,
            0x9C => Key.KeypadEnter,
            0x9D => Key.ControlRight,
            0xB5 => Key.KeypadDivide,
            0xB7 => Key.PrintScreen,
            0xB8 => Key.AltRight,
            0xC5 => Key.Pause,
            0xC7 => Key.Home,
            0xC8 => Key.Up,
            0xC9 => Key.PageUp,
            0xCB => Key.Left,
            0xCD => Key.Right,
            0xCF => Key.End,
            0xD0 => Key.Down,
            0xD1 => Key.PageDown,
            0xD2 => Key.Insert,
            0xD3 => Key.Delete,
            0xDB => Key.SuperLeft,
            0xDC => Key.SuperRight,
            0xDD => Key.Menu,
            _ => null,
        };
    }

    public static bool TryFromFileControl(
        string control,
        out uint scan,
        out uint device)
    {
        scan = 0u;
        device = 0u;
        if (string.IsNullOrWhiteSpace(control))
            return false;

        string token = control.Trim().ToUpperInvariant();
        if (token.StartsWith("DIMOFS_BUTTON", StringComparison.Ordinal)
            && int.TryParse(token["DIMOFS_BUTTON".Length..], out int button)
            && button is >= 0 and <= 4)
        {
            scan = (uint)(0x0C + button);
            device = 1u;
            return true;
        }

        if (!token.StartsWith("DIK_", StringComparison.Ordinal))
            return false;
        token = token[4..];
        scan = token switch
        {
            "ESCAPE" => 0x01,
            "1" => 0x02, "2" => 0x03, "3" => 0x04, "4" => 0x05,
            "5" => 0x06, "6" => 0x07, "7" => 0x08, "8" => 0x09,
            "9" => 0x0A, "0" => 0x0B,
            "MINUS" => 0x0C, "EQUALS" => 0x0D, "BACK" => 0x0E,
            "TAB" => 0x0F,
            "Q" => 0x10, "W" => 0x11, "E" => 0x12, "R" => 0x13,
            "T" => 0x14, "Y" => 0x15, "U" => 0x16, "I" => 0x17,
            "O" => 0x18, "P" => 0x19,
            "LBRACKET" => 0x1A, "RBRACKET" => 0x1B, "RETURN" => 0x1C,
            "LCONTROL" => 0x1D,
            "A" => 0x1E, "S" => 0x1F, "D" => 0x20, "F" => 0x21,
            "G" => 0x22, "H" => 0x23, "J" => 0x24, "K" => 0x25,
            "L" => 0x26, "SEMICOLON" => 0x27, "APOSTROPHE" => 0x28,
            "GRAVE" => 0x29, "LSHIFT" => 0x2A, "BACKSLASH" => 0x2B,
            "Z" => 0x2C, "X" => 0x2D, "C" => 0x2E, "V" => 0x2F,
            "B" => 0x30, "N" => 0x31, "M" => 0x32,
            "COMMA" => 0x33, "PERIOD" => 0x34, "SLASH" => 0x35,
            "RSHIFT" => 0x36, "MULTIPLY" or "NUMPADSTAR" => 0x37,
            "LMENU" or "LALT" => 0x38, "SPACE" => 0x39, "CAPITAL" => 0x3A,
            "F1" => 0x3B, "F2" => 0x3C, "F3" => 0x3D, "F4" => 0x3E,
            "F5" => 0x3F, "F6" => 0x40, "F7" => 0x41, "F8" => 0x42,
            "F9" => 0x43, "F10" => 0x44, "NUMLOCK" => 0x45,
            "SCROLL" => 0x46, "NUMPAD7" => 0x47, "NUMPAD8" => 0x48,
            "NUMPAD9" => 0x49, "SUBTRACT" or "NUMPADMINUS" => 0x4A,
            "NUMPAD4" => 0x4B, "NUMPAD5" => 0x4C, "NUMPAD6" => 0x4D,
            "ADD" or "NUMPADPLUS" => 0x4E, "NUMPAD1" => 0x4F,
            "NUMPAD2" => 0x50, "NUMPAD3" => 0x51, "NUMPAD0" => 0x52,
            "DECIMAL" or "NUMPADPERIOD" => 0x53,
            "F11" => 0x57, "F12" => 0x58, "F13" => 0x64,
            "F14" => 0x65, "F15" => 0x66, "NUMPADENTER" => 0x9C,
            "RCONTROL" => 0x9D, "DIVIDE" or "NUMPADSLASH" => 0xB5,
            "SYSRQ" => 0xB7, "RMENU" or "RALT" => 0xB8,
            "PAUSE" => 0xC5, "HOME" => 0xC7,
            "UP" or "UPARROW" => 0xC8, "PRIOR" or "PGUP" => 0xC9,
            "LEFT" => 0xCB, "RIGHT" or "RIGHTARROW" => 0xCD,
            "END" => 0xCF, "DOWN" or "DOWNARROW" => 0xD0,
            "NEXT" or "PGDN" => 0xD1, "INSERT" => 0xD2,
            "DELETE" => 0xD3, "LWIN" => 0xDB, "RWIN" => 0xDC,
            "APPS" => 0xDD,
            _ => uint.MaxValue,
        };
        return scan != uint.MaxValue;
    }

    public static bool TryToFileControl(KeyChord chord, out string control)
    {
        control = string.Empty;
        if (chord.Device == 1)
        {
            int button = (int)chord.Key switch
            {
                -1001 => 0,
                -1002 => 1,
                -1003 => 2,
                -1004 => 3,
                -1005 => 4,
                _ => -1,
            };
            if (button < 0) return false;
            control = $"DIMOFS_BUTTON{button}";
            return true;
        }
        if (chord.Device != 0) return false;

        for (uint scan = 1; scan <= 0xDD; scan++)
        {
            if (ToSilkKey(scan, 0) != chord.Key) continue;
            control = scan switch
            {
                0x37 => "DIK_NUMPADSTAR",
                0x4A => "DIK_NUMPADMINUS",
                0x4E => "DIK_NUMPADPLUS",
                0xB5 => "DIK_NUMPADSLASH",
                0xC8 => "DIK_UPARROW",
                0xC9 => "DIK_PGUP",
                0xCD => "DIK_RIGHTARROW",
                0xD0 => "DIK_DOWNARROW",
                0xD1 => "DIK_PGDN",
                _ => FileToken(scan),
            };
            return control.Length != 0;
        }
        return false;
    }

    private static string FileToken(uint scan) => scan switch
    {
        0x01 => "DIK_ESCAPE",
        >= 0x02 and <= 0x0A => $"DIK_{scan - 1}",
        0x0B => "DIK_0", 0x0C => "DIK_MINUS", 0x0D => "DIK_EQUALS",
        0x0E => "DIK_BACK", 0x0F => "DIK_TAB",
        >= 0x10 and <= 0x19 => $"DIK_{"QWERTYUIOP"[(int)(scan - 0x10)]}",
        0x1A => "DIK_LBRACKET", 0x1B => "DIK_RBRACKET",
        0x1C => "DIK_RETURN", 0x1D => "DIK_LCONTROL",
        >= 0x1E and <= 0x26 => $"DIK_{"ASDFGHJKL"[(int)(scan - 0x1E)]}",
        0x27 => "DIK_SEMICOLON", 0x28 => "DIK_APOSTROPHE",
        0x29 => "DIK_GRAVE", 0x2A => "DIK_LSHIFT", 0x2B => "DIK_BACKSLASH",
        >= 0x2C and <= 0x32 => $"DIK_{"ZXCVBNM"[(int)(scan - 0x2C)]}",
        0x33 => "DIK_COMMA", 0x34 => "DIK_PERIOD", 0x35 => "DIK_SLASH",
        0x36 => "DIK_RSHIFT", 0x38 => "DIK_LMENU", 0x39 => "DIK_SPACE",
        0x3A => "DIK_CAPITAL",
        >= 0x3B and <= 0x44 => $"DIK_F{scan - 0x3A}",
        0x45 => "DIK_NUMLOCK", 0x46 => "DIK_SCROLL",
        0x47 => "DIK_NUMPAD7", 0x48 => "DIK_NUMPAD8", 0x49 => "DIK_NUMPAD9",
        0x4B => "DIK_NUMPAD4", 0x4C => "DIK_NUMPAD5", 0x4D => "DIK_NUMPAD6",
        0x4F => "DIK_NUMPAD1", 0x50 => "DIK_NUMPAD2", 0x51 => "DIK_NUMPAD3",
        0x52 => "DIK_NUMPAD0", 0x53 => "DIK_DECIMAL",
        0x57 => "DIK_F11", 0x58 => "DIK_F12", 0x64 => "DIK_F13",
        0x65 => "DIK_F14", 0x66 => "DIK_F15", 0x9C => "DIK_NUMPADENTER",
        0x9D => "DIK_RCONTROL", 0xB7 => "DIK_SYSRQ", 0xB8 => "DIK_RALT",
        0xC5 => "DIK_PAUSE", 0xC7 => "DIK_HOME",
        0xCB => "DIK_LEFT", 0xCF => "DIK_END", 0xD2 => "DIK_INSERT",
        0xD3 => "DIK_DELETE", 0xDB => "DIK_LWIN", 0xDC => "DIK_RWIN",
        0xDD => "DIK_APPS",
        _ => string.Empty,
    };
}
