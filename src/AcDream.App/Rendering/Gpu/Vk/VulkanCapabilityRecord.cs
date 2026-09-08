using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.App.Platform;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed record VulkanDeviceFeatureSupport
{

    /// <summary>The three MDI dispatch sites are the entire draw architecture.</summary>
    public required bool MultiDrawIndirect { get; init; }

    public required bool DrawIndirectFirstInstance { get; init; }

    public required bool ShaderClipDistance { get; init; }

    /// <summary>DXT1/3/5 DAT surfaces upload as BC1/2/3 with no transcode.</summary>
    public required bool TextureCompressionBc { get; init; }

    /// <summary>Sampler-quality parity with the GL path.</summary>
    public required bool SamplerAnisotropy { get; init; }

    // ---- 1.1 ----

    /// <summary><c>gl_DrawID</c>. Resets per indirect dispatch exactly as GL's does.</summary>
    public required bool ShaderDrawParameters { get; init; }

    public required bool Multiview { get; init; }

    // ---- 1.2 ----

    /// <summary>One monotonic serial replaces the GL fence array; the retirement ledger keeps its keys.</summary>
    public required bool TimelineSemaphore { get; init; }

    public required bool HostQueryReset { get; init; }

    /// <summary>The global texture table is a runtime-sized descriptor array.</summary>
    public required bool RuntimeDescriptorArray { get; init; }

    /// <summary>Unregistered table slots are legitimately absent rather than an error.</summary>
    public required bool DescriptorBindingPartiallyBound { get; init; }

    /// <summary>Texture registration appends a descriptor write without rebuilding the set.</summary>
    public required bool DescriptorBindingSampledImageUpdateAfterBind { get; init; }

    public required bool DescriptorBindingUpdateUnusedWhilePending { get; init; }

    public required bool DescriptorBindingVariableDescriptorCount { get; init; }

    /// <summary>
    /// <c>nonuniformEXT</c> in the fragment shaders. Required, not optional:
    /// within one MDI dispatch different draws read different <c>Batches[]</c>
    /// entries, and "dynamically uniform" is defined over the whole dispatch on
    /// some implementations (plan §4.6).
    /// </summary>
    public required bool ShaderSampledImageArrayNonUniformIndexing { get; init; }

    // ---- 1.3 ----

    /// <summary>No render-pass or framebuffer objects anywhere in the frame.</summary>
    public required bool DynamicRendering { get; init; }

    /// <summary>Every barrier in the frame skeleton is a <c>vkCmdPipelineBarrier2</c>.</summary>
    public required bool Synchronization2 { get; init; }

    /// <summary>Relaxed shader interface rules for the dual-legal GLSL sources.</summary>
    public required bool Maintenance4 { get; init; }

    public required bool ShaderDemoteToHelperInvocation { get; init; }

    /// <summary>
    /// Every feature present. The starting point for the forced-unsupported gate
    /// knob and for tests that assert one specific absence at a time.
    /// </summary>
    internal static VulkanDeviceFeatureSupport Complete { get; } = new()
    {
        MultiDrawIndirect = true,
        DrawIndirectFirstInstance = true,
        ShaderClipDistance = true,
        TextureCompressionBc = true,
        SamplerAnisotropy = true,
        ShaderDrawParameters = true,
        Multiview = true,
        TimelineSemaphore = true,
        HostQueryReset = true,
        RuntimeDescriptorArray = true,
        DescriptorBindingPartiallyBound = true,
        DescriptorBindingSampledImageUpdateAfterBind = true,
        DescriptorBindingUpdateUnusedWhilePending = true,
        DescriptorBindingVariableDescriptorCount = true,
        ShaderSampledImageArrayNonUniformIndexing = true,
        DynamicRendering = true,
        Synchronization2 = true,
        Maintenance4 = true,
        ShaderDemoteToHelperInvocation = true,
    };

    internal VulkanDeviceFeatureSupport? Without(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return featureName.Trim() switch
        {
            var n when Is(n, nameof(MultiDrawIndirect)) => this with { MultiDrawIndirect = false },
            var n when Is(n, nameof(DrawIndirectFirstInstance)) => this with { DrawIndirectFirstInstance = false },
            var n when Is(n, nameof(ShaderClipDistance)) => this with { ShaderClipDistance = false },
            var n when Is(n, nameof(TextureCompressionBc)) => this with { TextureCompressionBc = false },
            var n when Is(n, nameof(SamplerAnisotropy)) => this with { SamplerAnisotropy = false },
            var n when Is(n, nameof(ShaderDrawParameters)) => this with { ShaderDrawParameters = false },
            var n when Is(n, nameof(Multiview)) => this with { Multiview = false },
            var n when Is(n, nameof(TimelineSemaphore)) => this with { TimelineSemaphore = false },
            var n when Is(n, nameof(HostQueryReset)) => this with { HostQueryReset = false },
            var n when Is(n, nameof(RuntimeDescriptorArray)) => this with { RuntimeDescriptorArray = false },
            var n when Is(n, nameof(DescriptorBindingPartiallyBound)) => this with { DescriptorBindingPartiallyBound = false },
            var n when Is(n, nameof(DescriptorBindingSampledImageUpdateAfterBind)) => this with { DescriptorBindingSampledImageUpdateAfterBind = false },
            var n when Is(n, nameof(DescriptorBindingUpdateUnusedWhilePending)) => this with { DescriptorBindingUpdateUnusedWhilePending = false },
            var n when Is(n, nameof(DescriptorBindingVariableDescriptorCount)) => this with { DescriptorBindingVariableDescriptorCount = false },
            var n when Is(n, nameof(ShaderSampledImageArrayNonUniformIndexing)) => this with { ShaderSampledImageArrayNonUniformIndexing = false },
            var n when Is(n, nameof(DynamicRendering)) => this with { DynamicRendering = false },
            var n when Is(n, nameof(Synchronization2)) => this with { Synchronization2 = false },
            var n when Is(n, nameof(Maintenance4)) => this with { Maintenance4 = false },
            var n when Is(n, nameof(ShaderDemoteToHelperInvocation)) => this with { ShaderDemoteToHelperInvocation = false },
            _ => null,
        };

        static bool Is(string candidate, string name)
            => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The device limits the plan asserts up front rather than discovering at draw
/// time (plan §4.1, §3.4).
/// </summary>
internal sealed record VulkanDeviceLimitSupport
{
    public required uint MaxPushConstantsSize { get; init; }

    public required uint MaxClipDistances { get; init; }

    /// <summary>Sets 0, 1 and 2 are all bound simultaneously, so at least 3.</summary>
    public required uint MaxBoundDescriptorSets { get; init; }

    public required uint MaxDescriptorSetStorageBuffersDynamic { get; init; }

    /// <summary>Must reach every storage binding declared by descriptor set 0.</summary>
    public required uint MaxDescriptorSetStorageBuffers { get; init; }

    /// <summary>
    /// Must reach every set-0 storage binding because the shared layout exposes
    /// all of them to both the vertex and fragment stages.
    /// </summary>
    public required uint MaxPerStageDescriptorStorageBuffers { get; init; }

    /// <summary>Must reach the number of dynamic uniform bindings set 1 declares.</summary>
    public required uint MaxDescriptorSetUniformBuffersDynamic { get; init; }

    public required uint MaxDescriptorSetUpdateAfterBindSampledImages { get; init; }

    public required uint MaxPerStageDescriptorUpdateAfterBindSampledImages { get; init; }

    /// <summary>The frame profiler's GPU timings need graphics-queue timestamps.</summary>
    public required bool TimestampComputeAndGraphics { get; init; }

    /// <summary>Ring allocations must satisfy this; getting it wrong is a driver error on Vulkan.</summary>
    public required uint MinStorageBufferOffsetAlignment { get; init; }

    public required uint MaxStorageBufferRange { get; init; }

    /// <summary>As above, for the SceneLighting uniform block.</summary>
    public required uint MinUniformBufferOffsetAlignment { get; init; }

    public required uint MaxImageDimension2D { get; init; }

    public required uint MaxImageArrayLayers { get; init; }

    /// <summary>Sum of device-local heap bytes reported by the selected physical device.</summary>
    public required ulong DeviceLocalHeapBytes { get; init; }

    public required uint MaxColorSampleCount { get; init; }

    /// <summary>
    /// A profile that satisfies every requirement, used as the base for tests
    /// and for the forced-unsupported knob. The numbers are the Vulkan 1.3
    /// guaranteed minimums where a guarantee exists, and the acdream requirement
    /// where it does not.
    /// </summary>
    internal static VulkanDeviceLimitSupport Complete { get; } = new()
    {
        MaxPushConstantsSize = GpuBindingModel.MaxPushConstantBytes,
        MaxClipDistances = GpuBindingModel.ClipPlanesPerSlot,
        MaxBoundDescriptorSets = 4,
        MaxDescriptorSetStorageBuffersDynamic = 4,
        MaxDescriptorSetStorageBuffers = GpuBindingModel.StorageBindingCount,
        MaxPerStageDescriptorStorageBuffers = GpuBindingModel.StorageBindingCount,
        MaxDescriptorSetUniformBuffersDynamic = 8,
        MaxDescriptorSetUpdateAfterBindSampledImages = GpuBindingModel.TextureTableCapacity,
        MaxPerStageDescriptorUpdateAfterBindSampledImages = GpuBindingModel.TextureTableCapacity,
        TimestampComputeAndGraphics = true,
        MinStorageBufferOffsetAlignment = 256,
        MaxStorageBufferRange = 128u * 1024u * 1024u,
        MinUniformBufferOffsetAlignment = 256,
        MaxImageDimension2D = 16384,
        MaxImageArrayLayers = 2048,
        DeviceLocalHeapBytes = 8UL * 1024 * 1024 * 1024,
        MaxColorSampleCount = 8,
    };
}

internal sealed record VulkanFormatSupport
{
    public required bool SwapchainUnormFormat { get; init; }

    public required Format DepthStencilFormat { get; init; }

    public required bool DepthStencilSampled { get; init; }

    public required bool Rgba16FloatColorAttachment { get; init; }

    /// <summary>RGBA16F supports optimal-tiling sampled-image reads.</summary>
    public required bool Rgba16FloatSampled { get; init; }

    /// <summary>RGBA16F supports linear filtering, required by scaled bloom/ray passes.</summary>
    public required bool Rgba16FloatLinearFilter { get; init; }

    public required uint MaxRgba16FloatSampleCount { get; init; }

    /// <summary>BC1 (DXT1) sampled-image support with optimal tiling.</summary>
    public required bool Bc1Sampled { get; init; }

    /// <summary>BC2 (DXT3) sampled-image support with optimal tiling.</summary>
    public required bool Bc2Sampled { get; init; }

    /// <summary>BC3 (DXT5) sampled-image support with optimal tiling.</summary>
    public required bool Bc3Sampled { get; init; }

    internal static VulkanFormatSupport Complete { get; } = new()
    {
        SwapchainUnormFormat = true,
        DepthStencilFormat = Format.D32SfloatS8Uint,
        DepthStencilSampled = true,
        Rgba16FloatColorAttachment = true,
        Rgba16FloatSampled = true,
        Rgba16FloatLinearFilter = true,
        MaxRgba16FloatSampleCount = 8,
        Bc1Sampled = true,
        Bc2Sampled = true,
        Bc3Sampled = true,
    };
}

internal sealed record VulkanSurfaceSupport(
    bool PresentSupported,
    Format SelectedFormat,
    ColorSpaceKHR SelectedColorSpace,
    PresentModeKHR SelectedPresentMode,
    uint SelectedImageCount,
    uint SelectedWidth,
    uint SelectedHeight,
    bool SupportsTransferSource,
    IReadOnlyList<Format> AvailableFormats,
    IReadOnlyList<PresentModeKHR> AvailablePresentModes);

internal sealed record VulkanPhysicalDeviceCandidate(
    int Index,
    string DeviceName,
    PhysicalDeviceType DeviceType,
    uint ApiVersion,
    uint DriverVersion,
    uint VendorId,
    uint DeviceId,
    ulong DeviceLocalHeapBytes);

internal sealed record VulkanFunctionProbeResult(
    bool DeviceCreation,
    bool DescriptorIndexingLayout,
    bool PushConstantLayout,
    bool DynamicRenderingClear,
    bool TimelineSemaphoreWait,
    bool HostQueryReset,
    bool OffscreenReadback,
    IReadOnlyList<string> Failures)
{
    internal static VulkanFunctionProbeResult NotRun { get; } = new(
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        ["active Vulkan device probe did not run"]);
}

internal sealed record VulkanCapabilityRecord(
    DateTimeOffset CapturedAtUtc,
    string RuntimeIdentifier,
    GraphicalHostOperatingSystem OperatingSystem,
    GraphicalDisplayProtocol RequestedDisplayProtocol,
    GraphicalDisplayProtocol ActiveDisplayProtocol,
    string InstanceApiVersion,
    string DeviceApiVersion,
    uint DeviceApiVersionPacked,
    string DeviceName,
    string DriverInfo,
    PhysicalDeviceType DeviceType,
    int SelectedDeviceIndex,
    string DeviceSelectionReason,
    string? RequestedDeviceOverride,
    string? ForcedUnsupportedFeature,
    IReadOnlyList<VulkanPhysicalDeviceCandidate> AvailableDevices,
    IReadOnlyList<string> InstanceExtensions,
    IReadOnlyList<string> DeviceExtensions,
    uint GraphicsQueueFamily,
    uint PresentQueueFamily,
    VulkanDeviceFeatureSupport Features,
    VulkanDeviceLimitSupport Limits,
    VulkanFormatSupport Formats,
    VulkanSurfaceSupport? Surface,
    VulkanFunctionProbeResult FunctionProbe,
    IReadOnlyList<string> SupportFailures)
{
    internal bool IsSupported => SupportFailures.Count == 0;

    internal GpuCapabilityRecord ToGpuCapabilityRecord() => new()
    {
        Backend = GpuBackendKind.Vulkan,
        DeviceName = DeviceName,
        DriverInfo = DriverInfo,
        ApiVersion = DeviceApiVersion,
        MaxTextureTableSlots =
            Math.Min(
                Limits.MaxDescriptorSetUpdateAfterBindSampledImages,
                Limits.MaxPerStageDescriptorUpdateAfterBindSampledImages),
        MaxStorageBufferBindings = Math.Min(
            Limits.MaxDescriptorSetStorageBuffers,
            Limits.MaxPerStageDescriptorStorageBuffers),
        MaxPushConstantBytes = Limits.MaxPushConstantsSize,
        MinStorageBufferOffsetAlignment = Limits.MinStorageBufferOffsetAlignment,
        MaxStorageBufferRangeBytes = Limits.MaxStorageBufferRange,
        MinUniformBufferOffsetAlignment = Limits.MinUniformBufferOffsetAlignment,
        MaxClipDistances = Limits.MaxClipDistances,
        MaxSampleCount = Limits.MaxColorSampleCount,
        MaxImageDimension2D = Limits.MaxImageDimension2D,
        MaxImageArrayLayers = Limits.MaxImageArrayLayers,
        DeviceLocalMemoryBytes = Limits.DeviceLocalHeapBytes,
        SupportsMultiDrawIndirect = Features.MultiDrawIndirect,
        SupportsDrawParameters = Features.ShaderDrawParameters,
        SupportsTextureCompressionBc = Features.TextureCompressionBc,
        SupportsTimestampQueries = Limits.TimestampComputeAndGraphics,
        SupportsMultiview = Features.Multiview,
        SupportsPersistentlyMappedRings = true,
        SupportsRgba16FloatRenderTargets =
            Formats.Rgba16FloatColorAttachment
            && Formats.Rgba16FloatSampled
            && Formats.Rgba16FloatLinearFilter
            && Formats.MaxRgba16FloatSampleCount > 0,
        MaxRgba16FloatSampleCount =
            Formats.Rgba16FloatColorAttachment
            && Formats.Rgba16FloatSampled
            && Formats.Rgba16FloatLinearFilter
                ? Math.Min(Formats.MaxRgba16FloatSampleCount, Limits.MaxColorSampleCount)
                : 0u,
        SupportsSampledDepth = Formats.DepthStencilSampled,
    };
}

internal static class VulkanCapabilityRequirements
{
    internal const uint RequiredApiMajor = 1;

    internal const uint RequiredApiMinor = 3;

    internal static IReadOnlyList<string> Evaluate(VulkanCapabilityRecord capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var failures = new List<string>();

        uint major = VulkanApiVersion.Major(capabilities.DeviceApiVersionPacked);
        uint minor = VulkanApiVersion.Minor(capabilities.DeviceApiVersionPacked);
        if (major < RequiredApiMajor || (major == RequiredApiMajor && minor < RequiredApiMinor))
        {
            failures.Add(
                $"Vulkan {RequiredApiMajor}.{RequiredApiMinor} is required; " +
                $"the selected device reports {major}.{minor}.");
        }

        VulkanDeviceFeatureSupport features = capabilities.Features;
        if (!features.MultiDrawIndirect)
            failures.Add("multiDrawIndirect is required to submit world geometry.");
        if (!features.DrawIndirectFirstInstance)
            failures.Add("drawIndirectFirstInstance is required; indirect commands carry a per-group instance base.");
        if (!features.ShaderDrawParameters)
            failures.Add("shaderDrawParameters (gl_DrawID) is required to select per-draw batch data.");
        if (!features.ShaderClipDistance)
            failures.Add("shaderClipDistance is required by the per-cell clip gate.");
        if (!features.TextureCompressionBc)
            failures.Add("textureCompressionBC is required to upload DAT surfaces without transcoding.");
        if (!features.SamplerAnisotropy)
            failures.Add("samplerAnisotropy is required for sampler-quality parity.");
        if (!features.TimelineSemaphore)
            failures.Add("timelineSemaphore is required; the frame serial is the semaphore value.");
        if (!features.HostQueryReset)
            failures.Add("hostQueryReset is required to reset timestamp pools from the CPU.");
        if (!features.RuntimeDescriptorArray)
            failures.Add("runtimeDescriptorArray is required by the global texture table.");
        if (!features.DescriptorBindingPartiallyBound)
            failures.Add("descriptorBindingPartiallyBound is required; unregistered texture slots are legitimately absent.");
        if (!features.DescriptorBindingSampledImageUpdateAfterBind)
            failures.Add("descriptorBindingSampledImageUpdateAfterBind is required to register textures without rebuilding the set.");
        if (!features.DescriptorBindingUpdateUnusedWhilePending)
            failures.Add("descriptorBindingUpdateUnusedWhilePending is required to recycle texture slots while frames are in flight.");
        if (!features.DescriptorBindingVariableDescriptorCount)
            failures.Add("descriptorBindingVariableDescriptorCount is required to size the texture table.");
        if (!features.ShaderSampledImageArrayNonUniformIndexing)
            failures.Add("shaderSampledImageArrayNonUniformIndexing is required; one indirect dispatch reads different texture slots per draw.");
        if (!features.DynamicRendering)
            failures.Add("dynamicRendering is required; the frame uses no render-pass or framebuffer objects.");
        if (!features.Synchronization2)
            failures.Add("synchronization2 is required; every barrier in the frame is a barrier2.");
        if (!features.Maintenance4)
            failures.Add("maintenance4 is required for the relaxed shader interface rules the shared GLSL relies on.");
        if (!features.ShaderDemoteToHelperInvocation)
            failures.Add("shaderDemoteToHelperInvocation is required; the SPIR-V 1.6 fragment modules lower discard to OpDemoteToHelperInvocation (#459).");

        VulkanDeviceLimitSupport limits = capabilities.Limits;
        if (limits.MaxPushConstantsSize < GpuBindingModel.PushConstantBytes)
        {
            failures.Add(
                $"{GpuBindingModel.PushConstantBytes} push-constant bytes are required; " +
                $"this device provides {limits.MaxPushConstantsSize}.");
        }
        if (limits.MaxClipDistances < GpuBindingModel.ClipPlanesPerSlot)
        {
            failures.Add(
                $"{GpuBindingModel.ClipPlanesPerSlot} clip distances are required by the per-cell clip gate; " +
                $"this device provides {limits.MaxClipDistances}.");
        }
        if (limits.MaxBoundDescriptorSets < VulkanDescriptorSetCount)
        {
            failures.Add(
                $"{VulkanDescriptorSetCount} simultaneously bound descriptor sets are required " +
                $"(storage, uniform, texture table); this device provides {limits.MaxBoundDescriptorSets}.");
        }
        if (limits.MaxDescriptorSetStorageBuffersDynamic < VulkanPipelineLayouts.DynamicStorageBindingCount)
        {
            failures.Add(
                $"set 0 declares {VulkanPipelineLayouts.DynamicStorageBindingCount} dynamic storage bindings " +
                $"(Vulkan guarantees 4); this device provides {limits.MaxDescriptorSetStorageBuffersDynamic}.");
        }
        if (limits.MaxDescriptorSetStorageBuffers < GpuBindingModel.StorageBindingCount)
        {
            failures.Add(
                $"set 0 declares {GpuBindingModel.StorageBindingCount} total storage bindings; " +
                $"this device provides {limits.MaxDescriptorSetStorageBuffers} per set.");
        }
        if (limits.MaxPerStageDescriptorStorageBuffers < GpuBindingModel.StorageBindingCount)
        {
            failures.Add(
                $"set 0 exposes {GpuBindingModel.StorageBindingCount} storage bindings to each shader stage; " +
                $"this device provides {limits.MaxPerStageDescriptorStorageBuffers} per stage.");
        }
        if (limits.MaxDescriptorSetUniformBuffersDynamic < VulkanFrameBindings.DynamicUniformBindingCount)
        {
            failures.Add(
                $"set 1 declares {VulkanFrameBindings.DynamicUniformBindingCount} dynamic uniform bindings " +
                $"(Vulkan guarantees 8); this device provides {limits.MaxDescriptorSetUniformBuffersDynamic}.");
        }
        if (limits.MaxDescriptorSetUpdateAfterBindSampledImages < GpuBindingModel.TextureTableCapacity)
        {
            failures.Add(
                $"the texture table needs {GpuBindingModel.TextureTableCapacity} update-after-bind sampled images; " +
                $"this device provides {limits.MaxDescriptorSetUpdateAfterBindSampledImages} per set.");
        }
        if (limits.MaxPerStageDescriptorUpdateAfterBindSampledImages < GpuBindingModel.TextureTableCapacity)
        {
            failures.Add(
                $"the texture table needs {GpuBindingModel.TextureTableCapacity} update-after-bind sampled images " +
                $"in the fragment stage; this device provides {limits.MaxPerStageDescriptorUpdateAfterBindSampledImages}.");
        }
        if (!limits.TimestampComputeAndGraphics)
            failures.Add("graphics-queue timestamps are required by the frame profiler.");

        VulkanFormatSupport formats = capabilities.Formats;
        if (!formats.SwapchainUnormFormat)
        {
            failures.Add(
                "the presentation surface must offer B8G8R8A8_UNORM; the renderer is " +
                "plain UNORM end to end and an sRGB swapchain would re-encode every frame.");
        }
        if (formats.DepthStencilFormat == Format.Undefined)
            failures.Add("a combined depth+stencil format is required by the portal aperture punch.");
        if (!formats.Bc1Sampled || !formats.Bc2Sampled || !formats.Bc3Sampled)
            failures.Add("BC1, BC2 and BC3 sampled-image support is required to upload DAT surfaces.");

        if (capabilities.Surface is { } surface)
        {
            if (!surface.PresentSupported)
                failures.Add("the selected device cannot present to the window surface.");
            if (!surface.SupportsTransferSource)
                failures.Add("the swapchain must support TRANSFER_SRC usage for screenshot capture.");
        }

        if (capabilities.FunctionProbe.Failures.Count != 0)
        {
            failures.AddRange(
                capabilities.FunctionProbe.Failures.Select(
                    failure => $"Vulkan device probe: {failure}"));
        }
        else
        {
            if (!capabilities.FunctionProbe.DeviceCreation)
                failures.Add("the Vulkan device-creation probe did not pass.");
            if (!capabilities.FunctionProbe.DescriptorIndexingLayout)
                failures.Add("the descriptor-indexing layout probe did not pass.");
            if (!capabilities.FunctionProbe.PushConstantLayout)
                failures.Add("the push-constant pipeline-layout probe did not pass.");
            if (!capabilities.FunctionProbe.DynamicRenderingClear)
                failures.Add("the dynamic-rendering clear probe did not pass.");
            if (!capabilities.FunctionProbe.TimelineSemaphoreWait)
                failures.Add("the timeline-semaphore wait probe did not pass.");
            if (!capabilities.FunctionProbe.HostQueryReset)
                failures.Add("the host query-reset probe did not pass.");
            if (!capabilities.FunctionProbe.OffscreenReadback)
                failures.Add("the offscreen readback probe did not return the expected pixels.");
        }

        return failures;
    }

    /// <summary>
    /// Sets 0 (storage), 1 (uniform) and 2 (texture table) are bound at once,
    /// so <c>maxBoundDescriptorSets</c> must reach 3. Vulkan guarantees 4.
    /// </summary>
    internal const uint VulkanDescriptorSetCount =
        GpuBindingModel.TextureTableSet + 1;

    internal static VulkanCapabilityRecord Reevaluate(VulkanCapabilityRecord capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        VulkanCapabilityRecord cleared = capabilities with { SupportFailures = [] };
        return cleared with { SupportFailures = Evaluate(cleared) };
    }

    /// <summary>
    /// Apply <c>ACDREAM_VULKAN_FORCE_UNSUPPORTED</c>. An unrecognised name is a
    /// hard failure rather than a silent no-op: a gate knob that quietly does
    /// nothing would report a pass the operator did not actually get.
    /// </summary>
    internal static VulkanCapabilityRecord ApplyForcedUnsupported(
        VulkanCapabilityRecord capabilities,
        string? featureName)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrWhiteSpace(featureName))
            return capabilities;

        VulkanDeviceFeatureSupport? forced = capabilities.Features.Without(featureName);
        if (forced is null)
        {
            return Reevaluate(
                capabilities with
                {
                    ForcedUnsupportedFeature = featureName,
                    FunctionProbe = capabilities.FunctionProbe with
                    {
                        Failures =
                        [
                            .. capabilities.FunctionProbe.Failures,
                            $"ACDREAM_VULKAN_FORCE_UNSUPPORTED named '{featureName}', " +
                            "which is not a required Vulkan feature.",
                        ],
                    },
                });
        }

        return Reevaluate(
            capabilities with
            {
                Features = forced,
                ForcedUnsupportedFeature = featureName,
            });
    }
}

