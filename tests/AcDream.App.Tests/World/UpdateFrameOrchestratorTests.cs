using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Net;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.App.Tests.Architecture;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;

namespace AcDream.App.Tests.World;

public sealed class UpdateFrameOrchestratorTests
{
    [Fact]
    public void Tick_PreservesTheCompleteAcceptedPhaseGraph()
    {
        var calls = new List<string>();
        UpdateFrameOrchestrator frame = Create(calls);

        frame.Tick(new UpdateFrameInput(1.0 / 60.0));

        Assert.Equal(
            [
                "teardown",
                "clock",
                "streaming",
                "input",
                "objects",
                "network",
                "commands",
                "ordinary-reconcile",
                "liveness",
                "teleport",
                "auto-entry",
                "camera",
                "commit",
            ],
            calls);
    }

    [Fact]
    public void ConditionalReconciles_RemainAtTheirOwningEdges()
    {
        var calls = new List<string>();
        UpdateFrameOrchestrator frame = Create(
            calls,
            teleportPlace: true,
            inboundCreatedPlayer: true);

        frame.Tick(new UpdateFrameInput(1.0 / 60.0));

        Assert.Equal(
            [
                "teardown",
                "clock",
                "streaming",
                "input",
                "objects",
                "network",
                "commands",
                "ordinary-reconcile",
                "liveness",
                "teleport-place",
                "teleport-reconcile",
                "teleport-reveal",
                "auto-entry",
                "inbound-player-projection",
                "inbound-player-reconcile",
                "camera",
                "commit",
            ],
            calls);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(double.MaxValue)]
    public void InvalidSimulationDelta_BecomesZeroWithoutSuppressingOuterPhases(
        double hostDelta)
    {
        var calls = new List<string>();
        var observed = new FrameObservations();
        UpdateFrameOrchestrator frame = Create(calls, observed: observed);

        frame.Tick(new UpdateFrameInput(hostDelta));

        Assert.Equal(
            [
                "teardown", "clock", "streaming", "input", "objects",
                "network", "commands", "ordinary-reconcile", "liveness",
                "teleport", "auto-entry", "camera", "commit",
            ],
            calls);
        Assert.Equal([0.0], observed.PublishedTimes);
        Assert.Equal([0.0], observed.Input.Select(value => value.SimulationDeltaSeconds));
        Assert.Equal([0f], observed.LiveDeltas);
        Assert.Equal([0f], observed.TeleportDeltas);
        Assert.Equal([0.0], observed.Camera.Select(value => value.SimulationDeltaSeconds));
        Assert.All(
            observed.Input.Concat(observed.Camera),
            timing => Assert.Equal(0.0, timing.ScriptTime));
    }

    [Fact]
    public void Clock_AdvancesOnceAndPublishesTheSameTimeToEveryConsumer()
    {
        var calls = new List<string>();
        var observed = new FrameObservations();
        UpdateFrameOrchestrator frame = Create(calls, observed: observed);

        frame.Tick(new UpdateFrameInput(0.25));
        frame.Tick(new UpdateFrameInput(double.NaN));
        frame.Tick(new UpdateFrameInput(0.5));

        Assert.Equal([0.25, 0.25, 0.75], observed.PublishedTimes);
        Assert.Equal(
            [0.25, 0.25, 0.75],
            observed.Input.Select(value => value.ScriptTime));
        Assert.Equal(
            [0.25, 0.25, 0.75],
            observed.Camera.Select(value => value.ScriptTime));
        Assert.Equal([0.25f, 0f, 0.5f], observed.LiveDeltas);
        Assert.Equal([0.25f, 0f, 0.5f], observed.TeleportDeltas);
        Assert.Equal(
            [0.25, 0.0, 0.5],
            observed.Input.Select(value => value.SimulationDeltaSeconds));
        Assert.Equal(
            [0.25, 0.0, 0.5],
            observed.Camera.Select(value => value.SimulationDeltaSeconds));
    }

    [Fact]
    public void QuiescedWorld_FreezesScriptClockAndLivenessWhilePortalPhasesContinue()
    {
        var calls = new List<string>();
        var observed = new FrameObservations();
        var transit = new RuntimeWorldTransitState();
        var availability = new WorldGenerationAvailabilityState(transit);
        long generation =
            RuntimeWorldTransitTestDriver.BeginPortal(
                transit,
                0x11340021u);
        UpdateFrameOrchestrator frame = Create(
            calls,
            observed: observed,
            availability: availability);

        frame.Tick(new UpdateFrameInput(0.25));
        frame.Tick(new UpdateFrameInput(0.25));
        Assert.True(transit.AcknowledgeDestinationReadiness(
            new RuntimeDestinationReadiness(
                generation,
                0x11340021u,
                IsIndoor: false,
                IsUnhydratable: false,
                RequiredRenderRadius: 1,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true)));
        Assert.True(RuntimeWorldTransitTestDriver.MaterializePortal(
            transit,
            generation,
            0x11340021u));
        transit.Complete(generation);
        frame.Tick(new UpdateFrameInput(0.25));

        Assert.Equal([0.0, 0.0, 0.25], observed.PublishedTimes);
        Assert.Equal(1, calls.Count(call => call == "liveness"));
        Assert.Equal(3, calls.Count(call => call == "streaming"));
        Assert.Equal(3, calls.Count(call => call == "network"));
        Assert.Equal(3, calls.Count(call => call == "teleport"));
        Assert.Equal(3, calls.Count(call => call == "camera"));
        Assert.Equal(3, calls.Count(call => call == "commit"));
        Assert.Equal([0.25f, 0.25f, 0.25f], observed.LiveDeltas);
        Assert.Equal([0.25f, 0.25f, 0.25f], observed.TeleportDeltas);
    }

