using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.App.Physics;

internal static class InboundInterpretedMotionFactory
{
    private const uint NonCombatStyle = 0x8000003Du;
    private const uint Ready = 0x41000003u;

    public static InboundInterpretedState Create(
        in CreateObject.ServerMotionState wire,
        uint fallbackForwardClass = 0x40000000u)
    {
        var result = new InboundInterpretedState
        {
            CurrentStyle = wire.Stance != 0
                ? 0x80000000u | wire.Stance
                : NonCombatStyle,
            ForwardCommand = ResolveForward(wire.ForwardCommand, fallbackForwardClass),
            ForwardSpeed = wire.ForwardSpeed ?? 1f,
            SideStepCommand = ResolveAxis(wire.SideStepCommand, 0x65000000u),
            SideStepSpeed = wire.SideStepSpeed ?? 1f,
            TurnCommand = ResolveAxis(wire.TurnCommand, 0x65000000u),
            TurnSpeed = wire.TurnSpeed ?? 1f,
        };

        if (wire.Commands is { Count: > 0 } commands)
        {
            var actions = new List<InboundMotionAction>(commands.Count);
            foreach (var item in commands)
            {
                uint command = MotionCommandResolver.ReconstructFullCommand(item.Command);
                if (command == 0)
                    command = 0x10000000u | item.Command;

                actions.Add(new InboundMotionAction(
                    command,
                    Stamp: item.PackedSequence & 0x7FFF,
                    Autonomous: (item.PackedSequence & 0x8000) != 0,
                    Speed: item.Speed));
            }

            result.Actions = actions;
        }

        return result;
    }

    private static uint ResolveForward(ushort? wireCommand, uint fallbackClass)
    {
        if (wireCommand is not { } command || command == 0)
            return Ready;

        uint resolved = MotionCommandResolver.ReconstructFullCommand(command);
        if (resolved != 0)
            return resolved;

        uint commandClass = fallbackClass & 0xFF000000u;
        return (commandClass != 0 ? commandClass : 0x40000000u) | command;
    }

    private static uint ResolveAxis(ushort? wireCommand, uint fallbackClass)
    {
        if (wireCommand is not { } command || command == 0)
            return 0;

        uint resolved = MotionCommandResolver.ReconstructFullCommand(command);
        return resolved != 0 ? resolved : fallbackClass | command;
    }
}
