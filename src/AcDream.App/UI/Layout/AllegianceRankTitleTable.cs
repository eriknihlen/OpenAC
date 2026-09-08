namespace AcDream.App.UI.Layout;

internal static class AllegianceRankTitleTable
{
    public const uint AllegianceRankPropertyId = 0x1Eu;

    public static string ComposeFullName(int rank, int heritageGroup, int gender, string name)
    {
        string? title = GetTitle(rank, heritageGroup, gender);
        return string.IsNullOrEmpty(title) ? name : $"{title} {name}";
    }

    public static string? GetTitle(int rank, int heritageGroup, int gender)
    {
        if (gender == 1)
        {
            if (!IsHeritageInRange(heritageGroup)) return null;
            return heritageGroup switch
            {
                1 => GetAluvianMaleTitle(rank),
                2 => GetGharundimMaleTitle(rank),
                3 => GetShoMaleTitle(rank),
                4 => GetViamontianMaleTitle(rank),
                5 or 0xA => GetShadowboundMaleTitle(rank), // 0xA = Penumbraen alias
                6 => GetGearknightMaleTitle(rank),
                7 => GetTumerokMaleTitle(rank),
                8 => GetLugianFemaleTitle(rank), // Lugian authors FEMALE only; reused here
                9 => GetEmpyreanMaleTitle(rank),
                0xB => GetUndeadMaleTitle(rank),
                _ => null,
            };
        }

        if (gender == 2)
        {
            if (!IsHeritageInRange(heritageGroup)) return null;
            return heritageGroup switch
            {
                1 => GetAluvianFemaleTitle(rank),
                2 => GetGharundimFemaleTitle(rank),
                3 => GetShoFemaleTitle(rank),
                4 => GetViamontianFemaleTitle(rank),
                5 or 0xA => GetShadowboundFemaleTitle(rank), // 0xA = Penumbraen alias
                6 => GetGearknightMaleTitle(rank), // Gearknight authors MALE only; reused here
                7 => GetTumerokMaleTitle(rank), // Tumerok authors MALE only; reused here
                8 => GetLugianFemaleTitle(rank),
                9 => GetEmpyreanFemaleTitle(rank),
                0xB => GetUndeadFemaleTitle(rank),
                _ => null,
            };
        }

        return null;
    }

    private static bool IsHeritageInRange(int heritageGroup)
        => unchecked((uint)(heritageGroup - 1)) <= 0xAu;


    private static string? GetAluvianMaleTitle(int rank) => rank switch
    {
        1 => "Yeoman",
        2 => "Baronet",
        3 => "Baron",
        4 => "Reeve",
        5 => "Thane",
        6 => "Ealdor",
        7 => "Duke",
        8 => "Aetheling",
        9 => "King",
        10 => "High King",
        _ => null,
    };

    private static string? GetAluvianFemaleTitle(int rank) => rank switch
    {
        1 => "Yeoman",
        2 => "Baronet",
        3 => "Baroness",
        4 => "Reeve",
        5 => "Thane",
        6 => "Ealdor",
        7 => "Duchess",
        8 => "Aetheling",
        9 => "Queen",
        10 => "High Queen",
        _ => null,
    };

    private static string? GetGharundimMaleTitle(int rank) => rank switch
    {
        1 => "Sayyid",
        2 => "Shayk",
        3 => "Maulan",
        4 => "Mu'allim",
        5 => "Naquib",
        6 => "Qadi",
        7 => "Mushir",
        8 => "Amir",
        9 => "Malik",
        10 => "Sultan",
        _ => null,
    };

    private static string? GetGharundimFemaleTitle(int rank) => rank switch
    {
        1 => "Sayyida",
        2 => "Shayka",
        3 => "Maulana",
        4 => "Mu'allima",
        5 => "Naquiba",
        6 => "Qadiya",
        7 => "Mushira",
        8 => "Amira",
        9 => "Malika",
        10 => "Sultana",
        _ => null,
    };

    private static string? GetShoMaleTitle(int rank) => rank switch
    {
        1 => "Jinin",
        2 => "Jo-chueh",
        3 => "Nan-chueh",
        4 => "Shi-chueh",
        5 => "Ta-chueh",
        6 => "Kun-chueh",
        7 => "Kou",
        8 => "Taikou",
        9 => "Ou",
        10 => "Koutei",
        _ => null,
    };

