using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Physics;


public static class MotionCommand
{
    public const uint Ready             = 0x41000003u;
    public const uint WalkForward       = 0x45000005u;
    public const uint RunForward        = 0x44000007u;
    public const uint WalkBackward      = 0x45000006u;
    public const uint TurnRight         = 0x6500000Du;
    public const uint TurnLeft          = 0x6500000Eu;
    public const uint SideStepRight     = 0x6500000Fu;
    public const uint SideStepLeft      = 0x65000010u;
    public const uint Fallen            = 0x40000008u;
    public const uint Falling           = 0x40000015u;
    public const uint Jump              = 0x2500003Bu;
    public const uint Jumpup            = 0x1000004Bu;
    public const uint FallDown          = 0x10000050u;
    public const uint Dead              = 0x40000011u;
    public const uint Sanctuary         = 0x10000057u;
    public const uint Crouch            = 0x41000012u;
    public const uint Sitting           = 0x41000013u;
    public const uint Sleeping          = 0x41000014u;
    public const uint CrouchLowerBound  = 0x41000011u;
    public const uint CrouchUpperExclusive = 0x41000015u;
}

public enum MovementType
{
    Invalid                 = 0,
    RawCommand              = 1,
    InterpretedCommand      = 2,
    StopRawCommand          = 3,
    StopInterpretedCommand  = 4,
    StopCompletely          = 5,
    MoveToObject            = 6,
    MoveToPosition          = 7,
    TurnToObject            = 8,
    TurnToHeading            = 9,
}


// ── Motion state structs ───────────────────────────────────────────────────────

public struct InterpretedMotionState
{
    public uint  ForwardCommand;
    /// <summary>Speed scalar for interpreted forward motion (offset +0x50).</summary>
    public float ForwardSpeed;
    public uint  SideStepCommand;
    /// <summary>Speed scalar for interpreted sidestep (offset +0x58).</summary>
    public float SideStepSpeed;
    public uint  TurnCommand;
    /// <summary>Speed scalar for turn (offset +0x60).</summary>
    public float TurnSpeed;
    public uint CurrentStyle;

    private List<RawMotionAction>? _actions;

    public readonly IReadOnlyList<RawMotionAction> Actions
        => (IReadOnlyList<RawMotionAction>?)_actions ?? Array.Empty<RawMotionAction>();

    /// <summary>Initialize to the idle/ready state.</summary>
    public static InterpretedMotionState Default() => new()
    {
        ForwardCommand  = MotionCommand.Ready,
        ForwardSpeed    = 1.0f,
        SideStepCommand = 0,
        SideStepSpeed   = 1.0f,
        TurnCommand     = 0,
        TurnSpeed       = 1.0f,
        CurrentStyle    = 0x8000003Du,
    };

    public void AddAction(uint motion, float speed, uint actionStamp, bool autonomous)
    {
        _actions ??= new List<RawMotionAction>();
        _actions.Add(new RawMotionAction(
            Command: (ushort)motion,
            Stamp: (ushort)actionStamp,
            Autonomous: autonomous,
            Speed: speed));
    }

    public uint RemoveAction()
    {
        if (_actions is null || _actions.Count == 0)
            return 0;
        var head = _actions[0];
        _actions.RemoveAt(0);
        return head.Command;
    }

    public readonly uint GetNumActions() => (uint)(_actions?.Count ?? 0);

    public void ApplyMotion(uint motion, MovementParameters p)
    {
        if (motion == 0x6500000du) // TurnRight
        {
            TurnCommand = motion;
            TurnSpeed = p.Speed;
            return;
        }
        if (motion == 0x6500000fu) // SideStepRight
        {
            SideStepCommand = motion;
            SideStepSpeed = p.Speed;
            return;
        }
        if ((motion & 0x40000000u) != 0)
        {
            ForwardCommand = motion;
            ForwardSpeed = p.Speed;
            return;
        }
        if (motion >= 0x80000000u) // arg2 < 0 as signed int32
        {
            ForwardCommand = 0x41000003u;
            CurrentStyle = motion;
            return;
        }
        if ((motion & 0x10000000u) != 0)
            AddAction(motion, p.Speed, p.ActionStamp, p.Autonomous);
    }

    public void RemoveMotion(uint motion)
    {
        if (motion == 0x6500000du)
        {
            TurnCommand = 0;
            return;
        }
        if (motion == 0x6500000fu)
        {
            SideStepCommand = 0;
            return;
        }
        if ((motion & 0x40000000u) == 0)
        {
            if (motion >= 0x80000000u && motion == CurrentStyle)
                CurrentStyle = 0x8000003du;
        }
        else if (motion == ForwardCommand)
        {
            ForwardCommand = 0x41000003u;
            ForwardSpeed = 1f;
        }
    }
}

