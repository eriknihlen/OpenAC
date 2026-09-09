using System;
using System.Collections.Concurrent;

namespace AcDream.Core.Items;

public sealed class ItemManaState
{
    private readonly ConcurrentDictionary<uint, float> _manaByGuid = new();
    private long _revision;

    /// <summary>Fires for every valid or invalid query response.</summary>
    public event Action<uint /*guid*/, float /*fraction*/, bool /*valid*/>? ItemManaChanged;

    public float GetManaPercent(uint guid) =>
        _manaByGuid.TryGetValue(guid, out float percent) ? percent : 0f;

    public bool HasMana(uint guid) => _manaByGuid.ContainsKey(guid);
    public int Count => _manaByGuid.Count;
    public long Revision => Interlocked.Read(ref _revision);

    public bool TryGetManaPercent(uint guid, out float fraction) =>
        _manaByGuid.TryGetValue(guid, out fraction);

    public void OnQueryItemManaResponse(uint itemGuid, float manaPercent, bool valid)
    {
        if (valid)
            _manaByGuid[itemGuid] = manaPercent;
        else
            _manaByGuid.TryRemove(itemGuid, out _);

        Interlocked.Increment(ref _revision);
        ItemManaChanged?.Invoke(itemGuid, manaPercent, valid);
    }

    public void Clear()
    {
        _manaByGuid.Clear();
        Interlocked.Increment(ref _revision);
    }
}
