using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AcDream.App.Platform;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanCapabilityGateTests
{
    private static VulkanCapabilityRecord SupportedRecord(
        VulkanDeviceFeatureSupport? features = null,
        VulkanDeviceLimitSupport? limits = null,
        VulkanFormatSupport? formats = null,
        VulkanSurfaceSupport? surface = null,
        VulkanFunctionProbeResult? probe = null,
        uint? apiVersion = null)
    {
        var record = new VulkanCapabilityRecord(
            DateTimeOffset.UnixEpoch,
            "win-x64",
            GraphicalHostOperatingSystem.Windows,
            GraphicalDisplayProtocol.Windows,
            GraphicalDisplayProtocol.Windows,
            "Vulkan 1.3.0",
            "Vulkan 1.3.280",
            apiVersion ?? VulkanApiVersion.Make(1, 3, 280),
            "AMD Radeon RX 9070 XT",
            "vendor 0x1002, device 0x7550, driver 2.0.0 (raw 0x00800000)",
            PhysicalDeviceType.DiscreteGpu,
            0,
            "automatic",
            RequestedDeviceOverride: null,
            ForcedUnsupportedFeature: null,
            AvailableDevices: [],
            InstanceExtensions: ["VK_KHR_surface", "VK_KHR_win32_surface"],
            DeviceExtensions: ["VK_KHR_swapchain"],
            GraphicsQueueFamily: 0,
            PresentQueueFamily: 0,
            features ?? VulkanDeviceFeatureSupport.Complete,
            limits ?? VulkanDeviceLimitSupport.Complete,
            formats ?? VulkanFormatSupport.Complete,
            surface ?? SupportedSurface(),
            probe ?? PassingProbe(),
            SupportFailures: []);
        return VulkanCapabilityRequirements.Reevaluate(record);
    }

    private static VulkanSurfaceSupport SupportedSurface() => new(
        PresentSupported: true,
        SelectedFormat: Format.B8G8R8A8Unorm,
        SelectedColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr,
        SelectedPresentMode: PresentModeKHR.FifoKhr,
        SelectedImageCount: 3,
        SelectedWidth: 1280,
        SelectedHeight: 720,
        SupportsTransferSource: true,
        AvailableFormats: [Format.B8G8R8A8Unorm],
        AvailablePresentModes: [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr]);

    private static VulkanFunctionProbeResult PassingProbe() => new(
        DeviceCreation: true,
        DescriptorIndexingLayout: true,
        PushConstantLayout: true,
        DynamicRenderingClear: true,
        TimelineSemaphoreWait: true,
        HostQueryReset: true,
        OffscreenReadback: true,
        Failures: []);

    [Fact]
    public void ACompleteDeviceIsAccepted()
    {
        VulkanCapabilityRecord record = SupportedRecord();

        Assert.Empty(record.SupportFailures);
        Assert.True(record.IsSupported);
    }

    [Fact]
    public void OptionalAtmosphericFormatsDoNotRejectTheRetailRenderer()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            formats: VulkanFormatSupport.Complete with
            {
                DepthStencilSampled = false,
                Rgba16FloatColorAttachment = false,
                Rgba16FloatSampled = false,
                Rgba16FloatLinearFilter = false,
                MaxRgba16FloatSampleCount = 8,
            });

        Assert.Empty(record.SupportFailures);
        GpuCapabilityRecord projected = record.ToGpuCapabilityRecord();
        Assert.False(projected.SupportsRgba16FloatRenderTargets);
        Assert.Equal(0u, projected.MaxRgba16FloatSampleCount);
        Assert.False(projected.SupportsSampledDepth);
    }

    [Fact]
    public void EveryRequiredFeatureIsIndividuallyEnforced()
    {
        IEnumerable<string> featureNames = typeof(VulkanDeviceFeatureSupport)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(bool))
            .Where(property => property.Name != nameof(VulkanDeviceFeatureSupport.Multiview))
            .Select(property => property.Name);

        foreach (string name in featureNames)
        {
            VulkanDeviceFeatureSupport? reduced =
                VulkanDeviceFeatureSupport.Complete.Without(name);
            Assert.NotNull(reduced);

            VulkanCapabilityRecord record = SupportedRecord(features: reduced);
            Assert.False(
                record.IsSupported,
                $"clearing {name} must reject the device.");
            Assert.Single(record.SupportFailures);
        }
    }

    [Fact]
    public void AnUnknownFeatureNameIsNotSilentlyIgnored()
    {
        Assert.Null(VulkanDeviceFeatureSupport.Complete.Without("NotAVulkanFeature"));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 0)]
    [InlineData(0, 9)]
    public void ADeviceBelowVulkan13IsRejected(uint major, uint minor)
    {
        VulkanCapabilityRecord record = SupportedRecord(
            apiVersion: VulkanApiVersion.Make(major, minor, 0));

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("Vulkan 1.3 is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Vulkan14IsAccepted()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            apiVersion: VulkanApiVersion.Make(1, 4, 0));

        Assert.True(record.IsSupported);
    }

    [Fact]
    public void PushConstantsBelowThePinnedBlockAreRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxPushConstantsSize = GpuBindingModel.PushConstantBytes - 1,
            });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("push-constant bytes are required", StringComparison.Ordinal));
    }

    [Fact]
    public void FewerThanEightClipDistancesAreRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with { MaxClipDistances = 6 });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("clip distances are required", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sets 0 (storage), 1 (uniform) and 2 (texture table) are bound at once, so
    /// two bound sets is not enough. This is the limit the §3.4 binding model
    /// silently assumes.
    /// </summary>
    [Fact]
    public void FewerThanThreeBoundDescriptorSetsAreRejected()
    {
        Assert.Equal(3u, VulkanCapabilityRequirements.VulkanDescriptorSetCount);

        VulkanCapabilityRecord record = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with { MaxBoundDescriptorSets = 2 });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains(
                "simultaneously bound descriptor sets are required",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TheDynamicStorageBindingSplitFitsVulkansGuaranteedMinimum()
    {
        const uint VulkanGuaranteedMinimum = 4;
        Assert.True(
            VulkanPipelineLayouts.DynamicStorageBindingCount <= VulkanGuaranteedMinimum,
            $"set 0 declares {VulkanPipelineLayouts.DynamicStorageBindingCount} dynamic storage " +
            $"bindings; Vulkan only guarantees {VulkanGuaranteedMinimum}.");

        // The bindings that stay dynamic are the ones a renderer feeds from the
        // per-frame ring; the rest point at long-lived buffers bound once per
        // pass and buy nothing from a dynamic offset.
        Assert.True(VulkanPipelineLayouts.IsDynamicStorageBinding(GpuBindingModel.StorageInstances));
        Assert.True(VulkanPipelineLayouts.IsDynamicStorageBinding(GpuBindingModel.StorageBatches));
        Assert.False(VulkanPipelineLayouts.IsDynamicStorageBinding(GpuBindingModel.StorageGlobalLights));
        Assert.False(VulkanPipelineLayouts.IsDynamicStorageBinding(GpuBindingModel.StorageClipRegions));

        Assert.False(VulkanPipelineLayouts.IsDynamicStorageBinding(
            GpuBindingModel.StorageInstanceDetailCategory));
    }

    [Fact]
    public void ADeviceWithTooFewDynamicBufferDescriptorsIsRejected()
    {
        VulkanCapabilityRecord storage = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxDescriptorSetStorageBuffersDynamic =
                    VulkanPipelineLayouts.DynamicStorageBindingCount - 1,
            });
        VulkanCapabilityRecord uniform = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxDescriptorSetUniformBuffersDynamic =
                    VulkanFrameBindings.DynamicUniformBindingCount - 1,
            });

        Assert.Contains(
            storage.SupportFailures,
            failure => failure.Contains("dynamic storage bindings", StringComparison.Ordinal));
        Assert.Contains(
            uniform.SupportFailures,
            failure => failure.Contains("dynamic uniform bindings", StringComparison.Ordinal));
        Assert.Empty(SupportedRecord().SupportFailures);
    }

    [Fact]
    public void MultiviewIsProjectedButDoesNotRejectTheAuthoritativeRenderer()
    {
        VulkanDeviceFeatureSupport reduced =
            VulkanDeviceFeatureSupport.Complete with { Multiview = false };
        VulkanCapabilityRecord record = SupportedRecord(features: reduced);
        Assert.True(record.IsSupported);
        Assert.False(record.ToGpuCapabilityRecord().SupportsMultiview);
    }

    [Fact]
    public void ADeviceWithTooFewTotalStorageDescriptorsIsRejectedPerSetAndPerStage()
    {
        VulkanCapabilityRecord perSet = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxDescriptorSetStorageBuffers =
                    GpuBindingModel.StorageBindingCount - 1,
            });
        VulkanCapabilityRecord perStage = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxPerStageDescriptorStorageBuffers =
                    GpuBindingModel.StorageBindingCount - 1,
            });

        Assert.Contains(
            perSet.SupportFailures,
            failure => failure.Contains("total storage bindings", StringComparison.Ordinal));
        Assert.Contains(
            perStage.SupportFailures,
            failure => failure.Contains("each shader stage", StringComparison.Ordinal));
        Assert.Empty(SupportedRecord().SupportFailures);
    }

    [Fact]
    public void ATextureTableSmallerThanTheCapacityIsRejectedPerSetAndPerStage()
    {
        VulkanCapabilityRecord perSet = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxDescriptorSetUpdateAfterBindSampledImages =
                    GpuBindingModel.TextureTableCapacity - 1,
            });
        VulkanCapabilityRecord perStage = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MaxPerStageDescriptorUpdateAfterBindSampledImages =
                    GpuBindingModel.TextureTableCapacity - 1,
            });

        Assert.Contains(
            perSet.SupportFailures,
            failure => failure.Contains("update-after-bind sampled images", StringComparison.Ordinal));
        Assert.Contains(
            perStage.SupportFailures,
            failure => failure.Contains("fragment stage", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingGraphicsTimestampsAreRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with { TimestampComputeAndGraphics = false });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("timestamps are required", StringComparison.Ordinal));
    }

    [Fact]
    public void ASurfaceWithoutTheUnormFormatIsRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            formats: VulkanFormatSupport.Complete with { SwapchainUnormFormat = false });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("B8G8R8A8_UNORM", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingDepthStencilFormatIsRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            formats: VulkanFormatSupport.Complete with { DepthStencilFormat = Format.Undefined });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("depth+stencil format", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void MissingAnyBcBlockIsRejected(bool bc1, bool bc2, bool bc3)
    {
        VulkanCapabilityRecord record = SupportedRecord(
            formats: VulkanFormatSupport.Complete with
            {
                Bc1Sampled = bc1,
                Bc2Sampled = bc2,
                Bc3Sampled = bc3,
            });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("BC1, BC2 and BC3", StringComparison.Ordinal));
    }

    [Fact]
    public void ASurfaceWithoutTransferSourceUsageIsRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            surface: SupportedSurface() with { SupportsTransferSource = false });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("TRANSFER_SRC", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeviceThatCannotPresentIsRejected()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            surface: SupportedSurface() with { PresentSupported = false });

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains("cannot present", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeadlessCaptureWithNoSurfaceIsAccepted()
    {
        VulkanCapabilityRecord record = SupportedRecord(surface: null);

        Assert.True(record.IsSupported);
    }

    [Fact]
    public void ProbeFailuresArePrefixedAndReplaceTheIndividualChecks()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            probe: VulkanFunctionProbeResult.NotRun);

        Assert.Contains(
            record.SupportFailures,
            failure => failure.StartsWith("Vulkan device probe:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            record.SupportFailures,
            failure => failure.Contains("probe did not pass", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DescriptorIndexingLayout", "descriptor-indexing layout probe")]
    [InlineData("PushConstantLayout", "push-constant pipeline-layout probe")]
    [InlineData("DynamicRenderingClear", "dynamic-rendering clear probe")]
    [InlineData("TimelineSemaphoreWait", "timeline-semaphore wait probe")]
    [InlineData("HostQueryReset", "host query-reset probe")]
    [InlineData("OffscreenReadback", "offscreen readback probe")]
    public void EachActiveProbeStepIsIndividuallyEnforced(string step, string expected)
    {
        VulkanFunctionProbeResult probe = step switch
        {
            "DescriptorIndexingLayout" => PassingProbe() with { DescriptorIndexingLayout = false },
            "PushConstantLayout" => PassingProbe() with { PushConstantLayout = false },
            "DynamicRenderingClear" => PassingProbe() with { DynamicRenderingClear = false },
            "TimelineSemaphoreWait" => PassingProbe() with { TimelineSemaphoreWait = false },
            "HostQueryReset" => PassingProbe() with { HostQueryReset = false },
            _ => PassingProbe() with { OffscreenReadback = false },
        };

        VulkanCapabilityRecord record = SupportedRecord(probe: probe);

        Assert.Contains(
            record.SupportFailures,
            failure => failure.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TheGateDoesNotRequireSrgbAnything()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            features: VulkanDeviceFeatureSupport.Complete.Without("MultiDrawIndirect")!);

        Assert.DoesNotContain(
            record.SupportFailures,
            failure => failure.Contains("sRGB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheForcedUnsupportedKnobRejectsTheNamedFeature()
    {
        VulkanCapabilityRecord forced = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            SupportedRecord(),
            "timelineSemaphore");

        Assert.False(forced.IsSupported);
        Assert.Equal("timelineSemaphore", forced.ForcedUnsupportedFeature);
        Assert.Contains(
            forced.SupportFailures,
            failure => failure.Contains("timelineSemaphore is required", StringComparison.Ordinal));
    }

    [Fact]
    public void TheForcedUnsupportedKnobIgnoresAnUnsetValue()
    {
        VulkanCapabilityRecord unchanged = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            SupportedRecord(),
            featureName: null);

        Assert.True(unchanged.IsSupported);
        Assert.Null(unchanged.ForcedUnsupportedFeature);
    }

    /// <summary>
    /// A knob that names a nonexistent feature must fail loudly. A silent no-op
    /// would report a pass the operator never actually exercised.
    /// </summary>
    [Fact]
    public void TheForcedUnsupportedKnobFailsOnAnUnknownName()
    {
        VulkanCapabilityRecord forced = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            SupportedRecord(),
            "TeapotShading");

        Assert.False(forced.IsSupported);
        Assert.Contains(
            forced.SupportFailures,
            failure => failure.Contains(
                "which is not a required Vulkan feature",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TheUnsupportedMessageNamesThePlatformDeviceFailuresAndReport()
    {
        VulkanCapabilityRecord record = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            SupportedRecord(),
            "DynamicRendering");

        string message = VulkanCapabilityGuard.FormatUnsupportedMessage(
            record,
            "artifacts/graphical-capabilities-vulkan.json");

        Assert.Contains("win-x64", message, StringComparison.Ordinal);
        Assert.Contains("AMD Radeon RX 9070 XT", message, StringComparison.Ordinal);
        Assert.Contains("dynamicRendering is required", message, StringComparison.Ordinal);
        Assert.Contains("graphical-capabilities-vulkan.json", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfUnsupportedRaisesNotSupportedExceptionForTheExitFourContract()
    {
        VulkanCapabilityRecord rejected = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            SupportedRecord(),
            "Synchronization2");

        // Program.cs maps NotSupportedException out of window.Run() to exit 4.
        Assert.Throws<NotSupportedException>(
            () => VulkanCapabilityGuard.ThrowIfUnsupported(rejected, "report.json"));
        VulkanCapabilityGuard.ThrowIfUnsupported(SupportedRecord(), "report.json");
    }

    [Fact]
    public void TheReportFileNameRemainsStableForDiagnosticsAutomation()
    {
        Assert.Equal(
            "graphical-capabilities-vulkan.json",
            VulkanCapabilityGuard.ReportFileName);
    }

    /// <summary>
    /// The JSON report is the artifact an operator sends with a bug report, so
    /// the fields that identify the machine and explain the refusal must be
    /// present and readable — enums as names, not integers.
    /// </summary>
    [Fact]
    public void TheJsonReportCarriesTheIdentifyingFieldsAsReadableNames()
    {
        VulkanCapabilityRecord record = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            SupportedRecord(),
            "Maintenance4");

        string json = VulkanCapabilityReportWriter.Serialize(record);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("win-x64", root.GetProperty("RuntimeIdentifier").GetString());
        Assert.Equal("AMD Radeon RX 9070 XT", root.GetProperty("DeviceName").GetString());
        Assert.Equal("DiscreteGpu", root.GetProperty("DeviceType").GetString());
        Assert.Equal("Windows", root.GetProperty("OperatingSystem").GetString());
        Assert.Equal("Maintenance4", root.GetProperty("ForcedUnsupportedFeature").GetString());
        Assert.False(root.GetProperty("Features").GetProperty("Maintenance4").GetBoolean());
        Assert.Equal(
            "B8G8R8A8Unorm",
            root.GetProperty("Surface").GetProperty("SelectedFormat").GetString());
        Assert.NotEmpty(root.GetProperty("SupportFailures").EnumerateArray().ToArray());
    }

    [Fact]
    public void TheRecordProjectsOntoTheBackendNeutralContract()
    {
        VulkanCapabilityRecord record = SupportedRecord(
            limits: VulkanDeviceLimitSupport.Complete with
            {
                MinStorageBufferOffsetAlignment = 16,
                MaxStorageBufferRange = 192u * 1024u * 1024u,
                MinUniformBufferOffsetAlignment = 64,
                MaxColorSampleCount = 4,
                MaxImageDimension2D = 8192,
                MaxImageArrayLayers = 128,
                DeviceLocalHeapBytes = 6UL * 1024 * 1024 * 1024,
                MaxDescriptorSetStorageBuffers = 48,
                MaxPerStageDescriptorStorageBuffers = 32,
                MaxDescriptorSetUpdateAfterBindSampledImages = 500_000,
                MaxPerStageDescriptorUpdateAfterBindSampledImages = 16_384,
            });

        GpuCapabilityRecord projected = record.ToGpuCapabilityRecord();

        Assert.Equal(GpuBackendKind.Vulkan, projected.Backend);
        Assert.Equal("AMD Radeon RX 9070 XT", projected.DeviceName);
        Assert.Equal("Vulkan 1.3.280", projected.ApiVersion);
        Assert.Equal(16u, projected.MinStorageBufferOffsetAlignment);
        Assert.Equal(192u * 1024u * 1024u, projected.MaxStorageBufferRangeBytes);
        Assert.Equal(64u, projected.MinUniformBufferOffsetAlignment);
        Assert.Equal(4u, projected.MaxSampleCount);
        Assert.Equal(8192u, projected.MaxImageDimension2D);
        Assert.Equal(128u, projected.MaxImageArrayLayers);
        Assert.Equal(6UL * 1024 * 1024 * 1024, projected.DeviceLocalMemoryBytes);
        Assert.Equal(16_384u, projected.MaxTextureTableSlots);
        Assert.Equal(32u, projected.MaxStorageBufferBindings);
        Assert.True(projected.SupportsMultiDrawIndirect);
        Assert.True(projected.SupportsDrawParameters);
        Assert.True(projected.SupportsTextureCompressionBc);
        Assert.True(projected.SupportsTimestampQueries);
        Assert.True(projected.SupportsPersistentlyMappedRings);
        Assert.True(projected.SupportsRgba16FloatRenderTargets);
        Assert.Equal(4u, projected.MaxRgba16FloatSampleCount);
        Assert.True(projected.SupportsSampledDepth);
        Assert.True(projected.SupportsMultiview);
        Assert.Empty(projected.SupportFailures);
    }

    [Fact]
    public void AnAcceptedDeviceAlsoSatisfiesTheNeutralContract()
    {
        GpuCapabilityRecord projected = SupportedRecord().ToGpuCapabilityRecord();

        Assert.True(projected.IsSupported);
    }

    [Theory]
    [InlineData(1u, 3u, 280u)]
    [InlineData(1u, 4u, 0u)]
    [InlineData(0u, 0u, 1u)]
    public void ApiVersionPackingRoundTrips(uint major, uint minor, uint patch)
    {
        uint packed = VulkanApiVersion.Make(major, minor, patch);

        Assert.Equal(major, VulkanApiVersion.Major(packed));
        Assert.Equal(minor, VulkanApiVersion.Minor(packed));
        Assert.Equal(patch, VulkanApiVersion.Patch(packed));
        Assert.Equal($"Vulkan {major}.{minor}.{patch}", VulkanApiVersion.Describe(packed));
    }
}
