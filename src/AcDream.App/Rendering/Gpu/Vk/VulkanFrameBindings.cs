using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanFrameBindings : IDisposable
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly VulkanPipelineLayouts.Created _layouts;
    private readonly VulkanBindingScopeArena _arena;
    private readonly uint _maxStorageBufferRangeBytes;
    private readonly List<DescriptorPool> _pools = [];
    private readonly List<(DescriptorSet Storage, DescriptorSet Uniform)> _sets = [];
    private readonly ulong[] _packBuffers = new ulong[VulkanPipelineLayouts.PackUniformBindingCount];
    private readonly uint[] _packOffsets = new uint[VulkanPipelineLayouts.PackUniformBindingCount];
    private readonly uint[] _packRanges = new uint[VulkanPipelineLayouts.PackUniformBindingCount];
    private readonly Dictionary<PackBindingKey, int> _packSlotsByState = [];
    private readonly List<DescriptorSet> _packSets = [];
    private int _packLiveCount;
    private int _packGeneration = -1;

    private bool _disposed;

    /// <summary>
    /// Set pairs one descriptor pool serves. Distinct descriptor states in a
    /// frame are the world renderers plus the retained UI, so this is generous;
    /// exceeding it allocates another pool rather than failing.
    /// </summary>
    private const int PairsPerPool = 16;

    /// <summary>
    /// Set 0's dynamic-offset slots, in binding order — the order
    /// <c>vkCmdBindDescriptorSets</c> requires. A plain binding has no slot.
    /// </summary>
    private static readonly uint[] DynamicStorageBindings = BuildDynamicStorageBindings();

    private static uint[] BuildDynamicStorageBindings()
    {
        var bindings = new List<uint>((int)GpuBindingModel.StorageBindingCount);
        for (uint binding = 0; binding < GpuBindingModel.StorageBindingCount; binding++)
        {
            if (VulkanPipelineLayouts.IsDynamicStorageBinding(binding))
                bindings.Add(binding);
        }

        return [.. bindings];
    }

    internal const int UniformBindingCount = 5;

    internal static uint DynamicUniformBindingCount { get; } =
        (uint)VulkanPipelineLayouts.DeclaredUniformBindings.Length;

    internal VulkanFrameBindings(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanPipelineLayouts.Created layouts,
        VulkanGpuBuffer ring,
        VulkanGpuBuffer dummy,
        uint maxStorageBufferRangeBytes)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(dummy);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStorageBufferRangeBytes, 16u);

        Ring = ring;
        Dummy = dummy;
        _maxStorageBufferRangeBytes = maxStorageBufferRangeBytes;
        _arena = new VulkanBindingScopeArena(
            (int)GpuBindingModel.StorageBindingCount,
            UniformBindingCount,
            VulkanPipelineLayouts.IsDynamicStorageBinding);

        uint dummyStorageRange = (uint)Math.Min(dummy.SizeBytes, _maxStorageBufferRangeBytes);
        for (uint binding = 0; binding < GpuBindingModel.StorageBindingCount; binding++)
            _arena.SeedStorage(binding, dummy.Handle.Handle, offsetBytes: 0, dummyStorageRange);

        uint dummyUniformRange = (uint)Math.Min(dummy.SizeBytes, 65536);
        for (uint binding = 0; binding < UniformBindingCount; binding++)
            _arena.SeedUniform(binding, dummy.Handle.Handle, dummyUniformRange);
        for (int binding = 0; binding < _packBuffers.Length; binding++)
        {
            _packBuffers[binding] = dummy.Handle.Handle;
            _packRanges[binding] = dummyUniformRange;
        }
    }

    internal VulkanGpuBuffer Ring { get; }

    internal VulkanGpuBuffer Dummy { get; }

    internal int ScopeCount => _arena.Count;

    /// <summary>
    /// Recycles the arena for a new frame on this slot. Safe because the slot's
    /// previous submission has retired before <c>BeginFrame</c> returns, which is
    /// the same guarantee that lets the ring rewind.
    /// </summary>
    internal void BeginFrame()
    {
        _arena.BeginFrame();
        _packSlotsByState.Clear();
        _packLiveCount = 0;
    }

    internal void SetStorage(uint binding, VulkanGpuBuffer buffer, uint offsetBytes, uint sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(binding, GpuBindingModel.StorageBindingCount);
        uint descriptorOffset = VulkanPipelineLayouts.IsDynamicStorageBinding(binding) ? 0 : offsetBytes;
        _arena.SetStorage(
            binding,
            buffer.Handle.Handle,
            offsetBytes,
            ClampRange(buffer, sizeBytes, descriptorOffset));
    }

    internal void SetUniform(uint binding, VulkanGpuBuffer buffer, uint offsetBytes, uint sizeBytes)
    {
        if (binding < UniformBindingCount)
        {
            _arena.SetUniform(
                binding,
                buffer.Handle.Handle,
                offsetBytes,
                Math.Min(ClampRange(buffer, sizeBytes, offsetBytes: 0), 65536));
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(binding, GpuBindingModel.UniformAtmosphericFrame);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(binding, GpuBindingModel.UniformPackSettings);
        int packBinding = (int)(binding - GpuBindingModel.UniformAtmosphericFrame);
        _packBuffers[packBinding] = buffer.Handle.Handle;
        _packOffsets[packBinding] = offsetBytes;
        _packRanges[packBinding] = Math.Min(ClampRange(buffer, sizeBytes, offsetBytes: 0), 65536);
    }

    internal void Bind(
        CommandBuffer commands,
        VulkanGpuDevice device,
        PipelineLayout pipelineLayout,
        VulkanPipelineLayouts.Created.PackState? packState = null)
    {
        (int index, int slot, bool needsWrite) = _arena.Resolve();
        if (slot < 0)
        {
            slot = _sets.Count;
            _sets.Add(AllocatePair());
            _arena.AssignSlot(index, slot);
        }

        if (needsWrite)
            WritePair(_sets[slot]);

        DescriptorSet* sets = stackalloc DescriptorSet[3];
        sets[0] = _sets[slot].Storage;
        sets[1] = _sets[slot].Uniform;
        sets[2] = device.TextureTable.Set;

        uint[] declaredUniforms = VulkanPipelineLayouts.DeclaredUniformBindings;
        int dynamicCount = DynamicStorageBindings.Length + declaredUniforms.Length;
        uint* offsets = stackalloc uint[dynamicCount];
        // Dynamic offsets are ordered by set, then by binding number, and only
        // the DYNAMIC descriptors have a slot at all.
        for (int i = 0; i < DynamicStorageBindings.Length; i++)
            offsets[i] = _arena.StorageOffset(DynamicStorageBindings[i]);
        for (int i = 0; i < declaredUniforms.Length; i++)
            offsets[DynamicStorageBindings.Length + i] = _arena.UniformOffset(declaredUniforms[i]);

        _vk.CmdBindDescriptorSets(
            commands,
            PipelineBindPoint.Graphics,
            pipelineLayout,
            0,
            3,
            sets,
            (uint)dynamicCount,
            offsets);

        if (packState is not null)
            BindPackSet(commands, pipelineLayout, packState);
    }

    private void BindPackSet(
        CommandBuffer commands,
        PipelineLayout pipelineLayout,
        VulkanPipelineLayouts.Created.PackState state)
    {
        if (_packGeneration != state.Generation)
        {
            _packGeneration = state.Generation;
            _packSets.Clear();
            _packSlotsByState.Clear();
            _packLiveCount = 0;
        }

        PackBindingKey key = CurrentPackKey();
        if (!_packSlotsByState.TryGetValue(key, out int slot))
        {
            slot = _packLiveCount++;
            _packSlotsByState.Add(key, slot);
            if (slot == _packSets.Count)
                _packSets.Add(state.AllocateDescriptorSet());
            WritePackSet(_packSets[slot]);
        }

        DescriptorSet set = _packSets[slot];
        uint* offsets = stackalloc uint[(int)VulkanPipelineLayouts.PackUniformBindingCount];
        for (int i = 0; i < _packOffsets.Length; i++)
            offsets[i] = _packOffsets[i];
        _vk.CmdBindDescriptorSets(
            commands,
            PipelineBindPoint.Graphics,
            pipelineLayout,
            GpuBindingModel.RenderPackUniformSet,
            1,
            &set,
            VulkanPipelineLayouts.PackUniformBindingCount,
            offsets);
    }

    private PackBindingKey CurrentPackKey() => new(
        _packBuffers[0], _packRanges[0],
        _packBuffers[1], _packRanges[1],
        _packBuffers[2], _packRanges[2],
        _packBuffers[3], _packRanges[3]);

    private void WritePackSet(DescriptorSet set)
    {
        for (int i = 0; i < _packBuffers.Length; i++)
        {
            WriteUniform(
                set,
                GpuBindingModel.UniformAtmosphericFrame + (uint)i,
                new Silk.NET.Vulkan.Buffer(_packBuffers[i]),
                _packRanges[i]);
        }
    }

    private void WritePair((DescriptorSet Storage, DescriptorSet Uniform) pair)
    {
        for (uint binding = 0; binding < GpuBindingModel.StorageBindingCount; binding++)
        {
            WriteStorage(
                pair.Storage,
                binding,
                new Silk.NET.Vulkan.Buffer(_arena.StorageBuffer(binding)),
                _arena.StorageDescriptorOffset(binding),
                _arena.StorageRange(binding),
                VulkanPipelineLayouts.IsDynamicStorageBinding(binding));
        }

        // Only the bindings the layout declares exist; the rest of the array is
        // bookkeeping so the offsets stay index-aligned with the binding number.
        foreach (uint binding in VulkanPipelineLayouts.DeclaredUniformBindings)
        {
            WriteUniform(
                pair.Uniform,
                binding,
                new Silk.NET.Vulkan.Buffer(_arena.UniformBuffer(binding)),
                _arena.UniformRange(binding));
        }
    }

    private (DescriptorSet Storage, DescriptorSet Uniform) AllocatePair()
    {
        if (_sets.Count % PairsPerPool == 0)
            _pools.Add(CreatePool());
        DescriptorPool pool = _pools[^1];
        return (Allocate(pool, _layouts.Storage), Allocate(pool, _layouts.Uniform));
    }

    private DescriptorPool CreatePool()
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[3];
        sizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBufferDynamic,
            DescriptorCount = VulkanPipelineLayouts.DynamicStorageBindingCount * PairsPerPool,
        };
        sizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount =
                (GpuBindingModel.StorageBindingCount - VulkanPipelineLayouts.DynamicStorageBindingCount)
                * PairsPerPool,
        };
        sizes[2] = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBufferDynamic,
            DescriptorCount = DynamicUniformBindingCount * PairsPerPool,
        };
        var poolCreate = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 2 * PairsPerPool,
            PoolSizeCount = 3,
            PPoolSizes = sizes,
        };
        VulkanInterop.Check(
            _vk.CreateDescriptorPool(_device, &poolCreate, null, out DescriptorPool pool),
            "vkCreateDescriptorPool (frame bindings)");
        return pool;
    }

    private uint ClampRange(VulkanGpuBuffer buffer, uint requested, uint offsetBytes)
    {
        long remaining = buffer.SizeBytes - offsetBytes;
        if (remaining <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offsetBytes),
                offsetBytes,
                $"A storage binding was pointed past the end of its {buffer.SizeBytes}-byte buffer. " +
                "A descriptor range of zero is not representable in Vulkan.");
        }

        uint available = (uint)Math.Min(remaining, _maxStorageBufferRangeBytes);
        return requested == 0 ? available : Math.Min(Math.Max(requested, 16), available);
    }

    private DescriptorSet Allocate(DescriptorPool pool, DescriptorSetLayout layout)
    {
        DescriptorSetLayout handle = layout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &handle,
        };
        VulkanInterop.Check(
            _vk.AllocateDescriptorSets(_device, &allocate, out DescriptorSet set),
            "vkAllocateDescriptorSets (frame bindings)");
        return set;
    }

    private void WriteStorage(
        DescriptorSet set,
        uint binding,
        Silk.NET.Vulkan.Buffer buffer,
        uint offsetBytes,
        uint rangeBytes,
        bool dynamic)
    {
        var info = new DescriptorBufferInfo
        {
            Buffer = buffer,
            Offset = offsetBytes,
            Range = rangeBytes,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = dynamic
                ? DescriptorType.StorageBufferDynamic
                : DescriptorType.StorageBuffer,
            PBufferInfo = &info,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    private void WriteUniform(
        DescriptorSet set,
        uint binding,
        Silk.NET.Vulkan.Buffer buffer,
        uint rangeBytes)
    {
        var info = new DescriptorBufferInfo
        {
            Buffer = buffer,
            Offset = 0,
            Range = rangeBytes,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            PBufferInfo = &info,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (DescriptorPool pool in _pools)
        {
            if (pool.Handle != 0)
                _vk.DestroyDescriptorPool(_device, pool, null);
        }

        _pools.Clear();
        _sets.Clear();
        _packSlotsByState.Clear();
        _packSets.Clear();
    }

    private readonly record struct PackBindingKey(
        ulong Buffer0,
        uint Range0,
        ulong Buffer1,
        uint Range1,
        ulong Buffer2,
        uint Range2,
        ulong Buffer3,
        uint Range3);
}
