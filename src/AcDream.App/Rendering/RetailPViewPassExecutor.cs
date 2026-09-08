using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Sky;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Rendering;
using AcDream.Core.Vfx;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface IEnvCellImmediateDrawSink
{
    void DrawImmediate(
        uint cellId,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive);
}


internal sealed class RetailPViewCellSource : IRetailPViewCellSource
{
    private readonly CellVisibility _cells;

    public RetailPViewCellSource(CellVisibility cells) =>
        _cells = cells ?? throw new ArgumentNullException(nameof(cells));

    public LoadedCell? Find(uint cellId) =>
        _cells.TryGetCell(cellId, out LoadedCell? cell) ? cell : null;
}

internal sealed partial class RetailPViewPassExecutor : IEnvCellImmediateDrawSink
{
    private readonly IWorldPassSurface _surface;
    private readonly IRenderFrameGlState _frameGlState;
    private readonly ClipFrame _clipFrame;
    private readonly TerrainModernRenderer? _terrain;
    private readonly EnvCellRenderer _envCells;
    private readonly WbDrawDispatcher _entities;
    private readonly SkyRenderer? _sky;
    private readonly ParticleSystem? _particles;
    private readonly ParticleRenderer? _particleRenderer;
    private readonly PortalDepthMaskRenderer? _portalDepthMask;
    private readonly RetailAlphaQueue _alpha;
    private readonly TerrainDrawDiagnosticsController _terrainDiagnostics;
    private readonly HashSet<uint> _noSceneParticleEntityIds = [];
    private readonly EnvCellAlphaDrawSource _envCellClipAlphaSource;
    private readonly EnvCellAlphaDrawSource _envCellBlendAlphaSource;

    internal delegate void RenderImmediateEnvCellRoute(
        uint cellId,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive);

