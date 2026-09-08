using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeSteadyStatePositionMergeTests
{
    private const uint Landblock = 0xA9C60000u;
    private const uint LandblockSentinel = Landblock | 0xFFFFu;
    private const uint Cell = Landblock | 0x0001u;
    private const uint OtherCell = Landblock | 0x0002u;

    [Fact]
    public void AcceptedPosition_WithholdsTheWireCellAtTheMergeBoundary()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime);
        const uint guid = 0x80005001u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, 1)).Canonical!;
        Assert.Equal(Cell, canonical.FullCellId);

        Assert.True(lifetime.TryApplyPosition(
            PositionUpdate(guid, OtherCell, positionSequence: 2),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);

        Assert.Equal(Cell, canonical.FullCellId);
        Assert.NotEqual(OtherCell, canonical.FullCellId);
        Assert.Equal(Cell, timestamps.PreMergeCommittedCellId);

        // The POSE half of the merge is emphatically not withheld.
        Assert.Equal(
            OtherCell,
            canonical.Snapshot.Position!.Value.LandblockId);
        Assert.Equal(30f, canonical.Snapshot.Position!.Value.PositionX);
        Assert.Equal((ushort)2, canonical.Snapshot.PositionSequence);

        Assert.True(lifetime.CommitRebucket(
            canonical,
            OtherCell,
            LandblockSentinel));
        Assert.Equal(OtherCell, canonical.FullCellId);
    }

    [Theory]
    [InlineData(0x50005101u, 0x50005102u)]
    [InlineData(0x80005201u, 0x80005202u)]
    public void CellChangingAcceptedPosition_ConservesOneRebucketAndOneChildPropagation(
        uint parentGuid,
        uint childGuid)
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime);
        RuntimeEntityRecord parent =
            lifetime.RegisterEntity(Spawn(parentGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.Equal(Cell, child.FullCellId);

        ulong childSpatialBefore = child.SpatialAuthorityVersion;
        var deltas = new List<RuntimeEntityDelta>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new RecordingEntityObserver(deltas));

        Assert.True(lifetime.TryApplyPosition(
            PositionUpdate(parentGuid, OtherCell, positionSequence: 3),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out _));
        Assert.Equal(PositionTimestampDisposition.Apply, disposition);
        Assert.True(lifetime.CommitRebucket(parent, OtherCell, LandblockSentinel));

        Assert.Equal(OtherCell, parent.FullCellId);

        List<RuntimeEntityDelta> parentDeltas = deltas
            .Where(d => d.Entity.Identity.ServerGuid == parentGuid)
            .ToList();
        Assert.Equal(
            new[]
            {
                RuntimeEntityChange.Updated,
                RuntimeEntityChange.Rebucketed,
            },
            parentDeltas.Select(d => d.Change));
        Assert.Equal(Cell, parentDeltas[0].Entity.CellId);
        Assert.Equal(
            OtherCell,
            parentDeltas[0].Entity.Position!.Value.ObjCellId);
        Assert.Equal(OtherCell, parentDeltas[1].Entity.CellId);
        Assert.Equal(
            OtherCell,
            parentDeltas[1].Entity.Position!.Value.ObjCellId);

        Assert.Equal(OtherCell, child.FullCellId);
        Assert.Equal(childSpatialBefore + 1UL, child.SpatialAuthorityVersion);
    }

    private sealed class RecordingEntityObserver(List<RuntimeEntityDelta> destination)
        : IRuntimeEntityObjectObserver
    {
        public void OnEntity(in RuntimeEntityDelta delta) => destination.Add(delta);

        public void OnInventory(in RuntimeInventoryDelta delta)
        {
        }
    }

    private static RuntimeEntityObjectLifetime EngineLifetime()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return new RuntimeEntityObjectLifetime(engine);
    }

    private static void Bind(RuntimeEntityObjectLifetime lifetime)
    {
        var token = new RuntimeGenerationToken(1UL);
        lifetime.BindEventContext(() => token, static () => 1UL);
    }

    private static bool CommitAttachment(
        RuntimeEntityObjectLifetime lifetime,
        uint parentGuid,
        ushort parentInstance,
        uint childGuid,
        ushort childPositionSequence)
    {
        var relation = new ParentAttachmentRelation(
            parentGuid,
            childGuid,
            ParentLocation: 0u,
            PlacementId: 0u,
            parentInstance,
            childPositionSequence);
        lifetime.Entities.ParentAttachments.AcceptCreateObjectRelation(relation);
        var update = new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            0u,
            0u,
            parentInstance,
            childPositionSequence);
        if (!lifetime.TryApplyParent(update, acknowledgeProjection: null, out _))
            return false;
        if (!lifetime.TryCommitParent(relation, acknowledgeProjection: null, out _))
            return false;
        if (!lifetime.Entities.ParentAttachments.CommitProjection(relation))
            return false;
        if (!lifetime.Entities.TryGetActive(
                childGuid,
                out RuntimeEntityRecord canonical))
        {
            return false;
        }

        return lifetime.CommitAcceptedParentCellless(
            canonical,
            canonical.PositionAuthorityVersion,
            acknowledgeProjection: null);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        uint guid,
        uint cell,
        ushort positionSequence) =>
        new(
            guid,
            new CreateObject.ServerPosition(cell, 30f, 20f, 7f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: 0,
            ForcePositionSequence: 0);

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort incarnation,
        bool includePosition = true)
    {
        CreateObject.ServerPosition? position = includePosition
            ? new CreateObject.ServerPosition(Cell, 10f, 20f, 7f, 1f, 0f, 0f, 0f)
            : null;
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.Gravity,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: null,
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
            guid,
            position,
            SetupTableId: null,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            null,
            PhysicsState: (uint)PhysicsStateFlags.Gravity,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
