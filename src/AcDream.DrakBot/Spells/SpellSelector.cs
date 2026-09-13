using AcDream.DrakBot.Combat;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Spells;

/// <summary>
/// Picks spells from what the character actually knows. Users name spells the
/// way the game does ("Heal Self", "Strength Self"); the tier suffix is
/// dropped so the selector can find the family and then take the highest
/// castable tier of it. <paramref name="isBlocked"/> lets the caller veto a
/// spell (for example one that is backing off after a failure) so the next
/// best tier is chosen instead.
/// </summary>
public sealed class SpellSelector(
    ISpellCatalog catalog,
    IMagicCommands magic,
    Func<uint, bool>? isBlocked = null,
    SpellTierGate? tiers = null)
{
    private static readonly string[] TierSuffixes =
    [
        " VIII", " VII", " VI", " IV", " V", " III", " II", " I",
    ];

    public static string BaseName(string spellName)
    {
        ArgumentNullException.ThrowIfNull(spellName);
        string trimmed = spellName.Trim();
        foreach (string suffix in TierSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^suffix.Length].TrimEnd();
        }
        return trimmed;
    }

    /// <summary>Highest known, castable tier of the self-buff family named by <paramref name="baseName"/>.</summary>
    public bool TryBestSelfBuff(string baseName, out PluginSpellInfo spell) =>
        TryBest(catalog.KnownSelfBuffs, baseName, out spell, buff: true);

    /// <summary>Highest known, castable tier of any known spell family named by <paramref name="baseName"/>.</summary>
    public bool TryBestKnown(string baseName, out PluginSpellInfo spell)
    {
        if (TryBest(catalog.KnownSelfBuffs, baseName, out spell, buff: true))
            return true;
        if (TryBest(catalog.KnownCombatSpells, baseName, out spell))
            return true;
        return TryBest(catalog.KnownAttackSpells, baseName, out spell);
    }

    /// <summary>
    /// Best direct attack the character can cast now, optionally restricted to
    /// one damage element by the spell's own name ("Flame", "Frost", ...).
    /// </summary>
    public bool TryBestAttack(string? elementKeyword, out PluginSpellInfo spell)
    {
        spell = default;
        int bestRank = int.MinValue;
        foreach (PluginSpellInfo candidate in catalog.KnownAttackSpells)
        {
            if (!string.IsNullOrWhiteSpace(elementKeyword)
                && !candidate.Name.Contains(elementKeyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!IsCastable(candidate, buff: false))
                continue;
            int rank = candidate.Tier * 1000 + candidate.Quality;
            if (rank > bestRank)
            {
                bestRank = rank;
                spell = candidate;
            }
        }
        return bestRank != int.MinValue;
    }

    /// <summary>
    /// The best castable war (or void) spell of an element and shape. The
    /// family is found by the name of any tier of it, so the lore-named top
    /// tiers ("Outlander's Insolence" for Force Streak VII) are reached
    /// through the family rather than by name.
    /// </summary>
    public bool TryBestOffensive(string element, SpellShape shape, bool ring, out PluginSpellInfo spell)
    {
        foreach (string family in WarSpellNames.Offensive(element, shape, ring))
        {
            if (TryBestInFamily(catalog.KnownAttackSpells, family, out spell)
                || TryBestInFamily(catalog.KnownCombatSpells, family, out spell))
            {
                return true;
            }
        }
        spell = default;
        return false;
    }

    /// <summary>The best castable tier of a creature debuff for a monster rule.</summary>
    public bool TryBestDebuff(DebuffKind kind, string element, out PluginSpellInfo spell)
    {
        string? family = WarSpellNames.Debuff(kind, element);
        if (family is null)
        {
            spell = default;
            return false;
        }
        return TryBestInFamily(catalog.KnownCombatSpells, family, out spell)
            || TryBestInFamily(catalog.KnownAttackSpells, family, out spell);
    }

    /// <summary>
    /// Highest castable tier in the family of <paramref name="baseName"/>:
    /// any known tier with that base name identifies the family, and the
    /// family's other tiers count whatever they are called.
    /// </summary>
    private bool TryBestInFamily(
        IReadOnlyList<PluginSpellInfo> pool,
        string baseName,
        out PluginSpellInfo spell)
    {
        spell = default;
        string wanted = BaseName(baseName);
        uint family = 0u;
        foreach (PluginSpellInfo candidate in pool)
        {
            if (string.Equals(BaseName(candidate.Name), wanted, StringComparison.OrdinalIgnoreCase))
            {
                family = candidate.Family;
                break;
            }
        }
        if (family == 0u)
            return TryBest(pool, baseName, out spell);

        int bestTier = int.MinValue;
        foreach (PluginSpellInfo candidate in pool)
        {
            if (candidate.Family != family || !IsCastable(candidate, buff: false))
                continue;
            if (candidate.Tier > bestTier)
            {
                bestTier = candidate.Tier;
                spell = candidate;
            }
        }
        return bestTier != int.MinValue;
    }

    private bool TryBest(
        IReadOnlyList<PluginSpellInfo> pool,
        string baseName,
        out PluginSpellInfo spell,
        bool buff = false)
    {
        spell = default;
        string wanted = BaseName(baseName);
        int bestTier = int.MinValue;
        foreach (PluginSpellInfo candidate in pool)
        {
            if (!string.Equals(BaseName(candidate.Name), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsCastable(candidate, buff))
                continue;
            if (candidate.Tier > bestTier)
            {
                bestTier = candidate.Tier;
                spell = candidate;
            }
        }
        return bestTier != int.MinValue;
    }

    private bool IsCastable(in PluginSpellInfo spell, bool buff)
    {
        if (!magic.HasComponents(spell.SpellId) || (isBlocked is not null && isBlocked(spell.SpellId)))
            return false;
        if (tiers is null)
            return true;
        return buff ? tiers.AllowsBuff(spell) : tiers.AllowsCombat(spell);
    }
}
