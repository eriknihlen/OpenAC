using System.Reflection;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests;

public sealed class GameRuntimeTests
{
    [Fact]
    public void RootOwnsOneExactInstanceOfEveryRuntimeLifetimeGroup()
    {
        using var runtime = Create();

        Assert.Same(runtime.EntityObjects.EntityView, runtime.Entities);
        Assert.Same(runtime.EntityObjects.InventoryView, runtime.Inventory);
        Assert.Same(runtime.InventoryOwner.View, runtime.InventoryState);
        Assert.Same(runtime.CharacterOwner.View, runtime.Character);
        Assert.Same(runtime.CommunicationOwner.View, runtime.Chat);
        Assert.Same(runtime.CommunicationOwner.SocialView, runtime.Social);
        Assert.Same(runtime.FellowshipOwner.View, runtime.Fellowship);
        Assert.Same(runtime.AllegianceOwner.View, runtime.Allegiance);
        Assert.Same(runtime.ActionOwner.View, runtime.Actions);
        Assert.Same(runtime.MovementOwner.View, runtime.Movement);
        Assert.Same(runtime.EnvironmentOwner, runtime.Environment);
        Assert.Same(runtime.TransitOwner, runtime.Portal);
        Assert.Same(runtime.Clock, ((IGameRuntimeView)runtime).Clock);
        Assert.Equal(RuntimeLifecycleState.Constructed, runtime.Lifecycle.State);
    }

    [Fact]
    public void RootsNeverShareIdentityClockWorldGameplayOrSessionState()
    {
        using var first = Create();
        using var second = Create();

        first.PlayerIdentity.ServerGuid = 0x50000001u;
        _ = first.Clock.Advance(0.25);
        first.ActionOwner.Selection.Select(
            0x70000001u,
            SelectionChangeSource.System);
        long reveal = first.TransitOwner.BeginLoginReveal(0x12340001u);

        Assert.Equal(0x50000001u, first.Lifecycle.PlayerGuid);
        Assert.Equal(0u, second.Lifecycle.PlayerGuid);
        Assert.Equal(1UL, first.Clock.FrameNumber);
        Assert.Equal(0UL, second.Clock.FrameNumber);
        Assert.Equal(0x70000001u, first.Actions.Snapshot.SelectedObjectId);
        Assert.Equal(0u, second.Actions.Snapshot.SelectedObjectId);
        Assert.Equal(reveal, first.Portal.Snapshot.Generation);
        Assert.Equal(RuntimePortalKind.None, second.Portal.Snapshot.Kind);
        Assert.NotSame(first.Session, second.Session);
        Assert.NotSame(first.EntityObjects, second.EntityObjects);
    }

    [Fact]
    public void EventSourceIsLazyOrderedAndOwnedByTheRoot()
    {
        using var runtime = Create();
        GameRuntimeOwnershipSnapshot before = runtime.CaptureOwnership();
        Assert.False(before.Events.OwnerEventsAttached);
        Assert.Equal(0, before.Events.ObserverCount);

        var observer = new RecordingObserver();
        using IDisposable subscription = runtime.Subscribe(observer);
        GameRuntimeOwnershipSnapshot attached = runtime.CaptureOwnership();
        Assert.True(attached.Events.OwnerEventsAttached);
        Assert.Equal(1, attached.Events.ObserverCount);

        runtime.CommunicationOwner.Chat.OnSystemMessage("hello", 1);

        RuntimeChatDelta chat = Assert.Single(observer.Chat);
        Assert.Equal("hello", chat.Entry.Text);
        Assert.Equal(runtime.Generation, chat.Stamp.Generation);
        Assert.Equal(runtime.Clock.FrameNumber, chat.Stamp.FrameNumber);

        subscription.Dispose();
        GameRuntimeOwnershipSnapshot detached = runtime.CaptureOwnership();
        Assert.False(detached.Events.OwnerEventsAttached);
        Assert.Equal(0, detached.Events.ObserverCount);
    }

    [Fact]
    public void HostLeaseBlocksTerminalDisposalUntilExactBorrowerReleases()
    {
        var runtime = Create();
        IDisposable lease = runtime.AcquireHostLease("graphical host");

        InvalidOperationException blocked = Assert.Throws<InvalidOperationException>(
            runtime.Dispose);
        Assert.Contains("graphical host", blocked.Message);
        GameRuntimeOwnershipSnapshot pending = runtime.CaptureOwnership();
        Assert.True(pending.IsDisposeRequested);
        Assert.False(pending.IsDisposed);
        Assert.Equal(1, pending.HostLeaseCount);
        Assert.Equal(GameRuntimeTeardownStage.None, pending.CompletedTeardownStages);

        lease.Dispose();
        runtime.Dispose();

        Assert.True(runtime.CaptureOwnership().IsConverged);
        runtime.Dispose();
        Assert.True(runtime.CaptureOwnership().IsConverged);
    }

