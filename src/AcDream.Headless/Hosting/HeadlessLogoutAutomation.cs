using AcDream.Core.Items;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.Headless.Hosting;

internal enum HeadlessLogoutOutcome
{
    None,
    Completed,
    TimedOut,
}

// Sequences a plugin-requested logoff headless: no portal-tunnel presentation to route through, so
// it drives Begin/Complete on the session's own tick instead of LocalPlayerTeleportController.
internal sealed class HeadlessLogoutAutomation
{
    // Above the longest server logoff hold, so a real confirmation lands first.
    internal static readonly TimeSpan DefaultConfirmationDeadline =
        TimeSpan.FromSeconds(45);

    private readonly GameRuntime _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _confirmationDeadline;
    private readonly Func<bool> _isConfirmed;
    private readonly object _gate = new();
    private bool _began;
    private bool _completed;
    private long _deadlineTimestamp;

    internal HeadlessLogoutAutomation(
        GameRuntime runtime,
        TimeProvider? timeProvider = null,
        TimeSpan? confirmationDeadline = null,
        Func<bool>? isConfirmed = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _confirmationDeadline = confirmationDeadline ?? DefaultConfirmationDeadline;
        _isConfirmed = isConfirmed
            ?? (() => _runtime.Session.CurrentSession?.IsCharacterLogOffConfirmed == true);
    }

    internal bool CanRequestLogout
    {
        get
        {
            lock (_gate)
                return CanRequestLogoutLocked();
        }
    }

    private bool CanRequestLogoutLocked() =>
        _runtime.Session.IsInWorld
        && !_runtime.TransitOwner.IsLogoutActive
        && !_runtime.TransitOwner.IsTeleportActive
        && !_runtime.TransitOwner.HasPendingTeleportStart;

    internal bool TryRequestLogout()
    {
        lock (_gate)
        {
            if (!CanRequestLogoutLocked()
                || !_runtime.TransitOwner.TryBeginLogoutRequest(IsLocalPlayerKiller))
            {
                return false;
            }
            _began = false;
            _deadlineTimestamp = HeadlessMonotonicTime.Add(
                _timeProvider,
                _timeProvider.GetTimestamp(),
                _confirmationDeadline);
            TryBeginLocked();
            return true;
        }
    }

    private bool IsLocalPlayerKiller
    {
        get
        {
            var bitfield = (PublicWeenieFlags)(_runtime.InventoryOwner.Objects
                .Get(_runtime.PlayerIdentity.ServerGuid)?.PublicWeenieBitfield ?? 0u);
            return (bitfield & PublicWeenieFlags.PlayerKiller) != 0
                || (bitfield & PublicWeenieFlags.PlayerKillerLite) != 0;
        }
    }

    // A refusal here, past the transit guard above, means the call landed inside a nested
    // top-level session operation; the next session tick retries it.
    private void TryBeginLocked()
    {
        if (!_began
            && _runtime.Session.BeginCharacterLogOff(_runtime.Generation).Accepted)
        {
            _began = true;
        }
    }

    internal HeadlessLogoutOutcome Tick()
    {
        lock (_gate)
        {
            if (_completed || !_runtime.TransitOwner.IsLogoutActive)
                return HeadlessLogoutOutcome.None;
            if (!_began)
                TryBeginLocked();
            if (_began
                && _runtime.TransitOwner.LogoutStage == RuntimeLogoutStage.Requested
                && _isConfirmed())
            {
                _runtime.TransitOwner.AcknowledgeLogoutConfirmed();
            }
            if (_runtime.TransitOwner.LogoutStage == RuntimeLogoutStage.Confirmed
                && _runtime.Session.CompleteCharacterLogOff(_runtime.Generation).Accepted)
            {
                _runtime.TransitOwner.CompleteLogout();
                _completed = true;
                return HeadlessLogoutOutcome.Completed;
            }
            if (_timeProvider.GetTimestamp() < _deadlineTimestamp)
                return HeadlessLogoutOutcome.None;

            _completed = true;
            _runtime.TransitOwner.CancelLogoutRequest();
            return HeadlessLogoutOutcome.TimedOut;
        }
    }
}
