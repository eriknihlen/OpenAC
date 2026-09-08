using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Expressions;

internal sealed class MossTankExpressionRuntime : IDisposable
{
    private const int DefaultInstructionBudget = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginHost _host;
    private readonly ExpressionState _state = new();
    private readonly ExpressionFunctionRegistry _functions;
    private readonly ExperienceMeter _experience;
    private readonly QuestTracker _quests;
    private readonly SalvageStagingManager _salvage;
    private readonly StatusHudManager _statusHud;
    private readonly List<DelayedExpression> _delayed = [];
    private int _nextDelayId = 1;
    private string _identity = string.Empty;
    private string? _persistentJson;
    private string? _globalJson;
    private bool _disposed;

    public MossTankExpressionRuntime(IPluginHost host, Random? random = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _experience = new ExperienceMeter(host);
        _quests = new QuestTracker(host);
        _salvage = new SalvageStagingManager(host);
        _statusHud = new StatusHudManager(host);
        _functions = CoreExpressionFunctions.CreateDefault(random);
        HostExpressionFunctions.Register(_functions, host);
        RegisterExperienceFunctions();
        RegisterQuestFunctions();
        RegisterSalvageFunctions();
        RegisterStatusHudFunctions();
        RegisterExecutionFunctions();
        BindIdentity(force: true);
    }

    public ExpressionState State => _state;
    internal ExpressionFunctionRegistry Registry => _functions;
    public IReadOnlyCollection<ExpressionFunction> Functions => _functions.Functions;
    public int PendingExecutionCount => _delayed.Count;

    public ExpressionValue Evaluate(
        string source,
        int instructionBudget = DefaultInstructionBudget,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BindIdentity(force: false);
        ExpressionProgram program = ExpressionProgram.Compile(source);
        var context = new ExpressionEvaluationContext(
            _state,
            _functions,
            instructionBudget,
            cancellationToken);
        ExpressionValue result = program.Evaluate(context);
        FlushVariables();
        return result;
    }

    public void OnTick(double elapsedSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BindIdentity(force: false);
        if (elapsedSeconds < 0d || !double.IsFinite(elapsedSeconds))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        _experience.OnTick(elapsedSeconds);
        _quests.OnTick(elapsedSeconds);
        if (_delayed.Count == 0)
            return;

        double elapsedMilliseconds = elapsedSeconds * 1000d;
        for (int index = 0; index < _delayed.Count; index++)
            _delayed[index] = _delayed[index] with
            {
                RemainingMilliseconds =
                    _delayed[index].RemainingMilliseconds - elapsedMilliseconds,
            };

        DelayedExpression[] ready = _delayed
            .Where(static delayed => delayed.RemainingMilliseconds <= 0d)
            .OrderBy(static delayed => delayed.Id)
            .ToArray();
        if (ready.Length == 0)
            return;
        _delayed.RemoveAll(static delayed => delayed.RemainingMilliseconds <= 0d);
        foreach (DelayedExpression delayed in ready)
        {
            try
            {
                Evaluate(delayed.Source);
            }
            catch (Exception error)
            {
                _host.Log.Error(
                    $"Delayed expression {delayed.Id} failed: {error.Message}");
            }
        }
    }

    public void ClearSession()
    {
        _state.Clear(ExpressionVariableScope.Session);
        _delayed.Clear();
    }

    public void DestroyAuxiliaryViews() => _statusHud.Destroy();

    public void Dispose()
    {
        if (_disposed)
            return;
        FlushVariables();
        _delayed.Clear();
        _statusHud.Destroy();
        _disposed = true;
    }

    private void RegisterExecutionFunctions()
    {
        _functions.Register("exec", 1, 1, (context, args) =>
            ExpressionProgram.Compile(args[0].AsString("exec")).Evaluate(context),
            "exec[expression]");
        _functions.Register("delayexec", 2, 2, (_, args) =>
        {
            double delay = Math.Max(0d, args[0].AsNumber("delayexec"));
            string source = args[1].AsString("delayexec");
            int id = NextDelayId();
            _delayed.Add(new DelayedExpression(id, delay, source));
            return ExpressionValue.Number(id);
        }, "delayexec[milliseconds,expression]");
        _functions.Register("clearexec", 1, 1, (_, args) =>
        {
            int id = args[0].AsInt32("clearexec");
            return ExpressionValue.Boolean(
                _delayed.RemoveAll(delayed => delayed.Id == id) != 0);
        }, "clearexec[id]");
    }

