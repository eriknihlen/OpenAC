using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGpuPipeline : IGpuPipeline
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly IGpuResourceRetirementQueue _retirement;
    private readonly VulkanDebugNames _debugNames;
    private readonly VulkanPipelineLayouts.Created _layouts;
    private readonly VulkanPipelineLayouts.Created.PackLayoutLease? _packLayoutLease;
    private readonly PipelineLayout _layout;
    private readonly PipelineCache _cache;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly bool _ownsShaderModules;
    private readonly Format _depthStencilFormat;
    private readonly Pipeline _withDepthAttachment;
    private readonly Pipeline _withoutDepthAttachment;
    private readonly Dictionary<GpuTextureFormat, VulkanGpuPipeline> _colorVariants = [];
    private bool _disposed;

    internal VulkanGpuPipeline(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        IGpuResourceRetirementQueue retirement,
        VulkanDebugNames debugNames,
        VulkanPipelineLayouts.Created layouts,
        VulkanPipelineLayouts.Created.PackLayoutLease? packLayoutLease,
        PipelineLayout layout,
        PipelineCache cache,
        ShaderModule vertexModule,
        ShaderModule fragmentModule,
        bool ownsShaderModules,
        GpuPipelineDescription description,
        Format colorFormat,
        Format depthStencilFormat)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _retirement = retirement ?? throw new ArgumentNullException(nameof(retirement));
        _debugNames = debugNames ?? throw new ArgumentNullException(nameof(debugNames));
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _packLayoutLease = packLayoutLease;
        _layout = layout;
        _cache = cache;
        _vertexModule = vertexModule;
        _fragmentModule = fragmentModule;
        _ownsShaderModules = ownsShaderModules;
        _depthStencilFormat = depthStencilFormat;
        Description = description ?? throw new ArgumentNullException(nameof(description));

        nint entryPoint = SilkMarshal.StringToPtr("main");
        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = (byte*)entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentModule,
                PName = (byte*)entryPoint,
            };

            GpuVertexLayout vertexLayout = description.VertexLayout;
            int bindingCount = vertexLayout.Bindings.Length;
            VertexInputBindingDescription* bindings =
                stackalloc VertexInputBindingDescription[Math.Max(1, bindingCount)];
            for (int i = 0; i < bindingCount; i++)
            {
                GpuVertexBinding declared = vertexLayout.Bindings[i];
                bindings[i] = new VertexInputBindingDescription
                {
                    Binding = declared.Binding,
                    Stride = declared.StrideBytes,
                    InputRate = declared.InputRate == GpuVertexInputRate.Instance
                        ? VertexInputRate.Instance
                        : VertexInputRate.Vertex,
                };
            }

            int attributeCount = vertexLayout.Attributes.Length;
            VertexInputAttributeDescription* attributes =
                stackalloc VertexInputAttributeDescription[Math.Max(1, attributeCount)];
            for (int i = 0; i < attributeCount; i++)
            {
                GpuVertexAttribute attribute = vertexLayout.Attributes[i];
                attributes[i] = new VertexInputAttributeDescription
                {
                    Location = attribute.Location,
                    Binding = attribute.Binding,
                    Format = VulkanViewportMapping.ToVulkan(attribute.Format),
                    Offset = attribute.OffsetBytes,
                };
            }

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindingCount,
                PVertexBindingDescriptions = bindingCount == 0 ? null : bindings,
                VertexAttributeDescriptionCount = (uint)attributeCount,
                PVertexAttributeDescriptions = attributeCount == 0 ? null : attributes,
            };
            var assembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = VulkanViewportMapping.ToVulkan(description.Topology),
                PrimitiveRestartEnable = false,
            };
            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = VulkanViewportMapping.ToVulkan(description.Cull),
                // The single inversion that pairs with the negative viewport
                // height. See VulkanViewportMapping.
                FrontFace = VulkanViewportMapping.ToVulkan(description.FrontFace),
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                DepthBiasEnable = false,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = VulkanTextureFormatMapping.SampleCountOf(description.SampleCount),
                SampleShadingEnable = false,
                AlphaToCoverageEnable = description.AlphaToCoverage && description.SampleCount > 1,
            };
            GpuStencilState stencil = description.Stencil;
            var stencilOps = new StencilOpState
            {
                FailOp = VulkanViewportMapping.ToVulkan(stencil.Fail),
                PassOp = VulkanViewportMapping.ToVulkan(stencil.Pass),
                DepthFailOp = VulkanViewportMapping.ToVulkan(stencil.DepthFail),
                CompareOp = VulkanViewportMapping.ToVulkan(stencil.Compare),
                CompareMask = stencil.CompareMask,
                WriteMask = stencil.WriteMask,
                Reference = stencil.Reference,
            };
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = description.Depth.Test,
                DepthWriteEnable = description.Depth.Write,
                DepthCompareOp = VulkanViewportMapping.ToVulkan(description.Depth.Compare),
                DepthBoundsTestEnable = false,
                StencilTestEnable = description.StencilTest,
                Front = stencilOps,
                Back = stencilOps,
            };

            (BlendFactor source, BlendFactor destination) =
                VulkanViewportMapping.BlendFactorsOf(description.Blend);
            var attachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = description.Blend != GpuBlendMode.None,
                SrcColorBlendFactor = source,
                DstColorBlendFactor = destination,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = source,
                DstAlphaBlendFactor = destination,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = description.ColorWrite
                    ? ColorComponentFlags.RBit | ColorComponentFlags.GBit
                        | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                    : 0,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = description.HasColorAttachment ? 1u : 0u,
                PAttachments = description.HasColorAttachment ? &attachment : null,
            };

            DynamicState* dynamicStates = stackalloc DynamicState[9];
            dynamicStates[0] = DynamicState.Viewport;
            dynamicStates[1] = DynamicState.Scissor;
            dynamicStates[2] = DynamicState.CullMode;
            dynamicStates[3] = DynamicState.FrontFace;
            dynamicStates[4] = DynamicState.DepthWriteEnable;
            uint dynamicStateCount = 5;
            if (description.StencilTest)
            {
                dynamicStates[5] = DynamicState.StencilOp;
                dynamicStates[6] = DynamicState.StencilCompareMask;
                dynamicStates[7] = DynamicState.StencilWriteMask;
                dynamicStates[8] = DynamicState.StencilReference;
                dynamicStateCount = 9;
            }
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = dynamicStateCount,
                PDynamicStates = dynamicStates,
            };

            Format color = colorFormat;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ViewMask = description.ViewMask,
                ColorAttachmentCount = description.HasColorAttachment ? 1u : 0u,
                PColorAttachmentFormats = description.HasColorAttachment ? &color : null,
                DepthAttachmentFormat = depthStencilFormat,
                StencilAttachmentFormat = depthStencilFormat,
            };

            var create = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
                // No RenderPass: dynamic rendering declares the formats inline.
                RenderPass = default,
                Subpass = 0,
            };

            VulkanInterop.Check(
                _vk.CreateGraphicsPipelines(_device, cache, 1, &create, null, out Pipeline withDepth),
                $"vkCreateGraphicsPipelines ('{description.Name}', depth attachment)");
            _withDepthAttachment = withDepth;
            debugNames.NamePipeline(withDepth, description.Name);

            rendering.DepthAttachmentFormat = Format.Undefined;
            rendering.StencilAttachmentFormat = Format.Undefined;
            try
            {
                VulkanInterop.Check(
                    _vk.CreateGraphicsPipelines(_device, cache, 1, &create, null, out Pipeline withoutDepth),
                    $"vkCreateGraphicsPipelines ('{description.Name}', no depth attachment)");
                _withoutDepthAttachment = withoutDepth;
                debugNames.NamePipeline(withoutDepth, $"{description.Name}-nodepth");
            }
            catch
            {
                _vk.DestroyPipeline(_device, withDepth, null);
                throw;
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }

    public GpuPipelineDescription Description { get; }

    internal Pipeline HandleFor(bool passHasDepthAttachment) =>
        passHasDepthAttachment ? _withDepthAttachment : _withoutDepthAttachment;

    /// <summary>
    /// Selects the prebuilt attachment-format variant required by the live pass.
    /// Missing variants fail before a draw can record undefined Vulkan usage.
    /// </summary>
    internal Pipeline HandleFor(
        bool passHasDepthAttachment,
        GpuTextureFormat colorFormat)
    {
        if (colorFormat == Description.ColorFormat)
            return HandleFor(passHasDepthAttachment);
        if (_colorVariants.TryGetValue(colorFormat, out VulkanGpuPipeline? variant))
            return variant.HandleFor(passHasDepthAttachment);
        throw new InvalidOperationException(
            $"Pipeline '{Description.Name}' has no prebuilt {colorFormat} attachment variant.");
    }

    internal bool IsDisposed => _disposed;

    internal PipelineLayout PipelineLayout => _layout;

    /// <summary>Non-null only for a pipeline flagged for render-pack ABI v1.</summary>
    internal VulkanPipelineLayouts.Created.PackState? PackState => _packLayoutLease?.State;

    internal bool AddColorFormatVariant(GpuTextureFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Description.HasColorAttachment
            || !Description.AllowColorFormatVariants
            || format == Description.ColorFormat
            || _colorVariants.ContainsKey(format))
            return false;

        var variantDescription = Description with
        {
            Name = $"{Description.Name}-{format.ToString().ToLowerInvariant()}",
            ColorFormat = format,
            AllowColorFormatVariants = false,
        };
        VulkanPipelineLayouts.Created.PackLayoutLease? packLease =
            Description.UsesRenderPackShaderAbi ? _layouts.AcquirePackLayout() : null;
        VulkanGpuPipeline variant;
        try
        {
            variant = new VulkanGpuPipeline(
                _vk,
                _device,
                _retirement,
                _debugNames,
                _layouts,
                packLease,
                packLease?.PipelineLayout ?? _layouts.PipelineLayout,
                _cache,
                _vertexModule,
                _fragmentModule,
                ownsShaderModules: false,
                variantDescription,
                VulkanTextureFormatMapping.FormatOf(format),
                _depthStencilFormat);
        }
        catch
        {
            packLease?.Dispose();
            throw;
        }
        _colorVariants.Add(format, variant);
        return true;
    }

    internal void RemoveColorFormatVariant(GpuTextureFormat format)
    {
        if (_colorVariants.Remove(format, out VulkanGpuPipeline? variant))
            variant.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (VulkanGpuPipeline variant in _colorVariants.Values)
            variant.Dispose();
        _colorVariants.Clear();
        Pipeline withDepth = _withDepthAttachment;
        Pipeline withoutDepth = _withoutDepthAttachment;
        ShaderModule vertex = _vertexModule;
        ShaderModule fragment = _fragmentModule;
        bool destroyModules = _ownsShaderModules;
        VulkanPipelineLayouts.Created.PackLayoutLease? packLease = _packLayoutLease;
        _retirement.Retire(() =>
        {
            _vk.DestroyPipeline(_device, withDepth, null);
            _vk.DestroyPipeline(_device, withoutDepth, null);
            if (destroyModules)
            {
                _vk.DestroyShaderModule(_device, fragment, null);
                _vk.DestroyShaderModule(_device, vertex, null);
            }
            packLease?.Dispose();
        });
    }
}

