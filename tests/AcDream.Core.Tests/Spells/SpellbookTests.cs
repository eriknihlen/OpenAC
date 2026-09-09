using AcDream.Core.Spells;
using Xunit;

namespace AcDream.Core.Tests.Spells;

public sealed class SpellbookTests
{
    [Fact]
    public void InstallMetadata_InstallsOnceAndRefreshesExistingLearnedState()
    {
        var book = new Spellbook();
        book.OnSpellLearned(1u);
        int changes = 0;
        book.SpellbookChanged += () => changes++;
        SpellTable table = SpellTable.Create([TestSpell(1u)]);

        book.InstallMetadata(table);

        Assert.Same(table, book.Metadata);
        Assert.True(book.TryGetMetadata(1u, out _));
        Assert.Equal(1, changes);
        book.InstallMetadata(table);
        Assert.Throws<InvalidOperationException>(
            () => book.InstallMetadata(SpellTable.Empty));
    }

    [Fact]
    public void OnSpellLearned_FiresEvent_Idempotent()
    {
        var book = new Spellbook();
        int events = 0;
        book.SpellLearned += _ => events++;

        book.OnSpellLearned(0x3E1);   // Flame Bolt I
        book.OnSpellLearned(0x3E1);   // duplicate — no event

        Assert.Equal(1, events);
        Assert.Equal(1, book.LearnedCount);
        Assert.True(book.Knows(0x3E1));
    }

    [Fact]
    public void OnSpellForgotten_FiresEvent()
    {
        var book = new Spellbook();
        book.OnSpellLearned(0x3E1);

        uint seen = 0;
        book.SpellForgotten += id => seen = id;

        book.OnSpellForgotten(0x3E1);
        Assert.Equal(0x3E1u, seen);
        Assert.False(book.Knows(0x3E1));
    }

    [Fact]
    public void OnEnchantmentAdded_FiresEvent_StoresByLayer()
    {
        var book = new Spellbook();
        ActiveEnchantmentRecord added = default;
        book.EnchantmentAdded += r => added = r;

        book.OnEnchantmentAdded(spellId: 42, layerId: 7, duration: 300f, casterGuid: 0xBEEF);

        Assert.Equal(42u, added.SpellId);
        Assert.Equal(7u, added.LayerId);
        Assert.Equal(300f, added.Duration, 4);
        Assert.Equal(1, book.ActiveCount);
    }

    [Fact]
    public void OnEnchantmentAdded_SameLayer_Replaces()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(42, 7, 300f, 0);
        book.OnEnchantmentAdded(42, 7, 600f, 0);   // refresh

