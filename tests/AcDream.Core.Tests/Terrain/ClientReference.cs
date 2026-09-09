using System.Numerics;
using System.Runtime.CompilerServices;

namespace AcDream.Core.Tests.Terrain;

public static class ClientReference
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSWtoNECut(int globalCellX, int globalCellY)
    {
        unchecked
        {
            int v7 = globalCellY * (214614067 * globalCellX + 1813693831)
                   - 1109124029 * globalCellX - 1369149221;
            return (double)(uint)v7 * 2.3283064e-10 >= 0.5;
        }
    }

    public static uint GetPalCode(
        int r0, int t0,
        int r1, int t1,
        int r2, int t2,
        int r3, int t3,
        int texSize = 1)
    {
        unchecked
        {
            return (uint)(t3
                + (texSize << 28)
                + 32 * (t2 + 32 * (t1 + 32 * (t0 + 32 * (r3 + 4 * (r2 + 4 * (r1 + 4 * r0)))))));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float GetVertexHeight(float[] landHeightTable, byte heightByte)
    {
        return landHeightTable[heightByte];
    }

    public static Vector3 GetVertexPosition(float[] landHeightTable, byte heightByte, int ix, int iy, float polySize = 24f)
    {
        return new Vector3(ix * polySize, iy * polySize, landHeightTable[heightByte]);
    }

    public const int MapWidth = 255;
    public const int MapHeight = 255;
    public const float CellSize = 24.0f;
    public const int CellsPerBlock = 8;
    public const float RoadWidth = 5.0f;
    public const float BlockLength = 192.0f;
}
