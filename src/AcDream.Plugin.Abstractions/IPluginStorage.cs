namespace AcDream.Plugin.Abstractions;

public interface IPluginStorage
{
    bool IsAvailable => false;
    string? ReadText(string key) => null;
    /// <summary>The raw file, for formats that are not text (a VTank <c>.met</c>).</summary>
    byte[]? ReadBytes(string key) => null;
    /// <summary>Relative file keys beneath one relative prefix.</summary>
    IReadOnlyList<string> List(string prefix) => Array.Empty<string>();
    void WriteText(string key, string content) =>
        throw new NotSupportedException("Plugin storage is unavailable.");
    bool Delete(string key) => false;

    /// <summary>
    /// The folder on disk behind this storage, for telling the player where
    /// their files are or opening it for them; null when the storage is not
    /// a folder (a test double, an unavailable host).
    /// </summary>
    string? Directory => null;
}

public sealed class NoOpPluginStorage : IPluginStorage
{
    public static NoOpPluginStorage Instance { get; } = new();
    private NoOpPluginStorage() { }
}
