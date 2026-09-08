namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginCommand(
    string Verb,
    string Arguments,
    string RawText);

public interface IPluginCommandRegistry
{
    IDisposable Register(string verb, Action<PluginCommand> handler);
}

public sealed class NoOpPluginCommandRegistry : IPluginCommandRegistry
{
    public static NoOpPluginCommandRegistry Instance { get; } = new();

    private NoOpPluginCommandRegistry()
    {
    }

    public IDisposable Register(string verb, Action<PluginCommand> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentNullException.ThrowIfNull(handler);
        return NoOpLease.Instance;
    }

    private sealed class NoOpLease : IDisposable
    {
        public static NoOpLease Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
