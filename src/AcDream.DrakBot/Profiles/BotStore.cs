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

    /// <summary>The storage underneath, for the file layer that shares it.</summary>
    public IPluginStorage Storage => storage;

    /// <summary>A loose file in the plugin's folder: a log dump, an export.</summary>
    public void WriteText(string fileName, string content)
    {
        if (storage.IsAvailable)
            storage.WriteText(SanitizeName(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName), content);
    }

    public IReadOnlyList<string> ProfileNames() => Names(ProfilePrefix);

    /// <summary>The profile in use when the bot last ran, so it comes back with the same one; null when never recorded.</summary>
    public string? LastProfileName
    {
        get
        {
            string? name = storage.ReadText(LastProfileKey)?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        set
        {
            if (storage.IsAvailable)
                storage.WriteText(LastProfileKey, value ?? string.Empty);
        }
    }

    private const string LastProfileKey = "last-profile.txt";

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

    /// <summary>
    /// A route by name: the JSON form first, else a VTank <c>.nav</c> file
    /// of that name dropped into the routes folder.
    /// </summary>
    public Route? LoadRoute(string name) => LoadRoute(name, out _);

    public Route? LoadRoute(string name, out string? warning)
    {
        warning = null;
        string? json = storage.ReadText(RouteKey(name));
        if (json is not null)
            return Route.FromJson(json) with { Name = name };
        string? nav = storage.ReadText(NavKey(name));
        if (nav is null)
            return null;
        return NavFile.Parse(name, nav.ReplaceLineEndings("\n").Split('\n'), out warning);
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

    private static string NavKey(string name) => RoutePrefix + SanitizeName(name) + ".nav";

    private IReadOnlyList<string> Names(string prefix)
    {
        if (!storage.IsAvailable)
            return [];
        return storage.List(prefix.TrimEnd('/'))
            .Select(static key => Path.GetFileNameWithoutExtension(key))
            .Where(static name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
