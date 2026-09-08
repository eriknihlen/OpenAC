namespace AcDream.App.Rendering.Gpu;

internal sealed record GpuCapabilityRecord
{
    public required GpuBackendKind Backend { get; init; }

    /// <summary>Adapter name, e.g. <c>"AMD Radeon RX 9070 XT"</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Driver identification string for diagnostics and bug reports.</summary>
    public required string DriverInfo { get; init; }

    /// <summary>API version actually in use, e.g. <c>"OpenGL 4.6"</c> or <c>"Vulkan 1.3.280"</c>.</summary>
    public required string ApiVersion { get; init; }

    public required uint MaxTextureTableSlots { get; init; }

    public required uint MaxStorageBufferBindings { get; init; }

    public required uint MaxPushConstantBytes { get; init; }

    /// <summary>Required alignment for a storage-buffer binding offset.</summary>
    public required uint MinStorageBufferOffsetAlignment { get; init; }

    public uint MaxStorageBufferRangeBytes { get; init; } = 128u * 1024u * 1024u;

    /// <summary>Required alignment for a uniform-buffer binding offset.</summary>
    public required uint MinUniformBufferOffsetAlignment { get; init; }

    public required uint MaxClipDistances { get; init; }

    public required uint MaxSampleCount { get; init; }

    /// <summary>Largest supported two-dimensional image edge from the selected adapter.</summary>
    public required uint MaxImageDimension2D { get; init; }

    public required uint MaxImageArrayLayers { get; init; }

    /// <summary>
    /// Total bytes in device-local heaps on the selected adapter. Render-pack
    /// policy derives a deliberately bounded share from this value before any
    /// optional image is allocated; zero means that no optional pack memory may
    /// be assumed.
    /// </summary>
    public required ulong DeviceLocalMemoryBytes { get; init; }

    /// <summary>Multi-draw-indirect. Mandatory — it is the entire draw architecture.</summary>
    public required bool SupportsMultiDrawIndirect { get; init; }

    /// <summary>Shader draw parameters (<c>gl_DrawID</c>). Mandatory — batch lookup depends on it.</summary>
    public required bool SupportsDrawParameters { get; init; }

    /// <summary>BC1/2/3 sampling. Mandatory — DAT surfaces upload as DXT without transcoding.</summary>
    public required bool SupportsTextureCompressionBc { get; init; }

    /// <summary>GPU timestamps. Optional: absence degrades profiling, not rendering.</summary>
    public required bool SupportsTimestampQueries { get; init; }

    public required bool SupportsPersistentlyMappedRings { get; init; }

    public required bool SupportsRgba16FloatRenderTargets { get; init; }

    public required uint MaxRgba16FloatSampleCount { get; init; }

    public required bool SupportsSampledDepth { get; init; }

    public required bool SupportsMultiview { get; init; }

    public IReadOnlyList<string> SupportFailures
    {
        get
        {
            List<string> failures = [];

            if (!SupportsMultiDrawIndirect)
                failures.Add("Multi-draw-indirect is required to submit world geometry.");
            if (!SupportsDrawParameters)
                failures.Add("Shader draw parameters (gl_DrawID) are required to select per-draw batch data.");
            if (!SupportsTextureCompressionBc)
                failures.Add("BC (DXT) texture compression is required to upload DAT surfaces.");
            if (MaxTextureTableSlots < GpuBindingModel.TextureTableCapacity)
            {
                failures.Add(
                    $"The texture table needs {GpuBindingModel.TextureTableCapacity} slots; " +
                    $"this device provides {MaxTextureTableSlots}.");
            }

            if (MaxStorageBufferBindings < GpuBindingModel.StorageBindingCount)
            {
                failures.Add(
                    $"{GpuBindingModel.StorageBindingCount} storage-buffer bindings are required; " +
                    $"this device provides {MaxStorageBufferBindings}.");
            }

            if (MaxPushConstantBytes < GpuBindingModel.PushConstantBytes)
            {
                failures.Add(
                    $"{GpuBindingModel.PushConstantBytes} push-constant bytes are required; " +
                    $"this device provides {MaxPushConstantBytes}.");
            }

            if (MaxClipDistances < GpuBindingModel.ClipPlanesPerSlot)
            {
                failures.Add(
                    $"{GpuBindingModel.ClipPlanesPerSlot} clip distances are required by the per-cell clip gate; " +
                    $"this device provides {MaxClipDistances}.");
            }

            return failures;
        }
    }

    public bool IsSupported => SupportFailures.Count == 0;
}
