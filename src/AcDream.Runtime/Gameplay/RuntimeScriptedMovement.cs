using System.Numerics;

namespace AcDream.Runtime.Gameplay;

/// <summary>Which way a scripted move goes.</summary>
public enum RuntimeMoveDirection
{
    Forward,
    Backward,
    StrafeLeft,
    StrafeRight,
    TurnLeft,
    TurnRight,
}

public enum RuntimeMovePace
{
    Walk,
    Run,
}

/// <summary>What a scripted move's amount counts.</summary>
public enum RuntimeMoveUnit
{
    /// <summary>Meters going forward, backward or sideways, and degrees for a turn.</summary>
    MetersOrDegrees,

    Seconds,
}

/// <summary>
/// The parts of movement that combine the way held movement keys do: going
/// forward or backward, strafing, and turning. Each carries one scripted move.
/// </summary>
public enum RuntimeMoveChannel
{
    Travel,
    Strafe,
    Turn,
}

/// <summary>Where a channel's most recent scripted move stands.</summary>
public enum RuntimeScriptedMoveState
{
    None = 0,

    Moving,

    /// <summary>It covered its distance, angle or time.</summary>
    Completed,

    /// <summary>A stop command ended it.</summary>
    Stopped,

    /// <summary>Time ran out: the limit for a move without an amount, or a move too slow to cover its distance or angle.</summary>
    TimeLimit,

    /// <summary>It stopped making progress.</summary>
    Blocked,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,
}

/// <summary>
/// A move for the client to carry out: a direction, a pace, and an amount that
/// counts <paramref name="Unit"/>. An amount of zero keeps going until stopped.
/// </summary>
public readonly record struct RuntimeMoveRequest(
    RuntimeMoveDirection Direction,
    RuntimeMovePace Pace,
    float Amount,
    RuntimeMoveUnit Unit = RuntimeMoveUnit.MetersOrDegrees)
{
    public RuntimeMoveChannel Channel => Direction switch
    {
        RuntimeMoveDirection.Forward or RuntimeMoveDirection.Backward => RuntimeMoveChannel.Travel,
        RuntimeMoveDirection.StrafeLeft or RuntimeMoveDirection.StrafeRight => RuntimeMoveChannel.Strafe,
        _ => RuntimeMoveChannel.Turn,
    };
}

/// <summary>
/// One channel's most recent scripted move. <paramref name="Covered"/> is meters,
/// or degrees for a turn, whatever the move's unit.
/// </summary>
public readonly record struct RuntimeMoveChannelSnapshot(
    long Sequence,
    RuntimeScriptedMoveState State,
    RuntimeMoveRequest Request,
    float Covered,
    float ElapsedSeconds);

/// <summary>
/// The most recent scripted move on each channel, and the most recent jump.
/// Move sequences grow by one for every move begun on any channel, and
/// <see cref="JumpSequence"/> by one for every jump.
/// </summary>
public readonly record struct RuntimeScriptedMoveSnapshot(
    RuntimeMoveChannelSnapshot Travel,
    RuntimeMoveChannelSnapshot Strafe,
    RuntimeMoveChannelSnapshot Turn,
    long JumpSequence,
    bool JumpCharging)
{
    public RuntimeMoveChannelSnapshot this[RuntimeMoveChannel channel] => channel switch
    {
        RuntimeMoveChannel.Travel => Travel,
        RuntimeMoveChannel.Strafe => Strafe,
        _ => Turn,
    };
}

/// <summary>
/// One frame's view of the body, with <paramref name="OffsetFromStart"/> measured
/// from where it stood when scripted movement last began.
/// </summary>
public readonly record struct RuntimeScriptedMoveSample(
    Vector3 OffsetFromStart,
    float HeadingDegrees,
    double TimeSeconds,
    bool InPortalSpace,
    bool ManualMovementHeld);

/// <summary>
/// Carries out scripted moves one frame at a time. A travel, a strafe and a turn
/// move are held together, each until its distance, angle or time is covered,
/// and each ends early when it stops making progress or runs out of time. Travel
/// and strafe progress is measured along and across the body's facing, so arcs
/// and diagonals count their whole path. A turn that passes its angle reports the
/// exact heading to land on. The player moving the character, or portal space,
/// ends every move. A jump is held for its charge and then released, alongside
/// any moves.
/// </summary>
public sealed class RuntimeScriptedMovement
{
    public const float MaximumMoveMeters = 500f;
    public const float MaximumTurnDegrees = 3600f;
    public const float MaximumMoveSeconds = 300f;
    public const double OpenEndedSeconds = 30d;
    public const double ProgressWindowSeconds = 1.5d;
    public const float MinimumProgressMeters = 0.3f;
    public const float MinimumProgressDegrees = 5f;
    public const float SlowestMetersPerSecond = 1f;
    public const float SlowestDegreesPerSecond = 30f;
    public const double FullJumpChargeSeconds = 1d;
    public const float ArrivalToleranceMeters = 0.05f;

