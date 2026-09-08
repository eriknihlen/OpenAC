namespace AcDream.Content.Pak;

internal sealed class PakTexturePayloadCache
{
    internal const long DefaultMaximumBytes = 64L * 1024 * 1024;
    internal const int DefaultMaximumEntries = 1_024;

    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private readonly Dictionary<ulong, Entry> _entries = [];
    private readonly LinkedList<ulong> _lru = [];
    private readonly Lock _gate = new();
    private long _bytes;

    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    private sealed record Entry(byte[] Bytes, LinkedListNode<ulong> Node);

    public PakTexturePayloadCache(
        long maximumBytes = DefaultMaximumBytes,
        int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        _maximumBytes = maximumBytes;
        _maximumEntries = maximumEntries;
    }

    public bool TryGet(ulong key, out byte[] bytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                bytes = null!;
                return false;
            }

            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
            bytes = entry.Bytes;
            return true;
        }
    }

    public byte[] AddOrGet(ulong key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddLast(existing.Node);
                return existing.Bytes;
            }

            if (bytes.LongLength > _maximumBytes)
                return bytes;

            LinkedListNode<ulong> node = _lru.AddLast(key);
            _entries.Add(key, new Entry(bytes, node));
            _bytes = checked(_bytes + bytes.LongLength);
            Trim();
            return bytes;
        }
    }

    private void Trim()
    {
        while ((_bytes > _maximumBytes || _entries.Count > _maximumEntries)
               && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            if (_entries.Remove(oldest.Value, out Entry? removed))
                _bytes -= removed.Bytes.LongLength;
        }
    }
}
