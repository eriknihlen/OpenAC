using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public sealed record GfxObjDump(
    uint                              GfxObjId,
    Vector3Dto                        BoundingSphereOrigin,
    float                             BoundingSphereRadius,
    IReadOnlyList<PolygonDump>        ResolvedPolygons);

public static class GfxObjDumpSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static GfxObjDump Capture(uint gfxObjId, GfxObjPhysics gfx)
    {
        var resolved = new List<PolygonDump>(gfx.Resolved.Count);
        foreach (var (id, poly) in gfx.Resolved)
        {
            var verts = new List<Vector3Dto>(poly.Vertices.Length);
            foreach (var v in poly.Vertices)
                verts.Add(Vector3Dto.From(v));

            resolved.Add(new PolygonDump(
                Id:        id,
                NumPoints: poly.NumPoints,
                SidesType: (int)poly.SidesType,
                Plane:     PlaneDto.From(poly.Plane),
                Vertices:  verts));
        }

        var bsOrigin = gfx.BoundingSphere?.Origin ?? Vector3.Zero;
        var bsRadius = gfx.BoundingSphere?.Radius ?? 0f;

        return new GfxObjDump(
            GfxObjId:             gfxObjId,
            BoundingSphereOrigin: Vector3Dto.From(bsOrigin),
            BoundingSphereRadius: bsRadius,
            ResolvedPolygons:     resolved);
    }

    public static void Write(GfxObjDump dump, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.Create(filePath);
        JsonSerializer.Serialize(stream, dump, WriteOptions);
    }

    public static GfxObjDump Read(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var dump = JsonSerializer.Deserialize<GfxObjDump>(stream, ReadOptions);
        if (dump is null)
            throw new InvalidDataException(
                $"GfxObj dump deserialized to null: {filePath}");
        return dump;
    }

    public static GfxObjPhysics Hydrate(GfxObjDump dump)
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>(
            dump.ResolvedPolygons.Count);
        foreach (var p in dump.ResolvedPolygons)
        {
            var verts = new Vector3[p.Vertices.Count];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = p.Vertices[i].ToVector3();

            resolved[p.Id] = new ResolvedPolygon
            {
                Vertices  = verts,
                Plane     = p.Plane.ToPlane(),
                NumPoints = p.NumPoints,
                SidesType = (DatReaderWriter.Enums.CullMode)p.SidesType,
                Id        = p.Id,
            };
        }

        var bsOrigin = dump.BoundingSphereOrigin.ToVector3();
        var bsRadius = dump.BoundingSphereRadius;
        if (bsRadius <= 0f && resolved.Count > 0)
        {
            (bsOrigin, bsRadius) = ComputeCoveringSphere(resolved);
        }

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = bsOrigin,
                Radius = bsRadius,
            },
        };
        foreach (var id in resolved.Keys)
            leaf.Polygons.Add(id);

        var bspTree = new PhysicsBSPTree { Root = leaf };

        return new GfxObjPhysics
        {
            BSP             = bspTree,
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices        = new VertexArray(),
            Resolved        = resolved,
            BoundingSphere  = leaf.BoundingSphere,
        };
    }

    private static (Vector3 origin, float radius) ComputeCoveringSphere(
        Dictionary<ushort, ResolvedPolygon> resolved)
    {
        var sum = Vector3.Zero;
        int count = 0;
        foreach (var poly in resolved.Values)
            foreach (var v in poly.Vertices)
            {
                sum += v;
                count++;
            }

        if (count == 0)
            return (Vector3.Zero, 0f);

        var centroid = sum / count;
        float maxDistSq = 0f;
        foreach (var poly in resolved.Values)
            foreach (var v in poly.Vertices)
            {
                float distSq = Vector3.DistanceSquared(centroid, v);
                if (distSq > maxDistSq) maxDistSq = distSq;
            }

        return (centroid, MathF.Sqrt(maxDistSq));
    }
}
