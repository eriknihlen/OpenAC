using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;
using CorePosition = AcDream.Core.Physics.Position;

namespace AcDream.Core.Tests.Physics.Motion;


internal sealed class ChaseLoader : IAnimationLoader
{
    private readonly Dictionary<uint, Animation> _anims = new();
    public void Register(uint id, Animation anim) => _anims[id] = anim;
    public Animation? LoadAnimation(uint id) =>
        _anims.TryGetValue(id, out var a) ? a : null;
}

internal sealed class RemoteChaseHarness
{
    public const uint NonCombat = 0x8000003Du;
    public const uint Combat = 0x8000003Cu;
    public const uint Ready = 0x41000003u;
    public const uint Walk = 0x45000005u;
    public const uint Run = 0x44000007u;
    public const uint TurnRight = 0x6500000Du;
    public const uint AttackAction = 0x10000063u; // one of the live scamp swings

    public const uint CreatureGuid = 0x80000BE5u;
    public const uint PlayerGuid = 0x5000000Au;

    public const float Dt = 1f / 60f;

    public const float OwnRadius = 0.3f;
    public const float StickyTargetRadius = 0.5f;

    public readonly PhysicsBody Body = new()
    {
        State = PhysicsStateFlags.ReportCollisions,
        TransientState = TransientStateFlags.Contact
                       | TransientStateFlags.OnWalkable
                       | TransientStateFlags.Active,
        InWorld = true,
    };
    public readonly MotionInterpreter Interp;
    public readonly AnimationSequencer Seq;
    public readonly MotionTableDispatchSink Sink;

    public readonly MovementManager Movement;

    public MoveToManager Mgr => Movement.MoveTo!;

    public readonly PositionManager Pm;
    private readonly CreatureHost _creatureHost;
    private readonly TargetHost _playerHost;

    public Vector3 PlayerPos;
    public Vector3 PlayerVelocity;

    // Scripted targeting stand-in (GameWindow: EntityPhysicsHost/TargetManager).
    private bool _targetArmed;
    private double _lastDeliveryTime = double.NegativeInfinity;
    private double _quantum = 0.5;
    public double Now;

    public int BeginTurnBlocked = 0;
    public int BeginTurnUnblocked = 0;
    public int RunInstalls;      // substate transitions INTO RunForward/Walk fwd
    public int TicksInRun;
    public int TicksInWalkFwd;
    public int TotalTicks;
    public int MaxRunStreak;
    private int _runStreak;
    private uint _prevSubstate;

    public readonly List<string> Trace = new();
    private readonly ITestOutputHelper? _log;

    public RemoteChaseHarness(ITestOutputHelper? log = null)
    {
        _log = log;
        var (setup, mtable, loader) = BuildFixture();
        Seq = new AnimationSequencer(setup, mtable, loader);

        // ── GameWindow spawn path (OnLiveEntitySpawnedLocked ~3781) ──
        Seq.InitializeState();
        Seq.SetCycle(NonCombat, Ready);

        Body.Orientation = MoveToMath.SetHeading(Quaternion.Identity, 0f); // face North
        Interp = new MotionInterpreter(Body)
        {
            WeenieObj = new RemoteWeenie(),
        };

        Sink = new MotionTableDispatchSink(Seq);
        Interp.DefaultSink = Sink;
        Interp.RemoveLinkAnimations = () => Seq.Manager.HandleEnterWorld();
        Interp.InitializeMotionTables = () => Seq.Manager.InitializeState();
        Interp.CheckForCompletedMotions = Seq.Manager.CheckForCompletedMotions;

        Movement = new MovementManager(Interp)
        {
            MoveToFactory = () =>
            {
                var mtm = new MoveToManager(
                    Interp,
                    stopCompletely: () => Movement!.PerformMovement(
                        new MovementStruct { Type = MovementType.StopCompletely }),
                    getPosition: () => new CorePosition(1u, Body.Position, Body.Orientation),
                    getHeading: () => MoveToMath.GetHeading(Body.Orientation),
                    setHeading: (h, _) => Body.Orientation =
                        MoveToMath.SetHeading(Body.Orientation, h),
                    getOwnRadius: () => OwnRadius,
                    getOwnHeight: () => 1f,
                    contact: () => Body.OnWalkable,
                    isInterpolating: () => false,
                    getVelocity: () => Body.Velocity,
                    getSelfId: () => CreatureGuid,
                    setTarget: (ctx, tlid, radius, q) =>
                    {
                        _targetArmed = tlid == PlayerGuid;
                        // TargetManager delivers the FIRST info synchronously on
                        // SetTarget (live log: HandleUpdateTarget printed directly
                        // after the arm, same network phase).
                        DeliverTargetInfo();
                    },
                    clearTarget: () => _targetArmed = false,
                    getTargetQuantum: () => _quantum,
                    setTargetQuantum: q => _quantum = q,
                    curTime: () => Now);
                mtm.StickTo = (tlid, radius, height) => Pm!.StickTo(tlid, radius, height);
                mtm.Unstick = Pm!.UnStick;
                return mtm;
            },
        };

        Interp.InterruptCurrentMovement =
            () => Movement.CancelMoveTo(WeenieError.ActionCancelled);

        _playerHost = new TargetHost(this);
        _creatureHost = new CreatureHost(this);
        Pm = new PositionManager(_creatureHost);
        Movement.MakeMoveToManager();
        Interp.UnstickFromObject = Pm.UnStick;

        // ── The anim-loop MotionDone binding (GameWindow.cs:10266) ──
        Seq.MotionDoneTarget = (m, ok) => Interp.MotionDone(m, ok);

        _prevSubstate = Seq.Manager.State.Substate;
    }

