using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Navigation;

/// <summary>
/// The plugin navigation API over one game runtime, handed out by every host: where the
/// character and the objects around it stand, the movement intent a plugin holds, moves
/// the client carries out, and walks it plans. A host binds the runtime, the movement
/// commands it validates against, and its walk controller as they come to exist; until
/// each is bound the members that need it answer unavailable.
/// </summary>
internal sealed partial class RuntimeNavigationAutomation : INavigationAutomation
{
    /// <summary>The farthest from its goal a walk may be asked to end.</summary>
    internal const float MaximumGoToArrivalMeters = 50f;

    private readonly object _gate = new();
    private readonly Func<bool> _isAvailable;
    private readonly List<GoToPause> _pauses = [];
    private GameRuntime? _runtime;
    private IRuntimeMovementCommands? _commands;
    private Func<RuntimeGenerationToken>? _generation;
    private NavigationWalkController? _walk;

    /// <param name="isAvailable">
    /// Whether the host can act for the character now; the runtime being in the world is
    /// checked as well.
    /// </param>
    public RuntimeNavigationAutomation(Func<bool>? isAvailable = null)
    {
        _isAvailable = isAvailable ?? (static () => true);
    }

    private bool _remoteBodiesUnsimulated;

    /// <summary>A host that never moves a remote entity's physics body reads remote positions from the latest snapshot instead.</summary>
    public void BindRemoteBodiesUnsimulated() => _remoteBodiesUnsimulated = true;

    private Position? EntityPosition(RuntimeEntityRecord record, uint playerId) =>
        _remoteBodiesUnsimulated && record.ServerGuid != playerId
            ? RuntimeNavigationProjection.FromServer(record.Snapshot.Position)
            : record.PhysicsBody?.CellPosition
                ?? RuntimeNavigationProjection.FromServer(record.Snapshot.Position);

    public void Bind(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
            _runtime = runtime;
    }

