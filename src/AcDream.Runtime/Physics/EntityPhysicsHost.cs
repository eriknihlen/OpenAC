using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Runtime.Physics;

public sealed class EntityPhysicsHost : IPhysicsObjHost
{
    private Func<Position> _getPosition;
    private Func<Vector3> _getVelocity;
    private Func<float> _getRadius;
    private Func<bool> _inContact;
    private Func<float?> _minterpMaxSpeed;
    private Func<double> _curTime;
    private Func<double> _physicsTimerTime;
    private Func<uint, IPhysicsObjHost?> _getObjectA;
    private Action<TargetInfo> _handleUpdateTarget;
    private Action _interruptCurrentMovement;
    private readonly TargetManager _targetManager;

    public EntityPhysicsHost(
        uint id,
        Func<Position> getPosition,
        Func<Vector3> getVelocity,
        Func<float> getRadius,
        Func<bool> inContact,
        Func<float?> minterpMaxSpeed,
        Func<double> curTime,
        Func<double> physicsTimerTime,
        Func<uint, IPhysicsObjHost?> getObjectA,
        Action<TargetInfo> handleUpdateTarget,
        Action interruptCurrentMovement)
    {
        Id = id;
        _getPosition = null!;
        _getVelocity = null!;
        _getRadius = null!;
        _inContact = null!;
        _minterpMaxSpeed = null!;
        _curTime = null!;
        _physicsTimerTime = null!;
        _getObjectA = null!;
        _handleUpdateTarget = null!;
        _interruptCurrentMovement = null!;
        Rebind(
            getPosition,
            getVelocity,
            getRadius,
            inContact,
            minterpMaxSpeed,
            curTime,
            physicsTimerTime,
            getObjectA,
            handleUpdateTarget,
            interruptCurrentMovement);
        _targetManager = new TargetManager(this);
        PositionManager = new PositionManager(this);
    }

    private void Rebind(
        Func<Position> getPosition,
        Func<Vector3> getVelocity,
        Func<float> getRadius,
        Func<bool> inContact,
        Func<float?> minterpMaxSpeed,
        Func<double> curTime,
        Func<double> physicsTimerTime,
        Func<uint, IPhysicsObjHost?> getObjectA,
        Action<TargetInfo> handleUpdateTarget,
        Action interruptCurrentMovement)
    {
        ArgumentNullException.ThrowIfNull(getPosition);
        ArgumentNullException.ThrowIfNull(getVelocity);
        ArgumentNullException.ThrowIfNull(getRadius);
        ArgumentNullException.ThrowIfNull(inContact);
        ArgumentNullException.ThrowIfNull(minterpMaxSpeed);
        ArgumentNullException.ThrowIfNull(curTime);
        ArgumentNullException.ThrowIfNull(physicsTimerTime);
        ArgumentNullException.ThrowIfNull(getObjectA);
        ArgumentNullException.ThrowIfNull(handleUpdateTarget);
        ArgumentNullException.ThrowIfNull(interruptCurrentMovement);

        _getPosition = getPosition;
        _getVelocity = getVelocity;
        _getRadius = getRadius;
        _inContact = inContact;
        _minterpMaxSpeed = minterpMaxSpeed;
        _curTime = curTime;
        _physicsTimerTime = physicsTimerTime;
        _getObjectA = getObjectA;
        _handleUpdateTarget = handleUpdateTarget;
        _interruptCurrentMovement = interruptCurrentMovement;
    }

    internal void RebindFrom(EntityPhysicsHost configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Id != Id)
            throw new ArgumentException(
                "A physics-host configuration must match the existing host GUID.",
                nameof(configuration));

        Rebind(
            configuration._getPosition,
            configuration._getVelocity,
            configuration._getRadius,
            configuration._inContact,
            configuration._minterpMaxSpeed,
            configuration._curTime,
            configuration._physicsTimerTime,
            configuration._getObjectA,
            configuration._handleUpdateTarget,
            configuration._interruptCurrentMovement);
    }

    // ── IPhysicsObjHost accessors ──────────────────────────────────────────
    public uint Id { get; }
    public Position Position => _getPosition();
    public Vector3 Velocity => _getVelocity();
    public float Radius => _getRadius();
    public bool InContact => _inContact();
    public float? MinterpMaxSpeed => _minterpMaxSpeed();
    public double CurTime => _curTime();
    public double PhysicsTimerTime => _physicsTimerTime();

    public TargetManager TargetManager => _targetManager;

    public PositionManager PositionManager { get; }

    // ── IPhysicsObjHost fan-out / target-tracking seams ────────────────────
    public IPhysicsObjHost? GetObjectA(uint id) => _getObjectA(id);
    public IPhysicsObjHost? GetRelationshipTarget(uint objectId) =>
        _targetManager.GetRelationshipTarget(objectId);

    public void HandleUpdateTarget(TargetInfo info)
    {
        _handleUpdateTarget(info);
        PositionManager.HandleUpdateTarget(info);
    }
    public void InterruptCurrentMovement() => _interruptCurrentMovement();

    public void SetTarget(uint contextId, uint objectId, float radius, double quantum)
        => _targetManager.SetTarget(contextId, objectId, radius, quantum);

    public void ClearTarget() => _targetManager.ClearTarget();
    public void ReceiveTargetUpdate(TargetInfo info, IPhysicsObjHost sender) =>
        _targetManager.ReceiveUpdate(info, sender);
    public void AddVoyeur(IPhysicsObjHost watcher, float radius, double quantum)
        => _targetManager.AddVoyeur(watcher, radius, quantum);
    public void RemoveVoyeur(uint watcherId, IPhysicsObjHost expectedWatcher) =>
        _targetManager.RemoveVoyeur(watcherId, expectedWatcher);

    // ── per-tick driver + Runtime-owned lifecycle ──────────────────────────

    public void HandleTargetting() => _targetManager.HandleTargetting();

    public void NotifyExitWorld() => _targetManager.NotifyVoyeurOfEvent(TargetStatus.ExitWorld);

    public void NotifyHidden() =>
        _targetManager.NotifyVoyeurOfEventAndClear(TargetStatus.ExitWorld);

    public void NotifyTeleported()
    {
        _targetManager.ClearTarget();
        _targetManager.NotifyVoyeurOfEvent(TargetStatus.Teleported);
    }
}
