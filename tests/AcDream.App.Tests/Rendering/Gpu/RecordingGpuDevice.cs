using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering.Gpu;

internal abstract record GpuRecordedCall;

internal sealed record GpuRecordedFrameBegin(long Serial, int SlotIndex) : GpuRecordedCall;

internal sealed record GpuRecordedFrameEnd(long Serial) : GpuRecordedCall;

internal sealed record GpuRecordedRingAllocation(GpuRingUsage Usage, int ByteCount, uint OffsetBytes) : GpuRecordedCall;

internal sealed record GpuRecordedHostStorageVisibility(string BufferName) : GpuRecordedCall;

internal sealed record GpuRecordedPassBegin(string Name, int SampleCount, uint ViewMask = 0) : GpuRecordedCall;

internal sealed record GpuRecordedPassEnd(string Name) : GpuRecordedCall;

internal sealed record GpuRecordedTimerScope(string Name) : GpuRecordedCall;

internal sealed record GpuRecordedPipelineBind(string PipelineName) : GpuRecordedCall;

internal sealed record GpuRecordedStorageBind(uint Binding, string BufferName, uint OffsetBytes, uint SizeBytes)
    : GpuRecordedCall;

internal sealed record GpuRecordedUniformBind(uint Binding, string BufferName, uint OffsetBytes, uint SizeBytes)
    : GpuRecordedCall;

internal sealed record GpuRecordedVertexBind(uint Binding, string BufferName, uint OffsetBytes) : GpuRecordedCall;
internal sealed record GpuRecordedStencil(GpuStencilState Stencil) : GpuRecordedCall;

internal sealed record GpuRecordedIndexBind(string BufferName, uint OffsetBytes, GpuIndexType IndexType)
    : GpuRecordedCall;

internal sealed record GpuRecordedPushConstants(GpuPushConstants Constants) : GpuRecordedCall;

internal sealed record GpuRecordedViewport(int X, int Y, int Width, int Height) : GpuRecordedCall;

internal sealed record GpuRecordedScissor(int X, int Y, int Width, int Height) : GpuRecordedCall;

internal sealed record GpuRecordedCullMode(GpuCullMode CullMode) : GpuRecordedCall;

internal sealed record GpuRecordedFrontFace(GpuFrontFace FrontFace) : GpuRecordedCall;

internal sealed record GpuRecordedDepthWrite(bool Enabled) : GpuRecordedCall;

internal sealed record GpuRecordedDrawIndexed(
    uint IndexCount,
    uint InstanceCount,
    uint FirstIndex,
    int VertexOffset,
    uint FirstInstance) : GpuRecordedCall;

internal sealed record GpuRecordedDraw(
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance) : GpuRecordedCall;

internal sealed record GpuRecordedMultiDrawIndirect(
    string BufferName,
    uint OffsetBytes,
    uint DrawCount,
    uint StrideBytes) : GpuRecordedCall;

internal sealed record GpuRecordedTextureRegistration(string TextureName, GpuSamplerDescription Sampler, uint Slot)
    : GpuRecordedCall;

internal sealed record GpuRecordedTextureRelease(uint Slot) : GpuRecordedCall;

internal sealed record GpuRecordedRenderTargetCreate(GpuRenderTargetDescription Description)
    : GpuRecordedCall;

internal sealed record GpuRecordedDirectionalDepthTargetCreate(
    GpuDirectionalDepthTargetDescription Description) : GpuRecordedCall;

internal sealed record GpuRecordedPipelineColorFormatAcquire(GpuTextureFormat Format)
    : GpuRecordedCall;

internal sealed record GpuRecordedPipelineColorFormatRelease(GpuTextureFormat Format)
    : GpuRecordedCall;

internal sealed class RecordingGpuDevice : IGpuDevice, IGpuPipelineFormatVariantHost
{
    private const int DefaultRingCapacityBytes = 8 * 1024 * 1024;

