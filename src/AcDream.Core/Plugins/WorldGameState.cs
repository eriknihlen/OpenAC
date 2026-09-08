using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class WorldGameState : IGameState
{
    private readonly List<WorldEntitySnapshot> _entities = new();
    private readonly Dictionary<uint, int> _indexById = new();

    public IReadOnlyList<WorldEntitySnapshot> Entities => _entities;

    public Func<IReadOnlyList<ContractSnapshot>>? ContractsSource { get; set; }

    public IReadOnlyList<ContractSnapshot> Contracts =>
        ContractsSource?.Invoke() ?? [];

    public void Add(WorldEntitySnapshot snapshot)
    {
        if (_indexById.TryGetValue(snapshot.Id, out int index))
        {
            _entities[index] = snapshot;
            return;
        }

        _indexById.Add(snapshot.Id, _entities.Count);
        _entities.Add(snapshot);
    }

    public bool RemoveById(uint id)
    {
        if (!_indexById.Remove(id, out int index))
            return false;

        int lastIndex = _entities.Count - 1;
        if (index != lastIndex)
        {
            WorldEntitySnapshot moved = _entities[lastIndex];
            _entities[index] = moved;
            _indexById[moved.Id] = index;
        }
        _entities.RemoveAt(lastIndex);
        return true;
    }

    public void Clear()
    {
        _entities.Clear();
        _indexById.Clear();
    }
}
