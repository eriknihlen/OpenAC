using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class BuffDueTracker
{
    private readonly HashSet<uint> _tracked = [];
    private readonly HashSet<uint> _forced = [];
    private readonly List<uint> _scratch = [];

    /// <summary><c>dm</c>'s half of the force (<c>eq.cs:371</c>).</summary>
    private readonly HashSet<(uint ItemObjectId, uint Family)> _forcedItems = [];

    public IReadOnlySet<uint> ForcedSpellIds => _forced;

    public bool HasForcedEntries => _forced.Count > 0;

    public bool HasForcedItems => _forcedItems.Count > 0;

    /// <summary>
    /// The polled equivalent of <c>eq.a(ActiveSpellInfo)</c> /
    /// <c>eq.b(ActiveSpellInfo)</c>: fold this pass's snapshot into the table.
    /// A newly-seen entry gets <c>a = d</c>, which is "not forced"; an entry
    /// that left the snapshot is dropped outright.
    /// </summary>
    public void Observe(IReadOnlyList<PluginActiveEnchantment> active)
    {
        ArgumentNullException.ThrowIfNull(active);
        _scratch.Clear();
        foreach (PluginActiveEnchantment enchantment in active)
        {
            if (enchantment.SpellId == 0u)
                continue;
            _scratch.Add(enchantment.SpellId);
            if (!_tracked.Contains(enchantment.SpellId))
            {
                _forced.Remove(enchantment.SpellId);
                _tracked.Add(enchantment.SpellId);
            }
        }

        List<uint>? gone = null;
        foreach (uint spellId in _tracked)
        {
            if (!_scratch.Contains(spellId))
                (gone ??= []).Add(spellId);
        }
        if (gone is null)
            return;
        foreach (uint spellId in gone)
        {
            _tracked.Remove(spellId);
            _forced.Remove(spellId);
        }
    }

    /// <summary>
    /// <c>eq.i()</c> (<c>eq.cs:362-372</c>), reached from
    /// <c>PluginCore.ForceBuff()</c>: every tracked entry's <c>a</c> stamp is
    /// set to now, so the due test <c>(entry.a - Now).TotalSeconds &gt;=
    /// threshold</c> (<c>eq.cs:490</c>) fails for all of them and everything
    /// reads as about to expire. The real <c>ExpireTime</c> (<c>d</c>) is not
    /// touched.
    /// </summary>
    public void ForceAll()
    {
        foreach (uint spellId in _tracked)
            _forced.Add(spellId);
    }

    public void ForceItems(IEnumerable<(uint ItemObjectId, uint Family)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach ((uint ItemObjectId, uint Family) row in rows)
            _forcedItems.Add(row);
    }

    /// <summary>
    /// Whether this item's row in that family still reads as forced.
    /// </summary>
    public bool IsItemForced(uint itemObjectId, uint family) =>
        _forcedItems.Contains((itemObjectId, family));

    public void NoteItemRecast(uint itemObjectId, uint family) =>
        _forcedItems.Remove((itemObjectId, family));

    public void NoteRecast(uint spellId) => _forced.Remove(spellId);

    public void CancelForce()
    {
        _forced.Clear();
        // eq.cs:383 — eq.e() ends on this.m_a.j.h(), dm's restore.
        _forcedItems.Clear();
    }

    public void Reset()
    {
        _tracked.Clear();
        _forced.Clear();
        _scratch.Clear();
        _forcedItems.Clear();
    }
}
