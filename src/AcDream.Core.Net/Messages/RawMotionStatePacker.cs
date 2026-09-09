using AcDream.Core.Net.Packets;
using AcDream.Core.Physics;

namespace AcDream.Core.Net.Messages;

public static class RawMotionStatePacker
{
    private const uint FlagCurrentHoldKey   = 0x001u;
    private const uint FlagCurrentStyle     = 0x002u;
    private const uint FlagForwardCommand   = 0x004u;
    private const uint FlagForwardHoldKey   = 0x008u;
    private const uint FlagForwardSpeed     = 0x010u;
    private const uint FlagSidestepCommand  = 0x020u;
    private const uint FlagSidestepHoldKey  = 0x040u;
    private const uint FlagSidestepSpeed    = 0x080u;
    private const uint FlagTurnCommand      = 0x100u;
    private const uint FlagTurnHoldKey      = 0x200u;
    private const uint FlagTurnSpeed        = 0x400u;
    private const int  NumActionsShift      = 11;

    public static void Pack(PacketWriter w, RawMotionState state)
    {
        var defaults = RawMotionState.Default;

        uint flags = 0u;
        if (state.CurrentHoldKey  != defaults.CurrentHoldKey)  flags |= FlagCurrentHoldKey;
        if (state.CurrentStyle    != defaults.CurrentStyle)    flags |= FlagCurrentStyle;
        if (state.ForwardCommand  != defaults.ForwardCommand)  flags |= FlagForwardCommand;
        if (state.ForwardHoldKey  != defaults.ForwardHoldKey)  flags |= FlagForwardHoldKey;
        if (state.ForwardSpeed    != defaults.ForwardSpeed)    flags |= FlagForwardSpeed;
        if (state.SidestepCommand != defaults.SidestepCommand) flags |= FlagSidestepCommand;
        if (state.SidestepHoldKey != defaults.SidestepHoldKey) flags |= FlagSidestepHoldKey;
        if (state.SidestepSpeed   != defaults.SidestepSpeed)   flags |= FlagSidestepSpeed;
        if (state.TurnCommand     != defaults.TurnCommand)     flags |= FlagTurnCommand;
        if (state.TurnHoldKey     != defaults.TurnHoldKey)     flags |= FlagTurnHoldKey;
        if (state.TurnSpeed       != defaults.TurnSpeed)       flags |= FlagTurnSpeed;

        int numActions = state.Actions.Count;
        flags |= (uint)(numActions << NumActionsShift);

        w.WriteUInt32(flags);

        if ((flags & FlagCurrentHoldKey)  != 0) w.WriteUInt32((uint)state.CurrentHoldKey);
        if ((flags & FlagCurrentStyle)    != 0) w.WriteUInt32(state.CurrentStyle);
        if ((flags & FlagForwardCommand)  != 0) w.WriteUInt32(state.ForwardCommand);
        if ((flags & FlagForwardHoldKey)  != 0) w.WriteUInt32((uint)state.ForwardHoldKey);
        if ((flags & FlagForwardSpeed)    != 0) w.WriteFloat(state.ForwardSpeed);
        if ((flags & FlagSidestepCommand) != 0) w.WriteUInt32(state.SidestepCommand);
        if ((flags & FlagSidestepHoldKey) != 0) w.WriteUInt32((uint)state.SidestepHoldKey);
        if ((flags & FlagSidestepSpeed)   != 0) w.WriteFloat(state.SidestepSpeed);
        if ((flags & FlagTurnCommand)     != 0) w.WriteUInt32(state.TurnCommand);
        if ((flags & FlagTurnHoldKey)     != 0) w.WriteUInt32((uint)state.TurnHoldKey);
        if ((flags & FlagTurnSpeed)       != 0) w.WriteFloat(state.TurnSpeed);

        foreach (var action in state.Actions)
        {
            w.WriteUInt16(action.Command);
            ushort stampWord = (ushort)((action.Stamp & 0x7FFF) | (action.Autonomous ? 0x8000 : 0));
            w.WriteUInt16(stampWord);
        }
    }
}
