using System.Numerics;
using AcDream.Core.Net;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.Runtime;

public sealed record GameRuntimeDependencies(
    IRuntimeCombatAttackOperations CombatAttackOperations,
    IRuntimeCombatTargetOperations CombatTargetOperations,
    IRuntimeCombatModeOperations CombatModeOperations,
    IRuntimeSpellCastOperations SpellCastOperations,
    TimeProvider? TimeProvider = null,
    Action<string>? Log = null,
    Action<string>? TimeSyncDiagnostic = null,
    ILiveSessionOperations? SessionOperations = null,
    Func<double>? CombatTime = null,
    uint FirstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId,
    int MaximumChatEntries = 500,
    Random? Random = null);

[Flags]
public enum GameRuntimeTeardownStage
{
    None = 0,
    HostLeasesReleased = 1 << 0,
    EventsDetached = 1 << 1,
    SessionDisposed = 1 << 2,
    TransitReset = 1 << 3,
    ActionsDisposed = 1 << 4,
    MovementDisposed = 1 << 5,
    CharacterDisposed = 1 << 6,
    InventoryDisposed = 1 << 7,
    CommunicationDisposed = 1 << 8,
    FellowshipDisposed = 1 << 9,
    AllegianceDisposed = 1 << 10,
    TradeDisposed = 1 << 11,
    IdentityDisposed = 1 << 12,
    EntityObjectsDisposed = 1 << 13,
    ContractsDisposed = 1 << 14,
    JournalDisposed = 1 << 15,
    Complete =
        HostLeasesReleased
        | EventsDetached
        | SessionDisposed
        | TransitReset
        | ActionsDisposed
        | MovementDisposed
        | CharacterDisposed
        | InventoryDisposed
        | CommunicationDisposed
        | FellowshipDisposed
        | AllegianceDisposed
        | TradeDisposed
        | ContractsDisposed
        | JournalDisposed
        | IdentityDisposed
        | EntityObjectsDisposed,
}

public readonly record struct GameRuntimeOwnershipSnapshot(
    bool IsDisposeRequested,
    bool IsDisposeDrainActive,
    bool IsDisposed,
    int HostLeaseCount,
    GameRuntimeTeardownStage CompletedTeardownStages,
    LiveSessionOwnershipSnapshot Session,
    RuntimeLocalPlayerIdentityOwnershipSnapshot PlayerIdentity,
    RuntimeSimulationOwnershipSnapshot Simulation,
    RuntimeWorldEnvironmentOwnershipSnapshot Environment,
    RuntimeWorldTransitOwnershipSnapshot Transit,
    RuntimeGenerationResetSnapshot GenerationReset,
    GameRuntimeEventOwnershipSnapshot Events)
{
    public bool IsConverged =>
        IsDisposed
        && IsDisposeRequested
        && !IsDisposeDrainActive
        && HostLeaseCount == 0
        && CompletedTeardownStages == GameRuntimeTeardownStage.Complete
        && Session.IsConverged
        && PlayerIdentity.IsConverged
        && Simulation.IsConverged
        && Transit.IsSessionIdle
        && GenerationReset.IsConverged
        && Events.IsConverged;
}

internal enum GameRuntimeConstructionPoint
{
    ClockCreated,
    SessionCreated,
    PlayerIdentityCreated,
    EntityObjectsCreated,
    InventoryCreated,
    CharacterCreated,
    CommunicationCreated,
    FellowshipCreated,
    AllegianceCreated,
    TradeCreated,
    ContractsCreated,
    JournalCreated,
    HouseCreated,
    MovementCreated,
    ActionsCreated,
    EnvironmentCreated,
    TransitCreated,
    EventsCreated,
}

internal sealed class GameRuntimeConstructionContext
{
    public LiveSessionController? Session { get; set; }
    public RuntimeLocalPlayerIdentityState? PlayerIdentity { get; set; }
    public RuntimeEntityObjectLifetime? EntityObjects { get; set; }
    public RuntimeInventoryState? Inventory { get; set; }
    public RuntimeCharacterState? Character { get; set; }
    public RuntimeCommunicationState? Communication { get; set; }
    public RuntimeFellowshipState? Fellowship { get; set; }
    public RuntimeAllegianceState? Allegiance { get; set; }
    public RuntimeTradeState? Trade { get; set; }
    public RuntimeContractState? Contracts { get; set; }
    public RuntimeJournalState? Journal { get; set; }
    public RuntimeHouseState? House { get; set; }
    public RuntimeLocalPlayerMovementState? Movement { get; set; }
    public RuntimeActionState? Actions { get; set; }
    public GameRuntimeEventHub? Events { get; set; }
}

