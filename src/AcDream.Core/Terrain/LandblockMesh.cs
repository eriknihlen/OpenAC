using System.Numerics;
using DatReaderWriter.DBObjs;

namespace AcDream.Core.Terrain;

public sealed record LandblockMeshData(TerrainVertex[] Vertices, uint[] Indices);

public static class LandblockMesh
{
    public const int HeightmapSide = 9;                // 9×9 heightmap samples
    public const int CellsPerSide = 8;
    public const int VerticesPerCell = 6;              // two triangles
    public const int VerticesPerLandblock = CellsPerSide * CellsPerSide * VerticesPerCell;  // 384
    public const float CellSize = 24.0f;
    public const float LandblockSize = CellsPerSide * CellSize;  // 192

    private const int RoadMask = 0x3;
    private const int TypeShift = 2;
    private const int TypeMask = 0x1F;

    public static LandblockMeshData Build(
        LandBlock block,
        uint landblockX,
        uint landblockY,
        float[] heightTable,
        TerrainBlendingContext ctx,
        System.Collections.Generic.IDictionary<uint, SurfaceInfo> surfaceCache)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(heightTable);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(surfaceCache);
        if (heightTable.Length < 256)
            throw new ArgumentException("heightTable must have 256 entries", nameof(heightTable));

        var heights = new float[HeightmapSide, HeightmapSide];
        for (int x = 0; x < HeightmapSide; x++)
        for (int y = 0; y < HeightmapSide; y++)
            heights[x, y] = heightTable[block.Height[x * HeightmapSide + y]];

        var normals = BuildRetailVertexNormals(
            heights,
            landblockX,
            landblockY);

        var vertices = new TerrainVertex[VerticesPerLandblock];
        var indices = new uint[VerticesPerLandblock];  // 1 index per vertex (no deduplication)

        int vi = 0;
        for (int cy = 0; cy < CellsPerSide; cy++)
        {
            for (int cx = 0; cx < CellsPerSide; cx++)
            {
                var tBL = block.Terrain[cx * HeightmapSide + cy];
                var tBR = block.Terrain[(cx + 1) * HeightmapSide + cy];
                var tTR = block.Terrain[(cx + 1) * HeightmapSide + (cy + 1)];
                var tTL = block.Terrain[cx * HeightmapSide + (cy + 1)];

                int rBL = tBL.Road & RoadMask;
                int rBR = tBR.Road & RoadMask;
                int rTR = tTR.Road & RoadMask;
                int rTL = tTL.Road & RoadMask;
                int ttBL = (int)tBL.Type & TypeMask;
                int ttBR = (int)tBR.Type & TypeMask;
                int ttTR = (int)tTR.Type & TypeMask;
                int ttTL = (int)tTL.Type & TypeMask;

                uint palCode = TerrainBlending.GetPalCode(
                    rBL, rBR, rTR, rTL, ttBL, ttBR, ttTR, ttTL);

                if (!surfaceCache.TryGetValue(palCode, out var surf))
                {
                    surf = TerrainBlending.BuildSurface(palCode, ctx);
                    surfaceCache[palCode] = surf;
                }

                var split = TerrainBlending.CalculateSplitDirection(
                    landblockX, (uint)cx, landblockY, (uint)cy);

                var (d0, d1, d2, d3) = TerrainBlending.FillCellData(surf, split);

                var posBL = new Vector3( cx      * CellSize,  cy      * CellSize, heights[cx,     cy    ]);
                var posBR = new Vector3((cx + 1) * CellSize,  cy      * CellSize, heights[cx + 1, cy    ]);
                var posTR = new Vector3((cx + 1) * CellSize, (cy + 1) * CellSize, heights[cx + 1, cy + 1]);
                var posTL = new Vector3( cx      * CellSize, (cy + 1) * CellSize, heights[cx,     cy + 1]);

                var nBL = normals[cx,     cy];
                var nBR = normals[cx + 1, cy];
                var nTR = normals[cx + 1, cy + 1];
                var nTL = normals[cx,     cy + 1];

                if (split == CellSplitDirection.SWtoNE)
                {
                    WriteCell(vertices, ref vi, d0, d1, d2, d3,
                        posBL, nBL, posBR, nBR, posTR, nTR,
                        posBL, nBL, posTR, nTR, posTL, nTL);
                }
                else
                {
                    WriteCell(vertices, ref vi, d0, d1, d2, d3,
                        posBL, nBL, posBR, nBR, posTL, nTL,
                        posBR, nBR, posTR, nTR, posTL, nTL);
                }
            }
        }