    /// <summary>A body step longer than this in one frame is a correction, not movement.</summary>
    public const float LongestStepMeters = 10f;

    private readonly Channel[] _channels = [new(), new(), new()];
    private long _sequence;
    private long _jumpSequence;
    private bool _jumpCharging;
    private double _jumpHoldSeconds;

    /// <summary>The power the jump under way was asked for, which it is released at exactly.</summary>
    private float _jumpPower;
    private double? _jumpReleaseAt;
    private RuntimeMovePace? _jumpLeaveAt;
    private bool _heldLastFrame;

    /// <summary>
    /// Whether the input last held ran. Letting go of a move keeps that pace for the frame it
    /// ends, since a walk let go of with run held stops with a lurch well past where a walk stops.
    /// </summary>
    private bool _heldRun;
    private bool _sampled;
    private Vector2 _lastOffset;
    private float _lastHeading;

    public RuntimeScriptedMoveSnapshot Snapshot => new(
        _channels[(int)RuntimeMoveChannel.Travel].Snapshot,
        _channels[(int)RuntimeMoveChannel.Strafe].Snapshot,
        _channels[(int)RuntimeMoveChannel.Turn].Snapshot,
        _jumpSequence,
        _jumpCharging);

    /// <summary>Whether a move or jump is held, or a release still has to reach the body.</summary>
    public bool IsActive => IsMoving || _jumpCharging || _heldLastFrame;

    private bool IsMoving => Array.Exists(_channels, static channel => channel.IsMoving);

    public static bool IsValid(in RuntimeMoveRequest request)
    {
        if (!Enum.IsDefined(request.Direction)
            || !Enum.IsDefined(request.Pace)
            || !Enum.IsDefined(request.Unit)
            || !float.IsFinite(request.Amount)
            || request.Amount < 0f)
        {
            return false;
        }
        float most = request.Unit == RuntimeMoveUnit.Seconds
            ? MaximumMoveSeconds
            : request.Channel == RuntimeMoveChannel.Turn ? MaximumTurnDegrees : MaximumMoveMeters;
        return request.Amount <= most;
    }

    /// <summary>Begins a move on its channel, replacing only a move already on that channel.</summary>
    public bool Begin(in RuntimeMoveRequest request)
    {
        if (!IsValid(request))
            return false;
        _channels[(int)request.Channel].Begin(++_sequence, request);
        return true;
    }

    /// <summary>Ends every move in progress.</summary>
    public bool Stop() => EndAll(RuntimeScriptedMoveState.Stopped);

    /// <summary>Ends the move on one channel, if there is one.</summary>
    public bool Stop(RuntimeMoveChannel channel) =>
        Enum.IsDefined(channel) && _channels[(int)channel].End(RuntimeScriptedMoveState.Stopped);

    public bool Interrupt() => EndAll(RuntimeScriptedMoveState.Interrupted);

    /// <summary>Ends every move and any charging jump, because the body is in portal space or gone.</summary>
    public bool Lose()
    {
        bool jump = _jumpCharging;
        _jumpCharging = false;
        _jumpReleaseAt = null;
        _jumpLeaveAt = null;
        return EndAll(RuntimeScriptedMoveState.Lost) | jump;
    }

    /// <summary>
    /// Charges a jump for <paramref name="power"/> of a full charge, then releases it. With
    /// <paramref name="leaveAt"/>, the body charges standing and presses forward at that pace
    /// from the frame the jump releases, so it leaves the ground from where it stood, moving.
    /// </summary>
    public bool BeginJump(float power, RuntimeMovePace? leaveAt = null)
    {
        if (!float.IsFinite(power) || power <= 0f || power > 1f || _jumpCharging)
            return false;
        _jumpHoldSeconds = power * FullJumpChargeSeconds;
        _jumpPower = power;
        _jumpReleaseAt = null;
        _jumpLeaveAt = leaveAt;
        _jumpSequence++;
        _jumpCharging = true;
        return true;
    }

    public MovementInput? Advance(in RuntimeScriptedMoveSample sample) => Advance(sample, out _);

