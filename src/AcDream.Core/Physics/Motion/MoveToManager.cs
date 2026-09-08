using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Physics.Motion;


public sealed class MoveToManager
{
    private readonly MotionInterpreter _interp;


    private readonly Action _stopCompletely;

    private readonly Func<Position> _getPosition;

    private readonly Func<float> _getHeading;

    private readonly Action<float, bool> _setHeading;

    private readonly Func<float> _getOwnRadius;

    private readonly Func<float> _getOwnHeight;

    private readonly Func<bool> _contact;

    private readonly Func<bool> _isInterpolating;

    private readonly Func<Vector3> _getVelocity;

    private readonly Func<uint> _getSelfId;

    private readonly Action<uint, uint, float, double> _setTarget;

    private readonly Action _clearTarget;

    private readonly Func<double> _getTargetQuantum;

    private readonly Action<double> _setTargetQuantum;

    public Action? Unstick { get; set; }

    public Action<uint, float, float>? StickTo { get; set; }

    public Action<WeenieError>? MoveToComplete { get; set; }

    public Action<WeenieError>? MoveToCancelled { get; set; }

    private readonly Func<double> _curTime;

    public bool HasPhysicsObj { get; set; } = true;

    public MoveToManager(
        MotionInterpreter interp,
        Action stopCompletely,
        Func<Position> getPosition,
        Func<float> getHeading,
        Action<float, bool> setHeading,
        Func<float> getOwnRadius,
        Func<float> getOwnHeight,
        Func<bool> contact,
        Func<bool> isInterpolating,
        Func<Vector3> getVelocity,
        Func<uint> getSelfId,
        Action<uint, uint, float, double> setTarget,
        Action clearTarget,
        Func<double> getTargetQuantum,
        Action<double> setTargetQuantum,
        Func<double>? curTime = null)
    {
        _interp = interp ?? throw new ArgumentNullException(nameof(interp));
        _stopCompletely = stopCompletely ?? throw new ArgumentNullException(nameof(stopCompletely));
        _getPosition = getPosition ?? throw new ArgumentNullException(nameof(getPosition));
        _getHeading = getHeading ?? throw new ArgumentNullException(nameof(getHeading));
        _setHeading = setHeading ?? throw new ArgumentNullException(nameof(setHeading));
        _getOwnRadius = getOwnRadius ?? throw new ArgumentNullException(nameof(getOwnRadius));
        _getOwnHeight = getOwnHeight ?? throw new ArgumentNullException(nameof(getOwnHeight));
        _contact = contact ?? throw new ArgumentNullException(nameof(contact));
        _isInterpolating = isInterpolating ?? throw new ArgumentNullException(nameof(isInterpolating));
        _getVelocity = getVelocity ?? throw new ArgumentNullException(nameof(getVelocity));
        _getSelfId = getSelfId ?? throw new ArgumentNullException(nameof(getSelfId));
        _setTarget = setTarget ?? throw new ArgumentNullException(nameof(setTarget));
        _clearTarget = clearTarget ?? throw new ArgumentNullException(nameof(clearTarget));
        _getTargetQuantum = getTargetQuantum ?? throw new ArgumentNullException(nameof(getTargetQuantum));
        _setTargetQuantum = setTargetQuantum ?? throw new ArgumentNullException(nameof(setTargetQuantum));

        double t = 0.0;
        _curTime = curTime ?? (() => t += 1.0 / 30.0);

        InitializeLocalVariables();
    }


    /// <summary>+0x00 <c>movement_type</c>.</summary>
    public MovementType MovementTypeState { get; private set; } = MovementType.Invalid;

    private static readonly Position IdentityPosition = new(0u, Vector3.Zero, Quaternion.Identity);

    /// <summary>+0x04 <c>sought_position</c>.</summary>
    public Position SoughtPosition { get; private set; } = IdentityPosition;

    public Position CurrentTargetPosition { get; private set; } = IdentityPosition;

    /// <summary>+0x94 <c>starting_position</c>.</summary>
    public Position StartingPosition { get; private set; } = IdentityPosition;

    public MovementParameters Params { get; private set; } = new();

    /// <summary>+0x108 <c>previous_heading</c>.</summary>
    public float PreviousHeading { get; private set; }

