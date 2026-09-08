using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeFriendlyTargetQueryTests
{
    private const uint Player = 0x50000001u;
    private const uint PlayerBit = 0x8u;

    [Fact]
    public void FindClosestOtherPlayer_UsesAbsoluteDerethCoordinatesAcrossLandblocks()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(
            runtime,
            Player,
            landblock: 0x01010001u,
            x: 191f,
            y: 100f,
            name: "Self",
            objectDescriptionFlags: PlayerBit);
        Add(
            runtime,
            0x50000010u,
            landblock: 0x02010001u,
            x: 1f,
            y: 100f,
            name: "NearOtherPlayer",
            objectDescriptionFlags: PlayerBit);
        Add(
            runtime,
            0x50000011u,
            landblock: 0x01010001u,
            x: 180f,
            y: 100f,
            name: "FarOtherPlayer",
            objectDescriptionFlags: PlayerBit);

        Assert.Equal(
            0x50000010u,
            RuntimeFriendlyTargetQuery.FindClosestOtherPlayer(runtime));
        Assert.Equal(
            "NearOtherPlayer",
            RuntimeFriendlyTargetQuery.TryGetName(runtime, 0x50000010u));
    }

    [Fact]
    public void FindClosestOtherPlayer_RejectsHiddenNoDrawSelfAndNonPlayerEntities()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, "Self", PlayerBit);
        Add(
            runtime,
            0x50000010u,
            0x01010001u,
            11f,
            10f,
            "Hidden",
            PlayerBit,
            PhysicsStateFlags.Hidden);
        Add(
            runtime,
            0x50000011u,
            0x01010001u,
            12f,
            10f,
            "NoDraw",
            PlayerBit,
            PhysicsStateFlags.NoDraw);
        Add(
            runtime,
            0x50000012u,
            0x01010001u,
            13f,
            10f,
            "NotAPlayer",
            objectDescriptionFlags: 0u);
        Add(
            runtime,
            0x50000013u,
            0x01010001u,
            14f,
            10f,
            "RealOtherPlayer",
            PlayerBit);

        Assert.Equal(
            0x50000013u,
            RuntimeFriendlyTargetQuery.FindClosestOtherPlayer(runtime));
    }

    [Fact]
    public void FindClosestOtherPlayer_ReturnsNullWithoutPlayerPositionOrOtherPlayer()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;

        Assert.Null(RuntimeFriendlyTargetQuery.FindClosestOtherPlayer(runtime));

        Add(runtime, Player, 0x01010001u, 10f, 10f, "Self", PlayerBit);
        Add(
            runtime,
            0x50000010u,
            0x01010001u,
            11f,
            10f,
            "NotAPlayer",
            objectDescriptionFlags: 0u);

        Assert.Null(RuntimeFriendlyTargetQuery.FindClosestOtherPlayer(runtime));
    }

    [Fact]
    public void TryGetName_ReturnsNullForAnUnresolvedGuid()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, "Self", PlayerBit);

        Assert.Null(RuntimeFriendlyTargetQuery.TryGetName(runtime, 0x50000099u));
    }

    [Fact]
    public void FindPlayerByName_PrefersTheNamedPlayerOverACloserStranger()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, "Self", PlayerBit);
        Add(
            runtime,
            0x50000010u,
            0x01010001u,
            11f,
            10f,
            "+Je",
            PlayerBit);
        Add(
            runtime,
            0x50000011u,
            0x01010001u,
            30f,
            10f,
            "+Horan",
            PlayerBit);

        Assert.Equal(
            0x50000011u,
            RuntimeFriendlyTargetQuery.FindPlayerByName(runtime, "+Horan"));
        Assert.Equal(
            0x50000010u,
            RuntimeFriendlyTargetQuery.FindClosestOtherPlayer(runtime));
    }

    [Fact]
    public void FindPlayerByName_ReturnsNullWhenNoPlayerHasThatName()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, "Self", PlayerBit);
        Add(runtime, 0x50000010u, 0x01010001u, 11f, 10f, "+Je", PlayerBit);

        Assert.Null(
            RuntimeFriendlyTargetQuery.FindPlayerByName(runtime, "+Horan"));
    }

    [Fact]
    public void FindPlayerByName_IsCaseSensitiveAndSkipsHiddenNoDrawAndSelf()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 0x01010001u, 10f, 10f, "+Horan", PlayerBit);
        Add(
            runtime,
            0x50000010u,
            0x01010001u,
            11f,
            10f,
            "+horan",
            PlayerBit);
        Add(
            runtime,
            0x50000011u,
            0x01010001u,
            12f,
            10f,
            "+Horan",
            PlayerBit,
            PhysicsStateFlags.Hidden);
        Add(
            runtime,
            0x50000012u,
            0x01010001u,
            13f,
            10f,
            "+Horan",
            PlayerBit,
            PhysicsStateFlags.NoDraw);
        Add(
            runtime,
            0x50000013u,
            0x01010001u,
            14f,
            10f,
            "+Horan",
            PlayerBit);

        Assert.Equal(
            0x50000013u,
            RuntimeFriendlyTargetQuery.FindPlayerByName(runtime, "+Horan"));
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

    private static void Add(
        GameRuntime runtime,
        uint guid,
        uint landblock,
        float x,
        float y,
        string name,
        uint objectDescriptionFlags,
        PhysicsStateFlags state = 0)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(
                Spawn(guid, landblock, x, y, name, objectDescriptionFlags, state))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint landblock,
        float x,
        float y,
        string name,
        uint objectDescriptionFlags,
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
            name,
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            ObjectDescriptionFlags: objectDescriptionFlags,
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
