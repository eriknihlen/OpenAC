using AcDream.Content.Pak;
using AcDream.Core.Physics;

namespace AcDream.Content;

public sealed class LayeredPreparedAssetSource :
    IPreparedAssetSource,
    IPreparedCollisionSource
{
    private IPreparedAssetSource? _baseAssets;
    private IPreparedAssetSource? _overlayAssets;
    private IPreparedCollisionSource? _baseCollision;
    private IPreparedCollisionSource? _overlayCollision;

    public LayeredPreparedAssetSource(
        IPreparedAssetSource baseSource,
        IPreparedAssetSource overlaySource)
    {
        ArgumentNullException.ThrowIfNull(baseSource);
        ArgumentNullException.ThrowIfNull(overlaySource);
        if (ReferenceEquals(baseSource, overlaySource))
        {
            throw new ArgumentException(
                "The base and overlay must have independent owners.",
                nameof(overlaySource));
        }

        _baseCollision = baseSource as IPreparedCollisionSource
            ?? throw new ArgumentException(
                "The base source must expose prepared collision payloads.",
                nameof(baseSource));
        _overlayCollision = overlaySource as IPreparedCollisionSource
            ?? throw new ArgumentException(
                "The overlay source must expose prepared collision payloads.",
                nameof(overlaySource));
        _baseAssets = baseSource;
        _overlayAssets = overlaySource;
    }

    public PreparedAssetSourceStats Stats
    {
        get
        {
            IPreparedAssetSource baseSource = Require(_baseAssets);
            IPreparedAssetSource overlay = Require(_overlayAssets);
            PreparedAssetSourceStats left = baseSource.Stats;
            PreparedAssetSourceStats right = overlay.Stats;
            return new(
                left.Probes + right.Probes,
                left.Reads + right.Reads,
                left.Loaded + right.Loaded,
                left.Missing + right.Missing,
                left.Corrupt + right.Corrupt);
        }
    }

    public PreparedCollisionSourceStats CollisionStats
    {
        get
        {
            IPreparedCollisionSource baseSource = Require(_baseCollision);
            IPreparedCollisionSource overlay = Require(_overlayCollision);
            PreparedCollisionSourceStats left = baseSource.CollisionStats;
            PreparedCollisionSourceStats right = overlay.CollisionStats;
            return new(
                left.Probes + right.Probes,
                left.Reads + right.Reads,
                left.Loaded + right.Loaded,
                left.Missing + right.Missing,
                left.Corrupt + right.Corrupt);
        }
    }

    public CacheStats DecodedTextureCacheStats
    {
        get
        {
            CacheStats left = Require(_baseAssets).DecodedTextureCacheStats;
            CacheStats right = Require(_overlayAssets).DecodedTextureCacheStats;
            return new(
                left.Hits + right.Hits,
                left.Misses + right.Misses,
                left.Evictions + right.Evictions);
        }
    }

    public long MappedVirtualBytes =>
        checked(
            Require(_baseAssets).MappedVirtualBytes
            + Require(_overlayAssets).MappedVirtualBytes);

    public PreparedAssetPresence Probe(PakAssetType type, uint sourceFileId)
    {
        PreparedAssetPresence overlay =
            Require(_overlayAssets).Probe(type, sourceFileId);
        return overlay == PreparedAssetPresence.Missing
            ? Require(_baseAssets).Probe(type, sourceFileId)
            : overlay;
    }

    public PreparedAssetReadResult Read(
        in PreparedAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        PreparedAssetReadResult overlay =
            Require(_overlayAssets).Read(request, cancellationToken);
        return overlay.Status == PreparedAssetReadStatus.Missing
            ? Require(_baseAssets).Read(request, cancellationToken)
            : overlay;
    }

    public PreparedAssetPresence ProbeCollision(
        PakAssetType type,
        uint sourceFileId)
    {
        PreparedAssetPresence overlay =
            Require(_overlayCollision).ProbeCollision(type, sourceFileId);
        return overlay == PreparedAssetPresence.Missing
            ? Require(_baseCollision).ProbeCollision(type, sourceFileId)
            : overlay;
    }

    public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
        ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
    {
        PreparedCollisionReadResult<FlatGfxObjCollisionAsset> overlay =
            Require(_overlayCollision).ReadGfxObjCollision(
                sourceFileId,
                cancellationToken);
        return overlay.Status == PreparedAssetReadStatus.Missing
            ? Require(_baseCollision).ReadGfxObjCollision(
                sourceFileId,
                cancellationToken)
            : overlay;
    }

    public PreparedCollisionReadResult<FlatSetupCollision>
        ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
    {
        PreparedCollisionReadResult<FlatSetupCollision> overlay =
            Require(_overlayCollision).ReadSetupCollision(
                sourceFileId,
                cancellationToken);
        return overlay.Status == PreparedAssetReadStatus.Missing
            ? Require(_baseCollision).ReadSetupCollision(
                sourceFileId,
                cancellationToken)
            : overlay;
    }

    public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
        ReadCellStructureCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
    {
        PreparedCollisionReadResult<FlatCellStructureCollisionAsset> overlay =
            Require(_overlayCollision).ReadCellStructureCollision(
                sourceFileId,
                cancellationToken);
        return overlay.Status == PreparedAssetReadStatus.Missing
            ? Require(_baseCollision).ReadCellStructureCollision(
                sourceFileId,
                cancellationToken)
            : overlay;
    }

    public PreparedCollisionReadResult<FlatEnvCellTopology>
        ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
    {
        PreparedCollisionReadResult<FlatEnvCellTopology> overlay =
            Require(_overlayCollision).ReadEnvCellTopology(
                sourceFileId,
                cancellationToken);
        return overlay.Status == PreparedAssetReadStatus.Missing
            ? Require(_baseCollision).ReadEnvCellTopology(
                sourceFileId,
                cancellationToken)
            : overlay;
    }

    public void Dispose()
    {
        IPreparedAssetSource? overlay = Interlocked.Exchange(
            ref _overlayAssets,
            null);
        IPreparedAssetSource? baseSource = Interlocked.Exchange(
            ref _baseAssets,
            null);
        _overlayCollision = null;
        _baseCollision = null;

        List<Exception>? failures = null;
        DisposeOne(overlay, ref failures);
        DisposeOne(baseSource, ref failures);
        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more prepared-content layers failed to dispose.",
                failures);
        }
    }

    private static T Require<T>(T? value)
        where T : class =>
        value ?? throw new ObjectDisposedException(
            nameof(LayeredPreparedAssetSource));

    private static void DisposeOne(
        IDisposable? value,
        ref List<Exception>? failures)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            value.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
    }
}
