namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A board of named status lines that plugins in one client share. Each
/// plugin writes only under its own plugin id and every plugin can read what
/// any plugin wrote, so one plugin can say what state it is in -- a mode, a
/// phase, a target -- and another can show or report it without either
/// referencing the other. Nothing here leaves the client, and the board
/// starts empty each time the client loads its plugins; a login does not
/// clear it.
/// </summary>
public interface IPluginStatusBoard
{
    /// <summary>
    /// True when this host keeps a status board. The default implementation
    /// always reports false.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// Sets one of the calling plugin's own status lines, or clears it.
    /// </summary>
    /// <param name="key">
    /// The line's name, chosen by the plugin, compared exactly. 1 to 128
    /// characters.
    /// </param>
    /// <param name="value">
    /// The text, at most 4096 characters; null clears the line.
    /// </param>
    /// <returns>
    /// False for a blank or over-long key, an over-long value, a 257th line
    /// for one plugin, or a host without a board -- which is what the
    /// default implementation does. Clearing a line that is not there is
    /// true.
    /// </returns>
    /// <remarks>
    /// A plugin's lines are cleared when the plugin is unloaded, and a write
    /// after that is refused, so a line never outlives the plugin that wrote
    /// it.
    /// </remarks>
    bool Publish(string key, string? value) => false;

    /// <summary>
    /// Reads one status line a plugin published.
    /// </summary>
    /// <param name="pluginId">
    /// The publishing plugin's id, as in its manifest, compared without
    /// regard to case.
    /// </param>
    /// <param name="key">The line's name, compared exactly.</param>
    /// <param name="value">The text; empty when this returns false.</param>
    /// <returns>
    /// False when that plugin has no such line, when it is not loaded, or on
    /// a host without a board -- which is what the default implementation
    /// does.
    /// </returns>
    bool TryRead(string pluginId, string key, out string value)
    {
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Every status line one plugin has published, keyed by name.
    /// </summary>
    /// <param name="pluginId">
    /// The publishing plugin's id, compared without regard to case.
    /// </param>
    /// <returns>
    /// A copy of the plugin's lines; empty when it has none, when it is not
    /// loaded, or on a host without a board -- which is what the default
    /// implementation returns.
    /// </returns>
    IReadOnlyDictionary<string, string> Capture(string pluginId) =>
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>
/// The status board a host without one offers: nothing is kept and every
/// read finds nothing.
/// </summary>
public sealed class NoOpPluginStatusBoard : IPluginStatusBoard
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpPluginStatusBoard Instance { get; } = new();

    private NoOpPluginStatusBoard()
    {
    }
}
