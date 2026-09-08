using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Input;

public sealed class RetailLocalPlayerFrameControllerTests
{
    [Fact]
    public void F751BetweenPhases_CannotRelocateOrReplayCapturedOutboundMovement()
    {
        PlayerMovementController controller = CreateController();
        var calls = new List<string>();
        var preNetwork = new List<(PlayerState State, MovementResult Movement)>();
        var postNetwork = new List<PlayerState>();
        var local = CreateFrame(
            canPresentPlayer: () => true,
            getController: () => controller,
            movementInput: Input(() => new MovementInput(Forward: true)),
            resolveLocalEntityId: () => 7u,
            handleTargeting: () => calls.Add("target"),
            isHidden: () => false,
            project: (_, _, _) => calls.Add("project"),
            sendPreNetwork: (owner, movement, _) =>
            {
                calls.Add("pre-outbound");
                preNetwork.Add((owner.State, movement));
            },
            sendPostNetwork: (owner, _) =>
            {
                calls.Add("post-position");
                postNetwork.Add(owner.State);
            });
        var live = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(dt =>
            {
                calls.Add("objects");
                local.AdvanceBeforeNetwork(dt);
            }),
            new GpuWorldState(),
            new TestLiveSessionFramePhase(() =>
            {
                calls.Add("f751");
                controller.State = PlayerState.PortalSpace;
            }),
            new TestPostNetworkCommandFramePhase(() =>
            {
                calls.Add("commands");
                local.RunPostNetworkCommandPhase();
            }),
            new TestLiveSpatialReconcilePhase(() => calls.Add("spatial")));

        live.Tick(PhysicsBody.MaxQuantum);
        Assert.True(local.TryGetPresentationAfterNetwork(out var presentation));

