using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanTextureSlotAllocator
{
    private readonly uint _capacity;
    private readonly PriorityQueue<uint, uint> _free = new();
    private readonly HashSet<uint> _live = [];
    private uint _highWater;

    internal VulkanTextureSlotAllocator(uint capacity)
    {
        ArgumentOutOfRangeException.ThrowIfZero(capacity);
        _capacity = capacity;
    }

    internal uint Capacity => _capacity;

    internal int LiveCount => _live.Count;

    internal int FreeCount => _free.Count;

    /// <summary>Highest slot index ever handed out, plus one. The table only ever writes below this.</summary>
    internal uint HighWater => _highWater;

    internal uint Allocate()
    {
        uint slot;
        if (_free.Count > 0)
        {
            slot = _free.Dequeue();
        }
        else
        {
            if (_highWater >= _capacity)
            {
                throw new InvalidOperationException(
                    $"The Vulkan texture table is full at {_capacity} slots. Raise " +
                    "GpuBindingModel.TextureTableCapacity and the capability gate's limit together.");
            }

            slot = _highWater++;
        }

        _live.Add(slot);
        return slot;
    }

    internal void Release(uint slot)
    {
        if (!_live.Remove(slot))
        {
            throw new InvalidOperationException(
                $"Texture table slot {slot} is not live; releasing it twice would let two textures " +
                "share one index.");
        }

        _free.Enqueue(slot, slot);
    }

    internal bool IsLive(uint slot) => _live.Contains(slot);
}

internal sealed unsafe class VulkanTextureTable : IDisposable
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly VulkanTextureSlotAllocator _slots;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly object _sync = new();

    private ImageView _defaultView;
    private Sampler _defaultSampler;
    private ImageLayout _defaultLayout = ImageLayout.ShaderReadOnlyOptimal;
    private bool _disposed;

    internal VulkanTextureTable(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        DescriptorSetLayout layout,
        uint capacity)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _slots = new VulkanTextureSlotAllocator(capacity);
        Capacity = capacity;

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = capacity,
        };
        var poolCreate = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            // The pool must be update-after-bind too, not only the layout;
            // omitting this is a validation error that only fires on the first
            // registration.
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        VulkanInterop.Check(
            _vk.CreateDescriptorPool(_device, &poolCreate, null, out _pool),
            "vkCreateDescriptorPool (texture table)");

        uint variableCount = capacity;
        DescriptorSetLayout setLayout = layout;
        var variable = new DescriptorSetVariableDescriptorCountAllocateInfo
        {
            SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
            DescriptorSetCount = 1,
            PDescriptorCounts = &variableCount,
        };
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            PNext = &variable,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        VulkanInterop.Check(
            _vk.AllocateDescriptorSets(_device, &allocate, out _set),
            "vkAllocateDescriptorSets (texture table)");
    }

    internal uint Capacity { get; }

    internal DescriptorSet Set => _set;

    internal int LiveSlotCount
    {
        get
        {
            lock (_sync)
                return _slots.LiveCount;
        }
    }

    internal uint HighWater
    {
        get
        {
            lock (_sync)
                return _slots.HighWater;
        }
    }

    /// <summary>
    /// Records the (view, sampler) pair written into a slot when it is scrubbed.
    /// Supplied after the default texture exists, which is necessarily after the
    /// table itself.
    /// </summary>
    internal void SetScrubTarget(
        ImageView view,
        Sampler sampler,
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal)
    {
        _defaultView = view;
        _defaultSampler = sampler;
        _defaultLayout = layout;
    }

    internal GpuTextureSlot Register(
        ImageView view,
        Sampler sampler,
        ImageLayout layout = ImageLayout.ShaderReadOnlyOptimal)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            uint slot = _slots.Allocate();
            Write(slot, view, sampler, layout);
            return new GpuTextureSlot(slot);
        }
    }

    internal void ReleaseNow(GpuTextureSlot slot)
    {
        lock (_sync)
        {
            if (_disposed || !slot.IsAssigned)
                return;
            if (_defaultView.Handle != 0 && _defaultSampler.Handle != 0)
                Write(slot.Index, _defaultView, _defaultSampler, _defaultLayout);
            _slots.Release(slot.Index);
        }
    }

    internal bool IsLive(GpuTextureSlot slot)
    {
        lock (_sync)
            return slot.IsAssigned && _slots.IsLive(slot.Index);
    }

    private void Write(
        uint slot,
        ImageView view,
        Sampler sampler,
        ImageLayout layout)
    {
        var info = new DescriptorImageInfo
        {
            ImageView = view,
            Sampler = sampler,
            ImageLayout = layout,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = GpuBindingModel.TextureTableBinding,
            DstArrayElement = slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_pool.Handle != 0)
                _vk.DestroyDescriptorPool(_device, _pool, null);
        }
    }
}
