using System.Collections.Generic;
using System.Linq;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.Textures;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The interface's ad-hoc uploads are kept for the life of the cache. The
/// releasable path is the one exception: an upload that is given back one
/// handle at a time. The handle is forgotten at once; the GPU work waits
/// for the frames in flight inside the device, which is the one owner of
/// that wait, so the cache asks the device at once and defers nothing of
/// its own.
/// </summary>
public sealed class TextureCacheReleasableUiTextureTests
{
    private static (RecordingGpuDevice Device, TextureCache Cache, HeldGpuRetirementQueue Queue) Build()
    {
        var queue = new HeldGpuRetirementQueue();
        var device = new RecordingGpuDevice(retirement: queue);
        var cache = new TextureCache(
            device,
            dats: null!,
            device.Retirement,
            Path.Combine(Path.GetTempPath(), "acdream-tests", "releasable"));
        device.Clear();
        return (device, cache, queue);
    }

    [Fact]
    public void ReleaseForgetsTheHandleNowAndFreesTheTextureOnlyWhenTheQueueSays()
    {
        (RecordingGpuDevice device, TextureCache cache, HeldGpuRetirementQueue queue) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");
        Assert.NotEqual(0u, handle);
        Assert.Equal(1, cache.ReleasableUiTextureCount);
        int slotsBefore = device.LiveTextureSlotCount;

        Assert.True(cache.ReleaseUiTexture(handle));

        Assert.Equal(0, cache.ReleasableUiTextureCount);
        Assert.Single(queue.Pending);
        Assert.Equal(slotsBefore, device.LiveTextureSlotCount);
        Assert.Empty(device.OfKind<GpuRecordedTextureRelease>());

        queue.RunAll();

        Assert.Equal(slotsBefore - 1, device.LiveTextureSlotCount);
        GpuRecordedTextureRelease released = Assert.Single(device.OfKind<GpuRecordedTextureRelease>());
        Assert.Equal(UiTextureTableHandle.ToSlot(handle).Index, released.Slot);
    }

    [Fact]
    public void AMipmappedUploadSendsEveryLevelAndSamplesThemTrilinearly()
    {
        (RecordingGpuDevice device, TextureCache cache, HeldGpuRetirementQueue queue) = Build();

        uint handle = cache.UploadReleasableRgba8(RgbaMipChain.Build(new byte[8 * 4 * 4], 8, 4), "test-image");

        RecordingGpuTexture texture = Assert.Single(device.CreatedTextures);
        Assert.Equal(4, texture.MipLevelCount);
        Assert.Equal([(0, 0, 8 * 4 * 4), (1, 0, 4 * 2 * 4), (2, 0, 2 * 1 * 4), (3, 0, 1 * 1 * 4)], texture.Uploads);
        Assert.False(texture.MipChainGenerated);
        GpuRecordedTextureRegistration registration = Assert.Single(device.OfKind<GpuRecordedTextureRegistration>());
        Assert.Equal(GpuSamplerDescription.WorldClamp, registration.Sampler);

        Assert.True(cache.ReleaseUiTexture(handle));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void APlainUploadHasOneLevel()
    {
        (RecordingGpuDevice device, TextureCache cache, _) = Build();

        cache.UploadReleasableRgba8(new byte[8 * 4 * 4], 8, 4, "test-image");

        Assert.Equal(1, Assert.Single(device.CreatedTextures).MipLevelCount);
    }

    [Fact]
    public void AMipmappedUploadCountsEveryLevelInTheMemoryTrackerAndGivesThemAllBack()
    {
        (_, TextureCache cache, _) = Build();
        long before = cache.TrackedTextureBytes;

        uint handle = cache.UploadReleasableRgba8(RgbaMipChain.Build(new byte[8 * 4 * 4], 8, 4), "test-image");

        // 8x4, 4x2, 2x1, 1x1.
        Assert.Equal((8L * 4 + 4 * 2 + 2 * 1 + 1 * 1) * 4, cache.TrackedTextureBytes - before);

        Assert.True(cache.ReleaseUiTexture(handle));
        Assert.Equal(before, cache.TrackedTextureBytes);
    }

    [Fact]
    public void ASecondReleaseOfTheSameHandleIsRefused()
    {
        (_, TextureCache cache, HeldGpuRetirementQueue queue) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");

        Assert.True(cache.ReleaseUiTexture(handle));
        Assert.False(cache.ReleaseUiTexture(handle));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void AHandleFromTheAdHocPathCannotBeReleasedThroughIt()
    {
        (_, TextureCache cache, HeldGpuRetirementQueue queue) = Build();
        uint adhoc = cache.UploadRgba8(new byte[4 * 4 * 4], 4, 4);

        Assert.False(cache.ReleaseUiTexture(adhoc));
        Assert.False(cache.ReleaseUiTexture(0u));
        Assert.Empty(queue.Pending);
    }

    [Fact]
    public void DisposeFreesWhatWasNeverReleased()
    {
        (RecordingGpuDevice device, TextureCache cache, _) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");

        // The client's order: the cache goes, then the device drains what
        // the cache gave back to it.
        cache.Dispose();
        device.Dispose();

        Assert.Contains(
            device.OfKind<GpuRecordedTextureRelease>(),
            call => call.Slot == UiTextureTableHandle.ToSlot(handle).Index);
    }
}
