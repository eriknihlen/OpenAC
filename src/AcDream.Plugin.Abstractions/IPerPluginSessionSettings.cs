namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Implemented by a host that keeps a separate set of session settings for
/// each plugin, so the per-plugin host wrapper can hand each plugin only
/// its own. Plugins read their settings through
/// <see cref="IPluginHost.SessionSettings"/> and never call this directly.
/// </summary>
public interface IPerPluginSessionSettings
{
    /// <summary>
    /// The settings this session was started with for one plugin, keyed by
    /// setting name. Empty when the session declared none for it.
    /// </summary>
    IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId);
}
