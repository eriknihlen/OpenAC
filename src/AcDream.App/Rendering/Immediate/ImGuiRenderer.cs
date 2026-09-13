using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using ImGuiNET;

namespace AcDream.App.Rendering.Immediate;

/// <summary>
/// Draws Dear ImGui's frame output through the RHI. It reuses the retained
/// UI's <c>ui_text</c> program: ImGui's vertex is pos/uv/packed-colour, which
/// is exactly that shader's input once the colour is declared as four
/// normalised bytes, so the draw lists copy straight into the frame's ring.
/// </summary>
internal sealed unsafe class ImGuiRenderer : IDisposable
{
    private const int VertexStrideBytes = 20;

    private static readonly GpuVertexLayout VertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: VertexStrideBytes,
        [
            new GpuVertexAttribute(0, GpuVertexFormat.Float2, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float2, 8),
            new GpuVertexAttribute(2, GpuVertexFormat.UByte4Normalized, 16),
        ]);

    private readonly IGpuDevice _device;
    private readonly IGpuPipeline _pipeline;
    private readonly Dictionary<nint, GpuTextureSlot> _textures = [];
    private IGpuTexture? _fontTexture;
    private IGpuSampler? _fontSampler;
    private GpuTextureSlot _fontSlot = GpuTextureSlot.Unassigned;
    private nint _nextTextureId = 1;
    private bool _disposed;

    internal ImGuiRenderer(IGpuDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "imgui",
            Shaders = new GpuShaderSet("ui_text"),
            VertexLayout = VertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.StraightAlpha,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = 1,
        });
    }

    /// <summary>Uploads the atlas ImGui built and hands the atlas its texture id.</summary>
    internal void CreateFontTexture(ImGuiIOPtr io)
    {
        ReleaseFontTexture();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        var rgba = new byte[width * height * bytesPerPixel];
        Marshal.Copy((nint)pixels, rgba, 0, rgba.Length);

        _fontTexture = _device.CreateTexture(new GpuTextureDescription(
            "imgui-font-atlas",
            GpuTextureKind.Texture2D,
            GpuTextureFormat.Rgba8Unorm,
            width,
            height,
            LayerCount: 1,
            MipLevelCount: 1));
        _fontTexture.Upload(0, 0, rgba);
        _fontSampler = _device.CreateSampler(new GpuSamplerDescription(
            GpuFilter.Linear,
            GpuFilter.Linear,
            GpuMipFilter.None,
            GpuAddressMode.ClampToEdge,
            GpuAddressMode.ClampToEdge,
            MaxAnisotropy: 1f));
        _fontSlot = _device.RegisterTexture(_fontTexture, _fontSampler);

        nint fontId = AllocateTextureId(_fontSlot);
        io.Fonts.SetTexID(fontId);
        io.Fonts.ClearTexData();
    }

    /// <summary>Makes a registered slot addressable from <c>ImGui.Image</c>; returns the id to pass.</summary>
    internal nint AllocateTextureId(GpuTextureSlot slot)
    {
        nint id = _nextTextureId++;
        _textures[id] = slot;
        return id;
    }

    internal void ReleaseTextureId(nint id) => _textures.Remove(id);

    internal void Draw(IGpuFrame frame, ImDrawDataPtr drawData, int viewportWidth, int viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (drawData.CmdListsCount == 0 || drawData.TotalVtxCount == 0)
            return;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "imgui",
            Color = new GpuColorAttachment(
                Target: null,
                Load: GpuLoadOp.Load,
                Store: GpuStoreOp.Store,
                ClearColor: default),
            Depth = null,
            SampleCount = 1,
        });
        encoder.BindPipeline(_pipeline);

        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ParamA = drawData.DisplaySize.X;
        constants.ParamB = drawData.DisplaySize.Y;
        constants.TextureIndexB = GpuTextureSlot.Unassigned.Index;

        Vector2 clipOffset = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;

        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr list = drawData.CmdLists[listIndex];
            int vertexBytes = list.VtxBuffer.Size * VertexStrideBytes;
            int indexBytes = list.IdxBuffer.Size * sizeof(ushort);
            if (vertexBytes == 0 || indexBytes == 0)
                continue;

            GpuRingAllocation vertices = frame.AllocateRing(vertexBytes, GpuRingUsage.Vertex);
            new ReadOnlySpan<byte>((void*)list.VtxBuffer.Data, vertexBytes).CopyTo(vertices.Data);
            GpuRingAllocation indices = frame.AllocateRing(indexBytes, GpuRingUsage.Index);
            new ReadOnlySpan<byte>((void*)list.IdxBuffer.Data, indexBytes).CopyTo(indices.Data);

            encoder.BindVertexBuffer(0, vertices.Buffer, vertices.OffsetBytes);
            encoder.BindIndexBuffer(indices.Buffer, indices.OffsetBytes, GpuIndexType.UInt16);

            for (int commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmdPtr command = list.CmdBuffer[commandIndex];
                if (command.UserCallback != nint.Zero)
                    continue;

                float clipLeft = (command.ClipRect.X - clipOffset.X) * clipScale.X;
                float clipTop = (command.ClipRect.Y - clipOffset.Y) * clipScale.Y;
                float clipRight = (command.ClipRect.Z - clipOffset.X) * clipScale.X;
                float clipBottom = (command.ClipRect.W - clipOffset.Y) * clipScale.Y;
                clipLeft = Math.Max(clipLeft, 0f);
                clipTop = Math.Max(clipTop, 0f);
                clipRight = Math.Min(clipRight, viewportWidth);
                clipBottom = Math.Min(clipBottom, viewportHeight);
                if (clipRight <= clipLeft || clipBottom <= clipTop)
                    continue;

                // The encoder's scissor origin is the bottom-left corner of
                // the attachment; ImGui's clip rects are top-left.
                encoder.SetScissor(
                    (int)clipLeft,
                    viewportHeight - (int)clipBottom,
                    (int)(clipRight - clipLeft),
                    (int)(clipBottom - clipTop));

                constants.TextureIndexA = _textures.TryGetValue(command.TextureId, out GpuTextureSlot slot)
                    ? slot.Index
                    : _fontSlot.Index;
                encoder.SetPushConstants(constants);
                encoder.DrawIndexed(
                    command.ElemCount,
                    1,
                    command.IdxOffset,
                    (int)command.VtxOffset,
                    0);
            }
        }

        encoder.SetScissor(0, 0, viewportWidth, viewportHeight);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseFontTexture();
        _pipeline.Dispose();
    }

    private void ReleaseFontTexture()
    {
        if (_fontSlot.IsAssigned)
        {
            _device.ReleaseTextureSlot(_fontSlot);
            _fontSlot = GpuTextureSlot.Unassigned;
        }
        _fontSampler?.Dispose();
        _fontSampler = null;
        _fontTexture?.Dispose();
        _fontTexture = null;
    }
}
