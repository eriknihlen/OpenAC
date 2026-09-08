using System;
using System.Text.Json;
using AcDream.App.Platform;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanCapabilityReportContractTests
{
    private static VulkanCapabilityRecord LavapipeShapedRecord(
        string? forcedUnsupportedFeature = null)
    {
        var record = new VulkanCapabilityRecord(
            DateTimeOffset.UnixEpoch,
            "linux-x64",
            GraphicalHostOperatingSystem.Linux,
            GraphicalDisplayProtocol.X11,
            GraphicalDisplayProtocol.X11,
            "Vulkan 1.4.0",
            "Vulkan 1.4.305",
            VulkanApiVersion.Make(1, 4, 305),
            "llvmpipe (LLVM 19.1.7, 256 bits)",
            "vendor 0x10005, device 0x0, driver 0.0.1",
            PhysicalDeviceType.Cpu,
            0,
            "automatic",
            RequestedDeviceOverride: null,
            ForcedUnsupportedFeature: null,
            AvailableDevices: [],
            InstanceExtensions: ["VK_KHR_surface", "VK_KHR_xlib_surface"],
            DeviceExtensions: ["VK_KHR_swapchain"],
            GraphicsQueueFamily: 0,
            PresentQueueFamily: 0,
            VulkanDeviceFeatureSupport.Complete,
            VulkanDeviceLimitSupport.Complete,
            VulkanFormatSupport.Complete,
            new VulkanSurfaceSupport(
                PresentSupported: true,
                SelectedFormat: Format.B8G8R8A8Unorm,
                SelectedColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr,
                SelectedPresentMode: PresentModeKHR.FifoKhr,
                SelectedImageCount: 3,
                SelectedWidth: 1280,
                SelectedHeight: 720,
                SupportsTransferSource: true,
                AvailableFormats: [Format.B8G8R8A8Unorm],
                AvailablePresentModes: [PresentModeKHR.FifoKhr]),
            new VulkanFunctionProbeResult(
                DeviceCreation: true,
                DescriptorIndexingLayout: true,
                PushConstantLayout: true,
                DynamicRenderingClear: true,
                TimelineSemaphoreWait: true,
                HostQueryReset: true,
                OffscreenReadback: true,
                Failures: []),
            SupportFailures: []);

        record = VulkanCapabilityRequirements.Reevaluate(record);
        return VulkanCapabilityRequirements.ApplyForcedUnsupported(
            record,
            forcedUnsupportedFeature);
    }

    private static JsonElement Report(string? forcedUnsupportedFeature = null)
        => JsonDocument
            .Parse(
                VulkanCapabilityReportWriter.Serialize(
                    LavapipeShapedRecord(forcedUnsupportedFeature)))
            .RootElement;

    [Fact]
    public void ThePassingRunCarriesEveryFieldTheCiJobReads()
    {
        JsonElement report = Report();

        Assert.Equal(0, report.GetProperty("SupportFailures").GetArrayLength());
        Assert.Equal("X11", report.GetProperty("ActiveDisplayProtocol").GetString());
        Assert.Equal("Cpu", report.GetProperty("DeviceType").GetString());

        JsonElement probe = report.GetProperty("FunctionProbe");
        Assert.Equal(0, probe.GetProperty("Failures").GetArrayLength());
        Assert.True(probe.GetProperty("DeviceCreation").GetBoolean());
        Assert.True(probe.GetProperty("OffscreenReadback").GetBoolean());

        // Present and human-readable: the job prints these to the log so a
        // failure elsewhere still records which device ran.
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("DeviceName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("DeviceApiVersion").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("DriverInfo").GetString()));
    }

    [Fact]
    public void ThePackedApiVersionUnpacksTheWayTheCiJobUnpacksIt()
    {
        uint packed = Report().GetProperty("DeviceApiVersionPacked").GetUInt32();

        uint major = packed / 4194304;
        uint minor = packed % 4194304 / 4096;

        Assert.Equal(VulkanApiVersion.Major(packed), major);
        Assert.Equal(VulkanApiVersion.Minor(packed), minor);
        Assert.True(major > 1 || (major == 1 && minor >= 3));
    }

    /// <summary>
    /// The forced-unsupported run's assertions, one for one with the "Verify the
    /// forced-unsupported gate exits 4" step. The feature name is the literal
    /// the workflow passes.
    /// </summary>
    [Fact]
    public void TheForcedUnsupportedRunCarriesEveryFieldTheCiJobReads()
    {
        JsonElement report = Report("timelineSemaphore");

        Assert.Equal(
            "timelineSemaphore",
            report.GetProperty("ForcedUnsupportedFeature").GetString());
        Assert.False(
            report.GetProperty("Features").GetProperty("TimelineSemaphore").GetBoolean());

        JsonElement failures = report.GetProperty("SupportFailures");
        Assert.True(failures.GetArrayLength() > 0);
        // The job matches on the feature name inside the failure sentence.
        Assert.Contains(
            failures.EnumerateArray(),
            failure => failure.GetString()?.Contains(
                "timelineSemaphore",
                StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The operator-facing refusal names the report path. The job greps the log
    /// for the file name, because a gate that refuses without saying where the
    /// evidence is has failed at the only job it has on a machine nobody owns.
    /// </summary>
    [Fact]
    public void TheRefusalMessageNamesTheReportTheJobUploads()
    {
        string message = VulkanCapabilityGuard.FormatUnsupportedMessage(
            LavapipeShapedRecord("timelineSemaphore"),
            "/tmp/diagnostics/graphical-capabilities-vulkan.json");

        Assert.Contains("graphical-capabilities-vulkan.json", message, StringComparison.Ordinal);
        Assert.Contains("timelineSemaphore", message, StringComparison.Ordinal);
    }

}