    /// <summary>
    /// Advances by one frame. Returns the input the body should act on, or
    /// <see langword="null"/> when nothing scripted is held and the frame's own
    /// input applies. <paramref name="heading"/> is the compass heading to set the
    /// body to at once, when a turn has just passed its angle.
    /// </summary>
    public MovementInput? Advance(in RuntimeScriptedMoveSample sample, out float? heading)
    {
        heading = null;
        if (!IsActive)
            return null;

        var offset = new Vector2(sample.OffsetFromStart.X, sample.OffsetFromStart.Y);
        Vector2 step = _sampled ? offset - _lastOffset : Vector2.Zero;
        float turned = _sampled ? SignedDegrees(sample.HeadingDegrees - _lastHeading) : 0f;
        _lastOffset = offset;
        _lastHeading = sample.HeadingDegrees;
        _sampled = true;

        if (sample.InPortalSpace)
        {
            Lose();
        }
        else if (IsMoving)
        {
            if (sample.ManualMovementHeld)
                Interrupt();
            else
                heading = Measure(sample, step, turned);
        }

        bool jump = false;
        float? released = null;
        if (_jumpCharging)
        {
            _jumpReleaseAt ??= sample.TimeSeconds + _jumpHoldSeconds;
            if (sample.TimeSeconds >= _jumpReleaseAt)
            {
                released = _jumpPower;
                _jumpCharging = false;
                _jumpReleaseAt = null;
                if (_jumpLeaveAt is { } pace)
                    Begin(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, pace, 0f));
                _jumpLeaveAt = null;
            }
            else
            {
                jump = true;
            }
        }

