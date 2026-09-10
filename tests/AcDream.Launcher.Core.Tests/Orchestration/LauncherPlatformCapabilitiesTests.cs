using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Orchestration;

public sealed class LauncherPlatformCapabilitiesTests
{
    [Fact]
    public void MacOSProvidesBothLauncherModes()
    {
        LauncherPlatformCapabilities capabilities =
            LauncherPlatformCapabilities.ForOperatingSystem(
                isWindows: false,
                isLinux: false,
                isMacOS: true);

        Assert.True(capabilities.IsMacOS);
        Assert.False(capabilities.IsWindows);
        Assert.False(capabilities.IsLinux);
        Assert.Equal("macOS", capabilities.PlatformName);
        Assert.True(capabilities.CanLaunchGraphicalClient);
        Assert.True(capabilities.CanRunHeadless);
        Assert.True(capabilities.ForLaunchMode(LaunchMode.Gui).IsAvailable);
        Assert.True(capabilities.ForLaunchMode(LaunchMode.GuiSelect).IsAvailable);
        Assert.True(capabilities.ForLaunchMode(LaunchMode.Headless).IsAvailable);
    }
}
