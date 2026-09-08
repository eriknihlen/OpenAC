namespace AcDream.App.UI.Layout;

internal static class CharacterIdentityText
{
    public const uint GenderPropertyId = 0x71u;
    public const uint HeritageGroupPropertyId = 0xBCu;

    public static string StatHeaderLine(CharacterSheet sheet)
    {
        if (string.IsNullOrWhiteSpace(sheet.Gender))
            return Join(sheet.Heritage, sheet.Title);
        return Join(sheet.Gender, sheet.Heritage, sheet.Title);
    }

    public static string? GenderDisplayName(int gender) => gender switch
    {
        1 => "Male",
        2 => "Female",
        _ => null,
    };

    public static string? HeritageGroupDisplayName(int heritageGroup) => heritageGroup switch
    {
        1 => "Aluvian",
        2 => "Gharu'ndim",
        3 => "Sho",
        4 => "Viamontian",
        5 => "Umbraen",
        6 => "Gearknight",
        7 => "Tumerok",
        8 => "Lugian",
        9 => "Empyrean",
        10 => "Penumbraen",
        11 => "Undead",
        12 => "Olthoi",
        13 => "Olthoi",
        _ => null,
    };

    public static string GenderHeritageDisplay(
        int gender,
        int heritageGroup,
        string? creatureTypeFallback)
    {
        string? heritage = heritageGroup == 0
            ? creatureTypeFallback
            : HeritageGroupDisplayName(heritageGroup);
        return Join(GenderDisplayName(gender), heritage);
    }

    private static string Join(params string?[] parts)
    {
        return string.Join(" ", parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim()));
    }
}
