using System.Numerics;

namespace AcDream.Core.World;

public readonly record struct MeshRef(uint GfxObjId, Matrix4x4 PartTransform)
{
    public IReadOnlyDictionary<uint, uint>? SurfaceOverrides { get; init; }
}
