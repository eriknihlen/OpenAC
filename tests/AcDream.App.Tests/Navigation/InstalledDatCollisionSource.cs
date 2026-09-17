using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using AcDream.App.Streaming;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatSetup = DatReaderWriter.DBObjs.Setup;

namespace AcDream.App.Tests.Navigation;

/// <summary>Collision read straight from the installed game files, as the client's prepared collision would give it.</summary>
internal sealed class InstalledDatCollisionSource : IPreparedCollisionSource
{
    private readonly IDatReaderWriter _dats;
    private long _probes;
    private long _reads;
    private long _loaded;
    private long _missing;

    public InstalledDatCollisionSource(IDatReaderWriter dats) => _dats = dats;

    public PreparedCollisionSourceStats CollisionStats => new(_probes, _reads, _loaded, _missing, Corrupt: 0);

    public PreparedAssetPresence ProbeCollision(PakAssetType type, uint sourceFileId)
    {
        _probes++;
        bool available = type switch
        {
            PakAssetType.GfxObjCollision => _dats.Get<DatGfxObj>(sourceFileId) is not null,
            PakAssetType.SetupCollision => _dats.Get<DatSetup>(sourceFileId) is not null,
            PakAssetType.CellStructureCollision => TryResolveCell(sourceFileId, out _, out _),
            PakAssetType.EnvCellTopology => TryResolveCell(sourceFileId, out _, out _),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        return available ? PreparedAssetPresence.Available : PreparedAssetPresence.Missing;
    }

    public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
        uint sourceFileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reads++;
        DatGfxObj? value = _dats.Get<DatGfxObj>(sourceFileId);
        return value is null
            ? Missing<FlatGfxObjCollisionAsset>()
            : Loaded(FlatCollisionAssetBuilder.FlattenGfxObj(value));
    }

    public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
        uint sourceFileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reads++;
        DatSetup? value = _dats.Get<DatSetup>(sourceFileId);
        return value is null
            ? Missing<FlatSetupCollision>()
            : Loaded(FlatCollisionAssetBuilder.FlattenSetup(value));
    }

    public PreparedCollisionReadResult<FlatCellStructureCollisionAsset> ReadCellStructureCollision(
        uint sourceFileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reads++;
        return TryResolveCell(sourceFileId, out _, out var structure)
            ? Loaded(FlatCollisionAssetBuilder.FlattenCellStructure(structure))
            : Missing<FlatCellStructureCollisionAsset>();
    }

    public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
        uint sourceFileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _reads++;
        if (!TryResolveCell(sourceFileId, out DatEnvCell? cell, out var structure))
            return Missing<FlatEnvCellTopology>();
        FlatCellStructureCollisionAsset flat = FlatCollisionAssetBuilder.FlattenCellStructure(structure);
        return Loaded(FlatCollisionAssetBuilder.FlattenEnvCellTopology(sourceFileId, cell!, flat.PortalPolygons));
    }

    public void Dispose()
    {
    }

    private bool TryResolveCell(
        uint sourceFileId,
        out DatEnvCell? cell,
        out DatReaderWriter.Types.CellStruct structure)
    {
        cell = _dats.Get<DatEnvCell>(sourceFileId);
        if (cell is not null
            && _dats.Get<DatEnvironment>(0x0D000000u | cell.EnvironmentId) is { } environment
            && environment.Cells.TryGetValue(cell.CellStructure, out var found)
            && found is not null)
        {
            structure = found;
            return true;
        }
        structure = null!;
        return false;
    }

    private PreparedCollisionReadResult<T> Loaded<T>(T value)
        where T : class
    {
        _loaded++;
        return PreparedCollisionReadResult<T>.Loaded(value);
    }

    private PreparedCollisionReadResult<T> Missing<T>()
        where T : class
    {
        _missing++;
        return PreparedCollisionReadResult<T>.Missing;
    }
}
