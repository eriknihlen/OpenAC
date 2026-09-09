using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal readonly record struct CombatTargetCandidate(
    PluginCombatTarget Target,
    ResolvedMonsterRule Rule,
    int Priority,
    double Distance,
    double AngleDelta,
    int Urgency,
    bool NeedsDebuff,
    bool IsLockSelection,
    bool IsLastTarget,
    uint PlannedWeapon,
    uint PlannedOffhand)
{
    public uint ObjectId => Target.ObjectId;

    public static CombatTargetCandidate Empty { get; } = new(
        Target: default,
        Rule: default,
        Priority: -1,
        Distance: 999999d,
        AngleDelta: double.MaxValue,
        Urgency: 0,
        NeedsDebuff: false,
        IsLockSelection: false,
        IsLastTarget: false,
        PlannedWeapon: 0u,
        PlannedOffhand: 0u);
}

internal static class CombatTargetSelector
{
    public static CombatTargetCandidate Select(
        IReadOnlyList<CombatTargetCandidate> candidates,
        DebuffEachFirst debuffEachFirst,
        TargetSelectionMethod method,
        double targetSelectAngleRange,
        uint wieldedWeapon,
        uint wieldedOffhand)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // dz.cs:712-713
        bool flag = debuffEachFirst
            is DebuffEachFirst.All or DebuffEachFirst.Priority;
        bool flag2 = debuffEachFirst == DebuffEachFirst.All;

        double num4 = method == TargetSelectionMethod.Both
            ? targetSelectAngleRange
            : 0d;

        CombatTargetCandidate best = CombatTargetCandidate.Empty;
        foreach (CombatTargetCandidate candidate in candidates)
        {
            if (Accepts(
                    candidate,
                    best,
                    flag,
                    flag2,
                    method,
                    num4,
                    wieldedWeapon,
                    wieldedOffhand))
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Step 1 — priority, with <c>DebuffEachFirst == All</c>'s override
    /// (<c>dz.cs:740-766</c>).
    /// </summary>
    private static bool Accepts(
        in CombatTargetCandidate f10,
        in CombatTargetCandidate best,
        bool flag,
        bool flag2,
        TargetSelectionMethod num3,
        double num4,
        uint wieldedWeapon,
        uint wieldedOffhand)
    {
        if (!flag2)
        {
            // dz.cs:740-750
            if (f10.Priority <= best.Priority)
            {
                if (f10.Priority < best.Priority)
                    return false;
                return TieBreak(
                    f10, best, flag, num3, num4, wieldedWeapon, wieldedOffhand);
            }
            return true;
        }

        // dz.cs:751-765 — "All": needing a debuff outranks priority itself.
        if (!f10.NeedsDebuff || best.NeedsDebuff)
        {
            if (!f10.NeedsDebuff && best.NeedsDebuff)
                return false;
            if (f10.Priority <= best.Priority)
            {
                if (f10.Priority < best.Priority)
                    return false;
                return TieBreak(
                    f10, best, flag, num3, num4, wieldedWeapon, wieldedOffhand);
            }
            return true;
        }
        return true;
    }

    private static bool TieBreak(
        in CombatTargetCandidate f10,
        in CombatTargetCandidate best,
        bool flag,
        TargetSelectionMethod num3,
        double num4,
        uint wieldedWeapon,
        uint wieldedOffhand)
    {
        // Step 2 — dz.cs:771-781. DebuffEachFirst == One skips this entirely.
        if (flag)
        {
            if (f10.NeedsDebuff && !best.NeedsDebuff)
                return true;
            if (!f10.NeedsDebuff && best.NeedsDebuff)
                return false;
        }

        // Step 3 — dz.cs:782-787.
        if (f10.Urgency > best.Urgency)
            return true;
        if (f10.Urgency < best.Urgency)
            return false;

        // Step 4 — dz.cs:788-793.
        if (f10.IsLockSelection && !best.IsLockSelection)
            return true;
        if (!f10.IsLockSelection && best.IsLockSelection)
            return false;

        if (f10.Distance < num4 && best.Distance < num4)
        {
            int num5 = (f10.PlannedWeapon != wieldedWeapon ? 1 : 0)
                + (f10.PlannedOffhand != wieldedOffhand ? 1 : 0);
            int num6 = (best.PlannedWeapon != wieldedWeapon ? 1 : 0)
                + (best.PlannedOffhand != wieldedOffhand ? 1 : 0);
            if (num5 < num6)
                return true;
            if (num5 > num6)
                return false;
        }

        // Step 6 — dz.cs:825-830, the sticky previous target (ga.e).
        if (f10.IsLastTarget && !best.IsLastTarget)
            return true;
        if (!f10.IsLastTarget && best.IsLastTarget)
            return false;

        switch (num3)
        {
            case TargetSelectionMethod.Both:
                if (f10.Distance < num4 && best.Distance > num4)
                    return true;
                if (f10.Distance > num4 && best.Distance < num4)
                    return false;
                if (f10.Distance < num4)
                {
                    if (f10.AngleDelta < best.AngleDelta)
                        return true;
                    if (f10.AngleDelta > best.AngleDelta)
                        return false;
                    return f10.Distance < best.Distance;
                }
                if (f10.Distance < best.Distance)
                    return true;
                if (f10.Distance > best.Distance)
                    return false;
                return f10.AngleDelta < best.AngleDelta;

            case TargetSelectionMethod.Range:
                if (f10.Distance < best.Distance)
                    return true;
                if (f10.Distance > best.Distance)
                    return false;
                return f10.AngleDelta < best.AngleDelta;

            case TargetSelectionMethod.Angle:
                if (f10.AngleDelta < best.AngleDelta)
                    return true;
                if (f10.AngleDelta > best.AngleDelta)
                    return false;
                return f10.Distance < best.Distance;

            default: // dz.cs:913-914 — an unknown method keeps the incumbent.
                return false;
        }
    }
}
