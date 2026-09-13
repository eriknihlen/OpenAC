using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Loot;

public enum LootAction
{
    Ignore = 0,
    Keep,
    /// <summary>Picked up and handed to the salvage behavior.</summary>
    Salvage,
}

/// <summary>
/// One rule: every populated criterion must match. Rules are tried in order
/// and the first match decides; nothing matching means <see cref="LootAction.Ignore"/>.
/// </summary>
public sealed record LootRule
{
    public string Name { get; init; } = string.Empty;

    public LootAction Action { get; init; } = LootAction.Keep;

    /// <summary>Case-insensitive substring of the item name.</summary>
    public string? NameContains { get; init; }

    public PluginObjectClass? ObjectClass { get; init; }

    /// <summary>Minimum vendor value; needs appraisal.</summary>
    public int? MinValue { get; init; }

    /// <summary>Minimum workmanship; needs appraisal.</summary>
    public float? MinWorkmanship { get; init; }

    /// <summary>Material type ids accepted; needs appraisal.</summary>
    public IReadOnlyList<uint>? Materials { get; init; }

    /// <summary>Whether any criterion can only be judged after the item is appraised.</summary>
    public bool NeedsAppraisal =>
        MinValue.HasValue || MinWorkmanship.HasValue || Materials is { Count: > 0 };

    public LootMatch Evaluate(in PluginInventoryItem item, bool isAppraised)
    {
        if (NameContains is { Length: > 0 }
            && !item.Name.Contains(NameContains, StringComparison.OrdinalIgnoreCase))
        {
            return LootMatch.NoMatch;
        }
        if (ObjectClass.HasValue && item.ObjectClass != ObjectClass.Value)
            return LootMatch.NoMatch;

        if (!NeedsAppraisal)
            return LootMatch.Match;
        if (!isAppraised)
            return LootMatch.NeedsAppraisal;

        if (MinValue.HasValue && item.Value < MinValue.Value)
            return LootMatch.NoMatch;
        if (MinWorkmanship.HasValue && item.Workmanship < MinWorkmanship.Value)
            return LootMatch.NoMatch;
        if (Materials is { Count: > 0 } && !Materials.Contains(item.MaterialType))
            return LootMatch.NoMatch;
        return LootMatch.Match;
    }
}

public enum LootMatch
{
    NoMatch = 0,
    Match,
    NeedsAppraisal,
}

public readonly record struct LootDecision(LootAction Action, string RuleName)
{
    public static LootDecision Ignore { get; } = new(LootAction.Ignore, string.Empty);

    public static LootDecision Appraise { get; } = new(LootAction.Ignore, "<appraise>");

    public bool RequiresAppraisal => RuleName == "<appraise>";
}

public sealed record LootRuleSet
{
    public static LootRuleSet Default { get; } = new()
    {
        Rules =
        [
            new LootRule { Name = "coins", ObjectClass = PluginObjectClass.Money },
            new LootRule { Name = "trade notes", ObjectClass = PluginObjectClass.TradeNote },
            new LootRule { Name = "keys", ObjectClass = PluginObjectClass.Key },
            new LootRule { Name = "mana stones", ObjectClass = PluginObjectClass.ManaStone },
            new LootRule { Name = "gems", ObjectClass = PluginObjectClass.Gem, MinValue = 1000 },
            new LootRule { Name = "valuable jewelry", ObjectClass = PluginObjectClass.Jewelry, MinValue = 5000 },
        ],
    };

    public IReadOnlyList<LootRule> Rules { get; init; } = [];

    /// <summary>
    /// First matching rule wins. If an earlier rule cannot be judged without
    /// appraisal and no earlier rule already matched, the answer is "appraise
    /// first"; the caller re-evaluates once the item is identified.
    /// </summary>
    public LootDecision Decide(in PluginInventoryItem item, bool isAppraised)
    {
        foreach (LootRule rule in Rules)
        {
            switch (rule.Evaluate(item, isAppraised))
            {
                case LootMatch.Match:
                    return new LootDecision(rule.Action, rule.Name);
                case LootMatch.NeedsAppraisal:
                    return LootDecision.Appraise;
            }
        }
        return LootDecision.Ignore;
    }
}
