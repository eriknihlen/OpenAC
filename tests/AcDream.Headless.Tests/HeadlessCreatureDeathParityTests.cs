using System.Net;
using System.Reflection;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

/// <summary>
/// The burst a server sends when a monster falls — its health reaching zero,
/// the notice naming who killed it, and the death animation — has to leave
/// the client in the same state whether or not a window is drawing anything.
/// Both hosts route it through the same session routing, bound here exactly
/// as the no-window host binds it.
/// </summary>
public sealed class HeadlessCreatureDeathParityTests
{
    private const uint Player = 0x50000001u;
    private const uint Monster = 0x50000020u;
    private const ushort DeadWireCommand = 0x0011;

    /// <summary>
    /// Mutation: bind the routing without its action owner — the shape the
    /// no-window host had while only the windowed host answered a death — and
    /// the selection stays on the corpse and the capture still calls it a
    /// live monster.
    /// </summary>
    [Fact]
    public void TheKillBurstLeavesTheSameStateOnAHostWithNoWindow()
    {
        using GameRuntime runtime = Create();
        using var session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 9));
        session.GameMessageCapture = (_, _) => { };
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 10f, PlayerObject());
        Add(runtime, Monster, 12f, MonsterObject());
        var chat = new ChatLog();
        var seen = new List<string>();
        chat.EntryAppended += entry => seen.Add(entry.Text);
        using LiveSessionEventRouter router = Router(session, runtime, chat);
        router.Attach();
        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.World);

        // 1. Health reaches zero.
        runtime.ActionOwner.Combat.OnUpdateHealth(Monster, 0f);
        // 2. The notice naming who killed it reaches the log the macro reads.
        chat.OnCombatLine(
            "Acdream split Platinum Golem apart!",
            logTextType: 0x00u,
            kind: CombatLineKind.Info);
        // 3. The death animation.
        FireMotion(session, DeadMotion(Monster));

        Assert.Contains("Acdream split Platinum Golem apart!", seen);
        Assert.Null(runtime.ActionOwner.Selection.SelectedObjectId);
        Assert.True(runtime.ActionOwner.CreatureDeath.IsDead(Monster, 1));
        Assert.Empty(RuntimeHostileTargetQuery.Capture(
            runtime,
            50f,
            HostileTargetScope.Selectable));
        RuntimeHostileTargetSnapshot corpse = Assert.Single(
            RuntimeHostileTargetQuery.Capture(
                runtime,
                50f,
                HostileTargetScope.Classified));
        Assert.True(corpse.IsDead);
    }

    /// <summary>
    /// The death animation alone is enough. A host that never asked the
    /// server for this creature's health still drops it.
    /// </summary>
    [Fact]
    public void TheDeathAnimationAloneIsEnoughWithoutAHealthReading()
    {
        using GameRuntime runtime = Create();
        using var session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 9));
        session.GameMessageCapture = (_, _) => { };
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 10f, PlayerObject());
        Add(runtime, Monster, 12f, MonsterObject());
        using LiveSessionEventRouter router = Router(session, runtime, new ChatLog());
        router.Attach();
        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.World);

        FireMotion(session, DeadMotion(Monster));

        Assert.False(runtime.ActionOwner.Combat.HasHealth(Monster));
        Assert.Null(runtime.ActionOwner.Selection.SelectedObjectId);
        Assert.True(runtime.ActionOwner.CreatureDeath.IsDead(Monster));
    }

    private static LiveSessionEventRouter Router(
        WorldSession session,
        GameRuntime runtime,
        ChatLog chat) => new(
        session,
        NoOpEntitySink(),
        new LiveEnvironmentSessionSink(_ => { }, _ => { }),
        new LiveInventorySessionBindings(
            runtime.InventoryOwner.Objects,
            () => runtime.PlayerIdentity.ServerGuid,
            OnShortcuts: null,
            OnUseDone: null,
            ItemMana: null,
            ExternalContainers: null),
        new LiveCharacterSessionBindings(
            runtime.ActionOwner.Combat,
            runtime.CharacterOwner,
            ResolveSkillFormulaBonus: null,
            OnSkillsUpdated: null,
            OnConfirmationRequest: null,
            OnConfirmationDone: null,
            ClientTime: () => 0d),
        new LiveSocialSessionBindings(chat, new TurbineChatState(), null, null),
        actions: runtime.ActionOwner);

    private static LiveEntitySessionSink NoOpEntitySink() => new(
        Spawned: _ => { },
        Deleted: _ => { },
        PickedUp: _ => { },
        MotionUpdated: _ => { },
        PositionUpdated: _ => { },
        VectorUpdated: _ => { },
        StateUpdated: _ => { },
        ParentUpdated: _ => { },
        TeleportStarted: _ => { },
        AppearanceUpdated: _ => { },
        DescriptionRefreshed: _ => { },
        PlayPhysicsScript: _ => { },
        PlayPhysicsScriptType: _ => { },
        SoundEvent: _ => { },
        IsHeld: _ => false);

    private static void FireMotion(
        WorldSession session,
        WorldSession.EntityMotionUpdate update)
    {
        FieldInfo field = typeof(WorldSession).GetField(
            nameof(session.MotionUpdated),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("motion event field not found");
        Assert.IsType<Action<WorldSession.EntityMotionUpdate>>(
            field.GetValue(session)).Invoke(update);
    }

    private static WorldSession.EntityMotionUpdate DeadMotion(uint guid) => new(
        guid,
        new CreateObject.ServerMotionState(
            Stance: 0,
            ForwardCommand: null,
            ForwardSpeed: null,
            Commands: new[]
            {
                new CreateObject.MotionItem(DeadWireCommand, 0, 1f),
            }),
        InstanceSequence: 1,
        MovementSequence: 1,
        ServerControlSequence: 0,
        IsAutonomous: false);

    private static GameRuntime Create()
    {
        var operations = new NoOpOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static ClientObject PlayerObject() => new()
    {
        ObjectId = Player,
        Type = ItemType.Creature,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
    };

    private static ClientObject MonsterObject() => new()
    {
        ObjectId = Monster,
        Name = "Platinum Golem",
        Type = ItemType.Creature,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
    };

    private static void Add(
        GameRuntime runtime,
        uint guid,
        float x,
        ClientObject item)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, x))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, float x)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u, x, 10f, 5f, 1f, 0f, 0f, 0f);
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
            RawState: 0u,
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

    private sealed class NoOpOperations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) => false;
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
