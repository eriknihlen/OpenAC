using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class ClientObjectTableUpdateTests
{
    [Fact]
    public void UpsertProperties_unknownObject_createsThenMerges()
    {
        var t = new ClientObjectTable();
        bool added = false;
        t.ObjectAdded += _ => added = true;

        var bundle = new PropertyBundle();
        bundle.Ints[5] = 1234;     // EncumbranceVal

        t.UpsertProperties(0x50000001u, bundle);

        var o = t.Get(0x50000001u);
        Assert.NotNull(o);
        Assert.Equal(1234, o!.Properties.Ints[5]);
        Assert.True(added);
    }

    [Fact]
    public void UpsertProperties_existingObject_mergesAndFiresUpdated()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = 0x50000001u });
        bool updated = false;
        t.ObjectUpdated += _ => updated = true;

        var bundle = new PropertyBundle();
        bundle.Ints[5] = 99;
        t.UpsertProperties(0x50000001u, bundle);

        Assert.Equal(99, t.Get(0x50000001u)!.Properties.Ints[5]);
        Assert.True(updated);
    }

    [Fact]
    public void UpdateStackSize_knownObject_setsFieldsAndFiresUpdated()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = 0x600u, StackSize = 1, Value = 5 });
        bool updated = false;
        t.ObjectUpdated += _ => updated = true;

        bool ok = t.UpdateStackSize(0x600u, stackSize: 25, value: 125);

        Assert.True(ok);
        Assert.Equal(25, t.Get(0x600u)!.StackSize);
        Assert.Equal(125, t.Get(0x600u)!.Value);
        Assert.True(updated);
    }

    [Fact]
    public void UpdateStackSize_unknownObject_returnsFalse()
        => Assert.False(new ClientObjectTable().UpdateStackSize(0xDEADu, 1, 1));

    [Fact]
    public void UpdateAppraisal_retainsPropertiesAndDefensiveSpellSnapshot()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = 0x700u });
        int updates = 0;
        table.ObjectUpdated += _ => updates++;
        var bundle = new PropertyBundle();
        bundle.Ints[106] = 420;
        uint[] spells = [1327u, 1132u];

        Assert.True(table.UpdateAppraisal(0x700u, bundle, spells, 12.345d));
        spells[0] = 0u;

        ClientObject item = table.Get(0x700u)!;
        Assert.Equal(420, item.Properties.Ints[106]);
        Assert.Equal([1327u, 1132u], item.AppraisedSpellIds);
        Assert.Equal(12345, item.LastAppraisalTimeMs);
        Assert.Equal(1, updates);
    }

    [Fact]
    public void UpdateAppraisal_retainsWeaponAndArmorProfilesAndSurvivesLaterPropertyUpdate()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = 0x701u });
        var bundle = new PropertyBundle();
        var weapon = new ClientWeaponProfile(
            DamageType: 4u,
            WeaponTime: 30u,
            WeaponSkill: 34u,
            Damage: 12u,
            DamageVariance: 0.2d,
            DamageMod: 1.1d,
            WeaponLength: 1.0d,
            MaxVelocity: 2.0d,
            WeaponOffense: 1.05d,
            MaxVelocityEstimated: 1u);
        var armor = new ClientArmorProfile(
            SlashingProtection: 1.5f,
            PiercingProtection: 1.4f,
            BludgeoningProtection: 1.3f,
            ColdProtection: 1.2f,
            FireProtection: 1.1f,
            AcidProtection: 1.0f,
            NetherProtection: 0.9f,
            LightningProtection: 0.8f);

        Assert.True(table.UpdateAppraisal(
            0x701u, bundle, [], weaponProfile: weapon, armorProfile: armor));

        ClientObject item = table.Get(0x701u)!;
        Assert.Equal(weapon, item.WeaponProfile);
        Assert.Equal(armor, item.ArmorProfile);

        // A later non-appraisal property update (a normal server property
        // broadcast) must not clear the retained profile -- only a fresh
        // appraisal response replaces it.
        var followUp = new PropertyBundle();
        followUp.Ints[5] = 999;
        Assert.True(table.UpdateProperties(0x701u, followUp));

        item = table.Get(0x701u)!;
        Assert.Equal(999, item.Properties.Ints[5]);
        Assert.Equal(weapon, item.WeaponProfile);
        Assert.Equal(armor, item.ArmorProfile);
    }

    [Fact]
    public void UpdateAppraisal_withoutAProfile_clearsAnyStaleOneFromAnEarlierAppraisal()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = 0x702u });
        var weapon = new ClientWeaponProfile(1u, 1u, 1u, 1u, 0d, 0d, 0d, 0d, 0d, 0u);
        Assert.True(table.UpdateAppraisal(
            0x702u, new PropertyBundle(), [], weaponProfile: weapon));
        Assert.NotNull(table.Get(0x702u)!.WeaponProfile);

        // Re-appraising the SAME object without a WeaponProfile blob (e.g.
        // it was reclassified, or the response genuinely omitted it) must
        // not leave the earlier appraisal's stale profile behind.
        Assert.True(table.UpdateAppraisal(0x702u, new PropertyBundle(), []));

        Assert.Null(table.Get(0x702u)!.WeaponProfile);
        Assert.Null(table.Get(0x702u)!.ArmorProfile);
    }

    [Fact]
    public void NotifyObjectUpdated_publishesAClientSideChangeToObservers()
    {
        var t = new ClientObjectTable();
        t.AddOrUpdate(new ClientObject { ObjectId = 0x50000001u });
        ClientObject? seen = null;
        t.ObjectUpdated += o => seen = o;

        t.Get(0x50000001u)!.SellState = 1;
        Assert.True(t.NotifyObjectUpdated(0x50000001u));

        Assert.Same(t.Get(0x50000001u), seen);
        Assert.Equal(1, seen!.SellState);
        Assert.False(t.NotifyObjectUpdated(0x50000002u));
    }
}
