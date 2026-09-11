using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;

using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

/// <summary>
/// Placement may only commit a point that some resident cell contains. The
/// ordinary movement path keeps its seed-cell fallback when nothing contains
/// the sphere; placement must not inherit that fallback, or a crowded
/// placement search can accept a candidate on the far side of a room's wall.
/// </summary>
public sealed class PlacementContainmentTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Room = Landblock | 0x0150u;
    private const uint MoverId = 0x50000002u;
    private const float Radius = 0.48f;
    private const float SphereHeight = 1.835f;

    // A room whose interior is the half-space x >= 0 (one containment
    // plane) with a flat floor at z = 0 that also extends outside the room,
    // so an outside candidate has ground under it and the only thing that
    // can reject it is containment.
    private static CellBSPTree HalfSpaceContainment() => new()
    {
        Root = new CellBSPNode
        {
            SplittingPlane = new Plane(new Vector3(1f, 0f, 0f), 0f),
            PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
        },
    };

    // The slab 0 <= x <= 2: two containment planes. A crowd can fill the
    // whole slab around a target, so a placement search that keeps stepping
    // outward reaches candidates outside the room.
    private static CellBSPTree SlabContainment() => new()
    {
        Root = new CellBSPNode
        {
            SplittingPlane = new Plane(new Vector3(1f, 0f, 0f), 0f),
            PosNode = new CellBSPNode
            {
                SplittingPlane = new Plane(new Vector3(-1f, 0f, 0f), 2f),
                PosNode        = new CellBSPNode { Type = BSPNodeType.Leaf },
            },
        },
    };

    private static CellPhysics RoomWithFloor(CellBSPTree containment)
    {
        var verts = new[]
        {
            new Vector3(-20f, -20f, 0f),
            new Vector3( 20f, -20f, 0f),
            new Vector3( 20f,  20f, 0f),
            new Vector3(-20f,  20f, 0f),
        };
        var floor = new ResolvedPolygon
        {
            Vertices  = verts,
            Plane     = new Plane(new Vector3(0f, 0f, 1f), 0f),
            NumPoints = 4,
            SidesType = CullMode.None,
        };
        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 40f },
        };
        leaf.Polygons.Add(0);
        return new CellPhysics
        {
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon> { [0] = floor },
            BSP                   = new PhysicsBSPTree { Root = leaf },
            Portals               = [new PortalInfo(0xFFFF, 0, 0)],
            PortalPolygons        = new Dictionary<ushort, ResolvedPolygon>(),
            VisibleCellIds        = new HashSet<uint> { Room },
            CellBSP               = containment,
        };
    }

    private static PhysicsEngine EngineWithRoom(CellBSPTree containment)
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(Room, RoomWithFloor(containment));
        var engine = new PhysicsEngine { DataCache = cache };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

    private static PhysicsSetPositionResult Place(PhysicsEngine engine, Vector3 feet)
    {
        ImmutableArray<FlatCollisionSphere> spheres = ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, Radius), Radius),
            new FlatCollisionSphere(new Vector3(0f, 0f, SphereHeight - Radius), Radius));
        return engine.SetPosition(
            new PhysicsSetPositionRequest(
                Position: feet,
                Orientation: Quaternion.Identity,
                CellId: Room,
                CellLocalPosition: feet,
                Spheres: spheres,
                Scale: 1f,
                StepUpHeight: 0.4f,
                StepDownHeight: 0.4f,
                MoverFlags: ObjectInfoState.EdgeSlide,
                MovingEntityId: MoverId,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide));
    }

    [Fact]
    public void PlacementInsideTheRoom_Commits()
    {
        PhysicsEngine engine = EngineWithRoom(HalfSpaceContainment());

        PhysicsSetPositionResult result = Place(engine, new Vector3(2f, 3f, 0f));

        Assert.True(result.IsCommitted,
            $"error={result.Error} residence={result.Residence} pos={result.Position} cell=0x{result.CellId:X8} contact={result.InContact} walkable={result.OnWalkable}");
        Assert.Equal(Room, result.CellId);
        Assert.True(result.Position.X >= 0f);
    }

    [Fact]
    public void PlacementTargetOutsideEveryCell_IsDeferredNeverCommitted()
    {
        PhysicsEngine engine = EngineWithRoom(HalfSpaceContainment());

        PhysicsSetPositionResult result = Place(engine, new Vector3(-1f, 3f, 0f));

        Assert.True(
            !result.IsCommitted || result.Position.X >= 0f,
            $"placement accepted a point outside every cell: {result.Position} in 0x{result.CellId:X8}");
    }

    private static void RegisterCrowdSphere(PhysicsEngine engine, uint id, Vector3 feet)
    {
        engine.ShadowObjects.Register(
            id,
            gfxObjId: 0u,
            worldPos: feet + new Vector3(0f, 0f, Radius),
            rotation: Quaternion.Identity,
            radius: Radius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Landblock,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 0f,
            scale: 1f,
            state: 0u,
            flags: EntityCollisionFlags.IsCreature,
            seedCellId: Room,
            isStatic: false);
    }

    [Fact]
    public void CrowdedPlacementInsideASlabRoom_NeverCommitsOutsideTheRoom()
    {
        PhysicsEngine engine = EngineWithRoom(SlabContainment());
        var target = new Vector3(1f, 3f, 0f);
        uint id = 0x50001000u;
        for (float x = 0.3f; x <= 1.8f; x += 0.75f)
            for (float y = -2f; y <= 8f; y += 0.75f)
                RegisterCrowdSphere(engine, id++, new Vector3(x, y, 0f));

        PhysicsSetPositionResult result = Place(engine, target);

        Assert.True(
            !result.IsCommitted || (result.Position.X >= 0f && result.Position.X <= 2f),
            $"placement escaped the room: committed={result.IsCommitted} pos={result.Position} cell=0x{result.CellId:X8} error={result.Error} residence={result.Residence}");
    }
}
