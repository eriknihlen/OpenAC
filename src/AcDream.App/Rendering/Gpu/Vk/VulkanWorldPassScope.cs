using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanWorldPassScope : IWorldPassScope
{
    private readonly Publication _publication;
    private IGpuPassEncoder? _encoder;

    internal VulkanWorldPassScope(int sampleCount)
    {
        if (sampleCount < 1)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        SampleCount = sampleCount;
        _publication = new Publication(this);
    }

    public int SampleCount { get; }

    public WorldFrameSections Sections { get; } = new();

    public IGpuPassEncoder? CurrentEncoder => _encoder;

    public int AttachmentWidth =>
        _encoder is VulkanGpuPassEncoder vulkan ? vulkan.AttachmentWidth : 0;

    public int AttachmentHeight =>
        _encoder is VulkanGpuPassEncoder vulkan ? vulkan.AttachmentHeight : 0;

    public IGpuPassEncoder RequireEncoder() =>
        _encoder ?? throw new InvalidOperationException(
            "The Vulkan world renderers record into the pass VulkanWorldScenePhase opens; "
            + "no pass is open. The phase must bracket every world draw.");

    public IDisposable Publish(IGpuPassEncoder encoder) =>
        PublishCore(encoder, preservePreparedSections: false);

    internal IDisposable PublishPrepared(IGpuPassEncoder encoder) =>
        PublishCore(encoder, preservePreparedSections: true);

    private IDisposable PublishCore(
        IGpuPassEncoder encoder,
        bool preservePreparedSections)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (_encoder is not null)
        {
            throw new InvalidOperationException(
                "A Vulkan world pass is already published; passes do not nest.");
        }

        _encoder = encoder;
        if (!preservePreparedSections)
            Sections.Reset();
        return _publication;
    }

    public void ClearInteriorDepth()
    {
        if (RequireEncoder() is not VulkanGpuPassEncoder encoder)
        {
            throw new InvalidOperationException(
                "The Vulkan world pass scope was published with a non-Vulkan encoder.");
        }

        if (!encoder.HasDepthAttachment)
            return;

        var attachment = new ClearAttachment
        {
            AspectMask = ImageAspectFlags.DepthBit,
            ColorAttachment = 0,
            ClearValue = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue(depth: 1f, stencil: 0),
            },
        };
        var rect = new ClearRect
        {
            Rect = new Rect2D(
                new Offset2D(0, 0),
                new Extent2D((uint)encoder.AttachmentWidth, (uint)encoder.AttachmentHeight)),
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        encoder.ClearAttachments(1, &attachment, 1, &rect);
    }

    private sealed class Publication : IDisposable
    {
        private readonly VulkanWorldPassScope _owner;

        internal Publication(VulkanWorldPassScope owner) => _owner = owner;

        public void Dispose()
        {
            _owner._encoder = null;
            _owner.Sections.Reset();
        }
    }
}
