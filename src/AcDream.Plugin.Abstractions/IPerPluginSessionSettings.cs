namespace AcDream.Plugin.Abstractions;

public interface IPerPluginSessionSettings
{
    IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId);
}
