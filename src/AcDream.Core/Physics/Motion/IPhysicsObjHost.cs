using System.Numerics;

namespace AcDream.Core.Physics.Motion;

public interface IPhysicsObjHost
{
    uint Id { get; }

    Position Position { get; }

    Vector3 Velocity { get; }

    float Radius { get; }

    bool InContact { get; }

    float? MinterpMaxSpeed { get; }

    double CurTime { get; }

    double PhysicsTimerTime { get; }

    IPhysicsObjHost? GetObjectA(uint id);

    IPhysicsObjHost? GetRelationshipTarget(uint objectId);

    void HandleUpdateTarget(TargetInfo info);

    void InterruptCurrentMovement();

    void SetTarget(uint contextId, uint objectId, float radius, double quantum);

    void ClearTarget();

    void ReceiveTargetUpdate(TargetInfo info, IPhysicsObjHost sender);

    void AddVoyeur(IPhysicsObjHost watcher, float radius, double quantum);

    void RemoveVoyeur(uint watcherId, IPhysicsObjHost expectedWatcher);
}