    // ── Wire events ─────────────────────────────────────────────────────────

    /// <summary>mt-0 UM (funnel apply): interrupt + unstick head, then
    /// MoveToInterpretedState — GameWindow.cs:4893 + 5008.</summary>
    public void UmInterpreted(uint stance, uint forward, float forwardSpeed = 1f,
        params InboundMotionAction[] actions)
    {
        Interp.InterruptCurrentMovement?.Invoke();
        Interp.UnstickFromObject?.Invoke();

        var ims = new InboundInterpretedState
        {
            CurrentStyle = stance,
            ForwardCommand = forward,
            ForwardSpeed = forwardSpeed,
            SideStepCommand = 0u,
            SideStepSpeed = 1f,
            TurnCommand = 0u,
            TurnSpeed = 1f,
        };
        if (actions.Length > 0)
            ims.Actions = new List<InboundMotionAction>(actions);

        Interp.MoveToInterpretedState(ims, Sink);
    }

    public void UmMoveToObject(
        float speed = 2.08f,
        float distanceToObject = 0.6f,
        float walkRunThreshold = 15f,
        bool sticky = false,
        bool useSpheres = false,
        float targetRadius = 0f,
        float targetHeight = 0f,
        uint wireStance = 0u)
    {
        Interp.InterruptCurrentMovement?.Invoke();
        Interp.UnstickFromObject?.Invoke();

        if (wireStance != 0u)
        {
            uint wireStyle = 0x80000000u | wireStance;
            if (Interp.InterpretedState.CurrentStyle != wireStyle)
                Interp.DoMotion(wireStyle, new MovementParameters());
        }

        var mp = new MovementParameters
        {
            CanWalk = true,
            CanRun = true,
            CanCharge = true,
            CanSidestep = false,
            CanWalkBackwards = false,
            Speed = speed,
            DistanceToObject = distanceToObject,
            WalkRunThreshhold = walkRunThreshold,
            FailDistance = float.MaxValue,
            Sticky = sticky,
            UseSpheres = useSpheres,
        };
        var ms = new MovementStruct
        {
            Type = MovementType.MoveToObject,
            ObjectId = PlayerGuid,
            TopLevelId = PlayerGuid,
            Pos = new CorePosition(1u, PlayerPos, Quaternion.Identity),
            Params = mp,
            Radius = targetRadius,
            Height = targetHeight,
        };
        Movement.PerformMovement(ms);
    }

    private void DeliverTargetInfo()
    {
        if (!_targetArmed) return;
        _lastDeliveryTime = Now;
        var pos = new CorePosition(1u, PlayerPos, Quaternion.Identity);
        var info = new TargetInfo
        {
            ObjectId = PlayerGuid,
            Status = TargetStatus.Ok,
            TargetPosition = pos,
            InterpolatedPosition = pos,
        };
        Movement.HandleUpdateTarget(info);
        Pm.HandleUpdateTarget(info);
    }


