using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanGpuRenderTarget : IGpuRenderTarget
{
    private readonly VulkanGpuTexture _color;
    private readonly VulkanGpuTexture? _multisampleColor;
    private readonly VulkanGpuTexture? _depth;
    private readonly VulkanGpuTexture? _multisampleDepth;
    private bool _disposed;

    internal VulkanGpuRenderTarget(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanDeviceMemoryAllocator allocator,
        VulkanUploadQueue uploads,
        IGpuResourceRetirementQueue retirement,
        VulkanDebugNames debugNames,
        in GpuRenderTargetDescription description,
        Format deviceDepthStencilFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.SampleCount);
        if (description.SampleableDepth && description.DepthFormat is null)
        {
            throw new ArgumentException(
                "SampleableDepth requires a depth format.",
                nameof(description));
        }
        Description = description;

        _color = new VulkanGpuTexture(
            vk,
            device,
            allocator,
            uploads,
            retirement,
            debugNames,
            new GpuTextureDescription(
                $"{description.Name}-color",
                GpuTextureKind.Texture2D,
                description.ColorFormat,
                description.Width,
                description.Height,
                LayerCount: 1,
                MipLevelCount: 1),
            sampleCount: 1,
            renderTarget: true,
            sampleable: true);

        if (description.SampleCount > 1)
        {
            _multisampleColor = new VulkanGpuTexture(
                vk,
                device,
                allocator,
                uploads,
                retirement,
                debugNames,
                new GpuTextureDescription(
                    $"{description.Name}-color-msaa",
                    GpuTextureKind.Texture2D,
                    description.ColorFormat,
                    description.Width,
                    description.Height,
                    LayerCount: 1,
                    MipLevelCount: 1),
                description.SampleCount,
                renderTarget: true,
                sampleable: false);
        }

        if (description.DepthFormat is { } depthFormat)
        {
            int retainedDepthSamples =
                description.SampleableDepth ? 1 : description.SampleCount;
            _depth = new VulkanGpuTexture(
                vk,
                device,
                allocator,
                uploads,
                retirement,
                debugNames,
                new GpuTextureDescription(
                    $"{description.Name}-depth",
                    GpuTextureKind.Texture2D,
                    depthFormat,
                    description.Width,
                    description.Height,
                    LayerCount: 1,
                    MipLevelCount: 1),
                retainedDepthSamples,
                renderTarget: true,
                sampleable: description.SampleableDepth,
                formatOverride: deviceDepthStencilFormat);

            if (description.SampleableDepth && description.SampleCount > 1)
            {
                _multisampleDepth = new VulkanGpuTexture(
                    vk,
                    device,
                    allocator,
                    uploads,
                    retirement,
                    debugNames,
                    new GpuTextureDescription(
                        $"{description.Name}-depth-msaa",
                        GpuTextureKind.Texture2D,
                        depthFormat,
                        description.Width,
                        description.Height,
                        LayerCount: 1,
                        MipLevelCount: 1),
                    description.SampleCount,
                    renderTarget: true,
                    sampleable: false,
                    formatOverride: deviceDepthStencilFormat);
            }
        }
    }

    public GpuRenderTargetDescription Description { get; }

    public IGpuTexture ColorTexture => _color;

    public IGpuTexture? DepthTexture => Description.SampleableDepth ? _depth : null;

    internal VulkanGpuTexture ColorAttachment => _multisampleColor ?? _color;

    /// <summary>The single-sampled resolve destination, or null at one sample.</summary>
    internal VulkanGpuTexture? ColorResolve => _multisampleColor is null ? null : _color;

    /// <summary>The image written as the pass's depth/stencil attachment.</summary>
    internal VulkanGpuTexture? DepthAttachment => _multisampleDepth ?? _depth;

    /// <summary>The sampleable depth resolve destination, or null when no resolve is required.</summary>
    internal VulkanGpuTexture? DepthResolve =>
        Description.SampleableDepth && _multisampleDepth is not null ? _depth : null;

    internal VulkanGpuTexture ColorResult => _color;

    internal VulkanGpuTexture? DepthResult => _depth;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _multisampleDepth?.Dispose();
        _depth?.Dispose();
        _multisampleColor?.Dispose();
        _color.Dispose();
    }
}

