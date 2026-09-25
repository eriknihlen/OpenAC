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

    /// <summary>
    /// The latest answer wins: an unsuccessful appraisal after a successful
    /// one is recorded (and published) even though the object was already
    /// answered, and a successful one after it clears the mark again without
    /// losing what the first answer delivered.
    /// </summary>
    [Fact]
    public void AppraisalOutcome_followsTheLatestAnswer()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = 0x701u });
        var bundle = new PropertyBundle();
        bundle.Ints[19u] = 40;
        int updates = 0;
        table.ObjectUpdated += _ => updates++;

        Assert.True(table.UpdateAppraisal(0x701u, bundle, Array.Empty<uint>()));
        Assert.False(table.Get(0x701u)!.LastAppraisalUnsuccessful);

        Assert.True(table.RecordUnsuccessfulAppraisal(0x701u));
        Assert.True(table.Get(0x701u)!.LastAppraisalUnsuccessful);
        Assert.Equal(40, table.Get(0x701u)!.Properties.Ints[19u]);
        Assert.Equal(2, updates);

        // A repeat refusal only moves its time stamp and says nothing.
        Assert.True(table.RecordUnsuccessfulAppraisal(0x701u));
        Assert.Equal(2, updates);

        Assert.True(table.UpdateAppraisal(0x701u, bundle, Array.Empty<uint>()));
        Assert.False(table.Get(0x701u)!.LastAppraisalUnsuccessful);
    }

    /// <summary>
    /// Each refusal is stamped with the table's clock, a repeat one too, so a
    /// reader can tell how long ago the server last refused.
    /// </summary>
    [Fact]
    public void AnUnsuccessfulAnswer_isStampedWithTheTableClock()
    {
        double now = 12.5d;
        var table = new ClientObjectTable(() => now);
        table.AddOrUpdate(new ClientObject { ObjectId = 0x701u });

        Assert.True(table.RecordUnsuccessfulAppraisal(0x701u));
        Assert.Equal(12.5d, table.Get(0x701u)!.LastAppraisalUnsuccessfulAtSeconds);

        now = 40d;
        Assert.True(table.RecordUnsuccessfulAppraisal(0x701u));
        Assert.Equal(40d, table.Get(0x701u)!.LastAppraisalUnsuccessfulAtSeconds);
    }

    private const uint Pack = 0x50000001u;
    private const uint Carried = 0x70000702u;

    public static TheoryData<string> ServerStatements() =>
    [
        "create",
        "properties",
        "upsert-properties",
        "int-property",
        "data-id-property",
        "instance-id-property",
        "int64-property",
        "stack-size",
        "house-restrictions",
        "server-move",
        "confirmed-move",
        "confirmed-wield",
        "membership",
        "pack-listing",
        "inventory-manifest",
        "equipment-manifest",
    ];

    /// <summary>
    /// The server keeps quiet about an object it has lost. Anything it does
    /// say about the object shows it still has it, so an earlier refusal to
    /// appraise it no longer stands.
    /// </summary>
    [Theory]
    [MemberData(nameof(ServerStatements))]
    public void AnythingTheServerSaysAboutTheObject_overturnsARefusal(string statement)
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = Carried, ContainerId = Pack });
        Assert.True(table.RecordUnsuccessfulAppraisal(Carried));
        int updates = 0;
        table.ObjectUpdated += item =>
        {
            if (item.ObjectId == Carried)
                updates++;
        };
        int moves = 0;
        table.ObjectMoved += move =>
        {
            if (move.ItemId == Carried)
                moves++;
        };

        switch (statement)
        {
            case "create":
                table.Ingest(new WeenieData(
                    Guid: Carried, Name: "Gem", Type: ItemType.Misc, WeenieClassId: 2,
                    IconId: 0, IconOverlayId: 0, IconUnderlayId: 0, Effects: 0,
                    Value: 5, StackSize: 1, StackSizeMax: 1, Burden: 10,
                    ContainerId: Pack, WielderId: null, ValidLocations: null,
                    CurrentWieldedLocation: null, Priority: null,
                    ItemsCapacity: null, ContainersCapacity: null,
                    Structure: null, MaxStructure: null, Workmanship: null));
                break;
            case "properties":
                table.UpdateProperties(Carried, new PropertyBundle());
                break;
            case "upsert-properties":
                table.UpsertProperties(Carried, new PropertyBundle());
                break;
            case "int-property":
                table.UpdateIntProperty(Carried, 19u, 40);
                break;
            case "data-id-property":
                table.UpdateDataIdProperty(Carried, 8u, 0x06001234u);
                break;
            case "instance-id-property":
                table.UpdateInstanceIdProperty(Carried, 3u, 0x50000009u);
                break;
            case "int64-property":
                table.UpdateInt64Property(Carried, 1u, 7L);
                break;
            case "stack-size":
                table.UpdateStackSize(Carried, 3, 15);
                break;
            case "house-restrictions":
                table.UpdateHouseRestrictions(
                    Carried,
                    new HouseRestrictionRecord(true, 0u, new Dictionary<uint, uint>()));
                break;
            case "server-move":
                table.ApplyServerMove(Carried, Pack, 0u, newSlot: 0);
                break;
            case "confirmed-move":
                table.ApplyConfirmedServerMove(Carried, Pack, 0u, newSlot: 0);
                break;
            case "confirmed-wield":
                table.ApplyConfirmedServerWield(Carried, Pack, EquipMask.HeadWear);
                break;
            case "membership":
                table.RecordMembership(Carried, Pack);
                break;
            case "pack-listing":
                table.ReplaceContents(Pack, [new ContainerContentEntry(Carried, 0u)]);
                break;
            case "inventory-manifest":
                table.InitializeInventoryManifest(
                    Pack,
                    [new ContainerContentEntry(Carried, 0u)]);
                break;
            case "equipment-manifest":
                table.InitializeEquipmentManifest(
                    Pack,
                    [new EquipmentManifestEntry(Carried, EquipMask.HeadWear, 0u)]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(statement));
        }

        Assert.False(table.Get(Carried)!.LastAppraisalUnsuccessful);
        // Whoever watches the object hears that it changed.
        Assert.True(updates + moves > 0, $"{statement}: nothing was published.");
    }

    /// <summary>
    /// A move the client makes on its own, ahead of the server, says nothing
    /// about whether the server still has the object.
    /// </summary>
    [Fact]
    public void AMoveTheClientMakesOnItsOwn_leavesARefusalStanding()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = Carried, ContainerId = Pack });
        Assert.True(table.RecordUnsuccessfulAppraisal(Carried));

        Assert.True(table.MoveItemOptimistic(Carried, 0x50000002u, 0));

        Assert.True(table.Get(Carried)!.LastAppraisalUnsuccessful);
    }
}