    /// <summary>
    /// The movement commands intents, turns, moves and jumps go through, and the
    /// generation they are validated against.
    /// </summary>
    public void BindCommands(IRuntimeMovementCommands commands, Func<RuntimeGenerationToken> generation)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
        {
            _commands = commands;
            _generation = generation;
        }
    }

    /// <summary>The walks <see cref="GoTo(uint, float)"/> asks for, which wait while a plugin or the character needs the body.</summary>
    public void BindWalk(NavigationWalkController walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        walk.PausedBy = PauseReason;
        lock (_gate)
            _walk = walk;
    }

    /// <summary>Forgets the runtime, so every member answers unavailable until one is bound again.</summary>
    public void UnbindRuntime()
    {
        lock (_gate)
            _runtime = null;
    }

    private bool IsAvailable(out GameRuntime runtime)
    {
        GameRuntime? bound;
        lock (_gate)
            bound = _runtime;
        runtime = bound!;
        return bound is not null
            && bound.Lifecycle.State == RuntimeLifecycleState.InWorld
            && _isAvailable();
    }

    private bool TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation)
    {
        IRuntimeMovementCommands? bound;
        Func<RuntimeGenerationToken>? current;
        lock (_gate)
        {
            bound = _commands;
            current = _generation;
        }
        commands = bound!;
        generation = default;
        if (bound is null || current is null || !IsAvailable(out _))
            return false;
        generation = current();
        return true;
    }

    private bool TryWalk(out NavigationWalkController walk)
    {
        NavigationWalkController? bound;
        lock (_gate)
            bound = _walk;
        walk = bound!;
        return bound is not null && IsAvailable(out _);
    }

    private static PluginNavigationCommandStatus StatusOf(in RuntimeCommandResult result) =>
        result.Status == RuntimeCommandStatus.Accepted
            ? PluginNavigationCommandStatus.Accepted
            : PluginNavigationCommandStatus.Rejected;

    // ── Where things stand ────────────────────────────────────────────────

    public PluginNavigationSnapshot Snapshot
    {
        get
        {
            if (!IsAvailable(out GameRuntime runtime))
                return default;

            RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
            if (!movement.HasController)
                return default;
            RuntimePortalSnapshot portal = runtime.Portal.Snapshot;
            PluginNavigationPosition livePosition = RuntimeNavigationProjection.Position(movement.Position);
            PluginNavigationPosition confirmedPosition = livePosition;
            ulong confirmedRevision = 0UL;
            if (runtime.EntityObjects.Entities.TryGetActive(
                    runtime.PlayerIdentity.ServerGuid,
                    out RuntimeEntityRecord localRecord)
                && RuntimeNavigationProjection.FromServer(localRecord.Snapshot.Position) is { } accepted)
            {
                confirmedPosition = RuntimeNavigationProjection.Position(accepted);
                confirmedRevision = localRecord.PositionAuthorityVersion;
            }
            return new PluginNavigationSnapshot(
                IsAvailable: true,
                IsPortalSpace: portal.Kind != RuntimePortalKind.None
                    && !portal.Completed
                    && !portal.Cancelled,
                LocalObjectId: runtime.PlayerIdentity.ServerGuid,
                Position: livePosition,
                IsMoving: movement.Velocity.LengthSquared() > 0.0001f
                    || movement.HasCommandInput,
                IsAirborne: movement.IsAirborne)
            {
                ConfirmedPosition = confirmedPosition,
                ConfirmedPositionRevision = confirmedRevision,
            };
        }
    }

    public bool TryGetObject(uint objectId, out PluginNavigationObject value)
    {
        if (!IsAvailable(out GameRuntime runtime) || objectId == 0u)
        {
            value = default;
            return false;
        }

        RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
        if (objectId == runtime.PlayerIdentity.ServerGuid)
        {
            value = new PluginNavigationObject(
                objectId,
                runtime.InventoryOwner.Objects.Get(objectId)?.Name
                    ?? string.Empty,
                RuntimeNavigationProjection.Position(movement.Position));
            return movement.HasController;
        }

        if (!runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record))
        {
            value = default;
            return false;
        }

        Position? position = EntityPosition(record, runtime.PlayerIdentity.ServerGuid);
        if (position is not { } current)
        {
            value = default;
            return false;
        }
        value = RuntimeNavigationProjection.Enrich(
            new PluginNavigationObject(
                objectId,
                runtime.InventoryOwner.Objects.Get(objectId)?.Name
                    ?? record.Snapshot.Name
                    ?? $"0x{objectId:X8}",
                RuntimeNavigationProjection.Position(current)),
            runtime.InventoryOwner.Objects.Get(objectId),
            record.FinalPhysicsState);
        return true;
    }

    public bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        if (!IsAvailable(out GameRuntime runtime)
            || string.IsNullOrWhiteSpace(name)
            || !double.IsFinite(maximumDistanceMeters)
            || maximumDistanceMeters < 0d)
        {
            value = default;
            return false;
        }

        double nearestDistance = maximumDistanceMeters;
        PluginNavigationObject nearest = default;
        bool found = false;
        foreach (RuntimeEntityRecord record in runtime.EntityObjects.Entities.ActiveRecords)
        {
            uint objectId = record.ServerGuid;
            string candidateName = runtime.InventoryOwner.Objects.Get(objectId)?.Name
                ?? record.Snapshot.Name
                ?? string.Empty;
            if (!candidateName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            Position? source = EntityPosition(record, runtime.PlayerIdentity.ServerGuid);
            if (source is not { } position)
                continue;
            PluginNavigationPosition candidate = RuntimeNavigationProjection.Position(position);
            double distance = near.HorizontalDistanceMeters(candidate);
            if (distance > nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = RuntimeNavigationProjection.Enrich(
                new PluginNavigationObject(objectId, candidateName, candidate),
                runtime.InventoryOwner.Objects.Get(objectId),
                record.FinalPhysicsState);
            found = true;
        }

        value = nearest;
        return found;
    }

    public IReadOnlyList<PluginNavigationObject> CaptureObjects()
    {
        if (!IsAvailable(out GameRuntime runtime))
            return Array.Empty<PluginNavigationObject>();

        var result = new List<PluginNavigationObject>();
        foreach (RuntimeEntityRecord record in runtime.EntityObjects.Entities.ActiveRecords)
        {
            Position? source = EntityPosition(record, runtime.PlayerIdentity.ServerGuid);
            if (source is not { } position)
                continue;
            ClientObject? item = runtime.InventoryOwner.Objects.Get(record.ServerGuid);
            string name = item?.Name
                ?? record.Snapshot.Name
                ?? $"0x{record.ServerGuid:X8}";
            result.Add(RuntimeNavigationProjection.Enrich(
                new PluginNavigationObject(
                    record.ServerGuid,
                    name,
                    RuntimeNavigationProjection.Position(position)),
                item,
                record.FinalPhysicsState));
        }
        result.Sort(static (left, right) => left.ObjectId.CompareTo(right.ObjectId));
        return result;
    }

    // ── Held movement ─────────────────────────────────────────────────────

    public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent)
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        return StatusOf(commands.SetIntent(
            generation,
            new MovementInput(
                intent.Forward,
                intent.Backward,
                intent.StrafeLeft,
                intent.StrafeRight,
                intent.TurnLeft,
                intent.TurnRight,
                intent.Run,
                MouseDeltaX: 0f,
                intent.Jump)));
    }

    public PluginNavigationCommandStatus ClearMovementIntent()
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        return StatusOf(commands.ClearIntent(generation));
    }

    public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        return StatusOf(commands.TurnToHeading(generation, headingDegrees));
    }

    // ── Moves the client carries out ──────────────────────────────────────

    public PluginNavigationCommandStatus Move(
        PluginMoveDirection direction,
        PluginMovePace pace,
        float amount,
        PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees)
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        if (RuntimeNavigationProjection.MoveRequest(direction, pace, amount, unit) is not { } request)
            return PluginNavigationCommandStatus.Rejected;
        return StatusOf(commands.BeginMove(generation, request));
    }

    public PluginNavigationCommandStatus StopMoving()
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        return StatusOf(commands.StopMove(generation));
    }

    public PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel)
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        if (RuntimeNavigationProjection.Channel(channel) is not { } runtimeChannel)
            return PluginNavigationCommandStatus.Rejected;
        return StatusOf(commands.StopMove(generation, runtimeChannel));
    }

    public PluginNavigationCommandStatus Jump(float power)
    {
        if (!TryCommands(out IRuntimeMovementCommands commands, out RuntimeGenerationToken generation))
            return PluginNavigationCommandStatus.Unavailable;
        return StatusOf(commands.Jump(generation, power));
    }

    public PluginMoveReport MoveReport =>
        IsAvailable(out GameRuntime runtime)
            ? RuntimeNavigationProjection.MoveReport(runtime.Movement.Snapshot.ScriptedMove)
            : default;

    // ── Walks the client plans ────────────────────────────────────────────

    public PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters) =>
        GoToFor(PlayerOwner, objectId, arrivalMeters);

    public PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters) =>
        GoToFor(PlayerOwner, position, arrivalMeters);

    public PluginNavigationCommandStatus StandOn(uint objectId, float arrivalMeters) =>
        StandOnFor(PlayerOwner, objectId, arrivalMeters);

    /// <summary>How far behind a player a follow keeps when no buffer is given.</summary>
    public const float DefaultFollowMeters = NavigationWalkController.DefaultFollowMeters;

    public PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters) =>
        FollowFor(PlayerOwner, playerId, bufferMeters);

    public PluginNavigationCommandStatus StopGoTo() => StopGoToFor(PlayerOwner);

    public PluginGoToReport GoToReport => GoToReportFor();

    public IDisposable PauseGoToWhile(Func<string?> need) => PauseGoToWhileFor(PlayerOwner, need);

    /// <summary>
    /// What needs the character now, so that a walk waits for it: a plugin that asked walks
    /// to wait while it does, an attack under way, or a movement intent a plugin holds. Null
    /// when nothing does. A plugin whose answer throws is taken to need nothing.
    /// </summary>
    internal string? PauseReason()
    {
        GoToPause[] pauses;
        lock (_gate)
            pauses = [.. _pauses];
        foreach (GoToPause pause in pauses)
        {
            string? need;
            try
            {
                need = pause.Need();
            }
            catch (Exception)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(need))
                return need.Trim();
        }
        if (!IsAvailable(out GameRuntime runtime))
            return null;
        RuntimeCombatAttackState attack = runtime.ActionOwner.CombatAttack;
        if (attack.AttackRequestInProgress || attack.AttackServerResponsePending || attack.RepeatAttackInProgress)
            return "the character is attacking";
        RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
        MovementInput held = movement.CommandInput;
        return movement.HasCommandInput
            && (held.Forward || held.Backward || held.StrafeLeft || held.StrafeRight || held.TurnLeft || held.TurnRight)
                ? "a plugin is steering the character"
                : null;
    }

    /// <summary>A plugin's request that walks wait while it needs the character, ended by disposing it.</summary>
    private sealed class GoToPause(RuntimeNavigationAutomation navigation, string owner, Func<string?> need) : IDisposable
    {
        private bool _disposed;

        /// <summary>The plugin id that asked, or the player.</summary>
        public string Owner { get; } = owner;

        /// <summary>What the owner says it needs the character for; null when nothing.</summary>
        public Func<string?> Need { get; } = need;

        public void Dispose()
        {
            lock (navigation._gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                navigation._pauses.Remove(this);
            }
        }
    }
}

