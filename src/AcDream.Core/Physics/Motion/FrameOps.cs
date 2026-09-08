using System;
using System.Numerics;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics.Motion;

public static class FrameOps
{
    public const float FEpsilon = 0.000199999995f;

    public static Quaternion SetRotate(
        Vector3 frameOrigin,
        Quaternion previous,
        Quaternion candidate)
    {
        double lengthSquared =
            ((double)candidate.W * candidate.W)
            + ((double)candidate.X * candidate.X)
            + ((double)candidate.Y * candidate.Y)
            + ((double)candidate.Z * candidate.Z);
        double inverseLength = 1.0 / Math.Sqrt(lengthSquared);
        var normalized = new Quaternion(
            (float)(candidate.X * inverseLength),
            (float)(candidate.Y * inverseLength),
            (float)(candidate.Z * inverseLength),
            (float)(candidate.W * inverseLength));

        if (float.IsNaN(frameOrigin.X)
            || float.IsNaN(frameOrigin.Y)
            || float.IsNaN(frameOrigin.Z)
            || float.IsNaN(normalized.W)
            || float.IsNaN(normalized.X)
            || float.IsNaN(normalized.Y)
            || float.IsNaN(normalized.Z))
        {
            return previous;
        }

        float normalizedLengthSquared = normalized.LengthSquared();
        return !float.IsNaN(normalizedLengthSquared)
               && MathF.Abs(normalizedLengthSquared - 1f) < FEpsilon * 5f
            ? normalized
            : previous;
    }

    public static void GRotate(Frame frame, Vector3 rotationGlobal)
    {
        float magSq = rotationGlobal.LengthSquared();
        if (magSq < FEpsilon * FEpsilon)
            return;

        float angle = MathF.Sqrt(magSq);
        float invMag = 1f / angle;
        float half = angle * 0.5f;
        float s = MathF.Sin(half);
        float c = MathF.Cos(half);

        var r = new Quaternion(
            rotationGlobal.X * s * invMag,
            rotationGlobal.Y * s * invMag,
            rotationGlobal.Z * s * invMag,
            c);
        frame.Orientation = SetRotate(
            frame.Origin,
            frame.Orientation,
            Quaternion.Multiply(r, frame.Orientation));
    }

    public static void Rotate(Frame frame, Vector3 rotationLocal)
        => GRotate(frame, Vector3.Transform(rotationLocal, frame.Orientation));

    public static void Combine(Frame frame, Frame pos)
    {
        frame.Origin += Vector3.Transform(pos.Origin, frame.Orientation);
        frame.Orientation = SetRotate(
            frame.Origin,
            frame.Orientation,
            frame.Orientation * pos.Orientation);
    }

    public static void Subtract1(Frame frame, Frame pos)
    {
        frame.Orientation = SetRotate(
            frame.Origin,
            frame.Orientation,
            frame.Orientation * Quaternion.Conjugate(pos.Orientation));
        frame.Origin -= Vector3.Transform(pos.Origin, frame.Orientation);
    }
}