    private void RegisterExperienceFunctions()
    {
        _functions.Register("xpreset", 0, 0, (_, _) =>
        {
            _experience.Reset();
            return ExpressionValue.One;
        }, "xpreset[]");
        _functions.Register("xpmeter", 0, 0, (_, _) =>
            ExpressionValue.String(_experience.Format()), "xpmeter[]");
        _functions.Register("xpduration", 0, 0, (_, _) =>
            ExpressionValue.Number(_experience.DurationSeconds), "xpduration[]");
        _functions.Register("xptotal", 0, 0, (_, _) =>
            ExpressionValue.Number(_experience.Experience), "xptotal[]");
        _functions.Register("lumtotal", 0, 0, (_, _) =>
            ExpressionValue.Number(_experience.Luminance), "lumtotal[]");
        _functions.Register("xpavg", 0, 0, (_, _) =>
            ExpressionValue.Number(_experience.ExperiencePerHour), "xpavg[]");
        _functions.Register("lumavg", 0, 0, (_, _) =>
            ExpressionValue.Number(_experience.LuminancePerHour), "lumavg[]");
    }

    private void RegisterQuestFunctions()
    {
        _functions.Register("testquestflag", 1, 1, (_, args) =>
            ExpressionValue.Boolean(_quests.HasCompleted(
                args[0].AsString("testquestflag"))), "testquestflag[questflag]");
        _functions.Register("getqueststatus", 1, 1, (_, args) =>
            ExpressionValue.Boolean(_quests.IsReady(
                args[0].AsString("getqueststatus"))), "getqueststatus[questflag]");
        _functions.Register("getquestktprogress", 1, 1, (_, args) =>
            ExpressionValue.Number(_quests.Progress(
                args[0].AsString("getquestktprogress"))),
            "getquestktprogress[questflag]");
        _functions.Register("getquestktrequired", 1, 1, (_, args) =>
            ExpressionValue.Number(_quests.Required(
                args[0].AsString("getquestktrequired"))),
            "getquestktrequired[questflag]");
        _functions.Register("isrefreshingquests", 0, 0, (_, _) =>
            ExpressionValue.Boolean(_quests.IsRefreshing), "isrefreshingquests[]");
    }

    private void RegisterSalvageFunctions()
    {
        _functions.Register("ustadd", 1, 1, (_, args) =>
            ExpressionValue.Boolean(_salvage.Add(
                args[0].AsObjectId("ustadd"))), "ustadd[object]");
        _functions.Register("ustopen", 0, 0, (_, _) =>
            ExpressionValue.Boolean(_salvage.Open()), "ustopen[]");
        _functions.Register("ustsalvage", 0, 0, (_, _) =>
            ExpressionValue.Boolean(_salvage.Salvage()), "ustsalvage[]");
    }

    private void RegisterStatusHudFunctions()
    {
        _functions.Register("statushud", 2, 2, (_, args) =>
            ExpressionValue.Boolean(_statusHud.Update(
                args[0].AsString("statushud"),
                args[1].ToDisplayString())),
            "statushud[key,value]");
        _functions.Register("statushudcolored", 3, 3, (_, args) =>
            ExpressionValue.Boolean(_statusHud.Update(
                args[0].AsString("statushudcolored"),
                args[1].ToDisplayString(),
                checked((uint)args[2].AsNumber("statushudcolored")))),
            "statushudcolored[key,value,rgb]");
    }

    private int NextDelayId()
    {
        int initial = _nextDelayId;
        do
        {
            int candidate = _nextDelayId++;
            if (_nextDelayId <= 0)
                _nextDelayId = 1;
            if (_delayed.All(delayed => delayed.Id != candidate))
                return candidate;
        }
        while (_nextDelayId != initial);
        throw new ExpressionEvaluationException("No delayed-expression ids remain");
    }

    private void BindIdentity(bool force)
    {
        ICharacterInfo character = _host.Automation.Character;
        string identity = string.Join(
            '\n',
            character.WorldName,
            character.AccountName,
            character.Name);
        if (!force && identity.Equals(_identity, StringComparison.Ordinal))
            return;
        if (_identity.Length != 0)
            FlushVariables();
        _identity = identity;
        _quests.BindIdentity(identity);
        _salvage.Clear();
        _state.Clear(ExpressionVariableScope.Session);
        _delayed.Clear();
        _experience.Reset();
        _persistentJson = LoadScope(ExpressionVariableScope.Persistent);
        _globalJson = LoadScope(ExpressionVariableScope.Global);
    }

    private string? LoadScope(ExpressionVariableScope scope)
    {
        _state.Clear(scope);
        if (!_host.Storage.IsAvailable || _identity.Length == 0)
            return null;
        try
        {
            string? json = _host.Storage.ReadText(StorageKey(scope));
            if (string.IsNullOrWhiteSpace(json))
                return null;
            Dictionary<string, StoredValue>? document = JsonSerializer.Deserialize<
                Dictionary<string, StoredValue>>(json, JsonOptions);
            if (document is not null)
            {
                _state.Replace(scope, document.Select(static pair =>
                    new KeyValuePair<string, ExpressionValue>(
                        pair.Key,
                        Restore(pair.Value))));
            }
            return json;
        }
        catch (Exception error)
        {
            _host.Log.Error($"Unable to load {scope} expression variables: {error.Message}");
            return null;
        }
    }

