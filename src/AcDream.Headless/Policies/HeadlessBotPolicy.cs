using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Policies;

internal interface IHeadlessBotPolicy : IRuntimeEventObserver, IDisposable
{
    bool IsComplete { get; }

    void Tick(IGameRuntimeView view, IGameRuntimeCommands commands);
}

internal static class HeadlessBotPolicyFactory
{
    internal static IHeadlessBotPolicy Create(
        HeadlessBotPolicyDescriptor descriptor,
        GameRuntime runtime,
        Func<GameEvents.CharacterConfirmationRequest?>?
            getPendingConfirmation = null,
        Action<bool>? respondToConfirmation = null,
        FellowshipAllegianceGateCoordinator? gateCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(runtime);
        return descriptor.Id switch
        {
            "idle" => new IdleHeadlessBotPolicy(),
            "lifecycle-smoke" => new LifecycleSmokeHeadlessBotPolicy(),
            "observer-movement" => new ObserverMovementHeadlessBotPolicy(),
            "portal-route-smoke" => new PortalRouteSmokeHeadlessBotPolicy(),
            "jump-probe" => new JumpProbeHeadlessBotPolicy(),
            "fellowship-allegiance-gate" =>
                CreateFellowshipAllegianceGatePolicy(
                    descriptor,
                    runtime,
                    getPendingConfirmation,
                    respondToConfirmation,
                    gateCoordinator),
            _ => throw new HeadlessConfigurationException(
                $"Unknown headless bot policy '{descriptor.Id}'."),
        };
    }

    private static IHeadlessBotPolicy CreateFellowshipAllegianceGatePolicy(
        HeadlessBotPolicyDescriptor descriptor,
        GameRuntime runtime,
        Func<GameEvents.CharacterConfirmationRequest?>? getPendingConfirmation,
        Action<bool>? respondToConfirmation,
        FellowshipAllegianceGateCoordinator? gateCoordinator)
    {
        if (descriptor.Role is not { } role)
        {
            throw new HeadlessConfigurationException(
                "Policy 'fellowship-allegiance-gate' requires a 'role' "
                + "('leader' or 'recruit').");
        }
        if (role == HeadlessBotPolicyRole.Leader
            && (getPendingConfirmation is null
                || respondToConfirmation is null
                || gateCoordinator is null))
        {
            throw new HeadlessConfigurationException(
                "Policy 'fellowship-allegiance-gate' role 'leader' requires "
                + "a confirmation-relay host (the swear confirmation "
                + "targets the patron) and a gate coordinator (to "
                + "name-match the Recruit bot).");
        }
        return role switch
        {
            HeadlessBotPolicyRole.Leader =>
                new FellowshipAllegianceLeaderBotPolicy(
                    runtime,
                    getPendingConfirmation!,
                    respondToConfirmation!,
                    gateCoordinator!),
            HeadlessBotPolicyRole.Recruit =>
                new FellowshipAllegianceRecruitBotPolicy(runtime),
            _ => throw new HeadlessConfigurationException(
                $"Unknown fellowship-allegiance-gate role '{role}'."),
        };
    }
}

internal sealed class IdleHeadlessBotPolicy : IHeadlessBotPolicy
{
    public bool IsComplete => false;

    public void Tick(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class ProbeHeadlessBotPolicy : IHeadlessBotPolicy
{
    public bool IsComplete => true;

    public void Tick(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class LifecycleSmokeHeadlessBotPolicy
    : IHeadlessBotPolicy
{
    private int _stage;
    private ulong _firstGeneration;

    public bool IsComplete => _stage == 3;

    public void Tick(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);
        switch (_stage)
        {
            case 0:
                if (!HasLocalPlayer(view))
                    return;
                Require(commands.Chat.Execute(
                    view.Generation,
                    new RuntimeChatCommand(
                        RuntimeChatChannel.Say,
                        "[acdream headless lifecycle gate]")));
                Require(commands.Portal.Execute(
                    view.Generation,
                    RuntimePortalCommand.RecallLifestone));
                _firstGeneration = view.Generation.Value;
                _stage = 1;
                break;
            case 1:
                if (view.Portal.Snapshot.Kind
                        is not RuntimePortalKind.Portal
                    || !view.Portal.Snapshot.Completed)
                {
                    return;
                }
                RuntimeSessionStartResult reconnect =
                    commands.Session.Reconnect(view.Generation);
                if (reconnect.Status
                    is not (
                        RuntimeSessionStartStatus.Connected
                        or RuntimeSessionStartStatus.Deferred))
                {
                    throw new InvalidOperationException(
                        $"Lifecycle gate reconnect was rejected with {reconnect.Status}.");
                }
                _stage = 2;
                break;
            case 2:
                if (view.Generation.Value <= _firstGeneration
                    || !HasLocalPlayer(view))
                {
                    return;
                }
                _stage = 3;
                break;
        }
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }

    private static bool HasLocalPlayer(IGameRuntimeView view) =>
        view.Lifecycle.State is RuntimeLifecycleState.InWorld
        && view.Lifecycle.PlayerGuid != 0u
        && view.Entities.TryGet(
            view.Lifecycle.PlayerGuid,
            out _);

    private static void Require(in RuntimeCommandResult result)
    {
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"Lifecycle gate command was rejected with {result.Status}.");
        }
    }
}

internal sealed class ObserverMovementHeadlessBotPolicy
    : IHeadlessBotPolicy
{
    private const uint RynthidCell = 0xF6820033u;
    private int _stage;
    private long _issuedAfterPortalGeneration;
    private double _movementDeadline;
    private bool _turning;

    public bool IsComplete => false;

    public void Tick(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);

        if (!HasLocalPlayer(view))
            return;

        if (_stage == 0)
        {
            _issuedAfterPortalGeneration =
                view.Portal.Snapshot.Generation;
            Require(commands.Chat.Execute(
                view.Generation,
                new RuntimeChatCommand(
                    RuntimeChatChannel.Say,
                    "@teleloc 0xF6820033 145.7 49.855 58.010 1 0 0 0")));
            _stage = 1;
            return;
        }

        if (_stage == 1)
        {
            RuntimePortalSnapshot portal = view.Portal.Snapshot;
            if (portal.Generation <= _issuedAfterPortalGeneration
                || portal.Kind is not RuntimePortalKind.Portal
                || !portal.Completed)
            {
                return;
            }
            if (portal.DestinationCell != RynthidCell)
            {
                throw new InvalidOperationException(
                    $"Observer gate completed at "
                    + $"0x{portal.DestinationCell:X8}; expected "
                    + $"0x{RynthidCell:X8}.");
            }

            Require(commands.Chat.Execute(
                view.Generation,
                new RuntimeChatCommand(
                    RuntimeChatChannel.Say,
                    "[acdream headless observer active]")));
            StartMovement(view, commands);
            _stage = 2;
            return;
        }

        if (view.Clock.SimulationTimeSeconds
            < _movementDeadline)
        {
            return;
        }

        StartMovement(view, commands);
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }

