using System;
using System.Numerics;

namespace AcDream.Core.Physics;

public static class LandDefs
{
    public const float CellLength = 24f;

    /// <summary>192 m landblock side.</summary>
    public const float BlockLength = 192f;

    public const int LandLength = 0x7F8;

    private const float Epsilon = 0.000199999995f;

    public static bool InBounds(int lx, int ly)
        => lx >= 0 && ly >= 0 && lx < LandLength && ly < LandLength;

    public static bool BlockIdToLcoord(uint cellId, out int lx, out int ly)
    {
        if (cellId == 0u) { lx = 0; ly = 0; return false; }
        lx = (int)((cellId >> 24) & 0xFFu) << 3;
        ly = (int)((cellId >> 16) & 0xFFu) << 3;
        return InBounds(lx, ly);
    }

    public static bool InboundValidCellId(uint cellId)
    {
        if (!CellLowInRange(cellId & 0xFFFFu)) return false;
        int lx = (int)((cellId >> 24) & 0xFFu) << 3;
        int ly = (int)((cellId >> 16) & 0xFFu) << 3;
        return InBounds(lx, ly);
    }

    public static bool GidToLcoord(uint cellId, out int lx, out int ly)
    {
        lx = 0; ly = 0;
        if (!InboundValidCellId(cellId)) return false;
        uint low = cellId & 0xFFFFu;
        if (low >= 0x100u) return false;   // outdoor only

        lx = ((int)((cellId >> 24) & 0xFFu) << 3) + (int)((low - 1u) >> 3);
        ly = ((int)((cellId >> 16) & 0xFFu) << 3) + (int)((low - 1u) & 7u);
        return InBounds(lx, ly);
    }

    public static uint LcoordToGid(int lx, int ly)
    {
        if (!InBounds(lx, ly)) return 0u;
        uint low = (uint)((ly & 7) + ((lx & 7) << 3) + 1);
        uint block = (uint)(((lx >> 3) << 8) | (ly >> 3));
        return (block << 16) | low;
    }

    public static bool GetOutsideLcoord(uint cellId, Vector3 blockLocalPos, out int lx, out int ly)
    {
        lx = 0; ly = 0;
        if (!CellLowInRange(cellId & 0xFFFFu)) return false;
        BlockIdToLcoord(cellId, out lx, out ly);
        lx += (int)MathF.Floor(blockLocalPos.X / CellLength);
        ly += (int)MathF.Floor(blockLocalPos.Y / CellLength);
        return InBounds(lx, ly);
    }

    public static bool AdjustToOutside(ref uint cellId, ref Vector3 blockLocalPos)
    {
        if (CellLowInRange(cellId & 0xFFFFu))
        {
            if (MathF.Abs(blockLocalPos.X) < Epsilon) blockLocalPos.X = 0f;
            if (MathF.Abs(blockLocalPos.Y) < Epsilon) blockLocalPos.Y = 0f;

            if (GetOutsideLcoord(cellId, blockLocalPos, out int lx, out int ly))
            {
                cellId = LcoordToGid(lx, ly);
                blockLocalPos.X -= MathF.Floor(blockLocalPos.X / BlockLength) * BlockLength;
                blockLocalPos.Y -= MathF.Floor(blockLocalPos.Y / BlockLength) * BlockLength;
                return true;
            }
        }

        cellId = 0u;
        return false;
    }

    public static Vector3 GetBlockOffset(uint source, uint dest)
    {
        uint srcBlock = source >> 16;
        uint dstBlock = dest >> 16;
        if (srcBlock == dstBlock)
            return Vector3.Zero;

        int srcLx, srcLy;
        if (source == 0u) { srcLx = 0; srcLy = 0; }
        else { srcLx = (int)((source >> 21) & 0x7f8u); srcLy = (int)((srcBlock & 0xFFu) << 3); }

        int dstLx, dstLy;
        if (dest == 0u) { dstLx = (int)source; dstLy = (int)source; }
        else { dstLx = (int)((dest >> 21) & 0x7f8u); dstLy = (int)((dstBlock & 0xFFu) << 3); }

        return new Vector3((dstLx - srcLx) * CellLength, (dstLy - srcLy) * CellLength, 0f);
    }

    private static bool CellLowInRange(uint low)
        => low is (>= 1u and <= 0x40u) or (>= 0x100u and <= 0xFFFDu) or 0xFFFFu;
}
