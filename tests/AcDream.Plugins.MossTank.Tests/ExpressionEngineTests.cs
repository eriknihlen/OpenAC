using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class ExpressionEngineTests
{
    [Theory]
    [InlineData("1+2*3", 7d)]
    [InlineData("(1+2)*3", 9d)]
    [InlineData("2^3^2", 512d)]
    [InlineData("0x10+1", 17d)]
    [InlineData("~1", -2d)]
    [InlineData("16>>2", 4d)]
    public void ArithmeticUsesUtilityBeltPrecedence(string source, double expected)
    {
        Assert.Equal(expected, Evaluate(source).AsNumber(), 10);
    }

    [Fact]
    public void StatementsAndAllVariableScopesReturnLastValue()
    {
        var state = new ExpressionState();

        ExpressionValue result = Evaluate(
            "$session=2;@persistent=3;&global=4;"
            + "setvar[dynamic,5];$session+@persistent+&global+$dynamic",
            state);

        Assert.Equal(14d, result.AsNumber());
        Assert.Equal(2d, state.Get(ExpressionVariableScope.Session, "session").AsNumber());
        Assert.Equal(3d, state.Get(ExpressionVariableScope.Persistent, "persistent").AsNumber());
        Assert.Equal(4d, state.Get(ExpressionVariableScope.Global, "global").AsNumber());
    }

    [Fact]
    public void NumericAndComputedVariableNamesAreSupported()
    {
        var state = new ExpressionState();

        ExpressionValue result = Evaluate(
            "$1=7;$name=`chosen`;$getvar[name]=9;$1+$chosen",
            state);

        Assert.Equal(16d, result.AsNumber());
    }

    [Fact]
    public void BooleanOperatorsShortCircuitAndReturnUtilityBeltValues()
    {
        ExpressionFunctionRegistry functions = CoreExpressionFunctions.CreateDefault();
        int calls = 0;
        functions.Register("boom", 0, 0, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("must not run");
        });
        var context = new ExpressionEvaluationContext(new ExpressionState(), functions);

        Assert.Equal(0d, ExpressionProgram.Compile("0&&boom[]").Evaluate(context).AsNumber());
        Assert.Equal(3d, ExpressionProgram.Compile("3||boom[]").Evaluate(context).AsNumber());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void StringsAreCaseInsensitiveAndPreserveDashedBareText()
    {
        Assert.True(Evaluate("`Olthoi`==`olthoi`").IsTruthy);
        Assert.Equal("Olthoi-Noble", Evaluate("Olthoi-Noble").AsString());
        Assert.Equal("Olthoi Noble", Evaluate("`Olthoi `+Noble").AsString());
    }

    [Fact]
    public void RegexOperatorPublishesCaptureGroups()
    {
        var state = new ExpressionState();

        ExpressionValue result = Evaluate(
            "`Olthoi 275`#`(?<level>[0-9]+)`;$capturegroup_level",
            state);

        Assert.Equal("275", result.AsString());
    }

    [Fact]
    public void ListsSupportMutationIndexSlicesAndHigherOrderFunctions()
    {
        var state = new ExpressionState();
        Evaluate("$0=old0;$1=old1;$2=old2", state);

        Assert.Equal(4d, Evaluate(
            "$items=listcreate[1,2,3];listadd[$items,4];listcount[$items]",
            state).AsNumber());
        Assert.Equal(4d, Evaluate("$items{-1}", state).AsNumber());
        Assert.Equal("[2,3]", Evaluate("$items{1:3}", state).ToDisplayString());
        Assert.Equal("[2,4,6,8]", Evaluate(
            "listmap[$items,`$1*2`]", state).ToDisplayString());
        Assert.Equal("[1,3]", Evaluate(
            "listfilter[$items,`$1%2==1`]", state).ToDisplayString());
        Assert.Equal(10d, Evaluate(
            "listreduce[$items,`$2+$1`]", state).AsNumber());
        Assert.Equal("[4,3,2,1]", Evaluate(
            "listsort[$items,`$2-$1`]", state).ToDisplayString());
        Assert.Equal("old0", state.Get(ExpressionVariableScope.Session, "0").AsString());
        Assert.Equal("old1", state.Get(ExpressionVariableScope.Session, "1").AsString());
        Assert.Equal("old2", state.Get(ExpressionVariableScope.Session, "2").AsString());
    }

    [Fact]
    public void ListRangeSupportsBothDirections()
    {
        Assert.Equal("[1,2,3]", Evaluate("listfromrange[1,3]").ToDisplayString());
        Assert.Equal("[3,2,1]", Evaluate("listfromrange[3,1]").ToDisplayString());
    }

    [Fact]
    public void DictionariesSupportMutationAndShallowCopy()
    {
        var state = new ExpressionState();

        Assert.Equal(2d, Evaluate(
            "$dict=dictcreate[a,1,b,2];$dict{b}", state).AsNumber());
        Assert.False(Evaluate("dictadditem[$dict,c,3]", state).IsTruthy);
        Assert.True(Evaluate("dictadditem[$dict,c,4]", state).IsTruthy);
        Assert.Equal(3d, Evaluate("dictsize[$dict]", state).AsNumber());
        Assert.Equal("[a,b,c]", Evaluate("dictkeys[$dict]", state).ToDisplayString());
        Assert.Equal(4d, Evaluate("dictgetitem[dictcopy[$dict],c]", state).AsNumber());
        Assert.True(Evaluate("dictremovekey[$dict,b]", state).IsTruthy);
    }

    [Fact]
    public void CollectionsRejectDirectAndIndirectCycles()
    {
        var state = new ExpressionState();
        Evaluate("$first=listcreate[];$second=listcreate[$first]", state);

        ExpressionEvaluationException direct = Assert.Throws<ExpressionEvaluationException>(
            () => Evaluate("listadd[$first,$first]", state));
        ExpressionEvaluationException indirect = Assert.Throws<ExpressionEvaluationException>(
            () => Evaluate("listadd[$first,$second]", state));

        Assert.Contains("cyclic", direct.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cyclic", indirect.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoordinatesUseAcCompassScaleAndMeters()
    {
        Assert.Equal(-12.5d, Evaluate(
            "coordinategetns[coordinateparse[`12.5S, 3.0E`]]").AsNumber());
        Assert.Equal(24d, Evaluate(
            "coordinatedistanceflat[coordinateparse[`0N, 0E`],"
            + "coordinateparse[`0.1N, 0E`]]").AsNumber(), 8);
        Assert.Equal("12.5S, 3.0E", Evaluate(
            "coordinatetostring[coordinateparse[`12.5S, 3.0E`]]").AsString());
    }

    [Fact]
    public void InstructionBudgetAndCancellationBoundNestedEvaluation()
    {
        var functions = CoreExpressionFunctions.CreateDefault();
        var budgeted = new ExpressionEvaluationContext(
            new ExpressionState(), functions, instructionBudget: 25);
        Assert.Throws<ExpressionEvaluationException>(() =>
            ExpressionProgram.Compile(
                "listmap[listfromrange[1,100],`$1*2`]").Evaluate(budgeted));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new ExpressionEvaluationContext(
            new ExpressionState(), functions, cancellationToken: cancellation.Token);
        Assert.Throws<OperationCanceledException>(() =>
            ExpressionProgram.Compile("1+1").Evaluate(cancelled));
    }

    [Fact]
    public void FunctionErrorsNameTheSignatureAndOffset()
    {
        ExpressionEvaluationException error = Assert.Throws<ExpressionEvaluationException>(
            () => Evaluate("sqrt[1,2]"));

        Assert.Contains("sqrt[number]", error.Message, StringComparison.Ordinal);
        Assert.Contains("offset 0", error.Message, StringComparison.Ordinal);
    }

    private static ExpressionValue Evaluate(
        string source,
        ExpressionState? state = null)
    {
        var context = new ExpressionEvaluationContext(
            state ?? new ExpressionState(),
            CoreExpressionFunctions.CreateDefault(new Random(1234)));
        return ExpressionProgram.Compile(source).Evaluate(context);
    }
}
