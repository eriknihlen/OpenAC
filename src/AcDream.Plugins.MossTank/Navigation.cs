using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum RouteMode
{
    Circular,
    Linear,
    Target,
    Once,
}

internal enum RouteWaypointType
{
    Point = 0,
    Portal = 1,
    Recall = 2,
    Pause = 3,
    ChatCommand = 4,
    OpenVendor = 5,
    PortalByName = 6,
    UseNpc = 7,
    Checkpoint = 8,
    Jump = 9,
}

internal enum RouteRecallKind
{
    PrimaryPortalRecall,
    SecondaryPortalRecall,
    LifestoneRecall,
    LifestoneSending,
    PortalRecall,
    RecallAphusLassel,
    RecallTheSanctuary,
    RecallToTheSingularityCaul,
    GlendenWoodRecall,
    AerlintheRecall,
    MountLetheRecall,
    UlgrimsRecall,
    BurRecall,
    ParadoxTouchedOlthoiInfestedAreaRecall,
    CallOfTheMhoireForge,
    ColosseumRecall,
    FacilityHubRecall,
    GearKnightInvasionAreaCampRecall,
    LostCityOfNeftetRecall,
    ReturnToTheKeep,
    RynthidRecall,
    ViridianRiseRecall,
    ViridianRiseGreatTreeRecall,
    CelestialHandStrongholdRecall,
    RadiantBloodStrongholdRecall,
    EldrytchWebStrongholdRecall,
    Marketplace,
}

internal enum RouteJumpDirection
{
    Forward,
    StrafeLeft,
    StrafeRight,
}

internal enum RouteInsertMode
{
    AddToEnd,
    InsertAbove,
    InsertBelow,
}

internal sealed class RouteWaypoint
{
    public RouteWaypointType Type { get; set; }
    public PluginNavigationPosition Position { get; set; }
    public PluginNavigationPosition ReferencePosition { get; set; }
    public uint ObjectId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    /// <summary>Decal ObjectClass retained for exact VTank NAV interchange.</summary>
    public int LegacyObjectClass { get; set; }
    public bool LegacyReferenceValid { get; set; } = true;
    public string Text { get; set; } = string.Empty;
    public int DurationMilliseconds { get; set; } = 5000;
    public RouteRecallKind Recall { get; set; }
    public uint RecallSpellId { get; set; }
    public string RecallSpellName { get; set; } = string.Empty;
    public float JumpHeadingDegrees { get; set; }
    public bool JumpRun { get; set; }
    public int JumpChargeMilliseconds { get; set; } = 1000;
    public RouteJumpDirection JumpDirection { get; set; }

    public RouteWaypoint Clone() => new()
    {
        Type = Type,
        Position = Position,
        ReferencePosition = ReferencePosition,
        ObjectId = ObjectId,
        ObjectName = ObjectName,
        LegacyObjectClass = LegacyObjectClass,
        LegacyReferenceValid = LegacyReferenceValid,
        Text = Text,
        DurationMilliseconds = DurationMilliseconds,
        Recall = Recall,
        RecallSpellId = RecallSpellId,
        RecallSpellName = RecallSpellName,
        JumpHeadingDegrees = JumpHeadingDegrees,
        JumpRun = JumpRun,
        JumpChargeMilliseconds = JumpChargeMilliseconds,
        JumpDirection = JumpDirection,
    };

    public string DisplayText => Type switch
    {
        RouteWaypointType.Point => $"Point: {FormatPosition(Position)}",
        RouteWaypointType.Portal => $"Portal: {ObjectLabel}",
        RouteWaypointType.Recall => $"Recall: {RecallLabel}",
        RouteWaypointType.Pause => string.Create(
            CultureInfo.InvariantCulture,
            $"Pause: {DurationMilliseconds / 1000d:0.###} seconds"),
        RouteWaypointType.ChatCommand => $"Chat command: {Text}",
        RouteWaypointType.OpenVendor => ObjectId == 0u
            ? "Close Vendor"
            : $"Open Vendor: {ObjectLabel}",
        RouteWaypointType.PortalByName => $"Portal: {ObjectLabel}",
        RouteWaypointType.UseNpc => $"Use NPC: {ObjectLabel}",
        RouteWaypointType.Checkpoint => $"Checkpoint: {FormatPosition(Position)}",
        RouteWaypointType.Jump =>
            $"Jump: {JumpHeadingDegrees.ToString("0.0", CultureInfo.InvariantCulture)}d, "
            + $"{JumpChargeMilliseconds.ToString(CultureInfo.InvariantCulture)}ms"
            + (JumpRun ? ", Shift" : string.Empty)
            + $", {JumpDirectionDisplayName(JumpDirection)}",
        _ => Type.ToString(),
    };

    private string ObjectLabel => string.IsNullOrWhiteSpace(ObjectName)
        ? $"0x{ObjectId:X8}"
        : ObjectName;

    internal string RecallLabel => !string.IsNullOrWhiteSpace(RecallSpellName)
        ? RecallSpellName
        : RecallSpellId != 0u
            ? RecallSpellId.ToString(CultureInfo.InvariantCulture)
            : RecallDisplayName(Recall);

    internal static string FormatPosition(in PluginNavigationPosition value)
    {
        string northSouth = value.NorthSouth >= 0d ? "N" : "S";
        string eastWest = value.EastWest >= 0d ? "E" : "W";
        return "("
            + Math.Abs(value.NorthSouth).ToString("0.###", CultureInfo.InvariantCulture)
            + northSouth
            + ", "
            + Math.Abs(value.EastWest).ToString("0.###", CultureInfo.InvariantCulture)
            + eastWest
            + ")";
    }

