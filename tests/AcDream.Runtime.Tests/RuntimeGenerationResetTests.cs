using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests;

public sealed class RuntimeGenerationResetTests
{
    [Fact]
    public void PopulatedResetConvergesEveryCanonicalOwnerAndStampsRetiringGeneration()
    {
        using var runtime = Create();
        const uint player = 0x50000001u;
        const uint creature = 0x70000001u;
        const uint item = 0x80000001u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.RegisterEntity(Spawn(creature, 3));
        runtime.EntityObjects.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Name = "item",
            ContainerId = player,
        });
        runtime.InventoryOwner.ExternalContainers.RequestOpen(creature);
        runtime.InventoryOwner.ExternalContainers.ApplyViewContents(creature);
        runtime.InventoryOwner.ItemMana.OnQueryItemManaResponse(
            item,
            0.5f,
            valid: true);
        runtime.CharacterOwner.Spellbook.OnSpellLearned(123u, 1f);
        runtime.CharacterOwner.LocalPlayer.OnVitalUpdate(
            7u,
            1u,
            100u,
            5u,
            80u);
        runtime.ActionOwner.Selection.Select(
            creature,
            SelectionChangeSource.System);
        runtime.ActionOwner.Combat.SetCombatMode(CombatMode.Missile);
        runtime.CommunicationOwner.Chat.SetLocalPlayerGuid(player);
        runtime.CommunicationOwner.Chat.OnSystemMessage("retained", 1u);
        runtime.CommunicationOwner.SpewBox.Enqueue("about to be torn down");
        _ = runtime.MovementOwner.Execute(
            RuntimeMovementCommand.ToggleRunLock);
        var observer = new RecordingObserver();
        using IDisposable subscription = runtime.Subscribe(observer);
        var host = new RecordingResetHost(runtime);
        var retiring = new RuntimeGenerationToken(9);

        runtime.ResetGeneration(retiring, host);

        Assert.Single(host.Retired);
        Assert.Equal(creature, host.Retired[0].ServerGuid);
        Assert.Equal(1, host.DrainCalls);
        Assert.Equal(1, host.CompleteCalls);
        Assert.All(
            observer.Combat
                .Concat(observer.Entity)
                .Concat(observer.Inventory),
            stamp => Assert.Equal(retiring, stamp.Generation));
        Assert.Equal(
            [1UL, 2UL, 3UL, 4UL, 5UL],
            observer.Combat
                .Concat(observer.Inventory)
                .Concat(observer.Entity)
                .OrderBy(static stamp => stamp.Sequence)
                .Select(static stamp => stamp.Sequence));
        Assert.Equal(0u, runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(0, runtime.Entities.Count);
        Assert.Equal(0, runtime.Inventory.ObjectCount);
        Assert.Equal(0, runtime.CharacterOwner.Spellbook.LearnedCount);
        Assert.Null(runtime.CharacterOwner.LocalPlayer.Get(
            AcDream.Core.Player.LocalPlayerState.VitalKind.Health));
        Assert.Equal(0u, runtime.Actions.Snapshot.SelectedObjectId);
        Assert.Equal(CombatMode.NonCombat, runtime.Actions.Snapshot.CombatMode);
        Assert.False(runtime.MovementOwner.AutoRunActive);
        Assert.Equal(1, runtime.CommunicationOwner.Chat.Count);
        runtime.CommunicationOwner.SpewBox.Tick(0d);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
        Assert.Null(
            runtime.CommunicationOwner.CommandTargets.LastIncomingTellSender);
        Assert.False(runtime.GenerationReset.CaptureSnapshot().IsActive);

        runtime.ResetGeneration(retiring, host);

        Assert.Single(host.Retired);
        Assert.Equal(1, host.DrainCalls);
        Assert.Equal(1, host.CompleteCalls);
    }

    [Fact]
    public void FailedHostDrainRetriesOnlyTheExactUnfinishedSuffix()
    {
        using var runtime = Create();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        runtime.EntityObjects.RegisterEntity(Spawn(0x70000001u, 1));
        runtime.EntityObjects.RegisterEntity(Spawn(0x70000002u, 2));
        var host = new RecordingResetHost(runtime)
        {
            FailDrainOnce = true,
        };
        var retiring = new RuntimeGenerationToken(12);

        RuntimeGenerationResetStageException failure =
            Assert.Throws<RuntimeGenerationResetStageException>(
                () => runtime.ResetGeneration(retiring, host));

        Assert.Equal(
            RuntimeGenerationResetStage.DrainHostProjection,
            failure.Stage);
        Assert.Equal(2, host.Retired.Count);
        Assert.Equal(1, host.DrainCalls);
        Assert.Equal(0, host.CompleteCalls);
        Assert.Equal(0, runtime.EntityObjects.Entities.PendingTeardownCount);
        Assert.NotEqual(0u, runtime.PlayerIdentity.ServerGuid);
        RuntimeGenerationResetSnapshot pending =
            runtime.GenerationReset.CaptureSnapshot();
        Assert.True(pending.IsActive);
        Assert.Equal(2, pending.RetirementCursor);

        runtime.ResetGeneration(retiring, host);

        Assert.Equal(2, host.Retired.Count);
        Assert.Equal(2, host.DrainCalls);
        Assert.Equal(1, host.CompleteCalls);
        Assert.Equal(0u, runtime.PlayerIdentity.ServerGuid);
        Assert.False(runtime.GenerationReset.CaptureSnapshot().IsActive);
    }

    [Fact]
    public void FailedHostCompletionKeepsOldIdentityAndDoesNotReplayDrainOrEntities()
    {
        using var runtime = Create();
        const uint player = 0x50000001u;
        runtime.PlayerIdentity.ServerGuid = player;
        runtime.EntityObjects.RegisterEntity(Spawn(0x70000001u, 1));
        var host = new RecordingResetHost(runtime)
        {
            FailCompleteOnce = true,
        };
        var retiring = new RuntimeGenerationToken(17);

        RuntimeGenerationResetStageException failure =
            Assert.Throws<RuntimeGenerationResetStageException>(
                () => runtime.ResetGeneration(retiring, host));

        Assert.Equal(
            RuntimeGenerationResetStage.CompleteHostProjection,
            failure.Stage);
        Assert.Equal(player, runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(0, runtime.EntityObjects.Entities.Count);
        Assert.Equal(0, runtime.EntityObjects.Entities.PendingTeardownCount);
        Assert.Single(host.Retired);
        Assert.Equal(1, host.DrainCalls);
        Assert.Equal(1, host.CompleteCalls);

        runtime.ResetGeneration(retiring, host);

        Assert.Single(host.Retired);
        Assert.Equal(1, host.DrainCalls);
        Assert.Equal(2, host.CompleteCalls);
        Assert.Equal(0u, runtime.PlayerIdentity.ServerGuid);
    }

    [Fact]
    public void PendingResetRejectsHostReplacementAndReentrantReset()
    {
        using var runtime = Create();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        runtime.EntityObjects.RegisterEntity(Spawn(0x70000001u, 1));
        var retiring = new RuntimeGenerationToken(4);
        var host = new RecordingResetHost(runtime)
        {
            Reenter = true,
            FailDrainOnce = true,
        };

        Assert.Throws<RuntimeGenerationResetStageException>(
            () => runtime.ResetGeneration(retiring, host));
        Assert.IsType<InvalidOperationException>(host.ReentrantFailure);
        Assert.Throws<InvalidOperationException>(() =>
            runtime.ResetGeneration(
                retiring,
                new RecordingResetHost(runtime)));

        runtime.ResetGeneration(retiring, host);
        Assert.False(runtime.GenerationReset.CaptureSnapshot().IsActive);
    }

    [Fact]
    public void FellowshipAndAllegianceBothClearAtGenerationReset()
    {
        using var runtime = Create();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        runtime.FellowshipOwner.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [new GameEvents.FellowMember(
                0x50000001u, 0u, 0u, 1u, 100u, 100u, 100u, 100u, 100u, 100u, 0u, "Self")],
            "The Fellows",
            LeaderGuid: 0x50000001u,
            ShareXp: true,
            EvenXpSplit: false,
            OpenFellow: true,
            Locked: false,
            Departed: []));
        runtime.AllegianceOwner.ApplyUpdate(new ClientCommandResponses.AllegianceUpdate(
            Rank: 2u,
            TotalMembers: 1u,
            TotalVassals: 0u,
            RecordCount: 1,
            AllegianceName: "The Order",
            Monarch: new ClientCommandResponses.AllegianceMemberRecord(
                0x50000001u, 0u, true, "Self"),
            Records: []));

        Assert.True(runtime.Fellowship.Snapshot.IsInFellowship);
        Assert.True(runtime.Allegiance.Snapshot.HasProfile);
        Assert.True(runtime.AllegianceOwner.HasServerSeed);

        var host = new RecordingResetHost(runtime);
        runtime.ResetGeneration(new RuntimeGenerationToken(3), host);

        Assert.False(runtime.Fellowship.Snapshot.IsInFellowship);
        Assert.Equal(0, runtime.Fellowship.Snapshot.MemberCount);
        Assert.False(runtime.Allegiance.Snapshot.HasProfile);
        Assert.False(runtime.AllegianceOwner.HasServerSeed);
        Assert.Equal(string.Empty, runtime.Allegiance.Snapshot.AllegianceName);
        Assert.Equal(0u, runtime.Allegiance.Snapshot.MonarchGuid);
        Assert.Equal(0, runtime.Allegiance.Snapshot.RecordCount);
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

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u,
            10f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            1,
            1,
            1,
            1,
            0,
            1,
            0,
            1,
            incarnation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
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
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class RecordingResetHost(GameRuntime runtime)
        : IRuntimeGenerationResetHost
    {
        public List<RuntimeEntityRecord> Retired { get; } = [];
        public int DrainCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public bool FailDrainOnce { get; set; }
        public bool FailCompleteOnce { get; set; }
        public bool Reenter { get; set; }
        public Exception? ReentrantFailure { get; private set; }

        public void RetireEntityProjection(RuntimeEntityRecord entity)
        {
            Retired.Add(entity);
            if (!Reenter)
                return;
            Reenter = false;
            ReentrantFailure = Record.Exception(() =>
                runtime.ResetGeneration(
                    runtime.GenerationReset
                        .CaptureSnapshot()
                        .RetiringGeneration,
                    this));
        }

        public void DrainEntityProjectionBoundary()
        {
            DrainCalls++;
            if (!FailDrainOnce)
                return;
            FailDrainOnce = false;
            throw new InvalidOperationException("injected drain failure");
        }

        public void CompleteEntityProjectionRetirement()
        {
            CompleteCalls++;
            Assert.NotEqual(0u, runtime.PlayerIdentity.ServerGuid);
            if (!FailCompleteOnce)
                return;
            FailCompleteOnce = false;
            throw new InvalidOperationException("injected completion failure");
        }
    }

    private sealed class RecordingObserver : IRuntimeEventObserver
    {
        public List<RuntimeEventStamp> Entity { get; } = [];
        public List<RuntimeEventStamp> Inventory { get; } = [];
        public List<RuntimeEventStamp> Combat { get; } = [];

        public void OnLifecycle(in RuntimeLifecycleDelta delta) { }
        public void OnCommand(in RuntimeCommandDelta delta) { }
        public void OnEntity(in RuntimeEntityDelta delta) =>
            Entity.Add(delta.Stamp);
        public void OnInventory(in RuntimeInventoryDelta delta) =>
            Inventory.Add(delta.Stamp);
        public void OnChat(in RuntimeChatDelta delta) { }
        public void OnMovement(in RuntimeMovementDelta delta) { }
        public void OnPortal(in RuntimePortalDelta delta) { }
        public void OnCombat(in RuntimeCombatDelta delta) =>
            Combat.Add(delta.Stamp);
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