    [Fact]
    public void Clock_RejectsFiniteDeltaAboveTheSinglePrecisionConsumerRange()
    {
        double tooLarge = (double)float.MaxValue * 2.0;

        Assert.True(double.IsFinite(tooLarge));
        Assert.Equal(0.0, UpdateFrameClock.NormalizeDeltaSeconds(tooLarge));
    }

    [Fact]
    public void AggregateTeardownFailure_IsReportedAndRetriedNextFrame()
    {
        var calls = new List<string>();
        var teardown = new RecordingTeardown(calls)
        {
            Failure = new AggregateException(new InvalidOperationException("transient")),
        };
        var failures = new RecordingFailureSink();
        UpdateFrameOrchestrator frame = Create(
            calls,
            teardown: teardown,
            failureSink: failures);

        frame.Tick(new UpdateFrameInput(0.1));
        teardown.Failure = null;
        frame.Tick(new UpdateFrameInput(0.1));

        Assert.Equal(2, teardown.Attempts);
        Assert.Single(failures.Errors);
        string[] oneFrame =
        [
            "teardown", "clock", "streaming", "input", "objects", "network",
            "commands", "ordinary-reconcile", "liveness", "teleport",
            "auto-entry", "camera", "commit",
        ];
        Assert.Equal(oneFrame.Concat(oneFrame), calls);
    }

    [Fact]
    public void NonAggregateTeardownFailure_PropagatesBeforeTimeOrLaterPhases()
    {
        var calls = new List<string>();
        var teardown = new RecordingTeardown(calls)
        {
            Failure = new InvalidOperationException("fatal"),
        };
        UpdateFrameOrchestrator frame = Create(calls, teardown: teardown);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => frame.Tick(new UpdateFrameInput(0.1)));

