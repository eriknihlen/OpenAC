using System.Net;
using System.Reflection;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Spells;

using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The death a monster's animation announces has to reach a host that draws
/// nothing exactly as it reaches one that draws the monster, so these run on a
/// runtime with no window at all.
/// </summary>
public sealed class RuntimeCreatureDeathStateTests
{
    internal const uint Player = 0x50000001u;
    internal const uint Monster = 0x50000020u;
    internal const uint Neighbour = 0x50000021u;

    // The wire half of the death motion; the catalog reconstructs the rest.
    internal const ushort DeadWireCommand = 0x0011;

    [Fact]
    public void DeadWireCommandReconstructsToTheDeathMotion()
    {
        Assert.Equal(
            MotionCommand.Dead,
            MotionCommandResolver.ReconstructFullCommand(DeadWireCommand));
    }

    /// <summary>
    /// Mutation: have the reader ignore the one-shot command list and only the
    /// forward-command arm survives, which loses the death a live server sends
    /// as a one-shot.
    /// </summary>
    [Fact]
    public void DeathArrivesAsAForwardCommandOrAsAOneShot()
    {
        var state = new RuntimeCreatureDeathState();
        var announced = new List<uint>();
        state.Died += announced.Add;

        state.ObserveMotion(Motion(Monster, forward: DeadWireCommand));
        state.ObserveMotion(Motion(Neighbour, oneShot: DeadWireCommand));

        Assert.Equal(new[] { Monster, Neighbour }, announced);
        Assert.True(state.IsDead(Monster, 1));
        Assert.True(state.IsDead(Neighbour, 1));
    }

    [Fact]
    public void ARepeatedDeathMotionIsAnnouncedOnlyOnce()
    {
        var state = new RuntimeCreatureDeathState();
        int announced = 0;
        state.Died += _ => announced++;

        state.ObserveMotion(Motion(Monster, forward: DeadWireCommand));
        state.ObserveMotion(Motion(Monster, forward: DeadWireCommand));

        Assert.Equal(1, announced);
    }

    [Fact]
    public void ALivingMotionSaysNothing()
    {
        var state = new RuntimeCreatureDeathState();

        state.ObserveMotion(Motion(Monster, forward: 0x0007));

        Assert.False(state.IsDead(Monster));
        Assert.Equal(0, state.Count);
    }

    /// <summary>
    /// Mutation: drop the incarnation from the record and a fresh creature
    /// handed a dead one's object id is reported dead before it has moved.
    /// </summary>
    [Fact]
    public void AFreshCreatureOnADeadObjectIdIsAlive()
    {
        var state = new RuntimeCreatureDeathState();

        state.ObserveMotion(
            Motion(Monster, forward: DeadWireCommand, incarnation: 1));

        Assert.True(state.IsDead(Monster, 1));
        Assert.False(state.IsDead(Monster, 2));
    }

    [Fact]
    public void ASpawnForgetsTheDeadAndReadsTheMotionItArrivesWith()
    {
        var state = new RuntimeCreatureDeathState();
        state.ObserveMotion(Motion(Monster, forward: DeadWireCommand));

        state.ObserveSpawn(SpawnWithMotion(Monster, forward: 0x0007));
        Assert.False(state.IsDead(Monster));

        state.ObserveSpawn(SpawnWithMotion(Monster, forward: DeadWireCommand));
        Assert.True(state.IsDead(Monster));
    }

    /// <summary>
    /// The point of the owner: a runtime with no window clears the selection
    /// off the death motion. Mutation: bind the router without its action
    /// owner, the way the windowed host used to learn of a death through its
    /// own presentation path, and the selection stays on the corpse.
    /// </summary>
    [Fact]
    public void TheDeathMotionClearsTheSelectionOnARuntimeWithNoWindow()
    {
        using GameRuntime runtime = Create();
        using WorldSession session = NewSession();
        runtime.PlayerIdentity.ServerGuid = Player;
        AddPlayer(runtime);
        AddMonster(runtime, Monster);
        using LiveSessionEventRouter router = Router(session, runtime);
        router.Attach();
        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.World);

        FireMotion(session, Motion(Monster, forward: DeadWireCommand));

