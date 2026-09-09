namespace AcDream.Plugins.MossTank;

internal sealed class ItemEnchantLedger
{
    private sealed class Entry
    {
        public double A;
        public double B;
        public int Quality;
    }

    /// <summary>
    /// <c>dm.b</c>, the three-level table
    /// <c>MySortedList&lt;targetId, MySortedList&lt;RealFamily,
    /// MySortedList&lt;spellId, b&gt;&gt;&gt;</c> (<c>dm.cs:66</c>), flattened
    /// to one key because nothing here iterates a level on its own.
    /// </summary>
    private readonly Dictionary<(uint Item, uint Family, uint SpellId), Entry>
        _entries = [];

    private readonly List<(uint Item, uint Family, uint SpellId)> _scratch = [];

    /// <summary>Whether any entry's due stamp is still zeroed by a force.</summary>
    public bool HasForcedEntries
    {
        get
        {
            foreach (Entry entry in _entries.Values)
            {
                if (entry.A < entry.B)
                    return true;
            }
            return false;
        }
    }

    public int Count => _entries.Count;

    public void NoteCast(
        uint itemObjectId,
        uint family,
        uint spellId,
        int quality,
        double durationSeconds,
        double nowSeconds,
        string spellName,
        Action<string>? log = null)
    {
        // dm.cs:186 — `if (A_0.Duration > 0.0)`.
        if (durationSeconds <= 0d)
            return;

        double expiry = nowSeconds + durationSeconds;
        (uint, uint, uint) key = (itemObjectId, family, spellId);
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry { A = double.MinValue, B = double.MinValue };
            _entries[key] = entry;
        }

        if (entry.B < expiry)
        {
            log?.Invoke(
                $"Cast {spellName} on {itemObjectId} ending at "
                    + $"{expiry:0.###}, OVERRIDDEN");
            entry.A = expiry;
            entry.B = expiry;
            entry.Quality = quality;
            return;
        }

        log?.Invoke(
            $"Cast {spellName} on {itemObjectId} ending at "
                + $"{expiry:0.###}, newer already present");
    }

    public double RemainingSeconds(
        uint itemObjectId, uint family, int quality, double nowSeconds)
    {
        double longest = 0d;
        foreach (KeyValuePair<(uint Item, uint Family, uint SpellId), Entry> pair
            in _entries)
        {
            if (pair.Key.Item != itemObjectId || pair.Key.Family != family)
                continue;
            if (pair.Value.Quality < quality)
                continue;
            double remaining = pair.Value.A - nowSeconds;
            if (remaining > longest)
                longest = remaining;
        }
        return longest;
    }

    public void ForceAll(double nowSeconds)
    {
        foreach (Entry entry in _entries.Values)
            entry.A = nowSeconds;
    }

    /// <summary>
    /// <c>dm.h()</c> (<c>dm.cs:354-366</c>), the line <c>eq.e()</c> ends on
    /// (<c>eq.cs:383</c>): <c>a = b</c> for every entry, putting the real
    /// remaining time back.
    /// </summary>
    public void CancelForce()
    {
        foreach (Entry entry in _entries.Values)
            entry.A = entry.B;
    }

    /// <summary>
    /// <c>dm.a(object, EventArgs)</c> (<c>dm.cs:153-176</c>) — the timer that
    /// drops an entry once its real expiry <c>b</c> is in the past. Without
    /// it the table would only ever grow.
    /// </summary>
    public void Expire(double nowSeconds)
    {
        _scratch.Clear();
        foreach (KeyValuePair<(uint Item, uint Family, uint SpellId), Entry> pair
            in _entries)
        {
            // dm.cs:162 — `if (this.b[key3][key4][key5].b < DateTimeOffset.Now)`.
            if (pair.Value.B < nowSeconds)
                _scratch.Add(pair.Key);
        }
        foreach ((uint, uint, uint) key in _scratch)
            _entries.Remove(key);
        _scratch.Clear();
    }

    public void Reset()
    {
        _entries.Clear();
        _scratch.Clear();
    }
}
