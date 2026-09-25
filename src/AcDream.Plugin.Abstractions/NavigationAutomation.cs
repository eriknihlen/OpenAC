namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Where something stands in the world: the cell it occupies plus its position
/// on the global map grid.
/// </summary>
/// <param name="CellId">
/// The packed identifier of the landblock cell the object is in. The high half
/// is the landblock and the low half the cell inside it.
/// </param>
/// <param name="EastWest">
/// East-west map coordinate, growing eastwards. One unit is 240 meters, the
/// same units the in-game location readout uses.
/// </param>
/// <param name="NorthSouth">
/// North-south map coordinate, growing northwards, in the same 240-meter units
/// as <paramref name="EastWest"/>.
/// </param>
/// <param name="Elevation">
/// Height above the cell floor, in the same 240-meter units as the horizontal
/// coordinates; multiply by 240 for meters.
/// </param>
/// <param name="HeadingDegrees">
/// The direction the object faces, in degrees clockwise from north (0 to 360).
/// </param>
/// <param name="IsOutdoor">
/// True when the cell is an outdoor landscape cell; false inside a dungeon or
/// building interior.
/// </param>
public readonly record struct PluginNavigationPosition(
    uint CellId,
    double EastWest,
    double NorthSouth,
    double Elevation,
    float HeadingDegrees,
    bool IsOutdoor)
{
    /// <summary>
    /// The flat ground distance in meters between this position and
    /// <paramref name="other"/>, ignoring any difference in elevation.
    /// </summary>
    /// <param name="other">The position to measure to.</param>
    /// <returns>The distance in meters.</returns>
    public double HorizontalDistanceMeters(in PluginNavigationPosition other)
    {
        double dx = EastWest - other.EastWest;
        double dy = NorthSouth - other.NorthSouth;
        return Math.Sqrt(dx * dx + dy * dy) * 240d;
    }
}

/// <summary>
/// One world object seen from the navigation surface: who it is, where it is,
/// and, for doors and other lockable things, whether it is open or locked.
/// </summary>
/// <param name="ObjectId">The server-assigned id of the object.</param>
/// <param name="Name">
/// The object's display name, or an empty string when the client has not
/// received one yet.
/// </param>
/// <param name="Position">Where the object currently stands.</param>
public readonly record struct PluginNavigationObject(
    uint ObjectId,
    string Name,
    PluginNavigationPosition Position)
{
    /// <summary>True when the object is a door.</summary>
    public bool IsDoor { get; init; }

    /// <summary>
    /// True when the object reports itself as open. False both for a closed
    /// object and for one that never reports an open state.
    /// </summary>
    public bool IsOpen { get; init; }

    /// <summary>
    /// True when the object reports itself as locked. False both for an
    /// unlocked object and for one that never reports a lock state.
    /// </summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// True when the client actually knows this object's open or locked state,
    /// so a plugin can tell "closed" apart from "never reported".
    /// </summary>
    public bool HasLockState { get; init; }

    /// <summary>
    /// How hard the lock is to pick, taken from the object's lockpick
    /// resistance; zero when the object carries no such value.
    /// </summary>
    public int LockDifficulty { get; init; }
}

/// <summary>The local movement state sampled atomically by a plugin tick.</summary>
/// <param name="IsAvailable">
/// True when a session is in the world and the remaining fields carry real
/// values. When it is false every other field is at its default.
/// </param>
/// <param name="IsPortalSpace">
/// True while the player is in transit through portal space, after leaving one
/// place and before arriving at the next.
/// </param>
/// <param name="LocalObjectId">The player character's own object id.</param>
/// <param name="Position">
/// The player's live locally predicted position, updated every frame.
/// </param>
/// <param name="IsMoving">
/// True when the player is actually moving or is holding a movement input.
/// </param>
/// <param name="IsAirborne">True when the player has no ground under foot.</param>
public readonly record struct PluginNavigationSnapshot(
    bool IsAvailable,
    bool IsPortalSpace,
    uint LocalObjectId,
    PluginNavigationPosition Position,
    bool IsMoving,
    bool IsAirborne)
{
    /// <summary>
    /// The last position the server confirmed, as opposed to the locally
    /// predicted <see cref="Position"/>. Equal to <see cref="Position"/> when
    /// no server position has been accepted yet.
    /// </summary>
    public PluginNavigationPosition ConfirmedPosition { get; init; }

    /// <summary>
    /// A counter that grows every time the server confirms a new position, so a
    /// plugin can tell a fresh confirmation from a repeat. Zero when nothing
    /// has been confirmed yet.
    /// </summary>
    public ulong ConfirmedPositionRevision { get; init; }

    /// <summary>
    /// A host-local monotonic revision for this complete navigation snapshot.
    /// It changes whenever any snapshot field changes and is useful for
    /// consumers that need to coalesce updates without comparing all fields.
    /// </summary>
    public ulong Revision { get; init; }
}

