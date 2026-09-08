using AcDream.Headless.Configuration;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Headless.Tests;

public sealed class LauncherHeadlessCommandLineContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-headless-cli-contract",
        Guid.NewGuid().ToString("N"));

    public LauncherHeadlessCommandLineContractTests()
    {
        Directory.CreateDirectory(AppDirectory);
        string suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        CreateStubExecutable(Path.Combine(AppDirectory, "AcDream.App" + suffix));
        CreateStubExecutable(Path.Combine(AppDirectory, "acdream-headless" + suffix));
    }

    private static void CreateStubExecutable(string path)
    {
        File.WriteAllText(path, "stub");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ProbeArgumentsParseAsAHeadlessRunCommand()
    {
        LauncherProcessSpec spec = ExecutableSet().CreateProbeSpec(ConfigPath);

        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(spec.Arguments);

        Assert.Equal("run", parsed.Command);
        Assert.Equal(ConfigPath, parsed.ConfigurationPath);
    }

    [Fact]
    public void HeadlessPlayArgumentsParseAsAHeadlessRunCommand()
    {
        LauncherProcessSpec spec = ExecutableSet()
            .CreatePlaySpec(LaunchMode.Headless, ConfigPath);

        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(spec.Arguments);

        Assert.Equal("run", parsed.Command);
        Assert.Equal(ConfigPath, parsed.ConfigurationPath);
    }

    [Theory]
    [InlineData(LaunchMode.Gui)]
    [InlineData(LaunchMode.GuiSelect)]
    public void GraphicalArgumentsAreNotAHeadlessCommandLine(LaunchMode mode)
    {
        LauncherProcessSpec spec = ExecutableSet().CreatePlaySpec(mode, ConfigPath);

        Assert.Equal(["--session-config", ConfigPath], spec.Arguments);
        Assert.Throws<HeadlessCommandLineException>(
            () => HeadlessCommandLine.Parse(spec.Arguments));
    }

    private string AppDirectory => Path.Combine(_root, "app");

    private string ConfigPath => Path.Combine(_root, "session.json");

    private LauncherExecutableSet ExecutableSet() =>
        LauncherExecutableSet.FromDirectory(AppDirectory);
}
