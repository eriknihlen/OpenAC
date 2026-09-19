using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Properties;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Navigation;

/// <summary>The local player's body, sampled from its movement controller and moved with scripted moves.</summary>
internal sealed class RuntimeNavigationWalkBody : INavigationWalkBody
{
    /// <summary>How fast a character turns at a walk, 1.5 radians a second as its motion table sets; at a run it turns half again as fast.</summary>
    private const float WalkTurnDegreesPerSecond = 1.5f * (180f / MathF.PI);

    /// <summary>
    /// How far a character goes on after it lets go of a steady walk or run while still holding
    /// the pace it moved at, as a scripted move lets go; measured on the human motion table, the
    /// same at every run skill.
    /// </summary>
    internal const float WalkStopMeters = 0.41f;
    internal const float RunStopMeters = 0.79f;

    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly IRuntimePortalView _portal;

    public RuntimeNavigationWalkBody(RuntimeLocalPlayerMovementState movement, IRuntimePortalView portal)
    {
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
    }

    public bool TrySample(out NavigationWalkBodySample sample)
    {
        if (_movement.Controller is not { CellId: not 0u } controller)
        {
            sample = default;
            return false;
        }

        RuntimePortalSnapshot portal = _portal.Snapshot;
        float jumpHeight = controller.FullJumpHeight;
        sample = new NavigationWalkBodySample(
            controller.Position,
            MoveToMath.HeadingFromYaw(controller.Yaw),
            NavBody.Player(controller.StepUpHeight, controller.StepDownHeight),
            _movement.ScriptedMove,
            portal.Kind != RuntimePortalKind.None && !portal.Completed && !portal.Cancelled,
            controller.CellId,
            jumpHeight > 0f
                ? new NavLeapAbility(
                    MotionInterpreter.WalkAnimSpeed,
                    controller.RunSpeed,
                    jumpHeight,
                    NavigationWalkController.SafeDropMeters,
                    new NavLeapPhysics(controller.PhysicsStepSeconds, controller.Elasticity, controller.GroundFriction))
                : null,
            controller.IsAirborne,
            new RuntimeRouteTurning(
                controller.RunSpeed,
                WalkTurnDegreesPerSecond * MotionInterpreter.RunTurnFactor,
                MotionInterpreter.WalkAnimSpeed,
                WalkTurnDegreesPerSecond),
            WalkStopMeters,
            RunStopMeters,
            _movement.PlayerMovementInputFrames);
        return true;
    }

    public bool BeginMove(in RuntimeMoveRequest request) => _movement.BeginMove(request);

    public bool StopMove(RuntimeMoveChannel channel) => _movement.StopMove(channel);

    public bool BeginJump(float power, RuntimeMovePace? leaveAt) => _movement.BeginJump(power, leaveAt);
}

/// <summary>
/// Finds an object by its collision in the physics world, or failing that by
/// the position the server last gave it, placed relative to the local player.
/// </summary>
internal sealed class RuntimeNavigationGoalSource : INavigationGoalSource
{
    private readonly PhysicsEngine _physics;
    private readonly GameRuntime _runtime;
    private readonly RuntimeLocalPlayerMovementState _movement;

