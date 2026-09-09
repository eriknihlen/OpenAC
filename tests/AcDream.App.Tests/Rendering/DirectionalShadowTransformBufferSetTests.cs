using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowTransformBufferSetTests
{
    [Fact]
    public void FlightSlotsRetainStaticPrefix_AndWarmedReuseAllocatesAndWritesNothing()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        Matrix4x4[] transforms =
        [
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateRotationZ(0.25f),
        ];

        RecordingGpuBuffer slotZero;
        using (IGpuFrame first = device.BeginFrame())
        {
            WorldTransformFrameSlice slice = buffers.Publish(
                first,
                topologyBuildSequence: 7,
                transforms,
                ReadOnlySpan<int>.Empty);
            slotZero = Assert.IsType<RecordingGpuBuffer>(slice.Buffer);
            Assert.Equal(0, first.SlotIndex);
            Assert.Equal(WorldTransformCapacityPolicy.InitialBindingSizeBytes,
                slice.BindingSizeBytes);
        }
        using (IGpuFrame second = device.BeginFrame())
        {
            WorldTransformFrameSlice slice = buffers.Publish(
                second,
                topologyBuildSequence: 7,
                transforms,
                ReadOnlySpan<int>.Empty);
            Assert.Equal(1, second.SlotIndex);
            Assert.NotSame(slotZero, slice.Buffer);
        }

        device.Clear();
        int buffersBefore = device.CreatedBuffers.Count;
        int uploadsBefore = slotZero.UploadCount;
        using IGpuFrame warmed = device.BeginFrame();
        Assert.Equal(0, warmed.SlotIndex);
        buffers.Publish(
            warmed,
            topologyBuildSequence: 7,
            transforms,
            ReadOnlySpan<int>.Empty);
        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowTransformBufferSet.Publish",
            () => buffers.Publish(
                warmed,
                topologyBuildSequence: 7,
                transforms,
                ReadOnlySpan<int>.Empty),
            batchSize: 256);
        Assert.Equal(buffersBefore, device.CreatedBuffers.Count);
        Assert.Equal(uploadsBefore, slotZero.UploadCount);
        Assert.Empty(device.OfKind<GpuRecordedHostStorageVisibility>());
        Assert.False(buffers.LastStats.TopologyUploaded);
        Assert.Equal(0, buffers.LastStats.BytesWritten);
        Assert.Equal(2, buffers.BufferCount);
        Assert.Equal(
            2L * WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            buffers.RetainedGpuBytes);
    }

    [Fact]
    public void StableTopology_UpdatesOnlyStrictDynamicRangesWithExactMatrixBits()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        Matrix4x4[] transforms =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateTranslation(4f, 5f, 6f),
            Matrix4x4.CreateTranslation(7f, 8f, 9f),
            Matrix4x4.CreateTranslation(10f, 11f, 12f),
        ];
        int[] dynamicSlots = [1, 2, 4];

        using (IGpuFrame first = device.BeginFrame())
            buffers.Publish(first, 11, transforms, dynamicSlots);
        using (IGpuFrame second = device.BeginFrame())
            buffers.Publish(second, 11, transforms, dynamicSlots);

        float exactX = BitConverter.Int32BitsToSingle(0x41234567);
        float exactY = BitConverter.Int32BitsToSingle(0x40ABCDEF);
        transforms[1] = Matrix4x4.CreateRotationX(0.3f)
            * Matrix4x4.CreateTranslation(exactX, 20f, 30f);
        transforms[2] = Matrix4x4.CreateRotationY(0.4f)
            * Matrix4x4.CreateTranslation(40f, exactY, 50f);
        transforms[4] = Matrix4x4.CreateRotationZ(0.5f)
            * Matrix4x4.CreateTranslation(60f, 70f, 80f);

        device.Clear();
        using IGpuFrame third = device.BeginFrame();
        WorldTransformFrameSlice slice = buffers.Publish(
            third,
            11,
            transforms,
            dynamicSlots);
        var retained = Assert.IsType<RecordingGpuBuffer>(slice.Buffer);
        Matrix4x4[] readback = new Matrix4x4[transforms.Length];
        retained.Read(0, MemoryMarshal.AsBytes(readback.AsSpan()));

        AssertMatrixBitsEqual(transforms[0], readback[0]);
        AssertMatrixBitsEqual(transforms[1], readback[1]);
        AssertMatrixBitsEqual(transforms[2], readback[2]);
        AssertMatrixBitsEqual(transforms[3], readback[3]);
        AssertMatrixBitsEqual(transforms[4], readback[4]);
        Assert.False(buffers.LastStats.TopologyUploaded);
        Assert.Equal(3, buffers.LastStats.DynamicMatricesUpdated);
        Assert.Equal(2, buffers.LastStats.DynamicRangesUpdated);
        Assert.Equal(3 * 64, buffers.LastStats.BytesWritten);
        Assert.Single(device.OfKind<GpuRecordedHostStorageVisibility>());
    }

    [Fact]
    public void ChangedMatrix_ReplaysExactlyToEveryFlightSlotWithoutRescanningAllDynamics()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        Matrix4x4[] transforms =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateTranslation(4f, 5f, 6f),
        ];
        int[] allDynamic = [1, 2];
        using (IGpuFrame first = device.BeginFrame())
            buffers.Publish(first, 12, transforms, [], allDynamic);
        using (IGpuFrame second = device.BeginFrame())
            buffers.Publish(second, 12, transforms, [], allDynamic);

        float exact = BitConverter.Int32BitsToSingle(0x41234567);
        transforms[2] = Matrix4x4.CreateRotationZ(0.25f)
            * Matrix4x4.CreateTranslation(exact, 8f, 9f);
        RecordingGpuBuffer slotZero;
        using (IGpuFrame changed = device.BeginFrame())
        {
            slotZero = Assert.IsType<RecordingGpuBuffer>(buffers.Publish(
                changed,
                12,
                transforms,
                [2],
                allDynamic).Buffer);
            Assert.Equal(1, buffers.LastStats.DynamicMatricesUpdated);
        }

        device.Clear();
        RecordingGpuBuffer slotOne;
        using (IGpuFrame replay = device.BeginFrame())
        {
            slotOne = Assert.IsType<RecordingGpuBuffer>(buffers.Publish(
                replay,
                12,
                transforms,
                [],
                allDynamic).Buffer);
            Assert.Equal(1, buffers.LastStats.DynamicMatricesUpdated);
            Assert.Equal(64, buffers.LastStats.BytesWritten);
            Assert.Equal(0, buffers.LastStats.CurrentChangedMatrices);
            Assert.Equal(1, buffers.LastStats.PendingReplayMatrices);
        }
        Matrix4x4[] zeroReadback = new Matrix4x4[3];
        Matrix4x4[] oneReadback = new Matrix4x4[3];
        slotZero.Read(0, MemoryMarshal.AsBytes(zeroReadback.AsSpan()));
        slotOne.Read(0, MemoryMarshal.AsBytes(oneReadback.AsSpan()));
        AssertMatrixBitsEqual(transforms[2], zeroReadback[2]);
        AssertMatrixBitsEqual(transforms[2], oneReadback[2]);
        Assert.Single(device.OfKind<GpuRecordedHostStorageVisibility>());
        Assert.True(buffers.RetainedScratchBytes > 0);
    }

    [Fact]
    public void RepeatedChanges_CoalesceToOneLatestValueForWaitingFlightSlot()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        Matrix4x4[] transforms =
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
        ];
        int[] allDynamic = [1];
        using (IGpuFrame first = device.BeginFrame())
            buffers.Publish(first, 13, transforms, [], allDynamic);
        using (IGpuFrame second = device.BeginFrame())
            buffers.Publish(second, 13, transforms, [], allDynamic);

        using (IGpuFrame current = device.BeginFrame())
        {
            transforms[1] = Matrix4x4.CreateTranslation(10f, 20f, 30f);
            buffers.Publish(current, 13, transforms, [1], allDynamic);
            transforms[1] = Matrix4x4.CreateTranslation(40f, 50f, 60f);
            buffers.Publish(current, 13, transforms, [1], allDynamic);
        }
        using IGpuFrame waiting = device.BeginFrame();
        WorldTransformFrameSlice slice = buffers.Publish(
            waiting,
            13,
            transforms,
            [],
            allDynamic);
        var retained = Assert.IsType<RecordingGpuBuffer>(slice.Buffer);
        Matrix4x4[] readback = new Matrix4x4[2];
        retained.Read(0, MemoryMarshal.AsBytes(readback.AsSpan()));

        AssertMatrixBitsEqual(transforms[1], readback[1]);
        Assert.Equal(1, buffers.LastStats.DynamicMatricesUpdated);
        Assert.Equal(1, buffers.LastStats.PendingReplayMatrices);
        Assert.Equal(64, buffers.LastStats.BytesWritten);
    }

    [Fact]
    public void DenseRefresh_UploadsFourExactContiguousRangesAndReplaysDirectlyPerFlight()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        var transforms = new Matrix4x4[2_000];
        for (int index = 0; index < transforms.Length; index++)
            transforms[index] = Matrix4x4.CreateTranslation(index, index + 1, index + 2);
        int[] dynamicSlots = Enumerable.Range(0, 494)
            .Concat(Enumerable.Range(500, 494))
            .Concat(Enumerable.Range(1_000, 494))
            .Concat(Enumerable.Range(1_500, 494))
            .ToArray();
        using (IGpuFrame first = device.BeginFrame())
            buffers.Publish(first, 14, transforms, [], dynamicSlots, false);
        using (IGpuFrame second = device.BeginFrame())
            buffers.Publish(second, 14, transforms, [], dynamicSlots, false);

        for (int index = 0; index < dynamicSlots.Length; index++)
        {
            int slot = dynamicSlots[index];
            transforms[slot] = Matrix4x4.CreateTranslation(
                slot + 10_000,
                slot + 20_000,
                slot + 30_000);
        }
        using (IGpuFrame dense = device.BeginFrame())
        {
            buffers.Publish(
                dense,
                14,
                transforms,
                dynamicSlots,
                dynamicSlots,
                denseRefresh: true);
            Assert.True(buffers.LastStats.DenseDirectUpload);
            Assert.False(buffers.LastStats.DenseFlightReplay);
            Assert.Equal(1_976, buffers.LastStats.DynamicMatricesUpdated);
            Assert.Equal(4, buffers.LastStats.DynamicRangesUpdated);
            Assert.Equal(1_976 * 64, buffers.LastStats.BytesWritten);
            Assert.Equal(0, buffers.LastStats.PendingReplayMatrices);
        }

        using IGpuFrame replay = device.BeginFrame();
        WorldTransformFrameSlice replaySlice = buffers.Publish(
            replay,
            14,
            transforms,
            [],
            dynamicSlots,
            denseRefresh: false);
        Assert.False(buffers.LastStats.DenseDirectUpload);
        Assert.True(buffers.LastStats.DenseFlightReplay);
        Assert.Equal(1_976, buffers.LastStats.DynamicMatricesUpdated);
        Assert.Equal(4, buffers.LastStats.DynamicRangesUpdated);
        Assert.Equal(1_976 * 64, buffers.LastStats.BytesWritten);
        var retained = Assert.IsType<RecordingGpuBuffer>(replaySlice.Buffer);
        var readback = new Matrix4x4[2_000];
        retained.Read(0, MemoryMarshal.AsBytes(readback.AsSpan()));
        AssertMatrixBitsEqual(transforms[0], readback[0]);
        AssertMatrixBitsEqual(transforms[1_993], readback[1_993]);
        AssertMatrixBitsEqual(transforms[1_999], readback[1_999]);
    }

    [Fact]
    public void TopologyChange_CreateSwapsCurrentSlotAndDisposalReleasesEverySlot()
    {
        using var device = new RecordingGpuDevice();
        var buffers = new DirectionalShadowTransformBufferSet(device);
        Matrix4x4[] firstPose = [Matrix4x4.Identity];
        RecordingGpuBuffer firstSlot;
        RecordingGpuBuffer secondSlot;
        using (IGpuFrame first = device.BeginFrame())
        {
            firstSlot = Assert.IsType<RecordingGpuBuffer>(buffers.Publish(
                first, 1, firstPose, []).Buffer);
        }
        using (IGpuFrame second = device.BeginFrame())
        {
            secondSlot = Assert.IsType<RecordingGpuBuffer>(buffers.Publish(
                second, 1, firstPose, []).Buffer);
        }

        Matrix4x4[] rebuiltPose =
        [
            Matrix4x4.CreateTranslation(9f, 8f, 7f),
            Matrix4x4.CreateScale(2f),
        ];
        RecordingGpuBuffer replacement;
        using (IGpuFrame third = device.BeginFrame())
        {
            replacement = Assert.IsType<RecordingGpuBuffer>(buffers.Publish(
                third, 2, rebuiltPose, []).Buffer);
        }

        Assert.True(firstSlot.IsDisposed);
        Assert.False(secondSlot.IsDisposed);
        Assert.False(replacement.IsDisposed);
        Assert.True(buffers.LastStats.TopologyUploaded);
        buffers.Dispose();
        Assert.True(secondSlot.IsDisposed);
        Assert.True(replacement.IsDisposed);
    }

    [Fact]
    public void InvalidDynamicSlotsAndUnsupportedBindingFailBeforePublishing()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        using IGpuFrame frame = device.BeginFrame();
        Matrix4x4[] pose = [Matrix4x4.Identity, Matrix4x4.Identity];

        Assert.Throws<InvalidOperationException>(() =>
            buffers.Publish(frame, 1, pose, [1, 1]));
        Assert.Throws<InvalidOperationException>(() =>
            buffers.Publish(frame, 1, pose, [2]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            buffers.Publish(
                frame,
                1,
                pose,
                [],
                [],
                denseRefresh: false,
                bindingSizeBytes: 64u));

        Assert.Equal(0, buffers.BufferCount);
        Assert.Empty(device.OfKind<GpuRecordedHostStorageVisibility>());
    }

    [Fact]
    public void ConnectedDenseDemandPublishesBeyondFormer65536CeilingInOneBinding()
    {
        using var device = new RecordingGpuDevice();
        using var buffers = new DirectionalShadowTransformBufferSet(device);
        var transforms = new Matrix4x4[68_395];
        transforms[^1] = Matrix4x4.CreateTranslation(68_394f, 2f, 3f);
        using IGpuFrame frame = device.BeginFrame();

        WorldTransformFrameSlice slice = buffers.Publish(
            frame,
            topologyBuildSequence: 68_395,
            transforms,
            ReadOnlySpan<int>.Empty);

        Assert.Equal(68_395u, slice.InstanceCount);
        Assert.True(slice.IsValidFor(frame));
        Assert.Equal(WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            slice.BindingSizeBytes);
        Assert.Same(Assert.Single(device.CreatedBuffers), slice.Buffer);
    }

    private static void AssertMatrixBitsEqual(
        Matrix4x4 expected,
        Matrix4x4 actual)
    {
        ReadOnlySpan<byte> expectedBits = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref expected, 1));
        ReadOnlySpan<byte> actualBits = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref actual, 1));
        Assert.True(expectedBits.SequenceEqual(actualBits));
    }
}
