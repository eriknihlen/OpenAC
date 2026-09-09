using System.Numerics;

namespace AcDream.Core.Terrain;

public readonly record struct Vertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    uint TerrainLayer);