    internal static string RecallDisplayName(RouteRecallKind value) => value switch
    {
        RouteRecallKind.PrimaryPortalRecall => "Primary Portal Recall",
        RouteRecallKind.SecondaryPortalRecall => "Secondary Portal Recall",
        RouteRecallKind.LifestoneRecall => "Lifestone Recall",
        RouteRecallKind.LifestoneSending => "Lifestone Sending",
        RouteRecallKind.PortalRecall => "Portal Recall",
        RouteRecallKind.RecallAphusLassel => "Recall Aphus Lassel",
        RouteRecallKind.RecallTheSanctuary => "Recall the Sanctuary",
        RouteRecallKind.RecallToTheSingularityCaul => "Recall to the Singularity Caul",
        RouteRecallKind.GlendenWoodRecall => "Glenden Wood Recall",
        RouteRecallKind.AerlintheRecall => "Aerlinthe Recall",
        RouteRecallKind.MountLetheRecall => "Mount Lethe Recall",
        RouteRecallKind.UlgrimsRecall => "Ulgrim's Recall",
        RouteRecallKind.BurRecall => "Bur Recall",
        RouteRecallKind.ParadoxTouchedOlthoiInfestedAreaRecall =>
            "Paradox-touched Olthoi Infested Area Recall",
        RouteRecallKind.CallOfTheMhoireForge => "Call of the Mhoire Forge",
        RouteRecallKind.ColosseumRecall => "Colosseum Recall",
        RouteRecallKind.FacilityHubRecall => "Facility Hub Recall",
        RouteRecallKind.GearKnightInvasionAreaCampRecall =>
            "Gear Knight Invasion Area Camp Recall",
        RouteRecallKind.LostCityOfNeftetRecall => "Lost City of Neftet Recall",
        RouteRecallKind.ReturnToTheKeep => "Return to the Keep",
        RouteRecallKind.RynthidRecall => "Rynthid Recall",
        RouteRecallKind.ViridianRiseRecall => "Viridian Rise Recall",
        RouteRecallKind.ViridianRiseGreatTreeRecall => "Viridian Rise Great Tree Recall",
        RouteRecallKind.CelestialHandStrongholdRecall => "Celestial Hand Stronghold Recall",
        RouteRecallKind.RadiantBloodStrongholdRecall => "Radiant Blood Stronghold Recall",
        RouteRecallKind.EldrytchWebStrongholdRecall => "Eldrytch Web Stronghold Recall",
        RouteRecallKind.Marketplace => "Marketplace Recall",
        _ => value.ToString(),
    };

    internal static string RecallShortCaption(RouteRecallKind value) => value switch
    {
        RouteRecallKind.PrimaryPortalRecall => "Primary",
        RouteRecallKind.SecondaryPortalRecall => "Secondary",
        RouteRecallKind.LifestoneRecall => "LS",
        RouteRecallKind.LifestoneSending => "LS Sending",
        RouteRecallKind.PortalRecall => "Portal",
        RouteRecallKind.RecallAphusLassel => "Aphus",
        RouteRecallKind.RecallTheSanctuary => "Sanctuary",
        RouteRecallKind.RecallToTheSingularityCaul => "Caul",
        RouteRecallKind.GlendenWoodRecall => "GW",
        RouteRecallKind.AerlintheRecall => "Aerlinthe",
        RouteRecallKind.MountLetheRecall => "Mt. Lethe",
        RouteRecallKind.UlgrimsRecall => "Ulgrim's",
        RouteRecallKind.BurRecall => "Bur",
        RouteRecallKind.ParadoxTouchedOlthoiInfestedAreaRecall => "PtOIA",
        RouteRecallKind.CallOfTheMhoireForge => "Graveyard",
        RouteRecallKind.ColosseumRecall => "Colosseum",
        RouteRecallKind.FacilityHubRecall => "Fac. Hub",
        RouteRecallKind.GearKnightInvasionAreaCampRecall => "Gear K. Camp",
        RouteRecallKind.LostCityOfNeftetRecall => "Neftet",
        RouteRecallKind.ReturnToTheKeep => "Candeth",
        RouteRecallKind.RynthidRecall => "Rynthid",
        RouteRecallKind.ViridianRiseRecall => "VR Rocks",
        RouteRecallKind.ViridianRiseGreatTreeRecall => "VR Tree",
        RouteRecallKind.CelestialHandStrongholdRecall => "Soc. CH",
        RouteRecallKind.RadiantBloodStrongholdRecall => "Soc. RB",
        RouteRecallKind.EldrytchWebStrongholdRecall => "Soc. EW",
        RouteRecallKind.Marketplace => "Marketplace",
        _ => value.ToString(),
    };

