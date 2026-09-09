using System.Linq;
using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class ClientObjectTableTests
{
    private static ClientObject MakeItem(uint id, string name = "Widget") =>
        new ClientObject
        {
            ObjectId = id,
            WeenieClassId = 1,
            Name = name,
            Type = ItemType.Misc,
            StackSize = 1,
            Burden = 10,
            Value = 5,
        };

    [Fact]
    public void AddOrUpdate_FiresAddedEvent()
    {
        var repo = new ClientObjectTable();
        ClientObject? added = null;
        repo.ObjectAdded += i => added = i;

        var item = MakeItem(100);
        repo.AddOrUpdate(item);

        Assert.Same(item, added);
        Assert.Equal(1, repo.ObjectCount);
        Assert.Same(item, repo.Get(100));
    }

    [Fact]
    public void AddOrUpdate_ExistingItem_FiresPropertiesUpdated()
    {
        var repo = new ClientObjectTable();
        var item = MakeItem(100);
        repo.AddOrUpdate(item);

        int propUpdateCount = 0;
        repo.ObjectUpdated += _ => propUpdateCount++;

        repo.AddOrUpdate(item);
        Assert.Equal(1, propUpdateCount);
    }

    [Fact]
    public void IsOwnedByObjectMatchesRetailDirectPackAndLocationRules()
    {
        const uint player = 0x5000_0001u;
        const uint pack = 0x4000_0001u;
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(MakeItem(player, "Player"));
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = pack,
            Name = "Pack",
            Type = ItemType.Container,
            ContainerId = player,
        });
        repo.ReplaceContents(
            player,
            [new ContainerContentEntry(pack, ContainerType: 1u)]);

        ClientObject loose = MakeItem(100u, "Loose");
        loose.ContainerId = player;
        repo.AddOrUpdate(loose);

        ClientObject packed = MakeItem(101u, "Packed");
        packed.ContainerId = pack;
        repo.AddOrUpdate(packed);

        ClientObject equipped = MakeItem(102u, "Equipped");
        equipped.WielderId = player;
        equipped.CurrentlyEquippedLocation = EquipMask.MeleeWeapon;
        repo.AddOrUpdate(equipped);

        ClientObject foreign = MakeItem(103u, "Foreign");
        foreign.ContainerId = 0x6000_0001u;
        repo.AddOrUpdate(foreign);

        Assert.True(repo.IsOwnedByObject(player, player));
        Assert.True(repo.IsOwnedByObject(loose.ObjectId, player));
        Assert.True(repo.IsOwnedByObject(packed.ObjectId, player));
        Assert.True(repo.IsOwnedByObject(equipped.ObjectId, player));
        Assert.False(repo.IsOwnedByObject(foreign.ObjectId, player));
        Assert.False(repo.IsOwnedByObject(packed.ObjectId, 0u));
    }

    [Fact]
    public void StopViewingContents_RemovesOnlyTemporaryProjection()
    {
        var repo = new ClientObjectTable();
        ClientObject item = MakeItem(100u);
        item.ContainerId = 77u;
        repo.AddOrUpdate(item);
        repo.ReplaceContents(77u, new uint[] { 100u });
        uint? changed = null;
        repo.ContainerContentsReplaced += id => changed = id;

        Assert.True(repo.StopViewingContents(77u));

        Assert.Empty(repo.GetContents(77u));
        Assert.Same(item, repo.Get(100u));
        Assert.Equal(77u, item.ContainerId);
        Assert.Equal(77u, changed);
        Assert.False(repo.StopViewingContents(77u));
    }

    [Fact]
    public void StopViewingContentsTree_RemovesNestedSnapshotsButPreservesObjects()
    {
        var repo = new ClientObjectTable();
        repo.ReplaceContents(77u, new[] { new ContainerContentEntry(88u, 1u) });
        repo.ReplaceContents(88u, new[] { new ContainerContentEntry(100u, 0u) });

        Assert.Equal(2, repo.StopViewingContentsTree(77u));

        Assert.Empty(repo.GetContents(77u));
        Assert.Empty(repo.GetContents(88u));
        Assert.NotNull(repo.Get(88u));
        Assert.NotNull(repo.Get(100u));
    }

    [Fact]
    public void MoveItem_UpdatesContainerAndFiresEvent()
    {
        var repo = new ClientObjectTable();
        var item = MakeItem(100);
        repo.AddOrUpdate(item);

        uint seenOld = 999, seenNew = 999;
        repo.ObjectMoved += move =>
        {
            seenOld = move.Previous.ContainerId;
            seenNew = move.Current.ContainerId;
        };

        repo.MoveItem(100, 42, newSlot: 3);

        Assert.Equal(0u, seenOld);
        Assert.Equal(42u, seenNew);
        Assert.Equal(42u, item.ContainerId);
        Assert.Equal(3,   item.ContainerSlot);
    }

    [Fact]
    public void MoveItem_Nonexistent_ReturnsFalse()
    {
        var repo = new ClientObjectTable();
        Assert.False(repo.MoveItem(999, 42));
    }

    [Fact]
    public void ApplyServerMove_PublishesCompleteOldAndNewRetailPlacements()
    {
        var repo = new ClientObjectTable();
        const uint player = 0x50000001u, pack = 0x40000005u, itemId = 101u;
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            WielderId = player,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
        });
        ClientObjectMove? seen = null;
        repo.ObjectMoved += move => seen = move;

        Assert.True(repo.ApplyServerMove(itemId, pack, 0u, 3));

        Assert.Equal(
            new ClientObjectPlacement(0u, -1, player, EquipMask.MissileWeapon),
            seen!.Value.Previous);
        Assert.Equal(
            new ClientObjectPlacement(pack, 3, 0u, EquipMask.None),
            seen.Value.Current);
    }

    [Fact]
    public void ConfirmedUnknownMove_PublishesGuidAndRawPlacement()
    {
        var repo = new ClientObjectTable();
        ClientObjectMove? seen = null;
        repo.ObjectMoved += move => seen = move;

        Assert.False(repo.ApplyConfirmedServerMove(
            0xDEADu,
            0x40000001u,
            newWielderId: 0u,
            newSlot: 7));

        Assert.Equal(0xDEADu, seen!.Value.ItemId);
        Assert.Null(seen.Value.Item);
        Assert.Equal(default, seen.Value.Previous);
        Assert.Equal(
            new ClientObjectPlacement(0x40000001u, 7, 0u, EquipMask.None),
            seen.Value.Current);
    }

    [Fact]
    public void ConfirmedUnknownWield_PublishesGuidPlacementAndCompletion()
    {
        var repo = new ClientObjectTable();
        const uint itemId = 0xBEEFu, player = 0x50000001u;
        ClientObjectMove? seen = null;
        uint completed = 0u;
        repo.ObjectMoved += move => seen = move;
        repo.WieldConfirmed += id => completed = id;

        Assert.False(repo.ApplyConfirmedServerWield(
            itemId,
            player,
            EquipMask.MissileWeapon));

        Assert.Equal(itemId, seen!.Value.ItemId);
        Assert.Null(seen.Value.Item);
        Assert.Equal(
            new ClientObjectPlacement(0u, 0, player, EquipMask.MissileWeapon),
            seen.Value.Current);
        Assert.Equal(itemId, completed);
    }

    [Fact]
    public void Remove_FiresEventAndRemoves()
    {
        var repo = new ClientObjectTable();
        var item = MakeItem(100);
        repo.AddOrUpdate(item);

        ClientObject? removed = null;
        repo.ObjectRemoved += i => removed = i;

        Assert.True(repo.Remove(100));
        Assert.Same(item, removed);
        Assert.Null(repo.Get(100));
    }

    [Fact]
    public void UpdateProperties_MergesIncomingBundle()
    {
        var repo = new ClientObjectTable();
        var item = MakeItem(100);
        item.Properties.Ints[1] = 10;
        repo.AddOrUpdate(item);

        var patch = new PropertyBundle();
        patch.Ints[2] = 20;          // new
        patch.Ints[1] = 15;          // overrides
        patch.Strings[100] = "desc";
        repo.UpdateProperties(100, patch);

        Assert.Equal(15, item.Properties.Ints[1]);
        Assert.Equal(20, item.Properties.Ints[2]);
        Assert.Equal("desc", item.Properties.Strings[100]);
    }

    [Fact]
    public void UpdateProperties_projects_assessed_shared_cooldown_metadata()
    {
        var repo = new ClientObjectTable();
        var item = MakeItem(100);
        repo.AddOrUpdate(item);
        var patch = new PropertyBundle();
        patch.Ints[ClientObjectTable.SharedCooldownPropertyId] = 42;
        patch.Floats[ClientObjectTable.CooldownDurationPropertyId] = 30d;

        repo.UpdateProperties(100, patch);

        Assert.Equal(42u, item.CooldownId);
        Assert.Equal(30d, item.CooldownDuration);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(MakeItem(1));
        repo.AddOrUpdate(MakeItem(2));
        repo.AddOrUpdate(MakeItem(3));

        repo.Clear();
        Assert.Equal(0, repo.ObjectCount);
    }

    [Fact]
    public void UpdateIntProperty_uiEffects_setsEffectsAndFires()
    {
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x500000ABu });
        ClientObject? fired = null;
        repo.ObjectUpdated += i => fired = i;
        bool ok = repo.UpdateIntProperty(0x500000ABu, ClientObjectTable.UiEffectsPropertyId, value: 0x9);
        Assert.True(ok);
        Assert.Equal(0x9u, repo.Get(0x500000ABu)!.Effects);
        Assert.Equal(0x9, repo.Get(0x500000ABu)!.Properties.Ints[ClientObjectTable.UiEffectsPropertyId]);
        Assert.NotNull(fired);
    }

    [Fact]
    public void UpdateIntProperty_unknownItem_returnsFalse()
    {
        var repo = new ClientObjectTable();
        Assert.False(repo.UpdateIntProperty(0xDEADBEEFu, 18u, 1));
    }

    [Fact]
    public void UpdateIntProperty_uiEffectsClearedToZero_clearsEffects()
    {
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x500000ACu, Effects = 0x1u });
        repo.UpdateIntProperty(0x500000ACu, ClientObjectTable.UiEffectsPropertyId, value: 0x1);
        Assert.Equal(0x1u, repo.Get(0x500000ACu)!.Effects);
        repo.UpdateIntProperty(0x500000ACu, ClientObjectTable.UiEffectsPropertyId, value: 0);
        Assert.Equal(0u, repo.Get(0x500000ACu)!.Effects);
    }

    [Fact]
    public void UpdateIntProperty_currentWieldedLocation_updatesTypedEquipLocation()
    {
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x500000ADu,
            CurrentlyEquippedLocation = EquipMask.ChestArmor,
        });

        Assert.True(repo.UpdateIntProperty(
            0x500000ADu,
            ClientObjectTable.CurrentWieldedLocationPropertyId,
            value: 0));

        Assert.Equal(EquipMask.None, repo.Get(0x500000ADu)!.CurrentlyEquippedLocation);
        Assert.Equal(0, repo.Get(0x500000ADu)!.Properties.Ints[ClientObjectTable.CurrentWieldedLocationPropertyId]);
    }

    [Fact]
    public void UpdateIntProperty_playerKillerStatus_setsPublicWeenieBitfield()
    {
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x500000AFu,
            PublicWeenieBitfield = 0x8u, // BF_PLAYER only
        });
        ClientObject? fired = null;
        repo.ObjectUpdated += i => fired = i;

        bool ok = repo.UpdateIntProperty(
            0x500000AFu,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        Assert.True(ok);
        Assert.Equal(0x2000008u, repo.Get(0x500000AFu)!.PublicWeenieBitfield);
        Assert.Equal(
            PlayerKillerStatusBitfield.PkLite,
            repo.Get(0x500000AFu)!.Properties.Ints[ClientObjectTable.PlayerKillerStatusPropertyId]);
        Assert.NotNull(fired);
    }

    [Fact]
    public void UpdateIntProperty_playerKillerStatus_nullBitfield_treatsMissingAsZero()
    {
        var repo = new ClientObjectTable();
        repo.AddOrUpdate(new ClientObject { ObjectId = 0x500000B4u });

        Assert.True(repo.UpdateIntProperty(
            0x500000B4u,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.Pk));

        Assert.Equal(0x20u, repo.Get(0x500000B4u)!.PublicWeenieBitfield);
    }

    [Fact]
    public void ClientObject_NewFields_DefaultAndSettable()
    {
        var o = new ClientObject
        {
            ObjectId = 1, WielderId = 0x42u, ItemsCapacity = 24, ContainersCapacity = 7,
            HookItemTypes = (uint)ItemType.Container, HookType = 4u,
            ContainerTypeHint = 1u, Priority = 8u, Structure = 5, MaxStructure = 10, Workmanship = 7.5f,
        };
        o.WeenieClassId = 0xABCDu;   // now settable
        Assert.Equal(0x42u, o.WielderId);
        Assert.Equal(24, o.ItemsCapacity);
        Assert.Equal(7, o.ContainersCapacity);
        Assert.Equal((uint)ItemType.Container, o.HookItemTypes);
        Assert.Equal(4u, o.HookType);
        Assert.True(o.IsHook);
        Assert.Equal(1u, o.ContainerTypeHint);
        Assert.Equal(8u, o.Priority);
        Assert.Equal(5, o.Structure);
        Assert.Equal(10, o.MaxStructure);
        Assert.Equal(7.5f, o.Workmanship);
        Assert.Equal(0xABCDu, o.WeenieClassId);
    }

    [Fact]
    public void IngestAndLiveProperties_PreserveRetailHookIdentity()
    {
        const uint guid = 0x500000AEu;
        var table = new ClientObjectTable();
        ClientObject obj = table.Ingest(
            FullWeenie(guid) with
            {
                HookItemTypes = (uint)ItemType.Container,
                HookType = 4u,
            });

        Assert.True(obj.IsHook);
        Assert.Equal((uint)ItemType.Container, obj.HookItemTypes);
        Assert.Equal(4u, obj.HookType);

        Assert.True(table.UpdateIntProperty(
            guid,
            ClientObjectTable.HookTypePropertyId,
            value: 0));
        Assert.False(obj.IsHook);
        Assert.True(table.UpdateIntProperty(
            guid,
            ClientObjectTable.HookTypePropertyId,
            value: 2));
        Assert.True(table.UpdateIntProperty(
            guid,
            ClientObjectTable.HookItemTypesPropertyId,
            value: (int)ItemType.Misc));
        Assert.True(obj.IsHook);
        Assert.Equal(2u, obj.HookType);
        Assert.Equal((uint)ItemType.Misc, obj.HookItemTypes);
    }

    [Fact]
    public void WeenieData_Construct()
    {
        var d = new WeenieData(Guid: 1, Name: "x", Type: ItemType.Misc, WeenieClassId: 2,
            IconId: 0x06001234u, IconOverlayId: 0, IconUnderlayId: 0, Effects: 0,
            Value: 5, StackSize: 1, StackSizeMax: 1, Burden: 10,
            ContainerId: 0x99u, WielderId: null, ValidLocations: null,
            CurrentWieldedLocation: null, Priority: null,
            ItemsCapacity: null, ContainersCapacity: null,
            Structure: null, MaxStructure: null, Workmanship: null);
        Assert.Equal(0x99u, d.ContainerId);
    }

    [Fact]
    public void Ingest_PetOwnerFromSecondHeader_IsPreserved()
    {
        var table = new ClientObjectTable();
        var data = new WeenieData(
            Guid: 0x50000010u, Name: "Combat Pet", Type: ItemType.Creature,
            WeenieClassId: 2, IconId: 0, IconOverlayId: 0, IconUnderlayId: 0, Effects: 0,
            Value: null, StackSize: null, StackSizeMax: null, Burden: null,
            ContainerId: null, WielderId: null, ValidLocations: null,
            CurrentWieldedLocation: null, Priority: null,
            ItemsCapacity: null, ContainersCapacity: null,
            Structure: null, MaxStructure: null, Workmanship: null,
            PetOwnerId: 0x50000001u);

        ClientObject result = table.Ingest(data);

        Assert.Equal(0x50000001u, result.PetOwnerId);
    }

    private static WeenieData FullWeenie(uint guid, uint icon = 0x06001234u,
        string name = "Sword", ItemType type = ItemType.MeleeWeapon, uint effects = 0,
        int? value = 100, int? stack = 1, uint? container = null, uint wcid = 0xABCDu) =>
        new WeenieData(guid, name, type, wcid, icon, 0, 0, effects,
            value, stack, StackSizeMax: 1, Burden: 10, ContainerId: container,
            WielderId: null, ValidLocations: null, CurrentWieldedLocation: null,
            Priority: null, ItemsCapacity: null, ContainersCapacity: null,
            Structure: null, MaxStructure: null, Workmanship: null);

    [Fact]
    public void Ingest_NewItemWithNoPriorStub_Creates_AndFiresAdded()
    {
        var table = new ClientObjectTable();
        ClientObject? added = null;
        table.ObjectAdded += o => added = o;
        var obj = table.Ingest(FullWeenie(0x500000B0u));
        Assert.NotNull(added);
        Assert.Equal(0x06001234u, table.Get(0x500000B0u)!.IconId);
        Assert.Equal(0xABCDu, obj.WeenieClassId);
    }

    [Fact]
    public void Ingest_Existing_PatchesInPlace_PreservesPropertyBundle()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x500000B1u));
        table.Get(0x500000B1u)!.Properties.Ints[999u] = 7;   // simulate appraise
        ClientObject? updated = null;
        table.ObjectUpdated += o => updated = o;
        table.Ingest(FullWeenie(0x500000B1u, name: "Renamed"));
        Assert.NotNull(updated);
        Assert.Equal("Renamed", table.Get(0x500000B1u)!.Name);
        Assert.Equal(7, table.Get(0x500000B1u)!.Properties.Ints[999u]);
    }

    [Fact]
    public void Ingest_AbsentNullableField_DoesNotClobber()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x500000B2u, value: 100));
        var noValue = FullWeenie(0x500000B2u) with { Value = null };
        table.Ingest(noValue);
        Assert.Equal(100, table.Get(0x500000B2u)!.Value);
    }

    [Fact]
    public void Ingest_Effects_AssignedUnconditionally_ClearsToZero()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x500000B3u, effects: 0x1u));
        Assert.Equal(0x1u, table.Get(0x500000B3u)!.Effects);
        table.Ingest(FullWeenie(0x500000B3u, effects: 0u));
        Assert.Equal(0u, table.Get(0x500000B3u)!.Effects);
    }

    [Fact]
    public void Ingest_UseabilityAndTargetType_AssignsTypedFields()
    {
        var table = new ClientObjectTable();
        var data = FullWeenie(0x500000B7u) with
        {
            Useability = 0x000A0008u,
            TargetType = (uint)ItemType.Creature,
        };

        table.Ingest(data);

        var item = table.Get(0x500000B7u);
        Assert.NotNull(item);
        Assert.Equal(0x000A0008u, item!.Useability);
        Assert.Equal((uint)ItemType.Creature, item.TargetType);
    }

    [Fact]
    public void Ingest_PluralName_PreservesWireValue()
    {
        var table = new ClientObjectTable();

        table.Ingest(FullWeenie(0x500000B9u, name: "Pyreal Scarab", stack: 2) with
        {
            PluralName = "Pyreal Scarabs",
        });

        Assert.Equal("Pyreal Scarabs", table.Get(0x500000B9u)!.PluralName);
        Assert.Equal("Pyreal Scarabs", table.Get(0x500000B9u)!.GetAppropriateName());
    }

    [Fact]
    public void Ingest_MaterialType_PreservesWireValueWithoutClobberingWhenAbsent()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x500000BCu) with { MaterialType = 77u });
        table.Ingest(FullWeenie(0x500000BCu) with { MaterialType = null });

        Assert.Equal(77u, table.Get(0x500000BCu)!.MaterialType);
    }

    [Fact]
    public void Ingest_AmmoType_PreservesWireValueForAutoWieldCompatibility()
    {
        var table = new ClientObjectTable();

        table.Ingest(FullWeenie(0x500000BAu, name: "Bow",
            type: ItemType.MissileWeapon) with
        {
            CombatUse = 2,
            AmmoType = 1,
        });

        Assert.Equal((byte)2, table.Get(0x500000BAu)!.CombatUse);
        Assert.Equal((ushort)1, table.Get(0x500000BAu)!.AmmoType);
    }

    [Fact]
    public void Ingest_RadarMetadata_PatchesOnlyPresentWireFields()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x500000B8u) with
        {
            RadarBlipColor = 4,
            RadarBehavior = 3,
        });

        table.Ingest(FullWeenie(0x500000B8u) with
        {
            RadarBlipColor = null,
            RadarBehavior = 4,
        });

        var item = table.Get(0x500000B8u);
        Assert.NotNull(item);
        Assert.Equal((byte)4, item!.RadarBlipColor);
        Assert.Equal((byte)4, item.RadarBehavior);
    }

    [Fact]
    public void RecordMembership_CreatesEntry_AndSetsEquip()
    {
        var table = new ClientObjectTable();
        table.RecordMembership(0x500000B4u, equip: EquipMask.MeleeWeapon);
        var o = table.Get(0x500000B4u);
        Assert.NotNull(o);
        Assert.Equal(EquipMask.MeleeWeapon, o!.CurrentlyEquippedLocation);
        Assert.Equal(0u, o.IconId);
    }

    [Fact]
    public void RecordMembership_OwnedEquip_UsesSameIndexShapeAsLiveWield()
    {
        const uint playerGuid = 0x50000001u;
        const uint weaponGuid = 0x500000B9u;
        var table = new ClientObjectTable();

        table.RecordMembership(
            weaponGuid,
            containerId: playerGuid,
            equip: EquipMask.MissileWeapon,
            priority: 7u);

        var weapon = table.Get(weaponGuid);
        Assert.NotNull(weapon);
        Assert.Equal(playerGuid, weapon!.ContainerId);
        Assert.Equal(-1, weapon.ContainerSlot);
        Assert.Equal(EquipMask.MissileWeapon, weapon.CurrentlyEquippedLocation);
        Assert.Equal(7u, weapon.Priority);
        Assert.Equal(new[] { weaponGuid }, table.GetContents(playerGuid));
    }

    [Fact]
    public void Ingest_AfterMembership_FillsData_NoDuplicate()
    {
        var table = new ClientObjectTable();
        table.RecordMembership(0x500000B5u);
        table.Ingest(FullWeenie(0x500000B5u));
        Assert.Equal(1, table.ObjectCount);
        Assert.Equal(0x06001234u, table.Get(0x500000B5u)!.IconId);
    }

    [Fact]
    public void Membership_AfterIngest_NoDuplicate_PreservesData()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x500000B6u));
        table.RecordMembership(0x500000B6u, equip: EquipMask.MeleeWeapon);  // then PD manifest
        Assert.Equal(1, table.ObjectCount);
        Assert.Equal(0x06001234u, table.Get(0x500000B6u)!.IconId);
        Assert.Equal(EquipMask.MeleeWeapon, table.Get(0x500000B6u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void RecordMembership_inventoryEntryClearsStaleEquipLocation()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x500000B8u,
            CurrentlyEquippedLocation = EquipMask.ChestWear | EquipMask.UpperArmWear,
        });

        table.RecordMembership(0x500000B8u);

        Assert.Equal(EquipMask.None, table.Get(0x500000B8u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void ContainerIndex_IngestThenContents_OrderedBySlot()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x510u, container: 0xC0u));
        table.Ingest(FullWeenie(0x511u, container: 0xC0u));
        table.MoveItem(0x510u, 0xC0u, newSlot: 1);
        table.MoveItem(0x511u, 0xC0u, newSlot: 0);
        Assert.Equal(new[] { 0x511u, 0x510u }, table.GetContents(0xC0u));
    }

    [Fact]
    public void ContainerIndex_Move_ReparentsBetweenContainers()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x520u, container: 0xC1u));
        table.MoveItem(0x520u, 0xC2u, newSlot: 0);
        Assert.Empty(table.GetContents(0xC1u));
        Assert.Equal(new[] { 0x520u }, table.GetContents(0xC2u));
    }

    [Fact]
    public void ContainerIndex_Remove_DropsFromContents()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x530u, container: 0xC3u));
        table.Remove(0x530u);
        Assert.Empty(table.GetContents(0xC3u));
    }

    [Fact]
    public void GetContents_UnknownContainer_Empty()
    {
        var table = new ClientObjectTable();
        Assert.Empty(table.GetContents(0xDEADu));
    }

    [Fact]
    public void Ingest_CreatureTyped_ResolvesNameAndTypeViaGet()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x560u, name: "Drudge", type: ItemType.Creature));
        var o = table.Get(0x560u);
        Assert.NotNull(o);
        Assert.Equal("Drudge", o!.Name);                       // LiveName(guid) reads this
        Assert.True((o.Type & ItemType.Creature) != 0);
    }

    [Fact]
    public void ReplaceContents_RecordsAllInListOrder()
    {
        var table = new ClientObjectTable();
        table.ReplaceContents(0xC9u, new uint[] { 0xA01u, 0xA02u, 0xA03u });
        Assert.Equal(new[] { 0xA01u, 0xA02u, 0xA03u }, table.GetContents(0xC9u));
        Assert.Equal(0u, table.Get(0xA01u)!.ContainerId);
    }

    [Fact]
    public void ReplaceContents_PublishesListReplacementWithoutSyntheticMove()
    {
        var table = new ClientObjectTable();
        int moves = 0;
        uint replaced = 0u;
        table.ObjectMoved += _ => moves++;
        table.ContainerContentsReplaced += container => replaced = container;

        table.ReplaceContents(0xC9u, new uint[] { 0xA01u });

        Assert.Equal(0, moves);
        Assert.Equal(0xC9u, replaced);
    }

    [Fact]
    public void ProjectionOnlyMember_AuthoritativeMoveEvictsStaleViewedList()
    {
        var table = new ClientObjectTable();
        const uint chest = 0x40000001u, pack = 0x50000001u, item = 0xA20u;
        table.ReplaceContents(chest, new uint[] { item });

        Assert.True(table.ApplyServerMove(item, pack, newWielderId: 0u, newSlot: 0));

        Assert.Empty(table.GetContents(chest));
        Assert.Equal(new[] { item }, table.GetContents(pack));
    }

    [Fact]
    public void BuyShapedFreshGuidCreateThenPlacementZeroEcho_InsertsAtRetailListHead()
    {
        var table = new ClientObjectTable();
        const uint pack = 0x50000001u;
        const uint existingA = 0xA01u;
        const uint existingB = 0xA02u;
        const uint boughtItem = 0xB01u;

        table.InitializeInventoryManifest(pack, new[]
        {
            new ContainerContentEntry(existingA, 0u),
            new ContainerContentEntry(existingB, 0u),
        });

        table.Ingest(new WeenieData(
            Guid: boughtItem,
            Name: "Arrow",
            Type: ItemType.MissileWeapon,
            WeenieClassId: 5u,
            IconId: 0u,
            IconOverlayId: 0u,
            IconUnderlayId: 0u,
            Effects: 0u,
            Value: 100,
            StackSize: 100,
            StackSizeMax: null,
            Burden: 1,
            ContainerId: pack,
            WielderId: 0u,
            ValidLocations: null,
            CurrentWieldedLocation: null,
            Priority: null,
            ItemsCapacity: null,
            ContainersCapacity: null,
            Structure: null,
            MaxStructure: null,
            Workmanship: null));

        Assert.True(table.ApplyConfirmedServerMove(
            boughtItem,
            pack,
            newWielderId: 0u,
            newSlot: 0,
            containerTypeHint: 0u));

        Assert.Equal(
            new[] { boughtItem, existingA, existingB },
            table.GetContents(pack));
    }

    [Fact]
    public void ContainIdArrivingBeforeCreateObject_StillInsertsAtRetailListHead()
    {
        var table = new ClientObjectTable();
        const uint pack = 0x50000001u;
        const uint existingA = 0xA01u;
        const uint existingB = 0xA02u;
        const uint boughtItem = 0xB02u;

        table.InitializeInventoryManifest(pack, new[]
        {
            new ContainerContentEntry(existingA, 0u),
            new ContainerContentEntry(existingB, 0u),
        });

        // The 0x0022 echo arrives FIRST — boughtItem does not exist in the
        // table yet. ApplyConfirmedServerMove itself still reports failure
        // (nothing to move YET) — the fix is that it no longer drops the
        // request on the floor.
        Assert.False(table.ApplyConfirmedServerMove(
            boughtItem,
            pack,
            newWielderId: 0u,
            newSlot: 0,
            containerTypeHint: 0u));

        table.Ingest(new WeenieData(
            Guid: boughtItem,
            Name: "Arrow",
            Type: ItemType.MissileWeapon,
            WeenieClassId: 5u,
            IconId: 0u,
            IconOverlayId: 0u,
            IconUnderlayId: 0u,
            Effects: 0u,
            Value: 100,
            StackSize: 100,
            StackSizeMax: null,
            Burden: 1,
            ContainerId: pack,
            WielderId: 0u,
            ValidLocations: null,
            CurrentWieldedLocation: null,
            Priority: null,
            ItemsCapacity: null,
            ContainersCapacity: null,
            Structure: null,
            MaxStructure: null,
            Workmanship: null));

        Assert.Equal(
            new[] { boughtItem, existingA, existingB },
            table.GetContents(pack));
        Assert.Equal(pack, table.Get(boughtItem)!.ContainerId);
        Assert.Equal(0u, table.Get(boughtItem)!.ContainerTypeHint);
    }

    [Fact]
    public void PendingUnresolvedPlacement_IsDroppedByClear()
    {
        var table = new ClientObjectTable();
        const uint pack = 0x50000001u;
        const uint neverArrives = 0xB03u;

        Assert.False(table.ApplyConfirmedServerMove(
            neverArrives, pack, newWielderId: 0u, newSlot: 0, containerTypeHint: 0u));

        table.Clear();

        table.Ingest(new WeenieData(
            Guid: neverArrives,
            Name: "Something Else",
            Type: ItemType.Misc,
            WeenieClassId: 9u,
            IconId: 0u,
            IconOverlayId: 0u,
            IconUnderlayId: 0u,
            Effects: 0u,
            Value: 1,
            StackSize: null,
            StackSizeMax: null,
            Burden: 1,
            ContainerId: pack,
            WielderId: 0u,
            ValidLocations: null,
            CurrentWieldedLocation: null,
            Priority: null,
            ItemsCapacity: null,
            ContainersCapacity: null,
            Structure: null,
            MaxStructure: null,
            Workmanship: null));

        Assert.Equal(new[] { neverArrives }, table.GetContents(pack));
    }

    [Fact]
    public void AuthoritativePickup_PlacementZeroInsertsAtRetailListHead()
    {
        var table = new ClientObjectTable();
        const uint corpse = 0x40000001u;
        const uint pack = 0x50000001u;
        const uint existingA = 0xA01u;
        const uint existingB = 0xA02u;
        const uint firstLoot = 0xA03u;
        const uint secondLoot = 0xA04u;

        table.InitializeInventoryManifest(pack, new[]
        {
            new ContainerContentEntry(existingA, 0u),
            new ContainerContentEntry(existingB, 0u),
        });
        table.ReplaceContents(corpse, new[] { firstLoot, secondLoot });

        Assert.True(table.ApplyConfirmedServerMove(
            firstLoot,
            pack,
            newWielderId: 0u,
            newSlot: 0,
            containerTypeHint: 0u));
        Assert.Equal(
            new[] { firstLoot, existingA, existingB },
            table.GetContents(pack));
        Assert.Equal(
            new[] { 0, 1, 2 },
            table.GetContents(pack).Select(id => table.Get(id)!.ContainerSlot));

        Assert.True(table.ApplyConfirmedServerMove(
            secondLoot,
            pack,
            newWielderId: 0u,
            newSlot: 0,
            containerTypeHint: 0u));
        Assert.Equal(
            new[] { secondLoot, firstLoot, existingA, existingB },
            table.GetContents(pack));
        Assert.Equal(
            new[] { 0, 1, 2, 3 },
            table.GetContents(pack).Select(id => table.Get(id)!.ContainerSlot));
        Assert.Empty(table.GetContents(corpse));
    }

    [Fact]
    public void AuthoritativeContainerMove_IndexesOnlyTheRetailContainerList()
    {
        var table = new ClientObjectTable();
        const uint pack = 0x50000001u;
        const uint looseA = 0xA11u;
        const uint looseB = 0xA12u;
        const uint bagA = 0xA21u;
        const uint bagB = 0xA22u;

        table.InitializeInventoryManifest(pack, new[]
        {
            new ContainerContentEntry(looseA, 0u),
            new ContainerContentEntry(bagA, 1u),
            new ContainerContentEntry(looseB, 0u),
        });
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = bagB,
            Type = ItemType.Container,
            ItemsCapacity = 24,
        });

        Assert.True(table.ApplyConfirmedServerMove(
            bagB,
            pack,
            newWielderId: 0u,
            newSlot: 0,
            containerTypeHint: 1u));

        uint[] loose = table.GetContents(pack)
            .Where(id => table.Get(id)!.ContainerTypeHint == 0u
                && (table.Get(id)!.Type & ItemType.Container) == 0)
            .ToArray();
        uint[] bags = table.GetContents(pack)
            .Where(id => table.Get(id)!.ContainerTypeHint != 0u
                || (table.Get(id)!.Type & ItemType.Container) != 0)
            .ToArray();
        Assert.Equal(new[] { looseA, looseB }, loose);
        Assert.Equal(new[] { bagB, bagA }, bags);
        Assert.Equal(0, table.Get(bagB)!.ContainerSlot);
        Assert.Equal(1, table.Get(bagA)!.ContainerSlot);
    }

    [Fact]
    public void ProjectionEvictionNotification_ReentrantMoveSeesCommittedState()
    {
        var table = new ClientObjectTable();
        const uint chest = 0x40000001u, packA = 0x50000001u;
        const uint packB = 0x50000002u, item = 0xA22u;
        table.ReplaceContents(chest, new uint[] { item });
        table.ContainerContentsReplaced += containerId =>
        {
            if (containerId == chest)
                Assert.True(table.ApplyServerMove(item, packB, newWielderId: 0u, newSlot: 0));
        };

        Assert.True(table.ApplyServerMove(item, packA, newWielderId: 0u, newSlot: 0));

        Assert.Empty(table.GetContents(chest));
        Assert.Empty(table.GetContents(packA));
        Assert.Equal(new[] { item }, table.GetContents(packB));
        Assert.Equal(packB, table.Get(item)!.ContainerId);
    }

    [Fact]
    public void ProjectionOnlyMember_DeleteEvictsStaleViewedListAndNotifies()
    {
        var table = new ClientObjectTable();
        const uint chest = 0x40000001u, item = 0xA21u;
        table.ReplaceContents(chest, new uint[] { item });
        int replacements = 0;
        table.ContainerContentsReplaced += id =>
        {
            if (id == chest) replacements++;
        };

        Assert.True(table.Remove(item));

        Assert.Empty(table.GetContents(chest));
        Assert.Equal(1, replacements);
    }

    [Fact]
    public void DeleteProjectionNotification_GuidReuseSurvivesCompletedTeardown()
    {
        var table = new ClientObjectTable();
        const uint chest = 0x40000001u, item = 0xA23u, player = 0x50000001u;
        table.ReplaceContents(chest, new uint[] { item });
        table.ContainerContentsReplaced += containerId =>
        {
            if (containerId != chest) return;
            table.AddOrUpdate(new ClientObject
            {
                ObjectId = item,
                WielderId = player,
                CurrentlyEquippedLocation = EquipMask.MissileWeapon,
            });
        };

        Assert.True(table.Remove(item));

        Assert.NotNull(table.Get(item));
        Assert.Equal(
            new[] { item },
            table.GetEquippedBy(player).Select(equipped => equipped.ObjectId));
    }

    [Fact]
    public void ReplaceContents_SecondSmallerList_ReplacesProjectionOnly()
    {
        var table = new ClientObjectTable();
        table.ReplaceContents(0xC9u, new uint[] { 0xA01u, 0xA02u, 0xA03u });
        table.ReplaceContents(0xC9u, new uint[] { 0xA02u });   // A01 + A03 removed server-side
        Assert.Equal(new[] { 0xA02u }, table.GetContents(0xC9u));
        Assert.Equal(0u, table.Get(0xA01u)!.ContainerId);
        Assert.Equal(0u, table.Get(0xA03u)!.ContainerId);
        Assert.Equal(0u, table.Get(0xA02u)!.ContainerId);
    }

    [Fact]
    public void ReplaceContents_listedMemberPreservesCanonicalPlacement()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xA10u,
            ContainerId = 0u,
            WielderId = 0x50000001u,
            CurrentlyEquippedLocation = EquipMask.ChestWear | EquipMask.UpperArmWear,
        });

        table.ReplaceContents(0x50000001u, new uint[] { 0xA10u });

        var item = table.Get(0xA10u)!;
        Assert.Equal(0u, item.ContainerId);
        Assert.Equal(0x50000001u, item.WielderId);
        Assert.Equal(-1, item.ContainerSlot);
        Assert.Equal(
            EquipMask.ChestWear | EquipMask.UpperArmWear,
            item.CurrentlyEquippedLocation);
    }

    [Fact]
    public void ReplaceContents_omittedMemberPreservesCanonicalPlacement()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = 0xA11u,
            WielderId = 0x50000001u,
        });
        table.MoveItem(0xA11u, 0x50000001u, newSlot: 0,
            newEquipLocation: EquipMask.ChestWear | EquipMask.UpperArmWear);

        table.ReplaceContents(0x50000001u, Array.Empty<uint>());

        var item = table.Get(0xA11u)!;
        Assert.Equal(0x50000001u, item.ContainerId);
        Assert.Equal(0x50000001u, item.WielderId);
        Assert.Equal(
            EquipMask.ChestWear | EquipMask.UpperArmWear,
            item.CurrentlyEquippedLocation);
    }

    [Fact]
    public void ReplaceContents_ReordersToNewListOrder()
    {
        var table = new ClientObjectTable();
        table.ReplaceContents(0xC9u, new uint[] { 0xA01u, 0xA02u });
        table.ReplaceContents(0xC9u, new uint[] { 0xA02u, 0xA01u });
        Assert.Equal(new[] { 0xA02u, 0xA01u }, table.GetContents(0xC9u));
    }

    [Fact]
    public void ReplaceContents_RecordsContainerTypeHintAndOrder()
    {
        var table = new ClientObjectTable();
        table.ReplaceContents(0xC9u, new[]
        {
            new ContainerContentEntry(0xA02u, 1u),
            new ContainerContentEntry(0xA01u, 0u),
        });

        Assert.Equal(new[] { 0xA02u, 0xA01u }, table.GetContents(0xC9u));
        Assert.Equal(1u, table.Get(0xA02u)!.ContainerTypeHint);
        Assert.Equal(0u, table.Get(0xA01u)!.ContainerTypeHint);
    }

    [Fact]
    public void ReplaceContents_ManifestOrderSurvivesLaterCreateObjects()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;

        table.ReplaceContents(player, new[]
        {
            new ContainerContentEntry(0xA01u, 0u),
            new ContainerContentEntry(0xA02u, 1u),
        });
        table.Ingest(FullWeenie(0xA02u, container: player, type: ItemType.Container));
        table.Ingest(FullWeenie(0xA01u, container: player));

        Assert.Equal(new[] { 0xA01u, 0xA02u }, table.GetContents(player));
        Assert.Equal(-1, table.Get(0xA01u)!.ContainerSlot);
        Assert.Equal(-1, table.Get(0xA02u)!.ContainerSlot);
        Assert.Equal(1u, table.Get(0xA02u)!.ContainerTypeHint);
    }

    [Fact]
    public void InitializeInventoryManifest_SeedsCanonicalOwnershipAndOrder()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;

        table.InitializeInventoryManifest(player, new[]
        {
            new ContainerContentEntry(0xA01u, 0u),
            new ContainerContentEntry(0xA02u, 1u),
        });

        Assert.Equal(new[] { 0xA01u, 0xA02u }, table.GetContents(player));
        Assert.Equal(player, table.Get(0xA01u)!.ContainerId);
        Assert.Equal(0, table.Get(0xA01u)!.ContainerSlot);
        Assert.Equal(player, table.Get(0xA02u)!.ContainerId);
        Assert.Equal(1, table.Get(0xA02u)!.ContainerSlot);
    }

    [Fact]
    public void InitializeEquipmentManifest_PreservesPackedOrderAndCanonicalOwnership()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;

        table.InitializeEquipmentManifest(player, new[]
        {
            new EquipmentManifestEntry(0xA11u, EquipMask.MeleeWeapon, 1u),
            new EquipmentManifestEntry(0xA12u, EquipMask.MissileAmmo, 2u),
        });

        Assert.Equal(
            new[] { 0xA11u, 0xA12u },
            table.GetEquippedBy(player).Select(item => item.ObjectId));
        Assert.Equal(0u, table.Get(0xA11u)!.ContainerId);
        Assert.Equal(player, table.Get(0xA11u)!.WielderId);
        Assert.Empty(table.GetContents(player));
    }

    [Fact]
    public void InitializeEquipmentManifest_ReplacementClearsOmittedCanonicalEquipment()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;
        const uint weapon = 0xA11u;

        table.InitializeEquipmentManifest(player, new[]
        {
            new EquipmentManifestEntry(weapon, EquipMask.MeleeWeapon, 1u),
        });
        table.Get(weapon)!.Burden = 10;

        table.InitializeEquipmentManifest(player, Array.Empty<EquipmentManifestEntry>());

        ClientObject item = table.Get(weapon)!;
        Assert.Equal(0u, item.WielderId);
        Assert.Equal(0u, item.ContainerId);
        Assert.Equal(-1, item.ContainerSlot);
        Assert.Equal(EquipMask.None, item.CurrentlyEquippedLocation);
        Assert.Empty(table.GetEquippedBy(player));
        Assert.Equal(0, table.SumCarriedBurden(player));
    }

    [Fact]
    public void InitializeInventoryManifest_ReplacementClearsOmittedCanonicalOwnership()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;
        const uint itemId = 0xA01u;

        table.InitializeInventoryManifest(player, new[]
        {
            new ContainerContentEntry(itemId, 0u),
        });
        table.Get(itemId)!.Burden = 10;

        table.InitializeInventoryManifest(player, Array.Empty<ContainerContentEntry>());

        ClientObject item = table.Get(itemId)!;
        Assert.Equal(0u, item.ContainerId);
        Assert.Equal(-1, item.ContainerSlot);
        Assert.Equal(0u, item.WielderId);
        Assert.Equal(EquipMask.None, item.CurrentlyEquippedLocation);
        Assert.Empty(table.GetContents(player));
        Assert.Equal(0, table.SumCarriedBurden(player));
    }

    [Fact]
    public void PlayerDescriptionManifests_MoveItemBetweenInventoryAndEquipment()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;
        const uint weapon = 0xA11u;

        table.InitializeInventoryManifest(player, new[]
        {
            new ContainerContentEntry(weapon, 0u),
        });
        table.InitializeEquipmentManifest(player, new[]
        {
            new EquipmentManifestEntry(weapon, EquipMask.MeleeWeapon, 1u),
        });

        Assert.Empty(table.GetContents(player));
        Assert.Equal(player, table.Get(weapon)!.WielderId);
        Assert.Equal(EquipMask.MeleeWeapon, table.Get(weapon)!.CurrentlyEquippedLocation);

        table.InitializeEquipmentManifest(player, Array.Empty<EquipmentManifestEntry>());
        table.InitializeInventoryManifest(player, new[]
        {
            new ContainerContentEntry(weapon, 0u),
        });

        Assert.Empty(table.GetEquippedBy(player));
        Assert.Equal(new[] { weapon }, table.GetContents(player));
        Assert.Equal(player, table.Get(weapon)!.ContainerId);
        Assert.Equal(0u, table.Get(weapon)!.WielderId);
    }

    [Fact]
    public void Reindex_UnknownSlotAppendsAfterKnownSnapshotSlots()
    {
        var table = new ClientObjectTable();
        const uint pack = 0x500000C9u;

        table.ReplaceContents(pack, new uint[] { 0xA01u, 0xA02u });
        table.Ingest(FullWeenie(0xA03u, container: pack));

        Assert.Equal(new[] { 0xA01u, 0xA02u, 0xA03u }, table.GetContents(pack));
        Assert.Equal(-1, table.Get(0xA03u)!.ContainerSlot);
    }

    [Fact]
    public void ReplaceContents_ZeroContainer_NoOp()
    {
        var table = new ClientObjectTable();
        table.ReplaceContents(0u, new uint[] { 0xA01u });
        Assert.Empty(table.GetContents(0u));
        Assert.Null(table.Get(0xA01u));
    }

    [Fact]
    public void MoveItemOptimistic_movesAndRemembersPreMove()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x700u, container: 0xC0u)); table.MoveItem(0x700u, 0xC0u, newSlot: 3);
        table.MoveItemOptimistic(0x700u, 0xC1u, 0);
        Assert.Equal(0xC1u, table.Get(0x700u)!.ContainerId);   // moved now (instant)
        Assert.True(table.RollbackMove(0x700u));                // pre-move was remembered
        Assert.Equal(0xC0u, table.Get(0x700u)!.ContainerId);
        Assert.Equal(3, table.Get(0x700u)!.ContainerSlot);     // and slot
    }

    [Fact]
    public void ConfirmMove_clearsPending_soRollbackIsNoOp()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x701u, container: 0xC0u));
        table.MoveItemOptimistic(0x701u, 0xC1u, 0);
        table.ConfirmMove(0x701u);
        Assert.False(table.RollbackMove(0x701u));              // nothing pending → no rollback
        Assert.Equal(0xC1u, table.Get(0x701u)!.ContainerId);
    }

    [Fact]
    public void MoveItemOptimistic_twice_keepsTheOriginalPreMove()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x702u, container: 0xC0u));
        table.MoveItemOptimistic(0x702u, 0xC1u, 0);
        table.MoveItemOptimistic(0x702u, 0xC2u, 0);
        table.RollbackMove(0x702u);
        Assert.Equal(0xC0u, table.Get(0x702u)!.ContainerId);   // rolls back to the FIRST pre-move
    }

    [Fact]
    public void RollbackMove_unknownItem_false()
        => Assert.False(new ClientObjectTable().RollbackMove(0xDEADu));

    [Fact]
    public void MoveItemOptimistic_twice_oneConfirm_stillRollsBack()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x910u, container: 0xC0u)); table.MoveItem(0x910u, 0xC0u, 5);
        table.MoveItemOptimistic(0x910u, 0xC1u, 0);   // move 1 (outstanding 1, snapshot C0/5)
        table.MoveItemOptimistic(0x910u, 0xC2u, 0);   // move 2 (outstanding 2)
        table.ConfirmMove(0x910u);
        Assert.True(table.RollbackMove(0x910u));        // move 2 rejected → can still roll back (not stranded at C2)
        Assert.Equal(0xC0u, table.Get(0x910u)!.ContainerId);
        Assert.Equal(5, table.Get(0x910u)!.ContainerSlot);
    }

    [Fact]
    public void Clear_dropsPendingMoves_soRecycledGuidIsntMisRolledBack() // I2: pending must not survive a table flush
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x911u, container: 0xC0u));
        table.MoveItemOptimistic(0x911u, 0xC1u, 0);   // pending snapshot for 0x911
        table.Clear();                                 // teleport/logoff flushes the table

        table.Ingest(FullWeenie(0x911u, container: 0xC5u));   // a recycled guid reappears fresh
        Assert.False(table.RollbackMove(0x911u));       // no stale pending → no mis-rollback
        Assert.Equal(0xC5u, table.Get(0x911u)!.ContainerId);
    }

    [Fact]
    public void MoveItemOptimistic_insertBefore_shiftsOthers_keepsGaplessOrder()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x920u, container: 0xC0u)); table.MoveItem(0x920u, 0xC0u, 0);
        table.Ingest(FullWeenie(0x921u, container: 0xC0u)); table.MoveItem(0x921u, 0xC0u, 1);
        table.Ingest(FullWeenie(0x922u, container: 0xC0u)); table.MoveItem(0x922u, 0xC0u, 2);

        // Move 0x922 (index 2) to index 0 → order [0x922, 0x920, 0x921], slots renumbered 0,1,2 (no ties,
        // no reshuffle). A raw slot-set would leave 0x922 + 0x920 both at slot 0 → unstable sort.
        table.MoveItemOptimistic(0x922u, 0xC0u, 0);
        Assert.Equal(new[] { 0x922u, 0x920u, 0x921u }, table.GetContents(0xC0u));
        Assert.Equal(0, table.Get(0x922u)!.ContainerSlot);
        Assert.Equal(1, table.Get(0x920u)!.ContainerSlot);
        Assert.Equal(2, table.Get(0x921u)!.ContainerSlot);
    }

    [Fact]
    public void ContainerIndex_SlotChange_ResortsInPlace()
    {
        var table = new ClientObjectTable();
        table.Ingest(FullWeenie(0x540u, container: 0xC4u));
        table.Ingest(FullWeenie(0x541u, container: 0xC4u));
        table.MoveItem(0x540u, 0xC4u, newSlot: 0);
        table.MoveItem(0x541u, 0xC4u, newSlot: 1);
        Assert.Equal(new[] { 0x540u, 0x541u }, table.GetContents(0xC4u));
        table.MoveItem(0x540u, 0xC4u, newSlot: 5);
        Assert.Equal(new[] { 0x541u, 0x540u }, table.GetContents(0xC4u));
        Assert.Equal(2, table.GetContents(0xC4u).Count);
    }

    [Fact]
    public void WieldItemOptimistic_equipsInstantly_setsContainerAndEquip()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u, pack = 0x40000005u;
        table.AddOrUpdate(new ClientObject { ObjectId = 0x940u });
        table.MoveItem(0x940u, pack, newSlot: 2);                     // a pack item (WielderId stays 0)
        table.WieldItemOptimistic(0x940u, player, EquipMask.HeadWear);
        var o = table.Get(0x940u)!;
        Assert.Equal(EquipMask.HeadWear, o.CurrentlyEquippedLocation); // equipped now (instant)
        Assert.Equal(player, o.ContainerId);
        Assert.Equal(0u, o.WielderId);
    }

    [Fact]
    public void WieldItemOptimistic_rollback_unequips_andReturnsToPack()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u, pack = 0x40000005u;
        table.AddOrUpdate(new ClientObject { ObjectId = 0x941u });
        table.MoveItem(0x941u, pack, newSlot: 2);
        table.WieldItemOptimistic(0x941u, player, EquipMask.HeadWear);
        Assert.True(table.RollbackMove(0x941u));                       // server rejected the wield
        var o = table.Get(0x941u)!;
        Assert.Equal(EquipMask.None, o.CurrentlyEquippedLocation);     // un-equipped
        Assert.Equal(pack, o.ContainerId);                            // back in the pack
        Assert.Equal(2, o.ContainerSlot);                             // at its original slot
    }

    [Fact]
    public void RollbackMove_restoresPreMoveEquipLocation() // unwield-reject must snap back to EQUIPPED
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;
        ClientObject? rolledBack = null;
        table.MoveRolledBack += item => rolledBack = item;
        table.AddOrUpdate(new ClientObject { ObjectId = 0x942u });
        table.MoveItem(0x942u, player, newSlot: -1, newEquipLocation: EquipMask.Shield); // equipped
        table.MoveItemOptimistic(0x942u, player, 0);
        Assert.Equal(EquipMask.None, table.Get(0x942u)!.CurrentlyEquippedLocation);
        Assert.True(table.RollbackMove(0x942u));                       // server rejected the unwield
        Assert.Equal(EquipMask.Shield, table.Get(0x942u)!.CurrentlyEquippedLocation); // restored to equipped
        Assert.Same(table.Get(0x942u), rolledBack);
    }

    [Fact]
    public void WieldItemOptimistic_unknownItem_false()
        => Assert.False(new ClientObjectTable().WieldItemOptimistic(0xDEADu, 0x1u, EquipMask.HeadWear));

    [Fact]
    public void ApplyServerMove_UpdatesAuthoritativeWielderAcrossWieldAndUnwield()
    {
        var table = new ClientObjectTable();
        const uint item = 0x9430u, player = 0x50000001u, pack = 0x40000005u;
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            ContainerId = player,
            WielderId = player,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
        });

        Assert.True(table.ApplyServerMove(item, pack, newWielderId: 0u, newSlot: 2));
        Assert.Equal(pack, table.Get(item)!.ContainerId);
        Assert.Equal(0u, table.Get(item)!.WielderId);
        Assert.Equal(EquipMask.None, table.Get(item)!.CurrentlyEquippedLocation);

        Assert.True(table.ApplyServerMove(
            item,
            newContainerId: 0u,
            newWielderId: player,
            newEquipLocation: EquipMask.MissileWeapon));
        Assert.Equal(0u, table.Get(item)!.ContainerId);
        Assert.Equal(player, table.Get(item)!.WielderId);
        Assert.Equal(EquipMask.MissileWeapon, table.Get(item)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void ApplyConfirmedServerWield_ReentrantMoveKeepsNewPendingRequest()
    {
        var table = new ClientObjectTable();
        const uint item = 0x9431u, player = 0x50000001u, pack = 0x40000005u;
        table.AddOrUpdate(new ClientObject { ObjectId = item, ContainerId = pack, ContainerSlot = 0 });
        Assert.True(table.WieldItemOptimistic(item, player, EquipMask.MissileWeapon));

        bool queuedFromNotification = false;
        table.ObjectMoved += move =>
        {
            if (queuedFromNotification || move.Current.WielderId != player) return;
            queuedFromNotification = true;
            Assert.True(table.MoveItemOptimistic(item, pack, newSlot: 0));
        };

        Assert.True(table.ApplyConfirmedServerWield(item, player, EquipMask.MissileWeapon));
        Assert.True(queuedFromNotification);
        Assert.True(table.RollbackMove(item));
        Assert.Equal(0u, table.Get(item)!.ContainerId);
        Assert.Equal(player, table.Get(item)!.WielderId);
        Assert.Equal(EquipMask.MissileWeapon, table.Get(item)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void GetEquippedBy_PreservesRetailHeadInsertionOrder()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u;
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x9432u,
            ContainerId = 0u,
            WielderId = player,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
        });
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x9433u,
            ContainerId = player,
            WielderId = 0u,
            CurrentlyEquippedLocation = EquipMask.MissileAmmo,
        });
        table.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x9434u,
            ContainerId = player,
            CurrentlyEquippedLocation = EquipMask.None,
        });

        Assert.Equal(
            new[] { 0x9433u, 0x9432u },
            table.GetEquippedBy(player).Select(item => item.ObjectId));
    }

    [Fact]
    public void RejectMove_withoutOptimisticMutation_stillPublishesServerFailure()
    {
        var table = new ClientObjectTable();
        MoveRequestFailure? seen = null;
        table.MoveRequestFailed += failure => seen = failure;

        Assert.False(table.RejectMove(0xDEADu, 0x04FFu));

        Assert.Equal(
            new MoveRequestFailure(0xDEADu, 0x04FFu, RolledBack: false),
            seen);
    }

    [Fact]
    public void RejectMove_withOptimisticMutation_rollsBackAndPublishesFailure()
    {
        var table = new ClientObjectTable();
        const uint item = 0x943u, player = 0x50000001u, pack = 0x40000005u;
        table.AddOrUpdate(new ClientObject { ObjectId = item });
        table.MoveItem(item, pack, 2);
        table.WieldItemOptimistic(item, player, EquipMask.MeleeWeapon);
        MoveRequestFailure? seen = null;
        table.MoveRequestFailed += failure => seen = failure;

        Assert.True(table.RejectMove(item, 0x04FFu));

        Assert.Equal(pack, table.Get(item)!.ContainerId);
        Assert.Equal(EquipMask.None, table.Get(item)!.CurrentlyEquippedLocation);
        Assert.Equal(
            new MoveRequestFailure(item, 0x04FFu, RolledBack: true),
            seen);
    }

    [Fact]
    public void ApplyConfirmedServerWield_publishesIndependentlyOfRollbackOutstandingCount()
    {
        var table = new ClientObjectTable();
        const uint item = 0x944u, player = 0x50000001u, pack = 0x40000005u;
        table.AddOrUpdate(new ClientObject { ObjectId = item });
        table.MoveItem(item, pack, 2);
        table.WieldItemOptimistic(item, player, EquipMask.MeleeWeapon);
        table.MoveItemOptimistic(item, pack, 0);
        var confirmed = new List<uint>();
        table.WieldConfirmed += itemId => confirmed.Add(itemId);

        Assert.True(table.ApplyConfirmedServerWield(
            item,
            player,
            EquipMask.MeleeWeapon));

        Assert.Equal(new[] { item }, confirmed);
        Assert.Equal(0u, table.Get(item)!.ContainerId);
        Assert.Equal(player, table.Get(item)!.WielderId);
        Assert.True(table.RollbackMove(item));
        Assert.Equal(pack, table.Get(item)!.ContainerId);
        Assert.Equal(0u, table.Get(item)!.WielderId);
        Assert.Equal(EquipMask.None, table.Get(item)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void WieldThenMove_oneConfirm_rollsBackToPreWield()
    {
        var table = new ClientObjectTable();
        const uint player = 0x50000001u, pack = 0x40000005u, pack2 = 0x40000006u;
        table.AddOrUpdate(new ClientObject { ObjectId = 0x950u });
        table.MoveItem(0x950u, pack, newSlot: 2);                      // pre-wield: pack slot 2
        table.WieldItemOptimistic(0x950u, player, EquipMask.HeadWear); // outstanding 1, snapshot (pack, 2, None)
        table.MoveItemOptimistic(0x950u, pack2, 0);                    // outstanding 2 (same snapshot)
        table.ConfirmMove(0x950u);
        Assert.True(table.RollbackMove(0x950u));                       // the second rejected → roll back
        var o = table.Get(0x950u)!;
        Assert.Equal(pack, o.ContainerId);
        Assert.Equal(2, o.ContainerSlot);                             // and slot
        Assert.Equal(EquipMask.None, o.CurrentlyEquippedLocation);    // pre-wield was un-equipped
    }

    [Fact]
    public void UpdateInt64Property_setsValueAndFiresUpdated()
    {
        var table = new ClientObjectTable();
        table.AddOrUpdate(new ClientObject { ObjectId = 0x960u });
        int updates = 0;
        table.ObjectUpdated += _ => updates++;

        Assert.True(table.UpdateInt64Property(0x960u, 2u, 12_345L));

        Assert.Equal(12_345L, table.Get(0x960u)!.Properties.Int64s[2u]);
        Assert.Equal(1, updates);
    }

    [Fact]
    public void UpdateInt64Property_unknownObject_false()
        => Assert.False(new ClientObjectTable().UpdateInt64Property(0xDEADu, 2u, 1L));
}
