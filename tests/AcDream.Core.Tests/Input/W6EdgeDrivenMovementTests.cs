using System;
using System.Numerics;
using AcDream.App.Input;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Tests.Input;

public class W6EdgeDrivenMovementTests
{
    private const uint NC = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;
    private const uint Run = 0x44000007u;

    private sealed class Loader : IAnimationLoader
    {
        private readonly System.Collections.Generic.Dictionary<uint, Animation> _anims = new();
        public void Register(uint id, Animation anim) => _anims[id] = anim;
        public Animation? LoadAnimation(uint id) => _anims.TryGetValue(id, out var a) ? a : null;
    }

    private static Animation MakeAnim(int frames)
    {
        var anim = new Animation();
        for (int f = 0; f < frames; f++)
        {
            var pf = new AnimationFrame(1);
            pf.Frames.Add(new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }
        return anim;
    }

    private static MotionData MakeMd(uint animId)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData { AnimId = qid, LowFrame = 0, HighFrame = -1, Framerate = 30f });
        return md;
    }

    private static AnimationSequencer MakeSequencer()
    {
        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(Vector3.One);

        var loader = new Loader();
        loader.Register(0x300u, MakeAnim(4));
        loader.Register(0x301u, MakeAnim(6));
        loader.Register(0x302u, MakeAnim(6));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NC };
        mt.StyleDefaults[(DRWMotionCommand)NC] = (DRWMotionCommand)Ready;
        mt.Cycles[(int)((NC << 16) | (Ready & 0xFFFFFFu))] = MakeMd(0x300u);
        mt.Cycles[(int)((NC << 16) | (Walk & 0xFFFFFFu))] = MakeMd(0x301u);
        mt.Cycles[(int)((NC << 16) | (Run & 0xFFFFFFu))] = MakeMd(0x302u);
        return new AnimationSequencer(setup, mt, loader);
    }

    private static PhysicsEngine MakeFlatEngine()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 1f;
        var terrain = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(0xA9B4FFFFu, terrain, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    private static PlayerMovementController MakeControllerWithRealSink(out AnimationSequencer seq)
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        seq = MakeSequencer();
        // The full W6 GameWindow bind set (EnterPlayerModeNow equivalent) —
        // sink binds BEFORE SetPosition, matching the R4-V5 stall-fix order
        // (SetPosition → StopCompletely needs the sink for its type-5
        // dispatch, else its A9 pending_motions node is orphaned).
        controller.Motion.DefaultSink = new MotionTableDispatchSink(seq);
        var s = seq;
        controller.Motion.RemoveLinkAnimations = () => s.Manager.HandleEnterWorld();
        controller.Motion.InitializeMotionTables = () => s.Manager.InitializeState();
        controller.Motion.CheckForCompletedMotions = s.Manager.CheckForCompletedMotions;
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;
        return controller;
    }

    private static void RunApplyPass(PlayerMovementController controller, float forwardSpeed)
    {
        controller.Motion.InterpretedState.ForwardSpeed = forwardSpeed;
        controller.Motion.apply_current_movement(cancelMoveTo: false, allowJump: false);
    }

    [Fact]
    public void ApplyPass_WithRealSink_ForwardSelfHeals()
    {
        var controller = MakeControllerWithRealSink(out _);
        var input = new MovementInput { Forward = true, Run = true };
        controller.Update(1f / 60f, input); // W press edge -> RunForward

        Assert.Equal(Run, controller.Motion.InterpretedState.ForwardCommand);

        // The live killer: a full apply pass mid-hold (was the ~10Hz
        // UM-echo tap pre-V5; see RunApplyPass).
        RunApplyPass(controller, 4.5f);

        Assert.Equal(Run, controller.Motion.InterpretedState.ForwardCommand);
        Assert.True(controller.Motion.get_state_velocity().Length() > 1f,
            "state velocity must survive the apply pass");
    }

    [Fact]
    public void HoldW_WithRealSink_AndEchoes_BodyKeepsMoving()
    {
        var controller = MakeControllerWithRealSink(out _);
        var input = new MovementInput { Forward = true, Run = true };

        float startX = 96f;
        Vector3 pos = new(startX, 96f, 50f);
        for (int f = 0; f < 120; f++)
        {
            if (f % 6 == 3)
                RunApplyPass(controller, 4.5f);
            pos = controller.Update(1f / 60f, input).Position;
        }

        Assert.True(pos.X - startX > 5f,
            $"expected sustained forward motion, got {pos.X - startX:F2} m");
    }

    [Fact]
    public void ShiftToggle_MidHold_WalkRunTransitionSurvivesEcho()
    {
        var controller = MakeControllerWithRealSink(out _);

        // Hold W at run for 30 frames.
        for (int f = 0; f < 30; f++)
            controller.Update(1f / 60f, new MovementInput { Forward = true, Run = true });
        Assert.Equal(Run, controller.Motion.InterpretedState.ForwardCommand);

        // Shift pressed (walk) — the set_hold_run edge demotes to walk.
        for (int f = 0; f < 30; f++)
        {
            if (f == 10) RunApplyPass(controller, 1.0f); // apply pass mid-walk
            controller.Update(1f / 60f, new MovementInput { Forward = true, Run = false });
        }
        Assert.Equal(Walk, controller.Motion.InterpretedState.ForwardCommand);

        // Shift released — promote back to run; survives another echo.
        for (int f = 0; f < 30; f++)
        {
            if (f == 10) RunApplyPass(controller, 4.5f);
            controller.Update(1f / 60f, new MovementInput { Forward = true, Run = true });
        }
        Assert.Equal(Run, controller.Motion.InterpretedState.ForwardCommand);
    }
}