public struct MovementStruct
{
    public MovementType Type;
    public uint  Motion;
    /// <summary>Speed scalar for this motion.</summary>
    public float Speed;
    /// <summary>Autonomous (player-initiated) flag.</summary>
    public bool  Autonomous;
    /// <summary>Whether to modify the interpreted state.</summary>
    public bool  ModifyInterpretedState;
    /// <summary>Whether to modify the raw state.</summary>
    public bool  ModifyRawState;

    public uint ObjectId;
    public uint TopLevelId;
    public Position Pos;
    public float Radius;
    public float Height;
    public Motion.MovementParameters? Params;
}

// ── Optional WeenieObject interface ──────────────────────────────────────────

public interface IWeenieObject
{
    /// <summary>vtable +0x30 — InqJumpVelocity. Returns true and sets vz if valid.</summary>
    bool InqJumpVelocity(float extent, out float vz);
    /// <summary>vtable +0x34 — InqRunRate. Returns true and sets rate if valid.</summary>
    bool InqRunRate(out float rate);
    bool CanJump(float extent);

    bool IsCreature() => true;

    bool IsThePlayer() => false;

    bool JumpStaminaCost(float extent, out int cost)
    {
        cost = 0;
        return true;
    }
}

// ── MotionInterpreter ─────────────────────────────────────────────────────────

public sealed class MotionInterpreter : IMotionDoneSink
{
    public const float WalkAnimSpeed     = 3.11999989f;
    /// <summary>Run animation base speed (_DAT_007c96e0 family).</summary>
    public const float RunAnimSpeed      = 4.0f;
    /// <summary>Sidestep animation base speed (_DAT_007c96e8 family).</summary>
    public const float SidestepAnimSpeed = 1.25f;
    public const float JumpVzEpsilon = 0.000199999995f;
    /// <summary>Fallback vertical jump velocity when WeenieObj is absent (_DAT_0079c6d4).</summary>
    public const float DefaultJumpVz     = 10.0f;
    public const float MaxJumpExtent     = 1.0f;

    public const float BackwardsFactor = 0.649999976f;

    public const float RunTurnFactor = 1.5f;

    public const float MaxSidestepAnimRate = 3.0f;

    public const float SidestepFactor = 0.5f;



    public PhysicsBody? PhysicsObj { get; set; }

    /// <summary>Optional WeenieObject for stamina / run-rate queries (struct offset +0x04).</summary>
    public IWeenieObject? WeenieObj { get; set; }

    public RawMotionState RawState;

    /// <summary>Interpreted motion state derived from raw (struct offsets +0x44..+0x7C).</summary>
    public InterpretedMotionState InterpretedState;

    public float JumpExtent;

    public float MyRunRate = 1.0f;

    public HoldKey CurrentHoldKey => RawState.CurrentHoldKey;

    public bool StandingLongJump;

    private readonly LinkedList<MotionNode> _pendingMotions = new();

    public IEnumerable<MotionNode> PendingMotions => _pendingMotions;

    public Action? UnstickFromObject { get; set; }

    public Action? InterruptCurrentMovement { get; set; }

    public Action? RemoveLinkAnimations { get; set; }


    public Action? InitializeMotionTables { get; set; }

    public Action? CheckForCompletedMotions { get; set; }

    public bool Initted { get; set; } = true;

    public Func<Vector3>? GetCycleVelocity { get; set; }


    public MotionInterpreter()
    {
        RawState        = new RawMotionState();
        InterpretedState = InterpretedMotionState.Default();
    }

    public MotionInterpreter(PhysicsBody physicsObj, IWeenieObject? weenieObj = null)
    {
        PhysicsObj = physicsObj;
        WeenieObj  = weenieObj;
        RawState        = new RawMotionState();
        InterpretedState = InterpretedMotionState.Default();
    }


    public WeenieError PerformMovement(MovementStruct mvs)
    {
        var p = mvs.Params ?? new MovementParameters
        {
            Speed = mvs.Speed,
            Autonomous = mvs.Autonomous,
            ModifyInterpretedState = mvs.ModifyInterpretedState,
            ModifyRawState = mvs.ModifyRawState,
        };

        bool dispatched = true;
        WeenieError result = mvs.Type switch
        {
            MovementType.RawCommand             => DoMotion(mvs.Motion, p),
            MovementType.InterpretedCommand     => DoInterpretedMotion(mvs.Motion, p),
            MovementType.StopRawCommand         => StopMotion(mvs.Motion, p),
            MovementType.StopInterpretedCommand => StopInterpretedMotion(mvs.Motion, p),
            MovementType.StopCompletely         => StopCompletely(),
            _ => Invalid(out dispatched),
        };

        if (dispatched)
            CheckForCompletedMotions?.Invoke();

        return result;

        static WeenieError Invalid(out bool dispatched)
        {
            dispatched = false;
            return WeenieError.GeneralMovementFailure;
        }
    }


    public WeenieError DoMotion(uint motion, float speed = 1.0f)
        => DoMotion(motion, new MovementParameters { Speed = speed });

