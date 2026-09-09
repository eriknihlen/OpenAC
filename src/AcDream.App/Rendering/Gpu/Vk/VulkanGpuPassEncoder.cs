using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGpuPassEncoder : IGpuPassEncoder
{
    private readonly VulkanGpuDevice _device;
    private readonly VulkanGpuFrame _frame;
    private readonly CommandBuffer _commands;
    private readonly VulkanFrameBindings _bindings;
    private readonly uint _attachmentWidth;
    private readonly uint _attachmentHeight;
    private readonly bool _hasDepthAttachment;
    private readonly bool _hasColorAttachment;
    private readonly GpuTextureFormat _colorFormat;

    internal int AttachmentWidth => (int)_attachmentWidth;

    internal int AttachmentHeight => (int)_attachmentHeight;

    internal bool HasDepthAttachment => _hasDepthAttachment;

    private VulkanGpuPipeline? _pipeline;
    private VulkanDrawBindingState _drawBindingState;
    private bool _closed;

    internal VulkanGpuPassEncoder(
        VulkanGpuDevice device,
        VulkanGpuFrame frame,
        CommandBuffer commands,
        VulkanFrameBindings bindings,
        GpuPassDescription pass,
        uint attachmentWidth,
        uint attachmentHeight,
        bool hasDepthAttachment,
        bool hasColorAttachment,
        GpuTextureFormat colorFormat)
    {
        _device = device;
        _frame = frame;
        _commands = commands;
        _bindings = bindings;
        _attachmentWidth = attachmentWidth;
        _attachmentHeight = attachmentHeight;
        _hasDepthAttachment = hasDepthAttachment;
        _hasColorAttachment = hasColorAttachment;
        _colorFormat = colorFormat;
        Pass = pass;

        // A pass always starts with the whole attachment drawable. GL's
        // BeginPass deliberately does not touch viewport or scissor because a
        // raw-GL renderer may have set them; Vulkan has no such ambient state,
        // and a pipeline with dynamic viewport MUST have one set before any
        // draw, so the full-attachment default is the only safe starting point.
        SetViewport(0, 0, (int)attachmentWidth, (int)attachmentHeight);
        SetScissor(0, 0, (int)attachmentWidth, (int)attachmentHeight);

    }

    public GpuPassDescription Pass { get; }

    public void BindPipeline(IGpuPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ThrowIfClosed();
        if (pipeline is not VulkanGpuPipeline vulkanPipeline)
            throw new ArgumentException("The Vulkan backend can only bind a Vulkan pipeline.", nameof(pipeline));
        if (vulkanPipeline.Description.HasColorAttachment != _hasColorAttachment)
        {
            throw new InvalidOperationException(
                $"Pipeline '{vulkanPipeline.Description.Name}' colour-attachment intent does not match pass '{Pass.Name}'.");
        }
        if (vulkanPipeline.Description.ViewMask != Pass.ViewMask)
        {
            throw new InvalidOperationException(
                $"Pipeline '{vulkanPipeline.Description.Name}' view mask does not match pass '{Pass.Name}'.");
        }

        _pipeline = vulkanPipeline;
        _device.Api.CmdBindPipeline(
            _commands,
            PipelineBindPoint.Graphics,
            vulkanPipeline.HandleFor(_hasDepthAttachment, _colorFormat));

        _device.CmdBindPipelineDefaults(_commands, vulkanPipeline.Description);
    }

    public void BindStorageBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes, uint sizeBytes)
    {
        ThrowIfClosed();
        _bindings.SetStorage(binding, RequireBuffer(buffer), offsetBytes, sizeBytes);
        _drawBindingState.MarkDirty();
    }

    public void BindUniformBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes, uint sizeBytes)
    {
        ThrowIfClosed();
        _bindings.SetUniform(binding, RequireBuffer(buffer), offsetBytes, sizeBytes);
        _drawBindingState.MarkDirty();
    }

    private void FlushBindings()
    {
        VulkanGpuPipeline pipeline = RequirePipeline();
        ulong pipelineLayout = pipeline.PipelineLayout.Handle;
        int packGeneration = pipeline.PackState?.Generation ?? 0;
        if (!_drawBindingState.RequiresBind(pipelineLayout, packGeneration))
            return;
        _bindings.Bind(
            _commands,
            _device,
            pipeline.PipelineLayout,
            pipeline.PackState);
        _drawBindingState.MarkBound(pipelineLayout, packGeneration);
    }

    internal void ClearAttachments(
        uint attachmentCount,
        ClearAttachment* attachments,
        uint rectCount,
        ClearRect* rects)
    {
        ThrowIfClosed();
        _device.Api.CmdClearAttachments(_commands, attachmentCount, attachments, rectCount, rects);
    }

    public void BindVertexBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes)
    {
        ThrowIfClosed();
        Silk.NET.Vulkan.Buffer handle = RequireBuffer(buffer).Handle;
        ulong offset = offsetBytes;
        _device.Api.CmdBindVertexBuffers(_commands, binding, 1, &handle, &offset);
    }

    public void BindIndexBuffer(IGpuBuffer buffer, uint offsetBytes, GpuIndexType indexType)
    {
        ThrowIfClosed();
        _device.Api.CmdBindIndexBuffer(
            _commands,
            RequireBuffer(buffer).Handle,
            offsetBytes,
            VulkanViewportMapping.ToVulkan(indexType));
    }

    public void SetPushConstants(in GpuPushConstants constants)
    {
        ThrowIfClosed();
        fixed (GpuPushConstants* pointer = &constants)
        {
            _device.Api.CmdPushConstants(
                _commands,
                _pipeline?.PipelineLayout ?? _device.Layouts.PipelineLayout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                (uint)GpuBindingModel.PushConstantBytes,
                pointer);
        }
    }

    public void SetViewport(int x, int y, int width, int height)
    {
        ThrowIfClosed();
        Viewport viewport = VulkanViewportMapping.ToVulkan(x, y, width, height, _attachmentHeight);
        _device.Api.CmdSetViewport(_commands, 0, 1, &viewport);
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        ThrowIfClosed();
        Rect2D scissor = VulkanViewportMapping.ScissorToVulkan(x, y, width, height, _attachmentHeight);
        _device.Api.CmdSetScissor(_commands, 0, 1, &scissor);
    }

    public void SetCullMode(GpuCullMode cullMode)
    {
        ThrowIfClosed();
        _device.Api.CmdSetCullMode(_commands, VulkanViewportMapping.ToVulkan(cullMode));
    }

    public void SetFrontFace(GpuFrontFace frontFace)
    {
        ThrowIfClosed();
        _device.Api.CmdSetFrontFace(_commands, VulkanViewportMapping.ToVulkan(frontFace));
    }

    public void SetDepthWrite(bool enabled)
    {
        ThrowIfClosed();
        _device.Api.CmdSetDepthWriteEnable(_commands, enabled);
    }

    public void SetStencil(in GpuStencilState stencil)
    {
        ThrowIfClosed();
        const StencilFaceFlags BothFaces = StencilFaceFlags.FaceFrontAndBack;
        _device.Api.CmdSetStencilOp(
            _commands,
            BothFaces,
            VulkanViewportMapping.ToVulkan(stencil.Fail),
            VulkanViewportMapping.ToVulkan(stencil.Pass),
            VulkanViewportMapping.ToVulkan(stencil.DepthFail),
            VulkanViewportMapping.ToVulkan(stencil.Compare));
        _device.Api.CmdSetStencilCompareMask(_commands, BothFaces, stencil.CompareMask);
        _device.Api.CmdSetStencilWriteMask(_commands, BothFaces, stencil.WriteMask);
        _device.Api.CmdSetStencilReference(_commands, BothFaces, stencil.Reference);
    }

    public void DrawIndexed(
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance)
    {
        ThrowIfClosed();
        RequirePipeline();
        FlushBindings();
        _device.Api.CmdDrawIndexed(_commands, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        ThrowIfClosed();
        RequirePipeline();
        FlushBindings();
        _device.Api.CmdDraw(_commands, vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public void MultiDrawIndexedIndirect(IGpuBuffer commands, uint offsetBytes, uint drawCount, uint strideBytes)
    {
        ThrowIfClosed();
        RequirePipeline();
        if (drawCount == 0)
            return;
        FlushBindings();
        _device.Api.CmdDrawIndexedIndirect(
            _commands,
            RequireBuffer(commands).Handle,
            offsetBytes,
            drawCount,
            strideBytes);
    }

    public IDisposable BeginTimerScope(string scopeName) =>
        _device.TimerPool.BeginScope(_commands, scopeName);

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _device.EndPass(this);
        _frame.ClosePass(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static VulkanGpuBuffer RequireBuffer(IGpuBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer is not VulkanGpuBuffer vulkanBuffer)
            throw new ArgumentException("The Vulkan backend can only bind Vulkan buffers.", nameof(buffer));
        return vulkanBuffer;
    }

    private VulkanGpuPipeline RequirePipeline()
    {
        if (_pipeline is null)
            throw new InvalidOperationException("BindPipeline must be called before drawing.");
        return _pipeline;
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);
}
