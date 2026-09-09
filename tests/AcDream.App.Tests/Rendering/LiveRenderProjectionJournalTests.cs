using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Scene.Arch;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering;

public sealed class LiveRenderProjectionJournalTests
{
    private const uint LandblockId = 0x1234FFFFu;
    private const uint CellId = 0x12340100u;
    private const uint Guid = 0x80000001u;

    [Fact]
    public void EntityReady_RegistersExactRootOnceAfterResourcesCommit()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 7);
        LiveEntityReadyCandidate candidate =
            LiveEntityReadyCandidate.Capture(record);

        Assert.True(harness.Projections.OnEntityReady(candidate));
        Assert.True(harness.Projections.OnEntityReady(candidate));

        Assert.Equal(1, harness.Journal.Count);
        Assert.Equal(1, harness.Projections.ProjectionCount);
        RenderProjectionDelta registered = harness.Journal.Pending[0];
        Assert.Equal(RenderProjectionDeltaKind.Register, registered.Kind);
        Assert.Equal(RenderProjectionClass.LiveDynamicRoot,
            registered.Record.ProjectionClass);
        Assert.Equal(record.LocalEntityId, registered.Record.Source.LocalEntityId);
        Assert.Equal(Guid, registered.Record.Source.ServerGuid);
        Assert.Equal(LandblockId, registered.Record.Residency.OwnerLandblockId);
        Assert.True((registered.Record.Flags & RenderProjectionFlags.Draw) != 0);
    }

    [Fact]
    public void EntityReady_RetainsExactLocalPlayerIdentityAtRenderPublication()
    {
        Harness harness = CreateHarness(loadLandblock: true, localPlayerGuid: Guid);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 12);

        Assert.True(harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record)));

        Assert.Equal(
            RenderCasterIdentityKind.LocalPlayer,
            Assert.Single(harness.Journal.Pending.ToArray())
                .Record.EntityPayload.CasterIdentity);
    }

    [Theory]
    [InlineData(0x80000001u, null, null, 0x80000001u, (byte)RenderCasterIdentityKind.LocalPlayer)]
    [InlineData(0x80000001u, null, 0x8u, 0u, (byte)RenderCasterIdentityKind.RemotePlayer)]
    [InlineData(0x50000001u, null, null, 0u, (byte)RenderCasterIdentityKind.RemotePlayer)]
    [InlineData(0x80000001u, (uint)ItemType.Creature, null, 0u, (byte)RenderCasterIdentityKind.NonPlayerCreature)]
    [InlineData(0x80000001u, (uint)ItemType.Misc, null, 0u, (byte)RenderCasterIdentityKind.OtherLiveDynamic)]
    public void CasterIdentityClassifier_UsesOnlyAuthoritativeSpawnFacts(
        uint serverGuid,
        uint? itemType,
        uint? objectDescriptionFlags,
        uint localPlayerGuid,
        byte expected)
    {
        Assert.Equal(
            (RenderCasterIdentityKind)expected,
            RenderCasterIdentityClassifier.Classify(
                serverGuid,
                itemType,
                objectDescriptionFlags,
                localPlayerGuid));
    }

    [Fact]
    public void PendingToLoadedAndLoadedToLoaded_RebucketWithoutLogicalRecreate()
    {
        Harness harness = CreateHarness(loadLandblock: false);
        BindVisibility(harness);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 1);
        Assert.False(record.IsSpatiallyVisible);
        harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record));
        RenderOwnerIncarnation incarnation =
            harness.Journal.Pending[0].Record.OwnerIncarnation;

        Load(harness.State, LandblockId);

        Assert.True(record.IsSpatiallyVisible);
        Assert.Equal(3, harness.Journal.Count);
        Assert.Equal(
            RenderProjectionDeltaKind.UpdateTransform,
            harness.Journal.Pending[1].Kind);
        Assert.Equal(
            RenderProjectionDeltaKind.UpdateFlags,
            harness.Journal.Pending[2].Kind);

        const uint secondLandblock = 0x1235FFFFu;
        const uint secondCell = 0x12350100u;
        Load(harness.State, secondLandblock);
        Assert.True(harness.Runtime.RebucketLiveEntity(Guid, secondCell));
        harness.Projections.SynchronizeActiveSources();

        Assert.Equal(5, harness.Journal.Count);
        Assert.Equal(
            RenderProjectionDeltaKind.UpdateTransform,
            harness.Journal.Pending[3].Kind);
        Assert.Equal(
            RenderProjectionDeltaKind.Rebucket,
            harness.Journal.Pending[4].Kind);
        Assert.Equal(
            incarnation,
            harness.Journal.Pending[4].Record.OwnerIncarnation);
        Assert.Equal(1, harness.Projections.ProjectionCount);
    }

    [Fact]
    public void SynchronizeActiveSources_MirrorsFinalTransformAppearanceAndHiddenFlags()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 2);
        harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record));
        harness.Journal.DrainTo(harness.Scene);
        WorldEntity entity = record.WorldEntity!;

        entity.SetPosition(new Vector3(20, 30, 40));
        entity.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        entity.ApplyAppearance(
            [new MeshRef(0x01010077u, Matrix4x4.Identity)],
            new PaletteOverride(0x04000001u, []),
            [new PartOverride(0, 0x01010077u)]);
        entity.IsDrawVisible = false;

        harness.Projections.SynchronizeActiveSources();

        Assert.Equal(
            [
                RenderProjectionDeltaKind.UpdateTransform,
                RenderProjectionDeltaKind.UpdateAppearance,
                RenderProjectionDeltaKind.UpdateFlags,
            ],
            harness.Journal.Pending.ToArray()
                .Select(delta => delta.Kind)
                .ToArray());
        harness.Journal.DrainTo(harness.Scene);
        Assert.True(harness.Scene.OpenQuery().TryGet(
            LiveRenderProjectionJournal.ProjectionId(entity.Id),
            out var projected));
        Assert.Equal(entity.Position, projected.Transform.Position);
        Assert.True((projected.Flags & RenderProjectionFlags.Hidden) != 0);
        Assert.True((projected.Flags & RenderProjectionFlags.Draw) == 0);
        Assert.Equal(1, projected.MeshSet.MeshCount);
    }

    [Fact]
    public void EquippedPoseAndRemoval_UseSameIncarnationWithoutGuidOwnershipMap()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(
            harness,
            Guid,
            instance: 3,
            projectionKind: LiveEntityProjectionKind.Attached);
        harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record));
        RenderOwnerIncarnation incarnation =
            harness.Journal.Pending[0].Record.OwnerIncarnation;
        harness.Journal.DrainTo(harness.Scene);

        record.WorldEntity!.SetPosition(new Vector3(9, 8, 7));
        harness.Projections.OnProjectionPoseReady(Guid);

        Assert.Single(harness.Journal.Pending.ToArray());
        Assert.Equal(
            RenderProjectionDeltaKind.UpdateTransform,
            harness.Journal.Pending[0].Kind);
        Assert.Equal(
            incarnation,
            harness.Journal.Pending[0].Record.OwnerIncarnation);
        Assert.True(harness.Runtime.WithdrawLiveEntityProjection(Guid));
        harness.Projections.OnProjectionRemoved(record);
        harness.Projections.OnProjectionRemoved(record);
        Assert.Equal(2, harness.Journal.Count);
        Assert.Equal(
            RenderProjectionDeltaKind.UpdateFlags,
            harness.Journal.Pending[1].Kind);
        Assert.Equal(1, harness.Projections.ProjectionCount);
    }

    [Fact]
    public void GenerationReplacement_UnregistersOldAndRejectsStaleReadyCandidate()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord first = Materialize(harness, Guid, instance: 4);
        LiveEntityReadyCandidate stale = LiveEntityReadyCandidate.Capture(first);
        harness.Projections.OnEntityReady(stale);
        uint firstLocalId = first.WorldEntity!.Id;
        harness.Journal.DrainTo(harness.Scene);

        LiveEntityRecord second = harness.Runtime.RegisterAndMaterializeProjection(
            Spawn(Guid, instance: 5),
            localId => Entity(localId, Guid));
        WorldEntity secondEntity = Assert.IsType<WorldEntity>(second.WorldEntity);
        second.InitialHydrationCompleted = true;
        LiveEntityReadyCandidate current = LiveEntityReadyCandidate.Capture(second);
        Assert.True(harness.Projections.OnEntityReady(current));
        Assert.False(harness.Projections.OnEntityReady(stale));

        Assert.Equal(2, harness.Journal.Count);
        Assert.Equal(
            [
                RenderProjectionDeltaKind.Unregister,
                RenderProjectionDeltaKind.Register,
            ],
            harness.Journal.Pending.ToArray()
                .Select(delta => delta.Kind)
                .ToArray());
        Assert.NotEqual(firstLocalId, secondEntity.Id);
        Assert.Equal(1, harness.Projections.ProjectionCount);
        harness.Journal.DrainTo(harness.Scene);
        Assert.False(harness.Scene.OpenQuery().TryGet(
            LiveRenderProjectionJournal.ProjectionId(firstLocalId),
            out _));
        Assert.True(harness.Scene.OpenQuery().TryGet(
            LiveRenderProjectionJournal.ProjectionId(secondEntity.Id),
            out _));
    }

    [Fact]
    public void DuplicateCreateAndReady_DoNotRegisterProjectionTwice()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 5);
        harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record));
        harness.Journal.DrainTo(harness.Scene);

        LiveEntityRegistrationResult duplicate =
            harness.Runtime.RegisterLiveEntity(Spawn(Guid, instance: 5));
        Assert.Same(record, duplicate.Projection);
        Assert.True(harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record)));

        Assert.Equal(0, harness.Journal.Count);
        Assert.Equal(1, harness.Projections.ProjectionCount);
        Assert.Equal(1, harness.Scene.Counts.Total);
    }

    [Fact]
    public void SessionClear_UnregistersEveryTrackedProjectionExactlyOnce()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 6);
        harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record));

        harness.Runtime.Clear();

        Assert.Equal(2, harness.Journal.Count);
        Assert.Equal(
            RenderProjectionDeltaKind.Unregister,
            harness.Journal.Pending[1].Kind);
        Assert.Equal(0, harness.Projections.ProjectionCount);
        Assert.Equal(0, harness.Runtime.Count);
    }

    [Fact]
    public void ResetTracking_DropsDerivedSourcesWithoutTouchingGameplayRuntime()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 7);
        harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record));

        harness.Projections.ResetTracking();

        Assert.Equal(0, harness.Projections.ProjectionCount);
        Assert.Equal(1, harness.Runtime.Count);
        Assert.Same(record, harness.Runtime.Records.Single());
    }

    [Fact]
    public void SynchronizeActiveSources_RecoversCurrentRootFromCanonicalWorkset()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(harness, Guid, instance: 8);
        Assert.True(harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record)));
        harness.Journal.DrainTo(harness.Scene);
        harness.Projections.ResetTracking();

        harness.Projections.SynchronizeActiveSources();

        RenderProjectionDelta recovered =
            Assert.Single(harness.Journal.Pending.ToArray());
        Assert.Equal(RenderProjectionDeltaKind.Register, recovered.Kind);
        Assert.Equal(
            RenderProjectionClass.LiveDynamicRoot,
            recovered.Record.ProjectionClass);
        Assert.Equal(record.LocalEntityId, recovered.Record.Source.LocalEntityId);
    }

    [Fact]
    public void SynchronizeActiveSources_DoesNotReactivateWithdrawnAttachment()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(
            harness,
            Guid,
            instance: 9,
            projectionKind: LiveEntityProjectionKind.Attached);
        Assert.True(harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record)));
        harness.Journal.DrainTo(harness.Scene);
        Assert.True(harness.Runtime.WithdrawLiveEntityProjection(Guid));
        harness.Projections.OnProjectionRemoved(record);
        harness.Journal.DrainTo(harness.Scene);

        harness.Projections.SynchronizeActiveSources();

        Assert.Equal(0, harness.Journal.Count);
        Assert.Equal(1, harness.Projections.ProjectionCount);
        Assert.True(harness.Scene.OpenQuery().TryGet(
            LiveRenderProjectionJournal.ProjectionId(record.WorldEntity!.Id),
            out RenderProjectionRecord inactive));
        Assert.True((inactive.Flags & RenderProjectionFlags.Draw) == 0);
    }

    [Fact]
    public void ProjectionPoseReady_RecoversCurrentAttachmentAfterPresentationRemoval()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        LiveEntityRecord record = Materialize(
            harness,
            Guid,
            instance: 10,
            projectionKind: LiveEntityProjectionKind.Attached);
        Assert.True(harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record)));
        harness.Journal.DrainTo(harness.Scene);
        Assert.True(harness.Runtime.WithdrawLiveEntityProjection(Guid));
        harness.Projections.OnProjectionRemoved(record);
        harness.Journal.DrainTo(harness.Scene);
        Assert.True(harness.Runtime.RebucketLiveEntity(Guid, CellId));

        harness.Projections.OnProjectionPoseReady(record.ServerGuid);

        RenderProjectionDelta recovered = Assert.Single(
            harness.Journal.Pending.ToArray(),
            delta => delta.Kind is RenderProjectionDeltaKind.UpdateFlags);
        Assert.Equal(record.LocalEntityId, recovered.Record.Source.LocalEntityId);
    }

    [Fact]
    public void WithdrawnAttachment_LogicalTeardownUnregistersRetainedIdentity()
    {
        Harness harness = CreateHarness(loadLandblock: true);
        const ushort instance = 11;
        LiveEntityRecord record = Materialize(
            harness,
            Guid,
            instance,
            projectionKind: LiveEntityProjectionKind.Attached);
        Assert.True(harness.Projections.OnEntityReady(
            LiveEntityReadyCandidate.Capture(record)));
        harness.Journal.DrainTo(harness.Scene);
        Assert.True(harness.Runtime.WithdrawLiveEntityProjection(Guid));
        harness.Projections.OnProjectionRemoved(record);
        harness.Journal.DrainTo(harness.Scene);

        Assert.True(harness.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, instance),
            isLocalPlayer: false));

        RenderProjectionDelta removed =
            Assert.Single(harness.Journal.Pending.ToArray());
        Assert.Equal(RenderProjectionDeltaKind.Unregister, removed.Kind);
        Assert.Equal(0, harness.Projections.ProjectionCount);
    }

    private static Harness CreateHarness(
        bool loadLandblock,
        uint localPlayerGuid = 0)
    {
        var state = new GpuWorldState();
        if (loadLandblock)
            Load(state, LandblockId);

        ILiveRenderProjectionSink? sink = null;
        var resources = new DelegateLiveEntityResourceLifecycle(
            static _ => { },
            entity => sink?.OnResourceUnregister(entity));
        var runtime = LiveEntityRuntimeFixture.Create(state, resources);
        var journal = new RenderProjectionJournal(
            RenderSceneGeneration.FromRaw(1));
        var localPlayer = new LocalPlayerIdentityState
        {
            ServerGuid = localPlayerGuid,
        };
        var projections = new LiveRenderProjectionJournal(
            runtime,
            journal,
            new GpuWorldRenderTraversalOrderSource(state),
            localPlayer);
        sink = projections;
        var scene = new ArchRenderScene(RenderSceneGeneration.FromRaw(1));
        return new Harness(state, runtime, journal, projections, scene);
    }

    private static void BindVisibility(Harness harness) =>
        harness.Runtime.ProjectionVisibilityChanged +=
            harness.Projections.OnProjectionVisibilityChanged;

    private static LiveEntityRecord Materialize(
        Harness harness,
        uint guid,
        ushort instance,
        LiveEntityProjectionKind projectionKind =
            LiveEntityProjectionKind.World)
    {
        LiveEntityRecord record = harness.Runtime.RegisterAndMaterializeProjection(
            Spawn(guid, instance),
            localId => Entity(localId, guid),
            projectionKind);
        Assert.NotNull(record.WorldEntity);
        record.InitialHydrationCompleted = true;
        return record;
    }

    private static void Load(GpuWorldState state, uint landblockId) =>
        state.AddLandblock(new LoadedLandblock(
            landblockId,
            new LandBlock(),
            Array.Empty<WorldEntity>(),
            PhysicsDatBundle.Empty));

    private static WorldEntity Entity(uint localId, uint guid) => new()
    {
        Id = localId,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = new Vector3(1, 2, 3),
        Rotation = Quaternion.Identity,
        MeshRefs =
        [
            new MeshRef(0x01010001u, Matrix4x4.Identity),
        ],
        ParentCellId = CellId,
    };

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
    {
        var position = new CreateObject.ServerPosition(
            CellId,
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
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
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
            guid,
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
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed record Harness(
        GpuWorldState State,
        LiveEntityRuntime Runtime,
        RenderProjectionJournal Journal,
        LiveRenderProjectionJournal Projections,
        ArchRenderScene Scene);
}
