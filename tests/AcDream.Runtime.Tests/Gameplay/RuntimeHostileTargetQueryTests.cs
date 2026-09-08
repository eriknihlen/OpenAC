using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeHostileTargetQueryTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void FindClosest_UsesAbsoluteDerethCoordinatesAcrossLandblocks()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(
            runtime,
            Player,
            landblock: 0x01010001u,
            x: 191f,
            y: 100f,
            PlayerObject(Player));
        Add(
            runtime,
            0x50000010u,
            landblock: 0x02010001u,
            x: 1f,
            y: 100f,
            Hostile(0x50000010u));
        Add(
            runtime,
            0x50000011u,
            landblock: 0x01010001u,
            x: 180f,
            y: 100f,
            Hostile(0x50000011u));

        Assert.Equal(
            0x50000010u,
            RuntimeHostileTargetQuery.FindClosest(runtime));
        Assert.True(
            RuntimeHostileTargetQuery.IsHostile(
                runtime,
                0x50000010u));
    }

    [Fact]
    public void Query_RejectsHiddenNoDrawDeadPlayersPetsNpcsAndNonCreatures()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, PlayerObject(Player));
        Add(
            runtime,
            0x50000010u,
            0x01010001u,
            11f,
            10f,
            Hostile(0x50000010u),
            PhysicsStateFlags.Hidden);
        Add(
            runtime,
            0x50000011u,
            0x01010001u,
            12f,
            10f,
            Hostile(0x50000011u),
            PhysicsStateFlags.NoDraw);
        Add(
            runtime,
            0x50000012u,
            0x01010001u,
            13f,
            10f,
            Hostile(0x50000012u));
        runtime.ActionOwner.Combat.OnUpdateHealth(0x50000012u, 0f);
        Add(
            runtime,
            0x50000013u,
            0x01010001u,
            14f,
            10f,
            PlayerObject(0x50000013u));
        Add(
            runtime,
            0x50000014u,
            0x01010001u,
            15f,
            10f,
            Hostile(0x50000014u, petOwner: Player));
        Add(
            runtime,
            0x50000015u,
            0x01010001u,
            16f,
            10f,
            new ClientObject
            {
                ObjectId = 0x50000015u,
                Type = ItemType.Misc,
                PublicWeenieBitfield =
                    SelectedObjectHealthPolicy.BfAttackable,
            });
        Add(
            runtime,
            0x50000016u,
            0x01010001u,
            17f,
            10f,
            new ClientObject
            {
                ObjectId = 0x50000016u,
                Type = ItemType.Creature,
            });
        Add(
            runtime,
            0x50000017u,
            0x01010001u,
            18f,
            10f,
            Hostile(0x50000017u));

        Assert.Equal(
            0x50000017u,
            RuntimeHostileTargetQuery.FindClosest(runtime));
        Assert.False(RuntimeHostileTargetQuery.IsHostile(runtime, 0u));
        Assert.False(
            RuntimeHostileTargetQuery.IsHostile(
                runtime,
                0x50000010u));
        Assert.False(
            RuntimeHostileTargetQuery.IsHostile(
                runtime,
                0x50000012u));
        Assert.False(
            RuntimeHostileTargetQuery.IsHostile(
                runtime,
                0x50000014u));
        Assert.True(
            RuntimeHostileTargetQuery.IsHostile(
                runtime,
                0x50000017u));
    }

    [Fact]
    public void FindClosest_ReturnsNullWithoutPlayerPositionOrHostile()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        runtime.InventoryOwner.Objects.AddOrUpdate(PlayerObject(Player));

        Assert.Null(RuntimeHostileTargetQuery.FindClosest(runtime));

        Add(runtime, Player, 0x01010001u, 10f, 10f, PlayerObject(Player));
        Add(
            runtime,
            0x50000010u,
            0x01010001u,
            11f,
            10f,
            new ClientObject
            {
                ObjectId = 0x50000010u,
                Type = ItemType.Creature,
            });

        Assert.Null(RuntimeHostileTargetQuery.FindClosest(runtime));
    }

    [Fact]
    public void Capture_ProjectsRetailRelativeHeadingRangeNameAndHealth()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, PlayerObject(Player));
        ClientObject east = Hostile(0x50000020u);
        east.Name = "East Drudge";
        east.WeenieClassId = 1234u;
        east.Properties.Ints[(uint)PropertyInt.CreatureType] = 4;
        Add(runtime, east.ObjectId, 0x01010001u, 13f, 10f, east);
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000020u,
            Type = ItemType.Armor,
            WielderId = east.ObjectId,
            CurrentlyEquippedLocation = EquipMask.Shield,
        });
        runtime.ActionOwner.Combat.OnUpdateHealth(east.ObjectId, 0.75f);

        IReadOnlyList<RuntimeHostileTargetSnapshot> captured =
            RuntimeHostileTargetQuery.Capture(runtime, 4f);

        RuntimeHostileTargetSnapshot target = Assert.Single(captured);
        Assert.Equal(east.ObjectId, target.ObjectId);
        Assert.Equal("East Drudge", target.Name);
        Assert.Equal(1234u, target.WeenieClassId);
        Assert.Equal(3f, target.Distance, 3);
        Assert.Equal(90f, target.RelativeAngleDegrees, 3);
        Assert.True(target.IsHealthKnown);
        Assert.Equal(0.75f, target.HealthFraction, 3);
        Assert.Equal(4, target.SpeciesId);
        Assert.True(target.HasShield);
        Assert.Equal(0, target.MaximumHealth);
        Assert.Empty(RuntimeHostileTargetQuery.Capture(runtime, 2.9f));
    }

    private static GameRuntime Create()
    {
        var operations = new Operations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static ClientObject PlayerObject(uint id) => new()
    {
        ObjectId = id,
        Type = ItemType.Creature,
        PublicWeenieBitfield =
            SelectedObjectHealthPolicy.BfPlayer,
    };

    private static ClientObject Hostile(
        uint id,
        uint petOwner = 0u) => new()
    {
        ObjectId = id,
        Type = ItemType.Creature,
        PublicWeenieBitfield =
            SelectedObjectHealthPolicy.BfAttackable,
        PetOwnerId = petOwner,
    };

    private static void Add(
        GameRuntime runtime,
        uint guid,
        uint landblock,
        float x,
        float y,
        ClientObject item,
        PhysicsStateFlags state = 0)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(
                Spawn(guid, landblock, x, y, state))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint landblock,
        float x,
        float y,
        PhysicsStateFlags state)
    {
        var position = new CreateObject.ServerPosition(
            landblock,
            x,
            y,
            5f,
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
            Instance: 1);
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
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class Operations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
