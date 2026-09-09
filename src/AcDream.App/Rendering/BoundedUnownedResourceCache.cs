namespace AcDream.App.Rendering;

internal sealed class BoundedUnownedResourceCache<TKey> where TKey : notnull
{
    private readonly long _budgetBytes;
    private readonly int _maximumCount;
    private readonly LinkedList<TKey> _lru = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    private long _residentBytes;

    private readonly record struct Entry(long Bytes, LinkedListNode<TKey> Node);

    public BoundedUnownedResourceCache(long budgetBytes, int maximumCount = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        _budgetBytes = budgetBytes;
        _maximumCount = maximumCount;
    }

    public int Count => _entries.Count;
    public long ResidentBytes => _residentBytes;
    public long BudgetBytes => _budgetBytes;
    public int MaximumCount => _maximumCount;
    public bool Contains(TKey key) => _entries.ContainsKey(key);

    public void MarkUnowned(TKey key, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (_entries.TryGetValue(key, out Entry existing))
        {
            if (existing.Bytes != bytes)
                throw new InvalidOperationException(
                    $"Cached resource {key} changed size from {existing.Bytes} to {bytes} bytes.");

            _lru.Remove(existing.Node);
            _lru.AddLast(existing.Node);
            return;
        }

        LinkedListNode<TKey> node = _lru.AddLast(key);
        _entries.Add(key, new Entry(bytes, node));
        _residentBytes = checked(_residentBytes + bytes);
    }

    public bool MarkOwned(TKey key)
    {
        if (!_entries.Remove(key, out Entry entry))
            return false;

        _lru.Remove(entry.Node);
        _residentBytes -= entry.Bytes;
        return true;
    }

    public bool TryTakeOldestOverBudget(out TKey key)
    {
        if ((_residentBytes <= _budgetBytes && _entries.Count <= _maximumCount)
            || _lru.First is null)
        {
            key = default!;
            return false;
        }

        LinkedListNode<TKey> node = _lru.First;
        key = node.Value;
        Entry entry = _entries[key];
        _lru.RemoveFirst();
        _entries.Remove(key);
        _residentBytes -= entry.Bytes;
        return true;
    }

    public bool TryTakeOldest(out TKey key)
    {
        if (_lru.First is null)
        {
            key = default!;
            return false;
        }

        LinkedListNode<TKey> node = _lru.First;
        key = node.Value;
        Entry entry = _entries[key];
        _lru.RemoveFirst();
        _entries.Remove(key);
        _residentBytes -= entry.Bytes;
        return true;
    }

    public bool TryTake(TKey key)
    {
        if (!_entries.Remove(key, out Entry entry))
            return false;

        _lru.Remove(entry.Node);
        _residentBytes -= entry.Bytes;
        return true;
    }

    public void Clear()
    {
        _lru.Clear();
        _entries.Clear();
        _residentBytes = 0;
    }
}
