using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;

/// <summary>
/// Finds the stack a moving stack joins when it is put into a container
/// with joining asked for: a drop on a pack's icon, a pickup into the
/// backpack, an automation move with the join flag. The container's own
/// items are searched first, then each pack inside it, pack by pack; the
/// first stack of the same thing with room for everything that moves is
/// the one. A stack with room for only part of it is passed over, so the
/// move stays whole.
/// </summary>
public static class StackJoinSearch
{
    /// <summary>
    /// The merge of all <paramref name="requested"/> of
    /// <paramref name="sourceId"/> into the first stack in
    /// <paramref name="containerId"/> that takes it, or null when none does.
    /// </summary>
    public static StackMergePlan? Plan(
        ClientObjectTable objects,
        uint sourceId,
        uint containerId,
        uint requested,
        bool readyForInventoryRequest)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (containerId == 0u
            || requested == 0u
            || objects.Get(sourceId) is not { } source
            || source.StackSizeMax <= 1)
        {
            return null;
        }

        int requestedAmount = (int)Math.Min(requested, int.MaxValue);
        StackMergeItem sourceMerge = ToStackMergeItem(source);
        foreach (uint targetId in ExhaustiveContainedItems(objects, containerId))
        {
            if (objects.Get(targetId) is not { } target)
                continue;
            StackMergePlan? plan = StackMergePlanner.Plan(
                sourceMerge,
                ToStackMergeItem(target),
                readyForInventoryRequest,
                requestedAmount);
            if (plan is { } complete && complete.Amount == requested)
                return complete;
        }
        return null;
    }

    /// <summary>
    /// Everything a container holds, for finding a stack to join: its own
    /// items in order, then the items of each pack inside it, pack by pack.
    /// </summary>
    public static IEnumerable<uint> ExhaustiveContainedItems(
        ClientObjectTable objects,
        uint containerId)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return ExhaustiveContainedItems(objects, containerId, new HashSet<uint>());
    }

    private static IEnumerable<uint> ExhaustiveContainedItems(
        ClientObjectTable objects,
        uint containerId,
        HashSet<uint> visitedContainers)
    {
        if (!visitedContainers.Add(containerId))
            yield break;
        var packs = new List<uint>();
        foreach (uint itemId in objects.GetContents(containerId))
        {
            if (objects.Get(itemId) is { } held
                && InventoryContainerPlacementPolicy.IsContainer(held))
            {
                packs.Add(itemId);
                continue;
            }
            yield return itemId;
        }
        foreach (uint pack in packs)
        {
            foreach (uint itemId in ExhaustiveContainedItems(objects, pack, visitedContainers))
                yield return itemId;
        }
    }

    /// <summary>The stacking facts of <paramref name="item"/>.</summary>
    public static StackMergeItem ToStackMergeItem(ClientObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new(
            item.ObjectId,
            item.WeenieClassId,
            item.StackSize,
            item.StackSizeMax,
            item.TradeState);
    }
}
