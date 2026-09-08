using AcDream.Core.Items;
using AcDream.Core.Net;

namespace AcDream.Runtime.Entities;

internal sealed class RuntimeEntityPvpBitfieldSnapshotSync : IDisposable
{
    private readonly RuntimeEntityDirectory _entities;
    private readonly ClientObjectTable _objects;
    private bool _disposed;

    public RuntimeEntityPvpBitfieldSnapshotSync(
        RuntimeEntityDirectory entities,
        ClientObjectTable objects)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _objects.ObjectUpdated += OnObjectUpdated;
    }

    private void OnObjectUpdated(ClientObject item)
    {
        if (item.PublicWeenieBitfield is not { } bitfield)
            return;
        if (!_entities.TryRefreshObjectDescriptionFlags(
                item.ObjectId,
                bitfield,
                out WorldSession.EntitySpawn merged))
        {
            return;
        }
        if (_entities.TryGetActive(item.ObjectId, out RuntimeEntityRecord record))
            _entities.RefreshSnapshot(record, merged, refreshPosition: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _objects.ObjectUpdated -= OnObjectUpdated;
    }
}
