using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcDream.Core.Physics;

public sealed record CellDump(
    uint                              CellId,
    Matrix4x4Dto                      WorldTransform,
    Matrix4x4Dto                      InverseWorldTransform,
    IReadOnlyList<PolygonDump>        ResolvedPolygons,
    IReadOnlyList<PolygonDump>        PortalPolygons,
    IReadOnlyList<PortalDump>         Portals,
    IReadOnlyList<uint>               VisibleCellIds);

public sealed record PolygonDump(
    ushort               Id,
    int                  NumPoints,
    int                  SidesType,
    PlaneDto             Plane,
    IReadOnlyList<Vector3Dto> Vertices);

public sealed record PortalDump(
    ushort   OtherCellId,
    ushort   PolygonId,
    ushort   Flags);

public sealed record Vector3Dto(float X, float Y, float Z)
{
    public static Vector3Dto From(Vector3 v) => new(v.X, v.Y, v.Z);
    public Vector3 ToVector3() => new(X, Y, Z);
}

public sealed record PlaneDto(Vector3Dto Normal, float D)
{
    public static PlaneDto From(Plane p) => new(Vector3Dto.From(p.Normal), p.D);
    public Plane ToPlane() => new(Normal.ToVector3(), D);
}

public sealed record Matrix4x4Dto(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34,
    float M41, float M42, float M43, float M44)
{
    public static Matrix4x4Dto From(Matrix4x4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);

    public Matrix4x4 ToMatrix() => new(
        M11, M12, M13, M14,
        M21, M22, M23, M24,
        M31, M32, M33, M34,
        M41, M42, M43, M44);
}

public static class CellDumpSerializer
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

    public static CellDump Capture(uint cellId, CellPhysics cell)
    {
        var resolved = new List<PolygonDump>(cell.Resolved.Count);
        foreach (var (id, poly) in cell.Resolved)
            resolved.Add(DumpPolygon(id, poly));

        var portalPolys = new List<PolygonDump>();
        if (cell.PortalPolygons is not null)
        {
            foreach (var (id, poly) in cell.PortalPolygons)
                portalPolys.Add(DumpPolygon(id, poly));
        }

        var portals = new List<PortalDump>(cell.Portals.Count);
        foreach (var p in cell.Portals)
            portals.Add(new PortalDump(p.OtherCellId, p.PolygonId, p.Flags));

        var visible = new List<uint>(cell.VisibleCellIds);

        return new CellDump(
            CellId:                cellId,
            WorldTransform:        Matrix4x4Dto.From(cell.WorldTransform),
            InverseWorldTransform: Matrix4x4Dto.From(cell.InverseWorldTransform),
            ResolvedPolygons:      resolved,
            PortalPolygons:        portalPolys,
            Portals:               portals,
            VisibleCellIds:        visible);
    }

    private static PolygonDump DumpPolygon(ushort id, ResolvedPolygon poly)
    {
        var verts = new List<Vector3Dto>(poly.Vertices.Length);
        foreach (var v in poly.Vertices)
            verts.Add(Vector3Dto.From(v));

        return new PolygonDump(
            Id:        id,
            NumPoints: poly.NumPoints,
            SidesType: (int)poly.SidesType,
            Plane:     PlaneDto.From(poly.Plane),
            Vertices:  verts);
    }

    public static void Write(CellDump dump, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.Create(filePath);
        JsonSerializer.Serialize(stream, dump, WriteOptions);
    }

    public static CellDump Read(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var dump = JsonSerializer.Deserialize<CellDump>(stream, ReadOptions);
        if (dump is null)
            throw new InvalidDataException($"Cell dump deserialized to null: {filePath}");
        return dump;
    }

    public static CellPhysics Hydrate(CellDump dump)
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>(dump.ResolvedPolygons.Count);
        foreach (var p in dump.ResolvedPolygons)
            resolved[p.Id] = HydratePolygon(p);

        var portalPolys = new Dictionary<ushort, ResolvedPolygon>(dump.PortalPolygons.Count);
        foreach (var p in dump.PortalPolygons)
            portalPolys[p.Id] = HydratePolygon(p);

        var portals = new List<PortalInfo>(dump.Portals.Count);
        foreach (var p in dump.Portals)
            portals.Add(new PortalInfo(
                otherCellId: p.OtherCellId,
                polygonId:   p.PolygonId,
                flags:       p.Flags));

        var visible = new HashSet<uint>(dump.VisibleCellIds);

        return new CellPhysics
        {
            BSP                   = null,
            PhysicsPolygons       = null,
            Vertices              = null,
            WorldTransform        = dump.WorldTransform.ToMatrix(),
            InverseWorldTransform = dump.InverseWorldTransform.ToMatrix(),
            Resolved              = resolved,
            CellBSP               = null,
            Portals               = portals,
            PortalPolygons        = portalPolys.Count == 0 ? null : portalPolys,
            VisibleCellIds        = visible,
        };
    }

    private static ResolvedPolygon HydratePolygon(PolygonDump p)
    {
        var verts = new Vector3[p.Vertices.Count];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = p.Vertices[i].ToVector3();

        return new ResolvedPolygon
        {
            Vertices  = verts,
            Plane     = p.Plane.ToPlane(),
            NumPoints = p.NumPoints,
            SidesType = (DatReaderWriter.Enums.CullMode)p.SidesType,
            Id        = p.Id,
        };
    }
}
