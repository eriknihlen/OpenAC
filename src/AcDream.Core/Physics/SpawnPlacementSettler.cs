using System.Numerics;

namespace AcDream.Core.Physics;

public static class SpawnPlacementSettler
{
    public const float SettleDistance = 0.5f;

    public static bool TrySettle(
        PhysicsEngine physicsEngine,
        PhysicsBody body,
        Vector3 worldPosition,
        uint cellId,
        float sphereRadius,
        float sphereHeight,
        ObjectInfoState moverFlags,
        uint movingEntityId,
        Action hitGround,
        Action leaveGround)
    {
        ArgumentNullException.ThrowIfNull(physicsEngine);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(hitGround);
        ArgumentNullException.ThrowIfNull(leaveGround);

        if (cellId == 0)
            return false;

        ResolveResult settle = physicsEngine.ResolveWithTransition(
            worldPosition,
            worldPosition - new Vector3(0f, 0f, SettleDistance),
            cellId,
            sphereRadius,
            sphereHeight,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: false,
            body,
            moverFlags,
            movingEntityId);
        if (!settle.Ok || !settle.InContact)
            return false;

        uint resolvedCellId = settle.CellId != 0
            ? settle.CellId
            : cellId;
        body.CommitTransitionPosition(resolvedCellId, settle.Position);
        PhysicsObjUpdate.CommitSetPositionTransition(
            body,
            settle.InContact,
            settle.OnWalkable,
            settle.CollisionNormalValid,
            settle.CollisionNormal,
            previousContact: false,
            previousOnWalkable: false,
            hitGround,
            leaveGround);
        return true;
    }
}
