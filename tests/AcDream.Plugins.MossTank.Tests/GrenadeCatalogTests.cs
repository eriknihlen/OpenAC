using AcDream.Plugins.MossTank;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class GrenadeCatalogTests
{
    [Fact]
    public void OfficialCatalog_containsExact72EntriesAndRepresentativeEdges()
    {
        Assert.Equal(72, GrenadeCatalog.All.Count);
        Assert.True(GrenadeCatalog.TryGet(
            "Iron Phial of Imperil",
            out GrenadeDefinition iron));
        Assert.Equal((1323u, 100, 75),
            (iron.SpellId, iron.Spellcraft, iron.RequiredAlchemy));
        Assert.True(GrenadeCatalog.TryGet(
            "Mana Phial of Fester",
            out GrenadeDefinition mana));
        Assert.Equal((2178u, 520, 400),
            (mana.SpellId, mana.Spellcraft, mana.RequiredAlchemy));
        Assert.False(GrenadeCatalog.TryGet(
            "mana phial of fester",
            out _));
    }
}
