using System.Numerics;
using DatReaderWriter.Enums;

namespace AcDream.Core.Physics;

internal static class FlatBspQuery
{
    private struct CollisionSphere
    {
        public Vector3 Center;
        public float Radius;

        public CollisionSphere(Vector3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }
    }

    private static ReadOnlySpan<Vector3> Vertices(
        FlatPhysicsBsp tree,
        in FlatCollisionPolygon polygon)
        => tree.PolygonTable.Vertices.AsSpan(
            polygon.VertexRange.Start,
            polygon.VertexRange.Count);

    private static bool NodeIntersects(
        in FlatPhysicsBspNode node,
        CollisionSphere sphere)
    {
        Vector3 d = sphere.Center - node.BoundingSphere.Origin;
        float radius = sphere.Radius + node.BoundingSphere.Radius;
        return d.LengthSquared() < radius * radius;
    }

    private static bool PosHitsSphere(
        FlatPhysicsBsp tree,
        int polygonIndex,
        CollisionSphere sphere,
        Vector3 movement,
        ref Vector3 contactPoint,
        ref int hitPolygonIndex)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        bool hit = BSPQuery.PolygonHitsSpherePrecise(
            polygon.Plane,
            Vertices(tree, polygon),
            sphere.Center,
            sphere.Radius,
            ref contactPoint);

        if (hit)
            hitPolygonIndex = polygonIndex;

        float moveDot = Vector3.Dot(movement, polygon.Plane.Normal);
        if (moveDot >= 0f)
            return false;

