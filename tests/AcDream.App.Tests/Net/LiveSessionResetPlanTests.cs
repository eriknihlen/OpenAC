using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Player;
using AcDream.Core.Selection;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Core.World;
using AcDream.Runtime;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Net;

public sealed class LiveSessionResetPlanTests
{
    private sealed class FailingOnceResources : ILiveEntityResourceLifecycle
    {
        private bool _failurePending = true;

        public void Register(WorldEntity entity) { }

        public void Unregister(WorldEntity entity)
        {
            if (_failurePending)
            {
                _failurePending = false;
                throw new InvalidOperationException("transient teardown");
            }
        }
    }

    [Fact]
    public void Execute_EmptyPlanConverges()
    {
        var plan = new LiveSessionResetPlan([]);

        plan.Execute();
        plan.Execute();

        Assert.Empty(plan.StageNames);
    }

    [Fact]
    public void Execute_AttemptsEveryStageAndAggregatesNamedFailures()
    {
        var calls = new List<string>();
        var plan = new LiveSessionResetPlan(
        [
            new("first", () =>
            {
                calls.Add("first");
                throw new InvalidOperationException("one");
            }),
            new("second", () => calls.Add("second")),
            new("third", () =>
            {
                calls.Add("third");
                throw new ArgumentException("three");
            }),
        ]);

        AggregateException error = Assert.Throws<AggregateException>(plan.Execute);

        Assert.Equal(["first", "second", "third"], calls);
        Assert.Collection(
            error.InnerExceptions,
            first => Assert.Equal(
                "first",
                Assert.IsType<LiveSessionResetStageException>(first).StageName),
            third => Assert.Equal(
                "third",
                Assert.IsType<LiveSessionResetStageException>(third).StageName));
    }

    [Fact]
    public void Execute_AfterFailedAttemptCanConvergeWithoutSkippingStages()
    {
        int firstCalls = 0;
        int secondCalls = 0;
        var plan = new LiveSessionResetPlan(
        [
            new("transient", () =>
            {
                firstCalls++;
                if (firstCalls == 1)
                    throw new InvalidOperationException("transient");
            }),
            new("always", () => secondCalls++),
        ]);

        Assert.Throws<AggregateException>(plan.Execute);
        plan.Execute();

        Assert.Equal(2, firstCalls);
        Assert.Equal(2, secondCalls);
    }

