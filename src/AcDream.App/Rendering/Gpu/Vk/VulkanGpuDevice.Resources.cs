using System.Numerics;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe partial class VulkanGpuDevice
{
    private VulkanPipelineLayouts.Created? _layouts;
    private VulkanTextureTable? _textureTable;
    private VulkanBackbufferAttachments? _backbufferAttachments;
    private VulkanGpuTexture? _defaultTexture;
    private VulkanPipelineCache? _pipelineCache;
    private VulkanGpuTimerPool? _timerPool;
    private VulkanGpuBuffer? _bindingDummy;
    private VulkanFrameBindings[] _frameBindings = [];

    private readonly Dictionary<GpuSamplerDescription, VulkanGpuSampler> _samplers = [];
    private readonly Dictionary<string, (ShaderModule Vertex, ShaderModule Fragment)> _shaderModules = [];
    private readonly HashSet<VulkanGpuPipeline> _pipelines = [];
    private readonly Dictionary<GpuTextureFormat, int> _pipelineFormatLeaseCounts = [];
    private readonly object _resourceCreationSync = new();
    private string _shaderSpirvDirectory = string.Empty;
    private float _maxSamplerAnisotropy = 1f;

    private VulkanGpuPassEncoder? _openPass;
    private bool _openPassIsBackbuffer;

    private const string FrameTimerScopeName = "frame";

    private int[] _frameTimerTags = [];
    private readonly Queue<(int Tag, long ElapsedUs)> _frameGpuSamples = new();
    private IDisposable? _openFrameTimerScope;

    private void InitialiseResources(string? shaderSpirvDirectory, string? pipelineCacheDirectory)
    {
        _shaderSpirvDirectory = shaderSpirvDirectory ?? string.Empty;

        _vk.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        _maxSamplerAnisotropy = properties.Limits.MaxSamplerAnisotropy;

        _layouts = VulkanPipelineLayouts.Create(_vk, _device);
        _pipelineCache = new VulkanPipelineCache(_vk, _physicalDevice, _device, pipelineCacheDirectory);
        _timerPool = new VulkanGpuTimerPool(
            _vk,
            _physicalDevice,
            _device,
            _flights.SlotCount,
            Capabilities.SupportsTimestampQueries);
        _frameTimerTags = new int[_flights.SlotCount];
        Array.Fill(_frameTimerTags, -1);
        _textureTable = new VulkanTextureTable(
            _vk,
            _device,
            _layouts.TextureTable,
            Math.Min(GpuBindingModel.TextureTableCapacity, Capabilities.MaxTextureTableSlots));
        _backbufferAttachments = new VulkanBackbufferAttachments(
            _vk,
            _device,
            _allocator,
            _debugNames,
            DepthStencilFormat);

        // The default slot is registered first so it is slot 0 and so the table
        // has something defined to scrub evicted slots with. GpuTextureSlot
        // documents Unassigned as a loud sentinel precisely so nothing silently
        // resolves to slot 0 — this texture exists for the renderers that
        // legitimately need a fallback and ask for it by name.
        _defaultTexture = new VulkanGpuTexture(
            _vk,
            _device,
            _allocator,
            _uploads,
            _flights,
            _debugNames,
            new GpuTextureDescription(
                "vk-default-white",
                GpuTextureKind.Texture2DArray,
                GpuTextureFormat.Rgba8Unorm,
                Width: 1,
                Height: 1,
                LayerCount: 1,
                MipLevelCount: 1));
        _defaultTexture.Upload(0, 0, [255, 255, 255, 255]);

        var defaultSampler = (VulkanGpuSampler)CreateSampler(GpuSamplerDescription.UiNearest);
        _textureTable.SetScrubTarget(_defaultTexture.View, defaultSampler.Handle);
        DefaultTextureSlot = _textureTable.Register(_defaultTexture.View, defaultSampler.Handle);

        // One dummy range every unused binding points at, so there is a single
        // descriptor set layout rather than a permutation per renderer.
        _bindingDummy = new VulkanGpuBuffer(
            _vk,
            _device,
            _allocator,
            _uploads,
            _flights,
            _debugNames,
            new GpuBufferDescription(
                "vk-binding-dummy",
                65536,
                GpuBufferUsage.Storage | GpuBufferUsage.Uniform,
                GpuMemoryResidency.HostWritable));

        _frameBindings = new VulkanFrameBindings[_flights.SlotCount];
        for (int slot = 0; slot < _flights.SlotCount; slot++)
        {
            _frameBindings[slot] = new VulkanFrameBindings(
                _vk,
                _device,
                _layouts,
                _ringBuffers[slot],
                _bindingDummy,
                Capabilities.MaxStorageBufferRangeBytes);
        }
    }

    private void BeginFrameResources(int slotIndex)
    {
        if (_timerPool is null)
            return;

        // BeginSlot reads back whatever this slot recorded the last time it was
        // used. That measurement belongs to the frame whose tag the slot still
        // holds — read it BEFORE BeginFrameTimerScope overwrites the tag.
        int issuingTag = _frameTimerTags[slotIndex];
        _timerPool.BeginSlot(slotIndex);
        if (issuingTag >= 0
            && _timerPool.TryTakeResolved(FrameTimerScopeName, out double milliseconds))
        {
            _frameGpuSamples.Enqueue((issuingTag, (long)(milliseconds * 1000d)));
        }
    }

    internal void BeginFrameTimerScope(int frameIndex)
    {
        if (_openFrame is null || _timerPool is null || _openFrameTimerScope is not null)
            return;

        int slot = _openFrame.SlotIndex;
        _frameTimerTags[slot] = frameIndex;
        _openFrameTimerScope = _timerPool.BeginScope(_commandBuffers[slot], FrameTimerScopeName);
    }

    internal void EndFrameTimerScope()
    {
        _openFrameTimerScope?.Dispose();
        _openFrameTimerScope = null;
    }

    internal bool TryTakeFrameGpuSample(out int frameIndex, out long elapsedUs)
    {
        if (_frameGpuSamples.Count == 0)
        {
            frameIndex = -1;
            elapsedUs = 0;
            return false;
        }

        (frameIndex, elapsedUs) = _frameGpuSamples.Dequeue();
        return true;
    }

    private VulkanFrameBindings FrameBindingsAt(int slotIndex) => _frameBindings[slotIndex];

    private void EndFrameResources(int slotIndex, CommandBuffer commands)
    {
        _ = slotIndex;
        _ = commands;
        EndFrameTimerScope();
        if (_openPass is not null)
        {
            throw new InvalidOperationException(
                "A pass is still open at frame end. Dispose the encoder before ending the frame — " +
                "a dynamic-rendering block left open makes the whole command buffer invalid.");
        }
    }

    private void DisposeResources()
    {
        _openFrameTimerScope = null;
        _frameGpuSamples.Clear();
        _frameTimerTags = [];
        foreach (VulkanFrameBindings bindings in _frameBindings)
            bindings.Dispose();
        _frameBindings = [];

        foreach ((ShaderModule vertex, ShaderModule fragment) in _shaderModules.Values)
        {
            if (vertex.Handle != 0)
                _vk.DestroyShaderModule(_device, vertex, null);
            if (fragment.Handle != 0)
                _vk.DestroyShaderModule(_device, fragment, null);
        }

        _shaderModules.Clear();
        _pipelines.Clear();
        _pipelineFormatLeaseCounts.Clear();

        foreach (VulkanGpuSampler sampler in _samplers.Values)
            sampler.Dispose();
        _samplers.Clear();

        _bindingDummy?.Dispose();
        _bindingDummy = null;
        _captureBuffer?.Dispose();
        _captureBuffer = null;
        _defaultTexture?.Dispose();
        _defaultTexture = null;

        _flights.DrainAll();

        _timerPool?.Dispose();
        _timerPool = null;
        _pipelineCache?.Dispose();
        _pipelineCache = null;
        _backbufferAttachments?.Dispose();
        _backbufferAttachments = null;
        _textureTable?.Dispose();
        _textureTable = null;
        _layouts?.Destroy(_vk, _device);
        _layouts = null;
    }

    /// <summary>The three shared descriptor set layouts and the one pipeline layout.</summary>
    internal VulkanPipelineLayouts.Created Layouts =>
        _layouts ?? throw new InvalidOperationException("The device's pipeline layouts have not been created.");

    /// <summary>The global sampled-texture table (plan §4.4).</summary>
    internal VulkanTextureTable TextureTable =>
        _textureTable ?? throw new InvalidOperationException("The device's texture table has not been created.");

    internal VulkanBackbufferAttachments BackbufferAttachments =>
        _backbufferAttachments ?? throw new InvalidOperationException("The backbuffer attachments have not been created.");

    internal VulkanGpuTimerPool TimerPool =>
        _timerPool ?? throw new InvalidOperationException("The device's timer pool has not been created.");

    internal bool PipelineCacheLoadedFromDisk => _pipelineCache?.LoadedFromDisk ?? false;

    public GpuTextureSlot DefaultTextureSlot { get; private set; } = GpuTextureSlot.Unassigned;

    public IGpuTimerPool Timers => TimerPool;

    internal void ConfigureBackbufferAttachments(uint width, uint height, Format colorFormat, int sampleCount)
    {
        BackbufferAttachments.Configure(width, height, colorFormat, sampleCount);
        ConfigureBackbufferCapture(width, height);
    }

    public IGpuTexture CreateTexture(in GpuTextureDescription description)
    {
        ThrowIfDisposed();
        if (description.Format == GpuTextureFormat.Rgba16FloatRenderTarget
            && !Capabilities.SupportsRgba16FloatRenderTargets)
        {
            throw new NotSupportedException(
                "RGBA16F colour-attachment, sampling, and linear filtering are unavailable.");
        }
        if (description.Format == GpuTextureFormat.Depth24Stencil8
            && !Capabilities.SupportsSampledDepth)
        {
            throw new NotSupportedException(
                "The selected combined depth/stencil format cannot expose a sampled depth aspect.");
        }
        return new VulkanGpuTexture(
            _vk,
            _device,
            _allocator,
            _uploads,
            _flights,
            _debugNames,
            description,
            sampleCount: 1,
            renderTarget: VulkanTextureFormatMapping.IsRenderTarget(description.Format));
    }

    public IGpuSampler CreateSampler(in GpuSamplerDescription description)
    {
        lock (_resourceCreationSync)
            return CreateSamplerLocked(in description);
    }

    private IGpuSampler CreateSamplerLocked(in GpuSamplerDescription description)
    {
        ThrowIfDisposed();
        if (_samplers.TryGetValue(description, out VulkanGpuSampler? existing)
            && !existing.IsDisposed)
            return existing;

        var created = new VulkanGpuSampler(
            _vk,
            _device,
            _flights,
            _debugNames,
            description,
            _maxSamplerAnisotropy);
        _samplers[description] = created;
        return created;
    }

    public IGpuRenderTarget CreateRenderTarget(in GpuRenderTargetDescription description)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.SampleCount);
        if ((uint)description.SampleCount > Capabilities.MaxSampleCount)
        {
            throw new NotSupportedException(
                $"The device supports at most {Capabilities.MaxSampleCount} colour/depth samples; "
                + $"'{description.Name}' requested {description.SampleCount}.");
        }
        if (description.ColorFormat == GpuTextureFormat.Rgba16FloatRenderTarget)
        {
            if (!Capabilities.SupportsRgba16FloatRenderTargets)
            {
                throw new NotSupportedException(
                    "RGBA16F colour-attachment, sampling, and linear filtering are required by this render target.");
            }
            if ((uint)description.SampleCount > Capabilities.MaxRgba16FloatSampleCount)
            {
                throw new NotSupportedException(
                    $"RGBA16F supports at most {Capabilities.MaxRgba16FloatSampleCount} samples on this device; "
                    + $"'{description.Name}' requested {description.SampleCount}.");
            }
        }
        if (description.SampleableDepth && !Capabilities.SupportsSampledDepth)
        {
            throw new NotSupportedException(
                "The selected combined depth/stencil format cannot expose a sampled depth aspect.");
        }
        return new VulkanGpuRenderTarget(
            _vk,
            _device,
            _allocator,
            _uploads,
            _flights,
            _debugNames,
            description,
            DepthStencilFormat);
    }

    public IGpuDirectionalDepthTarget CreateDirectionalDepthTarget(
        in GpuDirectionalDepthTargetDescription description)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(description.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Resolution);
        if (description.LayerCount is < 2 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(description),
                description.LayerCount,
                "Directional depth targets require 2-4 cascade layers.");
        }
        if (description.DepthFormat != GpuTextureFormat.Depth24Stencil8)
        {
            throw new ArgumentException(
                "Directional depth targets currently require Depth24Stencil8.",
                nameof(description));
        }
        if (!Capabilities.SupportsSampledDepth)
            throw new NotSupportedException("Sampled depth is unavailable on this device.");

        return new VulkanDirectionalDepthTarget(
            _vk,
            _device,
            _allocator,
            _uploads,
            _flights,
            _debugNames,
            description,
            DepthStencilFormat);
    }

    public GpuTextureSlot RegisterTexture(IGpuTexture texture, IGpuSampler sampler)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(sampler);
        if (texture is not VulkanGpuTexture vulkanTexture)
            throw new ArgumentException("The Vulkan backend can only register a Vulkan texture.", nameof(texture));
        if (sampler is not VulkanGpuSampler vulkanSampler)
            throw new ArgumentException("The Vulkan backend can only register a Vulkan sampler.", nameof(sampler));
        if (!vulkanTexture.IsSampleable || vulkanTexture.SampledView.Handle == 0)
        {
            throw new ArgumentException(
                $"Texture '{vulkanTexture.Name}' is an attachment-only image and has no sampled view.",
                nameof(texture));
        }

        return TextureTable.Register(
            vulkanTexture.SampledView,
            vulkanSampler.Handle,
            vulkanTexture.SampledLayout);
    }

    public void ReleaseTextureSlot(GpuTextureSlot slot)
    {
        ThrowIfDisposed();
        if (!slot.IsAssigned)
            throw new ArgumentException("Cannot release an unassigned texture slot.", nameof(slot));

        VulkanTextureTable table = TextureTable;
        _flights.Retire(() => table.ReleaseNow(slot));
    }

    public IGpuPipeline CreatePipeline(GpuPipelineDescription description)
    {
        lock (_resourceCreationSync)
            return CreatePipelineLocked(description);
    }

    private IGpuPipeline CreatePipelineLocked(GpuPipelineDescription description)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(description);
        if (description.ViewMask != 0 && !Capabilities.SupportsMultiview)
            throw new NotSupportedException("The selected Vulkan device does not support multiview pipelines.");

        (ShaderModule vertex, ShaderModule fragment, bool ownsModules) =
            LoadShaderModules(description.Shaders);
        Format colorFormat = VulkanTextureFormatMapping.FormatOf(description.ColorFormat);
        VulkanGpuPipeline pipeline;
        VulkanPipelineLayouts.Created.PackLayoutLease? packLease = null;
        try
        {
            packLease = description.UsesRenderPackShaderAbi
                ? Layouts.AcquirePackLayout()
                : null;
            pipeline = new VulkanGpuPipeline(
                _vk,
                _device,
                _flights,
                _debugNames,
                Layouts,
                packLease,
                packLease?.PipelineLayout ?? Layouts.PipelineLayout,
                _pipelineCache?.Handle ?? default,
                vertex,
                fragment,
                ownsModules,
                description,
                colorFormat,
                DepthStencilFormat);
        }
        catch
        {
            packLease?.Dispose();
            if (ownsModules)
            {
                _vk.DestroyShaderModule(_device, fragment, null);
                _vk.DestroyShaderModule(_device, vertex, null);
            }
            throw;
        }
        try
        {
            foreach (GpuTextureFormat format in _pipelineFormatLeaseCounts.Keys)
                pipeline.AddColorFormatVariant(format);
            _pipelines.Add(pipeline);
            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    public IDisposable AcquirePipelineColorFormat(GpuTextureFormat format)
    {
        lock (_resourceCreationSync)
            return AcquirePipelineColorFormatLocked(format);
    }

    private IDisposable AcquirePipelineColorFormatLocked(GpuTextureFormat format)
    {
        ThrowIfDisposed();
        if (!VulkanTextureFormatMapping.IsRenderTarget(format)
            || VulkanTextureFormatMapping.IsDepthStencil(format))
        {
            throw new ArgumentException(
                $"{format} is not a colour render-target format.",
                nameof(format));
        }
        if (format == GpuTextureFormat.Rgba16FloatRenderTarget
            && !Capabilities.SupportsRgba16FloatRenderTargets)
        {
            throw new NotSupportedException(
                "RGBA16F colour-attachment, sampling, and linear filtering are unavailable.");
        }

        _pipelines.RemoveWhere(static pipeline => pipeline.IsDisposed);
        if (!_pipelineFormatLeaseCounts.TryGetValue(format, out int count))
        {
            var added = new List<VulkanGpuPipeline>(_pipelines.Count);
            try
            {
                foreach (VulkanGpuPipeline pipeline in _pipelines)
                {
                    if (pipeline.AddColorFormatVariant(format))
                        added.Add(pipeline);
                }
            }
            catch
            {
                foreach (VulkanGpuPipeline pipeline in added)
                    pipeline.RemoveColorFormatVariant(format);
                throw;
            }
            _pipelineFormatLeaseCounts.Add(format, 1);
        }
        else
        {
            _pipelineFormatLeaseCounts[format] = checked(count + 1);
        }

        return new PipelineColorFormatLease(this, format);
    }

    private void ReleasePipelineColorFormat(GpuTextureFormat format)
    {
        lock (_resourceCreationSync)
        {
            if (_disposed || !_pipelineFormatLeaseCounts.TryGetValue(format, out int count))
                return;
            if (count > 1)
            {
                _pipelineFormatLeaseCounts[format] = count - 1;
                return;
            }

            _pipelineFormatLeaseCounts.Remove(format);
            _pipelines.RemoveWhere(static pipeline => pipeline.IsDisposed);
            foreach (VulkanGpuPipeline pipeline in _pipelines)
                pipeline.RemoveColorFormatVariant(format);
        }
    }

    private sealed class PipelineColorFormatLease(
        VulkanGpuDevice device,
        GpuTextureFormat format) : IDisposable
    {
        private VulkanGpuDevice? _device = device;

        public void Dispose() =>
            Interlocked.Exchange(ref _device, null)?.ReleasePipelineColorFormat(format);
    }

    private (ShaderModule Vertex, ShaderModule Fragment, bool OwnsModules) LoadShaderModules(
        in GpuShaderSet shaders)
    {
        if (shaders.HasEmbeddedSpirv)
        {
            ShaderModule embeddedVertex = CreateShaderModule(
                shaders.Name,
                "vert",
                shaders.VertexSpirv.Span);
            try
            {
                return (
                    embeddedVertex,
                    CreateShaderModule(shaders.Name, "frag", shaders.FragmentSpirv.Span),
                    true);
            }
            catch
            {
                _vk.DestroyShaderModule(_device, embeddedVertex, null);
                throw;
            }
        }

        string name = shaders.Name;
        if (_shaderModules.TryGetValue(name, out (ShaderModule Vertex, ShaderModule Fragment) existing))
            return (existing.Vertex, existing.Fragment, false);

        ShaderModule vertex = CreateShaderModule(name, "vert");
        ShaderModule fragment = CreateShaderModule(name, "frag");
        _shaderModules[name] = (vertex, fragment);
        return (vertex, fragment, false);
    }

    private ShaderModule CreateShaderModule(string name, string stage)
    {
        string path = Path.Combine(_shaderSpirvDirectory, $"{name}.{stage}.spv");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No committed SPIR-V for '{name}.{stage}'. Run tools/compile-shaders.ps1; if that " +
                "reports the shader as not yet Vulkan-expressible, its renderer-port slice has not " +
                "landed and no Vulkan pipeline can be built from it.",
                path);
        }

        return CreateShaderModule(name, stage, File.ReadAllBytes(path));
    }

    private ShaderModule CreateShaderModule(
        string name,
        string stage,
        ReadOnlySpan<byte> code)
    {
        if (code.Length < 4 || code.Length % 4 != 0)
            throw new InvalidDataException(
                $"'{name}.{stage}' is {code.Length} bytes, which is not valid word-aligned SPIR-V.");
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code) != 0x07230203u)
            throw new InvalidDataException($"'{name}.{stage}' has no SPIR-V header.");

        fixed (byte* first = code)
        {
            var create = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)first,
            };
            VulkanInterop.Check(
                _vk.CreateShaderModule(_device, &create, null, out ShaderModule module),
                $"vkCreateShaderModule ('{name}.{stage}')");
            return module;
        }
    }

    internal void CmdBindPipelineDefaults(CommandBuffer commands, GpuPipelineDescription description)
    {
        _vk.CmdSetCullMode(commands, VulkanViewportMapping.ToVulkan(description.Cull));
        _vk.CmdSetFrontFace(commands, VulkanViewportMapping.ToVulkan(description.FrontFace));
        _vk.CmdSetDepthWriteEnable(commands, description.Depth.Write);
        if (!description.StencilTest)
            return;

        GpuStencilState stencil = description.Stencil;
        const StencilFaceFlags BothFaces = StencilFaceFlags.FaceFrontAndBack;
        _vk.CmdSetStencilOp(
            commands,
            BothFaces,
            VulkanViewportMapping.ToVulkan(stencil.Fail),
            VulkanViewportMapping.ToVulkan(stencil.Pass),
            VulkanViewportMapping.ToVulkan(stencil.DepthFail),
            VulkanViewportMapping.ToVulkan(stencil.Compare));
        _vk.CmdSetStencilCompareMask(commands, BothFaces, stencil.CompareMask);
        _vk.CmdSetStencilWriteMask(commands, BothFaces, stencil.WriteMask);
        _vk.CmdSetStencilReference(commands, BothFaces, stencil.Reference);
    }

    internal IGpuPassEncoder BeginPass(VulkanGpuFrame frame, GpuPassDescription description)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(description);
        if (_openPass is not null)
            throw new InvalidOperationException("A pass is already open; dispose its encoder first.");

        CommandBuffer commands = _commandBuffers[frame.SlotIndex];

        _uploads.Record(commands);

        _debugNames.BeginLabel(commands, description.Name);

        uint width;
        uint height;
        ImageView colorView = default;
        ImageView resolveView = default;
        ImageView depthView = default;
        ImageView depthResolveView = default;
        bool hasColorAttachment = description.HasColorAttachment;
        bool backbuffer = hasColorAttachment && description.Color.Target is null;
        uint viewMask = description.ViewMask;
        GpuTextureFormat passColorFormat = GpuTextureFormat.Rgba8UnormRenderTarget;

        if (!hasColorAttachment)
        {
            if (description.SampleCount != 1)
                throw new InvalidOperationException("Directional depth passes are single-sampled.");
            if (description.Depth is not { DirectionalTarget: VulkanDirectionalDepthTarget target } depth)
            {
                throw new ArgumentException(
                    "A colour-less pass requires a Vulkan directional-depth target.",
                    nameof(description));
            }
            if (depth.Store != GpuStoreOp.Store)
                throw new InvalidOperationException("Directional depth must be stored for later sampling.");
            if (depth.Layer < 0 || depth.Layer >= target.Description.LayerCount)
                throw new ArgumentOutOfRangeException(nameof(description), "Directional depth layer is outside the target.");

            width = (uint)target.Description.Resolution;
            height = (uint)target.Description.Resolution;
            if (viewMask != 0)
            {
                if (!Capabilities.SupportsMultiview)
                    throw new NotSupportedException("The selected Vulkan device does not support multiview.");
                depthView = target.MultiviewView(viewMask);
                TransitionDirectionalDepthForRendering(
                    commands,
                    target,
                    baseLayer: 0,
                    layerCount: target.LayerCountForViewMask(viewMask));
            }
            else
            {
                depthView = target.ViewAt(depth.Layer);
                TransitionDirectionalDepthForRendering(commands, target, depth.Layer, 1);
            }
        }
        else if (backbuffer)
        {
            if (_backbuffer is null || _acquiredImageIndex is not { } imageIndex)
            {
                throw new InvalidOperationException(
                    "A pass declared Target: null, which the Vulkan backend honours literally as the " +
                    "swapchain image, but this device has no backbuffer or none was acquired for this frame.");
            }

            VulkanBackbufferAttachments attachments = BackbufferAttachments;
            width = _backbuffer.Width;
            height = _backbuffer.Height;
            TransitionBackbufferForRendering(commands, _backbuffer.ImageAt(imageIndex));

            if (attachments.HasMultisampledColor && description.SampleCount > 1)
            {
                colorView = attachments.ColorView;
                resolveView = _backbuffer.ViewAt(imageIndex);
                TransitionBackbufferScratchColor(commands, attachments);
            }
            else
            {
                colorView = _backbuffer.ViewAt(imageIndex);
            }

            if (description.Depth is not null && attachments.HasDepth)
            {
                depthView = attachments.DepthView;
                TransitionBackbufferDepth(commands, attachments);
            }
        }
        else
        {
            if (description.Color.Target is not VulkanGpuRenderTarget target)
                throw new ArgumentException("The Vulkan backend can only render into a Vulkan render target.");
            if (target.Description.SampleCount != description.SampleCount)
            {
                throw new InvalidOperationException(
                    $"Pass '{description.Name}' declares {description.SampleCount} samples but target "
                    + $"'{target.Description.Name}' was created for {target.Description.SampleCount}.");
            }
            width = (uint)target.Description.Width;
            height = (uint)target.Description.Height;
            passColorFormat = target.Description.ColorFormat;
            colorView = target.ColorAttachment.View;
            if (target.ColorResolve is { } colorResolve)
            {
                if (description.Color.Load == GpuLoadOp.Load)
                {
                    throw new InvalidOperationException(
                        $"Multisampled target '{target.Description.Name}' cannot Load a prior resolved image; "
                        + "its transient multisample attachment has no preserved contents.");
                }
                if (description.Color.Store != GpuStoreOp.Resolve)
                {
                    throw new InvalidOperationException(
                        $"Multisampled target '{target.Description.Name}' must use Store=Resolve so its "
                        + "single-sampled ColorTexture receives this pass.");
                }
                resolveView = colorResolve.View;
            }
            else if (description.Color.Store == GpuStoreOp.Resolve)
            {
                throw new InvalidOperationException(
                    $"Single-sampled target '{target.Description.Name}' cannot use Store=Resolve.");
            }
            TransitionRenderTargetForRendering(commands, target);
            if (description.Depth is not null && target.DepthAttachment is { } depth)
            {
                depthView = depth.View;
                if (target.Description.SampleCount > 1
                    && description.Depth.Value.Load == GpuLoadOp.Load)
                {
                    throw new InvalidOperationException(
                        $"Multisampled depth target '{target.Description.Name}' cannot Load transient depth.");
                }
                if (target.Description.SampleableDepth
                    && description.Depth.Value.Store != GpuStoreOp.Store)
                {
                    throw new InvalidOperationException(
                        $"Sampleable depth on '{target.Description.Name}' requires Store=Store.");
                }
                if (target.DepthResolve is { } depthResolve)
                {
                    depthResolveView = depthResolve.View;
                }
            }
        }

        RenderingAttachmentInfo colorAttachment = default;
        if (hasColorAttachment)
        {
            Vector4 clear = description.Color.ClearColor;
            colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = colorView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = VulkanViewportMapping.ToVulkan(description.Color.Load),
                StoreOp = description.Color.Store == GpuStoreOp.Resolve
                    ? AttachmentStoreOp.DontCare
                    : VulkanViewportMapping.ToVulkan(description.Color.Store),
                ClearValue = new ClearValue
                {
                    Color = new ClearColorValue
                    {
                        Float32_0 = clear.X,
                        Float32_1 = clear.Y,
                        Float32_2 = clear.Z,
                        Float32_3 = clear.W,
                    },
                },
            };
            if (resolveView.Handle != 0)
            {
                colorAttachment.ResolveMode = ResolveModeFlags.AverageBit;
                colorAttachment.ResolveImageView = resolveView;
                colorAttachment.ResolveImageLayout = ImageLayout.ColorAttachmentOptimal;
            }
        }

        RenderingAttachmentInfo depthAttachment = default;
        RenderingAttachmentInfo stencilAttachment = default;
        if (description.Depth is { } depthDescription && depthView.Handle != 0)
        {
            bool resolveDepth = depthResolveView.Handle != 0;
            depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depthView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = VulkanViewportMapping.ToVulkan(depthDescription.Load),
                StoreOp = resolveDepth
                    ? AttachmentStoreOp.DontCare
                    : VulkanViewportMapping.ToVulkan(depthDescription.Store),
                ClearValue = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue(
                        depthDescription.ClearDepth,
                        depthDescription.ClearStencil),
                },
            };
            stencilAttachment = depthAttachment;
            if (resolveDepth)
            {
                depthAttachment.ResolveMode = ResolveModeFlags.SampleZeroBit;
                depthAttachment.ResolveImageView = depthResolveView;
                depthAttachment.ResolveImageLayout = ImageLayout.DepthStencilAttachmentOptimal;
                stencilAttachment.ResolveMode = ResolveModeFlags.SampleZeroBit;
                stencilAttachment.ResolveImageView = depthResolveView;
                stencilAttachment.ResolveImageLayout = ImageLayout.DepthStencilAttachmentOptimal;
            }
        }

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
            LayerCount = 1,
            ViewMask = viewMask,
            ColorAttachmentCount = hasColorAttachment ? 1u : 0u,
            PColorAttachments = hasColorAttachment ? &colorAttachment : null,
            PDepthAttachment = depthAttachment.SType == StructureType.RenderingAttachmentInfo
                ? &depthAttachment
                : null,
            PStencilAttachment = stencilAttachment.SType == StructureType.RenderingAttachmentInfo
                ? &stencilAttachment
                : null,
        };
        _vk.CmdBeginRendering(commands, &rendering);

        _openPassIsBackbuffer = backbuffer;
        var encoder = new VulkanGpuPassEncoder(
            this,
            frame,
            commands,
            _frameBindings[frame.SlotIndex],
            description,
            width,
            height,
            hasDepthAttachment: depthView.Handle != 0,
            hasColorAttachment,
            colorFormat: passColorFormat);
        _openPass = encoder;
        return encoder;
    }

    internal void EndPass(VulkanGpuPassEncoder encoder)
    {
        if (!ReferenceEquals(_openPass, encoder))
            return;

        CommandBuffer commands = CurrentCommands;
        _vk.CmdEndRendering(commands);
        _debugNames.EndLabel(commands);

        if (!_openPassIsBackbuffer && encoder.Pass.Color.Target is VulkanGpuRenderTarget target)
        {
            TransitionRenderTargetForSampling(
                commands,
                target,
                colorStored: encoder.Pass.Color.Store != GpuStoreOp.DontCare,
                depthStored: encoder.Pass.Depth?.Store == GpuStoreOp.Store);
        }
        else if (encoder.Pass.Depth is
                 { DirectionalTarget: VulkanDirectionalDepthTarget directionalTarget } depth)
        {
            if (encoder.Pass.ViewMask != 0)
            {
                TransitionDirectionalDepthForSampling(
                    commands,
                    directionalTarget,
                    0,
                    directionalTarget.LayerCountForViewMask(encoder.Pass.ViewMask));
            }
            else
            {
                TransitionDirectionalDepthForSampling(commands, directionalTarget, depth.Layer, 1);
            }
        }

        _openPass = null;
    }

    private void TransitionBackbufferForRendering(CommandBuffer commands, Image image)
    {
        bool first = !_backbufferRenderingReady;
        _backbufferRenderingReady = true;

        ImageMemoryBarrier2 barrier = CreateBackbufferRenderingBarrier(image, first);
        SubmitImageBarrier(commands, barrier);
    }

    internal static ImageMemoryBarrier2 CreateBackbufferRenderingBarrier(
        Image image,
        bool first) => new()
    {
        SType = StructureType.ImageMemoryBarrier2,
        // The first image use is ordered by the acquire semaphore, whose wait
        // is scoped to this same stage. TopOfPipe here would run before that
        // wait and race the presentation engine's ownership/layout use.
        SrcStageMask = first
            ? AcquiredImageWaitStage
            : PipelineStageFlags2.ColorAttachmentOutputBit,
        SrcAccessMask = first
            ? AccessFlags2.None
            : AccessFlags2.ColorAttachmentWriteBit,
        DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        DstAccessMask = first
            ? AccessFlags2.ColorAttachmentWriteBit
            : AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
        OldLayout = first ? ImageLayout.Undefined : ImageLayout.ColorAttachmentOptimal,
        NewLayout = ImageLayout.ColorAttachmentOptimal,
        SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
        Image = image,
        SubresourceRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        },
    };

    private void TransitionBackbufferScratchColor(
        CommandBuffer commands,
        VulkanBackbufferAttachments attachments)
    {
        bool first = !attachments.ColorLayoutInitialized;
        attachments.MarkColorLayoutInitialized();
        TransitionImage(
            commands,
            attachments.ColorImage,
            ImageAspectFlags.ColorBit,
            first ? ImageLayout.Undefined : ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ColorAttachmentOptimal,
            first ? PipelineStageFlags2.TopOfPipeBit : PipelineStageFlags2.ColorAttachmentOutputBit,
            first ? AccessFlags2.None : AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);
    }

    private void TransitionBackbufferDepth(
        CommandBuffer commands,
        VulkanBackbufferAttachments attachments)
    {
        bool first = !attachments.DepthLayoutInitialized;
        attachments.MarkDepthLayoutInitialized();
        const PipelineStageFlags2 DepthStages =
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        TransitionImage(
            commands,
            attachments.DepthImage,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            first ? ImageLayout.Undefined : ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.DepthStencilAttachmentOptimal,
            first ? PipelineStageFlags2.TopOfPipeBit : DepthStages,
            first ? AccessFlags2.None : AccessFlags2.DepthStencilAttachmentWriteBit,
            DepthStages,
            AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit);
    }

    private void TransitionRenderTargetForRendering(CommandBuffer commands, VulkanGpuRenderTarget target)
    {
        VulkanGpuTexture colorAttachment = target.ColorAttachment;
        TransitionImage(
            commands,
            colorAttachment.Image,
            ImageAspectFlags.ColorBit,
            colorAttachment.CurrentLayout,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);
        colorAttachment.MarkLayout(ImageLayout.ColorAttachmentOptimal);

        if (target.ColorResolve is { } colorResolve)
        {
            TransitionImage(
                commands,
                colorResolve.Image,
                ImageAspectFlags.ColorBit,
                colorResolve.CurrentLayout,
                ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.None,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit);
            colorResolve.MarkLayout(ImageLayout.ColorAttachmentOptimal);
        }

        if (target.DepthAttachment is { } depth)
        {
            SubmitImageBarrier(commands, CreateRenderTargetDepthEntryBarrier(
                depth.Image,
                depth.CurrentLayout,
                fixedFunctionResolve: false));
            depth.MarkLayout(ImageLayout.DepthStencilAttachmentOptimal);
        }

        if (target.DepthResolve is { } depthResolve)
        {
            SubmitImageBarrier(commands, CreateRenderTargetDepthEntryBarrier(
                depthResolve.Image,
                depthResolve.CurrentLayout,
                fixedFunctionResolve: true));
            depthResolve.MarkLayout(ImageLayout.DepthStencilAttachmentOptimal);
        }
    }

    internal void PublishHostStorageWrites(
        VulkanGpuFrame frame,
        IGpuBuffer buffer)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(buffer);
        if (!ReferenceEquals(_openFrame, frame))
            throw new InvalidOperationException("Host writes require the open Vulkan frame.");
        if (_openPass is not null)
        {
            throw new InvalidOperationException(
                "Retained host writes must be published before opening a rendering pass.");
        }
        if (buffer is not VulkanGpuBuffer vkBuffer
            || buffer.Residency != GpuMemoryResidency.HostWritable
            || !buffer.Usage.HasFlag(GpuBufferUsage.Storage))
        {
            throw new ArgumentException(
                "Published host writes require a Vulkan host-writable storage buffer.",
                nameof(buffer));
        }

        CommandBuffer commands = _commandBuffers[frame.SlotIndex];
        BufferMemoryBarrier2 barrier = VulkanHostStorageVisibility.Create(
            vkBuffer.Handle,
            checked((ulong)vkBuffer.SizeBytes));
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier,
        };
        _vk.CmdPipelineBarrier2(commands, &dependency);
    }

    private void TransitionRenderTargetForSampling(
        CommandBuffer commands,
        VulkanGpuRenderTarget target,
        bool colorStored,
        bool depthStored)
    {
        if (colorStored)
        {
            VulkanGpuTexture color = target.ColorResult;
            TransitionImage(
                commands,
                color.Image,
                ImageAspectFlags.ColorBit,
                color.CurrentLayout,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderReadBit);
            color.MarkLayout(ImageLayout.ShaderReadOnlyOptimal);
        }

        if (depthStored && target.Description.SampleableDepth && target.DepthResult is { } depth)
        {
            SubmitImageBarrier(commands, CreateRenderTargetDepthSamplingBarrier(
                depth.Image,
                depth.CurrentLayout,
                fixedFunctionResolve: target.DepthResolve is not null));
            depth.MarkLayout(ImageLayout.DepthStencilReadOnlyOptimal);
        }
    }

    internal static ImageMemoryBarrier2 CreateRenderTargetDepthEntryBarrier(
        Image image,
        ImageLayout oldLayout,
        bool fixedFunctionResolve)
    {
        PipelineStageFlags2 writerStage = fixedFunctionResolve
            ? PipelineStageFlags2.ColorAttachmentOutputBit
            : PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        AccessFlags2 writerAccess = fixedFunctionResolve
            ? AccessFlags2.ColorAttachmentWriteBit
            : AccessFlags2.DepthStencilAttachmentWriteBit;
        return CreateImageBarrier(
            image,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            oldLayout,
            ImageLayout.DepthStencilAttachmentOptimal,
            PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.None,
            writerStage,
            writerAccess);
    }

    internal static ImageMemoryBarrier2 CreateRenderTargetDepthSamplingBarrier(
        Image image,
        ImageLayout oldLayout,
        bool fixedFunctionResolve)
    {
        PipelineStageFlags2 writerStage = fixedFunctionResolve
            ? PipelineStageFlags2.ColorAttachmentOutputBit
            : PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        AccessFlags2 writerAccess = fixedFunctionResolve
            ? AccessFlags2.ColorAttachmentWriteBit
            : AccessFlags2.DepthStencilAttachmentWriteBit;
        return CreateImageBarrier(
            image,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            oldLayout,
            ImageLayout.DepthStencilReadOnlyOptimal,
            writerStage,
            writerAccess,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderReadBit);
    }

    private void TransitionDirectionalDepthForRendering(
        CommandBuffer commands,
        VulkanDirectionalDepthTarget target,
        int baseLayer,
        int layerCount)
    {
        const PipelineStageFlags2 DepthStages =
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        ImageLayout oldLayout = target.LayoutAt(baseLayer);
        for (int i = 1; i < layerCount; i++)
        {
            if (target.LayoutAt(baseLayer + i) != oldLayout)
                throw new InvalidOperationException("Multiview directional layers must share one layout.");
        }
        TransitionImage(
            commands,
            target.Texture.Image,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            oldLayout,
            ImageLayout.DepthStencilAttachmentOptimal,
            oldLayout == ImageLayout.Undefined ? PipelineStageFlags2.TopOfPipeBit : PipelineStageFlags2.FragmentShaderBit,
            oldLayout == ImageLayout.Undefined ? AccessFlags2.None : AccessFlags2.ShaderReadBit,
            DepthStages,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            baseArrayLayer: (uint)baseLayer,
            layerCount: (uint)layerCount);
        for (int i = 0; i < layerCount; i++)
            target.MarkLayout(baseLayer + i, ImageLayout.DepthStencilAttachmentOptimal);
    }

    private void TransitionDirectionalDepthForSampling(
        CommandBuffer commands,
        VulkanDirectionalDepthTarget target,
        int baseLayer,
        int layerCount)
    {
        TransitionImage(
            commands,
            target.Texture.Image,
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            target.LayoutAt(baseLayer),
            ImageLayout.DepthStencilReadOnlyOptimal,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderReadBit,
            baseArrayLayer: (uint)baseLayer,
            layerCount: (uint)layerCount);
        for (int i = 0; i < layerCount; i++)
            target.MarkLayout(baseLayer + i, ImageLayout.DepthStencilReadOnlyOptimal);
    }

    private void TransitionImage(
        CommandBuffer commands,
        Image image,
        ImageAspectFlags aspect,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess,
        uint baseArrayLayer = 0,
        uint layerCount = Silk.NET.Vulkan.Vk.RemainingArrayLayers)
    {
        SubmitImageBarrier(commands, CreateImageBarrier(
            image,
            aspect,
            oldLayout,
            newLayout,
            sourceStage,
            sourceAccess,
            destinationStage,
            destinationAccess,
            baseArrayLayer,
            layerCount));
    }

    private static ImageMemoryBarrier2 CreateImageBarrier(
        Image image,
        ImageAspectFlags aspect,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess,
        uint baseArrayLayer = 0,
        uint layerCount = Silk.NET.Vulkan.Vk.RemainingArrayLayers) => new()
    {
        SType = StructureType.ImageMemoryBarrier2,
        SrcStageMask = sourceStage,
        SrcAccessMask = sourceAccess,
        DstStageMask = destinationStage,
        DstAccessMask = destinationAccess,
        OldLayout = oldLayout,
        NewLayout = newLayout,
        SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
        Image = image,
        SubresourceRange = new ImageSubresourceRange
        {
            AspectMask = aspect,
            BaseMipLevel = 0,
            LevelCount = Silk.NET.Vulkan.Vk.RemainingMipLevels,
            BaseArrayLayer = baseArrayLayer,
            LayerCount = layerCount,
        },
    };

    private void SubmitImageBarrier(CommandBuffer commands, ImageMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _vk.CmdPipelineBarrier2(commands, &dependency);
    }

    public byte[] CaptureBackbuffer(int width, int height)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_backbuffer is null)
            throw new InvalidOperationException("This device has no backbuffer to capture.");
        if (_captureBuffer is null)
        {
            throw new InvalidOperationException(
                "Backbuffer capture was not retained by this device. Construct it with " +
                "retainBackbufferCapture: true — reading the presented swapchain image " +
                "instead is a Vulkan usage error (see this method's remarks).");
        }
        if (width != _captureWidth || height != _captureHeight)
        {
            throw new ArgumentException(
                $"The retained capture is {_captureWidth}x{_captureHeight}; {width}x{height} was requested. " +
                "The capture buffer is sized with the swapchain, so a mismatch means the caller " +
                "and the backbuffer disagree about the frame that was just presented.");
        }
        if (!_captureValidity.HasSubmittedCopy)
        {
            throw new InvalidOperationException(
                "No retained capture copy has completed submission for the current backbuffer size.");
        }
        if (!_captureBuffer.HostWritesAreCoherent)
        {
            throw new NotSupportedException(
                "Retained backbuffer capture requires host-coherent memory; " +
                "capture-local invalidation for non-coherent memory is not implemented.");
        }

        VulkanInterop.Check(_vk.DeviceWaitIdle(_device), "vkDeviceWaitIdle (capture)");
        var pixels = new byte[(long)_captureWidth * _captureHeight * 4];
        _captureBuffer.Read(0, pixels);
        return VulkanBackbufferSwizzle.ToRgba(pixels, width, height, width * 4);
    }

    internal ImageLayout RecordBackbufferCapture(CommandBuffer commands, Image image)
    {
        if (_captureBuffer is null || _backbuffer is null)
            return ImageLayout.ColorAttachmentOptimal;
        if (_captureWidth != _backbuffer.Width || _captureHeight != _backbuffer.Height)
            return ImageLayout.ColorAttachmentOptimal;

        TransitionImage(
            commands,
            image,
            ImageAspectFlags.ColorBit,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferReadBit);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(_captureWidth, _captureHeight, 1),
        };
        _vk.CmdCopyImageToBuffer(
            commands,
            image,
            ImageLayout.TransferSrcOptimal,
            _captureBuffer.Handle,
            1,
            &region);
        ulong copiedByteCount = checked((ulong)_captureWidth * _captureHeight * 4);
        var hostReadBarrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Silk.NET.Vulkan.Vk.QueueFamilyIgnored,
            Buffer = _captureBuffer.Handle,
            Offset = 0,
            Size = copiedByteCount,
        };
        var hostReadDependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &hostReadBarrier,
        };
        _vk.CmdPipelineBarrier2(commands, &hostReadDependency);
        _captureValidity.RecordCopy();
        return ImageLayout.TransferSrcOptimal;
    }

    private void ConfigureBackbufferCapture(uint width, uint height)
    {
        if (!_retainBackbufferCapture)
            return;
        if (_captureBuffer is not null && _captureWidth == width && _captureHeight == height)
            return;

        _captureValidity.Invalidate();
        _captureBuffer?.Dispose();
        _captureBuffer = null;
        _captureWidth = width;
        _captureHeight = height;
        if (width == 0 || height == 0)
            return;

        _captureBuffer = new VulkanGpuBuffer(
            _vk,
            _device,
            _allocator,
            _uploads,
            ImmediateGpuResourceRetirementQueue.Instance,
            _debugNames,
            new GpuBufferDescription(
                "vk-backbuffer-capture",
                width * height * 4,
                GpuBufferUsage.TransferDestination,
                GpuMemoryResidency.HostReadable));
    }

    private readonly bool _retainBackbufferCapture;
    private VulkanGpuBuffer? _captureBuffer;
    private uint _captureWidth;
    private uint _captureHeight;
    private VulkanBackbufferCaptureValidity _captureValidity;
    private bool _backbufferRenderingReady;
}

internal struct VulkanBackbufferCaptureValidity
{
    internal bool CopyRecorded { get; private set; }

    internal bool HasSubmittedCopy { get; private set; }

    internal void BeginFrame()
    {
        CopyRecorded = false;
        HasSubmittedCopy = false;
    }

    internal void RecordCopy() => CopyRecorded = true;

    internal void CompleteSubmission()
    {
        HasSubmittedCopy = CopyRecorded;
        CopyRecorded = false;
    }

    internal void Invalidate()
    {
        CopyRecorded = false;
        HasSubmittedCopy = false;
    }
}
