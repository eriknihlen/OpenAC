using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering;

public readonly struct ClipPlaneSet
{
    private const int MaxPlanes = 8;

    private const float CollinearSinEps = 0.0087265f;

    // Drop a vertex whose two incident edges are shorter than this (NDC) — a duplicate
    // or near-duplicate point that would otherwise yield a garbage normalized normal.
    private const float DegenerateEdgeLen = 1e-6f;

    private const float MinPolygonArea = 1e-7f;
    private readonly Vector4[] _planes;

    private ClipPlaneSet(Vector4[] planes, bool isPlaneOverflow, bool isNothingVisible)
    {
        _planes = planes ?? Array.Empty<Vector4>();
        IsPlaneOverflow = isPlaneOverflow;
        IsNothingVisible = isNothingVisible;
    }

    public int Count => _planes?.Length ?? 0;

    public IReadOnlyList<Vector4> Planes => _planes ?? (IReadOnlyList<Vector4>)Array.Empty<Vector4>();

    internal Vector4[] PlaneArray => _planes ?? Array.Empty<Vector4>();

    public bool IsPlaneOverflow { get; }

    public bool IsNothingVisible { get; }

    public static ClipPlaneSet Empty { get; } =
        new(Array.Empty<Vector4>(), isPlaneOverflow: false, isNothingVisible: true);

    public static ClipPlaneSet From(CellView region)
    {
        if (region is null || region.IsEmpty || region.Polygons.Count == 0)
            return Empty;

        if (region.Polygons.Count > 1)
            return Overflow();

        return From(region.Polygons[0]);
    }

    public static ClipPlaneSet From(in ViewPolygon polygon)
    {
        if (polygon.IsEmpty)
            return Empty;

        Vector2[] input = polygon.Vertices;
        Vector2[]? rented = null;
        Span<Vector2> verts = input.Length <= 32
            ? stackalloc Vector2[input.Length]
            : (rented = ArrayPool<Vector2>.Shared.Rent(input.Length)).AsSpan(0, input.Length);

        try
        {
            int count = NormalizeAndMerge(input, verts);

            // Fewer than 3 distinct edges survive ⇒ a sliver/line with no area ⇒ nothing visible.
            if (count < 3)
                return Empty;

            ReadOnlySpan<Vector2> normalized = verts[..count];

            if (count > MaxPlanes)
                return Overflow();

            // 3..8 edges: emit one inward half-space plane per edge (CCW formula). This array is
            // the retained GPU-routing payload and therefore the one necessary allocation.
            var planes = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                Vector2 p = normalized[i];
                Vector2 q = normalized[(i + 1) % count];
                Vector2 dir = q - p;
                // Inward normal for CCW winding: perp(dir) = (-dir.y, dir.x) points to the polygon's
                // interior (the "left" side of the directed edge p→q).
                Vector2 n = Vector2.Normalize(new Vector2(-dir.Y, dir.X));
                planes[i] = new Vector4(n.X, n.Y, 0f, -Vector2.Dot(n, p));
            }
            return new ClipPlaneSet(planes, isPlaneOverflow: false, isNothingVisible: false);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Vector2>.Shared.Return(rented);
        }
    }

    private static ClipPlaneSet Overflow() =>
        new(Array.Empty<Vector4>(), isPlaneOverflow: true, isNothingVisible: false);

    private static int NormalizeAndMerge(ReadOnlySpan<Vector2> input, Span<Vector2> points)
    {
        if (input.Length < 3)
            return 0;

        int count = 0;
        foreach (Vector2 vertex in input)
        {
            if (count == 0 || (vertex - points[count - 1]).LengthSquared() > DegenerateEdgeLen * DegenerateEdgeLen)
                points[count++] = vertex;
        }
        // Wrap-around duplicate (last == first).
        if (count >= 2 && (points[count - 1] - points[0]).LengthSquared() <= DegenerateEdgeLen * DegenerateEdgeLen)
            count--;
        if (count < 3)
            return 0;

        if (SignedArea2(points[..count]) < 0f)
            points[..count].Reverse();

        bool changed = true;
        while (changed && count >= 3)
        {
            changed = false;
            for (int i = 0; i < count; i++)
            {
                Vector2 prev = points[(i - 1 + count) % count];
                Vector2 cur = points[i];
                Vector2 next = points[(i + 1) % count];

                Vector2 d0 = cur - prev;
                Vector2 d1 = next - cur;
                float l0 = d0.Length();
                float l1 = d1.Length();
                if (l0 < DegenerateEdgeLen || l1 < DegenerateEdgeLen)
                {
                    points[(i + 1)..count].CopyTo(points[i..]);
                    count--;
                    changed = true;
                    break;
                }

                d0 /= l0;
                d1 /= l1;
                float cross = d0.X * d1.Y - d0.Y * d1.X; // sin θ
                float dot = d0.X * d1.X + d0.Y * d1.Y;   // cos θ
                if (dot > 0f && MathF.Abs(cross) < CollinearSinEps)
                {
                    points[(i + 1)..count].CopyTo(points[i..]);
                    count--; // cur lies on the straight line prev→next
                    changed = true;
                    break;
                }
            }
        }

        if (count < 3)
            return 0;

        if (MathF.Abs(SignedArea2(points[..count])) * 0.5f < MinPolygonArea)
            return 0;

        return count;
    }

    // Twice the signed area (the "shoelace" sum). > 0 ⇒ CCW, < 0 ⇒ CW.
    private static float SignedArea2(ReadOnlySpan<Vector2> poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 p = poly[i];
            Vector2 q = poly[(i + 1) % poly.Length];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a;
    }
}
