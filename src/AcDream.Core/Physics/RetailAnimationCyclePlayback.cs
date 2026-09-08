using System;
using System.Numerics;
using DatReaderWriter.DBObjs;

namespace AcDream.Core.Physics;

public static class RetailAnimationCyclePlayback
{
    public static float Advance(
        float currFrame,
        int lowFrame,
        int highFrame,
        float framerate,
        float elapsedSeconds)
    {
        int span = highFrame - lowFrame;
        if (span <= 0 || elapsedSeconds <= 0f)
            return currFrame;

        float next = currFrame + elapsedSeconds * framerate;
        if (next > highFrame)
        {
            float over = next - lowFrame;
            next = lowFrame + (over % (span + 1));
        }
        else if (next < lowFrame)
        {
            next = lowFrame;
        }
        return next;
    }

    public static bool TryInterpolatePart(
        Animation animation,
        float currFrame,
        int lowFrame,
        int highFrame,
        int partIndex,
        out Vector3 origin,
        out Quaternion orientation)
    {
        ArgumentNullException.ThrowIfNull(animation);

        int frameIndex = (int)MathF.Floor(currFrame);
        if (frameIndex < lowFrame || frameIndex > highFrame || frameIndex >= animation.PartFrames.Count)
            frameIndex = lowFrame;
        int nextIndex = frameIndex + 1;
        if (nextIndex > highFrame || nextIndex >= animation.PartFrames.Count)
            nextIndex = lowFrame;
        float t = Math.Clamp(currFrame - frameIndex, 0f, 1f);

        var frames = animation.PartFrames[frameIndex].Frames;
        var nextFrames = animation.PartFrames[nextIndex].Frames;
        if (partIndex < frames.Count)
        {
            var first = frames[partIndex];
            var next = partIndex < nextFrames.Count ? nextFrames[partIndex] : first;
            origin = Vector3.Lerp(first.Origin, next.Origin, t);
            orientation = Quaternion.Slerp(first.Orientation, next.Orientation, t);
            return true;
        }

        origin = default;
        orientation = default;
        return false;
    }
}
