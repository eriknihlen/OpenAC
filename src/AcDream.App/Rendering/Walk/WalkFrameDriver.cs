using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Walk;

internal readonly record struct WalkFrameStaticRecords(
    ArraySegment<RenderProjectionRecord> Records, uint TupleLandblockId)
{
    public static readonly WalkFrameStaticRecords Empty =
        new(ArraySegment<RenderProjectionRecord>.Empty, 0);
}

internal interface IWalkFrameWorldData
{
    WalkFrameStaticRecords GetCellObjects(uint cellId);

    WalkFrameStaticRecords GetOutdoorObjects(uint cellId);

    WalkFrameStaticRecords GetCellStatics(uint cellId);

    WalkFrameStaticRecords GetCellDynamics(uint cellId);

    WalkFrameStaticRecords GetOutdoorStatics(uint cellId);

    WalkFrameStaticRecords GetOutdoorDynamics(uint cellId);

    WalkFrameStaticRecords GetBuildingShellStatics(WalkBuilding building);

    Matrix4x4 GetBuildingWorldTransform(WalkBuilding building);
}

internal interface IWalkFrameLeafRenderer
{
    void DrawSky();

    void DrawLandCellBatch(
        IReadOnlyList<(uint LandblockId, int SideCellCount, int CellIndex)> cells);

    bool HasRenderableEmittersInCell(uint cellId);

    void DrawCellShell(uint cellId);

    ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareStaticParticles(uint cellId);

    ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareCellParticles(uint cellId);

    void ClearInteriorDepth();

    int DrawExitSeals();

    void FlushLandscape();

    void DrawPunchFan(WalkPolygon worldPolygon, int activeViewIndex);

    void AlphaBarrier();

    void FlushSortCellExit();
}

internal interface IWalkFrameDriverTrace
{
    void OnFlush(int commandCount, IReadOnlyList<WalkDrawStage> stages);
}

internal enum WalkFrameEventKind : byte
{
    StreamMark,

    AlphaSubmitMark,

    Sky,

    LandCell,

    CellShell,

    PunchFan,

    AlphaBarrier,

    LandscapeFlush,

    ClearInteriorDepth,

    ExitSeals,

    StaticParticles,

    CellParticles,

    SortCellExit,
}

internal interface IWalkLookInViewSource
{
    IReadOnlyList<uint> LookInCellTurns { get; }

    bool SphereVisibleInLookInTurn(
        int routeIndex,
        in Vector3 center,
        float radius,
        bool testSphere = true);

    string DescribeLookInTurn(
        int routeIndex,
        in Vector3 center,
        float radius) => "unavailable";
}

internal readonly record struct WalkLookInSlice(
    int PlaneStart,
    int PlaneCount,
    uint ClipSlot);

internal readonly record struct WalkLookInTurn(
    uint CellId,
    int SliceStart,
    int SliceCount);

internal readonly struct WalkFrameEvent
{
    private WalkFrameEvent(
        WalkFrameEventKind kind, int intArg, uint cellId, float floatArg, WalkPolygon? polygon)
    {
        Kind = kind;
        IntArg = intArg;
        CellId = cellId;
        FloatArg = floatArg;
        Polygon = polygon;
    }

    internal WalkFrameEventKind Kind { get; }

    internal int IntArg { get; }

    internal uint CellId { get; }

    internal float FloatArg { get; }

    internal WalkPolygon? Polygon { get; }

    internal static WalkFrameEvent Mark(int exclusiveEnd) =>
        new(WalkFrameEventKind.StreamMark, exclusiveEnd, 0, 0f, null);

    internal static WalkFrameEvent AlphaSubmitMark(int exclusiveEnd) =>
        new(WalkFrameEventKind.AlphaSubmitMark, exclusiveEnd, 0, 0f, null);

    internal static WalkFrameEvent Sky() =>
        new(WalkFrameEventKind.Sky, 0, 0, 0f, null);

    internal static WalkFrameEvent LandCell(uint landblockId, int sideCellCount, int cellIndex) =>
        new(WalkFrameEventKind.LandCell, (sideCellCount << 8) | cellIndex, landblockId, 0f, null);

    internal static WalkFrameEvent CellShell(uint cellId) =>
        new(WalkFrameEventKind.CellShell, 0, cellId, 0f, null);

    internal static WalkFrameEvent PunchFan(WalkPolygon worldPolygon, int activeViewIndex) =>
        new(WalkFrameEventKind.PunchFan, activeViewIndex, 0, 0f, worldPolygon);

    internal static WalkFrameEvent AlphaBarrier() =>
        new(WalkFrameEventKind.AlphaBarrier, 0, 0, 0f, null);

    internal static WalkFrameEvent LandscapeCellParticles(
        uint cellId, int alphaExclusiveEnd, bool includeParticles) =>
        new(
            WalkFrameEventKind.StaticParticles,
            alphaExclusiveEnd,
            cellId,
            includeParticles ? 1f : 0f,
            null);

    internal static WalkFrameEvent CellParticles(
        uint cellId, int alphaExclusiveEnd, bool includeParticles) =>
        new(
            WalkFrameEventKind.CellParticles,
            alphaExclusiveEnd,
            cellId,
            includeParticles ? 1f : 0f,
            null);

    internal static WalkFrameEvent LandscapeFlush() =>
        new(WalkFrameEventKind.LandscapeFlush, 0, 0, 0f, null);

    internal static WalkFrameEvent ClearInteriorDepth() =>
        new(WalkFrameEventKind.ClearInteriorDepth, 0, 0, 0f, null);

    internal static WalkFrameEvent ExitSeals() =>
        new(WalkFrameEventKind.ExitSeals, 0, 0, 0f, null);

    internal static WalkFrameEvent SortCellExit() =>
        new(WalkFrameEventKind.SortCellExit, 0, 0, 0f, null);
}

