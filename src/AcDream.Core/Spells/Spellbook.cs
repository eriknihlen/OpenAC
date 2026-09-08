using System;
using System.Collections.Generic;
using System.Linq;

namespace AcDream.Core.Spells;

public sealed class Spellbook
{
    public const uint CooldownSpellOffset = 0x8000u;
    public const uint CooldownBucket = 8u;

    private readonly Dictionary<uint, float> _learnedSpells = new();
    private readonly Dictionary<uint, ActiveEnchantmentRecord> _activeById = new();
    private readonly Dictionary<uint, List<uint>> _enchantmentOrderByBucket = new();
    private readonly Dictionary<uint, EnchantmentMath.VitalMod> _vitalModCache = new();
    private readonly Dictionary<uint, EnchantmentMath.VitalMod> _attributeModCache = new();
    private readonly Dictionary<uint, EnchantmentMath.VitalMod> _skillModCache = new();
    private readonly List<uint>[] _favoriteSpells = Enumerable.Range(0, 8)
        .Select(_ => new List<uint>()).ToArray();
    private readonly Dictionary<uint, uint> _desiredComponents = new();
    private SpellTable _table;
    private bool _metadataInstalled;
    private uint _spellbookFilters = 0x3FFFu;

    public Spellbook(SpellTable? table = null)
    {
        _table = table ?? SpellTable.Empty;
        _metadataInstalled = table is not null;
    }

    public bool TryGetMetadata(uint spellId, out SpellMetadata meta) =>
        _table.TryGet(spellId, out meta);

    public SpellTable Metadata => _table;

    public void InstallMetadata(SpellTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_metadataInstalled)
        {
            if (ReferenceEquals(_table, table)) return;
            throw new InvalidOperationException("Spell metadata is already installed.");
        }

