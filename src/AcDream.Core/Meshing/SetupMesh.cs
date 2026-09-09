using System.Numerics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Meshing;

public static class SetupMesh
{
    public static IReadOnlyList<MeshRef> Flatten(Setup setup, AnimationFrame? motionFrameOverride = null)
    {
        AnimationFrame? defaultAnim = motionFrameOverride;
        if (defaultAnim is null && setup.PlacementFrames.TryGetValue(Placement.Resting, out var resting))
            defaultAnim = resting;
        if (defaultAnim is null && setup.PlacementFrames.TryGetValue(Placement.Default, out var af))
            defaultAnim = af;
        if (defaultAnim is null)
        {
            foreach (var kvp in setup.PlacementFrames)
            {
                defaultAnim = kvp.Value;
                break;
            }
        }

        var result = new List<MeshRef>(setup.Parts.Count);
        for (int i = 0; i < setup.Parts.Count; i++)
        {
            uint gfxObjId = (uint)setup.Parts[i];

            Frame frame;
            if (defaultAnim is not null && i < defaultAnim.Frames.Count)
                frame = defaultAnim.Frames[i];
            else
                frame = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };

            Vector3 scale = i < setup.DefaultScale.Count ? setup.DefaultScale[i] : Vector3.One;

            var transform =
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateFromQuaternion(frame.Orientation) *
                Matrix4x4.CreateTranslation(frame.Origin);

            result.Add(new MeshRef(gfxObjId, transform));
        }
        return result;
    }
}