    /// <summary>+0x10C <c>previous_distance</c>.</summary>
    public float PreviousDistance { get; private set; }

    /// <summary>+0x110 <c>previous_distance_time</c>.</summary>
    public double PreviousDistanceTime { get; private set; }

    /// <summary>+0x118 <c>original_distance</c>.</summary>
    public float OriginalDistance { get; private set; }

    /// <summary>+0x120 <c>original_distance_time</c>.</summary>
    public double OriginalDistanceTime { get; private set; }

    public uint FailProgressCount { get; private set; }

    /// <summary>+0x12C <c>sought_object_id</c>.</summary>
    public uint SoughtObjectId { get; private set; }

    /// <summary>+0x130 <c>top_level_object_id</c>.</summary>
    public uint TopLevelObjectId { get; private set; }

    /// <summary>+0x134 <c>sought_object_radius</c>.</summary>
    public float SoughtObjectRadius { get; private set; }

    /// <summary>+0x138 <c>sought_object_height</c>.</summary>
    public float SoughtObjectHeight { get; private set; }

    public uint CurrentCommand { get; private set; }

    /// <summary>+0x140 <c>aux_command</c>.</summary>
    public uint AuxCommand { get; private set; }

    /// <summary>+0x144 <c>moving_away</c>.</summary>
    public bool MovingAway { get; private set; }

    /// <summary>+0x148 <c>initialized</c>.</summary>
    public bool Initialized { get; private set; }

    private readonly LinkedList<MoveToNode> _pendingActions = new();

    /// <summary>Read-only inspection surface (tests): the node queue in
    /// head-to-tail order.</summary>
    public IEnumerable<MoveToNode> PendingActions => _pendingActions;


    public void InitializeLocalVariables()
    {
        MovementTypeState = MovementType.Invalid;

        Params.CanWalk = false;
        Params.CanRun = false;
        Params.CanSidestep = false;
        Params.CanWalkBackwards = false;
        Params.CanCharge = false;
        Params.FailWalk = false;
        Params.UseFinalHeading = false;
        Params.Sticky = false;
        Params.MoveAway = false;
        Params.MoveTowards = false;
        Params.UseSpheres = false;
        Params.SetHoldKey = false;
        Params.Autonomous = false;
        Params.ModifyRawState = false;
        Params.ModifyInterpretedState = false;
        Params.CancelMoveTo = false;
        Params.StopCompletelyFlag = false;
        Params.DisableJumpDuringLink = false;
        Params.ContextId = 0;

        PreviousDistance = float.MaxValue;
        PreviousDistanceTime = _curTime();
        OriginalDistance = float.MaxValue;
        OriginalDistanceTime = _curTime();

        PreviousHeading = 0f;
        FailProgressCount = 0;
        CurrentCommand = 0;
        AuxCommand = 0;
        MovingAway = false;
        Initialized = false;

        SoughtPosition = IdentityPosition;
        CurrentTargetPosition = IdentityPosition;

        SoughtObjectId = 0;
        TopLevelObjectId = 0;
        SoughtObjectRadius = 0f;
        SoughtObjectHeight = 0f;
    }

    public void Destroy()
    {
        _pendingActions.Clear();
        InitializeLocalVariables();
    }

    public bool IsMovingTo() => MovementTypeState != MovementType.Invalid;

    // ── 3. Entry points (movement_type setters) ─────────────────────────────

    public WeenieError PerformMovement(MovementStruct mvs)
    {
        CancelMoveTo(WeenieError.ActionCancelled);
        Unstick?.Invoke();

        switch (mvs.Type)
        {
            case MovementType.MoveToObject:
                MoveToObject(mvs.ObjectId, mvs.TopLevelId, mvs.Radius, mvs.Height, mvs.Params ?? new MovementParameters());
                break;
            case MovementType.MoveToPosition:
                MoveToPosition(mvs.Pos, mvs.Params ?? new MovementParameters());
                break;
            case MovementType.TurnToObject:
                TurnToObject(mvs.ObjectId, mvs.TopLevelId, mvs.Params ?? new MovementParameters());
                break;
            case MovementType.TurnToHeading:
                TurnToHeading(mvs.Params ?? new MovementParameters());
                break;
        }

        return WeenieError.None;
    }

