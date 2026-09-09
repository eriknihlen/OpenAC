namespace AcDream.Core.Items;

public static class ToolbarAmmoPolicy
{
    public readonly record struct Result(uint ObjectId, int DisplayCount)
    {
        public bool IsVisible => ObjectId != 0;
    }

    public static Result Resolve(IReadOnlyList<ClientObject> inventoryPlacements)
    {
        ArgumentNullException.ThrowIfNull(inventoryPlacements);

        ClientObject? missileWeapon = FindAtLocation(
            inventoryPlacements,
            EquipMask.MissileWeapon);
        ClientObject? ammo = missileWeapon is { StackSizeMax: > 1 }
            ? missileWeapon
            : FindAtLocation(inventoryPlacements, EquipMask.MissileAmmo);

        return ammo is null
            ? default
            : new Result(ammo.ObjectId, ammo.StackSize == 0 ? 1 : ammo.StackSize);
    }

    private static ClientObject? FindAtLocation(
        IReadOnlyList<ClientObject> inventoryPlacements,
        EquipMask location)
    {
        foreach (ClientObject item in inventoryPlacements)
            if ((item.CurrentlyEquippedLocation & location) != 0)
                return item;
        return null;
    }
}
