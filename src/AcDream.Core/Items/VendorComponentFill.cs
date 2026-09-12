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

/// <summary>What the component catalog knows about one component class.</summary>
public readonly record struct ComponentDescription(uint Category, string Name);

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
    // The seven component categories, numbered as the component table in the
    // game data numbers them. Both the command's category words and the
    // component book's own grouping key resolve to these, so they must not
    // drift apart: a mismatch silently fills the wrong category.
    public const uint ScarabCategory = 0u;
    public const uint HerbCategory = 1u;
    public const uint PowderedGemCategory = 2u;
    public const uint AlchemicalSubstanceCategory = 3u;
    public const uint TalismanCategory = 4u;
    public const uint TaperCategory = 5u;
    public const uint PeaCategory = 6u;

    /// <summary>How many real categories there are; the value above the last one.</summary>
    public const uint CategoryCount = 7u;

    /// <summary>The category value meaning "no category in particular" — fill matches every row.</summary>
    public const uint AnyCategory = 8u;

    /// <summary>The listed stack size a shop uses for "stocked without limit".</summary>
    public const int UnlimitedStock = -1;

    public const string ShortComponentsPrefix = "There was not enough: ";

    public const string ShortComponentsSeparator = ", ";

    public const string AbortedOnPriceMessage = "Buying aborted; max price reached.";

    /// <summary>
    /// Turns the component book's desired counts into the rows a fill walks:
    /// each component's category and name from the component catalog, its
    /// owned count from everything the player is carrying, ordered the way
    /// the component book lists them.
    /// </summary>
    /// <param name="describe">
    /// The catalog lookup, or null for a component the catalog does not know.
    /// </param>
    /// <param name="shopItems">
    /// Used only to name a component the catalog does not know; a component
    /// that stays nameless is simply never named in the shortfall report.
    /// </param>
    public static IReadOnlyList<ComponentFillDesire> BuildDesires(
        IReadOnlyDictionary<uint, uint> desiredComponents,
        ClientObjectTable objects,
        uint playerGuid,
        Func<uint, ComponentDescription?> describe,
        IReadOnlyList<VendorShopItem> shopItems)
    {
        ArgumentNullException.ThrowIfNull(desiredComponents);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(shopItems);

        var owned = new Dictionary<uint, int>();
        foreach (ClientObject item in objects.Objects)
        {
            if (!objects.IsOwnedByObject(item.ObjectId, playerGuid))
                continue;
            owned.TryGetValue(item.WeenieClassId, out int count);
            owned[item.WeenieClassId] = count + Math.Max(1, item.StackSize);
        }

        var desires = new List<ComponentFillDesire>();
        foreach ((uint weenieClassId, uint desired) in desiredComponents)
        {
            if (desired == 0u)
                continue;

            ComponentDescription? described = describe(weenieClassId);
            string name = described?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                name = FindStockName(shopItems, weenieClassId);

            owned.TryGetValue(weenieClassId, out int ownedCount);
            desires.Add(new ComponentFillDesire(
                weenieClassId,
                described?.Category ?? AnyCategory,
                name,
                (int)desired,
                ownedCount));
        }

        // Walk the rows in the order the component book lists them, so the
        // component a price ceiling cuts off is the one the player can see
        // it stopped at.
        desires.Sort(static (left, right) =>
        {
            int byCategory = left.Category.CompareTo(right.Category);
            return byCategory != 0
                ? byCategory
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return desires;
    }

    private static string FindStockName(
        IReadOnlyList<VendorShopItem> shopItems,
        uint weenieClassId)
    {
        foreach (VendorShopItem item in shopItems)
        {
            if (item.WeenieClassId == weenieClassId && !string.IsNullOrWhiteSpace(item.Name))
                return item.Name;
        }
        return string.Empty;
    }

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
                Report(shortComponents, desire.Name);
                continue;
            }

            stagedQuantities.TryGetValue(stock.ItemGuid, out int alreadyStaged);
            int wanted = shortfall - alreadyStaged;

            if (available < wanted)
            {
                Report(shortComponents, desire.Name);
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

    /// <summary>
    /// Names one component in the shortfall report. A component with no
    /// resolved name is left out rather than reported as a blank.
    /// </summary>
    private static void Report(List<string> shortComponents, string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            shortComponents.Add(name);
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
