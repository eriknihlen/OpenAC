using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;

public readonly record struct VendorShopProfile(
    uint MerchandiseItemTypes,
    uint MerchandiseMinValue,
    uint MerchandiseMaxValue,
    bool DealMagicalItems,
    float BuyPrice,
    float SellPrice,
    uint AlternateCurrencyWcid,
    uint AlternateCurrencyAmount,
    string AlternateCurrencyPluralName);

public readonly record struct VendorShopItem(
    uint ItemGuid,
    int StackSize,
    uint WeenieClassId,
    string? Name,
    uint? ItemType,
    uint IconId,
    int? Value,
    int? DescStackSize = null,
    int? MaxStackSize = null,
    uint IconUnderlayId = 0u,
    uint IconOverlayId = 0u,
    uint Effects = 0u,
    string? PluralName = null,
    // The rest of the listed item's description. A shop listing carries the
    // same full item description a spawned object does, so assessing an item
    // in a vendor's list must read exactly like assessing it in your pack.
    uint? ValidLocations = null,
    uint? Priority = null,
    int? ItemsCapacity = null,
    int? ContainersCapacity = null,
    int? Structure = null,
    int? MaxStructure = null,
    float? Workmanship = null,
    int? Burden = null,
    uint? MaterialType = null,
    uint? TargetType = null,
    byte? CombatUse = null,
    ushort? AmmoType = null,
    uint? PublicWeenieBitfield = null,
    uint? Useability = null,
    uint? HookItemTypes = null,
    uint? HookType = null);

public enum VendorStateTransitionKind
{
    /// <summary>A different vendor than whatever was previously open (or nothing) is now open.</summary>
    Opened,
    Refreshed,
    Closed,
    /// <summary>Session teardown (portal/reconnect/logout).</summary>
    Reset,
}

public readonly record struct VendorTransition(
    VendorStateTransitionKind Kind,
    uint PreviousVendorId,
    uint VendorId);

public sealed class VendorState
{
    public uint VendorId { get; private set; }
    public VendorShopProfile Profile { get; private set; }
    public IReadOnlyList<VendorShopItem> Items { get; private set; } = Array.Empty<VendorShopItem>();

    public event Action<VendorTransition>? Changed;

    public bool Apply(uint vendorGuid, VendorShopProfile profile, IReadOnlyList<VendorShopItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (vendorGuid == 0u) return false;

        uint previous = VendorId;
        bool sameVendor = previous != 0u && previous == vendorGuid;

        VendorId = vendorGuid;
        Profile = profile;
        Items = items;

        var transition = new VendorTransition(
            sameVendor ? VendorStateTransitionKind.Refreshed : VendorStateTransitionKind.Opened,
            previous,
            vendorGuid);
        Action<VendorTransition>? listeners = Changed;
        if (listeners is not null)
        {
            foreach (Action<VendorTransition> listener in listeners.GetInvocationList())
            {
                try { listener(transition); }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"[VendorState] Apply() observer threw: {error.Message}");
                }
            }
        }
        return true;
    }

    public bool Close()
    {
        if (VendorId == 0u) return false;

        uint previous = VendorId;
        ClearFields();

        var transition = new VendorTransition(VendorStateTransitionKind.Closed, previous, 0u);
        Action<VendorTransition>? listeners = Changed;
        if (listeners is not null)
        {
            foreach (Action<VendorTransition> listener in listeners.GetInvocationList())
            {
                try { listener(transition); }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"[VendorState] Close() observer threw: {error.Message}");
                }
            }
        }
        return true;
    }

    public bool Reset()
    {
        uint previous = VendorId;
        bool changed = previous != 0u;
        ClearFields();

        var transition = new VendorTransition(VendorStateTransitionKind.Reset, previous, 0u);
        Action<VendorTransition>? listeners = Changed;
        if (listeners is not null)
        {
            List<Exception>? failures = null;
            foreach (Action<VendorTransition> listener in listeners.GetInvocationList())
            {
                try { listener(transition); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null)
                throw new AggregateException(
                    "One or more vendor-state reset observers failed.",
                    failures);
        }
        return changed;
    }

    private void ClearFields()
    {
        VendorId = 0u;
        Profile = default;
        Items = Array.Empty<VendorShopItem>();
    }
}
