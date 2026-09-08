using System.Buffers;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering;

public readonly struct ViewPolygon
{
    public readonly Vector2[] Vertices;
    public readonly float MinX, MinY, MaxX, MaxY;

    public ViewPolygon(Vector2[] vertices)
    {
        Vertices = vertices;
        if (vertices is null || vertices.Length < 3)
        {
            MinX = MinY = MaxX = MaxY = 0f;
            return;
        }
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var v in vertices)
        {
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public bool IsEmpty => Vertices is null || Vertices.Length < 3;
}

internal sealed class PortalPolygonVertexStore
{
    private const int MaxRetainedArrays = 4_096;
    private const int MaxRetainedVertices = 65_536;
    private const int MaxRetainedPolygonVertices = 256;

    private sealed class Bucket
    {
        public readonly List<Vector2[]> Buffers = new(4);
        public int Used;
    }

    private readonly Dictionary<int, Bucket> _buckets = new();
    private int _retainedArrays;
    private int _retainedVertices;

    internal int AllocationCount { get; private set; }
    internal int RetainedArrayCount => _retainedArrays;

    internal Vector2[] Rent(int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(vertexCount, 1);

        if (_buckets.TryGetValue(vertexCount, out Bucket? bucket)
            && bucket.Used < bucket.Buffers.Count)
        {
            return bucket.Buffers[bucket.Used++];
        }

        Vector2[] result = GC.AllocateUninitializedArray<Vector2>(vertexCount);
        AllocationCount++;

        bool retain = vertexCount <= MaxRetainedPolygonVertices
            && _retainedArrays < MaxRetainedArrays
            && _retainedVertices + vertexCount <= MaxRetainedVertices;
        if (!retain)
            return result;

        if (bucket is null)
        {
            bucket = new Bucket();
            _buckets.Add(vertexCount, bucket);
        }
        bucket.Buffers.Add(result);
        bucket.Used++;
        _retainedArrays++;
        _retainedVertices += vertexCount;
        return result;
    }

    internal void ResetUsage()
    {
        foreach (Bucket bucket in _buckets.Values)
            bucket.Used = 0;
    }
}

public sealed class CellView
{
    private readonly ViewPolygon _fullScreenPolygon = new(new[]
    {
        new Vector2(-1f, -1f),
        new Vector2(1f, -1f),
        new Vector2(1f, 1f),
        new Vector2(-1f, 1f),
    });

    public readonly List<ViewPolygon> Polygons = new();

    private readonly Dictionary<int, int> _polygonKeyHeads = new();
    private readonly List<PolygonKey> _polygonKeyStorage = new();
    private int _activePolygonKeyCount;
    public float MinX { get; private set; } = float.MaxValue;
    public float MinY { get; private set; } = float.MaxValue;
    public float MaxX { get; private set; } = float.MinValue;
    public float MaxY { get; private set; } = float.MinValue;

    public bool IsEmpty => Polygons.Count == 0;

    internal bool IsRetainable
    {
        get
        {
            if (Polygons.Capacity > 256
                || _polygonKeyHeads.EnsureCapacity(0) > 512
                || _polygonKeyStorage.Count > 512)
            {
                return false;
            }

            int retainedCoordinateInts = 0;
            for (int i = 0; i < _polygonKeyStorage.Count; i++)
            {
                int length = _polygonKeyStorage[i].Coordinates.Length;
                if (length > 256)
                    return false;
                retainedCoordinateInts += length;
                if (retainedCoordinateInts > 8192)
                    return false;
            }
            return true;
        }
    }

    internal void Reset()
    {
        Polygons.Clear();
        _polygonKeyHeads.Clear();
        _activePolygonKeyCount = 0;
        MinX = float.MaxValue;
        MinY = float.MaxValue;
        MaxX = float.MinValue;
        MaxY = float.MinValue;
    }

    public static CellView FullScreen()
    {
        var v = new CellView();
        v.Add(v._fullScreenPolygon);
        return v;
    }

    internal void SetFullScreen()
    {
        Reset();
        Add(_fullScreenPolygon);
    }

    public bool Add(ViewPolygon p)
    {
        if (p.IsEmpty) return false;

        CanonicalKeyResult keyResult = TryAddCanonicalKey(p.Vertices);
        if (keyResult == CanonicalKeyResult.Degenerate) return false;
        if (keyResult == CanonicalKeyResult.Duplicate) return false;

        if (ContainedInExisting(p)) return false;

        Polygons.Add(p);
        if (p.MinX < MinX) MinX = p.MinX;
        if (p.MinY < MinY) MinY = p.MinY;
        if (p.MaxX > MaxX) MaxX = p.MaxX;
        if (p.MaxY > MaxY) MaxY = p.MaxY;
        return true;
    }

    private bool ContainedInExisting(in ViewPolygon p)
    {
        const float eps = DedupGridNdc;
        for (int i = 0; i < Polygons.Count; i++)
        {
            var e = Polygons[i];
            // bounding-rect quick reject (with slack)
            if (p.MinX < e.MinX - eps || p.MaxX > e.MaxX + eps
                || p.MinY < e.MinY - eps || p.MaxY > e.MaxY + eps)
                continue;
            if (ContainsAllVertices(e.Vertices, p.Vertices, eps))
                return true;
        }
        return false;
    }

    private static bool ContainsAllVertices(Vector2[] convex, Vector2[] pts, float eps)
    {
        if (convex.Length < 3) return false;

        // signed area → winding (CCW positive); inside = left of every CCW edge.
        float area2 = 0f;
        for (int i = 0; i < convex.Length; i++)
        {
            var a = convex[i];
            var b = convex[(i + 1) % convex.Length];
            area2 += a.X * b.Y - b.X * a.Y;
        }
        float sign = area2 >= 0f ? 1f : -1f;

        for (int i = 0; i < convex.Length; i++)
        {
            var a = convex[i];
            var b = convex[(i + 1) % convex.Length];
            var ab = b - a;
            float len = ab.Length();
            if (len < 1e-9f) continue;
            foreach (var pt in pts)
            {
                // signed perpendicular distance of pt from edge a→b (positive = inside for CCW)
                float cross = sign * (ab.X * (pt.Y - a.Y) - ab.Y * (pt.X - a.X));
                if (cross < -eps * len)
                    return false;        // a vertex lies outside this edge by more than eps
            }
        }
        return true;
    }

    private const float DedupGridNdc = 1e-3f;

    private CanonicalKeyResult TryAddCanonicalKey(Vector2[]? verts)
    {
        if (verts is null || verts.Length < 3)
            return CanonicalKeyResult.Degenerate;

        SnappedPoint[]? rented = null;
        Span<SnappedPoint> points = verts.Length <= 32
            ? stackalloc SnappedPoint[verts.Length]
            : (rented = ArrayPool<SnappedPoint>.Shared.Rent(verts.Length)).AsSpan(0, verts.Length);

        try
        {
            int count = 0;
            foreach (Vector2 vertex in verts)
            {
                var point = new SnappedPoint(
                    (int)MathF.Round(vertex.X / DedupGridNdc),
                    (int)MathF.Round(vertex.Y / DedupGridNdc));
                if (count == 0 || points[count - 1] != point)
                    points[count++] = point;
            }
            if (count >= 2 && points[count - 1] == points[0])
                count--;
            if (count < 2)
                return CanonicalKeyResult.Degenerate;

            SnappedPoint lo = points[0];
            SnappedPoint hi = points[0];
            for (int i = 1; i < count; i++)
            {
                SnappedPoint point = points[i];
                if (point.X < lo.X || (point.X == lo.X && point.Y < lo.Y)) lo = point;
                if (point.X > hi.X || (point.X == hi.X && point.Y > hi.Y)) hi = point;
            }

            bool removed = true;
            while (removed && count >= 3)
            {
                removed = false;
                for (int i = 0; i < count && count >= 3; i++)
                {
                    SnappedPoint previous = points[(i + count - 1) % count];
                    SnappedPoint current = points[i];
                    SnappedPoint next = points[(i + 1) % count];
                    long cross = (long)(current.X - previous.X) * (next.Y - current.Y)
                               - (long)(current.Y - previous.Y) * (next.X - current.X);
                    if (cross != 0)
                        continue;

                    points.Slice(i + 1, count - i - 1).CopyTo(points.Slice(i));
                    count--;
                    removed = true;
                    i--;
                }
            }

            if (count < 3)
            {
                if (lo == hi)
                    return CanonicalKeyResult.Degenerate;
                Span<SnappedPoint> segment = stackalloc SnappedPoint[2] { lo, hi };
                return AddCanonicalKey(PolygonKeyKind.Line, segment, start: 0);
            }

            int best = 0;
            for (int start = 1; start < count; start++)
                if (RotationLess(points, start, best, count)) best = start;

            return AddCanonicalKey(PolygonKeyKind.Polygon, points[..count], best);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<SnappedPoint>.Shared.Return(rented);
        }
    }

    private CanonicalKeyResult AddCanonicalKey(
        PolygonKeyKind kind,
        ReadOnlySpan<SnappedPoint> points,
        int start)
    {
        int hash = ComputeHash(kind, points, start);
        if (_polygonKeyHeads.TryGetValue(hash, out int keyIndex))
        {
            while (keyIndex >= 0)
            {
                PolygonKey existing = _polygonKeyStorage[keyIndex];
                if (existing.Equals(kind, points, start))
                    return CanonicalKeyResult.Duplicate;
                keyIndex = existing.Next;
            }
        }

        int coordinateCount = points.Length * 2;
        int storageIndex = FindOrCreateCoordinateStorage(coordinateCount);
        int[] coordinates = _polygonKeyStorage[storageIndex].Coordinates;
        for (int i = 0; i < points.Length; i++)
        {
            SnappedPoint point = points[(start + i) % points.Length];
            coordinates[i * 2] = point.X;
            coordinates[i * 2 + 1] = point.Y;
        }
        int next = _polygonKeyHeads.GetValueOrDefault(hash, -1);
        _polygonKeyStorage[storageIndex] = new PolygonKey(kind, coordinates, next);
        _polygonKeyHeads[hash] = storageIndex;
        _activePolygonKeyCount++;
        return CanonicalKeyResult.Added;
    }

    private int FindOrCreateCoordinateStorage(int coordinateCount)
    {
        int storageIndex = _activePolygonKeyCount;
        for (int i = storageIndex; i < _polygonKeyStorage.Count; i++)
        {
            if (_polygonKeyStorage[i].Coordinates.Length != coordinateCount)
                continue;
            if (i != storageIndex)
                (_polygonKeyStorage[storageIndex], _polygonKeyStorage[i]) =
                    (_polygonKeyStorage[i], _polygonKeyStorage[storageIndex]);
            return storageIndex;
        }

        _polygonKeyStorage.Add(new PolygonKey(
            PolygonKeyKind.Polygon,
            new int[coordinateCount],
            -1));
        int addedIndex = _polygonKeyStorage.Count - 1;
        if (addedIndex != storageIndex)
            (_polygonKeyStorage[storageIndex], _polygonKeyStorage[addedIndex]) =
                (_polygonKeyStorage[addedIndex], _polygonKeyStorage[storageIndex]);
        return storageIndex;
    }

    private static int ComputeHash(
        PolygonKeyKind kind,
        ReadOnlySpan<SnappedPoint> points,
        int start)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (byte)kind) * 16777619u;
            hash = (hash ^ (uint)points.Length) * 16777619u;
            for (int i = 0; i < points.Length; i++)
            {
                SnappedPoint point = points[(start + i) % points.Length];
                hash = (hash ^ (uint)point.X) * 16777619u;
                hash = (hash ^ (uint)point.Y) * 16777619u;
            }
            return (int)hash;
        }
    }

    private static bool RotationLess(
        ReadOnlySpan<SnappedPoint> points,
        int a,
        int b,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            SnappedPoint left = points[(a + i) % count];
            SnappedPoint right = points[(b + i) % count];
            if (left.X != right.X) return left.X < right.X;
            if (left.Y != right.Y) return left.Y < right.Y;
        }
        return false;
    }

    private readonly record struct SnappedPoint(int X, int Y);

    private readonly record struct PolygonKey(PolygonKeyKind Kind, int[] Coordinates, int Next)
    {
        public bool Equals(PolygonKeyKind kind, ReadOnlySpan<SnappedPoint> points, int start)
        {
            if (Kind != kind || Coordinates.Length != points.Length * 2)
                return false;
            for (int i = 0; i < points.Length; i++)
            {
                SnappedPoint point = points[(start + i) % points.Length];
                if (Coordinates[i * 2] != point.X || Coordinates[i * 2 + 1] != point.Y)
                    return false;
            }
            return true;
        }
    }

    private enum PolygonKeyKind : byte { Polygon, Line }
    private enum CanonicalKeyResult : byte { Degenerate, Duplicate, Added }
}
