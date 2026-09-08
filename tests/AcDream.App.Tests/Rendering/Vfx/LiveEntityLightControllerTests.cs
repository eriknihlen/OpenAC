using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class LiveEntityLightControllerTests
{
    [Fact]
    public void Refresh_FollowsCurrentTopLevelRootAndCell()
    {
        var fixture = new Fixture();
        WorldEntity entity = fixture.Materialize(LiveEntityProjectionKind.World);
        LightSource light = Assert.Single(fixture.Sink.GetOwnedLights(entity.Id)!);

        entity.SetPosition(new Vector3(20, 30, 40));
        entity.ParentCellId = 0x01010002u;
        Assert.True(fixture.Poses.UpdateRoot(entity));
        fixture.Controller.Refresh();

        Assert.Equal(new Vector3(21, 30, 40), light.WorldPosition);
        Assert.Equal(0x01010002u, light.CellId);
        Assert.True(light.IsDynamic);
    }

    [Fact]
    public void Register_AttachedOwnerPreservesComposedChildRoot()
    {
        var fixture = new Fixture();
        WorldEntity entity = fixture.Materialize(LiveEntityProjectionKind.Attached);
        fixture.Poses.Publish(
            entity.Id,
            Matrix4x4.CreateTranslation(50, 60, 70),
            Array.Empty<Matrix4x4>(),
            0x01010001u);

        fixture.Controller.OnAttachedPoseReady(Fixture.Guid);
        fixture.Controller.Refresh();

        LightSource light = Assert.Single(fixture.Sink.GetOwnedLights(entity.Id)!);
        Assert.Equal(new Vector3(51, 60, 70), light.WorldPosition);
    }

    [Fact]
    public void AuthoritativeLightingTransitions_DestroyAndRecreateSetupLights()
    {
        var fixture = new Fixture();
        WorldEntity entity = fixture.Materialize(LiveEntityProjectionKind.World);
        Assert.Equal(1, fixture.Lights.RegisteredCount);

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Fixture.Guid,
                (uint)PhysicsStateFlags.ReportCollisions,
                1,
                2),
            out _));
        fixture.Controller.OnStateChanged(Fixture.Guid);
        Assert.Equal(0, fixture.Lights.RegisteredCount);

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Fixture.Guid,
                (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Lighting),
                1,
                3),
            out _));
        fixture.Controller.OnStateChanged(Fixture.Guid);
        Assert.Equal(1, fixture.Lights.RegisteredCount);
        Assert.Single(fixture.Sink.GetOwnedLights(entity.Id)!);
    }

    [Fact]
    public void SetLightHookStateSurvivesProjectionWithdrawal()
    {
        var fixture = new Fixture();
        WorldEntity entity = fixture.Materialize(LiveEntityProjectionKind.World);

        fixture.Sink.OnHook(
            entity.Id,
            entity.Position,
            new SetLightHook { LightsOn = false });
        Assert.Equal(0, fixture.Lights.RegisteredCount);

        fixture.Controller.Unregister(entity.Id);
        Assert.False(fixture.Controller.Register(Fixture.Guid));
        Assert.Equal(0, fixture.Lights.RegisteredCount);
    }

    [Fact]
    public void LoadedPendingLoadedTransitionsOwnLightPresentationExactlyOnce()
    {
        var fixture = new Fixture();
        fixture.Materialize(LiveEntityProjectionKind.World);
        Assert.Equal(1, fixture.Lights.RegisteredCount);

        fixture.Spatial.RemoveLandblock(0x0101FFFFu);
        Assert.Equal(0, fixture.Lights.RegisteredCount);

        fixture.Spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        Assert.Equal(1, fixture.Lights.RegisteredCount);
    }

    [Fact]
    public void InitialVisibleProjectionLoadsSetupLightsOnce()
    {
        var fixture = new Fixture();

        fixture.Materialize(LiveEntityProjectionKind.World);

        Assert.Equal(1, fixture.SetupLoadCount);
        Assert.Equal(1, fixture.Lights.RegisteredCount);
    }

    [Fact]
    public void LoadedToLoadedRebucketDoesNotRecreateLightPresentation()
    {
        var fixture = new Fixture();
        fixture.Spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        fixture.Materialize(LiveEntityProjectionKind.World);
        Assert.Equal(1, fixture.SetupLoadCount);

        Assert.True(fixture.Runtime.RebucketLiveEntity(Fixture.Guid, 0x02020001u));

        Assert.Equal(1, fixture.SetupLoadCount);
        Assert.Equal(1, fixture.Lights.RegisteredCount);
    }

    private sealed class Fixture
    {
        public const uint Guid = 0x70000001u;
        private readonly Setup _setup = BuildSetup();

        public Fixture()
        {
            Spatial.AddLandblock(new LoadedLandblock(
                0x0101FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = LiveEntityRuntimeFixture.Create(Spatial, new NoopResources(), _ => { });
            Sink = new LightingHookSink(Lights, Poses);
            Controller = new LiveEntityLightController(
                Runtime,
                Poses,
                Sink,
                setupId =>
                {
                    SetupLoadCount++;
                    return setupId == 0x02000001u ? _setup : null;
                });
        }

        public GpuWorldState Spatial { get; } = new();
        public LiveEntityRuntime Runtime { get; }
        public EntityEffectPoseRegistry Poses { get; } = new();
        public LightManager Lights { get; } = new();
        public LightingHookSink Sink { get; }
        public LiveEntityLightController Controller { get; }
        public int SetupLoadCount { get; private set; }

        public WorldEntity Materialize(LiveEntityProjectionKind kind)
        {
            WorldSession.EntitySpawn spawn = Spawn();
            Runtime.RegisterLiveEntity(spawn);
            WorldEntity entity = Runtime.MaterializeLiveEntity(
                Guid,
                spawn.Position!.Value.LandblockId,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(10, 10, 5),
                    Rotation = Quaternion.Identity,
                    ParentCellId = 0x01010001u,
                    MeshRefs = Array.Empty<MeshRef>(),
                },
                kind)!;
            Poses.PublishMeshRefs(entity);
            return entity;
        }

        private static Setup BuildSetup()
        {
            var setup = new Setup();
            setup.Lights[0] = new LightInfo
            {
                ViewSpaceLocation = new Frame
                {
                    Origin = Vector3.UnitX,
                    Orientation = Quaternion.Identity,
                },
                Color = new ColorARGB { Red = 255, Green = 255, Blue = 255, Alpha = 255 },
                Intensity = 1f,
                Falloff = 8f,
            };
            return setup;
        }

        private static WorldSession.EntitySpawn Spawn()
        {
            var position = new CreateObject.ServerPosition(
                0x01010001u, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
            return new WorldSession.EntitySpawn(
                Guid,
                position,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                null,
                null,
                "fixture",
                null,
                null,
                null,
                PhysicsState: (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Lighting),
                InstanceSequence: 1,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1).WithConsistentPhysics();
        }
    }

    private sealed class NoopResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }
}