    private readonly List<GpuRecordedCall> _calls = [];
    private readonly List<Action> _queuedActions = [];
    private readonly List<RecordingGpuBuffer> _createdBuffers = [];
    private readonly List<RecordingGpuPipeline> _createdPipelines = [];
    private readonly List<RecordingGpuSampler> _createdSamplers = [];
    private readonly List<RecordingGpuRenderTarget> _createdRenderTargets = [];
    private readonly List<RecordingGpuDirectionalDepthTarget> _createdDirectionalDepthTargets = [];
    private readonly Dictionary<GpuSamplerDescription, RecordingGpuSampler> _samplers = [];
    private readonly Dictionary<GpuTextureFormat, int> _pipelineFormatLeases = [];
    private readonly byte[] _ring;
    private readonly Stack<uint> _freeTextureSlots = new();

    private uint _nextTextureSlot;
    private uint _ringCursor;
    private long _serial;
    private RecordingGpuFrame? _openFrame;
    private bool _disposed;

    public RecordingGpuDevice(int ringCapacityBytes = DefaultRingCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ringCapacityBytes, 1);
        _ring = new byte[ringCapacityBytes];
        RingBuffer = new RecordingGpuBuffer(
            new GpuBufferDescription(
                "test-ring",
                ringCapacityBytes,
                GpuBufferUsage.Storage | GpuBufferUsage.Uniform | GpuBufferUsage.Indirect,
                GpuMemoryResidency.HostWritable),
            _ring);

