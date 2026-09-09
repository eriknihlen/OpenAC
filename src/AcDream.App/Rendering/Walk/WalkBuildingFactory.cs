using System.Numerics;
using AcDream.Content;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering.Walk;

public static class WalkBuildingFactory
{
    public sealed record Entry(
        WalkBuilding Building, Matrix4x4 WorldTransform, Matrix4x4 InverseWorldTransform)
    {
        public Matrix4x4 PartZeroWorldTransform { get; } =
            Building.PartZeroTransform * WorldTransform;

        public Matrix4x4 InversePartZeroWorldTransform { get; } =
            Invert(Building.PartZeroTransform * WorldTransform);

        private static Matrix4x4 Invert(Matrix4x4 value)
        {
            if (!Matrix4x4.Invert(value, out Matrix4x4 inverse))
                throw new InvalidOperationException("A building part-zero transform is not invertible.");
            return inverse;
        }
    }

    public static List<Entry> Build(
        IDatReaderWriter dats, uint landblockId,
        IReadOnlyList<BuildingInfo>? buildingInfos, Vector3 lbOffset)
    {
        var result = new List<Entry>();
        if (buildingInfos is null)
            return result;
        uint lbMask = landblockId & 0xFFFF0000u;

        foreach (BuildingInfo buildingInfo in buildingInfos)
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
            uint partZeroGfxObjId = 0;
            Matrix4x4 partZeroTransform = Matrix4x4.Identity;
            float partZeroScaleZ = 1f;
            var degradeLevels = new List<WalkBuildingDegradeLevel>();
            if (dats.Get<GfxObj>(buildingInfo.ModelId) is GfxObj directGfxObj)
            {
                partZeroGfxObjId = buildingInfo.ModelId;
                PopulateGfx(directGfxObj);
            }
            else if (dats.Get<Setup>(buildingInfo.ModelId) is Setup setup)
            {
                IReadOnlyList<MeshRef> parts = SetupMesh.Flatten(setup);
                if (parts.Count > 0)
                {
                    MeshRef partZero = parts[0];
                    partZeroGfxObjId = partZero.GfxObjId;
                    partZeroTransform = partZero.PartTransform;
                    partZeroScaleZ = setup.DefaultScale.Count > 0
                        ? setup.DefaultScale[0].Z
                        : 1f;
                    if (dats.Get<GfxObj>(partZero.GfxObjId) is GfxObj setupGfxObj)
                        PopulateGfx(setupGfxObj);
                }
            }

            void PopulateGfx(GfxObj gfxObj)
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
                        degradeLevels.Add(
                            new WalkBuildingDegradeLevel(
                                (uint)level.Id,
                                level.DegradeMode,
                                level.MinDist,
                                level.IdealDist,
                                level.MaxDist,
                                levelBsp));
                    }
                }
            }

            Matrix4x4 worldTransform =
                Matrix4x4.CreateFromQuaternion(buildingInfo.Frame.Orientation)
                * Matrix4x4.CreateTranslation(origin + lbOffset);
            Matrix4x4.Invert(worldTransform, out Matrix4x4 inverse);
            result.Add(new Entry(
                new WalkBuilding
                {
                    PositionCellId = positionCellId,
                    Portals = portals,
                    GfxObjId = partZeroGfxObjId,
                    DrawingBsp = bsp,
                    DegradeLevels = degradeLevels.ToArray(),
                    SortCenter = sortCenter,
                    PartZeroTransform = partZeroTransform,
                    PartZeroScaleZ = partZeroScaleZ,
                },
                worldTransform,
                inverse));
        }
        return result;
    }

    private static int DecodeBuildingSide(ushort flags) => (flags & 0x2) != 0 ? 0 : 1;

    private static WalkBspNode? ConvertDrawingBsp(GfxObj gfxObj, DrawingBSPNode? node)
    {
        if (node is null) return null;
        var converted = new WalkBspNode
        {
            SplittingPlane = new WalkPlane(node.SplittingPlane.Normal, node.SplittingPlane.D),
            IsFail = node.Type == BSPNodeType.Leaf,
            PosNode = ConvertDrawingBsp(gfxObj, node.PosNode),
            NegNode = ConvertDrawingBsp(gfxObj, node.NegNode),
        };
        if (node.Type == BSPNodeType.Portal && node.Portals is not null)
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

    private static WalkPolygon? BuildGfxPolygon(GfxObj gfxObj, ushort polygonId)
    {
        if (!gfxObj.Polygons.TryGetValue(polygonId, out Polygon? poly)
            || poly is null || poly.VertexIds.Count < 3)
        {
            return null;
        }
        return BuildPolygonFromVertices(
            poly.VertexIds,
            id => gfxObj.VertexArray.Vertices.TryGetValue((ushort)id, out SWVertex? v)
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
