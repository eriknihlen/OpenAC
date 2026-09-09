namespace AcDream.Core.Terrain;

public static class TerrainBlending
{

    public static uint GetPalCode(
        int r1, int r2, int r3, int r4,
        int t1, int t2, int t3, int t4)
    {
        uint terrainBits = (uint)((t1 << 15) | (t2 << 10) | (t3 << 5) | t4);
        uint roadBits    = (uint)((r1 << 26) | (r2 << 24) | (r3 << 22) | (r4 << 20));
        uint sizeBits    = 1u << 28;
        return sizeBits | roadBits | terrainBits;
    }

    public static CellSplitDirection CalculateSplitDirection(
        uint landblockX, uint cellX, uint landblockY, uint cellY)
    {
        uint x = landblockX * 8 + cellX;
        uint y = landblockY * 8 + cellY;
        uint dw = unchecked(x * y * 0x0CCAC033u - x * 0x421BE3BDu + y * 0x6C1AC587u - 0x519B8F25u);
        return (dw & 0x80000000u) != 0
            ? CellSplitDirection.SWtoNE
            : CellSplitDirection.SEtoNW;
    }


    public static uint[] ExtractTerrainCodes(uint palCode) => new uint[]
    {
        (palCode >> 15) & 0x1Fu,
        (palCode >> 10) & 0x1Fu,
        (palCode >>  5) & 0x1Fu,
         palCode        & 0x1Fu,
    };

    public static int PseudoRandomIndex(uint palCode, int count)
    {
        if (count <= 0) return 0;
        int pseudoRandom = (int)Math.Floor((1379576222 * palCode - 1372186442) * 2.3283064e-10 * count);
        return pseudoRandom >= count ? 0 : pseudoRandom;
    }

    public static uint RotateTerrainCode(uint code)
    {
        code *= 2;
        return code >= 16 ? code - 15 : code;
    }

    public static (uint road0, uint road1, bool allRoad) GetRoadCodes(uint palCode)
    {
        uint mask = 0;
        if ((palCode & 0x0C000000u) != 0) mask |= 1;  // r1 in bits 26-27
        if ((palCode & 0x03000000u) != 0) mask |= 2;  // r2 in bits 24-25
        if ((palCode & 0x00C00000u) != 0) mask |= 4;  // r3 in bits 22-23
        if ((palCode & 0x00300000u) != 0) mask |= 8;  // r4 in bits 20-21

        if (mask == 0xF)
            return (0, 0, allRoad: true);

        return mask switch
        {
            0x0 => (0, 0, false),
            0xE => (6, 12, false),
            0xD => (9, 12, false),
            0xB => (9, 3, false),
            0x7 => (3, 6, false),
            _   => (mask, 0, false),
        };
    }

    public static (byte alphaLayer, byte rotation) FindTerrainAlpha(
        uint palCode, uint terrainCode, TerrainBlendingContext ctx)
    {
        bool isCornerTerrain = terrainCode is 1 or 2 or 4 or 8;
        var maps      = isCornerTerrain ? ctx.CornerAlphaLayers   : ctx.SideAlphaLayers;
        var tCodes    = isCornerTerrain ? ctx.CornerAlphaTCodes   : ctx.SideAlphaTCodes;

        if (maps.Count == 0 || tCodes.Count == 0)
            return (SurfaceInfo.None, 0);

        int randomIndex = PseudoRandomIndex(palCode, maps.Count);
        uint currentCode = tCodes[randomIndex];
        int rotationCount = 0;
        while (currentCode != terrainCode && rotationCount < 4)
        {
            currentCode = RotateTerrainCode(currentCode);
            rotationCount++;
        }

        if (rotationCount >= 4)
            return (SurfaceInfo.None, 0);

        return (maps[randomIndex], (byte)rotationCount);
    }

