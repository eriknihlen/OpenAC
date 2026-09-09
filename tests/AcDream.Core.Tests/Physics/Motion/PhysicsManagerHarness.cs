using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Tests.Physics.Motion;

internal sealed class PhysicsObjHostStub : IPhysicsObjHost
{
    public readonly Dictionary<uint, PhysicsObjHostStub> World;

    public PhysicsObjHostStub(uint id, Dictionary<uint, PhysicsObjHostStub> world)
    {
        Id = id;
        World = world;
        World[id] = this;
    }

    public uint Id { get; }
    public Position Position { get; set; } = new(1u, Vector3.Zero, Quaternion.Identity);
    public Vector3 Velocity { get; set; } = Vector3.Zero;
    public float Radius { get; set; } = 0.5f;
    public bool InContact { get; set; } = true;
    public float? MinterpMaxSpeed { get; set; } = 1.0f;
    public double CurTime { get; set; }
    public double PhysicsTimerTime { get; set; }

    public bool Resolvable { get; set; } = true;

    // ── owned R5 managers ──────────────────────────────────────────────────
    private TargetManager? _targetManager;
    public TargetManager TargetManager => _targetManager ??= new TargetManager(this);
    public TargetManager? TargetManagerOrNull => _targetManager;

    private PositionManager? _positionManager;
    public PositionManager PositionManager => _positionManager ??= new PositionManager(this);

    // ── recorded fan-outs ──────────────────────────────────────────────────
    public readonly List<TargetInfo> HandleUpdateTargetCalls = new();
    public int InterruptCurrentMovementCalls;

    // ── IPhysicsObjHost ────────────────────────────────────────────────────
    public IPhysicsObjHost? GetObjectA(uint id)
        => World.TryGetValue(id, out var h) && h.Resolvable ? h : null;

    public IPhysicsObjHost? GetRelationshipTarget(uint objectId) =>
        _targetManager?.GetRelationshipTarget(objectId);

    public void HandleUpdateTarget(TargetInfo info)
    {
        HandleUpdateTargetCalls.Add(info);
        if (info.ContextId == 0)
            _positionManager?.HandleUpdateTarget(info);
    }

    public void InterruptCurrentMovement() => InterruptCurrentMovementCalls++;

    public void SetTarget(uint contextId, uint objectId, float radius, double quantum)
        => TargetManager.SetTarget(contextId, objectId, radius, quantum);

    public void ClearTarget() => _targetManager?.ClearTarget();

    public void ReceiveTargetUpdate(TargetInfo info, IPhysicsObjHost sender) =>
        _targetManager?.ReceiveUpdate(info, sender);

    public void AddVoyeur(IPhysicsObjHost watcher, float radius, double quantum)
        => TargetManager.AddVoyeur(watcher, radius, quantum);

    public void RemoveVoyeur(uint watcherId, IPhysicsObjHost expectedWatcher) =>
        _targetManager?.RemoveVoyeur(watcherId, expectedWatcher);

    // ── test helpers ───────────────────────────────────────────────────────
    public void SetOrigin(Vector3 origin)
        => Position = new Position(Position.ObjCellId, origin, Position.Frame.Orientation);

    public void AdvanceClocks(double seconds)
    {
        CurTime += seconds;
        PhysicsTimerTime += seconds;
    }
}