    public RetailPViewPassExecutor(
        IWorldPassSurface surface,
        IRenderFrameGlState frameGlState,
        ClipFrame clipFrame,
        TerrainModernRenderer? terrain,
        EnvCellRenderer envCells,
        WbDrawDispatcher entities,
        SkyRenderer? sky,
        ParticleSystem? particles,
        ParticleRenderer? particleRenderer,
        PortalDepthMaskRenderer? portalDepthMask,
        RetailAlphaQueue alpha,
        TerrainDrawDiagnosticsController terrainDiagnostics)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _frameGlState = frameGlState
            ?? throw new ArgumentNullException(nameof(frameGlState));
        _clipFrame = clipFrame ?? throw new ArgumentNullException(nameof(clipFrame));
        _terrain = terrain;
        _envCells = envCells ?? throw new ArgumentNullException(nameof(envCells));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _sky = sky;
        _particles = particles;
        _particleRenderer = particleRenderer;
        _portalDepthMask = portalDepthMask;
        _alpha = alpha ?? throw new ArgumentNullException(nameof(alpha));
        _terrainDiagnostics = terrainDiagnostics
            ?? throw new ArgumentNullException(nameof(terrainDiagnostics));
        _envCellClipAlphaSource = new EnvCellAlphaDrawSource(
            _envCells.RenderTransparentOrdered,
            EnvCellTransparentRoute.Clip);
        _envCellBlendAlphaSource = new EnvCellAlphaDrawSource(
            _envCells.RenderTransparentOrdered,
            EnvCellTransparentRoute.Alpha);
    }

    public void BeginFrame()
    {
    }

    internal WbDrawDispatcher Dispatcher => _entities;

    internal (IGpuFrame Frame, IGpuPassEncoder Encoder) RequireWalkSubmission() =>
        _entities.RequireWalkSubmission();

    internal (int Width, int Height)? WalkAttachmentExtent =>
        _entities.WalkAttachmentExtent;

    public void AbortFrame()
    {
        List<Exception>? failures = null;
        TryAbort(_frameGlState.RestoreFrameDefaults);
        TryAbort(_noSceneParticleEntityIds.Clear);
        if (failures is { Count: > 0 })
            throw new AggregateException("Retail PView pass abort failed.", failures);

        void TryAbort(Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
    }

    public ClipFrameAssembly BeginWalkClipFrame(
        bool outdoorRoot,
        ClipFrameAssembly reuseAssembly) =>
        ClipFrameAssembler.BeginWalkFrame(_clipFrame, outdoorRoot, reuseAssembly);

    public void PrepareClipFrame() =>
        _surface.PrepareClipFrame();

    public void PrepareCellBatches(
        RetailPViewFrameInput frame,
        HashSet<uint> visibleCellIds) =>
        _envCells.PrepareRenderBatches(
            frame.ViewProjection,
            frame.CameraWorldPosition,
            filter: visibleCellIds,
            centerLbX: frame.RenderCenterLbX,
            centerLbY: frame.RenderCenterLbY,
            renderRadius: frame.RenderRadius);

    public void DrawOpaqueCellShells(HashSet<uint> cellIds) =>
        _envCells.Render(WbRenderPass.Opaque, cellIds);

    public bool CellHasTransparentShell(uint cellId) =>
        _envCells.CellHasTransparent(cellId);

    public void DrawTransparentCellShellsOrdered(IReadOnlyList<uint> cellIds) =>
        _envCells.RenderTransparentOrdered(cellIds);

    internal void SubmitOrDrawTransparentCellShell(uint cellId)
    {
        bool detailSurfaceActive = _envCells.TransparentDetailEnabled;
        EnvCellTransparentRoute routes = _envCells.GetTransparentRoutes(
            cellId,
            detailSurfaceActive);
        DispatchTransparentCellShell(
            cellId,
            routes,
            detailSurfaceActive,
            _alpha,
            _envCellClipAlphaSource,
            _envCellBlendAlphaSource,
            this);
    }

    private readonly List<uint> _singleCellListScratch = new(1);

    private void DrawImmediateEnvCellRoute(
        uint cellId,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive)
    {
        _singleCellListScratch.Clear();
        _singleCellListScratch.Add(cellId);
        _envCells.RenderTransparentOrdered(
            _singleCellListScratch,
            route,
            detailSurfaceActive);
    }

    void IEnvCellImmediateDrawSink.DrawImmediate(
        uint cellId,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive) =>
        DrawImmediateEnvCellRoute(cellId, route, detailSurfaceActive);

    internal static void DispatchTransparentCellShell(
        uint cellId,
        EnvCellTransparentRoute routes,
        bool detailSurfaceActive,
        RetailAlphaQueue queue,
        EnvCellAlphaDrawSource clipSource,
        EnvCellAlphaDrawSource alphaSource,
        IEnvCellImmediateDrawSink renderImmediate)
    {
        if ((routes & EnvCellTransparentRoute.Immediate) != 0)
        {
            renderImmediate.DrawImmediate(
                cellId,
                EnvCellTransparentRoute.Immediate,
                detailSurfaceActive);
        }

        if ((routes & EnvCellTransparentRoute.Clip) != 0)
        {
            int token = clipSource.AddPendingCellId(cellId);
            if (!queue.TryAppend(
                    RetailAlphaList.Clip,
                    clipSource,
                    token,
                    overrideClipmap: false))
            {
                clipSource.RollbackPendingCellId(token);
            }
        }

        if ((routes & EnvCellTransparentRoute.Alpha) != 0)
        {
            int token = alphaSource.AddPendingCellId(cellId);
            if (!queue.TryAppend(
                    RetailAlphaList.Alpha,
                    alphaSource,
                    token,
                    overrideClipmap: false))
            {
                alphaSource.RollbackPendingCellId(token);
            }
        }
    }


    internal static void DispatchTransparentCellShell(
        uint cellId,
        EnvCellTransparentRoute routes,
        bool detailSurfaceActive,
        RetailAlphaQueue queue,
        EnvCellAlphaDrawSource clipSource,
        EnvCellAlphaDrawSource alphaSource,
        RenderImmediateEnvCellRoute renderImmediate)
    {
        if ((routes & EnvCellTransparentRoute.Immediate) != 0)
        {
            renderImmediate(
                cellId,
                EnvCellTransparentRoute.Immediate,
                detailSurfaceActive);
        }

        if ((routes & EnvCellTransparentRoute.Clip) != 0)
        {
            int token = clipSource.AddPendingCellId(cellId);
            if (!queue.TryAppend(
                    RetailAlphaList.Clip,
                    clipSource,
                    token,
                    overrideClipmap: false))
            {
                clipSource.RollbackPendingCellId(token);
            }
        }

        if ((routes & EnvCellTransparentRoute.Alpha) != 0)
        {
            int token = alphaSource.AddPendingCellId(cellId);
            if (!queue.TryAppend(
                    RetailAlphaList.Alpha,
                    alphaSource,
                    token,
                    overrideClipmap: false))
            {
                alphaSource.RollbackPendingCellId(token);
            }
        }
    }

    internal delegate void RenderEnvCellsByRoute(
        IReadOnlyList<uint> cellIds,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive);

    internal sealed class EnvCellAlphaDrawSource : IRetailAlphaDrawSource
    {
        private readonly RenderEnvCellsByRoute _renderTransparentOrdered;
        private readonly EnvCellTransparentRoute _route;
        private readonly Action? _resetObserver;
        private readonly List<uint> _pendingCellIds = new();
        private readonly List<uint> _preparedCellIds = new();
        private readonly List<uint> _drawScratch = new();

        internal EnvCellAlphaDrawSource(
            RenderEnvCellsByRoute renderTransparentOrdered,
            EnvCellTransparentRoute route,
            Action? resetObserver = null)
        {
            _renderTransparentOrdered = renderTransparentOrdered
                ?? throw new ArgumentNullException(nameof(renderTransparentOrdered));
            _route = route;
            _resetObserver = resetObserver;
        }

        internal int AddPendingCellId(uint cellId)
        {
            int token = _pendingCellIds.Count;
            _pendingCellIds.Add(cellId);
            return token;
        }

        internal void RollbackPendingCellId(int token)
        {
            if (token != _pendingCellIds.Count - 1)
            {
                throw new InvalidOperationException(
                    "Only the just-reserved EnvCell alpha token can be rolled back.");
            }
            _pendingCellIds.RemoveAt(token);
        }

        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
        {
            _preparedCellIds.Clear();
            for (int i = 0; i < tokens.Length; i++)
                _preparedCellIds.Add(_pendingCellIds[tokens[i]]);
        }

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
        {
            if (drawCount <= 0)
                return;
            _drawScratch.Clear();
            for (int i = 0; i < drawCount; i++)
                _drawScratch.Add(_preparedCellIds[firstPreparedDraw + i]);
            _renderTransparentOrdered(
                _drawScratch,
                _route,
                detailSurfaceActive: false);
        }

        public void ResetAlphaSubmissions()
        {
            _pendingCellIds.Clear();
            _preparedCellIds.Clear();
            _drawScratch.Clear();
            _resetObserver?.Invoke();
        }

        internal int PendingCount => _pendingCellIds.Count;
        internal int PendingCapacity => _pendingCellIds.Capacity;
        internal int PreparedCapacity => _preparedCellIds.Capacity;
        internal int DrawCapacity => _drawScratch.Capacity;
    }

    public void DrawWeatherOnce(RetailPViewFrameInput frame)
    {
        if (!ShouldDrawWeatherOnce(frame.RenderSky, frame.RenderWeather, frame.PlayerCellId))
            return;

        _sky?.RenderWeather(
            frame.Camera,
            frame.CameraWorldPosition,
            frame.DayFraction,
            frame.ActiveDayGroup,
            frame.SkyKeyframe,
            frame.EnvironOverrideActive);

        if (_particles is not null && _particleRenderer is not null)
        {
            _particleRenderer.Draw(
                frame.Camera,
                frame.CameraWorldPosition,
                ParticleRenderPass.SkyPostScene);
        }
    }

    internal static bool ShouldDrawWeatherOnce(
        bool renderSky, bool renderWeather, uint playerCellId)
        => renderSky && renderWeather && (playerCellId & 0xFFFFu) < 0x100u;

    public void DrawLandscapeStaticParticles(
        RetailPViewFrameInput frame,
        uint cellId)
    {
        if (_particles is not null && _particleRenderer is not null)
        {
            _particleRenderer.DrawForCell(
                frame.Camera,
                frame.CameraWorldPosition,
                ParticleRenderPass.Scene,
                cellId,
                clipSlot: 0);
        }
    }

    internal ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareCellParticleAlpha(
        RetailPViewFrameInput frame,
        uint cellId)
    {
        if (_particles is null || _particleRenderer is null)
            return ReadOnlySpan<PreparedParticleAlphaSubmission>.Empty;

        return _particleRenderer.PrepareForCellAlpha(
            frame.Camera,
            frame.CameraWorldPosition,
            ParticleRenderPass.Scene,
            cellId,
            clipSlot: 0);
    }

    public void ClearInteriorDepth()
    {
        _surface.ClearInteriorDepth();
    }

    public int DrawExitPortalMask(
        RetailPViewFrameInput frame,
        uint cellId,
        ReadOnlySpan<Vector4> clipPlanes) =>
        DrawPortalDepthWrite(
            cellId,
            clipPlanes,
            frame,
            forceFarZ: frame.RootCell.IsOutdoorNode);

    public void DrawUnattachedSceneParticles(
        RetailPViewFrameInput frame,
        bool outdoorCells)
    {
        if (_particles is null || _particleRenderer is null)
            return;

        _particleRenderer.DrawForOwners(
            frame.Camera,
            frame.CameraWorldPosition,
            ParticleRenderPass.Scene,
            _noSceneParticleEntityIds,
            includeUnattached: true,
            clipSlot: 0,
            unattachedCellScope: outdoorCells
                ? UnattachedEmitterCellScope.OutdoorCells
                : UnattachedEmitterCellScope.InteriorCells);
    }

    public void FlushLandscapeAlpha() =>
        _alpha.Flush(RetailAlphaFlushSite.LandscapeFlush, 0f);

    internal void FlushBuildingAlpha() =>
        _alpha.Flush(RetailAlphaFlushSite.DrawBuilding, 0f);

    internal void FlushSortCellExitAlpha() =>
        _alpha.Flush(RetailAlphaFlushSite.SortCellExit, 0.75f);

    public void DrawCellParticles(
        RetailPViewFrameInput frame,
        uint cellId)
    {
        if (_particles is null || _particleRenderer is null)
            return;

        _particleRenderer.DrawForCell(
            frame.Camera,
            frame.CameraWorldPosition,
            ParticleRenderPass.Scene,
            cellId,
            clipSlot: 0);
    }

    private int DrawPortalDepthWrite(
        uint cellId,
        ReadOnlySpan<Vector4> clipPlanes,
        RetailPViewFrameInput frame,
        bool forceFarZ,
        int? onlyPortalIndex = null)
    {
        if (_portalDepthMask is null)
            return 0;
        LoadedCell? cell = frame.Cells.Find(cellId);
        if (cell is null)
            return 0;

        int submitted = 0;
        Span<Vector3> world = stackalloc Vector3[32];
        for (int index = 0; index < cell.Portals.Count; index++)
        {
            if (onlyPortalIndex.HasValue && index != onlyPortalIndex.Value)
                continue;
            if (cell.Portals[index].OtherCellId != 0xFFFF)
                continue;
            if (index >= cell.PortalPolygons.Count)
                break;
            Vector3[] localVertices = cell.PortalPolygons[index];

            if (WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(localVertices))
                continue;

            int count = Math.Min(localVertices.Length, world.Length);
            for (int vertex = 0; vertex < count; vertex++)
            {
                world[vertex] = Vector3.Transform(
                    localVertices[vertex],
                    cell.WorldTransform);
            }

            submitted++;

            _portalDepthMask.DrawDepthFan(
                world[..count],
                frame.ViewProjection,
                clipPlanes,
                forceFarZ);
        }
        return submitted;
    }

}