/// <summary>
/// The movement keys a plugin wants held down. The host applies this as if the
/// player were holding those keys, until it is replaced or cleared.
/// </summary>
/// <param name="Forward">Hold the forward key.</param>
/// <param name="Backward">Hold the backward key.</param>
/// <param name="StrafeLeft">Sidestep to the left without turning.</param>
/// <param name="StrafeRight">Sidestep to the right without turning.</param>
/// <param name="TurnLeft">Turn on the spot to the left.</param>
/// <param name="TurnRight">Turn on the spot to the right.</param>
/// <param name="Run">Run rather than walk. Defaults to true.</param>
/// <param name="Jump">Jump.</param>
public readonly record struct PluginMovementIntent(
    bool Forward = false,
    bool Backward = false,
    bool StrafeLeft = false,
    bool StrafeRight = false,
    bool TurnLeft = false,
    bool TurnRight = false,
    bool Run = true,
    bool Jump = false);

/// <summary>Which way a client-driven move goes.</summary>
public enum PluginMoveDirection
{
    /// <summary>Travel forward.</summary>
    Forward,
    /// <summary>Travel backward.</summary>
    Backward,
    /// <summary>Sidestep to the left without turning.</summary>
    StrafeLeft,
    /// <summary>Sidestep to the right without turning.</summary>
    StrafeRight,
    /// <summary>Turn in place to the left.</summary>
    TurnLeft,
    /// <summary>Turn in place to the right.</summary>
    TurnRight,
}

/// <summary>How fast a client-driven move travels.</summary>
public enum PluginMovePace
{
    /// <summary>Walking speed.</summary>
    Walk,
    /// <summary>Running speed.</summary>
    Run,
}

/// <summary>What a client-driven move's amount counts.</summary>
public enum PluginMoveUnit
{
    /// <summary>Meters going forward, backward or sideways, and degrees for a turn.</summary>
    MetersOrDegrees,

    /// <summary>Seconds the move keeps going.</summary>
    Seconds,
}

/// <summary>
/// The parts of movement that combine the way held movement keys do: going
/// forward or backward, strafing, and turning. Each carries one client-driven
/// move at a time.
/// </summary>
public enum PluginMoveChannel
{
    /// <summary>Forward and backward travel.</summary>
    Travel,
    /// <summary>Sidestepping.</summary>
    Strafe,
    /// <summary>Turning in place.</summary>
    Turn,
}

/// <summary>Where a channel's most recent client-driven move stands.</summary>
public enum PluginMoveState
{
    /// <summary>No move has been asked for on this channel.</summary>
    None = 0,

    /// <summary>The move is under way.</summary>
    Moving,

    /// <summary>It covered its distance, angle or time.</summary>
    Completed,

    /// <summary>A stop ended it.</summary>
    Stopped,

    /// <summary>Time ran out: the limit for a move without an amount, or a move too slow to cover its distance or angle.</summary>
    TimeLimit,

    /// <summary>It stopped making progress.</summary>
    Blocked,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,
}

/// <summary>
/// One channel's most recent client-driven move. <paramref name="Sequence"/> grows
/// by one for every move begun on any channel, so a plugin can tell its own move
/// from a later one. <paramref name="Covered"/> is meters, or degrees for a turn,
/// whatever the move's unit.
/// </summary>
public readonly record struct PluginMoveProgress(
    long Sequence,
    PluginMoveState State,
    PluginMoveDirection Direction,
    PluginMovePace Pace,
    float Amount,
    PluginMoveUnit Unit,
    float Covered,
    float ElapsedSeconds);