    public RuntimeNavigationGoalSource(
        PhysicsEngine physics,
        GameRuntime runtime,
        RuntimeLocalPlayerMovementState movement)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
    }

    /// <summary>The collision parts a query reads, reused from query to query on the thread that owns the physics world.</summary>
    private readonly List<ShadowEntry> _entries = [];

    /// <summary>Where an object the client knows stands in the physics world, from its body or the position the server gave it.</summary>
    private bool TryPlace(RuntimeEntityRecord record, out Vector3 position)
    {
        if (record.PhysicsBody is { } body && body.CellPosition.ObjCellId != 0u)
        {
            position = body.Position;
            return true;
        }
        if (_movement.Controller is { } controller && record.Snapshot.Position is { } placed)
        {
            AcDream.Core.Physics.Position here = controller.CellPosition;
            int blocksEast = (int)((placed.LandblockId >> 24) & 0xFFu) - (int)((here.ObjCellId >> 24) & 0xFFu);
            int blocksNorth = (int)((placed.LandblockId >> 16) & 0xFFu) - (int)((here.ObjCellId >> 16) & 0xFFu);
            position = controller.Position + new Vector3(
                (blocksEast * NavGeometry.LandblockSize) + placed.PositionX - here.Frame.Origin.X,
                (blocksNorth * NavGeometry.LandblockSize) + placed.PositionY - here.Frame.Origin.Y,
                placed.PositionZ - here.Frame.Origin.Z);
            return true;
        }
        position = default;
        return false;
    }

    public bool TryLocate(uint objectId, out Vector3 position)
    {
        bool known = _runtime.EntityObjects.Entities.TryGetActive(objectId, out RuntimeEntityRecord record);
        if (known && record.PhysicsBody is { } body && body.CellPosition.ObjCellId != 0u)
        {
            position = body.Position;
            return true;
        }

        _entries.Clear();
        _physics.ShadowObjects.CaptureEntries(objectId, _entries);
        if (_entries.Count > 0)
        {
            position = _entries[0].Position;
            return true;
        }

        if (known && _movement.Controller is { } controller && record.Snapshot.Position is { } placed)
        {
            AcDream.Core.Physics.Position here = controller.CellPosition;
            int blocksEast = (int)((placed.LandblockId >> 24) & 0xFFu) - (int)((here.ObjCellId >> 24) & 0xFFu);
            int blocksNorth = (int)((placed.LandblockId >> 16) & 0xFFu) - (int)((here.ObjCellId >> 16) & 0xFFu);
            position = controller.Position + new Vector3(
                (blocksEast * NavGeometry.LandblockSize) + placed.PositionX - here.Frame.Origin.X,
                (blocksNorth * NavGeometry.LandblockSize) + placed.PositionY - here.Frame.Origin.Y,
                placed.PositionZ - here.Frame.Origin.Z);
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>
    /// The server object whose collision edge comes nearest a spot. Live objects
    /// collide under the client's own ids, so each is matched back to its record;
    /// a door counts as closed while it still collides. A missile in flight blocks
    /// nothing, as it stands nowhere for <see cref="StandsStill"/>: an arrow or a
    /// spell's bolt passing a stalled body is not what stopped it.
    /// </summary>
    public bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
    {
        uint player = _runtime.PlayerIdentity.ServerGuid;
        RuntimeEntityRecord? nearestRecord = null;
        NavAvoidance nearestFootprint = default;
        float nearest = radius;
        _entries.Clear();
        _physics.ShadowObjects.CaptureEntries(_entries);
        foreach (ShadowEntry entry in _entries)
        {
            if (!_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.ServerGuid == player
                || !StandsAnywhere(record.FinalPhysicsState))
            {
                continue;
            }
            NavAvoidance footprint = NavGeometry.FootprintOf(entry, _physics.DataCache);
            float dx = footprint.Centre.X - position.X;
            float dy = footprint.Centre.Y - position.Y;
            float edge = MathF.Sqrt((dx * dx) + (dy * dy)) - footprint.Radius;
            if (edge < nearest)
            {
                nearest = edge;
                nearestRecord = record;
                nearestFootprint = footprint;
            }
        }
        if (nearestRecord is null)
        {
            blocker = default;
            return false;
        }

        uint objectId = nearestRecord.ServerGuid;
        ClientObject? item = _runtime.InventoryOwner.Objects.Get(objectId);
        bool door = item is not null
            && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Door) != 0;
        bool closed = door && !nearestRecord.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal);
        blocker = new NavigationBlocker(
            objectId,
            item?.Name ?? nearestRecord.Snapshot.Name ?? $"0x{objectId:X8}",
            closed,
            nearestFootprint.Centre,
            nearestFootprint.Radius,
            Moves: item is not null && Moves(item),
            Hostile: item is not null && Moves(item) && RuntimeHostileTargetQuery.IsHostile(_runtime, objectId));
        return true;
    }

    /// <summary>
    /// The server objects near a point whose collision stands in a route's way, as
    /// <see cref="StandsInTheWay"/> tells. Each part of an object's collision is a
    /// footprint of its own. A missile in flight stands nowhere, so an arrow or a
    /// spell's bolt on its way past is none of them.
    /// </summary>
    public bool StandsStill(uint entityLocalId) =>
        _runtime.EntityObjects.Entities.TryGetByLocalId(entityLocalId, out RuntimeEntityRecord record)
        && record.ServerGuid != _runtime.PlayerIdentity.ServerGuid
        && !record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal)
        && StandsAnywhere(record.FinalPhysicsState)
        && _runtime.InventoryOwner.Objects.Get(record.ServerGuid) is { } item
        && CanBeStoodOn(item);

    /// <summary>
    /// Whether an object in this state stands anywhere at all. A missile in flight does not: an
    /// arrow or a spell's bolt is somewhere else the moment after, so it is neither a wall a
    /// grid holds, nor something a route keeps out of, nor what stopped a walk that stalled.
    /// </summary>
    internal static bool StandsAnywhere(PhysicsStateFlags state) =>
        !state.HasFlag(PhysicsStateFlags.Missile);

    /// <summary>
    /// Whether a grid takes a server object's collision as the floors and walls it is:
    /// anything that stands where it is and a body can meet from any side, which is
    /// every object but a creature. A creature holds its ground but no body stands on
    /// one, and a door's collision comes and goes as it opens, so a route keeps out of
    /// those where they stand instead.
    /// </summary>
    internal static bool CanBeStoodOn(ClientObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return StandsInTheWay(item) && (item.Type & ItemType.Creature) == 0;
    }

    public IReadOnlyList<NavAvoidance> FindObstacles(
        Vector3 around,
        float radius,
        uint goalObjectId,
        IReadOnlySet<uint>? inGrid = null)
    {
        uint player = _runtime.PlayerIdentity.ServerGuid;
        var flatAround = new Vector2(around.X, around.Y);
        var obstacles = new List<NavAvoidance>();
        _entries.Clear();
        _physics.ShadowObjects.CaptureEntriesNear(flatAround, radius, _entries);
        foreach (ShadowEntry entry in _entries)
        {
            if (inGrid?.Contains(entry.EntityId) == true
                || ((PhysicsStateFlags)entry.State).HasFlag(PhysicsStateFlags.Ethereal)
                || !_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.ServerGuid == player
                || record.ServerGuid == goalObjectId
                || record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal)
                || !StandsAnywhere(record.FinalPhysicsState)
                || _runtime.InventoryOwner.Objects.Get(record.ServerGuid) is not { } item
                || !StandsInTheWay(item))
            {
                continue;
            }
            obstacles.Add(NavGeometry.FootprintOf(entry, _physics.DataCache));
        }
        return obstacles;
    }

    /// <summary>
    /// The portals near a point, other than the goal and any portal standing where the
    /// walk ends. A portal collides with nothing, so nothing else a route keeps out of
    /// holds it, yet a body that walks into one is sent somewhere else.
    /// </summary>
    public IReadOnlyList<NavAvoidance> FindPortals(Vector3 around, float radius, uint goalObjectId, Vector3 goal)
    {
        var flatAround = new Vector2(around.X, around.Y);
        var portals = new List<NavAvoidance>();
        var found = new HashSet<uint>();
        _entries.Clear();
        _physics.ShadowObjects.CaptureEntriesNear(flatAround, radius, _entries);
        foreach (ShadowEntry entry in _entries)
        {
            if (!_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.ServerGuid == goalObjectId
                || _runtime.InventoryOwner.Objects.Get(record.ServerGuid) is not { } item
                || !IsPortal(item))
            {
                continue;
            }
            portals.Add(PortalFootprint(NavGeometry.FootprintOf(entry, _physics.DataCache)));
            found.Add(record.ServerGuid);
        }

        // A portal need not be in the physics registry at all: it collides with
        // nothing, and one with no shape of its own is never put there. Every portal
        // the client knows of is kept out of all the same, by where it stands.
        foreach (RuntimeEntityRecord record in _runtime.EntityObjects.Entities.ActiveRecords.ToArray())
        {
            if (record.ServerGuid == goalObjectId
                || found.Contains(record.ServerGuid)
                || _runtime.InventoryOwner.Objects.Get(record.ServerGuid) is not { } item
                || !IsPortal(item)
                || !TryPlace(record, out Vector3 at)
                || Vector2.Distance(new Vector2(at.X, at.Y), flatAround) > radius + PortalLeastRadius)
            {
                continue;
            }
            portals.Add(PortalFootprint(new NavAvoidance(at, 0f)));
        }
        return ExceptTheOneTheWalkEnds(portals, goal);
    }

    /// <summary>
    /// The portals a route keeps out of: all but the one nearest where the walk ends,
    /// if that one stands near enough to be where it is going. In the Town Network the
    /// next portal stands ten metres away, so leaving out every portal near the goal
    /// would leave a neighbour for the walk to run into.
    /// </summary>
    internal static IReadOnlyList<NavAvoidance> ExceptTheOneTheWalkEnds(IReadOnlyList<NavAvoidance> portals, Vector3 goal)
    {
        ArgumentNullException.ThrowIfNull(portals);
        int going = -1;
        float nearest = float.PositiveInfinity;
        for (int index = 0; index < portals.Count; index++)
        {
            float away = Vector2.Distance(
                new Vector2(portals[index].Centre.X, portals[index].Centre.Y),
                new Vector2(goal.X, goal.Y));
            if (away < nearest && IsWhereTheWalkEnds(portals[index], goal))
            {
                nearest = away;
                going = index;
            }
        }
        return going < 0 ? portals : [.. portals.Where((_, index) => index != going)];
    }

    /// <summary>
    /// The least radius a portal is kept out of by, before the body's own. A portal's
    /// physics may state no shape at all, and a route that brushed its middle would
    /// still be sent through it.
    /// </summary>
    internal const float PortalLeastRadius = 1.5f;

    /// <summary>
    /// How near a walk's goal a portal may stand, beyond its own radius, and still be
    /// the portal the walk is going to. Travel walks to a spot a couple of metres from
    /// the portal it will use, where the compendium says the portal stands.
    /// </summary>
    internal const float PortalGoalReach = 3f;

    /// <summary>Whether an object is a portal, by its type or by the flag the server sends for one.</summary>
    internal static bool IsPortal(ClientObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.Type & ItemType.Portal) != 0
            || ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Portal) != 0;
    }

    internal static NavAvoidance PortalFootprint(NavAvoidance footprint) =>
        footprint with { Radius = MathF.Max(footprint.Radius, PortalLeastRadius) };

    /// <summary>
    /// Whether a portal stands where a walk ends, so the walk is going to it rather
    /// than past it: a walk to a portal by where it stands must still reach it.
    /// </summary>
    internal static bool IsWhereTheWalkEnds(NavAvoidance portal, Vector3 goal) =>
        Vector2.Distance(new Vector2(portal.Centre.X, portal.Centre.Y), new Vector2(goal.X, goal.Y))
            <= portal.Radius + PortalGoalReach;

    /// <summary>
    /// Whether a route keeps out of a server object's collision: not the collision of a
    /// player or a creature that moves, nor a door's, which walks open, nor a corpse's,
    /// which routes cross rather than go around. A creature that stands where it is, such
    /// as a vendor, is kept out of like any other object.
    /// </summary>
    internal static bool StandsInTheWay(ClientObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !Moves(item)
            && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u)
                & (PublicWeenieFlags.Door | PublicWeenieFlags.Corpse)) == 0;
    }

    /// <summary>
    /// A place in a cell's landblock frame, set relative to where the character stands in the
    /// physics world. A place with no height stands on the terrain there.
    /// </summary>
    public bool TryLocatePlace(uint cellId, Vector3 local, out Vector3 position)
    {
        if (_movement.Controller is not { } controller)
        {
            position = default;
            return false;
        }
        AcDream.Core.Physics.Position here = controller.CellPosition;
        position = OnTheGround(
            _physics,
            controller.Position + PlaceOffset(cellId, local, here.ObjCellId, here.Frame.Origin),
            controller.Position.Z);
        return true;
    }

    /// <summary>
    /// A point whose height is not known stood on the terrain under it, or at
    /// <paramref name="fallbackZ"/> where no terrain under it is loaded yet, as for a far goal
    /// walked to in stages, which is placed again as each stage ends. A point with a height is
    /// left as it is.
    /// </summary>
    internal static Vector3 OnTheGround(PhysicsEngine physics, Vector3 point, float fallbackZ) =>
        float.IsNaN(point.Z)
            ? point with { Z = physics.SampleTerrainZ(point.X, point.Y) ?? fallbackZ }
            : point;

    /// <summary>A point in the physics world measured from the corner of the first landblock, set relative to where the character stands.</summary>
    public bool TryGlobalOf(Vector3 world, out Vector3 global)
    {
        if (_movement.Controller is not { } controller)
        {
            global = default;
            return false;
        }
        AcDream.Core.Physics.Position here = controller.CellPosition;
        global = world - controller.Position - PlaceOffset(0u, Vector3.Zero, here.ObjCellId, here.Frame.Origin);
        return true;
    }

    public bool IsPlayer(uint objectId) =>
        _runtime.InventoryOwner.Objects.Get(objectId) is { } item
        && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Player) != 0;

    /// <summary>The portal the client knows of nearest a point, by where it stands.</summary>
    public bool TryFindPortal(Vector3 near, float radius, out uint portalId, out Vector3 position)
    {
        portalId = 0u;
        position = default;
        float nearest = radius;
        var flatNear = new Vector2(near.X, near.Y);
        foreach (RuntimeEntityRecord record in _runtime.EntityObjects.Entities.ActiveRecords.ToArray())
        {
            if (_runtime.InventoryOwner.Objects.Get(record.ServerGuid) is not { } item
                || !IsPortal(item)
                || !TryPlace(record, out Vector3 at))
            {
                continue;
            }
            float away = Vector2.Distance(new Vector2(at.X, at.Y), flatNear);
            if (away <= nearest)
            {
                nearest = away;
                portalId = record.ServerGuid;
                position = at;
            }
        }
        return portalId != 0u;
    }

    /// <summary>An object's collision, found under the client's own id for a live object.</summary>
    public bool TryGetSurfaces(uint objectId, out NavSurfaces surfaces)
    {
        uint entityId = _runtime.EntityObjects.Entities.TryGetActive(objectId, out RuntimeEntityRecord record)
            && record.LocalEntityId is { } local
                ? local
                : objectId;
        surfaces = NavGeometry.SurfacesOf(_physics, entityId)!;
        return surfaces is not null;
    }

    /// <summary>
    /// How far a place lies from where the character stands, each given as a cell and a
    /// point in that cell's landblock frame. A cell of zero measures its point from the
    /// corner of the first landblock, the way a place known only by its map coordinates is.
    /// </summary>
    internal static Vector3 PlaceOffset(uint cellId, Vector3 local, uint hereCellId, Vector3 hereLocal)
    {
        int blocksEast = (int)((cellId >> 24) & 0xFFu) - (int)((hereCellId >> 24) & 0xFFu);
        int blocksNorth = (int)((cellId >> 16) & 0xFFu) - (int)((hereCellId >> 16) & 0xFFu);
        return new Vector3(
            (blocksEast * NavGeometry.LandblockSize) + local.X - hereLocal.X,
            (blocksNorth * NavGeometry.LandblockSize) + local.Y - hereLocal.Y,
            local.Z - hereLocal.Z);
    }

    /// <summary>
    /// The players, and creatures that move, near a point, other than the character
    /// and the goal, each as the largest footprint among its parts' collision.
    /// </summary>
    public IReadOnlyList<NavAvoidance> FindCrowd(Vector3 around, float radius, uint goalObjectId)
    {
        uint player = _runtime.PlayerIdentity.ServerGuid;
        var flatAround = new Vector2(around.X, around.Y);
        var crowd = new Dictionary<uint, NavAvoidance>();
        _entries.Clear();
        _physics.ShadowObjects.CaptureEntriesNear(flatAround, radius, _entries);
        foreach (ShadowEntry entry in _entries)
        {
            if (((PhysicsStateFlags)entry.State).HasFlag(PhysicsStateFlags.Ethereal)
                || !_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.ServerGuid == player
                || record.ServerGuid == goalObjectId
                || record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal)
                || _runtime.InventoryOwner.Objects.Get(record.ServerGuid) is not { } item
                || !Moves(item))
            {
                continue;
            }
            NavAvoidance footprint = NavGeometry.FootprintOf(entry, _physics.DataCache);
            if (!crowd.TryGetValue(record.ServerGuid, out NavAvoidance kept) || footprint.Radius > kept.Radius)
                crowd[record.ServerGuid] = footprint;
        }
        return [.. crowd.Values];
    }

    /// <summary>
    /// Whether an object moves about: a player, or a creature that can be attacked or
    /// follows an owner. A creature that can't be attacked and follows no one, such as a
    /// vendor, stands where it is.
    /// </summary>
    internal static bool Moves(ClientObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var flags = (PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u);
        return (flags & PublicWeenieFlags.Player) != 0
            || ((item.Type & ItemType.Creature) != 0
                && ((flags & PublicWeenieFlags.Attackable) != 0 || item.PetOwnerId != 0u));
    }
}

