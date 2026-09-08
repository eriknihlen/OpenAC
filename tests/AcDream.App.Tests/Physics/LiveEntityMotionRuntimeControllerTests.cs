using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Selection;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityMotionRuntimeControllerTests
{
    [Fact]
    public void MoveToObject_StaticTargetWithoutHost_MaterializesCanonicalMinimalHost()
    {
        const uint targetGuid = 0x70000091u;
        const uint cellId = 0x01010001u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        runtime.RegisterAndMaterializeProjection(Spawn(targetGuid, cellId));
        Assert.False(runtime.TryGetPhysicsHost(targetGuid, out _));

        var origin = new LiveWorldOriginState();
        origin.Recenter(1, 1);
        var controller = new LiveEntityMotionRuntimeController(
            runtime,
            new PhysicsDataCache(),
            static () => null,
            new SelectionState(),
            origin);
        var movement = new MovementManager(new MotionInterpreter());
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x50000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: 0x3D,
                ForwardCommand: null,
                MovementType: 6,
                MoveToParameters: 0x203u,
                MoveToSpeed: 1f,
                MoveToRunRate: 1f,
                MoveToPath: new CreateObject.MoveToPathData(
                    TargetGuid: targetGuid,
                    OriginCellId: cellId,
                    OriginX: 10f,
                    OriginY: 10f,
                    OriginZ: 5f,
                    DistanceToObject: 0.6f,
                    MinDistance: 0f,
                    FailDistance: 15f,
                    WalkRunThreshold: 15f,
                    DesiredHeading: 0f,
                    Bitfield: 0x203u)),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        Assert.True(controller.RouteServerMoveTo(movement, cellId, update));
        Assert.True(runtime.TryGetPhysicsHost(targetGuid, out var targetHost));
        Assert.IsType<EntityPhysicsHost>(targetHost);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cellId)
    {
        var position = new CreateObject.ServerPosition(
            cellId,
            10f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Static,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid: guid,
            Position: position,
            SetupTableId: 0x02000001u,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "static target",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: (uint)PhysicsStateFlags.Static,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
