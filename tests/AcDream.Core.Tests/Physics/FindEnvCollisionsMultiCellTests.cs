using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class FindEnvCollisionsMultiCellTests
{
    private const uint VestibuleCellId = 0xA9B40157u;
    private const uint InteriorCellId  = 0xA9B40164u;

    private static CellBSPTree LeafCellBsp() => new CellBSPTree
    {
        Root = new CellBSPNode { Type = BSPNodeType.Leaf },
    };

    private static PhysicsBSPTree EmptyLeafBsp() => new PhysicsBSPTree
    {
        Root = new PhysicsBSPNode
        {
            Type           = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 10f },
        }
    };

    [Fact]
    public void IndoorSphereOverlappingAdjacentCellWithWall_HaltsTransition()
    {
        var (wallRoot, wallResolved) = BSPStepUpFixtures.TallWall();
        var interiorWT  = Matrix4x4.CreateTranslation(new Vector3(0.3f, 0f, 0f));
        Matrix4x4.Invert(interiorWT, out var interiorInv);

        var interior = new CellPhysics
        {
            BSP                   = new PhysicsBSPTree { Root = wallRoot },
            WorldTransform        = interiorWT,
            InverseWorldTransform = interiorInv,
            Resolved              = wallResolved,
            CellBSP               = LeafCellBsp(),
        };

        var portalPoly = new ResolvedPolygon
        {
            Vertices  = new[]
            {
                new Vector3(0.5f, -2.5f, 0f),
                new Vector3(0.5f,  2.5f, 0f),
                new Vector3(0.5f,  2.5f, 5f),
                new Vector3(0.5f, -2.5f, 5f),
            },
            Plane     = new Plane(new Vector3(1f, 0f, 0f), -0.5f),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var vestibule = new CellPhysics
        {
            BSP                   = EmptyLeafBsp(),
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP               = LeafCellBsp(),
            PortalPolygons        = new Dictionary<ushort, ResolvedPolygon> { [10] = portalPoly },
            Portals               = new[]
            {
                new PortalInfo(otherCellId: (ushort)(InteriorCellId & 0xFFFFu),
                               polygonId: 10, flags: 0),
            },
        };

        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();

        // Provide a flat terrain strip at z=0 so FindEnvCollisions's outdoor
        // fall-through has something to sample if it ever fires.
        var heights = new byte[81];
        Array.Fill(heights, (byte)0);
        var ht = new float[256];
        for (int i = 0; i < 256; i++) ht[i] = i * 1.0f;
        engine.AddLandblock(0xA9B4FFFFu, new TerrainSurface(heights, ht),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        engine.DataCache.RegisterCellStructForTest(VestibuleCellId, vestibule);
        engine.DataCache.RegisterCellStructForTest(InteriorCellId, interior);

        var from = new Vector3(0.1f, 0f, 0f);
        var to   = new Vector3(0.7f, 0f, 0f);
        var t    = BSPStepUpFixtures.MakeGroundedTransition(from, to,
            stepUpHeight: 0.04f,
            cellId: VestibuleCellId);

        t.FindTransitionalPosition(engine);

        Assert.True(t.SpherePath.CurPos.X <= 0.6f + PhysicsGlobals.EPSILON * 20f,
            $"Adjacent cell's wall must block the sphere at world x≈0.6; " +
            $"CurPos.X={t.SpherePath.CurPos.X:F4} (walked through = A4 regression).");
    }
}
