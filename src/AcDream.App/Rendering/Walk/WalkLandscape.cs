namespace AcDream.App.Rendering.Walk;

public sealed class WalkLandBlock
{
    public uint LandblockId;

    public int SideCellCount = 8;
    public float MaxZ;
    public float MinZ;

    public int Ring;

    public WalkBuilding?[] CellBuildings = [];

    // ---- per-frame visibility (draw_check_blocks / landcell_check) ----
    public WalkBoundingType InView;
    public WalkBoundingType[] CellInView = [];

    public int[] DrawArray = [];
    public int ClosestX = -1;
    public int ClosestY = -1;

    public void EnsureCellArrays()
    {
        int n = SideCellCount * SideCellCount;
        if (CellInView.Length < n) CellInView = new WalkBoundingType[n];
        if (DrawArray.Length < n) DrawArray = new int[n];
        if (CellBuildings.Length < n) CellBuildings = new WalkBuilding?[n];
    }
}

public sealed class WalkLandscape
{
    public const float BlockLength = 192f;
    public const float CellLength = 24f;

    public int MidWidth = 11;
    public WalkLandBlock?[] Blocks = [];      // [x * MidWidth + y]
    public int ViewerBlockX;
    public int ViewerBlockY;
    public int ViewerCellX;
    public int ViewerCellY;

    public float ViewerWorldOriginX;
    public float ViewerWorldOriginY;

    public int[] BlockDrawList = [];
    public int BlockDrawCount;

    public WalkLandBlock? BlockAt(int gridX, int gridY)
        => gridX >= 0 && gridX < MidWidth && gridY >= 0 && gridY < MidWidth
            ? Blocks[gridX * MidWidth + gridY]
            : null;

    public void CalcDrawOrder()
    {
        if (BlockDrawList.Length < MidWidth * MidWidth)
            BlockDrawList = new int[MidWidth * MidWidth];
        BlockDrawCount = LandWalkOrder.GetBlockOrder(
            ViewerBlockX, ViewerBlockY, MidWidth, BlockDrawList);

        for (int x = 0; x < MidWidth; x++)
        {
            for (int y = 0; y < MidWidth; y++)
            {
                WalkLandBlock? block = Blocks[x * MidWidth + y];
                if (block is null) continue;
                block.EnsureCellArrays();
                LandDirection dir = LandWalkOrder.GetDirection(
                    x - ViewerBlockX, y - ViewerBlockY);
                (int cx, int cy) = LandWalkOrder.ClosestCell(
                    dir, ViewerCellX, ViewerCellY, block.SideCellCount);
                if (cx == block.ClosestX && cy == block.ClosestY) continue;
                block.ClosestX = cx;
                block.ClosestY = cy;
                LandWalkOrder.FillCellOrderFarToNear(
                    cx, cy, block.SideCellCount,
                    block.DrawArray.AsSpan(0, block.SideCellCount * block.SideCellCount));
            }
        }
    }