internal sealed class WalkFrameDriver : IWalkEventSink, IWalkLookInViewSource
{
    private readonly WbDrawDispatcher _dispatcher;
    private readonly WalkStaticStreamPopulator _populator;
    private IWalkFrameLeafRenderer _leafRenderer;
    private readonly IWalkFrameWorldData _worldData;
    private readonly IWalkFrameDriverTrace? _trace;
    private ClipFrame? _clipFrame;
    private readonly OrderedDrawStream _stream = new();
    private readonly List<WalkFrameEvent> _events = new();
    private readonly List<int> _markPositions = new();
    private readonly List<WbDrawDispatcher.WalkClassifiedBatch> _alphaSubmissions = new();
    private int _alphaSubmitMark;

    internal HashSet<uint> VisitedCells { get; } = new();

    internal List<uint> LookInCellTurns { get; } = new();

    private readonly List<WalkLookInTurn> _lookInTurns = new();
    private readonly List<WalkLookInSlice> _lookInSlices = new();
    private readonly List<WalkPlane> _lookInPlanes = new();
    private readonly List<int> _floodViewRouteScratch = new();
    private WalkPlane _lookInCyPlane;

    internal bool WeatherTurnFired { get; private set; }

    private readonly List<(uint LandblockId, int SideCellCount, int CellIndex)> _pendingTerrainBatch = new();

    private readonly HashSet<uint> _cellShellsDrawnThisFrame = new();
    private readonly HashSet<uint> _cellParticleTurnsDrawnThisFrame = new();

    internal int PortalsDrawnCount;

    IReadOnlyList<uint> IWalkLookInViewSource.LookInCellTurns => LookInCellTurns;

    internal HashSet<uint> LookInCells { get; } = new();

    internal List<uint> InteriorFloodCells { get; } = new();

    internal int InteriorFloodViewSliceCountAt(int floodCellIndex)
    {
        int routeIndex = InteriorFloodViewRouteAt(floodCellIndex);
        return _lookInTurns[routeIndex].SliceCount;
    }

    internal ReadOnlySpan<Vector4> InteriorFloodViewClipPlanesAt(
        int floodCellIndex,
        int sliceOffset)
    {
        int routeIndex = InteriorFloodViewRouteAt(floodCellIndex);
        WalkLookInTurn turn = _lookInTurns[routeIndex];
        if ((uint)sliceOffset >= (uint)turn.SliceCount)
            throw new ArgumentOutOfRangeException(nameof(sliceOffset));

        WalkLookInSlice slice = _lookInSlices[turn.SliceStart + sliceOffset];
        if (_clipFrame is null)
            return ReadOnlySpan<Vector4>.Empty;
        return _clipFrame.GetSlotPlanes(slice.ClipSlot);
    }

    private int InteriorFloodViewRouteAt(int floodCellIndex)
    {
        if ((uint)floodCellIndex >= (uint)InteriorFloodCells.Count
            || (uint)floodCellIndex >= (uint)_floodViewRouteScratch.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(floodCellIndex));
        }

