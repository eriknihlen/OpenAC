using System.Numerics;
using AcDream.Runtime.Entities;

namespace AcDream.App.Physics;

internal sealed class RemoteMovementObservationTracker
{
    private readonly Dictionary<RuntimeEntityKey, (Vector3 Pos, DateTime Time)>
        _lastMove = [];

    internal (Vector3 Pos, DateTime Time) this[RuntimeEntityKey key]
    {
        set => _lastMove[key] = value;
    }

    internal bool TryGetValue(
        RuntimeEntityKey key,
        out (Vector3 Pos, DateTime Time) observation) =>
        _lastMove.TryGetValue(key, out observation);

    internal bool Remove(RuntimeEntityKey key) => _lastMove.Remove(key);

    internal void Clear() => _lastMove.Clear();
}