        MovementInput? input = null;
        if (IsMoving || jump)
        {
            MovementInput held = Hold(jump) with { JumpExtent = released };
            _heldRun = held.Run;
            input = held;
            _heldLastFrame = true;
        }
        else if (_heldLastFrame)
        {
            input = new MovementInput(Run: _heldRun, IsPersistentCommand: true, JumpExtent: released);
            _heldLastFrame = false;
        }
        if (!IsActive)
            _sampled = false;
        return input;
    }

    private float? Measure(in RuntimeScriptedMoveSample sample, Vector2 step, float turned)
    {
        if (step.Length() > LongestStepMeters)
            step = Vector2.Zero;
        float radians = sample.HeadingDegrees * (MathF.PI / 180f);
        var facing = new Vector2(MathF.Sin(radians), MathF.Cos(radians));
        float ahead = Vector2.Dot(step, facing);
        float aside = Vector2.Dot(step, new Vector2(facing.Y, -facing.X));

        float? heading = null;
        foreach (Channel channel in _channels)
        {
            if (!channel.IsMoving)
                continue;
            RuntimeMoveDirection direction = channel.Request.Direction;
            float toward = direction switch
            {
                RuntimeMoveDirection.Forward => ahead,
                RuntimeMoveDirection.Backward => -ahead,
                RuntimeMoveDirection.StrafeRight => aside,
                RuntimeMoveDirection.StrafeLeft => -aside,
                RuntimeMoveDirection.TurnRight => turned,
                _ => -turned,
            };
            if (channel.Advance(sample.TimeSeconds, toward) is { } overshoot)
            {
                heading = NormalizedDegrees(direction == RuntimeMoveDirection.TurnRight
                    ? sample.HeadingDegrees - overshoot
                    : sample.HeadingDegrees + overshoot);
            }
        }
        return heading;
    }

    private MovementInput Hold(bool jump)
    {
        Channel travel = _channels[(int)RuntimeMoveChannel.Travel];
        Channel strafe = _channels[(int)RuntimeMoveChannel.Strafe];
        Channel turn = _channels[(int)RuntimeMoveChannel.Turn];
        RuntimeMovePace pace = travel.IsMoving ? travel.Request.Pace
            : strafe.IsMoving ? strafe.Request.Pace
            : turn.IsMoving ? turn.Request.Pace
            : RuntimeMovePace.Run;
        return new MovementInput(
            Forward: travel.Holds(RuntimeMoveDirection.Forward),
            Backward: travel.Holds(RuntimeMoveDirection.Backward),
            StrafeLeft: strafe.Holds(RuntimeMoveDirection.StrafeLeft),
            StrafeRight: strafe.Holds(RuntimeMoveDirection.StrafeRight),
            TurnLeft: turn.Holds(RuntimeMoveDirection.TurnLeft),
            TurnRight: turn.Holds(RuntimeMoveDirection.TurnRight),
            Run: pace == RuntimeMovePace.Run,
            Jump: jump,
            IsPersistentCommand: true);
    }

    private bool EndAll(RuntimeScriptedMoveState state)
    {
        bool ended = false;
        foreach (Channel channel in _channels)
            ended |= channel.End(state);
        return ended;
    }

    private static float SignedDegrees(float degrees) => (((degrees % 360f) + 540f) % 360f) - 180f;

    private static float NormalizedDegrees(float degrees) => ((degrees % 360f) + 360f) % 360f;

    private sealed class Channel
    {
        private bool _started;
        private double _startedAt;
        private double _checkpointAt;
        private float _checkpointCovered;

        /// <summary>The latest frame's progress, kept across moves as the size of the next step.</summary>
        private float _lastStep;

        public long Sequence { get; private set; }
        public RuntimeScriptedMoveState State { get; private set; }
        public RuntimeMoveRequest Request { get; private set; }
        public float Covered { get; private set; }
        public double Elapsed { get; private set; }

        public bool IsMoving => State == RuntimeScriptedMoveState.Moving;

        public RuntimeMoveChannelSnapshot Snapshot => new(Sequence, State, Request, Covered, (float)Elapsed);

        public bool Holds(RuntimeMoveDirection direction) => IsMoving && Request.Direction == direction;

        public void Begin(long sequence, in RuntimeMoveRequest request)
        {
            Sequence = sequence;
            State = RuntimeScriptedMoveState.Moving;
            Request = request;
            Covered = 0f;
            Elapsed = 0d;
            _started = false;
        }

        public bool End(RuntimeScriptedMoveState state)
        {
            if (!IsMoving)
                return false;
            State = state;
            return true;
        }

        /// <summary>
        /// Counts one frame's progress toward the move and ends it when it is
        /// done. A distance ends within half a step of its amount. Returns how far
        /// a turn went past its angle on the frame it arrives, so the body can
        /// land on the angle.
        /// </summary>
        public float? Advance(double time, float toward)
        {
            if (!_started)
            {
                _started = true;
                _startedAt = time;
                _checkpointAt = time;
                _checkpointCovered = 0f;
                return null;
            }

            bool turn = Request.Channel == RuntimeMoveChannel.Turn;
            float progress = MathF.Max(0f, toward);
            Covered += progress;
            if (progress > 0f)
                _lastStep = progress;
            Elapsed = time - _startedAt;

            if (Request.Amount > 0f)
            {
                if (Request.Unit == RuntimeMoveUnit.Seconds)
                {
                    if (Elapsed >= Request.Amount)
                    {
                        State = RuntimeScriptedMoveState.Completed;
                        return null;
                    }
                }
                else if (turn)
                {
                    if (Covered >= Request.Amount)
                    {
                        float overshoot = Covered - Request.Amount;
                        Covered = Request.Amount;
                        State = RuntimeScriptedMoveState.Completed;
                        return overshoot;
                    }
                }
                else if (Covered >= Request.Amount - MathF.Max(ArrivalToleranceMeters, _lastStep * 0.5f))
                {
                    State = RuntimeScriptedMoveState.Completed;
                    return null;
                }
            }

            double limit = Request.Amount <= 0f
                ? OpenEndedSeconds
                : Request.Unit == RuntimeMoveUnit.Seconds
                    ? double.PositiveInfinity
                    : (Request.Amount / (turn ? SlowestDegreesPerSecond : SlowestMetersPerSecond))
                        + (ProgressWindowSeconds * 2d);
            if (Elapsed >= limit)
            {
                State = RuntimeScriptedMoveState.TimeLimit;
                return null;
            }

            if (time - _checkpointAt >= ProgressWindowSeconds)
            {
                if (Covered - _checkpointCovered < (turn ? MinimumProgressDegrees : MinimumProgressMeters))
                {
                    State = RuntimeScriptedMoveState.Blocked;
                    return null;
                }
                _checkpointAt = time;
                _checkpointCovered = Covered;
            }
            return null;
        }
    }
}

/// <summary>
/// The frame's movement input, with scripted moves or a jump in progress taking
/// the place of the frame's own input until they end or the player moves.
/// </summary>
public sealed class RuntimeScriptedMovementInputSource : IRuntimeMovementInputSource
{
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly IRuntimeMovementInputSource _inner;

    public RuntimeScriptedMovementInputSource(
        RuntimeLocalPlayerMovementState movement,
        IRuntimeMovementInputSource inner)
    {
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public MovementInput Capture()
    {
        MovementInput input = _inner.Capture();
        return _movement.CaptureScriptedInput(input) ?? input;
    }
}