    [Fact]
    public void Execute_BlocksNextSessionUntilOriginRetirementConverges()
    {
        const uint sourceId = 0x5353FFFFu;
        var state = new GpuWorldState();
        state.AddLandblock(new LoadedLandblock(
            sourceId,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var origin = new LiveWorldOriginState();
        Assert.True(origin.TryInitialize(0x53, 0x53));
        bool failRetirement = true;
        var controller = new StreamingController(
            enqueueLoad: static (_, _) => { },
            enqueueUnload: static _ => { },
            drainCompletions: static _ => Array.Empty<LandblockStreamResult>(),
            applyTerrain: static (_, _) => { },
            state,
            nearRadius: 0,
            farRadius: 0,
            removeTerrain: _ =>
            {
                if (failRetirement)
                    throw new InvalidOperationException("injected ending-session failure");
            });
        var recenter = new StreamingOriginRecenterCoordinator(controller, origin);
        int identityResets = 0;
        var plan = new LiveSessionResetPlan(
        [
            new("teleport transit", () =>
            {
                if (!recenter.Reset(sessionEnding: true))
                {
                    throw new InvalidOperationException(
                        "streaming-origin retirement remains pending");
                }
            }),
            new("session identity", () =>
            {
                identityResets++;
                origin.Reset();
            }),
        ]);

        AggregateException blocked = Assert.Throws<AggregateException>(plan.Execute);
        LiveSessionResetStageException failure = Assert.IsType<LiveSessionResetStageException>(
            Assert.Single(blocked.InnerExceptions));
        Assert.Equal("teleport transit", failure.StageName);
        Assert.Equal(1, identityResets);

        failRetirement = false;
        for (int frame = 0;
             frame < 64
             && !controller.IsOriginRecenterRetirementComplete();
             frame++)
        {
            controller.Tick(0x53, 0x53);
        }
        plan.Execute();

        Assert.Equal(2, identityResets);
        Assert.False(recenter.IsPending);
        Assert.False(origin.IsKnown);
        Assert.True(origin.TryInitialize(0x61, 0x62));
        Assert.Equal((0x61, 0x62), (origin.CenterX, origin.CenterY));
    }

    [Fact]
    public void Execute_ReentrantAttemptIsReportedButLaterStagesStillRun()
    {
        LiveSessionResetPlan? plan = null;
        bool tailRan = false;
        plan = new LiveSessionResetPlan(
        [
            new("reentrant", () => plan!.Execute()),
            new("tail", () => tailRan = true),
        ]);

        AggregateException error = Assert.Throws<AggregateException>(plan.Execute);

        Assert.True(tailRan);
        LiveSessionResetStageException stage = Assert.IsType<LiveSessionResetStageException>(
            Assert.Single(error.InnerExceptions));
        Assert.Equal("reentrant", stage.StageName);
        Assert.IsType<InvalidOperationException>(stage.InnerException);
    }

    [Fact]
    public void Constructor_RejectsDuplicateStageNames()
    {
        Assert.Throws<ArgumentException>(() => new LiveSessionResetPlan(
        [
            new("duplicate", () => { }),
            new("duplicate", () => { }),
        ]));
    }

    [Fact]
    public void Manifest_PreservesHostOrderAndForwardsExactRetiringGeneration()
    {
        var calls = new List<string>();
        RuntimeGenerationToken observed = default;
        Action Stage(string name) => () => calls.Add(name);
        var plan = LiveSessionResetManifest.Create(new()
        {
            MouseCapture = Stage("mouse capture"),
            PlayerPresentation = Stage("player presentation"),
            TeleportPresentation = Stage("teleport presentation"),
            WorldAudio = Stage("world audio"),
            SessionDialogs = Stage("session dialogs"),
            SettingsCharacterContext = Stage("settings character context"),
            EquippedChildren = Stage("equipped children"),
            InteractionPresentation = Stage("interaction presentation"),
            SelectionPresentation = Stage("selection presentation"),
            ParticleVisibility = Stage("particle visibility"),
            InboundEventFifo = Stage("inbound event fifo"),
            LiveLiveness = Stage("live liveness"),
            RuntimeGeneration = generation =>
            {
                observed = generation;
                calls.Add("runtime generation");
            },
            SessionIdentityPresentation = _ =>
                calls.Add("session identity presentation"),
            NetworkEffects = Stage("network effects"),
            AnimationHookFrames = Stage("animation hook frames"),
            LivePresentation = Stage("live presentation"),
            RemoteMovementDiagnostics =
                Stage("remote movement diagnostics"),
        });

        var retiring = new RuntimeGenerationToken(73);
        plan.Execute(retiring);

        Assert.Equal(ExpectedManifestNames(), plan.StageNames);
        Assert.Equal(ExpectedManifestNames(), calls);
        Assert.Equal(retiring, observed);
    }

    [Fact]
    public void GraphicalResetHost_RetriesExactProjectionBeforeIdentityClears()
    {
        const uint player = 0x50000001u;
        const uint landblock = 0x0101FFFFu;
        using GameRuntime runtime = GameRuntimeTestFactory.Create();
        runtime.PlayerIdentity.ServerGuid = player;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            landblock,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var live = new LiveEntityRuntime(
            spatial,
            new FailingOnceResources(),
            runtime.EntityObjects);
        runtime.EntityObjects.RegisterEntity(Spawn(player, 1, 1, 0x01010001u));
        live.MaterializeLiveEntity(
            player,
            0x01010001u,
            id => Entity(id, player));
        var host = new GraphicalRuntimeGenerationResetHost(
            live,
            static () => { });
        var retiring = new RuntimeGenerationToken(3);

        RuntimeGenerationResetStageException first =
            Assert.Throws<RuntimeGenerationResetStageException>(
                () => runtime.ResetGeneration(retiring, host));

        Assert.Equal(
            RuntimeGenerationResetStage.RetireEntities,
            first.Stage);
        Assert.Equal(player, runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(1, live.PendingTeardownCount);
        Assert.Equal(1, live.MaterializedCount);

        runtime.ResetGeneration(retiring, host);

        Assert.Equal(0u, runtime.PlayerIdentity.ServerGuid);
        Assert.Equal(0, live.Count);
        Assert.Equal(0, live.PendingTeardownCount);
        Assert.Equal(0, live.MaterializedCount);
    }

    private static string[] ExpectedManifestNames() =>
    [
        "mouse capture",
        "player presentation",
        "teleport presentation",
        "world audio",
        "session dialogs",
        "settings character context",
        "equipped children",
        "interaction presentation",
        "selection presentation",
        "particle visibility",
        "inbound event fifo",
        "live liveness",
        "runtime generation",
        "session identity presentation",
        "network effects",
        "animation hook frames",
        "live presentation",
        "remote movement diagnostics",
    ];

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = System.Numerics.Vector3.Zero,
        Rotation = System.Numerics.Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        ushort positionSequence,
        uint cell)
    {
        const PhysicsStateFlags state = PhysicsStateFlags.ReportCollisions;
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            positionSequence, 1, 1, 1, 0, 1, 0, 1, instance);
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
            PhysicsState: (uint)state,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: positionSequence,
            Physics: physics);
    }
}