public sealed class GameRuntime
    : IGameRuntimeView,
      IRuntimeEventSource,
      IDisposable
{
    private const int TeardownStageCount = 16;

    private readonly object _lifetimeGate = new();
    private readonly Dictionary<long, string> _hostLeases = [];
    private readonly GameRuntimeEventHub _events;
    private long _nextHostLeaseId;
    private int _disposeStage;
    private bool _disposeRequested;
    private bool _disposeDrainActive;
    private bool _disposed;

    public GameRuntime(GameRuntimeDependencies dependencies)
        : this(dependencies, faultInjection: null)
    {
    }

    internal GameRuntime(
        GameRuntimeDependencies dependencies,
        Action<GameRuntimeConstructionPoint, GameRuntimeConstructionContext>?
            faultInjection)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(
            dependencies.CombatAttackOperations);
        ArgumentNullException.ThrowIfNull(
            dependencies.CombatTargetOperations);
        ArgumentNullException.ThrowIfNull(
            dependencies.CombatModeOperations);
        ArgumentNullException.ThrowIfNull(
            dependencies.SpellCastOperations);
        if (dependencies.MaximumChatEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dependencies.MaximumChatEntries));
        }

        var context = new GameRuntimeConstructionContext();
        var construction = new ConstructionTransaction();
        try
        {
            var clock = new GameRuntimeClock();
            Fault(
                GameRuntimeConstructionPoint.ClockCreated,
                context,
                faultInjection);

            context.Session = dependencies.SessionOperations is null
                ? new LiveSessionController(
                    ProductionLiveSessionOperations.Instance,
                    dependencies.TimeProvider,
                    random: dependencies.Random)
                : new LiveSessionController(
                    dependencies.SessionOperations,
                    dependencies.TimeProvider,
                    random: dependencies.Random);
            construction.Own(context.Session);
            Fault(
                GameRuntimeConstructionPoint.SessionCreated,
                context,
                faultInjection);

            context.PlayerIdentity = new RuntimeLocalPlayerIdentityState();
            construction.Own(context.PlayerIdentity);
            Fault(
                GameRuntimeConstructionPoint.PlayerIdentityCreated,
                context,
                faultInjection);

            context.EntityObjects = new RuntimeEntityObjectLifetime(
                dependencies.FirstLocalEntityId,
                dependencies.TimeProvider,
                clock);
            construction.Own(context.EntityObjects);
            Fault(
                GameRuntimeConstructionPoint.EntityObjectsCreated,
                context,
                faultInjection);

            context.Inventory = new RuntimeInventoryState(
                context.EntityObjects);
            construction.Own(context.Inventory);
            Fault(
                GameRuntimeConstructionPoint.InventoryCreated,
                context,
                faultInjection);

            context.Character = new RuntimeCharacterState(
                timeProvider: dependencies.TimeProvider);
            construction.Own(context.Character);
            Fault(
                GameRuntimeConstructionPoint.CharacterCreated,
                context,
                faultInjection);

            context.Communication = new RuntimeCommunicationState(
                dependencies.MaximumChatEntries);
            construction.Own(context.Communication);
            Fault(
                GameRuntimeConstructionPoint.CommunicationCreated,
                context,
                faultInjection);

            context.Fellowship = new RuntimeFellowshipState(
                timeProvider: dependencies.TimeProvider);
            construction.Own(context.Fellowship);
            Fault(
                GameRuntimeConstructionPoint.FellowshipCreated,
                context,
                faultInjection);

            context.Allegiance = new RuntimeAllegianceState();
            construction.Own(context.Allegiance);
            Fault(
                GameRuntimeConstructionPoint.AllegianceCreated,
                context,
                faultInjection);

            context.Trade = new RuntimeTradeState(context.EntityObjects!.Objects);
            construction.Own(context.Trade);
            Fault(
                GameRuntimeConstructionPoint.TradeCreated,
                context,
                faultInjection);

            context.Contracts = new RuntimeContractState();
            construction.Own(context.Contracts);
            Fault(
                GameRuntimeConstructionPoint.ContractsCreated,
                context,
                faultInjection);

            context.Journal = new RuntimeJournalState();
            construction.Own(context.Journal);
            Fault(
                GameRuntimeConstructionPoint.JournalCreated,
                context,
                faultInjection);

            context.House = new RuntimeHouseState(context.EntityObjects.Objects);
            Fault(
                GameRuntimeConstructionPoint.HouseCreated,
                context,
                faultInjection);

            context.Movement = new RuntimeLocalPlayerMovementState();
            context.Movement.OnInterfaceText =
                (text, type) => context.Communication.AddText(text, type);
            construction.Own(context.Movement);
            Fault(
                GameRuntimeConstructionPoint.MovementCreated,
                context,
                faultInjection);

            context.Actions = new RuntimeActionState(
                context.Inventory.Transactions,
                context.Character.Spellbook,
                dependencies.CombatAttackOperations,
                dependencies.CombatTargetOperations,
                dependencies.CombatModeOperations,
                dependencies.SpellCastOperations,
                dependencies.CombatTime);
            construction.Own(context.Actions);
            Fault(
                GameRuntimeConstructionPoint.ActionsCreated,
                context,
                faultInjection);

            var environment = new RuntimeWorldEnvironmentState(
                dependencies.TimeProvider,
                dependencies.Log,
                dependencies.TimeSyncDiagnostic);
            Fault(
                GameRuntimeConstructionPoint.EnvironmentCreated,
                context,
                faultInjection);

            var transit = new RuntimeWorldTransitState(dependencies.Log);
            Fault(
                GameRuntimeConstructionPoint.TransitCreated,
                context,
                faultInjection);

            var generationReset = new RuntimeGenerationReset(
                transit,
                context.Communication,
                context.Inventory,
                context.Actions,
                context.Movement,
                context.EntityObjects,
                context.Character,
                context.PlayerIdentity,
                context.Fellowship,
                context.Allegiance,
                context.Trade,
                context.House,
                context.Contracts,
                context.Journal);

            context.Movement.AttachPhysicsPublication(
                new RuntimeLocalPlayerPhysicsPublicationState(
                    context.EntityObjects.Entities,
                    context.EntityObjects.Physics,
                    context.Movement,
                    context.PlayerIdentity));
            context.EntityObjects.LocalPlayerFirstEntry.BindPublication(
                context.Movement.PhysicsPublication);

            context.EntityObjects.BindEventContext(
                () => generationReset.ActiveRetiringGeneration
                    ?? context.Session.Generation,
                () => clock.FrameNumber);
            context.EntityObjects.BindLiveInputs(
                () => context.Character.UsePositionFromServer,
                () => context.Movement.Controller?.Position);
            context.Events = new GameRuntimeEventHub(
                context.EntityObjects,
                context.Communication,
                context.Actions);
            construction.Own(context.Events);
            Fault(
                GameRuntimeConstructionPoint.EventsCreated,
                context,
                faultInjection);

            Clock = clock;
            Session = context.Session;
            PlayerIdentity = context.PlayerIdentity;
            EntityObjects = context.EntityObjects;
            InventoryOwner = context.Inventory;
            CharacterOwner = context.Character;
            CommunicationOwner = context.Communication;
            FellowshipOwner = context.Fellowship;
            AllegianceOwner = context.Allegiance;
            TradeOwner = context.Trade;
            ContractsOwner = context.Contracts;
            JournalOwner = context.Journal;
            HouseOwner = context.House;
            MovementOwner = context.Movement;
            ActionOwner = context.Actions;
            EnvironmentOwner = environment;
            TransitOwner = transit;
            GenerationReset = generationReset;
            _events = context.Events;

            context.Session.ConfigureAutoSaveTick(
                session =>
                {
                    if (CharacterOwner.Options.IsDirty)
                        FlushCharacterOptions(session, ifAutoSaveDue: true);
                });
            context.Session.ConfigurePreLogoffFlush(
                session =>
                {
                    if (CharacterOwner.Options.IsDirty)
                        FlushCharacterOptions(session, ifAutoSaveDue: false);
                });

            construction.Complete();
        }
        catch (Exception failure)
        {
            construction.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private void FlushCharacterOptions(WorldSession session, bool ifAutoSaveDue)
    {
        RuntimeCharacterOptionsState options = CharacterOwner.Options;
        if (!options.IsDirty)
            return;

        void SendBlob()
        {
            CharacterOptionsBlobEcho echo = CharacterOptionsBlobSource.Capture(
                CharacterOwner,
                InventoryOwner.Shortcuts);
            session.SendSetCharacterOptions(
                echo.Options1,
                echo.Options2,
                echo.Shortcuts,
                echo.FavoriteSpells,
                echo.DesiredComponents,
                echo.SpellbookFilters);
        }

        if (ifAutoSaveDue)
            options.TryFlushIfAutoSaveDue(SendBlob);
        else
            options.TryFlush(SendBlob);
    }

    public GameRuntimeClock Clock { get; }
    public LiveSessionController Session { get; }
    public RuntimeLocalPlayerIdentityState PlayerIdentity { get; }
    public RuntimeEntityObjectLifetime EntityObjects { get; }
    public RuntimeInventoryState InventoryOwner { get; }
    public RuntimeCharacterState CharacterOwner { get; }
    public RuntimeCommunicationState CommunicationOwner { get; }
    public RuntimeFellowshipState FellowshipOwner { get; }
    public RuntimeAllegianceState AllegianceOwner { get; }

    public RuntimeTradeState TradeOwner { get; }
    public RuntimeContractState ContractsOwner { get; }
    public RuntimeJournalState JournalOwner { get; }

    public RuntimeHouseState HouseOwner { get; }
    public RuntimeActionState ActionOwner { get; }
    public RuntimeLocalPlayerMovementState MovementOwner { get; }
    internal RuntimeLocalPlayerPhysicsPublicationState
        LocalPlayerPhysicsPublication => MovementOwner.PhysicsPublication;
    public RuntimeWorldEnvironmentState EnvironmentOwner { get; }
    public RuntimeWorldTransitState TransitOwner { get; }
    public RuntimeGenerationReset GenerationReset { get; }
    public RuntimePlacementProjectionChannel Placements =>
        EntityObjects.Placements;

    public RuntimeGenerationToken Generation => Session.Generation;

    public RuntimeLifecycleSnapshot Lifecycle
    {
        get
        {
            RuntimeLifecycleState state;
            if (_disposed)
                state = RuntimeLifecycleState.Disposed;
            else if (Session.IsInWorld)
                state = RuntimeLifecycleState.InWorld;
            else if (Session.CurrentSession is not null)
                state = RuntimeLifecycleState.Starting;
            else if (Session.SessionGeneration == 0UL)
                state = RuntimeLifecycleState.Constructed;
            else
                state = RuntimeLifecycleState.Stopped;

            return new RuntimeLifecycleSnapshot(
                Generation,
                state,
                PlayerIdentity.ServerGuid,
                Session.CurrentSession is not null);
        }
    }

    IGameRuntimeClock IGameRuntimeView.Clock => Clock;
    public IRuntimeEntityView Entities => EntityObjects.EntityView;
    public IRuntimeInventoryView Inventory => EntityObjects.InventoryView;
    public IRuntimeInventoryStateView InventoryState => InventoryOwner.View;
    public IRuntimeCharacterView Character => CharacterOwner.View;
    public IRuntimeSocialView Social => CommunicationOwner.SocialView;
    public IRuntimeChatView Chat => CommunicationOwner.View;
    public IRuntimeCharacterSelectionView CharacterSelection =>
        Session.CharacterSelection;
    public IRuntimeConnectionView Connection => Session.Connection;
    public IRuntimeCharacterCreationView CharacterCreation =>
        Session.CharacterCreation;
    public IRuntimeFellowshipView Fellowship => FellowshipOwner.View;
    public IRuntimeAllegianceView Allegiance => AllegianceOwner.View;

    public IRuntimeTradeView Trade => TradeOwner.View;
    public IRuntimeContractView Contracts => ContractsOwner.View;
    public IRuntimeJournalView Journal => JournalOwner.View;
    public IRuntimeActionView Actions => ActionOwner.View;
    public IRuntimeMovementView Movement => MovementOwner.View;
    public IRuntimeWorldEnvironmentView Environment => EnvironmentOwner;
    public IRuntimePortalView Portal => TransitOwner;

    internal IGameRuntimeEventSink EventSink => _events;

    public RuntimeStateCheckpoint CaptureCheckpoint() =>
        new(
            Generation,
            Lifecycle.State,
            Clock.FrameNumber,
            Entities.Count,
            Entities.MaterializedCount,
            Inventory.ObjectCount,
            Inventory.ContainerCount,
            InventoryState.Snapshot,
            Character.Snapshot,
            Social.Snapshot,
            Chat.Revision,
            Chat.Count,
            Actions.Snapshot,
            Movement.Snapshot,
            Environment.Snapshot,
            Environment.Ownership,
            Portal.Snapshot,
            Portal.Ownership,
            Fellowship.Snapshot,
            Allegiance.Snapshot);

    public RuntimeLocalPlayerFrameController CreateLocalPlayerFrameController(
        IRuntimeLocalPlayerFrameHost host,
        IRuntimeMovementInputSource input)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        return new RuntimeLocalPlayerFrameController(
            host,
            input,
            () =>
            {
                _events.EmitMovement(MovementOwner.Snapshot);
                RuntimeVendorRangeQuery.EnforceRange(this);
            });
    }

    public void ResetGeneration(
        RuntimeGenerationToken retiringGeneration,
        IRuntimeGenerationResetHost host) =>
        GenerationReset.Reset(retiringGeneration, host);

    public IDisposable Subscribe(IRuntimeEventObserver observer)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(
                _disposeRequested || _disposed,
                this);
            return _events.Subscribe(observer);
        }
    }

    public IDisposable AcquireHostLease(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(
                _disposeRequested || _disposed,
                this);
            long id = checked(++_nextHostLeaseId);
            _hostLeases.Add(id, name);
            return new HostLease(this, id);
        }
    }

    public GameRuntimeOwnershipSnapshot CaptureOwnership()
    {
        lock (_lifetimeGate)
        {
            return new GameRuntimeOwnershipSnapshot(
                _disposeRequested,
                _disposeDrainActive,
                _disposed,
                _hostLeases.Count,
                CompletedTeardownStages,
                Session.CaptureOwnership(),
                PlayerIdentity.CaptureOwnership(),
                RuntimeSimulationOwnership.Capture(
                    EntityObjects,
                    InventoryOwner,
                    CharacterOwner,
                    CommunicationOwner,
                    ActionOwner,
                    MovementOwner,
                    FellowshipOwner,
                    AllegianceOwner,
                    TradeOwner),
                EnvironmentOwner.CaptureOwnership(),
                TransitOwner.CaptureOwnership(),
                GenerationReset.CaptureSnapshot(),
                _events.CaptureOwnership());
        }
    }

    /// <summary>
    /// Makes transport and all per-generation routes inert while retaining the
    /// root for ordered host projection teardown.
    /// </summary>
    public void StopSession()
    {
        Session.Dispose();
        if (!Session.IsDisposalComplete)
        {
            throw new InvalidOperationException(
                "The Runtime session shutdown was deferred by a re-entrant callback.");
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _disposeDrainActive)
                return;
            _disposeRequested = true;
            _disposeDrainActive = true;
        }

        List<Exception>? completedStageFailures = null;
        try
        {
            while (_disposeStage < TeardownStageCount)
            {
                bool complete;
                try
                {
                    complete = DrainCurrentStage();
                }
                catch (Exception error)
                {
                    complete = IsCurrentStageComplete();
                    if (!complete)
                        throw;
                    (completedStageFailures ??= []).Add(error);
                }

                if (!complete)
                {
                    throw new InvalidOperationException(
                        $"GameRuntime teardown stage {_disposeStage} did not complete.");
                }
                _disposeStage++;
            }

            lock (_lifetimeGate)
                _disposed = true;
        }
        finally
        {
            lock (_lifetimeGate)
                _disposeDrainActive = false;
        }

        if (completedStageFailures is not null)
        {
            throw new AggregateException(
                "GameRuntime reached terminal ownership with callback failures.",
                completedStageFailures);
        }
    }

    private GameRuntimeTeardownStage CompletedTeardownStages =>
        _disposeStage switch
        {
            <= 0 => GameRuntimeTeardownStage.None,
            1 => GameRuntimeTeardownStage.HostLeasesReleased,
            2 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached,
            3 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached
                | GameRuntimeTeardownStage.SessionDisposed,
            4 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached
                | GameRuntimeTeardownStage.SessionDisposed
                | GameRuntimeTeardownStage.TransitReset,
            5 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached
                | GameRuntimeTeardownStage.SessionDisposed
                | GameRuntimeTeardownStage.TransitReset
                | GameRuntimeTeardownStage.ActionsDisposed,
            6 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached
                | GameRuntimeTeardownStage.SessionDisposed
                | GameRuntimeTeardownStage.TransitReset
                | GameRuntimeTeardownStage.ActionsDisposed
                | GameRuntimeTeardownStage.MovementDisposed,
            7 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached
                | GameRuntimeTeardownStage.SessionDisposed
                | GameRuntimeTeardownStage.TransitReset
                | GameRuntimeTeardownStage.ActionsDisposed
                | GameRuntimeTeardownStage.MovementDisposed
                | GameRuntimeTeardownStage.CharacterDisposed,
            8 => GameRuntimeTeardownStage.HostLeasesReleased
                | GameRuntimeTeardownStage.EventsDetached
                | GameRuntimeTeardownStage.SessionDisposed
                | GameRuntimeTeardownStage.TransitReset
                | GameRuntimeTeardownStage.ActionsDisposed
                | GameRuntimeTeardownStage.MovementDisposed
                | GameRuntimeTeardownStage.CharacterDisposed
                | GameRuntimeTeardownStage.InventoryDisposed,
            9 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.FellowshipDisposed
                & ~GameRuntimeTeardownStage.AllegianceDisposed
                & ~GameRuntimeTeardownStage.TradeDisposed
                & ~GameRuntimeTeardownStage.ContractsDisposed
                & ~GameRuntimeTeardownStage.JournalDisposed
                & ~GameRuntimeTeardownStage.IdentityDisposed
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            10 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.AllegianceDisposed
                & ~GameRuntimeTeardownStage.TradeDisposed
                & ~GameRuntimeTeardownStage.ContractsDisposed
                & ~GameRuntimeTeardownStage.JournalDisposed
                & ~GameRuntimeTeardownStage.IdentityDisposed
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            11 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.TradeDisposed
                & ~GameRuntimeTeardownStage.ContractsDisposed
                & ~GameRuntimeTeardownStage.JournalDisposed
                & ~GameRuntimeTeardownStage.IdentityDisposed
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            12 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.ContractsDisposed
                & ~GameRuntimeTeardownStage.JournalDisposed
                & ~GameRuntimeTeardownStage.IdentityDisposed
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            13 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.JournalDisposed
                & ~GameRuntimeTeardownStage.IdentityDisposed
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            14 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.IdentityDisposed
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            15 => GameRuntimeTeardownStage.Complete
                & ~GameRuntimeTeardownStage.EntityObjectsDisposed,
            _ => GameRuntimeTeardownStage.Complete,
        };

    private bool DrainCurrentStage()
    {
        switch (_disposeStage)
        {
            case 0:
                lock (_lifetimeGate)
                {
                    if (_hostLeases.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "GameRuntime cannot retire while host leases remain: "
                            + string.Join(", ", _hostLeases.Values));
                    }
                }
                return true;
            case 1:
                _events.Dispose();
                return _events.CaptureOwnership().IsConverged;
            case 2:
                StopSession();
                return Session.CaptureOwnership().IsConverged;
            case 3:
                GenerationReset.DrainPending();
                TransitOwner.ResetSession();
                return TransitOwner.CaptureOwnership().IsSessionIdle;
            case 4:
                ActionOwner.Dispose();
                return ActionOwner.CaptureOwnership().IsConverged;
            case 5:
                MovementOwner.Dispose();
                return MovementOwner.CaptureOwnership().IsConverged;
            case 6:
                CharacterOwner.Dispose();
                return CharacterOwner.CaptureOwnership().IsConverged;
            case 7:
                InventoryOwner.Dispose();
                return InventoryOwner.CaptureOwnership().IsConverged;
            case 8:
                CommunicationOwner.Dispose();
                return CommunicationOwner.CaptureOwnership().IsConverged;
            case 9:
                FellowshipOwner.Dispose();
                return FellowshipOwner.CaptureOwnership().IsConverged;
            case 10:
                AllegianceOwner.Dispose();
                return AllegianceOwner.CaptureOwnership().IsConverged;
            case 11:
                TradeOwner.Dispose();
                return TradeOwner.CaptureOwnership().IsConverged;
            case 12:
                ContractsOwner.Dispose();
                return ContractsOwner.CaptureOwnership().IsConverged;
            case 13:
                JournalOwner.Dispose();
                return JournalOwner.CaptureOwnership().IsConverged;
            case 14:
                PlayerIdentity.Dispose();
                return PlayerIdentity.CaptureOwnership().IsConverged;
            case 15:
                EntityObjects.Dispose();
                return EntityObjects.CaptureOwnership().IsConverged
                    && EntityObjects.Physics.CaptureOwnership().IsConverged;
            default:
                return true;
        }
    }

    private bool IsCurrentStageComplete() =>
        _disposeStage switch
        {
            0 => HostLeaseCount == 0,
            1 => _events.CaptureOwnership().IsConverged,
            2 => Session.CaptureOwnership().IsConverged,
            3 => TransitOwner.CaptureOwnership().IsSessionIdle,
            4 => ActionOwner.CaptureOwnership().IsConverged,
            5 => MovementOwner.CaptureOwnership().IsConverged,
            6 => CharacterOwner.CaptureOwnership().IsConverged,
            7 => InventoryOwner.CaptureOwnership().IsConverged,
            8 => CommunicationOwner.CaptureOwnership().IsConverged,
            9 => FellowshipOwner.CaptureOwnership().IsConverged,
            10 => AllegianceOwner.CaptureOwnership().IsConverged,
            11 => TradeOwner.CaptureOwnership().IsConverged,
            12 => ContractsOwner.CaptureOwnership().IsConverged,
            13 => JournalOwner.CaptureOwnership().IsConverged,
            14 => PlayerIdentity.CaptureOwnership().IsConverged,
            15 => EntityObjects.CaptureOwnership().IsConverged
                && EntityObjects.Physics.CaptureOwnership().IsConverged,
            _ => true,
        };

    private int HostLeaseCount
    {
        get
        {
            lock (_lifetimeGate)
                return _hostLeases.Count;
        }
    }

    private void ReleaseHostLease(long id)
    {
        lock (_lifetimeGate)
            _hostLeases.Remove(id);
    }

    private static void Fault(
        GameRuntimeConstructionPoint point,
        GameRuntimeConstructionContext context,
        Action<GameRuntimeConstructionPoint, GameRuntimeConstructionContext>?
            faultInjection) =>
        faultInjection?.Invoke(point, context);

    private sealed class HostLease(GameRuntime owner, long id) : IDisposable
    {
        private GameRuntime? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseHostLease(id);
    }

    private sealed class ConstructionTransaction
    {
        private readonly List<IDisposable> _owners = [];
        private bool _complete;

        public void Own(IDisposable owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            if (_complete)
                throw new InvalidOperationException(
                    "Runtime construction already completed.");
            _owners.Add(owner);
        }

        public void Complete()
        {
            _complete = true;
            _owners.Clear();
        }

        public void RollbackAndThrow(Exception failure)
        {
            var failures = new List<Exception> { failure };
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                try
                {
                    _owners[i].Dispose();
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }
            }
            _owners.Clear();
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
            throw new AggregateException(
                "GameRuntime construction and rollback both failed.",
                failures);
        }
    }
}
