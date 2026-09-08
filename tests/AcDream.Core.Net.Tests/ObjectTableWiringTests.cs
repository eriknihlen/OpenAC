using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;

namespace AcDream.Core.Net.Tests;

public sealed class ObjectTableWiringTests
{
    [Fact]
    public void ToWeenieData_CopiesFieldsFromSpawn()
    {
        // Every EntitySpawn item field is set to a DISTINCT recognisable value so
        // a positional transposition in ObjectTableWiring.ToWeenieData would trip
        // at least one Assert. Every mapped WeenieData field is verified below.
        var spawn = new WorldSession.EntitySpawn(
            Guid: 0x00000600u,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: System.Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: System.Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: System.Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "Iron Sword",
            ItemType: (uint)ItemType.MeleeWeapon,
            MotionState: null,
            MotionTableId: null)
            with
            {
                WeenieClassId             = 0x00001001u,
                IconId                    = 0x06001111u,
                IconOverlayId             = 0x06002222u,
                IconUnderlayId            = 0x06003333u,
                UiEffects                 = 0x00000004u,
                Value                     = 7,
                StackSize                 = 1,
                StackSizeMax              = 1,
                Burden                    = 300,
                ContainerId               = 0x000000C9u,
                WielderId                 = 0x000000DAu,
                ValidLocations            = 0x00000012u,   // MeleeWeapon wield mask
                CurrentWieldedLocation    = 0x00000002u,   // right-hand
                Priority                  = 0x00000005u,
                ItemsCapacity             = 0,
                ContainersCapacity        = 0,
                HookItemTypes             = (uint)ItemType.Container,
                HookType                  = 4u,
                Structure                 = 80,
                MaxStructure              = 100,
                Workmanship               = 4.5f,
                Useability                = 0x000A0008u,
                TargetType                = (uint)ItemType.Creature,
                RadarBlipColor            = 4,
                RadarBehavior             = 3,
                ObjectDescriptionFlags    = 0x20800200u,
                CombatUse                 = 2,
                AmmoType                  = 1,
                PluralName                = "Iron Swords",
                PetOwnerId                = 0x50000001u,
                CooldownId                = 42u,
                CooldownDuration          = 30d,
                MaterialType              = 77u,
            };

        var d = ObjectTableWiring.ToWeenieData(spawn);

        // --- identity ---
        Assert.Equal(0x00000600u, d.Guid);
        Assert.Equal("Iron Sword",             d.Name);
        Assert.Equal(ItemType.MeleeWeapon,     d.Type);

        // --- weenie / icon ---
        Assert.Equal(0x00001001u,              d.WeenieClassId);
        Assert.Equal(0x06001111u,              d.IconId);
        Assert.Equal(0x06002222u,              d.IconOverlayId);
        Assert.Equal(0x06003333u,              d.IconUnderlayId);
        Assert.Equal(0x00000004u,              d.Effects);

        // --- quantity / economy ---
        Assert.Equal(7,                        d.Value);
        Assert.Equal(1,                        d.StackSize);
        Assert.Equal(1,                        d.StackSizeMax);
        Assert.Equal(300,                      d.Burden);

        Assert.Equal(0x000000C9u,              d.ContainerId);
        Assert.Equal(0x000000DAu,              d.WielderId);

        // --- equip masks ---
        Assert.Equal(0x00000012u,              d.ValidLocations);
        Assert.Equal(0x00000002u,              d.CurrentWieldedLocation);
        Assert.Equal(0x00000005u,              d.Priority);

        Assert.Equal(0,                        d.ItemsCapacity);
        Assert.Equal(0,                        d.ContainersCapacity);
        Assert.Equal((uint)ItemType.Container, d.HookItemTypes);
        Assert.Equal(4u,                       d.HookType);

        // --- durability ---
        Assert.Equal(80,                       d.Structure);
        Assert.Equal(100,                      d.MaxStructure);
        Assert.Equal(4.5f,                     d.Workmanship);
        Assert.Equal(0x000A0008u,              d.Useability);
        Assert.Equal((uint)ItemType.Creature,  d.TargetType);
        Assert.Equal((byte)4,                  d.RadarBlipColor);
        Assert.Equal((byte)3,                  d.RadarBehavior);
        Assert.Equal(0x20800200u,              d.PublicWeenieBitfield);
        Assert.Equal((byte)2,                  d.CombatUse);
        Assert.Equal((ushort)1,                d.AmmoType);
        Assert.Equal("Iron Swords",            d.PluralName);
        Assert.Equal(0x50000001u,              d.PetOwnerId);
        Assert.Equal(42u,                      d.CooldownId);
        Assert.Equal(30d,                      d.CooldownDuration);
        Assert.Equal(77u,                      d.MaterialType);
    }

