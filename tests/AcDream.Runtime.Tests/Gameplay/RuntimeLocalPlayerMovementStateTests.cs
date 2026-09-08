using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeLocalPlayerMovementStateTests
{
    private static PhysicsEngine MakeFlatEngine()
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
        return engine;
    }

    [Fact]
    public void ViewProjectsTheExactCanonicalControllerAndAutorunOwner()
    {
        var controller = new PlayerMovementController(new PhysicsEngine())
        {
            LocalEntityId = 0x50000001u,
        };
        controller.SeedPlacementForTest(
            new Vector3(11f, 12f, 13f),
            0xA9B40001u,
            new Vector3(11f, 12f, 13f));
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };

        Assert.Same(movement, movement.View);
        Assert.True(movement.Execute(RuntimeMovementCommand.ToggleRunLock));

        RuntimeMovementSnapshot snapshot = movement.View.Snapshot;
        Assert.True(snapshot.HasController);
        Assert.Equal(0x50000001u, snapshot.LocalEntityId);
        Assert.Equal(controller.CellPosition, snapshot.Position);
        Assert.Equal(controller.BodyVelocity, snapshot.Velocity);
        Assert.Equal(controller.IsAirborne, snapshot.IsAirborne);
        Assert.Equal(controller.SimTimeSeconds, snapshot.SimulationTimeSeconds);
        Assert.True(snapshot.AutoRunActive);
        Assert.Equal(movement.Revision, snapshot.Revision);
    }

    [Fact]
    public void ViewProjectsLiveBodyOrientationAfterLocalTurn()
    {
        var controller = new PlayerMovementController(new PhysicsEngine())
        {
            LocalEntityId = 0x50000001u,
        };
        controller.SeedPlacementForTest(
            new Vector3(11f, 12f, 13f),
            0xA9B40001u,
            new Vector3(11f, 12f, 13f));
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };
        Quaternion turned = AcDream.Core.Physics.Motion.MoveToMath.SetHeading(
            Quaternion.Identity,
            90f);

        controller.SetBodyOrientation(turned);

        Assert.NotEqual(turned, controller.CellPosition.Frame.Orientation);
        RuntimeMovementSnapshot snapshot = movement.View.Snapshot;
        Assert.Equal(turned, snapshot.Position.Frame.Orientation);
        Assert.Equal(
            90f,
            AcDream.Core.Physics.Motion.MoveToMath.GetHeading(
                snapshot.Position.Frame.Orientation),
            precision: 4);
    }

    [Fact]
    public void GraphicalAndDirectCommandsMutateOneAutorunLatch()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var input = new MovementInput(
            Forward: true,
            Run: true);

        Assert.True(movement.Execute(RuntimeMovementCommand.ToggleRunLock));
        movement.SetCommandInput(input);
        Assert.True(movement.AutoRunActive);
        Assert.True(movement.View.Snapshot.AutoRunActive);
        Assert.True(movement.HasCommandInput);
        Assert.Equal(input, movement.CommandInput);
        Assert.Equal(input, movement.View.Snapshot.CommandInput);

        Assert.True(movement.Execute(RuntimeMovementCommand.Stop));
        Assert.False(movement.AutoRunActive);
        Assert.False(movement.View.Snapshot.AutoRunActive);
        Assert.False(movement.HasCommandInput);
        Assert.False(movement.View.Snapshot.HasCommandInput);
    }

    [Fact]
    public void EscapeCommandsFinishJumpAndStopThroughCanonicalController()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(
            new Vector3(96f, 96f, 50f),
            0xA9B40001u,
            new Vector3(96f, 96f, 50f));
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };

        controller.Update(0.25f, new MovementInput(Jump: true));
        Assert.True(movement.View.JumpCharge.IsCharging);
        Assert.True(movement.Execute(RuntimeMovementCommand.FinishJump));
        Assert.False(movement.View.JumpCharge.IsCharging);

        controller.Update(1f / 60f, new MovementInput(Forward: true));
        Assert.False(movement.View.IsStandingStill);
        Assert.True(movement.Execute(RuntimeMovementCommand.StopCompletely));
        Assert.Equal(MotionCommand.Ready, controller.Motion.RawState.ForwardCommand);
    }

    [Fact]
    public void CommandInputIsDeduplicatedAndResetWithSessionIntent()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var input = new MovementInput(
            StrafeLeft: true,
            Run: true);

        movement.SetCommandInput(input);
        long revision = movement.Revision;
        movement.SetCommandInput(input);

        Assert.Equal(revision, movement.Revision);
        Assert.True(movement.Snapshot.HasCommandInput);

        movement.ResetInputIntent();

        Assert.False(movement.HasCommandInput);
        Assert.Equal(default, movement.CommandInput);
        Assert.Equal(revision + 1, movement.Revision);
    }

    [Theory]
    [InlineData(RuntimeMovementCommand.Ready, MotionCommand.Ready)]
    [InlineData(RuntimeMovementCommand.Crouch, MotionCommand.Crouch)]
    [InlineData(RuntimeMovementCommand.Sit, MotionCommand.Sitting)]
    [InlineData(RuntimeMovementCommand.Sleep, MotionCommand.Sleeping)]
    public void StanceCommandsUseCanonicalControllerAndEmitOneMovementEdge(
        RuntimeMovementCommand command,
        uint expectedMotion)
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };
        movement.SetCommandInput(
            new MovementInput(Forward: true, Run: true));

        Assert.True(movement.Execute(command));
        Assert.False(movement.HasCommandInput);
        Assert.Equal(
            expectedMotion,
            controller.Motion.RawState.ForwardCommand);

        MovementResult first = controller.Update(
            1f / 60f,
            default);
        MovementResult second = controller.Update(
            1f / 60f,
            default);

        Assert.True(first.ShouldSendMovementEvent);
        Assert.False(second.ShouldSendMovementEvent);
        if (command == RuntimeMovementCommand.Ready)
        {
            Assert.Null(first.ForwardCommand);
        }
        else
        {
            Assert.Equal(expectedMotion, first.ForwardCommand);
        }
    }

    [Fact]
    public void CommandMotionUsesCanonicalControllerAndEmitsOneMovementEdge()
    {
        const uint afkState = 0x43000118u;
        var controller = new PlayerMovementController(new PhysicsEngine());
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };

        Assert.True(movement.ExecuteMotion(afkState));
        Assert.Equal(afkState, controller.Motion.RawState.ForwardCommand);

        MovementResult first = controller.Update(1f / 60f, default);
        MovementResult second = controller.Update(1f / 60f, default);

        Assert.True(first.ShouldSendMovementEvent);
        RawMotionState outbound =
            LocalPlayerOutboundController.BuildRawMotionState(first);
        Assert.Equal(afkState, outbound.ForwardCommand);
        Assert.False(second.ShouldSendMovementEvent);
    }

    [Fact]
    public void OutboundRawOverridePreservesRetailActionAndStamp()
    {
        const uint cheer = 0x1300004Cu;
        var raw = new RawMotionState();
        raw.AddAction(
            cheer,
            speed: 1f,
            actionStamp: 7u,
            autonomous: true);
        var result = new MovementResult(
            default,
            default,
            0u,
            false,
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            RawMotionStateOverride: new RawMotionState(raw));

        RawMotionState firstRaw =
            LocalPlayerOutboundController.BuildRawMotionState(result);
        RawMotionAction firstAction = Assert.Single(firstRaw.Actions);

        Assert.Equal((ushort)0x004C, firstAction.Command);
        Assert.Equal(7, firstAction.Stamp);
        Assert.True(firstAction.Autonomous);

        raw.RemoveAction();
        Assert.Single(firstRaw.Actions);
    }

    [Fact]
    public void ConcurrentRuntimeInstancesHaveIndependentMovementState()
    {
        using var first = new RuntimeLocalPlayerMovementState();
        using var second = new RuntimeLocalPlayerMovementState();
        var firstController = new PlayerMovementController(new PhysicsEngine())
        {
            LocalEntityId = 1u,
        };
        var secondController = new PlayerMovementController(new PhysicsEngine())
        {
            LocalEntityId = 2u,
        };
        first.Controller = firstController;
        second.Controller = secondController;

        first.Execute(RuntimeMovementCommand.ToggleRunLock);
        firstController.SeedPlacementForTest(Vector3.One, 0xA9B40001u, Vector3.One);
        secondController.SeedPlacementForTest(
            new Vector3(2f),
            0xA9B50001u,
            new Vector3(2f));

        Assert.True(first.Snapshot.AutoRunActive);
        Assert.False(second.Snapshot.AutoRunActive);
        Assert.Equal(1u, first.Snapshot.LocalEntityId);
        Assert.Equal(2u, second.Snapshot.LocalEntityId);
        Assert.NotEqual(first.Snapshot.Position, second.Snapshot.Position);
    }


    [Fact]
    public void OnInterfaceText_SetBeforeControllerInstall_AppliesToTheInstalledController()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var received = new List<(string Text, AcDream.Core.Chat.RetailLogTextType Type)>();
        movement.OnInterfaceText = (text, type) => received.Add((text, type));

        var controller = new PlayerMovementController(new PhysicsEngine());
        movement.Controller = controller;

        controller.OnInterfaceText!("test line", AcDream.Core.Chat.RetailLogTextType.ClientLocal);

        Assert.Single(received);
        Assert.Equal("test line", received[0].Text);
        Assert.Equal(AcDream.Core.Chat.RetailLogTextType.ClientLocal, received[0].Type);
    }

    [Fact]
    public void OnInterfaceText_CommitRuntimeOwnedController_CarriesTheCallback()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var received = new List<string>();
        movement.OnInterfaceText = (text, _) => received.Add(text);

        var controller = new PlayerMovementController(new PhysicsEngine());
        movement.CommitRuntimeOwnedController(controller);

        Assert.NotNull(controller.OnInterfaceText);
        controller.OnInterfaceText!(
            "commit-path line", AcDream.Core.Chat.RetailLogTextType.ClientLocal);
        Assert.Equal(["commit-path line"], received);
    }

    [Fact]
    public void OnInterfaceText_SetAfterControllerInstall_StillAppliesToTheCurrentController()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var controller = new PlayerMovementController(new PhysicsEngine());
        movement.Controller = controller;

        var received = new List<string>();
        movement.OnInterfaceText = (text, _) => received.Add(text);

        controller.OnInterfaceText!("late-bound line", AcDream.Core.Chat.RetailLogTextType.ClientLocal);

        Assert.Equal(["late-bound line"], received);
    }

    [Fact]
    public void OnInterfaceText_SwappingController_AppliesToTheReplacement()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        var received = new List<string>();
        movement.OnInterfaceText = (text, _) => received.Add(text);

        var first = new PlayerMovementController(new PhysicsEngine());
        movement.Controller = first;
        var second = new PlayerMovementController(new PhysicsEngine());
        movement.Controller = second;

        second.OnInterfaceText!("from replacement", AcDream.Core.Chat.RetailLogTextType.ClientLocal);

        Assert.Equal(["from replacement"], received);
    }

    [Fact]
    public void MotionPreparationPublishesOnlyTheConstructionSeam()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        IRuntimeLocalPlayerMotionSource source = movement;
        var committed = new PlayerMovementController(new PhysicsEngine());
        var candidate = new PlayerMovementController(new PhysicsEngine());
        movement.Controller = committed;
        bool drained = false;

        using (movement.BeginMotionPreparation(
            candidate,
            () =>
            {
                drained = true;
                Assert.Same(committed.Motion, source.Motion);
            }))
        {
            Assert.True(drained);
            Assert.Same(committed, movement.Controller);
            Assert.Same(candidate.Motion, source.Motion);
        }

        Assert.Same(committed.Motion, source.Motion);
    }

    [Fact]
    public void TerminalDisposalConvergesWhenPreparationLeaseUnwindsLater()
    {
        var movement = new RuntimeLocalPlayerMovementState();
        var controller = new PlayerMovementController(new PhysicsEngine());
        IDisposable preparation = movement.BeginMotionPreparation(controller);
        movement.Controller = controller;
        movement.Execute(RuntimeMovementCommand.ToggleRunLock);

        movement.Dispose();
        preparation.Dispose();

        RuntimeLocalMovementOwnershipSnapshot ownership =
            movement.CaptureOwnership();
        Assert.True(ownership.IsConverged);
        Assert.Throws<ObjectDisposedException>(
            () => movement.Execute(RuntimeMovementCommand.ToggleRunLock));
    }

    [Fact]
    public void TypedSkillOptionsPreserveCompleteValuesAndFallbackPerField()
    {
        Assert.Equal(
            new PlayerMovementConstructionOptions(321, 654),
            PlayerMovementConstructionOptions.From(
                new RuntimeMovementSkillSnapshot(321, 654, 1)));
        Assert.Equal(
            new PlayerMovementConstructionOptions(
                PlayerMovementConstructionOptions.FallbackRunSkill,
                654),
            PlayerMovementConstructionOptions.From(
                new RuntimeMovementSkillSnapshot(-1, 654, 1)));
        Assert.Equal(
            new PlayerMovementConstructionOptions(
                321,
                PlayerMovementConstructionOptions.FallbackJumpSkill),
            PlayerMovementConstructionOptions.From(
                new RuntimeMovementSkillSnapshot(321, -1, 1)));
    }

    [Fact]
    public void WarmMovementSnapshotsAndOwnershipCaptureAllocateNothing()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        movement.Controller = new PlayerMovementController(new PhysicsEngine())
        {
            LocalEntityId = 7u,
        };

        for (int i = 0; i < 1_000; i++)
        {
            _ = movement.Snapshot;
            _ = movement.CaptureOwnership();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            _ = movement.Snapshot;
            _ = movement.CaptureOwnership();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }


    private static RuntimeMovementSkillState CompleteSkillState()
    {
        var skills = new RuntimeMovementSkillState();
        skills.Update(runSkill: 240, jumpSkill: 180);
        skills.UpdateBurden(0.5f);
        skills.UpdateStamina(37);
        // BF_PLAYER (0x8) + BF_PLAYER_KILLER (0x20) → ObjectInfoState.IsPK.
        skills.UpdateOwnPwdBitfield(0x28u);
        skills.UpdatePlayerKillerStatus(1, 123.5f);
        return skills;
    }

    private static PlayerMovementController NewDormantRuntimeController()
    {
        PlayerMovementController controller =
            PlayerMovementController.CreatePublicationCandidate(
                new PhysicsEngine(),
                PlayerMovementConstructionOptions.Fallback);
        controller.SealPublicationCandidate();
        controller.CommitRuntimeOwnership(new RetailObjectQuantumClock());
        return controller;
    }

    private static (float RunRate, float JumpVz, bool CanJump, int JumpCost,
        ObjectInfoState PvpFlags) CaptureStatObservables(
            PlayerMovementController controller)
    {
        IWeenieObject weenie = controller.Motion.WeenieObj!;
        Assert.True(weenie.InqRunRate(out float runRate));
        Assert.True(weenie.InqJumpVelocity(1.0f, out float jumpVz));
        bool canJump = weenie.CanJump(1.0f);
        Assert.True(((PlayerWeenie)weenie).JumpStaminaCost(1.0f, out int cost));
        return (runRate, jumpVz, canJump, cost, controller.OwnPvpFlags);
    }

    [Fact]
    public void ApplyCharacterMovementStats_LiveApplicationIsByteIdenticalToTheRetiredDirectPath()
    {
        RuntimeMovementSkillState skills = CompleteSkillState();
        RuntimeMovementSkillSnapshot snapshot = skills.Snapshot;

        // Old direct path (the deleted RuntimeMovementSkillProjection.ApplyTo
        // body), applied through the public gated setters.
        var direct = new PlayerMovementController(new PhysicsEngine());
        direct.SetCharacterSkills(snapshot.RunSkill, snapshot.JumpSkill);
        direct.SetCharacterBurden(snapshot.Burden);
        direct.SetCharacterStamina(snapshot.CurrentStamina);
        direct.OwnPvpFlags = EntityCollisionFlagsExt
            .FromPwdBitfield(snapshot.OwnPwdBitfield)
            .ToMoverState();
        direct.SetCharacterPkStatus(
            snapshot.PlayerKillerStatus,
            snapshot.LastPkAttackTimestamp);

        var routed = new PlayerMovementController(new PhysicsEngine());
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = routed,
        };

        Assert.Equal(
            RuntimeMovementStatsApplication.AppliedLive,
            movement.ApplyCharacterMovementStats(skills));
        Assert.Equal(
            CaptureStatObservables(direct),
            CaptureStatObservables(routed));
        Assert.Equal(ObjectInfoState.IsPK, routed.OwnPvpFlags);
    }

    [Fact]
    public void ApplyCharacterMovementStats_DormantWindowWriteLandsOnTheControllerThatGoesLive()
    {
        RuntimeMovementSkillState skills = CompleteSkillState();
        PlayerMovementController controller = NewDormantRuntimeController();
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };

        Assert.True(controller.IsRuntimeOwnedDormant);
        Assert.Throws<InvalidOperationException>(
            () => controller.SetCharacterSkills(1, 1));
        Assert.Equal(
            RuntimeMovementStatsApplication.AppliedDormant,
            movement.ApplyCharacterMovementStats(skills));

        controller.ActivateRuntimePublication();
        Assert.True(controller.IsRuntimePublished);
        (float runRate, _, _, _, ObjectInfoState pvp) =
            CaptureStatObservables(controller);
        Assert.Equal(
            PlayerWeenie.GetRunRate(0.5f, 240),
            runRate,
            precision: 5);
        Assert.Equal(ObjectInfoState.IsPK, pvp);
    }

    [Fact]
    public void ApplyCharacterMovementStats_TerminalControllerReportsTypedDisplacedDrop()
    {
        RuntimeMovementSkillState skills = CompleteSkillState();
        PlayerMovementController controller = NewDormantRuntimeController();
        using var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = controller,
        };
        controller.ActivateRuntimePublication();
        var before = CaptureStatObservables(controller);
        IWeenieObject weenie = controller.Motion.WeenieObj!;

        controller.RetireRuntimePublication();
        Assert.Throws<InvalidOperationException>(
            () => controller.SetCharacterSkills(1, 1));
        Assert.Equal(
            RuntimeMovementStatsApplication.DroppedDisplacedController,
            movement.ApplyCharacterMovementStats(skills));

        Assert.True(weenie.InqRunRate(out float runRate));
        Assert.Equal(before.RunRate, runRate);
        Assert.Equal(before.PvpFlags, controller.OwnPvpFlags);
    }

    [Fact]
    public void ApplyCharacterMovementStats_SealedCandidateIsATypedDisplacedDrop()
    {
        PlayerMovementController controller =
            PlayerMovementController.CreatePublicationCandidate(
                new PhysicsEngine(),
                PlayerMovementConstructionOptions.Fallback);
        controller.SealPublicationCandidate();

        Assert.Equal(
            RuntimeMovementStatsApplication.DroppedDisplacedController,
            controller.ApplyCharacterMovementStats(
                CompleteSkillState().Snapshot));
    }

    [Fact]
    public void ApplyCharacterMovementStats_AbsentControllerAndIncompleteSnapshotDropSilently()
    {
        var skills = new RuntimeMovementSkillState();
        using var movement = new RuntimeLocalPlayerMovementState();

        Assert.Equal(
            RuntimeMovementStatsApplication.DroppedNoController,
            movement.ApplyCharacterMovementStats(skills));

        movement.Controller = new PlayerMovementController(new PhysicsEngine());
        Assert.Equal(
            RuntimeMovementStatsApplication.DroppedIncompleteSnapshot,
            movement.ApplyCharacterMovementStats(skills));
    }

    [Fact]
    public void ApplyCharacterMovementStats_DisposedOwnerToleratesTheDisplacedCallback()
    {
        var movement = new RuntimeLocalPlayerMovementState
        {
            Controller = new PlayerMovementController(new PhysicsEngine()),
        };
        movement.Dispose();

        Assert.Equal(
            RuntimeMovementStatsApplication.DroppedNoController,
            movement.ApplyCharacterMovementStats(CompleteSkillState()));
        Assert.False(movement.ReportExhaustion());
    }

    [Fact]
    public void ReportExhaustion_DispatchesOnlyThroughALiveController()
    {
        using var movement = new RuntimeLocalPlayerMovementState();
        Assert.False(movement.ReportExhaustion());

        PlayerMovementController controller = NewDormantRuntimeController();
        movement.Controller = controller;
        Assert.False(movement.ReportExhaustion());

        controller.ActivateRuntimePublication();
        Assert.True(movement.ReportExhaustion());

        controller.RetireRuntimePublication();
        Assert.False(movement.ReportExhaustion());
    }

    [Fact]
    public void ApplyServerPhysicsState_DormantDropsForActivationAndLiveAppliesExactly()
    {
        PlayerMovementController controller =
            PlayerMovementController.CreatePublicationCandidate(
                new PhysicsEngine(),
                PlayerMovementConstructionOptions.Fallback);
        PhysicsBody body = controller.PhysicsBody;
        PhysicsStateFlags initial = body.State;
        PhysicsStateFlags pushed =
            PhysicsStateFlags.Gravity
            | PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Ethereal;
        Assert.NotEqual(initial, pushed);

        controller.SealPublicationCandidate();
        controller.CommitRuntimeOwnership(new RetailObjectQuantumClock());

        // Dormant: the activation transaction owns the dormant body's
        // physics state; the push is dropped and the body is untouched.
        Assert.Equal(
            RuntimeServerPhysicsStateApplication.DroppedDormantActivationOwned,
            controller.ApplyServerPhysicsState(pushed));
        Assert.Equal(initial, body.State);

        // Published: byte-identical to the direct ApplyPhysicsState body.
        controller.ActivateRuntimePublication();
        Assert.Equal(
            RuntimeServerPhysicsStateApplication.AppliedLive,
            controller.ApplyServerPhysicsState(pushed));
        Assert.Equal(pushed, body.State);

        // Terminal: a displaced push, typed instead of a fault, mutating
        // nothing.
        controller.RetireRuntimePublication();
        Assert.Throws<InvalidOperationException>(
            () => controller.ApplyPhysicsState(initial));
        Assert.Equal(
            RuntimeServerPhysicsStateApplication.DroppedDisplacedController,
            controller.ApplyServerPhysicsState(initial));
        Assert.Equal(pushed, body.State);
    }


    [Fact]
    public void RunAsDefaultMovement_Unbound_DefaultsToTrue()
    {
        var movement = new RuntimeLocalPlayerMovementState();

        Assert.Null(movement.RunAsDefaultMovementSource);
        Assert.True(movement.RunAsDefaultMovement);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunAsDefaultMovement_ReflectsBoundSourceLive(bool value)
    {
        var movement = new RuntimeLocalPlayerMovementState();
        bool current = value;
        movement.RunAsDefaultMovementSource = () => current;

        Assert.Equal(value, movement.RunAsDefaultMovement);

        current = !value;
        Assert.Equal(!value, movement.RunAsDefaultMovement);
    }
}