    public void MoveToObject(uint objectId, uint topLevelId, float radius, float height, MovementParameters p)
    {
        if (!HasPhysicsObj) return;

        _stopCompletely();
        StartingPosition = _getPosition();
        SoughtObjectId = objectId;
        SoughtObjectRadius = radius;
        SoughtObjectHeight = height;
        MovementTypeState = MovementType.MoveToObject;
        TopLevelObjectId = topLevelId;
        CopyParams(p);
        Initialized = false;

        if (topLevelId == _getSelfId())
        {
            CleanUp();
            _stopCompletely();
        }
        else
        {
            _setTarget(0, TopLevelObjectId, 0.5f, 0.0);
        }
    }

    public void MoveToPosition(Position target, MovementParameters p)
    {
        if (!HasPhysicsObj) return;

        _stopCompletely();
        CurrentTargetPosition = target;
        SoughtObjectRadius = 0f;

        float dist = GetCurrentDistance();

        Vector3 myPos = _getPosition().Frame.Origin;
        Vector3 targetPos = target.Frame.Origin;
        float toTargetHeading = MoveToMath.PositionHeading(myPos, targetPos);
        float headingDiff = toTargetHeading - _getHeading();
        if (MathF.Abs(headingDiff) < MoveToMath.Epsilon) headingDiff = 0f;
        if (headingDiff < -MoveToMath.Epsilon) headingDiff += 360f;

        p.GetCommand(dist, headingDiff, out uint cmd, out _, out _);
        if (cmd != 0)
        {
            AddTurnToHeadingNode(toTargetHeading);
            AddMoveToPositionNode();
        }

        if (p.UseFinalHeading)
        {
            AddTurnToHeadingNode(p.DesiredHeading);
        }

        SoughtPosition = target;
        StartingPosition = _getPosition();
        MovementTypeState = MovementType.MoveToPosition;
        CopyParams(p);
        Params.Sticky = false;

        BeginNextNode();
    }

    public void TurnToObject(uint objectId, uint topLevelId, MovementParameters p)
    {
        if (!HasPhysicsObj) return;

        if (p.StopCompletelyFlag)
        {
            _stopCompletely();
        }

        MovementTypeState = MovementType.TurnToObject;
        SoughtObjectId = objectId;

        CurrentTargetPosition = CurrentTargetPosition with
        {
            Frame = new CellFrame(
                CurrentTargetPosition.Frame.Origin,
                MoveToMath.SetHeading(CurrentTargetPosition.Frame.Orientation, p.DesiredHeading)),
        };

        TopLevelObjectId = topLevelId;
        CopyParams(p);

        if (topLevelId == _getSelfId())
        {
            CleanUp();
            _stopCompletely();
        }
        else
        {
            Initialized = false;
            _setTarget(0, topLevelId, 0.5f, 0.0);
        }
    }

    public void TurnToHeading(MovementParameters p)
    {
        if (!HasPhysicsObj) return;

        if (p.StopCompletelyFlag)
        {
            _stopCompletely();
        }

        CopyParams(p);
        Params.Sticky = false;

        SoughtPosition = SoughtPosition with
        {
            Frame = new CellFrame(
                SoughtPosition.Frame.Origin,
                MoveToMath.SetHeading(SoughtPosition.Frame.Orientation, p.DesiredHeading)),
        };

        MovementTypeState = MovementType.TurnToHeading;

        _pendingActions.AddLast(new MoveToNode(MovementType.TurnToHeading, p.DesiredHeading));

        BeginNextNode();
    }

    // ── 4. Node stepping ─────────────────────────────────────────────────────

    public void AddTurnToHeadingNode(float heading)
        => _pendingActions.AddLast(new MoveToNode(MovementType.TurnToHeading, heading));

    public void AddMoveToPositionNode()
        => _pendingActions.AddLast(new MoveToNode(MovementType.MoveToPosition, 0f));

    public void RemovePendingActionsHead()
    {
        if (_pendingActions.First is not null)
            _pendingActions.RemoveFirst();
    }

