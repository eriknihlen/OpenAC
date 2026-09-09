namespace AcDream.Core.Social;

public sealed class FriendsState
{
    private readonly object _gate = new();
    private readonly List<FriendEntry> _entries = new();
    private long _revision;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public long Revision => Interlocked.Read(ref _revision);

    public IReadOnlyList<FriendEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public bool TryGet(uint characterId, out FriendEntry? friend)
    {
        lock (_gate)
        {
            friend = _entries.Find(candidate => candidate.Id == characterId);
            return friend is not null;
        }
    }

    public void Apply(FriendsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            switch (update.Type)
            {
                case FriendsUpdateType.Full:
                    _entries.Clear();
                    _entries.AddRange(update.Entries);
                    break;
                case FriendsUpdateType.Add:
                    foreach (FriendEntry entry in update.Entries)
                    {
                        int index = _entries.FindIndex(candidate => candidate.Id == entry.Id);
                        if (index >= 0) _entries[index] = entry;
                        else _entries.Add(entry);
                    }
                    break;
                case FriendsUpdateType.Remove:
                case FriendsUpdateType.RemoveSilent:
                    foreach (FriendEntry entry in update.Entries)
                        _entries.RemoveAll(candidate => candidate.Id == entry.Id);
                    break;
                case FriendsUpdateType.OnlineStatus:
                    foreach (FriendEntry entry in update.Entries)
                    {
                        int index = _entries.FindIndex(candidate => candidate.Id == entry.Id);
                        if (index >= 0) _entries[index] = entry;
                    }
                    break;
            }
            Interlocked.Increment(ref _revision);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Interlocked.Increment(ref _revision);
        }
    }
}

public sealed record FriendEntry(
    uint Id,
    string Name,
    bool Online,
    bool AppearOffline,
    IReadOnlyList<uint> Friends,
    IReadOnlyList<uint> FriendOf);

public enum FriendsUpdateType : uint
{
    Full = 0,
    Add = 1,
    Remove = 2,
    RemoveSilent = 3,
    OnlineStatus = 4,
}

public sealed record FriendsUpdate(
    FriendsUpdateType Type,
    IReadOnlyList<FriendEntry> Entries);
