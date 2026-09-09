using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.Physics;

internal sealed class RemotePlacementDriveFixture : IDisposable
{
    internal const uint SourceLandblock = 0xB1000000u;
    internal const uint SourceCell = SourceLandblock | 0x0001u;
    internal const uint DestinationLandblock = 0xB2000000u;
    internal const uint DestinationCell = DestinationLandblock | 0x0001u;
    internal static readonly Vector3 DestinationWorldOffset = new(192f, 0f, 0f);
    internal const float SpawnHeight = 7f;

    private readonly ServiceWindow _window = new();

    internal RemotePlacementDriveFixture()
    {
        Lifetime = new RuntimeEntityObjectLifetime(FlatEngine());
        Drive = new RuntimeRemotePlacementDriveController(
            Lifetime,
            new GameRuntimeClock(),
            new UnusedCollisionSource(),
            _window);
    }

    internal RuntimeEntityObjectLifetime Lifetime { get; }

    internal RuntimeRemotePlacementDriveController Drive { get; }

    internal void AllowDestination() => _window.Allow(DestinationLandblock);

    internal void PublishDestinationCollision()
    {
        var heights = new byte[81];
        Array.Fill(heights, (byte)SpawnHeight);
        var heightTable = new float[256];
        for (int index = 0; index < heightTable.Length; index++)
            heightTable[index] = index;
        Lifetime.Physics.ObserveLocalWorldFrame(
            SourceCell, teleportAdvanced: false);
        Lifetime.Physics.SetPosition.BeginCollisionGeneration(
            DestinationLandblock, 1UL);
        Lifetime.Physics.Engine.AddLandblock(
            DestinationLandblock,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: DestinationWorldOffset.X,
            worldOffsetY: DestinationWorldOffset.Y);
        Lifetime.Physics.SetPosition.CommitCollisionGeneration(
            DestinationLandblock, 1UL, ready: true);
    }

    internal (RuntimeEntityRecord Record, RemoteMotion Remote, PhysicsBody Body)
        AddRemote(uint guid, Vector3 destination)
    {
        RuntimeEntityRecord record = Lifetime.RegisterEntity(
            Spawn(guid)).Canonical!;
        Lifetime.Entities.SetFinalPhysicsState(record, PhysicsStateFlags.Gravity);
        Lifetime.Entities.SetFullCell(
            record, SourceCell, (SourceCell & 0xFFFF0000u) | 0xFFFFu);
        var body = new PhysicsBody
        {
            Position = new Vector3(10f, 10f, SpawnHeight),
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = PhysicsStateFlags.Gravity,
            TransientState = TransientStateFlags.Active
                | TransientStateFlags.Contact
                | TransientStateFlags.OnWalkable,
        };
        body.SnapToCell(SourceCell, body.Position, body.Position);
        Lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        Lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        record.Snapshot = record.Snapshot with
        {
            Position = new CreateObject.ServerPosition(
                DestinationCell,
                destination.X,
                destination.Y,
                destination.Z,
                1f,
                0f,
                0f,
                0f),
        };
        RemoteMotion remote = Lifetime.Physics.GetOrCreateRemoteMotion(record);
        remote.LastServerPosTime = 1_700_000_000d;
        return (record, remote, body);
    }

    /// <summary>
    /// Stands in for the production placement-projection subscription this
    /// bare fixture never wires.
    /// </summary>
    internal void DrainPlacementFifo()
    {
        while (Lifetime.Physics.SetPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head))
        {
            if (!Lifetime.Physics.SetPosition.AcknowledgeProjection(head.Token))
                break;
        }
    }

    internal int LiveOperationCount =>
        Lifetime.Physics.CaptureOwnership().SetPositionOperationCount;

    internal int RemotePlacementLedger =>
        Lifetime.CaptureOwnership().RemotePlacementDrivePendingCount;

    public void Dispose() => Lifetime.Dispose();

    private static WorldSession.EntitySpawn Spawn(uint guid) => new(
        guid,
        new CreateObject.ServerPosition(
            SourceCell, 10f, 10f, SpawnHeight, 1f, 0f, 0f, 0f),
        SetupTableId: null,
        AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
        TextureChanges: Array.Empty<CreateObject.TextureChange>(),
        SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
        BasePaletteId: null,
        ObjScale: null,
        Name: "remote",
        ItemType: null,
        MotionState: null,
        MotionTableId: 0x09000001u);

    private static PhysicsEngine FlatEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            SourceLandblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }

    private sealed class ServiceWindow : IRuntimeRemotePlacementServiceWindow
    {
        private readonly HashSet<uint> _within = [];

        internal void Allow(uint landblockId) =>
            _within.Add((landblockId & 0xFFFF0000u) | 0xFFFFu);

        public bool IsWithinServiceWindow(uint landblockId) =>
            _within.Contains((landblockId & 0xFFFF0000u) | 0xFFFFu);
    }

    private sealed class UnusedCollisionSource : IPreparedCollisionSource
    {
        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) => PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
