using System.Numerics;

namespace AcDream.Core.Terrain;

public readonly record struct TerrainVertex(
    Vector3 Position,
    Vector3 Normal,
    uint Data0,
    uint Data1,
    uint Data2,
    uint Data3);
