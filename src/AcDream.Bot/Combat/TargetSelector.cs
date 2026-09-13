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
        out PluginCombatTarget target)
    {
        ArgumentNullException.ThrowIfNull(hostiles);
        ArgumentNullException.ThrowIfNull(settings);
        target = default;
        int bestPriorityRank = int.MaxValue;
        float bestDistance = float.PositiveInfinity;
        bool found = false;

        foreach (PluginCombatTarget candidate in hostiles)
        {
            if (candidate.Distance > settings.EngageDistance)
                continue;
            if (IsIgnored(candidate.Name, settings.IgnoreNames))
                continue;
            if (candidate.IsHealthKnown && candidate.HealthFraction <= 0f)
                continue;

            int priorityRank = PriorityRank(candidate.Name, settings.PriorityNames);
            float distance = candidate.ObjectId == currentTargetId
                ? candidate.Distance * 0.5f
                : candidate.Distance;

            bool better = priorityRank < bestPriorityRank
                || (priorityRank == bestPriorityRank && distance < bestDistance);
            if (!better)
                continue;
            bestPriorityRank = priorityRank;
            bestDistance = distance;
            target = candidate;
            found = true;
        }
        return found;
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
