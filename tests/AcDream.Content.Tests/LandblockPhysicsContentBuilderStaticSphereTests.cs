using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests;

public sealed class LandblockPhysicsContentBuilderStaticSphereTests
{
    private const uint LandblockId = 0xA9B40000u;
    private const uint SetupId = 0x02000042u;

    [Fact]
    public void PublishStaticCollision_SphereOnlySetup_MatchesFromSetupShapeForShape()
    {
        var setup = new Setup();
        setup.Spheres.Add(new Sphere
        {
            Origin = new Vector3(0.3f, -0.2f, 0.9f),
            Radius = 0.55f,
        });
        setup.Spheres.Add(new Sphere
        {
            Origin = new Vector3(-0.1f, 0.4f, 1.4f),
            Radius = 0.25f,
        });
        FlatSetupCollision flatSetup = FlatCollisionAssetBuilder.FlattenSetup(setup);

        const float entScale = 1.3f;
        var entity = new WorldEntity
        {
            Id = 0x80A9B401u,
            SourceGfxObjOrSetupId = SetupId,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = entScale,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var landblock = new LoadedLandblock(
            LandblockId,
            new LandBlock { Terrain = new TerrainInfo[81], Height = new byte[81] },
            new[] { entity });
        var collisions = new LandblockCollisionBuild(
            ImmutableDictionary<uint, FlatGfxObjCollisionAsset>.Empty,
            ImmutableDictionary<uint, FlatSetupCollision>.Empty.Add(SetupId, flatSetup),
            ImmutableDictionary<uint, FlatCellStructureCollisionAsset>.Empty,
            ImmutableDictionary<uint, FlatEnvCellTopology>.Empty,
            ImmutableArray<uint>.Empty,
            ImmutableArray.Create(SetupId),
            ImmutableArray<uint>.Empty);

        var engine = new PhysicsEngine();
        var cache = new PhysicsDataCache();
        LandblockPhysicsContentBuilder.CachePreparedObjects(cache, collisions);

        LandblockPhysicsContentBuilder.StaticCollisionPublication publication =
            LandblockPhysicsContentBuilder.PublishStaticCollision(
                engine, cache, landblock, collisions, origin: Vector3.Zero);

        Assert.Equal(1, publication.SetupOwnerCount);
        Assert.Equal(0, publication.BspOwnerCount);
        Assert.Equal(0, publication.NoCollisionCount);

        var expected = ShadowShapeBuilder.FromSetup(setup, entScale, _ => false);
        Assert.Equal(2, expected.Count);
        Assert.All(
            expected,
            shape => Assert.Equal(ShadowCollisionType.Sphere, shape.CollisionType));

        ShadowShape[] expectedOrdered = expected.OrderBy(shape => shape.Radius).ToArray();
        ShadowEntry[] entries = engine.ShadowObjects.AllEntriesForDebug()
            .Where(entry => entry.EntityId == entity.Id)
            .OrderBy(entry => entry.Radius)
            .ToArray();

        Assert.Equal(expectedOrdered.Length, entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            Assert.Equal(ShadowCollisionType.Sphere, entries[i].CollisionType);
            Assert.Equal(expectedOrdered[i].LocalPosition, entries[i].Position);
            Assert.Equal(expectedOrdered[i].Radius, entries[i].Radius);
            Assert.Equal(expectedOrdered[i].Scale, entries[i].Scale);
            Assert.Equal(expectedOrdered[i].CylHeight, entries[i].CylHeight);
        }
    }

    [Fact]
    public void PublishStaticCollision_NonCollidingGfxObjStatic_RegistersRenderOnly()
    {
        const uint gfxObjId = 0x01000043u;
        var gfx = new GfxObj
        {
            VertexArray = new VertexArray
            {
                Vertices = new Dictionary<ushort, SWVertex>
                {
                    [0] = new SWVertex { Origin = new Vector3(-0.5f, -0.5f, 0f) },
                    [1] = new SWVertex { Origin = new Vector3(0.5f, -0.5f, 0f) },
                    [2] = new SWVertex { Origin = new Vector3(0f, 0.5f, 1.2f) },
                },
            },
        };
        FlatGfxObjCollisionAsset asset = FlatCollisionAssetBuilder.FlattenGfxObj(gfx);
        Assert.True(asset.PhysicsBsp.RootIndex < 0); // the fixture really has no physics
        Assert.NotNull(asset.VisualBounds);

        var entity = new WorldEntity
        {
            Id = 0x80A9B402u,
            SourceGfxObjOrSetupId = gfxObjId,
            Position = new Vector3(12f, 12f, 0f),
            Rotation = Quaternion.Identity,
            MeshRefs = new[] { new MeshRef(gfxObjId, Matrix4x4.Identity) },
        };
        var landblock = new LoadedLandblock(
            LandblockId,
            new LandBlock { Terrain = new TerrainInfo[81], Height = new byte[81] },
            new[] { entity });
        var collisions = new LandblockCollisionBuild(
            ImmutableDictionary<uint, FlatGfxObjCollisionAsset>.Empty.Add(gfxObjId, asset),
            ImmutableDictionary<uint, FlatSetupCollision>.Empty,
            ImmutableDictionary<uint, FlatCellStructureCollisionAsset>.Empty,
            ImmutableDictionary<uint, FlatEnvCellTopology>.Empty,
            ImmutableArray.Create(gfxObjId),
            ImmutableArray<uint>.Empty,
            ImmutableArray<uint>.Empty);
        var engine = new PhysicsEngine();
        var cache = new PhysicsDataCache();
        LandblockPhysicsContentBuilder.CachePreparedObjects(cache, collisions);

        LandblockPhysicsContentBuilder.StaticCollisionPublication publication =
            LandblockPhysicsContentBuilder.PublishStaticCollision(
                engine, cache, landblock, collisions, origin: Vector3.Zero);

        Assert.Equal(0, publication.SetupOwnerCount);
        Assert.Equal(0, publication.BspOwnerCount);
        Assert.Equal(1, publication.NoCollisionCount);
        Assert.Empty(engine.ShadowObjects.AllEntriesForDebug());
        Assert.True(engine.ShadowObjects.TryGetRetailCellArray(entity.Id, out var cells));
        Assert.NotEmpty(cells);
        Assert.Equal(
            RetailCellArrayRoute.BoundingBox,
            engine.ShadowObjects.GetRetailCellArrayRoute(entity.Id));
        var parts = engine.ShadowObjects.GetRetailPartEntriesInCell(cells[0])
            .Where(part => part.EntityId == entity.Id)
            .ToArray();
        Assert.Single(parts);
        Assert.Equal(gfxObjId, parts[0].GfxObjId);
    }
}