    internal static uint SpellIdForRecall(RouteRecallKind value) => value switch
    {
        RouteRecallKind.PrimaryPortalRecall => 48u,
        RouteRecallKind.SecondaryPortalRecall => 2647u,
        RouteRecallKind.LifestoneRecall => 1635u,
        RouteRecallKind.LifestoneSending => 1636u,
        RouteRecallKind.PortalRecall => 2645u,
        RouteRecallKind.RecallAphusLassel => 2931u,
        RouteRecallKind.RecallTheSanctuary => 2023u,
        RouteRecallKind.RecallToTheSingularityCaul => 2943u,
        RouteRecallKind.GlendenWoodRecall => 3865u,
        RouteRecallKind.AerlintheRecall => 2041u,
        RouteRecallKind.MountLetheRecall => 2813u,
        RouteRecallKind.UlgrimsRecall => 2941u,
        RouteRecallKind.BurRecall => 4084u,
        RouteRecallKind.ParadoxTouchedOlthoiInfestedAreaRecall => 4198u,
        RouteRecallKind.CallOfTheMhoireForge => 4128u,
        RouteRecallKind.ColosseumRecall => 4213u,
        RouteRecallKind.FacilityHubRecall => 5175u,
        RouteRecallKind.GearKnightInvasionAreaCampRecall => 5330u,
        RouteRecallKind.LostCityOfNeftetRecall => 5541u,
        RouteRecallKind.ReturnToTheKeep => 4214u,
        RouteRecallKind.RynthidRecall => 6150u,
        RouteRecallKind.ViridianRiseRecall => 6321u,
        RouteRecallKind.ViridianRiseGreatTreeRecall => 6322u,
        RouteRecallKind.CelestialHandStrongholdRecall => 6325u,
        RouteRecallKind.RadiantBloodStrongholdRecall => 6327u,
        RouteRecallKind.EldrytchWebStrongholdRecall => 6326u,
        _ => 0u,
    };

    private static string JumpDirectionDisplayName(RouteJumpDirection value) =>
        value switch
        {
            RouteJumpDirection.StrafeLeft => "Strafe Left",
            RouteJumpDirection.StrafeRight => "Strafe Right",
            _ => "Forward",
        };
}

internal sealed class NavigationSettings
{
    public bool Enabled { get; set; }
    public bool Priority { get; set; }
    public RouteMode Mode { get; set; } = RouteMode.Circular;
    public double MinimumDistanceMeters { get; set; } = 2d;
    /// <summary>
    /// VTank's far stop range is the outer validity bound for a navigation
    /// rule, not a second arrival threshold.
    /// </summary>
    public double MaximumDistanceMeters { get; set; } = 999999d * 240d;
    public double PortalUseDistanceMeters { get; set; } = 4d;
    public uint FollowTargetObjectId { get; set; }
    public string FollowTargetName { get; set; } = string.Empty;
    public bool FollowAroundCorners { get; set; } = true;
    public bool OpenDoors { get; set; }
    public double DoorIdentifyRangeMeters { get; set; } = 20d;
    public double DoorOpenRangeMeters { get; set; } = 4d;
    public int DoorLockpickExcessThreshold { get; set; } = -50;
    public List<RouteWaypoint> Waypoints { get; } = [];
}

internal sealed class NavigationController
{
    private const float HeadingToleranceDegrees = 4f;
    private const float FarMovingTurnLimitDegrees = 45f;
    private const float NearMovingTurnLimitDegrees = 15f;
    private const double NearTargetMeters = 3d;
    private const double ChatInitialDelaySeconds = 0.2d;
    private const double UseRetrySeconds = 2d;
    private const double PortalTimeoutSeconds = 30d;
    private const double ObjectReacquireRadiusMeters = 2.5d;
    private const double PortalExitDistanceMeters = 15d;
    private const double RecallExitDistanceMeters = 2.4d;
    private const double JumpLaunchGraceSeconds = 0.25d;
    private const double JumpCompletionTimeoutSeconds = 3d;
    private const int JumpChargeCeilingMilliseconds = 2000;
    private const double CheckpointRetrySeconds = 15d;
    private const double FollowBreadcrumbSpacingMeters = 0.096d;
    private const double FollowPathCaptureRangeMeters = 240d;
    private const double FollowPathArrivalMeters = 2.4d;
    private const double DoorActionTimeoutSeconds = 5d;
    private const uint LockpickPublicFlag = 0x00020000u;
    private const uint LockpickSkillId = 23u;
    private const uint LockpickModifierProperty = 40u;

    private readonly IPluginHost _host;
    private readonly NavigationSettings _settings;
    private int _index;
    private bool _reverse;
    private bool _onceComplete;
    private RouteWaypoint? _activeAction;
    private double _actionElapsed;
    private double _retryElapsed;
    private long _useCompletionBaseline;
    private ulong _chatBaseline;
    private bool _actionSent;
    private bool _sawPortalSpace;
    private bool _jumpReleased;
    private bool _jumpAligned;
    private bool _jumpSawAirborne;
    private double _jumpChargeElapsed;
    private double _jumpReleaseElapsed;
    private double _checkpointElapsed;
    private readonly List<PluginNavigationPosition> _followPath = [];
    private uint _activeDoorObjectId;
    private uint _activeLockpickObjectId;
    private double _doorElapsed;
    private double _doorRetryElapsed;
    private PluginNavigationPosition _portalOrigin;
    private bool _hasPortalOrigin;
    private bool _hadMovementIntent;
    private string _status = "Navigation disabled.";

