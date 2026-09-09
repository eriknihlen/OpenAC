using System;
using System.Collections.Generic;
using AcDream.Core.Items;

namespace AcDream.Runtime.Gameplay;

public sealed class VendorShopItemMaterializer : IDisposable
{
    private readonly VendorState _vendor;
    private readonly ClientObjectTable _objects;

    private readonly Dictionary<uint, uint> _ownedGuids = new();
    private bool _disposed;

    public VendorShopItemMaterializer(VendorState vendor, ClientObjectTable objects)
    {
        _vendor = vendor ?? throw new ArgumentNullException(nameof(vendor));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _vendor.Changed += OnVendorTransition;
    }

    public int OwnedCount => _ownedGuids.Count;

    /// <summary>True if <paramref name="guid"/> is a shop item this materializer put in the table.</summary>
    public bool Owns(uint guid) => _ownedGuids.ContainsKey(guid);

    private void OnVendorTransition(VendorTransition transition)
    {
        IReadOnlyList<VendorShopItem> currentItems = _vendor.Items;
        var stillListed = new HashSet<uint>(currentItems.Count);
        foreach (VendorShopItem item in currentItems)
            stillListed.Add(item.ItemGuid);

        var nextOwned = new Dictionary<uint, uint>(currentItems.Count);
        try
        {
            foreach (KeyValuePair<uint, uint> owned in new List<KeyValuePair<uint, uint>>(_ownedGuids))
            {
                if (stillListed.Contains(owned.Key))
                    continue;

                ClientObject? live = _objects.Get(owned.Key);
                if (live is null || live.ContainerId != owned.Value)
                    continue;

                try
                {
                    _objects.Remove(owned.Key);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError(
                        "[VendorShopItemMaterializer] ObjectRemoved observer "
                        + "threw retiring guid=0x{0}: {1}",
                        owned.Key.ToString("X8"),
                        error);
                }
            }

            foreach (VendorShopItem item in currentItems)
            {
                bool ownedAlready = _ownedGuids.ContainsKey(item.ItemGuid);
                if (!ownedAlready && _objects.Get(item.ItemGuid) is not null)
                {
                    Console.Error.WriteLine(
                        "[VendorShopItemMaterializer] skipped guid=0x"
                        + item.ItemGuid.ToString("X8")
                        + " — already present in ClientObjectTable and not "
                        + "owned by this vendor session.");
                    continue;
                }

                WeenieData materialized = ToWeenieData(item, transition.VendorId);

                _objects.Ingest(materialized);
                nextOwned[item.ItemGuid] = transition.VendorId;
            }
        }
        finally
        {
            _ownedGuids.Clear();
            foreach (KeyValuePair<uint, uint> entry in nextOwned)
                _ownedGuids[entry.Key] = entry.Value;
        }
    }

    private static WeenieData ToWeenieData(VendorShopItem item, uint vendorId) => new(
        Guid: item.ItemGuid,
        Name: item.Name,
        Type: item.ItemType is { } t ? (ItemType)t : null,
        WeenieClassId: item.WeenieClassId,
        IconId: item.IconId,
        IconOverlayId: item.IconOverlayId,
        IconUnderlayId: item.IconUnderlayId,
        Effects: item.Effects,
        Value: item.Value,
        StackSize: VendorSplitPolicy.ResolveAuthoredStackSize(item.DescStackSize, item.MaxStackSize),
        StackSizeMax: item.MaxStackSize,
        Burden: null,
        ContainerId: vendorId,
        WielderId: 0u,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null,
        PluralName: item.PluralName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vendor.Changed -= OnVendorTransition;
        _ownedGuids.Clear();
    }
}
