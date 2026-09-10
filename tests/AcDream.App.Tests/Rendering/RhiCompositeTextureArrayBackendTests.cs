using System;
using System.Linq;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

public sealed class RhiCompositeTextureArrayBackendTests
{
    private static byte[] Rgba(int width, int height) => new byte[width * height * 4];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewAndCachedAppearanceTexturesPreserveBothAddressModes(bool palette)
    {
        using var device = new RecordingGpuDevice();
        var backend = new RhiCompositeTextureArrayBackend(device);
        using var cache = new CompositeTextureArrayCache(backend, ImmediateGpuResourceRetirementQueue.Instance);
        var key = new CompositeTextureKey(
            palette ? CompositeTextureKind.PaletteComposite : CompositeTextureKind.OriginalTextureOverride,
            1, 2, default);
        cache.BeginFrame();
        Assert.True(cache.TryAddAndAcquire(1, key,
            new AcDream.Core.Textures.DecodedTexture(Rgba(4, 4), 4, 4), out var added));
        Assert.True(cache.TryAcquire(2, key, out var cached));
        Assert.Equal(added, cached);
        Assert.True(cached.ResolveSlot(true).IsAssigned);
        Assert.NotEqual(cached.ResolveSlot(false), cached.ResolveSlot(true));
        var registration = Assert.Single(device.Calls.OfType<GpuRecordedTextureRegistration>(),
            r => r.Slot == cached.ResolveSlot(true).Index);
        Assert.Equal(GpuSamplerDescription.WorldRepeat with { MipFilter = GpuMipFilter.None }, registration.Sampler);
        Assert.Single(device.Calls.OfType<GpuRecordedTextureRegistration>(),
            r => r.Slot == cached.ResolveSlot(false).Index);
    }

    [Fact]
    public void CreateProducesASingleLevelArrayRegisteredIntoTheTable()
    {
        using var device = new RecordingGpuDevice();
        var backend = new RhiCompositeTextureArrayBackend(device);

        CompositeTextureArrayResource resource = backend.Create(32, 32, 8);

        Assert.True(resource.Slot.IsAssigned);
        Assert.True(resource.RepeatSlot.IsAssigned);
        Assert.NotEqual(resource.Slot, resource.RepeatSlot);
        var registrations = device.Calls.OfType<GpuRecordedTextureRegistration>()
            .Where(r => r.Slot == resource.Slot.Index || r.Slot == resource.RepeatSlot.Index).ToArray();
        Assert.Equal(2, registrations.Length);
        Assert.Equal(registrations[0].TextureName, registrations[1].TextureName);
        Assert.Equal(GpuSamplerDescription.WorldClamp with { MipFilter = GpuMipFilter.None }, registrations[0].Sampler);
        Assert.Equal(GpuSamplerDescription.WorldRepeat with { MipFilter = GpuMipFilter.None }, registrations[1].Sampler);
        Assert.Equal(32 * 32 * 4 * 8, resource.Bytes);
        // The GL identity fields are meaningless on this arm and say so.
        Assert.Equal(0u, resource.Name);
        Assert.Equal(0ul, resource.Handle);

        RecordingGpuTexture image = Assert.IsType<RecordingGpuTexture>(resource.Image);
        Assert.Equal(GpuTextureKind.Texture2DArray, image.Kind);
        Assert.Equal(GpuTextureFormat.Rgba8Unorm, image.Format);
        Assert.Equal(8, image.LayerCount);
        Assert.Equal(1, image.MipLevelCount);
    }

    [Fact]
    public void UploadWritesLevelZeroOfTheNamedLayer()
    {
        using var device = new RecordingGpuDevice();
        var backend = new RhiCompositeTextureArrayBackend(device);
        CompositeTextureArrayResource resource = backend.Create(32, 32, 4);

        backend.Upload(resource, 3, Rgba(32, 32));

        RecordingGpuTexture image = Assert.IsType<RecordingGpuTexture>(resource.Image);
        Assert.Equal([(0, 3, 32 * 32 * 4)], image.Uploads);
    }

    [Fact]
    public void ReleaseRetiresTheTableEntryBeforeTheImage()
    {
        using var device = new RecordingGpuDevice();
        var backend = new RhiCompositeTextureArrayBackend(device);
        int before = device.LiveTextureSlotCount;
        CompositeTextureArrayResource resource = backend.Create(32, 32, 4);
        Assert.Equal(before + 2, device.LiveTextureSlotCount);

        backend.MakeNonResident(resource);
        Assert.Equal(before, device.LiveTextureSlotCount);
        Assert.False(Assert.IsType<RecordingGpuTexture>(resource.Image).IsDisposed);

        backend.Delete(resource);
        Assert.True(Assert.IsType<RecordingGpuTexture>(resource.Image).IsDisposed);
    }

    [Fact]
    public void AGlResourceIsRefusedRatherThanDereferenced()
    {
        using var device = new RecordingGpuDevice();
        var backend = new RhiCompositeTextureArrayBackend(device);
        var foreign = new CompositeTextureArrayResource
        {
            Name = 7,
            Handle = 0xDEAD,
            Slot = new GpuTextureSlot(3),
            Width = 32,
            Height = 32,
            Capacity = 1,
            Bytes = 32 * 32 * 4,
        };

        Assert.Throws<InvalidOperationException>(() => backend.Upload(foreign, 0, Rgba(32, 32)));
        Assert.Throws<InvalidOperationException>(() => backend.Delete(foreign));
    }

    [Fact]
    public void TheReportedLayerCeilingExceedsTheCachesOwnCap()
    {
        using var device = new RecordingGpuDevice();
        var backend = new RhiCompositeTextureArrayBackend(device);

        Assert.True(backend.MaximumArrayLayers >= CompositeTextureArrayCache.MaximumLayersPerArray);
    }
}
