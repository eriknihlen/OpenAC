using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

// A focused plugin dialog claims only Enter, keypad Enter and Escape: every
// other key, held movement and plugin hotkeys work as if nothing in the
// interface had focus. Wired as the client wires it: the retained root's
// capture slot behind the composite capture source and the Silk mouse
// source the game's dispatcher reads. Keys reach the dispatcher first, then
// the interface.
public sealed class PluginDialogInputCaptureTests
{
    [Fact]
    public void AFocusedDialogLeavesTheKeyboardToTheGame()
    {
        var h = new Harness();
        h.ShowDialog();

        Assert.False(h.Slot.WantCaptureKeyboard);
        Assert.False(h.Slot.HasKeyboardFocus);
        Assert.False(h.Mouse.WantCaptureKeyboard);
        Assert.True(h.Mouse.ClaimsKey(Key.Enter));
        Assert.True(h.Mouse.ClaimsKey(Key.KeypadEnter));
        Assert.True(h.Mouse.ClaimsKey(Key.Escape));
        Assert.False(h.Mouse.ClaimsKey(Key.W));
    }

    [Fact]
    public void HeldMovementKeepsWorkingWhileADialogHasFocus()
    {
        var h = new Harness();
        h.Down(Key.W);
        Assert.True(h.Dispatcher.IsActionHeld(InputAction.MovementForward));

        h.ShowDialog();
        Assert.True(h.Dispatcher.IsActionHeld(InputAction.MovementForward));
        h.Up(Key.W);
        Assert.False(h.Dispatcher.IsActionHeld(InputAction.MovementForward));

        h.Down(Key.W);
        Assert.Contains((InputAction.MovementForward, ActivationType.Press), h.Fired);
        Assert.True(h.Dispatcher.IsActionHeld(InputAction.MovementForward));
    }

    [Fact]
    public void EnterAndEscapeAnswerTheDialogAndNotTheGame()
    {
        var h = new Harness();
        var answers = new List<int>();
        h.ShowDialog(answers);

        h.Press(Key.Enter);
        Assert.Equal([0], answers);
        h.ShowDialog(answers);
        h.Press(Key.Escape);
        Assert.Equal([0, 1], answers);

        Assert.DoesNotContain(h.Fired, f => f.Action is InputAction.EnterChatMode or InputAction.EscapeKey);

        // With no dialog up the same keys are the game's again.
        h.Press(Key.Enter);
        Assert.Contains((InputAction.EnterChatMode, ActivationType.Press), h.Fired);
    }

    [Fact]
    public void PluginHotkeysOtherThanTheAnswerKeysFireWhileADialogHasFocus()
    {
        // No game bindings, so a plugin may take Enter.
        var h = new Harness(new KeyBindings());
        int f9 = 0;
        int enter = 0;
        h.Hotkeys.Register("f9", "F9", new PluginKeyChord(PluginKey.F9), () => f9++);
        h.Hotkeys.Register("enter", "Enter", new PluginKeyChord(PluginKey.Enter), () => enter++);
        h.ShowDialog();

        h.Press(Key.F9);
        h.Press(Key.Enter);
        Assert.Equal(1, f9);
        Assert.Equal(0, enter);
        Assert.Null(h.Host.ShownFor(h.Owner));   // Enter answered the dialog instead

        h.Press(Key.Enter);
        Assert.Equal(1, enter);
    }

    [Fact]
    public void AFocusedFieldStillTakesEveryKey()
    {
        var h = new Harness();
        var field = new UiField { Left = 10f, Top = 10f, Width = 200f, Height = 20f };
        h.Root.AddChild(field);
        int f9 = 0;
        h.Hotkeys.Register("f9", "F9", new PluginKeyChord(PluginKey.F9), () => f9++);
        h.Down(Key.W);

        h.Root.SetKeyboardFocus(field);

        Assert.True(h.Slot.WantCaptureKeyboard);
        Assert.True(h.Slot.HasKeyboardFocus);
        Assert.True(h.Mouse.WantCaptureKeyboard);
        Assert.True(h.Mouse.ClaimsKey(Key.W));
        Assert.True(h.Mouse.ClaimsKey(Key.Enter));
        h.Fired.Clear();
        h.Press(Key.F9);
        h.Press(Key.Escape);   // claimed by the dispatcher check before the field lets go of focus
        Assert.Empty(h.Fired);
        Assert.Equal(0, f9);
    }

