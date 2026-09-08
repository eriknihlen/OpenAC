namespace AcDream.Plugin.Abstractions;

public interface IPluginStorage
{
    bool IsAvailable => false;
    string? ReadText(string key) => null;
    /// <summary>Relative file keys beneath one relative prefix.</summary>
    IReadOnlyList<string> List(string prefix) => Array.Empty<string>();
    void WriteText(string key, string content) =>
        throw new NotSupportedException("Plugin storage is unavailable.");
    bool Delete(string key) => false;
}

public sealed class NoOpPluginStorage : IPluginStorage
{
    public static NoOpPluginStorage Instance { get; } = new();
    private NoOpPluginStorage() { }
}
