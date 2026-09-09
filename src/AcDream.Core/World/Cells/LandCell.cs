using System;
using System.Numerics;
using AcDream.Core.Physics;   // TerrainSurface

namespace AcDream.Core.World.Cells;

public sealed class LandCell : ObjCell
{
    public const float CellSize = 24f;

    public TerrainSurface Terrain { get; }
    public int Cx { get; }
    public int Cy { get; }
    public uint? BuildingCellId { get; }

    private readonly Vector3 _worldOrigin;

    private LandCell(uint id, TerrainSurface terrain, Vector3 worldOrigin, int cx, int cy,
                     Matrix4x4 worldTransform, Matrix4x4 inverseWorldTransform,
                     Vector3 localBoundsMin, Vector3 localBoundsMax)
        : base(id, worldTransform, inverseWorldTransform, localBoundsMin, localBoundsMax,
               Array.Empty<CellPortal>(), Array.Empty<uint>(), seenOutside: false)
    {
        Terrain = terrain; Cx = cx; Cy = cy; _worldOrigin = worldOrigin; BuildingCellId = null;
    }

    public static LandCell Synthesize(uint id, TerrainSurface terrain, Vector3 worldOrigin, int cx, int cy)
    {
        float ox = cx * CellSize, oy = cy * CellSize;
        float z0 = terrain.SampleZ(ox, oy),              z1 = terrain.SampleZ(ox + CellSize, oy);
        float z2 = terrain.SampleZ(ox, oy + CellSize),   z3 = terrain.SampleZ(ox + CellSize, oy + CellSize);
        float zMin = MathF.Min(MathF.Min(z0, z1), MathF.Min(z2, z3));
        float zMax = MathF.Max(MathF.Max(z0, z1), MathF.Max(z2, z3));
        var min = new Vector3(ox, oy, zMin);
        var max = new Vector3(ox + CellSize, oy + CellSize, zMax);

        var transform = Matrix4x4.CreateTranslation(worldOrigin);
        Matrix4x4.Invert(transform, out var inverse);
        return new LandCell(id, terrain, worldOrigin, cx, cy, transform, inverse, min, max);
    }

    public override bool PointInCell(Vector3 worldPoint)
    {
        float lx = worldPoint.X - _worldOrigin.X;
        float ly = worldPoint.Y - _worldOrigin.Y;
        return lx >= Cx * CellSize && lx < (Cx + 1) * CellSize
            && ly >= Cy * CellSize && ly < (Cy + 1) * CellSize;
    }
}
