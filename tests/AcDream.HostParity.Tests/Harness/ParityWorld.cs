using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The little world a scenario stages, written once and handed to both arms
/// so the two clients are looking at exactly the same thing when they are
/// asked to do something.
///
/// Everything stands on the one flat square of ground <see cref="ParityPlayerBody"/>
/// lays down, a metre apart along the x axis, so "the nearest creature" and
/// "how far away is it" are questions with real answers.
/// </summary>
internal static class ParityWorld
{
    internal const uint Player = 0x50000001u;
    /// <summary>The combat table the staged character's stances come from.</summary>
    internal const uint CombatTable = 0x30000000u;
    internal const uint Monster = 0x50000012u;
    internal const uint SecondMonster = 0x50000013u;
    internal const uint DeadMonster = 0x50000011u;
    internal const uint HiddenMonster = 0x50000010u;
    internal const uint Bystander = 0x50000020u;

    /// <summary>Another player, standing next to the character.</summary>
    internal const uint OtherPlayer = 0x50000021u;

    /// <summary>The character's patron, who is nowhere near it.</summary>
    internal const uint Patron = 0x50000002u;

    /// <summary>The monarch of the allegiance the character belongs to.</summary>
    internal const uint Monarch = 0x50000003u;

    /// <summary>Where the character stands, in metres inside its cell.</summary>
    internal const float PlayerX = 96f;
    internal const float PlayerY = 97f;

    /// <summary>
    /// The player, a monster two metres further out, a second monster beyond
    /// it, a dead one, a hidden one and a harmless bystander -- and, with a
    /// body, a character that can turn towards any of them and swing.
    /// </summary>
    internal static ParityPlayerBody Stage(ParityArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        GameRuntime runtime = arm.Runtime;
        ParityPlayerBody body = arm.AdoptPlayerBody(
            ParityPlayerBody.Prepare(runtime));
        _ = body.SpawnLocalPlayer(Player, PlayerX, PlayerY);
        runtime.InventoryOwner.Objects.AddOrUpdate(PlayerObject(Player));

        Add(runtime, HiddenMonster, PlayerX + 1f, MonsterObject(HiddenMonster),
            PhysicsStateFlags.Hidden);
        Add(runtime, DeadMonster, PlayerX + 2f, MonsterObject(DeadMonster));
        runtime.ActionOwner.Combat.OnUpdateHealth(DeadMonster, 0f);
        Add(runtime, Monster, PlayerX + 3f, MonsterObject(Monster));
        Add(runtime, SecondMonster, PlayerX + 7f, MonsterObject(SecondMonster));
        Add(runtime, Bystander, PlayerX + 4f, BystanderObject(Bystander));
        body.Drive();
        SettleTheBody(arm);
        return body;
    }

    /// <summary>
    /// Runs the character's first frames until the pose it took on arrival
    /// has finished. A character that has just logged in is left standing
    /// before any plugin gets to it, and a stance change asks the body
    /// whether it is still busy with a motion before it goes out, so a
    /// scenario that started on the very frame of arrival would have every
    /// stance parked behind that first pose on both clients alike.
    /// </summary>
    private static void SettleTheBody(ParityArm arm)
    {
        // The body steps once per physics quantum, a thirtieth of a second,
        // so a handful of ticks covers the arrival pose several times over.
        const int MaximumTicks = 12;
        for (int tick = 0; tick < MaximumTicks; tick++)
        {
            if (arm.Runtime.MovementOwner.Controller is { } controller
                && !controller.Motion.MotionsPending())
            {
                return;
            }
            arm.StepBody();
        }
        throw new InvalidOperationException(
            $"{arm.Name}'s character is still taking up its arrival pose after {MaximumTicks} ticks.");
    }

