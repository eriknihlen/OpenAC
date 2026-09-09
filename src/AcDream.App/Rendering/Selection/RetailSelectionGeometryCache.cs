using System.Numerics;
using AcDream.Content;
using AcDream.Core.Selection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering.Selection;

internal sealed class RetailSelectionGeometryCache : IRetailSelectionGeometrySource
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly Dictionary<uint, RetailSelectionMesh?> _cache = new();

    public RetailSelectionGeometryCache(IDatReaderWriter dats, object datLock)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
    }

    public RetailSelectionMesh? Resolve(uint gfxObjId)
    {
        if (_cache.TryGetValue(gfxObjId, out var cached))
            return cached;
        RetailSelectionMesh? loaded = Load(gfxObjId);
        _cache[gfxObjId] = loaded;
        return loaded;
    }

    private RetailSelectionMesh? Load(uint gfxObjId)
    {
        GfxObj? gfx;
        lock (_datLock)
            gfx = _dats.Get<GfxObj>(gfxObjId);

        var root = gfx?.DrawingBSP?.Root;
        if (gfx is null || root is null || root.BoundingSphere.Radius <= 0f)
            return null;

        var polygons = new List<RetailSelectionPolygon>(gfx.Polygons.Count);
        foreach (var entry in gfx.Polygons)
        {
            var source = entry.Value;
            if (source.VertexIds.Count < 3)
                continue;

            var vertices = new Vector3[source.VertexIds.Count];
            bool valid = true;
            for (int i = 0; i < source.VertexIds.Count; i++)
            {
                if (!gfx.VertexArray.Vertices.TryGetValue((ushort)source.VertexIds[i], out var vertex))
                {
                    valid = false;
                    break;
                }
                vertices[i] = vertex.Origin;
            }
            if (!valid)
                continue;

            polygons.Add(new RetailSelectionPolygon(
                vertices,
                SingleSided: (int)source.SidesType == 0));
        }

        return new RetailSelectionMesh(
            root.BoundingSphere.Origin,
            root.BoundingSphere.Radius,
            polygons);
    }
}