    private void StartMovement(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        _turning = !_turning;
        MovementInput input = _turning
            ? new MovementInput(TurnRight: true)
            : new MovementInput(Forward: true, Run: true);
        Require(commands.Movement.SetIntent(
            view.Generation,
            input));
        _movementDeadline =
            view.Clock.SimulationTimeSeconds
            + (_turning ? 1.25 : 4.0);
    }

    private static bool HasLocalPlayer(
        IGameRuntimeView view) =>
        view.Lifecycle.State
            is RuntimeLifecycleState.InWorld
        && view.Lifecycle.PlayerGuid != 0u
        && view.Entities.TryGet(
            view.Lifecycle.PlayerGuid,
            out _);

    private static void Require(
        in RuntimeCommandResult result)
    {
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"Observer gate command was rejected with "
                + $"{result.Status}.");
        }
    }
}

internal sealed class PortalRouteSmokeHeadlessBotPolicy
    : IHeadlessBotPolicy
{
    private const uint AerlintheCell = 0x09040008u;
    private const uint CaulCell = 0xC95B0001u;
    private const uint RynthidCell = 0xF6820033u;

    private static readonly RouteStop[] Stops =
    [
        new(
            "aerlinthe",
            "@teleloc 0x09040008 11.4 188.6 87.705 1 0 0 0",
            AerlintheCell),
        new(
            "caul",
            "@teleloc 0xC95B0001 14.8 0.3 12.005 1 0 0 0",
            CaulCell),
        new(
            "rynthid",
            "@teleloc 0xF6820033 145.7 49.855 58.010 1 0 0 0",
            RynthidCell),
        new("lifestone", null, null),
    ];

    private int _nextStop;
    private bool _awaitingPortal;
    private long _issuedAfterPortalGeneration;
    private RouteMovementStage _movementStage;
    private double _movementDeadline;

    public bool IsComplete =>
        _nextStop == Stops.Length
        && !_awaitingPortal
        && _movementStage is RouteMovementStage.None;

    public void Tick(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);

        if (!HasLocalPlayer(view))
            return;

        if (_awaitingPortal)
        {
            RuntimePortalSnapshot portal = view.Portal.Snapshot;
            if (portal.Generation <= _issuedAfterPortalGeneration
                || portal.Kind is not RuntimePortalKind.Portal
                || !portal.Completed)
            {
                return;
            }

            RouteStop completed = Stops[_nextStop];
            if (completed.ExpectedCell is uint expectedCell
                && portal.DestinationCell != expectedCell)
            {
                throw new InvalidOperationException(
                    $"Portal route '{completed.Name}' completed at "
                    + $"0x{portal.DestinationCell:X8}; expected "
                    + $"0x{expectedCell:X8}.");
            }

            _awaitingPortal = false;
            StartMovement(
                view,
                commands,
                RouteMovementStage.ForwardOne);
            return;
        }

        if (_movementStage is not RouteMovementStage.None)
        {
            TickMovement(view, commands);
            return;
        }

        if (_nextStop == Stops.Length)
            return;

        RouteStop stop = Stops[_nextStop];
        _issuedAfterPortalGeneration = view.Portal.Snapshot.Generation;
        RuntimeCommandResult result = stop.ServerCommand is string text
            ? commands.Chat.Execute(
                view.Generation,
                new RuntimeChatCommand(RuntimeChatChannel.Say, text))
            : commands.Portal.Execute(
                view.Generation,
                RuntimePortalCommand.RecallLifestone);
        Require(result, stop.Name);
        _awaitingPortal = true;
    }

    private void TickMovement(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        if (view.Clock.SimulationTimeSeconds < _movementDeadline)
            return;

        switch (_movementStage)
        {
            case RouteMovementStage.ForwardOne:
                StartMovement(
                    view,
                    commands,
                    RouteMovementStage.TurnRight);
                break;
            case RouteMovementStage.TurnRight:
                StartMovement(
                    view,
                    commands,
                    RouteMovementStage.ForwardTwo);
                break;
            case RouteMovementStage.ForwardTwo:
                Require(
                    commands.Movement.ClearIntent(view.Generation),
                    Stops[_nextStop].Name);
                _movementStage = RouteMovementStage.None;
                _nextStop++;
                break;
        }
    }

    private void StartMovement(
        IGameRuntimeView view,
        IGameRuntimeCommands commands,
        RouteMovementStage stage)
    {
        MovementInput input;
        double durationSeconds;
        switch (stage)
        {
            case RouteMovementStage.ForwardOne:
            case RouteMovementStage.ForwardTwo:
                input = new MovementInput(Forward: true, Run: true);
                durationSeconds = 4.0;
                break;
            case RouteMovementStage.TurnRight:
                input = new MovementInput(TurnRight: true);
                durationSeconds = 2.0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }

        Require(
            commands.Movement.SetIntent(view.Generation, input),
            Stops[_nextStop].Name);
        _movementStage = stage;
        _movementDeadline =
            view.Clock.SimulationTimeSeconds + durationSeconds;
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }

    private static bool HasLocalPlayer(IGameRuntimeView view) =>
        view.Lifecycle.State is RuntimeLifecycleState.InWorld
        && view.Lifecycle.PlayerGuid != 0u
        && view.Entities.TryGet(
            view.Lifecycle.PlayerGuid,
            out _);

    private static void Require(
        in RuntimeCommandResult result,
        string stop)
    {
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"Portal route '{stop}' command was rejected with "
                + $"{result.Status}.");
        }
    }

    private readonly record struct RouteStop(
        string Name,
        string? ServerCommand,
        uint? ExpectedCell);

    private enum RouteMovementStage
    {
        None,
        ForwardOne,
        TurnRight,
        ForwardTwo,
    }
}

