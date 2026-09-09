namespace AcDream.Platform;

public interface IApplicationPathEnvironment
{
    bool IsWindows { get; }

    string CurrentDirectory { get; }

    string? GetEnvironmentVariable(string name);

    string GetFolderPath(Environment.SpecialFolder folder);
}

internal sealed class ApplicationPathEnvironment
    : IApplicationPathEnvironment
{
    internal static ApplicationPathEnvironment Instance { get; } = new();

    private ApplicationPathEnvironment()
    {
    }

    public bool IsWindows => OperatingSystem.IsWindows();

    public string CurrentDirectory => Environment.CurrentDirectory;

    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    public string GetFolderPath(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);
}

public sealed record ApplicationPathSet(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory,
    string? LegacyConfigDirectory)
{
    public string SettingsFile =>
        Path.Combine(ConfigDirectory, "settings.json");

    public string KeyBindingsFile =>
        Path.Combine(ConfigDirectory, "keybinds.json");

    public string LogsDirectory =>
        Path.Combine(DataDirectory, "logs");

    public string ScreenshotsDirectory =>
        Path.Combine(DataDirectory, "screenshots");

    public string PluginsDirectory =>
        Path.Combine(DataDirectory, "plugins");

    public string DiagnosticsDirectory =>
        Path.Combine(CacheDirectory, "diagnostics");

    public static ApplicationPathSet Resolve(
        string? configDirectory = null,
        string? dataDirectory = null,
        string? cacheDirectory = null,
        IApplicationPathEnvironment? platform = null)
    {
        platform ??= ApplicationPathEnvironment.Instance;

        // Explicit method/CLI arguments remain authoritative. The environment
        // seam lets graphical automation and portable installations isolate
        // all mutable user state without rewriting the real user's settings.
        configDirectory ??= NonEmpty(
            platform.GetEnvironmentVariable("ACDREAM_CONFIG_DIR"));
        dataDirectory ??= NonEmpty(
            platform.GetEnvironmentVariable("ACDREAM_DATA_DIR"));
        cacheDirectory ??= NonEmpty(
            platform.GetEnvironmentVariable("ACDREAM_CACHE_DIR"));

        string config;
        string data;
        string cache;
        if (platform.IsWindows)
        {
            string roaming = RequireFolder(
                platform,
                Environment.SpecialFolder.ApplicationData);
            string local = RequireFolder(
                platform,
                Environment.SpecialFolder.LocalApplicationData);
            config = Path.Combine(roaming, "acdream");
            data = Path.Combine(local, "acdream");
            cache = Path.Combine(local, "acdream", "cache");
        }
        else
        {
            string home = RequireFolder(
                platform,
                Environment.SpecialFolder.UserProfile);
            config = ResolveXdg(
                platform,
                "XDG_CONFIG_HOME",
                Path.Combine(home, ".config"),
                "acdream");
            data = ResolveXdg(
                platform,
                "XDG_DATA_HOME",
                Path.Combine(home, ".local", "share"),
                "acdream");
            cache = ResolveXdg(
                platform,
                "XDG_CACHE_HOME",
                Path.Combine(home, ".cache"),
                "acdream");
        }

        string? legacyConfigDirectory =
            platform.IsWindows && configDirectory is null
                ? Path.Combine(
                    RequireFolder(
                        platform,
                        Environment.SpecialFolder.LocalApplicationData),
                    "acdream")
                : null;

        return new ApplicationPathSet(
            Normalize(configDirectory ?? config, platform),
            Normalize(dataDirectory ?? data, platform),
            Normalize(cacheDirectory ?? cache, platform),
            legacyConfigDirectory is null
                ? null
                : Normalize(legacyConfigDirectory, platform));
    }

    private static string ResolveXdg(
        IApplicationPathEnvironment platform,
        string variable,
        string fallback,
        string leaf)
    {
        string? configured = platform.GetEnvironmentVariable(variable);
        string root = string.IsNullOrWhiteSpace(configured)
            ? fallback
            : configured;
        return Path.Combine(root, leaf);
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string RequireFolder(
        IApplicationPathEnvironment platform,
        Environment.SpecialFolder folder)
    {
        string value = platform.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The operating system did not provide {folder}.");
        }

        return value;
    }

    private static string Normalize(
        string path,
        IApplicationPathEnvironment platform)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Application directories cannot be empty.",
                nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path, platform.CurrentDirectory));
    }
}