    public static (byte alphaLayer, byte rotation) FindRoadAlpha(
        uint palCode, uint roadCode, TerrainBlendingContext ctx)
    {
        var maps = ctx.RoadAlphaLayers;
        var rCodes = ctx.RoadAlphaRCodes;
        if (maps.Count == 0 || rCodes.Count == 0)
            return (SurfaceInfo.None, 0);

        int randomIndex = PseudoRandomIndex(palCode, maps.Count);

        for (int i = 0; i < maps.Count; i++)
        {
            int index = (i + randomIndex) % maps.Count;
            uint currentCode = rCodes[index];
            for (int rotationCount = 0; rotationCount < 4; rotationCount++)
            {
                if (currentCode == roadCode)
                    return (maps[index], (byte)rotationCount);
                currentCode = RotateTerrainCode(currentCode);
            }
        }

        return (SurfaceInfo.None, 0);
    }

    public static SurfaceInfo BuildSurface(uint palCode, TerrainBlendingContext ctx)
    {
        var terrainCorners = ExtractTerrainCodes(palCode);  // t1..t4
        var (road0, road1, allRoad) = GetRoadCodes(palCode);

        if (allRoad)
        {
            return SurfaceInfo.BaseOnly(ctx.RoadLayer);
        }

        byte[] terrainLayers = new byte[4];
        uint[] overlayCodes = new uint[3];
        BuildOverlayLayers(terrainCorners, ctx, terrainLayers, overlayCodes);

        byte baseLayer = terrainLayers[0];

        byte ovl0Layer = SurfaceInfo.None, ovl0Alpha = SurfaceInfo.None, ovl0Rot = 0;
        byte ovl1Layer = SurfaceInfo.None, ovl1Alpha = SurfaceInfo.None, ovl1Rot = 0;
        byte ovl2Layer = SurfaceInfo.None, ovl2Alpha = SurfaceInfo.None, ovl2Rot = 0;

        if (overlayCodes[0] != 0)
        {
            var (a, r) = FindTerrainAlpha(palCode, overlayCodes[0], ctx);
            if (a != SurfaceInfo.None)
            {
                ovl0Layer = terrainLayers[1]; ovl0Alpha = a; ovl0Rot = r;
            }
        }
        if (overlayCodes[1] != 0)
        {
            var (a, r) = FindTerrainAlpha(palCode, overlayCodes[1], ctx);
            if (a != SurfaceInfo.None)
            {
                ovl1Layer = terrainLayers[2]; ovl1Alpha = a; ovl1Rot = r;
            }
        }
        if (overlayCodes[2] != 0)
        {
            var (a, r) = FindTerrainAlpha(palCode, overlayCodes[2], ctx);
            if (a != SurfaceInfo.None)
            {
                ovl2Layer = terrainLayers[3]; ovl2Alpha = a; ovl2Rot = r;
            }
        }

        // Road overlays: share the road terrain layer, differ in alpha and rotation.
        byte roadLayer = SurfaceInfo.None;
        byte road0Alpha = SurfaceInfo.None, road0Rot = 0;
        byte road1Alpha = SurfaceInfo.None, road1Rot = 0;
        if (ctx.RoadLayer != SurfaceInfo.None && road0 != 0)
        {
            var (a0, r0) = FindRoadAlpha(palCode, road0, ctx);
            if (a0 != SurfaceInfo.None)
            {
                roadLayer = ctx.RoadLayer;
                road0Alpha = a0;
                road0Rot = r0;
                if (road1 != 0)
                {
                    var (a1, r1) = FindRoadAlpha(palCode, road1, ctx);
                    if (a1 != SurfaceInfo.None)
                    {
                        road1Alpha = a1;
                        road1Rot = r1;
                    }
                }
            }
        }

        return new SurfaceInfo(
            baseLayer,
            ovl0Layer, ovl0Alpha, ovl0Rot,
            ovl1Layer, ovl1Alpha, ovl1Rot,
            ovl2Layer, ovl2Alpha, ovl2Rot,
            roadLayer, road0Alpha, road0Rot,
            road1Alpha, road1Rot);
    }

    private static void BuildOverlayLayers(
        uint[] terrainCorners,
        TerrainBlendingContext ctx,
        byte[] terrainLayers,
        uint[] overlayCodes)
    {
        // Find first duplicate pair — if any exists, use the duplicate path.
        for (int i = 0; i < 4; i++)
        {
            for (int j = i + 1; j < 4; j++)
            {
                if (terrainCorners[i] == terrainCorners[j])
                {
                    BuildWithDuplicates(terrainCorners, ctx, terrainLayers, overlayCodes, i);
                    return;
                }
            }
        }

        for (int i = 0; i < 4; i++)
            terrainLayers[i] = LookupTerrainLayer(ctx, terrainCorners[i]);
        overlayCodes[0] = 2;
        overlayCodes[1] = 4;
        overlayCodes[2] = 8;
    }

