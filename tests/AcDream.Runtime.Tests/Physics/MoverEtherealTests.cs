using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Runtime.Tests.Physics;

/// <summary>
/// An ethereal mover (a body whose own state has the ethereal bit, as a
/// respawned gem token's 0x414) passes through objects that are not static
/// and collides with static ones. Objects made from the dats (landblock and
/// cell statics, scenery) are static; objects the server creates are static
/// only when their state says so.
/// </summary>
public sealed class MoverEtherealTests
{
    private const uint LandblockId = 0xA9B40000u;
    private const uint CellId = LandblockId | 0x0001u;
    private const uint TableSetupId = 0x02007301u;
    private const uint TableId = 0x80A9B4F1u;
    private const float TableTop = 0.8f;
    private const float TableRadius = 1.0f;
    private static readonly Vector3 TableAt = new(12f, 12f, 0f);
    private const float TokenRadius = 0.1f;
    private const PhysicsStateFlags TokenState = (PhysicsStateFlags)0x414u;

    [Fact]
    public void DatStatics_AreRegisteredStatic()
    {
        PhysicsEngine engine = EngineWithDatTable();

        ShadowEntry[] entries = engine.ShadowObjects.AllEntriesForDebug()
            .Where(entry => entry.EntityId == TableId)
            .ToArray();

        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
            Assert.Equal((uint)PhysicsStateFlags.Static, entry.State));
    }

    [Fact]
    public void EtherealToken_FallsThroughAServerTable_ToTheFloor()
    {
        PhysicsEngine engine = EngineWithServerTable();

        Vector3 end = DropBody(engine, TokenState);

        Assert.True(MathF.Abs(end.Z) < 0.01f, $"it lies on the floor: {end}");
    }

    [Fact]
    public void EtherealToken_LandsOnADatTable()
    {
        PhysicsEngine engine = EngineWithDatTable();

        Vector3 end = DropBody(engine, TokenState);

        Assert.True(MathF.Abs(end.Z - TableTop) < 0.01f, $"it lies on the table: {end}");
    }

    [Fact]
    public void SolidObject_LandsOnAServerTable()
    {
        PhysicsEngine engine = EngineWithServerTable();

        Vector3 end = DropBody(engine, PhysicsStateFlags.Gravity);

        Assert.True(MathF.Abs(end.Z - TableTop) < 0.01f, $"it lies on the table: {end}");
    }

    /// <summary>
    /// Drops a body with <paramref name="state"/> from 2 m above the table
    /// centre, 0.25 m a tick, through the body's own sweep; returns where its
    /// foot ends.
    /// </summary>
    private static Vector3 DropBody(PhysicsEngine engine, PhysicsStateFlags state)
    {
        var pos = new Vector3(TableAt.X, TableAt.Y, 2f);
        var body = new PhysicsBody
        {
            Position = pos,
            Orientation = Quaternion.Identity,
            State = state,
        };
        uint cell = CellId;
        for (int tick = 0; tick < 20; tick++)
        {
            ResolveResult result = engine.ResolveWithTransition(
                pos, pos - new Vector3(0f, 0f, 0.25f), cell,
                TokenRadius, sphereHeight: 0f,
                stepUpHeight: 0.04f, stepDownHeight: 0.09f,
                isOnGround: false,
                body: body,
                moverFlags: ObjectInfoState.EdgeSlide,
                movingEntityId: 0x70000D51u);
            Assert.True(result.Ok, $"the sweep failed at {pos}");
            pos = result.Position;
            cell = result.CellId;
            body.Position = pos;
        }

        return pos;
    }

    private static PhysicsEngine EngineWithServerTable()
    {
        PhysicsEngine engine = FlatEngine();
        engine.ShadowObjects.Register(
            TableId, gfxObjId: 0u,
            TableAt, Quaternion.Identity, TableRadius,
            worldOffsetX: 0f, worldOffsetY: 0f, landblockId: LandblockId,
            collisionType: ShadowCollisionType.Cylinder,
            cylHeight: TableTop,
            state: 0u);
        return engine;
    }

    /// <summary>The table as the dats place it: a landblock static.</summary>
    private static PhysicsEngine EngineWithDatTable()
    {
        var setup = new Setup();
        setup.CylSpheres.Add(new CylSphere
        {
            Origin = Vector3.Zero,
            Radius = TableRadius,
            Height = TableTop,
        });
        FlatSetupCollision flatSetup = FlatCollisionAssetBuilder.FlattenSetup(setup);
        var entity = new WorldEntity
        {
            Id = TableId,
            SourceGfxObjOrSetupId = TableSetupId,
            Position = TableAt,
            Rotation = Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
        };
        var landblock = new LoadedLandblock(
            LandblockId,
            new LandBlock { Terrain = new TerrainInfo[81], Height = new byte[81] },
            new[] { entity });
        var collisions = new LandblockCollisionBuild(
            ImmutableDictionary<uint, FlatGfxObjCollisionAsset>.Empty,
            ImmutableDictionary<uint, FlatSetupCollision>.Empty.Add(TableSetupId, flatSetup),
            ImmutableDictionary<uint, FlatCellStructureCollisionAsset>.Empty,
            ImmutableDictionary<uint, FlatEnvCellTopology>.Empty,
            ImmutableArray<uint>.Empty,
            ImmutableArray.Create(TableSetupId),
            ImmutableArray<uint>.Empty);
        PhysicsEngine engine = FlatEngine();
        LandblockPhysicsContentBuilder.CachePreparedObjects(engine.DataCache!, collisions);
        LandblockPhysicsContentBuilder.StaticCollisionPublication publication =
            LandblockPhysicsContentBuilder.PublishStaticCollision(
                engine, engine.DataCache!, landblock, collisions, origin: Vector3.Zero);
        Assert.Equal(1, publication.SetupOwnerCount);
        return engine;
    }

    /// <summary>Flat terrain at 0 across the landblock.</summary>
    private static PhysicsEngine FlatEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            landblockId: LandblockId,
            terrain: new TerrainSurface(new byte[81], new float[256]),
            cells: Array.Empty<CellSurface>(),
            portals: Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }
}
