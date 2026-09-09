using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Wb;
using AcDream.Content;
using AcDream.Core.Meshing;
using AcDream.Core.Vfx;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using RuntimeParticleEmitter = AcDream.Core.Vfx.ParticleEmitter;

namespace AcDream.App.Rendering;

public sealed unsafe partial class ParticleRenderer : IDisposable
{
    private readonly record struct BatchKey(bool Additive);
    private readonly record struct ParticleDraw(BatchKey Key, ParticleInstance Instance);
    private readonly record struct MeshBatchKey(uint GfxObjId, int BatchIndex);
    private readonly record struct MeshParticleDraw(
        MeshBatchKey Key,
        ObjectRenderBatch Batch,
        MeshParticleInstance Instance);
    private readonly record struct DeferredParticleDraw(
        ParticleSubmissionKind Kind,
        ParticleDraw Billboard,
        MeshParticleDraw Mesh,
        Matrix4x4 ViewProjection);

    private readonly struct ParticleInstance
    {
        public readonly Vector3 Position;
        public readonly Vector3 AxisX;
        public readonly Vector3 AxisY;
        public readonly uint ColorArgb;
        public readonly AcDream.App.Rendering.Gpu.GpuTextureSlot TextureSlot;
        public readonly float DistanceSq;
        public readonly uint ClipSlot;

        public ParticleInstance(
            Vector3 position,
            Vector3 axisX,
            Vector3 axisY,
            uint colorArgb,
            AcDream.App.Rendering.Gpu.GpuTextureSlot textureSlot,
            float distanceSq,
            uint clipSlot)
        {
            Position = position;
            AxisX = axisX;
            AxisY = axisY;
            ColorArgb = colorArgb;
            TextureSlot = textureSlot;
            DistanceSq = distanceSq;
            ClipSlot = clipSlot;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BillboardGpuInstance
    {
        public Vector4 Center;
        public Vector4 AxisX;
        public Vector4 AxisY;
        public Vector4 Color;
        public uint TextureIndex;
        public uint ClipSlot;
    }

    /// <summary>Vertex-instance ABI shared with particle_mesh.vert.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MeshParticleGpuInstance
    {
        public Matrix4x4 Model;
        public Vector4 Color;
        public uint ClipSlot;
    }

    private readonly struct MeshParticleInstance
    {
        public readonly Matrix4x4 Model;
        public readonly uint ColorArgb;
        public readonly float DistanceSq;
        public readonly uint ClipSlot;

        public MeshParticleInstance(
            Matrix4x4 model,
            uint colorArgb,
            float distanceSq,
            uint clipSlot)
        {
            Model = model;
            ColorArgb = colorArgb;
            DistanceSq = distanceSq;
            ClipSlot = clipSlot;
        }
    }

    private readonly TextureCache? _textures;
    private readonly IDatReaderWriter? _dats;
    private readonly WbMeshAdapter? _meshAdapter;
    private readonly ParticleSystem _particles;
    private readonly RetailAlphaQueue? _alphaQueue;
    private readonly AlphaDrawSource _alphaSource;
    private readonly ReserveDeferredParticleDraw _reserveDeferredParticleDraw;
    private readonly DrawImmediateParticle _drawImmediateParticle;
    private DeferredParticleDraw _dispatchDeferredParticle;
    private readonly Dictionary<uint, ParticleGfxInfo> _particleGfxInfoByGfxObj = new();
    private readonly Dictionary<int, ParticleGfxInfo> _particleGfxInfoByEmitter = new();
    private readonly Dictionary<uint, RetailParticleGeometryKind> _geometryKindByGfxObj = new();
    private readonly Dictionary<uint, uint?> _firstDegradeModeByGfxObj = new();
    private readonly Dictionary<uint, TranslucencyKind> _meshBlendBySurface = new();
    private readonly ParticleMeshReferenceTracker? _meshReferences;
    private readonly ParticleEmitterRetirementTracker _emitterRetirements;
    private RetryableResourceReleaseLedger? _disposeResources;
    private bool _disposing;
    private bool _disposed;
    private readonly HashSet<uint> _meshLoadRequestedThisFrame = new();
    private bool _dynamicFrameStarted;

    internal (int SetCount, long CapacityBytes) DynamicBufferDiagnostics => (0, 0);

    private BillboardGpuInstance[] _instanceScratch = new BillboardGpuInstance[256];
    private MeshParticleGpuInstance[] _meshInstanceScratch = new MeshParticleGpuInstance[256];

    private readonly List<ParticleDraw> _drawListScratch = new(64);
    private readonly List<ParticleInstance> _runScratch = new(64);
    private readonly List<MeshParticleDraw> _meshDrawListScratch = new(64);
    private readonly List<MeshParticleInstance> _meshRunScratch = new(64);
    private readonly List<ParticleSubmission> _submissionScratch = new(128);
    private readonly List<PreparedParticleAlphaSubmission> _preparedCellAlphaScratch = new(128);
    private readonly List<RuntimeParticleEmitter> _scopedEmitterScratch = new(64);
    private readonly List<DeferredParticleDraw> _deferredAlpha = new(128);
    private DeferredParticleDraw[] _preparedAlpha = new DeferredParticleDraw[256];
    private uint[] _preparedInstanceOffsets = new uint[256];
    private int _preparedAlphaCount;
    private readonly RetainedScratchCapacityPolicy _alphaScratchPolicy;

    internal long AlphaScratchBudgetBytes => _alphaScratchPolicy.BudgetBytes;
    internal long RetainedAlphaScratchBytes => checked(
        (long)_deferredAlpha.Capacity * Unsafe.SizeOf<DeferredParticleDraw>()
        + (long)_preparedAlpha.Length * Unsafe.SizeOf<DeferredParticleDraw>()
        + (long)_preparedInstanceOffsets.Length * sizeof(uint)
        + (long)_preparedCellAlphaScratch.Capacity
            * Unsafe.SizeOf<PreparedParticleAlphaSubmission>());

    internal (int Count, int Capacity, long RetainedBytes)
        PreparedCellAlphaScratchDiagnostics =>
        (
            _preparedCellAlphaScratch.Count,
            _preparedCellAlphaScratch.Capacity,
            checked((long)_preparedCellAlphaScratch.Capacity
                * Unsafe.SizeOf<PreparedParticleAlphaSubmission>())
        );

    private sealed class AlphaDrawSource(ParticleRenderer owner) : IRetailAlphaDrawSource
    {
        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
            => owner.PrepareDeferredAlphaDraws(tokens);

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
            => owner.DrawPreparedAlphaBatch(firstPreparedDraw, drawCount);

        public void ResetAlphaSubmissions()
            => owner.ResetDeferredAlpha();
    }

    internal delegate int ReserveDeferredParticleDraw();

    internal delegate void DrawImmediateParticle(
        Matrix4x4 viewProjection,
        ParticleSubmissionKind kind,
        int drawIndex,
        bool opaqueDepthState);

    /// <summary>
    /// Starts one render frame. Wb point-of-use recovery is limited to one
    /// request per missing GfxObj even though portal slicing may invoke Draw
    /// many times during the frame.
    /// </summary>
    public void BeginFrame(int frameSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);

        _dynamicFrameStarted = true;
        _meshLoadRequestedThisFrame.Clear();
        _emitterRetirements.RetryPending();
        _textures?.TickParticleTextureCache();
    }

    public void Draw(
        ICamera camera,
        Vector3 cameraWorldPos,
        ParticleRenderPass renderPass = ParticleRenderPass.Scene,
        Func<AcDream.Core.Vfx.ParticleEmitter, bool>? emitterFilter = null,
        uint clipSlot = 0)
    {
        if (camera is null)
            return;

        Matrix4x4.Invert(camera.View, out var invView);
        Vector3 cameraRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        Vector3 cameraUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        BuildDrawLists(
            cameraWorldPos,
            renderPass,
            cameraRight,
            cameraUp,
            emitterFilter,
            scopedEmitters: null,
            clipSlot);
        FinishDraw(camera, renderPass);
    }

    public void DrawForOwners(
        ICamera camera,
        Vector3 cameraWorldPos,
        ParticleRenderPass renderPass,
        IReadOnlySet<uint> attachedOwnerIds,
        bool includeUnattached = false,
        IReadOnlySet<uint>? excludedAttachedOwnerIds = null,
        uint clipSlot = 0,
        UnattachedEmitterCellScope unattachedCellScope = UnattachedEmitterCellScope.Any)
    {
        if (camera is null)
            return;

        _particles.CopyRenderableEmittersForOwners(
            renderPass,
            attachedOwnerIds,
            includeUnattached,
            _scopedEmitterScratch,
            excludedAttachedOwnerIds,
            unattachedCellScope);
        Matrix4x4.Invert(camera.View, out Matrix4x4 invView);
        Vector3 cameraRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        Vector3 cameraUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        BuildDrawLists(
            cameraWorldPos,
            renderPass,
            cameraRight,
            cameraUp,
            emitterFilter: null,
            _scopedEmitterScratch,
            clipSlot);
        FinishDraw(camera, renderPass);
    }

    public void DrawForCell(
        ICamera camera,
        Vector3 cameraWorldPos,
        ParticleRenderPass renderPass,
        uint cellId,
        uint clipSlot = 0)
    {
        if (camera is null)
            return;

        _particles.CopyRenderableEmittersInCell(renderPass, cellId, _scopedEmitterScratch);
        if (_scopedEmitterScratch.Count == 0)
            return;
        Matrix4x4.Invert(camera.View, out Matrix4x4 invView);
        Vector3 cameraRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        Vector3 cameraUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        BuildDrawLists(
            cameraWorldPos,
            renderPass,
            cameraRight,
            cameraUp,
            emitterFilter: null,
            _scopedEmitterScratch,
            clipSlot);
        FinishDraw(camera, renderPass);
    }

    internal ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareForCellAlpha(
        ICamera camera,
        Vector3 cameraWorldPos,
        ParticleRenderPass renderPass,
        uint cellId,
        uint clipSlot = 0)
    {
        _preparedCellAlphaScratch.Clear();
        if (camera is null)
            return CollectionsMarshal.AsSpan(_preparedCellAlphaScratch);

        _particles.CopyRenderableEmittersInCell(renderPass, cellId, _scopedEmitterScratch);
        if (_scopedEmitterScratch.Count == 0)
            return CollectionsMarshal.AsSpan(_preparedCellAlphaScratch);

        Matrix4x4.Invert(camera.View, out Matrix4x4 invView);
        Vector3 cameraRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        Vector3 cameraUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        BuildDrawLists(
            cameraWorldPos,
            renderPass,
            cameraRight,
            cameraUp,
            emitterFilter: null,
            _scopedEmitterScratch,
            clipSlot);

        if (_submissionScratch.Count == 0)
            return CollectionsMarshal.AsSpan(_preparedCellAlphaScratch);

        bool defers = renderPass == ParticleRenderPass.Scene && _alphaQueue?.IsCollecting == true;
        if (!defers)
        {
            DrawOrdered(camera);
            return CollectionsMarshal.AsSpan(_preparedCellAlphaScratch);
        }

        ParticleSubmissionOrdering.Sort(_submissionScratch);
        Matrix4x4 viewProjection = camera.View * camera.Projection;
        RetailAlphaQueue queue = _alphaQueue!;
        int retainedClipCount = 0;
        int retainedAlphaCount = 0;
        for (int i = 0; i < _submissionScratch.Count; i++)
        {
            ParticleSubmission submission = _submissionScratch[i];
            TranslucencyKind translucency = submission.Kind == ParticleSubmissionKind.Mesh
                ? _meshDrawListScratch[submission.DrawIndex].Batch.Translucency
                : default;
            uint colorArgb = submission.Kind == ParticleSubmissionKind.Mesh
                ? _meshDrawListScratch[submission.DrawIndex].Instance.ColorArgb
                : default;
            RetailAlphaMeshDecision decision = RouteParticleSubmission(
                submission.Kind, translucency, colorArgb);
            PreparedCellAlphaActions actions = ResolvePreparedCellAlphaActions(
                decision,
                ref retainedClipCount,
                ref retainedAlphaCount);

            if (actions.Retain)
            {
                _preparedCellAlphaScratch.Add(new PreparedParticleAlphaSubmission(
                    queue,
                    decision.List,
                    _alphaSource,
                    this,
                    submission.Kind,
                    submission.DrawIndex,
                    viewProjection,
                    decision.OverrideClipmap,
                    submission.DistanceSq,
                    submission.Sequence));
            }

            if (actions.DrawImmediate)
            {
                _drawImmediateParticle(
                    viewProjection,
                    submission.Kind,
                    submission.DrawIndex,
                    opaqueDepthState: decision.Action == RetailAlphaMeshAction.Immediate);
            }
        }

        return CollectionsMarshal.AsSpan(_preparedCellAlphaScratch);
    }

    internal readonly record struct PreparedCellAlphaActions(
        bool Retain,
        bool DrawImmediate);

    internal static PreparedCellAlphaActions ResolvePreparedCellAlphaActions(
        RetailAlphaMeshDecision decision,
        ref int retainedClipCount,
        ref int retainedAlphaCount)
    {
        bool requestsRetention = decision.Action is RetailAlphaMeshAction.Append
            or RetailAlphaMeshAction.AppendClipAndImmediate;
        bool retain = false;
        if (requestsRetention)
        {
            if (decision.List == RetailAlphaList.Clip)
            {
                if (retainedClipCount < RetailAlphaQueue.ListCapacity)
                {
                    retainedClipCount++;
                    retain = true;
                }
            }
            else if (retainedAlphaCount < RetailAlphaQueue.ListCapacity)
            {
                retainedAlphaCount++;
                retain = true;
            }
        }

        return new PreparedCellAlphaActions(
            retain,
            decision.Action is RetailAlphaMeshAction.Immediate
                or RetailAlphaMeshAction.AppendClipAndImmediate);
    }

    private void FinishDraw(ICamera camera, ParticleRenderPass renderPass)
    {
        if (_submissionScratch.Count == 0)
            return;

        bool defers = renderPass == ParticleRenderPass.Scene && _alphaQueue?.IsCollecting == true;
        if (defers)
            DeferToRetailAlphaQueue(camera);
        else
            DrawOrdered(camera);
    }

    internal static RetailAlphaMeshDecision RouteParticleSubmission(
        ParticleSubmissionKind kind, TranslucencyKind meshTranslucency, uint meshColorArgb)
    {
        bool isMesh = kind == ParticleSubmissionKind.Mesh;
        byte mask = kind == ParticleSubmissionKind.Billboard
            ? RetailAlphaMeshRouter.MaskAlphaFamily
            : RetailAlphaMeshRouter.MaskFromTranslucencyKind(meshTranslucency);
        bool materialHasAlpha = isMesh && ((meshColorArgb >> 24) & 0xFFu) != 0xFFu;

        return RetailAlphaMeshRouter.Route(
            currentlyDrawingSky: false,
            delayMask: RetailAlphaMeshRouter.DefaultDelayMask,
            detailSurfaceActive: false,
            multiPassAlpha: false,
            subsetMask: mask,
            materialHasAlpha: materialHasAlpha);
    }

    private void DeferToRetailAlphaQueue(ICamera camera)
    {
        RetailAlphaQueue queue = _alphaQueue!;
        Matrix4x4 viewProjection = camera.View * camera.Projection;
        for (int i = 0; i < _submissionScratch.Count; i++)
        {
            ParticleSubmission submission = _submissionScratch[i];

            DeferredParticleDraw deferred = submission.Kind == ParticleSubmissionKind.Billboard
                ? new DeferredParticleDraw(
                    submission.Kind,
                    _drawListScratch[submission.DrawIndex],
                    default,
                    viewProjection)
                : new DeferredParticleDraw(
                    submission.Kind,
                    default,
                    _meshDrawListScratch[submission.DrawIndex],
                    viewProjection);

            _dispatchDeferredParticle = deferred;
            TranslucencyKind translucency = submission.Kind == ParticleSubmissionKind.Mesh
                ? _meshDrawListScratch[submission.DrawIndex].Batch.Translucency
                : default;
            uint colorArgb = submission.Kind == ParticleSubmissionKind.Mesh
                ? _meshDrawListScratch[submission.DrawIndex].Instance.ColorArgb
                : default;
            DeferToRetailAlphaQueue(
                submission.Kind,
                translucency,
                colorArgb,
                queue,
                _alphaSource,
                _reserveDeferredParticleDraw,
                _drawImmediateParticle,
                viewProjection,
                submission.DrawIndex);
        }
    }

    internal static RetailAlphaMeshDecision DeferToRetailAlphaQueue(
        ParticleSubmissionKind kind,
        TranslucencyKind meshTranslucency,
        uint meshColorArgb,
        RetailAlphaQueue queue,
        IRetailAlphaDrawSource source,
        ReserveDeferredParticleDraw reserveDeferred,
        DrawImmediateParticle drawImmediate,
        Matrix4x4 viewProjection,
        int drawIndex)
    {
        RetailAlphaMeshDecision decision = RouteParticleSubmission(
            kind,
            meshTranslucency,
            meshColorArgb);
        switch (decision.Action)
        {
            case RetailAlphaMeshAction.Append:
            {
                int token = reserveDeferred();
                queue.TryAppend(decision.List, source, token, decision.OverrideClipmap);
                break;
            }
            case RetailAlphaMeshAction.Immediate:
                drawImmediate(viewProjection, kind, drawIndex, opaqueDepthState: true);
                break;
            case RetailAlphaMeshAction.AppendClipAndImmediate:
            {
                int token = reserveDeferred();
                queue.TryAppend(decision.List, source, token, decision.OverrideClipmap);
                drawImmediate(viewProjection, kind, drawIndex, opaqueDepthState: false);
                break;
            }
        }
        return decision;
    }

    private int ReserveDispatchDeferredParticle()
    {
        int token = _deferredAlpha.Count;
        _deferredAlpha.Add(_dispatchDeferredParticle);
        return token;
    }

    internal int ReservePreparedDispatchDeferredParticle(
        ParticleSubmissionKind kind,
        int drawIndex,
        Matrix4x4 viewProjection)
    {
        DeferredParticleDraw deferred = kind == ParticleSubmissionKind.Billboard
            ? new DeferredParticleDraw(
                kind,
                _drawListScratch[drawIndex],
                default,
                viewProjection)
            : new DeferredParticleDraw(
                kind,
                default,
                _meshDrawListScratch[drawIndex],
                viewProjection);
        int token = _deferredAlpha.Count;
        _deferredAlpha.Add(deferred);
        return token;
    }

    internal void RollbackPreparedDispatchDeferredParticle(int token)
    {
        int tail = _deferredAlpha.Count - 1;
        if (token != tail)
        {
            throw new InvalidOperationException(
                $"Prepared particle rollback must target tail token {tail}, not {token}.");
        }

        _deferredAlpha.RemoveAt(tail);
    }

    private void DrawOrdered(ICamera camera)
    {
        DrawOrderedRhi(camera);
    }

    private void PrepareDeferredAlphaDraws(ReadOnlySpan<int> tokens)
    {
        if (tokens.Length == 0)
            return;
        PrepareDeferredAlphaDrawsRhi(tokens);
    }

    private void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
    {
        if (drawCount <= 0)
            return;
        if (firstPreparedDraw < 0
            || firstPreparedDraw > _preparedAlphaCount - drawCount)
            throw new ArgumentOutOfRangeException(nameof(firstPreparedDraw));
        DrawPreparedAlphaBatchRhi(firstPreparedDraw, drawCount);
    }

    private void ResetDeferredAlpha()
    {
        int observedCount = Math.Max(
            _deferredAlpha.Count,
            _preparedAlphaCount);
        _deferredAlpha.Clear();
        _preparedCellAlphaScratch.Clear();
        _preparedAlphaCount = 0;
        int currentCapacity = Math.Max(
            _deferredAlpha.Capacity,
            Math.Max(
                _preparedAlpha.Length,
                _preparedInstanceOffsets.Length));
        int bytesPerDraw = checked(
            2 * Unsafe.SizeOf<DeferredParticleDraw>() + sizeof(uint));
        int targetCapacity = _alphaScratchPolicy.ObserveAndSelectCapacity(
            currentCapacity,
            observedCount,
            bytesPerDraw,
            minimumCapacity: 256,
            growthQuantum: 256);
        if (targetCapacity >= currentCapacity)
            return;

        _deferredAlpha.Capacity = targetCapacity;
        Array.Resize(ref _preparedAlpha, targetCapacity);
        Array.Resize(ref _preparedInstanceOffsets, targetCapacity);
    }

    private void BuildDrawLists(
        Vector3 cameraWorldPos,
        ParticleRenderPass renderPass,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Func<AcDream.Core.Vfx.ParticleEmitter, bool>? emitterFilter,
        IReadOnlyList<RuntimeParticleEmitter>? scopedEmitters,
        uint clipSlot)
    {
        var draws = _drawListScratch;
        draws.Clear();
        _meshDrawListScratch.Clear();
        _submissionScratch.Clear();
        int sequence = 0;
        if (scopedEmitters is not null)
        {
            for (int i = 0; i < scopedEmitters.Count; i++)
            {
                AppendEmitterDraws(
                    scopedEmitters[i],
                    cameraWorldPos,
                    cameraRight,
                    cameraUp,
                    clipSlot,
                    ref sequence);
            }
            return;
        }

        foreach (RuntimeParticleEmitter emitter in _particles.EnumerateRenderableEmitters(renderPass))
        {
            if (emitterFilter is null || emitterFilter(emitter))
                AppendEmitterDraws(
                    emitter,
                    cameraWorldPos,
                    cameraRight,
                    cameraUp,
                    clipSlot,
                    ref sequence);
        }
    }

    private void AppendEmitterDraws(
        RuntimeParticleEmitter em,
        Vector3 cameraWorldPos,
        Vector3 cameraRight,
        Vector3 cameraUp,
        uint clipSlot,
        ref int sequence)
    {
        List<ParticleDraw> draws = _drawListScratch;
        ParticleGfxInfo gfxInfo = default;
        bool gfxInfoResolved = false;

        for (int idx = 0; idx < em.Particles.Length; idx++)
        {
            ref Particle p = ref em.Particles[idx];
            if (!p.Alive)
                continue;
            Vector3 pos = p.Position;
            uint gfxObjId = em.Desc.HwGfxObjId != 0 ? em.Desc.HwGfxObjId : em.Desc.GfxObjId;
            if (gfxObjId != 0
                && ResolveGeometryKind(gfxObjId) == RetailParticleGeometryKind.FullMesh
                && TryAppendMeshDraws(
                    em,
                    p,
                    gfxObjId,
                    cameraWorldPos,
                    clipSlot,
                    ref sequence))
            {
                continue;
            }

            if (!gfxInfoResolved)
            {
                gfxInfo = ResolveParticleGfxInfo(em);
                gfxInfoResolved = true;
            }
            Quaternion orientation = ParticleOrientation(em, p);
            Vector3 authoredSortPoint = p.Position
                + Vector3.Transform(gfxInfo.SortCenter * p.Size, orientation);
            float distSq = Vector3.DistanceSquared(
                authoredSortPoint,
                cameraWorldPos);
            bool additive = gfxInfo.HasMaterial
                ? gfxInfo.Additive
                : (em.Desc.Flags & EmitterFlags.Additive) != 0;
            var key = new BatchKey(additive);
            Vector3 axisX;
            Vector3 axisY;
            Vector3 toViewer = cameraWorldPos - pos;
            float toViewerLength = toViewer.Length();
            if (gfxInfo.IsBillboard)
            {
                Vector3 xd;
                Vector3 yd;
                if (toViewerLength > 1e-3f)
                {
                    (xd, yd) = RetailParticleFacing.OrientQuad(
                        2u,
                        Quaternion.Identity,
                        Vector3.UnitX,
                        Vector3.UnitY,
                        toViewer / toViewerLength,
                        cameraRight,
                        cameraUp);
                }
                else
                {
                    (xd, yd) = (cameraRight, cameraUp);
                }

                pos += (xd * gfxInfo.CenterOffset.X
                      + yd * gfxInfo.CenterOffset.Z) * p.Size;
                axisX = xd * (gfxInfo.Size.X * p.Size);
                axisY = yd * (gfxInfo.Size.Y * p.Size);
            }
            else
            {
                if (RetailParticleFacing.Faces(gfxInfo.DegradeMode)
                    && toViewerLength > 1e-3f)
                {
                    (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
                        gfxInfo.DegradeMode,
                        orientation,
                        gfxInfo.AxisX,
                        gfxInfo.AxisY,
                        toViewer / toViewerLength,
                        cameraRight,
                        cameraUp);
                    Vector3 localNormal = Vector3.Cross(gfxInfo.AxisX, gfxInfo.AxisY);
                    Vector3 spunNormal = Vector3.Cross(xd, yd);
                    if (spunNormal.LengthSquared() > 1e-10f)
                        spunNormal = Vector3.Normalize(spunNormal);
                    Vector3 c = gfxInfo.CenterOffset;
                    pos += (xd * Vector3.Dot(c, gfxInfo.AxisX)
                          + yd * Vector3.Dot(c, gfxInfo.AxisY)
                          + spunNormal * Vector3.Dot(c, localNormal)) * p.Size;
                    axisX = xd * (gfxInfo.Size.X * p.Size);
                    axisY = yd * (gfxInfo.Size.Y * p.Size);
                }
                else
                {
                    pos += Vector3.Transform(gfxInfo.CenterOffset * p.Size, orientation);
                    axisX = Vector3.Transform(gfxInfo.AxisX, orientation) * (gfxInfo.Size.X * p.Size);
                    axisY = Vector3.Transform(gfxInfo.AxisY, orientation) * (gfxInfo.Size.Y * p.Size);
                }
            }

            int drawIndex = draws.Count;
            draws.Add(new ParticleDraw(
                key,
                new ParticleInstance(
                    pos,
                    axisX,
                    axisY,
                    p.ColorArgb,
                    gfxInfo.TextureSlot,
                    distSq,
                    clipSlot)));
            _submissionScratch.Add(new ParticleSubmission(
                ParticleSubmissionKind.Billboard,
                drawIndex,
                distSq,
                sequence++));
        }
    }

    private bool TryAppendMeshDraws(
        AcDream.Core.Vfx.ParticleEmitter emitter,
        Particle particle,
        uint gfxObjId,
        Vector3 cameraWorldPosition,
        uint clipSlot,
        ref int sequence)
    {
        if (_meshAdapter is null || !MeshParticlesAvailable)
            return true;

        _meshReferences!.Register(emitter.Handle, gfxObjId);
        ObjectRenderData? renderData = _meshAdapter.TryGetRenderData(gfxObjId);
        if (renderData is null)
        {
            if (_meshLoadRequestedThisFrame.Add(gfxObjId))
                _meshAdapter.EnsureLoaded(gfxObjId);
            return true;
        }

        Quaternion orientation = ParticleOrientation(emitter, particle);
        Matrix4x4 model = Matrix4x4.CreateScale(particle.Size)
            * Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(particle.Position);
        Vector3 worldSortCenter = Vector3.Transform(renderData.SortCenter, model);
        float distanceSq = Vector3.DistanceSquared(worldSortCenter, cameraWorldPosition);
        var instance = new MeshParticleInstance(
            model,
            particle.ColorArgb,
            distanceSq,
            clipSlot);

        for (int batchIndex = 0; batchIndex < renderData.Batches.Count; batchIndex++)
        {
            ObjectRenderBatch batch = renderData.Batches[batchIndex];
            if (batch.IndexCount <= 0 || !batch.TextureSlot.IsAssigned)
                continue;

            int drawIndex = _meshDrawListScratch.Count;
            _meshDrawListScratch.Add(new MeshParticleDraw(
                new MeshBatchKey(gfxObjId, batchIndex),
                batch,
                instance));
            _submissionScratch.Add(new ParticleSubmission(
                ParticleSubmissionKind.Mesh,
                drawIndex,
                distanceSq,
                sequence++));
        }

        return true;
    }

    private const uint NoTextureSlot = 0xFFFFFFFFu;

    private static void WriteBillboardGpuInstance(
        ref BillboardGpuInstance destination,
        ParticleInstance particle)
    {
        destination = new BillboardGpuInstance
        {
            Center = new Vector4(particle.Position, 0f),
            AxisX = new Vector4(particle.AxisX, 0f),
            AxisY = new Vector4(particle.AxisY, 0f),
            Color = new Vector4(
                ((particle.ColorArgb >> 16) & 0xFF) / 255f,
                ((particle.ColorArgb >> 8) & 0xFF) / 255f,
                (particle.ColorArgb & 0xFF) / 255f,
                ((particle.ColorArgb >> 24) & 0xFF) / 255f),
            TextureIndex = particle.TextureSlot.IsAssigned
                ? particle.TextureSlot.Index
                : NoTextureSlot,
            ClipSlot = particle.ClipSlot,
        };
    }

    private static void WriteMeshGpuInstance(
        ref MeshParticleGpuInstance destination,
        MeshParticleInstance instance)
    {
        destination = new MeshParticleGpuInstance
        {
            Model = instance.Model,
            Color = new Vector4(
                ((instance.ColorArgb >> 16) & 0xFF) / 255f,
                ((instance.ColorArgb >> 8) & 0xFF) / 255f,
                (instance.ColorArgb & 0xFF) / 255f,
                ((instance.ColorArgb >> 24) & 0xFF) / 255f),
            ClipSlot = instance.ClipSlot,
        };
    }

    private TranslucencyKind ResolveMeshBlend(ObjectRenderBatch batch)
    {
        uint surfaceId = batch.Key.SurfaceId;
        if (surfaceId == 0 || _dats is null)
            return batch.IsAdditive ? TranslucencyKind.Additive : TranslucencyKind.AlphaBlend;
        if (_meshBlendBySurface.TryGetValue(surfaceId, out TranslucencyKind blend))
            return blend;

        blend = RetailParticleBlendResolver.Resolve(
            surfaceId,
            batch.IsAdditive,
            id => _dats.Get<Surface>(id),
            Console.Error.WriteLine);
        _meshBlendBySurface[surfaceId] = blend;
        return blend;
    }

    private RetailParticleGeometryKind ResolveGeometryKind(uint gfxObjId)
    {
        if (_geometryKindByGfxObj.TryGetValue(gfxObjId, out RetailParticleGeometryKind kind))
            return kind;

        kind = RetailParticleGeometryClassifier.Classify(
            ResolveFirstDegradeMode(gfxObjId));
        _geometryKindByGfxObj[gfxObjId] = kind;
        return kind;
    }

    private uint? ResolveFirstDegradeMode(uint gfxObjId)
    {
        if (_firstDegradeModeByGfxObj.TryGetValue(gfxObjId, out uint? mode))
            return mode;

        try
        {
            if (_dats?.Get<GfxObj>(gfxObjId) is { } gfx
                && gfx.Flags.HasFlag(GfxObjFlags.HasDIDDegrade)
                && gfx.DIDDegrade != 0
                && _dats.Get<GfxObjDegradeInfo>(gfx.DIDDegrade) is { Degrades.Count: > 0 } degrade)
            {
                mode = degrade.Degrades[0].DegradeMode;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[particle-geometry] Failed to decode GfxObj 0x{gfxObjId:X8} degrade metadata: {ex.Message}");
        }

        _firstDegradeModeByGfxObj[gfxObjId] = mode;
        return mode;
    }

    private void OnEmitterDied(int handle)
    {
        _emitterRetirements.BeginRetirement(handle);
    }

    private ParticleGfxInfo ResolveParticleGfxInfo(RuntimeParticleEmitter emitter)
    {
        if (_textures is null)
            return ParticleGfxInfo.Default;
        if (_particleGfxInfoByEmitter.TryGetValue(emitter.Handle, out ParticleGfxInfo resolved))
            return resolved;

        EmitterDesc desc = emitter.Desc;

        if (desc.TextureSurfaceId != 0)
        {
            resolved = ParticleGfxInfo.Billboard(
                _textures.AcquireParticleTexture(emitter.Handle, desc.TextureSurfaceId),
                Vector2.One,
                Vector3.Zero,
                Vector3.Zero,
                additive: (desc.Flags & EmitterFlags.Additive) != 0,
                hasMaterial: false,
                surfaceId: desc.TextureSurfaceId);
            _particleGfxInfoByEmitter.Add(emitter.Handle, resolved);
            return resolved;
        }

        uint gfxObjId = desc.HwGfxObjId != 0 ? desc.HwGfxObjId : desc.GfxObjId;
        if (gfxObjId == 0 || _dats is null)
            return ParticleGfxInfo.Default;

        if (!_particleGfxInfoByGfxObj.TryGetValue(gfxObjId, out var info))
        {
            info = ReadParticleGfxInfo(gfxObjId);
            _particleGfxInfoByGfxObj[gfxObjId] = info;
        }

        resolved = info;
        if (info.SurfaceId != 0)
        {
            resolved = info with
            {
                TextureSlot = _textures.AcquireParticleTexture(
                    emitter.Handle,
                    info.SurfaceId),
            };
        }
        _particleGfxInfoByEmitter.Add(emitter.Handle, resolved);
        return resolved;
    }

    private ParticleGfxInfo ReadParticleGfxInfo(uint gfxObjId)
    {
        try
        {
            var gfx = _dats?.Get<GfxObj>(gfxObjId);
            if (gfx is null)
                return ParticleGfxInfo.Default;

            uint surfaceId = gfx.Surfaces.Count > 0 ? gfx.Surfaces[0].DataId : 0u;
            bool additive = false;
            if (surfaceId != 0)
            {
                var surface = _dats?.Get<Surface>(surfaceId);
                additive = surface is not null && surface.Type.HasFlag(SurfaceType.Additive);
            }
            return AuthoredParticleGfxInfo(
                gfx,
                texture: AcDream.App.Rendering.Gpu.GpuTextureSlot.Unassigned,
                additive,
                hasMaterial: surfaceId != 0,
                surfaceId: surfaceId,
                degradeMode: ResolveFirstDegradeMode(gfxObjId) ?? 0u);
        }
        catch
        {
            return ParticleGfxInfo.Default;
        }
    }

    private ParticleGfxInfo AuthoredParticleGfxInfo(
        GfxObj gfx,
        AcDream.App.Rendering.Gpu.GpuTextureSlot texture,
        bool additive,
        bool hasMaterial,
        uint surfaceId,
        uint degradeMode)
    {
        if (gfx.VertexArray.Vertices.Count == 0)
            return ParticleGfxInfo.Billboard(
                texture,
                Vector2.One,
                Vector3.Zero,
                gfx.SortCenter,
                additive,
                hasMaterial,
                surfaceId);

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var (_, v) in gfx.VertexArray.Vertices)
        {
            min = Vector3.Min(min, v.Origin);
            max = Vector3.Max(max, v.Origin);
        }

        var size = max - min;
        var center = (min + max) * 0.5f;
        if (IsPointSprite(gfx))
        {
            float sx = FallbackParticleExtent(size.X) * 0.9f;
            float sy = FallbackParticleExtent(size.Z) * 0.9f;
            return ParticleGfxInfo.Billboard(
                texture,
                new Vector2(sx, sy),
                center,
                gfx.SortCenter,
                additive,
                hasMaterial,
                surfaceId);
        }

        Vector3 axisX;
        Vector3 axisY;
        Vector2 planeSize;
        if (size.Y > size.X && size.Y > size.Z)
        {
            if (size.X > size.Z)
            {
                axisX = Vector3.UnitX;
                axisY = Vector3.UnitY;
                planeSize = new Vector2(size.X, size.Y);
            }
            else
            {
                axisX = Vector3.UnitY;
                axisY = Vector3.UnitZ;
                planeSize = new Vector2(size.Y, size.Z);
            }
        }
        else if (size.X > size.Y && size.X > size.Z)
        {
            if (size.Z > size.Y)
            {
                axisX = Vector3.UnitX;
                axisY = Vector3.UnitZ;
                planeSize = new Vector2(size.X, size.Z);
            }
            else
            {
                axisX = Vector3.UnitX;
                axisY = Vector3.UnitY;
                planeSize = new Vector2(size.X, size.Y);
            }
        }
        else
        {
            if (size.X > size.Y)
            {
                axisX = Vector3.UnitX;
                axisY = Vector3.UnitZ;
                planeSize = new Vector2(size.X, size.Z);
            }
            else
            {
                axisX = Vector3.UnitY;
                axisY = Vector3.UnitZ;
                planeSize = new Vector2(size.Y, size.Z);
            }
        }

        planeSize.X = FallbackParticleExtent(planeSize.X);
        planeSize.Y = FallbackParticleExtent(planeSize.Y);
        return new ParticleGfxInfo(
            texture,
            planeSize,
            axisX,
            axisY,
            center,
            gfx.SortCenter,
            false,
            additive,
            hasMaterial,
            surfaceId,
            degradeMode);
    }

    private bool IsPointSprite(GfxObj gfx)
        => ResolveFirstDegradeMode(gfx.Id) == 2u;

    private static float FallbackParticleExtent(float value)
        => value > 1e-4f ? Math.Clamp(value, 1e-4f, 10_000f) : 1f;

    private static Quaternion ParticleOrientation(AcDream.Core.Vfx.ParticleEmitter em, Particle p)
    {
        Quaternion orientation = (em.Desc.Flags & EmitterFlags.AttachLocal) != 0
            ? em.AnchorRot
            : p.SpawnRotation;

        if (em.Desc.Type is AcDream.Core.Vfx.ParticleType.ParabolicLVGAGR
            or AcDream.Core.Vfx.ParticleType.ParabolicLVLALR
            or AcDream.Core.Vfx.ParticleType.ParabolicGVGAGR)
        {
            Vector3 angular = p.C * p.Age;
            float radians = angular.Length();
            if (radians > 1e-6f)
                orientation = Quaternion.Normalize(orientation * Quaternion.CreateFromAxisAngle(angular / radians, radians));
        }

        return orientation;
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposing = true;
        try
        {
            if (_disposeResources is null)
            {
                var releases = new List<(string Name, Action Release)>();
                BuildDisposeReleases(releases);
                _disposeResources = new RetryableResourceReleaseLedger(releases);
            }

            ResourceReleaseAttempt attempt = _disposeResources.Advance();
            if (!_disposeResources.IsComplete)
            {
                throw attempt.ToException(
                    "One or more particle renderer resources could not be released.");
            }

            CompleteDispose();
            _disposeResources = null;
            _disposed = true;

            if (attempt.HasFailures)
            {
                throw attempt.ToException(
                    "Particle renderer resources released with exceptional committed outcomes.");
            }
        }
        finally
        {
            _disposing = false;
        }
    }

    private void BuildDisposeReleases(List<(string Name, Action Release)> releases)
    {
        releases.Add(("emitter-death-subscription", () =>
            _particles.EmitterDied -= OnEmitterDied));
        releases.Add(("emitter-resources", RetireEveryResolvedEmitter));
        if (_meshReferences is not null)
            releases.Add(("mesh-references", _meshReferences.Dispose));

        releases.Add(("rhi-resources", DisposeRhiResources));
    }

    private void RetireEveryResolvedEmitter()
    {
        int[] handles = [.. _particleGfxInfoByEmitter.Keys];
        for (int i = 0; i < handles.Length; i++)
            _emitterRetirements.BeginRetirement(handles[i]);
        _emitterRetirements.CompleteOrThrow();
    }

    private void CompleteDispose()
    {
        _dynamicFrameStarted = false;
        _particleGfxInfoByEmitter.Clear();
        _particleGfxInfoByGfxObj.Clear();
        _geometryKindByGfxObj.Clear();
        _firstDegradeModeByGfxObj.Clear();
        _meshBlendBySurface.Clear();
        _deferredAlpha.Clear();
        _preparedCellAlphaScratch.Clear();
    }

    private readonly record struct ParticleGfxInfo(
        AcDream.App.Rendering.Gpu.GpuTextureSlot TextureSlot,
        Vector2 Size,
        Vector3 AxisX,
        Vector3 AxisY,
        Vector3 CenterOffset,
        Vector3 SortCenter,
        bool IsBillboard,
        bool Additive,
        bool HasMaterial,
        uint SurfaceId,
        uint DegradeMode)
    {
        public static ParticleGfxInfo Default { get; } =
            Billboard(
                AcDream.App.Rendering.Gpu.GpuTextureSlot.Unassigned,
                Vector2.One,
                Vector3.Zero,
                Vector3.Zero,
                additive: false,
                hasMaterial: false,
                surfaceId: 0);

        public static ParticleGfxInfo Billboard(
            AcDream.App.Rendering.Gpu.GpuTextureSlot textureSlot,
            Vector2 size,
            Vector3 centerOffset,
            Vector3 sortCenter,
            bool additive,
            bool hasMaterial,
            uint surfaceId) =>
            new(
                textureSlot,
                size,
                Vector3.UnitX,
                Vector3.UnitY,
                centerOffset,
                sortCenter,
                true,
                additive,
                hasMaterial,
                surfaceId,
                DegradeMode: 2u);
    }
}
