using DatReaderWriter.DBObjs;

namespace AcDream.Core.World;

public sealed record LoadedLandblock(
    uint LandblockId,
    LandBlock Heightmap,
    IReadOnlyList<WorldEntity> Entities,
    PhysicsDatBundle? PhysicsDats = null);
