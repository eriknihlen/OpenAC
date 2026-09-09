using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Physics;

public sealed class CellSurface
{
    public uint CellId { get; }

    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _triangles;

    public CellSurface(
        uint cellId,
        Dictionary<ushort, Vector3> vertices,
        List<List<short>> polygonVertexIds)
    {
        CellId = cellId;
        _triangles = new List<(Vector3, Vector3, Vector3)>();

        foreach (var polyVerts in polygonVertexIds)
        {
            if (polyVerts.Count < 3) continue;

            // Resolve vertex positions.
            var positions = new List<Vector3>(polyVerts.Count);
            bool skip = false;
            foreach (var vid in polyVerts)
            {
                if (!vertices.TryGetValue((ushort)vid, out var pos))
                {
                    skip = true;
                    break;
                }
                positions.Add(pos);
            }
            if (skip) continue;

            // Fan triangulation: (v0, v1, v2), (v0, v2, v3), ...
            for (int i = 1; i < positions.Count - 1; i++)
            {
                _triangles.Add((positions[0], positions[i], positions[i + 1]));
            }
        }
    }

    public CellSurface(
        uint cellId,
        FlatPolygonTable polygons,
        Quaternion rotation,
        Vector3 translation)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        CellId = cellId;
        int triangleCount = 0;
        for (int i = 0; i < polygons.Polygons.Length; i++)
            triangleCount += Math.Max(0, polygons.Polygons[i].VertexRange.Count - 2);
        _triangles = new List<(Vector3, Vector3, Vector3)>(triangleCount);

        for (int polygonIndex = 0;
            polygonIndex < polygons.Polygons.Length;
            polygonIndex++)
        {
            FlatIndexRange range =
                polygons.Polygons[polygonIndex].VertexRange;
            if (range.Count < 3)
                continue;

            Vector3 first =
                Vector3.Transform(polygons.Vertices[range.Start], rotation)
                + translation;
            Vector3 previous =
                Vector3.Transform(polygons.Vertices[range.Start + 1], rotation)
                + translation;
            for (int vertexOffset = 2;
                vertexOffset < range.Count;
                vertexOffset++)
            {
                Vector3 current = Vector3.Transform(
                    polygons.Vertices[range.Start + vertexOffset],
                    rotation) + translation;
                _triangles.Add((first, previous, current));
                previous = current;
            }
        }
    }

    public float? SampleFloorZ(float worldX, float worldY)
    {
        foreach (var (a, b, c) in _triangles)
        {
            if (PointInTriangleXY(worldX, worldY, a, b, c, out float z))
                return z;
        }
        return null;
    }


    private static bool PointInTriangleXY(
        float px, float py,
        Vector3 a, Vector3 b, Vector3 c,
        out float z)
    {
        z = 0;

        float v0x = c.X - a.X, v0y = c.Y - a.Y;
        float v1x = b.X - a.X, v1y = b.Y - a.Y;
        float v2x = px - a.X, v2y = py - a.Y;

        float dot00 = v0x * v0x + v0y * v0y;
        float dot01 = v0x * v1x + v0y * v1y;
        float dot02 = v0x * v2x + v0y * v2y;
        float dot11 = v1x * v1x + v1y * v1y;
        float dot12 = v1x * v2x + v1y * v2y;

        float denom = dot00 * dot11 - dot01 * dot01;
        if (MathF.Abs(denom) < 1e-10f) return false;  // degenerate triangle

        float invDenom = 1f / denom;
        float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
        float v = (dot00 * dot12 - dot01 * dot02) * invDenom;

        if (u < -1e-6f || v < -1e-6f || u + v > 1f + 1e-6f)
            return false;

        // Barycentric Z interpolation.
        z = a.Z * (1 - u - v) + b.Z * v + c.Z * u;
        return true;
    }
}