    private sealed class CreatureHost : IPhysicsObjHost
    {
        private readonly RemoteChaseHarness _h;
        public CreatureHost(RemoteChaseHarness h) => _h = h;
        public uint Id => CreatureGuid;
        public CorePosition Position => new(1u, _h.Body.Position, _h.Body.Orientation);
        public Vector3 Velocity => _h.Body.Velocity;
        public float Radius => OwnRadius;
        public bool InContact => _h.Body.OnWalkable;
        public float? MinterpMaxSpeed => _h.Interp.GetMaxSpeed();
        public double CurTime => _h.Now;
        public double PhysicsTimerTime => _h.Now;
        public IPhysicsObjHost? GetObjectA(uint id)
            => id == PlayerGuid ? _h._playerHost : null;
        public IPhysicsObjHost? GetRelationshipTarget(uint objectId)
            => GetObjectA(objectId);

        public void HandleUpdateTarget(TargetInfo info)
        {
            _h.Movement.HandleUpdateTarget(info);
            _h.Pm.HandleUpdateTarget(info);
        }

        public void InterruptCurrentMovement()
            => _h.Movement.CancelMoveTo(WeenieError.ActionCancelled);

        public void SetTarget(uint contextId, uint objectId, float radius, double quantum)
        {
            _h._targetArmed = objectId == PlayerGuid;
            _h._quantum = quantum;
            _h.DeliverTargetInfo();
        }

        public void ClearTarget() => _h._targetArmed = false;
        public void ReceiveTargetUpdate(TargetInfo info, IPhysicsObjHost sender) { }
        public void AddVoyeur(IPhysicsObjHost watcher, float radius, double quantum) { }
        public void RemoveVoyeur(uint watcherId, IPhysicsObjHost expectedWatcher) { }
    }

    private sealed class TargetHost : IPhysicsObjHost
    {
        private readonly RemoteChaseHarness _h;
        public TargetHost(RemoteChaseHarness h) => _h = h;
        public uint Id => PlayerGuid;
        public CorePosition Position => new(1u, _h.PlayerPos, Quaternion.Identity);
        public Vector3 Velocity => _h.PlayerVelocity;
        public float Radius => StickyTargetRadius;
        public bool InContact => true;
        public float? MinterpMaxSpeed => null;
        public double CurTime => _h.Now;
        public double PhysicsTimerTime => _h.Now;
        public IPhysicsObjHost? GetObjectA(uint id) => null;
        public IPhysicsObjHost? GetRelationshipTarget(uint objectId) => null;
        public void HandleUpdateTarget(TargetInfo info) { }
        public void InterruptCurrentMovement() { }
        public void SetTarget(uint contextId, uint objectId, float radius, double quantum) { }
        public void ClearTarget() { }
        public void ReceiveTargetUpdate(TargetInfo info, IPhysicsObjHost sender) { }
        public void AddVoyeur(IPhysicsObjHost watcher, float radius, double quantum) { }
        public void RemoveVoyeur(uint watcherId, IPhysicsObjHost expectedWatcher) { }
    }

    // ── The per-tick pipeline (GameWindow.TickAnimations order) ────────────

    public void Tick()
    {
        Now += Dt;
        TotalTicks++;

        PlayerPos += PlayerVelocity * Dt;

        var rootFrame = new Frame
        {
            Origin = Vector3.Zero,
            Orientation = Quaternion.Identity,
        };
        Seq.Advance(Dt, rootFrame);

        var pmDelta = new MotionDeltaFrame
        {
            Origin = rootFrame.Origin,
            Orientation = rootFrame.Orientation,
        };
        Pm.AdjustOffset(pmDelta, Dt);
        if (pmDelta.Origin != Vector3.Zero)
            Body.Position += Vector3.Transform(pmDelta.Origin, Body.Orientation);
        if (!pmDelta.Orientation.IsIdentity)
            Body.Orientation = Quaternion.Normalize(
                Body.Orientation * pmDelta.Orientation);

        Body.calc_acceleration();
        Body.UpdatePhysicsInternal(Dt);

        // process_hooks precedes the manager tail. AnimationDone decrements the
        // exact pending-animation node that owns the transition.
        var hooks = Seq.ConsumePendingHooks();
        for (int i = 0; i < hooks.Count; i++)
        {
            if (hooks[i] is DatReaderWriter.Types.AnimationDoneHook)
                Seq.Manager.AnimationDone(success: true);
        }

        if (_targetArmed && Now - _lastDeliveryTime >= _quantum)
            DeliverTargetInfo();
        Movement.UseTime();
        Seq.Manager.UseTime();
        Pm.UseTime();

        // ── Observables ──
        uint substate = Seq.Manager.State.Substate;
        if (substate == Run) TicksInRun++;
        if (substate == Walk) TicksInWalkFwd++;
        bool inFwd = substate == Run || substate == Walk;
        if (inFwd)
        {
            _runStreak++;
            if (_runStreak > MaxRunStreak) MaxRunStreak = _runStreak;
        }
        else
        {
            _runStreak = 0;
        }
        if (inFwd && _prevSubstate != Run && _prevSubstate != Walk)
            RunInstalls++;
        _prevSubstate = substate;
    }