    [Theory]
    [InlineData((int)GameRuntimeConstructionPoint.ClockCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.SessionCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.PlayerIdentityCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.EntityObjectsCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.InventoryCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.CharacterCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.CommunicationCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.FellowshipCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.AllegianceCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.MovementCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.ActionsCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.EnvironmentCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.TransitCreated)]
    [InlineData((int)GameRuntimeConstructionPoint.EventsCreated)]
    public void ConstructionFailureRetiresEveryAcquiredOwnerInReverse(
        int failurePointValue)
    {
        var failurePoint = (GameRuntimeConstructionPoint)failurePointValue;
        GameRuntimeConstructionContext? captured = null;

        Assert.Throws<InjectedConstructionFailure>(() =>
            new GameRuntime(
                Dependencies(),
                (point, context) =>
                {
                    if (point != failurePoint)
                        return;
                    captured = context;
                    throw new InjectedConstructionFailure();
                }));

        Assert.NotNull(captured);
        if (captured.Session is not null)
            Assert.True(captured.Session.CaptureOwnership().IsConverged);
        if (captured.PlayerIdentity is not null)
            Assert.True(captured.PlayerIdentity.CaptureOwnership().IsConverged);
        if (captured.Actions is not null)
            Assert.True(captured.Actions.CaptureOwnership().IsConverged);
        if (captured.Movement is not null)
            Assert.True(captured.Movement.CaptureOwnership().IsConverged);
        if (captured.Character is not null)
            Assert.True(captured.Character.CaptureOwnership().IsConverged);
        if (captured.Inventory is not null)
            Assert.True(captured.Inventory.CaptureOwnership().IsConverged);
        if (captured.Communication is not null)
            Assert.True(captured.Communication.CaptureOwnership().IsConverged);
        if (captured.Fellowship is not null)
            Assert.True(captured.Fellowship.CaptureOwnership().IsConverged);
        if (captured.Allegiance is not null)
            Assert.True(captured.Allegiance.CaptureOwnership().IsConverged);
        if (captured.EntityObjects is not null)
        {
            Assert.True(captured.EntityObjects.CaptureOwnership().IsConverged);
            Assert.True(
                captured.EntityObjects.Physics.CaptureOwnership().IsConverged);
        }
        if (captured.Events is not null)
            Assert.True(captured.Events.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void DirectRootLoadsNoGraphicalAudioOrUiBackend()
    {
        using var runtime = Create();
        _ = runtime.CaptureCheckpoint();

        string[] loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(loaded, static name =>
            name.StartsWith("AcDream.App", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AcDream.UI.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Silk.NET", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OpenAL", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Arch", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TerminalDisposalConvergesCompleteOwnershipLedger()
    {
        var runtime = Create();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        _ = runtime.MovementOwner.Execute(
            RuntimeMovementCommand.ToggleRunLock);
        runtime.ActionOwner.Selection.Select(
            0x70000001u,
            SelectionChangeSource.System);
        runtime.CommunicationOwner.Chat.OnSystemMessage("state", 1);
        _ = runtime.Subscribe(new RecordingObserver());

        runtime.Dispose();

        GameRuntimeOwnershipSnapshot ownership = runtime.CaptureOwnership();
        Assert.True(ownership.IsConverged);
        Assert.Equal(
            GameRuntimeTeardownStage.Complete,
            ownership.CompletedTeardownStages);
        Assert.Equal(0, ownership.Events.ObserverCount);
        Assert.Equal(0u, ownership.PlayerIdentity.ServerGuid);
    }

    [Fact]
    public void CompletedTeardownStagesAccumulatesExactlyOneFlagPerStage()
    {
        using var runtime = Create();
        FieldInfo stageField = typeof(GameRuntime).GetField(
            "_disposeStage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        PropertyInfo completedProperty = typeof(GameRuntime).GetProperty(
            "CompletedTeardownStages", BindingFlags.NonPublic | BindingFlags.Instance)!;

        GameRuntimeTeardownStage[] orderedFlags =
        [
            GameRuntimeTeardownStage.HostLeasesReleased,
            GameRuntimeTeardownStage.EventsDetached,
            GameRuntimeTeardownStage.SessionDisposed,
            GameRuntimeTeardownStage.TransitReset,
            GameRuntimeTeardownStage.ActionsDisposed,
            GameRuntimeTeardownStage.MovementDisposed,
            GameRuntimeTeardownStage.CharacterDisposed,
            GameRuntimeTeardownStage.InventoryDisposed,
            GameRuntimeTeardownStage.CommunicationDisposed,
            GameRuntimeTeardownStage.FellowshipDisposed,
            GameRuntimeTeardownStage.AllegianceDisposed,
            GameRuntimeTeardownStage.TradeDisposed,
            GameRuntimeTeardownStage.ContractsDisposed,
            GameRuntimeTeardownStage.JournalDisposed,
            GameRuntimeTeardownStage.IdentityDisposed,
            GameRuntimeTeardownStage.EntityObjectsDisposed,
        ];

        GameRuntimeTeardownStage expected = GameRuntimeTeardownStage.None;
        for (int stage = 0; stage <= orderedFlags.Length; stage++)
        {
            stageField.SetValue(runtime, stage);
            var actual = (GameRuntimeTeardownStage)completedProperty.GetValue(runtime)!;
            Assert.Equal(expected, actual);
            if (stage < orderedFlags.Length)
                expected |= orderedFlags[stage];
        }

        Assert.Equal(GameRuntimeTeardownStage.Complete, expected);

        int stageCount = (int)typeof(GameRuntime)
            .GetField("TeardownStageCount", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Equal(orderedFlags.Length, stageCount);
    }

    private static GameRuntime Create() => new(Dependencies());

    private static GameRuntimeDependencies Dependencies()
    {
        var operations = new Operations();
        return new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations);
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

    private sealed class RecordingObserver : IRuntimeEventObserver
    {
        public List<RuntimeChatDelta> Chat { get; } = [];

        public void OnLifecycle(in RuntimeLifecycleDelta delta) { }
        public void OnCommand(in RuntimeCommandDelta delta) { }
        public void OnEntity(in RuntimeEntityDelta delta) { }
        public void OnInventory(in RuntimeInventoryDelta delta) { }
        public void OnChat(in RuntimeChatDelta delta) => Chat.Add(delta);
        public void OnMovement(in RuntimeMovementDelta delta) { }
        public void OnPortal(in RuntimePortalDelta delta) { }
        public void OnCombat(in RuntimeCombatDelta delta) { }
    }

    private sealed class InjectedConstructionFailure : Exception
    {
    }
}
