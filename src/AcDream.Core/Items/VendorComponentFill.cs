using System;
using System.Collections.Generic;
using System.Text;

namespace AcDream.Core.Items;

/// <summary>
/// One component-book row as the buy-list fill sees it: the component's class,
/// which of the seven component categories it belongs to, its name, how many
/// the player asked for, and how many are already in the pack.
/// </summary>
public readonly record struct ComponentFillDesire(
    uint WeenieClassId,
    uint Category,
    string Name,
    int Desired,
    int Owned);

/// <summary>One staged purchase the fill wants made, in the order it wants it made.</summary>
public readonly record struct ComponentFillAdd(uint ItemGuid, int Quantity);

/// <summary>
/// What a fill decided: the staged buys, whether the price ceiling aborted it
/// (which drops the newest staged row), and the components the vendor could
/// not fully supply.
/// </summary>
public sealed record ComponentFillPlan(
    IReadOnlyList<ComponentFillAdd> Adds,
    bool AbortedOnPrice,
    IReadOnlyList<string> ShortComponents);

/// <summary>
/// Fills a vendor's buy list from the component book's desired counts: for
/// every component of the requested category the player is short of, stage
/// what the shop stocks, and stop once the staged total reaches the ceiling.
/// </summary>
public static class VendorComponentFill
{
    /// <summary>The category value meaning "no category in particular" — fill matches every row.</summary>
    public const uint AnyCategory = 8u;

    /// <summary>The listed stack size a shop uses for "stocked without limit".</summary>
    public const int UnlimitedStock = -1;

    public const string ShortComponentsPrefix = "There was not enough: ";

    public const string ShortComponentsSeparator = ", ";

    public const string AbortedOnPriceMessage = "Buying aborted; max price reached.";

    /// <param name="desires">The component-book rows, in the order the fill should walk them.</param>
    /// <param name="category">A single category, or <see cref="AnyCategory"/> for all of them.</param>
    /// <param name="maximumPrice">The spending ceiling, or 0 for no ceiling.</param>
    /// <param name="staged">What the buy list already holds; those amounts count against each desire.</param>
    public static ComponentFillPlan Plan(
        IReadOnlyList<ComponentFillDesire> desires,
        uint category,
        int maximumPrice,
        IReadOnlyList<VendorShopItem> shopItems,
        VendorShopProfile profile,
        IReadOnlyList<VendorStagingEntry> staged)
    {
        ArgumentNullException.ThrowIfNull(desires);
        ArgumentNullException.ThrowIfNull(shopItems);
        ArgumentNullException.ThrowIfNull(staged);

        var adds = new List<ComponentFillAdd>();
        var shortComponents = new List<string>();
        var stagedQuantities = new Dictionary<uint, int>(staged.Count);
        foreach (VendorStagingEntry entry in staged)
            stagedQuantities[entry.ItemGuid] = entry.Quantity;

        bool abortedOnPrice = false;
        int spent = 0;

        foreach (ComponentFillDesire desire in desires)
        {
            // The ceiling is tested before each row, so the buy that crossed
            // it is the one rolled back.
            if (maximumPrice != 0 && spent >= maximumPrice)
            {
                abortedOnPrice = true;
                break;
            }

            if (category != AnyCategory && desire.Category != category)
                continue;
            if (desire.Owned >= desire.Desired)
                continue;

            int shortfall = desire.Desired - desire.Owned;
            int available = shortfall;
            if (!TryFindStock(shopItems, desire.WeenieClassId, ref available, out VendorShopItem stock))
            {
                shortComponents.Add(desire.Name);
                continue;
            }

            stagedQuantities.TryGetValue(stock.ItemGuid, out int alreadyStaged);
            int wanted = shortfall - alreadyStaged;

            if (available < wanted)
            {
                shortComponents.Add(desire.Name);
                wanted = available;
            }

            if (wanted <= 0)
                continue;

            adds.Add(new ComponentFillAdd(stock.ItemGuid, wanted));
            stagedQuantities[stock.ItemGuid] = alreadyStaged + wanted;
            spent += VendorPricing.SellPrice(
                VendorPricing.PerUnitValue(stock.Value ?? 0, stock.DescStackSize),
                stock.ItemType ?? 0u,
                profile.SellPrice,
                wanted);
        }

        return new ComponentFillPlan(adds, abortedOnPrice, shortComponents);
    }

    /// <summary>The one line naming every component the shop could not fully supply.</summary>
    public static string FormatShortComponents(IReadOnlyList<string> shortComponents)
    {
        ArgumentNullException.ThrowIfNull(shortComponents);
        var text = new StringBuilder(ShortComponentsPrefix);
        for (int i = 0; i < shortComponents.Count; i++)
        {
            if (i > 0) text.Append(ShortComponentsSeparator);
            text.Append(shortComponents[i]);
        }
        return text.ToString();
    }

    /// <summary>
    /// Finds the shop's listing for a component class, narrowing
    /// <paramref name="available"/> to what it actually stocks.
    /// </summary>
    private static bool TryFindStock(
        IReadOnlyList<VendorShopItem> shopItems,
        uint weenieClassId,
        ref int available,
        out VendorShopItem stock)
    {
        foreach (VendorShopItem item in shopItems)
        {
            if (item.WeenieClassId != weenieClassId)
                continue;
            if (item.StackSize != UnlimitedStock && item.StackSize < available)
                available = item.StackSize;
            stock = item;
            return true;
        }

        stock = default;
        return false;
    }
}