    public float Heading => MoveToMath.GetHeading(Body.Orientation);
    public float DistToPlayer => Vector3.Distance(Body.Position, PlayerPos);

    public void Snapshot(string tag)
    {
        string line = FormattableString.Invariant(
            $"t={Now,6:F2} {tag,-14} mt={Mgr.MovementTypeState,-14} substate=0x{Seq.Manager.State.Substate:X8} heading={Heading,6:F1} dist={DistToPlayer,6:F2} pending={Interp.MotionsPending()} omega.Z={Seq.CurrentOmega.Z,6:F3}");
        Trace.Add(line);
        _log?.WriteLine(line);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private static Animation MakeAnim(int numFrames)
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var pf = new AnimationFrame(1u);
            pf.Frames.Add(new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity });
            anim.PartFrames.Add(pf);
        }
        return anim;
    }

    private static MotionData MakeMd(uint animId, float framerate = 30f,
        Vector3? velocity = null, Vector3? omega = null)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData
        {
            AnimId = qid,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = framerate,
        });
        if (velocity is { } v)
        {
            md.Velocity = v;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasVelocity;
        }
        if (omega is { } o)
        {
            md.Omega = o;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasOmega;
        }
        return md;
    }

    private static void AddLink(MotionTable mt, uint style, uint from, uint to, MotionData md)
    {
        int outer = (int)((style << 16) | (from & 0xFFFFFFu));
        if (!mt.Links.TryGetValue(outer, out var cmd))
        {
            cmd = new MotionCommandData();
            mt.Links[outer] = cmd;
        }
        cmd.MotionData[(int)to] = md;
    }

    private static (Setup, MotionTable, ChaseLoader) BuildFixture()
    {
        const uint ReadyAnimNC = 0x200u;
        const uint ReadyAnimC = 0x201u;
        const uint WalkAnim = 0x202u;
        const uint RunAnim = 0x203u;
        const uint ReadyToWalk = 0x204u;
        const uint WalkToReady = 0x205u;
        const uint ReadyToRun = 0x206u;
        const uint RunToReady = 0x207u;
        const uint StanceLink = 0x208u;
        const uint AttackLink = 0x209u;
        const uint TurnAnim = 0x20Au;

        var setup = new Setup();
        setup.Parts.Add(0x01000000u);
        setup.DefaultScale.Add(Vector3.One);

        var loader = new ChaseLoader();
        loader.Register(ReadyAnimNC, MakeAnim(30));
        loader.Register(ReadyAnimC, MakeAnim(30));
        loader.Register(WalkAnim, MakeAnim(30));
        loader.Register(RunAnim, MakeAnim(30));
        loader.Register(ReadyToWalk, MakeAnim(15));
        loader.Register(WalkToReady, MakeAnim(15));
        loader.Register(ReadyToRun, MakeAnim(15));
        loader.Register(RunToReady, MakeAnim(15));
        loader.Register(StanceLink, MakeAnim(15));
        loader.Register(AttackLink, MakeAnim(45)); // 1.5 s swing
        loader.Register(TurnAnim, MakeAnim(30));

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombat };
        mt.StyleDefaults[(DRWMotionCommand)NonCombat] = (DRWMotionCommand)Ready;
        mt.StyleDefaults[(DRWMotionCommand)Combat] = (DRWMotionCommand)Ready;

        static int CycleKey(uint style, uint substate)
            => (int)((style << 16) | (substate & 0xFFFFFFu));

        mt.Cycles[CycleKey(NonCombat, Ready)] = MakeMd(ReadyAnimNC);
        mt.Cycles[CycleKey(Combat, Ready)] = MakeMd(ReadyAnimC);
        foreach (uint style in new[] { NonCombat, Combat })
        {
            mt.Cycles[CycleKey(style, Walk)] =
                MakeMd(WalkAnim, velocity: new Vector3(0f, 3.12f, 0f));
            mt.Cycles[CycleKey(style, Run)] =
                MakeMd(RunAnim, velocity: new Vector3(0f, 4.0f, 0f));
            AddLink(mt, style, Ready, Walk, MakeMd(ReadyToWalk));
            AddLink(mt, style, Walk, Ready, MakeMd(WalkToReady));
            AddLink(mt, style, Ready, Run, MakeMd(ReadyToRun));
            AddLink(mt, style, Run, Ready, MakeMd(RunToReady));
        }
        AddLink(mt, NonCombat, Ready, Combat, MakeMd(StanceLink));
        AddLink(mt, Combat, Ready, NonCombat, MakeMd(StanceLink));
        AddLink(mt, Combat, Ready, AttackAction, MakeMd(AttackLink));

        mt.Modifiers[(int)(TurnRight & 0xFFFFFFu)] = MakeMd(
            TurnAnim,
            omega: new Vector3(0f, 0f, -1.5f));

        return (setup, mt, loader);
    }
}

