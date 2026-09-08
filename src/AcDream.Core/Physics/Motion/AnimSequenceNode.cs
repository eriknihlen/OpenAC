using System;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics.Motion;

public sealed class AnimSequenceNode
{
    /// <summary>Resolved dat animation, or null (id 0 / missing).</summary>
    public Animation? Anim { get; private set; }

    public float Framerate = 30f;

    public int LowFrame = -1;

    public int HighFrame = -1;

    public bool HasAnim => Anim is not null;

    public AnimSequenceNode()
    {
    }

    public AnimSequenceNode(AnimData animData, IAnimationLoader loader)
    {
        Framerate = animData.Framerate;
        LowFrame = animData.LowFrame;
        HighFrame = animData.HighFrame;
        SetAnimationId((uint)animData.AnimId, loader);
    }

    public void SetAnimationId(uint animId, IAnimationLoader loader)
    {
        Anim = animId == 0 ? null : loader.LoadAnimation(animId);

        if (Anim is null)
            return;

        int numFrames = Anim.PartFrames.Count;

        if (HighFrame < 0)
            HighFrame = numFrames - 1;
        if (LowFrame >= numFrames)
            LowFrame = numFrames - 1;
        if (HighFrame >= numFrames)
            HighFrame = numFrames - 1;
        if (LowFrame > HighFrame)
            HighFrame = LowFrame;
    }

    public int GetStartingFrame() => Framerate < 0f ? HighFrame + 1 : LowFrame;

    public int GetEndingFrame() => Framerate < 0f ? LowFrame : HighFrame + 1;

    public void MultiplyFramerate(float factor)
    {
        if (factor < 0f)
        {
            (LowFrame, HighFrame) = (HighFrame, LowFrame);
        }
        Framerate *= factor;
    }

    public Frame? GetPosFrame(double frameNumber)
        => GetPosFrame((int)Math.Floor(frameNumber));

    public Frame? GetPosFrame(int index)
    {
        if (Anim is null || index < 0 || index >= Anim.PartFrames.Count)
            return null;
        if (Anim.PosFrames is null || index >= Anim.PosFrames.Count)
            return null;
        return Anim.PosFrames[index];
    }

    public AnimationFrame? GetPartFrame(int index)
    {
        if (Anim is null || index < 0 || index >= Anim.PartFrames.Count)
            return null;
        return Anim.PartFrames[index];
    }
}
