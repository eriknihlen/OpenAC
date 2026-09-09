using System.Collections.Concurrent;
using System.Numerics;
using DatReaderWriter.DBObjs;

namespace AcDream.Core.Meshing;

public static class GfxObjBounds
{
    private static readonly ConcurrentDictionary<uint, (Vector3 Min, Vector3 Max)> _cache = new();

    public static (Vector3 Min, Vector3 Max)? Get(GfxObj? gfx)
    {
        if (gfx is null) return null;
        if (_cache.TryGetValue(gfx.Id, out var hit)) return hit;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in gfx.VertexArray.Vertices.Values)
        {
            var o = new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z);
            min = Vector3.Min(min, o);
            max = Vector3.Max(max, o);
        }
        if (min.X == float.MaxValue) return null;

        _cache[gfx.Id] = (min, max);
        return (min, max);
    }
}

public struct LocalBoundsAccumulator
{
    private Vector3 _min;
    private Vector3 _max;
    private bool _any;

    public void Add(Matrix4x4 partTransform, (Vector3 Min, Vector3 Max) partBounds)
    {
        Vector3 lo = partBounds.Min, hi = partBounds.Max;
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? lo.X : hi.X,
                (c & 2) == 0 ? lo.Y : hi.Y,
                (c & 4) == 0 ? lo.Z : hi.Z);
            var t = Vector3.Transform(corner, partTransform);
            if (!_any)
            {
                _min = _max = t;
                _any = true;
            }
            else
            {
                _min = Vector3.Min(_min, t);
                _max = Vector3.Max(_max, t);
            }
        }
    }

    public readonly bool TryGet(out Vector3 min, out Vector3 max)
    {
        min = _min;
        max = _max;
        return _any;
    }
}
