using System.Numerics;
using AcDream.Core.Rendering.Wb;
using AcDream.Core.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.World;

public static class SceneryGenerator
{
    // AC landblock geometry — matches LandblockMesh.
    private const int VerticesPerSide = 9;
    private const float CellSize = 24.0f;
    private const float LandblockSize = 192.0f;
    private const int CellsPerSide = 8;

    public readonly record struct ScenerySpawn(
        uint ObjectId,        // GfxObj or Setup id
        Vector3 LocalPosition, // landblock-local world units
        Quaternion Rotation,
        float Scale);

    public static IReadOnlyList<ScenerySpawn> Generate(
        IDatObjectSource dats,
        Region region,
        LandBlock block,
        uint landblockId,
        HashSet<int>? buildingCells = null,
        float[]? heightTable = null)
    {
        _ = heightTable;
        return GenerateInternal(dats, region, block, landblockId, buildingCells);
    }

    public static bool IsRoadVertex(ushort raw) => (raw & 0x3u) != 0;

    private static IReadOnlyList<ScenerySpawn> GenerateInternal(
        IDatObjectSource dats,
        Region region,
        LandBlock block,
        uint landblockId,
        HashSet<int>? buildingCells)
    {
        var result = new List<ScenerySpawn>();

        if (region.TerrainInfo?.TerrainTypes is null || region.SceneInfo?.SceneTypes is null)
            return result;

        var terrainEntries = WbSceneryAdapter.BuildTerrainEntries(block);

        uint blockX = (landblockId >> 24) * 8;
        uint blockY = ((landblockId >> 16) & 0xFFu) * 8;
        uint lbX    = landblockId >> 24;
        uint lbY    = (landblockId >> 16) & 0xFFu;

        for (int x = 0; x < VerticesPerSide; x++)
        {
            for (int y = 0; y < VerticesPerSide; y++)
            {
                int i = x * VerticesPerSide + y;
                ushort raw = block.Terrain[i];

                uint terrainType = (uint)((raw >> 2) & 0x1F);
                uint sceneType   = (uint)((raw >> 11) & 0x1F);

                if (terrainType >= region.TerrainInfo.TerrainTypes.Count) continue;
                var sceneTypeList = region.TerrainInfo.TerrainTypes[(int)terrainType].SceneTypes;
                if (sceneType >= sceneTypeList.Count) continue;

                uint sceneInfo = sceneTypeList[(int)sceneType];
                if (sceneInfo >= region.SceneInfo.SceneTypes.Count) continue;

                var scenes = region.SceneInfo.SceneTypes[(int)sceneInfo].Scenes;
                if (scenes.Count == 0) continue;

                uint cellX = (uint)x;
                uint cellY = (uint)y;
                uint globalCellX = cellX + blockX;
                uint globalCellY = cellY + blockY;

                // Scene-selection hash: identical to Generate.
                uint cellMat = globalCellY * (712977289u * globalCellX + 1813693831u)
                               - 1109124029u * globalCellX + 2139937281u;
                double offset = cellMat * 2.3283064e-10;
                int sceneIdx = (int)(scenes.Count * offset);
                if (sceneIdx >= scenes.Count || sceneIdx < 0) sceneIdx = 0;

                uint sceneId = (uint)scenes[sceneIdx];
                var scene = dats.Get<Scene>(sceneId);
                if (scene is null) continue;

                // Per-object frequency setup: identical to Generate.
                uint cellXMat = unchecked(0u - 1109124029u * globalCellX);
                uint cellYMat = 1813693831u * globalCellY;
                uint cellMat2 = 1360117743u * globalCellX * globalCellY + 1888038839u;

                for (uint j = 0; j < scene.Objects.Count; j++)
                {
                    var obj = scene.Objects[(int)j];
                    if (obj.WeenieObj != 0) continue;

                    double noise = unchecked((uint)(cellXMat + cellYMat - cellMat2 * (23399u + j))) * 2.3283064e-10;
                    if (noise >= obj.Frequency) continue;

                    // ─── WB substitution: displacement ───────────────────
                    var localPos = SceneryHelpers.Displace(obj, globalCellX, globalCellY, j);

                    float lx = cellX * CellSize + localPos.X;
                    float ly = cellY * CellSize + localPos.Y;

                    if (lx < 0 || ly < 0 || lx >= LandblockSize || ly >= LandblockSize)
                        continue;

                    if (TerrainUtils.OnRoad(new Vector3(lx, ly, 0), terrainEntries))
                        continue;

                    if (buildingCells is not null)
                    {
                        int dcx = Math.Clamp((int)(lx / CellSize), 0, CellsPerSide - 1);
                        int dcy = Math.Clamp((int)(ly / CellSize), 0, CellsPerSide - 1);
                        if (buildingCells.Contains(dcx * VerticesPerSide + dcy))
                            continue;
                    }

                    Vector3 normal = TerrainUtils.GetNormal(
                        region, terrainEntries, lbX, lbY,
                        new Vector3(lx, ly, 0));
                    if (!SceneryHelpers.CheckSlope(obj, normal.Z))
                        continue;

                    float lz = obj.BaseLoc.Origin.Z;

                    // ─── WB substitution: rotation ────────────────────────
                    Quaternion rotation;
                    if (obj.Align != 0)
                        rotation = SceneryHelpers.ObjAlign(obj, normal, lz, localPos);
                    else
                        rotation = SceneryHelpers.RotateObj(obj, globalCellX, globalCellY, j, localPos);

                    // ─── WB substitution: scale ───────────────────────────
                    float scale = SceneryHelpers.ScaleObj(obj, globalCellX, globalCellY, j);
                    if (scale <= 0) scale = 1f;

                    result.Add(new ScenerySpawn(
                        ObjectId:      obj.ObjectId,
                        LocalPosition: new Vector3(lx, ly, lz),
                        Rotation:      rotation,
                        Scale:         scale));
                }
            }
        }

        return result;
    }
}
