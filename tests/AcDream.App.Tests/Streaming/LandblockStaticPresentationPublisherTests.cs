using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.Rendering;
using AcDream.Core.Terrain;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockStaticPresentationPublisherTests
{
    private const uint LandblockId = 0xA9B4FFFFu;
    private const uint AdjacentLandblockId = 0xAAB4FFFFu;
    private const uint SetupId = 0x02000042u;
    private static readonly float[] HeightTable = new float[256];

    [Fact]
    public void Reapply_ReplacesLightsAndRefreshesPluginWithoutSpawnReplay()
    {
        var fixture = Fixture();
        int spawnCount = 0;
        fixture.Events.EntitySpawned += _ => spawnCount++;
        WorldEntity first = Entity(0x80A9B401u, new Vector3(12f, 12f, 0f));
        WorldEntity moved = Entity(first.Id, new Vector3(36f, 12f, 0f));

        Publish(fixture, Build(LandblockId, [first], BundleWithLight()));
        Assert.Equal(1, fixture.Lights.RegisteredCount);
        Assert.Equal(1, spawnCount);
        Assert.Equal(first.Position, Assert.Single(fixture.World.Entities).Position);

        fixture.Lighting.SetOwnerLighting(first.Id, enabled: false);
        fixture.Translucency.StartPartFade(first.Id, 0u, 0f, 0.6f, 0f);
        fixture.Translucency.StartPartFade(first.Id, 1u, 0f, 1f, 2f);
        fixture.Translucency.AdvanceAll(0.5f);
        Assert.True(fixture.Translucency.TryGetCurrentValue(
            first.Id,
            1u,
            out float activeFadeBefore));
        Publish(fixture, Build(LandblockId, [moved], BundleWithLight()));

        Assert.Equal(1, fixture.Lights.RegisteredCount);
        Assert.Equal(1, spawnCount);
        Assert.Equal(moved.Position, Assert.Single(fixture.World.Entities).Position);
        Assert.False(Assert.Single(fixture.Lighting.GetOwnedLights(first.Id)!).IsLit);
        Assert.Equal(1, fixture.Lighting.RetainedOwnerStateCount);
        Assert.True(fixture.Translucency.TryGetCurrentValue(
            first.Id,
            0u,
            out float settled));
        Assert.Equal(0.6f, settled);
        Assert.True(fixture.Translucency.TryGetCurrentValue(
            first.Id,
            1u,
            out float activeFadeAfter));
        Assert.Equal(activeFadeBefore, activeFadeAfter);
        Assert.Equal(1, fixture.Publisher.Diagnostics.PluginSpawnCount);
        Assert.Equal(1, fixture.Publisher.Diagnostics.PluginRefreshCount);
        Assert.Equal(2, fixture.Publisher.Diagnostics.LightReplacementCount);
    }

    [Fact]
    public void Reapply_OmittedStaticRemovesLightTranslucencyAndPluginProjection()
    {
        var fixture = Fixture();
        WorldEntity entity = Entity(0x80A9B401u, new Vector3(12f, 12f, 0f));
        Publish(fixture, Build(LandblockId, [entity], BundleWithLight()));
        fixture.Lighting.SetOwnerLighting(entity.Id, enabled: false);
        fixture.Translucency.StartPartFade(entity.Id, 0u, 0f, 1f, 2f);

        Publish(
            fixture,
            Build(LandblockId, Array.Empty<WorldEntity>(), PhysicsDatBundle.Empty));

        Assert.Equal(0, fixture.Lights.RegisteredCount);
        Assert.Equal(0, fixture.Lighting.RetainedOwnerStateCount);
        Assert.False(fixture.Translucency.TryGetCurrentValue(entity.Id, 0u, out _));
        Assert.Empty(fixture.World.Entities);
        Assert.Equal(1, fixture.Publisher.Diagnostics.PluginRemovalCount);
        Assert.Equal(0, fixture.Publisher.Diagnostics.ActiveLandblockCount);
        Assert.Equal(0, fixture.Publisher.Diagnostics.ActiveEntityCount);
    }

    [Fact]
    public void RetirementThenReload_EmitsNewLogicalPluginSpawn()
    {
        var fixture = Fixture();
        int spawnCount = 0;
        fixture.Events.EntitySpawned += _ => spawnCount++;
        WorldEntity entity = Entity(0x80A9B401u, new Vector3(12f, 12f, 0f));
        Publish(fixture, Build(LandblockId, [entity], BundleWithLight()));

        fixture.Publisher.RemoveLighting(entity);
        fixture.Publisher.RemoveTranslucency(entity);
        fixture.Publisher.RemovePluginProjection(entity);
        Assert.Equal(0, fixture.Publisher.Diagnostics.ActiveEntityCount);

        Publish(fixture, Build(LandblockId, [entity], BundleWithLight()));

        Assert.Equal(2, spawnCount);
        Assert.Equal(1, fixture.Lights.RegisteredCount);
        Assert.Equal(1, fixture.Publisher.Diagnostics.ActiveEntityCount);
    }

    [Fact]
    public void Prepare_RejectsDuplicateAndCrossLandblockStaticIdsBeforeMutation()
    {
        var fixture = Fixture();
        WorldEntity first = Entity(0x80A9B401u, Vector3.Zero);
        LandblockPhysicsPublication duplicatePhysics = PhysicsPrefix(
            fixture,
            Build(LandblockId, [first, Entity(first.Id, Vector3.One)]));

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Publisher.PreparePublication(duplicatePhysics));
        Assert.Empty(fixture.World.Entities);
        Assert.Equal(0, fixture.Lights.RegisteredCount);

        Publish(fixture, Build(LandblockId, [first]));
        LandblockPhysicsPublication adjacentPhysics = PhysicsPrefix(
            fixture,
            Build(AdjacentLandblockId, [Entity(first.Id, new Vector3(192f, 0f, 0f))]));
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Publisher.PreparePublication(adjacentPhysics));
        Assert.Single(fixture.World.Entities);
    }

    [Fact]
    public void LiveProjectionInBuild_IsIgnoredByDatStaticPresentation()
    {
        var fixture = Fixture();
        WorldEntity live = Entity(7u, Vector3.Zero, serverGuid: 0x80000007u);

        Publish(fixture, Build(LandblockId, [live], BundleWithLight()));

        Assert.Empty(fixture.World.Entities);
        Assert.Equal(0, fixture.Lights.RegisteredCount);
        Assert.Equal(0, fixture.Publisher.Diagnostics.ActiveEntityCount);
    }

    [Fact]
    public void ForeignReceipt_IsRejectedWithoutChangingOwnerState()
    {
        var first = Fixture();
        var second = Fixture();
        LandblockPhysicsPublication physics = PhysicsPrefix(
            first,
            Build(LandblockId, [Entity(1u, Vector3.Zero)]));
        LandblockStaticPresentationPublication receipt =
            first.Publisher.PreparePublication(physics);

        Assert.Throws<ArgumentException>(() =>
            second.Publisher.BeginPublication(receipt));
        Assert.Equal(0, second.Publisher.Diagnostics.BeginCount);
    }

    private static void Publish(FixtureState fixture, LandblockBuild build)
    {
        LandblockPhysicsPublication physics = PhysicsPrefix(fixture, build);
        fixture.Render.CompletePublication(fixture.LastRender!);
        LandblockStaticPresentationPublication presentation =
            fixture.Publisher.PreparePublication(physics);
        fixture.Publisher.BeginPublication(presentation);
        fixture.Physics.CompletePublication(
            physics,
            entity => fixture.Publisher.PrepareEntityBeforeCollision(
                presentation,
                entity));
        fixture.Publisher.CompletePublication(presentation);
    }

    private static LandblockPhysicsPublication PhysicsPrefix(
        FixtureState fixture,
        LandblockBuild build)
    {
        LandblockRenderPublication render = fixture.Render.BeginPublication(
            build,
            EmptyMesh());
        fixture.LastRender = render;
        LandblockPhysicsPublication physics =
            fixture.Physics.PreparePublication(render);
        fixture.Physics.BeginPublication(physics);
        return physics;
    }

    private static FixtureState Fixture()
    {
        var entityObjects =
            new AcDream.Runtime.Entities.RuntimeEntityObjectLifetime(
                new PhysicsDataCache());
        PhysicsDataCache cache = entityObjects.Physics.DataCache;
        PhysicsEngine engine = entityObjects.Physics.Engine;
        var gpuState = new GpuWorldState();
        var render = new LandblockRenderPublisher(
            static (_, _, _) => { },
            static _ => { },
            new CellVisibility(),
            gpuState);
        var physics = new LandblockPhysicsPublisher(
            entityObjects.Physics,
            HeightTable);
        var lights = new LightManager();
        var lighting = new LightingHookSink(lights, new NullPoseSource());
        var translucency = new TranslucencyFadeManager();
        var world = new WorldGameState();
        var events = new WorldEvents();
        return new FixtureState(
            render,
            physics,
            new LandblockStaticPresentationPublisher(
                lighting,
                translucency,
                world,
                events),
            lights,
            lighting,
            translucency,
            world,
            events);
    }

    private static PhysicsDatBundle BundleWithLight()
    {
        var setup = new Setup();
        setup.Lights[0] = new LightInfo
        {
            ViewSpaceLocation = new Frame { Orientation = Quaternion.Identity },
            Color = new ColorARGB
            {
                Red = 255,
                Green = 255,
                Blue = 255,
                Alpha = 255,
            },
            Intensity = 1f,
            Falloff = 8f,
        };
        return new PhysicsDatBundle(
            new LandBlockInfo(),
            new Dictionary<uint, EnvCell>(),
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>(),
            new Dictionary<uint, Setup> { [SetupId] = setup },
            new Dictionary<uint, GfxObj>());
    }

    private static LandblockBuild Build(
        uint landblockId,
        IReadOnlyList<WorldEntity> entities,
        PhysicsDatBundle? bundle = null) => new(
        new LoadedLandblock(
            landblockId,
            new LandBlock
            {
                Terrain = new TerrainInfo[81],
                Height = new byte[81],
            },
            entities,
            bundle ?? PhysicsDatBundle.Empty),
        Origin: new LandblockBuildOrigin(0xA9, 0xB4));

    private static WorldEntity Entity(
        uint id,
        Vector3 position,
        uint serverGuid = 0u) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = SetupId,
        Position = position,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static LandblockMeshData EmptyMesh() => new(
        Array.Empty<TerrainVertex>(),
        Array.Empty<uint>());

    private sealed record FixtureState(
        LandblockRenderPublisher Render,
        LandblockPhysicsPublisher Physics,
        LandblockStaticPresentationPublisher Publisher,
        LightManager Lights,
        LightingHookSink Lighting,
        TranslucencyFadeManager Translucency,
        WorldGameState World,
        WorldEvents Events)
    {
        public LandblockRenderPublication? LastRender { get; set; }
    }

    private sealed class NullPoseSource : IEntityEffectPoseSource
    {
        public bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld)
        {
            rootWorld = default;
            return false;
        }

        public bool TryGetPartPose(
            uint localEntityId,
            int partIndex,
            out Matrix4x4 partLocal)
        {
            partLocal = default;
            return false;
        }
    }
}