    private void FlushVariables()
    {
        if (!_host.Storage.IsAvailable || _identity.Length == 0)
            return;
        _persistentJson = FlushScope(
            ExpressionVariableScope.Persistent,
            _persistentJson);
        _globalJson = FlushScope(ExpressionVariableScope.Global, _globalJson);
    }

    private string? FlushScope(ExpressionVariableScope scope, string? previous)
    {
        try
        {
            Dictionary<string, StoredValue> document = _state.Capture(scope)
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => Store(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            string json = JsonSerializer.Serialize(document, JsonOptions);
            if (!json.Equals(previous, StringComparison.Ordinal))
                _host.Storage.WriteText(StorageKey(scope), json);
            return json;
        }
        catch (Exception error)
        {
            _host.Log.Error($"Unable to save {scope} expression variables: {error.Message}");
            return previous;
        }
    }

    private string StorageKey(ExpressionVariableScope scope)
    {
        ICharacterInfo character = _host.Automation.Character;
        string owner = scope == ExpressionVariableScope.Persistent
            ? string.Join('\n', character.WorldName, character.AccountName, character.Name)
            : string.Join('\n', character.WorldName, character.AccountName);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(owner));
        return $"expressions/{scope.ToString().ToLowerInvariant()}/"
            + $"{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}.json";
    }

    private static StoredValue Store(in ExpressionValue value) => value.Kind switch
    {
        ExpressionValueKind.Number => new StoredValue
        {
            Kind = "number",
            Number = value.AsNumber(),
        },
        ExpressionValueKind.Boolean => new StoredValue
        {
            Kind = "boolean",
            Number = value.AsNumber(),
        },
        ExpressionValueKind.String => new StoredValue
        {
            Kind = "string",
            Text = value.AsString(),
        },
        ExpressionValueKind.List => new StoredValue
        {
            Kind = "list",
            List = value.AsList().Items.Select(static item => Store(item)).ToList(),
        },
        ExpressionValueKind.Dictionary => new StoredValue
        {
            Kind = "dictionary",
            Dictionary = value.AsDictionary().Items.ToDictionary(
                static pair => pair.Key,
                static pair => Store(pair.Value),
                StringComparer.Ordinal),
        },
        ExpressionValueKind.Coordinates => StoreCoordinates(value.AsCoordinates()),
        ExpressionValueKind.WorldObject => new StoredValue
        {
            Kind = "worldobject",
            Number = value.AsObjectId(),
        },
        _ => throw new ExpressionEvaluationException(
            $"{value.Kind} values cannot be persisted"),
    };

    private static StoredValue StoreCoordinates(in ExpressionCoordinates value) => new()
    {
        Kind = "coordinates",
        Coordinates =
        [
            value.EastWest,
            value.NorthSouth,
            value.Elevation,
        ],
    };

    private static ExpressionValue Restore(StoredValue value) =>
        value.Kind.ToLowerInvariant() switch
        {
            "number" => ExpressionValue.Number(value.Number),
            "boolean" => ExpressionValue.Boolean(value.Number != 0d),
            "string" => ExpressionValue.String(value.Text),
            "list" => ExpressionValue.List(new ExpressionList(
                (value.List ?? []).Select(Restore))),
            "dictionary" => RestoreDictionary(value.Dictionary),
            "coordinates" => RestoreCoordinates(value.Coordinates),
            "worldobject" => ExpressionValue.WorldObject(checked((uint)value.Number)),
            _ => ExpressionValue.Zero,
        };

    private static ExpressionValue RestoreDictionary(
        Dictionary<string, StoredValue>? values)
    {
        var result = new ExpressionDictionary();
        if (values is not null)
        {
            foreach ((string key, StoredValue value) in values)
                result.Items[key] = Restore(value);
        }
        return ExpressionValue.Dictionary(result);
    }

    private static ExpressionValue RestoreCoordinates(double[]? values) =>
        values is { Length: >= 2 }
            ? ExpressionValue.Coordinates(new ExpressionCoordinates(
                values[0],
                values[1],
                values.Length >= 3 ? values[2] : 0d))
            : ExpressionValue.Zero;

    private sealed class StoredValue
    {
        public string Kind { get; set; } = "number";
        public double Number { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<StoredValue>? List { get; set; }
        public Dictionary<string, StoredValue>? Dictionary { get; set; }
        public double[]? Coordinates { get; set; }
    }

    private readonly record struct DelayedExpression(
        int Id,
        double RemainingMilliseconds,
        string Source);
}
