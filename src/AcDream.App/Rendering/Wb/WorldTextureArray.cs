using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.Content;
using AcDream.Core.Rendering.Wb;
using Chorizite.Core.Render.Enums;
using Microsoft.Extensions.Logging;

namespace AcDream.App.Rendering.Wb;

internal interface IWorldTextureArray : IDisposable
{
    /// <summary>Array layers allocated. Immutable for the array's lifetime.</summary>
    int Size { get; }

    long TotalSizeInBytes { get; }

    int PendingUpdateCount { get; }

    bool HasDurableDisposeOwnership { get; }

    bool IsPhysicalRetirementComplete { get; }

    void UpdateLayer(int layer, byte[] data, UploadPixelFormat? uploadPixelFormat, UploadPixelType? uploadPixelType);

    long ProcessDirtyUpdates();

    GpuTextureSlot ResolveSlot(bool wrapping);

    void ReleaseTextureSlots();
}

internal interface IWorldTextureArrayFactory
{
    internal static IWorldTextureArrayFactory For(
        IMeshPipelineDevice graphicsDevice,
        IGpuDevice gpuDevice,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ArgumentNullException.ThrowIfNull(gpuDevice);
        ArgumentNullException.ThrowIfNull(logger);
        return new RhiWorldTextureArrayFactory(gpuDevice);
    }

    /// <summary>The retirement queue array layers and images are released through.</summary>
    IGpuResourceRetirementQueue Retirement { get; }

    IWorldTextureArray CreateClampedArray(TextureFormat format, int width, int height, int layers);
}

internal sealed class RhiWorldTextureArrayFactory(IGpuDevice device) : IWorldTextureArrayFactory
{
    private readonly IGpuDevice _device = device ?? throw new ArgumentNullException(nameof(device));

    public IGpuResourceRetirementQueue Retirement => _device.Retirement;

    public IWorldTextureArray CreateClampedArray(TextureFormat format, int width, int height, int layers) =>
        new RhiWorldTextureArray(_device, format, width, height, layers);
}

internal sealed class RhiWorldTextureArray : IWorldTextureArray
{
    private const float WorldArrayAnisotropy = 16f;

    private readonly IGpuDevice _device;
    private readonly IGpuTexture _texture;
    private readonly GpuTextureFormat _format;
    private readonly int _width;
    private readonly int _height;
    private readonly int _mipLevelCount;
    private readonly List<PendingLayer> _pending = [];
    private readonly Lock _gate = new();

    private GpuTextureSlot _wrapSlot = GpuTextureSlot.Unassigned;
    private GpuTextureSlot _clampSlot = GpuTextureSlot.Unassigned;
    private bool _disposed;

    private readonly record struct PendingLayer(int Layer, byte[] Data);

    internal RhiWorldTextureArray(
        IGpuDevice device,
        TextureFormat format,
        int width,
        int height,
        int layers)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(layers, 1);

        _device = device;
        SourceFormat = format;
        _format = MapFormat(format);
        _width = width;
        _height = height;
        Size = layers;
        _mipLevelCount = MipLevelsFor(width, height);
        TotalSizeInBytes = checked(
            TextureAtlasManager.CalculateMipChainBytes(width, height, format) * layers);

