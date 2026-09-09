using System.Numerics;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Environment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.App.Rendering.Walk;

public static class WalkCellFactory
{
    public static WalkCell? BuildCell(IDatReaderWriter dats, uint cellId, Vector3 blockOffset)
    {
        if (dats.Get<EnvCell>(cellId) is not EnvCell envCell)
            return null;
        if (dats.Get<Environment>(0x0D000000u | envCell.EnvironmentId) is not Environment environment
            || !environment.Cells.TryGetValue(envCell.CellStructure, out CellStruct? cellStruct)
            || cellStruct is null)
        {
            return null;
        }

        Matrix4x4 worldTransform =
            Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation)
            * Matrix4x4.CreateTranslation(
                envCell.Position.Origin.X + blockOffset.X,
                envCell.Position.Origin.Y + blockOffset.Y,
                envCell.Position.Origin.Z + blockOffset.Z);
        Matrix4x4.Invert(worldTransform, out Matrix4x4 inverse);
        return FromParsed(cellId, envCell, cellStruct, worldTransform, inverse);
    }

    public static WalkCell FromParsed(
        uint cellId, EnvCell envCell, CellStruct cellStruct,
        Matrix4x4 worldTransform, Matrix4x4 inverseWorldTransform)
    {
        uint lbMask = cellId & 0xFFFF0000u;
        int portalCount = envCell.CellPortals.Count;
        var portals = new WalkCellPortal[portalCount];
        var polygons = new WalkPolygon[portalCount];
        for (int i = 0; i < portalCount; i++)
        {
            CellPortal portal = envCell.CellPortals[i];
            portals[i] = new WalkCellPortal
            {
                OtherCellId = portal.OtherCellId == 0xFFFF
                    ? 0xFFFFFFFFu
                    : lbMask | portal.OtherCellId,
                PolygonIndex = i,
                PortalSide = ((ushort)portal.Flags & 0x2) != 0 ? 0 : 1,
                ExactMatch = ((ushort)portal.Flags & 0x1) != 0,
                OtherPortalId = unchecked((short)portal.OtherPortalId),
            };
            polygons[i] = BuildPolygon(cellStruct, portal.PolygonId) ?? new WalkPolygon();
        }

        return new WalkCell
        {
            CellId = cellId,
            Portals = portals,
            PortalPolygons = polygons,
            StabList = envCell.VisibleCells.Select(v => lbMask | v).ToArray(),
            WorldTransform = worldTransform,
            InverseWorldTransform = inverseWorldTransform,
        };
    }

    public static Dictionary<uint, WalkCell> BuildInteriorCells(
        IDatReaderWriter dats, uint landblockId, Vector3 blockOffset,
        Dictionary<uint, WalkCell>? into = null)
    {
        uint lbMask = landblockId & 0xFFFF0000u;
        Dictionary<uint, WalkCell> cells = into ?? new Dictionary<uint, WalkCell>();
        if (dats.Get<LandBlockInfo>(lbMask | 0xFFFEu) is not LandBlockInfo info)
            return cells;

        uint firstCellId = lbMask | 0x0100u;
        for (uint offset = 0; offset < info.NumCells; offset++)
        {
            WalkCell? cell = BuildCell(dats, firstCellId + offset, blockOffset);
            if (cell is not null)
                cells[cell.CellId] = cell;
        }
        return cells;
    }

    private static WalkPolygon? BuildPolygon(CellStruct cellStruct, ushort polygonId)
    {
        if (!cellStruct.Polygons.TryGetValue(polygonId, out Polygon? poly)
            || poly is null || poly.VertexIds.Count < 3)
        {
            return null;
        }
        return BuildPolygonFromVertices(
            poly.VertexIds,
            id => cellStruct.VertexArray.Vertices.TryGetValue((ushort)id, out SWVertex? v)
                ? new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z)
                : null);
    }

    private static WalkPolygon? BuildPolygonFromVertices(
        IReadOnlyList<short> vertexIds, Func<short, Vector3?> resolve)
    {
        var vertices = new Vector3[vertexIds.Count];
        for (int i = 0; i < vertexIds.Count; i++)
        {
            Vector3? v = resolve(vertexIds[i]);
            if (v is null) return null;
            vertices[i] = v.Value;
        }
        Vector3 normal = Vector3.Normalize(
            Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]));
        return new WalkPolygon
        {
            Vertices = vertices,
            Plane = new WalkPlane(normal, -Vector3.Dot(normal, vertices[0])),
        };
    }
}