internal sealed class JumpProbeHeadlessBotPolicy : IHeadlessBotPolicy
{
    private enum Stage
    {
        WaitForPlayer,
        Charging,
        WaitReleaseAirborne,
        WaitMidAirPress,
        Done,
    }

    private Stage _stage = Stage.WaitForPlayer;
    private double _stageDeadline;
    private bool _wasAirborne;

    public bool IsComplete => _stage == Stage.Done;

    public void Tick(IGameRuntimeView view, IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);

        RuntimeMovementSnapshot movement = view.Movement.Snapshot;
        if (movement.IsAirborne != _wasAirborne)
        {
            Console.WriteLine(
                $"[jump-probe] airborne-transition {_wasAirborne} -> "
                + $"{movement.IsAirborne} stage={_stage} "
                + $"simTime={view.Clock.SimulationTimeSeconds:F3} "
                + $"hasCommandInput={movement.HasCommandInput} "
                + $"commandInput.Jump={movement.CommandInput.Jump}");
            _wasAirborne = movement.IsAirborne;
        }

        switch (_stage)
        {
            case Stage.WaitForPlayer:
                if (!HasLocalPlayer(view))
                    return;
                Console.WriteLine("[jump-probe] local player present; charging jump");
                Require(commands.Movement.SetIntent(
                    view.Generation,
                    new MovementInput(Jump: true)));
                _stageDeadline = view.Clock.SimulationTimeSeconds + 0.6;
                _stage = Stage.Charging;
                break;

            case Stage.Charging:
                if (view.Clock.SimulationTimeSeconds < _stageDeadline)
                    return;
                Console.WriteLine("[jump-probe] releasing jump (fire)");
                Require(commands.Movement.SetIntent(
                    view.Generation,
                    new MovementInput(Jump: false)));
                _stageDeadline = view.Clock.SimulationTimeSeconds + 3.0;
                _stage = Stage.WaitReleaseAirborne;
                break;

            case Stage.WaitReleaseAirborne:
                if (movement.IsAirborne)
                {
                    Console.WriteLine(
                        "[jump-probe] airborne confirmed; pressing jump mid-air "
                        + $"simTime={view.Clock.SimulationTimeSeconds:F3}");
                    Require(commands.Movement.SetIntent(
                        view.Generation,
                        new MovementInput(Jump: true)));
                    _stageDeadline = view.Clock.SimulationTimeSeconds + 1.0;
                    _stage = Stage.WaitMidAirPress;
                    return;
                }
                if (view.Clock.SimulationTimeSeconds >= _stageDeadline)
                {
                    Console.WriteLine(
                        "[jump-probe] TIMEOUT waiting for airborne after jump "
                        + "fire -- the released jump never registered as "
                        + "airborne.");
                    Require(commands.Movement.ClearIntent(view.Generation));
                    _stage = Stage.Done;
                }
                return;

            case Stage.WaitMidAirPress:
                if (view.Clock.SimulationTimeSeconds < _stageDeadline)
                    return;
                Console.WriteLine(
                    "[jump-probe] probe complete; final airborne="
                    + $"{movement.IsAirborne}");
                Require(commands.Movement.ClearIntent(view.Generation));
                _stage = Stage.Done;
                return;
        }
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }

    private static bool HasLocalPlayer(IGameRuntimeView view) =>
        view.Lifecycle.State is RuntimeLifecycleState.InWorld
        && view.Lifecycle.PlayerGuid != 0u
        && view.Entities.TryGet(
            view.Lifecycle.PlayerGuid,
            out _);

    private static void Require(in RuntimeCommandResult result)
    {
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"Jump probe command was rejected with {result.Status}.");
        }
    }
}

