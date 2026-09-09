using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class KeyChordTests
{
    [Fact]
    public void Same_key_no_modifier_equal()
    {
        var a = new KeyChord(Key.W, ModifierMask.None);
        var b = new KeyChord(Key.W, ModifierMask.None);
        Assert.Equal(a, b);
    }

    [Fact]
    public void CtrlA_does_not_match_bare_A()
    {
        var ctrlA = new KeyChord(Key.A, ModifierMask.Ctrl);
        var bareA = new KeyChord(Key.A, ModifierMask.None);
        Assert.NotEqual(ctrlA, bareA);
    }

    [Fact]
    public void CtrlA_does_not_match_ShiftCtrlA()
    {
        var ctrlA      = new KeyChord(Key.A, ModifierMask.Ctrl);
        var shiftCtrlA = new KeyChord(Key.A, ModifierMask.Shift | ModifierMask.Ctrl);
        Assert.NotEqual(ctrlA, shiftCtrlA);
    }

    [Fact]
    public void Different_keys_with_same_modifier_distinct()
    {
        Assert.NotEqual(
            new KeyChord(Key.A, ModifierMask.Shift),
            new KeyChord(Key.B, ModifierMask.Shift));
    }

    [Fact]
    public void Default_device_is_zero_keyboard()
    {
        var c = new KeyChord(Key.W, ModifierMask.None);
        Assert.Equal(0, c.Device);
    }

    [Fact]
    public void Different_device_with_same_key_distinct()
    {
        var keyboard = new KeyChord(Key.A, ModifierMask.None, Device: 0);
        var mouse    = new KeyChord(Key.A, ModifierMask.None, Device: 1);
        Assert.NotEqual(keyboard, mouse);
    }
}