    public WeenieError DoMotion(uint motion, MovementParameters p)
    {
        if (PhysicsObj is null)
            return WeenieError.NoPhysicsObject;

        uint originalMotion = motion;
        float speed = p.Speed;
        var local = new MovementParameters(); // var_2c — fresh re-default

        if (p.CancelMoveTo) // bitfield high-byte sign bit
            InterruptCurrentMovement?.Invoke();

        if (p.SetHoldKey) // bitfield & 0x800
            SetHoldKey(p.HoldKeyToApply, p.CancelMoveTo);

        adjust_motion(ref motion, ref speed, p.HoldKeyToApply); // mutates motion/speed in place

        if (InterpretedState.CurrentStyle != 0x8000003du) // not MotionStance_NonCombat
        {
            if (originalMotion == MotionCommand.Crouch)
                return WeenieError.CrouchInCombatStance;   // 0x3f
            if (originalMotion == MotionCommand.Sitting)
                return WeenieError.SitInCombatStance;      // 0x40
            if (originalMotion == MotionCommand.Sleeping)
                return WeenieError.SleepInCombatStance;    // 0x41
            if ((originalMotion & 0x2000000u) != 0)
                return WeenieError.ChatEmoteOutsideNonCombat; // 0x42
        }

        if ((originalMotion & 0x10000000u) != 0 && InterpretedState.GetNumActions() >= 6)
            return WeenieError.ActionDepthExceeded; // 0x45

        local.Speed = speed;
        local.HoldKeyToApply = p.HoldKeyToApply;

        WeenieError result = DoInterpretedMotion(motion, local); // ADJUSTED id, fresh local params

        if (result == WeenieError.None && p.ModifyRawState) // bitfield & 0x2000
            RawState.ApplyMotion(originalMotion, p);

        return result;
    }

    // ── StopMotion ────────────────────────────────────────────────────────────

    public WeenieError StopMotion(uint motion)
        => StopMotion(motion, new MovementParameters());

    public WeenieError StopMotion(uint motion, MovementParameters p)
    {
        if (PhysicsObj is null)
            return WeenieError.NoPhysicsObject;

        uint originalMotion = motion;
        float speed = p.Speed;
        var local = new MovementParameters(); // fresh re-default

        if (p.CancelMoveTo)
            InterruptCurrentMovement?.Invoke();

        adjust_motion(ref motion, ref speed, p.HoldKeyToApply);

        local.Speed = speed;
        local.HoldKeyToApply = p.HoldKeyToApply;

        WeenieError result = StopInterpretedMotion(motion, local); // ADJUSTED id

        if (result == WeenieError.None && p.ModifyRawState)
            RawState.RemoveMotion(originalMotion); // ORIGINAL id

        return result;
    }


    public WeenieError StopCompletely()
    {
        if (PhysicsObj is null)
            return WeenieError.NoPhysicsObject;

        InterruptCurrentMovement?.Invoke();

        // A9: snapshot BEFORE the overwrite below.
        WeenieError jumpSnapshot = MotionAllowsJump(InterpretedState.ForwardCommand);

        RawState.ForwardCommand  = MotionCommand.Ready;
        RawState.ForwardSpeed    = 1.0f;
        RawState.SidestepCommand = 0;
        RawState.TurnCommand     = 0;

        InterpretedState.ForwardCommand  = MotionCommand.Ready;
        InterpretedState.ForwardSpeed    = 1.0f;
        InterpretedState.SideStepCommand = 0;
        InterpretedState.TurnCommand     = 0;

        DefaultSink?.StopCompletely();

        PhysicsObj.set_velocity(Vector3.Zero);

        AddToQueue(contextId: 0, MotionCommand.Ready, (uint)jumpSnapshot);

        if (!PhysicsObj.InWorld)
            RemoveLinkAnimations?.Invoke();

        return WeenieError.None;
    }


    public Vector3 get_state_velocity()
    {
        var velocity = Vector3.Zero;

        if (InterpretedState.SideStepCommand == MotionCommand.SideStepRight)
            velocity.X = SidestepAnimSpeed * InterpretedState.SideStepSpeed;

        Vector3? cycleVel = GetCycleVelocity?.Invoke();
        bool haveCycleForward = cycleVel.HasValue
            && MathF.Abs(cycleVel.Value.Y) > float.Epsilon;

        if (InterpretedState.ForwardCommand == MotionCommand.WalkForward)
        {
            velocity.Y = haveCycleForward
                ? cycleVel!.Value.Y
                : WalkAnimSpeed * InterpretedState.ForwardSpeed;
        }
        else if (InterpretedState.ForwardCommand == MotionCommand.RunForward)
        {
            velocity.Y = haveCycleForward
                ? cycleVel!.Value.Y
                : RunAnimSpeed * InterpretedState.ForwardSpeed;
        }

        float rate = MyRunRate;
        if (WeenieObj is not null)
        {
            if (WeenieObj.InqRunRate(out float queried))
                rate = queried;
            // else: rate stays MyRunRate
        }

        float maxSpeed = RunAnimSpeed * rate;
        float len = velocity.Length();
        if (len > maxSpeed && len > 0f)
        {
            velocity = Vector3.Normalize(velocity) * maxSpeed;
        }

        return velocity;
    }


