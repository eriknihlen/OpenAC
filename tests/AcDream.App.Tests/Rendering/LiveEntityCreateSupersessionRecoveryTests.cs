using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Tests.Rendering;

public sealed class LiveEntityCreateSupersessionRecoveryTests
{
    [Fact]
    public void Recovery_RepublishesAppearanceThenCurrentSnapshotThenReplacesAnimation()
    {
        var operations = new List<string>();
        var resources = new CountingResources();
        var runtime = Runtime(resources);
        LiveEntityRecord record = RegisterAndMaterialize(runtime);
        ulong version = record.CreateIntegrationVersion;
        ulong installedAnimationVersion = 0;

        bool recovered = LiveEntityCreateSupersessionRecovery.TryApply(
            runtime,
            record,
            version,
            captureAppearance: () =>
            {
                operations.Add("capture");
                return new LiveEntityAppearanceUpdateState(record.WorldEntity!, null);
            },
            publishAppearance: _ =>
            {
                operations.Add("appearance");
                return true;
            },
            publishCurrentSnapshot: _ => operations.Add("current"),
            synchronizeAnimation: _ =>
            {
                operations.Add("animation");
                installedAnimationVersion = version;
            });

        Assert.True(recovered);
        Assert.Equal(["capture", "appearance", "current", "animation"], operations);
        Assert.Equal(version, installedAnimationVersion);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void Recovery_CreateVersionAdvanceDuringAppearance_StopsLaterOwners()
    {
        var operations = new List<string>();
        var resources = new CountingResources();
        var runtime = Runtime(resources);
        LiveEntityRecord record = RegisterAndMaterialize(runtime);
        ulong staleVersion = record.CreateIntegrationVersion;

        bool recovered = LiveEntityCreateSupersessionRecovery.TryApply(
            runtime,
            record,
            staleVersion,
            captureAppearance: () =>
            {
                operations.Add("capture");
                return new LiveEntityAppearanceUpdateState(record.WorldEntity!, null);
            },
            publishAppearance: _ =>
            {
                operations.Add("appearance");
                record.Canonical.AdvanceCreateAuthority();
                return true;
            },
            publishCurrentSnapshot: _ => operations.Add("current"),
            synchronizeAnimation: _ => operations.Add("animation"));

        Assert.False(recovered);
        Assert.Equal(["capture", "appearance"], operations);
        Assert.Same(record, AssertRecord(runtime));
        Assert.Equal(staleVersion + 1, record.CreateIntegrationVersion);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Recovery_FailureAtEachPublicationStage_CanReplayOnRetainedOwner(
        int failingStage)
    {
        var resources = new CountingResources();
        var runtime = Runtime(resources);
        LiveEntityRecord record = RegisterAndMaterialize(runtime);
        ulong version = record.CreateIntegrationVersion;
        ulong appearanceVersion = 1;
        ulong currentVersion = 1;
        ulong animationVersion = 1;
        bool fail = true;
        void FailOnce(int stage)
        {
            if (fail && failingStage == stage)
            {
                fail = false;
                throw new InvalidOperationException($"fixture stage {stage} failure");
            }
        }

        bool Apply() => LiveEntityCreateSupersessionRecovery.TryApply(
            runtime,
            record,
            version,
            captureAppearance: () =>
                new LiveEntityAppearanceUpdateState(record.WorldEntity!, null),
            publishAppearance: _ =>
            {
                appearanceVersion = version;
                FailOnce(0);
                return true;
            },
            publishCurrentSnapshot: _ =>
            {
                currentVersion = version;
                FailOnce(1);
            },
            synchronizeAnimation: _ =>
            {
                animationVersion = version;
                FailOnce(2);
            });

        Assert.Throws<InvalidOperationException>(() => Apply());
        Assert.True(Apply());

        Assert.Equal(version, appearanceVersion);
        Assert.Equal(version, currentVersion);
        Assert.Equal(version, animationVersion);
        Assert.Same(record, AssertRecord(runtime));
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(0, resources.UnregisterCount);
    }

    [Fact]
    public void CompletedOwner_PreservesSequencerSinkAndPendingQueue()
    {
        var runtime = Runtime(new CountingResources());
        LiveEntityRecord record = RegisterAndMaterialize(runtime);
        record.InitialHydrationCompleted = true;
        (Setup setup, MotionTable table, Loader loader) = MotionAssets();
        AnimationSequencer sequencer = SpawnMotionInitializer.Create(
            setup,
            table,
            loader,
            Wire(Ready));
        var animation = AnimationState(record.WorldEntity!, setup, sequencer);
        var body = MotionBody();
        var interpreter = new MotionInterpreter(body);
        var sink = new MotionTableDispatchSink(sequencer);
        interpreter.DefaultSink = sink;
        sequencer.MotionDoneTarget = interpreter.MotionDone;
        interpreter.DoMotion(Walk, new MovementParameters());
        int pendingBefore = sequencer.Manager.PendingAnimations.Count();

        bool reinitialized = LiveEntityCreateAnimationSynchronization
            .TrySynchronizeInterruptedInitialOwner(
                record,
                animation,
                canonicalAnimation: null,
                canonicalLowFrame: 0,
                canonicalHighFrame: 0,
                canonicalFramerate: 0f,
                motionTable: table,
                wireState: Wire(Ready));

        Assert.False(reinitialized);
        Assert.Same(sequencer, animation.Sequencer);
        Assert.Equal(pendingBefore, sequencer.Manager.PendingAnimations.Count());
        Assert.True(interpreter.MotionsPending());
        Assert.True(sink.ApplyMotion(Ready, 1f));
        Assert.Equal(Ready, sequencer.CurrentMotion);
    }

    [Fact]
    public void CompletedLegacyOwner_PreservesAnimationAndCurrentPhase()
    {
        var runtime = Runtime(new CountingResources());
        LiveEntityRecord record = RegisterAndMaterialize(runtime);
        record.InitialHydrationCompleted = true;
        var setup = new Setup();
        var retainedAnimation = new Animation();
        var canonicalAnimation = new Animation();
        var state = new LiveEntityAnimationState
        {
            Entity = record.WorldEntity!,
            Setup = setup,
            Animation = retainedAnimation,
            LowFrame = 2,
            HighFrame = 11,
            Framerate = 12f,
            Scale = 1f,
            PartTemplate = [],
            PartAvailability = [],
            CurrFrame = 7.25f,
            Sequencer = null,
        };

        bool synchronized = LiveEntityCreateAnimationSynchronization
            .TrySynchronizeInterruptedInitialOwner(
                record,
                state,
                canonicalAnimation,
                canonicalLowFrame: 0,
                canonicalHighFrame: 4,
                canonicalFramerate: 30f,
                motionTable: null,
                wireState: null);

        Assert.False(synchronized);
        Assert.Same(retainedAnimation, state.Animation);
        Assert.Equal(2, state.LowFrame);
        Assert.Equal(11, state.HighFrame);
        Assert.Equal(12f, state.Framerate);
        Assert.Equal(7.25f, state.CurrFrame);
    }

    [Fact]
    public void InterruptedInitialOwner_ReinitializesSameSequencerAndDrainsPairedQueues()
    {
        var runtime = Runtime(new CountingResources());
        LiveEntityRecord record = RegisterAndMaterialize(runtime);
        Assert.False(record.InitialHydrationCompleted);
        (Setup setup, MotionTable table, Loader loader) = MotionAssets();
        AnimationSequencer sequencer = SpawnMotionInitializer.Create(
            setup,
            table,
            loader,
            Wire(Ready));
        var animation = AnimationState(record.WorldEntity!, setup, sequencer);
        var body = MotionBody();
        var interpreter = new MotionInterpreter(body);
        var sink = new MotionTableDispatchSink(sequencer);
        interpreter.DefaultSink = sink;
        sequencer.MotionDoneTarget = interpreter.MotionDone;
        interpreter.DoMotion(Walk, new MovementParameters());
        Assert.True(interpreter.MotionsPending());
        Assert.NotEmpty(sequencer.Manager.PendingAnimations);

        bool reinitialized = LiveEntityCreateAnimationSynchronization
            .TrySynchronizeInterruptedInitialOwner(
                record,
                animation,
                canonicalAnimation: null,
                canonicalLowFrame: 0,
                canonicalHighFrame: 0,
                canonicalFramerate: 0f,
                motionTable: table,
                wireState: Wire(Ready));

        Assert.True(reinitialized);
        Assert.Same(sequencer, animation.Sequencer);
        Assert.False(interpreter.MotionsPending());
        Assert.Empty(sequencer.Manager.PendingAnimations);
        Assert.Equal(Ready, sequencer.CurrentMotion);
        Assert.True(sink.ApplyMotion(Walk, 1f));
        Assert.Equal(Walk, sequencer.CurrentMotion);
    }

    private const uint Guid = 0x70000061u;
    private const uint Cell = 0x01010001u;
    private const uint NonCombat = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;
    private const uint ReadyAnimation = 0x03000001u;
    private const uint WalkAnimation = 0x03000002u;

    private static LiveEntityRuntime Runtime(CountingResources resources)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new DatReaderWriter.DBObjs.LandBlock(),
            Array.Empty<WorldEntity>()));
        return LiveEntityRuntimeFixture.Create(
            spatial,
            resources,
            new DelegateLiveEntityRuntimeComponentLifecycle(_ => { }));
    }

    private static LiveEntityRecord RegisterAndMaterialize(LiveEntityRuntime runtime)
    {
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(
            Spawn(),
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = Guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                MeshRefs = [],
                ParentCellId = Cell,
            });
        return record;
    }

    private static LiveEntityRecord AssertRecord(LiveEntityRuntime runtime)
    {
        Assert.True(runtime.TryGetRecord(Guid, out LiveEntityRecord record));
        return record;
    }

    private static WorldSession.EntitySpawn Spawn() => new WorldSession.EntitySpawn(
        Guid,
        new CreateObject.ServerPosition(
            Cell,
            0f,
            0f,
            0f,
            1f,
            0f,
            0f,
            0f),
        0x02000001u,
        [],
        [],
        [],
        BasePaletteId: null,
        ObjScale: null,
        Name: "supersession fixture",
        ItemType: null,
        MotionState: null,
        MotionTableId: null,
        InstanceSequence: 1).WithConsistentPhysics();

    private static LiveEntityAnimationState AnimationState(
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
            Scale = 1f,
            PartTemplate = [],
            PartAvailability = [],
            Sequencer = sequencer,
        };

    private static PhysicsBody MotionBody() => new()
    {
        InWorld = true,
        State = PhysicsStateFlags.ReportCollisions,
        TransientState = TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable
            | TransientStateFlags.Active,
    };

    private static CreateObject.ServerMotionState Wire(uint command) => new(
        Stance: (ushort)(NonCombat & 0xFFFFu),
        ForwardCommand: (ushort)(command & 0xFFFFu));

    private static (Setup Setup, MotionTable Table, Loader Loader) MotionAssets()
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000001u);
        setup.DefaultScale.Add(Vector3.One);
        var loader = new Loader();
        loader.Add(ReadyAnimation, AnimationWithFrames(4));
        loader.Add(WalkAnimation, AnimationWithFrames(6));
        var table = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)NonCombat,
        };
        table.StyleDefaults[(DRWMotionCommand)NonCombat] =
            (DRWMotionCommand)Ready;
        table.Cycles[(int)((NonCombat << 16) | (Ready & 0xFFFFFFu))] =
            Motion(ReadyAnimation);
        table.Cycles[(int)((NonCombat << 16) | (Walk & 0xFFFFFFu))] =
            Motion(WalkAnimation);
        return (setup, table, loader);
    }

    private static Animation AnimationWithFrames(int count)
    {
        var animation = new Animation();
        for (int i = 0; i < count; i++)
        {
            var frame = new AnimationFrame(1);
            frame.Frames.Add(new Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            animation.PartFrames.Add(frame);
        }
        return animation;
    }

    private static MotionData Motion(uint animationId)
    {
        var data = new MotionData();
        QualifiedDataId<Animation> qualified = animationId;
        data.Anims.Add(new AnimData
        {
            AnimId = qualified,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = 30f,
        });
        return data;
    }

    private sealed class Loader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _animations = [];
        public void Add(uint id, Animation animation) => _animations[id] = animation;
        public Animation? LoadAnimation(uint id) =>
            _animations.GetValueOrDefault(id);
    }

    private sealed class CountingResources : ILiveEntityResourceLifecycle
    {
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public void Register(WorldEntity entity) => RegisterCount++;
        public void Unregister(WorldEntity entity) => UnregisterCount++;
    }
}
