using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal static unsafe class VulkanPipelineLayouts
{
    internal sealed class Created : IDisposable
    {
        private readonly Silk.NET.Vulkan.Vk _vk;
        private readonly Device _device;
        private readonly object _packLock = new();
        private PackState? _pack;
        private int _packReferences;
        private int _nextPackGeneration;
        private bool _disposed;

        internal Created(
            Silk.NET.Vulkan.Vk vk,
            Device device,
            DescriptorSetLayout storage,
            DescriptorSetLayout uniform,
            DescriptorSetLayout textureTable,
            PipelineLayout pipelineLayout)
        {
            _vk = vk;
            _device = device;
            Storage = storage;
            Uniform = uniform;
            TextureTable = textureTable;
            PipelineLayout = pipelineLayout;
        }

        internal DescriptorSetLayout Storage { get; }
        internal DescriptorSetLayout Uniform { get; }
        internal DescriptorSetLayout TextureTable { get; }
        internal PipelineLayout PipelineLayout { get; }

        internal PackLayoutLease AcquirePackLayout()
        {
            lock (_packLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _pack ??= CreatePackState(++_nextPackGeneration);
                _packReferences++;
                return new PackLayoutLease(this, _pack);
            }
        }

        private PackState CreatePackState(int generation)
        {
            DescriptorSetLayout packUniform = default;
            try
            {
                packUniform = CreatePackUniformSetLayout(_vk, _device);
                PipelineLayout packPipeline = CreatePackPipelineLayout(
                    _vk,
                    _device,
                    Storage,
                    Uniform,
                    TextureTable,
                    packUniform);
                return new PackState(_vk, _device, generation, packUniform, packPipeline);
            }
            catch
            {
                if (packUniform.Handle != 0)
                    _vk.DestroyDescriptorSetLayout(_device, packUniform, null);
                throw;
            }
        }

        private void ReleasePackLayout(PackState state)
        {
            lock (_packLock)
            {
                if (_pack != state || _packReferences <= 0)
                    return;
                _packReferences--;
                if (_packReferences != 0)
                    return;
                _pack = null;
                state.Destroy();
            }
        }

        internal void Destroy(Silk.NET.Vulkan.Vk vk, Device device)
        {
            if (_disposed)
                return;
            _disposed = true;
            lock (_packLock)
            {
                _pack?.Destroy();
                _pack = null;
                _packReferences = 0;
            }
            if (PipelineLayout.Handle != 0)
                vk.DestroyPipelineLayout(device, PipelineLayout, null);
            if (TextureTable.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, TextureTable, null);
            if (Uniform.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, Uniform, null);
            if (Storage.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, Storage, null);
        }

        public void Dispose() => _disposed = true;

        internal sealed class PackLayoutLease : IDisposable
        {
            private Created? _owner;

            internal PackLayoutLease(Created owner, PackState state)
            {
                _owner = owner;
                State = state;
            }

            internal PackState State { get; }
            internal PipelineLayout PipelineLayout => State.PipelineLayout;

            public void Dispose()
            {
                Created? owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleasePackLayout(State);
            }
        }

        /// <summary>
        /// One generation of the pack layout and all descriptor pools allocated
        /// against it. Keeping the pools here prevents a stale per-flight set
        /// from outliving the descriptor-set layout it was allocated from.
        /// </summary>
        internal sealed unsafe class PackState
        {
            private const int SetsPerPool = 32;
            private readonly Silk.NET.Vulkan.Vk _vk;
            private readonly Device _device;
            private readonly List<DescriptorPool> _pools = [];
            private int _setCount;
            private bool _destroyed;

            internal PackState(
                Silk.NET.Vulkan.Vk vk,
                Device device,
                int generation,
                DescriptorSetLayout descriptorSetLayout,
                PipelineLayout pipelineLayout)
            {
                _vk = vk;
                _device = device;
                Generation = generation;
                DescriptorSetLayout = descriptorSetLayout;
                PipelineLayout = pipelineLayout;
            }

            internal int Generation { get; }
            internal DescriptorSetLayout DescriptorSetLayout { get; }
            internal PipelineLayout PipelineLayout { get; }

            internal DescriptorSet AllocateDescriptorSet()
            {
                ObjectDisposedException.ThrowIf(_destroyed, this);
                if (_setCount % SetsPerPool == 0)
                    _pools.Add(CreatePool());

                DescriptorSetLayout layout = DescriptorSetLayout;
                var allocate = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _pools[^1],
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                VulkanInterop.Check(
                    _vk.AllocateDescriptorSets(_device, &allocate, out DescriptorSet set),
                    "vkAllocateDescriptorSets (render-pack set 3)");
                _setCount++;
                return set;
            }

            private DescriptorPool CreatePool()
            {
                var size = new DescriptorPoolSize
                {
                    Type = DescriptorType.UniformBufferDynamic,
                    DescriptorCount = PackUniformBindingCount * SetsPerPool,
                };
                var create = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = SetsPerPool,
                    PoolSizeCount = 1,
                    PPoolSizes = &size,
                };
                VulkanInterop.Check(
                    _vk.CreateDescriptorPool(_device, &create, null, out DescriptorPool pool),
                    "vkCreateDescriptorPool (render-pack set 3)");
                return pool;
            }

            internal void Destroy()
            {
                if (_destroyed)
                    return;
                _destroyed = true;
                foreach (DescriptorPool pool in _pools)
                    _vk.DestroyDescriptorPool(_device, pool, null);
                _pools.Clear();
                if (PipelineLayout.Handle != 0)
                    _vk.DestroyPipelineLayout(_device, PipelineLayout, null);
                if (DescriptorSetLayout.Handle != 0)
                    _vk.DestroyDescriptorSetLayout(_device, DescriptorSetLayout, null);
            }
        }
    }

    internal static Created Create(Silk.NET.Vulkan.Vk vk, Device device)
    {
        ArgumentNullException.ThrowIfNull(vk);

        DescriptorSetLayout storage = default;
        DescriptorSetLayout uniform = default;
        DescriptorSetLayout table = default;
        try
        {
            storage = CreateStorageSetLayout(vk, device);
            uniform = CreateUniformSetLayout(vk, device);
            table = CreateTextureTableSetLayout(vk, device);
            PipelineLayout layout = CreatePipelineLayout(vk, device, storage, uniform, table);
            return new Created(vk, device, storage, uniform, table, layout);
        }
        catch
        {
            if (table.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, table, null);
            if (uniform.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, uniform, null);
            if (storage.Handle != 0)
                vk.DestroyDescriptorSetLayout(device, storage, null);
            throw;
        }
    }

    internal static bool IsDynamicStorageBinding(uint binding) => binding switch
    {
        GpuBindingModel.StorageInstances => true,
        GpuBindingModel.StorageBatches => true,
        GpuBindingModel.StorageClipSlots => true,
        GpuBindingModel.StorageInstanceLightSets => true,
        _ => false,
    };

    internal static uint DynamicStorageBindingCount { get; } = CountDynamicStorageBindings();

    private static uint CountDynamicStorageBindings()
    {
        uint count = 0;
        for (uint binding = 0; binding < GpuBindingModel.StorageBindingCount; binding++)
        {
            if (IsDynamicStorageBinding(binding))
                count++;
        }

        return count;
    }

    internal static DescriptorSetLayout CreateStorageSetLayout(Silk.NET.Vulkan.Vk vk, Device device)
    {
        int count = (int)GpuBindingModel.StorageBindingCount;
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[count];
        for (int i = 0; i < count; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = IsDynamicStorageBinding((uint)i)
                    ? DescriptorType.StorageBufferDynamic
                    : DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            };
        }

        var create = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)count,
            PBindings = bindings,
        };
        VulkanInterop.Check(
            vk.CreateDescriptorSetLayout(device, &create, null, out DescriptorSetLayout layout),
            "vkCreateDescriptorSetLayout (set 0, storage)");
        return layout;
    }

    internal const uint UniformTerrainClip = 2;

    /// <summary>Bindings 5..8 in opt-in set 3.</summary>
    internal const uint PackUniformBindingCount = 4;

    internal static bool IsDeclaredPackUniformBinding(uint binding) =>
        binding >= GpuBindingModel.UniformAtmosphericFrame
        && binding <= GpuBindingModel.UniformPackSettings;

    internal static bool IsDeclaredUniformBinding(uint binding) => binding switch
    {
        GpuBindingModel.UniformSceneLighting => true,
        UniformTerrainClip => true,
        GpuBindingModel.UniformTerrainTiling => true,
        GpuBindingModel.UniformSkyParams => true,
        _ => false,
    };

    /// <summary>
    /// Set 1's declared bindings in ascending order — the order
    /// <c>vkCmdBindDescriptorSets</c> requires its dynamic offsets in.
    /// </summary>
    internal static uint[] DeclaredUniformBindings { get; } = BuildDeclaredUniformBindings();

    private static uint[] BuildDeclaredUniformBindings()
    {
        var bindings = new List<uint>(VulkanFrameBindings.UniformBindingCount);
        for (uint binding = 0; binding < VulkanFrameBindings.UniformBindingCount; binding++)
        {
            if (IsDeclaredUniformBinding(binding))
                bindings.Add(binding);
        }

        return [.. bindings];
    }

    internal static DescriptorSetLayout CreateUniformSetLayout(Silk.NET.Vulkan.Vk vk, Device device)
    {
        uint[] declared = DeclaredUniformBindings;
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[declared.Length];
        for (int i = 0; i < declared.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = declared[i],
                DescriptorType = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            };
        }

        var create = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)declared.Length,
            PBindings = bindings,
        };
        VulkanInterop.Check(
            vk.CreateDescriptorSetLayout(device, &create, null, out DescriptorSetLayout layout),
            "vkCreateDescriptorSetLayout (set 1, uniform)");
        return layout;
    }

    internal static DescriptorSetLayout CreatePackUniformSetLayout(
        Silk.NET.Vulkan.Vk vk,
        Device device)
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[(int)PackUniformBindingCount];
        for (uint i = 0; i < PackUniformBindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = GpuBindingModel.UniformAtmosphericFrame + i,
                DescriptorType = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            };
        }

        var create = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = PackUniformBindingCount,
            PBindings = bindings,
        };
        VulkanInterop.Check(
            vk.CreateDescriptorSetLayout(device, &create, null, out DescriptorSetLayout layout),
            "vkCreateDescriptorSetLayout (set 3, render-pack uniforms)");
        return layout;
    }

    internal static DescriptorSetLayout CreateTextureTableSetLayout(Silk.NET.Vulkan.Vk vk, Device device)
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = GpuBindingModel.TextureTableBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = GpuBindingModel.TextureTableCapacity,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        DescriptorBindingFlags flags =
            DescriptorBindingFlags.PartiallyBoundBit
            | DescriptorBindingFlags.UpdateAfterBindBit
            | DescriptorBindingFlags.UpdateUnusedWhilePendingBit
            | DescriptorBindingFlags.VariableDescriptorCountBit;

        var bindingFlags = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 1,
            PBindingFlags = &flags,
        };
        var create = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &bindingFlags,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 1,
            PBindings = &binding,
        };
        VulkanInterop.Check(
            vk.CreateDescriptorSetLayout(device, &create, null, out DescriptorSetLayout layout),
            "vkCreateDescriptorSetLayout (set 2, texture table)");
        return layout;
    }

    internal static PipelineLayout CreatePipelineLayout(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        DescriptorSetLayout storage,
        DescriptorSetLayout uniform,
        DescriptorSetLayout table)
    {
        DescriptorSetLayout* sets = stackalloc DescriptorSetLayout[3];
        sets[0] = storage;
        sets[1] = uniform;
        sets[2] = table;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = GpuBindingModel.PushConstantBytes,
        };
        var create = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = sets,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };
        VulkanInterop.Check(
            vk.CreatePipelineLayout(device, &create, null, out PipelineLayout layout),
            "vkCreatePipelineLayout");
        return layout;
    }

    internal static PipelineLayout CreatePackPipelineLayout(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        DescriptorSetLayout storage,
        DescriptorSetLayout uniform,
        DescriptorSetLayout table,
        DescriptorSetLayout packUniform)
    {
        DescriptorSetLayout* sets = stackalloc DescriptorSetLayout[4];
        sets[0] = storage;
        sets[1] = uniform;
        sets[2] = table;
        sets[3] = packUniform;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = GpuBindingModel.PushConstantBytes,
        };
        var create = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 4,
            PSetLayouts = sets,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };
        VulkanInterop.Check(
            vk.CreatePipelineLayout(device, &create, null, out PipelineLayout layout),
            "vkCreatePipelineLayout (render-pack ABI)");
        return layout;
    }
}
