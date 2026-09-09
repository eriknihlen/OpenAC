using AcDream.Content.Pak;
using AcDream.Core.Physics;

namespace AcDream.Content;

public readonly record struct PreparedCollisionSourceStats(
    long Probes,
    long Reads,
    long Loaded,
    long Missing,
    long Corrupt);

public readonly record struct PreparedCollisionReadResult<T>(
    PreparedAssetReadStatus Status,
    T? Data)
    where T : class
{
    public static PreparedCollisionReadResult<T> Missing =>
        new(PreparedAssetReadStatus.Missing, null);

    public static PreparedCollisionReadResult<T> Corrupt =>
        new(PreparedAssetReadStatus.Corrupt, null);

    public static PreparedCollisionReadResult<T> Loaded(T data) =>
        new(PreparedAssetReadStatus.Loaded, data);
}

public interface IPreparedCollisionSource : IDisposable
{
    PreparedAssetPresence ProbeCollision(
        PakAssetType type,
        uint sourceFileId);

    PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
        ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default);

    PreparedCollisionReadResult<FlatSetupCollision>
        ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default);

    PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
        ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default);

    PreparedCollisionReadResult<FlatEnvCellTopology>
        ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default);

    PreparedCollisionSourceStats CollisionStats { get; }
}

internal static class PreparedCollisionTypeContract
{
    public static void Validate(PakAssetType type)
    {
        if (type is not (
            PakAssetType.GfxObjCollision or
            PakAssetType.SetupCollision or
            PakAssetType.CellStructureCollision or
            PakAssetType.EnvCellTopology))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "unknown prepared collision asset type");
        }
    }
}

public sealed partial class PakPreparedAssetSource
{
    private long _collisionProbes;
    private long _collisionReads;
    private long _collisionLoaded;
    private long _collisionMissing;
    private long _collisionCorrupt;

    public PreparedCollisionSourceStats CollisionStats =>
        new(
            Volatile.Read(ref _collisionProbes),
            Volatile.Read(ref _collisionReads),
            Volatile.Read(ref _collisionLoaded),
            Volatile.Read(ref _collisionMissing),
            Volatile.Read(ref _collisionCorrupt));

    public PreparedAssetPresence ProbeCollision(
        PakAssetType type,
        uint sourceFileId)
    {
        PreparedCollisionTypeContract.Validate(type);
        Interlocked.Increment(ref _collisionProbes);
        ulong key = PakKey.Compose(type, sourceFileId);
        return _reader.ProbeEntry(key) switch
        {
            PakEntryState.Available => PreparedAssetPresence.Available,
            PakEntryState.Corrupt => PreparedAssetPresence.Corrupt,
            _ => PreparedAssetPresence.Missing,
        };
    }

    public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
        ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        ReadCollision(
            PakAssetType.GfxObjCollision,
            sourceFileId,
            static (bytes, token) =>
                FlatCollisionAssetSerializer.DeserializeGfxObj(bytes, token),
            cancellationToken);

    public PreparedCollisionReadResult<FlatSetupCollision>
        ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        ReadCollision(
            PakAssetType.SetupCollision,
            sourceFileId,
            static (bytes, token) =>
                FlatCollisionAssetSerializer.DeserializeSetup(bytes, token),
            cancellationToken);

    public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
        ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        ReadCollision(
            PakAssetType.CellStructureCollision,
            sourceFileId,
            static (bytes, token) =>
                FlatCollisionAssetSerializer.DeserializeCellStructure(
                    bytes,
                    token),
            cancellationToken);

    public PreparedCollisionReadResult<FlatEnvCellTopology>
        ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default) =>
        ReadCollision(
            PakAssetType.EnvCellTopology,
            sourceFileId,
            static (bytes, token) =>
                FlatCollisionAssetSerializer.DeserializeEnvCellTopology(
                    bytes,
                    token),
            cancellationToken);

    private PreparedCollisionReadResult<T> ReadCollision<T>(
        PakAssetType type,
        uint sourceFileId,
        Func<byte[], CancellationToken, T> deserialize,
        CancellationToken cancellationToken)
        where T : class
    {
        PreparedCollisionTypeContract.Validate(type);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _collisionReads);

        ulong key = PakKey.Compose(type, sourceFileId);
        PakObjectReadStatus status =
            _reader.ReadBlobBytes(key, out byte[]? bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (status == PakObjectReadStatus.Missing)
        {
            Interlocked.Increment(ref _collisionMissing);
            return PreparedCollisionReadResult<T>.Missing;
        }
        if (status == PakObjectReadStatus.Corrupt || bytes is null)
        {
            Interlocked.Increment(ref _collisionCorrupt);
            return PreparedCollisionReadResult<T>.Corrupt;
        }

        try
        {
            T data = deserialize(bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _collisionLoaded);
            return PreparedCollisionReadResult<T>.Loaded(data);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _reader.MarkPayloadCorrupt(
                key,
                $"collision deserialization failed despite matching CRC: " +
                $"{exception.GetType().Name}: {exception.Message}");
            Interlocked.Increment(ref _collisionCorrupt);
            return PreparedCollisionReadResult<T>.Corrupt;
        }
    }
}
