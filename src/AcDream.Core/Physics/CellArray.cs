using System.Collections;
using System.Collections.Generic;

namespace AcDream.Core.Physics;

public sealed class CellArray : ICollection<uint>, IReadOnlyCollection<uint>
{
    private readonly List<uint> _order = new();
    private readonly HashSet<uint> _seen = new();

    internal CellArray? UnionTarget { get; set; }

    public int Count => _order.Count;
    public bool IsReadOnly => false;

    public IReadOnlyList<uint> OrderedIds => _order;

    public void Add(uint id)
    {
        if (UnionTarget is { } target
            && !ReferenceEquals(target, this))
        {
            target.Add(id);
        }
        if (_seen.Add(id))
            _order.Add(id);
    }

    public bool Contains(uint id) => _seen.Contains(id);

    public void Clear() { _order.Clear(); _seen.Clear(); }

    public bool Remove(uint id)
    {
        if (!_seen.Remove(id)) return false;
        _order.Remove(id);
        return true;
    }

    public void CopyTo(uint[] array, int arrayIndex) => _order.CopyTo(array, arrayIndex);

    public IEnumerator<uint> GetEnumerator() => _order.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _order.GetEnumerator();
}
