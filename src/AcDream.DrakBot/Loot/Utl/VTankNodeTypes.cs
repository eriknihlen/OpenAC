namespace AcDream.DrakBot.Loot.Utl;

/// <summary>
/// Static metadata about VTank loot rule node (condition) types: id ↔ display
/// name, data-line arity, value semantics. Single source of truth for both the
/// parser/writer and the editor's typed UIs.
/// </summary>
public static class VTankNodeTypes
{
    public const int SpellNameMatch                       = 0;
    public const int StringValueMatch                     = 1;
    public const int LongValKeyLE                         = 2;
    public const int LongValKeyGE                         = 3;
    public const int DoubleValKeyLE                       = 4;
    public const int DoubleValKeyGE                       = 5;
    public const int DamagePercentGE                      = 6;
    public const int ObjectClass                          = 7;
    public const int SpellCountGE                         = 8;
    public const int SpellMatch                           = 9;
    public const int MinDamageGE                          = 10;
    public const int LongValKeyFlagExists                 = 11;
    public const int LongValKeyE                          = 12;
    public const int LongValKeyNE                         = 13;
    public const int AnySimilarColor                      = 14;
    public const int SimilarColorArmorType                = 15;
    public const int SlotSimilarColor                     = 16;
    public const int SlotExactPalette                     = 17;
    public const int CharacterSkillGE                     = 1000;
    public const int CharacterMainPackEmptySlotsGE        = 1001;
    public const int CharacterLevelGE                     = 1002;
    public const int CharacterLevelLE                     = 1003;
    public const int CharacterBaseSkill                   = 1004;
    public const int BuffedMedianDamageGE                 = 2000;
    public const int BuffedMissileDamageGE                = 2001;
    public const int BuffedLongValKeyGE                   = 2003;
    public const int BuffedDoubleValKeyGE                 = 2005;
    public const int CalcdBuffedTinkedDamageGE            = 2006;
    public const int TotalRatingsGE                       = 2007;
    public const int CalcedBuffedTinkedTargetMeleeGE      = 2008;
    public const int DisabledRule                         = 9999;

    /// <summary>Number of data lines a given node type consumes; -1 for unknown.</summary>
    public static int GetDataLineCount(int type) => type switch
    {
        SpellNameMatch                       => 1,  // pattern
        StringValueMatch                     => 2,  // pattern, key
        LongValKeyLE                         => 2,  // value, key
        LongValKeyGE                         => 2,  // value, key
        DoubleValKeyLE                       => 2,  // value, key
        DoubleValKeyGE                       => 2,  // value, key
        DamagePercentGE                      => 1,  // value
        ObjectClass                          => 1,  // class id
        SpellCountGE                         => 1,  // count
        SpellMatch                           => 3,  // match_rx, no_match_rx, count
        MinDamageGE                          => 1,  // value
        LongValKeyFlagExists                 => 2,  // flag, key
        LongValKeyE                          => 2,  // value, key
        LongValKeyNE                         => 2,  // value, key
        AnySimilarColor                      => 5,  // r, g, b, maxDiffH, maxDiffSV
        SimilarColorArmorType                => 6,  // r, g, b, maxDiffH, maxDiffSV, armorGroup
        SlotSimilarColor                     => 6,  // r, g, b, maxDiffH, maxDiffSV, slot
        SlotExactPalette                     => 2,  // slot, palette
        CharacterSkillGE                     => 2,  // value, skillId
        CharacterMainPackEmptySlotsGE        => 1,  // count
        CharacterLevelGE                     => 1,  // value
        CharacterLevelLE                     => 1,  // value
        CharacterBaseSkill                   => 3,  // skillId, min, max
        BuffedMedianDamageGE                 => 1,  // value
        BuffedMissileDamageGE                => 1,  // value
        BuffedLongValKeyGE                   => 2,  // value, key
        BuffedDoubleValKeyGE                 => 2,  // value, key
        CalcdBuffedTinkedDamageGE            => 1,  // value
        TotalRatingsGE                       => 1,  // value
        CalcedBuffedTinkedTargetMeleeGE      => 3,  // dot, def, atk
        DisabledRule                         => 1,  // "true"/"false"
        _                                    => -1,
    };

    /// <summary>Human-readable name for a node type, suitable for UI labels.</summary>
    public static string DisplayName(int type) => type switch
    {
        SpellNameMatch                       => "Spell Name Match",
        StringValueMatch                     => "String Value Match",
        LongValKeyLE                         => "Long Key ≤",
        LongValKeyGE                         => "Long Key ≥",
        DoubleValKeyLE                       => "Double Key ≤",
        DoubleValKeyGE                       => "Double Key ≥",
        DamagePercentGE                      => "Damage % ≥",
        ObjectClass                          => "Object Class",
        SpellCountGE                         => "Spell Count ≥",
        SpellMatch                           => "Spell Match",
        MinDamageGE                          => "Min Damage ≥",
        LongValKeyFlagExists                 => "Long Key Has Flag",
        LongValKeyE                          => "Long Key =",
        LongValKeyNE                         => "Long Key ≠",
        AnySimilarColor                      => "Any Similar Color",
        SimilarColorArmorType                => "Similar Color (Armor Type)",
        SlotSimilarColor                     => "Slot Similar Color",
        SlotExactPalette                     => "Slot Exact Palette",
        CharacterSkillGE                     => "Char Skill ≥ (buffed)",
        CharacterMainPackEmptySlotsGE        => "Char Pack Slots ≥",
        CharacterLevelGE                     => "Char Level ≥",
        CharacterLevelLE                     => "Char Level ≤",
        CharacterBaseSkill                   => "Char Base Skill in [min,max]",
        BuffedMedianDamageGE                 => "Buffed Median Damage ≥",
        BuffedMissileDamageGE                => "Buffed Missile Damage ≥",
        BuffedLongValKeyGE                   => "Buffed Long Key ≥",
        BuffedDoubleValKeyGE                 => "Buffed Double Key ≥",
        CalcdBuffedTinkedDamageGE            => "Buffed Tinked Damage ≥",
        TotalRatingsGE                       => "Total Ratings ≥",
        CalcedBuffedTinkedTargetMeleeGE      => "Tinked Target Melee ≥",
        DisabledRule                         => "Disabled Rule",
        _                                    => $"Unknown ({type})",
    };

    /// <summary>Every known node type id, in canonical UI order.</summary>
    public static readonly int[] All =
    {
        ObjectClass,
        StringValueMatch,
        LongValKeyE, LongValKeyNE, LongValKeyGE, LongValKeyLE, LongValKeyFlagExists,
        DoubleValKeyGE, DoubleValKeyLE,
        MinDamageGE, DamagePercentGE,
        SpellNameMatch, SpellCountGE, SpellMatch,
        AnySimilarColor, SimilarColorArmorType, SlotSimilarColor, SlotExactPalette,
        CharacterSkillGE, CharacterBaseSkill, CharacterLevelGE, CharacterLevelLE,
        CharacterMainPackEmptySlotsGE,
        BuffedMedianDamageGE, BuffedMissileDamageGE, BuffedLongValKeyGE, BuffedDoubleValKeyGE,
        CalcdBuffedTinkedDamageGE, TotalRatingsGE, CalcedBuffedTinkedTargetMeleeGE,
        DisabledRule,
    };
}
