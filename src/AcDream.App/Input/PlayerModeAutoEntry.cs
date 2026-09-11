using System;
using AcDream.App.Net;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Runtime;
using AcDream.Runtime.Physics;

namespace AcDream.App.Input;

internal interface IPlayerModeAutoEntryContext
{
    bool IsLiveInWorld { get; }
    bool IsPlayerEntityPresent { get; }
    bool IsPlayerControllerReady { get; }
    bool IsWorldReady { get; }
    bool IsPlayerModeActive { get; }
    void EnterPlayerMode();
}

internal sealed class LivePlayerModeAutoEntryContext
    : IPlayerModeAutoEntryContext
{
    private readonly ILiveInWorldSource _session;
    private readonly ILiveWorldSessionSource _worldSession;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly WorldRevealCoordinator _worldReveal;
    private readonly ILocalPlayerModeSource _mode;
    private readonly PlayerModeController _playerMode;

    public LivePlayerModeAutoEntryContext(
        ILiveInWorldSource session,
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        WorldRevealCoordinator worldReveal,
        ILocalPlayerModeSource mode,
        PlayerModeController playerMode)
        : this(
            session,
            session as ILiveWorldSessionSource
                ?? throw new ArgumentException(
                    "Auto-entry requires an ILiveWorldSessionSource so LoginComplete can clear Hidden.",
                    nameof(session)),
            liveEntities,
            identity,
            worldReveal,
            mode,
            playerMode)
    {
    }

    public LivePlayerModeAutoEntryContext(
        ILiveInWorldSource session,
        ILiveWorldSessionSource worldSession,
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        WorldRevealCoordinator worldReveal,
        ILocalPlayerModeSource mode,
        PlayerModeController playerMode)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _worldSession = worldSession
            ?? throw new ArgumentNullException(nameof(worldSession));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _worldReveal = worldReveal ?? throw new ArgumentNullException(nameof(worldReveal));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _playerMode = playerMode ?? throw new ArgumentNullException(nameof(playerMode));
    }

    public bool IsLiveInWorld => _session.IsInWorld;

    public bool IsPlayerEntityPresent =>
        _liveEntities.ContainsWorldEntity(_identity.ServerGuid);

    public bool IsPlayerControllerReady =>
        _playerMode.Controller is { IsRuntimePublished: true }
        && _liveEntities.TryGetRecord(
            _identity.ServerGuid,
            out LiveEntityRecord record)
        && record.PhysicsHost is EntityPhysicsHost;

    public bool IsWorldReady =>
        _liveEntities.TryGetSnapshot(
            _identity.ServerGuid,
            out AcDream.Core.Net.WorldSession.EntitySpawn player)
        && player.Position is { LandblockId: not 0u } position
        && _worldReveal.Evaluate(position.LandblockId).IsReady;

    public bool IsPlayerModeActive => _mode.IsPlayerMode;

    public void EnterPlayerMode()
    {
        _playerMode.EnterFromAutoEntry();
        RuntimePortalSnapshot reveal = _worldReveal.Snapshot;
        if (reveal.Kind == RuntimePortalKind.Login
            && reveal.Generation != 0
            && !reveal.Completed
            && !reveal.Cancelled)
        {
            // Early ObserveLoginMaterialized can let auto-entry Complete the
            // login reveal before portal FireLoginComplete. Completing without
            // LoginComplete leaves PhysicsStateFlags.Hidden set — purple create
            // particles, TickHidden, no locomotion.
            _worldSession.CurrentSession?.SendGameAction(
                AcDream.Core.Net.Messages.GameActionLoginComplete.Build());
            Console.WriteLine(
                "live: auto-entry sent LoginComplete for incomplete login reveal "
                + $"(gen={reveal.Generation} cell=0x{reveal.Readiness.DestinationCell:X8})");
        }

        _worldReveal.Complete();
    }
}

public sealed class PlayerModeAutoEntry
{
    private sealed class DelegateContext : IPlayerModeAutoEntryContext
    {
        private readonly Func<bool> _isLiveInWorld;
        private readonly Func<bool> _isPlayerEntityPresent;
        private readonly Func<bool> _isPlayerControllerReady;
        private readonly Func<bool> _isWorldReady;
        private readonly Action _enterPlayerMode;
        private readonly Func<bool> _isPlayerModeActive;

        public DelegateContext(
            Func<bool> isLiveInWorld,
            Func<bool> isPlayerEntityPresent,
            Func<bool> isPlayerControllerReady,
            Func<bool> isWorldReady,
            Action enterPlayerMode,
            Func<bool>? isPlayerModeActive)
        {
            _isLiveInWorld = isLiveInWorld
                ?? throw new ArgumentNullException(nameof(isLiveInWorld));
            _isPlayerEntityPresent = isPlayerEntityPresent
                ?? throw new ArgumentNullException(nameof(isPlayerEntityPresent));
            _isPlayerControllerReady = isPlayerControllerReady
                ?? throw new ArgumentNullException(nameof(isPlayerControllerReady));
            _isWorldReady = isWorldReady
                ?? throw new ArgumentNullException(nameof(isWorldReady));
            _enterPlayerMode = enterPlayerMode
                ?? throw new ArgumentNullException(nameof(enterPlayerMode));
            _isPlayerModeActive = isPlayerModeActive ?? (() => false);
        }

        public bool IsLiveInWorld => _isLiveInWorld();
        public bool IsPlayerEntityPresent => _isPlayerEntityPresent();
        public bool IsPlayerControllerReady => _isPlayerControllerReady();
        public bool IsWorldReady => _isWorldReady();
        public bool IsPlayerModeActive => _isPlayerModeActive();
        public void EnterPlayerMode() => _enterPlayerMode();
    }

    private readonly IPlayerModeAutoEntryContext _context;

    private bool _armed;

    internal PlayerModeAutoEntry(IPlayerModeAutoEntryContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public PlayerModeAutoEntry(
        Func<bool> isLiveInWorld,
        Func<bool> isPlayerEntityPresent,
        Func<bool> isPlayerControllerReady,
        Func<bool> isWorldReady,
        Action enterPlayerMode,
        Func<bool>? isPlayerModeActive = null)
        : this(new DelegateContext(
            isLiveInWorld,
            isPlayerEntityPresent,
            isPlayerControllerReady,
            isWorldReady,
            enterPlayerMode,
            isPlayerModeActive))
    {
    }

    public bool IsArmed => _armed;

    public void Arm() => _armed = true;

    public void Cancel() => _armed = false;

    /// <summary>
    /// Guard tick. If the trigger is armed AND every precondition is
    /// satisfied, invokes <c>enterPlayerMode</c>, disarms, and
    /// returns true. Returns false otherwise (no side effects).
    /// </summary>
    public bool TryEnter()
    {
        if (!_armed) return false;
        if (_context.IsPlayerModeActive)
        {
            _armed = false;
            return false;
        }
        if (!_context.IsLiveInWorld) return false;
        if (!_context.IsPlayerEntityPresent) return false;
        if (!_context.IsPlayerControllerReady) return false;
        if (!_context.IsWorldReady) return false;

        _armed = false;
        _context.EnterPlayerMode();
        return true;
    }
}
