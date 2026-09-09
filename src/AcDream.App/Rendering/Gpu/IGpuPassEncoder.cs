namespace AcDream.App.Rendering.Gpu;

internal interface IGpuPassEncoder : IDisposable
{
    /// <summary>The pass this encoder is recording into.</summary>
    GpuPassDescription Pass { get; }

    /// <summary>Binds the shader program and all baked fixed state.</summary>
    void BindPipeline(IGpuPipeline pipeline);

    void BindStorageBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes, uint sizeBytes);

    void BindUniformBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes, uint sizeBytes);

    void BindVertexBuffer(uint binding, IGpuBuffer buffer, uint offsetBytes);

    /// <summary>Binds the index source.</summary>
    void BindIndexBuffer(IGpuBuffer buffer, uint offsetBytes, GpuIndexType indexType);

    void SetPushConstants(in GpuPushConstants constants);

    void SetViewport(int x, int y, int width, int height);

    void SetScissor(int x, int y, int width, int height);

    void SetCullMode(GpuCullMode cullMode);

    void SetFrontFace(GpuFrontFace frontFace);

    /// <summary>Dynamic depth-write override — how the translucent pass stops occluding later draws.</summary>
    void SetDepthWrite(bool enabled);

    void SetStencil(in GpuStencilState stencil);

    /// <summary>Draws indexed geometry directly, without an indirect buffer.</summary>
    void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance);

    /// <summary>Draws non-indexed geometry — the retained UI's batched sprite/glyph quads.</summary>
    void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

    void MultiDrawIndexedIndirect(IGpuBuffer commands, uint offsetBytes, uint drawCount, uint strideBytes);

    IDisposable BeginTimerScope(string scopeName);
}
