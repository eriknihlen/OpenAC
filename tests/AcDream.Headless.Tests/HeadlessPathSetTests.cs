using AcDream.Headless.Configuration;
using AcDream.Headless.Platform;

namespace AcDream.Headless.Tests;

public sealed class HeadlessPathSetTests
{
    [Fact]
    public void LinuxUsesXdgDirectoriesWhenConfigured()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-xdg"));
        var platform = new FixturePlatform(isWindows: false)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "home", "bot"),
            Variables =
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(root, "cfg"),
                ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            },
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(),
            platform);

        Assert.Equal(
            Path.Combine(root, "cfg", "acdream"),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.Combine(root, "data", "acdream"),
            paths.DataDirectory);
        Assert.Equal(
            Path.Combine(root, "cache", "acdream"),
            paths.CacheDirectory);
    }

    [Fact]
    public void LinuxFallsBackToHomeAndNormalizesOverrides()
    {
        var platform = new FixturePlatform(isWindows: false)
        {
            CurrentDirectoryValue = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "acdream work")),
            UserProfile = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "bot home")),
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(
                ConfigDirectory: "relative cfg",
                DataDirectory: "dÃ¥ta",
                CacheDirectory: "cache"),
            platform);

        Assert.Equal(
            Path.GetFullPath("relative cfg", platform.CurrentDirectoryValue),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.GetFullPath("dÃ¥ta", platform.CurrentDirectoryValue),
            paths.DataDirectory);
        Assert.Equal(
            Path.GetFullPath("cache", platform.CurrentDirectoryValue),
            paths.CacheDirectory);
    }

    [Fact]
    public void WindowsUsesRoamingForConfigAndLocalForDataAndCache()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-windows"));
        var platform = new FixturePlatform(isWindows: true)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            ApplicationData = Path.Combine(root, "AppData", "Roaming"),
            LocalApplicationData = Path.Combine(root, "AppData", "Local"),
            UserProfile = Path.Combine(root, "Users", "bot"),
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(),
            platform);

        Assert.EndsWith(
            Path.Combine("AppData", "Roaming", "acdream"),
            paths.ConfigDirectory);
        Assert.EndsWith(
            Path.Combine("AppData", "Local", "acdream"),
            paths.DataDirectory);
        Assert.EndsWith(
            Path.Combine("AppData", "Local", "acdream", "cache"),
            paths.CacheDirectory);
    }

    private sealed class FixturePlatform(bool isWindows)
        : IHeadlessPlatformEnvironment
    {
        public bool IsWindows { get; } = isWindows;
        public string CurrentDirectoryValue { get; init; } =
            Environment.CurrentDirectory;
        public string CurrentDirectory => CurrentDirectoryValue;
        public string UserProfile { get; init; } =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string ApplicationData { get; init; } =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public string LocalApplicationData { get; init; } =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public Dictionary<string, string> Variables { get; } =
            new(StringComparer.Ordinal);

        public string? GetEnvironmentVariable(string name) =>
            Variables.TryGetValue(name, out string? value)
                ? value
                : null;

        public string GetFolderPath(Environment.SpecialFolder folder) =>
            folder switch
            {
                System.Environment.SpecialFolder.UserProfile => UserProfile,
                System.Environment.SpecialFolder.ApplicationData =>
                    ApplicationData,
                System.Environment.SpecialFolder.LocalApplicationData =>
                    LocalApplicationData,
                _ => string.Empty,
            };
    }
}