    public void adjust_motion(ref uint motion, ref float speed, HoldKey holdKey)
    {
        if (WeenieObj is not null && !WeenieObj.IsCreature())
            return;

        switch (motion)
        {
            case MotionCommand.RunForward:
                // Already normalized; no scale, no hold-key path.
                return;
            case MotionCommand.WalkBackward:
                motion = MotionCommand.WalkForward;
                speed *= -BackwardsFactor;
                break;
            case MotionCommand.TurnLeft:
                motion = MotionCommand.TurnRight;
                speed *= -1f;
                break;
            case MotionCommand.SideStepLeft:
                motion = MotionCommand.SideStepRight;
                speed *= -1f;
                break;
        }

        if (motion == MotionCommand.SideStepRight)
            speed *= SidestepFactor * (WalkAnimSpeed / SidestepAnimSpeed);

        if (holdKey == HoldKey.Invalid)
            holdKey = CurrentHoldKey;

        if (holdKey == HoldKey.Run)
            apply_run_to_command(ref motion, ref speed);
    }


    public void apply_run_to_command(ref uint motion, ref float speed)
    {
        float speedMod = 1.0f;
        if (WeenieObj is not null)
            speedMod = WeenieObj.InqRunRate(out float rate) ? rate : MyRunRate;

        switch (motion)
        {
            case MotionCommand.WalkForward:
                if (speed > 0f)
                    motion = MotionCommand.RunForward;
                speed *= speedMod;
                break;
            case MotionCommand.TurnRight:
                speed *= RunTurnFactor;
                break;
            case MotionCommand.SideStepRight:
                speed *= speedMod;
                if (MathF.Abs(speed) > MaxSidestepAnimRate)
                    speed = speed > 0f ? MaxSidestepAnimRate : -MaxSidestepAnimRate;
                break;
        }
    }


    public void apply_raw_movement(RawMotionState raw)
    {
        RawState.CurrentHoldKey = raw.CurrentHoldKey;

        InterpretedState.ForwardCommand  = raw.ForwardCommand;
        InterpretedState.ForwardSpeed    = raw.ForwardSpeed;
        InterpretedState.SideStepCommand = raw.SidestepCommand;
        InterpretedState.SideStepSpeed   = raw.SidestepSpeed;
        InterpretedState.TurnCommand     = raw.TurnCommand;
        InterpretedState.TurnSpeed       = raw.TurnSpeed;

        uint fCmd = InterpretedState.ForwardCommand; float fSpd = InterpretedState.ForwardSpeed;
        adjust_motion(ref fCmd, ref fSpd, raw.ForwardHoldKey);
        InterpretedState.ForwardCommand = fCmd; InterpretedState.ForwardSpeed = fSpd;

        uint sCmd = InterpretedState.SideStepCommand; float sSpd = InterpretedState.SideStepSpeed;
        adjust_motion(ref sCmd, ref sSpd, raw.SidestepHoldKey);
        InterpretedState.SideStepCommand = sCmd; InterpretedState.SideStepSpeed = sSpd;

        uint tCmd = InterpretedState.TurnCommand; float tSpd = InterpretedState.TurnSpeed;
        adjust_motion(ref tCmd, ref tSpd, raw.TurnHoldKey);
        InterpretedState.TurnCommand = tCmd; InterpretedState.TurnSpeed = tSpd;
    }


    public void apply_current_movement(bool cancelMoveTo, bool allowJump)
    {
        if (PhysicsObj is null || !Initted)
            return;

        bool isThePlayer = WeenieObj is null || WeenieObj.IsThePlayer();
        if (isThePlayer && PhysicsObj.LastMoveWasAutonomous)
        {
            apply_raw_movement(cancelMoveTo, allowJump);
            return;
        }

        ApplyCurrentMovementInterpreted(cancelMoveTo, allowJump);
    }

    public void apply_raw_movement(bool cancelMoveTo, bool allowJump)
    {
        if (PhysicsObj is null)
            return;

        apply_raw_movement(RawState);
        ApplyCurrentMovementInterpreted(cancelMoveTo, allowJump);
    }

    public IInterpretedMotionSink? DefaultSink { get; set; }

    private void ApplyCurrentMovementInterpreted(bool cancelMoveTo, bool allowJump)
    {
        if (PhysicsObj is null)
            return;

        if (DefaultSink is not null)
        {
            ApplyInterpretedMovement(InterpretedState.CurrentStyle, DefaultSink,
                cancelMoveTo, allowJump);
            return;
        }

        _ = cancelMoveTo;
        _ = allowJump;

        if (InterpretedState.ForwardCommand == MotionCommand.RunForward)
            MyRunRate = InterpretedState.ForwardSpeed;

        if (PhysicsObj.OnWalkable)
        {
            var localVelocity = get_state_velocity();
            PhysicsObj.set_local_velocity(localVelocity, PhysicsObj.LastMoveWasAutonomous);
        }
    }


