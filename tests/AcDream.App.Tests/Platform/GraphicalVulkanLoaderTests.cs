using System.Runtime.InteropServices;
using AcDream.App.Platform;

namespace AcDream.App.Tests.Platform;

public sealed partial class GraphicalVulkanLoaderTests
{
    [Fact]
    public void MissingPackagedRuntimeRetainsSystemFallback()
    {
        PackagedVulkanLayout? layout =
            GraphicalVulkanLoader.ResolvePackagedLayout(
                Path.GetTempPath(),
                _ => false);

        Assert.Null(layout);
    }

    [Fact]
    public void CompletePackagedRuntimeResolvesFlatClientLayout()
    {
        string applicationDirectory = Path.Combine(
            Path.GetTempPath(),
            "acdream-client");
        string[] expected =
        [
            Path.Combine(
                applicationDirectory,
                "Frameworks",
                "libvulkan.1.dylib"),
            Path.Combine(
                applicationDirectory,
                "Frameworks",
                "libMoltenVK.dylib"),
            Path.Combine(
                applicationDirectory,
                "Resources",
                "vulkan",
                "icd.d",
                "MoltenVK_icd.json"),
        ];
        var existing = new HashSet<string>(
            expected,
            StringComparer.OrdinalIgnoreCase);

        PackagedVulkanLayout? layout =
            GraphicalVulkanLoader.ResolvePackagedLayout(
                applicationDirectory,
                existing.Contains);

        Assert.NotNull(layout);
        Assert.Equal(expected[0], layout.LoaderPath);
        Assert.Equal(expected[1], layout.DriverLibraryPath);
        Assert.Equal(expected[2], layout.DriverManifestPath);
    }

    [Fact]
    public void PartialPackagedRuntimeFailsBeforeLoadingNativeCode()
    {
        string applicationDirectory = Path.Combine(
            Path.GetTempPath(),
            "acdream-client");
        string loader = Path.Combine(
            applicationDirectory,
            "Frameworks",
            "libvulkan.1.dylib");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => GraphicalVulkanLoader.ResolvePackagedLayout(
                applicationDirectory,
                path => string.Equals(
                    path,
                    loader,
                    StringComparison.OrdinalIgnoreCase)));

        Assert.Contains("libMoltenVK.dylib", error.Message);
        Assert.Contains("MoltenVK_icd.json", error.Message);
    }

    [Fact]
    [Trait("Lane", "MacOS")]
    public void PublishedEnvironmentVariableIsVisibleToNativeGetenv()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Lane=MacOS requires a native macOS host.");

        string name = "ACDREAM_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            GraphicalVulkanLoader.PublishEnvironmentVariable(name, "/tmp/probe");

            string? native = Marshal.PtrToStringUTF8(Getenv(name));

            Assert.Equal("/tmp/probe", native);

            GraphicalVulkanLoader.PublishEnvironmentVariable(name, "/tmp/probe-2");

            Assert.Equal("/tmp/probe-2", Marshal.PtrToStringUTF8(Getenv(name)));

            GraphicalVulkanLoader.PublishEnvironmentVariable(name, null);

            Assert.Equal(nint.Zero, Getenv(name));
        }
        finally
        {
            GraphicalVulkanLoader.PublishEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void EnvironmentSetEnvironmentVariableOnlyAppearsInsidePublishEnvironmentVariable()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "AcDream.App",
            "Platform",
            "GraphicalVulkanLoader.cs"));

        int occurrences = source.Split(
            "Environment.SetEnvironmentVariable",
            StringSplitOptions.None).Length - 1;

        Assert.True(
            occurrences == 1,
            "Environment.SetEnvironmentVariable must appear exactly once, " +
            "inside PublishEnvironmentVariable; route all other callers through it.");
    }

    [Fact]
    public void PublishEnvironmentVariableRequiresMacOS()
    {
        string name = "ACDREAM_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                GraphicalVulkanLoader.PublishEnvironmentVariable(name, null);
            }
            else
            {
                Assert.Throws<PlatformNotSupportedException>(
                    () => GraphicalVulkanLoader.PublishEnvironmentVariable(name, "/tmp/probe"));
            }

            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [LibraryImport("libSystem.dylib", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Getenv(string name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