    public NavigationController(IPluginHost host, NavigationSettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Status => _status;
    public int CurrentWaypointIndex => _index;
    public bool Reversing => _reverse;

    public void ToggleReverse()
    {
        _reverse = !_reverse;
        _status = $"Nav backwards is {_reverse}.";
    }

    public void Reset()
    {
        StopMovement();
        _index = 0;
        _reverse = false;
        _onceComplete = false;
        _checkpointElapsed = 0d;
        _followPath.Clear();
        ClearDoor();
        ClearAction();
        _status = _settings.Enabled
            ? "Route ready."
            : "Navigation disabled.";
    }

    public void ClearActionLocks()
    {
        StopMovement();
        _checkpointElapsed = 0d;
        ClearDoor();
        ClearAction();
        _status = _settings.Enabled
            ? "Route action locks cleared."
            : "Navigation disabled.";
    }

    public bool Tick(double elapsedSeconds, bool canAct)
    {
        elapsedSeconds = double.IsFinite(elapsedSeconds)
            ? Math.Max(0d, elapsedSeconds)
            : 0d;
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot snapshot = navigation.Snapshot;
        if (!_settings.Enabled || !snapshot.IsAvailable)
        {
            StopMovement();
            _status = _settings.Enabled
                ? "Waiting for the world."
                : "Navigation disabled.";
            return false;
        }
        if (snapshot.IsPortalSpace)
        {
            StopMovement();
            if (_activeAction?.Type is RouteWaypointType.Portal
                or RouteWaypointType.PortalByName
                or RouteWaypointType.Recall)
            {
                _sawPortalSpace = true;
            }
            _status = "Waiting for portal space.";
            return _activeAction is not null;
        }
        if (!canAct)
        {
            StopMovement();
            _status = "Navigation paused.";
            return false;
        }

        if (TickDoor(navigation, snapshot, elapsedSeconds))
            return true;

        if (_settings.Mode == RouteMode.Target)
            return TickFollow(navigation, snapshot);
        if (_onceComplete || _settings.Waypoints.Count == 0)
        {
            StopMovement();
            _status = _onceComplete ? "Once route complete." : "Route is empty.";
            return false;
        }

        _index = Math.Clamp(_index, 0, _settings.Waypoints.Count - 1);
        RouteWaypoint waypoint = _settings.Waypoints[_index];
        if (waypoint.Type == RouteWaypointType.Point)
        {
            double distance = snapshot.Position.HorizontalDistanceMeters(
                waypoint.Position);
            if (distance > BoundedMaximumDistance())
            {
                StopMovement();
                _status = $"Waypoint is outside NavFarStopRange ({distance:0.0}m).";
                return false;
            }
            if (distance <= BoundedMinimumDistance())
            {
                StopMovement();
                AdvanceWaypoint();
                return true;
            }
            _status = string.Create(
                CultureInfo.InvariantCulture,
                $"Waypoint {_index + 1}/{_settings.Waypoints.Count}: {distance:0.0}m");
            return Steer(navigation, snapshot.Position, waypoint.Position, distance);
        }
        if (waypoint.Type == RouteWaypointType.Checkpoint)
            return TickCheckpoint(navigation, snapshot, waypoint, elapsedSeconds);

        StopMovement();
        return TickAction(waypoint, elapsedSeconds, snapshot);
    }

    private bool TickFollow(
        INavigationAutomation navigation,
        in PluginNavigationSnapshot snapshot)
    {
        if (_settings.FollowTargetObjectId == 0u
            || !navigation.TryGetObject(
                _settings.FollowTargetObjectId,
                out PluginNavigationObject target))
        {
            StopMovement();
            _status = "Follow target unavailable.";
            return false;
        }
        double distance = snapshot.Position.HorizontalDistanceMeters(
            target.Position);
        if (distance > BoundedMaximumDistance())
        {
            StopMovement();
            _status = $"Follow target is outside NavFarStopRange ({distance:0.0}m).";
            return false;
        }
        if (distance <= BoundedMinimumDistance())
        {
            StopMovement();
            _status = $"Following {target.Name}: holding {distance:0.0}m.";
            return false;
        }
        PluginNavigationPosition destination = CaptureFollowDestination(
            snapshot.Position,
            target.Position);
        double destinationDistance = snapshot.Position.HorizontalDistanceMeters(
            destination);
        _status = $"Following {target.Name}: {distance:0.0}m.";
        return Steer(navigation, snapshot.Position, destination, destinationDistance);
    }

    private PluginNavigationPosition CaptureFollowDestination(
        in PluginNavigationPosition current,
        in PluginNavigationPosition target)
    {
        if (!_settings.FollowAroundCorners)
        {
            _followPath.Clear();
            return target;
        }

        if (_followPath.Count == 0
            || _followPath[^1].HorizontalDistanceMeters(target)
                >= FollowBreadcrumbSpacingMeters)
        {
            _followPath.Add(target);
        }

        for (int index = _followPath.Count - 1; index >= 1; index--)
        {
            if (DistanceToSegmentMeters(
                    current,
                    _followPath[index - 1],
                    _followPath[index]) < FollowPathArrivalMeters
                && current.HorizontalDistanceMeters(_followPath[index])
                    < FollowPathCaptureRangeMeters)
            {
                _followPath.RemoveRange(0, index);
                break;
            }
        }
        return _followPath.Count == 0 ? target : _followPath[0];
    }

    private bool TickDoor(
        INavigationAutomation navigation,
        in PluginNavigationSnapshot snapshot,
        double elapsedSeconds)
    {
        if (!_settings.OpenDoors)
        {
            ClearDoor();
            return false;
        }

        IReadOnlyList<PluginNavigationObject> objects = navigation.CaptureObjects();
        PluginNavigationObject door = default;
        bool found = false;
        double nearest = _settings.DoorIdentifyRangeMeters;
        foreach (PluginNavigationObject candidate in objects)
        {
            if (!candidate.IsDoor || candidate.IsOpen)
                continue;
            double distance = snapshot.Position.HorizontalDistanceMeters(
                candidate.Position);
            if (_activeDoorObjectId != 0u
                && candidate.ObjectId == _activeDoorObjectId)
            {
                door = candidate;
                nearest = distance;
                found = true;
                break;
            }
            if (_activeDoorObjectId == 0u && distance <= nearest)
            {
                door = candidate;
                nearest = distance;
                found = true;
            }
        }

        if (!found)
        {
            ClearDoor();
            return false;
        }
        if (!door.HasLockState)
        {
            if (nearest <= _settings.DoorIdentifyRangeMeters
                && _host.Automation.Loot.Appraisal.AwaitingObjectId == 0u)
            {
                _ = _host.Automation.Loot.Identify(door.ObjectId);
            }
            if (nearest <= _settings.DoorOpenRangeMeters)
            {
                StopMovement();
                _status = $"Identifying door: {door.Name}.";
                return true;
            }
            return false;
        }
        if (nearest > _settings.DoorOpenRangeMeters)
        {
            ClearDoor();
            return false;
        }

        if (_activeDoorObjectId == 0u)
        {
            _activeDoorObjectId = door.ObjectId;
            _activeLockpickObjectId = door.IsLocked
                ? SelectLockpick(door.LockDifficulty)
                : 0u;
            if (door.IsLocked && _activeLockpickObjectId == 0u)
            {
                _status = $"Locked door skipped: {door.Name}.";
                ClearDoor();
                return false;
            }
        }

        StopMovement();
        _doorElapsed += elapsedSeconds;
        _doorRetryElapsed += elapsedSeconds;
        if (_doorElapsed >= DoorActionTimeoutSeconds)
        {
            _status = $"Door timed out: {door.Name}.";
            ClearDoor();
            return false;
        }
        if (_doorRetryElapsed == elapsedSeconds || _doorRetryElapsed >= UseRetrySeconds)
        {
            PluginItemCommandResult result = _activeLockpickObjectId == 0u
                ? _host.Automation.Items.Use(door.ObjectId)
                : _host.Automation.Items.Apply(
                    _activeLockpickObjectId,
                    door.ObjectId);
            _doorRetryElapsed = 0d;
            _status = result.Accepted
                ? _activeLockpickObjectId == 0u
                    ? $"Opening door: {door.Name}."
                    : $"Picking lock: {door.Name}."
                : $"Waiting for door: {door.Name}.";
        }
        return true;
    }

    private uint SelectLockpick(int difficulty)
    {
        if (!_host.Automation.Character.TryGetSkill(
                LockpickSkillId,
                out PluginSkillInfo skill)
            || skill.Current < Math.Max(
                0,
                difficulty + _settings.DoorLockpickExcessThreshold))
        {
            return 0u;
        }

        uint selected = 0u;
        double bestModifier = double.MinValue;
        foreach (PluginInventoryItem item in _host.Automation.Items.CaptureOwnedItems())
        {
            if ((item.PublicFlags & LockpickPublicFlag) == 0u)
                continue;
            double modifier = 0d;
            if (_host.Automation.Items.TryCaptureProperties(
                    item.ObjectId,
                    out PluginItemProperties properties)
                && properties.Floats.TryGetValue(
                    LockpickModifierProperty,
                    out double current))
            {
                modifier = current;
            }
            if (modifier <= bestModifier)
                continue;
            bestModifier = modifier;
            selected = item.ObjectId;
        }
        return selected;
    }

    private void ClearDoor()
    {
        _activeDoorObjectId = 0u;
        _activeLockpickObjectId = 0u;
        _doorElapsed = 0d;
        _doorRetryElapsed = 0d;
    }

    private bool TickCheckpoint(
        INavigationAutomation navigation,
        in PluginNavigationSnapshot snapshot,
        RouteWaypoint waypoint,
        double elapsedSeconds)
    {
        double liveDistance = snapshot.Position.HorizontalDistanceMeters(
            waypoint.Position);
        if (liveDistance > BoundedMaximumDistance())
        {
            StopMovement();
            _checkpointElapsed = 0d;
            _status = $"Checkpoint is outside NavFarStopRange ({liveDistance:0.0}m).";
            return false;
        }
        if (liveDistance > BoundedMinimumDistance())
        {
            _checkpointElapsed = 0d;
            _status = $"Checkpoint {_index + 1}/{_settings.Waypoints.Count}: {liveDistance:0.0}m";
            return Steer(
                navigation,
                snapshot.Position,
                waypoint.Position,
                liveDistance);
        }

        StopMovement();
        PluginNavigationPosition confirmed = snapshot.ConfirmedPositionRevision == 0UL
            ? snapshot.Position
            : snapshot.ConfirmedPosition;
        double confirmedDistance = confirmed.HorizontalDistanceMeters(
            waypoint.Position);
        if (confirmedDistance <= BoundedMinimumDistance())
        {
            _checkpointElapsed = 0d;
            AdvanceWaypoint();
            return true;
        }

        _checkpointElapsed += elapsedSeconds;
        _status = $"Checkpoint: waiting for server ({confirmedDistance:0.0}m).";
        if (_checkpointElapsed >= CheckpointRetrySeconds)
        {
            _checkpointElapsed = 0d;
            _hadMovementIntent = navigation.SetMovementIntent(
                new PluginMovementIntent(Forward: true, Run: false))
                == PluginNavigationCommandStatus.Accepted;
            _status = "Checkpoint: nudging for server confirmation.";
        }
        return true;
    }

    private bool Steer(
        INavigationAutomation navigation,
        in PluginNavigationPosition current,
        in PluginNavigationPosition target,
        double distanceMeters)
    {
        float desired = DesiredHeading(current, target);
        float delta = SignedHeadingDelta(current.HeadingDegrees, desired);
        float absolute = Math.Abs(delta);
        bool turnRight = delta > HeadingToleranceDegrees;
        bool turnLeft = delta < -HeadingToleranceDegrees;
        bool forward = absolute <= HeadingToleranceDegrees
            || (distanceMeters > NearTargetMeters
                ? absolute <= FarMovingTurnLimitDegrees
                : absolute <= NearMovingTurnLimitDegrees);
        var intent = new PluginMovementIntent(
            Forward: forward,
            TurnLeft: turnLeft,
            TurnRight: turnRight,
            Run: true);
        PluginNavigationCommandStatus result =
            navigation.SetMovementIntent(intent);
        _hadMovementIntent = result == PluginNavigationCommandStatus.Accepted;
        return _hadMovementIntent;
    }

    private bool TickAction(
        RouteWaypoint waypoint,
        double elapsedSeconds,
        in PluginNavigationSnapshot navigation)
    {
        if (!ReferenceEquals(_activeAction, waypoint))
        {
            ClearAction();
            _activeAction = waypoint;
            _useCompletionBaseline = _host.Automation.Items.LastCompletion.Revision;
            _chatBaseline = _host.Automation.Chat.CaptureMessages(0)
                .Select(static message => message.Sequence)
                .DefaultIfEmpty()
                .Max();
        }
        _actionElapsed += elapsedSeconds;
        _retryElapsed += elapsedSeconds;

        switch (waypoint.Type)
        {
            case RouteWaypointType.Pause:
                _status = $"Pause: {Math.Max(0d, waypoint.DurationMilliseconds / 1000d - _actionElapsed):0.0}s";
                if (_actionElapsed * 1000d >= Math.Max(0, waypoint.DurationMilliseconds))
                    CompleteAction();
                return true;

            case RouteWaypointType.ChatCommand:
                _status = $"Chat command: {waypoint.Text}";
                if (_actionElapsed < ChatInitialDelaySeconds)
                    return true;
                if (!_actionSent)
                {
                    _actionSent = _host.Automation.Chat.Submit(waypoint.Text);
                    if (!_actionSent)
                    {
                        _status = "Chat command was refused.";
                        return true;
                    }
                }
                CompleteAction();
                return true;

            case RouteWaypointType.Recall:
                return TickRecall(waypoint, navigation);

            case RouteWaypointType.Portal:
            case RouteWaypointType.PortalByName:
            case RouteWaypointType.UseNpc:
            case RouteWaypointType.OpenVendor:
                return TickUse(waypoint, navigation);

            case RouteWaypointType.Jump:
                return TickJump(waypoint, elapsedSeconds, navigation);

            default:
                CompleteAction();
                return true;
        }
    }

    private bool TickUse(
        RouteWaypoint waypoint,
        in PluginNavigationSnapshot navigation)
    {
        if (waypoint.Type is RouteWaypointType.PortalByName
            or RouteWaypointType.UseNpc)
        {
            bool currentStillExists = waypoint.ObjectId != 0u
                && _host.Automation.Navigation.TryGetObject(
                    waypoint.ObjectId,
                    out PluginNavigationObject current)
                && (string.IsNullOrWhiteSpace(waypoint.ObjectName)
                    || current.Name.Equals(
                        waypoint.ObjectName,
                        StringComparison.OrdinalIgnoreCase));
            if (!currentStillExists)
            {
                // Search near the object's own recorded position ("tgtxyz"
                // in metaf's ptl/tlk grammar), not where the bot was
                // standing when the waypoint was authored ("myxyz" —
                // waypoint.Position): the object can be meaningfully far
                // from the approach point (e.g. a portal at the far end of
                // a room).
                if (!_host.Automation.Navigation.TryFindObject(
                        waypoint.ObjectName,
                        waypoint.ReferencePosition,
                        ObjectReacquireRadiusMeters,
                        out PluginNavigationObject reacquired))
                {
                    _status = $"Finding {waypoint.ObjectName}.";
                    if (_actionElapsed >= PortalTimeoutSeconds)
                        CompleteAction();
                    return true;
                }
                waypoint.ObjectId = reacquired.ObjectId;
                _actionSent = false;
                _retryElapsed = UseRetrySeconds;
            }
        }
        if (waypoint.Type == RouteWaypointType.OpenVendor
            && waypoint.ObjectId != 0u
            && _host.Automation.Items.ActiveVendorObjectId == waypoint.ObjectId)
        {
            CompleteAction();
            return true;
        }
        if (waypoint.ObjectId == 0u)
        {
            _status = "Waypoint object is unavailable; continuing.";
            CompleteAction();
            return true;
        }

        if ((waypoint.Type is RouteWaypointType.Portal
                or RouteWaypointType.PortalByName)
            && _host.Automation.Navigation.TryGetObject(
                waypoint.ObjectId,
                out PluginNavigationObject portal))
        {
            double distance = navigation.Position.HorizontalDistanceMeters(
                portal.Position);
            if (distance > Math.Clamp(
                    _settings.PortalUseDistanceMeters,
                    0.5d,
                    50d))
            {
                _status = $"Approaching {waypoint.ObjectName} ({distance:0.0}m).";
                return Steer(
                    _host.Automation.Navigation,
                    navigation.Position,
                    portal.Position,
                    distance);
            }
        }

        if (waypoint.Type == RouteWaypointType.UseNpc
            && HasNpcResponse(waypoint.ObjectName))
        {
            CompleteAction();
            return true;
        }

        PluginItemUseCompletion completion =
            _host.Automation.Items.LastCompletion;
        if (_actionSent
            && completion.Revision > _useCompletionBaseline
            && completion.SourceObjectId == waypoint.ObjectId)
        {
            _useCompletionBaseline = completion.Revision;
            if (!completion.IsSuccess)
            {
                _actionSent = false;
                _retryElapsed = UseRetrySeconds;
            }
        }

        if (navigation.IsPortalSpace)
            _sawPortalSpace = true;
        if (_sawPortalSpace && !navigation.IsPortalSpace)
        {
            if (waypoint.Type == RouteWaypointType.PortalByName
                && _hasPortalOrigin
                && navigation.Position.HorizontalDistanceMeters(_portalOrigin)
                    <= PortalExitDistanceMeters)
            {
                _sawPortalSpace = false;
                _actionSent = false;
                _retryElapsed = UseRetrySeconds;
                _status = "Portal exit stayed near its origin; retrying.";
                return true;
            }
            CompleteAction();
            return true;
        }
        if (_actionElapsed >= PortalTimeoutSeconds)
        {
            _status = $"Use timed out: {waypoint.ObjectName}.";
            CompleteAction();
            return true;
        }
        if (!_actionSent || _retryElapsed >= UseRetrySeconds)
        {
            PluginItemCommandResult result =
                _host.Automation.Items.Use(waypoint.ObjectId);
            _actionSent |= result.Accepted;
            if (result.Accepted && !_hasPortalOrigin)
            {
                _portalOrigin = navigation.Position;
                _hasPortalOrigin = true;
            }
            _retryElapsed = 0d;
            _status = result.Accepted
                ? $"Using {waypoint.ObjectName}."
                : $"Waiting to use {waypoint.ObjectName}.";
        }
        return true;
    }

    private bool HasNpcResponse(string npcName)
    {
        IReadOnlyList<PluginChatMessage> messages =
            _host.Automation.Chat.CaptureMessages(_chatBaseline);
        foreach (PluginChatMessage message in messages)
        {
            _chatBaseline = Math.Max(_chatBaseline, message.Sequence);
            if (message.Sender.Equals(npcName, StringComparison.OrdinalIgnoreCase)
                || message.Text.StartsWith(
                    npcName + " tells you, ",
                    StringComparison.OrdinalIgnoreCase)
                || message.Text.StartsWith(
                    npcName + " gives you",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool TickRecall(
        RouteWaypoint waypoint,
        in PluginNavigationSnapshot navigation)
    {
        if (waypoint.RecallSpellId == 0u && waypoint.Recall != RouteRecallKind.Marketplace)
        {
            _status = string.IsNullOrWhiteSpace(waypoint.RecallSpellName)
                ? "Recall waypoint has no spell; skipping."
                : $"Recall spell '{waypoint.RecallSpellName}' not found; skipping waypoint.";
            CompleteAction();
            return true;
        }

        if (navigation.IsPortalSpace)
            _sawPortalSpace = true;
        if (_sawPortalSpace && !navigation.IsPortalSpace)
        {
            if (!_hasPortalOrigin
                || navigation.Position.HorizontalDistanceMeters(_portalOrigin)
                    > RecallExitDistanceMeters)
            {
                CompleteAction();
                return true;
            }
            _sawPortalSpace = false;
            _actionSent = false;
            _retryElapsed = UseRetrySeconds;
        }
        if (_actionElapsed >= PortalTimeoutSeconds)
        {
            _status = "Recall timed out; continuing route.";
            CompleteAction();
            return true;
        }
        if (!_actionSent || _retryElapsed >= UseRetrySeconds)
        {
            bool accepted = SubmitRecall(waypoint);
            _actionSent |= accepted;
            if (accepted && !_hasPortalOrigin)
            {
                _portalOrigin = navigation.Position;
                _hasPortalOrigin = true;
            }
            _retryElapsed = 0d;
        }
        _status = $"Recall: {waypoint.RecallLabel}.";
        return true;
    }

    private bool SubmitRecall(RouteWaypoint waypoint)
    {
        if (waypoint.RecallSpellId != 0u)
            return _host.Automation.Magic.Cast(waypoint.RecallSpellId);

        return _host.Automation.Chat.Submit("/marketplace");
    }

    private bool TickJump(
        RouteWaypoint waypoint,
        double elapsedSeconds,
        in PluginNavigationSnapshot navigation)
    {
        if (!_jumpReleased)
        {
            if (!_jumpAligned)
            {
                float delta = SignedHeadingDelta(
                    navigation.Position.HeadingDegrees,
                    waypoint.JumpHeadingDegrees);
                if (Math.Abs(delta) > HeadingToleranceDegrees)
                {
                    PluginMovementIntent turn = new(
                        TurnLeft: delta < 0f,
                        TurnRight: delta > 0f,
                        Run: waypoint.JumpRun);
                    _hadMovementIntent = _host.Automation.Navigation
                        .SetMovementIntent(turn)
                        == PluginNavigationCommandStatus.Accepted;
                    _status = $"Aligning jump: {Math.Abs(delta):0.0}d.";
                    return true;
                }
                _jumpAligned = true;
            }

            _jumpChargeElapsed += elapsedSeconds;
            int effectiveChargeMilliseconds = Math.Clamp(
                waypoint.JumpChargeMilliseconds, 0, JumpChargeCeilingMilliseconds);
            bool hold = _jumpChargeElapsed * 1000d < effectiveChargeMilliseconds;
            if (hold)
            {
                PluginMovementIntent intent = JumpIntent(waypoint, jump: true);
                _hadMovementIntent = _host.Automation.Navigation
                    .SetMovementIntent(intent)
                    == PluginNavigationCommandStatus.Accepted;
                _status = $"Charging jump: {effectiveChargeMilliseconds}ms.";
                return true;
            }
            PluginMovementIntent release = JumpIntent(waypoint, jump: false);
            _ = _host.Automation.Navigation.SetMovementIntent(release);
            _jumpReleased = true;
            _jumpReleaseElapsed = 0d;
            _status = "Jump released.";
            return true;
        }
        _jumpReleaseElapsed += elapsedSeconds;
        _jumpSawAirborne |= navigation.IsAirborne;
        if (navigation.IsAirborne
            || (!_jumpSawAirborne
                && _jumpReleaseElapsed < JumpCompletionTimeoutSeconds)
            || (_jumpSawAirborne
                && _jumpReleaseElapsed < JumpLaunchGraceSeconds))
            return true;
        CompleteAction();
        return true;
    }

    private static PluginMovementIntent JumpIntent(
        RouteWaypoint waypoint,
        bool jump) => waypoint.JumpDirection switch
        {
            RouteJumpDirection.StrafeLeft => new PluginMovementIntent(
                StrafeLeft: true, Run: waypoint.JumpRun, Jump: jump),
            RouteJumpDirection.StrafeRight => new PluginMovementIntent(
                StrafeRight: true, Run: waypoint.JumpRun, Jump: jump),
            _ => new PluginMovementIntent(
                Forward: true, Run: waypoint.JumpRun, Jump: jump),
        };

    private void CompleteAction()
    {
        ClearAction();
        AdvanceWaypoint();
    }

    private void AdvanceWaypoint()
    {
        ClearAction();
        _checkpointElapsed = 0d;
        int count = _settings.Waypoints.Count;
        if (count == 0)
            return;
        switch (_settings.Mode)
        {
            case RouteMode.Circular:
                _index = !_reverse
                    ? (_index + 1) % count
                    : (_index - 1 + count) % count;
                break;
            case RouteMode.Linear:
                if (!_reverse)
                {
                    _index++;
                    if (_index >= count)
                    {
                        _index = Math.Max(0, count - 1);
                        _reverse = true;
                    }
                }
                else
                {
                    _index--;
                    if (_index < 0)
                    {
                        _index = 0;
                        _reverse = false;
                    }
                }
                break;
            case RouteMode.Once:
                _settings.Waypoints.RemoveAt(_index);
                _index = 0;
                _onceComplete = _settings.Waypoints.Count == 0;
                break;
        }
    }

    private void ClearAction()
    {
        _activeAction = null;
        _actionElapsed = 0d;
        _retryElapsed = 0d;
        _useCompletionBaseline = 0;
        _chatBaseline = 0;
        _actionSent = false;
        _sawPortalSpace = false;
        _jumpReleased = false;
        _jumpAligned = false;
        _jumpSawAirborne = false;
        _jumpChargeElapsed = 0d;
        _jumpReleaseElapsed = 0d;
        _portalOrigin = default;
        _hasPortalOrigin = false;
    }

    private void StopMovement()
    {
        if (!_hadMovementIntent)
            return;
        _ = _host.Automation.Navigation.ClearMovementIntent();
        _hadMovementIntent = false;
    }

    private double BoundedMinimumDistance() => Math.Clamp(
        _settings.MinimumDistanceMeters,
        0.5d,
        50d);

    private double BoundedMaximumDistance() => Math.Max(
        BoundedMinimumDistance(),
        _settings.MaximumDistanceMeters);

    internal static float DesiredHeading(
        in PluginNavigationPosition from,
        in PluginNavigationPosition to)
    {
        double dx = to.EastWest - from.EastWest;
        double dy = to.NorthSouth - from.NorthSouth;
        double heading = Math.Atan2(dx, dy) * 180d / Math.PI;
        if (heading < 0d)
            heading += 360d;
        return (float)heading;
    }

    internal static float SignedHeadingDelta(float current, float desired)
    {
        float delta = (desired - current) % 360f;
        if (delta > 180f)
            delta -= 360f;
        else if (delta < -180f)
            delta += 360f;
        return delta;
    }

    internal static double DistanceToSegmentMeters(
        in PluginNavigationPosition point,
        in PluginNavigationPosition start,
        in PluginNavigationPosition end)
    {
        double dx = end.EastWest - start.EastWest;
        double dy = end.NorthSouth - start.NorthSouth;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= double.Epsilon)
            return point.HorizontalDistanceMeters(start);
        double projection = ((point.EastWest - start.EastWest) * dx
            + (point.NorthSouth - start.NorthSouth) * dy) / lengthSquared;
        projection = Math.Clamp(projection, 0d, 1d);
        double nearestX = start.EastWest + projection * dx;
        double nearestY = start.NorthSouth + projection * dy;
        double deltaX = point.EastWest - nearestX;
        double deltaY = point.NorthSouth - nearestY;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY) * 240d;
    }
}
