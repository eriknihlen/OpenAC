using System;
using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct TerrainSurfacePolygon(
    float Z,
    Vector3 Normal,
    TerrainTriangleVertices Vertices);

/// <summary>
/// The terrain surface beneath one point is always exactly one triangle.
/// Keeping its three vertices inline avoids allocating two short arrays for
/// every transition substep while retaining the same vertex order and floats.
/// </summary>
public readonly record struct TerrainTriangleVertices(
    Vector3 V0,
    Vector3 V1,
    Vector3 V2)
{
    public int Length => 3;

    public Vector3 this[int index] => index switch
    {
        0 => V0,
        1 => V1,
        2 => V2,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

public sealed class TerrainSurface
{
    private const int HeightmapSide = 9;
    public const float CellSize = 24f;
    public const int CellsPerSide = 8;  // 192 / 24

    private readonly float[,] _z;               // pre-resolved heights [x, y]
    private readonly bool[,] _cornerIsWater;   // per-VERTEX water flag [x, y] — SurfChar[(type >> 2) & 0x1F]
    private readonly byte[,] _cellWaterType;

    /// <summary>
    /// The block as a whole: 0 = no water, 1 = some water, 2 = every cell
    /// fully under water (open sea). Only a full 8 x 8 block can be sea.
    /// </summary>
    public byte BlockWaterType { get; }

    public bool IsEntirelyWater => BlockWaterType == 2;
    private readonly uint _landblockX;
    private readonly uint _landblockY;

    public TerrainSurface(byte[] heights, float[] heightTable,
        uint landblockX = 0, uint landblockY = 0,
        byte[]? terrainTypes = null)
        : this(
            heights,
            (heightTable
                ?? throw new ArgumentNullException(nameof(heightTable)))
                .AsSpan(),
            landblockX,
            landblockY,
            terrainTypes)
    {
    }

    public TerrainSurface(
        byte[] heights,
        ReadOnlySpan<float> heightTable,
        uint landblockX = 0,
        uint landblockY = 0,
        byte[]? terrainTypes = null)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (heights.Length < 81)
            throw new ArgumentException("heights must have 81 entries", nameof(heights));
        if (heightTable.Length < 256)
            throw new ArgumentException("heightTable must have 256 entries", nameof(heightTable));

        _landblockX = landblockX;
        _landblockY = landblockY;

        // Pre-resolve all 81 heights so SampleZ is a pure lookup + lerp.
        _z = new float[HeightmapSide, HeightmapSide];
        for (int x = 0; x < HeightmapSide; x++)
            for (int y = 0; y < HeightmapSide; y++)
                _z[x, y] = heightTable[heights[x * HeightmapSide + y]];

        _cornerIsWater = new bool[HeightmapSide, HeightmapSide];
        if (terrainTypes is not null && terrainTypes.Length >= 81)
        {
            for (int x = 0; x < HeightmapSide; x++)
                for (int y = 0; y < HeightmapSide; y++)
                {
                    int typeBits = (terrainTypes[x * HeightmapSide + y] >> 2) & 0x1F;
                    _cornerIsWater[x, y] = typeBits >= 0x10 && typeBits <= 0x14;
                }
        }

        _cellWaterType = new byte[CellsPerSide, CellsPerSide];
        for (int cx = 0; cx < CellsPerSide; cx++)
            for (int cy = 0; cy < CellsPerSide; cy++)
            {
                int waterCorners = 0;
                if (_cornerIsWater[cx, cy]) waterCorners++;
                if (_cornerIsWater[cx + 1, cy]) waterCorners++;
                if (_cornerIsWater[cx + 1, cy + 1]) waterCorners++;
                if (_cornerIsWater[cx, cy + 1]) waterCorners++;

                _cellWaterType[cx, cy] = waterCorners switch
                {
                    0 => 0,   // NotWater
                    4 => 2,   // EntirelyWater
                    _ => 1,   // PartiallyWater
                };
            }

        bool anyWater = false;
        bool everyCellFlooded = true;
        for (int cx = 0; cx < CellsPerSide; cx++)
            for (int cy = 0; cy < CellsPerSide; cy++)
            {
                if (_cellWaterType[cx, cy] != 0) anyWater = true;
                if (_cellWaterType[cx, cy] != 2) everyCellFlooded = false;
            }
        BlockWaterType = !anyWater ? (byte)0 : everyCellFlooded ? (byte)2 : (byte)1;
    }

    public float SampleZ(float localX, float localY)
    {
        float fx = Math.Clamp(localX / CellSize, 0f, CellsPerSide - 0.001f);
        float fy = Math.Clamp(localY / CellSize, 0f, CellsPerSide - 0.001f);
        int cx = (int)fx;
        int cy = (int)fy;
        cx = Math.Clamp(cx, 0, CellsPerSide - 1);
        cy = Math.Clamp(cy, 0, CellsPerSide - 1);

        float tx = fx - cx;
        float ty = fy - cy;

        float hBL = _z[cx, cy];
        float hBR = _z[cx + 1, cy];
        float hTR = _z[cx + 1, cy + 1];
        float hTL = _z[cx, cy + 1];

        bool splitSWtoNE = IsSplitSWtoNE(_landblockX, (uint)cx, _landblockY, (uint)cy);

        return InterpolateZInTriangle(hBL, hBR, hTR, hTL, tx, ty, splitSWtoNE);
    }

    public static float SampleZFromHeightmap(
        byte[] heights, float[] heightTable,
        uint landblockX, uint landblockY,
        float localX, float localY)
    {
        ArgumentNullException.ThrowIfNull(heightTable);
        return SampleZFromHeightmap(
            heights,
            heightTable.AsSpan(),
            landblockX,
            landblockY,
            localX,
            localY);
    }

    public static float SampleZFromHeightmap(
        byte[] heights,
        ReadOnlySpan<float> heightTable,
        uint landblockX,
        uint landblockY,
        float localX,
        float localY)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (heights.Length < 81)
            throw new ArgumentException("heights must have 81 entries", nameof(heights));
        if (heightTable.Length < 256)
            throw new ArgumentException("heightTable must have 256 entries", nameof(heightTable));

        float fx = Math.Clamp(localX / CellSize, 0f, CellsPerSide - 0.001f);
        float fy = Math.Clamp(localY / CellSize, 0f, CellsPerSide - 0.001f);
        int cx = (int)fx;
        int cy = (int)fy;
        cx = Math.Clamp(cx, 0, CellsPerSide - 1);
        cy = Math.Clamp(cy, 0, CellsPerSide - 1);

        float tx = fx - cx;
        float ty = fy - cy;

        float hBL = heightTable[heights[cx * HeightmapSide + cy]];
        float hBR = heightTable[heights[(cx + 1) * HeightmapSide + cy]];
        float hTR = heightTable[heights[(cx + 1) * HeightmapSide + (cy + 1)]];
        float hTL = heightTable[heights[cx * HeightmapSide + (cy + 1)]];

        bool splitSWtoNE = IsSplitSWtoNE(landblockX, (uint)cx, landblockY, (uint)cy);
        return InterpolateZInTriangle(hBL, hBR, hTR, hTL, tx, ty, splitSWtoNE);
    }

    public static float SampleNormalZFromHeightmap(
        byte[] heights, float[] heightTable,
        uint landblockX, uint landblockY,
        float localX, float localY)
    {
        ArgumentNullException.ThrowIfNull(heights);
        ArgumentNullException.ThrowIfNull(heightTable);
        if (heights.Length < 81)
            throw new ArgumentException("heights must have 81 entries", nameof(heights));
        if (heightTable.Length < 256)
            throw new ArgumentException("heightTable must have 256 entries", nameof(heightTable));

        float fx = Math.Clamp(localX / CellSize, 0f, CellsPerSide - 0.001f);
        float fy = Math.Clamp(localY / CellSize, 0f, CellsPerSide - 0.001f);
        int cx = (int)fx;
        int cy = (int)fy;
        cx = Math.Clamp(cx, 0, CellsPerSide - 1);
        cy = Math.Clamp(cy, 0, CellsPerSide - 1);

        float tx = fx - cx;
        float ty = fy - cy;

        float hBL = heightTable[heights[cx * HeightmapSide + cy]];
        float hBR = heightTable[heights[(cx + 1) * HeightmapSide + cy]];
        float hTR = heightTable[heights[(cx + 1) * HeightmapSide + (cy + 1)]];
        float hTL = heightTable[heights[cx * HeightmapSide + (cy + 1)]];

        bool splitSWtoNE = IsSplitSWtoNE(landblockX, (uint)cx, landblockY, (uint)cy);

        float dzdx, dzdy;
        if (splitSWtoNE)
        {
            if (tx > ty)
            {
                dzdx = (hBR - hBL) / CellSize;
                dzdy = (hTR - hBR) / CellSize;
            }
            else
            {
                dzdx = (hTR - hTL) / CellSize;
                dzdy = (hTL - hBL) / CellSize;
            }
        }
        else
        {
            if (tx + ty <= 1f)
            {
                dzdx = (hBR - hBL) / CellSize;
                dzdy = (hTL - hBL) / CellSize;
            }
            else
            {
                dzdx = (hTR - hTL) / CellSize;
                dzdy = (hTR - hBR) / CellSize;
            }
        }

        return 1f / MathF.Sqrt(dzdx * dzdx + dzdy * dzdy + 1f);
    }

    private static float InterpolateZInTriangle(
        float hBL, float hBR, float hTR, float hTL,
        float tx, float ty, bool splitSWtoNE)
    {
        if (splitSWtoNE)
        {
            // Diagonal BL(0,0) → TR(1,1) — line y = x.
            // Triangles: {BL,BR,TR} below (tx > ty), {BL,TR,TL} above.
            if (tx > ty)
                return hBL + (hBR - hBL) * tx + (hTR - hBR) * ty;   // BL+BR+TR
            return hBL + (hTR - hTL) * tx + (hTL - hBL) * ty;       // BL+TR+TL
        }
        else
        {
            // Diagonal BR(1,0) → TL(0,1) — line x + y = 1.
            // Triangles: {BL,BR,TL} below (tx+ty <= 1), {BR,TR,TL} above.
            if (tx + ty <= 1f)
                return hBL + (hBR - hBL) * tx + (hTL - hBL) * ty;   // BL+BR+TL
            return hTR + (hTL - hTR) * (1f - tx) + (hBR - hTR) * (1f - ty); // BR+TR+TL
        }
    }

    public (float Z, System.Numerics.Vector3 Normal) SampleSurface(float localX, float localY)
    {
        float fx = Math.Clamp(localX / CellSize, 0f, CellsPerSide - 0.001f);
        float fy = Math.Clamp(localY / CellSize, 0f, CellsPerSide - 0.001f);
        int cx = (int)fx;
        int cy = (int)fy;
        cx = Math.Clamp(cx, 0, CellsPerSide - 1);
        cy = Math.Clamp(cy, 0, CellsPerSide - 1);

        float tx = fx - cx;
        float ty = fy - cy;

        float hBL = _z[cx, cy];
        float hBR = _z[cx + 1, cy];
        float hTR = _z[cx + 1, cy + 1];
        float hTL = _z[cx, cy + 1];

        bool splitSWtoNE = IsSplitSWtoNE(_landblockX, (uint)cx, _landblockY, (uint)cy);

        float z, dzdx, dzdy;

        if (splitSWtoNE)
        {
            // Diagonal BL(0,0) → TR(1,1). Triangles: {BL,BR,TR} / {BL,TR,TL}.
            if (tx > ty)
            {
                // {BL,BR,TR}: Z = hBL + (hBR-hBL)·tx + (hTR-hBR)·ty
                z = hBL + (hBR - hBL) * tx + (hTR - hBR) * ty;
                dzdx = (hBR - hBL) / CellSize;
                dzdy = (hTR - hBR) / CellSize;
            }
            else
            {
                // {BL,TR,TL}: Z = hBL + (hTR-hTL)·tx + (hTL-hBL)·ty
                z = hBL + (hTR - hTL) * tx + (hTL - hBL) * ty;
                dzdx = (hTR - hTL) / CellSize;
                dzdy = (hTL - hBL) / CellSize;
            }
        }
        else
        {
            // Diagonal BR(1,0) → TL(0,1). Triangles: {BL,BR,TL} / {BR,TR,TL}.
            if (tx + ty <= 1f)
            {
                // {BL,BR,TL}: Z = hBL + (hBR-hBL)·tx + (hTL-hBL)·ty
                z = hBL + (hBR - hBL) * tx + (hTL - hBL) * ty;
                dzdx = (hBR - hBL) / CellSize;
                dzdy = (hTL - hBL) / CellSize;
            }
            else
            {
                // {BR,TR,TL}: Z = hTR + (hTL-hTR)(1-tx) + (hBR-hTR)(1-ty)
                // Equivalent linear form: Z = [hBR+hTL-hTR] + (hTR-hTL)·tx + (hTR-hBR)·ty
                z = hTR + (hTL - hTR) * (1f - tx) + (hBR - hTR) * (1f - ty);
                dzdx = (hTR - hTL) / CellSize;
                dzdy = (hTR - hBR) / CellSize;
            }
        }

        var normal = System.Numerics.Vector3.Normalize(
            new System.Numerics.Vector3(-dzdx, -dzdy, 1f));
        return (z, normal);
    }

    public TerrainSurfacePolygon SampleSurfacePolygon(float localX, float localY)
    {
        float fx = Math.Clamp(localX / CellSize, 0f, CellsPerSide - 0.001f);
        float fy = Math.Clamp(localY / CellSize, 0f, CellsPerSide - 0.001f);
        int cx = Math.Clamp((int)fx, 0, CellsPerSide - 1);
        int cy = Math.Clamp((int)fy, 0, CellsPerSide - 1);

        float tx = fx - cx;
        float ty = fy - cy;

        float hBL = _z[cx, cy];
        float hBR = _z[cx + 1, cy];
        float hTR = _z[cx + 1, cy + 1];
        float hTL = _z[cx, cy + 1];

        bool splitSWtoNE = IsSplitSWtoNE(_landblockX, (uint)cx, _landblockY, (uint)cy);

        Vector3 bl = new(cx * CellSize, cy * CellSize, hBL);
        Vector3 br = new((cx + 1) * CellSize, cy * CellSize, hBR);
        Vector3 tr = new((cx + 1) * CellSize, (cy + 1) * CellSize, hTR);
        Vector3 tl = new(cx * CellSize, (cy + 1) * CellSize, hTL);

        float z;
        TerrainTriangleVertices vertices;

        if (splitSWtoNE)
        {
            if (tx > ty)
            {
                z = hBL + (hBR - hBL) * tx + (hTR - hBR) * ty;
                vertices = new TerrainTriangleVertices(bl, br, tr);
            }
            else
            {
                z = hBL + (hTR - hTL) * tx + (hTL - hBL) * ty;
                vertices = new TerrainTriangleVertices(bl, tr, tl);
            }
        }
        else
        {
            if (tx + ty <= 1f)
            {
                z = hBL + (hBR - hBL) * tx + (hTL - hBL) * ty;
                vertices = new TerrainTriangleVertices(bl, br, tl);
            }
            else
            {
                z = hTR + (hTL - hTR) * (1f - tx) + (hBR - hTR) * (1f - ty);
                vertices = new TerrainTriangleVertices(br, tr, tl);
            }
        }

        var normal = Vector3.Normalize(
            Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]));
        if (normal.Z < 0f)
            normal = -normal;

        return new TerrainSurfacePolygon(z, normal, vertices);
    }

    public float SampleWaterDepth(float localX, float localY)
    {
        float fx = Math.Clamp(localX / CellSize, 0f, CellsPerSide - 0.001f);
        float fy = Math.Clamp(localY / CellSize, 0f, CellsPerSide - 0.001f);
        int cx = Math.Clamp((int)fx, 0, CellsPerSide - 1);
        int cy = Math.Clamp((int)fy, 0, CellsPerSide - 1);

        byte waterType = _cellWaterType[cx, cy];
        if (waterType == 0) return 0f;     // NotWater
        if (waterType == 2) return 0.9f;   // EntirelyWater

        float tx = fx - cx;
        float ty = fy - cy;
        int vx = cx + (tx >= 0.5f ? 1 : 0);
        int vy = cy + (ty >= 0.5f ? 1 : 0);

        return _cornerIsWater[vx, vy] ? 0.45f : 0.1f;
    }

    public uint ComputeOutdoorCellId(float localX, float localY)
        => ComputeOutdoorCellLowId(localX, localY);

    public static uint ComputeOutdoorCellLowId(float localX, float localY)
    {
        int cx = Math.Clamp((int)(localX / CellSize), 0, CellsPerSide - 1);
        int cy = Math.Clamp((int)(localY / CellSize), 0, CellsPerSide - 1);
        return (uint)(1 + cx * CellsPerSide + cy);
    }

    public static uint ComputeOutdoorCellId(uint landblockId, float localX, float localY)
        => (landblockId & 0xFFFF0000u) | ComputeOutdoorCellLowId(localX, localY);

    private static bool IsSplitSWtoNE(uint landblockX, uint cellX, uint landblockY, uint cellY)
    {
        uint x = landblockX * 8 + cellX;
        uint y = landblockY * 8 + cellY;
        uint dw = unchecked(x * y * 0x0CCAC033u - x * 0x421BE3BDu + y * 0x6C1AC587u - 0x519B8F25u);
        return (dw & 0x80000000u) != 0;
    }
}
