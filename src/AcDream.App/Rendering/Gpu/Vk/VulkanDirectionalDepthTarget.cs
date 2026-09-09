using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal readonly record struct VulkanDirectionalMultiviewRange(uint BaseLayer, uint LayerCount);

internal static class VulkanDirectionalMultiviewContract
{
    internal static VulkanDirectionalMultiviewRange Resolve(uint viewMask, int targetLayerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetLayerCount);
        if (targetLayerCount > 31)
            throw new ArgumentOutOfRangeException(nameof(targetLayerCount));
        uint expected = (1u << targetLayerCount) - 1u;
        if (viewMask != expected)
            throw new NotSupportedException("Directional multiview must cover every contiguous target layer.");
        return new VulkanDirectionalMultiviewRange(0u, (uint)targetLayerCount);
    }
}

internal sealed unsafe class VulkanDirectionalDepthTarget : IGpuDirectionalDepthTarget
{
    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly IGpuResourceRetirementQueue _retirement;
    private readonly ImageView[] _layerViews;
    private readonly ImageLayout[] _layerLayouts;
    private bool _disposed;

    internal VulkanDirectionalDepthTarget(
        Silk.NET.Vulkan.Vk vk,
        Device device,
        VulkanDeviceMemoryAllocator allocator,
        VulkanUploadQueue uploads,
        IGpuResourceRetirementQueue retirement,
        VulkanDebugNames debugNames,
        in GpuDirectionalDepthTargetDescription description,
        Format depthStencilFormat)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        _retirement = retirement ?? throw new ArgumentNullException(nameof(retirement));
        Description = description;

        var textureDescription = new GpuTextureDescription(
            description.Name,
            GpuTextureKind.Texture2DArray,
            description.DepthFormat,
            description.Resolution,
            description.Resolution,
            description.LayerCount,
            MipLevelCount: 1);
        Texture = new VulkanGpuTexture(
            vk,
            device,
            allocator,
            uploads,
            retirement,
            debugNames,
            textureDescription,
            sampleCount: 1,
            renderTarget: true,
            sampleable: true,
            formatOverride: depthStencilFormat);

        _layerViews = new ImageView[description.LayerCount];
        _layerLayouts = new ImageLayout[description.LayerCount];
        try
        {
            for (int layer = 0; layer < _layerViews.Length; layer++)
            {
                var create = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = Texture.Image,
                    ViewType = ImageViewType.Type2D,
                    Format = Texture.VkFormat,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = (uint)layer,
                        LayerCount = 1,
                    },
                };
                VulkanInterop.Check(
                    vk.CreateImageView(device, &create, null, out ImageView view),
                    $"vkCreateImageView ('{description.Name}', layer {layer})");
                _layerViews[layer] = view;
                debugNames.NameImageView(view, $"{description.Name}-layer-{layer}");
            }
        }
        catch
        {
            foreach (ImageView view in _layerViews)
            {
                if (view.Handle != 0)
                    vk.DestroyImageView(device, view, null);
            }
            Texture.Dispose();
            throw;
        }
    }

    public GpuDirectionalDepthTargetDescription Description { get; }

    public IGpuTexture DepthTexture => Texture;

    internal VulkanGpuTexture Texture { get; }

    internal ImageView ViewAt(int layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, _layerViews.Length);
        return _layerViews[layer];
    }

    internal ImageView MultiviewView(uint viewMask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = VulkanDirectionalMultiviewContract.Resolve(viewMask, Description.LayerCount);
        return Texture.View;
    }

    internal int LayerCountForViewMask(uint viewMask)
    {
        return checked((int)VulkanDirectionalMultiviewContract.Resolve(
            viewMask,
            Description.LayerCount).LayerCount);
    }

    internal ImageLayout LayoutAt(int layer) => _layerLayouts[layer];

    internal void MarkLayout(int layer, ImageLayout layout) => _layerLayouts[layer] = layout;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        ImageView[] views = [.. _layerViews];
        _retirement.Retire(() =>
        {
            foreach (ImageView view in views)
                _vk.DestroyImageView(_device, view, null);
        });
        Texture.Dispose();
    }
}
