using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class InputDispatcherCaptureTests
{
    private static (InputDispatcher dispatcher, FakeKeyboardSource kb, FakeMouseSource mouse, KeyBindings bindings, List<(InputAction, ActivationType)> fired)
        Build()
    {
        var kb = new FakeKeyboardSource();
        var mouse = new FakeMouseSource();
        var bindings = new KeyBindings();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();
        var fired = new List<(InputAction, ActivationType)>();
        dispatcher.Fired += (a, t) => fired.Add((a, t));
        return (dispatcher, kb, mouse, bindings, fired);
    }

    [Fact]
    public void BeginCapture_consumes_next_chord_without_firing_actions()
    {
        var (dispatcher, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));

        KeyChord? captured = null;
        dispatcher.BeginCapture(c => captured = c);

        kb.EmitKeyDown(Key.W, ModifierMask.None);

        Assert.NotNull(captured);
        Assert.Equal(new KeyChord(Key.W, ModifierMask.None), captured!.Value);
        Assert.Empty(fired);
        Assert.False(dispatcher.IsCapturing);
    }

    [Fact]
    public void BeginCapture_Escape_cancels_with_default_chord()
    {
        var (dispatcher, kb, _, _, fired) = Build();

        KeyChord? captured = null;
        bool calledBack = false;
        dispatcher.BeginCapture(c => { captured = c; calledBack = true; });

        kb.EmitKeyDown(Key.Escape, ModifierMask.None);

        Assert.True(calledBack);
        Assert.Equal(default(KeyChord), captured!.Value);
        Assert.Empty(fired);
        Assert.False(dispatcher.IsCapturing);
    }

    [Fact]
    public void BeginCapture_modifier_only_keys_dont_complete_capture()
    {
        var (dispatcher, kb, _, _, _) = Build();

        bool calledBack = false;
        dispatcher.BeginCapture(_ => calledBack = true);

        kb.EmitKeyDown(Key.ShiftLeft, ModifierMask.Shift);
        Assert.False(calledBack);
        Assert.True(dispatcher.IsCapturing);

        kb.EmitKeyDown(Key.ControlLeft, ModifierMask.Shift | ModifierMask.Ctrl);
        Assert.False(calledBack);
        Assert.True(dispatcher.IsCapturing);

        KeyChord? captured = null;
        dispatcher.CancelCapture();
        bool fireCalled = false;
        dispatcher.BeginCapture(c => { captured = c; fireCalled = true; });
        kb.EmitKeyDown(Key.A, ModifierMask.Shift | ModifierMask.Ctrl);
        Assert.True(fireCalled);
        Assert.Equal(new KeyChord(Key.A, ModifierMask.Shift | ModifierMask.Ctrl), captured!.Value);
    }

    [Fact]
    public void BeginCapture_modifier_released_alone_becomes_bare_primary_key()
    {
        var (dispatcher, kb, _, _, fired) = Build();
        KeyChord? captured = null;
        dispatcher.BeginCapture(chord => captured = chord);

        kb.EmitKeyDown(Key.ShiftLeft, ModifierMask.Shift);
        Assert.Null(captured);
        kb.EmitKeyUp(Key.ShiftLeft, ModifierMask.Shift);

        Assert.Equal(
            new KeyChord(Key.ShiftLeft, ModifierMask.None),
            captured);
        Assert.Empty(fired);
    }

    [Fact]
    public void BeginCapture_completes_with_modifier_state()
    {
        var (dispatcher, kb, _, _, _) = Build();

        KeyChord? captured = null;
        dispatcher.BeginCapture(c => captured = c);

        kb.EmitKeyDown(Key.A, ModifierMask.Ctrl);

        Assert.Equal(new KeyChord(Key.A, ModifierMask.Ctrl), captured!.Value);
    }

    [Fact]
    public void BeginCapture_consumes_mouse_button_as_retail_qualified_control()
    {
        var (dispatcher, _, mouse, bindings, fired) = Build();
        var left = new KeyChord(
            InputDispatcher.MouseButtonToKey(MouseButton.Left),
            ModifierMask.Ctrl,
            Device: 1);
        bindings.Add(new Binding(left, InputAction.ToggleInventoryPanel));
        mouse.WantCaptureMouse = true;

        KeyChord? captured = null;
        dispatcher.BeginCapture(chord => captured = chord);
        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.Ctrl);

        Assert.Equal(left, captured);
        Assert.False(dispatcher.IsCapturing);
        Assert.Empty(fired);
    }

    [Fact]
    public void CancelCapture_invokes_callback_with_default_chord_and_clears_state()
    {
        var (dispatcher, _, _, _, _) = Build();

        KeyChord? captured = null;
        bool calledBack = false;
        dispatcher.BeginCapture(c => { captured = c; calledBack = true; });

        Assert.True(dispatcher.IsCapturing);

        dispatcher.CancelCapture();

        Assert.True(calledBack);
        Assert.Equal(default(KeyChord), captured!.Value);
        Assert.False(dispatcher.IsCapturing);
    }

    [Fact]
    public void IsCapturing_is_false_initially()
    {
        var (dispatcher, _, _, _, _) = Build();
        Assert.False(dispatcher.IsCapturing);
    }

    [Fact]
    public void SetBindings_replaces_active_bindings_table()
    {
        var (dispatcher, kb, _, _, fired) = Build();

        // Original table empty — pressing W fires nothing.
        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.Empty(fired);

        // Swap in a new table that binds W → MovementForward.
        var swapped = new KeyBindings();
        swapped.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));
        dispatcher.SetBindings(swapped);

        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.Single(fired);
        Assert.Equal((InputAction.MovementForward, ActivationType.Press), fired[0]);
    }
}
