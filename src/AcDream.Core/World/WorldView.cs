using AcDream.Core.Content;

namespace AcDream.Core.World;

public sealed class WorldView
{
    public uint CenterLandblockId { get; }
    public IReadOnlyList<LoadedLandblock> Landblocks { get; }
    public IEnumerable<WorldEntity> AllEntities => Landblocks.SelectMany(lb => lb.Entities);

    private WorldView(uint centerLandblockId, IReadOnlyList<LoadedLandblock> landblocks)
    {
        CenterLandblockId = centerLandblockId;
        Landblocks = landblocks;
    }

    public static WorldView Load(IDatObjectSource dats, uint centerLandblockId)
    {
        var loaded = new List<LoadedLandblock>();
        foreach (var id in NeighborLandblockIds(centerLandblockId))
        {
            var lb = LandblockLoader.Load(dats, id);
            if (lb is not null)
                loaded.Add(lb);
        }
        return new WorldView(centerLandblockId, loaded);
    }

    public static IEnumerable<uint> NeighborLandblockIds(uint centerLandblockId)
    {
        int cx = (int)((centerLandblockId >> 24) & 0xFFu);
        int cy = (int)((centerLandblockId >> 16) & 0xFFu);

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx > 0xFF || ny < 0 || ny > 0xFF)
                    continue;
                yield return (uint)((nx << 24) | (ny << 16) | 0xFFFFu);
            }
        }
    }
}
