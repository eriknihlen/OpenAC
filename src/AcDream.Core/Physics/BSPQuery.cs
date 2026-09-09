using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Physics;

public static class BSPQuery
{

    internal struct CollisionSphere
    {
        public Vector3 Center;
        public float Radius;

        public CollisionSphere(Vector3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }

    }


    private static bool NodeIntersects(PhysicsBSPNode node, CollisionSphere sphere)
    {
        var bs = node.BoundingSphere;
        var d = sphere.Center - bs.Origin;
        float r = sphere.Radius + bs.Radius;
        return d.LengthSquared() < r * r;
    }



    internal static bool PolygonHitsSpherePrecise(
        in Plane polyPlane,
        ReadOnlySpan<Vector3> verts,
        Vector3 sphereCenter,
        float sphereRadius,
        ref Vector3 contactPoint)
    {
        int n = verts.Length;
        if (n == 0) return true;

        float dist = Vector3.Dot(polyPlane.Normal, sphereCenter) + polyPlane.D;
        float rad = sphereRadius - PhysicsGlobals.EPSILON;

        if (MathF.Abs(dist) > rad) return false;

        float diff = rad * rad - dist * dist;
        contactPoint = sphereCenter - polyPlane.Normal * dist;

        int prevIdx = n - 1;
        for (int i = 0; i < n; i++)
        {
            var v = verts[i];
            var lv = verts[prevIdx];
            prevIdx = i;

            var edge = v - lv;
            var disp = contactPoint - lv;
            var cross = Vector3.Cross(polyPlane.Normal, edge);

            if (Vector3.Dot(disp, cross) >= 0f) continue;

            prevIdx = n - 1;
            for (int j = 0; j < n; j++)
            {
                v = verts[j];
                lv = verts[prevIdx];
                prevIdx = j;

                edge = v - lv;
                disp = contactPoint - lv;
                cross = Vector3.Cross(polyPlane.Normal, edge);
                float dispDot = Vector3.Dot(disp, cross);

                if (dispDot < 0f)
                {
                    if (cross.LengthSquared() * diff < dispDot * dispDot)
                        return false;

                    float dispEdge = Vector3.Dot(disp, edge);
                    if (dispEdge >= 0f && dispEdge <= edge.LengthSquared())
                        return true;
                }

                if (disp.LengthSquared() <= diff)
                    return true;
            }
            return false;
        }
        return true;
    }


    private static bool PosHitsSphere(
        ResolvedPolygon poly,
        CollisionSphere sphere,
        Vector3 movement,
        ref Vector3 contactPoint,
        ref ResolvedPolygon? hitPoly)
    {
        bool hit = PolygonHitsSpherePrecise(
            poly.Plane, poly.Vertices,
            sphere.Center, sphere.Radius,
            ref contactPoint);

        if (hit) hitPoly = poly;

        float moveDot = Vector3.Dot(movement, poly.Plane.Normal);
        if (moveDot >= 0f) return false;

        return hit;
    }


    private static bool HitsSphere(ResolvedPolygon poly, CollisionSphere sphere)
    {
        Vector3 cp = Vector3.Zero;
        return PolygonHitsSpherePrecise(
            poly.Plane, poly.Vertices,
            sphere.Center, sphere.Radius,
            ref cp);
    }


    private static bool WalkableHitsSphere(
        ResolvedPolygon poly,
        SpherePath path,
        CollisionSphere sphere,
        Vector3 up)
    {
        float dp = Vector3.Dot(up, poly.Plane.Normal);
        if (dp <= path.WalkableAllowance) return false;

        Vector3 cp = Vector3.Zero;
        return PolygonHitsSpherePrecise(
            poly.Plane, poly.Vertices,
            sphere.Center, sphere.Radius,
            ref cp);
    }


    private static bool CheckWalkable(
        ResolvedPolygon poly,
        CollisionSphere sphere,
        Vector3 up,
        bool small)
        => CheckWalkableSupport(
            poly.Plane,
            poly.Vertices,
            sphere.Center,
            small ? sphere.Radius * 0.5f : sphere.Radius,
            up);

    internal static bool CheckWalkableSupport(
        Plane plane,
        ReadOnlySpan<Vector3> vertices,
        Vector3 center,
        float supportRadius,
        Vector3 up)
    {
        float angleUp = Vector3.Dot(plane.Normal, up);
        if (angleUp < PhysicsGlobals.EPSILON) return false;

        float angle = (Vector3.Dot(plane.Normal, center) + plane.D) / angleUp;
        center -= up * angle;

        float radsum = supportRadius * supportRadius;

        int n = vertices.Length;
        int prevIdx = n - 1;

        for (int i = 0; i < n; i++)
        {
            var v = vertices[i];
            var lv = vertices[prevIdx];
            prevIdx = i;

            var edge = v - lv;
            var disp = center - lv;
            var cross = Vector3.Cross(plane.Normal, edge);
            float diff = Vector3.Dot(disp, cross);

            if (diff < 0f)
            {
                if (cross.LengthSquared() * radsum < diff * diff)
                    return false;

                float dispEdge = Vector3.Dot(disp, edge);
                if (dispEdge >= 0f && dispEdge <= edge.LengthSquared())
                    return true;

                return false;
            }

            if (disp.LengthSquared() <= radsum)
                return true;
        }
        return true;
    }


    private static bool AdjustSphereToPlane(
        ResolvedPolygon poly,
        SpherePath path,
        ref CollisionSphere validPos,
        Vector3 movement)
    {
        var inputCenter = validPos.Center;
        float walkInterpBefore = path.WalkInterp;

        float dpPos = Vector3.Dot(validPos.Center, poly.Plane.Normal) + poly.Plane.D;
        float dpMove = Vector3.Dot(movement, poly.Plane.Normal);
        float dist;

        if (dpMove <= PhysicsGlobals.EPSILON)
        {
            if (dpMove >= -PhysicsGlobals.EPSILON)
            {
                if (PhysicsDiagnostics.ProbePushBackEnabled)
                {
                    PhysicsDiagnostics.LogPushBackAdjust(
                        inputCenter, validPos.Center, poly.Plane, validPos.Radius,
                        walkInterpBefore, path.WalkInterp,
                        dpPos, dpMove, 0f,
                        applied: false);
                }
                return false;
            }
            dist = dpPos - validPos.Radius;
        }
        else
        {
            dist = -validPos.Radius - dpPos;
        }

        float iDist = dist / dpMove;
        float interp = (1f - iDist) * path.WalkInterp;

        if (interp >= path.WalkInterp || interp < -0.5f)
        {
            if (PhysicsDiagnostics.ProbePushBackEnabled)
            {
                PhysicsDiagnostics.LogPushBackAdjust(
                    inputCenter, validPos.Center, poly.Plane, validPos.Radius,
                    walkInterpBefore, path.WalkInterp,
                    dpPos, dpMove, iDist,
                    applied: false);
            }
            return false;
        }

        validPos.Center -= movement * iDist;
        path.WalkInterp = interp;

        if (PhysicsDiagnostics.ProbePushBackEnabled)
        {
            PhysicsDiagnostics.LogPushBackAdjust(
                inputCenter, validPos.Center, poly.Plane, validPos.Radius,
                walkInterpBefore, path.WalkInterp,
                dpPos, dpMove, iDist,
                applied: true);
        }

        if (PhysicsDiagnostics.ProbePolyDumpEnabled)
        {
            PhysicsDiagnostics.LogPolyDump(path.CheckCellId, poly);
        }

        return true;
    }


