using System.Numerics;
using AcDream.App.Rendering.Walk;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering.Walk;

public static class WalkLandscapeDatBuilder
{
    public sealed record BuiltWorld(
        WalkLandscape Landscape,
        Dictionary<uint, WalkCell> Cells,
        Dictionary<WalkBuilding, WalkWorldDatAdapter.BuildingEntry> Buildings);

    public const int MidRadius = 25;

    private static int SideCellCountForRing(int ring)
        => ring <= 1 ? 8 : ring == 2 ? 4 : ring <= 4 ? 2 : 1;

    public static void SetViewer(WalkLandscape landscape, uint cameraCellId, Vector3 cameraOrigin)
    {
        uint low = cameraCellId & 0xFFFFu;
        if (low >= 1 && low <= 0x40)
        {
            int cellIndex = (int)low - 1;
            landscape.ViewerCellX = cellIndex / 8;
            landscape.ViewerCellY = cellIndex % 8;
        }
        else
        {
            landscape.ViewerCellX = Math.Clamp((int)MathF.Floor(cameraOrigin.X / 24f), 0, 7);
            landscape.ViewerCellY = Math.Clamp((int)MathF.Floor(cameraOrigin.Y / 24f), 0, 7);
        }
    }

    public static BuiltWorld Build(DatCollection dats, uint cameraCellId, Vector3 cameraOrigin = default)
    {
        Region region = (Region)dats.Get<Region>(0x13000000u)!;
        float[] heightTable = region.LandDefs.LandHeightTable;

        int cameraBlockX = (int)(cameraCellId >> 24);
        int cameraBlockY = (int)((cameraCellId >> 16) & 0xFF);
        const int midRadius = MidRadius;
        const int midWidth = midRadius * 2 + 1;

        var landscape = new WalkLandscape
        {
            MidWidth = midWidth,
            Blocks = new WalkLandBlock?[midWidth * midWidth],
            ViewerBlockX = midRadius,
            ViewerBlockY = midRadius,
        };
        uint low = cameraCellId & 0xFFFFu;
        if (low >= 1 && low <= 0x40)
        {
            int cellIndex = (int)low - 1;
            landscape.ViewerCellX = cellIndex / 8;
            landscape.ViewerCellY = cellIndex % 8;
        }
        else
        {
            landscape.ViewerCellX = Math.Clamp((int)MathF.Floor(cameraOrigin.X / 24f), 0, 7);
            landscape.ViewerCellY = Math.Clamp((int)MathF.Floor(cameraOrigin.Y / 24f), 0, 7);
        }

        var cells = new Dictionary<uint, WalkCell>();
        var buildings = new Dictionary<WalkBuilding, WalkWorldDatAdapter.BuildingEntry>();

        for (int gx = 0; gx < midWidth; gx++)
        {
            for (int gy = 0; gy < midWidth; gy++)
            {
                int blockX = cameraBlockX + gx - midRadius;
                int blockY = cameraBlockY + gy - midRadius;
                if (blockX < 0 || blockX > 0xFF || blockY < 0 || blockY > 0xFF)
                    continue;
                uint landblockId = (uint)((blockX << 24) | (blockY << 16));
                if (dats.Get<LandBlock>(landblockId | 0xFFFFu) is not LandBlock landBlock)
                    continue;

                byte maxByte = 0, minByte = 255;
                foreach (byte h in landBlock.Height)
                {
                    if (h > maxByte) maxByte = h;
                    if (h < minByte) minByte = h;
                }
                int ring = Math.Max(Math.Abs(gx - midRadius), Math.Abs(gy - midRadius));
                int sideCellCount = SideCellCountForRing(ring);
                var block = new WalkLandBlock
                {
                    LandblockId = landblockId,
                    SideCellCount = sideCellCount,
                    MaxZ = heightTable[maxByte] + 200f,
                    MinZ = heightTable[minByte] - 1f,
                };
                block.EnsureCellArrays();

                var blockOffset = new Vector3(
                    (gx - midRadius) * WalkLandscape.BlockLength,
                    (gy - midRadius) * WalkLandscape.BlockLength,
                    0f);

                if (sideCellCount == 8)
                {
                    foreach (WalkWorldDatAdapter.BuildingEntry entry
                        in WalkWorldDatAdapter.BuildBuildings(dats, landblockId))
                    {
                        Matrix4x4 world = entry.WorldTransform
                            * Matrix4x4.CreateTranslation(blockOffset);
                        Matrix4x4.Invert(world, out Matrix4x4 inverse);
                        var placed = new WalkWorldDatAdapter.BuildingEntry(
                            entry.Building, world, inverse);
                        buildings[entry.Building] = placed;
                        int cellIndex = (int)(entry.Building.PositionCellId & 0xFFFFu) - 1;
                        if (cellIndex >= 0 && cellIndex < 64)
                            block.CellBuildings[cellIndex] = entry.Building;

                        foreach (WalkBldPortal portal in entry.Building.Portals)
                        {
                            if (portal.OtherCellId != 0xFFFFFFFFu
                                && !cells.ContainsKey(portal.OtherCellId))
                            {
                                WalkCell? c = WalkWorldDatAdapter.BuildCell(
                                    dats, portal.OtherCellId, blockOffset);
                                if (c is not null) cells[c.CellId] = c;
                            }
                            foreach (uint stab in portal.StabList)
                            {
                                if (cells.ContainsKey(stab)) continue;
                                WalkCell? c = WalkWorldDatAdapter.BuildCell(
                                    dats, stab, blockOffset);
                                if (c is not null) cells[c.CellId] = c;
                            }
                        }
                    }
                }
                landscape.Blocks[gx * midWidth + gy] = block;
            }
        }
        return new BuiltWorld(landscape, cells, buildings);
    }
}