        RecordingGpuTexture placeholder = new("default-white", GpuTextureKind.Texture2D, GpuTextureFormat.Rgba8Unorm, 1, 1, 1, 1);
        DefaultTextureSlot = RegisterTexture(placeholder, CreateSampler(GpuSamplerDescription.UiNearest));
    }

    public IReadOnlyList<GpuRecordedCall> Calls => _calls;

    public bool RecordingEnabled { get; set; } = true;

    /// <summary>Backing store for ring allocations, so tests can read what a renderer wrote.</summary>
    public ReadOnlySpan<byte> RingBytes => _ring;

    public uint RingBytesAllocated => _ringCursor;

    public int OpenFrameCount { get; private set; }

    public int LiveTextureSlotCount => (int)_nextTextureSlot - _freeTextureSlots.Count;

    public GpuBackendKind Backend => GpuBackendKind.Recording;

    public GpuCapabilityRecord Capabilities { get; init; } = new()
    {
        Backend = GpuBackendKind.Recording,
        DeviceName = "recording",
        DriverInfo = "in-memory test double",
        ApiVersion = "n/a",
        MaxTextureTableSlots = GpuBindingModel.TextureTableCapacity,
        MaxStorageBufferBindings = GpuBindingModel.StorageBindingCount,
        MaxPushConstantBytes = GpuBindingModel.MaxPushConstantBytes,
        MinStorageBufferOffsetAlignment = 256,
        MaxStorageBufferRangeBytes = 128u * 1024u * 1024u,
        MinUniformBufferOffsetAlignment = 256,
        MaxClipDistances = GpuBindingModel.ClipPlanesPerSlot,
        MaxSampleCount = 8,
        MaxImageDimension2D = 16_384,
        MaxImageArrayLayers = 2_048,
        DeviceLocalMemoryBytes = 8UL * 1024 * 1024 * 1024,
        SupportsMultiDrawIndirect = true,
        SupportsDrawParameters = true,
        SupportsTextureCompressionBc = true,
        SupportsTimestampQueries = true,
        SupportsPersistentlyMappedRings = true,
        SupportsRgba16FloatRenderTargets = true,
        MaxRgba16FloatSampleCount = 8,
        SupportsSampledDepth = true,
        SupportsMultiview = true,
    };

    public IGpuResourceRetirementQueue Retirement => ImmediateGpuResourceRetirementQueue.Instance;

    public RecordingGpuTimerPool RecordingTimers { get; } = new();

    public IGpuTimerPool Timers => RecordingTimers;

    public GpuTextureSlot DefaultTextureSlot { get; }

    public void Clear() => _calls.Clear();

    public IReadOnlyList<RecordingGpuBuffer> CreatedBuffers => _createdBuffers;

    public IReadOnlyList<RecordingGpuPipeline> CreatedPipelines => _createdPipelines;

    public IReadOnlyList<RecordingGpuSampler> CreatedSamplers => _createdSamplers;

    public IReadOnlyList<RecordingGpuRenderTarget> CreatedRenderTargets => _createdRenderTargets;

    public IReadOnlyList<RecordingGpuDirectionalDepthTarget> CreatedDirectionalDepthTargets =>
        _createdDirectionalDepthTargets;

    public IReadOnlyDictionary<GpuTextureFormat, int> PipelineFormatLeases =>
        _pipelineFormatLeases;

    public Func<GpuRenderTargetDescription, Exception?>? RenderTargetFailure { get; set; }

    public Func<GpuPipelineDescription, Exception?>? PipelineFailure { get; set; }

    public Func<GpuTextureDescription, Exception?>? TextureFailure { get; set; }

    public Func<IGpuTexture, IGpuSampler, Exception?>? TextureRegistrationFailure { get; set; }

    public IGpuBuffer CreateBuffer(in GpuBufferDescription description)
    {
        var buffer = new RecordingGpuBuffer(description);
        _createdBuffers.Add(buffer);
        return buffer;
    }

    public IReadOnlyList<RecordingGpuTexture> CreatedTextures => _createdTextures;

    private readonly List<RecordingGpuTexture> _createdTextures = [];

    public IGpuTexture CreateTexture(in GpuTextureDescription description)
    {
        if (TextureFailure?.Invoke(description) is { } failure)
            throw failure;
        RecordingGpuTexture texture = new(
            description.Name,
            description.Kind,
            description.Format,
            description.Width,
            description.Height,
            description.LayerCount,
            description.MipLevelCount);
        _createdTextures.Add(texture);
        return texture;
    }

    public IGpuSampler CreateSampler(in GpuSamplerDescription description)
    {
        if (_samplers.TryGetValue(description, out RecordingGpuSampler? existing)
            && !existing.IsDisposed)
            return existing;

        RecordingGpuSampler created = new(description);
        _samplers[description] = created;
        _createdSamplers.Add(created);
        return created;
    }

    public IGpuPipeline CreatePipeline(GpuPipelineDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.ViewMask != 0 && !Capabilities.SupportsMultiview)
            throw new NotSupportedException("Multiview pipelines are unsupported.");
        if (PipelineFailure?.Invoke(description) is { } failure)
            throw failure;
        var pipeline = new RecordingGpuPipeline(description);
        _createdPipelines.Add(pipeline);
        return pipeline;
    }

    public IGpuRenderTarget CreateRenderTarget(in GpuRenderTargetDescription description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.SampleCount);
        if (description.SampleableDepth && description.DepthFormat is null)
            throw new ArgumentException("SampleableDepth requires a depth format.", nameof(description));
        if ((uint)description.SampleCount > Capabilities.MaxSampleCount)
            throw new NotSupportedException("The requested sample count is unsupported.");
        if (description.ColorFormat == GpuTextureFormat.Rgba16FloatRenderTarget
            && (!Capabilities.SupportsRgba16FloatRenderTargets
                || (uint)description.SampleCount > Capabilities.MaxRgba16FloatSampleCount))
        {
            throw new NotSupportedException("RGBA16F render-target capabilities are insufficient.");
        }
        if (description.SampleableDepth && !Capabilities.SupportsSampledDepth)
            throw new NotSupportedException("Sampled depth is unsupported.");
        if (RenderTargetFailure?.Invoke(description) is { } failure)
            throw failure;
        var target = new RecordingGpuRenderTarget(description);
        _createdRenderTargets.Add(target);
        _calls.Add(new GpuRecordedRenderTargetCreate(description));
        return target;
    }

    public IGpuDirectionalDepthTarget CreateDirectionalDepthTarget(
        in GpuDirectionalDepthTargetDescription description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Resolution);
        if (description.LayerCount is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(description));
        if (description.DepthFormat != GpuTextureFormat.Depth24Stencil8)
            throw new ArgumentException("Directional depth requires Depth24Stencil8.", nameof(description));
        if (!Capabilities.SupportsSampledDepth)
            throw new NotSupportedException("Sampled depth is unsupported.");

        var target = new RecordingGpuDirectionalDepthTarget(description);
        _createdDirectionalDepthTargets.Add(target);
        _calls.Add(new GpuRecordedDirectionalDepthTargetCreate(description));
        return target;
    }

    public IDisposable AcquirePipelineColorFormat(GpuTextureFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (format == GpuTextureFormat.Rgba16FloatRenderTarget
            && !Capabilities.SupportsRgba16FloatRenderTargets)
            throw new NotSupportedException("RGBA16F render targets are unsupported.");
        _pipelineFormatLeases.TryGetValue(format, out int count);
        _pipelineFormatLeases[format] = checked(count + 1);
        _calls.Add(new GpuRecordedPipelineColorFormatAcquire(format));
        return new RecordingPipelineColorFormatLease(this, format);
    }

    private void ReleasePipelineColorFormat(GpuTextureFormat format)
    {
        if (!_pipelineFormatLeases.TryGetValue(format, out int count))
            return;
        if (count == 1)
            _pipelineFormatLeases.Remove(format);
        else
            _pipelineFormatLeases[format] = count - 1;
        _calls.Add(new GpuRecordedPipelineColorFormatRelease(format));
    }

    public GpuTextureSlot RegisterTexture(IGpuTexture texture, IGpuSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(sampler);

        if (TextureRegistrationFailure?.Invoke(texture, sampler) is { } failure)
            throw failure;

        uint slot = _freeTextureSlots.Count > 0 ? _freeTextureSlots.Pop() : _nextTextureSlot++;
        _calls.Add(new GpuRecordedTextureRegistration(texture.Name, sampler.Description, slot));
        return new GpuTextureSlot(slot);
    }

    public void ReleaseTextureSlot(GpuTextureSlot slot)
    {
        if (!slot.IsAssigned)
            throw new ArgumentException("Cannot release an unassigned texture slot.", nameof(slot));

        _freeTextureSlots.Push(slot.Index);
        _calls.Add(new GpuRecordedTextureRelease(slot.Index));
    }

    public IGpuFrame BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_openFrame is not null)
            throw new InvalidOperationException("The previous frame must end before another begins.");

        _ringCursor = 0;
        long serial = ++_serial;
        int slotIndex = (int)((serial - 1) % 2);
        _calls.Add(new GpuRecordedFrameBegin(serial, slotIndex));
        OpenFrameCount++;
        _openFrame = new RecordingGpuFrame(this, serial, slotIndex);
        return _openFrame;
    }

    public void QueueDeviceAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queuedActions.Add(action);
    }

    public void ProcessDeviceActions()
    {
        Action[] pending = [.. _queuedActions];
        _queuedActions.Clear();
        foreach (Action action in pending)
            action();
    }

    public byte[] CaptureBackbuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new byte[checked(width * height * 4)];
    }

    public void WaitIdle() => ProcessDeviceActions();

    public void Dispose() => _disposed = true;

    internal void Record(GpuRecordedCall call) => _calls.Add(call);

    internal GpuRingAllocation Allocate(int byteCount, GpuRingUsage usage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        uint alignment = usage switch
        {
            GpuRingUsage.Storage => Capabilities.MinStorageBufferOffsetAlignment,
            GpuRingUsage.Uniform => Capabilities.MinUniformBufferOffsetAlignment,
            _ => 4u,
        };

        uint aligned = AlignUp(_ringCursor, alignment);
        if (aligned + (uint)byteCount > (uint)_ring.Length)
        {
            throw new InvalidOperationException(
                $"Ring allocation of {byteCount} bytes for {usage} exceeds the {_ring.Length}-byte test ring.");
        }

        _ringCursor = aligned + (uint)byteCount;
        if (RecordingEnabled)
            _calls.Add(new GpuRecordedRingAllocation(usage, byteCount, aligned));
        return new GpuRingAllocation(RingBuffer, aligned, _ring.AsSpan((int)aligned, byteCount));
    }

    internal IGpuBuffer RingBuffer { get; }

    internal void CloseFrame(RecordingGpuFrame frame)
    {
        if (!ReferenceEquals(_openFrame, frame))
            return;

        _calls.Add(new GpuRecordedFrameEnd(frame.Serial));
        OpenFrameCount--;
        _openFrame = null;
    }

    private static uint AlignUp(uint value, uint alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    private sealed class RecordingPipelineColorFormatLease(
        RecordingGpuDevice device,
        GpuTextureFormat format) : IDisposable
    {
        private RecordingGpuDevice? _device = device;

        public void Dispose() =>
            Interlocked.Exchange(ref _device, null)?.ReleasePipelineColorFormat(format);
    }
}

