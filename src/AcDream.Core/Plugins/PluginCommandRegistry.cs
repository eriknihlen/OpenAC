using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class PluginCommandRegistry : IPluginCommandRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPluginCommandDefinition> _definitions = [];
    private readonly Action<string, Exception>? _onFailure;
    public PluginCommandRegistry(Action<string, Exception>? onFailure = null) => _onFailure = onFailure;

    public IReadOnlyList<IPluginCommandDefinition> Definitions { get { lock (_gate) return _definitions.ToArray(); } }

    public IDisposable Register(string verb, Action<PluginCommand> handler)
    {
        string normalized = NormalizeVerb(verb);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterCore([normalized], null, handler);
    }

    public IDisposable Register(IPluginCommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        string verb = NormalizeVerb(definition.Verb);
        string[] names = [verb, .. definition.Aliases.Select(NormalizeVerb)];
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Command aliases must be unique.", nameof(definition));
        return RegisterCore(names, definition, command => { definition.Invoke(command); });
    }

    public ValueTask<IReadOnlyList<PluginCommandCompletion>> CompleteAsync(PluginCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPluginCommandDefinition? definition = Find(command.Verb);
        if (definition is null) return ValueTask.FromResult<IReadOnlyList<PluginCommandCompletion>>([]);
        return ValueTask.FromResult(definition.Complete(command));
    }

    public string GetHelp()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, _definitions
                .OrderBy(d => d.Verb, StringComparer.OrdinalIgnoreCase)
                .Select(d => $"/{d.Verb} - {d.Description}"));
        }
    }

    public bool TryHandle(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return false;
        string trimmed = rawText.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('/' or '@')) return false;
        int separator = trimmed.IndexOfAny([' ', '\t']);
        string verb = separator < 0 ? trimmed[1..] : trimmed[1..separator];
        Registration? registration;
        lock (_gate) _registrations.TryGetValue(verb, out registration);
        if (registration is null) return false;
        string arguments = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
        try { registration.Invoke(new PluginCommand(registration.Verb, arguments, trimmed)); }
        catch (Exception error) { try { _onFailure?.Invoke(registration.Verb, error); } catch { } }
        return true;
    }

    private Registration RegisterCore(string[] names, IPluginCommandDefinition? definition, Action<PluginCommand> handler)
    {
        var registration = new Registration(this, names[0], names, definition, handler);
        lock (_gate)
        {
            if (names.Any(_registrations.ContainsKey)) throw new InvalidOperationException($"Plugin command '{names.First(_registrations.ContainsKey)}' is already registered.");
            foreach (string name in names) _registrations.Add(name, registration);
            if (definition is not null) _definitions.Add(definition);
        }
        return registration;
    }

    private IPluginCommandDefinition? Find(string verb) { lock (_gate) return _registrations.TryGetValue(verb, out var r) ? r.Definition : null; }
    private static string NormalizeVerb(string verb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        string normalized = verb.Trim().TrimStart('/', '@');
        if (normalized.Length is < 1 or > 32 || normalized.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Plugin command verbs must contain 1-32 letters or digits.", nameof(verb));
        return normalized;
    }
    private void Remove(Registration expected)
    {
        lock (_gate)
        {
            foreach (string name in expected.Names) if (_registrations.TryGetValue(name, out var current) && ReferenceEquals(current, expected)) _registrations.Remove(name);
            if (expected.Definition is not null) _definitions.Remove(expected.Definition);
        }
    }
    private sealed class Registration(PluginCommandRegistry owner, string verb, string[] names, IPluginCommandDefinition? definition, Action<PluginCommand> handler) : IDisposable
    {
        private readonly object _gate = new(); private PluginCommandRegistry? _owner = owner; private Action<PluginCommand>? _handler = handler;
        internal string Verb { get; } = verb; internal string[] Names { get; } = names; internal IPluginCommandDefinition? Definition { get; } = definition;
        internal void Invoke(PluginCommand command) { lock (_gate) _handler?.Invoke(command); }
        public void Dispose() { PluginCommandRegistry? owner; lock (_gate) { owner = _owner; _owner = null; _handler = null; } owner?.Remove(this); }
    }
}
