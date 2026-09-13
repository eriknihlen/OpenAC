using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;

namespace AcDream.App.Rendering.Immediate;

/// <summary>
/// Feeds the window's input into ImGui's event queue. Events arrive on the
/// window thread, the same thread that renders, so they go straight to the
/// IO object; ImGui itself buffers them until the next frame.
/// </summary>
internal sealed class ImGuiInputBridge : IDisposable
{
    private readonly IInputContext _input;
    private readonly List<IMouse> _mice = [];
    private readonly List<IKeyboard> _keyboards = [];
    private bool _disposed;

    internal ImGuiInputBridge(IInputContext input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        foreach (IMouse mouse in input.Mice)
            AttachMouse(mouse);
        foreach (IKeyboard keyboard in input.Keyboards)
            AttachKeyboard(keyboard);
        input.ConnectionChanged += OnConnectionChanged;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _input.ConnectionChanged -= OnConnectionChanged;
        foreach (IMouse mouse in _mice)
            DetachMouse(mouse);
        foreach (IKeyboard keyboard in _keyboards)
            DetachKeyboard(keyboard);
        _mice.Clear();
        _keyboards.Clear();
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        switch (device)
        {
            case IMouse mouse when connected && !_mice.Contains(mouse):
                AttachMouse(mouse);
                break;
            case IMouse mouse when !connected:
                DetachMouse(mouse);
                _mice.Remove(mouse);
                break;
            case IKeyboard keyboard when connected && !_keyboards.Contains(keyboard):
                AttachKeyboard(keyboard);
                break;
            case IKeyboard keyboard when !connected:
                DetachKeyboard(keyboard);
                _keyboards.Remove(keyboard);
                break;
        }
    }

    private void AttachMouse(IMouse mouse)
    {
        _mice.Add(mouse);
        mouse.MouseMove += OnMouseMove;
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.Scroll += OnScroll;
    }

    private void DetachMouse(IMouse mouse)
    {
        mouse.MouseMove -= OnMouseMove;
        mouse.MouseDown -= OnMouseDown;
        mouse.MouseUp -= OnMouseUp;
        mouse.Scroll -= OnScroll;
    }

    private void AttachKeyboard(IKeyboard keyboard)
    {
        _keyboards.Add(keyboard);
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
        keyboard.KeyChar += OnKeyChar;
    }

    private void DetachKeyboard(IKeyboard keyboard)
    {
        keyboard.KeyDown -= OnKeyDown;
        keyboard.KeyUp -= OnKeyUp;
        keyboard.KeyChar -= OnKeyChar;
    }

    private static void OnMouseMove(IMouse _, Vector2 position) =>
        ImGui.GetIO().AddMousePosEvent(position.X, position.Y);

    private static void OnMouseDown(IMouse _, MouseButton button)
    {
        if (TryMapButton(button, out int index))
            ImGui.GetIO().AddMouseButtonEvent(index, true);
    }

    private static void OnMouseUp(IMouse _, MouseButton button)
    {
        if (TryMapButton(button, out int index))
            ImGui.GetIO().AddMouseButtonEvent(index, false);
    }

    private static void OnScroll(IMouse _, ScrollWheel wheel) =>
        ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);

    private static void OnKeyDown(IKeyboard _, Key key, int scancode) => SetKey(key, true);

    private static void OnKeyUp(IKeyboard _, Key key, int scancode) => SetKey(key, false);

    private static void OnKeyChar(IKeyboard _, char character) =>
        ImGui.GetIO().AddInputCharacter(character);

    private static void SetKey(Key key, bool down)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        ImGuiKey? modifier = key switch
        {
            Key.ControlLeft or Key.ControlRight => ImGuiKey.ModCtrl,
            Key.ShiftLeft or Key.ShiftRight => ImGuiKey.ModShift,
            Key.AltLeft or Key.AltRight => ImGuiKey.ModAlt,
            Key.SuperLeft or Key.SuperRight => ImGuiKey.ModSuper,
            _ => null,
        };
        if (modifier is not null)
            io.AddKeyEvent(modifier.Value, down);
        if (TryMapKey(key, out ImGuiKey mapped))
            io.AddKeyEvent(mapped, down);
    }

    private static bool TryMapButton(MouseButton button, out int index)
    {
        index = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            MouseButton.Button4 => 3,
            MouseButton.Button5 => 4,
            _ => -1,
        };
        return index >= 0;
    }

    internal static bool TryMapKey(Key key, out ImGuiKey mapped)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            mapped = ImGuiKey.A + (key - Key.A);
            return true;
        }
        if (key >= Key.Number0 && key <= Key.Number9)
        {
            mapped = ImGuiKey._0 + (key - Key.Number0);
            return true;
        }
        if (key >= Key.F1 && key <= Key.F24)
        {
            mapped = ImGuiKey.F1 + (key - Key.F1);
            return true;
        }
        if (key >= Key.Keypad0 && key <= Key.Keypad9)
        {
            mapped = ImGuiKey.Keypad0 + (key - Key.Keypad0);
            return true;
        }
        mapped = key switch
        {
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Space => ImGuiKey.Space,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Apostrophe => ImGuiKey.Apostrophe,
            Key.Comma => ImGuiKey.Comma,
            Key.Minus => ImGuiKey.Minus,
            Key.Period => ImGuiKey.Period,
            Key.Slash => ImGuiKey.Slash,
            Key.Semicolon => ImGuiKey.Semicolon,
            Key.Equal => ImGuiKey.Equal,
            Key.LeftBracket => ImGuiKey.LeftBracket,
            Key.BackSlash => ImGuiKey.Backslash,
            Key.RightBracket => ImGuiKey.RightBracket,
            Key.GraveAccent => ImGuiKey.GraveAccent,
            Key.CapsLock => ImGuiKey.CapsLock,
            Key.ScrollLock => ImGuiKey.ScrollLock,
            Key.NumLock => ImGuiKey.NumLock,
            Key.PrintScreen => ImGuiKey.PrintScreen,
            Key.Pause => ImGuiKey.Pause,
            Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
            Key.KeypadDivide => ImGuiKey.KeypadDivide,
            Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
            Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
            Key.KeypadAdd => ImGuiKey.KeypadAdd,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.KeypadEqual => ImGuiKey.KeypadEqual,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.SuperLeft => ImGuiKey.LeftSuper,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.AltRight => ImGuiKey.RightAlt,
            Key.SuperRight => ImGuiKey.RightSuper,
            Key.Menu => ImGuiKey.Menu,
            _ => ImGuiKey.None,
        };
        return mapped != ImGuiKey.None;
    }
}