internal sealed class RecordingGpuFrame(RecordingGpuDevice device, long serial, int slotIndex) : IGpuFrame
{
    private bool _ended;

    public int SlotIndex { get; } = slotIndex;

    public long Serial { get; } = serial;

    public GpuRingAllocation AllocateRing(int byteCount, GpuRingUsage usage) => device.Allocate(byteCount, usage);

    public void PublishHostStorageWrites(IGpuBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Residency != GpuMemoryResidency.HostWritable
            || !buffer.Usage.HasFlag(GpuBufferUsage.Storage))
        {
            throw new ArgumentException(
                "Published host writes require a host-writable storage buffer.",
                nameof(buffer));
        }
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedHostStorageVisibility(buffer.Name));
    }

    public IGpuPassEncoder BeginPass(GpuPassDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (!description.HasColorAttachment)
        {
            if (description.SampleCount != 1
                || description.Depth is not { DirectionalTarget: RecordingGpuDirectionalDepthTarget directionalTarget } depth)
            {
                throw new InvalidOperationException(
                    "A colour-less recording pass requires a single-sampled directional-depth target.");
            }
            if (depth.Layer < 0 || depth.Layer >= directionalTarget.Description.LayerCount)
                throw new ArgumentOutOfRangeException(nameof(description));
            if (description.ViewMask != 0)
            {
                uint expected = (1u << directionalTarget.Description.LayerCount) - 1u;
                if (description.ViewMask != expected || !device.Capabilities.SupportsMultiview)
                    throw new NotSupportedException("Directional multiview requires every target layer and device support.");
            }
            if (depth.Store != GpuStoreOp.Store)
                throw new InvalidOperationException("Directional depth must be stored for sampling.");
        }
        if (description.Color.Target is RecordingGpuRenderTarget target)
        {
            if (description.SampleCount != target.Description.SampleCount)
            {
                throw new InvalidOperationException(
                    "Pass and offscreen-target sample counts must match.");
            }
            if (target.UsesMultisampleResolve && description.Color.Store != GpuStoreOp.Resolve)
            {
                throw new InvalidOperationException(
                    "A multisampled offscreen target must resolve into ColorTexture.");
            }
            if (target.UsesMultisampleResolve && description.Color.Load == GpuLoadOp.Load)
            {
                throw new InvalidOperationException(
                    "A transient multisample colour attachment cannot load the prior resolved image.");
            }
            if (!target.UsesMultisampleResolve && description.Color.Store == GpuStoreOp.Resolve)
            {
                throw new InvalidOperationException(
                    "A single-sampled offscreen target cannot use Store=Resolve.");
            }
            if (target.UsesMultisampleResolve && description.Depth?.Load == GpuLoadOp.Load)
            {
                throw new InvalidOperationException(
                    "A transient multisample depth attachment cannot load prior depth.");
            }
            if (target.Description.SampleableDepth
                && description.Depth is { }
                && description.Depth?.Store != GpuStoreOp.Store)
            {
                throw new InvalidOperationException(
                    "Sampleable depth requires Store=Store.");
            }
        }
        device.Record(new GpuRecordedPassBegin(description.Name, description.SampleCount, description.ViewMask));
        return new RecordingGpuPassEncoder(device, description);
    }

    public void End()
    {
        if (_ended)
            return;

        _ended = true;
        device.CloseFrame(this);
    }

    public void Dispose() => End();
}