        Assert.Equal(1, book.ActiveCount);
        foreach (var e in book.ActiveEnchantments)
            Assert.Equal(600f, e.Duration, 4);
    }

    [Fact]
    public void OnEnchantmentAdded_SameLayerDifferentSpell_RemainsDistinct()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(42, 7, 300f, 0);
        book.OnEnchantmentAdded(43, 7, 600f, 0);

        Assert.Equal(2, book.ActiveCount);
    }

    [Fact]
    public void GetVitalMod_ReusesCachedValueUntilEnchantmentsChange()
    {
        SpellTable table = SpellTable.Create(
        [
            TestSpell(42u) with { Family = 100u },
        ]);
        var book = new Spellbook(table);
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 42u,
            LayerId: 7u,
            Duration: 300f,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt,
            StatModKey: EnchantmentMath.StatKey.MaxHealth,
            StatModValue: 1.5f,
            Bucket: 1u));
        Assert.Equal(
            1.5f,
            book.GetVitalMod(EnchantmentMath.StatKey.MaxHealth).Multiplier);
        EnchantmentMath.VitalMod actual = default;

        for (int iteration = 0; iteration < 10_000; iteration++)
            actual = book.GetVitalMod(EnchantmentMath.StatKey.MaxHealth);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            actual =
                book.GetVitalMod(EnchantmentMath.StatKey.MaxHealth);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1.5f, actual.Multiplier);
        Assert.Equal(0, allocated);

        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 42u,
            LayerId: 7u,
            Duration: 300f,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.SecondAtt,
            StatModKey: EnchantmentMath.StatKey.MaxHealth,
            StatModValue: 1.25f,
            Bucket: 1u));

        Assert.Equal(
            1.25f,
            book.GetVitalMod(EnchantmentMath.StatKey.MaxHealth).Multiplier);
    }

    [Fact]
    public void LiveEnchantments_NewIdentityInsertsAtRetailListHead()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(Effect(1u, layer: 1u, category: 12u));
        book.OnEnchantmentAdded(Effect(2u, layer: 2u, category: 12u));

        Assert.Equal(2u, Assert.Single(book.EnchantmentsInEffectSnapshot).SpellId);
    }

    [Fact]
    public void LiveEnchantment_ExistingIdentityRefreshKeepsListPosition()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(Effect(1u, layer: 1u, category: 12u));
        book.OnEnchantmentAdded(Effect(2u, layer: 2u, category: 12u));
        book.OnEnchantmentAdded(Effect(1u, layer: 1u, category: 12u));

        Assert.Equal(2u, Assert.Single(book.EnchantmentsInEffectSnapshot).SpellId);
    }

    [Fact]
    public void ManifestEnchantments_PreserveReceivedListOrder()
    {
        var book = new Spellbook();
        book.ReplaceManifest(
            new Dictionary<uint, float>(),
            [Effect(1u, layer: 1u, category: 12u), Effect(2u, layer: 2u, category: 12u)],
            [],
            [],
            0x3FFFu);

        Assert.Equal(1u, Assert.Single(book.EnchantmentsInEffectSnapshot).SpellId);
    }

    [Fact]
    public void ReplaceManifest_ReplacesFavoritesFiltersAndDesiredComponents()
    {
        var book = new Spellbook();
        book.OnSpellLearned(999);

        book.ReplaceManifest(
            new Dictionary<uint, float> { [42] = 123f },
            [new ActiveEnchantmentRecord(50, 2, 60, 1)],
            [new uint[] { 42, 43 }],
            [(100u, 25u)],
            0xA5u);

        Assert.False(book.Knows(999));
        Assert.True(book.Knows(42));
        Assert.Equal(new uint[] { 42, 43 }, book.GetFavorites(0));
        Assert.Equal(25u, book.DesiredComponents[100]);
        Assert.Equal(0xA5u, book.SpellbookFilters);
        Assert.Equal(1, book.ActiveCount);
    }

    [Fact]
    public void SetFavorite_MovingWithinTheSameBar_InsertShiftsRatherThanSwaps()
    {
        var book = new Spellbook();
        book.SetFavorite(0, 0, 1u);
        book.SetFavorite(0, 1, 2u);
        book.SetFavorite(0, 2, 3u);

        book.SetFavorite(0, 1, 1u);

        Assert.Equal(new uint[] { 2u, 1u, 3u }, book.GetFavorites(0));
    }

    [Fact]
    public void SetFavorite_MovingToTheEnd_AppendsRatherThanLeavingAGap()
    {
        var book = new Spellbook();
        book.SetFavorite(0, 0, 1u);
        book.SetFavorite(0, 1, 2u);
        book.SetFavorite(0, 2, 3u);

        book.SetFavorite(0, 2, 1u);

        Assert.Equal(new uint[] { 2u, 3u, 1u }, book.GetFavorites(0));
    }

    [Fact]
    public void RemoveFavorite_LeavesTheRemainingBarContiguous()
    {
        var book = new Spellbook();
        book.SetFavorite(0, 0, 1u);
        book.SetFavorite(0, 1, 2u);
        book.SetFavorite(0, 2, 3u);

        book.RemoveFavorite(0, 2u);

        Assert.Equal(new uint[] { 1u, 3u }, book.GetFavorites(0));
    }

    [Fact]
    public void OnEnchantmentRemoved_FiresEvent()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(42, 7, 300f, 0);

        uint removedSpell = 0;
        book.EnchantmentRemoved += r => removedSpell = r.SpellId;

        book.OnEnchantmentRemoved(7, 42);
        Assert.Equal(42u, removedSpell);
        Assert.Equal(0, book.ActiveCount);
    }

    [Fact]
    public void OnCooldown_removes_first_expired_match_then_reveals_next_record()
    {
        var book = new Spellbook();
        var expired = new ActiveEnchantmentRecord(
            SpellId: 0x802Au,
            LayerId: 1u,
            Duration: 5d,
            CasterGuid: 0u,
            Bucket: Spellbook.CooldownBucket,
            StartTime: 0d);
        var active = expired with
        {
            LayerId = 2u,
            Duration = 30d,
            StartTime = 10d,
        };
        book.ReplaceManifest(
            new Dictionary<uint, float>(),
            [expired, active],
            Array.Empty<IReadOnlyList<uint>>(),
            Array.Empty<(uint Id, uint Amount)>(),
            0u);
        ActiveEnchantmentRecord? removed = null;
        book.EnchantmentRemoved += record => removed = record;

        Assert.False(book.OnCooldown(
            0x2Au,
            currentTime: 20d,
            out double expiredRemaining));
        Assert.Equal(-15d, expiredRemaining);
        Assert.Equal(expired, removed);
        Assert.Equal(1, book.ActiveCount);

        Assert.True(book.OnCooldown(
            0x2Au,
            currentTime: 20d,
            out double activeRemaining));
        Assert.Equal(20d, activeRemaining);
    }

    [Fact]
    public void OnCooldown_ignores_same_spell_outside_cooldown_registry()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 0x802Au,
            LayerId: 1u,
            Duration: 30d,
            CasterGuid: 0u,
            Bucket: 1u,
            StartTime: 10d));

        Assert.False(book.OnCooldown(
            0x2Au,
            currentTime: 20d,
            out double remaining));
        Assert.Equal(0d, remaining);
        Assert.Equal(1, book.ActiveCount);
    }

    [Fact]
    public void OnCooldown_zero_group_is_inactive_and_does_not_remove()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 0x8000u,
            LayerId: 1u,
            Duration: 30d,
            CasterGuid: 0u,
            Bucket: Spellbook.CooldownBucket,
            StartTime: 10d));

        Assert.False(book.OnCooldown(
            cooldownId: 0u,
            currentTime: 20d,
            out double remaining));
        Assert.Equal(0d, remaining);
        Assert.Equal(1, book.ActiveCount);
    }

    [Fact]
    public void OnPurgeAll_RemovesOnlyFiniteMultAndAddEnchantments()
    {
        var book = new Spellbook();
        book.OnEnchantmentAdded(new(1, 1, 100, 0, Bucket: 1));
        book.OnEnchantmentAdded(new(2, 2, 100, 0, Bucket: 2));
        book.OnEnchantmentAdded(new(3, 3, -1, 0, Bucket: 2));
        book.OnEnchantmentAdded(new(4, 4, 100, 0, Bucket: 4));
        book.OnEnchantmentAdded(new(5, 5, 100, 0, Bucket: 8));

        int removals = 0;
        book.EnchantmentRemoved += _ => removals++;

        book.OnPurgeAll();
        Assert.Equal(2, removals);
        Assert.Equal(3, book.ActiveCount);
        Assert.Equal([3u, 4u, 5u],
            book.ActiveEnchantmentSnapshot.Select(record => record.SpellId).Order().ToArray());
    }

    [Fact]
    public void OnPurgeBad_UsesStatModBeneficialFlag_AndPreservesPermanent()
    {
        const uint Beneficial = 0x02000000u;
        var book = new Spellbook();
        book.OnEnchantmentAdded(new(1, 1, 100, 0, StatModType: 0, Bucket: 1));
        book.OnEnchantmentAdded(new(2, 2, 100, 0, StatModType: Beneficial, Bucket: 2));
        book.OnEnchantmentAdded(new(3, 3, -1, 0, StatModType: 0, Bucket: 2));
        book.OnEnchantmentAdded(new(4, 4, 100, 0, StatModType: 0, Bucket: 8));

        book.OnPurgeBadEnchantments();

        Assert.Equal([2u, 3u, 4u],
            book.ActiveEnchantmentSnapshot.Select(record => record.SpellId).Order().ToArray());
    }

    private static ActiveEnchantmentRecord Effect(uint spellId, uint layer, uint category)
        => new(
            SpellId: spellId,
            LayerId: layer,
            Duration: 60d,
            CasterGuid: 1u,
            Bucket: 1u,
            StartTime: 10d,
            SpellCategory: category,
            PowerLevel: 7u);

    private static SpellMetadata TestSpell(uint spellId) => new(
        spellId, "Test", "War Magic", 0u, 0u, "", 0f, 0,
        false, false, "", 0, 0, 0u, 0, false, false, true,
        0f, 0u, 0u, 0u, 0);
}
