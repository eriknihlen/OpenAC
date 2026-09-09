using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;          // BSPQuery
using DatReaderWriter.Enums;          // EnvCellFlags
using DatReaderWriter.Types;

namespace AcDream.Core.World.Cells;

public sealed class EnvCell : ObjCell
{
    public CellBSPTree? ContainmentBsp { get; }

    public FlatCellContainmentBsp? FlatContainmentBsp { get; }

    public EnvCell(uint id, Matrix4x4 worldTransform, Matrix4x4 inverseWorldTransform,
                   Vector3 localBoundsMin, Vector3 localBoundsMax,
                   IReadOnlyList<CellPortal> portals, IReadOnlyList<uint> stabList,
                   bool seenOutside, CellBSPTree? containmentBsp,
                   FlatCellContainmentBsp? flatContainmentBsp = null)
        : base(id, worldTransform, inverseWorldTransform, localBoundsMin, localBoundsMax,
               portals, stabList, seenOutside)
    {
        ContainmentBsp = containmentBsp;
        FlatContainmentBsp = flatContainmentBsp;
    }

    public override bool PointInCell(Vector3 worldPoint)
    {
        if (Portals.Count == 0)
            return false;

        var local = Vector3.Transform(worldPoint, InverseWorldTransform);
        if (FlatContainmentBsp is { RootIndex: >= 0 })
            return FlatBspQuery.PointInsideCellBsp(FlatContainmentBsp, local);
        if (ContainmentBsp?.Root is not null)
            return BSPQuery.PointInsideCellBsp(ContainmentBsp.Root, local);   // BSPQuery.cs:1034
        return false;
    }

    public static EnvCell FromDat(uint id, DatReaderWriter.DBObjs.EnvCell datCell,
                                  CellStruct cellStruct, Matrix4x4 worldTransform,
                                  FlatCellContainmentBsp? flatContainmentBsp = null)
    {
        Matrix4x4.Invert(worldTransform, out var inverse);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var kvp in cellStruct.VertexArray.Vertices)
        {
            var p = new Vector3(kvp.Value.Origin.X, kvp.Value.Origin.Y, kvp.Value.Origin.Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (min.X == float.MaxValue) { min = Vector3.Zero; max = Vector3.Zero; }

        var portals = new List<CellPortal>(datCell.CellPortals.Count);
        foreach (var p in datCell.CellPortals)
        {
            portals.Add(new CellPortal(
                otherCellId:   p.OtherCellId,
                otherPortalId: p.OtherPortalId,
                polygonId:     p.PolygonId,
                flags:         (ushort)p.Flags,
                polygonLocal:  ResolvePortalPolygon(cellStruct, p.PolygonId)));
        }

        uint lbPrefix = id & 0xFFFF0000u;
        var stab = new List<uint>();
        if (datCell.VisibleCells is not null)                       // match BuildLoadedCell:5701 null guard
            foreach (var low in datCell.VisibleCells) stab.Add(lbPrefix | low);
        bool seenOutside = datCell.Flags.HasFlag(EnvCellFlags.SeenOutside);

        return new EnvCell(id, worldTransform, inverse, min, max, portals, stab,
                           seenOutside, cellStruct.CellBSP, flatContainmentBsp);
    }

    public static EnvCell FromPrepared(
        uint id,
        Matrix4x4 worldTransform,
        FlatCellStructureCollisionAsset structure,
        FlatEnvCellTopology topology)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(topology);
        Matrix4x4.Invert(worldTransform, out Matrix4x4 inverse);

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        IncludeVertices(
            structure.PhysicsBsp.PolygonTable.Vertices,
            ref min,
            ref max);
        IncludeVertices(
            structure.PortalPolygons.Vertices,
            ref min,
            ref max);
        if (min.X == float.MaxValue)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        var portals = new List<CellPortal>(topology.Portals.Length);
        for (int i = 0; i < topology.Portals.Length; i++)
        {
            FlatEnvCellPortal portal = topology.Portals[i];
            FlatCollisionPolygon polygon =
                structure.PortalPolygons.Polygons[portal.PolygonIndex];
            var vertices = new Vector3[polygon.VertexRange.Count];
            structure.PortalPolygons.Vertices.AsSpan(
                polygon.VertexRange.Start,
                polygon.VertexRange.Count).CopyTo(vertices);
            portals.Add(new CellPortal(
                portal.OtherCellId,
                otherPortalId: 0,
                portal.PolygonId,
                portal.Flags,
                vertices));
        }

        return new EnvCell(
            id,
            worldTransform,
            inverse,
            min,
            max,
            portals,
            topology.VisibleCellIds,
            topology.SeenOutside,
            containmentBsp: null,
            structure.ContainmentBsp);
    }

    private static void IncludeVertices(
        System.Collections.Immutable.ImmutableArray<Vector3> vertices,
        ref Vector3 min,
        ref Vector3 max)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }
    }

    private static IReadOnlyList<Vector3> ResolvePortalPolygon(CellStruct cellStruct, ushort polygonId)
    {
        if (!cellStruct.Polygons.TryGetValue(polygonId, out var poly) || poly.VertexIds.Count < 3)
            return Array.Empty<Vector3>();
        var verts = new Vector3[poly.VertexIds.Count];
        for (int i = 0; i < poly.VertexIds.Count; i++)
        {
            if (!cellStruct.VertexArray.Vertices.TryGetValue((ushort)poly.VertexIds[i], out var v))
                return Array.Empty<Vector3>();   // any miss -> empty (matches BuildLoadedCell:5686-5691)
            verts[i] = new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z);
        }
        return verts;
    }
}
