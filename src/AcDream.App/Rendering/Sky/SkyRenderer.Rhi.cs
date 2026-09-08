using System.Collections.Immutable;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.Core.Meshing;
using AcDream.Core.Terrain;
using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.Rendering.Sky;

public sealed unsafe partial class SkyRenderer
{
    private readonly IGpuDevice? _device;
    private readonly ICurrentGpuFrameSource? _frames;
    private readonly IWorldPassScope? _scope;
    private IGpuPipeline? _alphaPipeline;
    private IGpuPipeline? _additivePipeline;

    private readonly Dictionary<(uint SurfaceId, bool Repeat), GpuTextureSlot>
        _slotBySurfaceAndWrap = new();

    internal static readonly GpuVertexLayout SkyVertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: (uint)sizeof(Vertex),
        ImmutableArray.Create(
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12),
            new GpuVertexAttribute(2, GpuVertexFormat.Float2, 24)));

    internal SkyRenderer(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IWorldPassScope scope,
        IDatReaderWriter dats,
        TextureCache textures)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));

        _alphaPipeline = CreateSkyPipeline("sky-alpha", GpuBlendMode.StraightAlpha);
        try
        {
            _additivePipeline = CreateSkyPipeline("sky-additive", GpuBlendMode.Additive);
        }
        catch
        {
            _alphaPipeline.Dispose();
            _alphaPipeline = null;
            throw;
        }
    }

    private IGpuPipeline CreateSkyPipeline(string name, GpuBlendMode blend) =>
        _device!.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = new GpuShaderSet("sky"),
            VertexLayout = SkyVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = blend,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            FrontFace = GpuFrontFace.CounterClockwise,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = _scope!.SampleCount,
        });

    /// <summary>
    /// Uploads one submesh into its own device-local vertex/index buffer pair.
    /// Sky meshes are built once per GfxObj and never move, so this is the same
    /// "upload once, draw many frames" shape the GL arm's static-draw VBOs have.
    /// </summary>
    private SubMeshGpu UploadSubMeshRhi(GfxObjSubMesh sm)
    {
        IGpuDevice device = _device!;
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes<Vertex>(sm.Vertices);
        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes<uint>(sm.Indices);

        IGpuBuffer vertices = device.CreateBuffer(new GpuBufferDescription(
            $"sky-vertices-0x{sm.SurfaceId:X8}",
            Math.Max(vertexBytes.Length, 32),
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        IGpuBuffer indices;
        try
        {
            indices = device.CreateBuffer(new GpuBufferDescription(
                $"sky-indices-0x{sm.SurfaceId:X8}",
                Math.Max(indexBytes.Length, 4),
                GpuBufferUsage.Index | GpuBufferUsage.TransferDestination,
                GpuMemoryResidency.DeviceLocal));
        }
        catch
        {
            vertices.Dispose();
            throw;
        }

        try
        {
            if (!vertexBytes.IsEmpty)
                vertices.Upload(0, vertexBytes);
            if (!indexBytes.IsEmpty)
                indices.Upload(0, indexBytes);
        }
        catch
        {
            indices.Dispose();
            vertices.Dispose();
            throw;
        }

        return new SubMeshGpu
        {
            VertexBuffer = vertices,
            IndexBuffer = indices,
            IndexCount = sm.Indices.Length,
            SurfaceId = sm.SurfaceId,
            IsAdditive = sm.Translucency == TranslucencyKind.Additive,
            SurfLuminosity = sm.Luminosity,
            SurfDiffuse = sm.Diffuse,
            NeedsUvRepeat = sm.NeedsUvRepeat,
            SurfOpacity = sm.SurfOpacity,
            DisableFog = sm.DisableFog,
        };
    }

    private uint RhiTextureTableSlot(uint surfaceId, bool repeat)
    {
        var key = (surfaceId, repeat);
        if (!_slotBySurfaceAndWrap.TryGetValue(key, out GpuTextureSlot slot))
        {
            slot = _textures.RegisterWorldSurface(surfaceId, repeat);
            _slotBySurfaceAndWrap.Add(key, slot);
        }

        return slot.Index;
    }

    /// <summary>
    /// Records one submesh into the borrowed world pass.
    ///
    /// <para>Order matters for the same reason it does in terrain's arm:
    /// <c>BindPipeline</c> re-issues the pipeline's own fixed state, and the
    /// frame-global sections are bound AFTER this renderer's own binds because
    /// those binds are what select the descriptor scope the sections must land in
    /// (plan §5.5.14 item 2).</para>
    /// </summary>
    private void DrawSubMeshRhi(SubMeshGpu sub, uint textureSlot, bool nightSky = false)
    {
        if (sub.IndexCount == 0 || sub.VertexBuffer is null || sub.IndexBuffer is null)
            return;

        IWorldPassScope scope = _scope!;
        IGpuPassEncoder encoder = scope.RequireEncoder();
        IGpuFrame frame = _frames!.CurrentFrame
            ?? throw new InvalidOperationException(
                "SkyRenderer requires an open IGpuFrame (see GpuDeviceFrameLifetime).");

        encoder.BindPipeline(nightSky || sub.IsAdditive ? _additivePipeline! : _alphaPipeline!);
        var pushConstants = new GpuPushConstants
        {
            ViewProjection = System.Numerics.Matrix4x4.Identity,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 0,
            LightDebug = 0,
            TextureIndexA = textureSlot,
            TextureIndexB = 0,
            ParamA = nightSky ? 1f : 0f,
            ParamB = NightSkySeed,
        };
        encoder.SetPushConstants(in pushConstants);
        encoder.BindVertexBuffer(0, sub.VertexBuffer, 0);
        encoder.BindIndexBuffer(sub.IndexBuffer, 0, GpuIndexType.UInt32);

        GpuRingAllocation parameters = frame.AllocateRing(
            SkyParams.SizeInBytes,
            GpuRingUsage.Uniform);
        MemoryMarshal.Write(parameters.Data, in _params);
        encoder.BindUniformBuffer(
            GpuBindingModel.UniformSkyParams,
            parameters.Buffer,
            parameters.OffsetBytes,
            SkyParams.SizeInBytes);

        WorldFrameSectionBinding.BindSceneLighting(encoder, scope.Sections, frame);

        encoder.DrawIndexed((uint)sub.IndexCount, 1, 0, 0, 0);
    }

    private void DisposeRhi()
    {
        List<Exception>? failures = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        foreach (List<SubMeshGpu> subs in _gpuByGfxObj.Values)
        {
            foreach (SubMeshGpu sub in subs)
            {
                Attempt(() => sub.VertexBuffer?.Dispose());
                Attempt(() => sub.IndexBuffer?.Dispose());
            }
        }

        _gpuByGfxObj.Clear();
        _slotBySurfaceAndWrap.Clear();

        Attempt(() => _alphaPipeline?.Dispose());
        _alphaPipeline = null;
        Attempt(() => _additivePipeline?.Dispose());
        _additivePipeline = null;

        if (failures is not null)
            throw new AggregateException("The sky renderer's RHI resources did not fully release.", failures);
    }
}
