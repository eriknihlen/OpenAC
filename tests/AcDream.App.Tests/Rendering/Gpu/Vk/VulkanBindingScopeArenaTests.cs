using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanBindingScopeArenaTests
{
    private const ulong RingBuffer = 0x1000;
    private const ulong DummyBuffer = 0x2000;
    private const ulong DispatcherBuffer = 0x3000;
    private const ulong EnvCellBuffer = 0x4000;

    private static VulkanBindingScopeArena CreateArena()
    {
        var arena = new VulkanBindingScopeArena(
            (int)GpuBindingModel.StorageBindingCount,
            VulkanFrameBindings.UniformBindingCount,
            VulkanPipelineLayouts.IsDynamicStorageBinding);
        for (uint binding = 0; binding < GpuBindingModel.StorageBindingCount; binding++)
            arena.SeedStorage(binding, DummyBuffer, offsetBytes: 0, rangeBytes: 65536);
        for (uint binding = 0; binding < VulkanFrameBindings.UniformBindingCount; binding++)
            arena.SeedUniform(binding, DummyBuffer, rangeBytes: 65536);
        return arena;
    }

    [Fact]
    public void TwoRenderersBindingTheSameBindingToDifferentBuffersGetDifferentEntries()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int firstIndex, _, bool firstWrite) = arena.Resolve();
        arena.AssignSlot(firstIndex, slot: 0);
        Assert.True(firstWrite);

        arena.SetStorage(GpuBindingModel.StorageInstances, EnvCellBuffer, 0, 4096);
        (int secondIndex, _, bool secondWrite) = arena.Resolve();
        arena.AssignSlot(secondIndex, slot: 1);

        Assert.True(secondWrite);
        Assert.NotEqual(firstIndex, secondIndex);
        Assert.Equal(2, arena.Count);
        Assert.Equal(2, arena.LiveCount);
    }

    [Fact]
    public void ReturningToAnEarlierRenderersBuffersReusesItsEntryWithoutRewriting()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int dispatcher, _, _) = arena.Resolve();
        arena.AssignSlot(dispatcher, slot: 0);

        arena.SetStorage(GpuBindingModel.StorageInstances, EnvCellBuffer, 0, 4096);
        (int envCell, _, _) = arena.Resolve();
        arena.AssignSlot(envCell, slot: 1);

        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int again, int slot, bool needsWrite) = arena.Resolve();

        Assert.Equal(dispatcher, again);
        Assert.Equal(0, slot);
        Assert.False(needsWrite);
        Assert.Equal(2, arena.Count);
    }

    [Fact]
    public void ADynamicBindingsMovingOffsetCostsNoNewEntryAndNoWrite()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        arena.SetStorage(GpuBindingModel.StorageInstances, RingBuffer, 0, 4096);
        (int index, _, _) = arena.Resolve();
        arena.AssignSlot(index, slot: 0);

        arena.SetStorage(GpuBindingModel.StorageInstances, RingBuffer, 8192, 4096);
        (int second, _, bool needsWrite) = arena.Resolve();

        Assert.Equal(index, second);
        Assert.False(needsWrite);
        Assert.Equal(1, arena.Count);
        Assert.Equal(8192u, arena.StorageOffset(GpuBindingModel.StorageInstances));
        Assert.Equal(0u, arena.StorageDescriptorOffset(GpuBindingModel.StorageInstances));
    }

    [Fact]
    public void APlainBindingsMovingOffsetIsDescriptorVisibleAndTakesANewEntry()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        // Binding 4 (global lights) is plain: V6g's rule keeps only ring-fed
        // bindings dynamic, so a plain binding's offset lives in its descriptor.
        Assert.False(VulkanPipelineLayouts.IsDynamicStorageBinding(GpuBindingModel.StorageGlobalLights));

        arena.SetStorage(GpuBindingModel.StorageGlobalLights, RingBuffer, 0, 256);
        (int first, _, _) = arena.Resolve();
        arena.AssignSlot(first, slot: 0);

        arena.SetStorage(GpuBindingModel.StorageGlobalLights, RingBuffer, 512, 256);
        (int second, _, bool needsWrite) = arena.Resolve();
        arena.AssignSlot(second, slot: 1);

        Assert.NotEqual(first, second);
        Assert.True(needsWrite);
        Assert.Equal(512u, arena.StorageDescriptorOffset(GpuBindingModel.StorageGlobalLights));
    }

    [Fact]
    public void ASteadyFrameRewritesNothing()
    {
        VulkanBindingScopeArena arena = CreateArena();

        arena.BeginFrame();
        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int index, _, bool firstWrite) = arena.Resolve();
        arena.AssignSlot(index, slot: 0);
        Assert.True(firstWrite);

        // The next frame on the same flight slot binds the same buffers. The
        // previous frame has retired, so its entry is free to reclaim — and its
        // descriptors already say exactly this, so nothing is written.
        arena.BeginFrame();
        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int reused, int slot, bool needsWrite) = arena.Resolve();

        Assert.Equal(0, reused);
        Assert.Equal(0, slot);
        Assert.False(needsWrite);
        Assert.Equal(1, arena.Count);
    }

    [Fact]
    public void AnEntryReclaimedFromTheLastFrameIsProtectedForTheRestOfThisOne()
    {
        VulkanBindingScopeArena arena = CreateArena();

        arena.BeginFrame();
        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int dispatcher, _, _) = arena.Resolve();
        arena.AssignSlot(dispatcher, slot: 0);
        arena.SetStorage(GpuBindingModel.StorageInstances, EnvCellBuffer, 0, 4096);
        (int envCell, _, _) = arena.Resolve();
        arena.AssignSlot(envCell, slot: 1);

        arena.BeginFrame();
        arena.SetStorage(GpuBindingModel.StorageInstances, EnvCellBuffer, 0, 4096);
        (int reusedEnvCell, int envCellSlot, bool envCellWrite) = arena.Resolve();
        arena.SetStorage(GpuBindingModel.StorageInstances, DispatcherBuffer, 0, 4096);
        (int reusedDispatcher, int dispatcherSlot, bool dispatcherWrite) = arena.Resolve();

        Assert.False(envCellWrite);
        Assert.False(dispatcherWrite);
        Assert.NotEqual(reusedEnvCell, reusedDispatcher);
        Assert.NotEqual(envCellSlot, dispatcherSlot);
        Assert.Equal(2, arena.Count);
        Assert.Equal(2, arena.LiveCount);
    }

    [Fact]
    public void AThirdDistinctStateInOneFrameMaterialisesAThirdEntry()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        Bind(arena, DispatcherBuffer, slot: 0);
        Bind(arena, EnvCellBuffer, slot: 1);
        Bind(arena, RingBuffer, slot: 2);

        Assert.Equal(3, arena.Count);
        Assert.Equal(3, arena.LiveCount);

        static void Bind(VulkanBindingScopeArena arena, ulong buffer, int slot)
        {
            arena.SetStorage(GpuBindingModel.StorageInstances, buffer, 0, 4096);
            (int index, int existing, bool needsWrite) = arena.Resolve();
            Assert.True(needsWrite);
            Assert.Equal(-1, existing);
            arena.AssignSlot(index, slot);
        }
    }

    [Fact]
    public void AUniformBufferChangeAlsoSeparatesScopes()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        arena.SetUniform(GpuBindingModel.UniformSceneLighting, DispatcherBuffer, 0, 256);
        (int first, _, _) = arena.Resolve();
        arena.AssignSlot(first, slot: 0);

        arena.SetUniform(GpuBindingModel.UniformTerrainTiling, EnvCellBuffer, 0, 576);
        (int second, _, bool needsWrite) = arena.Resolve();
        arena.AssignSlot(second, slot: 1);

        Assert.NotEqual(first, second);
        Assert.True(needsWrite);
    }

    [Fact]
    public void AUniformBuffersMovingOffsetCostsNoNewEntry()
    {
        VulkanBindingScopeArena arena = CreateArena();
        arena.BeginFrame();

        arena.SetUniform(GpuBindingModel.UniformSceneLighting, RingBuffer, 0, 256);
        (int index, _, _) = arena.Resolve();
        arena.AssignSlot(index, slot: 0);

        arena.SetUniform(GpuBindingModel.UniformSceneLighting, RingBuffer, 1024, 256);
        (int second, _, bool needsWrite) = arena.Resolve();

        Assert.Equal(index, second);
        Assert.False(needsWrite);
        Assert.Equal(1024u, arena.UniformOffset(GpuBindingModel.UniformSceneLighting));
    }
}