/// <summary>
/// The most recent client-driven move on each channel, and the most recent jump.
/// <paramref name="JumpSequence"/> grows by one for every jump begun.
/// </summary>
public readonly record struct PluginMoveReport(
    PluginMoveProgress Travel,
    PluginMoveProgress Strafe,
    PluginMoveProgress Turn,
    long JumpSequence,
    bool JumpCharging)
{
    /// <summary>The most recent move on one channel.</summary>
    public PluginMoveProgress this[PluginMoveChannel channel] => channel switch
    {
        PluginMoveChannel.Travel => Travel,
        PluginMoveChannel.Strafe => Strafe,
        _ => Turn,
    };

    /// <summary>The channel a move in <paramref name="direction"/> occupies.</summary>
    public static PluginMoveChannel ChannelOf(PluginMoveDirection direction) => direction switch
    {
        PluginMoveDirection.Forward or PluginMoveDirection.Backward => PluginMoveChannel.Travel,
        PluginMoveDirection.StrafeLeft or PluginMoveDirection.StrafeRight => PluginMoveChannel.Strafe,
        _ => PluginMoveChannel.Turn,
    };
}

/// <summary>Where a walk the client plans to an object stands.</summary>
public enum PluginGoToState
{
    /// <summary>No walk has been asked for.</summary>
    None = 0,

    /// <summary>The client is building its navigation grid or searching it for a route.</summary>
    Planning,

    /// <summary>The character is walking the planned route.</summary>
    Walking,

    /// <summary>The character reached the goal within the requested distance.</summary>
    Arrived,

    /// <summary>No route joins the character to the object.</summary>
    NoRoute,

    /// <summary>The character stopped making progress, even after the client planned again.</summary>
    Blocked,

    /// <summary>A stop, or a later walk, ended it.</summary>
    Stopped,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,

    /// <summary>
    /// The walk stopped where the character stands while something else needs the
    /// character, such as a plugin fighting a monster, and plans on from there once
    /// nothing has needed it for a moment.
    /// </summary>
    Waiting,

    /// <summary>
    /// The walk ended at the nearest spot the character could reach, but no spot it could
    /// reach can see the goal, as when the goal is shut behind a wall. Not an arrival: a
    /// plugin that needs a line of sight should not act as though it had one.
    /// </summary>
    ArrivedWithoutSight,
}

/// <summary>
/// The most recent walk the client planned to an object. <paramref name="Sequence"/>
/// grows by one for every walk asked for, so a plugin can tell its own from a later
/// one. <paramref name="RemainingMeters"/> is the length of route left while the walk
/// goes on; once it has ended, the straight-line distance from the character to the
/// object, or NaN when the client cannot tell. <paramref name="Reason"/> says what
/// the walk is doing, or why it ended.
/// </summary>
public readonly record struct PluginGoToReport(
    long Sequence,
    PluginGoToState State,
    uint ObjectId,
    float RemainingMeters,
    int Replans,
    string? Reason)
{
    /// <summary>
    /// On a walk that ended blocked, the server object, such as a door that would
    /// not open, beside the spot where it last stopped making progress; otherwise zero.
    /// </summary>
    public uint BlockedByObjectId { get; init; }

    /// <summary>
    /// A monotonically increasing revision for this report stream. It changes
    /// whenever the reported walk sequence or state changes, allowing an event
    /// consumer to reject duplicate or stale reports.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>
    /// The current navigation state, repeated here as a stable event discriminator.
    /// This is equal to <see cref="State"/> and exists so consumers can inspect
    /// a report without relying on positional record members.
    /// </summary>
    public PluginGoToState CurrentState => State;

    /// <summary>
    /// Who asked for the walk under way: a plugin's id, or "player" for a chat command;
    /// null once no walk is under way.
    /// </summary>
    public string? Owner { get; init; }
}

/// <summary>What <see cref="INavigationAutomation.CheckRoomAhead"/> found.</summary>
public enum PluginRoomAheadStatus
{
    /// <summary>
    /// The client could not look: no session is in the world, the character has
    /// no body or no known shape yet, the spot is not loaded, the distance is out
    /// of range, or this host does not offer the check.
    /// </summary>
    Unknown = 0,

    /// <summary>A body the size of the character fits at the spot.</summary>
    Clear,

    /// <summary>Something solid is in the way at every height tried.</summary>
    Blocked,
}

