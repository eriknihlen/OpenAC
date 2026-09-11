namespace AcDream.Core.Items;

/// <summary>
/// Picks the pack an item goes into when the player puts it somewhere in
/// their inventory: the pack they named if it has room, else the main pack,
/// else the side packs in order. The server fills exactly the container it
/// is asked for, so this choice has to be made here before the request is
/// sent. When nothing has room the caller shows one of four notices.
/// </summary>
public static class InventoryPlacementSearch
{
    /// <summary>
    /// Item slots: -1 means no limit and 0 means the server sent no capacity
    /// (only the player and packs carry one), so both are unbounded here.
    /// Container slots: -1 means no limit but 0 is real, since side packs
    /// hold no packs.
    /// </summary>
    private static bool IsUnboundedItemCapacity(int capacity) => capacity <= 0;
    private static bool IsUnboundedContainerCapacity(int capacity) => capacity < 0;

    private static bool IsContainer(ClientObject? item) =>
        item is not null && InventoryContainerPlacementPolicy.IsContainer(item);

    /// <summary>
    /// True when <paramref name="containerId"/> can take <paramref name="itemId"/>:
    /// the container must be the player or in the player's inventory and not
    /// being traded; a plain item needs a free item slot (an unbounded
    /// capacity always fits, and an item already inside counts as fitting);
    /// a pack needs a free container slot under the same rules.
    /// </summary>
    public static bool WillItemFitInContainer(
        ClientObjectTable objects,
        uint itemId,
        uint containerId,
        uint playerId)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (containerId == 0u || itemId == containerId)
            return false;
        ClientObject? container = objects.Get(containerId);
        if (container is null)
        {
            // The player's own object is the root of every inventory; when
            // it has not been described yet its capacity is unknown, which
            // the placement policy also treats as unbounded.
            return containerId == playerId;
        }
        bool ownedByPlayer = containerId == playerId
            || (playerId != 0u && objects.IsOwnedByObject(containerId, playerId));
        if (!ownedByPlayer || container.TradeState == 1)
            return false;

        ClientObject? item = objects.Get(itemId);
        IReadOnlyList<uint> contents = objects.GetContents(containerId);
        if (!IsContainer(item))
        {
            int capacity = container.ItemsCapacity;
            if (IsUnboundedItemCapacity(capacity))
                return true;
            int loose = 0;
            foreach (uint id in contents)
            {
                if (id == itemId)
                    return true;
                if (!IsContainer(objects.Get(id)))
                    loose++;
            }
            return loose < capacity;
        }

        int containerCapacity = container.ContainersCapacity;
        if (IsUnboundedContainerCapacity(containerCapacity))
            return true;
        int packs = 0;
        foreach (uint id in contents)
        {
            if (id == itemId)
                return true;
            if (IsContainer(objects.Get(id)))
                packs++;
        }
        return packs < containerCapacity;
    }

    /// <summary>
    /// The container to request for <paramref name="itemId"/>: the named
    /// <paramref name="targetId"/> if it fits there, else the root (the
    /// player's main pack), else the first of the root's packs with room, in
    /// placement order. Returns 0 with the capacity rejection when nothing
    /// has room; the notice for it names the root, not the pack that was
    /// dropped on (<see cref="InventoryContainerPlacementPolicy.ComposeClientLocal"/>).
    /// </summary>
    public static uint ChooseContainer(
        ClientObjectTable objects,
        uint itemId,
        uint rootId,
        uint targetId,
        uint playerId,
        out InventoryContainerPlacementRejection refusal)
    {
        ArgumentNullException.ThrowIfNull(objects);
        refusal = InventoryContainerPlacementRejection.None;
        if (targetId != 0u && WillItemFitInContainer(objects, itemId, targetId, playerId))
            return targetId;
        if (rootId != 0u && WillItemFitInContainer(objects, itemId, rootId, playerId))
            return rootId;

        if (rootId != 0u)
        {
            List<ClientObject> packs = [];
            foreach (uint id in objects.GetContents(rootId))
            {
                if (objects.Get(id) is { } contained && IsContainer(contained))
                    packs.Add(contained);
            }
            packs.Sort(static (a, b) => a.ContainerSlot.CompareTo(b.ContainerSlot));
            foreach (ClientObject pack in packs)
            {
                if (WillItemFitInContainer(objects, itemId, pack.ObjectId, playerId))
                    return pack.ObjectId;
            }
        }

        refusal = IsContainer(objects.Get(itemId))
            ? InventoryContainerPlacementRejection.ContainerCapacityFull
            : InventoryContainerPlacementRejection.ItemCapacityFull;
        return 0u;
    }
}
