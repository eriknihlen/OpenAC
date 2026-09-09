using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WorldTransformFrameArenaTests
{
    [Fact]
    public void ShadowPrefixAndOrdinaryWorldAppendShareExactBindingAndBaseInstanceSpace()
    {
        using var device = new RecordingGpuDevice();
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();
        var arena = new WorldTransformFrameArena();
        Matrix4x4[] shadow =
        [
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateRotationZ(0.4f),
        ];
        Matrix4x4[] ordinary =
        [
            Matrix4x4.CreateScale(2f),
            Matrix4x4.CreateTranslation(9f, 8f, 7f),
        ];

        WorldTransformFrameSlice shadowSlice = arena.Begin(frame, shadow);
        WorldTransformFrameSlice ordinarySlice = arena.Append(frame, ordinary);

        Assert.Same(shadowSlice.Buffer, ordinarySlice.Buffer);
        Assert.Equal(shadowSlice.BaseOffsetBytes, ordinarySlice.BaseOffsetBytes);
        Assert.Equal(shadowSlice.BindingSizeBytes, ordinarySlice.BindingSizeBytes);
        Assert.Equal(0u, shadowSlice.FirstInstance);
        Assert.Equal((uint)shadow.Length, ordinarySlice.FirstInstance);
        Assert.Equal((uint)ordinary.Length, ordinarySlice.InstanceCount);
        Assert.Equal(4u, arena.UsedInstances);
        GpuRecordedRingAllocation allocation = Assert.Single(
            device.OfKind<GpuRecordedRingAllocation>());
        Assert.Equal(GpuRingUsage.Storage, allocation.Usage);
        Assert.Equal(
            checked((int)WorldTransformCapacityPolicy.InitialBindingSizeBytes),
            allocation.ByteCount);

        ReadOnlySpan<Matrix4x4> uploaded = MemoryMarshal.Cast<byte, Matrix4x4>(
            device.RingBytes.Slice(
                checked((int)shadowSlice.BaseOffsetBytes),
                checked((shadow.Length + ordinary.Length) * 64)));
        Assert.Equal(shadow[0], uploaded[0]);
        Assert.Equal(shadow[1], uploaded[1]);
        Assert.Equal(ordinary[0], uploaded[2]);
        Assert.Equal(ordinary[1], uploaded[3]);
    }

    [Fact]
    public void CapacityPolicyGrowsPastTwiceTheFormerCeiling_AndHonorsDeviceLimit()
    {
        const uint moreThanTwiceFormerCapacity = 131_073u;
        uint bytes = WorldTransformCapacityPolicy.ResolveBindingSizeBytes(
            moreThanTwiceFormerCapacity,
            WorldTransformCapacityPolicy.VulkanGuaranteedMaxStorageBufferRangeBytes);

        Assert.True(bytes > 8u * 1024u * 1024u);
        Assert.True(bytes >= moreThanTwiceFormerCapacity * 64u);
        NotSupportedException failure = Assert.Throws<NotSupportedException>(() =>
            WorldTransformCapacityPolicy.ResolveBindingSizeBytes(
                moreThanTwiceFormerCapacity,
                8u * 1024u * 1024u));
        Assert.Contains("fail safe", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedShadowPrefix_AndOrdinaryAppendUseTheExactSamePoseBuffer()
    {
        using var device = new RecordingGpuDevice();
        using IGpuBuffer retained = device.CreateBuffer(new GpuBufferDescription(
            "retained-shadow-slot-0",
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            GpuBufferUsage.Storage | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.HostWritable));
        using IGpuFrame frame = device.BeginFrame();
        Matrix4x4[] shadow =
        [
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateRotationZ(0.4f),
        ];
        retained.Upload(0, MemoryMarshal.AsBytes(shadow.AsSpan()));
        var shadowSlice = new WorldTransformFrameSlice(
            frame.Serial,
            retained,
            BaseOffsetBytes: 0,
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            FirstInstance: 0,
            InstanceCount: 2);
        var arena = new WorldTransformFrameArena();

        WorldTransformFrameSlice published = arena.BeginRetained(
            frame,
            in shadowSlice);
        Matrix4x4[] ordinary = [Matrix4x4.CreateTranslation(9f, 8f, 7f)];
        WorldTransformFrameSlice appended = arena.Append(frame, ordinary);

        Assert.Same(retained, published.Buffer);
        Assert.Same(retained, appended.Buffer);
        Assert.Equal(2u, appended.FirstInstance);
        Assert.Equal(WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            appended.BindingSizeBytes);
        Matrix4x4[] readback = new Matrix4x4[3];
        retained.Read(0, MemoryMarshal.AsBytes(readback.AsSpan()));
        Assert.Equal(shadow[0], readback[0]);
        Assert.Equal(shadow[1], readback[1]);
        Assert.Equal(ordinary[0], readback[2]);
        Assert.Empty(device.OfKind<GpuRecordedRingAllocation>());
    }

    [Fact]
    public void ConnectedDense68395CombinedMatricesRemainInOneAuthoritativeBinding()
    {
        const uint shadowPrefixInstances = 9_498u;
        const int ordinaryInstances = 68_395 - (int)shadowPrefixInstances;
        using var device = new RecordingGpuDevice();
        using IGpuBuffer retained = device.CreateBuffer(new GpuBufferDescription(
            "connected-dense-retained-transform-arena",
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            GpuBufferUsage.Storage | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.HostWritable));
        using IGpuFrame frame = device.BeginFrame();
        var prefix = new WorldTransformFrameSlice(
            frame.Serial,
            retained,
            BaseOffsetBytes: 0,
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            FirstInstance: 0,
            InstanceCount: shadowPrefixInstances);
        var arena = new WorldTransformFrameArena();

        arena.BeginRetained(frame, in prefix);
        WorldTransformFrameSlice ordinary = arena.Append(
            frame,
            new Matrix4x4[ordinaryInstances]);

        Assert.Same(retained, ordinary.Buffer);
        Assert.Equal(shadowPrefixInstances, ordinary.FirstInstance);
        Assert.Equal(68_395u, arena.UsedInstances);
        Assert.Equal(prefix.BindingSizeBytes, ordinary.BindingSizeBytes);
        Assert.True(ordinary.IsValidFor(frame));
        Assert.Empty(device.OfKind<GpuRecordedRingAllocation>());
    }

    [Fact]
    public void AppendOverflowFailsSafeWithoutAllocatingASecondPoseBuffer()
    {
        using var device = new RecordingGpuDevice();
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();
        var arena = new WorldTransformFrameArena();
        const uint bindingBytes = 4u * 64u;
        var full = new Matrix4x4[4];
        arena.Begin(frame, full, bindingBytes);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => arena.Append(frame, [Matrix4x4.Identity]));

        Assert.Contains("fail safe", failure.Message, StringComparison.Ordinal);
        Assert.True(arena.IsActiveFor(frame.Serial));
        Assert.Equal(
            bindingBytes / WorldTransformCapacityPolicy.MatrixBytes,
            arena.UsedInstances);
        Assert.Single(device.OfKind<GpuRecordedRingAllocation>());
    }

    [Fact]
    public void CancelBeforePackRetirementAllowsSameFrameRetailRingPublication()
    {
        using var device = new RecordingGpuDevice();
        device.Clear();
        var retained = Assert.IsType<RecordingGpuBuffer>(
            device.CreateBuffer(new GpuBufferDescription(
                "retained-shadow-slot-0",
                WorldTransformCapacityPolicy.InitialBindingSizeBytes,
                GpuBufferUsage.Storage | GpuBufferUsage.TransferDestination,
                GpuMemoryResidency.HostWritable)));
        using IGpuFrame frame = device.BeginFrame();
        var arena = new WorldTransformFrameArena();
        var retainedPrefix = new WorldTransformFrameSlice(
            frame.Serial,
            retained,
            BaseOffsetBytes: 0,
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            FirstInstance: 0,
            InstanceCount: 1);
        arena.BeginRetained(frame, in retainedPrefix);

        // This is the production late-budget-failure order: release the
        // dispatcher's borrow first, then retire the active pack owner.
        arena.Cancel(frame);
        retained.Dispose();
        WorldTransformFrameSlice retail = arena.Begin(
            frame,
            [Matrix4x4.CreateTranslation(4f, 5f, 6f)]);

        Assert.True(retained.IsDisposed);
        Assert.NotSame(retained, retail.Buffer);
        Assert.Same(device.RingBuffer, retail.Buffer);
        Assert.True(arena.IsActiveFor(frame.Serial));
        Assert.Single(device.OfKind<GpuRecordedRingAllocation>());
    }

    [Fact]
    public void FrameSerialChangeInvalidatesPriorPublication()
    {
        using var device = new RecordingGpuDevice();
        var arena = new WorldTransformFrameArena();
        using (IGpuFrame first = device.BeginFrame())
            arena.Begin(first, [Matrix4x4.Identity]);

        using IGpuFrame second = device.BeginFrame();
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => arena.Append(second, [Matrix4x4.Identity]));

        Assert.Contains("has not been published", failure.Message, StringComparison.Ordinal);
        Assert.False(arena.IsActive);
    }
}
