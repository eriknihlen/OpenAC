using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using AcDream.Content;
using AcDream.Core.Vfx;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering;

public sealed unsafe partial class ParticleRenderer
{
    private readonly IGpuDevice? _device;
    private readonly ICurrentGpuFrameSource? _frames;
    private readonly IWorldPassScope? _scope;

    private IGpuPipeline? _billboardAlphaPipeline;
    private IGpuPipeline? _billboardAdditivePipeline;
    private IGpuPipeline? _meshOpaquePipeline;
    private IGpuPipeline? _meshAlphaPipeline;
    private IGpuPipeline? _meshAdditivePipeline;
    private IGpuPipeline? _meshInversePipeline;
    private IGpuBuffer? _quadVertexBuffer;
    private IGpuBuffer? _quadIndexBuffer;

    /// <summary>
    /// The unit quad both arms draw billboards from: XY in [-0.5, +0.5] with a
    /// matching UV, four vertices of two floats each twice over.
    /// </summary>
    private static readonly float[] QuadVertices =
    [
        -0.5f, -0.5f, 0f, 0f,
         0.5f, -0.5f, 1f, 0f,
         0.5f,  0.5f, 1f, 1f,
        -0.5f,  0.5f, 0f, 1f,
    ];

    private static readonly uint[] QuadIndices = [0, 1, 2, 0, 2, 3];

    private const uint QuadStrideBytes = 4 * sizeof(float);

    private static readonly uint MeshInstanceStrideBytes =
        (uint)sizeof(MeshParticleGpuInstance);

    internal static GpuVertexLayout BillboardVertexLayout { get; } = new(
        ImmutableArray.Create(
            new GpuVertexBinding(0, QuadStrideBytes, GpuVertexInputRate.Vertex),
            new GpuVertexBinding(
                1,
                (uint)sizeof(BillboardGpuInstance),
                GpuVertexInputRate.Instance)),
        ImmutableArray.Create(
            new GpuVertexAttribute(0, GpuVertexFormat.Float2, 0, Binding: 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float2, 8, Binding: 0),
            new GpuVertexAttribute(2, GpuVertexFormat.Float4, 0, Binding: 1),
            new GpuVertexAttribute(3, GpuVertexFormat.Float4, 16, Binding: 1),
            new GpuVertexAttribute(4, GpuVertexFormat.Float4, 32, Binding: 1),
            new GpuVertexAttribute(5, GpuVertexFormat.Float4, 48, Binding: 1),
            new GpuVertexAttribute(6, GpuVertexFormat.UInt1, 64, Binding: 1),
            new GpuVertexAttribute(7, GpuVertexFormat.UInt1, 68, Binding: 1)));

