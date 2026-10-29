namespace AcDream.Core.Items;

public enum InventoryContainerPlacementRejection
{
    None,
    InvalidItem,
    CannotMovePlayer,
    CannotMoveCreature,
    SourceBeingTraded,
    InvalidDestination,
    DestinationBeingTraded,
    RecursiveContainment,
    ItemCapacityFull,
    ContainerCapacityFull,
    WrongHookType,
    HookHouseNotOwned,
}

public static class InventoryContainerPlacementPolicy
{
    public static InventoryContainerPlacementRejection Evaluate(
        ClientObjectTable objects,
        uint itemId,
        uint destinationId,
        uint playerId,
        bool wholeStack = true)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (itemId == 0u || objects.Get(itemId) is not { } item)
            return InventoryContainerPlacementRejection.InvalidItem;
        if (itemId == playerId)
            return InventoryContainerPlacementRejection.CannotMovePlayer;
        if ((item.Type & ItemType.Creature) != 0)
            return InventoryContainerPlacementRejection.CannotMoveCreature;
        if (item.TradeState == 1)
            return InventoryContainerPlacementRejection.SourceBeingTraded;
        ClientObject? destination = objects.Get(destinationId);
        if (destinationId == 0u
            || (destination is null && destinationId != playerId)
            || (destination is not null && !IsContainer(destination) && destinationId != playerId))
        {
            return InventoryContainerPlacementRejection.InvalidDestination;
        }
        if (destination?.TradeState == 1)
            return InventoryContainerPlacementRejection.DestinationBeingTraded;
        if (itemId == destinationId || IsContainedBy(objects, destinationId, itemId))
            return InventoryContainerPlacementRejection.RecursiveContainment;
        if (destination is { IsHook: true })
        {
            // Only the owner of the house may use its hooks; a hook of a
            // house nobody owns takes nothing.
            if (destination.HouseOwnerId is null or 0u)
                return InventoryContainerPlacementRejection.HookHouseNotOwned;
            if (!FitsHook(item, destination))
                return InventoryContainerPlacementRejection.WrongHookType;
        }

        bool alreadyDirectlyContained = item.ContainerId == destinationId;
        if (IsContainer(item))
        {
            int capacity = destination?.ContainersCapacity ?? 0;
            if (!alreadyDirectlyContained
                && capacity > 0
                && CountContainers(objects, destinationId) >= capacity)
            {
                return InventoryContainerPlacementRejection.ContainerCapacityFull;
            }
        }
        else
        {
            // Part of a stack takes a slot of its own, even beside the rest
            // of it: only the whole stack is a reorder.
            int capacity = destination?.ItemsCapacity ?? 0;
            if (!(alreadyDirectlyContained && wholeStack)
                && capacity > 0
                && CountItems(objects, destinationId) >= capacity)
            {
                return InventoryContainerPlacementRejection.ItemCapacityFull;
            }
        }

        return InventoryContainerPlacementRejection.None;
    }

    public static string? ComposeClientLocal(
        InventoryContainerPlacementRejection rejection,
        ClientObject? item,
        ClientObject? destination,
        uint playerId)
    {
        string itemName = item?.GetAppropriateName() ?? "item";
        string destinationName = destination?.GetAppropriateName() ?? "container";
        return rejection switch
        {
            InventoryContainerPlacementRejection.None => null,
            InventoryContainerPlacementRejection.InvalidItem => "That item is not valid!",
            InventoryContainerPlacementRejection.CannotMovePlayer =>
                "You cannot place yourself within another object!",
            InventoryContainerPlacementRejection.CannotMoveCreature =>
                "You cannot pick up creatures!",
            InventoryContainerPlacementRejection.SourceBeingTraded =>
                $"The {itemName} is being traded",
            InventoryContainerPlacementRejection.InvalidDestination =>
                "The destination container is not valid!",
            InventoryContainerPlacementRejection.DestinationBeingTraded =>
                $"The {destinationName} is being traded",
            InventoryContainerPlacementRejection.RecursiveContainment =>
                "You cannot place an object within itself!",
            // The player's own inventory is named "Backpack" in the item form.
            InventoryContainerPlacementRejection.ItemCapacityFull =>
                destination?.ObjectId == playerId
                    ? "Backpack is completely full!"
                    : $"The {destinationName} is completely full!",
            InventoryContainerPlacementRejection.ContainerCapacityFull =>
                destination?.ObjectId == playerId
                    ? $"{destinationName} can carry no more containers!"
                    : $"The {destinationName} can fit no more containers!",
            InventoryContainerPlacementRejection.WrongHookType =>
                CannotPlaceOnHook,
            InventoryContainerPlacementRejection.HookHouseNotOwned =>
                CannotPlaceOnHook + " You must own the house to manipulate the hook.",
            _ => null,
        };
    }

    private const string CannotPlaceOnHook = "That item cannot be placed on the hook.";

    /// <summary>
    /// The container-type field an object carries: 0 an ordinary item, 1 a
    /// pack, 2 a spell focus. A focus holds nothing, but it takes a
    /// container slot rather than an item slot, which is why
    /// <see cref="IsContainer"/> counts it as one.
    /// </summary>
    private const uint FocusContainerType = 2u;

    /// <summary>
    /// Whether this object occupies a CONTAINER slot rather than an item
    /// slot. A spell focus does, so it answers true here even though it can
    /// hold nothing -- use <see cref="CanHoldItems"/> to ask whether
    /// something can go inside.
    /// </summary>
    public static bool IsContainer(ClientObject item)
        => item.ContainerTypeHint != 0u
            || (item.Type & ItemType.Container) != 0
            || item.ItemsCapacity != 0
            || item.ContainersCapacity != 0;

    /// <summary>
    /// A spell focus. It counts as a container for slot accounting but
    /// holds nothing, so it is never somewhere an item can go: the server
    /// refuses any request naming one as the destination.
    /// </summary>
    public static bool IsFocus(ClientObject item)
        => item.ContainerTypeHint == FocusContainerType;

    /// <summary>
    /// An item names the kinds of hook it hangs on (floor, wall, ceiling,
    /// yard, roof) and a hook names the item types it takes. An item that
    /// names no hook kind fits none.
    /// </summary>
    private static bool FitsHook(ClientObject item, ClientObject hook)
        => (item.HookType & hook.HookType) != 0u
            && (hook.HookItemTypes & (uint)item.Type) != 0u;

    private static int CountItems(ClientObjectTable objects, uint containerId)
    {
        int count = 0;
        foreach (uint childId in objects.GetContents(containerId))
        {
            if (objects.Get(childId) is { } child && !IsContainer(child))
                count++;
        }
        return count;
    }

    private static int CountContainers(ClientObjectTable objects, uint containerId)
    {
        int count = 0;
        foreach (uint childId in objects.GetContents(containerId))
        {
            if (objects.Get(childId) is { } child && IsContainer(child))
                count++;
        }
        return count;
    }

    private static bool IsContainedBy(
        ClientObjectTable objects,
        uint candidateId,
        uint possibleAncestorId)
    {
        var visited = new HashSet<uint>();
        uint current = candidateId;
        while (current != 0u && visited.Add(current))
        {
            if (current == possibleAncestorId)
                return true;
            current = objects.Get(current)?.ContainerId ?? 0u;
        }
        return false;
    }
}
