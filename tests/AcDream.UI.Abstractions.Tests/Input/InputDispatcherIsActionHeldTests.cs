using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class InputDispatcherIsActionHeldTests
{
    private static (InputDispatcher dispatcher, FakeKeyboardSource kb, FakeMouseSource mouse, KeyBindings bindings)
        Build()
    {
        var kb = new FakeKeyboardSource();
        var mouse = new FakeMouseSource();
        var bindings = new KeyBindings();
        var dispatcher = InputDispatcher.CreateDetached(kb, mouse, bindings);
        dispatcher.Attach();
        return (dispatcher, kb, mouse, bindings);
    }

    [Fact]
    public void IsActionHeld_returns_true_while_bound_key_held()
    {
        var (dispatcher, kb, _, bindings) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));

        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));

        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));

        kb.EmitKeyUp(Key.W, ModifierMask.None);
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));
    }

    [Fact]
    public void IsActionHeld_CombatBindingOnSameChord_ShadowsGamePolling()
    {
        var (dispatcher, kb, _, bindings) = Build();
        var chord = new KeyChord(Key.W, ModifierMask.None);
        bindings.Add(new Binding(chord, InputAction.MovementForward));
        bindings.Add(new Binding(
            chord,
            InputAction.CombatCastCurrentSpell,
            Scope: InputScope.MagicCombat));
        dispatcher.SetCombatScope(InputScope.MagicCombat);

        kb.EmitKeyDown(Key.W, ModifierMask.None);

        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.True(dispatcher.IsActionHeld(InputAction.CombatCastCurrentSpell));

        dispatcher.SetCombatScope(null);
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.False(dispatcher.IsActionHeld(InputAction.CombatCastCurrentSpell));
    }

    [Fact]
    public void IsActionHeld_ExplicitShiftCombatChord_ShadowsBareMovementFallback()
    {
        var (dispatcher, kb, _, bindings) = Build();
        bindings.Add(new Binding(
            new KeyChord(Key.W, ModifierMask.None),
            InputAction.MovementForward));
        bindings.Add(new Binding(
            new KeyChord(Key.W, ModifierMask.Shift),
            InputAction.CombatCastCurrentSpell,
            Scope: InputScope.MagicCombat));
        dispatcher.SetCombatScope(InputScope.MagicCombat);

        kb.EmitKeyDown(Key.W, ModifierMask.Shift);

        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.True(dispatcher.IsActionHeld(InputAction.CombatCastCurrentSpell));
    }

    [Fact]
    public void IsActionHeld_returns_false_when_no_binding_for_action()
    {
        var (dispatcher, kb, _, bindings) = Build();
        // No binding for MovementBackup at all.
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));

        kb.EmitKeyDown(Key.W, ModifierMask.None);

        Assert.False(dispatcher.IsActionHeld(InputAction.MovementBackup));
    }

    [Fact]
    public void IsActionHeld_modifier_mismatch_returns_false()
    {
        var (dispatcher, kb, _, bindings) = Build();
        bindings.Add(new Binding(new KeyChord(Key.A, ModifierMask.Ctrl), InputAction.SelectionExamine));

        kb.EmitKeyDown(Key.A, ModifierMask.None);
        Assert.False(dispatcher.IsActionHeld(InputAction.SelectionExamine));

        kb.EmitKeyUp(Key.A, ModifierMask.None);
        kb.EmitKeyDown(Key.A, ModifierMask.Ctrl);
        Assert.True(dispatcher.IsActionHeld(InputAction.SelectionExamine));
    }

    [Fact]
    public void IsActionHeld_any_of_multiple_bindings_satisfies()
    {
        var (dispatcher, kb, _, bindings) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));
        bindings.Add(new Binding(new KeyChord(Key.Up, ModifierMask.None), InputAction.MovementForward));

        kb.EmitKeyDown(Key.Up, ModifierMask.None);
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));

        kb.EmitKeyUp(Key.Up, ModifierMask.None);
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));

        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));
    }

    [Fact]
    public void IsActionHeld_works_for_mouse_button_chord()
    {
        var (dispatcher, _, mouse, bindings) = Build();
        var rmb = new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Right), ModifierMask.None, Device: 1);
        bindings.Add(new Binding(rmb, InputAction.AcdreamRmbOrbitHold, ActivationType.Hold));

        Assert.False(dispatcher.IsActionHeld(InputAction.AcdreamRmbOrbitHold));

        mouse.EmitMouseDown(MouseButton.Right, ModifierMask.None);
        Assert.True(dispatcher.IsActionHeld(InputAction.AcdreamRmbOrbitHold));

        mouse.EmitMouseUp(MouseButton.Right, ModifierMask.None);
        Assert.False(dispatcher.IsActionHeld(InputAction.AcdreamRmbOrbitHold));
    }

    [Fact]
    public void IsActionHeld_returns_false_for_None_action()
    {
        var (dispatcher, _, _, _) = Build();
        Assert.False(dispatcher.IsActionHeld(InputAction.None));
    }

    [Fact]
    public void IsActionHeld_None_chord_remains_held_when_user_adds_Shift()
    {
        var (dispatcher, kb, _, bindings) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));

        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));

        kb.CurrentModifiers = ModifierMask.Shift;
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));
    }

    [Fact]
    public void IsActionHeld_None_chord_does_not_fire_when_user_adds_Ctrl()
    {
        var (dispatcher, kb, _, bindings) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));

        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));

        kb.CurrentModifiers = ModifierMask.Ctrl;
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));
    }

    [Fact]
    public void IsActionHeld_gated_off_while_keyboard_captured()
    {
        var (dispatcher, kb, mouse, bindings) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));
        kb.EmitKeyDown(Key.W, ModifierMask.None);

        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));

        mouse.WantCaptureKeyboard = true;
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));

        mouse.WantCaptureKeyboard = false;
        mouse.WantCaptureMouse = true;
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));
    }
}
