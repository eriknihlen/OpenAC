using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Rendering;

internal sealed class BackendNeutralWorldTextures : IDisposable
{
    private readonly IWorldTextureArray _rgba;
    private readonly IWorldTextureArray _compressed;
    private readonly ICompositeTextureArrayBackend _compositeBackend;
    private readonly CompositeTextureArrayResource _composite;
    private bool _disposed;

    private const int ArrayExtent = 64;

    private const int CompositeExtent = 32;

    private const int CompositeLayers = 8;

    internal static BackendNeutralWorldTextures Create(IGpuDevice device, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);
        return new BackendNeutralWorldTextures(device, log);
    }

    internal static void Exercise(IGpuDevice device, Action<string> log)
    {
        using BackendNeutralWorldTextures textures = Create(device, log);
    }

    private BackendNeutralWorldTextures(IGpuDevice device, Action<string> log)
    {
        var arrays = new RhiWorldTextureArrayFactory(device);

        int rgbaLayers = TextureAtlasManager.CalculateInitialCapacity(
            ArrayExtent,
            ArrayExtent,
            TextureFormat.RGBA8);
        int compressedLayers = TextureAtlasManager.CalculateInitialCapacity(
            ArrayExtent,
            ArrayExtent,
            TextureFormat.DXT1);

        IWorldTextureArray? rgba = null;
        IWorldTextureArray? compressed = null;
        ICompositeTextureArrayBackend? compositeBackend = null;
        CompositeTextureArrayResource? composite = null;
        try
        {
            rgba = arrays.CreateClampedArray(TextureFormat.RGBA8, ArrayExtent, ArrayExtent, rgbaLayers);
            rgba.UpdateLayer(0, OpaqueRgba(ArrayExtent, ArrayExtent), null, null);
            long rgbaMipBytes = rgba.ProcessDirtyUpdates();

            compressed = arrays.CreateClampedArray(TextureFormat.DXT1, ArrayExtent, ArrayExtent, compressedLayers);
            compressed.UpdateLayer(0, OpaqueBc1(ArrayExtent, ArrayExtent), null, null);
            long compressedMipBytes = compressed.ProcessDirtyUpdates();

            compositeBackend = new RhiCompositeTextureArrayBackend(device);
            composite = compositeBackend.Create(CompositeExtent, CompositeExtent, CompositeLayers);
            compositeBackend.Upload(composite, 0, OpaqueRgba(CompositeExtent, CompositeExtent));

            _rgba = rgba;
            _compressed = compressed;
            _compositeBackend = compositeBackend;
            _composite = composite;

            log(
                "[V6i-2] world texture creation on the RHI: "
                + $"RGBA8 {ArrayExtent}x{ArrayExtent}x{rgbaLayers} "
                + $"(wrap {rgba.ResolveSlot(true)}, clamp {rgba.ResolveSlot(false)}, "
                + $"{rgbaMipBytes} mip bytes blitted); "
                + $"BC1 {ArrayExtent}x{ArrayExtent}x{compressedLayers} "
                + $"(wrap {compressed.ResolveSlot(true)}, clamp {compressed.ResolveSlot(false)}, "
                + $"{compressedMipBytes} mip bytes encoded); "
                + $"composite {CompositeExtent}x{CompositeExtent}x{CompositeLayers} ({composite.Slot}).");
        }
        catch
        {
            if (composite is not null)
            {
                compositeBackend!.MakeNonResident(composite);
                compositeBackend.Delete(composite);
            }
            compressed?.Dispose();
            rgba?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _compositeBackend.MakeNonResident(_composite);
        _compositeBackend.Delete(_composite);
        _compressed.Dispose();
        _rgba.Dispose();
    }

    private static byte[] OpaqueRgba(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0xFF);
        return pixels;
    }

    private static byte[] OpaqueBc1(int width, int height)
    {
        int blocks = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4);
        var data = new byte[blocks * 8];
        for (int block = 0; block < blocks; block++)
        {
            int at = block * 8;
            // Two RGB565 endpoints, both white (0xFFFF), then four selector
            // bytes of zero: every texel takes endpoint 0.
            data[at + 0] = 0xFF;
            data[at + 1] = 0xFF;
            data[at + 2] = 0xFF;
            data[at + 3] = 0xFF;
        }

        return data;
    }
}
