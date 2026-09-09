using System.Collections.Generic;
using AcDream.Core.Terrain;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public abstract record LandblockStreamJob(uint LandblockId)
{
    public sealed record Load(
        uint LandblockId,
        LandblockStreamJobKind Kind,
        ulong Generation = 0,
        LandblockBuildOrigin Origin = default) : LandblockStreamJob(LandblockId)
    {
        public LandblockBuildRequest Request =>
            new(LandblockId, Kind, Generation, Origin);
    }
    public sealed record Unload(
        uint LandblockId,
        ulong Generation = 0) : LandblockStreamJob(LandblockId);

    public sealed record ClearLoads() : LandblockStreamJob(0);
}

/// <summary>
/// Outbox record the render thread drains. Either a successful load, a
/// failed load (logged and ignored until region recenters off/back), or
/// an unload notification (tells the render thread to release GPU state
/// for this landblock id).
/// </summary>
public abstract record LandblockStreamResult(uint LandblockId, ulong Generation)
{
    public sealed record Loaded(
        uint LandblockId,
        LandblockStreamTier Tier,
        LandblockBuild Build,
        LandblockMeshData MeshData,
        ulong Generation = 0
    ) : LandblockStreamResult(LandblockId, Generation)
    {
        public Loaded(
            uint landblockId,
            LandblockStreamTier tier,
            LoadedLandblock landblock,
            LandblockMeshData meshData,
            ulong generation = 0)
            : this(landblockId, tier, new LandblockBuild(landblock), meshData, generation)
        {
        }

        public LoadedLandblock Landblock => Build.Landblock;
    }

    public sealed record Promoted(
        uint LandblockId,
        LandblockBuild Build,
        LandblockMeshData MeshData,
        ulong Generation = 0
    ) : LandblockStreamResult(LandblockId, Generation)
    {
        public Promoted(
            uint landblockId,
            LoadedLandblock landblock,
            LandblockMeshData meshData,
            ulong generation = 0)
            : this(landblockId, new LandblockBuild(landblock), meshData, generation)
        {
        }

        public LoadedLandblock Landblock => Build.Landblock;
        public IReadOnlyList<WorldEntity> Entities => Landblock.Entities;
    }

    public sealed record Failed(
        uint LandblockId,
        string Error,
        ulong Generation = 0) : LandblockStreamResult(LandblockId, Generation);
    public sealed record Unloaded(
        uint LandblockId,
        ulong Generation = 0) : LandblockStreamResult(LandblockId, Generation);

    public sealed record WorkerCrashed(string Error) : LandblockStreamResult(0, 0);
}