/// <summary>Packed <c>VK_MAKE_API_VERSION</c> arithmetic, kept out of the interop layer so it is testable.</summary>
internal static class VulkanApiVersion
{
    internal static uint Major(uint packed) => (packed >> 22) & 0x7Fu;

    internal static uint Minor(uint packed) => (packed >> 12) & 0x3FFu;

    internal static uint Patch(uint packed) => packed & 0xFFFu;

    internal static uint Make(uint major, uint minor, uint patch)
        => (major << 22) | (minor << 12) | patch;

    internal static string Describe(uint packed)
        => $"Vulkan {Major(packed)}.{Minor(packed)}.{Patch(packed)}";
}

internal static class VulkanCapabilityGuard
{
    /// <summary>File name of the Vulkan report, beside the GL one in the diagnostics directory.</summary>
    internal const string ReportFileName = "graphical-capabilities-vulkan.json";

    internal static void ThrowIfUnsupported(
        VulkanCapabilityRecord capabilities,
        string reportPath)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.IsSupported)
            throw new NotSupportedException(FormatUnsupportedMessage(capabilities, reportPath));
    }

    internal static string FormatUnsupportedMessage(
        VulkanCapabilityRecord capabilities,
        string reportPath)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        return
            "acdream's Vulkan renderer is unsupported by the selected device.\n" +
            $"Platform: {capabilities.RuntimeIdentifier}, " +
            $"{capabilities.ActiveDisplayProtocol}, " +
            $"{capabilities.DeviceName} ({capabilities.DeviceType}), " +
            $"{capabilities.DeviceApiVersion}, {capabilities.DriverInfo}\n" +
            string.Join(
                "\n",
                capabilities.SupportFailures.Select(failure => $" - {failure}")) +
            $"\nFull capability report: {Path.GetFullPath(reportPath)}";
    }
}

internal static class VulkanCapabilityReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    internal static void Write(string path, VulkanCapabilityRecord capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(capabilities);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(capabilities));
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    /// <summary>Exposed so the report's shape can be asserted without touching the file system.</summary>
    internal static string Serialize(VulkanCapabilityRecord capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return JsonSerializer.Serialize(capabilities, Options);
    }
}