    internal static bool FindCrossedEdge(
        Plane polyPlane,
        ReadOnlySpan<Vector3> verts,
        Vector3 sphereCenter,
        Vector3 up,
        out Vector3 normal)
    {
        normal = Vector3.Zero;

        float angleUp = Vector3.Dot(polyPlane.Normal, up);
        if (MathF.Abs(angleUp) < PhysicsGlobals.EPSILON) return false;

        float angle = (Vector3.Dot(polyPlane.Normal, sphereCenter) + polyPlane.D) / angleUp;
        var center = sphereCenter - up * angle;

        int n = verts.Length;
        int prevIdx = n - 1;

        for (int i = 0; i < n; i++)
        {
            var v = verts[i];
            var lv = verts[prevIdx];
            prevIdx = i;

            var edge = v - lv;
            var disp = center - lv;
            var cross = Vector3.Cross(polyPlane.Normal, edge);

            if (Vector3.Dot(disp, cross) < 0f)
            {
                float crossLen = cross.Length();
                normal = crossLen > 0f ? cross * (1f / crossLen) : Vector3.Zero;
                return true;
            }
        }
        return false;
    }

    private static bool FindCrossedEdge(
        ResolvedPolygon poly,
        CollisionSphere sphere,
        Vector3 up,
        ref Vector3 normal)
    {
        if (!FindCrossedEdge(poly.Plane, poly.Vertices, sphere.Center, up, out var crossedNormal))
            return false;

        normal = crossedNormal;
        return true;
    }