        IGpuTexture? texture = null;
        try
        {
            texture = device.CreateTexture(new GpuTextureDescription(
                $"world-atlas-{format}-{width}x{height}x{layers}",
                GpuTextureKind.Texture2DArray,
                _format,
                width,
                height,
                layers,
                _mipLevelCount));
            _texture = texture;

            _clampSlot = device.RegisterTexture(
                texture,
                device.CreateSampler(GpuSamplerDescription.WorldClamp with
                {
                    MaxAnisotropy = WorldArrayAnisotropy,
                }));
            _wrapSlot = device.RegisterTexture(
                texture,
                device.CreateSampler(GpuSamplerDescription.WorldRepeat with
                {
                    MaxAnisotropy = WorldArrayAnisotropy,
                }));
        }
        catch
        {
            ReleaseSlotsQuietly();
            texture?.Dispose();
            throw;
        }
    }

    public int Size { get; }

    public long TotalSizeInBytes { get; }

    public int PendingUpdateCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    public bool HasDurableDisposeOwnership => _disposed;

    /// <inheritdoc/>
    public bool IsPhysicalRetirementComplete => _disposed;

    public void UpdateLayer(int layer, byte[] data, UploadPixelFormat? uploadPixelFormat, UploadPixelType? uploadPixelType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, Size);
        ValidateUploadPayload(
            SourceFormat,
            _width,
            _height,
            data.Length,
            uploadPixelFormat,
            uploadPixelType);

        lock (_gate)
        {
            int existing = _pending.FindLastIndex(p => p.Layer == layer);
            var update = new PendingLayer(layer, data);
            if (existing >= 0)
                _pending[existing] = update;
            else
                _pending.Add(update);
        }
    }

    public long ProcessDirtyUpdates()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PendingLayer[] flush;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return 0;
            flush = [.. _pending];
        }

        long generated = 0;
        bool compressed = BlockCompressionCodec.IsBlockCompressed(_format);
        foreach (PendingLayer layer in flush)
        {
            _texture.Upload(0, layer.Layer, layer.Data);
            if (_mipLevelCount <= 1)
                continue;
            if (!compressed)
                continue;

            foreach (BlockCompressionMipChain.Level level in
                BlockCompressionMipChain.BuildCompressed(
                    _format,
                    layer.Data,
                    _width,
                    _height,
                    _mipLevelCount))
            {
                _texture.Upload(level.MipLevel, layer.Layer, level.Data);
                generated = checked(generated + level.Data.Length);
            }
        }

        if (!compressed && _mipLevelCount > 1)
        {
            _texture.GenerateMipChain();
            generated = checked(generated + MipChainBytes());
        }

        lock (_gate)
        {
            foreach (PendingLayer layer in flush)
            {
                int index = _pending.FindIndex(p => p.Layer == layer.Layer && ReferenceEquals(p.Data, layer.Data));
                if (index >= 0)
                    _pending.RemoveAt(index);
            }
        }

        return generated;
    }

    public GpuTextureSlot ResolveSlot(bool wrapping) => wrapping ? _wrapSlot : _clampSlot;

    public void ReleaseTextureSlots() => ReleaseSlotsQuietly();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseSlotsQuietly();
        _texture.Dispose();
        lock (_gate)
            _pending.Clear();
    }

    internal TextureFormat SourceFormat { get; }

    private void ReleaseSlotsQuietly()
    {
        if (_wrapSlot.IsAssigned)
        {
            _device.ReleaseTextureSlot(_wrapSlot);
            _wrapSlot = GpuTextureSlot.Unassigned;
        }
        if (_clampSlot.IsAssigned)
        {
            _device.ReleaseTextureSlot(_clampSlot);
            _clampSlot = GpuTextureSlot.Unassigned;
        }
    }

    private long MipChainBytes() =>
        checked(TotalSizeInBytes
            - (TextureAtlasManager.CalculateLevelBytes(_width, _height, SourceFormat) * Size));

    internal static int MipLevelsFor(int width, int height) =>
        (int)Math.Floor(Math.Log2(Math.Max(1, Math.Max(width, height)))) + 1;

    private static GpuTextureFormat MapFormat(TextureFormat format) =>
        format switch
        {
            TextureFormat.RGBA8 => GpuTextureFormat.Rgba8Unorm,
            TextureFormat.DXT1 => GpuTextureFormat.Bc1Unorm,
            TextureFormat.DXT3 => GpuTextureFormat.Bc2Unorm,
            TextureFormat.DXT5 => GpuTextureFormat.Bc3Unorm,
            _ => throw new NotSupportedException(
                $"World texture format {format} has no GpuTextureFormat member. "
                + "RGB8 and Rgba32f are not in the pinned RHI format list, and A8 needs the "
                + "component swizzle the GL array applies, which lives in a Vulkan image view "
                + "and is not part of GpuTextureDescription. Campaign V's world-draw slice owns "
                + "extending the contract or proving no such atlas exists."),
        };

    private static bool IsCompressedFormat(TextureFormat format) =>
        format is TextureFormat.DXT1 or TextureFormat.DXT3 or TextureFormat.DXT5;

    internal static int CalculateExpectedDataSize(TextureFormat format, int width, int height)
    {
        if (IsCompressedFormat(format))
            return TextureHelpers.GetCompressedLayerSize(width, height, format);

        return format switch
        {
            TextureFormat.RGBA8 => checked(width * height * 4),
            TextureFormat.RGB8 => checked(width * height * 3),
            TextureFormat.A8 => checked(width * height),
            TextureFormat.Rgba32f => checked(width * height * 16),
            _ => throw new NotSupportedException($"Unsupported format {format}"),
        };
    }

    internal static void ValidateUploadPayload(
        TextureFormat format,
        int width,
        int height,
        int dataLength,
        UploadPixelFormat? uploadPixelFormat,
        UploadPixelType? uploadPixelType)
    {
        int expectedBytes = CalculateExpectedDataSize(format, width, height);
        if (dataLength != expectedBytes)
        {
            throw new ArgumentException(
                $"Texture-array layer payload has {dataLength} bytes; expected exactly {expectedBytes} "
                + $"for {format} {width}x{height}.",
                nameof(dataLength));
        }

        if (IsCompressedFormat(format))
        {
            if (uploadPixelFormat.HasValue || uploadPixelType.HasValue)
                throw new ArgumentException("Compressed texture uploads cannot specify pixel format/type overrides.");
            return;
        }

        UploadPixelFormat expectedFormat = format.ToPixelFormat();
        UploadPixelType expectedType = format.ToPixelType();
        if ((uploadPixelFormat ?? expectedFormat) != expectedFormat
            || (uploadPixelType ?? expectedType) != expectedType)
        {
            throw new ArgumentException(
                $"Upload descriptor {uploadPixelFormat}/{uploadPixelType} does not match "
                + $"the {expectedFormat}/{expectedType} transfer required by {format}.");
        }
    }
}
