using System.Numerics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Content.Vfx;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityPresentationControllerTests
{
    [Fact]
    public void InitialVisibleObject_DoesNotPlayUnHide()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);

        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));

        Assert.Empty(fixture.TypedPlays);
        Assert.True(fixture.Entity.IsDrawVisible);
        Assert.Equal(1, fixture.Shadows.TotalRegistered);
    }

    [Fact]
    public void HiddenThenVisible_PlaysTypedEffectsMutatesChildrenAndRestoresShadow()
    {
        Fixture fixture = new(
            PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions);

        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));

        Assert.Equal(
            [(fixture.Entity.Id, LiveEntityPresentationController.HiddenScriptType, 1f)],
            fixture.TypedPlays);
        Assert.Equal([(Fixture.Guid, true)], fixture.ChildNoDraw);
        Assert.Equal([Fixture.Guid], fixture.InvalidTargets);
        Assert.Equal([fixture.Entity.Id], fixture.PartArrayEnterWorld);
        Assert.Equal([0], fixture.PartArrayShadowCounts);
        Assert.Equal(
            ["effect:76", "children:True", "part-array", "target"],
            fixture.PresentationOrder);
        Assert.Equal(0, fixture.Shadows.TotalRegistered);
        Assert.False(fixture.Entity.IsDrawVisible);

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(Fixture.Guid, 0u, 1, 2),
            out _,
            out _));
        Assert.True(fixture.Controller.OnStateAccepted(Fixture.Guid));

        Assert.Equal(
            [
                (fixture.Entity.Id, LiveEntityPresentationController.HiddenScriptType, 1f),
                (fixture.Entity.Id, LiveEntityPresentationController.UnHideScriptType, 1f),
            ],
            fixture.TypedPlays);
        Assert.Equal([(Fixture.Guid, true), (Fixture.Guid, false)], fixture.ChildNoDraw);
        Assert.Equal(
            [fixture.Entity.Id, fixture.Entity.Id],
            fixture.PartArrayEnterWorld);
        Assert.Equal([0, 0], fixture.PartArrayShadowCounts);
        Assert.Equal(
            [
                "effect:76", "children:True", "part-array", "target",
                "effect:75", "children:False", "part-array",
            ],
            fixture.PresentationOrder);
        Assert.Equal(1, fixture.Shadows.TotalRegistered);
        Assert.True(fixture.Entity.IsDrawVisible);
        Assert.Same(
            fixture.Entity,
            Assert.Single(fixture.Runtime.VisibleRecords).WorldEntity);
    }

    [Theory]
    [InlineData(0x50000456u, (ushort)3)]
    [InlineData(0x70000199u, (ushort)0)]
    public void CommittedChild_HiddenThenVisible_NeverInstallsShadowRow(
        uint parentGuid,
        ushort parentInstanceSequence)
    {
        Fixture fixture = new(
            PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions);

        var relation = new ParentAttachmentRelation(
            parentGuid,
            Fixture.Guid,
            ParentLocation: 0,
            PlacementId: 0,
            parentInstanceSequence,
            ChildPositionSequence: 1);
        fixture.Runtime.ParentAttachments.AcceptCreateObjectRelation(relation);
        Assert.True(fixture.Runtime.ParentAttachments.CommitProjection(relation));

        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        Assert.Equal(0, fixture.Shadows.TotalRegistered);

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(Fixture.Guid, 0u, 1, 2),
            out _,
            out _));
        Assert.True(fixture.Controller.OnStateAccepted(Fixture.Guid));

        Assert.Equal(0, fixture.Shadows.TotalRegistered);
        Assert.True(fixture.Entity.IsDrawVisible);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void SpellRecall_HiddenAndUnHide_RetireMagicTimelineThroughController()
    {
        const uint humanSetup = 0x02000001u;
        const uint humanMotionTable = 0x09000001u;
        const uint magic = 0x80000049u;
        const uint ready = 0x41000003u;
        const uint magicPortal = 0x40000038u;
        string datDir = @"C:\Turbine\Asheron's Call";
        if (!File.Exists(Path.Combine(datDir, "client_portal.dat")))
            throw new InvalidOperationException(
                "Lane=InstalledDat requires installed retail DATs; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        Setup setup = Assert.IsType<Setup>(dats.Get<Setup>(humanSetup));
        MotionTable table = Assert.IsType<MotionTable>(dats.Get<MotionTable>(humanMotionTable));
        var sequencer = new AnimationSequencer(
            setup,
            table,
            new RetailAnimationLoader(dats));
        var fixture = new Fixture(
            PhysicsStateFlags.ReportCollisions,
            _ => sequencer.Manager.HandleEnterWorld());
        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));

        sequencer.SetCycle(magic, ready);
        sequencer.PlayAction(magicPortal);
        Assert.False(sequencer.CurrentNodeDiag.IsLooping);

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Fixture.Guid,
                (uint)(PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions),
                1,
                2),
            out _,
            out _));
        Assert.True(fixture.Controller.OnStateAccepted(Fixture.Guid));
        Assert.True(sequencer.CurrentNodeDiag.IsLooping);
        Assert.Empty(sequencer.Manager.PendingAnimations);

        var unhideSequencer = new AnimationSequencer(
            setup,
            table,
            new RetailAnimationLoader(dats));
        var unhideFixture = new Fixture(
            PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions,
            _ => unhideSequencer.Manager.HandleEnterWorld());
        Assert.True(unhideFixture.Controller.OnLiveEntityReady(Fixture.Guid));
        unhideSequencer.SetCycle(magic, ready);
        unhideSequencer.PlayAction(magicPortal);
        Assert.False(unhideSequencer.CurrentNodeDiag.IsLooping);
        Assert.True(unhideFixture.Runtime.TryApplyState(
            new SetState.Parsed(Fixture.Guid, 0u, 1, 3),
            out _,
            out _));
        Assert.True(unhideFixture.Controller.OnStateAccepted(Fixture.Guid));
        Assert.True(unhideSequencer.CurrentNodeDiag.IsLooping);
        Assert.Empty(unhideSequencer.Manager.PendingAnimations);
        Assert.Equal([0], fixture.PartArrayShadowCounts);
        Assert.Equal([0, 0], unhideFixture.PartArrayShadowCounts);
        Assert.Equal(1, unhideFixture.Shadows.TotalRegistered);
    }

    [Fact]
    public void IdenticalOrStaleState_DoesNotReplayHiddenEffect()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);
        fixture.Controller.OnLiveEntityReady(Fixture.Guid);
        var hidden = new SetState.Parsed(
            Fixture.Guid,
            (uint)(PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions),
            1,
            2);

        Assert.True(fixture.Runtime.TryApplyState(hidden, out _, out _));
        fixture.Controller.OnStateAccepted(Fixture.Guid);
        Assert.False(fixture.Runtime.TryApplyState(hidden, out _, out _));
        fixture.Controller.OnStateAccepted(Fixture.Guid);

        Assert.Single(fixture.TypedPlays);
        Assert.Equal(LiveEntityPresentationController.HiddenScriptType,
            fixture.TypedPlays[0].Type);
        Assert.Equal([fixture.Entity.Id], fixture.PartArrayEnterWorld);
    }

    [Fact]
    public void StateAcceptedDuringHiddenEffect_DrainsAfterCompleteHiddenTail()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);
        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        bool nestedAccepted = false;
        fixture.OnTypedPlay = type =>
        {
            if (type != LiveEntityPresentationController.HiddenScriptType
                || nestedAccepted)
            {
                return;
            }

            nestedAccepted = true;
            Assert.True(fixture.Runtime.TryApplyState(
                new SetState.Parsed(
                    Fixture.Guid,
                    (uint)PhysicsStateFlags.ReportCollisions,
                    1,
                    3),
                out _));
            Assert.True(fixture.Controller.OnStateAccepted(Fixture.Guid));
        };

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Fixture.Guid,
                (uint)(PhysicsStateFlags.Hidden
                    | PhysicsStateFlags.ReportCollisions),
                1,
                2),
            out _));
        Assert.True(fixture.Controller.OnStateAccepted(Fixture.Guid));

        Assert.True(nestedAccepted);
        Assert.Equal(
            [
                "effect:76", "children:True", "part-array", "target",
                "effect:75", "children:False", "part-array",
            ],
            fixture.PresentationOrder);
        Assert.True(fixture.Entity.IsDrawVisible);
        Assert.Equal(1, fixture.Shadows.TotalRegistered);
    }

    [Fact]
    public void DeleteAndGuidReuseDuringHiddenEffect_StopsOldTransitionTail()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);
        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        Assert.True(fixture.Runtime.TryGetRecord(
            Fixture.Guid,
            out LiveEntityRecord oldRecord));
        WorldEntity? replacement = null;
        fixture.OnTypedPlay = type =>
        {
            if (type != LiveEntityPresentationController.HiddenScriptType
                || replacement is not null)
            {
                return;
            }

            fixture.Controller.Forget(oldRecord);
            fixture.Shadows.Deregister(fixture.Entity.Id);
            Assert.True(fixture.Runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(Fixture.Guid, 1),
                isLocalPlayer: false));
            fixture.Runtime.RegisterLiveEntity(Fixture.Spawn(
                PhysicsStateFlags.ReportCollisions,
                instanceSequence: 2));
            replacement = fixture.Runtime.MaterializeLiveEntity(
                Fixture.Guid,
                0x01010001u,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Fixture.Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(20f, 20f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                });
            Assert.NotNull(replacement);
            Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        };

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Fixture.Guid,
                (uint)(PhysicsStateFlags.Hidden
                    | PhysicsStateFlags.ReportCollisions),
                1,
                2),
            out _));
        Assert.False(fixture.Controller.OnStateAccepted(Fixture.Guid));

        Assert.NotNull(replacement);
        Assert.Equal(["effect:76"], fixture.PresentationOrder);
        Assert.Empty(fixture.ChildNoDraw);
        Assert.Empty(fixture.PartArrayEnterWorld);
        Assert.Empty(fixture.InvalidTargets);
        Assert.True(fixture.Runtime.TryGetRecord(Fixture.Guid, out var current));
        Assert.Same(replacement, current.WorldEntity);
        Assert.NotSame(oldRecord, current);
    }

    [Fact]
    public void UnHideWhileLandblockPending_RestoresShadowWhenProjectionReturns()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);
        fixture.Controller.OnLiveEntityReady(Fixture.Guid);

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Fixture.Guid,
                (uint)(PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions),
                1,
                2),
            out _,
            out _));
        fixture.Controller.OnStateAccepted(Fixture.Guid);
        Assert.Equal(0, fixture.Shadows.TotalRegistered);

        Assert.True(fixture.Runtime.RebucketLiveEntity(Fixture.Guid, 0x02020001u));
        Assert.False(fixture.Runtime.TryGetInteractionEligibleEntity(
            Fixture.Guid,
            out _));

        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(Fixture.Guid, 0u, 1, 3),
            out _,
            out _));
        fixture.Controller.OnStateAccepted(Fixture.Guid);
        Assert.Equal(0, fixture.Shadows.TotalRegistered);

        fixture.Spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.True(fixture.Runtime.TryGetInteractionEligibleEntity(
            Fixture.Guid,
            out _));
        Assert.Equal(1, fixture.Shadows.TotalRegistered);
    }

    [Fact]
    public void VisibleOrdinaryProjectionPending_SuspendsAndHydrationRestoresShadow()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);
        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        Assert.Equal(1, fixture.Shadows.TotalRegistered);

        Assert.True(fixture.Runtime.RebucketLiveEntity(
            Fixture.Guid,
            0x02020001u));

        Assert.False(fixture.Runtime.TryGetInteractionEligibleEntity(
            Fixture.Guid,
            out _));
        Assert.Equal(0, fixture.Shadows.TotalRegistered);
        Assert.Equal(1, fixture.Shadows.RetainedRegistrationCount);
        Assert.Equal(1, fixture.Shadows.SuspendedRegistrationCount);
        Assert.True(fixture.Controller.HasDeferredShadowRestore(Fixture.Guid));

        fixture.Spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.Same(
            fixture.Entity,
            Assert.Single(fixture.Runtime.VisibleRecords).WorldEntity);
        Assert.Equal(1, fixture.Shadows.TotalRegistered);
        Assert.Equal(0, fixture.Shadows.SuspendedRegistrationCount);
        Assert.False(fixture.Controller.HasDeferredShadowRestore(Fixture.Guid));
    }

    [Fact]
    public void PendingVisibilityCallback_GuidReuseCannotRestoreOldShadowOwner()
    {
        Fixture fixture = new(PhysicsStateFlags.ReportCollisions);
        Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        Assert.True(fixture.Runtime.TryGetRecord(
            Fixture.Guid,
            out LiveEntityRecord oldRecord));
        WorldEntity oldEntity = fixture.Entity;
        WorldEntity? replacement = null;

        fixture.Runtime.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (visible
                || replacement is not null
                || !ReferenceEquals(record, oldRecord))
            {
                return;
            }

            fixture.Controller.Forget(oldRecord);
            fixture.Shadows.Deregister(oldEntity.Id);
            Assert.True(fixture.Runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(Fixture.Guid, 1),
                isLocalPlayer: false));
            fixture.Runtime.RegisterLiveEntity(Fixture.Spawn(
                PhysicsStateFlags.ReportCollisions,
                instanceSequence: 2));
            replacement = fixture.Runtime.MaterializeLiveEntity(
                Fixture.Guid,
                0x01010001u,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Fixture.Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(20f, 20f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                });
            Assert.NotNull(replacement);
            fixture.Shadows.Register(
                replacement.Id,
                0x01000001u,
                replacement.Position,
                replacement.Rotation,
                1f,
                0f,
                0f,
                0x01010000u,
                state: (uint)PhysicsStateFlags.ReportCollisions,
                seedCellId: 0x01010001u);
            Assert.True(fixture.Controller.OnLiveEntityReady(Fixture.Guid));
        };

        Assert.False(fixture.Runtime.RebucketLiveEntity(
            Fixture.Guid,
            0x02020001u));

        Assert.NotNull(replacement);
        Assert.True(fixture.Runtime.TryGetRecord(Fixture.Guid, out var current));
        Assert.NotSame(oldRecord, current);
        Assert.Same(replacement, current.WorldEntity);
        Assert.Equal(1, fixture.Shadows.TotalRegistered);
        Assert.Equal(1, fixture.Shadows.RetainedRegistrationCount);
        Assert.Equal(0, fixture.Shadows.SuspendedRegistrationCount);
        Assert.Contains(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == replacement.Id);
        Assert.DoesNotContain(
            fixture.Shadows.AllEntriesForDebug(),
            entry => entry.EntityId == oldEntity.Id);
        Assert.False(fixture.Controller.HasDeferredShadowRestore(Fixture.Guid));
    }

    [Fact]
    public void InitialPendingProjection_ReadyBarrierSuspendsAndHydrationRestoresShadow()
    {
        const uint pendingCell = 0x02020001u;
        var spatial = new GpuWorldState();
        var shadows = new ShadowObjectRegistry();
        var runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        runtime.RegisterLiveEntity(Fixture.Spawn(
            PhysicsStateFlags.ReportCollisions));
        WorldEntity entity = Assert.IsType<WorldEntity>(
            runtime.MaterializeLiveEntity(
                Fixture.Guid,
                pendingCell,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Fixture.Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(10f, 10f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = pendingCell,
                }));
        shadows.Register(
            entity.Id,
            0x01000001u,
            entity.Position,
            entity.Rotation,
            1f,
            0f,
            0f,
            0x02020000u,
            state: (uint)PhysicsStateFlags.ReportCollisions,
            seedCellId: pendingCell);
        using var controller = new LiveEntityPresentationController(
            runtime,
            shadows,
            (_, _, _) => true,
            new LiveEntityPartArrayEnterWorldPort(_ => { }),
            liveCenter: () => (2, 2));

        Assert.True(controller.OnLiveEntityReady(Fixture.Guid));

        Assert.Equal(0, shadows.TotalRegistered);
        Assert.Equal(1, shadows.RetainedRegistrationCount);
        Assert.Equal(1, shadows.SuspendedRegistrationCount);
        Assert.True(controller.HasDeferredShadowRestore(Fixture.Guid));

        spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.Same(entity, Assert.Single(runtime.VisibleRecords).WorldEntity);
        Assert.Equal(1, shadows.TotalRegistered);
        Assert.Equal(0, shadows.SuspendedRegistrationCount);
        Assert.False(controller.HasDeferredShadowRestore(Fixture.Guid));
    }

    [Fact]
    public void InitialPendingHydrationCallback_GuidReuseCannotRestoreOldShadow()
    {
        const uint pendingCell = 0x02020001u;
        var spatial = new GpuWorldState();
        var shadows = new ShadowObjectRegistry();
        var runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        runtime.RegisterLiveEntity(Fixture.Spawn(
            PhysicsStateFlags.ReportCollisions));
        WorldEntity oldEntity = Assert.IsType<WorldEntity>(
            runtime.MaterializeLiveEntity(
                Fixture.Guid,
                pendingCell,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Fixture.Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(10f, 10f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = pendingCell,
                }));
        Assert.True(runtime.TryGetRecord(Fixture.Guid, out LiveEntityRecord oldRecord));
        shadows.Register(
            oldEntity.Id,
            0x01000001u,
            oldEntity.Position,
            oldEntity.Rotation,
            1f,
            0f,
            0f,
            0x02020000u,
            state: (uint)PhysicsStateFlags.ReportCollisions,
            seedCellId: pendingCell);

        LiveEntityPresentationController? controller = null;
        WorldEntity? replacement = null;
        runtime.ProjectionVisibilityChanged += (record, visible) =>
        {
            if (!visible
                || replacement is not null
                || !ReferenceEquals(record, oldRecord))
            {
                return;
            }

            controller!.Forget(oldRecord);
            shadows.Deregister(oldEntity.Id);
            Assert.True(runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(Fixture.Guid, 1),
                isLocalPlayer: false));
            runtime.RegisterLiveEntity(Fixture.Spawn(
                PhysicsStateFlags.ReportCollisions,
                instanceSequence: 2));
            replacement = runtime.MaterializeLiveEntity(
                Fixture.Guid,
                pendingCell,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Fixture.Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(20f, 20f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = pendingCell,
                });
            Assert.NotNull(replacement);
            shadows.Register(
                replacement.Id,
                0x01000001u,
                replacement.Position,
                replacement.Rotation,
                1f,
                0f,
                0f,
                0x02020000u,
                state: (uint)PhysicsStateFlags.ReportCollisions,
                seedCellId: pendingCell);
            Assert.True(controller.OnLiveEntityReady(Fixture.Guid));
        };
        controller = new LiveEntityPresentationController(
            runtime,
            shadows,
            (_, _, _) => true,
            new LiveEntityPartArrayEnterWorldPort(_ => { }),
            liveCenter: () => (2, 2));
        Assert.True(controller.OnLiveEntityReady(Fixture.Guid));
        Assert.Equal(1, shadows.SuspendedRegistrationCount);

        spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));

        Assert.NotNull(replacement);
        Assert.True(runtime.TryGetRecord(Fixture.Guid, out var current));
        Assert.NotSame(oldRecord, current);
        Assert.Same(replacement, current.WorldEntity);
        Assert.Equal(1, shadows.TotalRegistered);
        Assert.Equal(1, shadows.RetainedRegistrationCount);
        Assert.Equal(0, shadows.SuspendedRegistrationCount);
        Assert.Contains(
            shadows.AllEntriesForDebug(),
            entry => entry.EntityId == replacement.Id);
        Assert.DoesNotContain(
            shadows.AllEntriesForDebug(),
            entry => entry.EntityId == oldEntity.Id);
        Assert.False(controller.HasDeferredShadowRestore(Fixture.Guid));
        controller.Dispose();
    }


    private sealed class Fixture
    {
        public const uint Guid = 0x70000051u;
        public List<(uint Owner, uint Type, float Intensity)> TypedPlays { get; } = [];
        public List<(uint Parent, bool NoDraw)> ChildNoDraw { get; } = [];
        public List<uint> InvalidTargets { get; } = [];
        public List<uint> PartArrayEnterWorld { get; } = [];
        public List<int> PartArrayShadowCounts { get; } = [];
        public List<string> PresentationOrder { get; } = [];
        public GpuWorldState Spatial { get; }
        public LiveEntityRuntime Runtime { get; }
        public ShadowObjectRegistry Shadows { get; } = new();
        public WorldEntity Entity { get; }
        public LiveEntityPresentationController Controller { get; }
        public Action<uint>? OnTypedPlay { get; set; }

        public Fixture(
            PhysicsStateFlags initialState,
            Action<uint>? onPartArrayEnterWorld = null)
        {
            Spatial = new GpuWorldState();
            Spatial.AddLandblock(new LoadedLandblock(
                0x0101FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = LiveEntityRuntimeFixture.Create(
                Spatial,
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
            WorldSession.EntitySpawn spawn = Spawn(initialState);
            Runtime.RegisterLiveEntity(spawn);
            Entity = Runtime.MaterializeLiveEntity(
                Guid,
                0x01010001u,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = new Vector3(10f, 10f, 5f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                })!;
            Shadows.Register(
                Entity.Id,
                0x01000001u,
                Entity.Position,
                Entity.Rotation,
                1f,
                0f,
                0f,
                0x01010000u,
                state: (uint)initialState,
                seedCellId: 0x01010001u);

            Controller = new LiveEntityPresentationController(
                Runtime,
                Shadows,
                (owner, type, intensity) =>
                {
                    TypedPlays.Add((owner, type, intensity));
                    PresentationOrder.Add($"effect:{type:X2}");
                    OnTypedPlay?.Invoke(type);
                    return true;
                },
                new LiveEntityPartArrayEnterWorldPort(localEntityId =>
                {
                    PartArrayEnterWorld.Add(localEntityId);
                    PartArrayShadowCounts.Add(Shadows.TotalRegistered);
                    PresentationOrder.Add("part-array");
                    onPartArrayEnterWorld?.Invoke(localEntityId);
                }),
                (parent, noDraw) =>
                {
                    ChildNoDraw.Add((parent, noDraw));
                    PresentationOrder.Add($"children:{noDraw}");
                },
                guid =>
                {
                    InvalidTargets.Add(guid);
                    PresentationOrder.Add("target");
                },
                () => (1, 1),
                onShadowRestored: null);
        }

        internal static WorldSession.EntitySpawn Spawn(
            PhysicsStateFlags state,
            ushort instanceSequence = 1)
        {
            var position = new CreateObject.ServerPosition(
                0x01010001u, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
            // C3c: residence admission requires the nested block's Instance
            // timestamp to agree with the flattened InstanceSequence.
            var timestamps = new PhysicsTimestamps(
                1, 1, 1, 1, 0, 1, 0, 1, instanceSequence);
            var physics = new PhysicsSpawnData(
                RawState: (uint)state,
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
                MotionTableId: 0x09000001u,
                PhysicsState: (uint)state,
                InstanceSequence: instanceSequence,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1,
                Physics: physics);
        }
    }
}
