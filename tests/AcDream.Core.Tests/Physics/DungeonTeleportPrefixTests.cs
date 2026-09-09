using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class DungeonTeleportPrefixTests
{
    private const uint DungeonLandblock = 0x00070000u;
    private const uint DungeonCellId    = 0x00070143u;  // indoor (low 0x0143 ≥ 0x0100)
    private const uint HoltburgLandblock = 0xA9B30000u;  // a neighbouring resident block

    private static readonly Vector3 SpawnPos = new(70f, 70f, 0.01f);

    [Fact]
    public void ValidatedDungeonClaim_KeepsItsLandblockPrefix_NotTheNeighbour()
    {
        var engine = BuildEngine();

        PhysicsSetPositionResult result = engine.SetPosition(
            new PhysicsSetPositionRequest(
                Position: SpawnPos,
                Orientation: Quaternion.Identity,
                CellId: DungeonCellId,
                CellLocalPosition: SpawnPos,
                Spheres: default,
                Scale: 1f,
                StepUpHeight: 0.4f,
                StepDownHeight: 0.4f,
                Flags: PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Teleport
                    | PhysicsSetPositionFlags.Slide));

        Assert.True(result.IsCommitted);
        Assert.Equal(DungeonCellId, result.CellId);
        Assert.Equal(DungeonLandblock, result.CellId & 0xFFFF0000u);
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static PhysicsEngine BuildEngine()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        cache.RegisterCellStructForTest(DungeonCellId, MakeDungeonCell());

        engine.AddLandblock(
            landblockId:  HoltburgLandblock,
            terrain:      FlatTerrain(),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        engine.AddLandblock(
            landblockId:  DungeonLandblock,
            terrain:      FlatTerrain(),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 130f);

        return engine;
    }

    /// <summary>Flat 81-vertex stub terrain (all zero heights).</summary>
    private static TerrainSurface FlatTerrain() => new(new byte[81], new float[256]);

    private static CellPhysics MakeDungeonCell()
    {
        var floor = new ResolvedPolygon
        {
            Vertices = new[]
            {
                new Vector3(-100f, -100f, 0f),
                new Vector3( 200f, -100f, 0f),
                new Vector3( 200f,  200f, 0f),
                new Vector3(-100f,  200f, 0f),
            },
            Plane     = new Plane(new Vector3(0f, 0f, 1f), 0f),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        return new CellPhysics
        {
            BSP                   = new PhysicsBSPTree { Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf } },
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon> { [0] = floor },
            CellBSP               = new CellBSPTree { Root = new CellBSPNode { Type = BSPNodeType.Leaf } },
            Portals               = [new PortalInfo(0xFFFF, 0, 0)],
            PortalPolygons        = new Dictionary<ushort, ResolvedPolygon>(),
            VisibleCellIds        = new HashSet<uint>(),
        };
    }
}