        Assert.Null(runtime.ActionOwner.Selection.SelectedObjectId);
        Assert.True(runtime.ActionOwner.CreatureDeath.IsDead(Monster, 1));
    }

    [Fact]
    public void DeletingACorpseForgetsItsDeathEvenWithoutAWindow()
    {
        using GameRuntime runtime = Create();
        using WorldSession session = NewSession();
        using LiveSessionEventRouter router = Router(session, runtime);
        router.Attach();
        FireMotion(session, Motion(Monster, forward: DeadWireCommand));
        Assert.Equal(1, runtime.ActionOwner.CreatureDeath.Count);

        FieldInfo field = typeof(WorldSession).GetField(
            nameof(session.EntityDeleted),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("delete event field not found");
        Assert.IsType<Action<DeleteObject.Parsed>>(field.GetValue(session))
            .Invoke(new DeleteObject.Parsed(Monster, 1));

        Assert.Equal(0, runtime.ActionOwner.CreatureDeath.Count);
    }

    [Fact]
    public void VisibilityExpiryForgetsTheDepartedCreaturesDeath()
    {
        using GameRuntime runtime = Create();
        AddMonster(runtime, Monster);
        runtime.ActionOwner.CreatureDeath.ObserveMotion(
            Motion(Monster, forward: DeadWireCommand));
        Assert.True(runtime.ActionOwner.CreatureDeath.IsDead(Monster, 1));

        Assert.True(RuntimeCanonicalEntityExpirySink.DeleteCanonicalOnly(
            runtime.EntityObjects,
            new DeleteObject.Parsed(Monster, 1)));

        Assert.False(runtime.ActionOwner.CreatureDeath.IsDead(Monster));
    }

    [Fact]
    public void APlacementWithdrawalKeepsTheActiveCreaturesDeath()
    {
        using GameRuntime runtime = Create();
        AddMonster(runtime, Monster);
        runtime.ActionOwner.CreatureDeath.ObserveMotion(
            Motion(Monster, forward: DeadWireCommand));
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(Monster, out RuntimeEntityRecord creature));

        Assert.True(runtime.EntityObjects.CommitWithdrawal(creature));

        Assert.True(runtime.ActionOwner.CreatureDeath.IsDead(Monster, 1));
    }

    [Fact]
    public void AStaleDeleteDoesNotForgetTheNewIncarnationsDeath()
    {
        var state = new RuntimeCreatureDeathState();
        state.ObserveMotion(Motion(Monster, forward: DeadWireCommand, incarnation: 2));

        state.Forget(Monster, incarnation: 1);

        Assert.True(state.IsDead(Monster, 2));
    }

    /// <summary>
    /// Mutation: stop projecting the death onto the snapshot and a macro
    /// reading the capture cannot tell a corpse from a monster whose health
    /// it was never told.
    /// </summary>
    [Fact]
    public void TheHostileCaptureCarriesTheDeath()
    {
        using GameRuntime runtime = Create();
        using WorldSession session = NewSession();
        runtime.PlayerIdentity.ServerGuid = Player;
        AddPlayer(runtime);
        AddMonster(runtime, Monster);
        using LiveSessionEventRouter router = Router(session, runtime);
        router.Attach();
        Assert.False(Captured(runtime).IsDead);
        // Nothing ever asked for this creature's health, so the older signal
        // has nothing to say about it either way.
        Assert.False(Captured(runtime).IsHealthKnown);

        FireMotion(session, Motion(Monster, forward: DeadWireCommand));

        Assert.True(Captured(runtime).IsDead);
        // And the scope a target picker uses refuses it outright.
        Assert.Null(RuntimeHostileTargetQuery.FindClosest(
            runtime,
            HostileTargetScope.Selectable));
    }

    [Fact]
    public void ASessionResetForgetsEveryDeath()
    {
        using GameRuntime runtime = Create();
        runtime.ActionOwner.CreatureDeath.ObserveMotion(
            Motion(Monster, forward: DeadWireCommand));

        runtime.ActionOwner.ResetSession();

        Assert.False(runtime.ActionOwner.CreatureDeath.IsDead(Monster));
    }

    private static RuntimeHostileTargetSnapshot Captured(GameRuntime runtime) =>
        Assert.Single(RuntimeHostileTargetQuery.Capture(
            runtime,
            50f,
            HostileTargetScope.Classified));


    internal static LiveSessionEventRouter Router(
        WorldSession session,
        GameRuntime runtime,
        Action<uint>? onQueryHealth = null) => new(
        session,
        NoOpEntitySink(),
        new LiveEnvironmentSessionSink(_ => { }, _ => { }),
        new LiveInventorySessionBindings(
            runtime.InventoryOwner.Objects,
            () => runtime.PlayerIdentity.ServerGuid,
            OnShortcuts: null,
            OnUseDone: null,
            ItemMana: new ItemManaState(),
            ExternalContainers: new ExternalContainerState()),
        new LiveCharacterSessionBindings(
            runtime.ActionOwner.Combat,
            runtime.CharacterOwner,
            ResolveSkillFormulaBonus: null,
            OnSkillsUpdated: null,
            OnConfirmationRequest: null,
            OnConfirmationDone: null,
            ClientTime: () => 0d),
        new LiveSocialSessionBindings(
            runtime.CommunicationOwner.Chat,
            runtime.CommunicationOwner.TurbineChat,
            runtime.CommunicationOwner.Friends,
            runtime.CommunicationOwner.Squelch),
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

    internal static void FireMotion(
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

    internal static WorldSession.EntityMotionUpdate Motion(
        uint guid,
        ushort forward = 0,
        ushort oneShot = 0,
        ushort incarnation = 1) => new(
        guid,
        new CreateObject.ServerMotionState(
            Stance: 0,
            ForwardCommand: forward == 0 ? null : forward,
            ForwardSpeed: null,
            Commands: oneShot == 0
                ? null
                : new[] { new CreateObject.MotionItem(oneShot, 0, 1f) }),
        incarnation,
        MovementSequence: 1,
        ServerControlSequence: 0,
        IsAutonomous: false);

    private static WorldSession.EntitySpawn SpawnWithMotion(
        uint guid,
        ushort forward) =>
        Spawn(guid, 10f, 10f) with
        {
            MotionState = new CreateObject.ServerMotionState(
                Stance: 0,
                ForwardCommand: forward),
        };

    internal static GameRuntime Create()
    {
        var operations = new NoOpOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    internal static void AddPlayer(GameRuntime runtime) =>
        Add(runtime, Player, 10f, 10f, new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
        });

    internal static void AddMonster(
        GameRuntime runtime,
        uint guid,
        string name = "Platinum Golem",
        float x = 12f) =>
        Add(runtime, guid, x, 10f, new ClientObject
        {
            ObjectId = guid,
            Name = name,
            Type = ItemType.Creature,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
        });

    private static void Add(
        GameRuntime runtime,
        uint guid,
        float x,
        float y,
        ClientObject item)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, x, y))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, float x, float y)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u, x, y, 5f, 1f, 0f, 0f, 0f);
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

    internal static WorldSession NewSession()
    {
        var session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 9));
        // Nothing is negotiated here, so outbound game messages are swallowed
        // rather than written to a transport that does not exist.
        session.GameMessageCapture = (_, _) => { };
        return session;
    }

    internal sealed class NoOpOperations :
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
