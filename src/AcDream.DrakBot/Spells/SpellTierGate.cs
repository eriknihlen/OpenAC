using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Spells;

/// <summary>
/// Caps the spell tier by the character's skill in the spell's school, the
/// way RynthAi's spell manager does: a tier is allowed once the buffed
/// skill reaches its minimum, with one ladder for combat casts and a
/// gentler one for buffs, since a fizzled buff costs little and a
/// well-buffed caster should still reach the top tiers. A spell whose
/// school is unknown, or a character whose skill cannot be read, is not
/// gated.
/// </summary>
public sealed class SpellTierGate(ICharacterInfo character, Func<SpellTierSettings> settings)
{
    public bool AllowsBuff(in PluginSpellInfo spell) => Allows(spell, settings().BuffMinimums);

    public bool AllowsCombat(in PluginSpellInfo spell) => Allows(spell, settings().CombatMinimums);

    /// <summary>The highest tier the ladder allows at this skill level.</summary>
    public static int MaxTier(uint skill, IReadOnlyList<int> minimums)
    {
        int tier = 1;
        for (int index = 0; index < minimums.Count && index < 8; index++)
        {
            if (skill >= minimums[index])
                tier = index + 1;
        }
        return tier;
    }

    private bool Allows(in PluginSpellInfo spell, IReadOnlyList<int> minimums)
    {
        if (spell.School == 0u || spell.Tier <= 1 || minimums.Count == 0)
            return true;
        if (!character.TryGetSkill(spell.School, out PluginSkillInfo skill))
            return true;
        return spell.Tier <= MaxTier(skill.Current, minimums);
    }
}
