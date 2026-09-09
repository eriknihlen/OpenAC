using System.Linq;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class KeyBindingsTests
{
    [Fact]
    public void Add_appends_to_All()
    {
        var b = new KeyBindings();
        var binding = new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward);
        b.Add(binding);
        Assert.Single(b.All);
        Assert.Equal(binding, b.All[0]);
    }

    [Fact]
    public void Remove_drops_binding()
    {
        var b = new KeyBindings();
        var binding = new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward);
        b.Add(binding);
        Assert.True(b.Remove(binding));
        Assert.Empty(b.All);
    }

    [Fact]
    public void Clear_empties_all()
    {
        var b = new KeyBindings();
        b.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));
        b.Add(new Binding(new KeyChord(Key.S, ModifierMask.None), InputAction.MovementBackup));
        b.Clear();
        Assert.Empty(b.All);
    }

    [Fact]
    public void Find_returns_matching_binding_by_chord_and_activation()
    {
        var b = new KeyBindings();
        var press   = new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward, ActivationType.Press);
        var release = new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward, ActivationType.Release);
        b.Add(press);
        b.Add(release);

        Assert.Equal(press,   b.Find(new KeyChord(Key.W, ModifierMask.None), ActivationType.Press));
        Assert.Equal(release, b.Find(new KeyChord(Key.W, ModifierMask.None), ActivationType.Release));
    }

    [Fact]
    public void Find_returns_null_when_no_match()
    {
        var b = new KeyBindings();
        b.Add(new Binding(new KeyChord(Key.W, ModifierMask.None), InputAction.MovementForward));
        Assert.Null(b.Find(new KeyChord(Key.S, ModifierMask.None), ActivationType.Press));
    }

    [Fact]
    public void RetailDefaults_resolve_shared_number_key_by_scope()
    {
        var bindings = KeyBindings.RetailDefaults();
        var chord = new KeyChord(Key.Number1, ModifierMask.None);

        Assert.Equal(
            InputAction.UseQuickSlot_1,
            bindings.Find(chord, ActivationType.Press, InputScope.Game)?.Action);
        Assert.Equal(
            InputAction.UseSpellSlot_1,
            bindings.Find(chord, ActivationType.Press, InputScope.MagicCombat)?.Action);
    }

    [Fact]
    public void ForAction_returns_all_chords_bound_to_action()
    {
        var b = new KeyBindings();
        b.Add(new Binding(new KeyChord(Key.W, ModifierMask.None),  InputAction.MovementForward));
        b.Add(new Binding(new KeyChord(Key.Up, ModifierMask.None), InputAction.MovementForward));
        b.Add(new Binding(new KeyChord(Key.S, ModifierMask.None),  InputAction.MovementBackup));

        var forward = b.ForAction(InputAction.MovementForward).ToList();
        Assert.Equal(2, forward.Count);
        Assert.Contains(forward, x => x.Chord.Key == Key.W);
        Assert.Contains(forward, x => x.Chord.Key == Key.Up);
    }

    [Fact]
    public void AcdreamCurrentDefaults_includes_WASD_movement()
    {
        var b = KeyBindings.AcdreamCurrentDefaults();
        Assert.NotNull(b.Find(new KeyChord(Key.W, ModifierMask.None), ActivationType.Press));
        Assert.NotNull(b.Find(new KeyChord(Key.S, ModifierMask.None), ActivationType.Press));
        Assert.NotNull(b.Find(new KeyChord(Key.A, ModifierMask.None), ActivationType.Press));
        Assert.NotNull(b.Find(new KeyChord(Key.D, ModifierMask.None), ActivationType.Press));
        Assert.NotNull(b.Find(new KeyChord(Key.Z, ModifierMask.None), ActivationType.Press));
        Assert.NotNull(b.Find(new KeyChord(Key.X, ModifierMask.None), ActivationType.Press));
    }

    [Fact]
    public void AcdreamCurrentDefaults_binds_shift_as_hold_for_run()
    {
        var b = KeyBindings.AcdreamCurrentDefaults();
        var hold = b.Find(new KeyChord(Key.ShiftLeft, ModifierMask.Shift), ActivationType.Hold);
        Assert.NotNull(hold);
        Assert.Equal(InputAction.MovementRunLock, hold!.Value.Action);
    }

    [Fact]
    public void RetailDefaults_differs_from_legacy_AcdreamCurrentDefaults()
    {
        var retail = KeyBindings.RetailDefaults();
        var current = KeyBindings.AcdreamCurrentDefaults();
        Assert.NotEqual(current.All.Count, retail.All.Count);
    }
}