internal sealed unsafe class VulkanPipelineCache : IDisposable
{
    private const uint HeaderLengthBytes = 32;
    private const uint HeaderVersionOne = 1;

    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly string? _path;
    private bool _disposed;

    internal VulkanPipelineCache(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        string? cacheDirectory)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;

        vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);
        byte[] pipelineCacheUuid = new byte[16];
        for (int i = 0; i < 16; i++)
            pipelineCacheUuid[i] = properties.PipelineCacheUuid[i];

        byte[]? initial = null;
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            _path = Path.Combine(cacheDirectory, "vulkan-pipeline-cache.bin");
            initial = TryReadCompatible(_path, properties.VendorID, properties.DeviceID, pipelineCacheUuid);
        }

        LoadedFromDisk = initial is not null;
        fixed (byte* data = initial)
        {
            var create = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)(initial?.Length ?? 0),
                PInitialData = initial is null ? null : data,
            };
            VulkanInterop.Check(
                _vk.CreatePipelineCache(_device, &create, null, out PipelineCache cache),
                "vkCreatePipelineCache");
            Handle = cache;
        }
    }

    internal PipelineCache Handle { get; }

    internal bool LoadedFromDisk { get; }

    internal static byte[]? ValidateHeader(
        byte[]? blob,
        uint vendorId,
        uint deviceId,
        ReadOnlySpan<byte> pipelineCacheUuid)
    {
        if (blob is null || blob.Length < HeaderLengthBytes)
            return null;

        uint length = BitConverter.ToUInt32(blob, 0);
        uint version = BitConverter.ToUInt32(blob, 4);
        uint blobVendor = BitConverter.ToUInt32(blob, 8);
        uint blobDevice = BitConverter.ToUInt32(blob, 12);
        if (length != HeaderLengthBytes || version != HeaderVersionOne)
            return null;
        if (blobVendor != vendorId || blobDevice != deviceId)
            return null;
        if (!blob.AsSpan(16, 16).SequenceEqual(pipelineCacheUuid))
            return null;

        return blob;
    }

    private static byte[]? TryReadCompatible(
        string path,
        uint vendorId,
        uint deviceId,
        ReadOnlySpan<byte> pipelineCacheUuid)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return ValidateHeader(File.ReadAllBytes(path), vendorId, deviceId, pipelineCacheUuid);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Save()
    {
        if (_disposed || _path is null)
            return;

        try
        {
            nuint size = 0;
            if (_vk.GetPipelineCacheData(_device, Handle, ref size, null) != Result.Success || size == 0)
                return;

            var data = new byte[(int)size];
            fixed (byte* first = data)
            {
                if (_vk.GetPipelineCacheData(_device, Handle, ref size, first) != Result.Success)
                    return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Save();
        _disposed = true;
        if (Handle.Handle != 0)
            _vk.DestroyPipelineCache(_device, Handle, null);
    }
}
