using AcDream.Core.Spells;
using DatReaderWriter.Enums;

namespace AcDream.Core.Tests.Spells;

public sealed class SpellMetadataTests
{
    [Theory]
    [InlineData(SpellType.Projectile, true)]
    [InlineData(SpellType.LifeProjectile, true)]
    [InlineData(SpellType.EnchantmentProjectile, true)]
    [InlineData(SpellType.Enchantment, false)]
    [InlineData(SpellType.Boost, false)]
    [InlineData(SpellType.Transfer, false)]
    public void ASpellIsAProjectileByItsMetaSpellTypeWithoutTheProjectileFlag(SpellType type, bool projectile)
    {
        var spell = new SpellMetadata(
            86u, "Force Bolt I", "War Magic", 0u, 0u, "", 0f, 0,
            false, false, "", 0, 0, 0x3u, 1, false, true, false,
            0f, 0u, 0u, 0u, (int)type);

        Assert.Equal(projectile, spell.IsProjectile);
    }
}
