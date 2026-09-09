namespace AcDream.App.UI.Layout;

public static class SampleData
{
    public static CharacterSheet SampleCharacter() => SampleCharacter(null);

    public static CharacterSheet SampleCharacter(string? name) => new()
    {
        Name       = string.IsNullOrWhiteSpace(name) ? "Studio Player" : name,
        Level      = 126,
        Gender     = "Female",
        Heritage   = "Aluvian",
        Title      = "the Adventurer",
        BirthDate  = "January 5, 2001",
        PlayTime   = "2 years, 114 days, 4 hours",
        Deaths     = 42,

        PkStatus      = "Non-Player Killer",
        TotalXp       = 1_250_000_000,
        XpToNextLevel = 42_000_000,
        XpFraction    = 0.63f,

        HealthCurrent  = 5,    HealthMax  = 5,
        StaminaCurrent = 10,   StaminaMax = 10,
        ManaCurrent    = 10,   ManaMax    = 10,

        Strength     = 200,
        Endurance    = 10,
        Quickness    = 200,
        Coordination = 10,
        Focus        = 10,
        Self         = 10,

        UnspentSkillCredits     = 12,
        SpecializedSkillCredits = 4,
        ChessRank = 12,
        FishingSkill = 4,

        SkillCredits = 96,

        UnassignedXp = 87_757_321_741L,

        AttributeRaiseCosts = new long[] { 0L, 95L, 100L, 0L, 110L, 105L, 90L, 88L, 112L },
        AttributeRaise10Costs = new long[] { 0L, 950L, 1_000L, 0L, 1_100L, 1_050L, 900L, 880L, 1_120L },

        Skills = new CharacterSkill[]
        {
            new( 6, "Melee Defense",   0x06000165u, CharacterSkillAdvancementClass.Specialized, 350, 354, false, 10, 20, 18_250_000L, 182_500_000L),
            new(34, "War Magic",       0x06001365u, CharacterSkillAdvancementClass.Specialized, 280, 285, false, 16, 28, 11_100_000L, 111_000_000L),

            new(14, "Arcane Lore",     0x0600016Eu, CharacterSkillAdvancementClass.Trained,     260, 269, false,  4,  6,  7_500_000L,  75_000_000L),
            new(33, "Life Magic",      0x06001364u, CharacterSkillAdvancementClass.Trained,     250, 252, false, 12, 20,  6_800_000L,  68_000_000L),
            new(47, "Missile Weapons", 0x0600015Fu, CharacterSkillAdvancementClass.Trained,     220, 221, false,  6, 12,  5_250_000L,  52_500_000L),

            new(21, "Healing",         0x06000133u, CharacterSkillAdvancementClass.Untrained,    10,  10, true,   6, 10, 0L),
            new(22, "Jump",            0x0600016Bu, CharacterSkillAdvancementClass.Untrained,   210, 210, true,   0,  4, 0L),
            new(36, "Loyalty",         0x06001367u, CharacterSkillAdvancementClass.Untrained,    10,  10, true,   0,  2, 0L),
            new(24, "Run",             0x06000173u, CharacterSkillAdvancementClass.Untrained,   390, 390, true,   0,  4, 0L),

            new(38, "Alchemy",         0x060019E4u, CharacterSkillAdvancementClass.Untrained,    10,  10, false,  6, 12, 0L),
            new(39, "Cooking",         0x06001A54u, CharacterSkillAdvancementClass.Untrained,    10,  10, false,  4,  8, 0L),
            new(37, "Fletching",       0x06001A55u, CharacterSkillAdvancementClass.Untrained,    10,  10, false,  4,  8, 0L),
        },

        CharacterInfoProperties = new Dictionary<uint, int>
        {
            [0x162u] = 2, // Swords melee mastery
        },

        BurdenCurrent = 1200,
        BurdenMax     = 4500,
    };
}
