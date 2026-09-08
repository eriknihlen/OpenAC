using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Physics;

namespace AcDream.App.Physics;

internal sealed class LiveEntityPvpBitfieldSync : IDisposable
{
    private readonly ClientObjectTable _objects;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ShadowObjectRegistry _shadows;
    private bool _disposed;

    public LiveEntityPvpBitfieldSync(
        ClientObjectTable objects,
        LiveEntityRuntime liveEntities,
        ShadowObjectRegistry shadows)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _objects.ObjectUpdated += OnObjectUpdated;
    }

    private void OnObjectUpdated(ClientObject item)
    {
        if (item.PublicWeenieBitfield is not { } bitfield)
            return;
        if (!_liveEntities.TryGetRecord(item.ObjectId, out LiveEntityRecord record)
            || record.WorldEntity is not { } entity)
        {
            return;
        }
        _shadows.UpdatePwdBitfieldFlags(entity.Id, bitfield);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _objects.ObjectUpdated -= OnObjectUpdated;
    }
}
