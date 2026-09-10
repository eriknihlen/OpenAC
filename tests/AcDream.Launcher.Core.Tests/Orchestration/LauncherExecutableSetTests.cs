using AcDream.Launcher.Core;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Orchestration;

public sealed class LauncherExecutableSetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-launcher-layout-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FromDirectoryResolvesThePublishedCoDeploymentLayout()
    {
        Directory.CreateDirectory(_root);
        string suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string graphical = Path.Combine(
            _root,
            PayloadExecutableNames.GraphicalHostForCurrentOs() + suffix);
        string headless = Path.Combine(_root, "acdream-headless" + suffix);
        File.WriteAllText(graphical, string.Empty);
        File.WriteAllText(headless, string.Empty);
        MakeExecutableOnUnix(graphical);
        MakeExecutableOnUnix(headless);

        LauncherExecutableSet set = LauncherExecutableSet.FromDirectory(_root);

        Assert.Equal(Path.GetFullPath(_root), set.WorkingDirectory);
        Assert.Equal(graphical, set.GraphicalHostPath);
        Assert.Equal(headless, set.HeadlessHostPath);
        Assert.True(set.GetAvailability(LaunchMode.Gui).IsAvailable);
        Assert.True(set.GetAvailability(LaunchMode.GuiSelect).IsAvailable);
        Assert.True(set.GetAvailability(LaunchMode.Headless).IsAvailable);
        Assert.Equal(
            graphical,
            set.CreatePlaySpec(LaunchMode.GuiSelect, "session.json").ExecutablePath);
        Assert.Equal(
            headless,
            set.CreateProbeSpec("session.json").ExecutablePath);
        Assert.False(
            set.CreatePlaySpec(LaunchMode.Gui, "session.json")
                .SupportsConsoleGracefulStop);
        Assert.False(
            set.CreatePlaySpec(LaunchMode.GuiSelect, "session.json")
                .SupportsConsoleGracefulStop);
        Assert.True(
            set.CreatePlaySpec(LaunchMode.Headless, "session.json")
                .SupportsConsoleGracefulStop);
        Assert.True(set.CreateProbeSpec("session.json").SupportsConsoleGracefulStop);
    }

    [Fact]
    public void MissingPublishedHostsHaveSpecificUnavailableReasons()
    {
        Directory.CreateDirectory(_root);
        LauncherExecutableSet set = LauncherExecutableSet.FromDirectory(_root);

        LauncherCapability gui = set.GetAvailability(LaunchMode.Gui);
        LauncherCapability probe = set.GetAvailability(LaunchMode.Headless);

        Assert.False(gui.IsAvailable);
        Assert.Contains("graphical client", gui.Reason, StringComparison.Ordinal);
        Assert.Contains(set.GraphicalHostPath, gui.Reason, StringComparison.Ordinal);
        Assert.False(probe.IsAvailable);
        Assert.Contains("headless host", probe.Reason, StringComparison.Ordinal);
        Assert.Contains(set.HeadlessHostPath, probe.Reason, StringComparison.Ordinal);
        Assert.Throws<LauncherOperationException>(() =>
            set.CreatePlaySpec(LaunchMode.Gui, "session.json"));
        Assert.Throws<LauncherOperationException>(() =>
            set.CreateProbeSpec("session.json"));
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void UnixRequiresExecutePermissionForBothCoDeployedHosts()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        }

        Directory.CreateDirectory(_root);
        string graphical = Path.Combine(
            _root,
            PayloadExecutableNames.GraphicalHostForCurrentOs());
        string headless = Path.Combine(_root, "acdream-headless");
        File.WriteAllText(graphical, string.Empty);
        File.WriteAllText(headless, string.Empty);
        UnixFileMode notExecutable = UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(graphical, notExecutable);
        File.SetUnixFileMode(headless, notExecutable);
        LauncherExecutableSet set = LauncherExecutableSet.FromDirectory(_root);

        LauncherCapability gui = set.GetAvailability(LaunchMode.Gui);
        LauncherCapability headlessCapability =
            set.GetAvailability(LaunchMode.Headless);

        Assert.False(gui.IsAvailable);
        Assert.Contains("not executable", gui.Reason, StringComparison.Ordinal);
        Assert.Contains("chmod +x", gui.Reason, StringComparison.Ordinal);
        Assert.False(headlessCapability.IsAvailable);
        Assert.Contains("not executable", headlessCapability.Reason, StringComparison.Ordinal);
        Assert.Throws<LauncherOperationException>(() =>
            set.CreatePlaySpec(LaunchMode.GuiSelect, "session.json"));
        Assert.Throws<LauncherOperationException>(() =>
            set.CreateProbeSpec("session.json"));

        MakeExecutableOnUnix(graphical);
        MakeExecutableOnUnix(headless);

        Assert.True(set.GetAvailability(LaunchMode.GuiSelect).IsAvailable);
        Assert.True(set.GetAvailability(LaunchMode.Headless).IsAvailable);
    }

    [Fact]
    public void AvailabilityRequiresExecutePermissionOnlyOnUnix()
    {
        var set = new LauncherExecutableSet(
            "graphical.exe",
            "headless.exe",
            fileExists: _ => true,
            hasUnixExecutePermission: _ => false);

        Assert.Equal(
            !LauncherOperatingSystem.IsUnix,
            set.GetAvailability(LaunchMode.Gui).IsAvailable);
        Assert.Equal(
            !LauncherOperatingSystem.IsUnix,
            set.GetAvailability(LaunchMode.Headless).IsAvailable);
    }

    private static void MakeExecutableOnUnix(string path)
    {
        if (LauncherOperatingSystem.IsUnix)
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }
    }
}
