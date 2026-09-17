namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A small key/value store the host keeps for one plugin, so its settings
/// and data survive between runs. Keys are relative paths; the host decides
/// where they live. A host with nowhere to write answers every call as
/// unavailable.
/// </summary>
public interface IPluginStorage
{
    /// <summary>
    /// Whether this storage can actually read and write. False on the inert
    /// storage a host with no location to write to hands out.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// The absolute directory keys are written beneath, or null when this
    /// storage is not backed by files. Useful for telling a user where their
    /// data went; keys still go through this interface.
    /// </summary>
    string? RootPath => null;

    /// <summary>
    /// Returns the text stored under <paramref name="key"/>, or null when
    /// nothing is stored there or the storage is unavailable.
    /// </summary>
    string? ReadText(string key) => null;

    /// <summary>Relative file keys beneath one relative prefix.</summary>
    IReadOnlyList<string> List(string prefix) => Array.Empty<string>();

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/>,
    /// replacing anything already there. Throws
    /// <see cref="NotSupportedException"/> when the storage is unavailable.
    /// </summary>
    void WriteText(string key, string content) =>
        throw new NotSupportedException("Plugin storage is unavailable.");

    /// <summary>
    /// Removes what is stored under <paramref name="key"/>. Returns false
    /// when there was nothing to remove or the storage is unavailable.
    /// </summary>
    bool Delete(string key) => false;
}

/// <summary>
/// The storage a host with nowhere to write hands out: it keeps nothing and
/// reports itself unavailable.
/// </summary>
public sealed class NoOpPluginStorage : IPluginStorage
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpPluginStorage Instance { get; } = new();
    private NoOpPluginStorage() { }
}