    internal static GpuVertexLayout MeshVertexLayout { get; } = new(
        ImmutableArray.Create(
            new GpuVertexBinding(
                0,
                GpuVertexLayout.WorldMesh.StrideBytes,
                GpuVertexInputRate.Vertex),
            new GpuVertexBinding(1, MeshInstanceStrideBytes, GpuVertexInputRate.Instance)),
        ImmutableArray.Create(
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0, Binding: 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12, Binding: 0),
            new GpuVertexAttribute(2, GpuVertexFormat.Float2, 24, Binding: 0),
            new GpuVertexAttribute(3, GpuVertexFormat.Float4, 0, Binding: 1),
            new GpuVertexAttribute(4, GpuVertexFormat.Float4, 16, Binding: 1),
            new GpuVertexAttribute(5, GpuVertexFormat.Float4, 32, Binding: 1),
            new GpuVertexAttribute(6, GpuVertexFormat.Float4, 48, Binding: 1),
            new GpuVertexAttribute(7, GpuVertexFormat.Float4, 64, Binding: 1),
            new GpuVertexAttribute(8, GpuVertexFormat.UInt1, 80, Binding: 1)));

    internal ParticleRenderer(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IWorldPassScope scope,
        ParticleSystem particles,
        TextureCache? textures = null,
        IDatReaderWriter? dats = null,
        WbMeshAdapter? meshAdapter = null,
        RetailAlphaQueue? alphaQueue = null,
        long? alphaScratchBudgetBytes = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _textures = textures;
        _dats = dats;
        _meshAdapter = meshAdapter;
        _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        _alphaQueue = alphaQueue;
        _alphaSource = new AlphaDrawSource(this);
        _reserveDeferredParticleDraw = ReserveDispatchDeferredParticle;
        _drawImmediateParticle = DrawImmediateParticleSubmissionRhi;
        long scratchBudget = alphaScratchBudgetBytes
            ?? AcDream.App.Rendering.Residency.AlphaScratchBudgetProfile.Create(
                AcDream.App.Rendering.Residency.ResidencyBudgetOptions.Default.AlphaScratchBytes)
                .ParticleBytes;
        _alphaScratchPolicy =
            new AcDream.App.Rendering.Residency.RetainedScratchCapacityPolicy(scratchBudget);
        if (_meshAdapter is not null)
        {
            _meshReferences = new ParticleMeshReferenceTracker(
                gfxObjId => _meshAdapter.IncrementRefCount(gfxObjId),
                gfxObjId => _meshAdapter.DecrementRefCount(gfxObjId));
        }
        _emitterRetirements = new ParticleEmitterRetirementTracker(
            handle => _meshReferences?.Release(handle),
            handle => _particleGfxInfoByEmitter.Remove(handle),
            handle => _textures?.ReleaseParticleTextureOwner(handle),
            error => Console.Error.WriteLine($"[particles] {error}"));

        try
        {
            CreateRhiResources(device, scope.SampleCount);
            _particles.EmitterDied += OnEmitterDied;
        }
        catch
        {
            DisposeRhiResources();
            throw;
        }
    }

    private bool MeshParticlesAvailable => _meshAlphaPipeline is not null;

    private void CreateRhiResources(IGpuDevice device, int sampleCount)
    {
        _billboardAlphaPipeline = CreateBillboardPipeline(
            device, "particle-billboard-alpha", GpuBlendMode.StraightAlpha, sampleCount);
        _billboardAdditivePipeline = CreateBillboardPipeline(
            device, "particle-billboard-additive", GpuBlendMode.Additive, sampleCount);

        ReadOnlySpan<byte> quadVertexBytes = MemoryMarshal.AsBytes<float>(QuadVertices);
        _quadVertexBuffer = device.CreateBuffer(new GpuBufferDescription(
            "particle-quad-vertices",
            quadVertexBytes.Length,
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        _quadVertexBuffer.Upload(0, quadVertexBytes);

        ReadOnlySpan<byte> quadIndexBytes = MemoryMarshal.AsBytes<uint>(QuadIndices);
        _quadIndexBuffer = device.CreateBuffer(new GpuBufferDescription(
            "particle-quad-indices",
            quadIndexBytes.Length,
            GpuBufferUsage.Index | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        _quadIndexBuffer.Upload(0, quadIndexBytes);

        // The mesh pipelines exist exactly when the GL arm's second shader would:
        // when a shared mesh arena is published to draw instanced GfxObjs from.
        if (_meshAdapter?.MeshManager?.GlobalBuffer is null)
            return;

        _meshOpaquePipeline = CreateMeshParticlePipeline(
            device, "particle-mesh-opaque", GpuBlendMode.None, depthWrite: true, sampleCount);
        _meshAlphaPipeline = CreateMeshParticlePipeline(
            device, "particle-mesh-alpha", GpuBlendMode.StraightAlpha, depthWrite: false, sampleCount);
        _meshAdditivePipeline = CreateMeshParticlePipeline(
            device, "particle-mesh-additive", GpuBlendMode.Additive, depthWrite: false, sampleCount);
        _meshInversePipeline = CreateMeshParticlePipeline(
            device, "particle-mesh-inverse", GpuBlendMode.InverseAlpha, depthWrite: false, sampleCount);
    }

    private static IGpuPipeline CreateBillboardPipeline(
        IGpuDevice device,
        string name,
        GpuBlendMode blend,
        int sampleCount) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = new GpuShaderSet("particle"),
            VertexLayout = BillboardVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = blend,
            Depth = new GpuDepthState(Test: true, Write: false, WorldDepthContract.WorldCompare),
            Cull = GpuCullMode.None,
            FrontFace = GpuFrontFace.CounterClockwise,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = sampleCount,
        });

    private static IGpuPipeline CreateMeshParticlePipeline(
        IGpuDevice device,
        string name,
        GpuBlendMode blend,
        bool depthWrite,
        int sampleCount) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = new GpuShaderSet("particle_mesh"),
            VertexLayout = MeshVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = blend,
            Depth = new GpuDepthState(Test: true, Write: depthWrite, WorldDepthContract.WorldCompare),
            Cull = GpuCullMode.None,
            FrontFace = GpuFrontFace.Clockwise,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = sampleCount,
        });

    private void DrawOrderedRhi(ICamera camera)
    {
        ParticleSubmissionOrdering.Sort(_submissionScratch);
        GlobalMeshBuffer? global = _meshAdapter?.MeshManager?.GlobalBuffer;
        Matrix4x4 viewProjection = camera.View * camera.Projection;
        IGpuPassEncoder encoder = _scope!.RequireEncoder();
        IGpuFrame frame = RequireRhiFrame();

        for (int i = 0; i < _submissionScratch.Count;)
        {
            ParticleSubmission submission = _submissionScratch[i];
            if (submission.Kind == ParticleSubmissionKind.Billboard)
            {
                BatchKey key = _drawListScratch[submission.DrawIndex].Key;
                _runScratch.Clear();
                do
                {
                    _runScratch.Add(_drawListScratch[submission.DrawIndex].Instance);
                    i++;
                    if (i >= _submissionScratch.Count)
                        break;
                    submission = _submissionScratch[i];
                }
                while (submission.Kind == ParticleSubmissionKind.Billboard
                    && _drawListScratch[submission.DrawIndex].Key == key);

                DrawInstancesRhi(encoder, frame, _runScratch, viewProjection, key.Additive);
                continue;
            }

            if (!MeshParticlesAvailable || global is null)
            {
                i++;
                continue;
            }

            MeshParticleDraw meshDraw = _meshDrawListScratch[submission.DrawIndex];
            MeshBatchKey meshKey = meshDraw.Key;
            ObjectRenderBatch batch = meshDraw.Batch;
            _meshRunScratch.Clear();
            do
            {
                _meshRunScratch.Add(_meshDrawListScratch[submission.DrawIndex].Instance);
                i++;
                if (i >= _submissionScratch.Count)
                    break;
                submission = _submissionScratch[i];
            }
            while (submission.Kind == ParticleSubmissionKind.Mesh
                && _meshDrawListScratch[submission.DrawIndex].Key == meshKey);

            int neededInstances = _meshRunScratch.Count;
            if (_meshInstanceScratch.Length < neededInstances)
                _meshInstanceScratch = new MeshParticleGpuInstance[neededInstances + 256];
            for (int instance = 0; instance < _meshRunScratch.Count; instance++)
            {
                WriteMeshGpuInstance(
                    ref _meshInstanceScratch[instance],
                    _meshRunScratch[instance]);
            }

            GpuRingAllocation instances = WriteVertexRing<MeshParticleGpuInstance>(
                frame,
                _meshInstanceScratch.AsSpan(0, neededInstances));
            DrawMeshBatchRhi(
                encoder,
                frame,
                global,
                batch,
                viewProjection,
                instances.Buffer,
                instances.OffsetBytes,
                (uint)_meshRunScratch.Count,
                firstInstance: 0,
                opaqueDepthState: false);
        }
    }

    private void DrawImmediateParticleSubmissionRhi(
        Matrix4x4 viewProjection,
        ParticleSubmissionKind kind,
        int drawIndex,
        bool opaqueDepthState)
    {
        IGpuPassEncoder encoder = _scope!.RequireEncoder();
        IGpuFrame frame = RequireRhiFrame();

        if (kind == ParticleSubmissionKind.Billboard)
        {
            ParticleDraw draw = _drawListScratch[drawIndex];
            _runScratch.Clear();
            _runScratch.Add(draw.Instance);
            DrawInstancesRhi(encoder, frame, _runScratch, viewProjection, draw.Key.Additive);
            return;
        }

        GlobalMeshBuffer? global = _meshAdapter?.MeshManager?.GlobalBuffer;
        if (!MeshParticlesAvailable || global is null)
            return;

        MeshParticleDraw meshDraw = _meshDrawListScratch[drawIndex];
        if (_meshInstanceScratch.Length < 1)
            _meshInstanceScratch = new MeshParticleGpuInstance[256];
        WriteMeshGpuInstance(ref _meshInstanceScratch[0], meshDraw.Instance);
        GpuRingAllocation instances = WriteVertexRing<MeshParticleGpuInstance>(
            frame,
            _meshInstanceScratch.AsSpan(0, 1));
        DrawMeshBatchRhi(
            encoder,
            frame,
            global,
            meshDraw.Batch,
            viewProjection,
            instances.Buffer,
            instances.OffsetBytes,
            instanceCount: 1,
            firstInstance: 0,
            opaqueDepthState: opaqueDepthState);
    }

    private void DrawInstancesRhi(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        List<ParticleInstance> instances,
        Matrix4x4 viewProjection,
        bool additive)
    {
        if (instances.Count == 0)
            return;

        if (_instanceScratch.Length < instances.Count)
            _instanceScratch = new BillboardGpuInstance[instances.Count + 256];
        for (int i = 0; i < instances.Count; i++)
            WriteBillboardGpuInstance(ref _instanceScratch[i], instances[i]);

        GpuRingAllocation ring = WriteVertexRing<BillboardGpuInstance>(
            frame,
            _instanceScratch.AsSpan(0, instances.Count));
        BindBillboardPipeline(
            encoder,
            frame,
            viewProjection,
            additive,
            ring.Buffer,
            ring.OffsetBytes);
        encoder.DrawIndexed(
            (uint)QuadIndices.Length,
            (uint)instances.Count,
            0,
            0,
            0);
    }

    /// <summary>
    /// Binds a billboard pipeline and immediately re-establishes both vertex
    /// sources and the index source. Every pipeline owns its own vertex array on
    /// GL, and attribute pointers plus the index binding are vertex-array state,
    /// so a pipeline switch silently drops them while storage bindings survive.
    /// </summary>
    private void BindBillboardPipeline(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        Matrix4x4 viewProjection,
        bool additive,
        IGpuBuffer instanceBuffer,
        uint instanceOffsetBytes)
    {
        encoder.BindPipeline(additive
            ? _billboardAdditivePipeline!
            : _billboardAlphaPipeline!);
        encoder.SetPushConstants(new GpuPushConstants
        {
            ViewProjection = viewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 0,
            LightDebug = 0,
            TextureIndexA = 0,
            TextureIndexB = 0,
            ParamA = 0f,
            ParamB = 0f,
        });
        encoder.BindVertexBuffer(0, _quadVertexBuffer!, 0);
        encoder.BindVertexBuffer(1, instanceBuffer, instanceOffsetBytes);
        encoder.BindIndexBuffer(_quadIndexBuffer!, 0, GpuIndexType.UInt32);
        WorldFrameSectionBinding.BindClipRegions(
            encoder,
            _scope!.Sections,
            frame);
    }

    private void DrawMeshBatchRhi(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        GlobalMeshBuffer global,
        ObjectRenderBatch batch,
        Matrix4x4 viewProjection,
        IGpuBuffer instanceBuffer,
        uint instanceOffsetBytes,
        uint instanceCount,
        uint firstInstance,
        bool opaqueDepthState = false)
    {
        if (instanceCount == 0)
            return;

        encoder.BindPipeline(PipelineForMeshBlend(ResolveMeshBlend(batch), opaqueDepthState));
        encoder.SetPushConstants(new GpuPushConstants
        {
            ViewProjection = viewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 0,
            LightDebug = 0,
            TextureIndexA = batch.TextureSlot.Index,
            TextureIndexB = 0,
            // uParamA is a float, so the array layer is widened here rather than
            // in the shader. Layers are small integers; the sampled value is
            // bit-identical to the GL arm's.
            ParamA = batch.TextureIndex,
            ParamB = 0f,
        });
        ApplyMeshCullModeRhi(encoder, batch.CullMode);
        encoder.BindVertexBuffer(
            0,
            global.VertexStore ?? throw new InvalidOperationException(
                "The shared mesh arena has no vertex store."),
            0);
        encoder.BindVertexBuffer(1, instanceBuffer, instanceOffsetBytes);
        encoder.BindIndexBuffer(
            global.IndexStore ?? throw new InvalidOperationException(
                "The shared mesh arena has no index store."),
            0,
            GpuIndexType.UInt16);
        WorldFrameSectionBinding.BindClipRegions(
            encoder,
            _scope!.Sections,
            frame);
        encoder.DrawIndexed(
            (uint)batch.IndexCount,
            instanceCount,
            (uint)batch.FirstIndex,
            (int)batch.BaseVertex,
            firstInstance);
    }

    internal readonly record struct MeshParticlePipelineState(
        GpuBlendMode Blend,
        GpuDepthState Depth);

    internal static MeshParticlePipelineState ResolveMeshParticlePipelineState(
        TranslucencyKind blend,
        bool opaqueDepthState) =>
        opaqueDepthState
            ? new MeshParticlePipelineState(
                GpuBlendMode.None,
                new GpuDepthState(Test: true, Write: true, WorldDepthContract.WorldCompare))
            : new MeshParticlePipelineState(
                blend switch
                {
                    TranslucencyKind.Additive => GpuBlendMode.Additive,
                    TranslucencyKind.InvAlpha => GpuBlendMode.InverseAlpha,
                    _ => GpuBlendMode.StraightAlpha,
                },
                new GpuDepthState(Test: true, Write: false, WorldDepthContract.WorldCompare));

    private IGpuPipeline PipelineForMeshBlend(
        TranslucencyKind blend,
        bool opaqueDepthState)
    {
        MeshParticlePipelineState state = ResolveMeshParticlePipelineState(
            blend,
            opaqueDepthState);
        return state.Blend switch
        {
            GpuBlendMode.None => _meshOpaquePipeline!,
            GpuBlendMode.Additive => _meshAdditivePipeline!,
            GpuBlendMode.InverseAlpha => _meshInversePipeline!,
            _ => _meshAlphaPipeline!,
        };
    }

    private static void ApplyMeshCullModeRhi(IGpuPassEncoder encoder, CullMode mode)
    {
        encoder.SetFrontFace(GpuFrontFace.Clockwise);
        encoder.SetCullMode(mode switch
        {
            CullMode.None => GpuCullMode.None,
            CullMode.Clockwise => GpuCullMode.Front,
            _ => GpuCullMode.Back,
        });
    }

    private void PrepareDeferredAlphaDrawsRhi(ReadOnlySpan<int> tokens)
    {
        IGpuFrame frame = RequireRhiFrame();
        int count = tokens.Length;
        if (_preparedAlpha.Length < count)
            Array.Resize(ref _preparedAlpha, count + 256);
        if (_preparedInstanceOffsets.Length < count)
            Array.Resize(ref _preparedInstanceOffsets, count + 256);
        if (_instanceScratch.Length < count)
            Array.Resize(ref _instanceScratch, count + 256);
        if (_meshInstanceScratch.Length < count)
            _meshInstanceScratch = new MeshParticleGpuInstance[count + 256];

        int billboardCount = 0;
        int meshCount = 0;
        for (int i = 0; i < count; i++)
        {
            DeferredParticleDraw deferred = _deferredAlpha[tokens[i]];
            _preparedAlpha[i] = deferred;
            if (deferred.Kind == ParticleSubmissionKind.Billboard)
            {
                _preparedInstanceOffsets[i] = (uint)billboardCount;
                WriteBillboardGpuInstance(
                    ref _instanceScratch[billboardCount++],
                    deferred.Billboard.Instance);
            }
            else
            {
                _preparedInstanceOffsets[i] = (uint)meshCount;
                WriteMeshGpuInstance(
                    ref _meshInstanceScratch[meshCount++],
                    deferred.Mesh.Instance);
            }
        }

        _preparedBillboardInstances = billboardCount > 0
            ? SectionOf(WriteVertexRing<BillboardGpuInstance>(
                frame,
                _instanceScratch.AsSpan(0, billboardCount)))
            : default;
        _preparedMeshInstances = meshCount > 0
            ? SectionOf(WriteVertexRing<MeshParticleGpuInstance>(
                frame,
                _meshInstanceScratch.AsSpan(0, meshCount)))
            : default;
        _preparedAlphaCount = count;
    }

    private void DrawPreparedAlphaBatchRhi(int firstPreparedDraw, int drawCount)
    {
        GlobalMeshBuffer? global = _meshAdapter?.MeshManager?.GlobalBuffer;
        IGpuPassEncoder encoder = _scope!.RequireEncoder();

        int i = firstPreparedDraw;
        int preparedEnd = firstPreparedDraw + drawCount;
        while (i < preparedEnd)
        {
            DeferredParticleDraw deferred = _preparedAlpha[i];
            if (deferred.Kind == ParticleSubmissionKind.Billboard)
            {
                BatchKey key = deferred.Billboard.Key;
                Matrix4x4 viewProjection = deferred.ViewProjection;
                uint baseInstance = _preparedInstanceOffsets[i];
                int runStart = i;
                do
                {
                    i++;
                    if (i >= preparedEnd)
                        break;
                    deferred = _preparedAlpha[i];
                }
                while (deferred.Kind == ParticleSubmissionKind.Billboard
                       && deferred.Billboard.Key == key
                       && deferred.ViewProjection == viewProjection);

                if (_preparedBillboardInstances.Buffer is { } billboards)
                {
                    BindBillboardPipeline(
                        encoder,
                        RequireRhiFrame(),
                        viewProjection,
                        key.Additive,
                        billboards,
                        _preparedBillboardInstances.OffsetBytes);
                    encoder.DrawIndexed(
                        (uint)QuadIndices.Length,
                        (uint)(i - runStart),
                        0,
                        0,
                        baseInstance);
                }

                continue;
            }

            if (!MeshParticlesAvailable || global is null)
            {
                i++;
                continue;
            }

            MeshBatchKey meshKey = deferred.Mesh.Key;
            ObjectRenderBatch batch = deferred.Mesh.Batch;
            Matrix4x4 meshViewProjection = deferred.ViewProjection;
            uint meshBaseInstance = _preparedInstanceOffsets[i];
            int meshRunStart = i;
            do
            {
                i++;
                if (i >= preparedEnd)
                    break;
                deferred = _preparedAlpha[i];
            }
            while (deferred.Kind == ParticleSubmissionKind.Mesh
                   && deferred.Mesh.Key == meshKey
                   && deferred.ViewProjection == meshViewProjection);

            if (_preparedMeshInstances.Buffer is { } meshInstances)
            {
                DrawMeshBatchRhi(
                    encoder,
                    RequireRhiFrame(),
                    global,
                    batch,
                    meshViewProjection,
                    meshInstances,
                    _preparedMeshInstances.OffsetBytes,
                    (uint)(i - meshRunStart),
                    meshBaseInstance,
                    opaqueDepthState: false);
            }
        }
    }

    private readonly record struct RhiVertexSection(IGpuBuffer? Buffer, uint OffsetBytes);

    private RhiVertexSection _preparedBillboardInstances;
    private RhiVertexSection _preparedMeshInstances;

    private static RhiVertexSection SectionOf(GpuRingAllocation allocation) =>
        new(allocation.Buffer, allocation.OffsetBytes);

    private static GpuRingAllocation WriteVertexRing<T>(IGpuFrame frame, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        int elementBytes = sizeof(T);
        int byteCount = Math.Max(data.Length * elementBytes, elementBytes);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, GpuRingUsage.Vertex);
        if (!data.IsEmpty)
            data.CopyTo(allocation.AsSpan<T>());
        return allocation;
    }

    private IGpuFrame RequireRhiFrame()
    {
        if (!_dynamicFrameStarted)
            throw new InvalidOperationException("BeginFrame must be called before drawing particles.");

        return _frames!.CurrentFrame
            ?? throw new InvalidOperationException(
                "ParticleRenderer requires an open IGpuFrame (see GpuDeviceFrameLifetime).");
    }

    private void DisposeRhiResources()
    {
        List<Exception>? failures = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        Attempt(() => _billboardAlphaPipeline?.Dispose());
        _billboardAlphaPipeline = null;
        Attempt(() => _billboardAdditivePipeline?.Dispose());
        _billboardAdditivePipeline = null;
        Attempt(() => _meshOpaquePipeline?.Dispose());
        _meshOpaquePipeline = null;
        Attempt(() => _meshAlphaPipeline?.Dispose());
        _meshAlphaPipeline = null;
        Attempt(() => _meshAdditivePipeline?.Dispose());
        _meshAdditivePipeline = null;
        Attempt(() => _meshInversePipeline?.Dispose());
        _meshInversePipeline = null;
        Attempt(() => _quadVertexBuffer?.Dispose());
        _quadVertexBuffer = null;
        Attempt(() => _quadIndexBuffer?.Dispose());
        _quadIndexBuffer = null;
        _preparedBillboardInstances = default;
        _preparedMeshInstances = default;

        if (failures is not null)
            throw new AggregateException("The particle renderer's RHI resources did not fully release.", failures);
    }
}
