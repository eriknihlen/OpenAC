using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;

/// <summary>One staged Buying/Selling-tab row — a shop/pack item guid plus a staged quantity.</summary>
public readonly record struct VendorStagingEntry(uint ItemGuid, int Quantity);

public enum VendorStagingAddOutcome
{
    /// <summary>Staged: a new entry was appended, or an existing one accumulated.</summary>
    Added,
    Capped,
    Ignored,
}

public sealed class VendorStagingList
{
    public const int MaxStagedQuantity = 0x1388;

    public const string TooMuchMessage =
        "I can't possibly sell you that much! Please be a little more reasonable.";

    private readonly List<VendorStagingEntry> _entries = new();

    public IReadOnlyList<VendorStagingEntry> Entries => _entries;
    public bool IsEmpty => _entries.Count == 0;

    public event Action? Changed;

    public VendorStagingAddOutcome Add(uint itemGuid, int quantity)
    {
        if (itemGuid == 0u || quantity <= 0)
            return VendorStagingAddOutcome.Ignored;

        int index = _entries.FindIndex(entry => entry.ItemGuid == itemGuid);
        if (index >= 0)
        {
            int total = _entries[index].Quantity + quantity;
            if (total > MaxStagedQuantity)
                return VendorStagingAddOutcome.Capped;
            _entries[index] = new VendorStagingEntry(itemGuid, total);
        }
        else
        {
            _entries.Add(new VendorStagingEntry(itemGuid, quantity));
        }
        Changed?.Invoke();
        return VendorStagingAddOutcome.Added;
    }

    public bool Remove(uint itemGuid, int amount)
    {
        int index = _entries.FindIndex(entry => entry.ItemGuid == itemGuid);
        if (index < 0)
            return false;

        VendorStagingEntry entry = _entries[index];
        if (amount == -1 || amount >= entry.Quantity)
            _entries.RemoveAt(index);
        else
            _entries[index] = entry with { Quantity = entry.Quantity - amount };
        Changed?.Invoke();
        return true;
    }

    public bool TryGet(uint itemGuid, out VendorStagingEntry entry)
    {
        int index = _entries.FindIndex(e => e.ItemGuid == itemGuid);
        if (index < 0)
        {
            entry = default;
            return false;
        }
        entry = _entries[index];
        return true;
    }

    public bool Replace(uint itemGuid, uint replacementGuid)
    {
        if (itemGuid == 0u || replacementGuid == 0u || itemGuid == replacementGuid)
            return false;

        int index = _entries.FindIndex(entry => entry.ItemGuid == itemGuid);
        if (index < 0 || _entries.Exists(entry => entry.ItemGuid == replacementGuid))
            return false;

        _entries[index] = _entries[index] with { ItemGuid = replacementGuid };
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_entries.Count == 0)
            return;
        _entries.Clear();
        Changed?.Invoke();
    }
}
