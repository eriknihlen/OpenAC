using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailStaticAnimatingObjectSchedulerTests
{
    private const uint OwnerId = 0x7F000001u;
    private const uint AnimationId = 0x0300AA01u;
    private const uint GfxId = 0x0100AA01u;

    [Fact]
    public void DefaultAnimation_UsesSeparateWholeElapsedStaticWorkset()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        int hookCaptures = 0;
        int posePublishes = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => hookCaptures++,
            (_, _, _) => posePublishes++);
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity();
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));

        var animatedIds = new HashSet<uint>();
        scheduler.CopyAnimatedEntityIdsTo(animatedIds);
        Assert.Contains(OwnerId, animatedIds);

        scheduler.Tick(1f / 60f);
        scheduler.ProcessHooks();

        Assert.Equal(1, hookCaptures);
        Assert.Equal(1, posePublishes);
        Assert.Single(entity.MeshRefs);
        Assert.True(entity.MeshRefs[0].PartTransform.Translation.X > 0f);

        scheduler.Tick(2f);
        scheduler.ProcessHooks();
        Assert.Equal(2, hookCaptures);
        Assert.Equal(2, posePublishes);

        Matrix4x4 beforeDiscard = entity.MeshRefs[0].PartTransform;
        scheduler.Tick(2.01f);
        scheduler.ProcessHooks();
        Assert.Equal(beforeDiscard, entity.MeshRefs[0].PartTransform);
        Assert.Equal(2, hookCaptures);
        Assert.Equal(2, posePublishes);
    }

    [Fact]
    public void Rebind_RetainsSequencerAndPublishesIntoReplacementEntity()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => { },
            (_, _, _) => { });
        Setup setup = MakeSetup();
        WorldEntity first = MakeEntity();
        ScriptActivationInfo info = new(
            ScriptId: 0,
            PartTransforms: first.IndexedPartTransforms,
            PartAvailability: first.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true);
        scheduler.Register(first, info);
        scheduler.Tick(1f / 60f);
        float phaseBeforeRebind = first.MeshRefs[0].PartTransform.Translation.X;

        WorldEntity replacement = MakeEntity();
        replacement.Position = new Vector3(4f, 5f, 6f);
        scheduler.Rebind(replacement, info with
        {
            PartTransforms = replacement.IndexedPartTransforms,
            PartAvailability = replacement.IndexedPartAvailable,
        });
        scheduler.Tick(1f / 60f);

        Assert.Equal(1, scheduler.Count);
        Assert.True(
            replacement.MeshRefs[0].PartTransform.Translation.X > phaseBeforeRebind);
    }

    [Fact]
    public void ShortStaticAnimFrame_RetainsCompleteTrailingPartState()
    {
        const uint trailingGfx = GfxId;
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => { },
            (_, _, _) => { });
        Setup setup = MakeSetup();
        setup.Parts.Add(trailingGfx);
        setup.DefaultScale.Add(new Vector3(3f, 4f, 5f));
        Matrix4x4 visualRest = Matrix4x4.CreateScale(6f)
            * Matrix4x4.CreateTranslation(7f, 8f, 9f);
        Matrix4x4 rigidRest = Matrix4x4.CreateTranslation(10f, 11f, 12f);
        WorldEntity entity = MakeEntity();
        entity.MeshRefs =
        [
            entity.MeshRefs[0],
            new MeshRef(trailingGfx, visualRest),
        ];
        entity.SetIndexedPartPoses(
            [Matrix4x4.Identity, rigidRest],
            [true, true]);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));

        scheduler.Tick(1f / 60f);

        Assert.Equal(visualRest, entity.MeshRefs[1].PartTransform);
        Assert.Equal(rigidRest, entity.IndexedPartTransforms[1]);
    }

    [Fact]
    public void SubEpsilonElapsed_IsDiscardedInsteadOfAccumulated()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        int hookCaptures = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => hookCaptures++,
            (_, _, _) => { });
        WorldEntity entity = MakeEntity();
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: MakeSetup(),
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));

        scheduler.Tick(0.0001f);
        scheduler.ProcessHooks();
        scheduler.Tick(0.0001f);
        scheduler.ProcessHooks();
        Assert.Equal(0, hookCaptures);

        scheduler.Tick(0.0003f);
        scheduler.ProcessHooks();
        Assert.Equal(1, hookCaptures);
    }

    [Fact]
    public void ScriptOnlyOwner_DoesNotEnterAnimationWorkset()
    {
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            new Loader(),
            (_, _) => { },
            (_, _, _) => { });
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity();
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0x3300AA01u,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup));

        Assert.Equal(0, scheduler.Count);
        var animatedIds = new HashSet<uint>();
        scheduler.CopyAnimatedEntityIdsTo(animatedIds);
        Assert.Empty(animatedIds);

        scheduler.Unregister(OwnerId);
    }

    [Fact]
    public void LivePhysicsStaticOwner_CatchesUpShortNonResidentIntervalOnReentry()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        bool resident = false;
        int captures = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => captures++,
            (_, _, _) => { },
            _ => resident);
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        BindLiveOwner(
            scheduler,
            entity,
            setup,
            loader,
            out _);

        scheduler.Tick(0.02f);
        scheduler.ProcessHooks();
        Assert.Equal(0, captures);

        resident = true;
        scheduler.Tick(0.02f);
        scheduler.ProcessHooks();
        Assert.Equal(1, captures);
        Assert.True(scheduler.TryTakePreparedFramesForTest(OwnerId, out var frames));

        var reference = new AnimationSequencer(
            setup,
            new MotionTable(),
            loader);
        IReadOnlyList<PartTransform> referenceFrames = reference.Advance(0.04f);

        Assert.Equal(referenceFrames[0], frames[0]);
    }

    [Fact]
    public void PendingLiveStaticOwner_BindsCanonicalRuntimeOwnerOnFirstTick()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        var sequencer = new AnimationSequencer(
            setup,
            new MotionTable(),
            loader);
        var body = new PhysicsBody
        {
            Orientation = entity.Rotation,
        };
        body.SnapToCell(0x01010001u, entity.Position, Vector3.Zero);
        LiveEntityAnimationState animation =
            LiveState(entity, setup, sequencer);
        int resolutions = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => { },
            (_, _, _) => { },
            resolveLiveOwner: candidate =>
            {
                Assert.Same(entity, candidate);
                resolutions++;
                return (animation, body);
            });
        Assert.True(scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true)));

        scheduler.Tick(0.02f);
        scheduler.Tick(0.02f);

        Assert.Equal(1, resolutions);
        Assert.True(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
    }

    [Fact]
    public void LivePhysicsStaticOwner_DiscardsLongNonResidentIntervalOnReentry()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        bool resident = false;
        int captures = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => captures++,
            (_, _, _) => { },
            _ => resident);
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: MakeSetup(),
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        BindLiveOwner(
            scheduler,
            entity,
            MakeSetup(),
            loader,
            out _);

        scheduler.Tick(2.01f);
        scheduler.ProcessHooks();
        resident = true;
        scheduler.Tick(0.1f);
        scheduler.ProcessHooks();
        Assert.Equal(0, captures);

        scheduler.Tick(0.1f);
        scheduler.ProcessHooks();
        Assert.Equal(1, captures);
    }

    [Fact]
    public void LivePhysicsStaticOwner_UsesCanonicalSequencerAndRawOmega()
    {
        const uint alternateAnimationId = 0x0300AA02u;
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        loader.Add(alternateAnimationId, OffsetAnimation(20f));
        var shadows = new ShadowObjectRegistry();
        Matrix4x4 committedRoot = Matrix4x4.Identity;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => { },
            (_, _, _) => { },
            commitLiveRoot: (entity, body) =>
            {
                shadows.UpdatePosition(
                    entity.Id,
                    body.Position,
                    body.Orientation,
                    worldOffsetX: 0f,
                    worldOffsetY: 0f,
                    landblockId: 0x01010000u,
                    seedCellId: body.CellPosition.ObjCellId);
                committedRoot = Matrix4x4.CreateFromQuaternion(entity.Rotation)
                    * Matrix4x4.CreateTranslation(entity.Position);
            });
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        entity.Position = new Vector3(12f, 12f, 50f);
        shadows.RegisterMultiPart(
            entity.Id,
            entity.Position,
            entity.Rotation,
            new[]
            {
                ShadowShape.Cylinder(
                    GfxId,
                    Vector3.UnitX,
                    Quaternion.Identity,
                    scale: 1f,
                    radius: 0.5f,
                    cylHeight: 1f),
            },
            state: 0u,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0x01010000u,
            seedCellId: 0x01010001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        AnimationSequencer sequencer = BindLiveOwner(
            scheduler,
            entity,
            setup,
            loader,
            out PhysicsBody body);
        body.Omega = new Vector3(0f, 0f, MathF.PI / 2f);

        // Simulates UpdateMotion replacing the sequence on the same PartArray.
        Assert.True(sequencer.InitializeSetupDefaultAnimation(alternateAnimationId));
        scheduler.Tick(0.01f);

        Assert.True(scheduler.TryTakePreparedFramesForTest(OwnerId, out var frames));
        Assert.True(frames[0].Origin.X >= 20f);
        Vector3 rotatedX = Vector3.Transform(Vector3.UnitX, entity.Rotation);
        Assert.InRange(rotatedX.X, -0.001f, 0.001f);
        Assert.InRange(rotatedX.Y, 0.999f, 1.001f);
        Assert.Equal(entity.Rotation, body.Orientation);
        ShadowEntry rotatedShadow = Assert.Single(
            shadows.AllEntriesForDebug(),
            item => item.EntityId == entity.Id);
        Assert.InRange(rotatedShadow.Position.X, 11.999f, 12.001f);
        Assert.InRange(rotatedShadow.Position.Y, 12.999f, 13.001f);
        Vector3 committedX = Vector3.TransformNormal(
            Vector3.UnitX,
            committedRoot);
        Assert.InRange(committedX.X, -0.001f, 0.001f);
        Assert.InRange(committedX.Y, 0.999f, 1.001f);
    }

    [Fact]
    public void LivePhysicsStaticOwner_ZeroOmegaDoesNotCommitUnchangedRoot()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        int rootCommits = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => { },
            (_, _, _) => { },
            commitLiveRoot: (_, _) => rootCommits++);
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        BindLiveOwner(scheduler, entity, setup, loader, out _);

        scheduler.Tick(0.1f);

        Assert.Equal(0, rootCommits);
        Assert.True(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
    }

    [Fact]
    public void LivePhysicsStaticOwner_WithdrawalDuringRootCommitDropsPreparedPoseAndHooks()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        bool resident = true;
        ulong residencyVersion = 1;
        int captures = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => captures++,
            (_, _, _) => { },
            isResident: _ => resident,
            commitLiveRoot: (_, _) =>
            {
                resident = false;
                residencyVersion++;
            },
            residencyVersion: _ => residencyVersion);
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        BindLiveOwner(scheduler, entity, setup, loader, out PhysicsBody body);
        body.Omega = new Vector3(0f, 0f, MathF.PI / 2f);

        scheduler.Tick(0.1f);
        scheduler.ProcessHooks();

        Assert.False(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
        Assert.Equal(0, captures);
    }

    [Fact]
    public void LivePhysicsStaticOwner_NonCyclicAnimationDoneRunsAfterFinalPartAndChildPose()
    {
        const uint style = 0x8000003Eu;
        const uint readyMotion = 0x41000003u;
        const uint actionMotion = 0x10000058u;
        const uint actionAnimationId = 0x0300AA03u;
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        loader.Add(actionAnimationId, OffsetAnimation(20f));
        Setup setup = MakeSetup();
        var table = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)style,
        };
        table.StyleDefaults[(DRWMotionCommand)style] =
            (DRWMotionCommand)readyMotion;
        int readyKey = unchecked((int)((style << 16) | (readyMotion & 0xFFFFFFu)));
        table.Cycles[readyKey] = MakeMotionData(AnimationId);
        var links = new MotionCommandData();
        links.MotionData[unchecked((int)actionMotion)] =
            MakeMotionData(actionAnimationId);
        table.Links[readyKey] = links;

        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        var sequencer = new AnimationSequencer(setup, table, loader);
        sequencer.SetCycle(style, readyMotion);
        sequencer.ConsumePendingHooks();
        sequencer.PlayAction(actionMotion);
        Assert.NotEmpty(sequencer.Manager.PendingAnimations);

        var order = new List<string>();
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(entity, entity.IndexedPartTransforms, entity.IndexedPartAvailable);
        var hookFrames = new AnimationHookFrameQueue(
            new AnimationHookRouter(),
            poses);
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (ownerId, ownerSequencer) =>
            {
                order.Add("process_hooks");
                hookFrames.Capture(ownerId, ownerSequencer);
            },
            (_, _, _) => { },
            commitLiveRoot: (_, _) => order.Add("root_committed"));
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        var body = new PhysicsBody
        {
            Orientation = entity.Rotation,
            Omega = new Vector3(0f, 0f, MathF.PI / 2f),
        };
        body.SnapToCell(0x01010001u, entity.Position, Vector3.Zero);
        Assert.True(scheduler.BindLiveOwner(
            entity,
            LiveState(entity, setup, sequencer),
            body));
        sequencer.MotionDoneTarget = (_, success) =>
        {
            Assert.True(success);
            order.Add("animation_done");
        };

        scheduler.Tick(0.2f);

        Assert.DoesNotContain("animation_done", order);
        Assert.True(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
        order.Add("parts_published");
        order.Add("children_updated");
        scheduler.ProcessHooks();

        Assert.Equal("root_committed", order[0]);
        Assert.Equal("parts_published", order[1]);
        Assert.Equal("children_updated", order[2]);
        Assert.Equal("process_hooks", order[3]);
        Assert.True(order.Count > 4);
        Assert.All(order.Skip(4), item => Assert.Equal("animation_done", item));
        Assert.Empty(sequencer.Manager.PendingAnimations);
    }

    [Fact]
    public void RebindingLiveOwner_DiscardsFramesPreparedByPriorSequencer()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => { },
            (_, _, _) => { });
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        AnimationSequencer first = BindLiveOwner(
            scheduler, entity, setup, loader, out PhysicsBody body);
        scheduler.Tick(0.02f);
        AnimationSequencer second = new(setup, new MotionTable(), loader);

        Assert.NotSame(first, second);
        Assert.True(scheduler.BindLiveOwner(
            entity,
            LiveState(entity, setup, second),
            body));
        Assert.False(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
    }

    [Fact]
    public void RepeatedTakeAfterConsumption_PreservesPendingProcessHooks()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        int captures = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, _) => captures++,
            (_, _, _) => { });
        Setup setup = MakeSetup();
        WorldEntity entity = MakeEntity(serverGuid: 0x70000001u);
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        BindLiveOwner(scheduler, entity, setup, loader, out _);
        scheduler.Tick(0.02f);

        Assert.True(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
        Assert.False(scheduler.TryTakePreparedFramesForTest(OwnerId, out _));
        scheduler.ProcessHooks();

        Assert.Equal(1, captures);
    }

    [Fact]
    public void AppearanceRevisionRejectsPreparedPose_ButPreservesMotionDone()
    {
        const uint style = 0x8000003Eu;
        const uint readyMotion = 0x41000003u;
        const uint actionMotion = 0x10000058u;
        const uint actionAnimationId = 0x0300AA03u;
        const uint serverGuid = 0x70000001u;
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        loader.Add(actionAnimationId, OffsetAnimation(20f));
        Setup setup = MakeSetup();
        var table = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)style,
        };
        table.StyleDefaults[(DRWMotionCommand)style] =
            (DRWMotionCommand)readyMotion;
        int readyKey = unchecked((int)((style << 16) | (readyMotion & 0xFFFFFFu)));
        table.Cycles[readyKey] = MakeMotionData(AnimationId);
        var links = new MotionCommandData();
        links.MotionData[unchecked((int)actionMotion)] =
            MakeMotionData(actionAnimationId);
        table.Links[readyKey] = links;

        WorldEntity entity = MakeEntity(serverGuid);
        var sequencer = new AnimationSequencer(setup, table, loader);
        sequencer.SetCycle(style, readyMotion);
        sequencer.ConsumePendingHooks();
        sequencer.PlayAction(actionMotion);
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(entity, entity.IndexedPartTransforms, entity.IndexedPartAvailable);
        var hookFrames = new AnimationHookFrameQueue(
            new AnimationHookRouter(),
            poses);
        int captures = 0;
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (ownerId, ownerSequencer) =>
            {
                captures++;
                hookFrames.Capture(ownerId, ownerSequencer);
            },
            (_, _, _) => { });
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: setup,
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));
        var body = new PhysicsBody
        {
            Orientation = entity.Rotation,
        };
        body.SnapToCell(0x01010001u, entity.Position, Vector3.Zero);
        LiveEntityAnimationState state = LiveState(entity, setup, sequencer);
        Assert.True(scheduler.BindLiveOwner(entity, state, body));
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(
            Spawn(serverGuid));
        record.WorldEntity = entity;
        record.AnimationRuntime = state;
        int motionDone = 0;
        sequencer.MotionDoneTarget = (_, success) =>
        {
            Assert.True(success);
            motionDone++;
        };

        scheduler.Tick(0.2f);
        state.InvalidatePresentationPoses();

        Assert.False(scheduler.TryTakeLivePartFrames(
            record,
            entity,
            state,
            record.ObjectClockEpoch,
            record.ProjectionMutationVersion,
            state.PresentationRevision,
            out _));
        scheduler.ProcessHooks();

        Assert.Equal(1, captures);
        Assert.True(motionDone > 0);
        Assert.Empty(sequencer.Manager.PendingAnimations);
    }

    private static Setup MakeSetup()
    {
        var setup = new Setup();
        setup.Parts.Add(GfxId);
        setup.DefaultScale.Add(Vector3.One);
        setup.DefaultAnimation = (QualifiedDataId<Animation>)AnimationId;
        return setup;
    }

    private static WorldEntity MakeEntity(
        uint serverGuid = 0,
        uint ownerId = OwnerId)
    {
        var entity = new WorldEntity
        {
            Id = ownerId,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x0200AA01u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = new[] { new MeshRef(GfxId, Matrix4x4.Identity) },
        };
        entity.SetIndexedPartPoses(
            new[] { Matrix4x4.Identity },
            new[] { true });
        return entity;
    }

    private static WorldSession.EntitySpawn Spawn(uint guid)
    {
        const uint cell = 0x01010001u;
        var position = new CreateObject.ServerPosition(
            cell,
            0f,
            0f,
            0f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
        var physics = new PhysicsSpawnData(
            RawState: 0u,
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
            guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            "static appearance fixture",
            null,
            null,
            null,
            PhysicsState: 0u,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    [Fact]
    public void SetOmegaHook_RotatesTheRootOfDatSceneryWithNoPhysicsBody()
    {
        var loader = new Loader();
        loader.Add(AnimationId, OmegaAnimation(new Vector3(0f, 0f, -0.027f)));
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, sequencer) => sequencer.ConsumePendingHooks(),
            (_, _, _) => { });
        WorldEntity entity = MakeEntity();
        Assert.Equal(0u, entity.ServerGuid);          // DAT scenery: no body
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: MakeSetup(),
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));

        scheduler.Tick(1f / 30f);
        Assert.Equal(Quaternion.Identity, entity.Rotation);
        scheduler.ProcessHooks();

        // ...and from the next frame on it turns.
        scheduler.Tick(1f / 30f);
        Assert.NotEqual(Quaternion.Identity, entity.Rotation);

        Assert.Equal(0f, entity.Rotation.X, 5);
        Assert.Equal(0f, entity.Rotation.Y, 5);
        Assert.NotEqual(0f, entity.Rotation.Z, 5);

        Quaternion afterFirst = entity.Rotation;
        scheduler.Tick(1f / 30f);
        Assert.NotEqual(afterFirst, entity.Rotation);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(180)]
    public void SetOmegaHook_DatSceneryOrbitIsIndependentOfHostFrameRate(
        int hostFramesPerSecond)
    {
        const float authoredYawPerQuantum = -0.027f;
        var loader = new Loader();
        loader.Add(
            AnimationId,
            OmegaAnimation(new Vector3(0f, 0f, authoredYawPerQuantum)));
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, sequencer) => sequencer.ConsumePendingHooks(),
            (_, _, _) => { });
        WorldEntity entity = MakeEntity();
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: MakeSetup(),
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));

        scheduler.Tick(1f / 30f);
        scheduler.ProcessHooks();

        float hostDelta = 1f / hostFramesPerSecond;
        for (int frame = 0; frame < hostFramesPerSecond; frame++)
        {
            scheduler.Tick(hostDelta);
            scheduler.ProcessHooks();
        }

        Vector3 actualForward = Vector3.Transform(Vector3.UnitX, entity.Rotation);
        float expectedYaw = authoredYawPerQuantum * 30f;
        var expectedForward = new Vector3(
            MathF.Cos(expectedYaw),
            MathF.Sin(expectedYaw),
            0f);
        Assert.InRange(Vector3.Distance(actualForward, expectedForward), 0f, 0.0001f);
    }

    [Fact]
    public void WithoutASetOmegaHookTheRootNeverTurns()
    {
        var loader = new Loader();
        loader.Add(AnimationId, TwoFrameAnimation());
        var scheduler = new RetailStaticAnimatingObjectScheduler(
            loader,
            (_, sequencer) => sequencer.ConsumePendingHooks(),
            (_, _, _) => { });
        WorldEntity entity = MakeEntity();
        scheduler.Register(entity, new ScriptActivationInfo(
            ScriptId: 0,
            PartTransforms: entity.IndexedPartTransforms,
            PartAvailability: entity.IndexedPartAvailable,
            Setup: MakeSetup(),
            DefaultAnimationId: AnimationId,
            UsesStaticAnimationWorkset: true));

        for (int i = 0; i < 4; i++)
        {
            scheduler.Tick(1f / 30f);
            scheduler.ProcessHooks();
        }

        // Ordinary animated scenery (a swinging sign, a waterwheel's parts)
        // must not acquire a spin it never asked for.
        Assert.Equal(Quaternion.Identity, entity.Rotation);
    }

    private static Animation OmegaAnimation(Vector3 axis)
    {
        Animation animation = TwoFrameAnimation();
        animation.PartFrames[0].Hooks.Add(new SetOmegaHook { Axis = axis });
        return animation;
    }

    private static Animation TwoFrameAnimation()
    {
        var animation = new Animation();
        var first = new AnimationFrame(1);
        first.Frames.Add(new Frame
        {
            Origin = Vector3.Zero,
            Orientation = Quaternion.Identity,
        });
        var second = new AnimationFrame(1);
        second.Frames.Add(new Frame
        {
            Origin = new Vector3(2f, 0f, 0f),
            Orientation = Quaternion.Identity,
        });
        animation.PartFrames.Add(first);
        animation.PartFrames.Add(second);
        return animation;
    }

    private static Animation OffsetAnimation(float x)
    {
        var animation = new Animation();
        var frame = new AnimationFrame(1);
        frame.Frames.Add(new Frame
        {
            Origin = new Vector3(x, 0f, 0f),
            Orientation = Quaternion.Identity,
        });
        animation.PartFrames.Add(frame);
        return animation;
    }

    private static MotionData MakeMotionData(uint animationId)
    {
        var data = new MotionData();
        data.Anims.Add(new AnimData
        {
            AnimId = (QualifiedDataId<Animation>)animationId,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = 10f,
        });
        return data;
    }

    private static AnimationSequencer BindLiveOwner(
        RetailStaticAnimatingObjectScheduler scheduler,
        WorldEntity entity,
        Setup setup,
        IAnimationLoader loader,
        out PhysicsBody body)
    {
        var sequencer = new AnimationSequencer(
            setup,
            new MotionTable(),
            loader);
        body = new PhysicsBody
        {
            Orientation = entity.Rotation,
        };
        body.SnapToCell(0x01010001u, entity.Position, Vector3.Zero);
        Assert.True(scheduler.BindLiveOwner(
            entity,
            LiveState(entity, setup, sequencer),
            body));
        return sequencer;
    }

    private static LiveEntityAnimationState LiveState(
        WorldEntity entity,
        Setup setup,
        AnimationSequencer sequencer) => new()
        {
            Entity = entity,
            Setup = setup,
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = entity.Scale,
            PartTemplate = setup.Parts.Select(
                    part => new LiveAnimationPartTemplate((uint)part, null, true))
                .ToArray(),
            PartAvailability = Enumerable.Repeat(true, setup.Parts.Count).ToArray(),
            Sequencer = sequencer,
        };

    private sealed class Loader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _animations = new();
        public void Add(uint id, Animation animation) => _animations[id] = animation;
        public Animation? LoadAnimation(uint id) =>
            _animations.TryGetValue(id, out Animation? animation)
                ? animation
                : null;
    }
}