        int routeIndex = _floodViewRouteScratch[floodCellIndex];
        if ((uint)routeIndex >= (uint)_lookInTurns.Count)
        {
            throw new InvalidOperationException(
                $"Interior flood cell 0x{InteriorFloodCells[floodCellIndex]:X8} "
                + "has no captured portal-view route.");
        }
        return routeIndex;
    }

    internal uint LookInSliceClipSlotAt(int routeIndex, int sliceOffset = 0)
    {
        WalkLookInTurn turn = _lookInTurns[routeIndex];
        if ((uint)sliceOffset >= (uint)turn.SliceCount)
            throw new ArgumentOutOfRangeException(nameof(sliceOffset));
        return _lookInSlices[turn.SliceStart + sliceOffset].ClipSlot;
    }

    internal List<WalkBuilding> VisitedBuildings { get; } = new();

    internal HashSet<uint> VisitedLandscapeCellIds { get; } = new();

    private IWalkBuildingFrameContext? _ctx;
    private Matrix4x4 _viewProjection;
    private Vector3 _cameraWorldPosition;

    private int _landscapeTurnsThisFrame;
    private WalkDrawStage? _currentDcStage;
    private bool _readyToReplay;
    private int _cellViewRouteIndex;
    private int _landscapeViewRouteIndex;

    private int _transcriptFrameNumber;

    internal WalkFrameDriver(
        WbDrawDispatcher dispatcher,
        IWalkFrameLeafRenderer leafRenderer,
        IWalkFrameWorldData worldData,
        IWalkFrameDriverTrace? trace = null,
        ClipFrame? clipFrame = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _leafRenderer = leafRenderer ?? throw new ArgumentNullException(nameof(leafRenderer));
        _worldData = worldData ?? throw new ArgumentNullException(nameof(worldData));
        _trace = trace;
        _clipFrame = clipFrame;
        _populator = new WalkStaticStreamPopulator(dispatcher);
    }

    internal void RebindFrame(
        IWalkFrameLeafRenderer leafRenderer,
        ClipFrame? clipFrame)
    {
        AbortFrame();
        _leafRenderer = leafRenderer
            ?? throw new ArgumentNullException(nameof(leafRenderer));
        _clipFrame = clipFrame;
    }

    internal void AbortFrame()
    {
        _ctx = null;
        _viewProjection = default;
        _cameraWorldPosition = default;
        _landscapeTurnsThisFrame = 0;
        _currentDcStage = null;
        _readyToReplay = false;
        WeatherTurnFired = false;
        _stream.Reset();
        _events.Clear();
        _markPositions.Clear();
        _alphaSubmissions.Clear();
        _pendingTerrainBatch.Clear();
        _alphaSubmitMark = 0;
        VisitedCells.Clear();
        LookInCellTurns.Clear();
        _lookInTurns.Clear();
        _lookInSlices.Clear();
        _lookInPlanes.Clear();
        _floodViewRouteScratch.Clear();
        _dispatcher.EndWalkPartFrame();
        _cellShellsDrawnThisFrame.Clear();
        _cellParticleTurnsDrawnThisFrame.Clear();
        _lookInCyPlane = default;
        LookInCells.Clear();
        VisitedBuildings.Clear();
        VisitedLandscapeCellIds.Clear();
        InteriorFloodCells.Clear();
        _cellViewRouteIndex = 0;
        _landscapeViewRouteIndex = -1;
    }

    internal void CopyVisibleCellsTo(HashSet<uint> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, VisitedCells)
            || ReferenceEquals(destination, VisitedLandscapeCellIds))
        {
            throw new ArgumentException(
                "The visible-cell destination cannot alias a walk source set.",
                nameof(destination));
        }

        destination.Clear();
        destination.UnionWith(VisitedCells);
        destination.UnionWith(VisitedLandscapeCellIds);
    }

    internal void RunFrame(
        RetailFrameWalk walk,
        uint cameraCellId,
        WalkCell? cameraCell,
        WalkLandscape landscape,
        IRetailFrameWalkContext ctx,
        IGpuFrame frame,
        IGpuPassEncoder encoder,
        Matrix4x4 viewProjection,
        Vector3 cameraWorldPosition)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(encoder);

        Collect(
            walk, cameraCellId, cameraCell, landscape, ctx,
            viewProjection, cameraWorldPosition);
        Replay(frame, encoder);
    }

    internal void Collect(
        RetailFrameWalk walk,
        uint cameraCellId,
        WalkCell? cameraCell,
        WalkLandscape landscape,
        IRetailFrameWalkContext ctx,
        Matrix4x4 viewProjection,
        Vector3 cameraWorldPosition)
    {
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(landscape);
        ArgumentNullException.ThrowIfNull(ctx);

        BeginFrame(ctx, viewProjection, cameraWorldPosition);
        if (AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled)
        {
            _transcriptFrameNumber++;
            WalkTranscriptDump.PrintFrameRoot(
                _transcriptFrameNumber,
                cameraCellId,
                cameraWorldPosition - new Vector3(
                    landscape.ViewerWorldOriginX, landscape.ViewerWorldOriginY, 0f),
                ctx.CyPlane.Normal);
        }
        try
        {
            walk.WalkFrame(cameraCellId, cameraCell, landscape, ctx, this);
            EndFrame();
        }
        catch
        {
            AbortFrame();
            throw;
        }
    }

    internal void BeginFrame(
        IRetailFrameWalkContext ctx,
        Matrix4x4 viewProjection,
        Vector3 cameraWorldPosition)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_ctx is not null)
        {
            throw new InvalidOperationException(
                "WalkFrameDriver.BeginFrame was called while a previous frame was still open — "
                + "the driver is not re-entrant (Campaign FW3.2b-1 fail-loud rule); call "
                + "EndFrame (or let a thrown exception's cleanup run) before starting the next.");
        }

        _dispatcher.BeginWalkPartFrame();
        _ctx = ctx;
        _viewProjection = viewProjection;
        _cameraWorldPosition = cameraWorldPosition;
        _landscapeTurnsThisFrame = 0;
        _currentDcStage = null;
        _readyToReplay = false;
        WeatherTurnFired = false;
        _stream.Reset();
        _events.Clear();
        _markPositions.Clear();
        _alphaSubmissions.Clear();
        _pendingTerrainBatch.Clear();
        _alphaSubmitMark = 0;
        VisitedCells.Clear();
        LookInCellTurns.Clear();
        _lookInTurns.Clear();
        _lookInSlices.Clear();
        _lookInPlanes.Clear();
        _lookInCyPlane = ctx.CyPlane;
        _cellShellsDrawnThisFrame.Clear();
        _cellParticleTurnsDrawnThisFrame.Clear();
        LookInCells.Clear();
        VisitedBuildings.Clear();
        VisitedLandscapeCellIds.Clear();
        InteriorFloodCells.Clear();
        _cellViewRouteIndex = 0;
        _landscapeViewRouteIndex = -1;
    }

    internal void EndFrame()
    {
        try
        {
            MarkIfGrown();
            MarkAlphaIfGrown();
        }
        finally
        {
            _dispatcher.EndWalkPartFrame();
            _ctx = null;
            _readyToReplay = true;
        }
    }

    internal void Replay(IGpuFrame frame, IGpuPassEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(encoder);
        if (!_readyToReplay)
        {
            throw new InvalidOperationException(
                "WalkFrameDriver.Replay was called without a completed Collect (BeginFrame/"
                + "EndFrame, or Collect/RunFrame) preceding it — there is nothing recorded to "
                + "replay.");
        }

        try
        {
            if (_stream.Count > 0)
                _dispatcher.PrepareOrderedStream(frame, _stream, _viewProjection, _markPositions);

            _pendingTerrainBatch.Clear();

            int cursor = 0;
            int alphaCursor = 0;
            for (int i = 0; i < _events.Count; i++)
            {
                WalkFrameEvent e = _events[i];
                switch (e.Kind)
                {
                    case WalkFrameEventKind.StreamMark:
                        int end = e.IntArg;
                        int count = end - cursor;
                        if (_trace is not null)
                            _trace.OnFlush(count, _stream.Stages.GetRange(cursor, count));
                        _dispatcher.DrawOrderedRange(encoder, cursor, count);
                        cursor = end;
                        break;
                    case WalkFrameEventKind.AlphaSubmitMark:
                        int alphaEnd = e.IntArg;
                        for (; alphaCursor < alphaEnd; alphaCursor++)
                        {
                            WbDrawDispatcher.WalkClassifiedBatch batch =
                                _alphaSubmissions[alphaCursor];
                            _dispatcher.SubmitWalkAlphaInstance(
                                in batch,
                                _viewProjection);
                        }
                        break;
                    case WalkFrameEventKind.Sky:
                        FlushPendingTerrainBatch();
                        _leafRenderer.DrawSky();
                        break;
                    case WalkFrameEventKind.LandCell:
                        _pendingTerrainBatch.Add((e.CellId, e.IntArg >> 8, e.IntArg & 0xFF));
                        break;
                    case WalkFrameEventKind.CellShell:
                        FlushPendingTerrainBatch();
                        _leafRenderer.DrawCellShell(e.CellId);
                        break;
                    case WalkFrameEventKind.PunchFan:
                        FlushPendingTerrainBatch();
                        _leafRenderer.DrawPunchFan(e.Polygon!, e.IntArg);
                        break;
                    case WalkFrameEventKind.AlphaBarrier:
                        FlushPendingTerrainBatch();
                        _leafRenderer.AlphaBarrier();
                        break;
                    case WalkFrameEventKind.SortCellExit:
                        FlushPendingTerrainBatch();
                        _leafRenderer.FlushSortCellExit();
                        break;
                    case WalkFrameEventKind.LandscapeFlush:
                        FlushPendingTerrainBatch();
                        _leafRenderer.FlushLandscape();
                        break;
                    case WalkFrameEventKind.ClearInteriorDepth:
                        FlushPendingTerrainBatch();
                        _leafRenderer.ClearInteriorDepth();
                        break;
                    case WalkFrameEventKind.ExitSeals:
                        FlushPendingTerrainBatch();
                        PortalsDrawnCount += _leafRenderer.DrawExitSeals();
                        break;
                    case WalkFrameEventKind.StaticParticles:
                        SubmitCellAlpha(
                            e,
                            ref alphaCursor,
                            staticParticleTurn: true);
                        break;
                    case WalkFrameEventKind.CellParticles:
                        SubmitCellAlpha(
                            e,
                            ref alphaCursor,
                            staticParticleTurn: false);
                        break;
                }
            }
            FlushPendingTerrainBatch();
        }
        finally
        {
            _stream.Reset();
            _events.Clear();
            _markPositions.Clear();
            _alphaSubmissions.Clear();
            _pendingTerrainBatch.Clear();
            _alphaSubmitMark = 0;
            _readyToReplay = false;
            _ctx = null;
        }
    }

    private void SubmitCellAlpha(
        in WalkFrameEvent e,
        ref int alphaCursor,
        bool staticParticleTurn)
    {
        ReadOnlySpan<PreparedParticleAlphaSubmission> particles =
            ReadOnlySpan<PreparedParticleAlphaSubmission>.Empty;
        bool includeParticles = e.FloatArg != 0f;
        if (includeParticles && _leafRenderer.HasRenderableEmittersInCell(e.CellId))
        {
            // Preserve the existing particle-turn terrain boundary: a row-5
            // immediate mesh may draw during preparation.
            FlushPendingTerrainBatch();
            particles = staticParticleTurn
                ? _leafRenderer.PrepareStaticParticles(e.CellId)
                : _leafRenderer.PrepareCellParticles(e.CellId);
        }

        int objectEnd = e.IntArg;
        int particleIndex = 0;
        while (alphaCursor < objectEnd || particleIndex < particles.Length)
        {
            bool takeParticle = particleIndex < particles.Length
                && (alphaCursor >= objectEnd
                    || particles[particleIndex].DistanceSq
                        > _alphaSubmissions[alphaCursor].SortDistanceSq);
            if (takeParticle)
            {
                particles[particleIndex++].Append();
            }
            else
            {
                WbDrawDispatcher.WalkClassifiedBatch batch =
                    _alphaSubmissions[alphaCursor++];
                _dispatcher.SubmitWalkAlphaInstance(in batch, _viewProjection);
            }
        }
    }

    private void FlushPendingTerrainBatch()
    {
        if (_pendingTerrainBatch.Count == 0)
            return;
        _leafRenderer.DrawLandCellBatch(_pendingTerrainBatch);
        _pendingTerrainBatch.Clear();
    }

    // ------------------------------------------------------------------
    // IWalkEventSink
    // ------------------------------------------------------------------

    void IWalkEventSink.Emit(in WalkEvent walkEvent)
    {
        switch (walkEvent.Kind)
        {
            case WalkEventKind.DrawInside:
                WalkTranscriptDump.PrintDrawInside(walkEvent.CellId);
                _currentDcStage = WalkDrawStage.CellStatic;
                VisitedCells.Add(walkEvent.CellId);
                break;
            case WalkEventKind.Landscape:
                WalkTranscriptDump.PrintLandscape();
                HandleLandscapeTurn(walkEvent.OutsideViewCount);
                break;
            case WalkEventKind.DrawCells:
                WalkTranscriptDump.PrintDrawCells(
                    outdoorPview: _currentDcStage == WalkDrawStage.LookInStatic,
                    walkEvent.OutsideViewCount,
                    walkEvent.Cells);
                foreach (uint id in walkEvent.Cells)
                    VisitedCells.Add(id);
                HandleDrawCellsTurn(walkEvent.Cells);
                break;
            case WalkEventKind.Building:
                WalkTranscriptDump.PrintBuilding(walkEvent.CellId);
                break;
        }
    }

    void IWalkEventSink.OnLandCellTurn(uint landblockId, int sideCellCount, int cellIndex)
    {
        RequireOpenFrame();
        if (sideCellCount is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sideCellCount),
                sideCellCount,
                "A landscape LOD grid must be 1, 2, 4, or 8 cells per side.");
        }
        if ((uint)cellIndex >= (uint)(sideCellCount * sideCellCount))
            throw new ArgumentOutOfRangeException(nameof(cellIndex));

        if (AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled)
        {
            WalkTranscriptDump.PrintLandCell(
                WalkTranscriptDump.LodCellId(landblockId, sideCellCount, cellIndex));
        }

        MarkIfGrown();
        _events.Add(WalkFrameEvent.LandCell(landblockId, sideCellCount, cellIndex));
    }

    void IWalkEventSink.OnSortCellTurn(uint landblockId, int sideCellCount, int cellIndex)
    {
        if (!AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled)
            return;

        RequireOpenFrame();
        if (sideCellCount is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sideCellCount),
                sideCellCount,
                "A landscape LOD grid must be 1, 2, 4, or 8 cells per side.");
        }
        if ((uint)cellIndex >= (uint)(sideCellCount * sideCellCount))
            throw new ArgumentOutOfRangeException(nameof(cellIndex));

        WalkTranscriptDump.PrintSortCell(
            WalkTranscriptDump.LodCellId(landblockId, sideCellCount, cellIndex));
    }

    void IWalkEventSink.OnLandscapeCellTurn(uint cellId)
        => HandleLandscapeCellTurn(cellId);

    void IWalkEventSink.OnLandscapeCellTurn(
        uint landblockId,
        int sideCellCount,
        int cellIndex)
    {
        if (sideCellCount is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sideCellCount),
                sideCellCount,
                "A landscape LOD grid must be 1, 2, 4, or 8 cells per side.");
        }
        if ((uint)cellIndex >= (uint)(sideCellCount * sideCellCount))
            throw new ArgumentOutOfRangeException(nameof(cellIndex));

        if (sideCellCount == 1)
            return;

        uint blockPrefix = landblockId & 0xFFFF0000u;
        if (sideCellCount == 8)
        {
            HandleLandscapeCellTurn(blockPrefix | checked((uint)(cellIndex + 1)));
            return;
        }

        int span = 8 / sideCellCount;
        int coarseX = cellIndex / sideCellCount;
        int coarseY = cellIndex % sideCellCount;
        int firstX = coarseX * span;
        int firstY = coarseY * span;
        for (int x = firstX; x < firstX + span; x++)
        {
            for (int y = firstY; y < firstY + span; y++)
            {
                HandleLandscapeCellTurn(
                    blockPrefix | checked((uint)(x * 8 + y + 1)));
            }
        }
    }

    private void HandleLandscapeCellTurn(uint cellId)
    {
        RequireOpenFrame();
        if (_landscapeViewRouteIndex < 0)
        {
            throw new InvalidOperationException(
                "A landscape cell turn fired before RetailFrameWalk installed its active "
                + "view set — the walk and draw driver are desynchronized.");
        }
        VisitedLandscapeCellIds.Add(cellId);
        WalkFrameStaticRecords records = _worldData.GetOutdoorObjects(cellId);
        _populator.PopulateCellObjects(
            _stream,
            WalkDrawStage.OutdoorStatic,
            cellId,
            records.Records,
            records.TupleLandblockId,
            _cameraWorldPosition,
            _viewProjection,
            this,
            _landscapeViewRouteIndex,
            _alphaSubmissions);
        MarkIfGrown();
        bool includeParticles = _cellParticleTurnsDrawnThisFrame.Add(cellId);
        _alphaSubmitMark = _alphaSubmissions.Count;
        _events.Add(WalkFrameEvent.LandscapeCellParticles(
            cellId,
            _alphaSubmitMark,
            includeParticles));
    }

    void IWalkEventSink.OnSortCellExit(uint landblockId, int sideCellCount, int cellIndex)
    {
        RequireOpenFrame();
        MarkIfGrown();
        MarkAlphaIfGrown();
        _events.Add(WalkFrameEvent.SortCellExit());
    }

    void IWalkEventSink.OnLandscapeViews(WalkPortalView activeViews)
    {
        ArgumentNullException.ThrowIfNull(activeViews);
        RequireOpenFrame();
        _landscapeViewRouteIndex = _cellViewRouteIndex++;
        CaptureViews(0, activeViews);
    }

    void IWalkEventSink.OnBuildingTurn(WalkBuilding building)
    {
        ArgumentNullException.ThrowIfNull(building);
        RequireOpenFrame();
        VisitedBuildings.Add(building);

        MarkIfGrown();
        MarkAlphaIfGrown();
        _events.Add(WalkFrameEvent.AlphaBarrier());

        _currentDcStage = WalkDrawStage.LookInStatic;
    }

    void IWalkEventSink.OnBuildingShellTurn(
        WalkBuilding building,
        WalkBuildingSelection selection)
    {
        ArgumentNullException.ThrowIfNull(building);
        RequireOpenFrame();

        MarkIfGrown();
        WalkFrameStaticRecords shell = _worldData.GetBuildingShellStatics(building);
        if (shell.Records.Count > 1)
        {
            throw new InvalidOperationException(
                $"Building 0x{building.PositionCellId:X8} has "
                + $"{shell.Records.Count} retained shell records; expected at most one.");
        }
        if (shell.Records.Count == 1)
        {
            ref readonly RenderProjectionRecord record =
                ref shell.Records.AsSpan()[0];
            _populator.PopulateBuildingShell(
                _stream,
                building.PositionCellId,
                in record,
                shell.TupleLandblockId,
                _cameraWorldPosition,
                _viewProjection,
                in selection,
                building.PartZeroTransform,
                _alphaSubmissions);
        }
        if (_alphaSubmissions.Count != _alphaSubmitMark)
        {
            MarkIfGrown();
            MarkAlphaIfGrown();
        }
    }

    void IWalkEventSink.OnPunchGeometry(
        WalkBuilding building, WalkPolygon polygon, int activeViewIndex)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(polygon);
        RequireOpenFrame();

        if (WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon.Vertices))
            return;

        MarkIfGrown();
        Matrix4x4 worldTransform = _worldData.GetBuildingWorldTransform(building);
        _events.Add(
            WalkFrameEvent.PunchFan(TransformToWorld(polygon, worldTransform), activeViewIndex));
    }

    void IWalkEventSink.OnInteriorFloodDrawTurn(IReadOnlyList<uint> cells, int outsideViewCount)
    {
        ArgumentNullException.ThrowIfNull(cells);
        RequireOpenFrame();

        if (outsideViewCount > 0)
        {
            MarkIfGrown();
            _events.Add(WalkFrameEvent.LandscapeFlush());

            _dispatcher.AdvanceWalkPartPassStamp();
            _cellShellsDrawnThisFrame.Clear();
            _cellParticleTurnsDrawnThisFrame.Clear();

            int armed = PortalsDrawnCount;
            PortalsDrawnCount = 0;
            if (armed != 0)
            {
                MarkIfGrown();
                _events.Add(WalkFrameEvent.ClearInteriorDepth());
            }

            MarkIfGrown();
            _events.Add(WalkFrameEvent.ExitSeals());
        }

        InteriorFloodCells.Clear();
        for (int i = 0; i < cells.Count; i++)
            InteriorFloodCells.Add(cells[i]);

        EmitFloodTurns(WalkDrawStage.CellStatic, cells);
    }

    void IWalkEventSink.OnWeatherTurn(uint viewerCellId)
    {
        RequireOpenFrame();
        WeatherTurnFired = true;

        if (!AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled)
            return;

        WalkTranscriptDump.PrintObjectCellTurn(viewerCellId);
    }

    // ------------------------------------------------------------------
    // Turn handlers
    // ------------------------------------------------------------------

    private void HandleLandscapeTurn(int activeViewCount)
    {
        RequireOpenFrame();
        if (activeViewCount < 1)
        {
            throw new InvalidOperationException(
                $"A Landscape turn fired with {activeViewCount} active views — "
                + "RetailFrameWalk only draws the landscape through an installed view set "
                + "(the outdoor root's full-screen default view, or an interior root's "
                + "surviving exit views, both at least 1). A zero/negative count is a "
                + "walk/driver desync (Campaign FW fail-loud rule).");
        }
        if (_landscapeTurnsThisFrame != 0)
        {
            throw new InvalidOperationException(
                "A second Landscape turn fired in one frame — RetailFrameWalk.WalkFrame/"
                + "DrawInside's own call graph guarantees at most one Landscape turn per "
                + "frame (outdoor root draws it once; an interior root draws it at most "
                + "once more, through surviving exit views). A second occurrence is a walk/"
                + "driver desync, not something to silently double-draw sky for (Campaign "
                + "FW3.2b-1 fail-loud rule).");
        }

        MarkIfGrown();
        _events.Add(WalkFrameEvent.Sky());
        _landscapeTurnsThisFrame++;
    }

    private void HandleDrawCellsTurn(IReadOnlyList<uint> cells)
    {
        RequireOpenFrame();
        if (_currentDcStage is not { } stage)
        {
            throw new InvalidOperationException(
                "A DrawCells turn fired before any DrawInside or Building turn established "
                + "which stage its cells belong to — a walk/driver desync (Campaign FW3.2b-1 "
                + "fail-loud rule): RetailFrameWalk only ever emits DrawCells after DrawInside "
                + "(the interior root's own flood) or after a building's look-in portal pass.");
        }

        if (stage == WalkDrawStage.CellStatic)
        {
            return;
        }

        EmitFloodTurns(stage, cells);
    }

    private void EmitFloodTurns(WalkDrawStage stage, IReadOnlyList<uint> cells)
    {
        _floodViewRouteScratch.Clear();
        for (int i = 0; i < cells.Count; i++)
            _floodViewRouteScratch.Add(-1);

        for (int i = cells.Count - 1; i >= 0; i--)
        {
            int viewRouteIndex = CaptureCellViewRoute(cells[i]);
            _floodViewRouteScratch[i] = viewRouteIndex;
            if (AcDream.Core.Rendering.RenderingDiagnostics.DumpWalkTranscriptEnabled)
            {
                int liveViewCount = _lookInTurns[viewRouteIndex].SliceCount;
                for (int view = 0; view < liveViewCount; view++)
                    WalkTranscriptDump.PrintEnvCellShell(cells[i]);
            }
            if (_cellShellsDrawnThisFrame.Add(cells[i]))
            {
                MarkIfGrown();
                _events.Add(WalkFrameEvent.CellShell(cells[i]));
            }
        }

        for (int i = cells.Count - 1; i >= 0; i--)
            EmitCellContentsTurn(stage, cells[i], _floodViewRouteScratch[i]);
    }

    private int CaptureCellViewRoute(uint cellId)
    {
        int viewRouteIndex = _cellViewRouteIndex++;
        CaptureCellViews(cellId);
        return viewRouteIndex;
    }

    private void EmitCellContentsTurn(
        WalkDrawStage stage,
        uint cellId,
        int viewRouteIndex)
    {
        WalkTranscriptDump.PrintObjectCellTurn(cellId);

        WalkFrameStaticRecords records = _worldData.GetCellObjects(cellId);
        _populator.PopulateCellObjects(
            _stream,
            stage,
            cellId,
            records.Records,
            records.TupleLandblockId,
            _cameraWorldPosition,
            _viewProjection,
            this,
            viewRouteIndex,
            _alphaSubmissions);
        MarkIfGrown();

        if (stage == WalkDrawStage.LookInStatic)
        {
            LookInCellTurns.Add(cellId);
            LookInCells.Add(cellId);
        }

        bool includeParticles = _cellParticleTurnsDrawnThisFrame.Add(cellId);
        _alphaSubmitMark = _alphaSubmissions.Count;
        _events.Add(WalkFrameEvent.CellParticles(
            cellId,
            _alphaSubmitMark,
            includeParticles));
    }

    private void CaptureCellViews(uint cellId)
    {
        IWalkBuildingFrameContext ctx = RequireOpenFrame();
        WalkCell? cell = ctx.GetVisible(cellId);
        if (cell is null || cell.NumView <= 0)
        {
            _lookInTurns.Add(new WalkLookInTurn(
                cellId, _lookInSlices.Count, 0));
            return;
        }

        CaptureViews(cellId, cell.TopView);
    }

    private void CaptureViews(uint cellId, WalkPortalView portalView)
    {
        int sliceStart = _lookInSlices.Count;
        for (int sliceIndex = 0; sliceIndex < portalView.ViewCount; sliceIndex++)
        {
            WalkViewPoly poly = portalView.View.Polys[sliceIndex];
            int planeStart = _lookInPlanes.Count;
            for (int edge = 0; edge < poly.VertexCount; edge++)
            {
                _lookInPlanes.Add(
                    portalView.View.Vertices[poly.VertexIndex + edge].Plane);
            }
            uint clipSlot = AppendClipSlot(portalView, poly);
            _lookInSlices.Add(new WalkLookInSlice(
                planeStart, poly.VertexCount, clipSlot));
        }
        _lookInTurns.Add(new WalkLookInTurn(
            cellId, sliceStart, _lookInSlices.Count - sliceStart));
    }

    public bool SphereVisibleInLookInTurn(
        int routeIndex,
        in Vector3 center,
        float radius,
        bool testSphere = true)
    {
        if ((uint)routeIndex >= (uint)_lookInTurns.Count)
            return false;

        WalkLookInTurn turn = _lookInTurns[routeIndex];
        for (int sliceOffset = 0; sliceOffset < turn.SliceCount; sliceOffset++)
        {
            WalkLookInSlice slice = _lookInSlices[turn.SliceStart + sliceOffset];
            if (!testSphere
                || WalkVisibilityMath.ViewconeCheck(
                    center,
                    radius,
                    _lookInCyPlane,
                    CollectionsMarshal.AsSpan(_lookInPlanes).Slice(
                        slice.PlaneStart,
                        slice.PlaneCount)) != WalkBoundingType.Outside)
            {
                return true;
            }
        }
        return false;
    }

    public string DescribeLookInTurn(
        int routeIndex,
        in Vector3 center,
        float radius)
    {
        if ((uint)routeIndex >= (uint)_lookInTurns.Count)
            return "route-missing";

        WalkLookInTurn turn = _lookInTurns[routeIndex];
        var description = new System.Text.StringBuilder(192);
        float cyDistance = Vector3.Dot(_lookInCyPlane.Normal, center)
            + _lookInCyPlane.D;
        description.Append("turnCell=0x")
            .Append(turn.CellId.ToString("X8"))
            .Append(" slices=").Append(turn.SliceCount)
            .Append(" cy=").Append(cyDistance.ToString("F5"))
            .Append(" cyMargin=").Append((cyDistance + radius).ToString("F5"));

        for (int sliceOffset = 0; sliceOffset < turn.SliceCount; sliceOffset++)
        {
            WalkLookInSlice slice = _lookInSlices[turn.SliceStart + sliceOffset];
            description.Append(" slice[").Append(sliceOffset).Append("]=");
            for (int planeOffset = 0; planeOffset < slice.PlaneCount; planeOffset++)
            {
                if (planeOffset != 0)
                    description.Append(',');
                WalkPlane plane = _lookInPlanes[slice.PlaneStart + planeOffset];
                float distance = Vector3.Dot(plane.Normal, center) + plane.D;
                description.Append(distance.ToString("F5"));
            }
        }
        return description.ToString();
    }

    private uint AppendClipSlot(WalkPortalView portalView, WalkViewPoly poly)
    {
        if (_clipFrame is null)
            return 0;

        IRetailFrameWalkContext ctx = (IRetailFrameWalkContext)RequireOpenFrame();
        int count = poly.VertexCount;
        if (count < 3)
            return 0;

        Span<Vector2> ndc = stackalloc Vector2[Math.Min(count, WalkCopyView.MaxVertices)];
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < ndc.Length; i++)
        {
            Vector2 px = portalView.View.Vertices[poly.VertexIndex + i].Point;
            Vector2 point = new(
                px.X / ctx.ViewportWidth * 2f - 1f,
                1f - px.Y / ctx.ViewportHeight * 2f);
            ndc[i] = point;
            minX = MathF.Min(minX, point.X);
            minY = MathF.Min(minY, point.Y);
            maxX = MathF.Max(maxX, point.X);
            maxY = MathF.Max(maxY, point.Y);
        }

        if (ndc.Length > ClipFrame.MaxPlanes)
        {
            Span<Vector4> aabbPlanes = stackalloc Vector4[4]
            {
                new(1f, 0f, 0f, -minX),
                new(-1f, 0f, 0f, maxX),
                new(0f, 1f, 0f, -minY),
                new(0f, -1f, 0f, maxY),
            };
            return checked((uint)_clipFrame.AppendSlot(aabbPlanes));
        }

        float area2 = 0f;
        for (int i = 0; i < ndc.Length; i++)
            area2 += ndc[i].X * ndc[(i + 1) % ndc.Length].Y
                - ndc[(i + 1) % ndc.Length].X * ndc[i].Y;
        bool ccw = area2 >= 0f;
        Span<Vector4> planes = stackalloc Vector4[ClipFrame.MaxPlanes];
        for (int i = 0; i < ndc.Length; i++)
        {
            int current = ccw ? i : ndc.Length - 1 - i;
            int next = ccw
                ? (i + 1) % ndc.Length
                : (ndc.Length - 2 - i + ndc.Length) % ndc.Length;
            Vector2 p = ndc[current];
            Vector2 q = ndc[next];
            Vector2 dir = q - p;
            Vector2 normal = Vector2.Normalize(new Vector2(-dir.Y, dir.X));
            planes[i] = new Vector4(normal.X, normal.Y, 0f, -Vector2.Dot(normal, p));
        }
        return checked((uint)_clipFrame.AppendSlot(planes[..ndc.Length]));
    }

    private void MarkIfGrown()
    {
        int count = _stream.Count;
        int last = _markPositions.Count > 0 ? _markPositions[^1] : 0;
        if (count == last)
            return;

        _markPositions.Add(count);
        _events.Add(WalkFrameEvent.Mark(count));
    }

    private void MarkAlphaIfGrown()
    {
        int count = _alphaSubmissions.Count;
        if (count == _alphaSubmitMark)
            return;

        _alphaSubmitMark = count;
        _events.Add(WalkFrameEvent.AlphaSubmitMark(count));
    }

    private IWalkBuildingFrameContext RequireOpenFrame() =>
        _ctx ?? throw new InvalidOperationException(
            "WalkFrameDriver received a walk turn outside BeginFrame/EndFrame — call "
            + "BeginFrame (or Collect/RunFrame) before driving the walk with this driver as "
            + "its IWalkEventSink.");

    private static WalkPolygon TransformToWorld(WalkPolygon local, Matrix4x4 worldTransform)
    {
        var vertices = new Vector3[local.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = Vector3.Transform(local.Vertices[i], worldTransform);

        Vector3 normal = local.Vertices.Length > 0
            ? Vector3.Normalize(Vector3.TransformNormal(local.Plane.Normal, worldTransform))
            : Vector3.Zero;
        float d = vertices.Length > 0 ? -Vector3.Dot(normal, vertices[0]) : 0f;

        return new WalkPolygon { Vertices = vertices, Plane = new WalkPlane(normal, d) };
    }
}
