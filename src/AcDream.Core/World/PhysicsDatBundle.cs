using System.Collections.Generic;
using DatReaderWriter.DBObjs;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.Core.World;

public sealed record PhysicsDatBundle(
    LandBlockInfo? Info,
    IReadOnlyDictionary<uint, EnvCell> EnvCells,
    IReadOnlyDictionary<uint, DatEnvironment> Environments,
    IReadOnlyDictionary<uint, Setup> Setups,
    IReadOnlyDictionary<uint, GfxObj> GfxObjs)
{
    public static readonly PhysicsDatBundle Empty = new(
        null,
        new Dictionary<uint, EnvCell>(),
        new Dictionary<uint, DatEnvironment>(),
        new Dictionary<uint, Setup>(),
        new Dictionary<uint, GfxObj>());

    public PhysicsDatBundle WithoutCollisionGraphs() => new(
        Info,
        EnvCells,
        Empty.Environments,
        Setups,
        Empty.GfxObjs);
}
