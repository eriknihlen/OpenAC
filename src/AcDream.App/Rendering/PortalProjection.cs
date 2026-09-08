using System.Buffers;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering;

public static class PortalProjection
{
    internal ref struct ClipPolygonLease
    {
        private readonly ArrayPool<Vector4>? _pool;
        private Vector4[]? _first;
        private Vector4[]? _second;
        private Vector4[]? _result;
        private readonly int _count;
        private bool _disposed;

        internal ClipPolygonLease(
            ArrayPool<Vector4>? pool,
            Vector4[]? first,
            Vector4[]? second,
            Vector4[]? result,
            int count)
        {
            _pool = pool;
            _first = first;
            _second = second;
            _result = result;
            _count = count;
            _disposed = false;
        }

        public int Count
        {
            get
            {
                ThrowIfDisposed();
                return _count;
            }
        }

        public ReadOnlySpan<Vector4> Span
        {
            get
            {
                ThrowIfDisposed();
                return _result is null
                    ? ReadOnlySpan<Vector4>.Empty
                    : _result.AsSpan(0, _count);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ArrayPool<Vector4>? pool = _pool;
            if (_first is not null)
                pool!.Return(_first);
            if (_second is not null)
                pool!.Return(_second);
            _first = null;
            _second = null;
            _result = null;
        }

        private readonly void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ClipPolygonLease));
        }
    }

    public static Vector2[] ProjectToNdc(IReadOnlyList<Vector3> localPoly, Matrix4x4 cellToWorld, Matrix4x4 viewProj)
        => ProjectToNdc(localPoly, cellToWorld, viewProj, ArrayPool<Vector4>.Shared);

    internal static Vector2[] ProjectToNdc(
        IReadOnlyList<Vector3> localPoly,
        Matrix4x4 cellToWorld,
        Matrix4x4 viewProj,
        ArrayPool<Vector4> vectorPool)
    {
        if (localPoly == null || localPoly.Count < 3) return System.Array.Empty<Vector2>();
        ArgumentNullException.ThrowIfNull(vectorPool);

        Matrix4x4 m = cellToWorld * viewProj;

        int capacity = checked(localPoly.Count + 5);
        Vector4[] first = vectorPool.Rent(capacity);
        Vector4[]? second = null;

        try
        {
            second = vectorPool.Rent(capacity);
            int currentCount = localPoly.Count;
            for (int i = 0; i < currentCount; i++)
                first[i] = Vector4.Transform(new Vector4(localPoly[i], 1f), m);

            Vector4[] current = first;
            Vector4[] output = second;
            ReadOnlySpan<HomogeneousPlane> planes =
            [
                HomogeneousPlane.EyeMinW,
                HomogeneousPlane.Left,
                HomogeneousPlane.Right,
                HomogeneousPlane.Bottom,
                HomogeneousPlane.Top,
            ];
            foreach (HomogeneousPlane plane in planes)
            {
                currentCount = ClipHomogeneousPlane(
                    current.AsSpan(0, currentCount), output, plane);
                if (currentCount < 3)
                    return System.Array.Empty<Vector2>();
                (current, output) = (output, current);
            }

            // Perspective divide → NDC xy. This is the only result allocation.
            var ndc = new Vector2[currentCount];
            for (int i = 0; i < currentCount; i++)
            {
                float w = current[i].W;
                ndc[i] = new Vector2(current[i].X / w, current[i].Y / w);
            }
            return ndc;
        }
        finally
        {
            vectorPool.Return(first);
            if (second is not null)
                vectorPool.Return(second);
        }
    }

    public static Vector4[] ProjectToClip(IReadOnlyList<Vector3> localPoly, Matrix4x4 cellToWorld, Matrix4x4 viewProj)
    {
        using ClipPolygonLease lease = ProjectToClipLease(localPoly, cellToWorld, viewProj);
        return lease.Count < 3 ? System.Array.Empty<Vector4>() : lease.Span.ToArray();
    }

    internal static ClipPolygonLease ProjectToClipLease(
        IReadOnlyList<Vector3> localPoly,
        Matrix4x4 cellToWorld,
        Matrix4x4 viewProj)
        => ProjectToClipLease(
            localPoly,
            cellToWorld,
            viewProj,
            ArrayPool<Vector4>.Shared);

    internal static ClipPolygonLease ProjectToClipLease(
        IReadOnlyList<Vector3> localPoly,
        Matrix4x4 cellToWorld,
        Matrix4x4 viewProj,
        ArrayPool<Vector4> vectorPool)
    {
        ArgumentNullException.ThrowIfNull(vectorPool);
        if (localPoly == null || localPoly.Count < 3)
            return new ClipPolygonLease(null, null, null, null, 0);

        Matrix4x4 m = cellToWorld * viewProj;
        Vector4[] transformed = vectorPool.Rent(localPoly.Count);
        Vector4[]? clipped = null;
        bool success = false;
        try
        {
            clipped = vectorPool.Rent(checked(localPoly.Count + 1));
            bool anyBehind = false;
            for (int i = 0; i < localPoly.Count; i++)
            {
                Vector4 vertex = Vector4.Transform(new Vector4(localPoly[i], 1f), m);
                if (vertex.W < 0f) anyBehind = true;
                transformed[i] = vertex;
            }

            ReadOnlySpan<Vector4> result = transformed.AsSpan(0, localPoly.Count);
            if (anyBehind)
            {
                int count = ClipHomogeneousPlane(result, clipped, HomogeneousPlane.EyeZero);
                if (count < 3)
                {
                    success = true;
                    return new ClipPolygonLease(
                        vectorPool,
                        transformed,
                        clipped,
                        null,
                        0);
                }
                result = clipped.AsSpan(0, count);
            }

            Vector4[] resultArray = anyBehind ? clipped : transformed;
            success = true;
            return new ClipPolygonLease(
                vectorPool,
                transformed,
                clipped,
                resultArray,
                result.Length);
        }
        finally
        {
            if (!success)
            {
                vectorPool.Return(transformed);
                if (clipped is not null)
                    vectorPool.Return(clipped);
            }
        }
    }

    public static Vector2[] ClipToRegion(IReadOnlyList<Vector4> subjectClip, IReadOnlyList<Vector2> regionCcwNdc)
    {
        if (subjectClip == null || regionCcwNdc == null || subjectClip.Count < 3 || regionCcwNdc.Count < 3)
            return System.Array.Empty<Vector2>();

        if (subjectClip is Vector4[] array)
            return ClipToRegion(array.AsSpan(), regionCcwNdc);

        Vector4[] rented = ArrayPool<Vector4>.Shared.Rent(subjectClip.Count);
        try
        {
            for (int i = 0; i < subjectClip.Count; i++)
                rented[i] = subjectClip[i];
            return ClipToRegion(rented.AsSpan(0, subjectClip.Count), regionCcwNdc);
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(rented);
        }
    }

    internal static Vector2[] ClipToRegion(
        ReadOnlySpan<Vector4> subjectClip,
        IReadOnlyList<Vector2> regionCcwNdc)
        => ClipToRegionCore(subjectClip, regionCcwNdc, vertexStore: null);

    internal static Vector2[] ClipToRegion(
        ReadOnlySpan<Vector4> subjectClip,
        IReadOnlyList<Vector2> regionCcwNdc,
        PortalPolygonVertexStore vertexStore)
        => ClipToRegionCore(
            subjectClip,
            regionCcwNdc,
            vertexStore,
            ArrayPool<Vector4>.Shared,
            ArrayPool<Vector2>.Shared);

    internal static Vector2[] ClipToRegion(
        ReadOnlySpan<Vector4> subjectClip,
        IReadOnlyList<Vector2> regionCcwNdc,
        PortalPolygonVertexStore vertexStore,
        ArrayPool<Vector4> vector4Pool)
        => ClipToRegionCore(
            subjectClip,
            regionCcwNdc,
            vertexStore,
            vector4Pool,
            ArrayPool<Vector2>.Shared);

    private static Vector2[] ClipToRegionCore(
        ReadOnlySpan<Vector4> subjectClip,
        IReadOnlyList<Vector2> regionCcwNdc,
        PortalPolygonVertexStore? vertexStore,
        ArrayPool<Vector4>? vector4Pool = null,
        ArrayPool<Vector2>? vector2Pool = null)
    {
        if (subjectClip.Length < 3 || regionCcwNdc == null || regionCcwNdc.Count < 3)
            return System.Array.Empty<Vector2>();
        vector4Pool ??= ArrayPool<Vector4>.Shared;
        vector2Pool ??= ArrayPool<Vector2>.Shared;

        int regionCount = regionCcwNdc.Count;
        int capacity = checked(subjectClip.Length + regionCount);
        Vector4[] first = vector4Pool.Rent(capacity);
        Vector4[]? second = null;
        Vector2[]? ndcScratch = null;

        try
        {
            second = vector4Pool.Rent(capacity);
            int currentCount = subjectClip.Length;
            subjectClip.CopyTo(first);

            Vector4[] current = first;
            Vector4[] output = second;
            for (int edge = 0; edge < regionCount; edge++)
            {
                if (currentCount < 3)
                    return System.Array.Empty<Vector2>();
                int outputCount = ClipHomogeneousEdge(
                    current.AsSpan(0, currentCount),
                    output,
                    regionCcwNdc[edge],
                    regionCcwNdc[(edge + 1) % regionCount]);
                (current, output) = (output, current);
                currentCount = outputCount;
            }
            if (currentCount < 3)
                return System.Array.Empty<Vector2>();

            ndcScratch = vector2Pool.Rent(currentCount);
            Span<Vector2> ndc = ndcScratch.AsSpan(0, currentCount);
            for (int i = 0; i < currentCount; i++)
            {
                float w = current[i].W;
                var vertex = new Vector2(current[i].X / w, current[i].Y / w);
                if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y))
                    return System.Array.Empty<Vector2>();
                ndc[i] = vertex;
            }

            int mergedCount = MergeSubPixelVertices(ndc);
            if (mergedCount < 3)
                return System.Array.Empty<Vector2>();
            Vector2[] merged = vertexStore?.Rent(mergedCount)
                ?? GC.AllocateUninitializedArray<Vector2>(mergedCount);
            ndc[..mergedCount].CopyTo(merged);

            EnsureCcw(merged);
            return merged;
        }
        finally
        {
            vector4Pool.Return(first);
            if (second is not null)
                vector4Pool.Return(second);
            if (ndcScratch is not null)
                vector2Pool.Return(ndcScratch);
        }
    }

    private const float VertexMergeEpsilonNdc = 2f / 1080f;

    private static int MergeSubPixelVertices(Span<Vector2> poly)
    {
        if (poly.Length < 3) return poly.Length;
        int kept = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 vertex = poly[i];
            if (kept > 0)
            {
                Vector2 previous = poly[kept - 1];
                if (MathF.Abs(vertex.X - previous.X) <= VertexMergeEpsilonNdc
                    && MathF.Abs(vertex.Y - previous.Y) <= VertexMergeEpsilonNdc)
                    continue;
            }
            poly[kept++] = vertex;
        }
        // Wrap-around: last ≈ first.
        while (kept >= 2)
        {
            Vector2 first = poly[0];
            Vector2 last = poly[kept - 1];
            if (MathF.Abs(first.X - last.X) <= VertexMergeEpsilonNdc
                && MathF.Abs(first.Y - last.Y) <= VertexMergeEpsilonNdc)
                kept--;
            else
                break;
        }
        return kept;
    }

    private static int ClipHomogeneousEdge(
        ReadOnlySpan<Vector4> polygon,
        Span<Vector4> result,
        Vector2 a,
        Vector2 b)
    {
        int outputCount = 0;
        float ex = b.X - a.X, ey = b.Y - a.Y;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector4 cur = polygon[i];
            Vector4 prev = polygon[(i + polygon.Length - 1) % polygon.Length];
            float dCur = ex * (cur.Y - cur.W * a.Y) - ey * (cur.X - cur.W * a.X);
            float dPrev = ex * (prev.Y - prev.W * a.Y) - ey * (prev.X - prev.W * a.X);
            bool curIn = dCur >= 0f;
            bool prevIn = dPrev >= 0f;

            if (curIn)
            {
                if (!prevIn) result[outputCount++] = Lerp(prev, cur, dPrev, dCur);
                result[outputCount++] = cur;
            }
            else if (prevIn)
            {
                result[outputCount++] = Lerp(prev, cur, dPrev, dCur);
            }
        }
        return outputCount;
    }

    private static void EnsureCcw(Vector2[] poly)
    {
        float area2 = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Length];
            area2 += p.X * q.Y - q.X * p.Y;
        }
        if (area2 < 0f) System.Array.Reverse(poly);
    }

    private const float MinW = 0.05f;

    private static int ClipHomogeneousPlane(
        ReadOnlySpan<Vector4> polygon,
        Span<Vector4> result,
        HomogeneousPlane plane)
    {
        int outputCount = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector4 cur = polygon[i];
            Vector4 prev = polygon[(i + polygon.Length - 1) % polygon.Length];
            float dCur = PlaneDistance(cur, plane);
            float dPrev = PlaneDistance(prev, plane);
            bool curIn = dCur >= 0f;
            bool prevIn = dPrev >= 0f;

            if (curIn)
            {
                if (!prevIn) result[outputCount++] = Lerp(prev, cur, dPrev, dCur);
                result[outputCount++] = cur;
            }
            else if (prevIn)
            {
                result[outputCount++] = Lerp(prev, cur, dPrev, dCur);
            }
        }
        return outputCount;
    }

    private static float PlaneDistance(in Vector4 vertex, HomogeneousPlane plane) => plane switch
    {
        HomogeneousPlane.EyeMinW => vertex.W - MinW,
        HomogeneousPlane.EyeZero => vertex.W,
        HomogeneousPlane.Left => vertex.W + vertex.X,
        HomogeneousPlane.Right => vertex.W - vertex.X,
        HomogeneousPlane.Bottom => vertex.W + vertex.Y,
        HomogeneousPlane.Top => vertex.W - vertex.Y,
        _ => throw new System.ArgumentOutOfRangeException(nameof(plane)),
    };

    private enum HomogeneousPlane : byte
    {
        EyeMinW,
        EyeZero,
        Left,
        Right,
        Bottom,
        Top,
    }

    private static Vector4 Lerp(Vector4 p, Vector4 q, float dp, float dq)
    {
        float t = dp / (dp - dq);
        return p + t * (q - p);
    }
}