        Assert.Equal("fatal", error.Message);
        Assert.Equal(["teardown"], calls);
    }

    [Fact]
    public void OrchestrationTypes_UseOnlyTheExplicitTypedOwnerGraph()
    {
        Type[] expectedFieldTypes =
        [
            typeof(IUpdateFrameTeardownPhase),
            typeof(IUpdateFrameFailureSink),
            typeof(UpdateFrameClock),
            typeof(IUpdateFrameScriptClockPublisher),
            typeof(IStreamingFramePhase),
            typeof(IGameplayInputFramePhase),
            typeof(IRetailLiveFramePhase),
            typeof(ILiveEntityLivenessFramePhase),
            typeof(ILocalPlayerTeleportFramePhase),
            typeof(IPlayerModeAutoEntryFramePhase),
            typeof(ICameraFramePhase),
            typeof(IUpdateFrameCommitPhase),
            typeof(IWorldGenerationAvailability),
        ];

        FieldInfo[] fields = typeof(UpdateFrameOrchestrator).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(
            expectedFieldTypes.OrderBy(type => type.FullName),
            fields.Select(field => field.FieldType).OrderBy(type => type.FullName));
        Assert.All(fields, field =>
        {
            Assert.NotEqual(typeof(GameWindow), field.FieldType);
            Assert.False(typeof(Delegate).IsAssignableFrom(field.FieldType));
        });

        Type[] phaseInterfaces = expectedFieldTypes.Where(type => type.IsInterface).ToArray();
        Assert.DoesNotContain(
            phaseInterfaces,
            phaseInterface => phaseInterface.IsAssignableFrom(typeof(GameWindow)));

        Type[] productionPhaseOwners = typeof(GameWindow).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Where(type => phaseInterfaces.Any(contract => contract.IsAssignableFrom(type)))
            .ToArray();
        foreach (Type owner in productionPhaseOwners)
        {
            FieldInfo[] ownerFields = owner.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.DoesNotContain(ownerFields, field => field.FieldType == typeof(GameWindow));
            Assert.DoesNotContain(
                ownerFields,
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }

        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_physicsScriptGameTime");
    }

    [Fact]
    public void PhysicsScriptClockConsumers_RetainTheTypedSourceNotGameWindowClosures()
    {
        Type[] consumers =
        [
            typeof(DatLiveEntityProjectionMaterializer),
            typeof(AcDream.App.Physics.LiveEntityNetworkUpdateController),
        ];

        foreach (Type consumer in consumers)
        {
            FieldInfo clock = Assert.Single(
                consumer.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => field.Name == "_gameTime");
            Assert.Equal(typeof(IPhysicsScriptTimeSource), clock.FieldType);
            Assert.DoesNotContain(
                consumer.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => field.FieldType == typeof(GameWindow));
        }

        Assert.False(typeof(IPhysicsScriptTimeSource).IsAssignableFrom(typeof(GameWindow)));
    }

    [Fact]
    public void ProductionFrame_PublishesPhysicsScriptTimeExactlyOnce()
    {
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.Name == nameof(PhysicsScriptClockPublisher.PublishTime));

        MethodInfo publish = RequiredMethod(
            typeof(PhysicsScriptClockPublisher),
            nameof(PhysicsScriptClockPublisher.PublishTime));
        Assert.Single(
            CompiledCallGraph.Read(publish),
            call => call.Target.Name == "PublishTime");

        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(FrameRootCompositionPhase),
                "ComposeCore")),
            call => call.Target.DeclaringType == typeof(PhysicsScriptClockPublisher)
                && call.Target.IsConstructor);
    }

    [Fact]
    public void ExtractedLiveObjectSource_PinsRetailAndRegisteredAdaptationOrder()
    {
        MethodInfo live = RequiredMethod(typeof(LiveObjectFrameController), "TickCore");
        AssertNamedCallOrder(
            live,
            ("RetailLocalPlayerFrameController", "AdvanceBeforeNetwork"),
            ("SelectionInteractionController", "DrainOutbound"),
            ("LiveEntityAnimationScheduler", "Tick"),
            ("RetailStaticAnimatingObjectScheduler", "Tick"),
            ("LiveEntityAnimationPresenter", "Present"),
            ("EquippedChildRenderController", "Tick"),
            ("RetailStaticAnimatingObjectScheduler", "ProcessHooks"),
            ("LiveEffectFrameController", "Tick"));
        AssertNamedCallOrder(
            RequiredMethod(typeof(LiveEffectFrameController), "Tick"),
            ("TranslucencyFadeManager", "AdvanceAll"),
            ("AnimationHookFrameQueue", "Drain"),
            ("EntityEffectController", "RefreshLiveOwnerPoses"),
            ("ParticleHookSink", "RefreshAttachedEmitters"),
            ("LiveEntityLightController", "Refresh"),
            ("ParticleVisibilityController", "Apply"),
            ("ParticleSystem", "Tick"),
            ("PhysicsScriptRunner", "Tick"));
        AssertNamedCallOrder(
            RequiredMethod(typeof(LiveSpatialPresentationReconciler), "Reconcile"),
            ("EntityEffectController", "RefreshLiveOwnerPoses"),
            ("EquippedChildRenderController", "ReconcileSpatialMutations"),
            ("ParticleHookSink", "RefreshAttachedEmitters"),
            ("LiveEntityLightController", "Refresh"));

        AssertSingleNamedCall(live, "RetailStaticAnimatingObjectScheduler", "Tick");
        AssertSingleNamedCall(live, "RetailStaticAnimatingObjectScheduler", "ProcessHooks");
        AssertSingleNamedCall(live, "LiveEffectFrameController", "Tick");
        AssertSingleNamedCall(
            RequiredMethod(typeof(LiveEffectFrameController), "Tick"),
            "ParticleSystem",
            "Tick");
        AssertSingleNamedCall(
            RequiredMethod(typeof(LiveEffectFrameController), "Tick"),
            "PhysicsScriptRunner",
            "Tick");
    }

    [Fact]
    public void PlacementRetryRunsAfterStreamingAndInboundBeforeCommandReconcile()
    {
        AssertCallOrder(
            RequiredMethod(typeof(UpdateFrameOrchestrator), nameof(UpdateFrameOrchestrator.Tick)),
            (typeof(IStreamingFramePhase), nameof(IStreamingFramePhase.Tick)),
            (typeof(IRetailLiveFramePhase), nameof(IRetailLiveFramePhase.Tick)));
        AssertCallOrder(
            RequiredMethod(typeof(RetailLiveFrameCoordinator), nameof(RetailLiveFrameCoordinator.Tick)),
            (typeof(IRuntimeLiveSessionFramePhase), nameof(IRuntimeLiveSessionFramePhase.Tick)),
            (typeof(IRuntimePlacementProjectionRetryPhase),
                nameof(IRuntimePlacementProjectionRetryPhase.RetryPending)),
            (typeof(IPostNetworkCommandFramePhase),
                nameof(IPostNetworkCommandFramePhase.RunPostNetworkCommandPhase)),
            (typeof(ILiveSpatialReconcilePhase), nameof(ILiveSpatialReconcilePhase.Reconcile)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiveFrameRetriesPlacementAfterInboundEvenWhenWorldIsQuiesced(
        bool worldAvailable)
    {
        var calls = new List<string>();
        var phases = new RecordingLivePhases(calls)
        {
            IsWorldAvailable = worldAvailable,
        };
        var frame = new RetailLiveFrameCoordinator(
            phases,
            new GpuWorldState(),
            phases,
            phases,
            phases,
            phases,
            phases,
            phases);

        frame.Tick(1f / 60f);

        Assert.Equal(
            worldAvailable
                ? ["objects", "network", "placement-retry", "commands", "reconcile"]
                : ["network", "placement-retry", "commands", "render-sync"],
            calls);
    }

    [Fact]
    public void GameWindow_ComposesTheLiveFrameOwnersWithoutOwningTheirBodies()
    {
        IReadOnlyList<CompiledCall> session = CompiledCallGraph.Read(
            RequiredMethod(typeof(SessionPlayerCompositionPhase), "CompleteSessionPlayer"));
        Assert.Contains(session, call =>
            call.Target.DeclaringType == typeof(LiveObjectFrameController)
            && call.Target.IsConstructor);
        Assert.Contains(session, call =>
            call.Target.DeclaringType == typeof(LiveSpatialPresentationReconciler)
            && call.Target.IsConstructor);
        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(FrameRootCompositionPhase),
                "ComposeCore")),
            call => call.Target.DeclaringType == typeof(RetailLiveFrameCoordinator)
                && call.Target.IsConstructor);

        IReadOnlyList<CompiledCall> windowCalls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));
        Assert.DoesNotContain(windowCalls, call =>
            call.Target.Name is "AdvanceLiveObjectRuntime"
                or "ReconcileLiveObjectSpatialPresentation");
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ILiveAnimationPresentationContext));
    }

    [Fact]
    public void GameWindow_DelegatesTheCompleteStreamingFrameBody()
    {
        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(SessionPlayerCompositionPhase),
                "CompleteSessionPlayer")),
            call => call.Target.DeclaringType == typeof(StreamingFrameController)
                && call.Target.IsConstructor);
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_streamingFrame");

        MethodInfo onUpdate = RequiredMethod(typeof(GameWindow), "OnUpdate");
        Assert.Single(
            CompiledCallGraph.Read(onUpdate),
            call => call.Target.DeclaringType == typeof(GameFrameGraphSlot)
                && call.Target.Name == nameof(GameFrameGraphSlot.Tick));
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.Name is "Compute" or "DrainRescued"
                || call.Target.DeclaringType == typeof(StreamingController)
                    && call.Target.Name == nameof(StreamingController.Tick));
    }

    [Fact]
    public void GameWindow_DelegatesTheCompleteGameplayInputFrameBody()
    {
        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(SessionPlayerCompositionPhase),
                "CompleteSessionPlayer")),
            call => call.Target.DeclaringType == typeof(GameplayInputFrameController)
                && call.Target.IsConstructor);

        HashSet<string> displacedWindowCalls =
        [
            "TryTakeRawSample",
            "CaptureMovementInput",
            "EndMouseLookAndRestoreCursor",
            "HideCursorForMouseLook",
            "RestoreCursorAfterMouseLook",
            "CanStartLiveCombatAttack",
            "SendLiveCombatAttack",
            "PreparePlayerForAttackRequest",
            "DumpMovementTruthOutbound",
            "DumpMovementTruthServerEcho",
            "EndMouseLook",
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => displacedWindowCalls.Contains(call.Target.Name)
                || call.Target.DeclaringType == typeof(GameplayInputFrameController)
                    && call.Target.Name == nameof(GameplayInputFrameController.Tick));

        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(
                typeof(CameraPointerInputController),
                nameof(CameraPointerInputController.HandleFocusChanged))),
            call => call.Target.DeclaringType == typeof(GameplayInputFrameController)
                && call.Target.Name == nameof(GameplayInputFrameController.EndMouseLook));
        AssertCallOrder(
            RequiredMethod(
                typeof(LocalPlayerTeleportController),
                nameof(LocalPlayerTeleportController.OnTeleportStarted)),
            (typeof(RuntimeWorldTransitState),
                nameof(RuntimeWorldTransitState.CanQueueTeleportStart)),
            (typeof(ILocalPlayerTeleportInputLifetime),
                nameof(ILocalPlayerTeleportInputLifetime.EndMouseLook)),
            (typeof(RuntimeWorldTransitState),
                nameof(RuntimeWorldTransitState.TryQueueTeleportStart)));
        Assert.Contains(
            CompiledCallGraph.Read(RequiredMethod(typeof(PlayerModeController), "Exit")),
            call => call.Target.Name == "EndMouseLook");

        MethodInfo focus = RequiredMethod(typeof(GameWindow), "OnFocusChanged");
        Assert.Single(
            CompiledCallGraph.Read(focus),
            call => call.Target.DeclaringType == typeof(CameraPointerInputController)
                && call.Target.Name == nameof(CameraPointerInputController.HandleFocusChanged));
    }

    [Fact]
    public void GameWindow_DelegatesTheCompleteLocalTeleportLifetime()
    {
        MethodInfo complete = RequiredMethod(
            typeof(SessionPlayerCompositionPhase),
            "CompleteSessionPlayer");
        MethodBase presentationFactory = ReferencedMethodConstructing(
            complete,
            typeof(LocalPlayerTeleportPresentation));
        Assert.NotNull(ReferencedMethodConstructing(
            presentationFactory,
            typeof(LocalPlayerTeleportController)));

        HashSet<string> removedFields =
        [
            "_teleportTransit",
            "_teleportAnim",
            "_teleportViewPlane",
            "_pendingTeleport",
        ];
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => removedFields.Contains(field.Name));
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => call.Target.DeclaringType == typeof(LocalPlayerTeleportController)
                && call.Target.Name == nameof(LocalPlayerTeleportController.Tick)
                || call.Target.Name is "AimTeleportDestination"
                    or "ResetTeleportTransitState"
                    or "PlaceTeleportArrival"
                    or "TryActivatePendingTeleportPresentation");

        FieldInfo teleport = Assert.Single(
            typeof(AcDream.App.Physics.LiveEntityNetworkUpdateController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ILocalPlayerTeleportNetworkSink));
        Assert.Equal(typeof(ILocalPlayerTeleportNetworkSink), teleport.FieldType);
        Assert.DoesNotContain(
            typeof(AcDream.App.Physics.LiveEntityNetworkUpdateController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));

        AssertCallOrder(
            RequiredMethod(typeof(FrameRootCompositionPhase), "ComposeCore"),
            (typeof(LiveEntityLivenessFramePhase), ".ctor"),
            (typeof(SessionPlayerResult), "get_LocalTeleport"),
            (typeof(PlayerModeAutoEntryFramePhase), ".ctor"),
            (typeof(LivePresentationResult), "get_WorldAvailability"),
            (typeof(UpdateFrameOrchestrator), ".ctor"));
    }

    [Fact]
    public void GameplayInputOwnersUseTypedSeamsWithoutGameWindowBackReferences()
    {
        Type[] owners =
        [
            typeof(AcDream.App.Input.GameplayInputFrameController),
            typeof(AcDream.App.Input.DispatcherMovementInputSource),
            typeof(AcDream.App.Input.MouseLookController),
        ];

        foreach (Type owner in owners)
        {
            FieldInfo[] fields = owner.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
            Assert.DoesNotContain(
                fields,
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }

        Assert.DoesNotContain(
            typeof(AcDream.App.Input.MouseLookController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(Silk.NET.Input.IMouse));

        FieldInfo combatOperations = Assert.Single(
            typeof(AcDream.Runtime.Gameplay.RuntimeCombatAttackState).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_operations");
        Assert.Equal(
            typeof(AcDream.Runtime.Gameplay.IRuntimeCombatAttackOperations),
            combatOperations.FieldType);
        Assert.DoesNotContain(
            typeof(AcDream.App.Combat.LiveCombatAttackOperations).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        FieldInfo combatTargets = Assert.Single(
            typeof(AcDream.App.Combat.LiveCombatAttackOperations).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_targets");
        Assert.Equal(
            typeof(AcDream.App.Combat.ICombatAttackTargetSource),
            combatTargets.FieldType);
        Assert.DoesNotContain(
            typeof(AcDream.App.Combat.CombatAttackTargetSource).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(
            typeof(AcDream.App.Combat.CombatAttackTargetSource).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType
                == typeof(AcDream.App.Interaction.SelectionInteractionController));

        FieldInfo outboundDiagnostics = Assert.Single(
            typeof(LocalPlayerOutboundController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_diagnostic");
        Assert.Equal(
            typeof(IMovementTruthDiagnosticSink),
            outboundDiagnostics.FieldType);
        Assert.DoesNotContain(
            typeof(AcDream.App.Input.MovementTruthDiagnosticController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));

        FieldInfo approachCompletions = Assert.Single(
            typeof(AcDream.App.Input.PlayerModeController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_approachCompletions");
        Assert.Equal(
            typeof(AcDream.App.Interaction.IPlayerApproachCompletionLifetimeOwner),
            approachCompletions.FieldType);
        Assert.DoesNotContain(
            typeof(AcDream.App.Input.PlayerModeController).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType
                == typeof(AcDream.App.Interaction.SelectionInteractionController));
        Assert.DoesNotContain(
            typeof(AcDream.App.Interaction.PlayerApproachCompletionState).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(Delegate).IsAssignableFrom(field.FieldType));

        MethodInfo attach = RequiredMethod(typeof(PlayerModeController), "BuildControllerAndCamera");
        AssertNamedCallOrder(
            attach,
            ("PlayerMovementController", "get_IsRuntimePublished"),
            ("CameraController", "EnterChaseMode"),
            ("LocalPlayerShadowSynchronizer", "SyncPose"),
            ("LocalPlayerPhysicsHostSlot", "set_Host"),
            ("LocalPlayerModeState", "set_IsPlayerMode"));
        Assert.Contains(
            CompiledCallGraph.Read(attach),
            call => call.Target.DeclaringType?.Name == "LocalPlayerShadowSynchronizer"
                && call.Target.Name == "Restore");
        Assert.Contains(
            CompiledCallGraph.Read(attach),
            call => call.Target.DeclaringType?.Name == "CameraController"
                && call.Target.Name == "RestoreState");

        AssertNamedCallOrder(
            RequiredMethod(typeof(MouseLookController), nameof(MouseLookController.Tick)),
            ("PlayerMovementController", "StopMouseDrift"),
            ("RetailChaseCamera", "FilterMouseDelta"),
            ("MouseLookState", "ApplyDelta"));

        ConstructorInfo[] localFrameConstructors =
            typeof(RetailLocalPlayerFrameController).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(localFrameConstructors);
        Assert.All(
            localFrameConstructors,
            constructor =>
            {
                Type[] parameters = constructor.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .ToArray();
                Assert.Contains(typeof(IMovementInputSource), parameters);
                Assert.DoesNotContain(parameters, type =>
                    type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(Func<>));
            });
    }

    [Fact]
    public void CameraAndLocalPlayerFrameOwnersUseTypedSeamsWithoutWindowCallbacks()
    {
        Type[] owners =
        [
            typeof(AcDream.App.Rendering.CameraFrameController),
            typeof(AcDream.App.Input.RetailLocalPlayerFrameController),
            typeof(AcDream.App.Input.LocalPlayerProjectionController),
            typeof(AcDream.App.Input.LiveLocalPlayerFrameRuntime),
            typeof(AcDream.App.Input.LiveLocalPlayerProjectionRuntime),
            typeof(AcDream.App.Combat.CombatCameraTargetSource),
        ];

        foreach (Type owner in owners)
        {
            FieldInfo[] fields = owner.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
            Assert.DoesNotContain(
                fields,
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }

        MethodInfo onUpdate = RequiredMethod(typeof(GameWindow), "OnUpdate");
        Assert.Single(
            CompiledCallGraph.Read(onUpdate),
            call => call.Target.DeclaringType == typeof(GameFrameGraphSlot)
                && call.Target.Name == nameof(GameFrameGraphSlot.Tick));
        HashSet<string> displacedCalls =
        [
            "CanAdvanceLocalPlayer",
            "GetCombatCameraTargetPoint",
            "TryGetPresentationAfterNetwork",
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => displacedCalls.Contains(call.Target.Name)
                || call.Target.DeclaringType?.Name == "FlyCamera"
                    && call.Target.Name == "Update");
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_cameraFrame");

        AssertNamedCallOrder(
            RequiredMethod(typeof(CameraFrameController), nameof(CameraFrameController.Tick)),
            ("RetailLocalPlayerFrameController", "TryGetPresentationAfterNetwork"),
            ("ILiveSpatialReconcilePhase", "Reconcile"),
            ("ChaseCamera", "Update"),
            ("RetailChaseCamera", "Update"));
    }

    [Fact]
    public void GameWindow_OnUpdateOwnsOnlyProfilingAndOneOrchestratorTick()
    {
        MethodInfo update = RequiredMethod(typeof(GameWindow), "OnUpdate");
        AssertCallOrder(
            update,
            (typeof(AcDream.App.Diagnostics.FrameProfiler), "BeginStage"),
            (typeof(GameFrameGraphSlot), nameof(GameFrameGraphSlot.Tick)));
        Assert.Single(
            CompiledCallGraph.Read(update),
            call => call.Target.DeclaringType == typeof(GameFrameGraphSlot)
                && call.Target.Name == nameof(GameFrameGraphSlot.Tick));
        Assert.DoesNotContain(
            CompiledCallGraph.Read(update),
            call => call.Target.DeclaringType == typeof(UpdateFrameClock)
                && call.Target.Name == nameof(UpdateFrameClock.Advance)
                || call.Target.Name is "TryEnter"
                || call.Target.DeclaringType == typeof(LiveEntityLivenessFramePhase)
                    && call.Target.Name == nameof(LiveEntityLivenessFramePhase.Tick));
        Assert.DoesNotContain(
            typeof(GameWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_liveFrameCoordinator");
    }

    [Fact]
    public void ProductionFrameAdaptersRetainTypedOwnersWithoutWindowCallbacks()
    {
        Type[] adapters =
        [
            typeof(LiveEntityTeardownFramePhase),
            typeof(ConsoleUpdateFrameFailureSink),
            typeof(PhysicsScriptClockPublisher),
            typeof(LiveEntityLivenessFramePhase),
            typeof(PlayerModeAutoEntryFramePhase),
        ];

        foreach (Type adapter in adapters)
        {
            FieldInfo[] fields = adapter.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
            Assert.DoesNotContain(
                fields,
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }

        FieldInfo clock = Assert.Single(
            typeof(LiveEntityLivenessFramePhase).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_clock");
        Assert.Equal(typeof(IClientMonotonicTimeSource), clock.FieldType);

        FieldInfo teardownRuntime = Assert.Single(
            typeof(LiveEntityTeardownFramePhase).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(typeof(LiveEntityRuntime), teardownRuntime.FieldType);

        Type[] typedProductionOwners =
        [
            typeof(LiveEntityLivenessController),
            typeof(AcDream.App.Input.LivePlayerModeAutoEntryContext),
            typeof(LiveSessionLocalPhysicsTimestampPublisher),
            typeof(AcDream.App.Physics.LiveEntityNetworkUpdateController),
            typeof(AcDream.App.Rendering.LiveEntityPartArrayLifecycle),
            typeof(AcDream.App.Net.LiveEntitySessionController),
        ];
        foreach (Type owner in typedProductionOwners)
        {
            FieldInfo[] fields = owner.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
            Assert.DoesNotContain(
                fields,
                field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        }

        FieldInfo originIdentity = Assert.Single(
            typeof(LiveEntityWorldOriginCoordinator).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_identity");
        Assert.Equal(
            typeof(AcDream.App.Input.ILocalPlayerIdentitySource),
            originIdentity.FieldType);

        HashSet<string> displacedWindowCalls =
        [
            "PublishLocalPhysicsTimestamps",
            "LoginWorldReady",
            "OnPrune",
            "CreateLiveEntitySessionSink",
            "CreateLiveSessionEventRouter",
            "CellLocalForSeed",
            "OnPlayScriptReceived",
        ];
        Assert.DoesNotContain(
            CompiledCallGraph.ReadDeclared(typeof(GameWindow)),
            call => displacedWindowCalls.Contains(call.Target.Name));
        Assert.DoesNotContain(
            typeof(GameWindow).GetMethods(
                BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic),
            method => displacedWindowCalls.Contains(method.Name));
        MethodBase presentationFactory = ReferencedMethodConstructing(
            RequiredMethod(typeof(LivePresentationCompositionPhase), "ComposeCore"),
            typeof(LiveEntityPresentationController));
        Assert.Contains(
            CompiledCallGraph.ReadMethodReferences(presentationFactory),
            call => call.Target.DeclaringType == typeof(LiveWorldOriginState)
                && call.Target.Name == nameof(LiveWorldOriginState.GetCenter));
        MethodInfo eventRouter = RequiredMethod(typeof(LiveSessionRuntimeFactory), "CreateEventRouter");
        Assert.Contains(
            CompiledCallGraph.Read(eventRouter),
            call => call.Target.DeclaringType == typeof(LiveEntitySessionController)
                && call.Target.Name == nameof(LiveEntitySessionController.CreateSink));
        Assert.DoesNotContain(
            typeof(LiveSessionRuntimeFactory).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(GameWindow));
        Assert.DoesNotContain(
            typeof(LiveSessionRuntimeFactory).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(GameWindow));
    }

    private static UpdateFrameOrchestrator Create(
        List<string> calls,
        RecordingTeardown? teardown = null,
        RecordingFailureSink? failureSink = null,
        FrameObservations? observed = null,
        bool teleportPlace = false,
        bool inboundCreatedPlayer = false,
        IWorldGenerationAvailability? availability = null)
    {
        teardown ??= new RecordingTeardown(calls);
        failureSink ??= new RecordingFailureSink();
        return new UpdateFrameOrchestrator(
            teardown,
            failureSink,
            new UpdateFrameClock(),
            new RecordingClockPublisher(calls, observed),
            new RecordingStreaming(calls),
            new RecordingInput(calls, observed),
            new RecordingLiveFrame(calls, observed),
            new RecordingLiveness(calls),
            new RecordingTeleport(calls, observed, teleportPlace),
            new RecordingAutoEntry(calls),
            new RecordingCamera(calls, observed, inboundCreatedPlayer),
            new RecordingCommit(calls),
            availability);
    }

    private sealed class RecordingTeardown(List<string> calls)
        : IUpdateFrameTeardownPhase
    {
        public Exception? Failure { get; set; }
        public int Attempts { get; private set; }

        public void RetryPendingTeardowns()
        {
            calls.Add("teardown");
            Attempts++;
            if (Failure is not null)
                throw Failure;
        }
    }

    private sealed class RecordingFailureSink : IUpdateFrameFailureSink
    {
        public List<AggregateException> Errors { get; } = [];
        public void ReportTeardownFailure(AggregateException error) => Errors.Add(error);
    }

    private sealed class RecordingClockPublisher(
        List<string> calls,
        FrameObservations? observed) : IUpdateFrameScriptClockPublisher
    {
        public void PublishTime(double scriptTime)
        {
            calls.Add("clock");
            observed?.PublishedTimes.Add(scriptTime);
        }
    }

    private sealed class RecordingStreaming(List<string> calls) : IStreamingFramePhase
    {
        public void Tick() => calls.Add("streaming");
    }

    private sealed class RecordingInput(
        List<string> calls,
        FrameObservations? observed) : IGameplayInputFramePhase
    {
        public void Tick(UpdateFrameTiming timing)
        {
            calls.Add("input");
            observed?.Input.Add(timing);
        }
    }

    private sealed class RecordingLiveFrame(
        List<string> calls,
        FrameObservations? observed) : IRetailLiveFramePhase
    {
        public void Tick(float deltaSeconds)
        {
            observed?.LiveDeltas.Add(deltaSeconds);
            calls.Add("objects");
            calls.Add("network");
            calls.Add("commands");
            calls.Add("ordinary-reconcile");
        }
    }

    private sealed class RecordingLiveness(List<string> calls)
        : ILiveEntityLivenessFramePhase
    {
        public void Tick() => calls.Add("liveness");
    }

    private sealed class RecordingTeleport(
        List<string> calls,
        FrameObservations? observed,
        bool place)
        : ILocalPlayerTeleportFramePhase
    {
        public void Tick(float deltaSeconds)
        {
            observed?.TeleportDeltas.Add(deltaSeconds);
            if (!place)
            {
                calls.Add("teleport");
                return;
            }

            calls.Add("teleport-place");
            calls.Add("teleport-reconcile");
            calls.Add("teleport-reveal");
        }
    }

    private sealed class RecordingAutoEntry(List<string> calls)
        : IPlayerModeAutoEntryFramePhase
    {
        public void TryEnter() => calls.Add("auto-entry");
    }

    private sealed class RecordingCamera(
        List<string> calls,
        FrameObservations? observed,
        bool inboundCreatedPlayer) : ICameraFramePhase
    {
        public void Tick(UpdateFrameTiming timing)
        {
            if (inboundCreatedPlayer)
            {
                calls.Add("inbound-player-projection");
                calls.Add("inbound-player-reconcile");
            }

            calls.Add("camera");
            observed?.Camera.Add(timing);
        }
    }

    private sealed class RecordingLivePhases(List<string> calls) :
        ILiveObjectFramePhase,
        IRuntimeLiveSessionFramePhase,
        IPostNetworkCommandFramePhase,
        ILiveSpatialReconcilePhase,
        IRenderProjectionSyncPhase,
        IWorldGenerationAvailability,
        IRuntimePlacementProjectionRetryPhase
    {
        public bool IsWorldAvailable { get; set; }
        public long QuiescedGeneration => IsWorldAvailable ? 0L : 1L;

        public void Tick(float deltaSeconds) => calls.Add("objects");
        public void Tick() => calls.Add("network");
        public void RunPostNetworkCommandPhase() => calls.Add("commands");
        public void Reconcile() => calls.Add("reconcile");
        public void SynchronizeActiveSources() => calls.Add("render-sync");
        public void RetryPending() => calls.Add("placement-retry");
    }

    private sealed class RecordingCommit(List<string> calls)
        : IUpdateFrameCommitPhase
    {
        public void Commit() => calls.Add("commit");
    }

    private sealed class FrameObservations
    {
        public List<double> PublishedTimes { get; } = [];
        public List<UpdateFrameTiming> Input { get; } = [];
        public List<float> LiveDeltas { get; } = [];
        public List<float> TeleportDeltas { get; } = [];
        public List<UpdateFrameTiming> Camera { get; } = [];
    }

    private static MethodInfo RequiredMethod(Type owner, string name) =>
        owner.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(owner.FullName, name);

    private static MethodBase ReferencedMethodConstructing(
        MethodBase owner,
        Type constructed) =>
        Assert.Single(
            CompiledCallGraph.ReadMethodReferences(owner)
                .Select(reference => reference.Target)
                .Where(method => method.GetMethodBody() is not null)
                .Distinct(),
            method => Constructs(method, constructed));

    private static bool Constructs(MethodBase method, Type type) =>
        method.GetMethodBody() is not null
        && CompiledCallGraph.Read(method).Any(call =>
            call.Target.DeclaringType == type && call.Target.IsConstructor);

    private static void AssertCallOrder(
        MethodBase method,
        params (Type Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((Type type, string name) in expected)
        {
            int found = Enumerable.Range(cursor + 1, calls.Count - cursor - 1)
                .FirstOrDefault(index => calls[index].Target.DeclaringType == type
                    && calls[index].Target.Name == name, -1);
            Assert.True(found > cursor,
                $"Missing compiled edge after {cursor}: {type.FullName}.{name}.");
            cursor = found;
        }
    }

    private static void AssertNamedCallOrder(
        MethodBase method,
        params (string Type, string Method)[] expected)
    {
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(method);
        int cursor = -1;
        foreach ((string type, string name) in expected)
        {
            int found = Enumerable.Range(cursor + 1, calls.Count - cursor - 1)
                .FirstOrDefault(index => calls[index].Target.DeclaringType?.Name == type
                    && calls[index].Target.Name == name, -1);
            Assert.True(found > cursor,
                $"Missing compiled edge after {cursor}: {type}.{name}.");
            cursor = found;
        }
    }

    private static void AssertSingleNamedCall(
        MethodBase method,
        string type,
        string name) =>
        Assert.Single(
            CompiledCallGraph.Read(method),
            call => call.Target.DeclaringType?.Name == type
                && call.Target.Name == name);
}
