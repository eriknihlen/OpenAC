using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanSynchronizationDependencyTests
{
    private const PipelineStageFlags2 DepthStages =
        PipelineStageFlags2.EarlyFragmentTestsBit |
        PipelineStageFlags2.LateFragmentTestsBit;

    [Fact]
    public void AcquiredImage_FirstTransitionChainsToTheActualSemaphoreWait()
    {
        var image = new VkImage(0x4771u);
        var semaphore = new VkSemaphore(0x4772u);

        SemaphoreSubmitInfo wait = VulkanGpuDevice.CreateAcquiredImageWait(semaphore);
        ImageMemoryBarrier2 barrier =
            VulkanGpuDevice.CreateBackbufferRenderingBarrier(image, first: true);

        Assert.Equal(StructureType.SemaphoreSubmitInfo, wait.SType);
        Assert.Equal(semaphore, wait.Semaphore);
        Assert.Equal(VulkanGpuDevice.AcquiredImageWaitStage, wait.StageMask);
        Assert.Equal(wait.StageMask, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.None, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.ColorAttachmentWriteBit, barrier.DstAccessMask);
        Assert.Equal(ImageLayout.Undefined, barrier.OldLayout);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, barrier.NewLayout);
        Assert.Equal(image, barrier.Image);
        AssertColorImage(barrier.SubresourceRange);
    }

    [Fact]
    public void AcquiredImage_SubsequentPassPreservesPriorColorWrites()
    {
        var image = new VkImage(0x4773u);

        ImageMemoryBarrier2 barrier =
            VulkanGpuDevice.CreateBackbufferRenderingBarrier(image, first: false);

        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.ColorAttachmentWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, barrier.DstStageMask);
        Assert.Equal(
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
            barrier.DstAccessMask);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, barrier.OldLayout);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, barrier.NewLayout);
        Assert.Equal(image, barrier.Image);
        AssertColorImage(barrier.SubresourceRange);
    }

    [Fact]
    public void BackbufferBarrier_IsBoundToOrdinaryAndFilmicProductionPasses()
    {
        string resources = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanGpuDevice.Resources.cs");
        string frame = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanGpuFrame.cs");
        string ordinary = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanCompositionFramePhases.cs");
        string filmic = Source("src", "AcDream.App", "Rendering", "Packs",
            "AtmosphericPostProcessGraph.cs");

        Assert.Contains(
            "ImageMemoryBarrier2 barrier = CreateBackbufferRenderingBarrier(image, first);",
            resources,
            StringComparison.Ordinal);
        string device = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanGpuDevice.cs");
        Assert.Contains(
            "SemaphoreSubmitInfo waitSemaphore = CreateAcquiredImageWait(_imageAcquired[slot]);",
            device,
            StringComparison.Ordinal);
        Assert.Contains("_device.BeginPass(this, description)", frame, StringComparison.Ordinal);
        Assert.Contains("Name = \"vk-world\"", ordinary, StringComparison.Ordinal);
        Assert.Contains("Target: null", ordinary, StringComparison.Ordinal);
        Assert.Contains("\"atmospheric-filmic\"", filmic, StringComparison.Ordinal);
        Assert.Contains("target: null", filmic, StringComparison.Ordinal);
    }

    [Fact]
    public void DepthTransitions_DistinguishSingleSampleSampleableDepth()
    {
        var image = new VkImage(0x4774u);
        ImageMemoryBarrier2 entry = VulkanGpuDevice.CreateRenderTargetDepthEntryBarrier(
            image,
            ImageLayout.Undefined,
            fixedFunctionResolve: false);
        ImageMemoryBarrier2 exit = VulkanGpuDevice.CreateRenderTargetDepthSamplingBarrier(
            image,
            ImageLayout.DepthStencilAttachmentOptimal,
            fixedFunctionResolve: false);

        AssertDepthEntry(entry, image, DepthStages, AccessFlags2.DepthStencilAttachmentWriteBit);
        AssertDepthExit(exit, image, DepthStages, AccessFlags2.DepthStencilAttachmentWriteBit);
    }

    [Fact]
    public void DepthTransitions_MultisampleNonSampleableDepthKeepsOrdinaryWriterMasks()
    {
        var image = new VkImage(0x4775u);
        ImageMemoryBarrier2 entry = VulkanGpuDevice.CreateRenderTargetDepthEntryBarrier(
            image,
            ImageLayout.Undefined,
            fixedFunctionResolve: false);

        AssertDepthEntry(entry, image, DepthStages, AccessFlags2.DepthStencilAttachmentWriteBit);
    }

    [Fact]
    public void DepthTransitions_SampleableMsaaResolveUsesColorWriterAtEntryAndExit()
    {
        var image = new VkImage(0x4776u);
        ImageMemoryBarrier2 entry = VulkanGpuDevice.CreateRenderTargetDepthEntryBarrier(
            image,
            ImageLayout.Undefined,
            fixedFunctionResolve: true);
        ImageMemoryBarrier2 exit = VulkanGpuDevice.CreateRenderTargetDepthSamplingBarrier(
            image,
            ImageLayout.DepthStencilAttachmentOptimal,
            fixedFunctionResolve: true);

        AssertDepthEntry(
            entry,
            image,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);
        AssertDepthExit(
            exit,
            image,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit);
    }

    [Fact]
    public void DepthBarrierFactories_AreBoundToTheActualAttachmentAndResolveBranches()
    {
        string source = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanGpuDevice.Resources.cs");

        Assert.Contains(
            "depth.CurrentLayout,\n                fixedFunctionResolve: false));",
            Normalize(source),
            StringComparison.Ordinal);
        Assert.Contains(
            "depthResolve.CurrentLayout,\n                fixedFunctionResolve: true));",
            Normalize(source),
            StringComparison.Ordinal);
        Assert.Contains(
            "fixedFunctionResolve: target.DepthResolve is not null));",
            Normalize(source),
            StringComparison.Ordinal);
        Assert.Contains(
            "depthAttachment.ResolveMode = ResolveModeFlags.SampleZeroBit;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "stencilAttachment.ResolveMode = ResolveModeFlags.SampleZeroBit;",
            source,
            StringComparison.Ordinal);

        string target = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanGpuRenderTarget.cs");
        Assert.Contains(
            "int retainedDepthSamples =\n                description.SampleableDepth ? 1 : description.SampleCount;",
            Normalize(target),
            StringComparison.Ordinal);
        Assert.Contains(
            "Description.SampleableDepth && _multisampleDepth is not null ? _depth : null",
            target,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceMigrationBarrier_CoversTheExactSourceRange()
    {
        var source = new VkBuffer(0x4777u);
        BufferMemoryBarrier2 barrier = VulkanUploadQueue.CreateDeviceMigrationReadBarrier(
            source,
            sourceOffsetBytes: 4096,
            byteCount: 8192);

        Assert.Equal(StructureType.BufferMemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.AllTransferBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.AllTransferBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.TransferReadBit, barrier.DstAccessMask);
        Assert.Equal(source, barrier.Buffer);
        Assert.Equal(4096ul, barrier.Offset);
        Assert.Equal(8192ul, barrier.Size);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
    }

    [Fact]
    public void DeviceMigrationBarrier_RejectsAnEmptyRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VulkanUploadQueue.CreateDeviceMigrationReadBarrier(default, 0, 0));
    }

    [Fact]
    public void UploadQueue_ActualNativeRecord_StagesThenMigratesWithExactDependencyAndOneConsumerBarrier()
    {
        using var native = new RecordingNativeContext();
        var queue = CreateCommandWitnessQueue(native);
        var commands = new CommandBuffer((nint)0xC04771u);
        var staging = new VkBuffer(0xA04771u);
        var a = new VkBuffer(0xA04772u);
        var b = new VkBuffer(0xA04773u);

        EnqueueHostStagingCopy(queue, staging, a, sourceOffset: 16, destinationOffset: 32, size: 64);
        queue.EnqueueBufferCopy(a, b, sourceOffsetBytes: 48, destinationOffsetBytes: 96, byteCount: 128);

        Assert.True(queue.Record(commands));
        Assert.Collection(
            native.Commands,
            command => AssertCopy(command, commands, staging, a, 16, 32, 64),
            command => AssertMigrationBarrier(command, commands, a, 48, 128),
            command => AssertCopy(command, commands, a, b, 48, 96, 128),
            command => AssertTrailingConsumerBarrier(command, commands));

        int emitted = native.Commands.Count;
        Assert.False(queue.Record(commands));
        Assert.Equal(emitted, native.Commands.Count);
    }

    [Fact]
    public void UploadQueue_ActualNativeRecord_EmitsEachMigrationDependencyInAnAToBToCChain()
    {
        using var native = new RecordingNativeContext();
        var queue = CreateCommandWitnessQueue(native);
        var commands = new CommandBuffer((nint)0xC04772u);
        var a = new VkBuffer(0xB04771u);
        var b = new VkBuffer(0xB04772u);
        var c = new VkBuffer(0xB04773u);

        queue.EnqueueBufferCopy(a, b, sourceOffsetBytes: 64, destinationOffsetBytes: 80, byteCount: 144);
        queue.EnqueueBufferCopy(b, c, sourceOffsetBytes: 96, destinationOffsetBytes: 112, byteCount: 288);

        Assert.True(queue.Record(commands));
        Assert.Collection(
            native.Commands,
            command => AssertMigrationBarrier(command, commands, a, 64, 144),
            command => AssertCopy(command, commands, a, b, 64, 80, 144),
            command => AssertMigrationBarrier(command, commands, b, 96, 288),
            command => AssertCopy(command, commands, b, c, 96, 112, 288),
            command => AssertTrailingConsumerBarrier(command, commands));
    }

    [Fact]
    public void UploadQueue_ActualNativeRecord_EmitsMigrationDependencyForAnEarlierDrainProducer()
    {
        using var native = new RecordingNativeContext();
        var queue = CreateCommandWitnessQueue(native);
        var commands = new CommandBuffer((nint)0xC04773u);
        var staging = new VkBuffer(0xD04771u);
        var a = new VkBuffer(0xD04772u);
        var b = new VkBuffer(0xD04773u);

        EnqueueHostStagingCopy(queue, staging, a, sourceOffset: 256, destinationOffset: 512, size: 1024);
        Assert.True(queue.Record(commands));
        Assert.Collection(
            native.Commands,
            command => AssertCopy(command, commands, staging, a, 256, 512, 1024),
            command => AssertTrailingConsumerBarrier(command, commands));

        queue.EnqueueBufferCopy(a, b, sourceOffsetBytes: 640, destinationOffsetBytes: 768, byteCount: 896);
        Assert.True(queue.Record(commands));
        Assert.Collection(
            native.Commands,
            command => AssertCopy(command, commands, staging, a, 256, 512, 1024),
            command => AssertTrailingConsumerBarrier(command, commands),
            command => AssertMigrationBarrier(command, commands, a, 640, 896),
            command => AssertCopy(command, commands, a, b, 640, 768, 896),
            command => AssertTrailingConsumerBarrier(command, commands));
    }

    [Fact]
    public void UploadQueue_ActualNativeRecordBodyRemainsBoundToRecord()
    {
        string source = Source("src", "AcDream.App", "Rendering", "Gpu", "Vk",
            "VulkanUploadQueue.cs");
        int loopStart = source.IndexOf(
            "foreach (BufferCopy2 copy in _bufferCopies)",
            StringComparison.Ordinal);
        int loopEnd = source.IndexOf(
            "foreach (ImageCopy2 copy in _imageCopies)",
            loopStart,
            StringComparison.Ordinal);
        Assert.True(loopStart >= 0 && loopEnd > loopStart);
        string loop = source[loopStart..loopEnd];

        int predicate = loop.IndexOf(
            "RequiresDeviceMigrationReadBarrier(copy.Kind)",
            StringComparison.Ordinal);
        int descriptor = loop.IndexOf(
            "CreateDeviceMigrationReadBarrier(",
            StringComparison.Ordinal);
        int dependency = loop.IndexOf(
            "_vk.CmdPipelineBarrier2(commands, &dependency);",
            StringComparison.Ordinal);
        int copy = loop.IndexOf(
            "_vk.CmdCopyBuffer(commands, copy.Source, copy.Destination, 1, &region);",
            StringComparison.Ordinal);
        Assert.True(predicate >= 0 && descriptor > predicate && dependency > descriptor && copy > dependency);

        Assert.Contains(
            "BufferCopyKind.HostStaging",
            MethodBody(source, "internal void StageBufferWrite(", "internal void StageImageWrite("),
            StringComparison.Ordinal);
        Assert.Contains(
            "BufferCopyKind.DeviceMigration",
            MethodBody(source, "internal void EnqueueBufferCopy(", "internal bool Record("),
            StringComparison.Ordinal);

        Assert.Contains(
            "SrcAccessMask = AccessFlags2.TransferWriteBit",
            source[loopEnd..],
            StringComparison.Ordinal);
        Assert.Contains(
            "DstStageMask = PipelineStageFlags2.VertexInputBit",
            source[loopEnd..],
            StringComparison.Ordinal);
        Assert.Contains(
            "DstAccessMask = AccessFlags2.VertexAttributeReadBit",
            source[loopEnd..],
            StringComparison.Ordinal);
    }

    private static VulkanUploadQueue CreateCommandWitnessQueue(RecordingNativeContext native)
    {
        var queue = (VulkanUploadQueue)RuntimeHelpers.GetUninitializedObject(typeof(VulkanUploadQueue));
        SetField(queue, "_vk", new Silk.NET.Vulkan.Vk(native));
        InitializeCollectionField(queue, "_bufferCopies");
        InitializeCollectionField(queue, "_imageCopies");
        InitializeCollectionField(queue, "_mipBlits");
        InitializeCollectionField(queue, "_imageEntryLayouts");
        return queue;
    }

    private static void EnqueueHostStagingCopy(
        VulkanUploadQueue queue,
        VkBuffer source,
        VkBuffer destination,
        ulong sourceOffset,
        ulong destinationOffset,
        ulong size)
    {
        FieldInfo field = Field("_bufferCopies");
        var copies = Assert.IsAssignableFrom<IList>(field.GetValue(queue));
        Type copyType = field.FieldType.GenericTypeArguments.Single();
        object copy = Activator.CreateInstance(
            copyType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                source,
                destination,
                sourceOffset,
                destinationOffset,
                size,
                VulkanUploadQueue.BufferCopyKind.HostStaging,
            ],
            culture: null) ?? throw new InvalidOperationException("Could not construct pending staging copy.");
        copies.Add(copy);
    }

    private static void InitializeCollectionField(VulkanUploadQueue queue, string name)
    {
        FieldInfo field = Field(name);
        SetField(queue, name, Activator.CreateInstance(field.FieldType)!);
    }

    private static void SetField(VulkanUploadQueue queue, string name, object value) =>
        Field(name).SetValue(queue, value);

    private static FieldInfo Field(string name) =>
        typeof(VulkanUploadQueue).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing VulkanUploadQueue field {name}.");

    private static void AssertCopy(
        RecordedNativeCommand command,
        CommandBuffer commands,
        VkBuffer source,
        VkBuffer destination,
        ulong sourceOffset,
        ulong destinationOffset,
        ulong size)
    {
        Assert.Equal(RecordedNativeCommandKind.CopyBuffer, command.Kind);
        Assert.Equal(commands, command.Commands);
        Assert.Equal(source, command.Source);
        Assert.Equal(destination, command.Destination);
        Assert.Equal(1u, command.RegionCount);
        Assert.Equal(sourceOffset, command.Copy.SrcOffset);
        Assert.Equal(destinationOffset, command.Copy.DstOffset);
        Assert.Equal(size, command.Copy.Size);
    }

    private static void AssertMigrationBarrier(
        RecordedNativeCommand command,
        CommandBuffer commands,
        VkBuffer source,
        ulong sourceOffset,
        ulong size)
    {
        Assert.Equal(RecordedNativeCommandKind.BufferBarrier, command.Kind);
        Assert.Equal(commands, command.Commands);
        BufferMemoryBarrier2 barrier = command.BufferBarrier;
        Assert.Equal(StructureType.BufferMemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.AllTransferBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.AllTransferBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.TransferReadBit, barrier.DstAccessMask);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
        Assert.Equal(source, barrier.Buffer);
        Assert.Equal(sourceOffset, barrier.Offset);
        Assert.Equal(size, barrier.Size);
    }

    private static void AssertTrailingConsumerBarrier(
        RecordedNativeCommand command,
        CommandBuffer commands)
    {
        Assert.Equal(RecordedNativeCommandKind.MemoryBarrier, command.Kind);
        Assert.Equal(commands, command.Commands);
        MemoryBarrier2 barrier = command.MemoryBarrier;
        Assert.Equal(StructureType.MemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.AllTransferBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
        Assert.Equal(
            PipelineStageFlags2.VertexInputBit |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit |
            PipelineStageFlags2.DrawIndirectBit,
            barrier.DstStageMask);
        Assert.Equal(
            AccessFlags2.VertexAttributeReadBit |
            AccessFlags2.IndexReadBit |
            AccessFlags2.ShaderReadBit |
            AccessFlags2.UniformReadBit |
            AccessFlags2.IndirectCommandReadBit,
            barrier.DstAccessMask);
    }

    private enum RecordedNativeCommandKind
    {
        BufferBarrier,
        CopyBuffer,
        MemoryBarrier,
    }

    private readonly record struct RecordedNativeCommand(
        RecordedNativeCommandKind Kind,
        CommandBuffer Commands,
        VkBuffer Source,
        VkBuffer Destination,
        uint RegionCount,
        BufferCopy Copy,
        BufferMemoryBarrier2 BufferBarrier,
        MemoryBarrier2 MemoryBarrier);

    private sealed unsafe class RecordingNativeContext : INativeContext
    {
        private static RecordingNativeContext? s_active;

        internal RecordingNativeContext()
        {
            Assert.Null(s_active);
            s_active = this;
        }

        internal List<RecordedNativeCommand> Commands { get; } = [];

        public nint GetProcAddress(string proc, int? slot = null) => proc switch
        {
            "vkCmdCopyBuffer" =>
                (nint)(delegate* unmanaged<CommandBuffer, VkBuffer, VkBuffer, uint, BufferCopy*, void>)
                    &CaptureCopyBuffer,
            "vkCmdPipelineBarrier2" =>
                (nint)(delegate* unmanaged<CommandBuffer, DependencyInfo*, void>)
                    &CapturePipelineBarrier,
            _ => (nint)(delegate* unmanaged<void>)&NoOp,
        };

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
        {
            addr = GetProcAddress(proc, slot);
            return true;
        }

        public void Dispose()
        {
            if (ReferenceEquals(s_active, this))
                s_active = null;
        }

        [UnmanagedCallersOnly]
        private static void CaptureCopyBuffer(
            CommandBuffer commands,
            VkBuffer source,
            VkBuffer destination,
            uint regionCount,
            BufferCopy* regions)
        {
            RecordingNativeContext active = s_active!;
            active.Commands.Add(new RecordedNativeCommand(
                RecordedNativeCommandKind.CopyBuffer,
                commands,
                source,
                destination,
                regionCount,
                regionCount == 0 ? default : regions[0],
                default,
                default));
        }

        [UnmanagedCallersOnly]
        private static void CapturePipelineBarrier(CommandBuffer commands, DependencyInfo* dependency)
        {
            RecordingNativeContext active = s_active!;
            if (dependency->BufferMemoryBarrierCount == 1)
            {
                active.Commands.Add(new RecordedNativeCommand(
                    RecordedNativeCommandKind.BufferBarrier,
                    commands,
                    default,
                    default,
                    0,
                    default,
                    dependency->PBufferMemoryBarriers[0],
                    default));
                return;
            }

            active.Commands.Add(new RecordedNativeCommand(
                RecordedNativeCommandKind.MemoryBarrier,
                commands,
                default,
                default,
                0,
                default,
                default,
                dependency->PMemoryBarriers[0]));
        }

        [UnmanagedCallersOnly]
        private static void NoOp()
        {
        }
    }

    private static void AssertDepthEntry(
        ImageMemoryBarrier2 barrier,
        VkImage image,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        Assert.Equal(StructureType.ImageMemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.AllCommandsBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.None, barrier.SrcAccessMask);
        Assert.Equal(destinationStage, barrier.DstStageMask);
        Assert.Equal(destinationAccess, barrier.DstAccessMask);
        Assert.Equal(ImageLayout.Undefined, barrier.OldLayout);
        Assert.Equal(ImageLayout.DepthStencilAttachmentOptimal, barrier.NewLayout);
        Assert.Equal(image, barrier.Image);
        AssertDepthStencilImage(barrier.SubresourceRange);
    }

    private static void AssertDepthExit(
        ImageMemoryBarrier2 barrier,
        VkImage image,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess)
    {
        Assert.Equal(StructureType.ImageMemoryBarrier2, barrier.SType);
        Assert.Equal(sourceStage, barrier.SrcStageMask);
        Assert.Equal(sourceAccess, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.FragmentShaderBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.ShaderReadBit, barrier.DstAccessMask);
        Assert.Equal(ImageLayout.DepthStencilAttachmentOptimal, barrier.OldLayout);
        Assert.Equal(ImageLayout.DepthStencilReadOnlyOptimal, barrier.NewLayout);
        Assert.Equal(image, barrier.Image);
        AssertDepthStencilImage(barrier.SubresourceRange);
    }

    private static void AssertColorImage(ImageSubresourceRange range)
    {
        Assert.Equal(ImageAspectFlags.ColorBit, range.AspectMask);
        Assert.Equal(0u, range.BaseMipLevel);
        Assert.Equal(1u, range.LevelCount);
        Assert.Equal(0u, range.BaseArrayLayer);
        Assert.Equal(1u, range.LayerCount);
    }

    private static void AssertDepthStencilImage(ImageSubresourceRange range)
    {
        Assert.Equal(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, range.AspectMask);
        Assert.Equal(0u, range.BaseMipLevel);
        Assert.Equal(Silk.NET.Vulkan.Vk.RemainingMipLevels, range.LevelCount);
        Assert.Equal(0u, range.BaseArrayLayer);
        Assert.Equal(Silk.NET.Vulkan.Vk.RemainingArrayLayers, range.LayerCount);
    }

    private static string MethodBody(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. path]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
