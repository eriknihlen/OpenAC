using System;
using System.Collections.Generic;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics.Motion;

public static class MotionTablePose
{
    public static IReadOnlyList<Frame>? DefaultStatePartFrames(
        MotionTable mt,
        Func<uint, Animation?> loadAnimation)
    {
        if (mt is null) return null;

        // SetDefaultState: StyleDefaults[DefaultStyle] → the default substate.
        if (!mt.StyleDefaults.TryGetValue(mt.DefaultStyle, out var defaultSubstateCmd))
            return null;

        uint style = (uint)mt.DefaultStyle;
        uint substate = (uint)defaultSubstateCmd;
        int key = (int)((style << 16) | (substate & 0xFFFFFFu));

        if (!mt.Cycles.TryGetValue(key, out var cycle) || cycle.Anims.Count == 0)
            return null;

        var animRef = cycle.Anims[0];
        var anim = loadAnimation(animRef.AnimId);
        if (anim is null || anim.PartFrames.Count == 0) return null;

        int idx = Math.Clamp((int)animRef.LowFrame, 0, anim.PartFrames.Count - 1);
        var frames = anim.PartFrames[idx].Frames;
        return frames.Count > 0 ? frames : null;
    }
}
