using System.Numerics;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering.Vfx;

internal static class IndexedSetupPartPoseBuilder
{
    public static (Matrix4x4[] Poses, bool[] Available) Build(
        Setup setup,
        WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(entity);

        int partCount = setup.Parts.Count;
        var poses = new Matrix4x4[partCount];
        IReadOnlyList<Matrix4x4> defaults = SetupPartTransforms.Compute(
            setup,
            objectScale: entity.Scale);
        for (int i = 0; i < partCount; i++)
            poses[i] = i < defaults.Count ? defaults[i] : Matrix4x4.Identity;

        var expectedGfx = new uint[partCount];
        for (int i = 0; i < partCount; i++)
            expectedGfx[i] = (uint)setup.Parts[i];
        for (int i = 0; i < entity.PartOverrides.Count; i++)
        {
            PartOverride replacement = entity.PartOverrides[i];
            if (replacement.PartIndex < expectedGfx.Length)
                expectedGfx[replacement.PartIndex] = replacement.GfxObjId;
        }

        var available = new bool[partCount];
        var consumed = new bool[entity.MeshRefs.Count];
        for (int partIndex = 0; partIndex < partCount; partIndex++)
        {
            for (int drawableIndex = 0; drawableIndex < entity.MeshRefs.Count; drawableIndex++)
            {
                if (consumed[drawableIndex]
                    || entity.MeshRefs[drawableIndex].GfxObjId != expectedGfx[partIndex])
                {
                    continue;
                }

                consumed[drawableIndex] = true;
                available[partIndex] = true;
                break;
            }
        }

        return (poses, available);
    }
}
