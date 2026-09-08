using AcDream.UI.Abstractions.Input;

namespace AcDream.UI.Abstractions.Tests.Input;

public class ModifierMaskTests
{
    [Fact]
    public void None_is_zero()
    {
        Assert.Equal((uint)0, (uint)ModifierMask.None);
    }

    [Fact]
    public void Bit_values_match_retail_keymap_metakey_table()
    {
        Assert.Equal((uint)0x01, (uint)ModifierMask.Shift);
        Assert.Equal((uint)0x02, (uint)ModifierMask.Ctrl);
        Assert.Equal((uint)0x04, (uint)ModifierMask.Alt);
        Assert.Equal((uint)0x08, (uint)ModifierMask.Win);
    }

    [Fact]
    public void Or_combines_bits()
    {
        var combo = ModifierMask.Shift | ModifierMask.Ctrl;
        Assert.True(combo.HasFlag(ModifierMask.Shift));
        Assert.True(combo.HasFlag(ModifierMask.Ctrl));
        Assert.False(combo.HasFlag(ModifierMask.Alt));
    }

    [Fact]
    public void Equality_distinguishes_distinct_masks()
    {
        Assert.NotEqual(ModifierMask.Shift, ModifierMask.Ctrl);
        Assert.NotEqual(ModifierMask.None, ModifierMask.Shift);
        Assert.Equal(ModifierMask.Shift | ModifierMask.Ctrl,
                     ModifierMask.Ctrl | ModifierMask.Shift);
    }
}
