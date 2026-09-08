using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

public sealed class LiveEntityAnimationPresenterTests
{
    private const uint Guid = 0x70000031u;
    private const uint Cell = 0x01010001u;

    [Fact]
    public void Present_ComposesDistinctVisualAndRigidChannels()
    {
        var fixture = Build(partCount: 2, scale: 2f);
        fixture.State.Setup.DefaultScale[0] = new Vector3(3f, 4f, 5f);
        fixture.State.PartTemplate =
        [
            new LiveAnimationPartTemplate(0x01000001u, new Dictionary<uint, uint> { [1] = 2 }, true),
            new LiveAnimationPartTemplate(0x01000002u, null, false),
        ];
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        PartTransform[] frames =
        [
            new(new Vector3(1f, 2f, 3f), rotation),
            new(new Vector3(4f, 5f, 6f), Quaternion.Identity),
        ];
        var poses = new EntityEffectPoseRegistry();
        var presenter = Presenter(fixture.Live, poses, new Context());

        presenter.Present(Schedules(fixture, frames));

        Assert.Single(fixture.Entity.MeshRefs);
        Matrix4x4 expectedVisual = Matrix4x4.CreateScale(3f, 4f, 5f)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(1f, 2f, 3f)
            * Matrix4x4.CreateScale(2f);
        Matrix4x4 expectedRigid = Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(2f, 4f, 6f);
        Assert.Equal(expectedVisual, fixture.Entity.MeshRefs[0].PartTransform);
        Assert.Equal(expectedRigid, fixture.Entity.IndexedPartTransforms[0]);
        Assert.Equal(2, fixture.Entity.IndexedPartTransforms.Count);
        Assert.True(poses.TryGetPartPose(fixture.Entity.Id, 0, out Matrix4x4 effectPose));
        Assert.Equal(expectedRigid, effectPose);
        Assert.Equal(2u, fixture.Entity.MeshRefs[0].SurfaceOverrides![1]);
    }

    [Fact]
    public void OldSchedule_IsRejectedForCurrentOwnerEvenWhenIndexedByItsLocalId()
    {
        var old = Build(partCount: 1);
        LiveEntityAnimationSchedule stale = Schedule(old,
            [new PartTransform(new Vector3(99f, 0f, 0f), Quaternion.Identity)]);
        Assert.True(old.Live.ClearAnimationRuntime(Guid));
        LiveEntityAnimationState replacement = State(old.Entity, partCount: 1, scale: 1f);
        old.Live.SetAnimationRuntime(Guid, replacement);
        var presenter = Presenter(old.Live, new EntityEffectPoseRegistry(), new Context());

        presenter.Present(new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [old.Record.ProjectionKey!.Value] = stale,
        });