    public void ReportExhaustion()
    {
        if (PhysicsObj is null || !Initted)
            return;

        bool isThePlayer = WeenieObj is null || WeenieObj.IsThePlayer();
        if (isThePlayer && PhysicsObj.LastMoveWasAutonomous)
        {
            apply_raw_movement(cancelMoveTo: false, allowJump: true);
            return;
        }

        ApplyCurrentMovementInterpreted(cancelMoveTo: false, allowJump: true);
    }


    public void SetWeenieObject(IWeenieObject? weenie)
    {
        WeenieObj = weenie;

        if (PhysicsObj is null || !Initted)
            return;

        bool isThePlayer = weenie is null || weenie.IsThePlayer();
        if (isThePlayer && PhysicsObj.LastMoveWasAutonomous)
        {
            apply_raw_movement(cancelMoveTo: true, allowJump: true);
            return;
        }

        ApplyCurrentMovementInterpreted(cancelMoveTo: true, allowJump: true);
    }

    public void SetPhysicsObject(PhysicsBody? physicsObj)
    {
        PhysicsObj = physicsObj;

        if (physicsObj is null || !Initted)
            return;

        bool isThePlayer = WeenieObj is null || WeenieObj.IsThePlayer();
        if (isThePlayer && physicsObj.LastMoveWasAutonomous)
        {
            apply_raw_movement(cancelMoveTo: true, allowJump: true);
            return;
        }

        ApplyCurrentMovementInterpreted(cancelMoveTo: true, allowJump: true);
    }


    public WeenieError JumpChargeIsAllowed(float extent)
    {
        if (WeenieObj is not null && !WeenieObj.CanJump(extent))
            return WeenieError.CantJumpLoadedDown; // 0x49

        uint forward = InterpretedState.ForwardCommand;
        if (forward != MotionCommand.Fallen
            && (forward <= MotionCommand.CrouchLowerBound || forward > MotionCommand.Sleeping))
            return WeenieError.None;

        return WeenieError.YouCantJumpFromThisPosition; // 0x48
    }


    public WeenieError ChargeJump()
    {
        if (WeenieObj is not null && !WeenieObj.CanJump(JumpExtent))
            return WeenieError.CantJumpLoadedDown; // 0x49

        uint forward = InterpretedState.ForwardCommand;
        if (forward == MotionCommand.Fallen
            || (forward > MotionCommand.CrouchLowerBound && forward <= MotionCommand.Sleeping))
            return WeenieError.YouCantJumpFromThisPosition; // 0x48

        if (PhysicsObj is not null)
        {
            bool onGround = PhysicsObj.TransientState.HasFlag(TransientStateFlags.Contact)
                         && PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable);
            if (onGround
                && forward == MotionCommand.Ready
                && InterpretedState.SideStepCommand == 0
                && InterpretedState.TurnCommand == 0)
            {
                StandingLongJump = true;
            }
        }

