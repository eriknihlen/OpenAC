using System;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;
using Position = AcDream.Core.Physics.Position;

namespace AcDream.Core.Tests.Input;

public class PlayerMoveToCutoverTests
{
    private const uint NC = 0x8000003Du;
    private const uint MissileCombat = 0x8000003Fu;
    private const uint Ready = 0x41000003u;
    private const uint Reload = 0x40000016u;
    private const uint Walk = 0x45000005u;
    private const uint Run = 0x44000007u;
    private const uint TurnRight = 0x6500000Du;

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
        loader.Register(0x303u, MakeAnim(6));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NC };
        mt.StyleDefaults[(DRWMotionCommand)NC] = (DRWMotionCommand)Ready;
        mt.StyleDefaults[(DRWMotionCommand)MissileCombat] = (DRWMotionCommand)Ready;
        mt.Cycles[(int)((NC << 16) | (Ready & 0xFFFFFFu))] = MakeMd(0x300u);
        mt.Cycles[(int)((MissileCombat << 16) | (Ready & 0xFFFFFFu))] = MakeMd(0x300u);
        mt.Cycles[(int)((MissileCombat << 16) | (Reload & 0xFFFFFFu))] = MakeMd(0x301u);
        mt.Cycles[(int)((NC << 16) | (Walk & 0xFFFFFFu))] = MakeMd(0x301u);
        mt.Cycles[(int)((NC << 16) | (Run & 0xFFFFFFu))] = MakeMd(0x302u);
        mt.Cycles[(int)((NC << 16) | (TurnRight & 0xFFFFFFu))] = MakeMd(0x303u);
        mt.Cycles[(int)((MissileCombat << 16) | (TurnRight & 0xFFFFFFu))] = MakeMd(0x303u);
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

    private sealed class Rig
    {
        public required PlayerMovementController Controller;
        public required MoveToManager MoveTo;
        public required EntityPhysicsHost PlayerHost;
        public required Dictionary<uint, IPhysicsObjHost> Hosts;
        public int MoveToCompleteCount;
        public WeenieError LastCompleteError;

        public void AddStaticTarget(uint guid, Position position)
        {
            var target = new EntityPhysicsHost(
                guid,
                getPosition: () => position,
                getVelocity: () => Vector3.Zero,
                getRadius: () => 0.5f,
                inContact: () => true,
                minterpMaxSpeed: () => null,
                curTime: () => Controller.SimTimeSeconds,
                physicsTimerTime: () => Controller.SimTimeSeconds,
                getObjectA: id => Hosts.TryGetValue(id, out var host) ? host : null,
                handleUpdateTarget: _ => { },
                interruptCurrentMovement: () => { });
            Hosts[guid] = target;
        }
    }

    private static Rig MakeRig() => MakeRig(out _);

    private static Rig MakeRig(out AnimationSequencer seqOut)
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var seq = MakeSequencer();
        seqOut = seq;
        seq.MotionDoneTarget = (m, ok) => controller.Motion.MotionDone(m, ok);
        controller.Motion.DefaultSink = new MotionTableDispatchSink(seq);
        controller.Motion.RemoveLinkAnimations = () => seq.Manager.HandleEnterWorld();
        controller.Motion.InitializeMotionTables = () => seq.Manager.InitializeState();
        controller.Motion.CheckForCompletedMotions = seq.Manager.CheckForCompletedMotions;
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f; // heading 90 = facing +X

        const uint selfGuid = 0x5000000Au;
        var hosts = new Dictionary<uint, IPhysicsObjHost>();
        EntityPhysicsHost playerHost = null!;
        var rig = new Rig
        {
            Controller = controller,
            MoveTo = null!,
            PlayerHost = null!,
            Hosts = hosts,
        };
        var moveTo = new MoveToManager(
            controller.Motion,
            stopCompletely: () => controller.StopCompletelyAtPhysicsObjectBoundary(),
            getPosition: () => new Position(
                controller.CellId, controller.Position, controller.BodyOrientation),
            getHeading: () => MoveToMath.HeadingFromYaw(controller.Yaw),
            setHeading: (h, _) => controller.Yaw = MoveToMath.YawFromHeading(h),
            getOwnRadius: () => 0f,
            getOwnHeight: () => 0f,
            contact: () => controller.BodyInContact,
            isInterpolating: () => false,
            getVelocity: () => controller.BodyVelocity,
            getSelfId: () => selfGuid,
            setTarget: (ctx, id, radius, quantum) =>
                playerHost.SetTarget(ctx, id, radius, quantum),
            clearTarget: () => playerHost.ClearTarget(),
            getTargetQuantum: () => playerHost.TargetManager.GetTargetQuantum(),
            setTargetQuantum: quantum => playerHost.TargetManager.SetTargetQuantum(quantum),
            curTime: () => controller.SimTimeSeconds);
        playerHost = new EntityPhysicsHost(
            selfGuid,
            getPosition: () => new Position(
                controller.CellId, controller.Position, controller.BodyOrientation),
            getVelocity: () => controller.BodyVelocity,
            getRadius: () => 0.5f,
            inContact: () => controller.BodyInContact,
            minterpMaxSpeed: () => controller.Motion.GetMaxSpeed(),
            curTime: () => controller.SimTimeSeconds,
            physicsTimerTime: () => controller.SimTimeSeconds,
            getObjectA: id => hosts.TryGetValue(id, out var host) ? host : null,
            handleUpdateTarget: info => moveTo.HandleUpdateTarget(info),
            interruptCurrentMovement: () =>
                moveTo.CancelMoveTo(WeenieError.ActionCancelled));
        hosts[selfGuid] = playerHost;
        moveTo.MoveToComplete = err =>
        {
            rig.MoveToCompleteCount++;
            rig.LastCompleteError = err;
        };
        controller.MoveTo = moveTo;
        controller.Motion.InterruptCurrentMovement =
            () => moveTo.CancelMoveTo(WeenieError.ActionCancelled);
        rig.MoveTo = moveTo;
        rig.PlayerHost = playerHost;
        return rig;
    }

    /// <summary>Stands in for the player sequencer's MotionDone feed (bound
    /// in production via the R3-W2 MotionTableManager seam) — without it,
    /// pending_motions never drains in this fixture and the manager's
    /// wait-for-anims gates wedge.</summary>
    private static void Drain(PlayerMovementController c)
    {
        while (c.Motion.MotionsPending())
            c.Motion.MotionDone(0, true);
    }

    [Fact]
    public void ServerMoveToPosition_WalksToArrival_WithNoUserInput()
    {
        var rig = MakeRig();
        var c = rig.Controller;
        var dest = new Vector3(104f, 96f, 50f); // 8 m dead ahead (heading 90)

        // Mirrors the GameWindow wire path's P1 unpack store (00509730):
        // a server moveto arrives with IsAutonomous=false, routing the
        // per-tick pump's A3 dual dispatch to the interpreted branch.
        rig.Controller.SetLastMoveWasAutonomous(false);
        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(c.CellId, dest, Quaternion.Identity),
            Params = new MovementParameters { UseSpheres = false, DistanceToObject = 0.6f },
        });
        Drain(c);
        Assert.True(rig.MoveTo.IsMovingTo());

        for (int f = 0; f < 1200 && rig.MoveTo.IsMovingTo(); f++)
        {
            var result = c.Update(1f / 60f, new MovementInput());
            Assert.False(result.MotionStateChanged,
                $"manager-driven frame {f} must not flag MotionStateChanged");
            Drain(c);
        }

        Assert.False(rig.MoveTo.IsMovingTo(), "moveto must arrive within the frame budget");
        float distLeft = Vector2.Distance(
            new Vector2(c.Position.X, c.Position.Y), new Vector2(dest.X, dest.Y));
        Assert.True(distLeft <= 1.0f,
            $"body must stop at the arrival radius; {distLeft:F2} m left");
        Assert.Equal(1, rig.MoveToCompleteCount);
        Assert.Equal(WeenieError.None, rig.LastCompleteError);
        Assert.Equal(Ready, c.Motion.InterpretedState.ForwardCommand);
    }

    [Fact]
    public void ServerTurnToHeading_RotatesYaw_AndSnapsExact()
    {
        var rig = MakeRig();
        var c = rig.Controller;

        // Mirrors the GameWindow wire path's P1 unpack store (00509730):
        // a server moveto arrives with IsAutonomous=false, routing the
        // per-tick pump's A3 dual dispatch to the interpreted branch.
        rig.Controller.SetLastMoveWasAutonomous(false);
        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.TurnToHeading,
            Params = new MovementParameters { DesiredHeading = 180f }, // South = -Y
        });
        Drain(c);
        Assert.True(rig.MoveTo.IsMovingTo());

        for (int f = 0; f < 600 && rig.MoveTo.IsMovingTo(); f++)
        {
            c.Update(1f / 60f, new MovementInput());
            Drain(c);
        }

        Assert.False(rig.MoveTo.IsMovingTo(), "turn-to must complete within the frame budget");
        // HandleTurnToHeading's arrival snap (the ONE heading snap in the
        // family, 0052a146) writes through the Yaw seam: heading 180 → yaw
        // -π/2 per the P5 bridge.
        Assert.Equal(MoveToMath.YawFromHeading(180f), c.Yaw, 3);
        Assert.Equal(1, rig.MoveToCompleteCount);
    }

    [Fact]
    public void MovementKeyEdge_CancelsMoveTo_WithoutFiringComplete()
    {
        var rig = MakeRig();
        var c = rig.Controller;

        // Mirrors the GameWindow wire path's P1 unpack store (00509730):
        // a server moveto arrives with IsAutonomous=false, routing the
        // per-tick pump's A3 dual dispatch to the interpreted branch.
        rig.Controller.SetLastMoveWasAutonomous(false);
        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(c.CellId, new Vector3(150f, 96f, 50f), Quaternion.Identity),
            Params = new MovementParameters { UseSpheres = false },
        });
        Drain(c);
        Assert.True(rig.MoveTo.IsMovingTo());
        c.Update(1f / 60f, new MovementInput());
        Drain(c);
        Assert.True(rig.MoveTo.IsMovingTo());

        var result = c.Update(1f / 60f, new MovementInput { Forward = true });

        Assert.False(rig.MoveTo.IsMovingTo(), "user input must cancel the moveto (TS-36)");
        Assert.Equal(0, rig.MoveToCompleteCount);
        Assert.True(result.MotionStateChanged);
    }

    [Fact]
    public void LoginQueue_DrainsToEmpty_UnderProductionFeed()
    {
        var rig = MakeRig(out var seq);
        var c = rig.Controller;
        for (int f = 0; f < 300 && c.Motion.MotionsPending(); f++)
        {
            c.Update(1f / 60f, new MovementInput());
            seq.Advance(1f / 60f);
        }

        Assert.False(c.Motion.MotionsPending(),
            "login pending_motions must drain to empty under the production feed");
    }

    [Fact]
    public void ServerMoveToPosition_ProductionCompletionFeed_WalksToArrival()
    {
        var rig = MakeRig(out var seq);
        var c = rig.Controller;
        var dest = new Vector3(104f, 96f, 50f); // 8 m dead ahead (heading 90)

        // A few idle frames first — production always has render ticks
        // between login (SetPosition → StopCompletely queues the A9 node
        // with NO dispatch) and the first Use.
        for (int f = 0; f < 30; f++)
        {
            c.Update(1f / 60f, new MovementInput());
            seq.Advance(1f / 60f);
        }

        // Mirrors the GameWindow wire path's P1 unpack store (00509730):
        // a server moveto arrives with IsAutonomous=false, routing the
        // per-tick pump's A3 dual dispatch to the interpreted branch.
        rig.Controller.SetLastMoveWasAutonomous(false);
        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(c.CellId, dest, Quaternion.Identity),
            Params = new MovementParameters { UseSpheres = false, DistanceToObject = 0.6f },
        });
        Assert.True(rig.MoveTo.IsMovingTo());

        for (int f = 0; f < 1200 && rig.MoveTo.IsMovingTo(); f++)
        {
            c.Update(1f / 60f, new MovementInput());
            seq.Advance(1f / 60f);
        }

        string queueDump = string.Join(", ",
            System.Linq.Enumerable.Select(c.Motion.PendingMotions, n => $"0x{n.Motion:X8}"));
        Assert.False(rig.MoveTo.IsMovingTo(),
            $"moveto wedged: pending_motions never drained under the production "
            + $"completion feed (queue: [{queueDump}])");
        Assert.Equal(1, rig.MoveToCompleteCount);
    }

    [Fact]
    public void ServerTurnToObject_AfterMissileReload_ProductionFeed_RotatesAndCompletes()
    {
        var rig = MakeRig(out var seq);
        var c = rig.Controller;
        for (int f = 0; f < 120; f++)
        {
            c.Update(1f / 60f, new MovementInput { Run = true });
            seq.Advance(1f / 60f);
        }

        Assert.Equal(WeenieError.None, c.Motion.DoMotion(
            MissileCombat, new MovementParameters()));
        Assert.Equal(WeenieError.None, c.Motion.DoInterpretedMotion(
            Reload, new MovementParameters { Speed = 2f }));

        const uint targetGuid = 0x80001234u;
        rig.AddStaticTarget(targetGuid, new Position(
            c.CellId, new Vector3(88f, 96f, 50f), Quaternion.Identity));

        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.TurnToObject,
            ObjectId = targetGuid,
            TopLevelId = targetGuid,
            Params = new MovementParameters
            {
                StopCompletelyFlag = true,
                Speed = 1f,
            },
        });

        float initialYaw = c.Yaw;
        for (int f = 0; f < 600 && rig.MoveTo.IsMovingTo(); f++)
        {
            c.Update(1f / 60f, new MovementInput { Run = true });
            seq.Advance(1f / 60f);
        }

        string interpQueue = string.Join(", ",
            c.Motion.PendingMotions.Select(n => $"0x{n.Motion:X8}"));
        string animationQueue = string.Join(", ",
            seq.Manager.PendingAnimations.Select(n => $"0x{n.Motion:X8}/{n.NumAnims}"));
        Assert.True(initialYaw != c.Yaw,
            $"turn never changed yaw; moving={rig.MoveTo.IsMovingTo()} "
            + $"complete={rig.MoveToCompleteCount} error={rig.LastCompleteError} "
            + $"interp=[{interpQueue}] animation=[{animationQueue}]");
        Assert.False(rig.MoveTo.IsMovingTo());
        Assert.Equal(1, rig.MoveToCompleteCount);
        Assert.InRange(MoveToMath.HeadingFromYaw(c.Yaw), 269.99f, 270.01f);
        Assert.False(c.Motion.MotionsPending());
    }

    [Fact]
    public void KeyboardTurnRelease_ThenAttackStop_DrainsBeforeServerTurnToObject()
    {
        var rig = MakeRig(out var seq);
        var c = rig.Controller;

        for (int f = 0; f < 90; f++)
        {
            c.Update(1f / 60f, new MovementInput { Run = true, TurnRight = true });
            seq.Advance(1f / 60f);
        }

        c.Update(1f / 60f, new MovementInput { Run = true });
        seq.Advance(1f / 60f);
        for (int f = 0; f < 120 && c.Motion.MotionsPending(); f++)
        {
            c.Update(1f / 60f, new MovementInput { Run = true });
            seq.Advance(1f / 60f);
        }

        Assert.False(c.Motion.MotionsPending(),
            "keyboard turn release must leave no interpreter queue orphan");
        Assert.Empty(seq.Manager.PendingAnimations);

        Assert.True(c.PrepareForAttackRequest());
        Assert.False(c.Motion.MotionsPending(),
            "attack-entry StopCompletely must synchronously drain zero-tick Ready");
        Assert.Empty(seq.Manager.PendingAnimations);

        const uint targetGuid = 0x80004321u;
        rig.AddStaticTarget(targetGuid, new Position(
            c.CellId, c.Position + new Vector3(12f, 0f, 0f), Quaternion.Identity));
        float initialYaw = c.Yaw;
        rig.Controller.SetLastMoveWasAutonomous(false);
        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.TurnToObject,
            ObjectId = targetGuid,
            TopLevelId = targetGuid,
            Params = new MovementParameters
            {
                StopCompletelyFlag = true,
                Speed = 1f,
            },
        });

        for (int f = 0; f < 600 && rig.MoveTo.IsMovingTo(); f++)
        {
            c.Update(1f / 60f, new MovementInput { Run = true });
            seq.Advance(1f / 60f);
        }

        Assert.NotEqual(initialYaw, c.Yaw);
        Assert.False(rig.MoveTo.IsMovingTo());
        Assert.Equal(1, rig.MoveToCompleteCount);
        Assert.InRange(MoveToMath.HeadingFromYaw(c.Yaw), 89.99f, 90.01f);
        Assert.False(c.Motion.MotionsPending());
        Assert.Empty(seq.Manager.PendingAnimations);
    }

    [Fact]
    public void JumpRelease_CancelsMoveTo()
    {
        var rig = MakeRig();
        var c = rig.Controller;

        // Mirrors the GameWindow wire path's P1 unpack store (00509730):
        // a server moveto arrives with IsAutonomous=false, routing the
        // per-tick pump's A3 dual dispatch to the interpreted branch.
        rig.Controller.SetLastMoveWasAutonomous(false);
        rig.MoveTo.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(c.CellId, new Vector3(150f, 96f, 50f), Quaternion.Identity),
            Params = new MovementParameters { UseSpheres = false },
        });
        Drain(c);
        Assert.True(rig.MoveTo.IsMovingTo());

        c.Update(1f / 60f, new MovementInput { Jump = true });
        c.Update(1f / 60f, new MovementInput());

        Assert.False(rig.MoveTo.IsMovingTo(), "jump must cancel the moveto (TS-36)");
        Assert.Equal(0, rig.MoveToCompleteCount);
    }
}
