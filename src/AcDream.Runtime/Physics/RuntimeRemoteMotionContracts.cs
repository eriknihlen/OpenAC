using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Runtime.Physics;

public interface IRuntimeRemoteMotion
{
    PhysicsBody Body { get; }
}

public interface IRuntimePhysicsHostConsumer
{
    void BindPhysicsHost(Func<IPhysicsObjHost?> read);
}

public interface IRuntimeCanonicalPhysicsConsumer
{
    void BindCanonicalRuntime(
        Func<IPhysicsObjHost?> readPhysicsHost,
        Func<uint> readCell,
        Action<uint> writeCell);
}

public interface IRuntimeCanonicalCellConsumer
{
    void BindCanonicalCell(Func<uint> read, Action<uint> write);
}

public interface IRuntimeRemotePlacement :
    IRuntimeRemoteMotion,
    IRuntimeCanonicalCellConsumer
{
    uint CellId { get; set; }
    bool Airborne { get; set; }
    Vector3 LastServerPosition { get; set; }
    double LastServerPositionTime { get; set; }
    Vector3 LastShadowSyncPosition { get; set; }
    Quaternion LastShadowSyncOrientation { get; set; }
    void HitGround();
    void LeaveGround();
}
