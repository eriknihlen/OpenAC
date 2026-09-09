using AcDream.Core.Items;

namespace AcDream.App.UI;

internal static class ItemEquipRules
{
    public const EquipMask AutoWearMask =
        EquipMask.HeadWear
        | EquipMask.ChestWear
        | EquipMask.AbdomenWear
        | EquipMask.UpperArmWear
        | EquipMask.LowerArmWear
        | EquipMask.HandWear
        | EquipMask.UpperLegWear
        | EquipMask.LowerLegWear
        | EquipMask.FootWear
        | EquipMask.ChestArmor
        | EquipMask.AbdomenArmor
        | EquipMask.UpperArmArmor
        | EquipMask.LowerArmArmor
        | EquipMask.UpperLegArmor
        | EquipMask.LowerLegArmor
        | EquipMask.Cloak;

    public static bool IsAutoWearItem(ClientObject item)
        => (item.ValidLocations & AutoWearMask) != EquipMask.None;

    public static EquipMask ResolvePaperdollDropWieldMask(ClientObject item, EquipMask targetMask)
    {
        if ((item.ValidLocations & targetMask) == EquipMask.None)
            return EquipMask.None;

        return IsAutoWearItem(item)
            ? item.ValidLocations
            : item.ValidLocations & targetMask;
    }
}
