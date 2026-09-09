using AcDream.Content;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Physics;

internal sealed class LiveCollisionAssetPublisher
{
    private readonly PhysicsDataCache _cache;
    private readonly IPreparedCollisionSource _source;

    public LiveCollisionAssetPublisher(
        PhysicsDataCache cache,
        IPreparedCollisionSource source)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public void CacheGfxObj(uint id, GfxObj gfxObj)
    {
        ArgumentNullException.ThrowIfNull(gfxObj);
        FlatGfxObjCollisionAsset? prepared =
            _cache.GetFlatGfxObj(id) is not null
                ? null
                : Require(
                    _source.ReadGfxObjCollision(id),
                    "GfxObj collision",
                    id);
        _cache.CacheGfxObj(id, gfxObj, prepared);
    }

    public void CacheSetup(uint id, Setup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        FlatSetupCollision? prepared =
            _cache.GetFlatSetup(id) is not null
                ? null
                : Require(
                    _source.ReadSetupCollision(id),
                    "Setup collision",
                    id);
        _cache.CacheSetup(id, setup, prepared);
    }

    private static T Require<T>(
        PreparedCollisionReadResult<T> result,
        string kind,
        uint id)
        where T : class
    {
        if (result.Status == PreparedAssetReadStatus.Loaded &&
            result.Data is not null)
        {
            return result.Data;
        }

        throw new InvalidDataException(
            $"{kind} 0x{id:X8} is {result.Status}. " +
            "Live collision cannot publish only one representation.");
    }
}