/// <summary>How the runtime's positions, objects, moves and walks read to a plugin, and back.</summary>
internal static class RuntimeNavigationProjection
{
    /// <summary>A position as map coordinates: 240 m to a unit, from the middle of the map.</summary>
    public static PluginNavigationPosition Position(Position position)
    {
        uint cellId = position.ObjCellId;
        uint blockX = (cellId >> 24) & 0xFFu;
        uint blockY = (cellId >> 16) & 0xFFu;
        Vector3 local = position.Frame.Origin;
        return new PluginNavigationPosition(
            cellId,
            (((double)blockX - 127d) * 192d + local.X - 84d) / 240d,
            (((double)blockY - 127d) * 192d + local.Y - 84d) / 240d,
            local.Z / 240d,
            MoveToMath.GetHeading(position.Frame.Orientation),
            (cellId & 0xFFFFu) is >= 1u and <= 0x40u);
    }

    /// <summary>
    /// A plugin position's point in its landblock's own frame, the inverse of
    /// <see cref="Position(AcDream.Core.Physics.Position)"/>; for a position with no cell,
    /// the point measured from the corner of the first landblock.
    /// </summary>
    public static Vector3 LandblockLocal(in PluginNavigationPosition position)
    {
        uint blockX = (position.CellId >> 24) & 0xFFu;
        uint blockY = (position.CellId >> 16) & 0xFFu;
        return new Vector3(
            (float)((position.EastWest * 240d) + 84d - (((double)blockX - 127d) * 192d)),
            (float)((position.NorthSouth * 240d) + 84d - (((double)blockY - 127d) * 192d)),
            (float)(position.Elevation * 240d));
    }