    public void BeginNextNode()
    {
        if (_pendingActions.First is not null)
        {
            MovementType type = _pendingActions.First.Value.Type;
            if (type == MovementType.MoveToPosition)
            {
                BeginMoveForward();
                return;
            }
            if (type == MovementType.TurnToHeading)
            {
                BeginTurnToHeading();
                return;
            }
            return; // unknown node type: stall (defensive)
        }

        if (Params.Sticky)
        {
            float height = SoughtObjectHeight;
            float radius = SoughtObjectRadius;
            uint tlid = TopLevelObjectId;

            CleanUp();
            if (HasPhysicsObj) _stopCompletely();

            StickTo?.Invoke(tlid, radius, height);
            MoveToComplete?.Invoke(WeenieError.None);
            return;
        }

        CleanUp();
        if (HasPhysicsObj) _stopCompletely();
        MoveToComplete?.Invoke(WeenieError.None);
    }

    public void BeginMoveForward()
    {
        if (!HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        float dist = GetCurrentDistance();

        Vector3 myPos = _getPosition().Frame.Origin;
        Vector3 targetPos = CurrentTargetPosition.Frame.Origin;
        float heading = MoveToMath.PositionHeading(myPos, targetPos) - _getHeading();
        if (MathF.Abs(heading) < MoveToMath.Epsilon) heading = 0f;
        if (heading < -MoveToMath.Epsilon) heading += 360f;

        Params.GetCommand(dist, heading, out uint cmd, out HoldKey holdKey, out bool movingAway);

        if (cmd == 0)
        {
            RemovePendingActionsHead();
            BeginNextNode();
            return;
        }

        var localParams = new MovementParameters
        {
            HoldKeyToApply = holdKey,
            CancelMoveTo = false,
            Speed = Params.Speed,
        };

        WeenieError err = _DoMotion(cmd, localParams);
        if (err != WeenieError.None)
        {
            CancelMoveTo(err);
            return;
        }

        CurrentCommand = cmd;
        MovingAway = movingAway;
        Params.HoldKeyToApply = holdKey;
        PreviousDistance = dist;
        PreviousDistanceTime = _curTime();
        OriginalDistance = dist;
        OriginalDistanceTime = _curTime();
    }

    public void BeginTurnToHeading()
    {
        if (_pendingActions.First is null || !HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        if (_interp.MotionsPending())
        {
            return;
        }

        float targetHeading = _pendingActions.First.Value.Heading;
        float diff = MoveToMath.HeadingDiff(targetHeading, _getHeading(), MotionCommand.TurnRight);

        uint turn;
        if (diff > 180f)
        {
            if (diff + MoveToMath.Epsilon >= 360f)
            {
                RemovePendingActionsHead();
                BeginNextNode();
                return;
            }
            turn = MotionCommand.TurnLeft;
        }
        else
        {
            if (diff <= MoveToMath.Epsilon)
            {
                RemovePendingActionsHead();
                BeginNextNode();
                return;
            }
            turn = MotionCommand.TurnRight;
        }

        var localParams = new MovementParameters
        {
            CancelMoveTo = false,
            Speed = Params.Speed,
            HoldKeyToApply = Params.HoldKeyToApply,
        };

        WeenieError err = _DoMotion(turn, localParams);
        if (err != WeenieError.None)
        {
            CancelMoveTo(err);
            return;
        }

        CurrentCommand = turn;
        PreviousHeading = diff;
    }


    public float GetCurrentDistance()
    {
        if (!HasPhysicsObj) return 0f;

        Vector3 myPos = _getPosition().Frame.Origin;
        Vector3 targetPos = CurrentTargetPosition.Frame.Origin;

        if (!Params.UseSpheres)
        {
            return Vector3.Distance(myPos, targetPos);
        }

        return MoveToMath.CylinderDistance(
            _getOwnRadius(), _getOwnHeight(), myPos,
            SoughtObjectRadius, SoughtObjectHeight, targetPos);
    }

    public bool CheckProgressMade(float currentDistance)
    {
        double elapsed = _curTime() - PreviousDistanceTime;
        if (elapsed <= 1.0) return true;

        float progress = MovingAway
            ? currentDistance - PreviousDistance
            : PreviousDistance - currentDistance;

        if (progress / (float)elapsed >= 0.25f)
        {
            PreviousDistance = currentDistance;
            PreviousDistanceTime = _curTime();

            float total = MovingAway
                ? currentDistance - OriginalDistance
                : OriginalDistance - currentDistance;
            float totalRate = total / (float)(_curTime() - OriginalDistanceTime);

            if (totalRate >= 0.25f) return true;
        }

        return false;
    }

    // ── 6. Per-tick drivers + target updates ────────────────────────────────

    public void UseTime()
    {
        if (!HasPhysicsObj || !_contact()) return;

        if (_pendingActions.First is null) return;

        bool objectMoveGate = TopLevelObjectId == 0 || MovementTypeState == MovementType.Invalid || Initialized;
        if (!objectMoveGate) return;

        MovementType type = _pendingActions.First.Value.Type;
        if (type == MovementType.MoveToPosition)
        {
            HandleMoveToPosition();
        }
        else if (type == MovementType.TurnToHeading)
        {
            HandleTurnToHeading();
        }
    }

    public void HandleMoveToPosition()
    {
        if (!HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        Vector3 curPos = _getPosition().Frame.Origin;

        var localParams = new MovementParameters
        {
            Speed = Params.Speed,
            CancelMoveTo = false,
            HoldKeyToApply = Params.HoldKeyToApply,
        };

        if (_interp.MotionsPending())
        {
            if (AuxCommand != 0)
            {
                _StopMotion(AuxCommand, localParams);
                AuxCommand = 0;
            }
        }
        else
        {
            Vector3 targetPos = CurrentTargetPosition.Frame.Origin;
            float toTargetHeading = MoveToMath.PositionHeading(curPos, targetPos);
            float heading = Params.GetDesiredHeading(CurrentCommand, MovingAway) + toTargetHeading;
            if (heading >= 360f) heading -= 360f;

            float diff = heading - _getHeading();
            if (MathF.Abs(diff) < MoveToMath.Epsilon) diff = 0f;
            if (diff < -MoveToMath.Epsilon) diff += 360f;

            if (diff <= 20f || diff >= 340f)
            {
                if (AuxCommand != 0)
                {
                    _StopMotion(AuxCommand, localParams);
                    AuxCommand = 0;
                }
            }
            else
            {
                uint turn = diff >= 180f ? MotionCommand.TurnLeft : MotionCommand.TurnRight;
                if (turn != AuxCommand)
                {
                    _DoMotion(turn, localParams);
                    AuxCommand = turn;
                }
            }
        }

        float dist = GetCurrentDistance();
        if (!CheckProgressMade(dist))
        {
            if (!_isInterpolating() && !_interp.MotionsPending())
            {
                FailProgressCount += 1;
            }
        }
        else
        {
            FailProgressCount = 0;

            bool arrived = MovingAway
                ? dist >= Params.MinDistance
                : dist <= Params.DistanceToObject;

            if (!arrived)
            {
                float startDist = Vector3.Distance(StartingPosition.Frame.Origin, _getPosition().Frame.Origin);
                if (startDist > Params.FailDistance)
                {
                    CancelMoveTo(WeenieError.YouChargedTooFar);
                }
            }
            else
            {
                RemovePendingActionsHead();
                _StopMotion(CurrentCommand, localParams);
                CurrentCommand = 0;
                if (AuxCommand != 0)
                {
                    _StopMotion(AuxCommand, localParams);
                    AuxCommand = 0;
                }
                BeginNextNode();
            }
        }

        if (TopLevelObjectId != 0 && MovementTypeState != MovementType.Invalid)
        {
            Vector3 v = _getVelocity();
            float speed = v.Length();
            if (speed > 0.1)
            {
                float eta = dist / speed;
                if (MathF.Abs(eta - (float)_getTargetQuantum()) >= 1.0f)
                {
                    _setTargetQuantum(eta);
                }
            }
        }
    }

    public void HandleTurnToHeading()
    {
        if (!HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        uint cmd = CurrentCommand;
        if (cmd != MotionCommand.TurnLeft && cmd != MotionCommand.TurnRight)
        {
            BeginTurnToHeading();
            return;
        }

        MoveToNode head = _pendingActions.First!.Value;
        float heading = _getHeading();

        if (MoveToMath.HeadingGreater(heading, head.Heading, cmd))
        {
            FailProgressCount = 0;
            _setHeading(head.Heading, true);
            RemovePendingActionsHead();

            var localParams = new MovementParameters
            {
                CancelMoveTo = false,
                HoldKeyToApply = Params.HoldKeyToApply,
            };
            _StopMotion(CurrentCommand, localParams);
            CurrentCommand = 0;
            BeginNextNode();
            return;
        }

        float diff = MoveToMath.HeadingDiff(heading, PreviousHeading, cmd);
        if (diff < 180f && diff > MoveToMath.Epsilon)
        {
            FailProgressCount = 0;
            PreviousHeading = heading;
            return;
        }

        PreviousHeading = heading;
        if (!_isInterpolating() && !_interp.MotionsPending())
        {
            FailProgressCount += 1;
        }
    }

    public void HandleUpdateTarget(TargetInfo info)
    {
        if (!HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        if (TopLevelObjectId != info.ObjectId) return;

        if (!Initialized)
        {
            if (TopLevelObjectId == _getSelfId())
            {
                Position selfPos = _getPosition();
                SoughtPosition = selfPos;
                CurrentTargetPosition = selfPos;
                CleanUpAndCallWeenie(WeenieError.None);
                return;
            }

            if (info.Status != TargetStatus.Ok)
            {
                CancelMoveTo(WeenieError.NoObject);
                return;
            }

            if (MovementTypeState == MovementType.MoveToObject)
            {
                MoveToObject_Internal(info.TargetPosition, info.InterpolatedPosition);
            }
            else if (MovementTypeState == MovementType.TurnToObject)
            {
                TurnToObject_Internal(info.TargetPosition);
            }
        }
        else
        {
            if (info.Status != TargetStatus.Ok)
            {
                CancelMoveTo(WeenieError.ObjectGone);
                return;
            }

            if (MovementTypeState == MovementType.MoveToObject)
            {
                SoughtPosition = info.InterpolatedPosition;
                CurrentTargetPosition = info.TargetPosition;
                PreviousDistance = float.MaxValue;
                PreviousDistanceTime = _curTime();
                OriginalDistance = float.MaxValue;
                OriginalDistanceTime = _curTime();
            }
        }
    }

    public void HitGround()
    {
        if (MovementTypeState == MovementType.Invalid) return;
        BeginNextNode();
    }


    public void MoveToObject_Internal(Position target, Position interpolated)
    {
        if (!HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        SoughtPosition = interpolated;
        CurrentTargetPosition = target;

        Vector3 myPos = _getPosition().Frame.Origin;
        Vector3 interpPos = interpolated.Frame.Origin;
        float iHeading = MoveToMath.PositionHeading(myPos, interpPos);

        float dist = GetCurrentDistance();

        float diff = iHeading - _getHeading();
        if (MathF.Abs(diff) < MoveToMath.Epsilon) diff = 0f;
        if (diff < -MoveToMath.Epsilon) diff += 360f;

        Params.GetCommand(dist, diff, out uint cmd, out _, out _);
        if (cmd != 0)
        {
            AddTurnToHeadingNode(iHeading);
            AddMoveToPositionNode();
        }

        if (Params.UseFinalHeading)
        {
            float final = iHeading + Params.DesiredHeading;
            if (final >= 360f) final -= 360f;
            AddTurnToHeadingNode(final);
        }

        Initialized = true;
        BeginNextNode();
    }

    public void TurnToObject_Internal(Position target)
    {
        if (!HasPhysicsObj)
        {
            CancelMoveTo(WeenieError.NoPhysicsObject);
            return;
        }

        CurrentTargetPosition = target;

        float soughtHeading = MoveToMath.GetHeading(SoughtPosition.Frame.Orientation);

        Vector3 myPos = _getPosition().Frame.Origin;
        Vector3 targetPos = CurrentTargetPosition.Frame.Origin;
        float targetHeading = MoveToMath.PositionHeading(myPos, targetPos);

        float final = (targetHeading + soughtHeading) % 360f;
        if (final < 0f) final += 360f;

        SoughtPosition = SoughtPosition with
        {
            Frame = new CellFrame(SoughtPosition.Frame.Origin, MoveToMath.SetHeading(SoughtPosition.Frame.Orientation, final)),
        };

        _pendingActions.AddLast(new MoveToNode(MovementType.TurnToHeading, final));

        Initialized = true;
        BeginNextNode();
    }


    private WeenieError _DoMotion(uint motion, MovementParameters p)
    {
        if (!HasPhysicsObj) return WeenieError.NoPhysicsObject;

        float speed = p.Speed;
        _interp.adjust_motion(ref motion, ref speed, p.HoldKeyToApply);
        p.Speed = speed;

        return _interp.DoInterpretedMotion(motion, p);
    }

    private WeenieError _StopMotion(uint motion, MovementParameters p)
    {
        if (!HasPhysicsObj) return WeenieError.NoPhysicsObject;

        float speed = p.Speed;
        _interp.adjust_motion(ref motion, ref speed, p.HoldKeyToApply);
        p.Speed = speed;

        return _interp.StopInterpretedMotion(motion, p);
    }

    public void CancelMoveTo(WeenieError error)
    {
        if (MovementTypeState == MovementType.Invalid) return;

        _pendingActions.Clear();
        CleanUp();
        if (HasPhysicsObj) _stopCompletely();
        MoveToCancelled?.Invoke(error);
    }

    public void CleanUp()
    {
        var localParams = new MovementParameters
        {
            HoldKeyToApply = Params.HoldKeyToApply,
            CancelMoveTo = false,
        };

        if (HasPhysicsObj)
        {
            if (CurrentCommand != 0) _StopMotion(CurrentCommand, localParams);
            if (AuxCommand != 0) _StopMotion(AuxCommand, localParams);
            if (TopLevelObjectId != 0 && MovementTypeState != MovementType.Invalid)
            {
                _clearTarget();
            }
        }

        InitializeLocalVariables();
    }

    public void CleanUpAndCallWeenie(WeenieError error)
    {
        CleanUp();
        if (HasPhysicsObj) _stopCompletely();
        MoveToComplete?.Invoke(error);
    }


    private void CopyParams(MovementParameters p)
    {
        Params.CanWalk = p.CanWalk;
        Params.CanRun = p.CanRun;
        Params.CanSidestep = p.CanSidestep;
        Params.CanWalkBackwards = p.CanWalkBackwards;
        Params.CanCharge = p.CanCharge;
        Params.FailWalk = p.FailWalk;
        Params.UseFinalHeading = p.UseFinalHeading;
        Params.Sticky = p.Sticky;
        Params.MoveAway = p.MoveAway;
        Params.MoveTowards = p.MoveTowards;
        Params.UseSpheres = p.UseSpheres;
        Params.SetHoldKey = p.SetHoldKey;
        Params.Autonomous = p.Autonomous;
        Params.ModifyRawState = p.ModifyRawState;
        Params.ModifyInterpretedState = p.ModifyInterpretedState;
        Params.CancelMoveTo = p.CancelMoveTo;
        Params.StopCompletelyFlag = p.StopCompletelyFlag;
        Params.DisableJumpDuringLink = p.DisableJumpDuringLink;

        Params.DistanceToObject = p.DistanceToObject;
        Params.MinDistance = p.MinDistance;
        Params.DesiredHeading = p.DesiredHeading;
        Params.Speed = p.Speed;
        Params.FailDistance = p.FailDistance;
        Params.WalkRunThreshhold = p.WalkRunThreshhold;
        Params.ContextId = p.ContextId;
        Params.HoldKeyToApply = p.HoldKeyToApply;
        Params.ActionStamp = p.ActionStamp;
    }
}

public readonly record struct TargetInfo(
    uint ObjectId,
    TargetStatus Status,
    Position TargetPosition,
    Position InterpolatedPosition,
    uint ContextId = 0,
    float Radius = 0f,
    double Quantum = 0.0,
    Vector3 InterpolatedHeading = default,
    Vector3 Velocity = default,
    double LastUpdateTime = 0.0);

public enum TargetStatus
{
    /// <summary>0 — undefined/uninitialized.</summary>
    Undefined = 0,
    /// <summary>1 — target resolved and tracked normally.</summary>
    Ok = 1,
    /// <summary>2 — target left the world (despawned).</summary>
    ExitWorld = 2,
    /// <summary>3 — target teleported.</summary>
    Teleported = 3,
    Contained = 4,
    /// <summary>5 — target became parented (e.g. mounted/wielded).</summary>
    Parented = 5,
    /// <summary>6 — tracker timed out without an update.</summary>
    TimedOut = 6,
}
