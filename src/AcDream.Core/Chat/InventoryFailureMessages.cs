using AcDream.Core.Items;

namespace AcDream.Core.Chat;

public static class InventoryFailureMessages
{
    public static string? Compose(
        InventoryRequestKind kind,
        string itemName,
        uint weenieError)
    {
        string? verb = kind switch
        {
            InventoryRequestKind.Merge => "merged",
            InventoryRequestKind.SplitToContainer => "split",
            InventoryRequestKind.SplitToWorld => "split",
            InventoryRequestKind.Move => "moved",
            InventoryRequestKind.Pickup => "picked up",
            InventoryRequestKind.PutInContainer => "put in the container",
            InventoryRequestKind.DropToWorld => "dropped",
            InventoryRequestKind.Wield => "wielded",
            InventoryRequestKind.Give => "given",
            _ => null,
        };
        if (verb is null)
            return null;

        return $"The {itemName} can't be {verb}{Suffix(weenieError)}";
    }

    private static string Suffix(uint weenieError) => weenieError switch
    {
        0x1Du => " - you're too busy",
        0x20u => " - you must control both objects",
        0x28u => " - the item is under someone else's control",
        0x2Au => " - you are too encumbered",
        0x36u => " - action cancelled",
        0x37u or 0x38u or 0x39u => " - unable to move to object",
        0x3EEu => " - the container is closed",
        _ => "",
    };

    public static bool SuppressesGenericFailureText(uint weenieError)
        => weenieError is 0x1Eu or 0x2Bu or 0x3EFu or 0x43Eu or 0x4CEu
            or 0x4CFu or 0x46Au;
}
