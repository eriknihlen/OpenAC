using System.Linq;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class TextureCacheLinearTwinTests
{
    private static (RecordingGpuDevice device, TextureCache cache) Build()
    {
        var device = new RecordingGpuDevice();
        var cache = new TextureCache(device, dats: null!);
        device.Clear();
        return (device, cache);
    }

    [Fact]
    public void UnknownHandle_ReturnsUnchanged()
    {
        (_, TextureCache cache) = Build();

        Assert.Equal(0u, cache.GetOrCreateLinearUiTwin(0u));
        Assert.Equal(12345u, cache.GetOrCreateLinearUiTwin(12345u));
    }

    [Fact]
    public void NearestHandle_GetsADifferentTwinHandle_SampledLinear()
    {
        (RecordingGpuDevice device, TextureCache cache) = Build();
        byte[] rgba = new byte[4 * 4 * 4];
        uint nearestHandle = cache.UploadRgba8(rgba, 4, 4, nearest: true);

        uint twinHandle = cache.GetOrCreateLinearUiTwin(nearestHandle);

        Assert.NotEqual(0u, twinHandle);
        Assert.NotEqual(nearestHandle, twinHandle);

        var registrations = device.OfKind<GpuRecordedTextureRegistration>().ToList();
        Assert.Equal(2, registrations.Count);
        Assert.Equal(GpuFilter.Nearest, registrations[0].Sampler.MinFilter);
        Assert.Equal(GpuFilter.Linear, registrations[1].Sampler.MinFilter);
        Assert.Equal(GpuFilter.Linear, registrations[1].Sampler.MagFilter);

        // Both registrations name the SAME underlying texture — the twin reuses
        // the original decoded pixels rather than re-uploading.
        Assert.Equal(registrations[0].TextureName, registrations[1].TextureName);
    }

    [Fact]
    public void NearestHandle_RepeatedRequest_ReturnsTheSameCachedTwin()
    {
        (RecordingGpuDevice device, TextureCache cache) = Build();
        byte[] rgba = new byte[4 * 4 * 4];
        uint nearestHandle = cache.UploadRgba8(rgba, 4, 4, nearest: true);

        uint first = cache.GetOrCreateLinearUiTwin(nearestHandle);
        uint second = cache.GetOrCreateLinearUiTwin(nearestHandle);

        Assert.Equal(first, second);
        Assert.Equal(2, device.OfKind<GpuRecordedTextureRegistration>().Count());
    }

    [Fact]
    public void NonNearestUpload_HasNoTwin()
    {
        (_, TextureCache cache) = Build();
        byte[] rgba = new byte[4 * 4 * 4];
        uint linearHandle = cache.UploadRgba8(rgba, 4, 4, nearest: false);

        Assert.Equal(linearHandle, cache.GetOrCreateLinearUiTwin(linearHandle));
    }

    [Fact]
    public void Dispose_ReleasesTheTwinSlot()
    {
        (RecordingGpuDevice device, TextureCache cache) = Build();
        byte[] rgba = new byte[4 * 4 * 4];
        uint nearestHandle = cache.UploadRgba8(rgba, 4, 4, nearest: true);
        uint twinHandle = cache.GetOrCreateLinearUiTwin(nearestHandle);
        GpuTextureSlot twinSlot = UiTextureTableHandle.ToSlot(twinHandle);

        cache.Dispose();

        Assert.Contains(
            device.OfKind<GpuRecordedTextureRelease>(),
            release => release.Slot == twinSlot.Index);
    }
}
