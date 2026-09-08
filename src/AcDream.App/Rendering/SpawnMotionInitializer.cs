using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

internal static class SpawnMotionInitializer
{
    internal readonly record struct Plan(uint Style, uint Motion);

    public static AnimationSequencer Create(
        Setup setup,
        MotionTable motionTable,
        IAnimationLoader loader,
        CreateObject.ServerMotionState? wireState)
    {
        var sequencer = new AnimationSequencer(setup, motionTable, loader);
        Plan plan = ResolvePlan(motionTable, wireState);

        // set_description: install the table default, then apply MovementData.
        sequencer.InitializeState();
        sequencer.SetCycle(plan.Style, plan.Motion);

        sequencer.Manager.HandleEnterWorld();
        return sequencer;
    }

    public static void Reinitialize(
        AnimationSequencer sequencer,
        MotionTable motionTable,
        CreateObject.ServerMotionState? wireState)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        ArgumentNullException.ThrowIfNull(motionTable);
        Plan plan = ResolvePlan(motionTable, wireState);
        sequencer.Reset();
        sequencer.InitializeState();
        sequencer.SetCycle(plan.Style, plan.Motion);
        sequencer.Manager.HandleEnterWorld();
    }

    internal static Plan ResolvePlan(
        MotionTable motionTable,
        CreateObject.ServerMotionState? wireState)
    {
        uint style = wireState is { Stance: > 0 } state
            ? 0x80000000u | state.Stance
            : (uint)motionTable.DefaultStyle;

        uint motion = MotionCommand.Ready;
        if (wireState?.ForwardCommand is ushort command && command > 0)
        {
            uint resolved = MotionCommandResolver.ReconstructFullCommand(command);
            motion = resolved != 0
                ? resolved
                : 0x40000000u | command;
        }

        return new Plan(style, motion);
    }
}
