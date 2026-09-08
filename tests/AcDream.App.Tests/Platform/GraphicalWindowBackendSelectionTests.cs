using AcDream.App.Platform;

namespace AcDream.App.Tests.Platform;

public sealed class GraphicalWindowBackendSelectionTests
{
    [Fact]
    public void WindowsAlwaysSelectsWin32()
    {
        GraphicalWindowBackendSelection selection =
            GraphicalWindowBackendSelection.Resolve(
                GraphicalHostOperatingSystem.Windows,
                _ => "wayland");

        Assert.Equal(
            GraphicalDisplayProtocol.Windows,
            selection.RequestedProtocol);
    }

    [Theory]
    [InlineData("auto", "Automatic")]
    [InlineData("x11", "X11")]
    [InlineData("wayland", "Wayland")]
    [InlineData(" WAYLAND ", "Wayland")]
    public void ExplicitLinuxProtocolWins(
        string configured,
        string expected)
    {
        var environment = Environment(
            (GraphicalWindowBackendSelection.EnvironmentVariable, configured),
            ("XDG_SESSION_TYPE", "x11"),
            ("DISPLAY", ":0"));

        GraphicalWindowBackendSelection selection =
            GraphicalWindowBackendSelection.Resolve(
                GraphicalHostOperatingSystem.Linux,
                environment);

        Assert.Equal(expected, selection.RequestedProtocol.ToString());
    }

    [Fact]
    public void InvalidExplicitLinuxProtocolIsActionable()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => GraphicalWindowBackendSelection.Resolve(
                GraphicalHostOperatingSystem.Linux,
                Environment(
                    (GraphicalWindowBackendSelection.EnvironmentVariable,
                        "mir"))));

        Assert.Contains("auto, x11, or wayland", error.Message);
        Assert.Contains("mir", error.Message);
    }

    [Theory]
    [MemberData(nameof(AutomaticLinuxCases))]
    public void LinuxEnvironmentSelectsExpectedProtocol(
        (string Name, string? Value)[] variables,
        string expected)
    {
        GraphicalWindowBackendSelection selection =
            GraphicalWindowBackendSelection.Resolve(
                GraphicalHostOperatingSystem.Linux,
                Environment(variables));

        Assert.Equal(expected, selection.RequestedProtocol.ToString());
    }

    public static TheoryData<
        (string Name, string? Value)[],
        string> AutomaticLinuxCases => new()
    {
        {
            [
                ("XDG_SESSION_TYPE", "wayland"),
                ("WAYLAND_DISPLAY", "wayland-0"),
                ("DISPLAY", ":0"),
            ],
            "Wayland"
        },
        {
            [
                ("XDG_SESSION_TYPE", "x11"),
                ("WAYLAND_DISPLAY", "wayland-0"),
                ("DISPLAY", ":0"),
            ],
            "X11"
        },
        {
            [("WAYLAND_DISPLAY", "wayland-0")],
            "Wayland"
        },
        {
            [],
            "Automatic"
        },
    };

    private static Func<string, string?> Environment(
        params (string Name, string? Value)[] variables)
    {
        var values = variables.ToDictionary(
            pair => pair.Name,
            pair => pair.Value,
            StringComparer.Ordinal);
        return name => values.GetValueOrDefault(name);
    }
}
