using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class AdjustOffsetConformanceTests
{
    private readonly ITestOutputHelper _out;
    public AdjustOffsetConformanceTests(ITestOutputHelper output) => _out = output;

    private const float Tolerance = 1e-5f;


    [Fact]
    public void AdjustOffset_AwayFromPlane_SnapsPreservingXYAndResolvingZ()
    {
        var normal = new Vector3(0.5f, 0f, 0.8660254f);
        var t = new Transition();
        t.CollisionInfo.SetContactPlane(new Plane(normal, 0f), cellId: 0, isWater: false);

        // Moving +X only: dot(offset, N) = 0.5 > 0 -> AWAY from the plane -> snap.
        var offset = new Vector3(1f, 0f, 0f);
        Vector3 result = t.AdjustOffset(offset);

        // snap_to_plane preserves X and Y exactly and re-solves Z:
        //   z = -(x*N.x + y*N.y) / N.z = -(1*0.5 + 0*0) / 0.8660254 = -0.5773502691896258
        Assert.Equal(1f, result.X, Tolerance);
        Assert.Equal(0f, result.Y, Tolerance);
        Assert.Equal(-0.5773502691896258f, result.Z, Tolerance);

        _out.WriteLine($"snap result = ({result.X:F7}, {result.Y:F7}, {result.Z:F7})");
    }

    [Fact]
    public void AdjustOffset_AwayFromPlane_NearVerticalPlane_IsNoOp()
    {
        // |N.z| = 0.0001 <= PhysicsGlobals.EPSILON (0.0002) -> snap_to_plane's
        // divide-guard trips -> the ENTIRE offset (X, Y, and Z) is left
        // unchanged, not just Z.
        var normal = new Vector3(1f, 0f, 0.0001f);
        var t = new Transition();
        t.CollisionInfo.SetContactPlane(new Plane(normal, 0f), cellId: 0, isWater: false);

        // dot(offset, N) = 1*1 + 0 + 0*0.0001 = 1 > 0 -> away-from-plane arm
        // entered, but the epsilon guard inside must no-op.
        var offset = new Vector3(1f, 0f, 0f);
        Vector3 result = t.AdjustOffset(offset);

        Assert.Equal(offset, result);
        _out.WriteLine($"epsilon no-op result = ({result.X:F7}, {result.Y:F7}, {result.Z:F7})");
    }


    [Fact]
    public void AdjustOffset_IntoPlane_SubtractsFullNormalComponent()
    {
        var normal = new Vector3(0.5f, 0f, 0.8660254f);
        var t = new Transition();
        t.CollisionInfo.SetContactPlane(new Plane(normal, 0f), cellId: 0, isWater: false);

        // Moving -X: dot(offset, N) = -0.5 <= 0 -> INTO the plane -> subtract.
        var offset = new Vector3(-1f, 0f, 0f);
        Vector3 result = t.AdjustOffset(offset);

        Assert.Equal(-0.75f, result.X, Tolerance);
        Assert.Equal(0f, result.Y, Tolerance);
        Assert.Equal(0.4330127f, result.Z, Tolerance);
    }


    [Fact]
    public void AdjustOffset_SafetyPush_UsesBareRadiusForTriggerAndNumerator()
    {
        const float radius = 0.5f;
        var normal = new Vector3(0.5f, 0f, 0.8660254f); // 30 degrees, unit.
        const float dist = 0.47f; // strictly between radius*N.z (0.4330127) and radius (0.5)

        float naturalRestingDistOld = radius * normal.Z;
        Assert.True(dist > naturalRestingDistOld - PhysicsGlobals.EPSILON,
            "fixture must NOT trip the old radius*N.z trigger");
        Assert.True(dist < radius - PhysicsGlobals.EPSILON,
            "fixture MUST trip the new bare-radius trigger");

        var t = new Transition();
        t.CollisionInfo.SetContactPlane(new Plane(normal, 0f), cellId: 0xA9B40001u, isWater: false);

        float centerZ = dist / normal.Z;
        t.SpherePath.GlobalSphere[0].Origin = new Vector3(0f, 0f, centerZ);
        t.SpherePath.GlobalSphere[0].Radius = radius;

        float checkPosZBefore = t.SpherePath.CheckPos.Z;
        float globSphereZBefore = t.SpherePath.GlobalSphere[0].Origin.Z;

        t.AdjustOffset(Vector3.Zero);

        float expectedZDist = (radius - dist) / normal.Z; // bare-radius numerator
        float actualPush = t.SpherePath.CheckPos.Z - checkPosZBefore;

        Assert.True(actualPush > 0f,
            "the bare-radius trigger must fire and push the sphere up; " +
            "the old radius*N.z trigger would NOT have fired for this fixture " +
            $"(dist={dist}, old threshold={naturalRestingDistOld - PhysicsGlobals.EPSILON:F7}).");
        Assert.Equal(expectedZDist, actualPush, Tolerance);
        Assert.Equal(globSphereZBefore + expectedZDist, t.SpherePath.GlobalSphere[0].Origin.Z, Tolerance);

        _out.WriteLine($"push = {actualPush:F7} (expected bare-radius zDist = {expectedZDist:F7}); " +
                        $"old naturalRestingDist formula would have given " +
                        $"{(naturalRestingDistOld - dist) / normal.Z:F7} AND would not have fired at all.");
    }

    [Fact]
    public void AdjustOffset_SafetyPush_DoesNotFire_WhenAboveBareRadiusThreshold()
    {
        const float radius = 0.5f;
        var normal = new Vector3(0.5f, 0f, 0.8660254f);
        const float dist = 0.6f;

        var t = new Transition();
        t.CollisionInfo.SetContactPlane(new Plane(normal, 0f), cellId: 0xA9B40001u, isWater: false);

        float centerZ = dist / normal.Z;
        t.SpherePath.GlobalSphere[0].Origin = new Vector3(0f, 0f, centerZ);
        t.SpherePath.GlobalSphere[0].Radius = radius;

        float checkPosZBefore = t.SpherePath.CheckPos.Z;
        t.AdjustOffset(Vector3.Zero);

        Assert.Equal(checkPosZBefore, t.SpherePath.CheckPos.Z, Tolerance);
    }


    [Fact]
    public void Uphill_NoContactFlapAcrossTicks()
    {
        const float radius = 0.5f;
        const float angleDegrees = 42f;
        const uint cellId = 0xA9B40157u;

        float theta = angleDegrees * MathF.PI / 180f;
        float sinT = MathF.Sin(theta);
        float cosT = MathF.Cos(theta);
        Assert.True(cosT > PhysicsGlobals.FloorZ,
            "fixture sanity: the slope must be walkable by retail's own FloorZ test");

        var (engine, root) = BuildSlopeEngine(sinT, cosT, cellId);

        float x0 = 1.0f;
        float RestingRootZ(float x) => (radius * (1f - cosT) + sinT * x) / cosT;

        var body = new PhysicsBody
        {
            ContactPlaneValid = true,
            ContactPlane = new Plane(new Vector3(-sinT, 0f, cosT), 0f),
            ContactPlaneCellId = cellId,
            ContactPlaneIsWater = false,
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };

        Vector3 position = new(x0, 0f, RestingRootZ(x0));
        const float dxPerTick = 0.12f;
        const int ticks = 15;

        for (int tick = 0; tick < ticks; tick++)
        {
            Vector3 target = position + new Vector3(dxPerTick, 0f, 0f);

            ResolveResult result = engine.ResolveWithTransition(
                currentPos: position,
                targetPos: target,
                cellId: cellId,
                sphereRadius: radius,
                sphereHeight: 0f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f,
                isOnGround: true,
                body: body);

            _out.WriteLine(
                $"tick {tick}: ok={result.Ok} pos=({result.Position.X:F4},{result.Position.Y:F4}," +
                $"{result.Position.Z:F4}) inContact={result.InContact} onWalkable={result.OnWalkable} " +
                $"planeN=({result.ContactPlane.Normal.X:F4},{result.ContactPlane.Normal.Y:F4}," +
                $"{result.ContactPlane.Normal.Z:F4})");

            Assert.True(result.Ok, $"tick {tick}: transition must not get stuck running uphill");
            Assert.True(result.InContact,
                $"tick {tick}: contact must not be lost running uphill");
            Assert.True(result.OnWalkable,
                $"tick {tick}: OnWalkable must not flap to false running uphill on a walkable " +
                "slope. This guards the bare-radius push under the " +
                "plant-then-lift mechanism.");

            position = result.Position;
        }

        float expectedMinimumAdvance = dxPerTick * cosT * cosT * (ticks - 3);
        Assert.True(position.X - x0 > expectedMinimumAdvance,
            $"expected at least {expectedMinimumAdvance:F4} m of horizontal advance " +
            "(dx*cos^2(theta) per tick, retail's unchanged into-plane projection); " +
            $"got {position.X - x0:F4} m -- a shortfall here would mean the mover " +
            "stalled, not merely slowed by the expected slope projection.");
    }

    private static (PhysicsEngine Engine, PhysicsBSPNode Root) BuildSlopeEngine(
        float sinT, float cosT, uint cellId)
    {
        float ZAt(float x) => x * sinT / cosT;
        Vector3[] vertices =
        [
            new(-10f, -30f, ZAt(-10f)),
            new(60f, -30f, ZAt(60f)),
            new(60f, 30f, ZAt(60f)),
            new(-10f, 30f, ZAt(-10f)),
        ];
        var plane = new Plane(new Vector3(-sinT, 0f, cosT), 0f);

        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = new Vector3(25f, 0f, ZAt(25f)), Radius = 100f },
        };
        root.Polygons.Add(1);

        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [1] = new ResolvedPolygon
            {
                Id = 1,
                Vertices = vertices,
                Plane = plane,
                NumPoints = vertices.Length,
                SidesType = CullMode.None,
            },
        };

        var cell = new CellPhysics
        {
            BSP = new PhysicsBSPTree { Root = root },
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = resolved,
            CellBSP = new CellBSPTree { Root = new CellBSPNode { Type = BSPNodeType.Leaf } },
        };

        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };

        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 1f;
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        engine.DataCache.RegisterCellStructForTest(cellId, cell);
        return (engine, root);
    }
}