public sealed class RemoteChaseEndToEndHarnessTests
{
    private readonly ITestOutputHelper _out;
    public RemoteChaseEndToEndHarnessTests(ITestOutputHelper output) => _out = output;

    private const float Dt = RemoteChaseHarness.Dt;

    private static int Seconds(float s) => (int)MathF.Round(s / Dt);

    [Theory]
    [InlineData(90f)]    // target to the East → TurnRight path
    [InlineData(270f)]   // target to the West → TurnLeft path
    [InlineData(170f)]   // near-reversal → long right turn
    public void SingleArm_TurnCompletes_AndForwardInstalls(float bearingDeg)
    {
        var h = new RemoteChaseHarness(_out);

        float rad = (90f - bearingDeg) * MathF.PI / 180f;
        h.PlayerPos = new Vector3(MathF.Cos(rad), MathF.Sin(rad), 0f) * 15f;
        h.PlayerVelocity = Vector3.Zero;

        // settle spawn state
        for (int i = 0; i < Seconds(0.5f); i++) h.Tick();
        h.Snapshot("spawned");

        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready);
        for (int i = 0; i < Seconds(1.5f); i++) h.Tick();
        h.Snapshot("post-stance");

        // the arm
        h.UmMoveToObject();
        h.Snapshot("armed");

        int installTick = -1;
        for (int i = 0; i < Seconds(6f); i++)
        {
            h.Tick();
            if (installTick < 0
                && (h.Seq.Manager.State.Substate == RemoteChaseHarness.Run
                    || h.Seq.Manager.State.Substate == RemoteChaseHarness.Walk))
            {
                installTick = i;
                h.Snapshot("fwd-install");
            }
            if (i % Seconds(0.5f) == 0) h.Snapshot("tick");
        }
        h.Snapshot("end");

