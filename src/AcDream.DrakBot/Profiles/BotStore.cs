using AcDream.DrakBot.Navigation;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Profiles;

/// <summary>
/// Profiles and routes as JSON in the plugin's own storage. Keys are plain
/// relative paths so the files are easy to find and hand-edit.
/// </summary>
public sealed class BotStore(IPluginStorage storage)
{
    private const string ProfilePrefix = "profiles/";
    private const string RoutePrefix = "routes/";

    public bool IsAvailable => storage.IsAvailable;

    public IReadOnlyList<string> ProfileNames() => Names(ProfilePrefix);

    public IReadOnlyList<string> RouteNames() => Names(RoutePrefix);

    public BotProfile? LoadProfile(string name)
    {
        string? json = storage.ReadText(ProfileKey(name));
        return json is null ? null : BotProfile.FromJson(json) with { Name = name };
    }

    public void SaveProfile(BotProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        storage.WriteText(ProfileKey(profile.Name), profile.ToJson());
    }

    public Route? LoadRoute(string name)
    {
        string? json = storage.ReadText(RouteKey(name));
        return json is null ? null : Route.FromJson(json) with { Name = name };
    }

    public void SaveRoute(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        storage.WriteText(RouteKey(route.Name), route.ToJson());
    }

    public static string SanitizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char character in name.Trim())
            builder.Append(Array.IndexOf(invalid, character) >= 0 || character is '/' or '\\' ? '_' : character);
        return builder.ToString();
    }

    private static string ProfileKey(string name) => ProfilePrefix + SanitizeName(name) + ".json";

    private static string RouteKey(string name) => RoutePrefix + SanitizeName(name) + ".json";

    private IReadOnlyList<string> Names(string prefix)
    {
        if (!storage.IsAvailable)
            return [];
        return storage.List(prefix.TrimEnd('/'))
            .Select(static key => Path.GetFileNameWithoutExtension(key))
            .Where(static name => !string.IsNullOrEmpty(name))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
