using System;
using System.Linq;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class RhiWorldTextureArrayTests
{
    private const int Extent = 64;

    private static byte[] Rgba(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0xFF);
        return pixels;
    }

    private static byte[] Bc1(int width, int height) =>
        new byte[Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8];

    [Fact]
    public void AnUncompressedArrayLetsTheDeviceBlitItsMipChain()
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);

        using IWorldTextureArray array = arrays.CreateClampedArray(TextureFormat.RGBA8, Extent, Extent, 4);
        array.UpdateLayer(2, Rgba(Extent, Extent), null, null);
        Assert.Equal(1, array.PendingUpdateCount);

        long generated = array.ProcessDirtyUpdates();

        RecordingGpuTexture texture = LastCreatedTexture(device);
        Assert.True(texture.MipChainGenerated);
        // Exactly one upload: level 0 of the layer that was staged. Every other
        // level is the device's blit.
        Assert.Equal([(0, 2, Extent * Extent * 4)], texture.Uploads);
        Assert.True(generated > 0);
        Assert.Equal(0, array.PendingUpdateCount);
    }

    [Fact]
    public void ABlockCompressedArrayUploadsACpuBuiltChainAndNeverBlits()
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);

        using IWorldTextureArray array = arrays.CreateClampedArray(TextureFormat.DXT1, Extent, Extent, 2);
        array.UpdateLayer(0, Bc1(Extent, Extent), null, null);
        array.ProcessDirtyUpdates();

        RecordingGpuTexture texture = LastCreatedTexture(device);
        Assert.False(texture.MipChainGenerated);
        // 64x64 is seven levels; level 0 was staged and 1..6 were encoded, all
        // for the one layer that was written.
        Assert.Equal(7, texture.Uploads.Count);
        Assert.All(texture.Uploads, upload => Assert.Equal(0, upload.Layer));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], texture.Uploads.Select(u => u.MipLevel));
    }

    [Fact]
    public void EveryArrayIsAddressableThroughBothAddressModes()
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);

        using IWorldTextureArray array = arrays.CreateClampedArray(TextureFormat.RGBA8, Extent, Extent, 1);

        GpuTextureSlot wrap = array.ResolveSlot(wrapping: true);
        GpuTextureSlot clamp = array.ResolveSlot(wrapping: false);
        Assert.True(wrap.IsAssigned);
        Assert.True(clamp.IsAssigned);
        Assert.NotEqual(wrap, clamp);

        GpuSamplerDescription[] samplers =
        [
            .. device.Calls.OfType<GpuRecordedTextureRegistration>()
                .Where(registration => registration.TextureName.StartsWith("world-atlas", StringComparison.Ordinal))
                .Select(registration => registration.Sampler),
        ];
        Assert.Contains(GpuSamplerDescription.WorldClamp, samplers.Select(Isotropic));
        Assert.Contains(GpuSamplerDescription.WorldRepeat, samplers.Select(Isotropic));
        Assert.All(samplers, sampler => Assert.True(
            sampler.MaxAnisotropy > 1f,
            $"world atlas sampler {sampler.AddressU} asked for anisotropy "
            + $"{sampler.MaxAnisotropy}; the GL arm always asks for the device maximum."));

        static GpuSamplerDescription Isotropic(GpuSamplerDescription sampler) =>
            sampler with { MaxAnisotropy = 1f };
    }

    [Fact]
    public void DisposalReturnsBothSlotsAndTheImage()
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);
        int before = device.LiveTextureSlotCount;

        IWorldTextureArray array = arrays.CreateClampedArray(TextureFormat.RGBA8, Extent, Extent, 1);
        Assert.Equal(before + 2, device.LiveTextureSlotCount);

        array.Dispose();

        Assert.Equal(before, device.LiveTextureSlotCount);
        Assert.True(LastCreatedTexture(device).IsDisposed);
        Assert.True(array.IsPhysicalRetirementComplete);
        Assert.True(array.HasDurableDisposeOwnership);

        array.ReleaseTextureSlots();
        Assert.Equal(before, device.LiveTextureSlotCount);
    }

    [Fact]
    public void AMisSizedLayerIsRejectedByTheSharedValidator()
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);

        using IWorldTextureArray array = arrays.CreateClampedArray(TextureFormat.RGBA8, Extent, Extent, 1);

        Assert.Throws<ArgumentException>(() => array.UpdateLayer(0, new byte[16], null, null));
        Assert.Equal(0, array.PendingUpdateCount);
    }

    [Theory]
    [InlineData(TextureFormat.A8)]
    [InlineData(TextureFormat.RGB8)]
    [InlineData(TextureFormat.Rgba32f)]
    public void AFormatWithNoRhiEquivalentIsRefusedAtCreation(TextureFormat format)
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);

        Assert.Throws<NotSupportedException>(
            () => arrays.CreateClampedArray(format, Extent, Extent, 1));
    }

    /// <summary>
    /// Both arms meter the same bytes, because the eviction budget that reads
    /// this number is shared policy above the seam.
    /// </summary>
    [Fact]
    public void AllocatedBytesMatchTheSharedMipChainAccounting()
    {
        using var device = new RecordingGpuDevice();
        var arrays = new RhiWorldTextureArrayFactory(device);

        using IWorldTextureArray array = arrays.CreateClampedArray(TextureFormat.RGBA8, Extent, Extent, 3);

        Assert.Equal(
            TextureAtlasManager.CalculateMipChainBytes(Extent, Extent, TextureFormat.RGBA8) * 3,
            array.TotalSizeInBytes);
    }

    private static RecordingGpuTexture LastCreatedTexture(RecordingGpuDevice device) =>
        device.CreatedTextures.Count > 0
            ? device.CreatedTextures[^1]
            : throw new InvalidOperationException("The device created no texture.");
}