    private static string? GetShoFemaleTitle(int rank) => rank switch
    {
        1 => "Jinin",
        2 => "Jo-chueh",
        3 => "Nan-chueh",
        4 => "Shi-chueh",
        5 => "Ta-chueh",
        6 => "Kun-chueh",
        7 => "Kou",
        8 => "Taikou",
        9 => "Jo-ou",
        10 => "Koutei",
        _ => null,
    };

    private static string? GetViamontianMaleTitle(int rank) => rank switch
    {
        1 => "Squire",
        2 => "Banner",
        3 => "Baron",
        4 => "Viscount",
        5 => "Count",
        6 => "Marquis",
        7 => "Duke",
        8 => "Grand Duke",
        9 => "King",
        10 => "High King",
        _ => null,
    };

    private static string? GetViamontianFemaleTitle(int rank) => rank switch
    {
        1 => "Dame",
        2 => "Banner",
        3 => "Baroness",
        4 => "Viscountess",
        5 => "Countess",
        6 => "Marquise",
        7 => "Duchess",
        8 => "Grand Duchess",
        9 => "Queen",
        10 => "High Queen",
        _ => null,
    };

    private static string? GetShadowboundMaleTitle(int rank) => rank switch
    {
        1 => "Tenebrous",
        2 => "Shade",
        3 => "Squire",
        4 => "Knight",
        5 => "Void Knight",
        6 => "Void Lord",
        7 => "Duke",
        8 => "Archduke",
        9 => "Highborn",
        10 => "King",
        _ => null,
    };

    private static string? GetShadowboundFemaleTitle(int rank) => rank switch
    {
        1 => "Tenebrous",
        2 => "Shade",
        3 => "Squire",
        4 => "Knight",
        5 => "Void Knight",
        6 => "Void Lady",
        7 => "Duchess",
        8 => "Archduchess",
        9 => "Highborn",
        10 => "Queen",
        _ => null,
    };

    private static string? GetGearknightMaleTitle(int rank) => rank switch
    {
        1 => "Tribunus",
        2 => "Praefectus",
        3 => "Optio",
        4 => "Centurion",
        5 => "Principes",
        6 => "Legatus",
        7 => "Consul",
        8 => "Dux",
        9 => "Secondus",
        10 => "Primus",
        _ => null,
    };

    private static string? GetTumerokMaleTitle(int rank) => rank switch
    {
        1 => "Xutua",
        2 => "Tuona",
        3 => "Ona",
        4 => "Nuona",
        5 => "Turea",
        6 => "Rea",
        7 => "Nurea",
        8 => "Kauh",
        9 => "Sutah",
        10 => "Tah",
        _ => null,
    };

    private static string? GetLugianFemaleTitle(int rank) => rank switch
    {
        1 => "Laigus",
        2 => "Raigus",
        3 => "Amploth",
        4 => "Arintoth",
        5 => "Obeloth",
        6 => "Lithos",
        7 => "Kantos",
        8 => "Gigas",
        9 => "Extas",
        10 => "Tiatus",
        _ => null,
    };

    private static string? GetEmpyreanMaleTitle(int rank) => rank switch
    {
        1 => "Ensign",
        2 => "Corporal",
        3 => "Lieutenant",
        4 => "Commander",
        5 => "Captain",
        6 => "Commodore",
        7 => "Admiral",
        8 => "Warlord",
        9 => "Ipharsin",
        10 => "Aulin",
        _ => null,
    };

    private static string? GetEmpyreanFemaleTitle(int rank) => rank switch
    {
        1 => "Ensign",
        2 => "Corporal",
        3 => "Lieutenant",
        4 => "Commander",
        5 => "Captain",
        6 => "Commodore",
        7 => "Admiral",
        8 => "Warlord",
        9 => "Ipharsia",
        10 => "Aulia",
        _ => null,
    };

    private static string? GetUndeadMaleTitle(int rank) => rank switch
    {
        1 => "Neophyte",
        2 => "Acolyte",
        3 => "Adept",
        4 => "Esquire",
        5 => "Squire",
        6 => "Knight",
        7 => "Count",
        8 => "Viscount",
        9 => "Highness",
        10 => "Annointed",
        _ => null,
    };

    private static string? GetUndeadFemaleTitle(int rank) => rank switch
    {
        1 => "Neophyte",
        2 => "Acolyte",
        3 => "Adept",
        4 => "Esquire",
        5 => "Squire",
        6 => "Knight",
        7 => "Countess",
        8 => "Viscountess",
        9 => "Highness",
        10 => "Annointed",
        _ => null,
    };
}
