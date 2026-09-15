using System.Numerics;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Physics;

/// <summary>
/// Drives the local player's movement controller through the retail human
/// motion table the way player mode wires it (the sequencer's root motion
/// moves the body, its cycle velocity is the controller's speed source) and
/// checks that a sidestep held while running steers the run sideways rather
/// than replacing it: the run cycle keeps the body moving forward and the
/// sidestep rides on it as a modifier, at run speed.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed partial class PlayerRunSidestepInstalledDatTests
{
    private const uint HumanSetup = 0x02000001u;
    private const uint HumanMotionTable = 0x09000001u;
    private const float FrameSeconds = 1f / 60f;
    private const int FramesPerStep = 30;

    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void SidestepHeldWhileRunning_SteersTheRunInsteadOfEndingIt()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var (controller, sequencer) = CreateWiredController(dats);

        // Facing +X: forward root motion lands on X, a right sidestep on -Y.
        controller.Yaw = 0f;
        Step(controller, new MovementInput(Forward: true, Run: true));
        Vector3 runningStraight = Step(
            controller,
            new MovementInput(Forward: true, Run: true));
        Assert.True(runningStraight.X > 1.5f, $"run forward moved {runningStraight}");
        Assert.Equal(0f, runningStraight.Y, 3);

        Vector3 steered = Step(
            controller,
            new MovementInput(Forward: true, Run: true, StrafeRight: true));

        // The run cycle stays the substate and the sidestep is laid over it.
        MotionState table = sequencer.Manager.State;
        Assert.Equal(MotionCommand.RunForward, table.Substate);
        MotionEntry sidestep = Assert.Single(table.Modifiers);
        Assert.Equal(MotionCommand.SideStepRight, sidestep.Motion);
        Assert.Equal(
            MotionCommand.RunForward,
            controller.Motion.InterpretedState.ForwardCommand);
        Assert.Equal(
            MotionCommand.SideStepRight,
            controller.Motion.InterpretedState.SideStepCommand);

        // Forward progress continues at the run's pace while the body also
        // slides right: the run-held sidestep is the walk sidestep scaled by
        // the run rate, capped at the sidestep animation's maximum rate.
        Assert.True(steered.X > 1.5f, $"steered run moved {steered}");
        Assert.True(steered.Y < -1f, $"steered run slid {steered}");
        float walkSidestepRate = MotionInterpreter.SidestepFactor
            * (MotionInterpreter.WalkAnimSpeed / MotionInterpreter.SidestepAnimSpeed);
        Assert.True(controller.Motion.WeenieObj!.InqRunRate(out float runRate));
        float runSidestepRate = MathF.Min(
            walkSidestepRate * runRate,
            MotionInterpreter.MaxSidestepAnimRate);
        Assert.Equal(runSidestepRate, sidestep.SpeedMod, 2);
        Assert.Equal(
            MotionInterpreter.SidestepAnimSpeed * runSidestepRate,
            sequencer.CurrentVelocity.X,
            2);

        Vector3 released = Step(
            controller,
            new MovementInput(Forward: true, Run: true));
        Assert.Empty(sequencer.Manager.State.Modifiers);
        Assert.True(released.X > 1.5f, $"run resumed straight {released}");
        Assert.Equal(0f, released.Y, 3);
    }

    [InstalledDatFact]
    public void SidestepFromStandstill_IsItsOwnCycle_UntilARunLaysOverIt()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        var (controller, sequencer) = CreateWiredController(dats);
        controller.Yaw = 0f;

        Step(controller, new MovementInput(StrafeLeft: true));
        Vector3 walkingSidestep = Step(controller, new MovementInput(StrafeLeft: true));
        Assert.Equal(MotionCommand.SideStepRight, sequencer.Manager.State.Substate);
        Assert.Empty(sequencer.Manager.State.Modifiers);
        Assert.True(walkingSidestep.Y > 0.5f, $"walk sidestep slid {walkingSidestep}");
        Assert.Equal(0f, walkingSidestep.X, 3);

        Vector3 steeredIntoRun = Step(
            controller,
            new MovementInput(StrafeLeft: true, Forward: true, Run: true));
        Assert.Equal(MotionCommand.RunForward, sequencer.Manager.State.Substate);
        Assert.Equal(
            MotionCommand.SideStepRight,
            Assert.Single(sequencer.Manager.State.Modifiers).Motion);
        Assert.True(steeredIntoRun.X > 1.5f, $"run from sidestep moved {steeredIntoRun}");
        Assert.True(steeredIntoRun.Y > 1f, $"run from sidestep slid {steeredIntoRun}");
    }

    private static Vector3 Step(PlayerMovementController controller, MovementInput input)
    {
        Vector3 before = controller.Position;
        for (int i = 0; i < FramesPerStep; i++)
            controller.Update(FrameSeconds, input);
        return controller.Position - before;
    }

    private static (PlayerMovementController Controller, AnimationSequencer Sequencer)
        CreateWiredController(DatCollection dats)
    {
        var setup = Assert.IsType<Setup>(dats.Get<Setup>(HumanSetup));
        var motionTable = Assert.IsType<MotionTable>(
            dats.Get<MotionTable>(HumanMotionTable));
        var sequencer = new AnimationSequencer(
            setup,
            motionTable,
            new RetailAnimationLoader(dats));

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

        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(
            new Vector3(96f, 96f, 50f),
            0xA9B40001u,
            new Vector3(96f, 96f, 50f));

        // The same wiring PlayerModeController.BuildControllerAndCamera does.
        controller.AttachCycleVelocityAccessor(() => sequencer.CurrentVelocity);
        controller.AttachAnimationRootMotionSource((dt, delta) =>
        {
            var root = new Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            };
            sequencer.Advance(dt, root);
            delta.Origin = root.Origin;
            delta.Orientation = root.Orientation;
        });
        controller.Motion.RemoveLinkAnimations = sequencer.Manager.HandleEnterWorld;
        controller.Motion.InitializeMotionTables = sequencer.Manager.InitializeState;
        controller.Motion.CheckForCompletedMotions =
            sequencer.Manager.CheckForCompletedMotions;
        controller.Motion.DefaultSink = new MotionTableDispatchSink(sequencer);
        sequencer.Manager.HandleEnterWorld();
        controller.Motion.HandleExitWorld();
        return (controller, sequencer);
    }
}
