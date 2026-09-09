using System.Numerics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Environment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.App.Tests.Rendering.Walk;

using AcDream.App.Rendering.Walk;

public static class WalkWorldDatAdapter
{
    /// <summary>One built building plus its landblock-local placement.</summary>
    public sealed record BuildingEntry(
        WalkBuilding Building, Matrix4x4 WorldTransform, Matrix4x4 InverseWorldTransform);

    public static WalkCell? BuildCell(DatCollection dats, uint cellId)
        => BuildCell(dats, cellId, Vector3.Zero);

    public static WalkCell? BuildCell(DatCollection dats, uint cellId, Vector3 blockOffset)
    {
        if (dats.Get<EnvCell>(cellId) is not EnvCell envCell)
            return null;
        if (dats.Get<Environment>(0x0D000000u | envCell.EnvironmentId) is not Environment environment
            || !environment.Cells.TryGetValue(envCell.CellStructure, out CellStruct? cellStruct)
            || cellStruct is null)
        {
            return null;
        }

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
            polygons[i] = BuildPolygon(cellStruct, portal.PolygonId)
                ?? new WalkPolygon();
        }

        Matrix4x4 worldTransform =
            Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation)
            * Matrix4x4.CreateTranslation(
                envCell.Position.Origin.X + blockOffset.X,
                envCell.Position.Origin.Y + blockOffset.Y,
                envCell.Position.Origin.Z + blockOffset.Z);
        Matrix4x4.Invert(worldTransform, out Matrix4x4 inverse);
        return new WalkCell
        {
            CellId = cellId,
            Portals = portals,
            PortalPolygons = polygons,
            StabList = envCell.VisibleCells.Select(v => lbMask | v).ToArray(),
            WorldTransform = worldTransform,
            InverseWorldTransform = inverse,
        };
    }

    public static Dictionary<uint, WalkCell> BuildInteriorCells(
        DatCollection dats, uint landblockId)
        => BuildInteriorCells(dats, landblockId, Vector3.Zero, null);

    public static Dictionary<uint, WalkCell> BuildInteriorCells(
        DatCollection dats, uint landblockId, Vector3 blockOffset,
        Dictionary<uint, WalkCell>? into)
    {
        uint lbMask = landblockId & 0xFFFF0000u;
        Dictionary<uint, WalkCell> cells = into ?? new Dictionary<uint, WalkCell>();
        for (uint low = 0x0100; low <= 0xFFFD; low++)
        {
            WalkCell? cell = BuildCell(dats, lbMask | low, blockOffset);
            if (cell is null) break;
            cells[cell.CellId] = cell;
        }
        return cells;
    }

    public static List<BuildingEntry> BuildBuildings(DatCollection dats, uint landblockId)
    {
        uint lbMask = landblockId & 0xFFFF0000u;
        var result = new List<BuildingEntry>();
        if (dats.Get<LandBlockInfo>(lbMask | 0xFFFEu) is not LandBlockInfo info
            || info.Buildings is null)
        {
            return result;
        }

        foreach (BuildingInfo buildingInfo in info.Buildings)
        {
            Vector3 origin = new(
                buildingInfo.Frame.Origin.X,
                buildingInfo.Frame.Origin.Y,
                buildingInfo.Frame.Origin.Z);
            int cellX = (int)MathF.Floor(origin.X / 24f);
            int cellY = (int)MathF.Floor(origin.Y / 24f);
            uint positionCellId = lbMask | (uint)(cellX * 8 + cellY + 1);

            var portals = new WalkBldPortal[buildingInfo.Portals.Count];
            for (int i = 0; i < portals.Length; i++)
            {
                BuildingPortal portal = buildingInfo.Portals[i];
                portals[i] = new WalkBldPortal
                {
                    PortalSide = DecodeBuildingSide((ushort)portal.Flags),
                    ExactMatch = ((ushort)portal.Flags & 0x1) != 0,
                    OtherCellId = portal.OtherCellId == 0xFFFF
                        ? 0xFFFFFFFFu
                        : lbMask | portal.OtherCellId,
                    OtherPortalId = unchecked((short)portal.OtherPortalId),
                    StabList = portal.StabList.Select(s => lbMask | s).ToArray(),
                };
            }

            WalkBspNode? bsp = null;
            Vector3 sortCenter = Vector3.Zero;
            var degradeLevels = new List<WalkBuildingDegradeLevel>();
            if (dats.Get<GfxObj>(buildingInfo.ModelId) is GfxObj gfxObj)
            {
                bsp = ConvertDrawingBsp(gfxObj, gfxObj.DrawingBSP?.Root);
                sortCenter = new Vector3(gfxObj.SortCenter.X, gfxObj.SortCenter.Y, gfxObj.SortCenter.Z);
                if (gfxObj.DIDDegrade != 0
                    && dats.Get<GfxObjDegradeInfo>(gfxObj.DIDDegrade)
                        is GfxObjDegradeInfo degradeInfo)
                {
                    foreach (GfxObjInfo level in degradeInfo.Degrades)
                    {
                        WalkBspNode? levelBsp = null;
                        if (level.Id != 0
                            && dats.Get<GfxObj>((uint)level.Id) is GfxObj levelGfx)
                        {
                            levelBsp = ConvertDrawingBsp(levelGfx, levelGfx.DrawingBSP?.Root);
                        }
                        degradeLevels.Add(new WalkBuildingDegradeLevel(
                            (uint)level.Id, level.DegradeMode,
                            level.MinDist, level.IdealDist, level.MaxDist, levelBsp));
                    }
                }
            }

            Matrix4x4 worldTransform =
                Matrix4x4.CreateFromQuaternion(buildingInfo.Frame.Orientation)
                * Matrix4x4.CreateTranslation(origin);
            Matrix4x4.Invert(worldTransform, out Matrix4x4 inverse);
            result.Add(new BuildingEntry(
                new WalkBuilding
                {
                    PositionCellId = positionCellId,
                    Portals = portals,
                    GfxObjId = buildingInfo.ModelId,
                    DrawingBsp = bsp,
                    DegradeLevels = degradeLevels.ToArray(),
                    SortCenter = sortCenter,
                },
                worldTransform,
                inverse));
        }
        return result;
    }

    internal static WalkBspNode? ConvertDrawingBsp(GfxObj gfxObj, DrawingBSPNode? node)
    {
        if (node is null) return null;
        var converted = new WalkBspNode
        {
            SplittingPlane = new WalkPlane(
                node.SplittingPlane.Normal, node.SplittingPlane.D),
            IsFail = node.Type == DatReaderWriter.Enums.BSPNodeType.Leaf,
            PosNode = ConvertDrawingBsp(gfxObj, node.PosNode),
            NegNode = ConvertDrawingBsp(gfxObj, node.NegNode),
        };
        if (node.Type == DatReaderWriter.Enums.BSPNodeType.Portal && node.Portals is not null)
        {
            var refs = new List<WalkPortalRef>(node.Portals.Count);
            foreach (PortalRef portalRef in node.Portals)
            {
                WalkPolygon? polygon = BuildGfxPolygon(gfxObj, portalRef.PolyId);
                if (polygon is not null)
                    refs.Add(new WalkPortalRef
                    {
                        PortalIndex = portalRef.PortalIndex,
                        Polygon = polygon,
                    });
            }
            converted.InPortals = refs.ToArray();
        }
        return converted;
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

    public static bool FlipGfxPolygonPlanes;

    /// <summary>Adjudication toggle: the BuildingPortal side-flag decode.
    /// 0 = (Flags &amp; 0x2), 1 = inverted 0x2, 2 = (Flags &amp; 0x1),
    /// 3 = inverted 0x1.</summary>
    public static int BuildingSideMode = 1;

    private static int DecodeBuildingSide(ushort flags) => BuildingSideMode switch
    {
        0 => (flags & 0x2) != 0 ? 1 : 0,
        1 => (flags & 0x2) != 0 ? 0 : 1,
        2 => (flags & 0x1) != 0 ? 1 : 0,
        _ => (flags & 0x1) != 0 ? 0 : 1,
    };

    private static WalkPolygon? BuildGfxPolygon(GfxObj gfxObj, ushort polygonId)
    {
        if (!gfxObj.Polygons.TryGetValue(polygonId, out Polygon? poly)
            || poly is null || poly.VertexIds.Count < 3)
        {
            return null;
        }
        WalkPolygon? built = BuildPolygonFromVertices(
            poly.VertexIds,
            id => gfxObj.VertexArray.Vertices.TryGetValue((ushort)id, out SWVertex? v)
                ? new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z)
                : null);
        if (built is not null && FlipGfxPolygonPlanes)
            built.Plane = new WalkPlane(-built.Plane.Normal, -built.Plane.D);
        return built;
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
