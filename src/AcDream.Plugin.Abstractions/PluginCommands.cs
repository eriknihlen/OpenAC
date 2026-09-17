namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One invocation of a chat command a plugin registered, as the player
/// typed it.
/// </summary>
/// <param name="Verb">
/// The command word without its leading slash or at-sign, as registered.
/// </param>
/// <param name="Arguments">
/// Everything the player typed after the verb, trimmed; empty when there
/// was nothing.
/// </param>
/// <param name="RawText">The whole trimmed line, leading slash included.</param>
public readonly record struct PluginCommand(
    string Verb,
    string Arguments,
    string RawText);

/// <summary>
/// Lets a plugin claim chat commands of its own, so a line the player types
/// as <c>/myverb ...</c> reaches the plugin instead of the server.
/// </summary>
public interface IPluginCommandRegistry
{
    /// <summary>
    /// Claims one verb (1-32 letters or digits, matched ignoring case, with
    /// any leading slash or at-sign stripped) and routes it to
    /// <paramref name="handler"/>. Throws when the verb is malformed or
    /// already claimed. Dispose the result to release it; the host also
    /// releases every verb a plugin claimed when that plugin unloads.
    /// </summary>
    IDisposable Register(string verb, Action<PluginCommand> handler);
}

/// <summary>
/// The command registry a host with no command line hands out: it validates
/// the arguments and then keeps nothing, so the handler never fires.
/// </summary>
public sealed class NoOpPluginCommandRegistry : IPluginCommandRegistry
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpPluginCommandRegistry Instance { get; } = new();

    private NoOpPluginCommandRegistry()
    {
    }

    /// <inheritdoc/>
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
