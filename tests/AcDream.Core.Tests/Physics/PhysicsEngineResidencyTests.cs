using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PhysicsEngineResidencyTests
{
    [Fact]
    public void IsLandblockTerrainResident_trueOnlyAfterAddLandblock()
    {
        var eng = new PhysicsEngine();
        uint canonical = 0xA9B4FFFFu;
        uint lbId      = canonical & 0xFFFF0000u; // AddLandblock id

        Assert.False(eng.IsLandblockTerrainResident(canonical));

        eng.AddLandblock(lbId, terrain: null!, cells: Array.Empty<CellSurface>(),
            portals: Array.Empty<PortalPlane>(), worldOffsetX: 0f, worldOffsetY: 0f);

        Assert.True(eng.IsLandblockTerrainResident(canonical));
    }

    [Fact]
    public void IsLandblockTerrainResident_matchesOnHigh16_acceptsCellResolvedId()
    {
        var eng = new PhysicsEngine();
        eng.AddLandblock(0xA9B40000u, null!, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), 0f, 0f);
        Assert.True(eng.IsLandblockTerrainResident(0xA9B40019u));
        Assert.False(eng.IsLandblockTerrainResident(0xC6A90019u)); // different LB
    }

    [Fact]
    public void DemoteLandblockToTerrain_RemovesEnvCellsButPreservesTerrainResidency()
    {
        const uint landblockId = 0xA9B4FFFFu;
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        engine.AddLandblock(
            landblockId,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        cache.CellGraph.Add(new EnvCell(
            0xA9B40174u,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector3.Zero,
            Vector3.One,
            Array.Empty<CellPortal>(),
            Array.Empty<uint>(),
            false,
            null));

        engine.DemoteLandblockToTerrain(landblockId);

        Assert.True(engine.IsLandblockTerrainResident(landblockId));
        Assert.Null(cache.CellGraph.GetVisible(0xA9B40174u));
        Assert.IsType<LandCell>(cache.CellGraph.GetVisible(0xA9B40001u));
    }

    [Fact]
    public void DemoteLandblockToTerrain_DeregistersCrossBoundaryStaticButRetainsDynamic()
    {
        const uint landblockId = 0xA9B4FFFFu;
        const uint adjacentCell = 0xAAB40001u;
        const uint staticId = 100u;
        const uint dynamicId = 200u;
        var engine = new PhysicsEngine();
        engine.AddLandblock(
            landblockId,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        var seamPosition = new Vector3(191.5f, 12f, 1f);
        engine.ShadowObjects.Register(
            staticId,
            0x01000001u,
            seamPosition,
            Quaternion.Identity,
            2f,
            0f,
            0f,
            landblockId,
            isStatic: true);
        engine.ShadowObjects.Register(
            dynamicId,
            0x01000002u,
            seamPosition,
            Quaternion.Identity,
            2f,
            0f,
            0f,
            landblockId,
            isStatic: false);
        Assert.Contains(
            engine.ShadowObjects.GetObjectsInCell(adjacentCell),
            entry => entry.EntityId == staticId);
        Assert.Contains(
            engine.ShadowObjects.GetObjectsInCell(adjacentCell),
            entry => entry.EntityId == dynamicId);

        engine.DemoteLandblockToTerrain(landblockId);

        Assert.DoesNotContain(
            engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == staticId);
        Assert.Contains(
            engine.ShadowObjects.GetObjectsInCell(adjacentCell),
            entry => entry.EntityId == dynamicId);
        Assert.Equal(1, engine.ShadowObjects.RetainedRegistrationCount);
    }

    [Fact]
    public void RemoveLandblock_DeregistersCrossBoundaryStaticButRetainsDynamic()
    {
        const uint landblockId = 0xA9B4FFFFu;
        const uint adjacentCell = 0xAAB40001u;
        const uint staticId = 300u;
        const uint dynamicId = 400u;
        var engine = new PhysicsEngine();
        engine.AddLandblock(
            landblockId,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        var seamPosition = new Vector3(191.5f, 12f, 1f);
        engine.ShadowObjects.Register(
            staticId, 0x01000003u, seamPosition, Quaternion.Identity, 2f,
            0f, 0f, landblockId, isStatic: true);
        engine.ShadowObjects.Register(
            dynamicId, 0x01000004u, seamPosition, Quaternion.Identity, 2f,
            0f, 0f, landblockId, isStatic: false);

        engine.RemoveLandblock(landblockId);

        Assert.DoesNotContain(
            engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == staticId);
        Assert.Contains(
            engine.ShadowObjects.GetObjectsInCell(adjacentCell),
            entry => entry.EntityId == dynamicId);
        Assert.Equal(1, engine.ShadowObjects.RetainedRegistrationCount);
    }

    [Fact]
    public void RemoveLandblock_RemovesOnlyItsCachedBuildings()
    {
        const uint landblockId = 0xA9B4FFFFu;
        const uint retiredBuildingCell = 0xA9B40001u;
        const uint adjacentBuildingCell = 0xAAB40001u;
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        cache.RegisterBuildingForTest(
            retiredBuildingCell,
            Building(Matrix4x4.Identity));
        cache.RegisterBuildingForTest(
            adjacentBuildingCell,
            Building(Matrix4x4.CreateTranslation(192f, 0f, 0f)));

        engine.RemoveLandblock(landblockId);

        Assert.Null(cache.GetBuilding(retiredBuildingCell));
        Assert.NotNull(cache.GetBuilding(adjacentBuildingCell));
    }

    private static BuildingPhysics Building(Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out Matrix4x4 inverse);
        return new BuildingPhysics
        {
            WorldTransform = transform,
            InverseWorldTransform = inverse,
            Portals = Array.Empty<BldPortalInfo>(),
        };
    }
}