    internal static ClientObject PlayerObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = "Parity",
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
        // A character carries a main pack, as every one the server sends
        // does; a using client reads that before anything else about it.
        ItemsCapacity = 102,
        ContainersCapacity = 7,
        // Every character the server sends carries the table its combat
        // stances come from; a body without one is never ready for melee.
        Properties = { DataIds = { [(uint)PropertyDataId.CombatTable] = CombatTable } },
    };

    internal static ClientObject MonsterObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = $"Monster {objectId:X8}",
        // Creatures come fixed in place, as the server's own creature
        // records do; a loose one would read as something to pick up.
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable
            | (uint)PublicWeenieFlags.Stuck,
    };

    /// <summary>A creature nothing may swing at.</summary>
    internal static ClientObject BystanderObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = $"Bystander {objectId:X8}",
        PublicWeenieBitfield = (uint)PublicWeenieFlags.Stuck,
    };

    /// <summary>
    /// Puts another player on the ground two metres out. The character's own
    /// entry is already a player; this is someone else's, which is what an
    /// allegiance command can be pointed at.
    /// </summary>
    internal static void StageAnotherPlayer(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ClientObject player = PlayerObject(OtherPlayer);
        player.Name = "Another";
        Add(runtime, OtherPlayer, PlayerX + 2f, player);
    }

    /// <summary>
    /// The allegiance the server states for this character: a monarch, the
    /// patron above it, and itself below that patron. Neither the monarch nor
    /// the patron is anywhere in the world, which is the ordinary case.
    /// </summary>
    internal static void StageAllegiance(ParityArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        // Said by the server and read by the client's own parser and inbound
        // route, which is how the client really learns this: it asked on
        // arriving in the world, and this is the answer. Writing it into the
        // owner by hand would leave the asking, the parsing and the routing
        // out of every scenario that stands on it.
        arm.Server.AllegianceUpdate(new ClientCommandResponses.AllegianceUpdate(
            Rank: 3u,
            TotalMembers: 3u,
            TotalVassals: 1u,
            RecordCount: 3,
            AllegianceName: "The Order",
            Monarch: new ClientCommandResponses.AllegianceMemberRecord(
                Monarch, 0u, true, "Monarch"),
            Records:
            [
                new ClientCommandResponses.AllegianceMemberRecord(
                    Patron, Monarch, false, "Patron"),
                new ClientCommandResponses.AllegianceMemberRecord(
                    Player, Patron, true, "Parity"),
            ]));
    }

    // ── things to carry, and things to open ─────────────────────────────

    /// <summary>A healing kit: usable on its own, carried in the main pack.</summary>
    internal const uint Kit = 0x50000040u;
    internal const uint KitIconUnderlay = 0x06005B0Cu;
    internal const uint KitIconOverlay = 0x06006C0Bu;
    internal const uint KitCoverage = 0x0000_0400u;

    /// <summary>A side pack, carried, with room for four things.</summary>
    internal const uint SidePack = 0x50000041u;

    /// <summary>A tinkering tool, which is what a salvage request needs.</summary>
    internal const uint SalvageTool = 0x50000042u;

    /// <summary>Something to salvage with it.</summary>
    internal const uint ScrapItem = 0x50000043u;

    /// <summary>An item that cannot be used without naming a target.</summary>
    internal const uint TargetedItem = 0x50000044u;

    /// <summary>What the scrap is made of, as the server states it.</summary>
    internal const uint SteelMaterial = 61u;

    /// <summary>
    /// A part-filled stack the character already carries, of the same kind as
    /// the coins lying in the corpse.
    /// </summary>
    internal const uint CarriedCoin = 0x50000045u;

    /// <summary>What takes the last free slot in the side pack.</summary>
    internal const uint SidePackFiller = 0x50000046u;

    /// <summary>
    /// A part-filled stack of the same coins as <see cref="CarriedCoin"/>,
    /// lying in the side pack, for a move to join.
    /// </summary>
    internal const uint SidePackCoin = 0x50000047u;

    /// <summary>A corpse lying on the ground, openable.</summary>
    internal const uint Corpse = 0x50000050u;

    /// <summary>
    /// Close enough to open without going anywhere first, in metres: inside
    /// the reach an object that never said how close to come is given.
    /// </summary>
    internal const float WithinArmsReach = 0.4f;

    /// <summary>Inside the corpse: something plain, and something worth a look.</summary>
    internal const uint CorpseCoin = 0x50000051u;
    internal const uint CorpseGem = 0x50000052u;

    /// <summary>
    /// A corpse the character can never get within reach of: three metres out
    /// and four metres up, on a ledge nothing here can climb.
    /// </summary>
    internal const uint CorpseOutOfEveryReach = 0x50000053u;

    /// <summary>How far above the ground that one sits, in metres.</summary>
    private const float OutOfEveryReachHeight = 4f;

    /// <summary>
    /// Fills the character's packs: a kit to use, a side pack to move things
    /// into, a tinkering tool and something to salvage with it, and an item
    /// that refuses to be used without a target. The player object itself is
    /// given room, because where a picked-up item goes is decided by the
    /// client, not the server, and a container with no room decides
    /// differently.
    /// </summary>
    internal static void StageCarriedItems(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject player = PlayerObject(Player);
        player.ItemsCapacity = 10;
        player.ContainersCapacity = 2;
        objects.AddOrUpdate(player);
        ClientObject kit = Carried(Kit, "Healing Kit", ItemUseability.Contained);
        // The icon layers a loot rule can match on: the backdrop the server
        // gives a rare, and an imbue mark.
        kit.IconUnderlayId = KitIconUnderlay;
        kit.IconOverlayId = KitIconOverlay;
        kit.Priority = KitCoverage;
        kit.PluralName = "Healing Kits";
        objects.AddOrUpdate(kit);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = SidePack,
            Type = ItemType.Container,
            Name = "Side Pack",
            ContainerId = Player,
            WeenieClassId = 0x0000_0834u,
            ItemsCapacity = 4,
            ContainerTypeHint = 1,
            StackSize = 1,
            Useability = ItemUseability.Contained,
        });
        ClientObject tool = Carried(
            SalvageTool, "Tinkering Tool", ItemUseability.Contained);
        tool.Type = ItemType.TinkeringTool;
        objects.AddOrUpdate(tool);
        ClientObject scrap = Carried(
            ScrapItem, "Scrap", ItemUseability.Contained);
        // What it is made of, which is what a salvage reads before it sends:
        // a thing with no material is not salvageable at all, so without this
        // both clients refuse and agree about refusing.
        scrap.MaterialType = SteelMaterial;
        objects.AddOrUpdate(scrap);
        ClientObject stone = Carried(
            TargetedItem,
            "Mana Stone",
            ItemUseability.Contained | (ItemUseability.Contained << 16));
        // What it may be used ON. A targeted item whose target kinds are
        // left blank is compatible with nothing, so the apply is turned away
        // before it is composed -- on both clients, which is agreement
        // about doing nothing.
        stone.TargetType = (uint)ItemType.Misc;
        objects.AddOrUpdate(stone);
    }

    /// <summary>
    /// The carried items, plus two part-filled stacks of one kind of coin:
    /// one loose in the main pack and one in the side pack.
    /// </summary>
    internal static void StageTwoStacksOfOneKind(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        StageCarriedItems(runtime);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        objects.AddOrUpdate(Coins(CarriedCoin, Player, 40));
        objects.AddOrUpdate(Coins(SidePackCoin, SidePack, 250));
        // The listing the server sends for the pack, which is what a search
        // for a stack to join walks.
        objects.ReplaceContents(SidePack, [new ContainerContentEntry(SidePackCoin, 0u)]);
    }

    private static ClientObject Coins(uint objectId, uint containerId, int count) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Money,
        Name = "Pyreal",
        ContainerId = containerId,
        WeenieClassId = 0x0000_0111u,
        StackSize = count,
        StackSizeMax = 25000,
        Useability = ItemUseability.Contained,
    };

    /// <summary>
    /// Leaves the character with nowhere to put anything new -- every item
    /// slot taken and no room for another pack -- while it still carries a
    /// part-filled stack of the coins lying in the corpse. Adding to that
    /// stack costs no slot, so this is the shape that tells a placement which
    /// joins a stack apart from one that only looks for room.
    ///
    /// The capacities are counted off what was actually staged rather than
    /// written down, so the inventory stays full if the staging above it
    /// grows.
    /// </summary>
    internal static void StageAFullInventoryOverAStack(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        StageCarriedItems(runtime);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;

        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CarriedCoin,
            Type = ItemType.Money,
            Name = "Pyreal",
            ContainerId = Player,
            WeenieClassId = 0x0000_0111u,
            StackSize = 250,
            StackSizeMax = 25000,
            Useability = ItemUseability.Contained,
        });

        ClientObject filler = Carried(
            SidePackFiller, "Trinket", ItemUseability.Contained);
        filler.ContainerId = SidePack;
        objects.AddOrUpdate(filler);

        // The listing the server sends for each pack. Slots are counted
        // against the listing, not against what each thing says it is in, so
        // a staging that skips this leaves both packs looking bottomless.
        objects.ReplaceContents(SidePack, [new ContainerContentEntry(SidePackFiller, 0u)]);
        objects.ReplaceContents(
            Player,
            [
                new ContainerContentEntry(Kit, 0u),
                new ContainerContentEntry(SalvageTool, 0u),
                new ContainerContentEntry(ScrapItem, 0u),
                new ContainerContentEntry(TargetedItem, 0u),
                new ContainerContentEntry(CarriedCoin, 0u),
                new ContainerContentEntry(SidePack, 1u),
            ]);

        if (objects.Get(SidePack) is { } sidePack)
        {
            sidePack.ItemsCapacity = CountHeld(objects, SidePack, packs: false);
            objects.AddOrUpdate(sidePack);
        }

        if (objects.Get(Player) is { } player)
        {
            player.ItemsCapacity = CountHeld(objects, Player, packs: false);
            player.ContainersCapacity = CountHeld(objects, Player, packs: true);
            objects.AddOrUpdate(player);
        }
    }

    /// <summary>
    /// How many of one container's contents are packs, or how many are not.
    /// </summary>
    private static int CountHeld(
        ClientObjectTable objects,
        uint containerId,
        bool packs)
    {
        int held = 0;
        foreach (uint id in objects.GetContents(containerId))
        {
            if (objects.Get(id) is not { } item)
                continue;
            if (InventoryContainerPlacementPolicy.IsContainer(item) == packs)
                held++;
        }
        return held;
    }

    /// <summary>
    /// Lays a corpse on the ground three metres out with nothing visible in
    /// it yet, the way one looks before it has been opened. Its contents
    /// arrive from <see cref="DeliverCorpseContents"/>.
    /// </summary>
    /// <param name="metresOut">
    /// How far out to lay it, in metres. Three metres is the ordinary case
    /// after a fight and is out of reach, so a use of it walks first; a
    /// scenario about what the server says back rather than about the walk
    /// lays it at arm's length instead.
    /// </param>
    internal static void StageCorpse(GameRuntime runtime, float metresOut = 3f)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Add(runtime, Corpse, PlayerX + metresOut, new ClientObject
        {
            ObjectId = Corpse,
            Type = ItemType.Container,
            Name = "Corpse of Monster",
            ItemsCapacity = 8,
            StackSize = 1,
            Useability = ItemUseability.Remote,
            PublicWeenieBitfield =
                (uint)(PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable),
        },
        useability: ItemUseability.Remote);
    }

    /// <summary>
    /// Lays a second corpse three metres out and four metres up. Asking to use
    /// it begins a walk -- how far off it is is judged across the ground, and
    /// across the ground it is three metres away -- and that walk can never
    /// arrive, because the gap the walk measures counts the four metres of air
    /// as well. It is the ordinary shape of a walk that never gets there: a
    /// target the character cannot reach and keeps trying to.
    /// </summary>
    internal static void StageCorpseOutOfEveryReach(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Add(runtime, CorpseOutOfEveryReach, PlayerX + 3f, new ClientObject
        {
            ObjectId = CorpseOutOfEveryReach,
            Type = ItemType.Container,
            Name = "Corpse on a Ledge",
            ItemsCapacity = 8,
            StackSize = 1,
            Useability = ItemUseability.Remote,
            PublicWeenieBitfield =
                (uint)(PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable),
        },
        useability: ItemUseability.Remote,
        z: ParityPlayerBody.GroundHeight + OutOfEveryReachHeight);
    }

    /// <summary>
    /// Puts the two things inside the corpse into what the client knows,
    /// without saying they have been listed. A scenario that lets the server
    /// send the listing itself stages them with this and lets the inbound
    /// message do the rest.
    /// </summary>
    internal static void StageCorpseContents(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        AddCorpseContents(runtime.InventoryOwner.Objects);
    }

    /// <summary>
    /// What the server says once the corpse has been used: the two things
    /// inside it, and the answer that the listing is complete.
    /// </summary>
    internal static void DeliverCorpseContents(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        AddCorpseContents(objects);
        // The listing the server sends with the contents, in its order.
        objects.ReplaceContents(Corpse, [CorpseCoin, CorpseGem]);
        _ = runtime.InventoryOwner.ExternalContainers.ApplyViewContents(Corpse);
    }

    private static void AddCorpseContents(ClientObjectTable objects)
    {
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CorpseCoin,
            Type = ItemType.Money,
            Name = "Pyreal",
            ContainerId = Corpse,
            WeenieClassId = 0x0000_0111u,
            StackSize = 250,
            StackSizeMax = 25000,
            Useability = ItemUseability.Contained,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CorpseGem,
            Type = ItemType.Gem,
            Name = "Gem",
            ContainerId = Corpse,
            WeenieClassId = 0x0000_0222u,
            StackSize = 1,
            Useability = ItemUseability.Contained,
        });
    }

    private static ClientObject Carried(
        uint objectId, string name, uint useability) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Misc,
        Name = name,
        ContainerId = Player,
        WeenieClassId = objectId & 0xFFFFu,
        StackSize = 1,
        Useability = useability,
    };

    /// <summary>
    /// Puts one thing in the world a metre or more out.
    /// </summary>
    /// <param name="useability">
    /// What the server says can be done with it. This is what a client reads
    /// before it sends a use, and it is NOT the same field as the one on the
    /// thing in the character's own record of its packs: a thing on the
    /// ground with nothing said here is refused before the use is ever
    /// composed, on every client.
    /// </param>
    /// <param name="z">
    /// How high off the ground it sits, in metres. Everything stands on the
    /// ground unless a scenario needs otherwise.
    /// </param>
    internal static void Add(
        GameRuntime runtime,
        uint guid,
        float x,
        ClientObject item,
        PhysicsStateFlags state = 0,
        uint? useability = null,
        float? z = null)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(
                guid,
                x,
                PlayerY,
                ParityPlayerBody.Cell,
                state,
                useability,
                z))
            .Canonical!;
        runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }


    /// <summary>
    /// One create as the server sends it. <paramref name="instance"/> is
    /// which incarnation of this object it is: a second create carrying a
    /// later one is the server re-sending the same object.
    /// </summary>
    internal static WorldSession.EntitySpawn Spawn(
        uint guid,
        float x,
        float y,
        uint cell,
        PhysicsStateFlags state,
        uint? useability = null,
        float? z = null,
        ushort instance = 1)
    {
        var position = new CreateObject.ServerPosition(
            cell,
            x,
            y,
            z ?? ParityPlayerBody.GroundHeight,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)state,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            Useability: useability,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
