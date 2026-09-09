using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeEntityChildCellPropagationTests
{
    private const uint Landblock = 0xA9C60000u;
    private const uint Cell = Landblock | 0x0001u;

    [Fact]
    public void Attach_ParentCelled_ChildEndsAtParentCellAndStaysSuspended()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040001u;
        const uint childGuid = 0x70040002u;
        RuntimeEntityRecord parent =
            lifetime.RegisterEntity(Spawn(parentGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));

        var deltas = new List<RuntimeEntityDelta>();
        using IDisposable subscription = lifetime.Events.Subscribe(
            new RecordingEntityObserver(deltas));

        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));

        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.Equal(parent.FullCellId, child.FullCellId);
        Assert.Equal(parent.CanonicalLandblockId, child.CanonicalLandblockId);
        Assert.NotEqual(0u, child.FullCellId);
        Assert.False(child.ObjectClock.IsActive);
        Assert.Null(child.Snapshot.Position);

        RuntimeEntityDelta withdrawn = Assert.Single(
            deltas,
            d => d.Change is RuntimeEntityChange.Withdrawn
                && d.Entity.Identity.ServerGuid == childGuid);
        Assert.Equal(parent.FullCellId, withdrawn.Entity.CellId);
        Assert.NotEqual(0u, withdrawn.Entity.CellId);
    }

    private sealed class RecordingEntityObserver(List<RuntimeEntityDelta> destination)
        : IRuntimeEntityObjectObserver
    {
        public void OnEntity(in RuntimeEntityDelta delta) => destination.Add(delta);
        public void OnInventory(in RuntimeInventoryDelta delta)
        {
        }
    }

    [Fact]
    public void Attach_ParentCellless_ChildStaysCellless_ThenLaterParentCellCommitRecellsViaPropagation()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040101u;
        const uint childGuid = 0x70040102u;
        lifetime.RegisterEntity(Spawn(parentGuid, 1, includePosition: false));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));

        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));

        Assert.True(lifetime.Entities.TryGetActive(
            parentGuid, out RuntimeEntityRecord parent));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.Equal(0u, parent.FullCellId);
        Assert.Equal(0u, child.FullCellId);

        Assert.True(lifetime.CommitRebucket(parent, Cell, Landblock | 0xFFFFu));
        Assert.Equal(Cell, child.FullCellId);
    }

    [Fact]
    public void PropagationChokepoint_RecursesThroughGrandchildAndIsIdempotentOnASameCellCommit()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040201u;
        const uint childGuid = 0x70040202u;
        const uint grandchildGuid = 0x70040203u;
        RuntimeEntityRecord parent =
            lifetime.RegisterEntity(Spawn(parentGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        lifetime.RegisterEntity(Spawn(grandchildGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(CommitAttachment(lifetime, childGuid, 1, grandchildGuid, 2));

        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.Entities.TryGetActive(
            grandchildGuid, out RuntimeEntityRecord grandchild));
        Assert.Equal(parent.FullCellId, child.FullCellId);
        Assert.Equal(parent.FullCellId, grandchild.FullCellId);

        uint newCell = parent.FullCellId + 0x0002u;
        uint newLandblock = (newCell & 0xFFFF0000u) | 0xFFFFu;
        Assert.True(lifetime.CommitRebucket(parent, newCell, newLandblock));

        Assert.Equal(newCell, child.FullCellId);
        // Unbounded-depth recursion: the grandchild follows too.
        Assert.Equal(newCell, grandchild.FullCellId);

        ulong childVersionBefore = child.SpatialAuthorityVersion;
        ulong grandchildVersionBefore = grandchild.SpatialAuthorityVersion;
        Assert.True(lifetime.CommitRebucket(parent, newCell, newLandblock));
        Assert.Equal(childVersionBefore, child.SpatialAuthorityVersion);
        Assert.Equal(grandchildVersionBefore, grandchild.SpatialAuthorityVersion);
    }

    [Fact]
    public void PropagationChokepoint_TerminatesAHostileTwoCycleInsteadOfLoopingForever()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint aGuid = 0x70040301u;
        const uint bGuid = 0x70040302u;
        RuntimeEntityRecord a =
            lifetime.RegisterEntity(Spawn(aGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(bGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, aGuid, 1, bGuid, 2));
        Assert.True(CommitAttachment(lifetime, bGuid, 1, aGuid, 2));

        Assert.True(lifetime.Entities.TryGetActive(bGuid, out RuntimeEntityRecord b));
        uint newCell = a.FullCellId + 0x0002u;

        Assert.True(lifetime.CommitRebucket(a, newCell, (newCell & 0xFFFF0000u) | 0xFFFFu));
        Assert.Equal(newCell, a.FullCellId);
        Assert.Equal(newCell, b.FullCellId);
    }

    [Fact]
    public void RefreshSnapshot_WireMergeCellChange_PropagatesToCommittedChild()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040401u;
        const uint childGuid = 0x70040402u;
        RuntimeEntityRecord parent =
            lifetime.RegisterEntity(Spawn(parentGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.Equal(parent.FullCellId, child.FullCellId);

        WorldSession.EntitySpawn moved = parent.Snapshot with
        {
            Position = parent.Snapshot.Position!.Value with
            {
                LandblockId = parent.Snapshot.Position!.Value.LandblockId + 0x0002u,
            },
        };
        lifetime.Entities.RefreshSnapshot(parent, moved, refreshPosition: true);

        Assert.NotEqual(0u, parent.FullCellId);
        Assert.Equal(parent.FullCellId, child.FullCellId);
    }

    [Fact]
    public void P1_SnapshotMutationOfACommittedChild_LeavesItsCanonicalCellTrackingTheParent()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040501u;
        const uint childGuid = 0x70040502u;
        RuntimeEntityRecord parent =
            lifetime.RegisterEntity(Spawn(parentGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        uint expectedCell = parent.FullCellId;

        var objDesc = new ObjDescEvent.Parsed(
            childGuid,
            new CreateObject.ModelData(
                BasePaletteId: null,
                SubPalettes: [],
                TextureChanges: [],
                AnimPartChanges: []),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(lifetime.TryApplyObjDesc(
            objDesc, acknowledgeProjection: null, out _));

        Assert.Null(child.Snapshot.Position);
        Assert.Equal(expectedCell, child.FullCellId);
    }

    [Fact]
    public void Withdrawal_PickupOfParent_ZeroesCommittedChildrenRecursively()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040601u;
        const uint childGuid = 0x70040602u;
        const uint grandchildGuid = 0x70040603u;
        lifetime.RegisterEntity(Spawn(parentGuid, 1));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        lifetime.RegisterEntity(Spawn(grandchildGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(CommitAttachment(lifetime, childGuid, 1, grandchildGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.Entities.TryGetActive(
            grandchildGuid, out RuntimeEntityRecord grandchild));
        Assert.NotEqual(0u, child.FullCellId);
        Assert.NotEqual(0u, grandchild.FullCellId);

        Assert.True(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(parentGuid, InstanceSequence: 1, PositionSequence: 2),
            acknowledgeProjection: null,
            out _));

        Assert.Equal(0u, child.FullCellId);
        Assert.Equal(0u, grandchild.FullCellId);
    }

    [Theory]
    [InlineData(0x50000601u, (ushort)8)]
    [InlineData(0x70040609u, (ushort)0)]
    public void Withdrawal_PickupOfParent_ZeroesCommittedChildren_DualParentClass(
        uint parentGuid,
        ushort parentInstance)
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint childGuid = 0x7004060Au;
        lifetime.RegisterEntity(Spawn(parentGuid, parentInstance));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, parentInstance, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.NotEqual(0u, child.FullCellId);

        Assert.True(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(parentGuid, InstanceSequence: parentInstance, PositionSequence: 2),
            acknowledgeProjection: null,
            out _));

        Assert.Equal(0u, child.FullCellId);
    }

    [Fact]
    public void Withdrawal_CommitWithdrawalOfParent_ZeroesCommittedChildren()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040701u;
        const uint childGuid = 0x70040702u;
        RuntimeEntityRecord parent =
            lifetime.RegisterEntity(Spawn(parentGuid, 1)).Canonical!;
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.NotEqual(0u, child.FullCellId);

        Assert.True(lifetime.CommitWithdrawal(parent));

        Assert.Equal(0u, parent.FullCellId);
        Assert.Equal(0u, child.FullCellId);
    }

    [Fact]
    public void Delete_ZeroesChildrenBeforeRelationsAreTornDown_P7()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040801u;
        const uint childGuid = 0x70040802u;
        const uint grandchildGuid = 0x70040803u;
        lifetime.RegisterEntity(Spawn(parentGuid, 1));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        lifetime.RegisterEntity(Spawn(grandchildGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(CommitAttachment(lifetime, childGuid, 1, grandchildGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.Entities.TryGetActive(
            grandchildGuid, out RuntimeEntityRecord grandchild));
        Assert.NotEqual(0u, child.FullCellId);
        Assert.NotEqual(0u, grandchild.FullCellId);

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(parentGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);

        Assert.Equal(0u, child.FullCellId);
        Assert.Equal(0u, grandchild.FullCellId);
        Assert.False(lifetime.Entities.ParentAttachments.HasCommittedParent(childGuid));
        Assert.True(lifetime.Entities.ParentAttachments.HasCommittedParent(grandchildGuid));
    }

    [Fact]
    public void EndGeneration_ReplacementParentGeneration_ZeroesFormerlyCommittedChildren()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040901u;
        const uint childGuid = 0x70040902u;
        lifetime.RegisterEntity(Spawn(parentGuid, 1));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.NotEqual(0u, child.FullCellId);

        lifetime.RegisterEntity(Spawn(parentGuid, 2));

        Assert.Equal(0u, child.FullCellId);
        Assert.False(lifetime.Entities.ParentAttachments.HasCommittedParent(childGuid));
    }

    [Fact]
    public void PickupOfTheChildItself_D7_RelationGoneCellZeroClockSuspendedAndOwnChildrenAlsoCellless()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040A01u;
        const uint childGuid = 0x70040A02u;
        const uint grandchildGuid = 0x70040A03u;
        lifetime.RegisterEntity(Spawn(parentGuid, 1));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));
        lifetime.RegisterEntity(Spawn(grandchildGuid, 1, includePosition: false));
        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(CommitAttachment(lifetime, childGuid, 1, grandchildGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            childGuid, out RuntimeEntityRecord child));
        Assert.True(lifetime.Entities.TryGetActive(
            grandchildGuid, out RuntimeEntityRecord grandchild));

        Assert.True(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(childGuid, InstanceSequence: 1, PositionSequence: 3),
            acknowledgeProjection: null,
            out _));

        Assert.False(lifetime.Entities.ParentAttachments.HasCommittedParent(childGuid));
        Assert.Equal(0u, child.FullCellId);
        Assert.False(child.ObjectClock.IsActive);
        Assert.Equal(0u, grandchild.FullCellId);
    }

    [Fact]
    public void NeverArmPartition_D8_RouteSevenEventsNeverEngagePlacementOrParkMachinery()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const uint parentGuid = 0x70040B01u;
        const uint childGuid = 0x70040B02u;
        lifetime.RegisterEntity(Spawn(parentGuid, 1));
        lifetime.RegisterEntity(Spawn(childGuid, 1, includePosition: false));

        int placementOpsBefore =
            lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount;

        Assert.True(CommitAttachment(lifetime, parentGuid, 1, childGuid, 2));
        Assert.True(lifetime.Entities.TryGetActive(
            parentGuid, out RuntimeEntityRecord parent));
        for (int i = 0; i < 5; i++)
        {
            uint nextCell = parent.FullCellId + 0x0002u;
            Assert.True(lifetime.CommitRebucket(
                parent, nextCell, (nextCell & 0xFFFF0000u) | 0xFFFFu));
        }
        Assert.True(lifetime.TryApplyPickup(
            new PickupEvent.Parsed(childGuid, InstanceSequence: 1, PositionSequence: 3),
            acknowledgeProjection: null,
            out _));
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(parentGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);

        Assert.Equal(
            placementOpsBefore,
            lifetime.Physics.SetPosition.CaptureOwnership().ActiveOperationCount);
        Assert.Equal(
            0,
            lifetime.CaptureOwnership().CommittedParentRelationCount);
    }

    [Fact]
    public void PropagationChokepoint_DeepChain_FullyPropagatesOnBothWriteAndWithdraw()
    {
        using RuntimeEntityObjectLifetime lifetime = EngineLifetime();
        Bind(lifetime, 1UL);
        const int chainLength = 200; // well beyond the retired 64-level cap
        var guids = new uint[chainLength + 1];
        for (int i = 0; i <= chainLength; i++)
            guids[i] = 0x70050000u + (uint)i;

        lifetime.RegisterEntity(Spawn(guids[0], 1));
        for (int i = 1; i <= chainLength; i++)
            lifetime.RegisterEntity(Spawn(guids[i], 1, includePosition: false));
        for (int i = 0; i < chainLength; i++)
        {
            Assert.True(CommitAttachment(
                lifetime, guids[i], 1, guids[i + 1], childPositionSequence: 2));
        }

        Assert.True(lifetime.Entities.TryGetActive(
            guids[0], out RuntimeEntityRecord root));
        uint originalCell = root.FullCellId;
        Assert.True(lifetime.Entities.TryGetActive(
            guids[chainLength], out RuntimeEntityRecord tailBeforeCrossing));
        Assert.Equal(originalCell, tailBeforeCrossing.FullCellId);
        Assert.NotEqual(0u, originalCell);

        uint newCell = originalCell + 0x0002u;
        Assert.True(lifetime.CommitRebucket(
            root, newCell, (newCell & 0xFFFF0000u) | 0xFFFFu));
        for (int i = 0; i <= chainLength; i++)
        {
            Assert.True(lifetime.Entities.TryGetActive(
                guids[i], out RuntimeEntityRecord record));
            Assert.Equal(newCell, record.FullCellId);
        }

        Assert.True(lifetime.CommitWithdrawal(root));
        for (int i = 0; i <= chainLength; i++)
        {
            Assert.True(lifetime.Entities.TryGetActive(
                guids[i], out RuntimeEntityRecord record));
            Assert.Equal(0u, record.FullCellId);
        }
    }

    // ---------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------

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

    private static void Bind(RuntimeEntityObjectLifetime lifetime, ulong generation)
    {
        var token = new RuntimeGenerationToken(generation);
        lifetime.BindEventContext(() => token, static () => 1UL);
    }

    private static bool CommitAttachment(
        RuntimeEntityObjectLifetime lifetime,
        uint parentGuid,
        ushort parentInstance,
        uint childGuid,
        ushort childPositionSequence,
        uint parentLocation = 0u,
        uint placementId = 0u)
    {
        var relation = new ParentAttachmentRelation(
            parentGuid,
            childGuid,
            parentLocation,
            placementId,
            parentInstance,
            childPositionSequence);
        lifetime.Entities.ParentAttachments.AcceptCreateObjectRelation(relation);
        var update = new ParentEvent.Parsed(
            parentGuid,
            childGuid,
            parentLocation,
            placementId,
            parentInstance,
            childPositionSequence);
        if (!lifetime.TryApplyParent(update, acknowledgeProjection: null, out _))
            return false;
        if (!lifetime.TryCommitParent(relation, acknowledgeProjection: null, out _))
            return false;
        if (!lifetime.Entities.ParentAttachments.CommitProjection(relation))
            return false;
        if (!lifetime.Entities.TryGetActive(childGuid, out RuntimeEntityRecord canonical))
            return false;
        return lifetime.CommitAcceptedParentCellless(
            canonical,
            canonical.PositionAuthorityVersion,
            acknowledgeProjection: null);
    }

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
            BasePaletteId: null,
            ObjScale: null,
            Name: "route-7 fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: physics.RawState,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
