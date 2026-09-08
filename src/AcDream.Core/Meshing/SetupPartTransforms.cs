using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Meshing;

public static class SetupPartTransforms
{
    public static IReadOnlyList<Matrix4x4> Compute(
        Setup setup,
        AnimationFrame? motionFrameOverride = null,
        float objectScale = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(setup);
        AnimationFrame? source = motionFrameOverride;
        if (source is null && setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting))
        {
            source = resting;
        }
        else if (source is null && setup.PlacementFrames.TryGetValue(Placement.Default, out var def))
        {
            source = def;
        }
        else if (source is null)
        {
            foreach (var kvp in setup.PlacementFrames)
            {
                source = kvp.Value;
                break;
            }
        }

        if (source is null)
            return Array.Empty<Matrix4x4>();

        int partCount = setup.Parts.Count;
        var result = new Matrix4x4[partCount];
        for (int i = 0; i < partCount; i++)
        {
            Frame frame = i < source.Frames.Count
                ? source.Frames[i]
                : new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
            result[i] = Matrix4x4.CreateFromQuaternion(frame.Orientation)
                      * Matrix4x4.CreateTranslation(frame.Origin * objectScale);
        }
        return result;
    }
}
