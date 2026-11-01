using System;
using System.Collections.Generic;

namespace AcDream.Core.Spells;

public static class EnchantmentRegistryProjection
{
    public static IReadOnlyList<ActiveEnchantmentRecord> GetEnchantmentsInEffect(
        IEnumerable<ActiveEnchantmentRecord> enchantments)
    {
        ArgumentNullException.ThrowIfNull(enchantments);
        ActiveEnchantmentRecord[] source = enchantments as ActiveEnchantmentRecord[]
            ?? [.. enchantments];
        var result = new List<ActiveEnchantmentRecord>();
        DuelList(source, bucket: 1u, result);
        DuelList(source, bucket: 2u, result);
        return result;
    }

    private static void DuelList(
        IReadOnlyList<ActiveEnchantmentRecord> source,
        uint bucket,
        List<ActiveEnchantmentRecord> result)
    {
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            ActiveEnchantmentRecord candidate = source[sourceIndex];
            if (candidate.Bucket != bucket) continue;
            Duel(candidate, result);
        }
    }

    /// <summary>
    /// Enters <paramref name="candidate"/> into <paramref name="inEffect"/>.
    /// When the first record there of the candidate's spell category beats
    /// it, nothing changes; otherwise that record is taken out (unless its
    /// identity is zero) and the candidate is appended.
    /// </summary>
    internal static void Duel(
        ActiveEnchantmentRecord candidate,
        List<ActiveEnchantmentRecord> inEffect)
    {
        int incumbentIndex = inEffect.FindIndex(existing =>
            existing.SpellCategory == candidate.SpellCategory);
        if (incumbentIndex >= 0)
        {
            ActiveEnchantmentRecord incumbent = inEffect[incumbentIndex];
            if (!CandidateWins(incumbent, candidate))
                return;
            if (incumbent.Identity != 0u)
                inEffect.RemoveAt(incumbentIndex);
        }
        inEffect.Add(candidate);
    }

    /// <summary>
    /// The higher power level wins (compared as signed values); at equal
    /// power the candidate wins unless it started strictly earlier. Lists are
    /// walked from the head, where new records go, so an exact tie goes to
    /// the record that arrived first.
    /// </summary>
    private static bool CandidateWins(
        ActiveEnchantmentRecord incumbent,
        ActiveEnchantmentRecord candidate)
    {
        int incumbentPower = unchecked((int)incumbent.PowerLevel);
        int candidatePower = unchecked((int)candidate.PowerLevel);
        return candidatePower > incumbentPower
            || (candidatePower == incumbentPower
                && candidate.StartTime >= incumbent.StartTime);
    }
}