        _table = table;
        _metadataInstalled = true;
        if (_learnedSpells.Count != 0) NotifySpellbookChanged();
        if (_activeById.Count != 0) NotifyEnchantmentsChanged();
    }

    public EnchantmentMath.VitalMod GetVitalMod(uint statKey)
    {
        if (_vitalModCache.TryGetValue(
                statKey,
                out EnchantmentMath.VitalMod cached))
        {
            return cached;
        }

        EnchantmentMath.VitalMod calculated = EnchantmentMath.GetMod(
            ActiveEnchantments,
            _table,
            statKey,
            EnchantmentMath.EnchantmentTypeFlag.SecondAtt,
            includeVitae: true);
        _vitalModCache.Add(statKey, calculated);
        return calculated;
    }

    public EnchantmentMath.VitalMod GetAttributeMod(uint attributeId)
    {
        if (_attributeModCache.TryGetValue(
                attributeId,
                out EnchantmentMath.VitalMod cached))
        {
            return cached;
        }

        EnchantmentMath.VitalMod calculated = EnchantmentMath.GetMod(
            ActiveEnchantments,
            _table,
            attributeId,
            EnchantmentMath.EnchantmentTypeFlag.Attribute,
            includeVitae: false);
        _attributeModCache.Add(attributeId, calculated);
        return calculated;
    }

    public EnchantmentMath.VitalMod GetSkillMod(uint skillId)
    {
        if (_skillModCache.TryGetValue(
                skillId,
                out EnchantmentMath.VitalMod cached))
        {
            return cached;
        }

        EnchantmentMath.VitalMod calculated =
            EnchantmentMath.GetSkillMod(ActiveEnchantments, _table, skillId);
        _skillModCache.Add(skillId, calculated);
        return calculated;
    }

    /// <summary>Fires when a spell is added to the player's spellbook.</summary>
    public event Action<uint>? SpellLearned;

    /// <summary>Fires when a spell is removed (rare — usually on respec / admin).</summary>
    public event Action<uint>? SpellForgotten;

    /// <summary>Fires when an enchantment is added / refreshed.</summary>
    public event Action<ActiveEnchantmentRecord>? EnchantmentAdded;

    /// <summary>Fires when an enchantment is removed (expired / dispelled).</summary>
    public event Action<ActiveEnchantmentRecord>? EnchantmentRemoved;

    public event Action? StateChanged;

    public event Action? SpellbookChanged;

    public event Action? EnchantmentsChanged;

    public event Action? DesiredComponentsChanged;

    public IReadOnlyCollection<uint> LearnedSpells => _learnedSpells.Keys;

    /// <summary>Stable learned-spell snapshot, including server spell power.</summary>
    public IReadOnlyList<LearnedSpellRecord> LearnedSpellSnapshot => _learnedSpells
        .Select(pair => new LearnedSpellRecord(pair.Key, pair.Value))
        .OrderBy(spell => spell.SpellId)
        .ToArray();

    public IEnumerable<ActiveEnchantmentRecord> ActiveEnchantments => _activeById.Values;

    public IReadOnlyList<ActiveEnchantmentRecord> ActiveEnchantmentSnapshot =>
        _activeById.Values.OrderBy(record => record.Identity).ToArray();

    public IReadOnlyList<ActiveEnchantmentRecord> EnchantmentsInEffectSnapshot
    {
        get
        {
            var ordered = new List<ActiveEnchantmentRecord>();
            AppendBucket(bucket: 1u, ordered);
            AppendBucket(bucket: 2u, ordered);
            return EnchantmentRegistryProjection.GetEnchantmentsInEffect(ordered);
        }
    }

    public IReadOnlyList<uint> GetFavorites(int tabIndex)
    {
        if ((uint)tabIndex >= _favoriteSpells.Length) return Array.Empty<uint>();
        return _favoriteSpells[tabIndex].ToArray();
    }

    public IReadOnlyDictionary<uint, uint> DesiredComponents => _desiredComponents;

    public uint SpellbookFilters => _spellbookFilters;

    public int LearnedCount => _learnedSpells.Count;
    public int ActiveCount  => _activeById.Count;
    public bool HasCooldownEnchantments =>
        _enchantmentOrderByBucket.TryGetValue(
            CooldownBucket,
            out List<uint>? order)
        && order.Count != 0;

    public bool Knows(uint spellId) => _learnedSpells.ContainsKey(spellId);

    // ── Inbound handlers ─────────────────────────────────────────────────────

    /// <summary>0x02C1 MagicUpdateSpell: learn a spell.</summary>
    public void OnSpellLearned(uint spellId, float power = 0f)
    {
        if (_learnedSpells.TryAdd(spellId, power))
        {
            SpellLearned?.Invoke(spellId);
            NotifySpellbookChanged();
        }
        else if (_learnedSpells[spellId] != power)
        {
            _learnedSpells[spellId] = power;
            NotifySpellbookChanged();
        }
    }

    /// <summary>0x01A8 MagicRemoveSpell: forget a spell.</summary>
    public void OnSpellForgotten(uint spellId)
    {
        if (_learnedSpells.Remove(spellId))
        {
            SpellForgotten?.Invoke(spellId);
            NotifySpellbookChanged();
        }
    }

    /// <summary>0x02C2 MagicUpdateEnchantment: enchantment added / refreshed.</summary>
    public void OnEnchantmentAdded(uint spellId, uint layerId, float duration, uint casterGuid)
    {
        var record = new ActiveEnchantmentRecord(spellId, layerId, duration, casterGuid);
        UpsertLiveEnchantment(record);
        EnchantmentAdded?.Invoke(record);
        NotifyEnchantmentsChanged();
    }

    public void OnEnchantmentAdded(ActiveEnchantmentRecord record)
    {
        UpsertLiveEnchantment(record);
        EnchantmentAdded?.Invoke(record);
        NotifyEnchantmentsChanged();
    }

    /// <summary>0x02C3 / 0x02C7 MagicRemove/DispelEnchantment.</summary>
    public void OnEnchantmentRemoved(uint layerId, uint spellId)
    {
        uint identity = ActiveEnchantmentRecord.MakeIdentity(spellId, layerId);
        if (RemoveActiveEnchantment(identity, out ActiveEnchantmentRecord record))
        {
            EnchantmentRemoved?.Invoke(record);
            NotifyEnchantmentsChanged();
        }
    }

    public void OnEnchantmentsAdded(IEnumerable<ActiveEnchantmentRecord> records)
    {
        foreach (ActiveEnchantmentRecord record in records)
        {
            UpsertLiveEnchantment(record);
            EnchantmentAdded?.Invoke(record);
        }
        NotifyEnchantmentsChanged();
    }

    public void OnEnchantmentsRemoved(IEnumerable<(uint SpellId, uint Layer)> spells)
    {
        bool changed = false;
        foreach ((uint spellId, uint layer) in spells)
        {
            uint identity = ActiveEnchantmentRecord.MakeIdentity(spellId, layer);
            if (!RemoveActiveEnchantment(identity, out ActiveEnchantmentRecord record)) continue;
            changed = true;
            EnchantmentRemoved?.Invoke(record);
        }
        if (changed) NotifyEnchantmentsChanged();
    }

    public bool OnCooldown(
        uint cooldownId,
        double currentTime,
        out double remaining)
    {
        remaining = 0d;
        if (cooldownId == 0u
            || !_enchantmentOrderByBucket.TryGetValue(
                CooldownBucket,
                out List<uint>? order))
            return false;

        uint cooldownSpellId = unchecked(cooldownId + CooldownSpellOffset);
        for (int index = 0; index < order.Count; index++)
        {
            uint identity = order[index];
            if (!_activeById.TryGetValue(
                    identity,
                    out ActiveEnchantmentRecord record)
                || (record.SpellId & 0xFFFFu) != cooldownSpellId)
                continue;

            remaining = record.Duration + record.StartTime - currentTime;
            if (remaining > 0d)
                return true;

            RemoveActiveEnchantment(identity, out _);
            EnchantmentRemoved?.Invoke(record);
            NotifyEnchantmentsChanged();
            return false;
        }

        return false;
    }

    public void OnPurgeAll()
    {
        ActiveEnchantmentRecord[] removed = _activeById.Values
            .Where(IsPurgeable)
            .ToArray();
        foreach (ActiveEnchantmentRecord record in removed)
            RemoveActiveEnchantment(record.Identity, out _);
        foreach (var rec in removed)
            EnchantmentRemoved?.Invoke(rec);
        if (removed.Length != 0) NotifyEnchantmentsChanged();
    }

    public void OnPurgeBadEnchantments()
    {
        const uint BeneficialStatMod = 0x02000000u;
        ActiveEnchantmentRecord[] removed = _activeById.Values
            .Where(record => IsPurgeable(record)
                && (record.StatModType.GetValueOrDefault() & BeneficialStatMod) == 0)
            .ToArray();
        foreach (ActiveEnchantmentRecord record in removed)
            RemoveActiveEnchantment(record.Identity, out _);
        foreach (ActiveEnchantmentRecord record in removed)
            EnchantmentRemoved?.Invoke(record);
        if (removed.Length != 0) NotifyEnchantmentsChanged();
    }

    private static bool IsPurgeable(ActiveEnchantmentRecord record) =>
        (record.Bucket is 1u or 2u) && record.Duration != -1f;

    public void ReplaceManifest(
        IReadOnlyDictionary<uint, float> learnedSpells,
        IEnumerable<ActiveEnchantmentRecord> enchantments,
        IReadOnlyList<IReadOnlyList<uint>> favorites,
        IReadOnlyList<(uint Id, uint Amount)> desiredComponents,
        uint spellbookFilters)
    {
        _learnedSpells.Clear();
        foreach ((uint spellId, float power) in learnedSpells)
            _learnedSpells[spellId] = power;

        _activeById.Clear();
        _enchantmentOrderByBucket.Clear();
        _vitalModCache.Clear();
        _attributeModCache.Clear();
        _skillModCache.Clear();
        foreach (ActiveEnchantmentRecord enchantment in enchantments)
        {
            UpsertManifestEnchantment(enchantment);
            if (enchantment.Bucket == 4)
            {
                string vitaeValue = enchantment.StatModValue is float v
                    ? v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                    : "NULL";
                System.Console.WriteLine(
                    System.FormattableString.Invariant(
                        $"[stat-chain] login vitae installed: spell={enchantment.SpellId} statModType=0x{enchantment.StatModType ?? 0:X} key={enchantment.StatModKey ?? 0} value={vitaeValue}"));
            }
        }

        for (int i = 0; i < _favoriteSpells.Length; i++)
        {
            _favoriteSpells[i].Clear();
            if (i < favorites.Count)
                _favoriteSpells[i].AddRange(favorites[i]);
        }

        _desiredComponents.Clear();
        foreach ((uint id, uint amount) in desiredComponents)
            _desiredComponents[id] = amount;
        _spellbookFilters = spellbookFilters;
        SpellbookChanged?.Invoke();
        EnchantmentsChanged?.Invoke();
        DesiredComponentsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void SetFavorite(int tabIndex, int position, uint spellId)
    {
        if ((uint)tabIndex >= _favoriteSpells.Length || position < 0) return;
        List<uint> tab = _favoriteSpells[tabIndex];
        if (position > tab.Count) position = tab.Count;
        tab.Remove(spellId);
        tab.Insert(Math.Min(position, tab.Count), spellId);
        NotifySpellbookChanged();
    }

    public void RemoveFavorite(int tabIndex, uint spellId)
    {
        if ((uint)tabIndex >= _favoriteSpells.Length) return;
        if (_favoriteSpells[tabIndex].Remove(spellId)) NotifySpellbookChanged();
    }

    public void SetSpellbookFilters(uint filters)
    {
        if (_spellbookFilters == filters) return;
        _spellbookFilters = filters;
        NotifySpellbookChanged();
    }

    public void SetDesiredComponent(uint componentId, uint amount)
    {
        bool exists = _desiredComponents.TryGetValue(
            componentId,
            out uint current);
        if (exists && current == amount) return;
        _desiredComponents[componentId] = amount;
        DesiredComponentsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void ClearDesiredComponents()
    {
        if (_desiredComponents.Count == 0) return;
        _desiredComponents.Clear();
        DesiredComponentsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        _learnedSpells.Clear();
        _activeById.Clear();
        _enchantmentOrderByBucket.Clear();
        _vitalModCache.Clear();
        _attributeModCache.Clear();
        _skillModCache.Clear();
        foreach (List<uint> tab in _favoriteSpells) tab.Clear();
        _desiredComponents.Clear();
        _spellbookFilters = 0x3FFFu;
        SpellbookChanged?.Invoke();
        EnchantmentsChanged?.Invoke();
        DesiredComponentsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void NotifySpellbookChanged()
    {
        SpellbookChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void NotifyEnchantmentsChanged()
    {
        _vitalModCache.Clear();
        _attributeModCache.Clear();
        _skillModCache.Clear();
        EnchantmentsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void AppendBucket(uint bucket, List<ActiveEnchantmentRecord> destination)
    {
        if (!_enchantmentOrderByBucket.TryGetValue(bucket, out List<uint>? order)) return;
        foreach (uint identity in order)
            if (_activeById.TryGetValue(identity, out ActiveEnchantmentRecord record)
                && record.Bucket == bucket)
                destination.Add(record);
    }

    private void UpsertLiveEnchantment(ActiveEnchantmentRecord record)
    {
        uint identity = record.Identity;
        if (_activeById.TryGetValue(identity, out ActiveEnchantmentRecord previous))
        {
            _activeById[identity] = record;
            if (previous.Bucket == record.Bucket)
            {
                EnsureOrdered(identity, record.Bucket, insertAtHead: true);
                return;
            }
            RemoveFromOrder(previous);
        }
        else
        {
            _activeById.Add(identity, record);
        }
        GetBucketOrder(record.Bucket).Insert(0, identity);
    }

    /// <summary>Bulk UnPack preserves the received head-to-tail list order.</summary>
    private void UpsertManifestEnchantment(ActiveEnchantmentRecord record)
    {
        uint identity = record.Identity;
        if (_activeById.TryGetValue(identity, out ActiveEnchantmentRecord previous))
        {
            _activeById[identity] = record;
            if (previous.Bucket == record.Bucket)
            {
                EnsureOrdered(identity, record.Bucket, insertAtHead: false);
                return;
            }
            RemoveFromOrder(previous);
        }
        else
        {
            _activeById.Add(identity, record);
        }
        GetBucketOrder(record.Bucket).Add(identity);
    }

    private bool RemoveActiveEnchantment(
        uint identity,
        out ActiveEnchantmentRecord record)
    {
        if (!_activeById.Remove(identity, out record)) return false;
        RemoveFromOrder(record);
        return true;
    }

    private void EnsureOrdered(uint identity, uint bucket, bool insertAtHead)
    {
        List<uint> order = GetBucketOrder(bucket);
        if (order.Contains(identity)) return;
        if (insertAtHead) order.Insert(0, identity);
        else order.Add(identity);
    }

    private List<uint> GetBucketOrder(uint bucket)
    {
        if (_enchantmentOrderByBucket.TryGetValue(bucket, out List<uint>? order))
            return order;
        order = new List<uint>();
        _enchantmentOrderByBucket.Add(bucket, order);
        return order;
    }

    private void RemoveFromOrder(ActiveEnchantmentRecord record)
    {
        if (!_enchantmentOrderByBucket.TryGetValue(record.Bucket, out List<uint>? order))
            return;
        order.Remove(record.Identity);
        if (order.Count == 0)
            _enchantmentOrderByBucket.Remove(record.Bucket);
    }
}

public readonly record struct LearnedSpellRecord(uint SpellId, float Power);

public readonly record struct ActiveEnchantmentRecord(
    uint   SpellId,
    uint   LayerId,
    double Duration,
    uint   CasterGuid,
    uint?  StatModType   = null,
    uint?  StatModKey    = null,
    float? StatModValue  = null,
    uint   Bucket        = 0,
    double StartTime     = 0,
    uint   SpellCategory = 0,
    uint   PowerLevel    = 0,
    float  DegradeModifier = 0,
    float  DegradeLimit  = 0,
    double LastTimeDegraded = 0,
    uint?  SpellSetId    = null)
{
    public uint Identity => MakeIdentity(SpellId, LayerId);

    public static uint MakeIdentity(uint spellId, uint layerId) =>
        (spellId & 0xFFFFu) | ((layerId & 0xFFFFu) << 16);
}