/// <summary>
/// Doors as the client sees them. A door is closed while its physics still
/// collides, and using one goes through the client's own use, which walks the
/// character into the door's use range first.
/// </summary>
internal sealed class RuntimeNavigationDoors : INavigationDoors
{
    /// <summary>A door more than this far above or below the character is not on its way.</summary>
    private const float DoorHeightReach = 3f;

    /// <summary>How near an object's middle the client uses it where the character stands, where the server sends no range of its own.</summary>
    private const float DefaultUseReach = 0.6f;

    private readonly PhysicsEngine _physics;
    private readonly GameRuntime _runtime;
    private readonly Action<uint> _use;
    private readonly Func<uint, bool>? _appraise;

    public RuntimeNavigationDoors(PhysicsEngine physics, GameRuntime runtime, Action<uint> use, Func<uint, bool>? appraise = null)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _use = use ?? throw new ArgumentNullException(nameof(use));
        _appraise = appraise;
    }

    /// <summary>The collision parts a query reads, reused from query to query on the thread that owns the physics world.</summary>
    private readonly List<ShadowEntry> _entries = [];

    public bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door)
    {
        var start = new Vector2(from.X, from.Y);
        Vector2 along = new Vector2(to.X, to.Y) - start;
        float lengthSquared = along.LengthSquared();
        uint nearestId = 0u;
        NavAvoidance nearestFootprint = default;
        float nearestAhead = float.PositiveInfinity;
        _entries.Clear();
        _physics.ShadowObjects.CaptureEntries(_entries);
        foreach (ShadowEntry entry in _entries)
        {
            if (MathF.Abs(entry.Position.Z - from.Z) > DoorHeightReach
                || !_runtime.EntityObjects.Entities.TryGetByLocalId(entry.EntityId, out RuntimeEntityRecord record)
                || record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal)
                || !IsDoor(record.ServerGuid))
            {
                continue;
            }
            NavAvoidance footprint = NavGeometry.FootprintOf(entry, _physics.DataCache);
            var at = new Vector2(footprint.Centre.X, footprint.Centre.Y);
            float t = lengthSquared > 1e-6f ? Vector2.Dot(at - start, along) / lengthSquared : 0f;
            if (t < 0f)
                continue;
            t = MathF.Min(t, 1f);
            if (Vector2.Distance(at, start + (along * t)) > corridor)
                continue;
            float ahead = t * MathF.Sqrt(lengthSquared);
            if (ahead < nearestAhead)
            {
                nearestAhead = ahead;
                nearestId = record.ServerGuid;
                nearestFootprint = footprint;
            }
        }
        if (nearestId == 0u)
        {
            door = default;
            return false;
        }
        door = new NavigationDoor(
            nearestId,
            _runtime.InventoryOwner.Objects.Get(nearestId)?.Name ?? $"0x{nearestId:X8}",
            nearestFootprint.Centre,
            nearestFootprint.Radius);
        return true;
    }

    public bool IsOpen(uint doorId) =>
        _runtime.EntityObjects.Entities.TryGetActive(doorId, out RuntimeEntityRecord record)
        && record.FinalPhysicsState.HasFlag(PhysicsStateFlags.Ethereal);

    public void Use(uint doorId) => _use(doorId);

    public float UseReach(uint doorId) =>
        _runtime.InventoryOwner.Objects.Get(doorId) is { } item
            ? (float)item.Properties.GetFloat((uint)PropertyFloat.UseRadius, DefaultUseReach)
            : DefaultUseReach;

    /// <summary>What the client's last appraisal of a door said of its lock, or null before one arrives.</summary>
    public bool? IsLocked(uint doorId) =>
        _runtime.InventoryOwner.Objects.Get(doorId) is { LastAppraisalTimeMs: not 0 } item
            ? item.Properties.Bools.TryGetValue((uint)PropertyBool.Locked, out bool locked) && locked
            : null;

    public bool Appraise(uint doorId) => _appraise?.Invoke(doorId) == true;

    private bool IsDoor(uint objectId) =>
        _runtime.InventoryOwner.Objects.Get(objectId) is { } item
        && ((PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u) & PublicWeenieFlags.Door) != 0;
}