        // Indices are trivial 0..383 since we don't deduplicate verts.
        for (uint i = 0; i < VerticesPerLandblock; i++)
            indices[i] = i;

        return new LandblockMeshData(vertices, indices);
    }

    private static Vector3[,] BuildRetailVertexNormals(
        float[,] heights,
        uint landblockX,
        uint landblockY)
    {
        var normalSums = new Vector3[HeightmapSide, HeightmapSide];

        for (int cy = 0; cy < CellsPerSide; cy++)
        {
            for (int cx = 0; cx < CellsPerSide; cx++)
            {
                var posBL = new Vector3( cx      * CellSize,  cy      * CellSize, heights[cx,     cy    ]);
                var posBR = new Vector3((cx + 1) * CellSize,  cy      * CellSize, heights[cx + 1, cy    ]);
                var posTR = new Vector3((cx + 1) * CellSize, (cy + 1) * CellSize, heights[cx + 1, cy + 1]);
                var posTL = new Vector3( cx      * CellSize, (cy + 1) * CellSize, heights[cx,     cy + 1]);

                var split = TerrainBlending.CalculateSplitDirection(
                    landblockX, (uint)cx, landblockY, (uint)cy);

                if (split == CellSplitDirection.SWtoNE)
                {
                    AccumulateFaceNormal(
                        normalSums,
                        posBL, cx,     cy,
                        posBR, cx + 1, cy,
                        posTR, cx + 1, cy + 1);
                    AccumulateFaceNormal(
                        normalSums,
                        posBL, cx,     cy,
                        posTR, cx + 1, cy + 1,
                        posTL, cx,     cy + 1);
                }
                else
                {
                    AccumulateFaceNormal(
                        normalSums,
                        posBL, cx,     cy,
                        posBR, cx + 1, cy,
                        posTL, cx,     cy + 1);
                    AccumulateFaceNormal(
                        normalSums,
                        posBR, cx + 1, cy,
                        posTR, cx + 1, cy + 1,
                        posTL, cx,     cy + 1);
                }
            }
        }

        var normals = new Vector3[HeightmapSide, HeightmapSide];
        for (int x = 0; x < HeightmapSide; x++)
        {
            for (int y = 0; y < HeightmapSide; y++)
            {
                Vector3 sum = normalSums[x, y];
                normals[x, y] = sum.LengthSquared() > 0f
                    ? Vector3.Normalize(sum)
                    : Vector3.UnitZ;
            }
        }

        return normals;
    }

    private static void AccumulateFaceNormal(
        Vector3[,] normalSums,
        Vector3 p0, int x0, int y0,
        Vector3 p1, int x1, int y1,
        Vector3 p2, int x2, int y2)
    {
        Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
        if (cross.LengthSquared() <= 0f)
            return;

        Vector3 faceNormal = Vector3.Normalize(cross);
        normalSums[x0, y0] += faceNormal;
        normalSums[x1, y1] += faceNormal;
        normalSums[x2, y2] += faceNormal;
    }

    private static void WriteCell(
        TerrainVertex[] verts, ref int vi,
        uint d0, uint d1, uint d2, uint d3,
        Vector3 p0, Vector3 n0,
        Vector3 p1, Vector3 n1,
        Vector3 p2, Vector3 n2,
        Vector3 p3, Vector3 n3,
        Vector3 p4, Vector3 n4,
        Vector3 p5, Vector3 n5)
    {
        verts[vi++] = new TerrainVertex(p0, n0, d0, d1, d2, d3);
        verts[vi++] = new TerrainVertex(p1, n1, d0, d1, d2, d3);
        verts[vi++] = new TerrainVertex(p2, n2, d0, d1, d2, d3);
        verts[vi++] = new TerrainVertex(p3, n3, d0, d1, d2, d3);
        verts[vi++] = new TerrainVertex(p4, n4, d0, d1, d2, d3);
        verts[vi++] = new TerrainVertex(p5, n5, d0, d1, d2, d3);
    }
}
