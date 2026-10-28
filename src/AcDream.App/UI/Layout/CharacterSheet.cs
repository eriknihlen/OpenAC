using System;
using System.Collections.Generic;

namespace AcDream.App.UI.Layout;

public sealed class CharacterSheet
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string Name { get; init; } = string.Empty;

    public int? Level { get; init; }

    /// <summary>Gender display string, e.g. "Female". Null = omit.</summary>
    public string? Gender { get; init; }

    public string? Race { get; init; }

    /// <summary>Heritage group display string, e.g. "Aluvian". Null = omit.</summary>
    public string? Heritage { get; init; }

    /// <summary>Title string, e.g. "the Adventurer". Null = omit.</summary>
    public string? Title { get; init; }


    public long TotalXp { get; init; }

    public long XpToNextLevel { get; init; }

    /// <summary>
    /// What stands in place of the number when there is no next level to
    /// earn: the character is at the top of the level table. Null while
    /// there is experience still to go, or while the table is not known.
    /// </summary>
    public string? XpToNextLevelText { get; init; }

    public float XpFraction { get; init; }

    public string? PkStatus { get; init; }

    public long AvailableLuminance { get; init; }

    public long MaximumLuminance { get; init; }


    public string? BirthDate { get; init; }

    public string? PlayTime { get; init; }

    public int Deaths { get; init; }

    public int? BirthTimestamp { get; init; }

    public int? TotalPlayTimeSeconds { get; init; }


    public int HealthCurrent  { get; init; }
    public int HealthMax      { get; init; }
    public int StaminaCurrent { get; init; }
    public int StaminaMax     { get; init; }
    public int ManaCurrent    { get; init; }
    public int ManaMax        { get; init; }

    /// <summary>
    /// Unenchanted max Health/Stamina/Mana in that order.
    /// </summary>
    public int[] VitalBaseMaxValues { get; init; } = Array.Empty<int>();

    public int[] VitalVitaeModifiers { get; init; } = Array.Empty<int>();


    public int Strength     { get; init; }
    public int Endurance    { get; init; }
    public int Quickness    { get; init; }
    public int Coordination { get; init; }
    public int Focus        { get; init; }
    public int Self         { get; init; }

    /// <summary>
    /// Each attribute without enchantments, as created plus raised:
    /// Strength, Endurance, Coordination, Quickness, Focus, Self. The
    /// character information page grades its resistances from these.
    /// </summary>
    public int[] AttributeBaseValues { get; init; } = Array.Empty<int>();

    /// <summary>
    /// What each attribute started at, before any experience was spent on it
    /// and with no enchantment counted: Strength, Endurance, Coordination,
    /// Quickness, Focus, Self. This is what "innate" means on the character
    /// information page.
    /// </summary>
    public int[] AttributeInnateValues { get; init; } = Array.Empty<int>();


    public int UnspentSkillCredits     { get; init; }
    public int SpecializedSkillCredits { get; init; }

    /// <summary>The chess rank a character who never played chess has.</summary>
    public const int UnrankedChessRank = 1400;

    public int ChessRank { get; init; } = UnrankedChessRank;

    public int FishingSkill { get; init; }

    public int SkillCredits { get; init; }

    public bool AwaitingRaise { get; init; }

    public long UnassignedXp { get; init; }


    public long[] AttributeRaiseCosts { get; init; } = Array.Empty<long>();

    public long[] AttributeRaise10Costs { get; init; } = Array.Empty<long>();

    public IReadOnlyList<CharacterSkill> Skills { get; init; } = Array.Empty<CharacterSkill>();


    public string? AugmentationName { get; init; }

    public IReadOnlyDictionary<uint, int> CharacterInfoProperties { get; init; }
        = new Dictionary<uint, int>();


    public int BurdenCurrent { get; init; }
    public int BurdenMax     { get; init; }
    public int EncumbranceAugmentations { get; init; }
}

public enum CharacterSkillAdvancementClass
{
    Inactive = 0,
    Untrained = 1,
    Trained = 2,
    Specialized = 3,
}

public sealed record CharacterSkill(
    uint Id,
    string Name,
    uint IconDid,
    CharacterSkillAdvancementClass AdvancementClass,
    int BaseLevel,
    int CurrentLevel,
    bool UsableUntrained,
    int TrainedCost,
    int SpecializedCost,
    long RaiseCost,
    long Raise10Cost = 0L,
    int VitaeModifier = 0,
    string? TooltipText = null);
