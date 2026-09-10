using AcDream.App.Platform;

namespace AcDream.App.Tests.Platform;

public sealed class GraphicalHostPlatformServicesTests
{
    [Fact]
    public void CurrentPlatformOwnsPathsPacingAndNativeClosure()
    {
        GraphicalHostPlatformServices platform =
            GraphicalHostPlatformServices.Resolve();

        Assert.NotNull(platform.Paths);
        Assert.NotNull(platform.FramePacingWaiters);
        Assert.Equal(2, platform.NativeDependencies.Count);
        Assert.All(
            platform.NativeDependencies,
            dependency =>
            {
                Assert.False(string.IsNullOrWhiteSpace(dependency.Feature));
                Assert.False(string.IsNullOrWhiteSpace(
                    dependency.PublishedFileName));
            });

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                GraphicalHostOperatingSystem.Windows,
                platform.OperatingSystem);
            Assert.StartsWith(
                "win-",
                platform.RuntimeIdentifier,
                StringComparison.Ordinal);
            Assert.Contains(
                platform.NativeDependencies,
                dependency =>
                    dependency.PublishedFileName == "glfw3.dll");
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                GraphicalHostOperatingSystem.Linux,
                platform.OperatingSystem);
            Assert.Equal("linux-x64", platform.RuntimeIdentifier);
            Assert.Contains(
                platform.NativeDependencies,
                dependency =>
                    dependency.PublishedFileName == "libglfw.so.3");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(
                GraphicalHostOperatingSystem.MacOS,
                platform.OperatingSystem);
            Assert.StartsWith(
                "osx-",
                platform.RuntimeIdentifier,
                StringComparison.Ordinal);
            Assert.Contains(
                platform.NativeDependencies,
                dependency =>
                    dependency.PublishedFileName == "libglfw.3.dylib");
            return;
        }

        throw new PlatformNotSupportedException();
    }
}
