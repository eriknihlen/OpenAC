using System.Numerics;
using System.Runtime.InteropServices;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanRhiScene : IDisposable
{
    internal static readonly (string Corner, uint Rgba)[] QuadrantMarkers =
    [
        ("top-left", 0xE04040FFu),
        ("top-right", 0x40E040FFu),
        ("bottom-left", 0x4060E0FFu),
        ("bottom-right", 0xF0F0F0FFu),
    ];

    private const int OffscreenExtent = 128;

    private readonly VulkanGpuDevice _device;
    private readonly IGpuBuffer _vertexArena;
    private readonly IGpuBuffer _indexArena;
    private readonly IGpuPipeline _meshPipeline;
    private readonly IGpuPipeline _linePipeline;
    private readonly IGpuRenderTarget _offscreen;
    private readonly IGpuTexture _cardTexture;
    private readonly IGpuTexture _compressedTexture;
    private readonly List<IDisposable> _owned = [];

    private readonly GpuTextureSlot _cardSlot;
    private readonly GpuTextureSlot _compressedSlot;
    private GpuTextureSlot _offscreenSlot = GpuTextureSlot.Unassigned;

    private readonly uint _quadIndexCount;
    private readonly uint _lineVertexCount;
    private readonly uint _lineFirstVertex;

    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Vertex(Vector3 position, Vector3 normal, Vector2 texCoord)
    {
        public Vector3 Position = position;
        public Vector3 Normal = normal;
        public Vector2 TexCoord = texCoord;
    }

    /// <summary>std430 <c>BatchData</c> at the pinned 16-byte stride.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct BatchData
    {
        public uint TextureIndex;
        public uint TextureLayer;
        public uint Tint;
        public uint Pad;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DrawIndexedIndirectCommand
    {
        public uint IndexCount;
        public uint InstanceCount;
        public uint FirstIndex;
        public int VertexOffset;
        public uint FirstInstance;
    }

    internal VulkanRhiScene(VulkanGpuDevice device, int sampleCount)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        SampleCount = Math.Max(1, sampleCount);

        // ── the mesh arena: device-local, filled through the staging ring ──
        Vertex[] vertices = BuildVertices(out ushort[] indices, out _quadIndexCount, out _lineFirstVertex, out _lineVertexCount);
        _vertexArena = device.CreateBuffer(new GpuBufferDescription(
            "vk-scene-vertex-arena",
            vertices.Length * Marshal.SizeOf<Vertex>(),
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        _indexArena = device.CreateBuffer(new GpuBufferDescription(
            "vk-scene-index-arena",
            indices.Length * sizeof(ushort),
            GpuBufferUsage.Index | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        _vertexArena.Upload(0, MemoryMarshal.AsBytes<Vertex>(vertices));
        _indexArena.Upload(0, MemoryMarshal.AsBytes<ushort>(indices));
        _owned.Add(_vertexArena);
        _owned.Add(_indexArena);

        _cardTexture = BuildOrientationCard(device);
        _compressedTexture = BuildCompressedCheckerboard(device);
        _owned.Add(_cardTexture);
        _owned.Add(_compressedTexture);

        IGpuSampler sampler = device.CreateSampler(GpuSamplerDescription.WorldClamp);
        _cardSlot = device.RegisterTexture(_cardTexture, sampler);
        _compressedSlot = device.RegisterTexture(_compressedTexture, sampler);

        _offscreen = device.CreateRenderTarget(new GpuRenderTargetDescription(
            "vk-scene-offscreen",
            OffscreenExtent,
            OffscreenExtent,
            GpuTextureFormat.Rgba8UnormRenderTarget,
            DepthFormat: null,
            SampleCount: 1));
        _owned.Add(_offscreen);

        _meshPipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "vk-scene-mesh",
            Shaders = new GpuShaderSet("vk_probe"),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.StraightAlpha,
            Depth = GpuDepthState.OpaqueDefault,
            Cull = GpuCullMode.None,
            SampleCount = SampleCount,
        });
        _linePipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "vk-scene-line",
            Shaders = new GpuShaderSet("vk_probe"),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.LineList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            SampleCount = SampleCount,
        });
        _owned.Add(_meshPipeline);
        _owned.Add(_linePipeline);

        OffscreenPipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "vk-scene-offscreen",
            Shaders = new GpuShaderSet("vk_probe"),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            SampleCount = 1,
        });
        _owned.Add(OffscreenPipeline);
    }

    internal int SampleCount { get; }

    internal IGpuPipeline OffscreenPipeline { get; }

    internal void Render(IGpuFrame frame, uint width, uint height, double seconds)
    {
        ArgumentNullException.ThrowIfNull(frame);

        RenderOffscreen(frame);
        RenderMain(frame, width, height, seconds);
    }

    private void RenderOffscreen(IGpuFrame frame)
    {
        using (IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "vk-scene-offscreen",
            Color = new GpuColorAttachment(
                _offscreen,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                new Vector4(0.12f, 0.02f, 0.24f, 1f)),
            Depth = null,
            SampleCount = 1,
        }))
        {
            using IDisposable _ = encoder.BeginTimerScope("offscreen");
            encoder.BindPipeline(OffscreenPipeline);

            GpuPushConstants constants = GpuPushConstants.Default;
            constants.LightingMode = 1;
            encoder.SetPushConstants(constants);

            WriteInstances(frame, encoder, [Matrix4x4.CreateScale(0.75f)]);
            WriteBatches(frame, encoder, [new BatchData { Tint = 0xFFC020FFu }]);
            BindArena(encoder);
            encoder.DrawIndexed(6, 1, 0, 0, 0);
        }

        if (!_offscreenSlot.IsAssigned)
        {
            _offscreenSlot = _device.RegisterTexture(
                _offscreen.ColorTexture,
                _device.CreateSampler(GpuSamplerDescription.UiNearest));
        }
    }

    private void RenderMain(IGpuFrame frame, uint width, uint height, double seconds)
    {
        using IGpuPassEncoder encoder = frame.BeginPass(GpuPassDescription.BackbufferClear(
            "vk-scene-main",
            new Vector4(0.043f, 0.075f, 0.153f, 1f),
            SampleCount));
        using IDisposable scope = encoder.BeginTimerScope("main");

        float aspect = height == 0 ? 1f : width / (float)height;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            aspect,
            0.1f,
            50f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            new Vector3(0f, 0f, 3.4f),
            Vector3.Zero,
            Vector3.UnitY);

        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ViewProjection = view * projection;
        constants.LightingMode = 0;

        encoder.BindPipeline(_meshPipeline);
        encoder.SetPushConstants(constants);
        encoder.SetCullMode(GpuCullMode.None);
        encoder.SetDepthWrite(true);

        // Four quadrant markers plus one wide backdrop. Nothing here is
        // mirror-symmetric, on purpose.
        float wobble = (float)Math.Sin(seconds) * 0.05f;
        Matrix4x4[] instances =
        [
            Matrix4x4.CreateScale(2.6f, 1.6f, 1f) * Matrix4x4.CreateTranslation(0f, 0f, -0.4f),
            Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(-1.0f, 0.55f + wobble, 0f),
            Matrix4x4.CreateScale(0.36f) * Matrix4x4.CreateTranslation(0.95f, 0.55f, 0f),
            Matrix4x4.CreateScale(0.28f) * Matrix4x4.CreateTranslation(-1.0f, -0.6f, 0f),
            Matrix4x4.CreateScale(0.44f) * Matrix4x4.CreateTranslation(0.6f, -0.62f, 0f),
        ];
        BatchData[] batches =
        [
            new BatchData { TextureIndex = _cardSlot.Index, Tint = 0xFFFFFFFFu },
            new BatchData { TextureIndex = _compressedSlot.Index, Tint = QuadrantMarkers[0].Rgba },
            new BatchData { TextureIndex = _offscreenSlot.Index, Tint = QuadrantMarkers[1].Rgba },
            new BatchData { TextureIndex = _compressedSlot.Index, Tint = QuadrantMarkers[2].Rgba },
            new BatchData { TextureIndex = _cardSlot.Index, Tint = QuadrantMarkers[3].Rgba },
        ];

        WriteInstances(frame, encoder, instances);
        WriteBatches(frame, encoder, batches);
        BindArena(encoder);

        GpuRingAllocation commands = frame.AllocateRing(
            instances.Length * Marshal.SizeOf<DrawIndexedIndirectCommand>(),
            GpuRingUsage.Indirect);
        Span<DrawIndexedIndirectCommand> span = commands.AsSpan<DrawIndexedIndirectCommand>();
        for (int i = 0; i < instances.Length; i++)
        {
            span[i] = new DrawIndexedIndirectCommand
            {
                IndexCount = _quadIndexCount,
                InstanceCount = 1,
                FirstIndex = 0,
                VertexOffset = 0,
                // The per-group instance base — the reason
                // drawIndirectFirstInstance is a required feature.
                FirstInstance = (uint)i,
            };
        }

        encoder.MultiDrawIndexedIndirect(
            commands.Buffer,
            commands.OffsetBytes,
            (uint)instances.Length,
            (uint)Marshal.SizeOf<DrawIndexedIndirectCommand>());

        constants.LightingMode = 1;
        encoder.BindPipeline(_linePipeline);
        encoder.SetPushConstants(constants);
        encoder.SetDepthWrite(false);

        WriteInstances(frame, encoder, [Matrix4x4.Identity]);
        WriteBatches(frame, encoder, [new BatchData { Tint = 0xFFE060FFu }]);
        BindArena(encoder);
        encoder.Draw(_lineVertexCount, 1, _lineFirstVertex, 0);
    }

    private void BindArena(IGpuPassEncoder encoder)
    {
        encoder.BindVertexBuffer(0, _vertexArena, 0);
        encoder.BindIndexBuffer(_indexArena, 0, GpuIndexType.UInt16);
    }

    private static void WriteInstances(
        IGpuFrame frame,
        IGpuPassEncoder encoder,
        ReadOnlySpan<Matrix4x4> transforms)
    {
        GpuRingAllocation allocation = frame.AllocateRing(
            transforms.Length * Marshal.SizeOf<Matrix4x4>(),
            GpuRingUsage.Storage);
        transforms.CopyTo(allocation.AsSpan<Matrix4x4>());
        encoder.BindStorageBuffer(
            GpuBindingModel.StorageInstances,
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)allocation.Data.Length);
    }

    private static void WriteBatches(
        IGpuFrame frame,
        IGpuPassEncoder encoder,
        ReadOnlySpan<BatchData> batches)
    {
        GpuRingAllocation allocation = frame.AllocateRing(
            batches.Length * GpuBindingModel.GpuBatchDataStrideBytes,
            GpuRingUsage.Storage);
        batches.CopyTo(allocation.AsSpan<BatchData>());
        encoder.BindStorageBuffer(
            GpuBindingModel.StorageBatches,
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)allocation.Data.Length);
    }

    /// <summary>
    /// One unit quad (indexed) followed by an asymmetric open line figure. Both
    /// live in the same arena, which is what a real mesh arena does and what the
    /// vertex-offset/first-vertex plumbing has to get right.
    /// </summary>
    private static Vertex[] BuildVertices(
        out ushort[] indices,
        out uint quadIndexCount,
        out uint lineFirstVertex,
        out uint lineVertexCount)
    {
        var vertices = new List<Vertex>
        {
            new(new Vector3(-0.5f, 0.5f, 0f), Vector3.UnitZ, new Vector2(0f, 0f)),
            new(new Vector3(-0.5f, -0.5f, 0f), Vector3.UnitZ, new Vector2(0f, 1f)),
            new(new Vector3(0.5f, -0.5f, 0f), Vector3.UnitZ, new Vector2(1f, 1f)),
            new(new Vector3(0.5f, 0.5f, 0f), Vector3.UnitZ, new Vector2(1f, 0f)),
        };
        indices = [0, 1, 2, 0, 2, 3];
        quadIndexCount = 6;

        lineFirstVertex = (uint)vertices.Count;
        // An "L" opening up and to the left, drawn as a line list: three
        // segments, no symmetry in either axis.
        Vector3[] path =
        [
            new(-1.5f, 0.9f, 0.2f),
            new(-1.5f, -0.9f, 0.2f),
            new(-1.5f, -0.9f, 0.2f),
            new(0.2f, -0.9f, 0.2f),
            new(0.2f, -0.9f, 0.2f),
            new(0.2f, -0.4f, 0.2f),
        ];
        foreach (Vector3 point in path)
            vertices.Add(new Vertex(point, Vector3.UnitZ, Vector2.Zero));
        lineVertexCount = (uint)path.Length;

        return [.. vertices];
    }

    private static IGpuTexture BuildOrientationCard(VulkanGpuDevice device)
    {
        const int extent = 16;
        int levels = VulkanTextureFormatMapping.FullMipLevelCount(extent, extent);
        IGpuTexture texture = device.CreateTexture(new GpuTextureDescription(
            "vk-scene-orientation-card",
            GpuTextureKind.Texture2DArray,
            GpuTextureFormat.Rgba8Unorm,
            extent,
            extent,
            LayerCount: 1,
            MipLevelCount: levels));

        var pixels = new byte[extent * extent * 4];
        for (int y = 0; y < extent; y++)
        {
            for (int x = 0; x < extent; x++)
            {
                bool top = y < extent / 2;
                bool left = x < extent / 2;
                uint colour = (top, left) switch
                {
                    (true, true) => QuadrantMarkers[0].Rgba,
                    (true, false) => QuadrantMarkers[1].Rgba,
                    (false, true) => QuadrantMarkers[2].Rgba,
                    _ => QuadrantMarkers[3].Rgba,
                };
                bool frame = x == 0 || y == 0 || x == extent - 1 || y == extent - 1;
                if (frame)
                    colour = 0x101010FFu;

                int offset = ((y * extent) + x) * 4;
                pixels[offset + 0] = (byte)(colour >> 24);
                pixels[offset + 1] = (byte)(colour >> 16);
                pixels[offset + 2] = (byte)(colour >> 8);
                pixels[offset + 3] = (byte)colour;
            }
        }

        texture.Upload(0, 0, pixels);
        texture.GenerateMipChain();
        return texture;
    }

    private static IGpuTexture BuildCompressedCheckerboard(VulkanGpuDevice device)
    {
        const int extent = 32;
        int levels = VulkanTextureFormatMapping.FullMipLevelCount(extent, extent);
        IGpuTexture texture = device.CreateTexture(new GpuTextureDescription(
            "vk-scene-checkerboard",
            GpuTextureKind.Texture2DArray,
            GpuTextureFormat.Bc1Unorm,
            extent,
            extent,
            LayerCount: 1,
            MipLevelCount: levels));

        var rgba = new byte[extent * extent * 4];
        for (int y = 0; y < extent; y++)
        {
            for (int x = 0; x < extent; x++)
            {
                bool light = ((x / 4) + (y / 4)) % 2 == 0;
                byte value = light ? (byte)0xFF : (byte)0x50;
                int offset = ((y * extent) + x) * 4;
                rgba[offset + 0] = value;
                rgba[offset + 1] = value;
                rgba[offset + 2] = value;
                rgba[offset + 3] = 0xFF;
            }
        }

        texture.Upload(0, 0, BlockCompressionCodec.EncodeLevel(GpuTextureFormat.Bc1Unorm, rgba, extent, extent));
        foreach (BlockCompressionMipChain.Level level in
                 BlockCompressionMipChain.BuildFromRgba(GpuTextureFormat.Bc1Unorm, rgba, extent, extent, levels))
        {
            texture.Upload(level.MipLevel, 0, level.Data);
        }

        return texture;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _owned.Clear();
    }
}
