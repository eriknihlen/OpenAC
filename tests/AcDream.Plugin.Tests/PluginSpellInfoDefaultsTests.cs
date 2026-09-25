using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests;

/// <summary>
/// A spell described by a host that does not read the spell table's
/// effects, formula version, display order and component loss reports them
/// as zero.
/// </summary>
public sealed class PluginSpellInfoDefaultsTests
{
    [Fact]
    public void ASpellsTableFieldsDefaultToZero()
    {
        PluginSpellInfo spell = new(
            1u, "Spell", 0u, 1, 1, 1, 0f, 0u, string.Empty, false, false);

        Assert.Equal(0u, spell.CasterEffect);
        Assert.Equal(0u, spell.TargetEffect);
        Assert.Equal(0u, spell.FormulaVersion);
        Assert.Equal(0, spell.DisplayOrder);
        Assert.Equal(0f, spell.ComponentLoss);
    }
}
