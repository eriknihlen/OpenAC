namespace AcDream.Plugins.MossTank.Tests;

public sealed class MonsterExpressionTests
{
    [Theory]
    [InlineData("range>5", true)]
    [InlineData("range<=5", false)]
    [InlineData("range>5 && species==drudge", true)]
    [InlineData("range>5 && species==drudge && name!=drudge ravener", true)]
    [InlineData("hasshield && metastate==hunting", true)]
    [InlineData("typeid==1234", true)]
    [InlineData("maxhp>=450", true)]
    public void DocumentedVariablesAndOperatorsEvaluateVerbatim(
        string source,
        bool expected)
    {
        MonsterExpression expression = MonsterExpression.Compile(source);

        Assert.True(expression.TryEvaluate(
            Context(),
            out MonsterValue value,
            out string? error),
            error);
        Assert.Equal(MonsterValueKind.Boolean, value.Kind);
        Assert.Equal(expected, value.Boolean);
    }

    [Fact]
    public void BareTextMatchesTheMonsterNameCaseInsensitively()
    {
        MonsterExpression expression = MonsterExpression.Compile("Drudge Ravener");

        Assert.True(expression.IsMatch(Context(name: "drudge ravener"), out _));
        Assert.False(expression.IsMatch(Context(name: "Drudge Lurker"), out _));
    }

    [Fact]
    public void RegexUsesLeftAsInputAndRightAsPattern()
    {
        MonsterExpression expression = MonsterExpression.Compile(
            "name#^drudge .\\+er$");

        Assert.True(expression.IsMatch(Context(), out string? error), error);
    }

    [Fact]
    public void EscapedOperatorAndDigitRemainLiteralStringCharacters()
    {
        MonsterExpression expression = MonsterExpression.Compile(
            "name==Prototype \\#\\2");

        Assert.True(expression.IsMatch(Context(name: "Prototype #2"), out _));
        Assert.Throws<MonsterExpressionException>(() =>
            MonsterExpression.Compile("name==Prototype 2"));
    }

    [Fact]
    public void UsesVtankDocumentedNonstandardPrecedence()
    {
        MonsterExpression subtraction = MonsterExpression.Compile("10-3+1==6");
        MonsterExpression modulo = MonsterExpression.Compile("20/6%4==10");

        Assert.True(subtraction.IsMatch(Context(), out _));
        Assert.True(modulo.IsMatch(Context(), out _));
    }

    [Fact]
    public void BooleanOperatorsShortCircuitInvalidRightBranch()
    {
        MonsterExpression expression = MonsterExpression.Compile(
            "false && 1/0==0");

        Assert.True(expression.TryEvaluate(
            Context(),
            out MonsterValue value,
            out string? error),
            error);
        Assert.False(value.Boolean);
    }

    [Fact]
    public void SettingNamesAreCaseSensitiveAndMakeExpressionDynamic()
    {
        var settings = new Dictionary<string, MonsterValue>(StringComparer.Ordinal)
        {
            ["DoJiggle"] = MonsterValue.FromBoolean(true),
        };
        var context = Context(setting: name =>
            settings.TryGetValue(name, out MonsterValue value) ? value : null);
        MonsterExpression correct = MonsterExpression.Compile("setting_DoJiggle");
        MonsterExpression wrong = MonsterExpression.Compile("setting_dojiggle");

        Assert.True(correct.IsDynamic);
        Assert.True(correct.IsMatch(context, out _));
        Assert.False(wrong.IsMatch(context, out _));
    }

    [Fact]
    public void TypeMismatchIsAReportedNonMatchRatherThanAPluginCrash()
    {
        MonsterExpression expression = MonsterExpression.Compile("name==5");

        Assert.False(expression.IsMatch(Context(), out string? error));
        Assert.Contains("matching operand types", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolverChecksRowsInOrderAfterDefaultAndCarriesAllActions()
    {
        var fallback = new MonsterRule("DEFAULT", 0);
        var first = new MonsterRule(
            "species==drudge",
            new MonsterRuleActions
            {
                Priority = 3,
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                ExtraVulnerability = MonsterDamageType.Fire,
                WeaponObjectId = 0x70000001u,
                OffhandObjectId = 0x70000002u,
                PetDamageType = MonsterDamageType.Cold,
            });
        var later = new MonsterRule("range>1", 4);

        ResolvedMonsterRule resolved = MonsterRuleResolver.Resolve(
            [fallback, first, later],
            Context());

        Assert.Same(first, resolved.Rule);
        Assert.Equal(3, resolved.Priority);
        Assert.Equal(
            MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
            resolved.Actions.Flags);
        Assert.Equal(MonsterDamageType.Fire, resolved.Actions.DamageType);
        Assert.Equal(0x70000001u, resolved.Actions.WeaponObjectId);
        Assert.Equal(0x70000002u, resolved.Actions.OffhandObjectId);
        Assert.Equal(MonsterDamageType.Cold, resolved.Actions.PetDamageType);
    }

    [Fact]
    public void ResolverFallsBackToDefaultAndClampsPriority()
    {
        var fallback = new MonsterRule("DEFAULT", 99);

        ResolvedMonsterRule resolved = MonsterRuleResolver.Resolve(
            [fallback, new MonsterRule("species==olthoi", 4)],
            Context());

        Assert.Same(fallback, resolved.Rule);
        Assert.Equal(4, resolved.Priority);
    }

    private static MonsterExpressionContext Context(
        string name = "Drudge Lurker",
        Func<string, MonsterValue?>? setting = null) => new(
            name,
            TypeId: 1234u,
            Species: "Drudge",
            MaximumHealth: 450,
            Range: 8f,
            HasShield: true,
            MetaState: "Hunting",
            Setting: setting);
}