        Assert.Equal(
            [
                "objects", "target", "project", "pre-outbound", "f751",
                "commands", "project", "post-position", "spatial",
            ],
            calls);
        Assert.Single(preNetwork);
        Assert.Equal(PlayerState.InWorld, preNetwork[0].State);
        Assert.True(preNetwork[0].Movement.ShouldSendMovementEvent);
        Assert.Equal([PlayerState.PortalSpace], postNetwork);
        Assert.Equal(PlayerState.PortalSpace, controller.State);
        Assert.True(presentation.AdvancedBeforeNetwork);
    }

    [Fact]
    public void OwnerCreatedByInboundPass_IsProjectedWithoutPhysicsOrOutboundTick()
    {
        PlayerMovementController? controller = null;
        int projections = 0;
        int preNetwork = 0;
        int postNetwork = 0;
        var local = CreateFrame(
            canPresentPlayer: () => controller is not null,
            getController: () => controller,
            movementInput: Input(() => new MovementInput(Forward: true)),
            resolveLocalEntityId: () => 9u,
            handleTargeting: () => { },
            isHidden: () => false,
            project: (_, _, _) => projections++,
            sendPreNetwork: (_, _, _) => preNetwork++,
            sendPostNetwork: (_, _) => postNetwork++);

        local.AdvanceBeforeNetwork(1f / 60f);
        controller = CreateController();
        float timeBefore = controller.SimTimeSeconds;

        Assert.True(local.TryGetPresentationAfterNetwork(out var presentation));

        Assert.False(presentation.AdvancedBeforeNetwork);
        Assert.Equal(timeBefore, controller.SimTimeSeconds);
        Assert.Equal(1, projections);
        Assert.Equal(0, preNetwork);
        Assert.Equal(0, postNetwork);
        Assert.False(presentation.Movement.ShouldSendMovementEvent);
    }

    [Fact]
    public void OrdinaryInboundRootMutation_IsReconciledWithoutSecondPhysicsTick()
    {
        PlayerMovementController controller = CreateController();
        Vector3 projectedRoot = default;
        var local = CreateFrame(
            canPresentPlayer: () => true,
            getController: () => controller,
            movementInput: Input(() => new MovementInput()),
            resolveLocalEntityId: () => 7u,
            handleTargeting: () => { },
            isHidden: () => false,
            project: (_, movement, _) => projectedRoot = movement.RenderPosition,
            sendPreNetwork: (_, _, _) => { },
            sendPostNetwork: (_, _) => { });
        var live = new RetailLiveFrameCoordinator(
            new TestLiveObjectFramePhase(local.AdvanceBeforeNetwork),
            new GpuWorldState(),
            new TestLiveSessionFramePhase(
                () => projectedRoot = new Vector3(999f, 999f, 999f)),
            local,
            new TestLiveSpatialReconcilePhase(() => { }));

        live.Tick(1f / 60f);
        float timeAfterOneTick = controller.SimTimeSeconds;

        Assert.Equal(controller.RenderPosition, projectedRoot);
        Assert.Equal(1f / 60f, timeAfterOneTick);
    }

    [Fact]
    public void TargetManager_ObservesPostTransitionPlayerPosition()
    {
        PlayerMovementController controller = CreateController();
        Vector3 initial = controller.Position;
        Vector3 positionSeenByTargetManager = new(float.NaN);
        var local = CreateFrame(
            canPresentPlayer: () => true,
            getController: () => controller,
            movementInput: Input(() => new MovementInput(Forward: true)),
            resolveLocalEntityId: () => 7u,
            handleTargeting: () => positionSeenByTargetManager = controller.Position,
            isHidden: () => false,
            project: (_, _, _) => { },
            sendPreNetwork: (_, _, _) => { },
            sendPostNetwork: (_, _) => { });

        local.AdvanceBeforeNetwork(PhysicsBody.MaxQuantum);

        Assert.NotEqual(initial, controller.Position);
        Assert.Equal(controller.Position, positionSeenByTargetManager);
    }

    [Fact]
    public void FrozenObject_IsPresentedWithoutPhysicsInputOrOutboundState()
    {
        PlayerMovementController controller = CreateController();
        Vector3 initial = controller.Position;
        int inputCaptures = 0;
        int projections = 0;
        int preNetwork = 0;
        int postNetwork = 0;
        var local = CreateFrame(
            canPresentPlayer: () => true,
            getController: () => controller,
            movementInput: Input(() =>
            {
                inputCaptures++;
                return new MovementInput(Forward: true);
            }),
            resolveLocalEntityId: () => 7u,
            handleTargeting: () => throw new InvalidOperationException(
                "Frozen object advanced its manager tail."),
            isHidden: () => false,
            project: (_, _, _) => projections++,
            sendPreNetwork: (_, _, _) => preNetwork++,
            sendPostNetwork: (_, _) => postNetwork++,
            objectClockDisposition: () =>
                RetailObjectClockDisposition.Suspend);

        local.AdvanceBeforeNetwork(PhysicsBody.MaxQuantum);
        local.RunPostNetworkCommandPhase();
        Assert.True(local.TryGetPresentationAfterNetwork(out var presentation));

        Assert.Equal(initial, controller.Position);
        Assert.Equal(PhysicsBody.MaxQuantum, controller.SimTimeSeconds);
        Assert.Equal(0, inputCaptures);
        Assert.Equal(2, projections);
        Assert.Equal(0, preNetwork);
        Assert.Equal(0, postNetwork);
        Assert.False(presentation.AdvancedBeforeNetwork);
    }

    [Fact]
    public void HiddenPoseIsDirtyOnlyWhenACompleteObjectQuantumRuns()
    {
        PlayerMovementController controller = CreateController();
        var local = CreateFrame(
            canPresentPlayer: () => true,
            getController: () => controller,
            movementInput: Input(() => new MovementInput()),
            resolveLocalEntityId: () => 7u,
            handleTargeting: () => { },
            isHidden: () => true,
            project: (_, _, _) => { },
            sendPreNetwork: (_, _, _) => { },
            sendPostNetwork: (_, _) => { });

        local.AdvanceBeforeNetwork(PhysicsBody.MaxQuantum);
        Assert.True(local.HiddenPartPoseDirty);

        local.AdvanceBeforeNetwork(PhysicsBody.MinQuantum / 2f);
        Assert.False(local.HiddenPartPoseDirty);
    }

    [Fact]
    public void InvalidElapsed_PublishesSnapshotWithoutInputOrNetworkCallbacks()
    {
        PlayerMovementController controller = CreateController();
        int inputCaptures = 0;
        int targeting = 0;
        int projections = 0;
        int preNetwork = 0;
        int postNetwork = 0;
        var local = CreateFrame(
            canPresentPlayer: () => true,
            getController: () => controller,
            movementInput: Input(() =>
            {
                inputCaptures++;
                return new MovementInput(Forward: true);
            }),
            resolveLocalEntityId: () => 7u,
            handleTargeting: () => targeting++,
            isHidden: () => false,
            project: (_, _, _) => projections++,
            sendPreNetwork: (_, _, _) => preNetwork++,
            sendPostNetwork: (_, _) => postNetwork++);
        float initialTime = controller.SimTimeSeconds;
        Vector3 initialPosition = controller.Position;

        foreach (float elapsed in new[]
                 {
                     float.NaN,
                     float.PositiveInfinity,
                     float.NegativeInfinity,
                     -1f,
                     0f,
                 })
        {
            local.AdvanceBeforeNetwork(elapsed);
            local.RunPostNetworkCommandPhase();
            Assert.True(local.TryGetPresentationAfterNetwork(out var frame));
            Assert.False(frame.AdvancedBeforeNetwork);
            Assert.False(local.HiddenPartPoseDirty);
        }

        Assert.Equal(initialTime, controller.SimTimeSeconds);
        Assert.Equal(initialPosition, controller.Position);
        Assert.Equal(0, inputCaptures);
        Assert.Equal(0, targeting);
        Assert.Equal(0, preNetwork);
        Assert.Equal(0, postNetwork);
        Assert.Equal(10, projections);
    }

    [Fact]
    public void GraphicalWrapperAndDirectRuntimeFrameProduceIdenticalTrace()
    {
        List<string> Run(bool graphical)
        {
            PlayerMovementController controller = CreateController();
            var trace = new List<string>();
            var host = new TestLocalPlayerFrameRuntime(
                () => true,
                () => controller,
                () => 7u,
                () => trace.Add("target"),
                () => false,
                (_, movement, hidden) =>
                    trace.Add(
                        $"project:{movement.CellId:X8}:"
                        + $"{movement.ForwardCommand:X8}:"
                        + $"{movement.ShouldSendMovementEvent}:"
                        + $"{hidden}"),
                (_, movement, hidden) =>
                    trace.Add(
                        $"pre:{movement.ForwardCommand:X8}:"
                        + $"{movement.ShouldSendMovementEvent}:"
                        + $"{hidden}"),
                (_, hidden) => trace.Add($"post:{hidden}"),
                () => RetailObjectClockDisposition.Advance);
            IMovementInputSource input = Input(
                () => new MovementInput(
                    Forward: true,
                    Run: true));

            if (graphical)
            {
                var frame = new RetailLocalPlayerFrameController(
                    host,
                    input);
                frame.AdvanceBeforeNetwork(PhysicsBody.MaxQuantum);
                frame.RunPostNetworkCommandPhase();
            }
            else
            {
                var frame = new RuntimeLocalPlayerFrameController(
                    host,
                    input);
                frame.AdvanceBeforeNetwork(PhysicsBody.MaxQuantum);
                frame.RunPostNetworkCommandPhase();
            }

            return trace;
        }

        Assert.Equal(Run(graphical: true), Run(graphical: false));
    }

    private static PlayerMovementController CreateController()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = i;

        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        var clock = new RetailObjectQuantumClock();
        var controller = new PlayerMovementController(engine, clock);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001u, new Vector3(96f, 96f, 50f));
        Assert.True(clock.IsActive);
        return controller;
    }

    private static IMovementInputSource Input(Func<MovementInput> capture) =>
        new TestMovementInputSource(capture);

    private static RetailLocalPlayerFrameController CreateFrame(
        Func<bool> canPresentPlayer,
        Func<PlayerMovementController?> getController,
        IMovementInputSource movementInput,
        Func<uint> resolveLocalEntityId,
        Action handleTargeting,
        Func<bool> isHidden,
        Action<PlayerMovementController, MovementResult, bool> project,
        Action<PlayerMovementController, MovementResult, bool> sendPreNetwork,
        Action<PlayerMovementController, bool> sendPostNetwork,
        Func<RetailObjectClockDisposition>? objectClockDisposition = null) =>
        new(
            new TestLocalPlayerFrameRuntime(
                canPresentPlayer,
                getController,
                resolveLocalEntityId,
                handleTargeting,
                isHidden,
                project,
                sendPreNetwork,
                sendPostNetwork,
                objectClockDisposition),
            movementInput);

    private sealed class TestLocalPlayerFrameRuntime(
        Func<bool> canPresentPlayer,
        Func<PlayerMovementController?> getController,
        Func<uint> resolveLocalEntityId,
        Action handleTargeting,
        Func<bool> isHidden,
        Action<PlayerMovementController, MovementResult, bool> project,
        Action<PlayerMovementController, MovementResult, bool> sendPreNetwork,
        Action<PlayerMovementController, bool> sendPostNetwork,
        Func<RetailObjectClockDisposition>? objectClockDisposition)
        : ILocalPlayerFrameRuntime
    {
        public bool CanPresentPlayer => canPresentPlayer();
        public PlayerMovementController? Controller => getController();
        public uint ResolveLocalEntityId() => resolveLocalEntityId();
        public void HandleTargeting() => handleTargeting();
        public bool IsHidden => isHidden();
        public RetailObjectClockDisposition ObjectClockDisposition =>
            objectClockDisposition?.Invoke() ?? RetailObjectClockDisposition.Advance;

        public void Project(
            PlayerMovementController controller,
            MovementResult movement,
            bool hidden) => project(controller, movement, hidden);

        public void SendPreNetwork(
            PlayerMovementController controller,
            MovementResult movement,
            bool hidden) => sendPreNetwork(controller, movement, hidden);

        public void SendPostNetwork(
            PlayerMovementController controller,
            bool hidden) => sendPostNetwork(controller, hidden);
    }

    private sealed class TestMovementInputSource(Func<MovementInput> capture)
        : IMovementInputSource
    {
        public MovementInput Capture() => capture();
    }
}
