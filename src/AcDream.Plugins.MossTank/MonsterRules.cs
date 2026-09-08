namespace AcDream.Plugins.MossTank;

[Flags]
internal enum MonsterActionFlags
{
    None = 0,
    Fester = 1 << 0,
    Broadside = 1 << 1,
    GravityWell = 1 << 2,
    Imperil = 1 << 3,
    Yield = 1 << 4,
    Vulnerability = 1 << 5,
    Attack = 1 << 6,
    Ring = 1 << 7,
    Streak = 1 << 8,
    WeakeningCurse = 1 << 9,
    FesteringCurse = 1 << 10,
    Corruption = 1 << 11,
    DestructiveCurse = 1 << 12,
    Corrosion = 1 << 13,
}

internal enum MonsterDamageType
{
    Auto = 0,
    Slash,
    Pierce,
    Bludgeon,
    Cold,
    Fire,
    Acid,
    Electric,
    Nether,
    VoidBasic,
    DrainAuto,
    Harm,
    None,
    PlayerAuto,
    Prismatic,
    Random,
    Fists,
    Physical,
}

internal sealed record MonsterRuleActions
{
    public MonsterActionFlags Flags { get; init; } = MonsterActionFlags.Attack;
    public int Priority { get; init; }
    public MonsterDamageType DamageType { get; init; } = MonsterDamageType.Auto;
    public MonsterDamageType ExtraVulnerability { get; init; } =
        MonsterDamageType.Auto;
    public uint WeaponObjectId { get; init; }
    public uint OffhandObjectId { get; init; }
    public string WeaponName { get; init; } = string.Empty;
    public string OffhandName { get; init; } = string.Empty;
    public MonsterDamageType PetDamageType { get; init; } =
        MonsterDamageType.PlayerAuto;

    public int BoundedPriority => Math.Clamp(Priority, -1, 4);
    public bool Attacks => (Flags
        & (MonsterActionFlags.Attack | MonsterActionFlags.Ring)) != 0;
    public bool UsesPrimaryAttack => (Flags & MonsterActionFlags.Attack) != 0;
    public bool UsesRing => (Flags & MonsterActionFlags.Ring) != 0;
    public bool UsesStreak => (Flags & MonsterActionFlags.Streak) != 0;
}

internal sealed class MonsterRule
{
    private readonly MonsterExpression? _compiled;

    public MonsterRule(string expression, int priority)
        : this(expression, new MonsterRuleActions { Priority = priority })
    {
    }

    public MonsterRule(string expression, MonsterRuleActions actions)
    {
        Expression = string.IsNullOrWhiteSpace(expression)
            ? "DEFAULT"
            : expression.Trim();
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
        if (!IsDefault)
            _compiled = MonsterExpression.Compile(Expression);
    }

    public string Expression { get; }
    public MonsterRuleActions Actions { get; }
    public int Priority => Actions.BoundedPriority;
    public bool IsDefault => Expression.Equals(
        "DEFAULT",
        StringComparison.OrdinalIgnoreCase);
    public bool IsDynamic => _compiled?.IsDynamic == true;

    public bool Matches(
        in MonsterExpressionContext context,
        out string? error)
    {
        if (_compiled is null)
        {
            error = null;
            return IsDefault;
        }
        return _compiled.IsMatch(context, out error);
    }
}

internal readonly record struct ResolvedMonsterRule(
    MonsterRule Rule,
    string? EvaluationError)
{
    public MonsterRuleActions Actions => Rule.Actions;
    public int Priority => Rule.Priority;
}

internal static class MonsterRuleResolver
{
    internal static ResolvedMonsterRule Resolve(
        IEnumerable<MonsterRule> rules,
        in MonsterExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(rules);
        MonsterRule? fallback = null;
        string? firstError = null;
        foreach (MonsterRule rule in rules)
        {
            if (rule.IsDefault)
            {
                fallback ??= rule;
                continue;
            }
            if (rule.Matches(context, out string? error))
                return new ResolvedMonsterRule(rule, firstError);
            firstError ??= error;
        }

        fallback ??= new MonsterRule("DEFAULT", 0);
        return new ResolvedMonsterRule(fallback, firstError);
    }
}