internal sealed class FellowshipAllegianceGateCoordinator
{
    public string? RecruitCharacterName { get; set; }
}

internal sealed class FellowshipAllegianceLeaderBotPolicy : IHeadlessBotPolicy
{
    private static readonly bool AllegianceGateEnabled = false;

    private const string FellowshipName = "AcdreamFA6Gate";
    private const double ProximityRetryPeriodSeconds = 5d;
    private const double ProximityTimeoutSeconds = 90d;
    private const double StageTimeoutSeconds = 60d;

    private enum Stage
    {
        WaitForPlayer,
        ArmAllegianceSubscription,
        EstablishProximity,
        CreateFellowship,
        WaitInFellowship,
        Recruit,
        WaitRecruited,
        DeclarePanelOpen,
        WaitForVassal,
        MidFlowReconnect,
        WaitReconnectPlayer,
        RearmAllegianceSubscription,
        WaitReconnectReseed,
        Teardown,
        WaitTeardown,
        Done,
    }

    private readonly GameRuntime _runtime;
    private readonly Func<GameEvents.CharacterConfirmationRequest?>
        _getPendingConfirmation;
    private readonly Action<bool> _respondToConfirmation;
    private readonly FellowshipAllegianceGateCoordinator _coordinator;
    private Stage _stage = Stage.WaitForPlayer;
    private double _stageDeadline;
    private double _nextProximityAttempt;
    private double _proximityDeadline;
    private uint _targetGuid;
    private ulong _reconnectFromGeneration;

    internal FellowshipAllegianceLeaderBotPolicy(
        GameRuntime runtime,
        Func<GameEvents.CharacterConfirmationRequest?> getPendingConfirmation,
        Action<bool> respondToConfirmation,
        FellowshipAllegianceGateCoordinator coordinator)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _getPendingConfirmation = getPendingConfirmation
            ?? throw new ArgumentNullException(nameof(getPendingConfirmation));
        _respondToConfirmation = respondToConfirmation
            ?? throw new ArgumentNullException(nameof(respondToConfirmation));
        _coordinator = coordinator
            ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public bool IsComplete => _stage == Stage.Done;

    public void Tick(IGameRuntimeView view, IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);

        if (_getPendingConfirmation() is { } pending)
        {
            Console.WriteLine(
                $"[fa6-leader] answering pending confirmation type="
                + $"{pending.Type} context={pending.ContextId} "
                + $"text='{pending.Message}' -> accepted=true");
            _respondToConfirmation(true);
        }

