namespace AcDream.App.Rendering.Walk;

public static class LandWalkOrder
{
    private static readonly int[] XConst = [0, 0, 0, 0, 1, 0, -1, 0];
    private static readonly int[] XRing = [0, -1, 0, 1, 0, -1, 0, 1];
    private static readonly int[] XStep = [-1, 0, 1, 0, 1, 0, -1, 0];
    private static readonly int[] YConst = [0, 0, 0, 0, 0, 1, 0, -1];
    private static readonly int[] YStep = [0, -1, 0, 1, 0, 1, 0, -1];
    private static readonly int[] YRing = [1, 0, -1, 0, 1, 0, -1, 0];

    public static int GetBlockOrder(int viewerX, int viewerY, int width, Span<int> order)
    {
        int count = 0;
        order[count++] = viewerX * width + viewerY;
        int maxRing = MaxRingTo(viewerX, viewerY, width, width);
        for (int ring = 1; ring <= maxRing; ring++)
        {
            for (int step = 0; step < ring; step++)
            {
                for (int slot = 0; slot < 8; slot++)
                {
                    int x = XStep[slot] * step + XRing[slot] * ring + XConst[slot] + viewerX;
                    int y = YStep[slot] * step + YRing[slot] * ring + YConst[slot] + viewerY;
                    if (x >= 0 && x < width && y >= 0 && y < width)
                        order[count++] = x * width + y;
                }
            }
        }
        return count;
    }

    public static void FillCellOrderFarToNear(int closestX, int closestY, int side, Span<int> order)
    {
        int k = side * side;
        order[--k] = closestX * side + closestY;
        int maxRing = MaxRingTo(closestX, closestY, side, side);
        for (int ring = 1; ring <= maxRing; ring++)
        {
            for (int step = 0; step < ring; step++)
            {
                for (int slot = 0; slot < 8; slot++)
                {
                    int x = XStep[slot] * step + XRing[slot] * ring + XConst[slot] + closestX;
                    int y = YStep[slot] * step + YRing[slot] * ring + YConst[slot] + closestY;
                    if (x >= 0 && x < side && y >= 0 && y < side)
                        order[--k] = x * side + y;
                }
            }
        }
    }

    public static LandDirection GetDirection(int dx, int dy)
    {
        if (dx < 0)
        {
            if (dy < 0) return LandDirection.SouthWest;
            return dy > 0 ? LandDirection.NorthWest : LandDirection.West;
        }
        if (dx == 0)
        {
            if (dy < 0) return LandDirection.South;
            return dy > 0 ? LandDirection.North : LandDirection.InViewerBlock;
        }
        if (dy < 0) return LandDirection.SouthEast;
        return dy > 0 ? LandDirection.NorthEast : LandDirection.East;
    }

    public static (int X, int Y) ClosestCell(
        LandDirection dir, int viewerSqX, int viewerSqY, int side)
    {
        int scale = 8 / side;
        return dir switch
        {
            LandDirection.InViewerBlock => (viewerSqX / scale, viewerSqY / scale),
            LandDirection.North => (viewerSqX / scale, 0),
            LandDirection.South => (viewerSqX / scale, side - 1),
            LandDirection.East => (0, viewerSqY / scale),
            LandDirection.West => (side - 1, viewerSqY / scale),
            LandDirection.NorthWest => (side - 1, 0),
            LandDirection.SouthWest => (side - 1, side - 1),
            LandDirection.NorthEast => (0, 0),
            LandDirection.SouthEast => (0, side - 1),
            _ => throw new ArgumentOutOfRangeException(nameof(dir)),
        };
    }

    private static int MaxRingTo(int x, int y, int width, int height)
    {
        int max = x;
        if (y > max) max = y;
        if (width - 1 - x > max) max = width - 1 - x;
        if (height - 1 - y > max) max = height - 1 - y;
        return max;
    }
}

public enum LandDirection
{
    InViewerBlock = 0,
    North = 1,
    South = 2,
    East = 3,
    West = 4,
    NorthWest = 5,
    SouthWest = 6,
    NorthEast = 7,
    SouthEast = 8,
}
