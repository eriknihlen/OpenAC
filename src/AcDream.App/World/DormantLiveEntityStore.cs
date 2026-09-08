using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.App.World;

internal enum DormantCreateDisposition
{
    NoDormantRecord,
    ExistingGeneration,
    NewGeneration,
    StaleGeneration,
}

internal sealed class DormantLiveEntityStore
{
    private readonly Dictionary<uint, WorldSession.EntitySpawn> _spawns = new();

    public int Count => _spawns.Count;

    public DormantCreateDisposition ClassifyCreate(
        WorldSession.EntitySpawn incoming)
    {
        if (!_spawns.TryGetValue(incoming.Guid, out WorldSession.EntitySpawn retained))
            return DormantCreateDisposition.NoDormantRecord;

        if (PhysicsTimestampGate.IsNewer(
                retained.InstanceSequence,
                incoming.InstanceSequence))
        {
            return DormantCreateDisposition.NewGeneration;
        }

        if (PhysicsTimestampGate.IsNewer(
                incoming.InstanceSequence,
                retained.InstanceSequence))
        {
            return DormantCreateDisposition.StaleGeneration;
        }

        return DormantCreateDisposition.ExistingGeneration;
    }

    public void Retain(WorldSession.EntitySpawn snapshot)
    {
        if (_spawns.TryGetValue(snapshot.Guid, out WorldSession.EntitySpawn current)
            && PhysicsTimestampGate.IsNewer(
                snapshot.InstanceSequence,
                current.InstanceSequence))
        {
            return;
        }

        _spawns[snapshot.Guid] = snapshot;
    }

    public bool RemoveThroughAcceptedCreate(WorldSession.EntitySpawn accepted)
    {
        DormantCreateDisposition disposition = ClassifyCreate(accepted);
        if (disposition is DormantCreateDisposition.StaleGeneration
            or DormantCreateDisposition.NoDormantRecord)
        {
            return false;
        }

        return _spawns.Remove(accepted.Guid);
    }

    public bool RemoveExact(DeleteObject.Parsed delete)
    {
        if (!_spawns.TryGetValue(delete.Guid, out WorldSession.EntitySpawn retained)
            || retained.InstanceSequence != delete.InstanceSequence)
        {
            return false;
        }

        return _spawns.Remove(delete.Guid);
    }

    public WorldSession.EntitySpawn[] SnapshotLandblock(uint landblockId)
    {
        uint canonical = CanonicalLandblock(landblockId);
        return _spawns.Values
            .Where(spawn => spawn.Position is { } position
                && CanonicalLandblock(position.LandblockId) == canonical
                && spawn.SetupTableId is not null)
            .ToArray();
    }

    public bool Contains(uint guid, ushort generation) =>
        _spawns.TryGetValue(guid, out WorldSession.EntitySpawn spawn)
        && spawn.InstanceSequence == generation;

    public bool TryGetExact(
        uint guid,
        ushort generation,
        out WorldSession.EntitySpawn snapshot)
    {
        if (_spawns.TryGetValue(guid, out snapshot)
            && snapshot.InstanceSequence == generation)
        {
            return true;
        }

        snapshot = default;
        return false;
    }

    public void Clear() => _spawns.Clear();

    private static uint CanonicalLandblock(uint cellId) =>
        (cellId & 0xFFFF0000u) | 0xFFFFu;
}