    private static void BuildWithDuplicates(
        uint[] terrainCorners,
        TerrainBlendingContext ctx,
        byte[] terrainLayers,
        uint[] overlayCodes,
        int duplicateIndex)
    {
        uint primaryTerrain = terrainCorners[duplicateIndex];
        uint secondaryTerrain = 0;

        terrainLayers[0] = LookupTerrainLayer(ctx, primaryTerrain);
        terrainLayers[1] = SurfaceInfo.None;
        terrainLayers[2] = SurfaceInfo.None;
        terrainLayers[3] = SurfaceInfo.None;
        overlayCodes[0] = 0;
        overlayCodes[1] = 0;
        overlayCodes[2] = 0;
        bool foundSecondary = false;

        for (int k = 0; k < 4; k++)
        {
            if (primaryTerrain == terrainCorners[k]) continue;

            if (!foundSecondary)
            {
                overlayCodes[0] = (uint)(1 << k);
                secondaryTerrain = terrainCorners[k];
                terrainLayers[1] = LookupTerrainLayer(ctx, secondaryTerrain);
                foundSecondary = true;
            }
            else
            {
                if (secondaryTerrain == terrainCorners[k]
                    && overlayCodes[0] == (1u << (k - 1)))
                {
                    overlayCodes[0] += (uint)(1 << k);
                }
                else
                {
                    terrainLayers[2] = LookupTerrainLayer(ctx, terrainCorners[k]);
                    overlayCodes[1] = (uint)(1 << k);
                }
                break;
            }
        }
    }

    private static byte LookupTerrainLayer(TerrainBlendingContext ctx, uint terrainCode)
    {
        return ctx.TerrainTypeToLayer.TryGetValue(terrainCode, out var layer)
            ? layer
            : SurfaceInfo.None;
    }

    public static (uint d0, uint d1, uint d2, uint d3) FillCellData(
        SurfaceInfo s, CellSplitDirection split)
    {
        uint texBase    = s.BaseLayer;
        uint alphaBase  = SurfaceInfo.None;  // unused, always 255

        uint texOvl0    = s.Ovl0Layer;
        uint alphaOvl0  = s.Ovl0AlphaLayer;
        uint rotOvl0    = s.Ovl0Rotation;

        uint texOvl1    = s.Ovl1Layer;
        uint alphaOvl1  = s.Ovl1AlphaLayer;
        uint rotOvl1    = s.Ovl1Rotation;

        uint texOvl2    = s.Ovl2Layer;
        uint alphaOvl2  = s.Ovl2AlphaLayer;
        uint rotOvl2    = s.Ovl2Rotation;

        // Road0 and Road1 share s.RoadLayer (same road texture, different alpha masks).
        uint texRd0     = s.RoadLayer;
        uint alphaRd0   = s.Road0AlphaLayer;
        uint rotRd0     = s.Road0Rotation;

        uint texRd1     = s.Road1AlphaLayer == SurfaceInfo.None ? SurfaceInfo.None : s.RoadLayer;
        uint alphaRd1   = s.Road1AlphaLayer;
        uint rotRd1     = s.Road1Rotation;

        uint d0 = (texBase | (alphaBase << 8)) | ((texOvl0 | (alphaOvl0 << 8)) << 16);
        uint d1 = (texOvl1 | (alphaOvl1 << 8)) | ((texOvl2 | (alphaOvl2 << 8)) << 16);
        uint d2 = (texRd0  | (alphaRd0  << 8)) | ((texRd1  | (alphaRd1  << 8)) << 16);
        uint d3 =                    0u                 // rotBase in bits 0-1 (unused)
                  | (rotOvl0 << 2)
                  | (rotOvl1 << 4)
                  | (rotOvl2 << 6)
                  | (rotRd0  << 8)
                  | (rotRd1  << 10)
                  | ((((uint)split) & 1u) << 12);

        return (d0, d1, d2, d3);
    }
}