        return hit;
    }

    private static bool HitsSphere(
        FlatPhysicsBsp tree,
        int polygonIndex,
        CollisionSphere sphere)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        Vector3 contactPoint = Vector3.Zero;
        return BSPQuery.PolygonHitsSpherePrecise(
            polygon.Plane,
            Vertices(tree, polygon),
            sphere.Center,
            sphere.Radius,
            ref contactPoint);
    }

    private static bool WalkableHitsSphere(
        FlatPhysicsBsp tree,
        int polygonIndex,
        SpherePath path,
        CollisionSphere sphere,
        Vector3 up)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        float dp = Vector3.Dot(up, polygon.Plane.Normal);
        if (dp <= path.WalkableAllowance)
            return false;

        Vector3 contactPoint = Vector3.Zero;
        return BSPQuery.PolygonHitsSpherePrecise(
            polygon.Plane,
            Vertices(tree, polygon),
            sphere.Center,
            sphere.Radius,
            ref contactPoint);
    }

    private static bool CheckWalkable(
        FlatPhysicsBsp tree,
        int polygonIndex,
        CollisionSphere sphere,
        Vector3 up,
        bool small)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        ReadOnlySpan<Vector3> vertices = Vertices(tree, polygon);

        float angleUp = Vector3.Dot(polygon.Plane.Normal, up);
        if (angleUp < PhysicsGlobals.EPSILON)
            return false;

        float angle =
            (Vector3.Dot(polygon.Plane.Normal, sphere.Center) + polygon.Plane.D) /
            angleUp;
        Vector3 center = sphere.Center - up * angle;

        float radiusSquared = sphere.Radius * sphere.Radius;
        if (small)
            radiusSquared *= 0.25f;

        int previousIndex = vertices.Length - 1;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 vertex = vertices[i];
            Vector3 previousVertex = vertices[previousIndex];
            previousIndex = i;

            Vector3 edge = vertex - previousVertex;
            Vector3 displacement = center - previousVertex;
            Vector3 cross = Vector3.Cross(polygon.Plane.Normal, edge);
            float difference = Vector3.Dot(displacement, cross);

            if (difference < 0f)
            {
                if (cross.LengthSquared() * radiusSquared < difference * difference)
                    return false;

                float displacementAlongEdge = Vector3.Dot(displacement, edge);
                if (displacementAlongEdge >= 0f &&
                    displacementAlongEdge <= edge.LengthSquared())
                {
                    return true;
                }

                return false;
            }

            if (displacement.LengthSquared() <= radiusSquared)
                return true;
        }

        return true;
    }

    private static bool AdjustSphereToPlane(
        FlatPhysicsBsp tree,
        int polygonIndex,
        SpherePath path,
        ref CollisionSphere validPosition,
        Vector3 movement)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];

        Vector3 inputCenter = validPosition.Center;
        float walkInterpolationBefore = path.WalkInterp;
        float positionDot =
            Vector3.Dot(validPosition.Center, polygon.Plane.Normal) + polygon.Plane.D;
        float movementDot = Vector3.Dot(movement, polygon.Plane.Normal);
        float distance;

        if (movementDot <= PhysicsGlobals.EPSILON)
        {
            if (movementDot >= -PhysicsGlobals.EPSILON)
            {
                LogPushBackAdjust(
                    tree,
                    polygonIndex,
                    inputCenter,
                    validPosition.Center,
                    validPosition.Radius,
                    walkInterpolationBefore,
                    path.WalkInterp,
                    positionDot,
                    movementDot,
                    0f,
                    applied: false);
                return false;
            }

            distance = positionDot - validPosition.Radius;
        }
        else
        {
            distance = -validPosition.Radius - positionDot;
        }

        float inverseDistance = distance / movementDot;
        float interpolation = (1f - inverseDistance) * path.WalkInterp;

        if (interpolation >= path.WalkInterp || interpolation < -0.5f)
        {
            LogPushBackAdjust(
                tree,
                polygonIndex,
                inputCenter,
                validPosition.Center,
                validPosition.Radius,
                walkInterpolationBefore,
                path.WalkInterp,
                positionDot,
                movementDot,
                inverseDistance,
                applied: false);
            return false;
        }

        validPosition.Center -= movement * inverseDistance;
        path.WalkInterp = interpolation;

        LogPushBackAdjust(
            tree,
            polygonIndex,
            inputCenter,
            validPosition.Center,
            validPosition.Radius,
            walkInterpolationBefore,
            path.WalkInterp,
            positionDot,
            movementDot,
            inverseDistance,
            applied: true);

        if (PhysicsDiagnostics.ProbePolyDumpEnabled)
        {
            PhysicsDiagnostics.LogPolyDump(
                path.CheckCellId,
                MaterializeResolvedPolygon(tree, polygonIndex));
        }

        return true;
    }

    private static void LogPushBackAdjust(
        FlatPhysicsBsp tree,
        int polygonIndex,
        Vector3 inputCenter,
        Vector3 outputCenter,
        float radius,
        float walkInterpolationBefore,
        float walkInterpolationAfter,
        float positionDot,
        float movementDot,
        float inverseDistance,
        bool applied)
    {
        if (!PhysicsDiagnostics.ProbePushBackEnabled)
            return;

        PhysicsDiagnostics.LogPushBackAdjust(
            inputCenter,
            outputCenter,
            tree.PolygonTable.Polygons[polygonIndex].Plane,
            radius,
            walkInterpolationBefore,
            walkInterpolationAfter,
            positionDot,
            movementDot,
            inverseDistance,
            applied);
    }

    private static bool FindCrossedEdge(
        FlatPhysicsBsp tree,
        int polygonIndex,
        CollisionSphere sphere,
        Vector3 up,
        ref Vector3 normal)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        if (!BSPQuery.FindCrossedEdge(
                polygon.Plane,
                Vertices(tree, polygon),
                sphere.Center,
                up,
                out Vector3 crossedNormal))
        {
            return false;
        }

        normal = crossedNormal;
        return true;
    }

    private static Vector3 TransformNormal(Vector3 normal, Quaternion localToWorld)
    {
        Vector3 worldNormal = Vector3.Transform(normal, localToWorld);
        return worldNormal.LengthSquared() > PhysicsGlobals.EpsilonSq
            ? Vector3.Normalize(worldNormal)
            : Vector3.UnitZ;
    }

    private static Plane BuildWorldPlane(
        Vector3 worldNormal,
        ReadOnlySpan<Vector3> localVertices,
        Quaternion localToWorld,
        float scale,
        Vector3 worldOrigin)
    {
        float d = localVertices.Length > 0
            ? -Vector3.Dot(
                worldNormal,
                Vector3.Transform(localVertices[0] * scale, localToWorld) + worldOrigin)
            : 0f;
        return new Plane(worldNormal, d);
    }

    private static void AdjustToPlacementPolygon(
        FlatPhysicsBsp tree,
        int polygonIndex,
        ref CollisionSphere validPosition,
        ref CollisionSphere validPosition2,
        bool hasValidPosition2,
        float radius,
        bool centerSolid,
        bool clearCell)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        Vector3 moveDirection = Vector3.Zero;

        if (centerSolid)
        {
            moveDirection = polygon.Plane.Normal;
        }
        else
        {
            Vector3 up = Vector3.UnitZ;
            if (!FindCrossedEdge(
                    tree,
                    polygonIndex,
                    validPosition,
                    up,
                    ref moveDirection))
            {
                moveDirection = polygon.Plane.Normal;
            }
        }

        float distance =
            Vector3.Dot(validPosition.Center, polygon.Plane.Normal) + polygon.Plane.D;
        float pushAmount = radius - distance;
        if (pushAmount <= 0f)
            pushAmount = PhysicsGlobals.EPSILON;

        Vector3 offset = moveDirection * pushAmount;
        validPosition.Center += offset;
        if (hasValidPosition2)
            validPosition2.Center += offset;
    }

    private static float AdjustSphereToPolygon(
        FlatPhysicsBsp tree,
        int polygonIndex,
        CollisionSphere checkPosition,
        Vector3 currentPosition,
        Vector3 movement)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        float positionDot =
            Vector3.Dot(currentPosition, polygon.Plane.Normal) + polygon.Plane.D;
        if (MathF.Abs(positionDot) < checkPosition.Radius)
            return 1f;

        float movementDot = Vector3.Dot(movement, polygon.Plane.Normal);
        if (MathF.Abs(movementDot) <= PhysicsGlobals.EPSILON)
            return 0f;

        float radius = positionDot < 0f
            ? -checkPosition.Radius
            : checkPosition.Radius;
        return (radius - positionDot) / movementDot;
    }

    public static bool PointInsideCellBsp(
        FlatCellContainmentBsp tree,
        Vector3 point)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return PointInsideCellBsp(tree, tree.RootIndex, point);
    }

    private static bool PointInsideCellBsp(
        FlatCellContainmentBsp tree,
        int nodeIndex,
        Vector3 point)
    {
        if (nodeIndex < 0)
            return true;

        FlatCellBspNode node = tree.Nodes[nodeIndex];
        if (node.Type == BSPNodeType.Leaf)
            return true;

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, point) + node.SplittingPlane.D;
        if (distance >= 0f)
        {
            return node.PositiveChildIndex >= 0
                ? PointInsideCellBsp(tree, node.PositiveChildIndex, point)
                : true;
        }

        return false;
    }

    public static bool SphereIntersectsCellBsp(
        FlatCellContainmentBsp tree,
        Vector3 center,
        float radius)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return SphereIntersectsCellBsp(tree, tree.RootIndex, center, radius);
    }

    private static bool SphereIntersectsCellBsp(
        FlatCellContainmentBsp tree,
        int nodeIndex,
        Vector3 center,
        float radius)
    {
        if (nodeIndex < 0)
            return true;

        FlatCellBspNode node = tree.Nodes[nodeIndex];
        if (node.Type == BSPNodeType.Leaf)
            return true;

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, center) + node.SplittingPlane.D;
        float expandedRadius = radius + 0.01f;
        if (distance < -expandedRadius)
            return false;

        return node.PositiveChildIndex >= 0
            ? SphereIntersectsCellBsp(
                tree,
                node.PositiveChildIndex,
                center,
                radius)
            : true;
    }

    public static bool BoxIntersectsCellBsp(
        FlatCellContainmentBsp tree,
        Vector3 min,
        Vector3 max)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return BoxIntersectsCellBsp(tree, tree.RootIndex, min, max);
    }

    private static bool BoxIntersectsCellBsp(
        FlatCellContainmentBsp tree,
        int nodeIndex,
        Vector3 min,
        Vector3 max)
    {
        if (nodeIndex < 0)
            return true;

        FlatCellBspNode node = tree.Nodes[nodeIndex];
        if (node.Type == BSPNodeType.Leaf)
            return true;

        if (BSPQuery.ClassifyBox(node.SplittingPlane, min, max) ==
            BSPQuery.PlaneSide.Negative)
        {
            return false;
        }

        return node.PositiveChildIndex >= 0
            ? BoxIntersectsCellBsp(
                tree,
                node.PositiveChildIndex,
                min,
                max)
            : true;
    }

    /// <summary>
    /// Flat static sphere/polygon overlap shadow query.
    /// </summary>
    public static bool SphereIntersectsPoly(
        FlatPhysicsBsp tree,
        Vector3 sphereCenter,
        float sphereRadius,
        out ushort hitPolygonId,
        out Vector3 hitNormal)
    {
        ArgumentNullException.ThrowIfNull(tree);
        hitPolygonId = 0;
        hitNormal = Vector3.Zero;
        if (tree.RootIndex < 0)
            return false;

        return SphereIntersectsPolyStaticRecurse(
            tree,
            tree.RootIndex,
            sphereCenter,
            sphereRadius,
            ref hitPolygonId,
            ref hitNormal);
    }

    private static bool SphereIntersectsPolyStaticRecurse(
        FlatPhysicsBsp tree,
        int nodeIndex,
        Vector3 center,
        float radius,
        ref ushort hitPolygonId,
        ref Vector3 hitNormal)
    {
        if (nodeIndex < 0)
            return false;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        Vector3 difference = center - node.BoundingSphere.Origin;
        float combinedRadius = radius + node.BoundingSphere.Radius;
        if (difference.LengthSquared() >= combinedRadius * combinedRadius)
            return false;

        if (node.Type == BSPNodeType.Leaf)
        {
            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                FlatCollisionPolygon polygon =
                    tree.PolygonTable.Polygons[polygonIndex];
                Vector3 contactPoint = Vector3.Zero;
                if (BSPQuery.PolygonHitsSpherePrecise(
                        polygon.Plane,
                        Vertices(tree, polygon),
                        center,
                        radius,
                        ref contactPoint))
                {
                    hitPolygonId = polygon.Id;
                    hitNormal = polygon.Plane.Normal;
                    return true;
                }
            }

            return false;
        }

        float splitDistance =
            Vector3.Dot(node.SplittingPlane.Normal, center) + node.SplittingPlane.D;
        float reach = radius - PhysicsGlobals.EPSILON;

        if (splitDistance >= reach)
        {
            return SphereIntersectsPolyStaticRecurse(
                tree,
                node.PositiveChildIndex,
                center,
                radius,
                ref hitPolygonId,
                ref hitNormal);
        }

        if (splitDistance <= -reach)
        {
            return SphereIntersectsPolyStaticRecurse(
                tree,
                node.NegativeChildIndex,
                center,
                radius,
                ref hitPolygonId,
                ref hitNormal);
        }

        if (SphereIntersectsPolyStaticRecurse(
                tree,
                node.PositiveChildIndex,
                center,
                radius,
                ref hitPolygonId,
                ref hitNormal))
        {
            return true;
        }

        return SphereIntersectsPolyStaticRecurse(
            tree,
            node.NegativeChildIndex,
            center,
            radius,
            ref hitPolygonId,
            ref hitNormal);
    }

    /// <summary>
    /// Flat swept-sphere BSP shadow query.
    /// </summary>
    public static bool SphereIntersectsPolyWithTime(
        FlatPhysicsBsp tree,
        Vector3 sphereCenter,
        float sphereRadius,
        Vector3 movement,
        out ushort hitPolygonId,
        out Vector3 hitNormal,
        out float hitTime)
    {
        ArgumentNullException.ThrowIfNull(tree);
        hitPolygonId = 0;
        hitNormal = Vector3.Zero;
        hitTime = float.MaxValue;
        if (tree.RootIndex < 0)
            return false;

        SphereIntersectsPolyWithTimeRecurse(
            tree,
            tree.RootIndex,
            sphereCenter,
            sphereRadius,
            movement,
            ref hitPolygonId,
            ref hitNormal,
            ref hitTime);

        return hitTime < float.MaxValue;
    }

    private static void SphereIntersectsPolyWithTimeRecurse(
        FlatPhysicsBsp tree,
        int nodeIndex,
        Vector3 center,
        float radius,
        Vector3 movement,
        ref ushort hitPolygonId,
        ref Vector3 hitNormal,
        ref float bestTime)
    {
        if (nodeIndex < 0)
            return;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        Vector3 difference = center - node.BoundingSphere.Origin;
        float combinedRadius =
            radius + node.BoundingSphere.Radius + movement.Length() + 0.1f;
        if (difference.LengthSquared() >= combinedRadius * combinedRadius)
            return;

        if (node.Type == BSPNodeType.Leaf)
        {
            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                FlatCollisionPolygon polygon =
                    tree.PolygonTable.Polygons[polygonIndex];
                if (Vector3.Dot(movement, polygon.Plane.Normal) >= 0f)
                    continue;

                Vector3 contactPoint = Vector3.Zero;
                if (BSPQuery.PolygonHitsSpherePrecise(
                        polygon.Plane,
                        Vertices(tree, polygon),
                        center,
                        radius,
                        ref contactPoint))
                {
                    if (0f < bestTime)
                    {
                        bestTime = 0f;
                        hitPolygonId = polygon.Id;
                        hitNormal = polygon.Plane.Normal;
                    }

                    continue;
                }

                Vector3 endCenter = center + movement;
                if (BSPQuery.PolygonHitsSpherePrecise(
                        polygon.Plane,
                        Vertices(tree, polygon),
                        endCenter,
                        radius,
                        ref contactPoint) &&
                    1f < bestTime)
                {
                    bestTime = 1f;
                    hitPolygonId = polygon.Id;
                    hitNormal = polygon.Plane.Normal;
                }
            }

            return;
        }

        float splitDistance =
            Vector3.Dot(node.SplittingPlane.Normal, center) + node.SplittingPlane.D;
        float reach = radius + movement.Length();

        if (splitDistance >= reach)
        {
            SphereIntersectsPolyWithTimeRecurse(
                tree,
                node.PositiveChildIndex,
                center,
                radius,
                movement,
                ref hitPolygonId,
                ref hitNormal,
                ref bestTime);
            return;
        }

        if (splitDistance <= -reach)
        {
            SphereIntersectsPolyWithTimeRecurse(
                tree,
                node.NegativeChildIndex,
                center,
                radius,
                movement,
                ref hitPolygonId,
                ref hitNormal,
                ref bestTime);
            return;
        }

        SphereIntersectsPolyWithTimeRecurse(
            tree,
            node.PositiveChildIndex,
            center,
            radius,
            movement,
            ref hitPolygonId,
            ref hitNormal,
            ref bestTime);
        SphereIntersectsPolyWithTimeRecurse(
            tree,
            node.NegativeChildIndex,
            center,
            radius,
            movement,
            ref hitPolygonId,
            ref hitNormal,
            ref bestTime);
    }

    private static bool SphereIntersectsPolyInternal(
        FlatPhysicsBsp tree,
        int nodeIndex,
        CollisionSphere sphere,
        Vector3 movement,
        ref int hitPolygonIndex,
        ref Vector3 contactPoint)
    {
        if (nodeIndex < 0)
            return false;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        if (!NodeIntersects(node, sphere))
            return false;

        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.PolygonIndexRange.Count == 0)
                return false;

            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                if (PosHitsSphere(
                        tree,
                        polygonIndex,
                        sphere,
                        movement,
                        ref contactPoint,
                        ref hitPolygonIndex))
                {
                    return true;
                }
            }

            return false;
        }

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, sphere.Center) +
            node.SplittingPlane.D;
        float reach = sphere.Radius - PhysicsGlobals.EPSILON;

        if (distance >= reach)
        {
            return SphereIntersectsPolyInternal(
                tree,
                node.PositiveChildIndex,
                sphere,
                movement,
                ref hitPolygonIndex,
                ref contactPoint);
        }

        if (distance <= -reach)
        {
            return SphereIntersectsPolyInternal(
                tree,
                node.NegativeChildIndex,
                sphere,
                movement,
                ref hitPolygonIndex,
                ref contactPoint);
        }

        if (node.PositiveChildIndex >= 0 &&
            SphereIntersectsPolyInternal(
                tree,
                node.PositiveChildIndex,
                sphere,
                movement,
                ref hitPolygonIndex,
                ref contactPoint))
        {
            return true;
        }

        if (node.NegativeChildIndex >= 0 &&
            SphereIntersectsPolyInternal(
                tree,
                node.NegativeChildIndex,
                sphere,
                movement,
                ref hitPolygonIndex,
                ref contactPoint))
        {
            return true;
        }

        return false;
    }

    private static void FindWalkableInternal(
        FlatPhysicsBsp tree,
        int nodeIndex,
        SpherePath path,
        ref CollisionSphere validPosition,
        Vector3 movement,
        Vector3 up,
        ref int hitPolygonIndex,
        ref ushort hitPolygonId,
        ref bool changed)
    {
        if (nodeIndex < 0)
            return;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        if (!NodeIntersects(node, validPosition))
            return;

        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.PolygonIndexRange.Count == 0)
                return;

            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                bool walkable = WalkableHitsSphere(
                    tree,
                    polygonIndex,
                    path,
                    validPosition,
                    up);
                bool adjusted = walkable &&
                    AdjustSphereToPlane(
                        tree,
                        polygonIndex,
                        path,
                        ref validPosition,
                        movement);

                if (walkable && adjusted)
                {
                    changed = true;
                    hitPolygonIndex = polygonIndex;
                    hitPolygonId = tree.PolygonTable.Polygons[polygonIndex].Id;
                }
            }

            return;
        }

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, validPosition.Center) +
            node.SplittingPlane.D;
        float reach = validPosition.Radius - PhysicsGlobals.EPSILON;

        if (distance >= reach)
        {
            FindWalkableInternal(
                tree,
                node.PositiveChildIndex,
                path,
                ref validPosition,
                movement,
                up,
                ref hitPolygonIndex,
                ref hitPolygonId,
                ref changed);
            return;
        }

        if (distance <= -reach)
        {
            FindWalkableInternal(
                tree,
                node.NegativeChildIndex,
                path,
                ref validPosition,
                movement,
                up,
                ref hitPolygonIndex,
                ref hitPolygonId,
                ref changed);
            return;
        }

        FindWalkableInternal(
            tree,
            node.PositiveChildIndex,
            path,
            ref validPosition,
            movement,
            up,
            ref hitPolygonIndex,
            ref hitPolygonId,
            ref changed);
        FindWalkableInternal(
            tree,
            node.NegativeChildIndex,
            path,
            ref validPosition,
            movement,
            up,
            ref hitPolygonIndex,
            ref hitPolygonId,
            ref changed);
    }

    private static bool HitsWalkableInternal(
        FlatPhysicsBsp tree,
        int nodeIndex,
        SpherePath path,
        CollisionSphere sphere,
        Vector3 up)
    {
        if (nodeIndex < 0)
            return false;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        if (!NodeIntersects(node, sphere))
            return false;

        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.PolygonIndexRange.Count == 0)
                return false;

            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                if (WalkableHitsSphere(tree, polygonIndex, path, sphere, up) &&
                    CheckWalkable(tree, polygonIndex, sphere, up, small: true))
                {
                    return true;
                }
            }

            return false;
        }

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, sphere.Center) +
            node.SplittingPlane.D;
        float reach = sphere.Radius - PhysicsGlobals.EPSILON;

        if (distance >= reach)
        {
            return HitsWalkableInternal(
                tree,
                node.PositiveChildIndex,
                path,
                sphere,
                up);
        }

        if (distance <= -reach)
        {
            return HitsWalkableInternal(
                tree,
                node.NegativeChildIndex,
                path,
                sphere,
                up);
        }

        if (HitsWalkableInternal(
                tree,
                node.PositiveChildIndex,
                path,
                sphere,
                up))
        {
            return true;
        }

        return HitsWalkableInternal(
            tree,
            node.NegativeChildIndex,
            path,
            sphere,
            up);
    }

    private static bool SphereIntersectsSolidInternal(
        FlatPhysicsBsp tree,
        int nodeIndex,
        CollisionSphere sphere,
        bool centerCheck)
    {
        if (nodeIndex < 0)
            return false;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.PolygonIndexRange.Count == 0)
                return false;

            if (centerCheck && node.Solid != 0)
            {
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                    PhysicsDiagnostics.LastPlacementFailSolidLeaf = true;
                return true;
            }

            if (!NodeIntersects(node, sphere))
                return false;

            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                if (!HitsSphere(tree, polygonIndex, sphere))
                    continue;

                RecordDiagnosticHit(tree, polygonIndex);
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                {
                    FlatCollisionPolygon polygon =
                        tree.PolygonTable.Polygons[polygonIndex];
                    PhysicsDiagnostics.LastPlacementFailPolyId = polygon.Id;
                    PhysicsDiagnostics.LastPlacementFailPolyNormal =
                        polygon.Plane.Normal;
                    PhysicsDiagnostics.LastPlacementFailPolyD = polygon.Plane.D;
                }

                return true;
            }

            return false;
        }

        if (!NodeIntersects(node, sphere))
            return false;

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, sphere.Center) +
            node.SplittingPlane.D;
        float reach = sphere.Radius - PhysicsGlobals.EPSILON;

        if (distance >= reach)
        {
            return SphereIntersectsSolidInternal(
                tree,
                node.PositiveChildIndex,
                sphere,
                centerCheck);
        }

        if (distance <= -reach)
        {
            return SphereIntersectsSolidInternal(
                tree,
                node.NegativeChildIndex,
                sphere,
                centerCheck);
        }

        if (distance < 0f)
        {
            if (SphereIntersectsSolidInternal(
                    tree,
                    node.PositiveChildIndex,
                    sphere,
                    centerCheck: false))
            {
                return true;
            }

            return SphereIntersectsSolidInternal(
                tree,
                node.NegativeChildIndex,
                sphere,
                centerCheck);
        }

        if (SphereIntersectsSolidInternal(
                tree,
                node.PositiveChildIndex,
                sphere,
                centerCheck))
        {
            return true;
        }

        return SphereIntersectsSolidInternal(
            tree,
            node.NegativeChildIndex,
            sphere,
            centerCheck: false);
    }

    private static bool SphereIntersectsSolidPolygonInternal(
        FlatPhysicsBsp tree,
        int nodeIndex,
        CollisionSphere sphere,
        float radius,
        ref bool centerSolid,
        ref int hitPolygonIndex,
        bool centerCheck)
    {
        if (nodeIndex < 0)
            return centerSolid;

        FlatPhysicsBspNode node = tree.Nodes[nodeIndex];
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.PolygonIndexRange.Count == 0)
                return false;

            if (centerCheck && node.Solid != 0)
                centerSolid = true;

            if (!NodeIntersects(node, sphere))
                return centerSolid;

            int end = node.PolygonIndexRange.EndExclusive;
            for (int i = node.PolygonIndexRange.Start; i < end; i++)
            {
                int polygonIndex = tree.PolygonIndexStream[i];
                if (HitsSphere(tree, polygonIndex, sphere))
                {
                    hitPolygonIndex = polygonIndex;
                    return true;
                }
            }

            return centerSolid;
        }

        if (!NodeIntersects(node, sphere))
            return centerSolid;

        float distance =
            Vector3.Dot(node.SplittingPlane.Normal, sphere.Center) +
            node.SplittingPlane.D;
        float reach = radius - PhysicsGlobals.EPSILON;

        if (distance >= reach)
        {
            return SphereIntersectsSolidPolygonInternal(
                tree,
                node.PositiveChildIndex,
                sphere,
                radius,
                ref centerSolid,
                ref hitPolygonIndex,
                centerCheck);
        }

        if (distance <= -reach)
        {
            return SphereIntersectsSolidPolygonInternal(
                tree,
                node.NegativeChildIndex,
                sphere,
                radius,
                ref centerSolid,
                ref hitPolygonIndex,
                centerCheck);
        }

        if (distance <= 0f)
        {
            SphereIntersectsSolidPolygonInternal(
                tree,
                node.NegativeChildIndex,
                sphere,
                radius,
                ref centerSolid,
                ref hitPolygonIndex,
                centerCheck);
            if (hitPolygonIndex >= 0)
                return centerSolid;

            return SphereIntersectsSolidPolygonInternal(
                tree,
                node.PositiveChildIndex,
                sphere,
                radius,
                ref centerSolid,
                ref hitPolygonIndex,
                centerCheck: false);
        }

        SphereIntersectsSolidPolygonInternal(
            tree,
            node.PositiveChildIndex,
            sphere,
            radius,
            ref centerSolid,
            ref hitPolygonIndex,
            centerCheck);
        if (hitPolygonIndex >= 0)
            return centerSolid;

        return SphereIntersectsSolidPolygonInternal(
            tree,
            node.NegativeChildIndex,
            sphere,
            radius,
            ref centerSolid,
            ref hitPolygonIndex,
            centerCheck: false);
    }

    private static bool AdjustToPlane(
        FlatPhysicsBsp tree,
        ref CollisionSphere checkPosition,
        Vector3 currentPosition,
        int hitPolygonIndex,
        Vector3 contactPoint)
    {
        Vector3 movement = checkPosition.Center - currentPosition;
        double clearTime = 0.0;
        double hitTime = 1.0;
        int iteration = 0;
        const int MaxIterations = 15;

        while (true)
        {
            float touchTime = AdjustSphereToPolygon(
                tree,
                hitPolygonIndex,
                checkPosition,
                currentPosition,
                movement);
            if (touchTime == 1f)
                break;

            checkPosition.Center = currentPosition + movement * touchTime;

            int nextHitPolygonIndex = -1;
            Vector3 nextContactPoint = Vector3.Zero;
            if (!SphereIntersectsPolyInternal(
                    tree,
                    tree.RootIndex,
                    checkPosition,
                    movement,
                    ref nextHitPolygonIndex,
                    ref nextContactPoint))
            {
                clearTime = touchTime;
                break;
            }

            if (nextHitPolygonIndex >= 0)
                hitPolygonIndex = nextHitPolygonIndex;

            iteration++;
            hitTime = touchTime;
            if (iteration >= MaxIterations)
                return false;
        }

        while (iteration < MaxIterations)
        {
            double average = (clearTime + hitTime) * 0.5;
            checkPosition.Center =
                currentPosition + movement * (float)average;

            int nextHitPolygonIndex = -1;
            Vector3 nextContactPoint = Vector3.Zero;
            if (!SphereIntersectsPolyInternal(
                    tree,
                    tree.RootIndex,
                    checkPosition,
                    movement,
                    ref nextHitPolygonIndex,
                    ref nextContactPoint))
            {
                clearTime = average;
            }
            else
            {
                hitTime = average;
            }

            if (hitTime - clearTime < 0.02)
                break;
            iteration++;
        }

        checkPosition.Center = currentPosition + movement * (float)clearTime;
        return true;
    }

    private static TransitionState CheckWalkableDispatch(
        FlatPhysicsBsp tree,
        SpherePath path,
        CollisionSphere checkPosition,
        Vector3 up)
    {
        CollisionSphere validPosition = checkPosition;
        return HitsWalkableInternal(
            tree,
            tree.RootIndex,
            path,
            validPosition,
            up)
            ? TransitionState.Collided
            : TransitionState.OK;
    }

    private static TransitionState StepSphereDown(
        FlatPhysicsBsp tree,
        Transition transition,
        CollisionSphere checkPosition,
        Vector3 up,
        float scale,
        Quaternion localToWorld = default,
        Vector3 worldOrigin = default)
    {
        if (localToWorld == default)
            localToWorld = Quaternion.Identity;

        SpherePath path = transition.SpherePath;
        CollisionInfo collisions = transition.CollisionInfo;
        float stepDownAmount = -(path.StepDownAmt * path.WalkInterp);
        Vector3 movement = up * stepDownAmount * (1f / scale);

        CollisionSphere validPosition = checkPosition;
        bool changed = false;
        int hitPolygonIndex = -1;
        ushort hitPolygonId = 0;

        FindWalkableInternal(
            tree,
            tree.RootIndex,
            path,
            ref validPosition,
            movement,
            up,
            ref hitPolygonIndex,
            ref hitPolygonId,
            ref changed);

        if (changed && hitPolygonIndex >= 0)
        {
            FlatCollisionPolygon hitPolygon =
                tree.PolygonTable.Polygons[hitPolygonIndex];
            ReadOnlySpan<Vector3> vertices = Vertices(tree, hitPolygon);
            Vector3 adjusted = validPosition.Center - checkPosition.Center;
            Vector3 offset =
                Vector3.Transform(adjusted, localToWorld) * scale;
            path.AddOffsetToCheckPos(offset);

            Vector3 worldNormal =
                TransformNormal(hitPolygon.Plane.Normal, localToWorld);
            Plane worldPlane = BuildWorldPlane(
                worldNormal,
                vertices,
                localToWorld,
                scale,
                worldOrigin);
            collisions.SetContactPlane(worldPlane, path.CheckCellId, false);
            path.SetWalkableTransformed(
                worldPlane,
                vertices,
                localToWorld,
                scale,
                worldOrigin,
                Vector3.UnitZ);

            RecordDiagnosticHit(tree, hitPolygonIndex);
            return TransitionState.Adjusted;
        }

        return TransitionState.OK;
    }

    public static bool FindWalkableSphere(
        FlatPhysicsBsp tree,
        Transition transition,
        Vector3 sphereCenter,
        float sphereRadius,
        float probeDistance,
        Vector3 up,
        out int hitPolygonIndex,
        out ushort hitPolygonId,
        out Vector3 adjustedCenter)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(transition);

        CollisionSphere validPosition =
            new(sphereCenter, sphereRadius);
        adjustedCenter = validPosition.Center;
        hitPolygonIndex = -1;
        hitPolygonId = 0;

        if (tree.RootIndex < 0)
            return false;

        Vector3 movement = -up * probeDistance;
        bool changed = false;
        int polygonIndex = -1;
        ushort polygonId = 0;

        FindWalkableInternal(
            tree,
            tree.RootIndex,
            transition.SpherePath,
            ref validPosition,
            movement,
            up,
            ref polygonIndex,
            ref polygonId,
            ref changed);

        if (changed && polygonIndex >= 0)
        {
            hitPolygonIndex = polygonIndex;
            hitPolygonId = polygonId;
            adjustedCenter = validPosition.Center;
            return true;
        }

        return false;
    }

    private static TransitionState StepSphereUp(
        Transition transition,
        Vector3 collisionNormal,
        PhysicsEngine engine)
    {
        bool stepped = transition.DoStepUp(collisionNormal, engine);
        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            SpherePath path = transition.SpherePath;
            Console.WriteLine(FormattableString.Invariant(
                $"[stepsphereup] cell=0x{path.CheckCellId:X8} stepUpFlag={path.StepUp} stepDownFlag={path.StepDown} n=({collisionNormal.X:F2},{collisionNormal.Y:F2},{collisionNormal.Z:F2}) stepped={stepped} pos=({path.CheckPos.X:F3},{path.CheckPos.Y:F3},{path.CheckPos.Z:F3})"));
        }

        if (stepped)
            return TransitionState.OK;

        TransitionState slideResult =
            transition.SpherePath.StepUpSlide(transition);
        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[stepsphereup] cell=0x{transition.SpherePath.CheckCellId:X8} → StepUpSlide={slideResult}"));
        }

        return slideResult;
    }

    private static TransitionState SlideSphere(
        Transition transition,
        Vector3 worldNormal)
        => transition.SlideSphereInternal(
            worldNormal,
            transition.SpherePath.GlobalCurrCenter[0].Origin);

    private static TransitionState CollideWithPoint(
        FlatPhysicsBsp tree,
        Transition transition,
        CollisionSphere checkPosition,
        Vector3 currentPosition,
        int hitPolygonIndex,
        Vector3 contactPoint,
        float scale,
        Quaternion localToWorld = default)
    {
        if (localToWorld == default)
            localToWorld = Quaternion.Identity;

        FlatCollisionPolygon hitPolygon =
            tree.PolygonTable.Polygons[hitPolygonIndex];
        ObjectInfo objectInfo = transition.ObjectInfo;
        SpherePath path = transition.SpherePath;
        CollisionInfo collisions = transition.CollisionInfo;
        Vector3 collisionNormal =
            Vector3.Transform(hitPolygon.Plane.Normal, localToWorld);

        if ((objectInfo.State & ObjectInfoState.PerfectClip) == 0)
        {
            collisions.SetCollisionNormal(collisionNormal);
            RecordDiagnosticHit(tree, hitPolygonIndex);
            return TransitionState.Collided;
        }

        CollisionSphere validPosition = checkPosition;
        if (!AdjustToPlane(
                tree,
                ref validPosition,
                currentPosition,
                hitPolygonIndex,
                contactPoint))
        {
            RecordDiagnosticHit(tree, hitPolygonIndex);
            return TransitionState.Collided;
        }

        collisions.SetCollisionNormal(collisionNormal);
        RecordDiagnosticHit(tree, hitPolygonIndex);
        Vector3 adjusted = validPosition.Center - checkPosition.Center;
        Vector3 offset =
            Vector3.Transform(adjusted, localToWorld) * scale;
        path.AddOffsetToCheckPos(offset);
        return TransitionState.Adjusted;
    }

    private static TransitionState NegativePolygonHitDispatch(
        FlatPhysicsBsp tree,
        SpherePath path,
        int hitPolygonIndex,
        bool stepUp,
        Quaternion localToWorld = default)
    {
        if (localToWorld == default)
            localToWorld = Quaternion.Identity;

        FlatCollisionPolygon hitPolygon =
            tree.PolygonTable.Polygons[hitPolygonIndex];
        path.NegPolyHit = true;
        path.NegStepUp = stepUp;
        path.NegCollisionNormal =
            Vector3.Transform(hitPolygon.Plane.Normal, localToWorld);

        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            Vector3 normal = hitPolygon.Plane.Normal;
            Console.WriteLine(FormattableString.Invariant(
                $"[neg-poly] cell=0x{path.CheckCellId:X8} stepUp={stepUp} stepDownFlag={path.StepDown} poly=0x{hitPolygon.Id:X4} nLocal=({normal.X:F3},{normal.Y:F3},{normal.Z:F3}) sides={hitPolygon.SidesType} checkPos=({path.CheckPos.X:F3},{path.CheckPos.Y:F3},{path.CheckPos.Z:F3})"));
        }

        return TransitionState.OK;
    }

    private static TransitionState PlacementInsert(
        FlatPhysicsBsp tree,
        Transition transition,
        bool clearCell)
    {
        SpherePath path = transition.SpherePath;
        CollisionSphere sphere0 = new(
            path.LocalSphere[0].Origin,
            path.LocalSphere[0].Radius);
        float radius = sphere0.Radius;

        bool hasSphere1 = path.NumSphere > 1;
        CollisionSphere sphere1 = default;
        if (hasSphere1)
        {
            sphere1 = new CollisionSphere(
                path.LocalSphere[1].Origin,
                path.LocalSphere[1].Radius);
        }

        const int MaxIterations = 20;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            bool centerSolid = false;
            int hitPolygonIndex = -1;
            if (SphereIntersectsSolidPolygonInternal(
                    tree,
                    tree.RootIndex,
                    sphere0,
                    radius,
                    ref centerSolid,
                    ref hitPolygonIndex,
                    clearCell))
            {
                if (hitPolygonIndex >= 0)
                {
                    AdjustToPlacementPolygon(
                        tree,
                        hitPolygonIndex,
                        ref sphere0,
                        ref sphere1,
                        hasSphere1,
                        radius,
                        centerSolid,
                        clearCell);
                    continue;
                }
            }
            else
            {
                if (hasSphere1)
                {
                    centerSolid = false;
                    hitPolygonIndex = -1;
                    if (SphereIntersectsSolidPolygonInternal(
                            tree,
                            tree.RootIndex,
                            sphere1,
                            radius,
                            ref centerSolid,
                            ref hitPolygonIndex,
                            clearCell))
                    {
                        if (hitPolygonIndex >= 0)
                        {
                            AdjustToPlacementPolygon(
                                tree,
                                hitPolygonIndex,
                                ref sphere1,
                                ref sphere0,
                                hasValidPosition2: true,
                                radius,
                                centerSolid,
                                clearCell);
                            continue;
                        }
                    }
                    else
                    {
                        return PlacementInsertInner(sphere0, path, iteration);
                    }
                }
                else
                {
                    return PlacementInsertInner(sphere0, path, iteration);
                }
            }

            radius *= 2f;
        }

        return TransitionState.Collided;
    }

    private static TransitionState PlacementInsertInner(
        CollisionSphere sphere0,
        SpherePath path,
        int iteration)
    {
        if (iteration == 0)
            return TransitionState.OK;

        Vector3 adjustment =
            sphere0.Center - path.LocalSphere[0].Origin;
        path.AddOffsetToCheckPos(adjustment);
        return TransitionState.Adjusted;
    }

    public static TransitionState FindCollisions(
        FlatPhysicsBsp tree,
        Transition transition,
        DatReaderWriter.Types.Sphere localSphere,
        DatReaderWriter.Types.Sphere? localSphere1,
        Vector3 localCurrentCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld = default,
        PhysicsEngine? engine = null,
        Vector3 worldOrigin = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(transition);

        CollisionSphere sphere0 =
            new(localSphere.Origin, localSphere.Radius);
        bool hasSphere1 = localSphere1 is not null;
        CollisionSphere sphere1 = hasSphere1
            ? new CollisionSphere(localSphere1!.Origin, localSphere1.Radius)
            : default;

        return FindCollisionsCore(
            tree,
            transition,
            sphere0,
            hasSphere1,
            sphere1,
            localCurrentCenter,
            localSpaceZ,
            scale,
            localToWorld,
            engine,
            worldOrigin);
    }

    internal static TransitionState FindCollisions(
        FlatPhysicsBsp tree,
        Transition transition,
        Vector3 localSphereCenter,
        float localSphereRadius,
        bool hasLocalSphere1,
        Vector3 localSphere1Center,
        float localSphere1Radius,
        Vector3 localCurrentCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld = default,
        PhysicsEngine? engine = null,
        Vector3 worldOrigin = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(transition);

        return FindCollisionsCore(
            tree,
            transition,
            new CollisionSphere(localSphereCenter, localSphereRadius),
            hasLocalSphere1,
            hasLocalSphere1
                ? new CollisionSphere(
                    localSphere1Center,
                    localSphere1Radius)
                : default,
            localCurrentCenter,
            localSpaceZ,
            scale,
            localToWorld,
            engine,
            worldOrigin);
    }

    private static TransitionState FindCollisionsCore(
        FlatPhysicsBsp tree,
        Transition transition,
        CollisionSphere sphere0,
        bool hasSphere1,
        CollisionSphere sphere1,
        Vector3 localCurrentCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld,
        PhysicsEngine? engine,
        Vector3 worldOrigin)
    {
        if (tree.RootIndex < 0)
            return TransitionState.OK;
        if (localToWorld == default)
            localToWorld = Quaternion.Identity;

        SpherePath path = transition.SpherePath;
        CollisionInfo collisions = transition.CollisionInfo;
        ObjectInfo objectInfo = transition.ObjectInfo;
        Vector3 movement = sphere0.Center - localCurrentCenter;

        if (PhysicsDiagnostics.ProbePushBackEnabled)
        {
            PhysicsDiagnostics.LogPushBackDispatch(
                sphereCenter: sphere0.Center,
                movement,
                collide: path.Collide,
                insertType: (int)path.InsertType,
                objState: unchecked((int)objectInfo.State),
                walkInterpEntry: path.WalkInterp,
                returnState: -1);
        }

        Vector3 LocalToWorld(Vector3 value)
            => Vector3.Transform(value, localToWorld);

        // Path 1: Placement or obstruction-ethereal.
        if (path.InsertType == InsertType.Placement ||
            path.ObstructionEthereal)
        {
            bool clearCell =
                !(path.BldgCheck && path.HitsInteriorCell);

            if (PhysicsDiagnostics.ProbePlacementFailEnabled)
            {
                PhysicsDiagnostics.LastPlacementFailPolyId = 0;
                PhysicsDiagnostics.LastPlacementFailSolidLeaf = false;
            }

            if (SphereIntersectsSolidInternal(
                    tree,
                    tree.RootIndex,
                    sphere0,
                    clearCell))
            {
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                {
                    PhysicsDiagnostics.LogPlacementFail(
                        "Path1.sphere0",
                        sphere0.Center,
                        sphere0.Radius,
                        0,
                        path.CheckCellId,
                        worldOrigin,
                        objectInfo.Ethereal);
                }

                return TransitionState.Collided;
            }

            if (PhysicsDiagnostics.ProbePlacementFailEnabled)
            {
                PhysicsDiagnostics.LastPlacementFailPolyId = 0;
                PhysicsDiagnostics.LastPlacementFailSolidLeaf = false;
            }

            if (hasSphere1 &&
                SphereIntersectsSolidInternal(
                    tree,
                    tree.RootIndex,
                    sphere1,
                    clearCell))
            {
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                {
                    PhysicsDiagnostics.LogPlacementFail(
                        "Path1.sphere1",
                        sphere1.Center,
                        sphere1.Radius,
                        1,
                        path.CheckCellId,
                        worldOrigin,
                        objectInfo.Ethereal);
                }

                return TransitionState.Collided;
            }

            return TransitionState.OK;
        }

        if (path.CheckWalkable)
        {
            return CheckWalkableDispatch(
                tree,
                path,
                sphere0,
                localSpaceZ);
        }

        // Path 3: StepDown.
        if (path.StepDown)
        {
            return StepSphereDown(
                tree,
                transition,
                sphere0,
                localSpaceZ,
                scale,
                localToWorld,
                worldOrigin);
        }

        if (path.Collide)
        {
            CollisionSphere validPosition = sphere0;
            int hitPolygonIndex = -1;
            ushort hitPolygonId = 0;
            bool changed = false;

            FindWalkableInternal(
                tree,
                tree.RootIndex,
                path,
                ref validPosition,
                movement,
                localSpaceZ,
                ref hitPolygonIndex,
                ref hitPolygonId,
                ref changed);

            if (changed && hitPolygonIndex >= 0)
            {
                FlatCollisionPolygon hitPolygon =
                    tree.PolygonTable.Polygons[hitPolygonIndex];
                ReadOnlySpan<Vector3> vertices =
                    Vertices(tree, hitPolygon);
                Vector3 localOffset =
                    validPosition.Center - sphere0.Center;
                Vector3 worldOffset =
                    LocalToWorld(localOffset) * scale;
                path.AddOffsetToCheckPos(worldOffset);

                Vector3 worldNormal =
                    TransformNormal(hitPolygon.Plane.Normal, localToWorld);
                Plane worldPlane = BuildWorldPlane(
                    worldNormal,
                    vertices,
                    localToWorld,
                    scale,
                    worldOrigin);
                collisions.SetContactPlane(
                    worldPlane,
                    path.CheckCellId,
                    false);
                path.SetWalkableTransformed(
                    worldPlane,
                    vertices,
                    localToWorld,
                    scale,
                    worldOrigin,
                    Vector3.UnitZ);
                RecordDiagnosticHit(tree, hitPolygonIndex);
                return TransitionState.Adjusted;
            }

            return TransitionState.OK;
        }

        if ((objectInfo.State & ObjectInfoState.Contact) != 0)
        {
            int hitPolygonIndex0 = -1;
            Vector3 contact0 = Vector3.Zero;
            bool hit0 = SphereIntersectsPolyInternal(
                tree,
                tree.RootIndex,
                sphere0,
                movement,
                ref hitPolygonIndex0,
                ref contact0);

            if (hit0)
            {
                RecordDiagnosticHit(tree, hitPolygonIndex0);
                Vector3 worldNormal = LocalToWorld(
                    tree.PolygonTable.Polygons[hitPolygonIndex0].Plane.Normal);
                if (engine is not null && !path.StepUp && !path.StepDown)
                    return StepSphereUp(transition, worldNormal, engine);

                return SlideSphere(transition, worldNormal);
            }

            if (hasSphere1)
            {
                int hitPolygonIndex1 = -1;
                Vector3 contact1 = Vector3.Zero;
                bool hit1 = SphereIntersectsPolyInternal(
                    tree,
                    tree.RootIndex,
                    sphere1,
                    movement,
                    ref hitPolygonIndex1,
                    ref contact1);

                if (hit1)
                {
                    RecordDiagnosticHit(tree, hitPolygonIndex1);
                    Vector3 worldNormal = LocalToWorld(
                        tree.PolygonTable.Polygons[hitPolygonIndex1].Plane.Normal);
                    return SlideSphere(transition, worldNormal);
                }

                if (hitPolygonIndex1 >= 0)
                {
                    RecordDiagnosticHit(tree, hitPolygonIndex1);
                    NegativePolygonHitDispatch(
                        tree,
                        path,
                        hitPolygonIndex1,
                        stepUp: false,
                        localToWorld);
                    return TransitionState.OK;
                }

                if (hitPolygonIndex0 >= 0)
                {
                    RecordDiagnosticHit(tree, hitPolygonIndex0);
                    NegativePolygonHitDispatch(
                        tree,
                        path,
                        hitPolygonIndex0,
                        stepUp: true,
                        localToWorld);
                    return TransitionState.OK;
                }
            }

            return TransitionState.OK;
        }

        int defaultHitPolygonIndex0 = -1;
        Vector3 defaultContact0 = Vector3.Zero;
        bool defaultHit0 = SphereIntersectsPolyInternal(
            tree,
            tree.RootIndex,
            sphere0,
            movement,
            ref defaultHitPolygonIndex0,
            ref defaultContact0);

        if (defaultHit0 || defaultHitPolygonIndex0 >= 0)
        {
            if ((objectInfo.State & ObjectInfoState.PathClipped) != 0)
            {
                return CollideWithPoint(
                    tree,
                    transition,
                    sphere0,
                    localCurrentCenter,
                    defaultHitPolygonIndex0,
                    defaultContact0,
                    scale,
                    localToWorld);
            }

            Vector3 worldNormal0 = LocalToWorld(
                tree.PolygonTable.Polygons[defaultHitPolygonIndex0].Plane.Normal);
            path.SetCollide(worldNormal0);
            path.WalkableAllowance = PhysicsGlobals.LandingZ;
            RecordDiagnosticHit(tree, defaultHitPolygonIndex0);
            return TransitionState.Adjusted;
        }

        if (hasSphere1)
        {
            int defaultHitPolygonIndex1 = -1;
            Vector3 defaultContact1 = Vector3.Zero;
            bool defaultHit1 = SphereIntersectsPolyInternal(
                tree,
                tree.RootIndex,
                sphere1,
                movement,
                ref defaultHitPolygonIndex1,
                ref defaultContact1);

            if (defaultHit1 || defaultHitPolygonIndex1 >= 0)
            {
                Vector3 worldNormal1 = LocalToWorld(
                    tree.PolygonTable.Polygons[defaultHitPolygonIndex1].Plane.Normal);
                collisions.SetCollisionNormal(worldNormal1);
                RecordDiagnosticHit(tree, defaultHitPolygonIndex1);
                return TransitionState.Collided;
            }
        }

        return TransitionState.OK;
    }

    private static void RecordDiagnosticHit(
        FlatPhysicsBsp tree,
        int polygonIndex)
    {
        if (PhysicsDiagnostics.ProbeBuildingEnabled ||
            PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            PhysicsDiagnostics.LastBspHitPoly =
                MaterializeResolvedPolygon(tree, polygonIndex);
        }
    }

    private static ResolvedPolygon MaterializeResolvedPolygon(
        FlatPhysicsBsp tree,
        int polygonIndex)
    {
        FlatCollisionPolygon polygon = tree.PolygonTable.Polygons[polygonIndex];
        return new ResolvedPolygon
        {
            Id = polygon.Id,
            Plane = polygon.Plane,
            SidesType = polygon.SidesType,
            NumPoints = polygon.NumPoints,
            Vertices = Vertices(tree, polygon).ToArray(),
        };
    }
}