        switch (_stage)
        {
            case Stage.WaitForPlayer:
                if (!HasLocalPlayer(view))
                    return;
                Console.WriteLine("[fa6-leader] local player present");
                Advance(view, Stage.ArmAllegianceSubscription);
                break;

            case Stage.ArmAllegianceSubscription:
                Require(
                    commands.Allegiance.SetUpdateSubscription(
                        view.Generation,
                        true),
                    "arm allegiance subscription (0x001F on)");
                Console.WriteLine("[fa6-leader] allegiance subscription armed");
                _nextProximityAttempt = view.Clock.SimulationTimeSeconds;
                _proximityDeadline =
                    view.Clock.SimulationTimeSeconds + ProximityTimeoutSeconds;
                Advance(view, Stage.EstablishProximity);
                break;

            case Stage.EstablishProximity:
                TickEstablishProximity(view, commands);
                break;

            case Stage.CreateFellowship:
                if (view.Fellowship.Snapshot.IsInFellowship)
                {
                    Console.WriteLine(
                        "[fa6-leader] already in a fellowship — skipping create");
                    Advance(view, Stage.Recruit);
                    return;
                }
                Require(
                    commands.Fellowship.Create(
                        view.Generation,
                        FellowshipName,
                        shareXp: true),
                    "create fellowship");
                Console.WriteLine(
                    $"[fa6-leader] sent FellowshipCreate '{FellowshipName}'");
                Advance(view, Stage.WaitInFellowship);
                break;

            case Stage.WaitInFellowship:
                WaitUntil(
                    view,
                    view.Fellowship.Snapshot.IsInFellowship,
                    Stage.Recruit,
                    "own fellowship snapshot IsInFellowship");
                break;

            case Stage.Recruit:
                if (view.Fellowship.Snapshot.MemberCount >= 2)
                {
                    Console.WriteLine(
                        "[fa6-leader] fellowship already has 2 members — skipping recruit");
                    Advance(view, Stage.DeclarePanelOpen);
                    return;
                }
                Require(
                    commands.Fellowship.Recruit(view.Generation, _targetGuid),
                    "recruit");
                Console.WriteLine(
                    $"[fa6-leader] sent FellowshipRecruit target=0x{_targetGuid:X8}");
                Advance(view, Stage.WaitRecruited);
                break;

            case Stage.WaitRecruited:
                WaitUntil(
                    view,
                    view.Fellowship.Snapshot.MemberCount >= 2,
                    Stage.DeclarePanelOpen,
                    "own fellowship MemberCount reaching 2 (proves the "
                        + "recruit round-trip completed)");
                break;

            case Stage.DeclarePanelOpen:
                Require(
                    commands.Fellowship.SetPanelOpen(view.Generation, true),
                    "declare fellowship panel open (0x00A6)");
                if (view.Fellowship.TryGetMember(
                        _targetGuid,
                        out RuntimeFellowMemberSnapshot member))
                {
                    Console.WriteLine(
                        $"[fa6-leader] panel-open declared; recruit vitals "
                        + $"name='{member.Name}' maxHealth={member.MaxHealth}");
                    if (member.MaxHealth == 0u)
                    {
                        throw new InvalidOperationException(
                            "[fa6-leader] recruit's own fellowship member "
                            + "row carries no vitals (MaxHealth == 0) after "
                            + "the panel-open declaration.");
                    }
                }
                else
                {
                    Console.WriteLine(
                        "[fa6-leader] WARNING panel-open declared but "
                        + "recruit's member row is not yet resolvable");
                }
                Advance(
                    view,
                    AllegianceGateEnabled
                        ? Stage.WaitForVassal
                        : Stage.MidFlowReconnect);
                break;

            case Stage.WaitForVassal:
                WaitUntil(
                    view,
                    view.Allegiance.Snapshot.TotalVassals >= 1,
                    Stage.MidFlowReconnect,
                    "own allegiance TotalVassals reaching 1 (proves the "
                        + "recruit's swear reached this bot's own tree)");
                if (_stage == Stage.MidFlowReconnect)
                {
                    bool hasTargetAsVassal = false;
                    foreach (RuntimeAllegianceMemberSnapshot vassal
                        in view.Allegiance.GetVassals(view.Lifecycle.PlayerGuid))
                    {
                        if (vassal.CharacterId == _targetGuid)
                        {
                            hasTargetAsVassal = true;
                            break;
                        }
                    }
                    if (!hasTargetAsVassal)
                    {
                        throw new InvalidOperationException(
                            $"[fa6-leader] TotalVassals reached 1 but "
                            + $"GetVassals does not contain target "
                            + $"0x{_targetGuid:X8}.");
                    }
                    Console.WriteLine(
                        $"[fa6-leader] vassal list contains recruit "
                        + $"0x{_targetGuid:X8} — decisive allegiance "
                        + "establish confirmed on the LEADER side");
                }
                break;

            case Stage.MidFlowReconnect:
                _reconnectFromGeneration = view.Generation.Value;
                RuntimeSessionStartResult reconnect =
                    commands.Session.Reconnect(view.Generation);
                if (reconnect.Status
                    is not (RuntimeSessionStartStatus.Connected
                        or RuntimeSessionStartStatus.Deferred))
                {
                    throw new InvalidOperationException(
                        $"[fa6-leader] mid-flow reconnect rejected with "
                        + $"{reconnect.Status}.");
                }
                Console.WriteLine("[fa6-leader] mid-flow reconnect issued");
                Advance(view, Stage.WaitReconnectPlayer);
                break;

            case Stage.WaitReconnectPlayer:
                if (view.Generation.Value <= _reconnectFromGeneration
                    || !HasLocalPlayer(view))
                {
                    CheckStageTimeout(view, "reconnect to materialize a new generation with a local player");
                    return;
                }
                Console.WriteLine(
                    $"[fa6-leader] reconnected — generation "
                    + $"{_reconnectFromGeneration} -> {view.Generation.Value}");
                Advance(view, Stage.RearmAllegianceSubscription);
                break;

            case Stage.RearmAllegianceSubscription:
                Require(
                    commands.Allegiance.SetUpdateSubscription(
                        view.Generation,
                        true),
                    "re-arm allegiance subscription after reconnect");
                Console.WriteLine(
                    "[fa6-leader] allegiance subscription re-armed post-reconnect");
                Advance(view, Stage.WaitReconnectReseed);
                break;

            case Stage.WaitReconnectReseed:
                if (view.Fellowship.Snapshot.IsInFellowship
                    && view.Fellowship.Snapshot.MemberCount >= 2
                    && (!AllegianceGateEnabled
                        || view.Allegiance.Snapshot.TotalVassals >= 1))
                {
                    Console.WriteLine(
                        "[fa6-leader] RECONNECT-IDEMPOTENCE CONFIRMED: "
                        + "fellowship"
                        + (AllegianceGateEnabled ? " and allegiance both" : "")
                        + " re-seeded "
                        + $"(members={view.Fellowship.Snapshot.MemberCount}, "
                        + $"vassals={view.Allegiance.Snapshot.TotalVassals})");
                    Advance(view, Stage.Teardown);
                    return;
                }
                CheckStageTimeout(
                    view,
                    "fellowship"
                        + (AllegianceGateEnabled ? " and allegiance" : "")
                        + " to re-seed after reconnect "
                        + $"(IsInFellowship={view.Fellowship.Snapshot.IsInFellowship}, "
                        + $"MemberCount={view.Fellowship.Snapshot.MemberCount}, "
                        + $"TotalVassals={view.Allegiance.Snapshot.TotalVassals})");
                break;

            case Stage.Teardown:
                Require(
                    commands.Fellowship.Quit(view.Generation, disband: true),
                    "disband fellowship");
                Console.WriteLine("[fa6-leader] sent FellowshipQuit disband=true");
                Advance(view, Stage.WaitTeardown);
                break;

            case Stage.WaitTeardown:
                WaitUntil(
                    view,
                    !view.Fellowship.Snapshot.IsInFellowship,
                    Stage.Done,
                    "own fellowship snapshot clearing after disband");
                if (_stage == Stage.Done)
                {
                    Console.WriteLine(
                        "[fa6-leader] TEARDOWN CONFIRMED: fellowship "
                        + "disbanded; gate complete");
                }
                break;

            case Stage.Done:
                break;
        }
    }

    private void TickEstablishProximity(
        IGameRuntimeView view,
        IGameRuntimeCommands commands)
    {
        string? recruitName = _coordinator.RecruitCharacterName;
        if (recruitName is not null)
        {
            uint? found = RuntimeFriendlyTargetQuery.FindPlayerByName(
                _runtime,
                recruitName);
            if (found is { } guid)
            {
                _targetGuid = guid;
                Console.WriteLine(
                    $"[fa6-leader] proximity established: recruit guid="
                    + $"0x{guid:X8} name='{recruitName}' (name-matched)");
                Advance(view, Stage.CreateFellowship);
                return;
            }
        }

        if (view.Clock.SimulationTimeSeconds >= _nextProximityAttempt)
        {
            Require(
                commands.Chat.Execute(
                    view.Generation,
                    new RuntimeChatCommand(RuntimeChatChannel.Say, "@teleallto")),
                "send @teleallto");
            Console.WriteLine(
                "[fa6-leader] sent @teleallto (recruitName="
                + $"{(recruitName is null ? "<unknown>" : $"'{recruitName}'")}, "
                + "not yet resolved as a nearby player entity)");
            _nextProximityAttempt =
                view.Clock.SimulationTimeSeconds + ProximityRetryPeriodSeconds;
        }

        if (view.Clock.SimulationTimeSeconds >= _proximityDeadline)
        {
            throw new InvalidOperationException(
                "[fa6-leader] timed out establishing proximity to the "
                + "recruit bot — "
                + (recruitName is null
                    ? "its character name was never discovered (the second "
                        + "account never reached CharacterList selection)."
                    : $"'{recruitName}' never streamed in as a nearby "
                        + "player entity. Either the second account never "
                        + "connected, or @teleallto was refused."));
        }
    }

    private void Advance(IGameRuntimeView view, Stage next)
    {
        Console.WriteLine($"[fa6-leader] stage {_stage} -> {next}");
        _stage = next;
        _stageDeadline = view.Clock.SimulationTimeSeconds + StageTimeoutSeconds;
    }

    private void WaitUntil(
        IGameRuntimeView view,
        bool condition,
        Stage next,
        string description)
    {
        if (condition)
        {
            Advance(view, next);
            return;
        }
        CheckStageTimeout(view, description);
    }

    private void CheckStageTimeout(IGameRuntimeView view, string description)
    {
        if (view.Clock.SimulationTimeSeconds >= _stageDeadline)
        {
            throw new InvalidOperationException(
                $"[fa6-leader] timed out in stage {_stage} waiting for: "
                + description);
        }
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }

    private static bool HasLocalPlayer(IGameRuntimeView view) =>
        view.Lifecycle.State is RuntimeLifecycleState.InWorld
        && view.Lifecycle.PlayerGuid != 0u
        && view.Entities.TryGet(
            view.Lifecycle.PlayerGuid,
            out _);

    private static void Require(in RuntimeCommandResult result, string what)
    {
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"[fa6-leader] command rejected ({what}): {result.Status}");
        }
    }
}

