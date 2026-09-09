using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGpuTexture : IGpuTexture
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly VulkanDeviceMemoryAllocator _allocator;
    private readonly VulkanUploadQueue _uploads;
    private readonly IGpuResourceRetirementQueue _retirement;
    private readonly VulkanAllocation _allocation;
    private bool _disposed;

    internal VulkanGpuTexture(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanDeviceMemoryAllocator allocator,
        VulkanUploadQueue uploads,
        IGpuResourceRetirementQueue retirement,
        VulkanDebugNames debugNames,
        in GpuTextureDescription description,
        int sampleCount = 1,
        bool renderTarget = false,
        bool sampleable = true,
        // Fully qualified: in a parameter-default expression the simple name
        // `Format` binds to this type's own GpuTextureFormat property first.
        Format formatOverride = Silk.NET.Vulkan.Format.Undefined)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _retirement = retirement ?? throw new ArgumentNullException(nameof(retirement));
        ArgumentException.ThrowIfNullOrWhiteSpace(description.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.LayerCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.MipLevelCount);
        if (sampleCount > 1 && sampleable)
        {
            throw new ArgumentException(
                "A multisampled image cannot be registered in acdream's single-sampled texture table; "
                + "create a separate single-sampled resolve image.",
                nameof(sampleable));
        }

        Name = description.Name;
        Kind = description.Kind;
        Format = description.Format;
        Width = description.Width;
        Height = description.Height;
        LayerCount = description.LayerCount;
        MipLevelCount = description.MipLevelCount;
        SampleCount = sampleCount;
        IsSampleable = sampleable;
        VkFormat = formatOverride != Silk.NET.Vulkan.Format.Undefined
            ? formatOverride
            : VulkanTextureFormatMapping.FormatOf(description.Format);
        Aspect = VulkanTextureFormatMapping.AspectOf(description.Format);

        bool depthStencil = VulkanTextureFormatMapping.IsDepthStencil(description.Format);
        ImageUsageFlags usage = depthStencil
            ? ImageUsageFlags.DepthStencilAttachmentBit
            : ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit;
        if (sampleable)
            usage |= ImageUsageFlags.SampledBit;
        if (renderTarget && !depthStencil)
            usage |= ImageUsageFlags.ColorAttachmentBit;
        if (sampleCount > 1)
        {
            usage = depthStencil
                ? ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransientAttachmentBit
                : ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit;
        }

        var create = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = VkFormat,
            Extent = new Extent3D((uint)description.Width, (uint)description.Height, 1),
            MipLevels = (uint)description.MipLevelCount,
            ArrayLayers = (uint)description.LayerCount,
            Samples = VulkanTextureFormatMapping.SampleCountOf(sampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VulkanInterop.Check(
            _vk.CreateImage(_device, &create, null, out Image image),
            $"vkCreateImage ('{description.Name}')");
        Image = image;

        try
        {
            _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements requirements);
            _allocation = _allocator.Allocate(requirements, GpuMemoryResidency.DeviceLocal, description.Name);
            VulkanInterop.Check(
                _vk.BindImageMemory(_device, image, _allocation.Memory, _allocation.OffsetBytes),
                $"vkBindImageMemory ('{description.Name}')");

            var viewCreate = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = renderTarget
                    ? VulkanTextureFormatMapping.ViewTypeOf(description.Kind)
                    : VulkanTextureFormatMapping.SampledViewTypeOf(description.Kind),
                Format = VkFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = Aspect,
                    BaseMipLevel = 0,
                    LevelCount = (uint)description.MipLevelCount,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)description.LayerCount,
                },
            };
            VulkanInterop.Check(
                _vk.CreateImageView(_device, &viewCreate, null, out ImageView view),
                $"vkCreateImageView ('{description.Name}')");
            View = view;
            SampledView = renderTarget && !sampleable ? default : view;

            if (renderTarget && sampleable)
            {
                viewCreate.ViewType = VulkanTextureFormatMapping.SampledViewTypeOf(description.Kind);
                if (depthStencil)
                    viewCreate.SubresourceRange.AspectMask = ImageAspectFlags.DepthBit;
                VulkanInterop.Check(
                    _vk.CreateImageView(_device, &viewCreate, null, out ImageView sampled),
                    $"vkCreateImageView ('{description.Name}', sampled)");
                SampledView = sampled;
                debugNames.NameImageView(sampled, $"{description.Name}-sampled-view");
            }
        }
        catch
        {
            _vk.DestroyImage(_device, image, null);
            throw;
        }

        debugNames.NameImage(image, description.Name);
        debugNames.NameImageView(View, $"{description.Name}-view");
    }

    public string Name { get; }
    public GpuTextureKind Kind { get; }
    public GpuTextureFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int LayerCount { get; }
    public int MipLevelCount { get; }

    internal int SampleCount { get; }
    internal bool IsSampleable { get; }
    internal Image Image { get; }

    /// <summary>The view a pass names as an attachment, and the only view a non-attachment has.</summary>
    internal ImageView View { get; }

    internal ImageView SampledView { get; }
    internal ImageLayout SampledLayout =>
        VulkanTextureFormatMapping.IsDepthStencil(Format)
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    internal Format VkFormat { get; }
    internal ImageAspectFlags Aspect { get; }

    internal ImageLayout CurrentLayout { get; private set; } = ImageLayout.Undefined;

    internal void MarkLayout(ImageLayout layout) => CurrentLayout = layout;

    public void Upload(int mipLevel, int layer, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mipLevel, MipLevelCount);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, LayerCount);
        if (data.IsEmpty)
            return;

        (int width, int height) = VulkanTextureFormatMapping.LevelExtent(Width, Height, mipLevel);
        int expected = VulkanTextureFormatMapping.LevelSizeBytes(Format, width, height);
        if (data.Length < expected)
        {
            throw new ArgumentException(
                $"Mip {mipLevel} of '{Name}' is {width}x{height} and needs {expected} bytes; " +
                $"{data.Length} were supplied.",
                nameof(data));
        }

        _uploads.StageImageWrite(Image, mipLevel, layer, width, height, CurrentLayout, data, Name);
        CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    public void GenerateMipChain()
    {
        ThrowIfDisposed();
        if (MipLevelCount <= 1)
            return;

        if (BlockCompressionCodec.IsBlockCompressed(Format))
        {
            throw new NotSupportedException(
                $"'{Name}' is {Format}, and Vulkan cannot blit into a block-compressed image. " +
                "Build the chain on the CPU with BlockCompressionMipChain and upload each level " +
                "through Upload(mipLevel, layer, data). The GL path's reliance on driver-defined " +
                "glGenerateMipmap for compressed arrays is deliberately not carried forward.");
        }

        _uploads.EnqueueMipBlit(Image, Width, Height, MipLevelCount, LayerCount, CurrentLayout);
        CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Image image = Image;
        ImageView view = View;
        ImageView sampledView = SampledView;
        VulkanAllocation allocation = _allocation;
        _retirement.Retire(() =>
        {
            if (sampledView.Handle != 0 && sampledView.Handle != view.Handle)
                _vk.DestroyImageView(_device, sampledView, null);
            _vk.DestroyImageView(_device, view, null);
            _vk.DestroyImage(_device, image, null);
            _allocator.Free(allocation);
        });
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed unsafe class VulkanGpuSampler : IGpuSampler
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly IGpuResourceRetirementQueue _retirement;
    private bool _disposed;

    internal VulkanGpuSampler(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        IGpuResourceRetirementQueue retirement,
        VulkanDebugNames debugNames,
        in GpuSamplerDescription description,
        float maxSupportedAnisotropy)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _retirement = retirement ?? throw new ArgumentNullException(nameof(retirement));
        Description = description;

        float anisotropy = Math.Clamp(description.MaxAnisotropy, 1f, Math.Max(1f, maxSupportedAnisotropy));
        var create = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = VulkanTextureFormatMapping.FilterOf(description.MinFilter),
            MagFilter = VulkanTextureFormatMapping.FilterOf(description.MagFilter),
            MipmapMode = VulkanTextureFormatMapping.MipmapModeOf(description.MipFilter),
            AddressModeU = VulkanTextureFormatMapping.AddressModeOf(description.AddressU),
            AddressModeV = VulkanTextureFormatMapping.AddressModeOf(description.AddressV),
            AddressModeW = VulkanTextureFormatMapping.AddressModeOf(description.AddressV),
            AnisotropyEnable = anisotropy > 1f,
            MaxAnisotropy = anisotropy,
            MinLod = 0f,
            // GpuMipFilter.None means "level 0 only", which Vulkan expresses as a
            // zero-width LOD range rather than as a filter mode.
            MaxLod = description.MipFilter == GpuMipFilter.None ? 0f : Silk.NET.Vulkan.Vk.LodClampNone,
            BorderColor = BorderColor.FloatTransparentBlack,
            CompareEnable = false,
            UnnormalizedCoordinates = false,
        };
        VulkanInterop.Check(
            _vk.CreateSampler(_device, &create, null, out Sampler sampler),
            "vkCreateSampler");
        Handle = sampler;
        debugNames.NameSampler(
            sampler,
            $"sampler-{description.MinFilter}-{description.MipFilter}-{description.AddressU}");
    }

    public GpuSamplerDescription Description { get; }

    internal Sampler Handle { get; }

    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Sampler handle = Handle;
        _retirement.Retire(() => _vk.DestroySampler(_device, handle, null));
    }
}
