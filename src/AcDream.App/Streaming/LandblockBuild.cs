using AcDream.App.Rendering.Wb;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public readonly record struct LandblockTerrainBounds(float MaxZ, float MinZ);

public sealed record LandblockBuild(
    LoadedLandblock Landblock,
    EnvCellLandblockBuild? EnvCells = null,
    LandblockBuildOrigin Origin = default,
    LandblockCollisionBuild? Collisions = null,
    LandblockTerrainBounds TerrainBounds = default)
{
    public uint LandblockId => Landblock.LandblockId;
}
