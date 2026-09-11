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
}

public static class InventoryContainerPlacementPolicy
{
    public static InventoryContainerPlacementRejection Evaluate(
        ClientObjectTable objects,
        uint itemId,
        uint destinationId,
        uint playerId)
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
            int capacity = destination?.ItemsCapacity ?? 0;
            if (!alreadyDirectlyContained
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
            _ => null,
        };
    }

    public static bool IsContainer(ClientObject item)
        => item.ContainerTypeHint != 0u
            || (item.Type & ItemType.Container) != 0
            || item.ItemsCapacity != 0
            || item.ContainersCapacity != 0;

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
