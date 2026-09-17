using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Navigation;

// Who is driving. One walk runs at a time, and it belongs to whoever asked for it: a
// plugin, by id, or the player through a chat command. Another plugin asking while that
// walk is under way is refused rather than quietly taking the character; the player's own
// commands always win. A plugin that goes away takes its walk and its pauses with it.
//
// Ownership is keyed on the walk controller's request sequence: the owner recorded here
// holds the walk only while the controller's current request is the one it started. A
// request that reached the controller any other way (a route preview from chat, say) is
// the player's.
internal sealed partial class RuntimeNavigationAutomation : IScopedNavigationSource
{
    /// <summary>The owner of walks and pauses asked for through chat, which outrank every plugin's.</summary>
    internal const string PlayerOwner = "player";

    private string? _walkOwner;
    private long _walkSequence;

    /// <summary>Who owns the walk under way, or null when no walk is.</summary>
    internal string? WalkOwner
    {
        get
        {
            lock (_gate)
                return _walk is { } walk ? OwnerOfLocked(walk) : null;
        }
    }

    public INavigationAutomation ScopeTo(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return new OwnedNavigation(this, ownerId);
    }

    public void Release(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        lock (_gate)
        {
            _pauses.RemoveAll(pause => pause.Owner == ownerId);
            if (_walk is { } walk && OwnerOfLocked(walk) == ownerId)
            {
                walk.Stop();
                _walkOwner = null;
            }
        }
    }

    // The owner of the controller's current request: the recorded owner while the request
    // is the one it started, the player for any other request, null while nothing runs.
    private string? OwnerOfLocked(NavigationWalkController walk)
    {
        if (!walk.IsBusy)
            return null;
        return walk.Report.Sequence == _walkSequence ? _walkOwner : PlayerOwner;
    }

    // A walk request from an owner, refused while another owner's walk is under way unless
    // the request is the player's. The request reaches the controller under the gate so the
    // recorded owner is always the one whose request is current.
    private PluginNavigationCommandStatus BeginWalk(
        string owner,
        Func<PluginNavigationCommandStatus> validate,
        Func<NavigationWalkController, long> start)
    {
        lock (_gate)
        {
            if (!TryWalk(out NavigationWalkController walk))
                return PluginNavigationCommandStatus.Unavailable;
            PluginNavigationCommandStatus validity = validate();
            if (validity != PluginNavigationCommandStatus.Accepted)
                return validity;
            string? current = OwnerOfLocked(walk);
            if (owner != PlayerOwner && current is not null && current != owner)
                return PluginNavigationCommandStatus.Held;
            _walkSequence = start(walk);
            _walkOwner = owner;
            return PluginNavigationCommandStatus.Accepted;
        }
    }

    private static PluginNavigationCommandStatus ValidateArrival(uint objectId, float arrivalMeters) =>
        objectId == 0u || !(arrivalMeters > 0f) || arrivalMeters > MaximumGoToArrivalMeters
            ? PluginNavigationCommandStatus.Rejected
            : PluginNavigationCommandStatus.Accepted;

    private PluginNavigationCommandStatus GoToFor(string owner, uint objectId, float arrivalMeters) =>
        BeginWalk(
            owner,
            () => ValidateArrival(objectId, arrivalMeters),
            walk => walk.WalkTo(objectId, arrivalMeters));

    private PluginNavigationCommandStatus GoToFor(string owner, PluginNavigationPosition position, float arrivalMeters) =>
        BeginWalk(
            owner,
            () => !double.IsFinite(position.EastWest)
                || !double.IsFinite(position.NorthSouth)
                || double.IsInfinity(position.Elevation)
                || !(arrivalMeters > 0f)
                || arrivalMeters > MaximumGoToArrivalMeters
                ? PluginNavigationCommandStatus.Rejected
                : PluginNavigationCommandStatus.Accepted,
            walk => walk.WalkToPlace(position.CellId, RuntimeNavigationProjection.LandblockLocal(position), arrivalMeters));

