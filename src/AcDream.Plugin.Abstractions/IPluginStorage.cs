using System.Text.Json;

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Identifies a plugin storage namespace. Scope names are logical names and
/// are sanitized by the host; plugins must not construct filesystem paths.
/// </summary>
public readonly record struct PluginStorageScope(string Name)
{
    /// <summary>The plugin-wide namespace.</summary>
    public static PluginStorageScope Global { get; } = new("global");

    /// <summary>Creates a character-specific namespace.</summary>
    public static PluginStorageScope Character(string characterId) => new($"character/{characterId}");

    /// <summary>Creates a world-specific namespace.</summary>
    public static PluginStorageScope World(string worldId) => new($"world/{worldId}");
}

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

    /// <summary>
    /// Relative file keys beneath one relative prefix. An empty prefix lists
    /// every key in the storage.
    /// </summary>
    IReadOnlyList<string> List(string prefix) => Array.Empty<string>();

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/>,
    /// replacing anything already there. Throws
    /// <see cref="NotSupportedException"/> when the storage is unavailable.
    /// </summary>
    void WriteText(string key, string content) =>
        throw new NotSupportedException("Plugin storage is unavailable.");

    /// <summary>
    /// Creates the directory named by <paramref name="prefix"/> beneath the
    /// storage root, together with every missing parent, so a plugin can lay
    /// its folder tree out before it has anything to write into it. The
    /// prefix is a slash-separated relative key prefix; a trailing slash is
    /// allowed. Returns true when the directory exists afterwards, and false
    /// when the storage is unavailable. Throws
    /// <see cref="ArgumentException"/> for a prefix that is absolute or
    /// escapes the storage root, exactly as <see cref="WriteText"/> does for
    /// such a key.
    /// </summary>
    bool EnsureDirectory(string prefix) => false;

    /// <summary>
    /// Removes what is stored under <paramref name="key"/>. Returns false
    /// when there was nothing to remove or the storage is unavailable.
    /// </summary>
    bool Delete(string key) => false;

    /// <summary>
    /// Opens a child namespace for scoped plugin data, kept apart from the
    /// plugin's other scopes. A scope name follows the key rules: forward
    /// slashes nest it, and a blank or rooted name, or one containing
    /// <c>..</c> or a backslash, throws <see cref="ArgumentException"/>.
    /// </summary>
    IPluginStorage OpenScope(PluginStorageScope scope) => this;
}

/// <summary>JSON conveniences for plugin storage.</summary>
public static class PluginStorageExtensions
{
    /// <summary>Reads and deserializes a JSON value, or returns its default when absent.</summary>
    public static T? ReadJson<T>(this IPluginStorage storage, string key,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        string? text = storage.ReadText(key);
        return text is null ? default : JsonSerializer.Deserialize<T>(text, options);
    }

    /// <summary>Serializes and atomically stores a JSON value.</summary>
    public static void WriteJson<T>(this IPluginStorage storage, string key, T value,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        storage.WriteText(key, JsonSerializer.Serialize(value, options));
    }
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
