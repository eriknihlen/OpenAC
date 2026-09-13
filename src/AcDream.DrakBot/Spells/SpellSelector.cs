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
    Func<uint, bool>? isBlocked = null)
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
        TryBest(catalog.KnownSelfBuffs, baseName, out spell);

    /// <summary>Highest known, castable tier of any known spell family named by <paramref name="baseName"/>.</summary>
    public bool TryBestKnown(string baseName, out PluginSpellInfo spell)
    {
        if (TryBest(catalog.KnownSelfBuffs, baseName, out spell))
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
            if (!IsCastable(candidate.SpellId))
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

    private bool TryBest(
        IReadOnlyList<PluginSpellInfo> pool,
        string baseName,
        out PluginSpellInfo spell)
    {
        spell = default;
        string wanted = BaseName(baseName);
        int bestTier = int.MinValue;
        foreach (PluginSpellInfo candidate in pool)
        {
            if (!string.Equals(BaseName(candidate.Name), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsCastable(candidate.SpellId))
                continue;
            if (candidate.Tier > bestTier)
            {
                bestTier = candidate.Tier;
                spell = candidate;
            }
        }
        return bestTier != int.MinValue;
    }

    private bool IsCastable(uint spellId) =>
        magic.HasComponents(spellId) && (isBlocked is null || !isBlocked(spellId));
}
