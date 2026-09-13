using AcDream.Bot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Combat;

/// <summary>
/// Chooses which hostile to fight. Priority names win outright in list order;
/// otherwise a monster already being fought is preferred so the bot does not
/// flip between two targets at similar range; otherwise the nearest.
/// </summary>
public static class TargetSelector
{
    public static bool TrySelect(
        IReadOnlyList<PluginCombatTarget> hostiles,
        CombatSettings settings,
        uint currentTargetId,
        out PluginCombatTarget target,
        Func<uint, bool>? exclude = null)
    {
        IReadOnlyList<PluginCombatTarget> ranked = Rank(hostiles, settings, currentTargetId, exclude);
        target = ranked.Count == 0 ? default : ranked[0];
        return ranked.Count != 0;
    }

    /// <summary>
    /// Every eligible hostile, best first, so a caller that finds the best
    /// one unusable (no line of sight, say) can fall through to the next.
    /// </summary>
    public static IReadOnlyList<PluginCombatTarget> Rank(
        IReadOnlyList<PluginCombatTarget> hostiles,
        CombatSettings settings,
        uint currentTargetId,
        Func<uint, bool>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(hostiles);
        ArgumentNullException.ThrowIfNull(settings);
        var ranked = new List<(int Rank, float Distance, int Order, PluginCombatTarget Target)>();
        foreach (PluginCombatTarget candidate in hostiles)
        {
            if (candidate.Distance > settings.EngageDistance)
                continue;
            if (IsIgnored(candidate.Name, settings.IgnoreNames))
                continue;
            if (candidate.IsHealthKnown && candidate.HealthFraction <= 0f)
                continue;
            if (exclude is not null && exclude(candidate.ObjectId))
                continue;

            int priorityRank = PriorityRank(candidate.Name, settings.PriorityNames);
            float distance = candidate.ObjectId == currentTargetId
                ? candidate.Distance * 0.5f
                : candidate.Distance;
            ranked.Add((priorityRank, distance, ranked.Count, candidate));
        }
        ranked.Sort(static (left, right) =>
        {
            int order = left.Rank.CompareTo(right.Rank);
            if (order == 0)
                order = left.Distance.CompareTo(right.Distance);
            // Sort is not stable; keep host order for exact ties.
            return order != 0 ? order : left.Order.CompareTo(right.Order);
        });
        var result = new PluginCombatTarget[ranked.Count];
        for (int index = 0; index < ranked.Count; index++)
            result[index] = ranked[index].Target;
        return result;
    }

    private static bool IsIgnored(string name, IReadOnlyList<string> ignoreNames)
    {
        foreach (string ignored in ignoreNames)
        {
            if (name.Contains(ignored, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int PriorityRank(string name, IReadOnlyList<string> priorityNames)
    {
        for (int index = 0; index < priorityNames.Count; index++)
        {
            if (name.Contains(priorityNames[index], StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return int.MaxValue;
    }
}
