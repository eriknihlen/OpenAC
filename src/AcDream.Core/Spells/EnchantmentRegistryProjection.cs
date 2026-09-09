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

            int incumbentIndex = result.FindIndex(existing =>
                existing.SpellCategory == candidate.SpellCategory);
            if (incumbentIndex >= 0)
            {
                ActiveEnchantmentRecord incumbent = result[incumbentIndex];
                if (!CandidateWins(incumbent, candidate))
                    continue;
                result.RemoveAt(incumbentIndex);
            }
            result.Add(candidate);
        }
    }

    private static bool CandidateWins(
        ActiveEnchantmentRecord incumbent,
        ActiveEnchantmentRecord candidate)
    {
        int incumbentPower = unchecked((int)incumbent.PowerLevel);
        int candidatePower = unchecked((int)candidate.PowerLevel);
        return candidatePower > incumbentPower
            || (candidatePower == incumbentPower
                && candidate.StartTime > incumbent.StartTime);
    }
}
