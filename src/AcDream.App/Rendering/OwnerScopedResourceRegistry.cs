namespace AcDream.App.Rendering;

/// <summary>
/// Tracks shared resources by logical owner. Repeated use of the same key by
/// one owner is idempotent; a key is returned for destruction only after its
/// final owner leaves. Render-thread only.
/// </summary>
internal sealed class OwnerScopedResourceRegistry<TKey> where TKey : notnull
{
    private readonly Dictionary<uint, HashSet<TKey>> _keysByOwner = new();
    private readonly Dictionary<TKey, int> _ownerCountByKey = new();

    public int OwnerCount => _keysByOwner.Count;
    public int ResourceCount => _ownerCountByKey.Count;

    public bool Acquire(uint ownerId, TKey key)
    {
        if (ownerId == 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId));

        if (!_keysByOwner.TryGetValue(ownerId, out HashSet<TKey>? keys))
        {
            keys = new HashSet<TKey>();
            _keysByOwner.Add(ownerId, keys);
        }

        if (!keys.Add(key))
            return false;

        _ownerCountByKey[key] = _ownerCountByKey.GetValueOrDefault(key) + 1;
        return true;
    }

    public IReadOnlyList<TKey> ReleaseOwner(uint ownerId)
    {
        if (!_keysByOwner.Remove(ownerId, out HashSet<TKey>? keys))
            return Array.Empty<TKey>();

        List<TKey>? unowned = null;
        foreach (TKey key in keys)
        {
            int remaining = _ownerCountByKey[key] - 1;
            if (remaining > 0)
            {
                _ownerCountByKey[key] = remaining;
                continue;
            }

            _ownerCountByKey.Remove(key);
            (unowned ??= new List<TKey>()).Add(key);
        }

        return unowned is null ? Array.Empty<TKey>() : unowned;
    }

    public void Clear()
    {
        _keysByOwner.Clear();
        _ownerCountByKey.Clear();
    }
}