internal sealed class FellowshipAllegianceRecruitBotPolicy : IHeadlessBotPolicy
{
    private static readonly bool AllegianceGateEnabled = false;

    private const double StageTimeoutSeconds = 150d;

    private enum Stage
    {
        WaitForPlayer,
        ArmAllegianceSubscription,
        WaitForRecruit,
        Swear,
        WaitSwornSeed,
        MidFlowReconnect,
        WaitReconnectPlayer,
        RearmAllegianceSubscription,
        WaitReconnectReseed,
        Break,
        WaitBrokenSeed,
        WaitFellowshipDisbandCleared,
        Done,
    }

    private readonly GameRuntime _runtime;
    private Stage _stage = Stage.WaitForPlayer;
    private double _stageDeadline;
    private uint _patronGuid;
    private ulong _reconnectFromGeneration;

    internal FellowshipAllegianceRecruitBotPolicy(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public bool IsComplete => _stage == Stage.Done;

    public void Tick(IGameRuntimeView view, IGameRuntimeCommands commands)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);

        switch (_stage)
        {
            case Stage.WaitForPlayer:
                if (!HasLocalPlayer(view))
                    return;
                Console.WriteLine("[fa6-recruit] local player present");
                Advance(view, Stage.ArmAllegianceSubscription);
                break;

            case Stage.ArmAllegianceSubscription:
                Require(
                    commands.Allegiance.SetUpdateSubscription(
                        view.Generation,
                        true),
                    "arm allegiance subscription (0x001F on)");
                Console.WriteLine("[fa6-recruit] allegiance subscription armed");
                Advance(view, Stage.WaitForRecruit);
                break;

            case Stage.WaitForRecruit:
                if (view.Fellowship.Snapshot.IsInFellowship
                    && view.Fellowship.Snapshot.MemberCount >= 2)
                {
                    uint leaderGuid = view.Fellowship.Snapshot.LeaderGuid;
                    if (leaderGuid == 0u || leaderGuid == view.Lifecycle.PlayerGuid)
                    {
                        throw new InvalidOperationException(
                            $"[fa6-recruit] DECISIVE ASSERTION FAILED: own "
                            + $"fellowship snapshot flipped but LeaderGuid="
                            + $"0x{leaderGuid:X8} is not a distinct other "
                            + "player.");
                    }
                    uint? nearbyOther =
                        RuntimeFriendlyTargetQuery.FindClosestOtherPlayer(_runtime);
                    if (nearbyOther is { } otherGuid && otherGuid != leaderGuid)
                    {
                        throw new InvalidOperationException(
                            $"[fa6-recruit] DECISIVE ASSERTION FAILED: "
                            + $"fellowship LeaderGuid=0x{leaderGuid:X8} does "
                            + $"not match the nearest other player entity "
                            + $"0x{otherGuid:X8}.");
                    }
                    _patronGuid = leaderGuid;
                    Console.WriteLine(
                        "[fa6-recruit] DECISIVE ASSERTION PASSED: own "
                        + "RuntimeFellowshipState flipped IsInFellowship=true, "
                        + $"MemberCount={view.Fellowship.Snapshot.MemberCount}, "
                        + $"LeaderGuid=0x{leaderGuid:X8} — recruit inbound "
                        + "path reached THIS process's own Runtime owner");
                    Advance(
                        view,
                        AllegianceGateEnabled
                            ? Stage.Swear
                            : Stage.MidFlowReconnect);
                    return;
                }
                CheckStageTimeout(
                    view,
                    "own fellowship snapshot to flip IsInFellowship=true with "
                        + "MemberCount>=2 (the Leader's recruit never landed "
                        + "on this bot's own Runtime owner)");
                break;

            case Stage.Swear:
                LogDistanceToPatron(view);
                Require(
                    commands.Allegiance.Swear(view.Generation, _patronGuid),
                    "swear allegiance");
                Console.WriteLine(
                    $"[fa6-recruit] sent AllegianceSwear patron=0x{_patronGuid:X8}");
                Advance(view, Stage.WaitSwornSeed);
                break;

            case Stage.WaitSwornSeed:
                if (view.Allegiance.TryGetPatron(
                        view.Lifecycle.PlayerGuid,
                        out RuntimeAllegianceMemberSnapshot patron)
                    && patron.CharacterId == _patronGuid)
                {
                    Console.WriteLine(
                        "[fa6-recruit] DECISIVE ASSERTION PASSED: own "
                        + $"RuntimeAllegianceState TryGetPatron == 0x"
                        + $"{_patronGuid:X8} — swear inbound path reached "
                        + "THIS process's own Runtime owner");
                    Advance(view, Stage.MidFlowReconnect);
                    return;
                }
                CheckStageTimeout(
                    view,
                    "own allegiance snapshot's patron to become the Leader "
                        + "bot after swearing");
                break;

            case Stage.MidFlowReconnect:
                _reconnectFromGeneration = view.Generation.Value;
                RuntimeSessionStartResult reconnect =
                    commands.Session.Reconnect(view.Generation);
                if (reconnect.Status
                    is not (RuntimeSessionStartStatus.Connected
                        or RuntimeSessionStartStatus.Deferred))
                {
                    throw new InvalidOperationException(
                        $"[fa6-recruit] mid-flow reconnect rejected with "
                        + $"{reconnect.Status}.");
                }
                Console.WriteLine("[fa6-recruit] mid-flow reconnect issued");
                Advance(view, Stage.WaitReconnectPlayer);
                break;

            case Stage.WaitReconnectPlayer:
                if (view.Generation.Value <= _reconnectFromGeneration
                    || !HasLocalPlayer(view))
                {
                    CheckStageTimeout(
                        view,
                        "reconnect to materialize a new generation with a "
                            + "local player");
                    return;
                }
                Console.WriteLine(
                    $"[fa6-recruit] reconnected — generation "
                    + $"{_reconnectFromGeneration} -> {view.Generation.Value}");
                Advance(view, Stage.RearmAllegianceSubscription);
                break;

            case Stage.RearmAllegianceSubscription:
                Require(
                    commands.Allegiance.SetUpdateSubscription(
                        view.Generation,
                        true),
                    "re-arm allegiance subscription after reconnect");
                Console.WriteLine(
                    "[fa6-recruit] allegiance subscription re-armed post-reconnect");
                Advance(view, Stage.WaitReconnectReseed);
                break;

            case Stage.WaitReconnectReseed:
                if (AllegianceGateEnabled)
                {
                    if (view.Fellowship.Snapshot.IsInFellowship
                        && view.Fellowship.Snapshot.MemberCount >= 2
                        && view.Allegiance.TryGetPatron(
                            view.Lifecycle.PlayerGuid,
                            out RuntimeAllegianceMemberSnapshot reseededPatron)
                        && reseededPatron.CharacterId == _patronGuid)
                    {
                        Console.WriteLine(
                            "[fa6-recruit] RECONNECT-IDEMPOTENCE CONFIRMED: "
                            + "fellowship and allegiance both re-seeded on "
                            + "the RECRUIT side");
                        Advance(view, Stage.Break);
                        return;
                    }
                    CheckStageTimeout(
                        view,
                        "fellowship and allegiance to re-seed after reconnect");
                    break;
                }
                if (view.Fellowship.Snapshot.IsInFellowship
                    && view.Fellowship.Snapshot.MemberCount >= 2)
                {
                    Console.WriteLine(
                        "[fa6-recruit] RECONNECT-IDEMPOTENCE CONFIRMED: "
                        + "fellowship re-seeded on the RECRUIT side "
                        + "(allegiance verification disabled)");
                    Advance(view, Stage.WaitFellowshipDisbandCleared);
                    return;
                }
                CheckStageTimeout(
                    view,
                    "fellowship to re-seed after reconnect");
                break;

            case Stage.Break:
                Require(
                    commands.Allegiance.Break(view.Generation, _patronGuid),
                    "break allegiance");
                Console.WriteLine(
                    $"[fa6-recruit] sent AllegianceBreak target=0x{_patronGuid:X8}");
                Advance(view, Stage.WaitBrokenSeed);
                break;

            case Stage.WaitBrokenSeed:
                if (!view.Allegiance.TryGetPatron(
                        view.Lifecycle.PlayerGuid,
                        out RuntimeAllegianceMemberSnapshot stillPatron)
                    || stillPatron.CharacterId != _patronGuid)
                {
                    Console.WriteLine(
                        "[fa6-recruit] TEARDOWN CONFIRMED: own allegiance "
                        + "snapshot no longer shows the Leader as patron");
                    Advance(view, Stage.WaitFellowshipDisbandCleared);
                    return;
                }
                CheckStageTimeout(
                    view,
                    "own allegiance snapshot's patron to clear after break");
                break;

            case Stage.WaitFellowshipDisbandCleared:
                WaitUntil(
                    view,
                    !view.Fellowship.Snapshot.IsInFellowship,
                    Stage.Done,
                    "own fellowship snapshot clearing after the Leader's "
                        + "disband");
                if (_stage == Stage.Done)
                {
                    Console.WriteLine(
                        "[fa6-recruit] TEARDOWN CONFIRMED: own fellowship "
                        + "snapshot cleared after disband; gate complete");
                }
                break;

            case Stage.Done:
                break;
        }
    }

    private void LogDistanceToPatron(IGameRuntimeView view)
    {
        if (!_runtime.EntityObjects.Entities.TryGetActive(
                view.Lifecycle.PlayerGuid,
                out AcDream.Runtime.Entities.RuntimeEntityRecord self)
            || self.Snapshot.Position is not { } selfPosition
            || !_runtime.EntityObjects.Entities.TryGetActive(
                _patronGuid,
                out AcDream.Runtime.Entities.RuntimeEntityRecord patron)
            || patron.Snapshot.Position is not { } patronPosition)
        {
            Console.WriteLine(
                "[fa6-diag] LogDistanceToPatron: self or patron position "
                + "unresolvable");
            return;
        }

        static System.Numerics.Vector3 Absolute(
            AcDream.Core.Net.Messages.CreateObject.ServerPosition position)
        {
            int landblockX = (int)((position.LandblockId >> 24) & 0xFFu);
            int landblockY = (int)((position.LandblockId >> 16) & 0xFFu);
            return new System.Numerics.Vector3(
                position.PositionX + landblockX * 192f,
                position.PositionY + landblockY * 192f,
                position.PositionZ);
        }

        float distance = System.Numerics.Vector3.Distance(
            Absolute(selfPosition),
            Absolute(patronPosition));
        Console.WriteLine(
            $"[fa6-diag] distance self(0x{view.Lifecycle.PlayerGuid:X8})->"
            + $"patron(0x{_patronGuid:X8}) = {distance:F3} m "
            + $"selfCell=0x{selfPosition.LandblockId:X8} "
            + $"patronCell=0x{patronPosition.LandblockId:X8}");
    }

    private void Advance(IGameRuntimeView view, Stage next)
    {
        Console.WriteLine($"[fa6-recruit] stage {_stage} -> {next}");
        _stage = next;
        _stageDeadline = view.Clock.SimulationTimeSeconds + StageTimeoutSeconds;
    }

    private void WaitUntil(
        IGameRuntimeView view,
        bool condition,
        Stage next,
        string description)
    {
        if (condition)
        {
            Advance(view, next);
            return;
        }
        CheckStageTimeout(view, description);
    }

    private void CheckStageTimeout(IGameRuntimeView view, string description)
    {
        if (view.Clock.SimulationTimeSeconds >= _stageDeadline)
        {
            throw new InvalidOperationException(
                $"[fa6-recruit] timed out in stage {_stage} waiting for: "
                + description);
        }
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta)
    {
    }

    public void OnCommand(in RuntimeCommandDelta delta)
    {
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    public void OnChat(in RuntimeChatDelta delta)
    {
    }

    public void OnMovement(in RuntimeMovementDelta delta)
    {
    }

    public void OnPortal(in RuntimePortalDelta delta)
    {
    }

    public void OnCombat(in RuntimeCombatDelta delta)
    {
    }

    public void Dispose()
    {
    }

    private static bool HasLocalPlayer(IGameRuntimeView view) =>
        view.Lifecycle.State is RuntimeLifecycleState.InWorld
        && view.Lifecycle.PlayerGuid != 0u
        && view.Entities.TryGet(
            view.Lifecycle.PlayerGuid,
            out _);

    private static void Require(in RuntimeCommandResult result, string what)
    {
        if (!result.Accepted)
        {
            throw new InvalidOperationException(
                $"[fa6-recruit] command rejected ({what}): {result.Status}");
        }
    }
}