internal sealed class RecordingGpuPassEncoder(RecordingGpuDevice device, GpuPassDescription pass) : IGpuPassEncoder
{
    private bool _closed;

    public GpuPassDescription Pass { get; } = pass;

    public void BindPipeline(IGpuPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.Description.HasColorAttachment != Pass.HasColorAttachment)
            throw new InvalidOperationException("Pipeline and pass colour-attachment intents must match.");
        if (pipeline.Description.ViewMask != Pass.ViewMask)
            throw new InvalidOperationException("Pipeline and pass view masks must match.");
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedPipelineBind(pipeline.Description.Name));
    }

    public void BindStorageBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes, uint sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedStorageBind(binding, buffer.Name, offsetBytes, sizeBytes));
    }

    public void BindUniformBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes, uint sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedUniformBind(binding, buffer.Name, offsetBytes, sizeBytes));
    }

    public void BindVertexBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedVertexBind(binding, buffer.Name, offsetBytes));
    }

    public void BindIndexBuffer(IGpuBuffer buffer, uint offsetBytes, GpuIndexType indexType)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedIndexBind(buffer.Name, offsetBytes, indexType));
    }

    public void SetPushConstants(in GpuPushConstants constants)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedPushConstants(constants));
    }

    public void SetViewport(int x, int y, int width, int height)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedViewport(x, y, width, height));
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedScissor(x, y, width, height));
    }

    public void SetCullMode(GpuCullMode cullMode)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedCullMode(cullMode));
    }

    public void SetFrontFace(GpuFrontFace frontFace)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedFrontFace(frontFace));
    }

    public void SetStencil(in GpuStencilState stencil)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedStencil(stencil));
    }

    public void SetDepthWrite(bool enabled)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedDepthWrite(enabled));
    }

    public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedDrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance));
    }

    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedDraw(vertexCount, instanceCount, firstVertex, firstInstance));
    }

    public void MultiDrawIndexedIndirect(IGpuBuffer commands, uint offsetBytes, uint drawCount, uint strideBytes)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (device.RecordingEnabled)
            device.Record(new GpuRecordedMultiDrawIndirect(commands.Name, offsetBytes, drawCount, strideBytes));
    }

    public IDisposable BeginTimerScope(string scopeName)
    {
        device.Record(new GpuRecordedTimerScope(scopeName));
        return NullDisposable.Instance;
    }

    public void Dispose()
    {
        if (_closed)
            return;

        _closed = true;
        device.Record(new GpuRecordedPassEnd(Pass.Name));
    }

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class RecordingGpuBuffer : IGpuBuffer
{
    private readonly byte[] _storage;

    internal RecordingGpuBuffer(
        GpuBufferDescription description,
        byte[]? storage = null)
    {
        if (storage is not null && storage.Length != description.SizeBytes)
        {
            throw new ArgumentException(
                "External recording storage must match the buffer size.",
                nameof(storage));
        }
        _storage = storage ?? new byte[description.SizeBytes];
        Name = description.Name;
        SizeBytes = description.SizeBytes;
        Usage = description.Usage;
        Residency = description.Residency;
    }

    public string Name { get; }

    public long SizeBytes { get; }

    public GpuBufferUsage Usage { get; }

    public GpuMemoryResidency Residency { get; }

    public bool HostWritesAreCoherent =>
        Residency == GpuMemoryResidency.HostWritable;

    public bool IsDisposed { get; private set; }

    public int UploadCount { get; private set; }

    public long UploadedBytes { get; private set; }

    public void Upload(long offsetBytes, ReadOnlySpan<byte> data)
    {
        data.CopyTo(_storage.AsSpan((int)offsetBytes, data.Length));
        UploadCount++;
        UploadedBytes = checked(UploadedBytes + data.Length);
    }

    public void CopyTo(IGpuBuffer destination, long sourceOffsetBytes, long destinationOffsetBytes, long byteCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination is not RecordingGpuBuffer target)
            throw new ArgumentException("Recording buffers can only copy to recording buffers.", nameof(destination));

        _storage.AsSpan((int)sourceOffsetBytes, (int)byteCount)
            .CopyTo(target._storage.AsSpan((int)destinationOffsetBytes, (int)byteCount));
    }

    public void Read(long offsetBytes, Span<byte> destination) =>
        _storage.AsSpan((int)offsetBytes, destination.Length).CopyTo(destination);

    public void Dispose() => IsDisposed = true;
}