        Assert.Empty(replacement.MeshRefsScratch);
        Assert.Empty(replacement.EffectPartPosesScratch);
    }

    [Fact]
    public void ShortFirstFrame_RetainsDistinctVisualAndRigidRestPoses()
    {
        var fixture = Build(partCount: 2, scale: 2f);
        Matrix4x4 visualRest = Matrix4x4.CreateScale(7f)
            * Matrix4x4.CreateTranslation(8f, 9f, 10f);
        Matrix4x4 rigidRest = Matrix4x4.CreateTranslation(11f, 12f, 13f);
        fixture.Entity.MeshRefs =
        [
            new MeshRef(0x01000001u, Matrix4x4.Identity),
            new MeshRef(0x01000002u, visualRest),
        ];
        fixture.Entity.SetIndexedPartPoses(
            [Matrix4x4.Identity, rigidRest],
            [true, true]);
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), new Context());

        presenter.Present(Schedules(fixture,
            [new PartTransform(Vector3.UnitX, Quaternion.Identity)]));

        Assert.Equal(visualRest, fixture.Entity.MeshRefs[1].PartTransform);
        Assert.Equal(rigidRest, fixture.Entity.IndexedPartTransforms[1]);
    }

    [Fact]
    public void LaterShortFrame_RetainsPreviouslyPresentedTrailingPart()
    {
        var fixture = Build(partCount: 2);
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), new Context());
        presenter.Present(Schedules(fixture,
        [
            new PartTransform(Vector3.UnitX, Quaternion.Identity),
            new PartTransform(new Vector3(4f, 5f, 6f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f)),
        ]));
        Matrix4x4 visual = fixture.Entity.MeshRefs[1].PartTransform;
        Matrix4x4 rigid = fixture.Entity.IndexedPartTransforms[1];

        presenter.Present(Schedules(fixture,
            [new PartTransform(Vector3.UnitY, Quaternion.Identity)]));

        Assert.Equal(visual, fixture.Entity.MeshRefs[1].PartTransform);
        Assert.Equal(rigid, fixture.Entity.IndexedPartTransforms[1]);
    }

    [Fact]
    public void AppearanceShrink_ClearsAllTrailingPresentationState()
    {
        var fixture = Build(partCount: 3);
        var poses = new EntityEffectPoseRegistry();
        var presenter = Presenter(fixture.Live, poses, new Context());
        presenter.Present(Schedules(fixture,
        [
            new PartTransform(Vector3.UnitX, Quaternion.Identity),
            new PartTransform(Vector3.UnitY, Quaternion.Identity),
            new PartTransform(Vector3.UnitZ, Quaternion.Identity),
        ]));
        var replacementSetup = new Setup();
        replacementSetup.Parts.Add(0x01000009u);
        replacementSetup.DefaultScale.Add(Vector3.One);
        fixture.Entity.MeshRefs = [new MeshRef(0x01000009u, Matrix4x4.Identity)];
        fixture.Entity.SetIndexedPartPoses([Matrix4x4.Identity], [true]);
        LiveEntityAppearanceBinding.RebindAnimation(
            fixture.State,
            fixture.Entity,
            replacementSetup,
            1f,
            [new LiveAnimationPartTemplate(0x01000009u, null, true)],
            [true]);

        presenter.Present(Schedules(fixture,
            [new PartTransform(new Vector3(9f, 0f, 0f), Quaternion.Identity)]));

        Assert.Single(fixture.Entity.MeshRefs);
        Assert.Single(fixture.Entity.IndexedPartTransforms);
        Assert.Single(fixture.State.VisualPartPosesScratch);
        Assert.Single(fixture.State.EffectPartPosesScratch);
        Assert.True(poses.TryGetPartPoseSnapshot(
            fixture.Entity.Id,
            out IReadOnlyList<Matrix4x4> published,
            out IReadOnlyList<bool> available));
        Assert.Single(published);
        Assert.Single(available);
    }

    [Fact]
    public void SchedulePreparedBeforeAppearanceRebind_IsRejected()
    {
        var fixture = Build(partCount: 2);
        LiveEntityAnimationSchedule stale = Schedule(fixture,
        [
            new PartTransform(Vector3.UnitX, Quaternion.Identity),
            new PartTransform(Vector3.UnitY, Quaternion.Identity),
        ]);
        var replacement = new Setup();
        replacement.Parts.Add(0x01000099u);
        replacement.DefaultScale.Add(Vector3.One);
        LiveEntityAppearanceBinding.RebindAnimation(
            fixture.State,
            fixture.Entity,
            replacement,
            1f,
            [new LiveAnimationPartTemplate(0x01000099u, null, true)],
            [true]);
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), new Context());

        presenter.Present(new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [fixture.Record.ProjectionKey!.Value] = stale,
        });

        Assert.Empty(fixture.State.MeshRefsScratch);
        Assert.Empty(fixture.State.EffectPartPosesScratch);
    }

    [Fact]
    public void MotionDone_OldComponentCannotResolveReplacementByGuid()
    {
        var fixture = Build(partCount: 1, withSequencer: true);
        var context = new Context();
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), context);
        presenter.PrepareAnimation(fixture.Record, fixture.State);
        Action<uint, bool> oldCallback = fixture.State.Sequencer!.MotionDoneTarget!;

        Assert.True(fixture.Live.ClearAnimationRuntime(Guid));
        fixture.Live.SetAnimationRuntime(Guid, State(fixture.Entity, 1, 1f));
        oldCallback(0x10000001u, true);

        Assert.Equal(0, context.ResolveCount);
    }

    [Fact]
    public void MotionDone_SameComponentStillResolvesAfterObjectClockRebase()
    {
        var fixture = Build(partCount: 1, withSequencer: true);
        var context = new Context();
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), context);
        presenter.PrepareAnimation(fixture.Record, fixture.State);

        fixture.Record.SuspendObjectClock();
        fixture.Record.ResetObjectClockForEnterWorld(isStatic: false);
        fixture.State.Sequencer!.MotionDoneTarget!(0x10000001u, true);

        Assert.Equal(1, context.ResolveCount);
        Assert.Same(fixture.Record, context.LastRecord);
    }

    [Fact]
    public void MotionDone_CurrentLogicalOwnerResolvesWhileSpatialProjectionIsWithdrawn()
    {
        var fixture = Build(partCount: 1, withSequencer: true);
        var interpreter = new MotionInterpreter(new PhysicsBody());
        interpreter.AddToQueue(0, MotionCommand.Ready, 0);
        var context = new Context { Motion = interpreter };
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), context);

        Assert.True(fixture.Live.WithdrawLiveEntityProjection(Guid));
        Assert.False(fixture.Record.IsSpatiallyProjected);
        presenter.PrepareAnimation(fixture.Record, fixture.State);
        Assert.NotNull(fixture.State.Sequencer!.MotionDoneTarget);
        fixture.State.Sequencer!.MotionDoneTarget!(MotionCommand.Ready, true);

        Assert.Equal(1, context.ResolveCount);
        Assert.Same(fixture.Record, context.LastRecord);
        Assert.False(interpreter.MotionsPending());
    }

    [Fact]
    public void EffectPoseCallback_NestedRuntimeSnapshotDoesNotTruncatePresentation()
    {
        var first = Build(partCount: 1, guid: Guid);
        var second = Add(first.Live, Guid + 1, partCount: 1);
        var poses = new EntityEffectPoseRegistry();
        poses.EffectPoseChanged += _ =>
        {
            var nested = new List<LiveEntityRecord>();
            first.Live.CopySpatialRootObjectRecordsTo(nested);
            Assert.Equal(2, nested.Count);
        };
        var presenter = Presenter(first.Live, poses, new Context());
        var schedules = new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [first.Record.ProjectionKey!.Value] = Schedule(first,
                [new PartTransform(Vector3.UnitX, Quaternion.Identity)]),
            [second.Record.ProjectionKey!.Value] = Schedule(second,
                [new PartTransform(Vector3.UnitY, Quaternion.Identity)]),
        };

        presenter.Present(schedules);

        Assert.Single(first.Entity.MeshRefs);
        Assert.Single(second.Entity.MeshRefs);
    }

    [Fact]
    public void EffectPoseCallback_ReplacesNextOwnerWithoutPublishingItsStaleSchedule()
    {
        var first = Build(partCount: 1, guid: Guid);
        var second = Add(first.Live, Guid + 1, partCount: 1);
        LiveEntityAnimationState replacement = State(second.Entity, 1, 1f, withSequencer: true);
        var poses = new EntityEffectPoseRegistry();
        bool replaced = false;
        poses.EffectPoseChanged += ownerId =>
        {
            if (ownerId != first.Entity.Id || replaced)
                return;
            replaced = true;
            Assert.True(first.Live.ClearAnimationRuntime(Guid + 1));
            first.Live.SetAnimationRuntime(Guid + 1, replacement);
        };
        var presenter = Presenter(first.Live, poses, new Context());
        var schedules = new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [first.Record.ProjectionKey!.Value] = Schedule(first,
                [new PartTransform(Vector3.UnitX, Quaternion.Identity)]),
            [second.Record.ProjectionKey!.Value] = Schedule(second,
                [new PartTransform(new Vector3(99f, 0f, 0f), Quaternion.Identity)]),
        };

        presenter.Present(schedules);

        Assert.True(replaced);
        Assert.Empty(replacement.MeshRefsScratch);
        Assert.Empty(replacement.EffectPartPosesScratch);
    }

    [Fact]
    public void EffectPoseCallback_RecursivePresentDoesNotTruncateOuterPass()
    {
        var first = Build(partCount: 1, guid: Guid);
        var second = Add(first.Live, Guid + 1, partCount: 1);
        var poses = new EntityEffectPoseRegistry();
        var presenter = Presenter(first.Live, poses, new Context());
        var schedules = new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [first.Record.ProjectionKey!.Value] = Schedule(first,
                [new PartTransform(Vector3.UnitX, Quaternion.Identity)]),
            [second.Record.ProjectionKey!.Value] = Schedule(second,
                [new PartTransform(Vector3.UnitY, Quaternion.Identity)]),
        };
        bool recursed = false;
        poses.EffectPoseChanged += _ =>
        {
            if (recursed)
                return;
            recursed = true;
            presenter.Present(schedules);
        };

        presenter.Present(schedules);

        Assert.True(recursed);
        Assert.Single(first.Entity.MeshRefs);
        Assert.Single(second.Entity.MeshRefs);
    }

    [Fact]
    public void EffectPoseCallback_RecursivePresentConsumesLegacyElapsedOnce()
    {
        var fixture = Build(partCount: 1);
        fixture.State.Sequencer = null;
        fixture.State.LowFrame = 0;
        fixture.State.HighFrame = 1;
        fixture.State.Framerate = 1f;
        fixture.State.CurrFrame = 0f;
        var animation = new Animation();
        for (int i = 0; i < 2; i++)
        {
            var frame = new AnimationFrame(1);
            frame.Frames.Add(new Frame
            {
                Origin = new Vector3(i, 0f, 0f),
                Orientation = Quaternion.Identity,
            });
            animation.PartFrames.Add(frame);
        }
        fixture.State.Animation = animation;
        var schedule = new LiveEntityAnimationSchedule(
            SequenceFrames: null,
            LegacyAdvanceSeconds: 0.5f,
            ComposeParts: true,
            fixture.Record,
            fixture.Entity,
            fixture.State,
            fixture.Record.ObjectClockEpoch,
            fixture.Record.ProjectionMutationVersion,
            fixture.State.PresentationRevision);
        var schedules = new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [fixture.Record.ProjectionKey!.Value] = schedule,
        };
        var poses = new EntityEffectPoseRegistry();
        var presenter = Presenter(fixture.Live, poses, new Context());
        bool recursed = false;
        poses.EffectPoseChanged += _ =>
        {
            if (recursed)
                return;
            recursed = true;
            presenter.Present(schedules);
        };

        presenter.Present(schedules);

        Assert.True(recursed);
        Assert.Equal(0.5f, fixture.State.CurrFrame);
    }

    [Fact]
    public void LegacyCycle_WrapsAndInterpolatesThroughSharedRetailPlayback()
    {
        var fixture = Build(partCount: 1);
        fixture.State.Sequencer = null;
        fixture.State.LowFrame = 0;
        fixture.State.HighFrame = 2;
        fixture.State.Framerate = 2f;
        fixture.State.CurrFrame = 2.5f;
        var animation = new Animation();
        foreach (float x in new[] { 0f, 10f, 20f })
        {
            var frame = new AnimationFrame(1);
            frame.Frames.Add(new Frame
            {
                Origin = new Vector3(x, 0f, 0f),
                Orientation = Quaternion.Identity,
            });
            animation.PartFrames.Add(frame);
        }
        fixture.State.Animation = animation;
        var schedule = new LiveEntityAnimationSchedule(
            SequenceFrames: null,
            LegacyAdvanceSeconds: 0.5f,
            ComposeParts: true,
            fixture.Record,
            fixture.Entity,
            fixture.State,
            fixture.Record.ObjectClockEpoch,
            fixture.Record.ProjectionMutationVersion,
            fixture.State.PresentationRevision);
        var presenter = Presenter(fixture.Live, new EntityEffectPoseRegistry(), new Context());

        presenter.Present(new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [fixture.Record.ProjectionKey!.Value] = schedule,
        });

        // Legacy arithmetic: 2.5 + (0.5 * 2) = 3.5, wrapped over the
        // inclusive 0..2 span to 0.5, then interpolated halfway 0 -> 10.
        Assert.Equal(0.5f, fixture.State.CurrFrame);
        Assert.Equal(new Vector3(5f, 0f, 0f), fixture.Entity.MeshRefs[0].PartTransform.Translation);
    }

    private static LiveEntityAnimationPresenter Presenter(
        LiveEntityRuntime live,
        EntityEffectPoseRegistry poses,
        Context context) => new(
            live,
            new EmptyStaticPartFrameSource(),
            poses,
            context,
            AnimationPresentationDiagnostics.Disabled,
            hidePartIndex: -1);

    private sealed class EmptyStaticPartFrameSource : ILiveStaticPartFrameSource
    {
        public bool TryTakeLivePartFrames(
            LiveEntityRecord record,
            WorldEntity entity,
            LiveEntityAnimationState animation,
            ulong objectClockEpoch,
            ulong projectionMutationVersion,
            ulong presentationRevision,
            out IReadOnlyList<PartTransform> partFrames)
        {
            partFrames = Array.Empty<PartTransform>();
            return false;
        }
    }

    private static IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> Schedules(
        Fixture fixture,
        IReadOnlyList<PartTransform> frames) =>
        new Dictionary<RuntimeEntityKey, LiveEntityAnimationSchedule>
        {
            [fixture.Record.ProjectionKey!.Value] = Schedule(fixture, frames),
        };

    private static LiveEntityAnimationSchedule Schedule(
        Fixture fixture,
        IReadOnlyList<PartTransform> frames) => new(
            frames,
            0f,
            true,
            fixture.Record,
            fixture.Entity,
            fixture.State,
            fixture.Record.ObjectClockEpoch,
            fixture.Record.ProjectionMutationVersion,
            fixture.State.PresentationRevision);

    private static Fixture Build(
        int partCount,
        float scale = 1f,
        bool withSequencer = true,
        uint guid = Guid)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        return Add(live, guid, partCount, scale, withSequencer);
    }

    private static Fixture Add(
        LiveEntityRuntime live,
        uint guid,
        int partCount,
        float scale = 1f,
        bool withSequencer = true)
    {
        LiveEntityRecord record = live.RegisterAndMaterializeProjection(
            Spawn(guid),
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = Cell,
            });
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        LiveEntityAnimationState state = State(entity, partCount, scale, withSequencer);
        entity.SetIndexedPartPoses(
            Enumerable.Repeat(Matrix4x4.Identity, partCount).ToArray(),
            Enumerable.Repeat(true, partCount).ToArray());
        record.HasPartArray = true;
        live.SetAnimationRuntime(guid, state);
        return new Fixture(live, record, entity, state);
    }

    private static LiveEntityAnimationState State(
        WorldEntity entity,
        int partCount,
        float scale,
        bool withSequencer = false)
    {
        var setup = new Setup();
        for (int i = 0; i < partCount; i++)
        {
            setup.Parts.Add(0x01000001u + (uint)i);
            setup.DefaultScale.Add(Vector3.One);
        }
        return new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = setup,
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = scale,
            PartTemplate = Enumerable.Range(0, partCount)
                .Select(i => new LiveAnimationPartTemplate(0x01000001u + (uint)i, null, true))
                .ToArray(),
            PartAvailability = Enumerable.Repeat(true, partCount).ToArray(),
            Sequencer = withSequencer
                ? new AnimationSequencer(setup, new MotionTable(), new NullLoader())
                : null,
        };
    }

    private static WorldSession.EntitySpawn Spawn(uint guid)
    {
        var position = new CreateObject.ServerPosition(Cell, 0f, 0f, 0f, 1f, 0f, 0f, 0f);
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
            "presenter fixture",
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

    private sealed class Context : ILiveAnimationPresentationContext
    {
        public uint LocalPlayerGuid => 0x50000001u;
        public MotionInterpreter? Motion { get; init; }
        public int ResolveCount { get; private set; }
        public LiveEntityRecord? LastRecord { get; private set; }
        public MotionInterpreter? ResolveMotionInterpreter(LiveEntityRecord record)
        {
            ResolveCount++;
            LastRecord = record;
            return Motion;
        }
    }

    private sealed class NullLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    private sealed record Fixture(
        LiveEntityRuntime Live,
        LiveEntityRecord Record,
        WorldEntity Entity,
        LiveEntityAnimationState State);
}
