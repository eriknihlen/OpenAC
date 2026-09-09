using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class BroadphaseReachFilterTests
{
    private const uint LandblockId = 0xA9B60000u;
    private const uint CellId      = LandblockId | 0x0001u;
    private const uint EntityId    = 0x333BEEF1u;
    private const uint GfxObjId    = 0x333BEEF2u;
    private const ushort FloorPolyId = 1;

    private const float SphereRadius   = 0.48f;
    private const float SphereHeight   = 1.835f;
    private const float StepUpHeight   = 0.60f;
    private const float StepDownHeight = 0.04f;

    /// <summary>Part origin. The filter measured to THIS point.</summary>
    private static readonly Vector3 PartOrigin = new(12f, 12f, 0f);

    /// <summary>
    /// Root bounding-sphere radius the registry publishes for this object,
    /// and the radius of the single BSP leaf node.
    /// </summary>
    private const float RootSphereRadius = 6f;

    [Fact]
    public void OffCentreBspFloorStopsAFallingMover()
    {
        var engine = BuildEngineWithFloorSlab(slabLocalZ: 40f);

        Assert.NotEmpty(engine.ShadowObjects.GetObjectsInCell(CellId));

        var (finalFeetZ, blocked) = DropOnto(engine, startFeetZ: 41.4f);

        Assert.True(
            finalFeetZ > 39.5f,
            $"the mover fell through a floor slab 40 m above its owner's part "
            + $"origin: feet reached z={finalFeetZ:F3}, and an unobstructed "
            + $"fall would have reached 37.8. blockedAtLeastOnce={blocked}. "
            + $"The query-site broadphase discarded the "
            + $"candidate before BSPQuery ever ran.");
    }

    [Fact]
    public void CentredBspFloorStopsAFallingMover()
    {
        var engine = BuildEngineWithFloorSlab(slabLocalZ: 0f);

        Assert.NotEmpty(engine.ShadowObjects.GetObjectsInCell(CellId));

        var (finalFeetZ, blocked) = DropOnto(engine, startFeetZ: 1.4f);

        Assert.True(
            finalFeetZ > -0.5f,
            $"the control slab at the part origin must still stop the mover; "
            + $"feet reached z={finalFeetZ:F3}, blockedAtLeastOnce={blocked}.");
    }

    private static (float FinalFeetZ, bool Blocked) DropOnto(
        PhysicsEngine engine, float startFeetZ)
    {
        var pos = new Vector3(PartOrigin.X, PartOrigin.Y, startFeetZ);
        bool blocked = false;

        for (int tick = 0; tick < 12; tick++)
        {
            Vector3 target = pos - new Vector3(0f, 0f, 0.30f);
            var result = engine.ResolveWithTransition(
                pos, target, CellId,
                SphereRadius, SphereHeight,
                StepUpHeight, StepDownHeight,
                isOnGround: false,
                body: null,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            blocked |= result.CollisionNormalValid;
            pos = result.Position;
        }

        return (pos.Z, blocked);
    }

    private static PhysicsEngine BuildEngineWithFloorSlab(float slabLocalZ)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        // Flat terrain far below: it must never be what stops the mover.
        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:  LandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        System.Array.Empty<CellSurface>(),
            portals:      System.Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        var floorVerts = new[]
        {
            new Vector3(-4f, -4f, slabLocalZ),
            new Vector3( 4f, -4f, slabLocalZ),
            new Vector3( 4f,  4f, slabLocalZ),
            new Vector3(-4f,  4f, slabLocalZ),
        };
        var floorNormal = new Vector3(0f, 0f, 1f);
        var floorPoly = new ResolvedPolygon
        {
            Vertices  = floorVerts,
            Plane     = new Plane(floorNormal, -Vector3.Dot(floorNormal, floorVerts[0])),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var leaf = new PhysicsBSPNode
        {
            Type           = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(0f, 0f, slabLocalZ),
                Radius = RootSphereRadius,
            },
        };
        leaf.Polygons.Add(FloorPolyId);

        var physics = new GfxObjPhysics
        {
            BSP             = new PhysicsBSPTree { Root = leaf },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices        = new VertexArray(),
            Resolved        = new Dictionary<ushort, ResolvedPolygon> { [FloorPolyId] = floorPoly },
            BoundingSphere  = new Sphere
            {
                Origin = new Vector3(0f, 0f, slabLocalZ),
                Radius = RootSphereRadius,
            },
        };
        cache.RegisterGfxObjForTest(GfxObjId, physics);

        // Registration publishes the ROOT SPHERE'S RADIUS about the PART
        // ORIGIN — the exact pairing the deleted filter then mis-measured.
        engine.ShadowObjects.Register(
            entityId:      EntityId,
            gfxObjId:      GfxObjId,
            worldPos:      PartOrigin,
            rotation:      Quaternion.Identity,
            radius:        RootSphereRadius,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   LandblockId,
            collisionType: ShadowCollisionType.BSP,
            cylHeight:     0f,
            scale:         1.0f,
            state:         0x1u,                       // STATIC_PS
            flags:         EntityCollisionFlags.None);

        return engine;
    }
}