/// <summary>
/// The answer of <see cref="INavigationAutomation.CheckRoomAhead"/>.
/// </summary>
/// <param name="Status">Whether there is room, or that the client could not tell.</param>
/// <param name="Position">
/// Where the body would stand when <paramref name="Status"/> is
/// <see cref="PluginRoomAheadStatus.Clear"/>, facing the way the character
/// faces; the default position otherwise.
/// </param>
public readonly record struct PluginRoomAhead(
    PluginRoomAheadStatus Status,
    PluginNavigationPosition Position)
{
    /// <summary>True when the check found room.</summary>
    public bool IsClear => Status == PluginRoomAheadStatus.Clear;
}

/// <summary>The outcome of a plan-only navigation request.</summary>
public enum PluginNavigationPlanStatus
{
    /// <summary>A path to the requested destination was found.</summary>
    Routed,
    /// <summary>No path to the requested destination could be planned.</summary>
    NoRoute,
    /// <summary>The character or navigation world is unavailable.</summary>
    Unavailable,
    /// <summary>The destination or arrival distance is invalid.</summary>
    InvalidTarget,
    /// <summary>The host could not complete the route search because of an internal error.</summary>
    Failed,
}

/// <summary>A detached path preview. Points run from the character toward the goal.</summary>
public sealed record PluginNavigationPlan(
    PluginNavigationPlanStatus Status,
    IReadOnlyList<PluginNavigationPosition> Path,
    string Reason)
{
    /// <summary>Distance along the planned route, in meters.</summary>
    public float LengthMeters { get; init; }
}

/// <summary>What became of a movement command a plugin sent.</summary>
public enum PluginNavigationCommandStatus
{
    /// <summary>
    /// The host cannot take movement commands at all right now: no session is
    /// in the world, or this host does not drive movement.
    /// </summary>
    Unavailable = 0,

    /// <summary>The command was taken and applied.</summary>
    Accepted,

    /// <summary>
    /// The host could take commands but refused this one, for instance because
    /// it arrived for a session that has already ended.
    /// </summary>
    Rejected,

    /// <summary>
    /// Another owner's walk is under way, so this one was refused rather than taking the
    /// character from it: a plugin cannot start or stop a walk another plugin started. The
    /// player's own commands always win.
    /// </summary>
    Held,
}

/// <summary>
/// Reading where the player and nearby objects are, and driving the player's
/// own movement.
/// </summary>
public interface INavigationAutomation
{
    /// <summary>
    /// Plans a path to an object without moving or taking ownership of the character.
    /// Call from the thread that raises <see cref="IEvents.Tick"/>: the world snapshot is
    /// captured before this method returns, then grid building and route search run on a
    /// worker. The result may become stale as the world changes.
    /// </summary>
    Task<PluginNavigationPlan> PreviewPathAsync(uint objectId, float arrivalMeters = 2.5f) =>
        Task.FromResult(new PluginNavigationPlan(PluginNavigationPlanStatus.Unavailable, [], "navigation is unavailable"));

    /// <summary>
    /// Plans a path to a cell-aware position without moving or taking ownership of
    /// the character. Call from the thread that raises <see cref="IEvents.Tick"/>.
    /// An elevation of NaN selects ground at the destination.
    /// </summary>
    Task<PluginNavigationPlan> PreviewPathAsync(PluginNavigationPosition position, float arrivalMeters = 2.5f) =>
        Task.FromResult(new PluginNavigationPlan(PluginNavigationPlanStatus.Unavailable, [], "navigation is unavailable"));

    /// <summary>
    /// Raised when the current navigation snapshot changes. Handlers run on
    /// the same thread as <see cref="IEvents.Tick"/>. Hosts that do not
    /// provide navigation leave this event inert. A subscription does not
    /// replay the current snapshot; read <see cref="Snapshot"/> first when an
    /// initial value is required.
    /// </summary>
    event Action<PluginNavigationSnapshot> SnapshotChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The player's current movement state. Its <c>IsAvailable</c> is false
    /// when no session is in the world.
    /// </summary>
    PluginNavigationSnapshot Snapshot { get; }

    /// <summary>Look up one world object by its id.</summary>
    /// <param name="objectId">The object id to look up.</param>
    /// <param name="value">The object, when one was found.</param>
    /// <returns>
    /// False when no session is in the world, the id is zero, the object is
    /// unknown to the client, or its position cannot be resolved.
    /// </returns>
    bool TryGetObject(uint objectId, out PluginNavigationObject value);