        Assert.True(installTick >= 0,
            $"forward cycle never installed within 6 s of the arm (bearing {bearingDeg}°); " +
            $"final: mt={h.Mgr.MovementTypeState} substate=0x{h.Seq.Manager.State.Substate:X8} " +
            $"heading={h.Heading:F1} pending={h.Interp.MotionsPending()} omega.Z={h.Seq.CurrentOmega.Z:F3}");
        Assert.True(installTick <= Seconds(4f),
            $"forward cycle took {installTick * Dt:F2} s to install (bearing {bearingDeg}°) — " +
            "retail installs within the turn duration (~1-2 s)");
    }

    [Fact]
    public void FleeingTarget_RunSustainedAcrossRearms()
    {
        var h = new RemoteChaseHarness(_out);

        h.PlayerPos = new Vector3(0f, 10f, 0f);       // dead ahead, 10 m
        h.PlayerVelocity = new Vector3(0f, 4f, 0f);   // fleeing straight away

        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready);
        for (int i = 0; i < Seconds(1.5f); i++) h.Tick();

        int arms = 0;
        int chaseTicks = Seconds(12f);
        for (int i = 0; i < chaseTicks; i++)
        {
            if (i % Seconds(2f) == 0)
            {
                h.UmMoveToObject();
                arms++;
                h.Snapshot($"arm#{arms}");
            }
            h.Tick();
            if (i % Seconds(1f) == 0) h.Snapshot("tick");
        }
        h.Snapshot("end");

        int fwdTicks = h.TicksInRun + h.TicksInWalkFwd;
        float fwdFraction = fwdTicks / (float)chaseTicks;
        _out.WriteLine(FormattableString.Invariant(
            $"arms={arms} installs={h.RunInstalls} fwdTicks={fwdTicks}/{chaseTicks} ({fwdFraction:P0}) maxStreak={h.MaxRunStreak * Dt:F2}s"));

        Assert.True(h.RunInstalls >= arms - 1,
            $"run installed only {h.RunInstalls}× across {arms} arms — " +
            "retail reinstalls per arm (21/22)");
        Assert.True(fwdFraction > 0.5f,
            $"forward substate held only {fwdFraction:P0} of the chase — the run is not " +
            "sustained (live #170 symptom: short bursts + idle glide)");
    }

    [Fact]
    public void AttackUm_ThenRearm_RunReinstalls_AndQueueDrains()
    {
        var h = new RemoteChaseHarness(_out);

        h.PlayerPos = new Vector3(0f, 12f, 0f);
        h.PlayerVelocity = Vector3.Zero;

        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready);
        for (int i = 0; i < Seconds(1.5f); i++) h.Tick();

        h.UmMoveToObject();
        for (int i = 0; i < Seconds(3f); i++) h.Tick();
        h.Snapshot("chasing");

        // the swing: interrupt + action (stamp 1, autonomous false)
        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready, 1f,
            new InboundMotionAction(RemoteChaseHarness.AttackAction, Stamp: 1,
                Autonomous: false, Speed: 1f));
        for (int i = 0; i < Seconds(2.5f); i++) h.Tick();
        h.Snapshot("post-swing");

        Assert.False(h.Interp.MotionsPending(),
            "pending_motions did not fully empty after the swing — the #170 " +
            "residual (retail: add_to_queue == MotionDone exactly)");

        h.PlayerPos += new Vector3(0f, 10f, 0f);
        h.UmMoveToObject();
        int installTick = -1;
        for (int i = 0; i < Seconds(5f); i++)
        {
            h.Tick();
            if (installTick < 0
                && (h.Seq.Manager.State.Substate == RemoteChaseHarness.Run
                    || h.Seq.Manager.State.Substate == RemoteChaseHarness.Walk))
            {
                installTick = i;
            }
        }
        h.Snapshot("post-rearm");

        Assert.True(installTick >= 0,
            $"run did not reinstall after the post-swing re-arm; " +
            $"mt={h.Mgr.MovementTypeState} substate=0x{h.Seq.Manager.State.Substate:X8} " +
            $"pending={h.Interp.MotionsPending()}");
    }


    private static float AbsHeadingDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    [Fact]
    public void StickyArrival_TracksStrafingTarget_ThenLeaseExpires()
    {
        var h = new RemoteChaseHarness(_out);
        h.PlayerPos = new Vector3(0f, 10f, 0f);   // dead ahead (North)
        h.PlayerVelocity = Vector3.Zero;

        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready);
        for (int i = 0; i < Seconds(1.5f); i++) h.Tick();

        h.UmMoveToObject(sticky: true, useSpheres: true,
            targetRadius: RemoteChaseHarness.StickyTargetRadius, targetHeight: 1.2f);

        int stickTick = -1;
        for (int i = 0; i < Seconds(8f) && stickTick < 0; i++)
        {
            h.Tick();
            if (h.Pm.GetStickyObjectId() == RemoteChaseHarness.PlayerGuid)
                stickTick = i;
        }
        h.Snapshot("stuck");
        Assert.True(stickTick >= 0,
            $"sticky never armed within 8 s of the arm; mt={h.Mgr.MovementTypeState} " +
            $"dist={h.DistToPlayer:F2}");
        Assert.Equal(MovementType.Invalid, h.Mgr.MovementTypeState);

        // The target strafes — the follower must track gap AND facing
        // (adjust_offset resolves the LIVE target via GetObjectA per tick).
        h.PlayerVelocity = new Vector3(2f, 0f, 0f);
        for (int i = 0; i < Seconds(0.8f); i++) h.Tick();
        h.Snapshot("strafed");

        Assert.Equal(RemoteChaseHarness.PlayerGuid, h.Pm.GetStickyObjectId()); // inside the lease
        float gap = MoveToMath.CylinderDistanceNoZ(
            RemoteChaseHarness.OwnRadius, h.Body.Position,
            RemoteChaseHarness.StickyTargetRadius, h.PlayerPos);
        Assert.True(MathF.Abs(gap - StickyManager.StickyRadius) < 0.1f,
            $"stick gap {gap:F2} m — expected ≈{StickyManager.StickyRadius:F1} m (StickyRadius)");
        float bearing = MoveToMath.PositionHeading(h.Body.Position, h.PlayerPos);
        Assert.True(AbsHeadingDiff(h.Heading, bearing) < 5f,
            $"facing {h.Heading:F1}° vs bearing {bearing:F1}° — sticky facing not tracking");

        h.PlayerVelocity = Vector3.Zero;
        for (int i = 0; i < Seconds(0.5f); i++) h.Tick();
        Assert.Equal(0u, h.Pm.GetStickyObjectId());
    }

    [Fact]
    public void NextPerformMovement_Unsticks_ThenRearrivalResticks()
    {
        var h = new RemoteChaseHarness(_out);
        h.PlayerPos = new Vector3(0f, 8f, 0f);
        h.PlayerVelocity = Vector3.Zero;

        h.UmInterpreted(RemoteChaseHarness.Combat, RemoteChaseHarness.Ready);
        for (int i = 0; i < Seconds(1.5f); i++) h.Tick();

        h.UmMoveToObject(sticky: true, useSpheres: true,
            targetRadius: RemoteChaseHarness.StickyTargetRadius, targetHeight: 1.2f);
        for (int i = 0; i < Seconds(8f); i++)
        {
            h.Tick();
            if (h.Pm.GetStickyObjectId() != 0u) break;
        }
        Assert.Equal(RemoteChaseHarness.PlayerGuid, h.Pm.GetStickyObjectId());

        h.PlayerPos += new Vector3(0f, 8f, 0f);
        h.UmMoveToObject(sticky: true, useSpheres: true,
            targetRadius: RemoteChaseHarness.StickyTargetRadius, targetHeight: 1.2f);
        Assert.Equal(0u, h.Pm.GetStickyObjectId());
        Assert.Equal(MovementType.MoveToObject, h.Mgr.MovementTypeState);

        int restick = -1;
        for (int i = 0; i < Seconds(8f) && restick < 0; i++)
        {
            h.Tick();
            if (h.Pm.GetStickyObjectId() == RemoteChaseHarness.PlayerGuid)
                restick = i;
        }
        Assert.True(restick >= 0,
            $"chase did not re-stick after the re-arm; mt={h.Mgr.MovementTypeState} " +
            $"dist={h.DistToPlayer:F2}");
    }

    [Fact]
    public void ChaseArm_WithStanceChange_AppliesStanceBeforeTheChase()
    {
        var h = new RemoteChaseHarness(_out);
        h.PlayerPos = new Vector3(0f, 12f, 0f);
        h.PlayerVelocity = Vector3.Zero;

        for (int i = 0; i < Seconds(0.5f); i++) h.Tick();
        Assert.Equal(RemoteChaseHarness.NonCombat, h.Seq.Manager.State.Style);

        h.UmMoveToObject(wireStance: RemoteChaseHarness.Combat & 0xFFFFu);

        Assert.Equal(RemoteChaseHarness.Combat, h.Interp.InterpretedState.CurrentStyle);

        int installTick = -1;
        for (int i = 0; i < Seconds(6f) && installTick < 0; i++)
        {
            h.Tick();
            if (h.Seq.Manager.State.Substate == RemoteChaseHarness.Run
                || h.Seq.Manager.State.Substate == RemoteChaseHarness.Walk)
                installTick = i;
        }
        Assert.True(installTick >= 0,
            $"forward cycle never installed after the stance-carrying arm; " +
            $"mt={h.Mgr.MovementTypeState} substate=0x{h.Seq.Manager.State.Substate:X8}");
        Assert.Equal(RemoteChaseHarness.Combat, h.Seq.Manager.State.Style);
    }
}
