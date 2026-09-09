using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class PluginCommandRegistry : IPluginCommandRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, Exception>? _onFailure;

    public PluginCommandRegistry(Action<string, Exception>? onFailure = null)
    {
        _onFailure = onFailure;
    }

    public IDisposable Register(string verb, Action<PluginCommand> handler)
    {
        string normalized = NormalizeVerb(verb);
        ArgumentNullException.ThrowIfNull(handler);
        var registration = new Registration(this, normalized, handler);
        lock (_gate)
        {
            if (_registrations.ContainsKey(normalized))
            {
                throw new InvalidOperationException(
                    $"Plugin command '{normalized}' is already registered.");
            }
            _registrations.Add(normalized, registration);
        }
        return registration;
    }

    public bool TryHandle(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return false;
        string trimmed = rawText.Trim();
        if (trimmed.Length < 2 || trimmed[0] is not ('/' or '@'))
            return false;

        int separator = trimmed.IndexOfAny([' ', '\t'], 1);
        string verb = separator < 0
            ? trimmed[1..]
            : trimmed[1..separator];
        if (verb.Length == 0)
            return false;

        Registration? registration;
        lock (_gate)
            _registrations.TryGetValue(verb, out registration);
        if (registration is null)
            return false;

        string arguments = separator < 0
            ? string.Empty
            : trimmed[(separator + 1)..].Trim();
        try
        {
            registration.Invoke(new PluginCommand(
                registration.Verb,
                arguments,
                trimmed));
        }
        catch (Exception error)
        {
            try
            {
                _onFailure?.Invoke(registration.Verb, error);
            }
            catch
            {
            }
        }
        return true;
    }

    private static string NormalizeVerb(string verb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        string normalized = verb.Trim().TrimStart('/', '@');
        if (normalized.Length is < 1 or > 32
            || normalized.Any(static value => !char.IsLetterOrDigit(value)))
        {
            throw new ArgumentException(
                "Plugin command verbs must contain 1-32 letters or digits.",
                nameof(verb));
        }
        return normalized;
    }

    private void Remove(Registration expected)
    {
        lock (_gate)
        {
            if (_registrations.TryGetValue(expected.Verb, out Registration? current)
                && ReferenceEquals(current, expected))
            {
                _registrations.Remove(expected.Verb);
            }
        }
    }

    private sealed class Registration(
        PluginCommandRegistry owner,
        string verb,
        Action<PluginCommand> handler) : IDisposable
    {
        private readonly object _gate = new();
        private PluginCommandRegistry? _owner = owner;
        private Action<PluginCommand>? _handler = handler;

        internal string Verb { get; } = verb;

        internal void Invoke(PluginCommand command)
        {
            Action<PluginCommand>? callback;
            lock (_gate)
                callback = _handler;
            callback?.Invoke(command);
        }

        public void Dispose()
        {
            PluginCommandRegistry? currentOwner;
            lock (_gate)
            {
                currentOwner = _owner;
                _owner = null;
                _handler = null;
            }
            currentOwner?.Remove(this);
        }
    }
}
