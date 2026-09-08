using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

internal sealed class RemoteRampHarness : IDisposable
{
    private const uint LandblockId = 0x0101FFFFu;

    internal const float StartX = 96f;

    internal const float StartY = 96f;

    private readonly RuntimeEntityObjectLifetime _lifetime;
    private readonly RuntimeEntityRecord _record;
    private readonly RuntimeRemotePhysicsUpdater _updater;

    internal RemoteMotion Remote { get; }

    internal TerrainSurface Surface { get; }

    internal PhysicsEngine Engine => _lifetime.Physics.Engine;

    private RemoteRampHarness(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record,
        RemoteMotion remote,
        RuntimeRemotePhysicsUpdater updater,
        TerrainSurface surface)
    {
        _lifetime = lifetime;
        _record = record;
        Remote = remote;
        _updater = updater;
        Surface = surface;
    }

    /// <summary>Terrain height at a world-space XY (the landblock sits at 0,0).</summary>
    internal float SurfaceZ(float worldX, float worldY)
        => Surface.SampleZ(worldX, worldY);

    internal float SurfaceZUnderBody()
        => SurfaceZ(Remote.Body.Position.X, Remote.Body.Position.Y);

    internal static RemoteRampHarness OnRamp(float gradient)
    {
        RemoteRampHarness harness = Create(gradient, heightAboveSurface: 0f);
        SpawnPlacementSettler.TrySettle(
            harness._lifetime.Physics.Engine,
            harness.Remote.Body,
            harness.Remote.Body.Position,
            harness.Remote.CellId,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f,
            ObjectInfoState.EdgeSlide,
            harness._record.LocalEntityId!.Value,
            harness.Remote.Movement.HitGround,
            harness.Remote.Motion.LeaveGround);
        harness.Remote.Airborne = !harness.Remote.Body.OnWalkable;
        return harness;
    }

    internal static RemoteRampHarness Airborne(float gradient, float height)
    {
        RemoteRampHarness harness = Create(gradient, heightAboveSurface: height);
        harness.Remote.Body.TransientState &= ~(TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable);
        harness.Remote.Body.ContactPlaneValid = false;
        harness.Remote.Airborne = true;
        return harness;
    }

    private static RemoteRampHarness Create(float gradient, float heightAboveSurface)
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        TerrainSurface surface = Ramp(gradient);
        lifetime.Physics.Engine.AddLandblock(
            LandblockId,
            surface,
            Array.Empty<AcDream.Core.Physics.CellSurface>(),
            Array.Empty<AcDream.Core.Physics.PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        RuntimeEntityRecord record = lifetime.Entities.AddActive(Spawn());
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity
                | PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.EdgeSlide,
            InWorld = true,
        };
        var remote = new RemoteMotion(body);
        lifetime.Entities.SetPhysicsBody(record, body);
        lifetime.Entities.SetRemoteMotion(record, remote);
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);

        float surfaceZ = surface.SampleZ(StartX, StartY);
        body.Position = new Vector3(
            StartX,
            StartY,
            surfaceZ + heightAboveSurface);
        body.Orientation = Quaternion.Identity;
        remote.CellId = TerrainSurface.ComputeOutdoorCellId(
            LandblockId,
            StartX,
            StartY);
        remote.LastServerPos = body.Position;
        remote.LastServerPosTime = 1.0;

        return new RemoteRampHarness(
            lifetime,
            record,
            remote,
            new RuntimeRemotePhysicsUpdater(lifetime.Physics),
            surface);
    }

    /// <summary>Tick with an empty animation root-motion frame.</summary>
    internal void Tick(int count, float dt = 1f / 30f)
        => Tick(count, Vector3.Zero, dt);

    internal void Tick(int count, Vector3 rootMotionLocalPerTick, float dt = 1f / 30f)
    {
        var frame = new MotionDeltaFrame();
        for (int i = 0; i < count; i++)
        {
            frame.Reset();
            frame.Origin = rootMotionLocalPerTick;
            _updater.Tick(
                _record,
                Remote,
                objectScale: 1f,
                sequencer: null,
                dt,
                _record.ObjectClockEpoch,
                frame,
                radius: 0.48f,
                height: 1.835f,
                liveCenterX: 1,
                liveCenterY: 1);
        }
    }

    private static TerrainSurface Ramp(float gradient)
    {
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = i * gradient * TerrainSurface.CellSize;

        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)(8 - y);

        return new TerrainSurface(heights, heightTable);
    }

    private static WorldSession.EntitySpawn Spawn()
    {
        var position = new CreateObject.ServerPosition(
            LandblockId,
            StartX,
            StartY,
            0f,
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
        const uint rawState = (uint)(PhysicsStateFlags.Gravity
            | PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.EdgeSlide);
        var physics = new PhysicsSpawnData(
            RawState: rawState,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
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
            0x70000101u,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "remote-ramp-fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: rawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    public void Dispose() => _lifetime.Dispose();
}
