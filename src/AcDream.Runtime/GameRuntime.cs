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
    /// <summary>
    /// Where the attack power-up reads the time from. Left out, it is the
    /// runtime's own simulation clock, which is what both hosts want.
    /// </summary>
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
    BookCreated,
    HouseCreated,
    MovementCreated,
    ActionsCreated,
    ItemInteractionCreated,
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
    public RuntimeBookState? Book { get; set; }
    public RuntimeHouseState? House { get; set; }
    public RuntimeLocalPlayerMovementState? Movement { get; set; }
    public RuntimeActionState? Actions { get; set; }
    public RuntimeItemInteraction? ItemInteraction { get; set; }
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
    private RuntimeLifecycleState _lastEmittedLifecycleState = RuntimeLifecycleState.Constructed;
    private bool _leftWorldSinceLastEmission;
    private ulong _leftWorldGeneration;
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

            context.Book = new RuntimeBookState(
                () => context.PlayerIdentity!.ServerGuid);
            Fault(
                GameRuntimeConstructionPoint.BookCreated,
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
                // The power-up is timed off the simulation clock unless a
                // host insists otherwise, so both clients build a swing at
                // the same rate whether or not there is a window to draw it.
                dependencies.CombatTime
                    ?? (() => clock.SimulationTimeSeconds));
            construction.Own(context.Actions);
            Fault(
                GameRuntimeConstructionPoint.ActionsCreated,
                context,
                faultInjection);

            context.ItemInteraction = RuntimeItemInteractionComposition.Create(
                context.Session,
                context.PlayerIdentity,
                context.Inventory,
                context.Actions,
                context.Character,
                context.Communication,
                clock);
            construction.Own(context.ItemInteraction);
            Fault(
                GameRuntimeConstructionPoint.ItemInteractionCreated,
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
                context.Journal,
                context.Book);

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
            BookOwner = context.Book;
            HouseOwner = context.House;
            MovementOwner = context.Movement;
            ActionOwner = context.Actions;
            ItemInteractionOwner = context.ItemInteraction;
            EnvironmentOwner = environment;
            TransitOwner = transit;
            GenerationReset = generationReset;
            _events = context.Events;

            // A combat-mode change asks the body whether it is in position
            // and the session whether a teleport is under way, on every
            // client alike, so it is bound here from runtime state alone.
            ActionOwner.CombatMode.BindReadiness(
                new RuntimeCombatModeReadiness(this));

            // The walk-to-then-use route. It is built here, from runtime
            // state alone, so a client with no window reaches an object the
            // character does not own exactly the way a client with one does.
            // The selection follows the world: when the object the player
            // has selected is taken out of it, or the world stops showing
            // it, the selection lets it go. One owner, for every client.
            _selectionFollowsEntities = new RuntimeSelectionEntityFollower(
                context.EntityObjects.Events,
                context.Actions.Selection);

            ApproachCompletions = new RuntimeApproachCompletionState();
            context.Movement.AttachApproachCompletions(ApproachCompletions);
            var approachSource = new RuntimeApproachSource(
                this,
                ApproachCompletions);
            WorldObjectUseOwner = new RuntimeWorldObjectUse(
                context.ItemInteraction,
                new RuntimeSessionInteractionTransport(
                    () => context.Session.CurrentSession),
                approachSource,
                guid =>
                    guid != 0u
                    && context.EntityObjects.Entities.TryGetActive(
                        guid,
                        out Entities.RuntimeEntityRecord record)
                        ? record.Snapshot.Useability
                        : null,
                dependencies.Log);
            // The end of that walk: arrival sends what was armed for it, and a
            // walk that has stopped getting anywhere is given up on. Both are
            // taken once per frame, from the per-frame local-player step every
            // client takes, so a use out of reach finishes with or without a
            // window.
            ArmedApproachDrive = new RuntimeInteractionApproachDriver(
                ApproachCompletions,
                context.ItemInteraction.RuntimeTransactions,
                WorldObjectUseOwner,
                approachSource,
                dependencies.Log);
            // How fast the character runs and jumps follows what the server
            // says about its skills, burden and stamina. One owner, so a
            // client without a window does not read stale speeds.
            MovementStats = new Gameplay.RuntimeMovementStatsApplier(
                context.Movement,
                context.Character.MovementSkills,
                dependencies.Log ?? (static _ => { }));
            GhostDismissalOwner = new Entities.RuntimeGhostDismissal(
                context.EntityObjects,
                () => PlayerIdentity.ServerGuid);
            SelectionCycleOwner = new RuntimeSelectionCycle(this);
            // The character carries itself forward by its own animation
            // cycles on every client. What a client with something to
            // draw adds is the pose work, hung off the same one advance.
            LocalPlayerMotion = new Gameplay.RuntimeLocalPlayerMotionArming(
                context.EntityObjects,
                () => PlayerIdentity.ServerGuid);

            context.Session.ConfigureAutoSaveTick(
                session =>
                {
                    if (CharacterOwner.Options.IsDirty)
                        FlushCharacterOptions(session, ifAutoSaveDue: true);
                });
            context.Session.ConfigureLeavingWorld(AnnounceLeavingWorld);
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

    /// <summary>
    /// The stop a swing asks for, on its way out. It carries no movement
    /// diagnostic: what it sends is one packet at one moment, not a stream
    /// worth watching.
    /// </summary>
    private readonly LocalPlayerOutboundController _attackRequestOutbound =
        new(static (_, _, _, _, _, _) => { });

    /// <summary>Clears the selection when its object leaves the world.</summary>
    private readonly RuntimeSelectionEntityFollower? _selectionFollowsEntities;

    public GameRuntimeClock Clock { get; }

    /// <summary>
    /// Takes one host frame off the clock, for whichever client is running.
    /// The frame number counts host frames and always moves. Simulation time
    /// is the world's own clock, and it only moves while there is a world to
    /// simulate: between giving up the world the character was standing in
    /// and standing up the next one -- through a portal, or arriving at
    /// login -- elapsed time means nothing, and time that ran through the gap
    /// would be handed in one lump to whatever moves on the first frame
    /// after it.
    /// </summary>
    /// <param name="hostDeltaSeconds">
    /// How long the host frame took. Anything that is not a finite, positive
    /// number of seconds counts as no time at all.
    /// </param>
    public RuntimeFrameTime AdvanceFrameClock(double hostDeltaSeconds) =>
        Clock.Advance(
            hostDeltaSeconds,
            TransitOwner.IsWorldSimulationAvailable);

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
    public RuntimeBookState BookOwner { get; }

    public RuntimeHouseState HouseOwner { get; }
    public RuntimeActionState ActionOwner { get; }

    /// <summary>
    /// The one owner of item and equipment requests. Both hosts borrow it,
    /// so a plugin gets the same answers with or without a window.
    /// </summary>
    public RuntimeItemInteraction ItemInteractionOwner { get; }

    /// <summary>
    /// The one walk-to-then-use route for objects the character does not own.
    /// Both clients answer a plugin from this, so a corpse several meters off
    /// is walked to and opened the same way with or without a window.
    /// </summary>
    internal RuntimeWorldObjectUse WorldObjectUseOwner { get; }

    /// <summary>
    /// Letting go of an object the client still believes in. The decision is
    /// the same on every client; a client that draws lends the route that
    /// takes down what it drew.
    /// </summary>
    public Entities.RuntimeGhostDismissal GhostDismissalOwner { get; }

    /// <summary>
    /// Stepping the selection from one nearby character to the next. The
    /// order is over the entity directory, so a key press and a plugin call
    /// land on the same character on either client.
    /// </summary>
    public RuntimeSelectionCycle SelectionCycleOwner { get; }

    /// <summary>
    /// Where walks sent by that route report that they arrived or were called
    /// off. Whoever gives the character its body begins a run of walks here
    /// and ends it when the body goes.
    /// </summary>
    internal RuntimeApproachCompletionState ApproachCompletions { get; }

    /// <summary>
    /// Ends the walks armed to act on something once they are over. Driven
    /// once per frame from the per-frame local-player step, on every client;
    /// a client with something to draw on lends it the walk-then-pickup half
    /// and the words a person who clicked is told.
    /// </summary>
    internal RuntimeInteractionApproachDriver ArmedApproachDrive { get; }
    public RuntimeLocalPlayerMovementState MovementOwner { get; }

    /// <summary>
    /// Gives the character its own locomotion, on whichever client is
    /// running. One owner, so the character covers the same ground and turns
    /// through the same angle with or without a window.
    /// </summary>
    internal Gameplay.RuntimeLocalPlayerMotionArming LocalPlayerMotion { get; }

    /// <summary>
    /// Re-derives the character's run and jump speed from the numbers the
    /// server last sent. Both clients bind their character-session hooks to
    /// this one instance.
    /// </summary>
    internal Gameplay.RuntimeMovementStatsApplier MovementStats { get; }
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

    /// <summary>
    /// Called as a stay in the world ends, before the session is torn down
    /// and before it stops reporting itself in the world. When the character
    /// was last announced in the world, observers hear that it is leaving
    /// while the state they may want to read is still there; the lifecycle
    /// change itself is still raised afterwards, as before. Once per stay,
    /// whoever calls it.
    /// </summary>
    internal void AnnounceLeavingWorld()
    {
        if (_lastEmittedLifecycleState != RuntimeLifecycleState.InWorld
            || _leftWorldSinceLastEmission)
        {
            return;
        }
        _leftWorldSinceLastEmission = true;
        _leftWorldGeneration = Session.SessionGeneration;
        _events.EmitLeavingWorld();
    }

    /// <summary>
    /// The single lifecycle-emission gate: compares the live Lifecycle.State
    /// against the last state this call itself emitted and raises exactly
    /// one EmitLifecycle for any observed change, deduped by
    /// previous == current. Callable from a command boundary (Start/
    /// Reconnect/Stop, where the change is usually already visible by the
    /// time the call returns) and from the per-frame session tick (where
    /// the async connect-to-in-world edge, and any other edge a command
    /// boundary did not just observe, actually lands) without double-
    /// firing when both paths converge on the same state in one frame.
    /// A stay that ended and a new one that began inside one call (a
    /// logoff straight into the next character) still reads in-world on
    /// both sides; the announced leave and the new generation tell them
    /// apart, and the gate then raises the leave and the arrival as two changes.
    /// </summary>
    internal void SyncLifecycleEmission()
    {
        RuntimeLifecycleState current = Lifecycle.State;
        RuntimeLifecycleState previous = _lastEmittedLifecycleState;
        bool reentered = _leftWorldSinceLastEmission
            && current == RuntimeLifecycleState.InWorld
            && Session.SessionGeneration != _leftWorldGeneration;
        if (previous == current && !reentered)
            return;
        _leftWorldSinceLastEmission = false;
        if (previous == current)
        {
            _lastEmittedLifecycleState = RuntimeLifecycleState.Starting;
            _events.EmitLifecycle(previous, RuntimeLifecycleState.Starting);
            previous = RuntimeLifecycleState.Starting;
        }
        _lastEmittedLifecycleState = current;
        if (current == RuntimeLifecycleState.InWorld)
            AskTheServerAboutTheAllegiance();
        _events.EmitLifecycle(previous, current);
    }

    /// <summary>
    /// The one request the runtime itself makes on arriving in the world.
    /// The server says nothing about a character's allegiance unless it is
    /// asked, and until it does, every allegiance question -- who the patron
    /// is, who the vassals are, whether a break has anyone to break with --
    /// answers "nobody". Asking here rather than from a panel is what the
    /// original client does, and it is the only way a client with no panels
    /// open, or no panels at all, knows any of it.
    /// </summary>
    private void AskTheServerAboutTheAllegiance()
    {
        if (Session.CurrentSession is not { } session)
            return;
        if (AllegianceOwner.NoteEnteredWorld())
            session.SendAllegianceUpdateRequest(true);
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
    public IRuntimeBookView Book => BookOwner.View;
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

    /// <summary>
    /// Stops the character for a swing and tells the server it stopped.
    /// A character asked to swing while it is running has to stand still
    /// first, and the server has to hear about that at once or it works the
    /// swing out from where it still believed the character was. One
    /// implementation, so a swing costs the same wherever it was asked for.
    /// </summary>
    public void PrepareLocalPlayerForAttackRequest()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        if (!MovementOwner.PrepareForAttackRequest()
            || MovementOwner.Controller is not { } controller)
        {
            return;
        }

        _ = _attackRequestOutbound.TrySendMovement(
            Session.CurrentSession,
            controller,
            controller.CaptureMovementResult(mouseLookEvent: false));
    }

    /// <summary>
    /// Tells everything watching the character where it has got to. A walk
    /// somebody else is running at this character is re-aimed from here, once
    /// per frame.
    /// </summary>
    /// <remarks>
    /// The character's own body only. A creature this character is walking at
    /// tells its own watchers where IT has got to as the last stage of its own
    /// step, so a client that carries other creatures' bodies forward already
    /// re-aims the walk -- and doing it from here as well would re-aim it
    /// twice a frame on one client and once on another, which is the
    /// difference rather than the fix.
    /// </remarks>
    public void HandleLocalPlayerTargeting()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        uint player = PlayerIdentity.ServerGuid;
        if (player == 0u
            || !EntityObjects.Physics.TryGetPhysicsHost(player, out var self)
            || self is not EntityPhysicsHost localBody)
        {
            return;
        }

        localBody.HandleTargetting();
    }

    /// <summary>
    /// Closes this frame's pass over other creatures' bodies, and re-aims a
    /// walk this character is running at a creature the pass did not carry.
    /// </summary>
    /// <remarks>
    /// A creature tells everything watching it where it has got to as the last
    /// stage of its own step, so a walk aimed at a creature whose body is
    /// being carried forward is re-aimed by that. A creature whose body is not
    /// being carried -- one this client has no animation content for, one too
    /// far away to be worth carrying, one that never moves, one carrying
    /// something in flight -- would otherwise never say so, and the walk would
    /// keep running at where that creature was when it was last heard from.
    /// This covers exactly those, once a frame, on every client.
    /// </remarks>
    public void FinishRemoteBodyPass()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        // The one close both clients run every frame after the character's
        // own step, which is where a combat-mode change parked behind a
        // motion learns that the motion has run out.
        ActionOwner.CombatMode.ApplyPendingMode();
        AcDream.Runtime.Physics.RuntimePhysicsState physics =
            EntityObjects.Physics;
        try
        {
            uint player = PlayerIdentity.ServerGuid;
            uint sought =
                MovementOwner.Controller?.Movement.MoveTo?.TopLevelObjectId
                ?? 0u;
            if (sought == 0u
                || sought == player
                || physics.DidCarryRemoteBody(sought))
            {
                return;
            }

            if (physics.ResolveObjectTableHost(sought)
                is AcDream.Runtime.Physics.EntityPhysicsHost soughtBody)
            {
                soughtBody.HandleTargetting();
            }
        }
        finally
        {
            physics.ForgetRemoteBodiesCarried();
        }
    }

    public RuntimeLocalPlayerFrameController CreateLocalPlayerFrameController(
        IRuntimeLocalPlayerFrameHost host,
        IRuntimeMovementInputSource input)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        var frame = new RuntimeLocalPlayerFrameController(
            host,
            new RuntimeScriptedMovementInputSource(MovementOwner, input),
            () =>
            {
                _events.EmitMovement(MovementOwner.Snapshot);
                RuntimeVendorRangeQuery.EnforceRange(this);
            },
            ArmedApproachDrive);
        frame.BindMotionArming(LocalPlayerMotion);
        return frame;
    }

    public void ResetGeneration(
        RuntimeGenerationToken retiringGeneration,
        IRuntimeGenerationResetHost host)
    {
        // A new generation starts without an opinion about whether the
        // character was out of stamina, so the first update after it says so
        // is a crossing and not a repeat.
        MovementStats.Reset();
        GenerationReset.Reset(retiringGeneration, host);
    }

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
                _selectionFollowsEntities?.Dispose();
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
                // The item owner only borrows the action and inventory
                // state it subscribes to, so it detaches first.
                ItemInteractionOwner.Dispose();
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