internal sealed class RecordingGpuTexture(
    string name,
    GpuTextureKind kind,
    GpuTextureFormat format,
    int width,
    int height,
    int layerCount,
    int mipLevelCount) : IGpuTexture
{
    private readonly List<(int MipLevel, int Layer, int ByteCount)> _uploads = [];

    public string Name { get; } = name;

    public GpuTextureKind Kind { get; } = kind;

    public GpuTextureFormat Format { get; } = format;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int LayerCount { get; } = layerCount;

    public int MipLevelCount { get; } = mipLevelCount;

    public bool MipChainGenerated { get; private set; }

    public bool IsDisposed { get; private set; }

    public IReadOnlyList<(int MipLevel, int Layer, int ByteCount)> Uploads => _uploads;

    public void Upload(int mipLevel, int layer, ReadOnlySpan<byte> data) =>
        _uploads.Add((mipLevel, layer, data.Length));

    public void GenerateMipChain() => MipChainGenerated = true;

    public void Dispose() => IsDisposed = true;
}

internal sealed class RecordingGpuSampler(GpuSamplerDescription description) : IGpuSampler
{
    public GpuSamplerDescription Description { get; } = description;

    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal sealed class RecordingGpuPipeline(GpuPipelineDescription description) : IGpuPipeline
{
    public GpuPipelineDescription Description { get; } = description;

    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal sealed class RecordingGpuRenderTarget : IGpuRenderTarget
{
    public RecordingGpuRenderTarget(GpuRenderTargetDescription description)
    {
        Description = description;
        ColorTexture = new RecordingGpuTexture(
            $"{description.Name}-color",
            GpuTextureKind.Texture2D,
            description.ColorFormat,
            description.Width,
            description.Height,
            layerCount: 1,
            mipLevelCount: 1);
        if (description.SampleableDepth && description.DepthFormat is { } depthFormat)
        {
            DepthTexture = new RecordingGpuTexture(
                $"{description.Name}-depth",
                GpuTextureKind.Texture2D,
                depthFormat,
                description.Width,
                description.Height,
                layerCount: 1,
                mipLevelCount: 1);
        }
    }

    public GpuRenderTargetDescription Description { get; }

    public IGpuTexture ColorTexture { get; }

    public IGpuTexture? DepthTexture { get; }

    public int AttachmentSampleCount => Description.SampleCount;

    public bool UsesMultisampleResolve => Description.SampleCount > 1;

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        ColorTexture.Dispose();
        DepthTexture?.Dispose();
    }

}

internal sealed class RecordingGpuDirectionalDepthTarget : IGpuDirectionalDepthTarget
{
    public RecordingGpuDirectionalDepthTarget(GpuDirectionalDepthTargetDescription description)
    {
        Description = description;
        DepthTexture = new RecordingGpuTexture(
            $"{description.Name}-depth",
            GpuTextureKind.Texture2DArray,
            description.DepthFormat,
            description.Resolution,
            description.Resolution,
            description.LayerCount,
            mipLevelCount: 1);
    }

    public GpuDirectionalDepthTargetDescription Description { get; }

    public IGpuTexture DepthTexture { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        DepthTexture.Dispose();
    }
}

internal sealed class RecordingGpuTimerPool : IGpuTimerPool
{
    private readonly Dictionary<string, double> _resolved = new(StringComparer.Ordinal);

    public bool IsSupported => true;

    internal void SetResolved(string scopeName, double milliseconds) =>
        _resolved[scopeName] = milliseconds;

    internal void ClearResolved() => _resolved.Clear();

    public bool TryResolve(string scopeName, out double milliseconds)
    {
        return _resolved.TryGetValue(scopeName, out milliseconds);
    }

    public bool TryTakeResolved(string scopeName, out double milliseconds)
    {
        if (!_resolved.TryGetValue(scopeName, out milliseconds))
            return false;
        _resolved.Remove(scopeName);
        return true;
    }
}

internal static class RecordingGpuDeviceAssertions
{
    public static IEnumerable<T> OfKind<T>(this RecordingGpuDevice device) where T : GpuRecordedCall =>
        device.Calls.OfType<T>();

    public static Vector4 ClearColorOf(this GpuPassDescription pass) => pass.Color.ClearColor;
}
