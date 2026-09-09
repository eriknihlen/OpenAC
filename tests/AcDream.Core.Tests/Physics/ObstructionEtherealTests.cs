using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ObstructionEtherealTests
{
    private const uint ETHEREAL_PS          = 0x4u;
    private const uint IGNORE_COLLISIONS_PS = 0x10u;


    [Fact]
    public void ShouldSkip_BothBits_InstantSkip()
    {
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: ETHEREAL_PS | IGNORE_COLLISIONS_PS,
            targetFlags: EntityCollisionFlags.None,
            moverState:  ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void ShouldSkip_EtherealAlone_NotInstantSkip()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: ETHEREAL_PS,
            targetFlags: EntityCollisionFlags.None,
            moverState:  ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void ShouldSkip_IgnoreCollisionsAlone_NotSkipped()
    {
        // IGNORE_COLLISIONS alone (0x10) without ETHEREAL -> not instant-skipped;
        // falls through to shape dispatch (no obstruction_ethereal set either).
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: IGNORE_COLLISIONS_PS,
            targetFlags: EntityCollisionFlags.None,
            moverState:  ObjectInfoState.IsPlayer));
    }


    [Fact]
    public void SpherePath_HasObstructionEtherealField()
    {
        var sp = new SpherePath();
        Assert.False(sp.ObstructionEthereal);   // default is false
        sp.ObstructionEthereal = true;
        Assert.True(sp.ObstructionEthereal);
    }


    [Fact]
    public void CylinderEtherealAlone_IsPassable()
    {
        var engine = BuildEngineWithSingleShadow(
            collisionType: ShadowCollisionType.Cylinder,
            objectState:   ETHEREAL_PS,
            objectPos:     new Vector3(12f, 12f, 0f),
            radius:        0.3f,
            cylHeight:     1.5f);

        var start  = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);

        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 20);

        Assert.False(blocked,
            $"Ethereal-alone Cylinder must be passable (no collision). " +
            $"Sphere stopped at ({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}).");
    }

    [Fact]
    public void CylinderNonEthereal_StillBlocks()
    {
        var engine = BuildEngineWithSingleShadow(
            collisionType: ShadowCollisionType.Cylinder,
            objectState:   0u,   // no ethereal bit
            objectPos:     new Vector3(12f, 12f, 0f),
            radius:        0.3f,
            cylHeight:     1.5f);

        var start   = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);

        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 20);

        Assert.True(blocked,
            $"Non-ethereal Cylinder must still block. " +
            $"Sphere reached ({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}) after 20 ticks.");
    }


    [Fact]
    public void SphereEtherealAlone_IsPassable()
    {
        var engine = BuildEngineWithSingleShadow(
            collisionType: ShadowCollisionType.Sphere,
            objectState:   ETHEREAL_PS,
            objectPos:     new Vector3(12f, 12f, 0.48f),
            radius:        0.4f,
            cylHeight:     0f);

        var start   = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);

        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 20);

        Assert.False(blocked,
            $"Ethereal-alone Sphere must be passable (no collision). " +
            $"Sphere stopped at ({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}).");
    }

    [Fact]
    public void SphereNonEthereal_StillBlocks()
    {
        var engine = BuildEngineWithSingleShadow(
            collisionType: ShadowCollisionType.Sphere,
            objectState:   0u,   // no ethereal bit
            objectPos:     new Vector3(12f, 12f, 0.48f),
            radius:        0.4f,
            cylHeight:     0f);

        var start   = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);

        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 20);

        Assert.True(blocked,
            $"Non-ethereal Sphere must still block. " +
            $"Sphere reached ({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}) after 20 ticks.");
    }


    private const uint Layer2LandblockId = 0xA9B50000u;
    private const uint Layer2CellId      = Layer2LandblockId | 0x0001u;
    private const uint Layer2EntityId    = 0xF4300u;
    private const uint Layer2GfxObjId    = 0xF4301u;
    private const ushort WallPolyId      = 1;

    private static PhysicsEngine BuildEngineWithBspSlab(uint objectState)
    {
        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;

        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        engine.AddLandblock(
            landblockId:  Layer2LandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        System.Array.Empty<CellSurface>(),
            portals:      System.Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        // Vertical wall polygon in OBJECT-LOCAL space (object is at world (12,12,0)).
        // Wall at local y=0, x in [-1,1], z in [-2,5].  Normal = -Y (faces the
        // approaching player who is at world y<12 = local y<0).
        // The player sphere is transformed to local space before BSP query:
        //   localOrigin = globalSphereOrigin - objectWorldPos = (12,y,0.48)-(12,12,0)
        //   = (0, y-12, 0.48).  Starts at y-12=-1, sweeps toward 0.
        var wallVerts = new[]
        {
            new Vector3(-1f, 0f, -2f),
            new Vector3( 1f, 0f, -2f),
            new Vector3( 1f, 0f,  5f),
            new Vector3(-1f, 0f,  5f),
        };
        var wallNormal = new Vector3(0f, -1f, 0f);
        float dVal = -Vector3.Dot(wallNormal, wallVerts[0]);   // = 0
        var wallPoly = new ResolvedPolygon
        {
            Vertices  = wallVerts,
            Plane     = new System.Numerics.Plane(wallNormal, dVal),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var leaf = new PhysicsBSPNode
        {
            Type           = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 6f },
        };
        leaf.Polygons.Add(WallPolyId);

        var bspTree = new PhysicsBSPTree { Root = leaf };
        var physics = new GfxObjPhysics
        {
            BSP             = bspTree,
            PhysicsPolygons = new Dictionary<ushort, DatReaderWriter.Types.Polygon>(),
            Vertices        = new VertexArray(),
            Resolved        = new Dictionary<ushort, ResolvedPolygon> { [WallPolyId] = wallPoly },
            BoundingSphere  = new Sphere { Origin = Vector3.Zero, Radius = 6f },
        };
        cache.RegisterGfxObjForTest(Layer2GfxObjId, physics);

        engine.ShadowObjects.Register(
            entityId:      Layer2EntityId,
            gfxObjId:      Layer2GfxObjId,
            worldPos:      new Vector3(12f, 12f, 0f),
            rotation:      System.Numerics.Quaternion.Identity,
            radius:        10f,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   Layer2LandblockId,
            collisionType: ShadowCollisionType.BSP,
            cylHeight:     0f,
            scale:         1.0f,
            state:         objectState,
            flags:         EntityCollisionFlags.None);

        return engine;
    }

    [Fact]
    public void BspEtherealNonStatic_Layer2_IsPassable()
    {
        const uint EtherealNonStatic = 0x4u;   // ETHEREAL_PS, no STATIC_PS
        var engine = BuildEngineWithBspSlab(EtherealNonStatic);

        var start   = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);
        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 30,
            startCellId: Layer2CellId);

        Assert.False(blocked,
            $"Ethereal non-static BSP slab must be passable via Layer-2 override " +
            $"(retail pc:276973). Sphere stopped at " +
            $"({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}).");
    }

    [Fact]
    public void BspEtherealStatic_Layer2Suppressed_StillBlocks()
    {
        const uint EtherealStatic = 0x4u | 0x1u;   // ETHEREAL_PS | STATIC_PS
        var engine = BuildEngineWithBspSlab(EtherealStatic);

        var start   = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);
        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 30,
            startCellId: Layer2CellId);

        Assert.True(blocked,
            $"Ethereal STATIC BSP slab must still block (Layer-2 suppressed by STATIC_PS=0x1). " +
            $"Sphere reached ({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}).");
    }

    [Fact]
    public void BspNonEthereal_NotPassable_RegressionGuard()
    {
        const uint NonEthereal = 0x0u;
        var engine = BuildEngineWithBspSlab(NonEthereal);

        var start   = new Vector3(12f, 11f, 0.48f);
        var perTick = new Vector3(0f, 0.10f, 0f);
        var (blocked, finalPos, _) = SweepUntilBlocked(engine, start, perTick, maxTicks: 30,
            startCellId: Layer2CellId);

        Assert.True(blocked,
            $"Non-ethereal BSP slab must still block (Layer-2 does not apply when " +
            $"sp.ObstructionEthereal=false). Sphere reached " +
            $"({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}).");
    }

    // Helpers

    private const uint TestLandblockId = 0xA9B40000u;
    private const uint TestCellId      = TestLandblockId | 0x0001u;
    private const uint TestEntityId    = 0xF4201u;
    private const uint TestGfxObjId    = 0xF4202u;

    private const float SphereRadius   = 0.48f;
    private const float SphereHeight   = 1.20f;
    private const float StepUpHeight   = 0.60f;
    private const float StepDownHeight = 0.04f;

    private static PhysicsEngine BuildEngineWithSingleShadow(
        ShadowCollisionType collisionType,
        uint                objectState,
        Vector3             objectPos,
        float               radius,
        float               cylHeight)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        // Stub landblock: flat terrain far below so it never interferes.
        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:  TestLandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        System.Array.Empty<CellSurface>(),
            portals:      System.Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        engine.ShadowObjects.Register(
            entityId:      TestEntityId,
            gfxObjId:      TestGfxObjId,
            worldPos:      objectPos,
            rotation:      System.Numerics.Quaternion.Identity,
            radius:        radius,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   TestLandblockId,
            collisionType: collisionType,
            cylHeight:     cylHeight,
            scale:         1.0f,
            state:         objectState,
            flags:         EntityCollisionFlags.None);

        return engine;
    }

    private static (bool blocked, Vector3 finalPos, Vector3 normal)
        SweepUntilBlocked(PhysicsEngine engine, Vector3 start, Vector3 perTick, int maxTicks,
                          uint startCellId = 0)
    {
        Vector3 pos      = start;
        uint    cellId   = startCellId != 0 ? startCellId : TestCellId;
        bool    onGround = false;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            Vector3 target = pos + perTick;
            var result = engine.ResolveWithTransition(
                pos, target, cellId,
                SphereRadius, SphereHeight,
                StepUpHeight, StepDownHeight,
                onGround,
                body:           null,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            if (result.CollisionNormalValid)
                return (true, result.Position, result.CollisionNormal);

            pos      = result.Position;
            cellId   = result.CellId;
            onGround = result.IsOnGround;
        }

        return (false, pos, Vector3.Zero);
    }
}