    [Fact]
    public void AKeyHeldWhenAFieldTakesTheKeyboardKeepsMovingUntilReleased()
    {
        // Clicking the chat entry while running: the run goes on until the
        // key comes up, and a key typed into the field moves nothing.
        var h = new Harness();
        var field = new UiField { Left = 10f, Top = 10f, Width = 200f, Height = 20f };
        h.Root.AddChild(field);
        h.Down(Key.W);

        h.Root.SetKeyboardFocus(field);

        Assert.True(h.Mouse.WantCaptureKeyboard);
        Assert.True(h.Dispatcher.IsActionHeld(InputAction.MovementForward));
        h.Up(Key.W);
        Assert.False(h.Dispatcher.IsActionHeld(InputAction.MovementForward));

        h.Down(Key.W);
        Assert.False(h.Dispatcher.IsActionHeld(InputAction.MovementForward));
        h.Root.SetKeyboardFocus(null);
        Assert.False(h.Mouse.WantCaptureKeyboard);
        Assert.False(h.Dispatcher.IsActionHeld(InputAction.MovementForward));
    }

    private sealed class Harness
    {
        public Harness(KeyBindings? bindings = null)
        {
            Mouse = SilkMouseSource.CreateDetached(
                new MouseSurface(),
                new CompositeInputCaptureSource(new DevToolsInputCaptureSource(enabled: false), Slot),
                Keyboard,
                new HostQuiescenceGate());
            bindings ??= KeyBindings.RetailDefaults();
            Dispatcher = InputDispatcher.CreateDetached(Keyboard, Mouse, bindings);
            Dispatcher.Fired += (action, activation) => Fired.Add((action, activation));
            Dispatcher.Attach();
            Hotkeys.Bind(Keyboard, bindings, Dispatcher, Slot);
            Slot.Bind(Root);
            Root.AddChild(Owner);
            Host = new PluginDialogHost(Root, static () => null, font: null);
        }

        public UiRoot Root { get; } = new() { Width = 800f, Height = 600f };
        public UiPanel Owner { get; } = new() { Left = 100f, Top = 100f, Width = 200f, Height = 150f };
        public RetainedUiInputCaptureSlot Slot { get; } = new();
        public FakeKeyboard Keyboard { get; } = new();
        public SilkMouseSource Mouse { get; }
        public InputDispatcher Dispatcher { get; }
        public AppHotkeyRegistry Hotkeys { get; } = new(overridesFilePath: null);
        public PluginDialogHost Host { get; }
        public List<(InputAction Action, ActivationType Activation)> Fired { get; } = new();

        public void ShowDialog(List<int>? answers = null)
        {
            Host.Confirm("T", "M", ["OK", "Cancel"], Owner, i => answers?.Add(i));
            Assert.IsAssignableFrom<UiPluginDialog>(Root.KeyboardFocus);
        }

        public void Down(Key key)
        {
            Keyboard.Down(key);
            Root.OnKeyDown((int)key);
        }

        public void Up(Key key)
        {
            Keyboard.Up(key);
            Root.OnKeyUp((int)key);
        }

        public void Press(Key key)
        {
            Down(key);
            Up(key);
        }
    }

    private sealed class FakeKeyboard : IKeyboardSource
    {
        private readonly HashSet<Key> _held = new();
        public event Action<Key, ModifierMask>? KeyDown;
        public event Action<Key, ModifierMask>? KeyUp;
        public bool IsHeld(Key key) => _held.Contains(key);
        public ModifierMask CurrentModifiers => ModifierMask.None;

        public void Down(Key key)
        {
            _held.Add(key);
            KeyDown?.Invoke(key, ModifierMask.None);
        }

        public void Up(Key key)
        {
            _held.Remove(key);
            KeyUp?.Invoke(key, ModifierMask.None);
        }
    }

    private sealed class MouseSurface : IMouseEventSurface
    {
        public void AddMouseDown(Action<MouseButton> callback) { }
        public void RemoveMouseDown(Action<MouseButton> callback) { }
        public void AddMouseUp(Action<MouseButton> callback) { }
        public void RemoveMouseUp(Action<MouseButton> callback) { }
        public void AddMouseMove(Action<Vector2> callback) { }
        public void RemoveMouseMove(Action<Vector2> callback) { }
        public void AddScroll(Action<float> callback) { }
        public void RemoveScroll(Action<float> callback) { }
        public bool IsButtonPressed(MouseButton button) => false;
    }
}