    public void CheckBlocks(in WalkPlane cyPlane, WalkPortalView activeViews)
    {
        foreach (WalkLandBlock? block in Blocks)
        {
            if (block is null) continue;
            block.EnsureCellArrays();
            block.InView = WalkBoundingType.Outside;
            Array.Clear(block.CellInView, 0, block.SideCellCount * block.SideCellCount);
        }

        int viewCount = activeViews.ViewCount;
        Span<float> boundsScratch = stackalloc float[32];
        int cornerRow = MidWidth + 1;
        var intervals = new float[2 * cornerRow][];
        for (int i = 0; i < intervals.Length; i++) intervals[i] = new float[32];

        int v = 0;
        while (true)
        {
            WalkPlane[] edgePlanes;
            int edgeCount;
            bool last;
            if (viewCount == 0)
            {
                edgePlanes = [];
                edgeCount = 0;
                last = true;
            }
            else
            {
                WalkViewPoly poly = activeViews.View.Polys[v];
                edgePlanes = new WalkPlane[poly.VertexCount];
                for (int k = 0; k < poly.VertexCount; k++)
                    edgePlanes[k] = activeViews.View.Vertices[poly.VertexIndex + k].Plane;
                edgeCount = poly.VertexCount;
                v++;
                last = v == viewCount;
            }

            for (int j = 0; j <= MidWidth; j++)
                WalkVisibilityMath.FillClipHeights(
                    ViewerWorldOriginX + (0 - ViewerBlockX) * BlockLength,
                    ViewerWorldOriginY + (j - ViewerBlockY) * BlockLength,
                    cyPlane, edgePlanes, intervals[j]);
            for (int bx = 0; bx < MidWidth; bx++)
            {
                int westRow = (bx & 1) * cornerRow;
                int eastRow = ((bx - 1) & 1) * cornerRow;
                for (int j = 0; j <= MidWidth; j++)
                    WalkVisibilityMath.FillClipHeights(
                        ViewerWorldOriginX + (bx + 1 - ViewerBlockX) * BlockLength,
                        ViewerWorldOriginY + (j - ViewerBlockY) * BlockLength,
                        cyPlane, edgePlanes, intervals[eastRow + j]);
                for (int by = 0; by < MidWidth; by++)
                {
                    WalkLandBlock? block = Blocks[bx * MidWidth + by];
                    if (block is null) continue;
                    WalkBoundingType bt = WalkVisibilityMath.BlockCheck(
                        intervals[westRow + by], intervals[westRow + by + 1],
                        intervals[eastRow + by], intervals[eastRow + by + 1],
                        edgeCount, block.MaxZ, block.MinZ);
                    if (bt != WalkBoundingType.Outside)
                    {
                        block.InView = bt;
                        LandCellCheck(block, bx, by, cyPlane, edgePlanes);
                    }
                }
            }
            if (last) return;
        }
    }

    private static readonly float[][] CellGridScratch = CreateCellGridScratch();

    private static float[][] CreateCellGridScratch()
    {
        var grid = new float[2 * 9][];
        for (int i = 0; i < grid.Length; i++) grid[i] = new float[32];
        return grid;
    }

    private void LandCellCheck(
        WalkLandBlock block, int bx, int by,
        in WalkPlane cyPlane, WalkPlane[] edgePlanes)
    {
        int n = block.SideCellCount;
        if (n != 8)
        {
            for (int i = 0; i < n * n; i++)
                block.CellInView[i] = WalkBoundingType.PartiallyInside;
            return;
        }
        if (block.InView == WalkBoundingType.EntirelyInside)
        {
            for (int i = 0; i < n * n; i++)
                block.CellInView[i] = WalkBoundingType.EntirelyInside;
            return;
        }
        float x0 = ViewerWorldOriginX + (bx - ViewerBlockX) * BlockLength;
        float y0 = ViewerWorldOriginY + (by - ViewerBlockY) * BlockLength;
        int cornerRow = n + 1;
        float[][] grid = CellGridScratch;
        for (int j = 0; j <= n; j++)
            WalkVisibilityMath.FillClipHeights(
                x0, j * CellLength + y0, cyPlane, edgePlanes, grid[j]);
        for (int cx = 0; cx < n; cx++)
        {
            int westRow = (cx & 1) * cornerRow;
            int eastRow = ((cx - 1) & 1) * cornerRow;
            for (int j = 0; j <= n; j++)
                WalkVisibilityMath.FillClipHeights(
                    (cx + 1) * CellLength + x0, j * CellLength + y0,
                    cyPlane, edgePlanes, grid[eastRow + j]);
            for (int cy = 0; cy < n; cy++)
            {
                if (block.CellInView[n * cx + cy] != WalkBoundingType.Outside)
                    continue;   // union across views: never downgrade
                block.CellInView[n * cx + cy] = WalkVisibilityMath.BlockCheck(
                    grid[westRow + cy], grid[westRow + cy + 1],
                    grid[eastRow + cy], grid[eastRow + cy + 1],
                    edgePlanes.Length, block.MaxZ, block.MinZ);
            }
        }
    }
}
