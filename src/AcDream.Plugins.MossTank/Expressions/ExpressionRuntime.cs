namespace AcDream.Plugins.MossTank.Expressions;

internal enum ExpressionVariableScope
{
    Session,
    Persistent,
    Global,
}

internal sealed class ExpressionState
{
    private readonly Dictionary<string, ExpressionValue> _session =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExpressionValue> _persistent =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExpressionValue> _global =
        new(StringComparer.OrdinalIgnoreCase);

    public ExpressionValue Get(ExpressionVariableScope scope, string name) =>
        Table(scope).TryGetValue(name, out ExpressionValue value)
            ? value
            : ExpressionValue.Zero;

    public bool Contains(ExpressionVariableScope scope, string name) =>
        Table(scope).ContainsKey(name);

    public ExpressionValue Set(
        ExpressionVariableScope scope,
        string name,
        ExpressionValue value)
    {
        Table(scope)[name] = value;
        return value;
    }

    public bool Clear(ExpressionVariableScope scope, string name) =>
        Table(scope).Remove(name);

    public void Clear(ExpressionVariableScope scope) => Table(scope).Clear();

    public IReadOnlyDictionary<string, ExpressionValue> Capture(
        ExpressionVariableScope scope) =>
        new Dictionary<string, ExpressionValue>(Table(scope),
            StringComparer.OrdinalIgnoreCase);

    public void Replace(
        ExpressionVariableScope scope,
        IEnumerable<KeyValuePair<string, ExpressionValue>> values)
    {
        Dictionary<string, ExpressionValue> target = Table(scope);
        target.Clear();
        foreach ((string name, ExpressionValue value) in values)
            target[name] = value;
    }

    private Dictionary<string, ExpressionValue> Table(
        ExpressionVariableScope scope) => scope switch
    {
        ExpressionVariableScope.Session => _session,
        ExpressionVariableScope.Persistent => _persistent,
        ExpressionVariableScope.Global => _global,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

internal delegate ExpressionValue ExpressionFunctionHandler(
    ExpressionEvaluationContext context,
    IReadOnlyList<ExpressionValue> arguments);

internal sealed record ExpressionFunction(
    string Name,
    int MinimumArguments,
    int MaximumArguments,
    ExpressionFunctionHandler Handler,
    string Signature,
    string Description);

internal sealed class ExpressionFunctionRegistry
{
    private readonly Dictionary<string, ExpressionFunction> _functions =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ExpressionFunction> Functions =>
        _functions.Values;

    public void Register(
        string name,
        int minimumArguments,
        int maximumArguments,
        ExpressionFunctionHandler handler,
        string? signature = null,
        string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (minimumArguments < 0 || maximumArguments < minimumArguments)
            throw new ArgumentOutOfRangeException(nameof(minimumArguments));
        var function = new ExpressionFunction(
            name,
            minimumArguments,
            maximumArguments,
            handler,
            signature ?? name + "[...]",
            description);
        if (!_functions.TryAdd(name, function))
        {
            throw new InvalidOperationException(
                $"Expression function '{name}' is already registered.");
        }
    }

    public void Alias(string alias, string existing)
    {
        if (!_functions.TryGetValue(existing, out ExpressionFunction? function))
            throw new InvalidOperationException(
                $"Expression function '{existing}' is not registered.");
        Register(
            alias,
            function.MinimumArguments,
            function.MaximumArguments,
            function.Handler,
            function.Signature.Replace(existing, alias, StringComparison.Ordinal),
            function.Description);
    }

    public ExpressionFunction Resolve(string name, int offset)
    {
        if (_functions.TryGetValue(name, out ExpressionFunction? function))
            return function;
        throw new ExpressionEvaluationException(
            $"Unknown expression method: {name}",
            offset);
    }
}

internal sealed class ExpressionEvaluationContext
{
    private int _remainingInstructions;

    public ExpressionEvaluationContext(
        ExpressionState state,
        ExpressionFunctionRegistry functions,
        int instructionBudget = 10_000,
        CancellationToken cancellationToken = default)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Functions = functions ?? throw new ArgumentNullException(nameof(functions));
        if (instructionBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(instructionBudget));
        _remainingInstructions = instructionBudget;
        CancellationToken = cancellationToken;
    }

    public ExpressionState State { get; }
    public ExpressionFunctionRegistry Functions { get; }
    public CancellationToken CancellationToken { get; }
    public int RemainingInstructions => _remainingInstructions;

    public void Step(int offset)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (--_remainingInstructions < 0)
        {
            throw new ExpressionEvaluationException(
                "Expression instruction budget exceeded",
                offset);
        }
    }

    public ExpressionValue Invoke(
        string name,
        IReadOnlyList<ExpressionValue> arguments,
        int offset)
    {
        Step(offset);
        ExpressionFunction function = Functions.Resolve(name, offset);
        if (arguments.Count < function.MinimumArguments
            || arguments.Count > function.MaximumArguments)
        {
            string expected = function.MinimumArguments == function.MaximumArguments
                ? function.MinimumArguments.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                : $"{function.MinimumArguments}..{function.MaximumArguments}";
            throw new ExpressionEvaluationException(
                $"{function.Signature} expects {expected} arguments; "
                + $"{arguments.Count} were passed",
                offset);
        }
        try
        {
            return function.Handler(this, arguments);
        }
        catch (ExpressionEvaluationException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ExpressionEvaluationException(
                $"{function.Signature} failed: {error.Message}",
                offset);
        }
    }
}