    public static Position? FromServer(
        AcDream.Core.Net.Messages.CreateObject.ServerPosition? position) =>
        position is not { } value
            ? null
            : new Position(
                value.LandblockId,
                new Vector3(value.PositionX, value.PositionY, value.PositionZ),
                new Quaternion(value.RotationX, value.RotationY, value.RotationZ, value.RotationW));

    /// <summary>
    /// An object with what the client knows of a door it is: open, locked, and how hard its lock
    /// is. A door stands open when the world shows it passable, as <paramref name="physicsState"/>
    /// says, since its Open property comes with an appraisal and does not follow the door opening
    /// and closing afterwards. Its lock is known once it has been appraised.
    /// </summary>
    public static PluginNavigationObject Enrich(in PluginNavigationObject value, ClientObject? item, PhysicsStateFlags? physicsState)
    {
        if (item is null)
            return value;
        bool door = ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Door) != 0;
        bool hasOpen = item.Properties.Bools.TryGetValue((uint)PropertyBool.Open, out bool isOpen);
        bool hasLocked = item.Properties.Bools.TryGetValue((uint)PropertyBool.Locked, out bool isLocked);
        return value with
        {
            IsDoor = door,
            IsOpen = door && physicsState is { } state
                ? state.HasFlag(PhysicsStateFlags.Ethereal)
                : hasOpen && isOpen,
            IsLocked = hasLocked && isLocked,
            HasLockState = hasOpen || hasLocked || item.LastAppraisalTimeMs > 0,
            LockDifficulty = item.Properties.GetInt((uint)PropertyInt.ResistLockpick),
        };
    }

    public static RuntimeMoveRequest? MoveRequest(
        PluginMoveDirection direction,
        PluginMovePace pace,
        float amount,
        PluginMoveUnit unit)
    {
        RuntimeMoveDirection? runtimeDirection = direction switch
        {
            PluginMoveDirection.Forward => RuntimeMoveDirection.Forward,
            PluginMoveDirection.Backward => RuntimeMoveDirection.Backward,
            PluginMoveDirection.StrafeLeft => RuntimeMoveDirection.StrafeLeft,
            PluginMoveDirection.StrafeRight => RuntimeMoveDirection.StrafeRight,
            PluginMoveDirection.TurnLeft => RuntimeMoveDirection.TurnLeft,
            PluginMoveDirection.TurnRight => RuntimeMoveDirection.TurnRight,
            _ => null,
        };
        RuntimeMovePace? runtimePace = pace switch
        {
            PluginMovePace.Walk => RuntimeMovePace.Walk,
            PluginMovePace.Run => RuntimeMovePace.Run,
            _ => null,
        };
        RuntimeMoveUnit? runtimeUnit = unit switch
        {
            PluginMoveUnit.MetersOrDegrees => RuntimeMoveUnit.MetersOrDegrees,
            PluginMoveUnit.Seconds => RuntimeMoveUnit.Seconds,
            _ => null,
        };
        if (runtimeDirection is not { } d || runtimePace is not { } p || runtimeUnit is not { } u)
            return null;
        var request = new RuntimeMoveRequest(d, p, amount, u);
        return RuntimeScriptedMovement.IsValid(request) ? request : null;
    }

    public static RuntimeMoveChannel? Channel(PluginMoveChannel channel) => channel switch
    {
        PluginMoveChannel.Travel => RuntimeMoveChannel.Travel,
        PluginMoveChannel.Strafe => RuntimeMoveChannel.Strafe,
        PluginMoveChannel.Turn => RuntimeMoveChannel.Turn,
        _ => null,
    };

    public static PluginMoveReport MoveReport(in RuntimeScriptedMoveSnapshot snapshot) =>
        new(
            MoveProgress(snapshot.Travel),
            MoveProgress(snapshot.Strafe),
            MoveProgress(snapshot.Turn),
            snapshot.JumpSequence,
            snapshot.JumpCharging);

    private static PluginMoveProgress MoveProgress(in RuntimeMoveChannelSnapshot channel) =>
        new(
            channel.Sequence,
            channel.State switch
            {
                RuntimeScriptedMoveState.Moving => PluginMoveState.Moving,
                RuntimeScriptedMoveState.Completed => PluginMoveState.Completed,
                RuntimeScriptedMoveState.Stopped => PluginMoveState.Stopped,
                RuntimeScriptedMoveState.TimeLimit => PluginMoveState.TimeLimit,
                RuntimeScriptedMoveState.Blocked => PluginMoveState.Blocked,
                RuntimeScriptedMoveState.Interrupted => PluginMoveState.Interrupted,
                RuntimeScriptedMoveState.Lost => PluginMoveState.Lost,
                _ => PluginMoveState.None,
            },
            channel.Request.Direction switch
            {
                RuntimeMoveDirection.Backward => PluginMoveDirection.Backward,
                RuntimeMoveDirection.StrafeLeft => PluginMoveDirection.StrafeLeft,
                RuntimeMoveDirection.StrafeRight => PluginMoveDirection.StrafeRight,
                RuntimeMoveDirection.TurnLeft => PluginMoveDirection.TurnLeft,
                RuntimeMoveDirection.TurnRight => PluginMoveDirection.TurnRight,
                _ => PluginMoveDirection.Forward,
            },
            channel.Request.Pace == RuntimeMovePace.Walk ? PluginMovePace.Walk : PluginMovePace.Run,
            channel.Request.Amount,
            channel.Request.Unit == RuntimeMoveUnit.Seconds ? PluginMoveUnit.Seconds : PluginMoveUnit.MetersOrDegrees,
            channel.Covered,
            channel.ElapsedSeconds);

    public static PluginGoToReport GoToReport(in NavigationWalkReport report) =>
        new(
            report.Sequence,
            report.State switch
            {
                NavigationWalkState.Planning => PluginGoToState.Planning,
                NavigationWalkState.Walking => PluginGoToState.Walking,
                NavigationWalkState.Waiting => PluginGoToState.Waiting,
                NavigationWalkState.Arrived => PluginGoToState.Arrived,
                NavigationWalkState.ArrivedWithoutSight => PluginGoToState.ArrivedWithoutSight,
                NavigationWalkState.NoRoute => PluginGoToState.NoRoute,
                NavigationWalkState.Blocked => PluginGoToState.Blocked,
                NavigationWalkState.Stopped => PluginGoToState.Stopped,
                NavigationWalkState.Interrupted => PluginGoToState.Interrupted,
                NavigationWalkState.Lost => PluginGoToState.Lost,
                _ => PluginGoToState.None,
            },
            report.ObjectId,
            report.RemainingMeters,
            report.Replans,
            report.Reason)
        {
            BlockedByObjectId = report.BlockedByObjectId,
        };
}
