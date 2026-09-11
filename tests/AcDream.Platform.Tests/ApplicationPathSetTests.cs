using AcDream.Platform;

namespace AcDream.Platform.Tests;

public sealed class ApplicationPathSetTests
{
    [Fact]
    public void MacUsesApplicationSupportAndCachesOutsideTheApplicationBundle()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mac-home"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            IsMacOS = true,
            UserProfile = home,
            Variables = { ["XDG_DATA_HOME"] = Path.Combine(home, "linux-data") },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(home, "Library", "Application Support", "acdream"), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(home, "Library", "Caches", "acdream"), paths.CacheDirectory);
        Assert.Equal(
            Path.Combine(home, ".config", "acdream"),
            paths.LegacyConfigDirectory);
    }

    [Fact]
    public void MacLegacyConfigFollowsXdgConfigHomeWhenItIsSet()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mac-xdg-home"));
        string xdg = Path.Combine(home, "xdg-config");
        var platform = new FixtureEnvironment(isWindows: false)
        {
            IsMacOS = true,
            UserProfile = home,
            Variables = { ["XDG_CONFIG_HOME"] = xdg },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(xdg, "acdream"), paths.LegacyConfigDirectory);
    }

    [Fact]
    public void MacHasNoLegacyConfigWhenTheConfigDirectoryIsOverridden()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mac-explicit-config"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            IsMacOS = true,
            UserProfile = home,
            CurrentDirectoryValue = home,
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            configDirectory: Path.Combine(home, "explicit"),
            platform: platform);

        Assert.Null(paths.LegacyConfigDirectory);
    }

    [Fact]
    public void MacPreservesExplicitAndEnvironmentOverrides()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mac-overrides"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            IsMacOS = true,
            UserProfile = home,
            CurrentDirectoryValue = home,
            Variables =
            {
                ["ACDREAM_CONFIG_DIR"] = "custom-config",
                ["ACDREAM_DATA_DIR"] = "environment-data",
                ["ACDREAM_CACHE_DIR"] = "custom-cache",
            },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(dataDirectory: "explicit-data", platform: platform);

        Assert.Equal(Path.Combine(home, "custom-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(home, "explicit-data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(home, "custom-cache"), paths.CacheDirectory);
    }

    [Fact]
    public void LinuxUsesXdgRootsAndPublishesFeaturePaths()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-runtime-xdg"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "home"),
            Variables =
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(root, "cfg"),
                ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            platform: platform);

        Assert.Equal(
            Path.Combine(root, "cfg", "acdream"),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.Combine(root, "data", "acdream"),
            paths.DataDirectory);
        Assert.Equal(
            Path.Combine(root, "cache", "acdream"),
            paths.CacheDirectory);
        Assert.Equal(
            Path.Combine(paths.ConfigDirectory, "settings.json"),
            paths.SettingsFile);
        Assert.Equal(
            Path.Combine(paths.ConfigDirectory, "keybinds.json"),
            paths.KeyBindingsFile);
        Assert.Equal(
            Path.Combine(paths.DataDirectory, "plugins"),
            paths.PluginsDirectory);
        Assert.Equal(
            Path.Combine(paths.CacheDirectory, "diagnostics"),
            paths.DiagnosticsDirectory);
        Assert.Null(paths.LegacyConfigDirectory);
    }

    [Fact]
    public void LinuxFallsBackToHomeAndNormalizesOverrides()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream runtime paths"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "home"),
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            "relative config",
            "dåta",
            "cache",
            platform);

        Assert.Equal(
            Path.GetFullPath(
                "relative config",
                platform.CurrentDirectoryValue),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.GetFullPath("dåta", platform.CurrentDirectoryValue),
            paths.DataDirectory);
        Assert.Equal(
            Path.GetFullPath("cache", platform.CurrentDirectoryValue),
            paths.CacheDirectory);
    }

    [Fact]
    public void WindowsUsesRoamingConfigAndLocalData()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-runtime-windows"));
        var platform = new FixtureEnvironment(isWindows: true)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            ApplicationData = Path.Combine(root, "AppData", "Roaming"),
            LocalApplicationData =
                Path.Combine(root, "AppData", "Local"),
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            platform: platform);

        Assert.Equal(
            Path.Combine(
                platform.ApplicationData,
                "acdream"),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.Combine(
                platform.LocalApplicationData,
                "acdream"),
            paths.DataDirectory);
        Assert.Equal(
            Path.Combine(
                platform.LocalApplicationData,
                "acdream",
                "cache"),
            paths.CacheDirectory);
        Assert.Equal(
            Path.Combine(
                platform.LocalApplicationData,
                "acdream"),
            paths.LegacyConfigDirectory);
    }

    [Fact]
    public void CrossPlatformEnvironmentOverridesIsolateAllMutableUserState()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-isolated-automation"));
        var platform = new FixtureEnvironment(isWindows: true)
        {
            CurrentDirectoryValue = root,
            ApplicationData = Path.Combine(root, "real-roaming"),
            LocalApplicationData = Path.Combine(root, "real-local"),
            Variables =
            {
                ["ACDREAM_CONFIG_DIR"] = "capture-config",
                ["ACDREAM_DATA_DIR"] = "capture-data",
                ["ACDREAM_CACHE_DIR"] = "capture-cache",
            },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(root, "capture-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "capture-data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(root, "capture-cache"), paths.CacheDirectory);
        Assert.Null(paths.LegacyConfigDirectory);
    }

    [Fact]
    public void ExplicitArgumentsOverrideAutomationEnvironmentRoots()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-explicit-paths"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            CurrentDirectoryValue = root,
            UserProfile = Path.Combine(root, "home"),
            Variables =
            {
                ["ACDREAM_CONFIG_DIR"] = "environment-config",
                ["ACDREAM_DATA_DIR"] = "environment-data",
                ["ACDREAM_CACHE_DIR"] = "environment-cache",
            },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            "explicit-config",
            "explicit-data",
            "explicit-cache",
            platform);

        Assert.Equal(Path.Combine(root, "explicit-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "explicit-data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(root, "explicit-cache"), paths.CacheDirectory);
    }

    private sealed class FixtureEnvironment(bool isWindows)
        : IApplicationPathEnvironment
    {
        public bool IsWindows { get; } = isWindows;

        public bool IsMacOS { get; init; }

        public string CurrentDirectoryValue { get; init; } =
            Environment.CurrentDirectory;

        public string CurrentDirectory => CurrentDirectoryValue;

        public string UserProfile { get; init; } =
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

        public string ApplicationData { get; init; } =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

        public string LocalApplicationData { get; init; } =
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

        public Dictionary<string, string> Variables { get; } =
            new(StringComparer.Ordinal);

        public string? GetEnvironmentVariable(string name) =>
            Variables.TryGetValue(name, out string? value)
                ? value
                : null;

        public string GetFolderPath(
            Environment.SpecialFolder folder) =>
            folder switch
            {
                Environment.SpecialFolder.UserProfile => UserProfile,
                Environment.SpecialFolder.ApplicationData =>
                    ApplicationData,
                Environment.SpecialFolder.LocalApplicationData =>
                    LocalApplicationData,
                _ => string.Empty,
            };
    }
}
