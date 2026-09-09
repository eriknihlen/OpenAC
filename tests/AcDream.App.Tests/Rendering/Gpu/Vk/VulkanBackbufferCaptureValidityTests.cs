using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Gpu;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed unsafe class VulkanBackbufferCaptureValidityTests
{
    [Fact]
    public void ValidityRequiresTheSuccessfulSubmissionContainingTheRecordedCopy()
    {
        var validity = new VulkanBackbufferCaptureValidity();

        Assert.False(validity.CopyRecorded);
        Assert.False(validity.HasSubmittedCopy);

        validity.Invalidate();
        Assert.False(validity.HasSubmittedCopy);

        validity.RecordCopy();
        Assert.True(validity.CopyRecorded);
        Assert.False(validity.HasSubmittedCopy);

        // Recording without the successful-submit transition represents an
        // open frame or a QueueSubmit2 failure: old/new bytes remain unreadable.
        validity.BeginFrame();
        Assert.False(validity.CopyRecorded);
        Assert.False(validity.HasSubmittedCopy);

        validity.CompleteSubmission();
        Assert.False(validity.HasSubmittedCopy);

        validity.BeginFrame();
        validity.RecordCopy();
        validity.CompleteSubmission();
        Assert.False(validity.CopyRecorded);
        Assert.True(validity.HasSubmittedCopy);

        validity.BeginFrame();
        Assert.False(validity.HasSubmittedCopy);
    }

    [Fact]
    public void ProductionBindsValidityToResizeOpenCopySubmitAndReadEdges()
    {
        string device = Source(
            "src", "AcDream.App", "Rendering", "Gpu", "Vk", "VulkanGpuDevice.cs");
        string resources = Source(
            "src", "AcDream.App", "Rendering", "Gpu", "Vk", "VulkanGpuDevice.Resources.cs");

        string begin = MethodBody(device, "internal bool TryBeginFrame(", "private long CompletedSerial(");
        AssertOrdered(
            begin,
            "long serial = _flights.BeginFrame();",
            "_captureValidity.BeginFrame();",
            "_backbuffer.TryAcquire(");

        string end = MethodBody(device, "internal void EndFrame(", "internal bool PresentSucceeded");
        AssertOrdered(
            end,
            "ImageLayout current = RecordBackbufferCapture(commands, presentable);",
            "_vk.EndCommandBuffer(commands)",
            "_vk.QueueSubmit2(_graphicsQueue, 1, &submit, default)",
            "_captureValidity.CompleteSubmission();");

        string record = MethodBody(
            resources,
            "internal ImageLayout RecordBackbufferCapture(",
            "private void ConfigureBackbufferCapture(");
        AssertOrdered(
            record,
            "_vk.CmdCopyImageToBuffer(",
            "_vk.CmdPipelineBarrier2(commands, &hostReadDependency);",
            "_captureValidity.RecordCopy();");

        string configure = MethodBody(
            resources,
            "private void ConfigureBackbufferCapture(",
            "private readonly bool _retainBackbufferCapture;");
        AssertOrdered(
            configure,
            "_captureValidity.Invalidate();",
            "_captureBuffer?.Dispose();",
            "_captureBuffer = new VulkanGpuBuffer(");

        string capture = MethodBody(
            resources,
            "public byte[] CaptureBackbuffer(",
            "internal ImageLayout RecordBackbufferCapture(");
        AssertOrdered(
            capture,
            "if (!_captureValidity.HasSubmittedCopy)",
            "if (!_captureBuffer.HostWritesAreCoherent)",
            "_vk.DeviceWaitIdle(_device)",
            "_captureBuffer.Read(0, pixels);");
    }

    [Fact]
    public void RecordBackbufferCapture_ActualNativeCallsPublishExactRangeAfterCopy()
    {
        using var native = new RecordingNativeContext();
        VulkanGpuBuffer capture = CreateCaptureBuffer(
            new VkBuffer(0x4761u),
            width: 7,
            height: 5,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            mapped: null);
        var backbuffer = new Backbuffer(width: 7, height: 5);
        VulkanGpuDevice device = CreateDevice(native, capture, backbuffer, width: 7, height: 5);
        var commands = new CommandBuffer((nint)0xC04761u);
        var image = new VkImage(0xA04761u);

        Assert.Equal(ImageLayout.TransferSrcOptimal, device.RecordBackbufferCapture(commands, image));
        Assert.Collection(
            native.Commands,
            command =>
            {
                Assert.Equal(NativeCommandKind.ImageBarrier, command.Kind);
                Assert.Equal(commands, command.Commands);
                Assert.Equal(image, command.ImageBarrier.Image);
                Assert.Equal(ImageLayout.ColorAttachmentOptimal, command.ImageBarrier.OldLayout);
                Assert.Equal(ImageLayout.TransferSrcOptimal, command.ImageBarrier.NewLayout);
            },
            command =>
            {
                Assert.Equal(NativeCommandKind.CopyImageToBuffer, command.Kind);
                Assert.Equal(commands, command.Commands);
                Assert.Equal(image, command.Image);
                Assert.Equal(ImageLayout.TransferSrcOptimal, command.ImageLayout);
                Assert.Equal(capture.Handle, command.Buffer);
                Assert.Equal(1u, command.RegionCount);
                Assert.Equal(new Extent3D(7, 5, 1), command.Copy.ImageExtent);
            },
            command => AssertHostReadBarrier(command, commands, capture.Handle, 7ul * 5 * 4));

        Assert.True(GetValidity(device).CopyRecorded);
    }

    [Fact]
    public void RecordBackbufferCapture_AbsentOrMismatchedCaptureRecordsNothing()
    {
        using var native = new RecordingNativeContext();
        var commands = new CommandBuffer((nint)0xC04762u);
        var image = new VkImage(0xA04762u);
        VulkanGpuDevice absent = CreateDevice(native, null, new Backbuffer(4, 3), 4, 3);

        Assert.Equal(ImageLayout.ColorAttachmentOptimal, absent.RecordBackbufferCapture(commands, image));
        Assert.Empty(native.Commands);

        VulkanGpuBuffer capture = CreateCaptureBuffer(
            new VkBuffer(0x4762u), 4, 3,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            mapped: null);
        VulkanGpuDevice mismatched = CreateDevice(native, capture, new Backbuffer(5, 3), 4, 3);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, mismatched.RecordBackbufferCapture(commands, image));
        Assert.Empty(native.Commands);
        Assert.False(GetValidity(mismatched).CopyRecorded);
    }

    [Fact]
    public void CaptureBackbuffer_CoherentSubmittedCopyWaitsReadsAndSwizzles()
    {
        using var native = new RecordingNativeContext();
        byte* mapped = (byte*)NativeMemory.Alloc(4);
        try
        {
            byte[] source = [11, 22, 33, 44];
            source.CopyTo(new Span<byte>(mapped, 4));
            VulkanGpuBuffer capture = CreateCaptureBuffer(
                new VkBuffer(0x4763u), 1, 1,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                mapped);
            VulkanGpuDevice device = CreateDevice(native, capture, new Backbuffer(1, 1), 1, 1);
            MarkSubmittedCopy(device);

            Assert.Equal(new byte[] { 33, 22, 11, 44 }, device.CaptureBackbuffer(1, 1));
            Assert.Equal(1, native.DeviceWaitIdleCount);
        }
        finally
        {
            NativeMemory.Free(mapped);
        }
    }

    [Fact]
    public void CaptureBackbuffer_NonCoherentSubmittedCopyFailsBeforeNativeWaitOrRead()
    {
        using var native = new RecordingNativeContext();
        byte* mapped = (byte*)NativeMemory.Alloc(4);
        try
        {
            new Span<byte>(mapped, 4).Fill(0xA5);
            VulkanGpuBuffer capture = CreateCaptureBuffer(
                new VkBuffer(0x4764u), 1, 1,
                MemoryPropertyFlags.HostVisibleBit,
                mapped);
            VulkanGpuDevice device = CreateDevice(native, capture, new Backbuffer(1, 1), 1, 1);
            MarkSubmittedCopy(device);

            NotSupportedException error = Assert.Throws<NotSupportedException>(
                () => device.CaptureBackbuffer(1, 1));
            Assert.Contains("host-coherent memory", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, native.DeviceWaitIdleCount);
            Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5 }, new Span<byte>(mapped, 4).ToArray());
        }
        finally
        {
            NativeMemory.Free(mapped);
        }
    }

    private static VulkanGpuDevice CreateDevice(
        RecordingNativeContext native,
        VulkanGpuBuffer? capture,
        IVulkanBackbuffer backbuffer,
        uint width,
        uint height)
    {
        var device = (VulkanGpuDevice)RuntimeHelpers.GetUninitializedObject(typeof(VulkanGpuDevice));
        SetField(device, "_vk", new Silk.NET.Vulkan.Vk(native));
        SetField(device, "_device", new Device((nint)0x4760));
        SetField(device, "_captureBuffer", capture);
        SetField(device, "_backbuffer", backbuffer);
        SetField(device, "_captureWidth", width);
        SetField(device, "_captureHeight", height);
        SetField(device, "_captureValidity", new VulkanBackbufferCaptureValidity());
        return device;
    }

    private static VulkanGpuBuffer CreateCaptureBuffer(
        VkBuffer handle,
        uint width,
        uint height,
        MemoryPropertyFlags properties,
        void* mapped)
    {
        ulong size = checked((ulong)width * height * 4);
        var buffer = (VulkanGpuBuffer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanGpuBuffer));
        SetField(buffer, "<Name>k__BackingField", "capture-witness");
        SetField(buffer, "<SizeBytes>k__BackingField", checked((long)size));
        SetField(buffer, "<Usage>k__BackingField", GpuBufferUsage.TransferDestination);
        SetField(buffer, "<Residency>k__BackingField", GpuMemoryResidency.HostReadable);
        SetField(buffer, "<Handle>k__BackingField", handle);
        SetField(buffer, "_allocation", new VulkanAllocation(
            default,
            0,
            size,
            0,
            properties,
            default,
            mapped));
        return buffer;
    }

    private static void MarkSubmittedCopy(VulkanGpuDevice device)
    {
        VulkanBackbufferCaptureValidity validity = GetValidity(device);
        validity.RecordCopy();
        validity.CompleteSubmission();
        SetField(device, "_captureValidity", validity);
    }

    private static VulkanBackbufferCaptureValidity GetValidity(VulkanGpuDevice device) =>
        (VulkanBackbufferCaptureValidity)(Field(typeof(VulkanGpuDevice), "_captureValidity").GetValue(device)
            ?? throw new InvalidOperationException("Missing capture validity."));

    private static void SetField(object target, string name, object? value) =>
        Field(target.GetType(), name).SetValue(target, value);

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing {type.Name} field {name}.");

    private static void AssertHostReadBarrier(
        RecordedNativeCommand command,
        CommandBuffer commands,
        VkBuffer buffer,
        ulong byteCount)
    {
        Assert.Equal(NativeCommandKind.BufferBarrier, command.Kind);
        Assert.Equal(commands, command.Commands);
        BufferMemoryBarrier2 barrier = command.BufferBarrier;
        Assert.Equal(StructureType.BufferMemoryBarrier2, barrier.SType);
        Assert.Equal(PipelineStageFlags2.CopyBit, barrier.SrcStageMask);
        Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags2.HostBit, barrier.DstStageMask);
        Assert.Equal(AccessFlags2.HostReadBit, barrier.DstAccessMask);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
        Assert.Equal(Silk.NET.Vulkan.Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
        Assert.Equal(buffer, barrier.Buffer);
        Assert.Equal(0ul, barrier.Offset);
        Assert.Equal(byteCount, barrier.Size);
    }

    private enum NativeCommandKind
    {
        ImageBarrier,
        CopyImageToBuffer,
        BufferBarrier,
    }

    private readonly record struct RecordedNativeCommand(
        NativeCommandKind Kind,
        CommandBuffer Commands,
        VkImage Image,
        ImageLayout ImageLayout,
        VkBuffer Buffer,
        uint RegionCount,
        BufferImageCopy Copy,
        ImageMemoryBarrier2 ImageBarrier,
        BufferMemoryBarrier2 BufferBarrier);

    private sealed unsafe class RecordingNativeContext : INativeContext
    {
        private static RecordingNativeContext? s_active;

        internal RecordingNativeContext()
        {
            Assert.Null(s_active);
            s_active = this;
        }

        internal List<RecordedNativeCommand> Commands { get; } = [];
        internal int DeviceWaitIdleCount { get; private set; }

        public nint GetProcAddress(string proc, int? slot = null) => proc switch
        {
            "vkCmdCopyImageToBuffer" =>
                (nint)(delegate* unmanaged<CommandBuffer, VkImage, ImageLayout, VkBuffer, uint, BufferImageCopy*, void>)
                    &CaptureCopyImageToBuffer,
            "vkCmdPipelineBarrier2" =>
                (nint)(delegate* unmanaged<CommandBuffer, DependencyInfo*, void>)&CapturePipelineBarrier,
            "vkDeviceWaitIdle" =>
                (nint)(delegate* unmanaged<Device, Result>)&CaptureDeviceWaitIdle,
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
        private static void CaptureCopyImageToBuffer(
            CommandBuffer commands,
            VkImage image,
            ImageLayout layout,
            VkBuffer buffer,
            uint regionCount,
            BufferImageCopy* regions)
        {
            s_active!.Commands.Add(new RecordedNativeCommand(
                NativeCommandKind.CopyImageToBuffer,
                commands,
                image,
                layout,
                buffer,
                regionCount,
                regionCount == 0 ? default : regions[0],
                default,
                default));
        }

        [UnmanagedCallersOnly]
        private static void CapturePipelineBarrier(CommandBuffer commands, DependencyInfo* dependency)
        {
            if (dependency->ImageMemoryBarrierCount == 1)
            {
                s_active!.Commands.Add(new RecordedNativeCommand(
                    NativeCommandKind.ImageBarrier,
                    commands,
                    default,
                    default,
                    default,
                    0,
                    default,
                    dependency->PImageMemoryBarriers[0],
                    default));
                return;
            }

            s_active!.Commands.Add(new RecordedNativeCommand(
                NativeCommandKind.BufferBarrier,
                commands,
                default,
                default,
                default,
                0,
                default,
                default,
                dependency->PBufferMemoryBarriers[0]));
        }

        [UnmanagedCallersOnly]
        private static Result CaptureDeviceWaitIdle(Device device)
        {
            s_active!.DeviceWaitIdleCount++;
            return Result.Success;
        }

        [UnmanagedCallersOnly]
        private static void NoOp()
        {
        }
    }

    private sealed class Backbuffer(uint width, uint height) : IVulkanBackbuffer
    {
        public Format ImageFormat => Format.B8G8R8A8Unorm;
        public uint Width => width;
        public uint Height => height;
        public bool TryAcquire(VkSemaphore acquired, out uint imageIndex) => throw new NotSupportedException();
        public VkImage ImageAt(uint imageIndex) => throw new NotSupportedException();
        public ImageView ViewAt(uint imageIndex) => throw new NotSupportedException();
        public VkSemaphore RenderCompleteAt(uint imageIndex) => throw new NotSupportedException();
        public bool Present(uint imageIndex) => throw new NotSupportedException();
    }

    private static void AssertOrdered(string source, params string[] tokens)
    {
        int previous = -1;
        foreach (string token in tokens)
        {
            int current = source.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Missing or out-of-order production token: {token}");
            previous = current;
        }
    }

    private static string MethodBody(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

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
