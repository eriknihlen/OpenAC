using System.Linq;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class KeyBindingsRetailTests
{
    [Fact]
    public void MovementForward_bound_to_W_and_Up()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.MovementForward).ToList();
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.W, ModifierMask.None));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.Up, ModifierMask.None));
    }

    [Fact]
    public void MovementBackup_bound_to_X_and_Down_NOT_S()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.MovementBackup).ToList();
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.X, ModifierMask.None));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.Down, ModifierMask.None));
        Assert.DoesNotContain(binds, x => x.Chord.Key == Key.S);
    }

    [Fact]
    public void S_key_is_MovementStop_in_retail()
    {
        var b = KeyBindings.RetailDefaults();
        var stop = b.Find(new KeyChord(Key.S, ModifierMask.None), ActivationType.Press);
        Assert.NotNull(stop);
        Assert.Equal(InputAction.MovementStop, stop!.Value.Action);
    }

    [Fact]
    public void MovementStrafeLeft_bound_to_Z_and_AltA_and_AltLeft()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.MovementStrafeLeft).ToList();
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.Z, ModifierMask.None));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.A, ModifierMask.Alt));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.Left, ModifierMask.Alt));
    }

    [Fact]
    public void MovementStrafeRight_bound_to_C_and_AltD_and_AltRight()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.MovementStrafeRight).ToList();
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.C, ModifierMask.None));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.D, ModifierMask.Alt));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.Right, ModifierMask.Alt));
    }

    [Fact]
    public void Sleeping_bound_to_B()
    {
        var b = KeyBindings.RetailDefaults();
        Assert.Contains(b.ForAction(InputAction.Sleeping),
            x => x.Chord == new KeyChord(Key.B, ModifierMask.None));
    }

    [Fact]
    public void MovementWalkMode_uses_Hold_activation()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.MovementWalkMode).ToList();
        Binding binding = Assert.Single(binds);
        Assert.Equal(ActivationType.Hold, binding.Activation);
        Assert.Equal(
            new KeyChord(Key.ShiftLeft, ModifierMask.None),
            binding.Chord);
    }

    [Fact]
    public void ToggleChatEntry_bound_to_Tab_NOT_fly_toggle()
    {
        var b = KeyBindings.RetailDefaults();
        var hit = b.Find(new KeyChord(Key.Tab, ModifierMask.None), ActivationType.Press);
        Assert.NotNull(hit);
        Assert.Equal(InputAction.ToggleChatEntry, hit!.Value.Action);
    }

    [Fact]
    public void LOGOUT_is_Shift_Escape()
    {
        var b = KeyBindings.RetailDefaults();
        var hit = b.Find(new KeyChord(Key.Escape, ModifierMask.Shift), ActivationType.Press);
        Assert.NotNull(hit);
        Assert.Equal(InputAction.LOGOUT, hit!.Value.Action);
    }

    [Fact]
    public void EscapeKey_is_bare_Escape()
    {
        var b = KeyBindings.RetailDefaults();
        var hit = b.Find(new KeyChord(Key.Escape, ModifierMask.None), ActivationType.Press);
        Assert.NotNull(hit);
        Assert.Equal(InputAction.EscapeKey, hit!.Value.Action);
    }

    [Fact]
    public void SelectRightIsAReleaseCompletedClick()
    {
        var bindings = KeyBindings.RetailDefaults();
        Binding binding = Assert.Single(bindings.ForAction(InputAction.SelectRight));

        Assert.Equal(1, binding.Chord.Device);
        Assert.Equal(
            InputDispatcher.MouseButtonToKey(MouseButton.Right),
            binding.Chord.Key);
        Assert.Equal(ActivationType.Click, binding.Activation);
    }

    [Theory]
    [InlineData(Key.Delete, InputAction.CombatLowAttack)]
    [InlineData(Key.End, InputAction.CombatMediumAttack)]
    [InlineData(Key.PageDown, InputAction.CombatHighAttack)]
    public void CombatAttackHeightBindings_EmitHoldTransitions(Key key, InputAction action)
    {
        var b = KeyBindings.RetailDefaults();

        var hit = b.Find(new KeyChord(key, ModifierMask.None), ActivationType.Hold);

        Assert.Equal(action, hit?.Action);
    }

    [Fact]
    public void QuickSlot_5_BareAndCtrlBothUseRetailAction()
    {
        var b = KeyBindings.RetailDefaults();
        var bare = b.Find(new KeyChord(Key.Number5, ModifierMask.None), ActivationType.Press);
        var ctrl = b.Find(new KeyChord(Key.Number5, ModifierMask.Ctrl), ActivationType.Press);

        Assert.Equal(InputAction.UseQuickSlot_5, bare?.Action);
        Assert.Equal(InputAction.UseQuickSlot_5, ctrl?.Action);
        Assert.Empty(b.ForAction(InputAction.SelectQuickSlot_5));
    }

    [Fact]
    public void UseQuickSlot_18_bound_to_Alt_Number9()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.UseQuickSlot_18).ToList();
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.Number9, ModifierMask.Alt));
    }

    [Fact]
    public void TogglePluginManager_is_Shift_Ctrl_F1()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.TogglePluginManager).ToList();
        Assert.Contains(binds, x =>
            x.Chord.Key == Key.F1
            && x.Chord.Modifiers.HasFlag(ModifierMask.Shift)
            && x.Chord.Modifiers.HasFlag(ModifierMask.Ctrl));
    }

    [Fact]
    public void Acdream_debug_actions_relocated_to_Ctrl_F_keys()
    {
        var b = KeyBindings.RetailDefaults();
        Assert.Contains(b.ForAction(InputAction.AcdreamToggleDebugPanel),
            x => x.Chord == new KeyChord(Key.F1, ModifierMask.Ctrl));
        Assert.Contains(b.ForAction(InputAction.AcdreamToggleCollisionWires),
            x => x.Chord == new KeyChord(Key.F2, ModifierMask.Ctrl));
        Assert.Contains(b.ForAction(InputAction.AcdreamDumpNearby),
            x => x.Chord == new KeyChord(Key.F3, ModifierMask.Ctrl));
    }

    [Fact]
    public void ToggleHelp_is_bare_F1_NOT_acdream_debug()
    {
        var b = KeyBindings.RetailDefaults();
        var hit = b.Find(new KeyChord(Key.F1, ModifierMask.None), ActivationType.Press);
        Assert.NotNull(hit);
        Assert.Equal(InputAction.ToggleHelp, hit!.Value.Action);
    }

    [Fact]
    public void Total_binding_count_is_at_least_80()
    {
        var b = KeyBindings.RetailDefaults();
        Assert.True(b.All.Count >= 80,
            $"Expected at least 80 bindings, got {b.All.Count}");
    }

    [Fact]
    public void Cry_emote_bound_to_U()
    {
        var b = KeyBindings.RetailDefaults();
        Assert.Contains(b.ForAction(InputAction.Cry),
            x => x.Chord == new KeyChord(Key.U, ModifierMask.None));
    }

    [Fact]
    public void CombatToggleCombat_bound_to_GraveAccent()
    {
        var b = KeyBindings.RetailDefaults();
        Assert.Contains(b.ForAction(InputAction.CombatToggleCombat),
            x => x.Chord == new KeyChord(Key.GraveAccent, ModifierMask.None));
    }

    [Fact]
    public void CameraActivateAlternateMode_bound_to_F2_and_NumpadDivide()
    {
        var b = KeyBindings.RetailDefaults();
        var binds = b.ForAction(InputAction.CameraActivateAlternateMode).ToList();
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.F2, ModifierMask.None));
        Assert.Contains(binds, x => x.Chord == new KeyChord(Key.KeypadDivide, ModifierMask.None));
        Assert.All(binds, x => Assert.Equal(ActivationType.Hold, x.Activation));
    }

    [Theory]
    [InlineData(InputAction.CombatAimLow)]
    [InlineData(InputAction.CombatAimMedium)]
    [InlineData(InputAction.CombatAimHigh)]
    public void Missile_aim_actions_use_retail_press_and_release_edges(InputAction action)
    {
        var binding = Assert.Single(KeyBindings.RetailDefaults().ForAction(action));
        Assert.Equal(ActivationType.Hold, binding.Activation);
        Assert.Equal(InputScope.MissileCombat, binding.Scope);
    }

    [Fact]
    public void ScrollUp_bound_to_Ctrl_Up()
    {
        var b = KeyBindings.RetailDefaults();
        Assert.Contains(b.ForAction(InputAction.ScrollUp),
            x => x.Chord == new KeyChord(Key.Up, ModifierMask.Ctrl));
    }

    [Fact]
    public void ToggleOptionsPanel_bound_to_F11()
    {
        var b = KeyBindings.RetailDefaults();
        var bound = b.Find(new KeyChord(Key.F11, ModifierMask.None), ActivationType.Press);
        Assert.NotNull(bound);
        Assert.Equal(InputAction.ToggleOptionsPanel, bound!.Value.Action);
    }

    [Fact]
    public void NoDefaultBindingReachesTheFreeFlyCamera()
    {
        foreach (KeyBindings bindings in new[]
                 {
                     KeyBindings.RetailDefaults(),
                     KeyBindings.AcdreamCurrentDefaults(),
                 })
        {
            Assert.DoesNotContain(
                bindings.All,
                binding => binding.Action == InputAction.AcdreamToggleFlyMode);
        }
    }
}