    private PluginNavigationCommandStatus StandOnFor(string owner, uint objectId, float arrivalMeters) =>
        BeginWalk(
            owner,
            () => ValidateArrival(objectId, arrivalMeters),
            walk => walk.StandOn(objectId, arrivalMeters));

    private PluginNavigationCommandStatus FollowFor(string owner, uint playerId, float bufferMeters) =>
        BeginWalk(
            owner,
            () => ValidateArrival(playerId, bufferMeters),
            walk => walk.Follow(playerId, bufferMeters));

    // A plugin stops only the walk it started; the player stops any.
    private PluginNavigationCommandStatus StopGoToFor(string owner)
    {
        lock (_gate)
        {
            if (!TryWalk(out NavigationWalkController walk))
                return PluginNavigationCommandStatus.Unavailable;
            string? current = OwnerOfLocked(walk);
            if (current is null)
                return PluginNavigationCommandStatus.Rejected;
            if (owner != PlayerOwner && current != owner)
                return PluginNavigationCommandStatus.Held;
            walk.Stop();
            _walkOwner = null;
            return PluginNavigationCommandStatus.Accepted;
        }
    }

    private PluginGoToReport GoToReportFor()
    {
        lock (_gate)
        {
            if (!TryWalk(out NavigationWalkController walk))
                return default;
            NavigationWalkReport report = walk.Report;
            string? owner = walk.IsBusy
                ? report.Sequence == _walkSequence ? _walkOwner : PlayerOwner
                : null;
            return RuntimeNavigationProjection.GoToReport(report) with { Owner = owner };
        }
    }

    private IDisposable PauseGoToWhileFor(string owner, Func<string?> need)
    {
        ArgumentNullException.ThrowIfNull(need);
        var pause = new GoToPause(this, owner, need);
        lock (_gate)
            _pauses.Add(pause);
        return pause;
    }

    /// <summary>One plugin's view: reads and moves pass straight through; walks and pauses carry its id.</summary>
    private sealed class OwnedNavigation(RuntimeNavigationAutomation inner, string owner) : INavigationAutomation
    {
        public PluginNavigationSnapshot Snapshot => inner.Snapshot;

        public bool TryGetObject(uint objectId, out PluginNavigationObject value) =>
            inner.TryGetObject(objectId, out value);

        public bool TryFindObject(
            string name,
            in PluginNavigationPosition near,
            double maximumDistanceMeters,
            out PluginNavigationObject value) =>
            inner.TryFindObject(name, in near, maximumDistanceMeters, out value);

        public IReadOnlyList<PluginNavigationObject> CaptureObjects() => inner.CaptureObjects();

        public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent) =>
            inner.SetMovementIntent(in intent);

        public PluginNavigationCommandStatus ClearMovementIntent() => inner.ClearMovementIntent();

        public PluginNavigationCommandStatus FaceHeading(float headingDegrees) => inner.FaceHeading(headingDegrees);

        public PluginNavigationCommandStatus Move(
            PluginMoveDirection direction,
            PluginMovePace pace,
            float amount,
            PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees) =>
            inner.Move(direction, pace, amount, unit);

        public PluginNavigationCommandStatus StopMoving() => inner.StopMoving();

        public PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel) => inner.StopMoving(channel);

        public PluginNavigationCommandStatus Jump(float power) => inner.Jump(power);

        public PluginMoveReport MoveReport => inner.MoveReport;

        public PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters) =>
            inner.GoToFor(owner, objectId, arrivalMeters);

        public PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters) =>
            inner.GoToFor(owner, position, arrivalMeters);

        public PluginNavigationCommandStatus StandOn(uint objectId, float arrivalMeters) =>
            inner.StandOnFor(owner, objectId, arrivalMeters);

        public PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters) =>
            inner.FollowFor(owner, playerId, bufferMeters);

        public PluginNavigationCommandStatus StopGoTo() => inner.StopGoToFor(owner);

        public PluginGoToReport GoToReport => inner.GoToReportFor();

        public IDisposable PauseGoToWhile(Func<string?> need) => inner.PauseGoToWhileFor(owner, need);
    }
}