        return WeenieError.None;
    }


    public WeenieError jump(float extent, int adjustStamina = 0)
    {
        if (PhysicsObj is null)
            return WeenieError.NoPhysicsObject;

        InterruptCurrentMovement?.Invoke();

        var result = jump_is_allowed(extent, out _);
        if (result == WeenieError.None)
        {
            JumpExtent = extent;
            PhysicsObj.set_on_walkable(false);
            return WeenieError.None;
        }

        StandingLongJump = false;
        return result;
    }


    public float GetJumpVZ()
    {
        float extent = JumpExtent;

        if (extent < JumpVzEpsilon)
            return 0.0f;

        if (extent > MaxJumpExtent)
            extent = MaxJumpExtent;

        if (WeenieObj is null)
            return DefaultJumpVz;

        if (WeenieObj.InqJumpVelocity(extent, out float vz))
            return vz;

        return 0.0f;
    }


    public Vector3 GetLeaveGroundVelocity()
    {
        var velocity = get_state_velocity();
        velocity.Z = GetJumpVZ();

        float eps = JumpVzEpsilon;
        if (MathF.Abs(velocity.X) < eps && MathF.Abs(velocity.Y) < eps && MathF.Abs(velocity.Z) < eps
            && PhysicsObj is not null)
        {
            var invOrientation = Quaternion.Inverse(PhysicsObj.Orientation);
            velocity = Vector3.Transform(PhysicsObj.Velocity, invOrientation);
        }

        return velocity;
    }


    public WeenieError jump_is_allowed(float extent, out int staminaCost)
    {
        staminaCost = 0;

        if (PhysicsObj is not null)
        {
            bool nonCreatureWeenie = WeenieObj is not null && !WeenieObj.IsCreature();
            bool gravityStateOff   = !PhysicsObj.State.HasFlag(PhysicsStateFlags.Gravity);
            bool grounded          = PhysicsObj.TransientState.HasFlag(TransientStateFlags.Contact)
                                   && PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable);

            if (nonCreatureWeenie || gravityStateOff || grounded)
                return JumpIsAllowedSharedGate(extent, ref staminaCost);
        }

        return WeenieError.NotGrounded;
    }

    private WeenieError JumpIsAllowedSharedGate(float extent, ref int staminaCost)
    {
        if (PhysicsObj is not null && PhysicsObj.IsFullyConstrained)
            return WeenieError.GeneralMovementFailure; // 0x47

        var head = _pendingMotions.First;
        uint peeked = head is not null ? head.Value.JumpErrorCode : 0;

        if (head is null || peeked == 0)
        {
            WeenieError chargeResult = JumpChargeIsAllowed(extent);
            if (chargeResult == WeenieError.None)
            {
                WeenieError motionResult = MotionAllowsJump(InterpretedState.ForwardCommand);
                if (motionResult != WeenieError.None)
                    return motionResult;

                if (WeenieObj is null)
                    return motionResult; // == None

                if (!WeenieObj.JumpStaminaCost(extent, out staminaCost))
                    return WeenieError.GeneralMovementFailure; // 0x47 — can't afford

                return motionResult; // == None (success)
            }

            return chargeResult;
        }

        return (WeenieError)peeked;
    }


    public bool contact_allows_move(uint motion)
    {
        if (PhysicsObj is null)
            return false;

        if (motion > 0x40000015u)
        {
            if (motion is MotionCommand.TurnRight or MotionCommand.TurnLeft)
                return true;
        }
        else if (motion == MotionCommand.Falling || motion == 0x40000011u)
        {
            return true;
        }

        if (WeenieObj is not null && !WeenieObj.IsCreature())
            return true;

        if (!PhysicsObj.State.HasFlag(PhysicsStateFlags.Gravity))
            return true;

        bool grounded = PhysicsObj.TransientState.HasFlag(TransientStateFlags.Contact)
                     && PhysicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable);

        return grounded;
    }


    public void AddToQueue(uint contextId, uint motion, uint jumpErrorCode)
    {
        _pendingMotions.AddLast(new MotionNode(contextId, motion, jumpErrorCode));
    }

    public bool MotionsPending() => _pendingMotions.First is not null;

    public void MotionDone(uint motion, bool success)
    {
        if (PhysicsObj is null)
            return;

        var head = _pendingMotions.First;
        if (head is null)
            return;

        if ((head.Value.Motion & 0x10000000u) != 0)
        {
            UnstickFromObject?.Invoke();
            InterpretedState.RemoveAction();
            RawState.RemoveAction();
        }

        var head1 = _pendingMotions.First;
        if (head1 is not null)
        {
            _pendingMotions.RemoveFirst();
        }
    }

    public void HandleExitWorld()
    {
        while (_pendingMotions.First is not null)
        {
            var head = _pendingMotions.First!;
            if ((head.Value.Motion & 0x10000000u) != 0)
            {
                UnstickFromObject?.Invoke();
                InterpretedState.RemoveAction();
                RawState.RemoveAction();
            }

            _pendingMotions.RemoveFirst();
        }
    }

    public bool IsStandingStill()
    {
        if (PhysicsObj is null)
            return false;

        const TransientStateFlags groundedMask =
            TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
        bool grounded = (PhysicsObj.TransientState & groundedMask) == groundedMask;
        if (!grounded)
            return false;

        return InterpretedState.ForwardCommand == MotionCommand.Ready
            && InterpretedState.SideStepCommand == 0
            && InterpretedState.TurnCommand == 0;
    }

    public static WeenieError MotionAllowsJump(uint motion)
    {
        if (motion > 0x40000018u)
        {
            if (motion > 0x41000014u)
                return WeenieError.None;
            if (motion < 0x41000012u && (motion < 0x4000001eu || motion > 0x40000039u))
                return WeenieError.None;
        }
        else if (motion < 0x40000016u)
        {
            if (motion > 0x10000131u)
            {
                if (motion != 0x40000008u)
                    return WeenieError.None;
            }
            else if (motion < 0x10000128u && (motion < 0x1000006fu || motion > 0x10000078u))
            {
                return WeenieError.None;
            }
        }

        return WeenieError.YouCantJumpFromThisPosition;
    }


    public void LeaveGround()
    {
        if (PhysicsObj is null)
            return;

        bool isCreature = WeenieObj is null || WeenieObj.IsCreature();
        if (!isCreature)
            return;

        if (!PhysicsObj.State.HasFlag(PhysicsStateFlags.Gravity))
            return;

        var velocity = GetLeaveGroundVelocity();
        PhysicsObj.set_local_velocity(velocity, autonomous: true);

        StandingLongJump = false;
        JumpExtent       = 0f;

        RemoveLinkAnimations?.Invoke();
        apply_current_movement(cancelMoveTo: false, allowJump: true);
    }


    public void HitGround()
    {
        if (PhysicsObj is null)
            return;

        bool isCreature = WeenieObj is null || WeenieObj.IsCreature();
        if (!isCreature)
            return;

        if (!PhysicsObj.State.HasFlag(PhysicsStateFlags.Gravity))
            return;

        RemoveLinkAnimations?.Invoke();
        apply_current_movement(cancelMoveTo: false, allowJump: true);
    }


    public void EnterDefaultState()
    {
        RawState = new RawMotionState();
        InterpretedState = InterpretedMotionState.Default();

        InitializeMotionTables?.Invoke();

        AddToQueue(contextId: 0, MotionCommand.Ready, jumpErrorCode: 0);

        Initted = true;

        LeaveGround();
    }


    public void set_hold_run(bool holdingRun, bool interrupt)
    {
        bool runKeyUp = !holdingRun;
        bool notCurrentlyRun = RawState.CurrentHoldKey != HoldKey.Run;

        if (runKeyUp != notCurrentlyRun)
        {
            RawState.CurrentHoldKey = holdingRun ? HoldKey.Run : HoldKey.None;
            apply_current_movement(cancelMoveTo: interrupt, allowJump: true);
        }
    }

    public void SetHoldKey(HoldKey key, bool cancelMoveTo)
    {
        HoldKey current = RawState.CurrentHoldKey;
        if (key == current)
            return;

        if (key == HoldKey.None)
        {
            if (current == HoldKey.Run)
            {
                RawState.CurrentHoldKey = HoldKey.None;
                apply_current_movement(cancelMoveTo, allowJump: true);
            }
        }
        else if (key == HoldKey.Run && current != HoldKey.Run)
        {
            RawState.CurrentHoldKey = HoldKey.Run;
            apply_current_movement(cancelMoveTo, allowJump: true);
        }
    }


    public float GetMaxSpeed()
    {
        float rate = 1.0f;
        if (WeenieObj is not null && !WeenieObj.InqRunRate(out rate))
            rate = MyRunRate;
        return RunAnimSpeed * rate;
    }

    public float GetAdjustedMaxSpeed()
    {
        if (InterpretedState.ForwardCommand == MotionCommand.RunForward)
        {
            return InterpretedState.ForwardSpeed * RunAnimSpeed;
        }

        float rate = 1.0f;
        if (WeenieObj is not null && !WeenieObj.InqRunRate(out rate))
            rate = MyRunRate;
        return rate;
    }



    public bool IsLocalPlayer;

    public int ServerActionStamp;

    public int MoveToInterpretedState(in InboundInterpretedState ims, IInterpretedMotionSink? sink = null)
    {
        if (PhysicsObj is null) return 0;

        RawState.CurrentStyle = ims.CurrentStyle;

        bool allowJump = MotionAllowsJump(InterpretedState.ForwardCommand) == WeenieError.None;

        InterpretedState.CurrentStyle    = ims.CurrentStyle;
        InterpretedState.ForwardCommand  = ims.ForwardCommand;
        InterpretedState.ForwardSpeed    = ims.ForwardSpeed;
        InterpretedState.SideStepCommand = ims.SideStepCommand;
        InterpretedState.SideStepSpeed   = ims.SideStepSpeed;
        InterpretedState.TurnCommand     = ims.TurnCommand;
        InterpretedState.TurnSpeed       = ims.TurnSpeed;

        ApplyInterpretedMovement(ims.CurrentStyle, sink, cancelMoveTo: true, allowJump: allowJump);

        if (ims.Actions is { } actions)
        {
            foreach (var a in actions)
            {
                int incoming = a.Stamp & 0x7FFF;
                int stored   = ServerActionStamp & 0x7FFF;
                int diff     = incoming >= stored ? incoming - stored : stored - incoming;
                bool newer   = diff <= 0x3FFF ? stored < incoming : incoming < stored;

                if (!newer) continue;

                // Local player skips its own autonomous echoes (305977).
                if (IsLocalPlayer && a.Autonomous) continue;

                ServerActionStamp = incoming;
                DispatchInterpretedMotion(a.Command, a.Speed, a.Autonomous, sink);
            }
        }

        return 1;
    }

    public void ApplyInterpretedMovement(
        uint currentStyle, IInterpretedMotionSink? sink,
        bool cancelMoveTo = false, bool allowJump = false)
    {
        if (PhysicsObj is null) return;

        var p = new MovementParameters
        {
            SetHoldKey = false,
            ModifyInterpretedState = false,
            CancelMoveTo = cancelMoveTo,
            DisableJumpDuringLink = !allowJump,
        };

        if (InterpretedState.ForwardCommand == MotionCommand.RunForward)
            MyRunRate = InterpretedState.ForwardSpeed;

        p.Speed = 1.0f;
        DoInterpretedMotion(currentStyle, p, sink);

        if (!contact_allows_move(InterpretedState.ForwardCommand))
        {
            p.Speed = 1.0f;
            DoInterpretedMotion(MotionCommand.Falling, p, sink);
        }
        else if (StandingLongJump)
        {
            p.Speed = 1.0f;
            DoInterpretedMotion(MotionCommand.Ready, p, sink);
            StopInterpretedMotion(MotionCommand.SideStepRight, p, sink);
        }
        else
        {
            p.Speed = InterpretedState.ForwardSpeed;
            DoInterpretedMotion(InterpretedState.ForwardCommand, p, sink);
            if (InterpretedState.SideStepCommand == 0)
            {
                StopInterpretedMotion(MotionCommand.SideStepRight, p, sink);
            }
            else
            {
                p.Speed = InterpretedState.SideStepSpeed;
                DoInterpretedMotion(InterpretedState.SideStepCommand, p, sink);
            }
        }

        if (InterpretedState.TurnCommand != 0)
        {
            p.Speed = InterpretedState.TurnSpeed;
            DoInterpretedMotion(InterpretedState.TurnCommand, p, sink);
            return;
        }

        StopInterpretedMotion(MotionCommand.TurnRight, p, sink);
    }

    public WeenieError DoInterpretedMotion(uint motion, MovementParameters p)
        => DoInterpretedMotion(motion, p, DefaultSink);

    private WeenieError DoInterpretedMotion(uint motion, MovementParameters p, IInterpretedMotionSink? sink)
    {
        if (PhysicsObj is null)
            return WeenieError.NoPhysicsObject;

        WeenieError result;

        if (contact_allows_move(motion))
        {
            bool standingLongJumpStateOnly = StandingLongJump
                && (motion == MotionCommand.WalkForward
                    || motion == MotionCommand.RunForward
                    || motion == MotionCommand.SideStepRight);

            if (standingLongJumpStateOnly)
            {
                // label_528440 — state-only: no dispatch, no queue.
                if (p.ModifyInterpretedState)
                    InterpretedState.ApplyMotion(motion, p);
                return WeenieError.None;
            }

            if (motion == MotionCommand.Dead)
                RemoveLinkAnimations?.Invoke();

            bool dispatchOk = sink?.ApplyMotion(motion, p.Speed) ?? true;

            if (!dispatchOk)
            {
                result = WeenieError.GeneralMovementFailure;
            }
            else
            {
                WeenieError jumpErr;
                if (!p.DisableJumpDuringLink)
                {
                    jumpErr = MotionAllowsJump(motion);
                    if (jumpErr == WeenieError.None && (motion & 0x10000000u) == 0)
                        jumpErr = MotionAllowsJump(InterpretedState.ForwardCommand);
                }
                else
                {
                    jumpErr = WeenieError.YouCantJumpFromThisPosition; // 0x48 — forced BLOCKED
                }

                AddToQueue(p.ContextId, motion, (uint)jumpErr);

                if (p.ModifyInterpretedState)
                    InterpretedState.ApplyMotion(motion, p);

                result = WeenieError.None;
            }
        }
        else if ((motion & 0x10000000u) == 0)
        {
            if (p.ModifyInterpretedState)
                InterpretedState.ApplyMotion(motion, p);
            result = WeenieError.None;
        }
        else
        {
            result = WeenieError.NotGrounded;
        }

        if (!PhysicsObj.InWorld)
            RemoveLinkAnimations?.Invoke();

        return result;
    }

    public WeenieError StopInterpretedMotion(uint motion, MovementParameters p)
        => StopInterpretedMotion(motion, p, DefaultSink);

    private WeenieError StopInterpretedMotion(uint motion, MovementParameters p, IInterpretedMotionSink? sink)
    {
        if (PhysicsObj is null)
            return WeenieError.NoPhysicsObject;

        WeenieError result;

        bool standingLongJumpStateOnly = StandingLongJump
            && (motion == MotionCommand.WalkForward
                || motion == MotionCommand.RunForward
                || motion == MotionCommand.SideStepRight);

        if (!contact_allows_move(motion) || standingLongJumpStateOnly)
        {
            if (p.ModifyInterpretedState)
                InterpretedState.RemoveMotion(motion);
            result = WeenieError.None;
        }
        else
        {
            bool dispatchOk = sink?.StopMotion(motion) ?? true;

            if (!dispatchOk)
            {
                result = WeenieError.GeneralMovementFailure;
            }
            else
            {
                result = WeenieError.None;

                AddToQueue(p.ContextId, MotionCommand.Ready, (uint)result);

                if (p.ModifyInterpretedState)
                    InterpretedState.RemoveMotion(motion);
            }
        }

        if (!PhysicsObj.InWorld)
            RemoveLinkAnimations?.Invoke();

        return result;
    }

    private WeenieError DispatchInterpretedMotion(
        uint motion, float speed, bool autonomous, IInterpretedMotionSink? sink)
        => DoInterpretedMotion(
            motion, new MovementParameters { Speed = speed, Autonomous = autonomous }, sink);
}
