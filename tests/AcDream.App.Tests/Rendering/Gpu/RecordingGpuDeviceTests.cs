using System.Numerics;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering.Gpu;

public sealed class RecordingGpuDeviceTests
{
    [Fact]
    public void FramePassAndDrawCallsAreRecordedInSubmissionOrder()
    {
        using RecordingGpuDevice device = new();
        IGpuPipeline pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "mesh-opaque",
            Shaders = new GpuShaderSet("mesh_modern"),
            VertexLayout = GpuVertexLayout.WorldMesh,
        });
        IGpuBuffer indirect = device.CreateBuffer(new GpuBufferDescription(
            "indirect", 4096, GpuBufferUsage.Indirect, GpuMemoryResidency.HostWritable));
        device.Clear();

        using (IGpuFrame frame = device.BeginFrame())
        {
            using (IGpuPassEncoder pass = frame.BeginPass(
                GpuPassDescription.BackbufferClear("world", Vector4.Zero, sampleCount: 1)))
            {
                pass.BindPipeline(pipeline);
                pass.SetCullMode(GpuCullMode.None);
                pass.MultiDrawIndexedIndirect(indirect, offsetBytes: 0, drawCount: 12, strideBytes: 20);
            }

            frame.End();
        }

        Assert.Collection(
            device.Calls,
            call => Assert.Equal(new GpuRecordedFrameBegin(1, 0), call),
            call => Assert.Equal(new GpuRecordedPassBegin("world", 1), call),
            call => Assert.Equal(new GpuRecordedPipelineBind("mesh-opaque"), call),
            call => Assert.Equal(new GpuRecordedCullMode(GpuCullMode.None), call),
            call => Assert.Equal(new GpuRecordedMultiDrawIndirect("indirect", 0, 12, 20), call),
            call => Assert.Equal(new GpuRecordedPassEnd("world"), call),
            call => Assert.Equal(new GpuRecordedFrameEnd(1), call));
    }

    [Fact]
    public void RingAllocationsAreAlignedForTheirUsageAndReadableAfterWriting()
    {
        using RecordingGpuDevice device = new();
        uint storageAlignment = device.Capabilities.MinStorageBufferOffsetAlignment;

        using IGpuFrame frame = device.BeginFrame();

        GpuRingAllocation first = frame.AllocateRing(12, GpuRingUsage.Indirect);
        Assert.Equal(0u, first.OffsetBytes);
        Assert.Equal(12, first.Data.Length);

        GpuRingAllocation second = frame.AllocateRing(64, GpuRingUsage.Storage);
        Assert.Equal(0u, second.OffsetBytes % storageAlignment);
        Assert.True(second.OffsetBytes >= 12);

        Span<Matrix4x4> transforms = second.AsSpan<Matrix4x4>();
        Assert.Equal(1, transforms.Length);
        transforms[0] = Matrix4x4.CreateTranslation(1f, 2f, 3f);

        frame.End();

        ReadOnlySpan<byte> ring = device.RingBytes;
        Matrix4x4 written = System.Runtime.InteropServices.MemoryMarshal.Read<Matrix4x4>(
            ring.Slice((int)second.OffsetBytes, 64));
        Assert.Equal(Matrix4x4.CreateTranslation(1f, 2f, 3f), written);
    }

    [Fact]
    public void RingIsRewoundEachFrameSoPerFrameDataDoesNotAccumulate()
    {
        using RecordingGpuDevice device = new();

        using (IGpuFrame first = device.BeginFrame())
        {
            first.AllocateRing(256, GpuRingUsage.Storage);
            first.End();
        }

        uint afterFirst = device.RingBytesAllocated;

        using (IGpuFrame second = device.BeginFrame())
        {
            GpuRingAllocation allocation = second.AllocateRing(256, GpuRingUsage.Storage);
            Assert.Equal(0u, allocation.OffsetBytes);
            second.End();
        }

        Assert.Equal(afterFirst, device.RingBytesAllocated);
    }

    [Fact]
    public void OverlargeRingRequestThrowsRatherThanTruncating()
    {
        using RecordingGpuDevice device = new(ringCapacityBytes: 1024);
        using IGpuFrame frame = device.BeginFrame();

        Assert.Throws<InvalidOperationException>(() =>
        {
            frame.AllocateRing(4096, GpuRingUsage.Storage);
        });
    }

    [Fact]
    public void OverlappingFramesAreRejected()
    {
        using RecordingGpuDevice device = new();
        using IGpuFrame frame = device.BeginFrame();

        Assert.Throws<InvalidOperationException>(device.BeginFrame);
    }

    [Fact]
    public void FrameSlotsAlternateAcrossTwoFramesInFlight()
    {
        using RecordingGpuDevice device = new();

        int[] slots = new int[4];
        for (int i = 0; i < slots.Length; i++)
        {
            using IGpuFrame frame = device.BeginFrame();
            slots[i] = frame.SlotIndex;
            frame.End();
        }

        Assert.Equal([0, 1, 0, 1], slots);
        Assert.Equal(0, device.OpenFrameCount);
    }

    [Fact]
    public void TimerMeasurementsCanBeInspectedOrConsumedExactlyOnce()
    {
        using RecordingGpuDevice device = new();
        device.RecordingTimers.SetResolved("pack-pass", 1.25);

        Assert.True(device.Timers.TryResolve("pack-pass", out double inspected));
        Assert.Equal(1.25, inspected);
        Assert.True(device.Timers.TryTakeResolved("pack-pass", out double consumed));
        Assert.Equal(1.25, consumed);
        Assert.False(device.Timers.TryTakeResolved("pack-pass", out _));
        Assert.False(device.Timers.TryResolve("pack-pass", out _));
    }

    [Fact]
    public void ReleasedTextureSlotsAreRecycledRatherThanLeaked()
    {
        using RecordingGpuDevice device = new();
        IGpuSampler sampler = device.CreateSampler(GpuSamplerDescription.WorldRepeat);
        IGpuTexture texture = device.CreateTexture(new GpuTextureDescription(
            "wall", GpuTextureKind.Texture2DArray, GpuTextureFormat.Bc1Unorm, 64, 64, 4, 1));

        int liveBefore = device.LiveTextureSlotCount;
        GpuTextureSlot slot = device.RegisterTexture(texture, sampler);
        Assert.True(slot.IsAssigned);
        Assert.Equal(liveBefore + 1, device.LiveTextureSlotCount);

        device.ReleaseTextureSlot(slot);
        Assert.Equal(liveBefore, device.LiveTextureSlotCount);

        GpuTextureSlot reused = device.RegisterTexture(texture, sampler);
        Assert.Equal(slot.Index, reused.Index);
    }

    [Fact]
    public void ReleasingAnUnassignedSlotIsRejected()
    {
        using RecordingGpuDevice device = new();
        Assert.Throws<ArgumentException>(() => device.ReleaseTextureSlot(GpuTextureSlot.Unassigned));
    }

    [Fact]
    public void DefaultTextureSlotIsRegisteredAndUsable()
    {
        using RecordingGpuDevice device = new();

        // Renderers needing a fallback take this, rather than assuming slot 0
        // resolves to something sensible.
        Assert.True(device.DefaultTextureSlot.IsAssigned);
    }

    [Fact]
    public void MultisampledHdrTargetExposesSingleSampledColorAndOptionalDepthResults()
    {
        using RecordingGpuDevice device = new();
        device.Clear();
        var description = new GpuRenderTargetDescription(
            "hdr-world",
            1920,
            1080,
            GpuTextureFormat.Rgba16FloatRenderTarget,
            GpuTextureFormat.Depth24Stencil8,
            SampleCount: 4,
            SampleableDepth: true);

        var target = Assert.IsType<RecordingGpuRenderTarget>(
            device.CreateRenderTarget(description));

        Assert.Equal(description, target.Description);
        Assert.Equal(GpuTextureFormat.Rgba16FloatRenderTarget, target.ColorTexture.Format);
        Assert.NotNull(target.DepthTexture);
        Assert.Equal(GpuTextureFormat.Depth24Stencil8, target.DepthTexture!.Format);
        Assert.Equal(4, target.AttachmentSampleCount);
        Assert.True(target.UsesMultisampleResolve);
        Assert.Same(target, Assert.Single(device.CreatedRenderTargets));
        Assert.Equal(
            new GpuRecordedRenderTargetCreate(description),
            Assert.Single(device.Calls));

        using IGpuFrame frame = device.BeginFrame();
        using (frame.BeginPass(new GpuPassDescription
        {
            Name = "hdr-world",
            Color = new GpuColorAttachment(
                target,
                GpuLoadOp.Clear,
                GpuStoreOp.Resolve,
                Vector4.Zero),
            Depth = new GpuDepthAttachment(
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                1f,
                0),
            SampleCount = 4,
        }))
        {
        }
        frame.End();

        target.Dispose();
        Assert.True(target.IsDisposed);
        Assert.True(Assert.IsType<RecordingGpuTexture>(target.ColorTexture).IsDisposed);
        Assert.True(Assert.IsType<RecordingGpuTexture>(target.DepthTexture).IsDisposed);
    }

    [Fact]
    public void AttachmentOnlyDepthIsNotExposedAndInvalidResolveContractsFailLoudly()
    {
        using RecordingGpuDevice device = new();
        var target = Assert.IsType<RecordingGpuRenderTarget>(
            device.CreateRenderTarget(new GpuRenderTargetDescription(
                "ordinary-offscreen",
                320,
                240,
                GpuTextureFormat.Rgba8UnormRenderTarget,
                GpuTextureFormat.Depth24Stencil8,
                SampleCount: 1)));
        Assert.Null(target.DepthTexture);

        using IGpuFrame frame = device.BeginFrame();
        Assert.Throws<InvalidOperationException>(() => frame.BeginPass(new GpuPassDescription
        {
            Name = "invalid-resolve",
            Color = new GpuColorAttachment(
                target,
                GpuLoadOp.Clear,
                GpuStoreOp.Resolve,
                Vector4.Zero),
            SampleCount = 1,
        }));
        frame.End();

        Assert.Throws<ArgumentException>(() => device.CreateRenderTarget(
            new GpuRenderTargetDescription(
                "missing-depth",
                16,
                16,
                GpuTextureFormat.Rgba16FloatRenderTarget,
                DepthFormat: null,
                SampleCount: 1,
                SampleableDepth: true)));
    }

    [Fact]
    public void SamplersAreDeduplicatedByValue()
    {
        using RecordingGpuDevice device = new();

        IGpuSampler first = device.CreateSampler(GpuSamplerDescription.WorldRepeat);
        IGpuSampler second = device.CreateSampler(GpuSamplerDescription.WorldRepeat);
        IGpuSampler other = device.CreateSampler(GpuSamplerDescription.UiNearest);

        Assert.Same(first, second);
        Assert.NotSame(first, other);
    }

    [Fact]
    public void QueuedDeviceActionsRunOnlyWhenProcessed()
    {
        using RecordingGpuDevice device = new();
        int ran = 0;

        device.QueueDeviceAction(() => ran++);
        Assert.Equal(0, ran);

        device.ProcessDeviceActions();
        Assert.Equal(1, ran);

        device.ProcessDeviceActions();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void DisposingAFrameClosesItExactlyOnce()
    {
        using RecordingGpuDevice device = new();

        IGpuFrame frame = device.BeginFrame();
        frame.End();
        frame.Dispose();

        Assert.Single(device.OfKind<GpuRecordedFrameEnd>());
        Assert.Equal(0, device.OpenFrameCount);
    }
}
