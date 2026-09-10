using AcDream.App.Platform;

namespace AcDream.App.Tests.Platform;

public sealed class GraphicalVulkanLoaderTests
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
}
