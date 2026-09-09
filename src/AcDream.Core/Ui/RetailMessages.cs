namespace AcDream.Core.Ui;

public static class RetailMessages
{
    public static string CannotBeUsed(string entityName)
        => $"The {entityName} cannot be used";

    public static string CantBePickedUp(string entityName)
        => $"The {entityName} can't be picked up!";

    public const string CannotPickUpCreatures = "You cannot pick up creatures!";

    public static string BeingWieldedBySomeoneElse(string entityName)
        => $"The {entityName} is being wielded by someone else!";

    public static string CannotBeUsedWith(string targetName)
        => $"Cannot be used with {targetName}";

    public static string CannotBePickedUp(string entityName)
        => $"The {entityName} cannot be picked up!";

    public static string CannotBeUsedWhileOnHook_HooksOff(string entityName)
        => $"The {entityName} cannot be used while on a hook, use the '@house hooks on' command to make the hook openable.\n";

    public static string CannotBeUsedWhileOnHook_NotOwner(string entityName)
        => $"The {entityName} cannot be used while on a hook and only the owner may open the hook.\n";
}
