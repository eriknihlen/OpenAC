using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Physics;

public enum HoldKey : uint
{
    Invalid = 0x0,
    None    = 0x1,
    Run     = 0x2,
}

public readonly record struct RawMotionAction(
    ushort Command,
    ushort Stamp,
    bool Autonomous,
    float Speed = 1f);

public sealed class RawMotionState
{
    public RawMotionState()
    {
    }

    public RawMotionState(RawMotionState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        CurrentHoldKey = other.CurrentHoldKey;
        CurrentStyle = other.CurrentStyle;
        ForwardCommand = other.ForwardCommand;
        ForwardHoldKey = other.ForwardHoldKey;
        ForwardSpeed = other.ForwardSpeed;
        SidestepCommand = other.SidestepCommand;
        SidestepHoldKey = other.SidestepHoldKey;
        SidestepSpeed = other.SidestepSpeed;
        TurnCommand = other.TurnCommand;
        TurnHoldKey = other.TurnHoldKey;
        TurnSpeed = other.TurnSpeed;
        _actions.AddRange(other._actions);
    }

    public HoldKey CurrentHoldKey  { get; set; } = HoldKey.None;
    public uint    CurrentStyle    { get; set; } = 0x8000003Du;
    public uint    ForwardCommand  { get; set; } = 0x41000003u;
    public HoldKey ForwardHoldKey  { get; set; } = HoldKey.Invalid;
    public float   ForwardSpeed    { get; set; } = 1.0f;
    public uint    SidestepCommand { get; set; }
    public HoldKey SidestepHoldKey { get; set; } = HoldKey.Invalid;
    public float   SidestepSpeed   { get; set; } = 1.0f;
    public uint    TurnCommand     { get; set; }
    public HoldKey TurnHoldKey     { get; set; } = HoldKey.Invalid;
    public float   TurnSpeed       { get; set; } = 1.0f;

    private readonly List<RawMotionAction> _actions = new();

    public IReadOnlyList<RawMotionAction> Actions
    {
        get => _actions;
        set
        {
            _actions.Clear();
            _actions.AddRange(value);
        }
    }

    public static readonly RawMotionState Default = new();

    public void AddAction(uint motion, float speed, uint actionStamp, bool autonomous)
    {
        _actions.Add(new RawMotionAction(
            Command: (ushort)motion,
            Stamp: (ushort)actionStamp,
            Autonomous: autonomous,
            Speed: speed));
    }

    public uint RemoveAction()
    {
        if (_actions.Count == 0)
            return 0;
        var head = _actions[0];
        _actions.RemoveAt(0);
        return head.Command;
    }

    public void ApplyMotion(uint motion, MovementParameters p)
    {
        if (motion - 0x6500000du > 3u)
        {
            if ((motion & 0x40000000u) == 0)
            {
                if (motion < 0x80000000u) // arg2 >= 0 as signed int32
                {
                    if ((motion & 0x10000000u) != 0)
                        AddAction(motion, p.Speed, p.ActionStamp, p.Autonomous);
                }
                else if (CurrentStyle != motion)
                {
                    ForwardCommand = 0x41000003u;
                    CurrentStyle = motion;
                }
            }
            else if (motion != 0x44000007u)
            {
                ForwardCommand = motion;
                if (p.SetHoldKey)
                {
                    ForwardHoldKey = HoldKey.Invalid;
                    ForwardSpeed = p.Speed;
                }
                else
                {
                    ForwardHoldKey = p.HoldKeyToApply;
                    ForwardSpeed = p.Speed;
                }
            }
            return;
        }

        switch (motion)
        {
            case 0x6500000du: // TurnRight
            case 0x6500000eu: // TurnLeft
                TurnCommand = motion;
                if (p.SetHoldKey)
                {
                    TurnHoldKey = HoldKey.Invalid;
                    TurnSpeed = p.Speed;
                }
                else
                {
                    TurnHoldKey = p.HoldKeyToApply;
                    TurnSpeed = p.Speed;
                }
                return;
            case 0x6500000fu: // SideStepRight
            case 0x65000010u: // SideStepLeft
                SidestepCommand = motion;
                if (p.SetHoldKey)
                {
                    SidestepHoldKey = HoldKey.Invalid;
                    SidestepSpeed = p.Speed;
                }
                else
                {
                    SidestepHoldKey = p.HoldKeyToApply;
                    SidestepSpeed = p.Speed;
                }
                return;
        }
    }

    public void RemoveMotion(uint motion)
    {
        if (motion - 0x6500000du > 3u)
        {
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
            return;
        }

        switch (motion)
        {
            case 0x6500000du:
            case 0x6500000eu:
                TurnCommand = 0;
                return;
            case 0x6500000fu:
            case 0x65000010u:
                SidestepCommand = 0;
                return;
        }
    }
}
