namespace AcDream.Core.Items;

public static class SalvageItemPolicy
{
    public static bool IsValidMaterial(uint material) => material is
        1 or 2 or >= 4 and <= 8 or >= 10 and <= 55 or >= 57 and <= 64
        or >= 66 and <= 71 or >= 73 and <= 77;

    public static bool IsSuitable(ClientObject item, uint selectedMaterial = 0, bool allowMultipleMaterials = true)
    {
        uint material = item.MaterialType ?? 0u;
        return IsValidMaterial(material)
            && item.Structure < 100
            && ((item.PublicWeenieBitfield ?? 0u) & 0xFF000000u) == 0u
            && (allowMultipleMaterials || selectedMaterial == 0u || selectedMaterial == material);
    }
}
