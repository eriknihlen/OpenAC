using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using AcDream.Core.Content;

namespace AcDream.Core.Meshing;

public static class MotionResolver
{
    public sealed record IdleCycle(
        Animation Animation,
        int LowFrame,
        int HighFrame,
        float Framerate);

    public static IdleCycle? GetIdleCycle(
        Setup setup,
        IDatObjectSource dats,
        IAnimationLoader animationLoader,
        uint? motionTableIdOverride = null,
        ushort? stanceOverride = null,
        ushort? commandOverride = null)
    {
        var resolved = ResolveIdleCycleInternal(
            setup, dats, animationLoader,
            motionTableIdOverride, stanceOverride, commandOverride);
        if (resolved is null) return null;
        var (anim, ad) = resolved.Value;

        int numFrames = anim.PartFrames.Count;
        int lowFrame = ad.LowFrame;
        int highFrame = ad.HighFrame;

        if (highFrame < 0)
            highFrame = numFrames - 1;
        if (lowFrame >= numFrames)
            lowFrame = numFrames - 1;
        if (highFrame >= numFrames)
            highFrame = numFrames - 1;
        if (lowFrame < 0)
            lowFrame = 0;
        if (lowFrame > highFrame)
            highFrame = lowFrame;

        return new IdleCycle(anim, lowFrame, highFrame, ad.Framerate);
    }

    public static AnimationFrame? GetIdleFrame(
        Setup setup,
        IDatObjectSource dats,
        IAnimationLoader animationLoader,
        uint? motionTableIdOverride = null,
        ushort? stanceOverride = null,
        ushort? commandOverride = null)
    {
        var resolved = ResolveIdleCycleInternal(
            setup, dats, animationLoader,
            motionTableIdOverride, stanceOverride, commandOverride);
        if (resolved is null) return null;
        var (animation, animData) = resolved.Value;
        int frameIdx = animData.LowFrame;
        if (frameIdx < 0 || frameIdx >= animation.PartFrames.Count)
            frameIdx = 0;
        return animation.PartFrames[frameIdx];
    }

    private static (Animation, AnimData)? ResolveIdleCycleInternal(
        Setup setup,
        IDatObjectSource dats,
        IAnimationLoader animationLoader,
        uint? motionTableIdOverride,
        ushort? stanceOverride,
        ushort? commandOverride)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(animationLoader);

        uint mtableId = motionTableIdOverride ?? (uint)setup.DefaultMotionTable;
        if (mtableId == 0) return null;

        var mtable = dats.Get<MotionTable>(mtableId);
        if (mtable is null) return null;

        uint styleVal;
        uint substateVal;

        bool TryGetTableDefault(out uint styleOut, out uint substateOut)
        {
            if (mtable.StyleDefaults.TryGetValue(mtable.DefaultStyle, out var defaultSubstate))
            {
                styleOut = (uint)mtable.DefaultStyle;
                substateOut = (uint)defaultSubstate;
                return true;
            }
            styleOut = 0;
            substateOut = 0;
            return false;
        }

        if (stanceOverride is { } stance && stance != 0)
        {
            styleVal = stance;
            if (commandOverride is { } cmd && cmd != 0)
            {
                substateVal = cmd;
            }
            else if (mtable.StyleDefaults.TryGetValue((DatReaderWriter.Enums.MotionCommand)styleVal, out var subFromStyle))
            {
                substateVal = (uint)subFromStyle;
            }
            else
            {
                if (!TryGetTableDefault(out styleVal, out substateVal))
                    return null;
            }
        }
        else
        {
            if (!TryGetTableDefault(out styleVal, out substateVal))
                return null;
        }

        int cycleKey = (int)((styleVal << 16) | (substateVal & 0xFFFFFF));

        if (!mtable.Cycles.TryGetValue(cycleKey, out var motionData) || motionData is null
            || motionData.Anims.Count == 0)
        {
            if (mtable.StyleDefaults.TryGetValue(mtable.DefaultStyle, out var fallbackSub))
            {
                int fallbackKey = (int)(((uint)mtable.DefaultStyle << 16) | ((uint)fallbackSub & 0xFFFFFF));
                if (!mtable.Cycles.TryGetValue(fallbackKey, out motionData) || motionData is null)
                    return null;
                if (motionData.Anims.Count == 0) return null;
            }
            else
            {
                return null;
            }
        }

        var animData = motionData.Anims[0];

        uint animId = (uint)animData.AnimId;
        if (animId == 0) return null;

        var animation = animationLoader.LoadAnimation(animId);
        if (animation is null) return null;
        if (animation.PartFrames.Count == 0) return null;

        return (animation, animData);
    }
}
