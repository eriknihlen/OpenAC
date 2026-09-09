using System.Collections.Immutable;
using AcDream.Core.Physics;

namespace AcDream.Core.World;

public sealed record LandblockCollisionBuild(
    ImmutableDictionary<uint, FlatGfxObjCollisionAsset> GfxObjs,
    ImmutableDictionary<uint, FlatSetupCollision> Setups,
    ImmutableDictionary<uint, FlatCellStructureCollisionAsset> CellStructures,
    ImmutableDictionary<uint, FlatEnvCellTopology> EnvCells,
    ImmutableArray<uint> GfxObjIds,
    ImmutableArray<uint> SetupIds,
    ImmutableArray<uint> EnvCellIds)
{
    public static readonly LandblockCollisionBuild Empty = new(
        ImmutableDictionary<uint, FlatGfxObjCollisionAsset>.Empty,
        ImmutableDictionary<uint, FlatSetupCollision>.Empty,
        ImmutableDictionary<uint, FlatCellStructureCollisionAsset>.Empty,
        ImmutableDictionary<uint, FlatEnvCellTopology>.Empty,
        ImmutableArray<uint>.Empty,
        ImmutableArray<uint>.Empty,
        ImmutableArray<uint>.Empty);
}
