using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.DBObjs;

namespace AcDream.Core.Audio;

public sealed class AmbientSoundGatherer
{
    public const int CellsPerSide = 8;

    /// <summary>Terrain-word entries per landblock side (a 9×9 vertex grid).</summary>
    private const int VerticesPerSide = 9;

    private const uint NoIndex = 0xFFFFFFFFu;

    private readonly AmbientSoundScheduler _scheduler;

    public AmbientSoundGatherer(AmbientSoundScheduler scheduler) =>
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    public void Rebuild(
        Region region,
        uint viewerLandblockId,
        Vector3 listenerLocalPosition,
        Func<uint, ushort[]?> landblocks,
        double now)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(landblocks);

        _scheduler.BeginRebuild();

        uint viewerX = viewerLandblockId >> 24;
        uint viewerY = (viewerLandblockId >> 16) & 0xFFu;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                long blockX = viewerX + dx;
                long blockY = viewerY + dy;
                if (blockX < 0 || blockX > 0xFF || blockY < 0 || blockY > 0xFF)
                    continue;

                uint landblockId = ((uint)blockX << 24) | ((uint)blockY << 16) | 0xFFFFu;
                ushort[]? terrain = landblocks(landblockId);
                if (terrain is null || terrain.Length < VerticesPerSide * VerticesPerSide)
                    continue;

                ContributeLandblock(
                    region,
                    dx,
                    dy,
                    terrain,
                    listenerLocalPosition);
            }
        }

        _scheduler.EndRebuild(now);
    }

    private void ContributeLandblock(
        Region region,
        int blockDeltaX,
        int blockDeltaY,
        ushort[] terrain,
        Vector3 listenerLocalPosition)
    {
        // The ring neighbour's origin RELATIVE to the listener's own landblock.
        const float landblockLength = CellsPerSide * AmbientSoundConstants.LandCellLength;
        float blockOriginX = blockDeltaX * landblockLength;
        float blockOriginY = blockDeltaY * landblockLength;

        for (int x = 0; x < CellsPerSide; x++)
        {
            for (int y = 0; y < CellsPerSide; y++)
            {
                ushort raw = terrain[(x * VerticesPerSide) + y];
                uint terrainType = (uint)((raw >> 2) & 0x1F);
                uint sceneIndex = (uint)((raw >> 11) & 0x1F);

                if (!TryResolveStbDesc(region, terrainType, sceneIndex, out var stb))
                    continue;

                var offset = new Vector3(
                    blockOriginX + (x * AmbientSoundConstants.LandCellLength)
                        - listenerLocalPosition.X,
                    blockOriginY + (y * AmbientSoundConstants.LandCellLength)
                        - listenerLocalPosition.Y,
                    0f);
                if (offset.LengthSquared() > AmbientSoundConstants.MaxDistanceSq)
                    continue;

                _scheduler.ContributeCell(offset, stb!, static (stb, index) =>
                {
                    var sound = stb.AmbientSounds[index];
                    return new AmbientSoundDescriptor(
                        (SoundId)(uint)sound.SType,
                        sound.Volume,
                        sound.BaseChance,
                        sound.MinRate,
                        sound.MaxRate);
                });
            }
        }
    }

    private static bool TryResolveStbDesc(
        Region region,
        uint terrainType,
        uint sceneIndex,
        out DatReaderWriter.Types.AmbientSTBDesc? stb)
    {
        stb = null;

        var terrainTypes = region.TerrainInfo?.TerrainTypes;
        if (terrainTypes is null || terrainType >= terrainTypes.Count)
            return false;

        var sceneTypes = terrainTypes[(int)terrainType].SceneTypes;
        if (sceneIndex >= sceneTypes.Count)
            return false;

        uint sceneTypeIndex = sceneTypes[(int)sceneIndex];
        var sceneList = region.SceneInfo?.SceneTypes;
        if (sceneTypeIndex == NoIndex || sceneList is null || sceneTypeIndex >= sceneList.Count)
            return false;

        uint stbIndex = sceneList[(int)sceneTypeIndex].StbIndex;
        var descriptors = region.SoundInfo?.STBDesc;
        if (stbIndex == NoIndex || descriptors is null || stbIndex >= descriptors.Count)
            return false;

        var candidate = descriptors[(int)stbIndex];
        if (candidate.STBId == 0 || candidate.AmbientSounds.Count == 0)
            return false;

        stb = candidate;
        return true;
    }
}