    /// <summary>
    /// Find the object closest to <paramref name="near"/> whose name matches
    /// exactly, ignoring letter case, within the given radius.
    /// </summary>
    /// <param name="name">The name to match.</param>
    /// <param name="near">The position distances are measured from.</param>
    /// <param name="maximumDistanceMeters">
    /// How far to search, in meters, measured along the ground.
    /// </param>
    /// <param name="value">The nearest matching object, when one was found.</param>
    /// <returns>
    /// False when nothing matched inside the radius, when the name is blank or
    /// the distance is negative or not a number, and on a host that does not
    /// implement the search: the default implementation always returns false.
    /// </returns>
    bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        value = default;
        return false;
    }

    /// <summary>
    /// A detached list of every world object whose position the client can
    /// resolve, ordered by object id, for plugin-owned proximity policies such
    /// as an automatic door opener. Hosts may return an empty list.
    /// </summary>
    /// <returns>
    /// The objects, or an empty list when no session is in the world or the
    /// host does not offer the projection.
    /// </returns>
    IReadOnlyList<PluginNavigationObject> CaptureObjects() =>
        Array.Empty<PluginNavigationObject>();

    /// <summary>
    /// Looks for room to set a body the size of the character down
    /// <paramref name="distanceMeters"/> straight ahead of where it faces: the
    /// room a pet needs before it is summoned. The client asks its own
    /// collision the way it places any object that enters the world, without
    /// letting the body slide aside, so a wall, a building, rising terrain, a
    /// door or any other solid object in that spot blocks it; live creatures
    /// and players standing there do not, as they move on. Ground a little
    /// higher or lower than the character's feet is tried too: up to 70 cm
    /// above and about 66 cm below. Nothing moves; call from the thread that
    /// raises <see cref="IEvents.Tick"/>.
    /// </summary>
    /// <param name="distanceMeters">
    /// How far ahead to look, in metres, above 0 and at most 10. Three metres
    /// is a summoned pet's usual distance.
    /// </param>
    /// <returns>
    /// Clear with the spot, Blocked, or Unknown when the client cannot look
    /// (see <see cref="PluginRoomAheadStatus.Unknown"/>); the default
    /// implementation always answers Unknown, so a plugin that must not stall
    /// on a host without the check can treat Unknown as room.
    /// </returns>
    PluginRoomAhead CheckRoomAhead(float distanceMeters) => default;

    /// <summary>
    /// Hold the given movement keys until the intent is replaced or cleared.
    /// </summary>
    /// <param name="intent">The keys to hold.</param>
    /// <returns>
    /// <see cref="PluginNavigationCommandStatus.Unavailable"/> when no session
    /// is in the world, otherwise whether the session took the command.
    /// </returns>
    PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent);

    /// <summary>Release every movement key the plugin was holding.</summary>
    /// <returns>
    /// <see cref="PluginNavigationCommandStatus.Unavailable"/> when no session
    /// is in the world, otherwise whether the session took the command.
    /// </returns>
    PluginNavigationCommandStatus ClearMovementIntent();

    /// <summary>Turn the player on the spot to face a compass direction.</summary>
    /// <param name="headingDegrees">
    /// The direction to face, in degrees clockwise from north.
    /// </param>
    /// <returns>
    /// <see cref="PluginNavigationCommandStatus.Unavailable"/> when no session
    /// is in the world or the host does not implement turning, which is what
    /// the default implementation always returns; otherwise whether the session
    /// took the command.
    /// </returns>
    PluginNavigationCommandStatus FaceHeading(float headingDegrees) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// Starts a move the client carries out and ends by itself. Moves on
    /// different channels are held together, the way movement keys are, so a
    /// plugin can run while it strafes or turns; a new move replaces only the
    /// move on its own channel. <paramref name="amount"/> counts
    /// <paramref name="unit"/>, and zero keeps going until stopped, for at most
    /// thirty seconds. A turn lands on its exact angle. A move also ends when it
    /// stops making progress or runs out of time, and every move ends when the
    /// player moves the character or it enters portal space.
    /// </summary>
    PluginNavigationCommandStatus Move(
        PluginMoveDirection direction,
        PluginMovePace pace,
        float amount,
        PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Ends every client-driven move in progress.</summary>
    PluginNavigationCommandStatus StopMoving() =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Ends the client-driven move on one channel, if there is one.</summary>
    PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Jumps with <paramref name="power"/> of a full charge, above 0 and at most 1.</summary>
    PluginNavigationCommandStatus Jump(float power) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>The most recent client-driven move on each channel, and the most recent jump.</summary>
    PluginMoveReport MoveReport => default;

    /// <summary>
    /// Walks the character to an object along a route the client plans through
    /// what it collides with: around walls and objects, through doorways, and up
    /// and down ramps and stairs. The walk ends within
    /// <paramref name="arrivalMeters"/> of the object, at a spot with no wall
    /// between the character and the object, facing it. When the character
    /// stops making progress the client plans again from where it stands,
    /// keeping out of the spot where it stuck, a few times. A later walk, <see cref="StopGoTo"/>, the player
    /// moving the character, or portal space ends it. While the character attacks,
    /// a plugin holds a movement intent, or a plugin that asked with
    /// <see cref="PauseGoToWhile"/> needs the character, the walk stops where the
    /// character stands and waits, then plans again from there and goes on. The
    /// walk steers with client-driven moves, so a plugin's own moves fight it while
    /// it lasts.
    /// </summary>
    PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// Walks the character to a place the way <see cref="GoTo(uint, float)"/> walks to
    /// an object: along a route the client plans, ending within
    /// <paramref name="arrivalMeters"/> of <paramref name="position"/> on the floor that
    /// position stands on, and waiting while something else needs the character. A position
    /// on a rock top is arrived at on the rock, or not at all. An elevation of NaN stands the
    /// position on the ground there, for map coordinates given without a height. It does not turn the character to face
    /// anything on arrival. A position with no cell, such as one read from a VTank route
    /// file, is placed by its map coordinates alone. <see cref="GoToReport"/> reports the
    /// walk with no object id.
    /// </summary>
    PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// Walks the character onto an object and stands it on the object's top, the highest
    /// floor on the object a body stands on, the way <see cref="GoTo(uint, float)"/> walks to
    /// an object: jumping up where the character can, ending within
    /// <paramref name="arrivalMeters"/> of the middle of that top and never on the ground
    /// beside the object. An object with nothing on top a body stands on, or no way up,
    /// ends the walk with no route. <see cref="StopGoTo"/> ends it and
    /// <see cref="GoToReport"/> reports it.
    /// </summary>
    PluginNavigationCommandStatus StandOn(uint objectId, float arrivalMeters) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>
    /// Follows a player until <see cref="StopGoTo"/>, a later walk, the player's own movement
    /// input, or leaving the world ends it. The client walks up behind the player, within
    /// <paramref name="bufferMeters"/> of them, holds there facing them, and plans again as they
    /// move, on the way as well as once there, leaping where the character can. It never gives
    /// up: with no route, or blocked, it tries again a moment later; with the player out of
    /// sight it waits for them; and a player who vanishes beside a portal is followed through
    /// it. Only players can be followed; anything else ends the walk with no route.
    /// <see cref="GoToReport"/> reports it, walking or waiting, for as long as it lasts.
    /// </summary>
    PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>Ends the walk to an object under way, if there is one.</summary>
    PluginNavigationCommandStatus StopGoTo() =>
        PluginNavigationCommandStatus.Unavailable;

    /// <summary>The most recent walk the client planned to an object.</summary>
    PluginGoToReport GoToReport => default;

    /// <summary>
    /// Has walks to objects wait for a plugin that sometimes needs the character,
    /// such as a combat macro. While <paramref name="need"/> returns what the plugin
    /// is doing, such as "fighting a monster", a walk under way stops where
    /// the character stands and reports that it waits on that. Once nothing has
    /// needed the character for a moment, the walk plans again from where the
    /// character stands and goes on. The client asks on the update thread, every
    /// frame a walk is under way, until the result is disposed.
    /// </summary>
    IDisposable PauseGoToWhile(Func<string?> need) => NoGoToPause.Instance;
}

file sealed class NoGoToPause : IDisposable
{
    public static NoGoToPause Instance { get; } = new();

    public void Dispose()
    {
    }
}