internal sealed unsafe class VulkanBackbufferAttachments : IDisposable
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly VulkanDeviceMemoryAllocator _allocator;
    private readonly VulkanDebugNames _debugNames;

    private Image _colorImage;
    private ImageView _colorView;
    private VulkanAllocation _colorAllocation;
    private Image _depthImage;
    private ImageView _depthView;
    private VulkanAllocation _depthAllocation;
    private bool _disposed;

    internal VulkanBackbufferAttachments(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanDeviceMemoryAllocator allocator,
        VulkanDebugNames debugNames,
        Format depthStencilFormat)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _debugNames = debugNames ?? throw new ArgumentNullException(nameof(debugNames));
        DepthStencilFormat = depthStencilFormat;
    }

    internal Format DepthStencilFormat { get; }

    internal uint Width { get; private set; }

    internal uint Height { get; private set; }

    internal int SampleCount { get; private set; } = 1;

    internal Format ColorFormat { get; private set; }

    internal ImageView ColorView => _colorView;

    internal ImageView DepthView => _depthView;

    internal Image ColorImage => _colorImage;

    internal Image DepthImage => _depthImage;

    internal bool HasMultisampledColor => _colorView.Handle != 0;

    internal bool HasDepth => _depthView.Handle != 0;

    internal bool ColorLayoutInitialized { get; private set; }

    internal bool DepthLayoutInitialized { get; private set; }

    internal void MarkColorLayoutInitialized() => ColorLayoutInitialized = true;

    internal void MarkDepthLayoutInitialized() => DepthLayoutInitialized = true;

    internal void Configure(uint width, uint height, Format colorFormat, int sampleCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == 0 || height == 0)
            return;
        if (width == Width && height == Height && colorFormat == ColorFormat && sampleCount == SampleCount)
            return;

        DestroyImages();
        Width = width;
        Height = height;
        ColorFormat = colorFormat;
        SampleCount = Math.Max(1, sampleCount);

        if (SampleCount > 1)
        {
            (_colorImage, _colorAllocation, _colorView) = CreateAttachment(
                colorFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
                ImageAspectFlags.ColorBit,
                "vk-backbuffer-msaa-color");
        }

        if (DepthStencilFormat != Format.Undefined)
        {
            (_depthImage, _depthAllocation, _depthView) = CreateAttachment(
                DepthStencilFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                "vk-backbuffer-depth");
        }
    }

    private (Image, VulkanAllocation, ImageView) CreateAttachment(
        Format format,
        ImageUsageFlags usage,
        ImageAspectFlags aspect,
        string name)
    {
        var create = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(Width, Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = VulkanTextureFormatMapping.SampleCountOf(SampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VulkanInterop.Check(_vk.CreateImage(_device, &create, null, out Image image), $"vkCreateImage ({name})");

        _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements requirements);
        VulkanAllocation allocation = _allocator.Allocate(
            requirements,
            GpuMemoryResidency.DeviceLocal,
            name);
        VulkanInterop.Check(
            _vk.BindImageMemory(_device, image, allocation.Memory, allocation.OffsetBytes),
            $"vkBindImageMemory ({name})");

        var viewCreate = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        VulkanInterop.Check(
            _vk.CreateImageView(_device, &viewCreate, null, out ImageView view),
            $"vkCreateImageView ({name})");

        _debugNames.NameImage(image, name);
        _debugNames.NameImageView(view, $"{name}-view");
        return (image, allocation, view);
    }

    private void DestroyImages()
    {
        if (_colorView.Handle != 0)
            _vk.DestroyImageView(_device, _colorView, null);
        if (_colorImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _colorImage, null);
            _allocator.Free(_colorAllocation);
        }

        if (_depthView.Handle != 0)
            _vk.DestroyImageView(_device, _depthView, null);
        if (_depthImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _depthImage, null);
            _allocator.Free(_depthAllocation);
        }

        _colorView = default;
        _colorImage = default;
        _colorAllocation = default;
        _depthView = default;
        _depthImage = default;
        _depthAllocation = default;
        // A recreated image is a new image in UNDEFINED layout, whatever the old
        // one had reached.
        ColorLayoutInitialized = false;
        DepthLayoutInitialized = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DestroyImages();
    }
}