    [Fact]
    public void ApplyPlayerInt64PropertyUpdate_SynchronizesBothPlayerProjections()
    {
        const uint playerGuid = 0x50000001u;
        var table = new ClientObjectTable();
        var playerObject = new ClientObject { ObjectId = playerGuid };
        playerObject.Properties.Int64s[2u] = 0L;
        table.AddOrUpdate(playerObject);

        var localPlayer = new LocalPlayerState();
        var loginProperties = new PropertyBundle();
        loginProperties.Int64s[2u] = 0L;
        localPlayer.OnProperties(loginProperties);

        int tableChanges = 0;
        int playerChanges = 0;
        table.ObjectUpdated += _ => tableChanges++;
        localPlayer.CharacterChanged += () => playerChanges++;

        ObjectTableWiring.ApplyPlayerInt64PropertyUpdate(
            table,
            localPlayer,
            playerGuid,
            new WorldSession.PlayerInt64PropertyUpdate(2u, 75_000L));

        Assert.Equal(75_000L, table.Get(playerGuid)!.Properties.GetInt64(2u));
        Assert.Equal(75_000L, localPlayer.Properties.GetInt64(2u));
        Assert.Equal(1, tableChanges);
        Assert.Equal(1, playerChanges);
    }

    private static WeenieData MinimalWeenie(uint guid) => new(
        Guid: guid,
        Name: "Test Item",
        Type: null,
        WeenieClassId: 1u,
        IconId: 0x06000001u,
        IconOverlayId: 0u,
        IconUnderlayId: 0u,
        Effects: 0u,
        Value: null,
        StackSize: null,
        StackSizeMax: null,
        Burden: null,
        ContainerId: null,
        WielderId: null,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null);

    [Fact]
    public void EntityDeleted_EvictsWeenieFromTable()
    {
        var table = new ClientObjectTable();
        const uint guid = 0x80000200u;
        table.Ingest(MinimalWeenie(guid));
        Assert.NotNull(table.Get(guid));

        // Fire EntityDeleted as WorldSession does for DeleteObject (0xF747).
        ObjectTableWiring.ApplyEntityDelete(
            table, new DeleteObject.Parsed(guid, 0));

        Assert.Null(table.Get(guid));
    }

    [Fact]
    public void NewGenerationCreateObject_ReplacesPriorQualities()
    {
        const uint guid = 0x80000300u;
        var table = new ClientObjectTable();
        ClientObject old = table.Ingest(MinimalWeenie(guid));
        old.Properties.Ints[123u] = 456;

        var stale = new WorldSession.EntitySpawn(
            Guid: guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: "Stale Name",
            ItemType: null,
            MotionState: null,
            MotionTableId: null);

        int removed = 0;
        int generationRemoved = 0;
        table.ObjectRemoved += _ => removed++;
        table.ObjectRemovalClassified += removal =>
        {
            if (removal.Reason == ClientObjectRemovalReason.GenerationReplacement)
                generationRemoved++;
        };

        ObjectTableWiring.ApplyEntitySpawn(table, stale, replaceGeneration: true);

        Assert.Equal("Stale Name", table.Get(guid)!.Name);
        Assert.False(table.Get(guid)!.Properties.Ints.ContainsKey(123u));
        Assert.Equal(1, removed);
        Assert.Equal(1, generationRemoved);
    }

    [Fact]
    public void SameGenerationCreateObject_MergesPriorQualities()
    {
        const uint guid = 0x80000400u;
        var table = new ClientObjectTable();
        ClientObject old = table.Ingest(MinimalWeenie(guid));
        old.Properties.Ints[123u] = 456;

        var refresh = new WorldSession.EntitySpawn(
            Guid: guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: "Refreshed Name",
            ItemType: null,
            MotionState: null,
            MotionTableId: null);

        ObjectTableWiring.ApplyEntitySpawn(table, refresh);

        Assert.NotNull(table.Get(guid));
        Assert.Equal("Refreshed Name", table.Get(guid)!.Name);
        Assert.Equal(456, table.Get(guid)!.Properties.Ints[123u]);
    }

    [Fact]
    public void GenerationReplacement_InvalidatedByRemovalObserver_DoesNotInstallOuterQualities()
    {
        const uint guid = 0x80000500u;
        var table = new ClientObjectTable();
        table.Ingest(MinimalWeenie(guid));
        bool accepting = true;
        table.ObjectRemovalClassified += removal =>
        {
            if (removal.Reason is not ClientObjectRemovalReason.GenerationReplacement)
                return;

            table.Ingest(MinimalWeenie(guid) with { Name = "Nested Newer" });
            accepting = false;
        };
        var outer = new WorldSession.EntitySpawn(
            Guid: guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: "Outer Older",
            ItemType: null,
            MotionState: null,
            MotionTableId: null);

        bool applied = ObjectTableWiring.ApplyEntitySpawn(
            table,
            outer,
            replaceGeneration: true,
            accepting: () => accepting);

        Assert.False(applied);
        Assert.Equal("Nested Newer", table.Get(guid)!.Name);
    }
}
