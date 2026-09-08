using System;
using System.Collections.Generic;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class InputDispatcherTests
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
    public void KeyDown_with_matching_chord_fires_press_action()
    {
        var (_, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));

        kb.EmitKeyDown(Key.W, ModifierMask.None);

        Assert.Single(fired);
        Assert.Equal((InputAction.MovementForward, ActivationType.Press), fired[0]);
    }

    [Fact]
    public void KeyDown_with_mismatched_modifier_does_not_fire()
    {
        var (_, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(new KeyChord(Key.A, ModifierMask.Ctrl), InputAction.SelectionExamine));

        kb.EmitKeyDown(Key.A, ModifierMask.None);
        Assert.Empty(fired);

        kb.EmitKeyDown(Key.A, ModifierMask.Shift | ModifierMask.Ctrl);
        Assert.Empty(fired);

        kb.EmitKeyDown(Key.A, ModifierMask.Ctrl);
        Assert.Single(fired);
    }

    [Fact]
    public void WantCaptureKeyboard_suppresses_KeyDown_events()
    {
        var (_, kb, mouse, bindings, fired) = Build();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));
        mouse.WantCaptureKeyboard = true;

        kb.EmitKeyDown(Key.W, ModifierMask.None);

        Assert.Empty(fired);
    }

    [Fact]
    public void WantCaptureMouse_suppresses_MouseDown_events()
    {
        var (_, _, mouse, bindings, fired) = Build();
        var leftClick = new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Left), ModifierMask.None, Device: 1);
        bindings.Add(new Binding(leftClick, InputAction.SelectLeft));
        mouse.WantCaptureMouse = true;

        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);

        Assert.Empty(fired);
    }

    [Fact]
    public void Default_active_scope_is_Game()
    {
        var (dispatcher, _, _, _, _) = Build();
        Assert.Equal(InputScope.Game, dispatcher.ActiveScope);
    }

    [Fact]
    public void Magic_combat_scope_shadows_game_binding_for_same_chord()
    {
        var (dispatcher, kb, _, bindings, fired) = Build();
        var chord = new KeyChord(Key.Number1, ModifierMask.None);
        bindings.Add(new Binding(chord, InputAction.UseQuickSlot_1));
        bindings.Add(new Binding(chord, InputAction.UseSpellSlot_1, Scope: InputScope.MagicCombat));

        kb.EmitKeyDown(Key.Number1, ModifierMask.None);
        dispatcher.SetCombatScope(InputScope.MagicCombat);
        kb.EmitKeyDown(Key.Number1, ModifierMask.None);

        Assert.Equal(
            [(InputAction.UseQuickSlot_1, ActivationType.Press),
             (InputAction.UseSpellSlot_1, ActivationType.Press)],
            fired);
    }

    [Fact]
    public void Same_scope_retail_duplicate_chord_fires_every_distinct_action()
    {
        var (_, kb, _, bindings, fired) = Build();
        var chord = new KeyChord(Key.Number1, ModifierMask.Alt);
        bindings.Add(new Binding(chord, InputAction.ToggleFloatingChatWindow1));
        bindings.Add(new Binding(chord, InputAction.UseQuickSlot_10));

        kb.EmitKeyDown(Key.Number1, ModifierMask.Alt);

        Assert.Equal(
            [(InputAction.ToggleFloatingChatWindow1, ActivationType.Press),
             (InputAction.UseQuickSlot_10, ActivationType.Press)],
            fired);
    }

    [Fact]
    public void Changing_combat_scope_releases_hold_resolved_in_previous_scope()
    {
        var (dispatcher, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(
            new KeyChord(Key.Delete, ModifierMask.None),
            InputAction.CombatLowAttack,
            ActivationType.Hold,
            InputScope.MeleeCombat));
        dispatcher.SetCombatScope(InputScope.MeleeCombat);

        kb.EmitKeyDown(Key.Delete, ModifierMask.None);
        dispatcher.SetCombatScope(InputScope.MagicCombat);

        Assert.Equal(
            [(InputAction.CombatLowAttack, ActivationType.Press),
             (InputAction.CombatLowAttack, ActivationType.Release)],
            fired);
    }

    [Fact]
    public void PushScope_changes_ActiveScope()
    {
        var (dispatcher, _, _, _, _) = Build();
        dispatcher.PushScope(InputScope.Chat);
        Assert.Equal(InputScope.Chat, dispatcher.ActiveScope);
    }

    [Fact]
    public void PopScope_with_mismatched_expected_throws()
    {
        var (dispatcher, _, _, _, _) = Build();
        dispatcher.PushScope(InputScope.Chat);
        Assert.Throws<InvalidOperationException>(() => dispatcher.PopScope(InputScope.Dialog));
    }

    [Fact]
    public void PopScope_restores_previous()
    {
        var (dispatcher, _, _, _, _) = Build();
        dispatcher.PushScope(InputScope.Chat);
        dispatcher.PopScope(InputScope.Chat);
        Assert.Equal(InputScope.Game, dispatcher.ActiveScope);
    }

    [Fact]
    public void Hold_binding_fires_Press_on_KeyDown_and_Release_on_KeyUp()
    {
        var (_, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(
            new KeyChord(Key.ShiftLeft, ModifierMask.None),
            InputAction.MovementRunLock,
            ActivationType.Hold));

        kb.EmitKeyDown(Key.ShiftLeft, ModifierMask.None);
        Assert.Contains((InputAction.MovementRunLock, ActivationType.Press), fired);

        kb.EmitKeyUp(Key.ShiftLeft, ModifierMask.None);
        Assert.Contains((InputAction.MovementRunLock, ActivationType.Release), fired);
    }

    [Fact]
    public void Tick_fires_Hold_for_currently_held_chords()
    {
        var (dispatcher, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(
            new KeyChord(Key.ShiftLeft, ModifierMask.None),
            InputAction.MovementRunLock,
            ActivationType.Hold));

        kb.EmitKeyDown(Key.ShiftLeft, ModifierMask.None);
        fired.Clear(); // discard the initial Press transition

        dispatcher.Tick();
        dispatcher.Tick();

        // Two Hold ticks while held.
        Assert.Equal(2, fired.Count);
        Assert.All(fired, e => Assert.Equal((InputAction.MovementRunLock, ActivationType.Hold), e));

        kb.EmitKeyUp(Key.ShiftLeft, ModifierMask.None);
        fired.Clear();

        dispatcher.Tick();
        Assert.Empty(fired); // no longer held
    }

    [Fact]
    public void RetailBareLeftShiftBinding_NormalizesSilkSelfModifierBit()
    {
        var kb = new FakeKeyboardSource();
        var mouse = new FakeMouseSource();
        var dispatcher = InputDispatcher.CreateDetached(
            kb,
            mouse,
            KeyBindings.RetailDefaults());
        dispatcher.Attach();
        var fired = new List<(InputAction, ActivationType)>();
        dispatcher.Fired += (action, activation) => fired.Add((action, activation));

        kb.EmitKeyDown(Key.ShiftLeft, ModifierMask.Shift);
        kb.EmitKeyUp(Key.ShiftLeft, ModifierMask.Shift);

        Assert.Contains(
            (InputAction.MovementWalkMode, ActivationType.Press),
            fired);
        Assert.Contains(
            (InputAction.MovementWalkMode, ActivationType.Release),
            fired);
    }

    [Fact]
    public void Hold_callback_scope_change_DoesNotDispatchStaleSnapshotChord()
    {
        var (dispatcher, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(
            new KeyChord(Key.Delete, ModifierMask.None),
            InputAction.CombatLowAttack,
            ActivationType.Hold,
            InputScope.MeleeCombat));
        bindings.Add(new Binding(
            new KeyChord(Key.End, ModifierMask.None),
            InputAction.CombatHighAttack,
            ActivationType.Hold,
            InputScope.MeleeCombat));
        dispatcher.SetCombatScope(InputScope.MeleeCombat);
        kb.EmitKeyDown(Key.Delete, ModifierMask.None);
        kb.EmitKeyDown(Key.End, ModifierMask.None);
        fired.Clear();

        bool changed = false;
        dispatcher.Fired += (_, activation) =>
        {
            if (activation != ActivationType.Hold || changed)
                return;
            changed = true;
            dispatcher.SetCombatScope(InputScope.MagicCombat);
        };

        dispatcher.Tick();

        Assert.Single(fired, entry => entry.Item2 == ActivationType.Hold);
        Assert.Equal(2, fired.Count(entry => entry.Item2 == ActivationType.Release));
    }

    [Fact]
    public void Release_binding_fires_only_on_KeyUp()
    {
        var (_, kb, _, bindings, fired) = Build();
        bindings.Add(new Binding(
            new KeyChord(Key.W, ModifierMask.None),
            InputAction.MovementStop,
            ActivationType.Release));

        kb.EmitKeyDown(Key.W, ModifierMask.None);
        Assert.Empty(fired);

        kb.EmitKeyUp(Key.W, ModifierMask.None);
        Assert.Single(fired);
        Assert.Equal((InputAction.MovementStop, ActivationType.Release), fired[0]);
    }

    [Fact]
    public void MouseDown_with_matching_chord_fires_action()
    {
        var (_, _, mouse, bindings, fired) = Build();
        var leftClick = new KeyChord(InputDispatcher.MouseButtonToKey(MouseButton.Left), ModifierMask.None, Device: 1);
        bindings.Add(new Binding(leftClick, InputAction.SelectLeft));

        mouse.EmitMouseDown(MouseButton.Left, ModifierMask.None);

        Assert.Single(fired);
        Assert.Equal((InputAction.SelectLeft, ActivationType.Press), fired[0]);
    }

    [Fact]
    public void Tick_no_op_when_no_chords_held()
    {
        var (dispatcher, _, _, _, fired) = Build();
        dispatcher.Tick();
        Assert.Empty(fired);
    }

    [Fact]
    public void Scroll_positive_emits_ScrollUp_press()
    {
        var (_, _, mouse, _, fired) = Build();
        mouse.EmitScroll(1.0f);
        Assert.Single(fired);
        Assert.Equal((InputAction.ScrollUp, ActivationType.Press), fired[0]);
    }

    [Fact]
    public void Scroll_negative_emits_ScrollDown_press()
    {
        var (_, _, mouse, _, fired) = Build();
        mouse.EmitScroll(-1.0f);
        Assert.Single(fired);
        Assert.Equal((InputAction.ScrollDown, ActivationType.Press), fired[0]);
    }

    [Fact]
    public void WantCaptureMouse_suppresses_Scroll_events()
    {
        var (_, _, mouse, _, fired) = Build();
        mouse.WantCaptureMouse = true;
        mouse.EmitScroll(1.0f);
        Assert.Empty(fired);
    }

    [Fact]
    public void AutomationHeldAction_usesFiredTransitionsAndHeldPolling()
    {
        var (dispatcher, _, _, _, fired) = Build();

        Assert.True(dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, true));
        Assert.True(dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.Equal([(InputAction.MovementForward, ActivationType.Press)], fired);

        Assert.True(dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, false));
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.Equal(
            [
                (InputAction.MovementForward, ActivationType.Press),
                (InputAction.MovementForward, ActivationType.Release),
            ],
            fired);
    }

    [Fact]
    public void AutomationPress_obeysKeyboardCapture()
    {
        var (dispatcher, _, mouse, _, fired) = Build();
        mouse.WantCaptureKeyboard = true;

        Assert.False(dispatcher.TryInvokeAutomationAction(InputAction.CombatToggleCombat));
        Assert.False(dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, true));
        Assert.Empty(fired);

        mouse.WantCaptureKeyboard = false;
        Assert.True(dispatcher.TryInvokeAutomationAction(InputAction.CombatToggleCombat));
        Assert.Equal([(InputAction.CombatToggleCombat, ActivationType.Press)], fired);
    }

    [Fact]
    public void AutomationHeldRelease_isDeliveredAfterKeyboardCaptureBegins()
    {
        var (dispatcher, _, mouse, _, fired) = Build();
        Assert.True(dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, true));

        mouse.WantCaptureKeyboard = true;
        Assert.False(dispatcher.IsActionHeld(InputAction.MovementForward));
        Assert.True(dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, false));

        Assert.Equal(
            [
                (InputAction.MovementForward, ActivationType.Press),
                (InputAction.MovementForward, ActivationType.Release),
            ],
            fired);
    }

    [Fact]
    public void AutomationInjection_rejectsUndefinedActions()
    {
        var (dispatcher, _, _, _, _) = Build();
        var undefined = (InputAction)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => dispatcher.TryInvokeAutomationAction(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => dispatcher.TrySetAutomationActionHeld(undefined, true));
    }
}