    private static Vector3 TransformNormal(Vector3 normal, Quaternion localToWorld)
    {
        var worldNormal = Vector3.Transform(normal, localToWorld);
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


    private static void AdjustToPlacementPoly(
        ResolvedPolygon poly,
        ref CollisionSphere validPos,
        ref CollisionSphere validPos2,
        bool hasValidPos2,
        float radius,
        bool centerSolid,
        bool clearCell)
    {
        Vector3 moveDir = Vector3.Zero;

        if (centerSolid)
        {
            moveDir = poly.Plane.Normal;
        }
        else
        {
            var up = Vector3.UnitZ;
            if (!FindCrossedEdge(poly, validPos, up, ref moveDir))
                moveDir = poly.Plane.Normal;
        }

        float dist = Vector3.Dot(validPos.Center, poly.Plane.Normal) + poly.Plane.D;
        float pushAmt = radius - dist;
        if (pushAmt <= 0f) pushAmt = PhysicsGlobals.EPSILON;

        var offset = moveDir * pushAmt;
        validPos.Center += offset;

        if (hasValidPos2)
            validPos2.Center += offset;
    }


    private static float AdjustSphereToPoly(
        ResolvedPolygon poly,
        CollisionSphere checkPos,
        Vector3 curPos,
        Vector3 movement)
    {
        float dpPos = Vector3.Dot(curPos, poly.Plane.Normal) + poly.Plane.D;
        if (MathF.Abs(dpPos) < checkPos.Radius) return 1f;

        float dpMove = Vector3.Dot(movement, poly.Plane.Normal);
        if (MathF.Abs(dpMove) <= PhysicsGlobals.EPSILON) return 0f;

        float r = dpPos < 0f ? -checkPos.Radius : checkPos.Radius;
        return (r - dpPos) / dpMove;
    }



    private static bool SphereIntersectsPolyInternal(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        CollisionSphere sphere,
        Vector3 movement,
        ref ResolvedPolygon? hitPoly,
        ref Vector3 contactPoint)
    {
        if (node is null) return false;
        if (!NodeIntersects(node, sphere)) return false;

        // Leaf: test each polygon.
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.Polygons.Count == 0) return false;

            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;

                if (PosHitsSphere(poly, sphere, movement, ref contactPoint, ref hitPoly))
                    return true;
            }
            return false;
        }

        float dist = Vector3.Dot(node.SplittingPlane.Normal, sphere.Center)
                      + node.SplittingPlane.D;
        float reach = sphere.Radius - PhysicsGlobals.EPSILON;

        if (dist >= reach)
            return SphereIntersectsPolyInternal(node.PosNode, resolved, sphere, movement,
                                                 ref hitPoly, ref contactPoint);

        if (dist <= -reach)
            return SphereIntersectsPolyInternal(node.NegNode, resolved, sphere, movement,
                                                 ref hitPoly, ref contactPoint);

        if (node.PosNode is not null &&
            SphereIntersectsPolyInternal(node.PosNode, resolved, sphere, movement,
                                          ref hitPoly, ref contactPoint))
            return true;

        if (node.NegNode is not null &&
            SphereIntersectsPolyInternal(node.NegNode, resolved, sphere, movement,
                                          ref hitPoly, ref contactPoint))
            return true;

        return false;
    }


    private static void FindWalkableInternal(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        SpherePath path,
        ref CollisionSphere validPos,
        Vector3 movement,
        Vector3 up,
        ref ResolvedPolygon? hitPoly,
        ref ushort hitPolyId,
        ref bool changed)
    {
        if (node is null) return;
        if (!NodeIntersects(node, validPos)) return;

        // Leaf.
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.Polygons.Count == 0) return;

            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;

                bool walkable = WalkableHitsSphere(poly, path, validPos, up);
                bool adjusted = walkable
                    && AdjustSphereToPlane(poly, path, ref validPos, movement);

                if (walkable && adjusted)
                {
                    changed = true;
                    hitPoly = poly;
                    hitPolyId = polyId;
                }
            }
            return;
        }

        float dist = Vector3.Dot(node.SplittingPlane.Normal, validPos.Center)
                      + node.SplittingPlane.D;
        float reach = validPos.Radius - PhysicsGlobals.EPSILON;

        if (dist >= reach)
        {
            FindWalkableInternal(node.PosNode, resolved, path, ref validPos, movement, up,
                                  ref hitPoly, ref hitPolyId, ref changed);
            return;
        }

        if (dist <= -reach)
        {
            FindWalkableInternal(node.NegNode, resolved, path, ref validPos, movement, up,
                                  ref hitPoly, ref hitPolyId, ref changed);
            return;
        }

        // Straddles.
        FindWalkableInternal(node.PosNode, resolved, path, ref validPos, movement, up,
                              ref hitPoly, ref hitPolyId, ref changed);
        FindWalkableInternal(node.NegNode, resolved, path, ref validPos, movement, up,
                              ref hitPoly, ref hitPolyId, ref changed);
    }


    private static bool HitsWalkableInternal(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        SpherePath path,
        CollisionSphere sphere,
        Vector3 up)
    {
        if (node is null) return false;
        if (!NodeIntersects(node, sphere)) return false;

        // Leaf.
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.Polygons.Count == 0) return false;

            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;

                if (WalkableHitsSphere(poly, path, sphere, up) &&
                    CheckWalkable(poly, sphere, up, small: true))
                    return true;
            }
            return false;
        }

        // Internal.
        float dist = Vector3.Dot(node.SplittingPlane.Normal, sphere.Center)
                      + node.SplittingPlane.D;
        float reach = sphere.Radius - PhysicsGlobals.EPSILON;

        if (dist >= reach)
            return HitsWalkableInternal(node.PosNode, resolved, path, sphere, up);

        if (dist <= -reach)
            return HitsWalkableInternal(node.NegNode, resolved, path, sphere, up);

        if (HitsWalkableInternal(node.PosNode, resolved, path, sphere, up)) return true;
        return HitsWalkableInternal(node.NegNode, resolved, path, sphere, up);
    }


    private static bool SphereIntersectsSolidInternal(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        CollisionSphere sphere,
        bool centerCheck)
    {
        if (node is null) return false;

        // Leaf.
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.Polygons.Count == 0) return false;
            if (centerCheck && node.Solid != 0)
            {
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                    PhysicsDiagnostics.LastPlacementFailSolidLeaf = true;
                return true;
            }
            if (!NodeIntersects(node, sphere)) return false;

            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;
                if (HitsSphere(poly, sphere))
                {
                    if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                        PhysicsDiagnostics.LastBspHitPoly = poly;

                    if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                    {
                        PhysicsDiagnostics.LastPlacementFailPolyId = poly.Id;
                        PhysicsDiagnostics.LastPlacementFailPolyNormal = poly.Plane.Normal;
                        PhysicsDiagnostics.LastPlacementFailPolyD = poly.Plane.D;
                    }
                    return true;
                }
            }
            return false;
        }

        if (!NodeIntersects(node, sphere)) return false;

        // Internal.
        float dist = Vector3.Dot(node.SplittingPlane.Normal, sphere.Center)
                      + node.SplittingPlane.D;
        float reach = sphere.Radius - PhysicsGlobals.EPSILON;

        if (dist >= reach)
            return SphereIntersectsSolidInternal(node.PosNode, resolved, sphere, centerCheck);

        if (dist <= -reach)
            return SphereIntersectsSolidInternal(node.NegNode, resolved, sphere, centerCheck);

        if (dist < 0f)
        {
            if (SphereIntersectsSolidInternal(node.PosNode, resolved, sphere, false))
                return true;
            return SphereIntersectsSolidInternal(node.NegNode, resolved, sphere, centerCheck);
        }
        else
        {
            if (SphereIntersectsSolidInternal(node.PosNode, resolved, sphere, centerCheck))
                return true;
            return SphereIntersectsSolidInternal(node.NegNode, resolved, sphere, false);
        }
    }


    private static bool SphereIntersectsSolidPolyInternal(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        CollisionSphere sphere,
        float radius,
        ref bool centerSolid,
        ref ResolvedPolygon? hitPoly,
        bool centerCheck)
    {
        if (node is null) return centerSolid;

        // Leaf.
        if (node.Type == BSPNodeType.Leaf)
        {
            if (node.Polygons.Count == 0) return false;

            if (centerCheck && node.Solid != 0)
                centerSolid = true;

            if (!NodeIntersects(node, sphere))
                return centerSolid;

            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;
                if (HitsSphere(poly, sphere))
                {
                    hitPoly = poly;
                    return true;
                }
            }
            return centerSolid;
        }

        if (!NodeIntersects(node, sphere)) return centerSolid;

        // Internal.
        float dist = Vector3.Dot(node.SplittingPlane.Normal, sphere.Center)
                      + node.SplittingPlane.D;
        float reach = radius - PhysicsGlobals.EPSILON;

        if (dist >= reach)
            return SphereIntersectsSolidPolyInternal(node.PosNode, resolved, sphere, radius,
                                                      ref centerSolid, ref hitPoly, centerCheck);

        if (dist <= -reach)
            return SphereIntersectsSolidPolyInternal(node.NegNode, resolved, sphere, radius,
                                                      ref centerSolid, ref hitPoly, centerCheck);

        // Straddles.
        if (dist <= 0f)
        {
            SphereIntersectsSolidPolyInternal(node.NegNode, resolved, sphere, radius,
                                               ref centerSolid, ref hitPoly, centerCheck);
            if (hitPoly is not null) return centerSolid;
            return SphereIntersectsSolidPolyInternal(node.PosNode, resolved, sphere, radius,
                                                      ref centerSolid, ref hitPoly, false);
        }
        else
        {
            SphereIntersectsSolidPolyInternal(node.PosNode, resolved, sphere, radius,
                                               ref centerSolid, ref hitPoly, centerCheck);
            if (hitPoly is not null) return centerSolid;
            return SphereIntersectsSolidPolyInternal(node.NegNode, resolved, sphere, radius,
                                                      ref centerSolid, ref hitPoly, false);
        }
    }


    public static bool PointInsideCellBsp(CellBSPNode? node, Vector3 point)
    {
        if (node is null) return true;
        if (node.Type == BSPNodeType.Leaf) return true;

        float dist = Vector3.Dot(node.SplittingPlane.Normal, point) + node.SplittingPlane.D;

        if (dist >= 0f)
            return node.PosNode is not null ? PointInsideCellBsp(node.PosNode, point) : true;

        // Behind → outside.
        return false;
    }

    public static bool SphereIntersectsCellBsp(CellBSPNode? node, Vector3 center, float radius)
    {
        if (node is null) return true;
        if (node.Type == BSPNodeType.Leaf) return true;

        float dist = Vector3.Dot(node.SplittingPlane.Normal, center) + node.SplittingPlane.D;
        float rad = radius + 0.01f;

        // Behind splitting plane by more than radius → sphere fully outside.
        if (dist < -rad) return false;

        return node.PosNode is not null
            ? SphereIntersectsCellBsp(node.PosNode, center, radius)
            : true;
    }


    internal const float BoxPlaneEpsilon = 0.000199999995f;

    internal enum PlaneSide
    {
        Positive = 0,
        Negative = 1,
        Straddle = 2,
    }

    internal static PlaneSide WhichSide(in Plane plane, Vector3 point, float eps)
    {
        float dist = Vector3.Dot(plane.Normal, point) + plane.D;
        if (dist >= eps) return PlaneSide.Positive;
        if (dist < -eps) return PlaneSide.Negative;
        return PlaneSide.Straddle;
    }

    internal static PlaneSide ClassifyBox(in Plane plane, Vector3 min, Vector3 max)
    {
        Span<Vector3> corners =
        [
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, max.Y, max.Z),
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, min.Z),
        ];

        PlaneSide side0 = WhichSide(plane, corners[0], BoxPlaneEpsilon);
        if (side0 == PlaneSide.Straddle) return PlaneSide.Straddle;

        for (int i = 1; i < corners.Length; i++)
        {
            if (WhichSide(plane, corners[i], BoxPlaneEpsilon) != side0)
                return PlaneSide.Straddle;
        }

        return side0;
    }

    public static bool BoxIntersectsCellBsp(CellBSPNode? node, Vector3 min, Vector3 max)
    {
        if (node is null) return true;
        if (node.Type == BSPNodeType.Leaf) return true;

        if (ClassifyBox(node.SplittingPlane, min, max) == PlaneSide.Negative)
            return false;

        return node.PosNode is not null
            ? BoxIntersectsCellBsp(node.PosNode, min, max)
            : true;
    }



    private static bool AdjustToPlane(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        ref CollisionSphere checkPos,
        Vector3 curPos,
        ResolvedPolygon hitPoly,
        Vector3 contactPoint)
    {
        var movement = checkPos.Center - curPos;

        double clearTime = 0.0;
        double hitTime = 1.0;
        int i = 0;

        const int MaxIter = 15;

        while (true)
        {
            float touchTime = AdjustSphereToPoly(hitPoly, checkPos, curPos, movement);

            if (touchTime == 1f) break;

            checkPos.Center = curPos + movement * touchTime;

            ResolvedPolygon? hp2 = null;
            Vector3 cp2 = Vector3.Zero;
            if (!SphereIntersectsPolyInternal(root, resolved, checkPos, movement,
                                               ref hp2, ref cp2))
            {
                clearTime = touchTime;
                break;
            }

            if (hp2 is not null) hitPoly = hp2;

            i++;
            hitTime = touchTime;
            if (i >= MaxIter) return false;
        }

        while (i < MaxIter)
        {
            double avg = (clearTime + hitTime) * 0.5;
            checkPos.Center = curPos + movement * (float)avg;

            ResolvedPolygon? hp2 = null;
            Vector3 cp2 = Vector3.Zero;
            if (!SphereIntersectsPolyInternal(root, resolved, checkPos, movement,
                                               ref hp2, ref cp2))
                clearTime = avg;
            else
                hitTime = avg;

            if (hitTime - clearTime < 0.02) break;
            i++;
        }

        checkPos.Center = curPos + movement * (float)clearTime;
        return true;
    }


    private static TransitionState CheckWalkableDispatch(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        SpherePath path,
        CollisionSphere checkPos,
        Vector3 up)
    {
        var validPos = checkPos;
        return HitsWalkableInternal(root, resolved, path, validPos, up)
            ? TransitionState.Collided
            : TransitionState.OK;
    }


    private static TransitionState StepSphereDown(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        CollisionSphere checkPos,
        Vector3 up,
        float scale,
        Quaternion localToWorld = default,
        Vector3 worldOrigin = default)
    {
        if (localToWorld == default) localToWorld = Quaternion.Identity;

        var path = transition.SpherePath;
        var collisions = transition.CollisionInfo;

        float stepDownAmount = -(path.StepDownAmt * path.WalkInterp);
        var movement = up * stepDownAmount * (1f / scale);

        var validPos = checkPos;
        bool changed = false;
        ResolvedPolygon? polyHit = null;
        ushort _polyId = 0;   // step-down doesn't need the id, but the signature requires it

        FindWalkableInternal(root, resolved, path, ref validPos, movement, up,
                              ref polyHit, ref _polyId, ref changed);

        if (changed && polyHit is not null)
        {
            var adjusted = validPos.Center - checkPos.Center;
            var offset = Vector3.Transform(adjusted, localToWorld) * scale;
            path.AddOffsetToCheckPos(offset);

            var worldNormal = TransformNormal(polyHit.Plane.Normal, localToWorld);
            var worldPlane = BuildWorldPlane(
                worldNormal,
                polyHit.Vertices,
                localToWorld,
                scale,
                worldOrigin);
            collisions.SetContactPlane(worldPlane, path.CheckCellId, false);

            path.SetWalkableTransformed(
                worldPlane,
                polyHit.Vertices,
                localToWorld,
                scale,
                worldOrigin,
                Vector3.UnitZ);

            if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                PhysicsDiagnostics.LastBspHitPoly = polyHit;

            return TransitionState.Adjusted;
        }

        return TransitionState.OK;
    }


    public static bool FindWalkableSphere(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        Sphere sphere,
        float probeDistance,
        Vector3 up,
        out ResolvedPolygon? hitPoly,
        out ushort hitPolyId,
        out Vector3 adjustedCenter)
        => FindWalkableSphereCore(
            root,
            resolved,
            transition,
            new CollisionSphere(sphere.Origin, sphere.Radius),
            probeDistance,
            up,
            out hitPoly,
            out hitPolyId,
            out adjustedCenter);

    internal static bool FindWalkableSphere(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        Vector3 sphereCenter,
        float sphereRadius,
        float probeDistance,
        Vector3 up,
        out ResolvedPolygon? hitPoly,
        out ushort hitPolyId,
        out Vector3 adjustedCenter)
        => FindWalkableSphereCore(
            root,
            resolved,
            transition,
            new CollisionSphere(sphereCenter, sphereRadius),
            probeDistance,
            up,
            out hitPoly,
            out hitPolyId,
            out adjustedCenter);

    private static bool FindWalkableSphereCore(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        CollisionSphere validPos,
        float probeDistance,
        Vector3 up,
        out ResolvedPolygon? hitPoly,
        out ushort hitPolyId,
        out Vector3 adjustedCenter)
    {
        adjustedCenter = validPos.Center;
        hitPoly = null;
        hitPolyId = 0;

        if (root is null) return false;

        var movement = -up * probeDistance;
        bool changed = false;
        ushort polyId = 0;
        ResolvedPolygon? poly = null;

        FindWalkableInternal(root, resolved, transition.SpherePath, ref validPos,
                              movement, up, ref poly, ref polyId, ref changed);

        if (changed && poly is not null)
        {
            hitPoly = poly;
            hitPolyId = polyId;
            adjustedCenter = validPos.Center;
            return true;
        }

        return false;
    }


    private static TransitionState StepSphereUp(
        Transition transition,
        Vector3 collisionNormal,
        PhysicsEngine engine)
    {
        bool stepped = transition.DoStepUp(collisionNormal, engine!);

        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            var p = transition.SpherePath;
            Console.WriteLine(System.FormattableString.Invariant(
                $"[stepsphereup] cell=0x{p.CheckCellId:X8} stepUpFlag={p.StepUp} stepDownFlag={p.StepDown} n=({collisionNormal.X:F2},{collisionNormal.Y:F2},{collisionNormal.Z:F2}) stepped={stepped} pos=({p.CheckPos.X:F3},{p.CheckPos.Y:F3},{p.CheckPos.Z:F3})"));
        }

        if (stepped)
            return TransitionState.OK;

        var slideRes = transition.SpherePath.StepUpSlide(transition);
        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
            Console.WriteLine(System.FormattableString.Invariant(
                $"[stepsphereup] cell=0x{transition.SpherePath.CheckCellId:X8} → StepUpSlide={slideRes}"));
        return slideRes;
    }


    private static TransitionState SlideSphere(
        Transition transition,
        Vector3 worldNormal)
        => transition.SlideSphereInternal(
            worldNormal, transition.SpherePath.GlobalCurrCenter[0].Origin);


    private static TransitionState CollideWithPt(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        CollisionSphere checkPos,
        Vector3 curPos,
        ResolvedPolygon hitPoly,
        Vector3 contactPoint,
        float scale,
        Quaternion localToWorld = default)
    {
        if (localToWorld == default) localToWorld = Quaternion.Identity;

        var obj = transition.ObjectInfo;
        var path = transition.SpherePath;
        var collisions = transition.CollisionInfo;

        var collisionNormal = Vector3.Transform(hitPoly.Plane.Normal, localToWorld);

        if ((obj.State & ObjectInfoState.PerfectClip) == 0)
        {
            collisions.SetCollisionNormal(collisionNormal);
            if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                PhysicsDiagnostics.LastBspHitPoly = hitPoly;
            return TransitionState.Collided;
        }

        var validPos = checkPos;

        if (!AdjustToPlane(root, resolved, ref validPos, curPos, hitPoly, contactPoint))
        {
            if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                PhysicsDiagnostics.LastBspHitPoly = hitPoly;
            return TransitionState.Collided;
        }

        collisions.SetCollisionNormal(collisionNormal);
        if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
            PhysicsDiagnostics.LastBspHitPoly = hitPoly;

        var adjusted = validPos.Center - checkPos.Center;
        var offset = Vector3.Transform(adjusted, localToWorld) * scale;
        path.AddOffsetToCheckPos(offset);

        return TransitionState.Adjusted;
    }


    private static TransitionState NegPolyHitDispatch(
        SpherePath path,
        ResolvedPolygon hitPoly,
        bool stepUp,
        Quaternion localToWorld = default)
    {
        if (localToWorld == default) localToWorld = Quaternion.Identity;
        path.NegPolyHit = true;
        path.NegStepUp = stepUp;
        path.NegCollisionNormal = Vector3.Transform(hitPoly.Plane.Normal, localToWorld);

        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            var nl = hitPoly.Plane.Normal;
            Console.WriteLine(System.FormattableString.Invariant(
                $"[neg-poly] cell=0x{path.CheckCellId:X8} stepUp={stepUp} stepDownFlag={path.StepDown} poly=0x{hitPoly.Id:X4} nLocal=({nl.X:F3},{nl.Y:F3},{nl.Z:F3}) sides={hitPoly.SidesType} checkPos=({path.CheckPos.X:F3},{path.CheckPos.Y:F3},{path.CheckPos.Z:F3})"));
        }
        return TransitionState.OK;
    }


    private static TransitionState PlacementInsert(
        PhysicsBSPNode root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        bool clearCell)
    {
        var path = transition.SpherePath;

        var s0 = new CollisionSphere(
            path.LocalSphere[0].Origin,
            path.LocalSphere[0].Radius);

        float rad = s0.Radius;

        bool hasS1 = path.NumSphere > 1;
        CollisionSphere s1 = default;
        if (hasS1)
            s1 = new CollisionSphere(
                path.LocalSphere[1].Origin,
                path.LocalSphere[1].Radius);

        ResolvedPolygon? hitPoly = null;

        const int MaxIter = 20;
        for (int i = 0; i < MaxIter; i++)
        {
            bool centerSolid = false;
            hitPoly = null;

            if (SphereIntersectsSolidPolyInternal(root, resolved, s0, rad,
                                                   ref centerSolid, ref hitPoly, clearCell))
            {
                if (hitPoly is not null)
                {
                    AdjustToPlacementPoly(
                        hitPoly,
                        ref s0,
                        ref s1,
                        hasS1,
                        rad,
                        centerSolid,
                        clearCell);
                    continue;
                }
            }
            else
            {
                if (hasS1)
                {
                    centerSolid = false;
                    hitPoly = null;

                    if (SphereIntersectsSolidPolyInternal(root, resolved, s1, rad,
                                                           ref centerSolid, ref hitPoly, clearCell))
                    {
                        if (hitPoly is not null)
                        {
                            AdjustToPlacementPoly(
                                hitPoly,
                                ref s1,
                                ref s0,
                                true,
                                rad,
                                centerSolid,
                                clearCell);
                            continue;
                        }
                    }
                    else
                    {
                        return PlacementInsertInner(s0, path, i);
                    }
                }
                else
                {
                    return PlacementInsertInner(s0, path, i);
                }
            }

            rad *= 2f;
        }
        return TransitionState.Collided;
    }

    private static TransitionState PlacementInsertInner(
        CollisionSphere s0,
        SpherePath path,
        int iteration)
    {
        if (iteration == 0) return TransitionState.OK;

        var adjust = s0.Center - path.LocalSphere[0].Origin;
        path.AddOffsetToCheckPos(adjust);
        return TransitionState.Adjusted;
    }


    public static TransitionState FindCollisions(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        DatReaderWriter.Types.Sphere localSphere,
        DatReaderWriter.Types.Sphere? localSphere1,
        Vector3 localCurrCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld = default,
        PhysicsEngine? engine = null,
        Vector3 worldOrigin = default)
    {
        var sphere0 = new CollisionSphere(localSphere.Origin, localSphere.Radius);
        bool hasSphere1 = localSphere1 is not null;
        CollisionSphere sphere1 = hasSphere1
            ? new CollisionSphere(localSphere1!.Origin, localSphere1.Radius)
            : default;

        return FindCollisionsCore(
            root,
            resolved,
            transition,
            sphere0,
            hasSphere1,
            sphere1,
            localCurrCenter,
            localSpaceZ,
            scale,
            localToWorld,
            engine,
            worldOrigin);
    }

    internal static TransitionState FindCollisions(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        Vector3 localSphereCenter,
        float localSphereRadius,
        bool hasLocalSphere1,
        Vector3 localSphere1Center,
        float localSphere1Radius,
        Vector3 localCurrCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld = default,
        PhysicsEngine? engine = null,
        Vector3 worldOrigin = default)
        => FindCollisionsCore(
            root,
            resolved,
            transition,
            new CollisionSphere(localSphereCenter, localSphereRadius),
            hasLocalSphere1,
            hasLocalSphere1
                ? new CollisionSphere(localSphere1Center, localSphere1Radius)
                : default,
            localCurrCenter,
            localSpaceZ,
            scale,
            localToWorld,
            engine,
            worldOrigin);

    private static TransitionState FindCollisionsCore(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Transition transition,
        CollisionSphere sphere0,
        bool hasSphere1,
        CollisionSphere sphere1,
        Vector3 localCurrCenter,
        Vector3 localSpaceZ,
        float scale,
        Quaternion localToWorld,
        PhysicsEngine? engine,
        Vector3 worldOrigin)
    {
        if (root is null) return TransitionState.OK;
        // Default quaternion (0,0,0,0) → treat as identity
        if (localToWorld == default) localToWorld = Quaternion.Identity;

        var path = transition.SpherePath;
        var collisions = transition.CollisionInfo;
        var obj = transition.ObjectInfo;

        var movement = sphere0.Center - localCurrCenter;

        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            Console.WriteLine(System.FormattableString.Invariant(
                $"[path-dispatch] insertType={path.InsertType} obstructionEthereal={path.ObstructionEthereal} checkWalkable={path.CheckWalkable} stepDown={path.StepDown} collide={path.Collide} contact={((obj.State & ObjectInfoState.Contact) != 0)} hasSphere1={hasSphere1}"));
        }

        if (PhysicsDiagnostics.ProbePushBackEnabled)
        {
            PhysicsDiagnostics.LogPushBackDispatch(
                sphereCenter: sphere0.Center,
                movement: movement,
                collide: path.Collide,
                insertType: (int)path.InsertType,
                objState: unchecked((int)obj.State),
                walkInterpEntry: path.WalkInterp,
                returnState: -1);
        }

        Vector3 L2W(Vector3 v) => Vector3.Transform(v, localToWorld);

        if (path.InsertType == InsertType.Placement || path.ObstructionEthereal)
        {
            bool clearCell = !(path.BldgCheck && path.HitsInteriorCell);

            if (PhysicsDiagnostics.ProbePlacementFailEnabled)
            {
                PhysicsDiagnostics.LastPlacementFailPolyId = 0;
                PhysicsDiagnostics.LastPlacementFailSolidLeaf = false;
            }

            if (SphereIntersectsSolidInternal(root, resolved, sphere0, clearCell))
            {
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                    PhysicsDiagnostics.LogPlacementFail(
                        "Path1.sphere0", sphere0.Center, sphere0.Radius, 0,
                        path.CheckCellId, worldOrigin, obj.Ethereal);
                return TransitionState.Collided;
            }

            if (PhysicsDiagnostics.ProbePlacementFailEnabled)
            {
                PhysicsDiagnostics.LastPlacementFailPolyId = 0;
                PhysicsDiagnostics.LastPlacementFailSolidLeaf = false;
            }

            if (hasSphere1 &&
                SphereIntersectsSolidInternal(root, resolved, sphere1, clearCell))
            {
                if (PhysicsDiagnostics.ProbePlacementFailEnabled)
                    PhysicsDiagnostics.LogPlacementFail(
                        "Path1.sphere1", sphere1.Center, sphere1.Radius, 1,
                        path.CheckCellId, worldOrigin, obj.Ethereal);
                return TransitionState.Collided;
            }

            return TransitionState.OK;
        }

        if (path.CheckWalkable)
        {
            return CheckWalkableDispatch(root, resolved, path, sphere0, localSpaceZ);
        }

        // ----------------------------------------------------------------
        // Path 3: StepDown → step_sphere_down
        // ----------------------------------------------------------------
        if (path.StepDown)
        {
            return StepSphereDown(root, resolved, transition, sphere0, localSpaceZ, scale, localToWorld, worldOrigin);
        }

        if (path.Collide)
        {
            var validPos = sphere0;
            ResolvedPolygon? hitPoly = null;
            ushort _hitPolyId = 0;   // Path 4 doesn't need the id
            bool changed = false;

            FindWalkableInternal(root, resolved, path, ref validPos, movement, localSpaceZ,
                                  ref hitPoly, ref _hitPolyId, ref changed);

            if (changed && hitPoly is not null)
            {
                var localOffset = validPos.Center - sphere0.Center;
                var worldOffset = L2W(localOffset) * scale;
                path.AddOffsetToCheckPos(worldOffset);

                var worldNormal = TransformNormal(hitPoly.Plane.Normal, localToWorld);
                var worldPlane = BuildWorldPlane(
                    worldNormal,
                    hitPoly.Vertices,
                    localToWorld,
                    scale,
                    worldOrigin);
                collisions.SetContactPlane(worldPlane, path.CheckCellId, false);
                path.SetWalkableTransformed(
                    worldPlane,
                    hitPoly.Vertices,
                    localToWorld,
                    scale,
                    worldOrigin,
                    Vector3.UnitZ);

                if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                    PhysicsDiagnostics.LastBspHitPoly = hitPoly;

                return TransitionState.Adjusted;
            }
            return TransitionState.OK;
        }

        if ((obj.State & ObjectInfoState.Contact) != 0)
        {
            ResolvedPolygon? hitPoly0 = null;
            Vector3 contact0 = Vector3.Zero;

            bool hit0 = SphereIntersectsPolyInternal(root, resolved, sphere0, movement,
                                                      ref hitPoly0, ref contact0);

            if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
            {
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[path5-diag] hit0={hit0} hitPoly0={(hitPoly0 is not null)} hasSphere1={hasSphere1}"));
            }

            if (hit0)
            {
                // Full hit — step_sphere_up.
                if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                    PhysicsDiagnostics.LastBspHitPoly = hitPoly0;

                var worldNormal = L2W(hitPoly0!.Plane.Normal);
                if (engine is not null && !path.StepUp && !path.StepDown)
                    return StepSphereUp(transition, worldNormal, engine);

                return SlideSphere(transition, worldNormal);
            }

            if (hasSphere1)
            {
                ResolvedPolygon? hitPoly1 = null;
                Vector3 contact1 = Vector3.Zero;
                bool hit1 = SphereIntersectsPolyInternal(root, resolved, sphere1, movement,
                                                         ref hitPoly1, ref contact1);

                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                {
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[path5-diag] hit1={hit1} hitPoly1={(hitPoly1 is not null)}"));
                }

                if (hit1)
                {
                    // Sphere 1 (head) full hit → slide_sphere.
                    if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                        PhysicsDiagnostics.LastBspHitPoly = hitPoly1;

                    var worldNormal = L2W(hitPoly1!.Plane.Normal);
                    return SlideSphere(transition, worldNormal);
                }

                // Sphere 1 (head) near-miss → neg_poly_hit, neg_step_up = false → outer slide.
                if (hitPoly1 is not null)
                {
                    if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                        PhysicsDiagnostics.LastBspHitPoly = hitPoly1;
                    NegPolyHitDispatch(path, hitPoly1, stepUp: false, localToWorld);
                    return TransitionState.OK;
                }

                // Sphere 0 (foot) near-miss → neg_poly_hit, neg_step_up = true → outer step_up.
                if (hitPoly0 is not null)
                {
                    if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                        PhysicsDiagnostics.LastBspHitPoly = hitPoly0;
                    NegPolyHitDispatch(path, hitPoly0, stepUp: true, localToWorld);
                    return TransitionState.OK;
                }
            }

            return TransitionState.OK;
        }

        {
            ResolvedPolygon? hitPoly0 = null;
            Vector3 contact0 = Vector3.Zero;

            bool hit0 = SphereIntersectsPolyInternal(root, resolved, sphere0, movement,
                                                      ref hitPoly0, ref contact0);

            if (hit0 || hitPoly0 is not null)
            {
                if ((obj.State & ObjectInfoState.PathClipped) != 0)
                {
                    return CollideWithPt(root, resolved, transition,
                                          sphere0, localCurrCenter,
                                          hitPoly0!, contact0, scale, localToWorld);
                }

                var worldNormal0 = L2W(hitPoly0!.Plane.Normal);
                path.SetCollide(worldNormal0);
                path.WalkableAllowance = PhysicsGlobals.LandingZ;
                if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                    PhysicsDiagnostics.LastBspHitPoly = hitPoly0;
                return TransitionState.Adjusted;
            }

            if (hasSphere1)
            {
                ResolvedPolygon? hitPoly1 = null;
                Vector3 contact1 = Vector3.Zero;

                bool hit1 = SphereIntersectsPolyInternal(root, resolved, sphere1, movement,
                                                          ref hitPoly1, ref contact1);

                if (hit1 || hitPoly1 is not null)
                {
                    var worldNormal1 = L2W(hitPoly1!.Plane.Normal);

                    collisions.SetCollisionNormal(worldNormal1);
                    if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                        PhysicsDiagnostics.LastBspHitPoly = hitPoly1;
                    return TransitionState.Collided;
                }
            }
        }

        return TransitionState.OK;
    }


    // -------------------------------------------------------------------------
    // Legacy FindCollisions — wraps the new resolved-polygon version.
    // Used by TransitionTypes.cs FindEnvCollisions.
    // -------------------------------------------------------------------------

    public static TransitionState FindCollisions(
        PhysicsBSPNode? root,
        Dictionary<ushort, DatReaderWriter.Types.Polygon> polygons,
        DatReaderWriter.Types.VertexArray vertices,
        Transition transition,
        DatReaderWriter.Types.Sphere localSphere,
        DatReaderWriter.Types.Sphere? localSphere1,
        Vector3 localCurrCenter,
        Vector3 localSpaceZ,
        float scale)
    {
        var resolved = BuildResolved(polygons, vertices);
        return FindCollisions(root, resolved, transition,
                               localSphere, localSphere1, localCurrCenter, localSpaceZ, scale);
    }


    public static bool SphereIntersectsPoly(
        PhysicsBSPNode? node,
        Dictionary<ushort, DatReaderWriter.Types.Polygon> polygons,
        DatReaderWriter.Types.VertexArray vertices,
        Vector3 sphereCenter,
        float sphereRadius,
        out ushort hitPolyId,
        out Vector3 hitNormal)
    {
        hitPolyId = 0;
        hitNormal = Vector3.Zero;
        if (node is null) return false;

        var resolved = BuildResolved(polygons, vertices);
        return SphereIntersectsPolyStaticRecurse(node, resolved, sphereCenter, sphereRadius,
                                                  ref hitPolyId, ref hitNormal);
    }

    internal static bool SphereIntersectsPoly(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Vector3 sphereCenter,
        float sphereRadius,
        out ushort hitPolyId,
        out Vector3 hitNormal)
    {
        hitPolyId = 0;
        hitNormal = Vector3.Zero;
        if (node is null) return false;

        return SphereIntersectsPolyStaticRecurse(
            node,
            resolved,
            sphereCenter,
            sphereRadius,
            ref hitPolyId,
            ref hitNormal);
    }

    private static bool SphereIntersectsPolyStaticRecurse(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Vector3 center,
        float radius,
        ref ushort hitPolyId,
        ref Vector3 hitNormal)
    {
        if (node is null) return false;

        // Broad phase.
        var bs = node.BoundingSphere;
        var d = center - bs.Origin;
        float r = radius + bs.Radius;
        if (d.LengthSquared() >= r * r) return false;

        if (node.Type == BSPNodeType.Leaf)
        {
            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;

                Vector3 cp = Vector3.Zero;
                if (PolygonHitsSpherePrecise(poly.Plane, poly.Vertices,
                                              center, radius, ref cp))
                {
                    hitPolyId = polyId;
                    hitNormal = poly.Plane.Normal;
                    return true;
                }
            }
            return false;
        }

        float splitDist = Vector3.Dot(node.SplittingPlane.Normal, center) + node.SplittingPlane.D;
        float reach = radius - PhysicsGlobals.EPSILON;

        if (splitDist >= reach)
            return SphereIntersectsPolyStaticRecurse(node.PosNode, resolved,
                                                      center, radius, ref hitPolyId, ref hitNormal);

        if (splitDist <= -reach)
            return SphereIntersectsPolyStaticRecurse(node.NegNode, resolved,
                                                      center, radius, ref hitPolyId, ref hitNormal);

        if (SphereIntersectsPolyStaticRecurse(node.PosNode, resolved,
                center, radius, ref hitPolyId, ref hitNormal))
            return true;

        return SphereIntersectsPolyStaticRecurse(node.NegNode, resolved,
                center, radius, ref hitPolyId, ref hitNormal);
    }

    // -------------------------------------------------------------------------
    // Legacy SphereIntersectsPolyWithTime — swept-sphere BSP query.
    // Used by FindObjCollisions in TransitionTypes.cs.
    // -------------------------------------------------------------------------

    public static bool SphereIntersectsPolyWithTime(
        PhysicsBSPNode? node,
        Dictionary<ushort, DatReaderWriter.Types.Polygon> polygons,
        DatReaderWriter.Types.VertexArray vertices,
        Vector3 sphereCenter,
        float sphereRadius,
        Vector3 movement,
        out ushort hitPolyId,
        out Vector3 hitNormal,
        out float hitTime)
    {
        hitPolyId = 0;
        hitNormal = Vector3.Zero;
        hitTime = float.MaxValue;
        if (node is null) return false;

        var resolved = BuildResolved(polygons, vertices);

        SphereIntersectsPolyWithTimeRecurse(node, resolved,
            sphereCenter, sphereRadius, movement,
            ref hitPolyId, ref hitNormal, ref hitTime);

        return hitTime < float.MaxValue;
    }

    internal static bool SphereIntersectsPolyWithTime(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Vector3 sphereCenter,
        float sphereRadius,
        Vector3 movement,
        out ushort hitPolyId,
        out Vector3 hitNormal,
        out float hitTime)
    {
        hitPolyId = 0;
        hitNormal = Vector3.Zero;
        hitTime = float.MaxValue;
        if (node is null) return false;

        SphereIntersectsPolyWithTimeRecurse(
            node,
            resolved,
            sphereCenter,
            sphereRadius,
            movement,
            ref hitPolyId,
            ref hitNormal,
            ref hitTime);

        return hitTime < float.MaxValue;
    }

    private static void SphereIntersectsPolyWithTimeRecurse(
        PhysicsBSPNode? node,
        Dictionary<ushort, ResolvedPolygon> resolved,
        Vector3 center,
        float radius,
        Vector3 movement,
        ref ushort hitPolyId,
        ref Vector3 hitNormal,
        ref float bestTime)
    {
        if (node is null) return;

        // Broad phase.
        var bs = node.BoundingSphere;
        var d = center - bs.Origin;
        float r = radius + bs.Radius + movement.Length() + 0.1f;
        if (d.LengthSquared() >= r * r) return;

        if (node.Type == BSPNodeType.Leaf)
        {
            foreach (ushort polyId in node.Polygons)
            {
                if (!resolved.TryGetValue(polyId, out var poly)) continue;

                if (Vector3.Dot(movement, poly.Plane.Normal) >= 0f) continue;

                // Test at start position.
                Vector3 cp = Vector3.Zero;
                if (PolygonHitsSpherePrecise(poly.Plane, poly.Vertices, center, radius, ref cp))
                {
                    if (0f < bestTime)
                    {
                        bestTime = 0f;
                        hitPolyId = polyId;
                        hitNormal = poly.Plane.Normal;
                    }
                    continue;
                }

                // Test at end position.
                var endCenter = center + movement;
                if (PolygonHitsSpherePrecise(poly.Plane, poly.Vertices, endCenter, radius, ref cp))
                {
                    if (1f < bestTime)
                    {
                        bestTime = 1f;
                        hitPolyId = polyId;
                        hitNormal = poly.Plane.Normal;
                    }
                }
            }
            return;
        }

        float splitDist = Vector3.Dot(node.SplittingPlane.Normal, center) + node.SplittingPlane.D;
        float reach = radius + movement.Length();

        if (splitDist >= reach)
        {
            SphereIntersectsPolyWithTimeRecurse(node.PosNode, resolved,
                center, radius, movement, ref hitPolyId, ref hitNormal, ref bestTime);
            return;
        }

        if (splitDist <= -reach)
        {
            SphereIntersectsPolyWithTimeRecurse(node.NegNode, resolved,
                center, radius, movement, ref hitPolyId, ref hitNormal, ref bestTime);
            return;
        }

        SphereIntersectsPolyWithTimeRecurse(node.PosNode, resolved,
            center, radius, movement, ref hitPolyId, ref hitNormal, ref bestTime);
        SphereIntersectsPolyWithTimeRecurse(node.NegNode, resolved,
            center, radius, movement, ref hitPolyId, ref hitNormal, ref bestTime);
    }


    private static Dictionary<ushort, ResolvedPolygon> BuildResolved(
        Dictionary<ushort, DatReaderWriter.Types.Polygon> polygons,
        DatReaderWriter.Types.VertexArray vertices)
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>(polygons.Count);
        foreach (var (id, poly) in polygons)
        {
            int n = poly.VertexIds.Count;
            if (n < 3) continue;

            var verts = new Vector3[n];
            bool valid = true;
            for (int i = 0; i < n; i++)
            {
                ushort vid = (ushort)poly.VertexIds[i];
                if (!vertices.Vertices.TryGetValue(vid, out var sv))
                { valid = false; break; }
                verts[i] = sv.Origin;
            }
            if (!valid) continue;

            var normal = Vector3.Zero;
            for (int i = 1; i < n - 1; i++)
                normal += Vector3.Cross(verts[i] - verts[0], verts[i + 1] - verts[0]);

            float len = normal.Length();
            if (len < 1e-8f) continue;
            normal /= len;

            float dotSum = 0f;
            for (int i = 0; i < n; i++)
                dotSum += Vector3.Dot(normal, verts[i]);
            float planeD = -(dotSum / n);

            resolved[id] = new ResolvedPolygon
            {
                Vertices = verts,
                Plane = new Plane(normal, planeD),
                NumPoints = n,
                SidesType = poly.SidesType,
                Id = id,
            };
        }
        return resolved;
    }
}
