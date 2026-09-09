using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public sealed class WalkLandscapeAssembler
{
    public const int MidRadius = 25;

    public const int GridWidth = MidRadius * 2 + 1;

    private sealed class BlockData
    {
        public required float MaxZ;
        public required float MinZ;
        public required IReadOnlyList<WalkBuildingFactory.Entry> Buildings;
    }

    private readonly Dictionary<(int X, int Y), BlockData> _blocks = new();
    private int _viewerBlockX = int.MinValue;
    private int _viewerBlockY = int.MinValue;

    public WalkLandscape Landscape { get; } = new()
    {
        MidWidth = GridWidth,
        Blocks = new WalkLandBlock?[GridWidth * GridWidth],
        ViewerBlockX = MidRadius,
        ViewerBlockY = MidRadius,
    };

    public void PublishLandblock(
        uint landblockId, float maxZ, float minZ,
        IReadOnlyList<WalkBuildingFactory.Entry> buildings)
    {
        (int bx, int by) = BlockCoords(landblockId);
        _blocks[(bx, by)] = new BlockData { MaxZ = maxZ, MinZ = minZ, Buildings = buildings };
        RefreshSlotIfWindowed(bx, by);
    }

    public void ClearBuildings(uint landblockId)
    {
        (int bx, int by) = BlockCoords(landblockId);
        if (!_blocks.TryGetValue((bx, by), out BlockData? data))
            return;
        data.Buildings = Array.Empty<WalkBuildingFactory.Entry>();
        RefreshSlotIfWindowed(bx, by);
    }

    public void RetireLandblock(uint landblockId)
    {
        (int bx, int by) = BlockCoords(landblockId);
        _blocks.Remove((bx, by));
        RefreshSlotIfWindowed(bx, by);
    }

    public void SetViewer(uint cameraCellId, Vector3 cameraOrigin)
    {
        int cameraBlockX = (int)(cameraCellId >> 24);
        int cameraBlockY = (int)((cameraCellId >> 16) & 0xFF);
        if (cameraBlockX != _viewerBlockX || cameraBlockY != _viewerBlockY)
        {
            _viewerBlockX = cameraBlockX;
            _viewerBlockY = cameraBlockY;
            RebuildWindow();
        }

        uint low = cameraCellId & 0xFFFFu;
        if (low >= 1 && low <= 0x40)
        {
            int cellIndex = (int)low - 1;
            Landscape.ViewerCellX = cellIndex / 8;
            Landscape.ViewerCellY = cellIndex % 8;
            Landscape.ViewerWorldOriginX = DeriveViewerBlockOrigin(
                cameraOrigin.X, Landscape.ViewerCellX);
            Landscape.ViewerWorldOriginY = DeriveViewerBlockOrigin(
                cameraOrigin.Y, Landscape.ViewerCellY);
        }
        else
        {
            Landscape.ViewerWorldOriginX = MathF.Floor(
                cameraOrigin.X / WalkLandscape.BlockLength) * WalkLandscape.BlockLength;
            Landscape.ViewerWorldOriginY = MathF.Floor(
                cameraOrigin.Y / WalkLandscape.BlockLength) * WalkLandscape.BlockLength;
            Landscape.ViewerCellX = Math.Clamp((int)MathF.Floor(
                (cameraOrigin.X - Landscape.ViewerWorldOriginX) / WalkLandscape.CellLength), 0, 7);
            Landscape.ViewerCellY = Math.Clamp((int)MathF.Floor(
                (cameraOrigin.Y - Landscape.ViewerWorldOriginY) / WalkLandscape.CellLength), 0, 7);
        }
    }

    private static float DeriveViewerBlockOrigin(float cameraAxis, int cellAxis)
    {
        float cellCenter = (cellAxis + 0.5f) * WalkLandscape.CellLength;
        return MathF.Round(
            (cameraAxis - cellCenter) / WalkLandscape.BlockLength,
            MidpointRounding.AwayFromZero) * WalkLandscape.BlockLength;
    }

    internal static int SideCellCountForRing(int ring)
        => ring <= 1 ? 8 : ring == 2 ? 4 : ring <= 4 ? 2 : 1;

    internal static int RingOf(int gridX, int gridY)
        => Math.Max(Math.Abs(gridX - MidRadius), Math.Abs(gridY - MidRadius));

    private void RebuildWindow()
    {
        for (int gx = 0; gx < GridWidth; gx++)
        {
            for (int gy = 0; gy < GridWidth; gy++)
            {
                int bx = _viewerBlockX + gx - MidRadius;
                int by = _viewerBlockY + gy - MidRadius;
                Landscape.Blocks[gx * GridWidth + gy] = BuildSlot(bx, by, gx, gy);
            }
        }
    }

    private void RefreshSlotIfWindowed(int bx, int by)
    {
        if (_viewerBlockX == int.MinValue)
            return;   // SetViewer has never run — no window to refresh yet.
        int gx = bx - _viewerBlockX + MidRadius;
        int gy = by - _viewerBlockY + MidRadius;
        if (gx < 0 || gx >= GridWidth || gy < 0 || gy >= GridWidth)
            return;
        Landscape.Blocks[gx * GridWidth + gy] = BuildSlot(bx, by, gx, gy);
    }

    private WalkLandBlock? BuildSlot(int bx, int by, int gx, int gy)
    {
        if (bx < 0 || bx > 0xFF || by < 0 || by > 0xFF)
            return null;
        if (!_blocks.TryGetValue((bx, by), out BlockData? data))
            return null;

        int sideCellCount = SideCellCountForRing(RingOf(gx, gy));
        var block = new WalkLandBlock
        {
            LandblockId = (uint)bx << 24 | (uint)by << 16,
            SideCellCount = sideCellCount,
            Ring = RingOf(gx, gy),
            MaxZ = data.MaxZ,
            MinZ = data.MinZ,
        };
        block.EnsureCellArrays();

        if (sideCellCount == 8)
        {
            foreach (WalkBuildingFactory.Entry entry in data.Buildings)
            {
                int cellIndex = (int)(entry.Building.PositionCellId & 0xFFFFu) - 1;
                if (cellIndex >= 0 && cellIndex < 64)
                    block.CellBuildings[cellIndex] = entry.Building;
            }
        }
        return block;
    }

    private static (int X, int Y) BlockCoords(uint landblockId)
        => ((int)((landblockId >> 24) & 0xFFu), (int)((landblockId >> 16) & 0xFFu));
}
