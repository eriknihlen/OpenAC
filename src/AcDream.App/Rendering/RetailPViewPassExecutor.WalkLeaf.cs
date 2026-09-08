using System.Diagnostics;
using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Vfx;

namespace AcDream.App.Rendering;

internal sealed partial class RetailPViewPassExecutor
{
    internal void DrawWalkSky(RetailPViewFrameInput frame)
    {
        if (!frame.RenderSky)
            return;

        _sky?.RenderSky(
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
                ParticleRenderPass.SkyPreScene);
        }
    }

    internal void DrawWalkLandCellBatch(
        RetailPViewFrameInput frame,
        IReadOnlyList<(uint LandblockId, int SideCellCount, int CellIndex)> cells)
    {
        long start = Stopwatch.GetTimestamp();
        _terrain?.DrawLandCells(frame.ViewProjection, cells);
        _terrainDiagnostics.AccumulateWalkBatch(Stopwatch.GetTimestamp() - start);
    }

    internal void CompleteWalkTerrainFrame() => _terrainDiagnostics.CompleteWalkFrame();

    internal bool HasWalkRenderableEmittersInCell(uint cellId) =>
        _particles?.HasRenderableEmittersInCell(ParticleRenderPass.Scene, cellId) ?? false;

    internal void DrawWalkPunchFan(
        RetailPViewFrameInput frame,
        ClipFrameAssembly clipAssembly,
        WalkPolygon worldPolygon,
        int activeViewIndex)
    {
        if (_portalDepthMask is null)
            return;
        Vector3[] vertices = worldPolygon.Vertices;
        if (vertices.Length < 3)
            return;

        ReadOnlySpan<ClipViewSlice> slices = clipAssembly.OutsideViewSlices;
        if ((uint)activeViewIndex >= (uint)slices.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeViewIndex),
                activeViewIndex,
                $"punch fan pinned to walk view {activeViewIndex} but the "
                + $"reassembled outside view only holds {slices.Length} "
                + "index-aligned slice(s) — ReassembleOutsideViewFromWalk "
                + "desynchronized from RetailFrameWalk.DrawBuilding's own "
                + "view count (fail-loud rule; never draw unclipped).");
        }

        ClipViewSlice slice = slices[activeViewIndex];
        if (slice.NothingVisible)
            return;

        Span<Vector3> world = stackalloc Vector3[32];
        int count = Math.Min(vertices.Length, world.Length);
        for (int vertex = 0; vertex < count; vertex++)
            world[vertex] = vertices[vertex];
        _portalDepthMask.DrawDepthFan(
            world[..count],
            frame.ViewProjection,
            slice.Planes,
            forceFarZ: true);
    }
}

internal sealed class WalkProductionLeafRenderer : IWalkFrameLeafRenderer
{
    private RetailPViewPassExecutor _passes = null!;
    private RetailPViewFrameInput _frame = null!;
    private ClipFrameAssembly _clipAssembly = null!;
    private Action _flushLandscape = null!;
    private Action _clearInteriorDepth = null!;
    private Func<int> _drawExitSeals = null!;
    private readonly HashSet<uint> _singleCellScratch = new();

    internal WalkProductionLeafRenderer(
        RetailPViewPassExecutor passes,
        RetailPViewFrameInput frame,
        ClipFrameAssembly clipAssembly,
        Action flushLandscape,
        Action clearInteriorDepth,
        Func<int> drawExitSeals)
        => Reset(passes, frame, clipAssembly, flushLandscape, clearInteriorDepth, drawExitSeals);

    internal void Reset(
        RetailPViewPassExecutor passes,
        RetailPViewFrameInput frame,
        ClipFrameAssembly clipAssembly,
        Action flushLandscape,
        Action clearInteriorDepth,
        Func<int> drawExitSeals)
    {
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _clipAssembly = clipAssembly
            ?? throw new ArgumentNullException(nameof(clipAssembly));
        _flushLandscape = flushLandscape
            ?? throw new ArgumentNullException(nameof(flushLandscape));
        _clearInteriorDepth = clearInteriorDepth
            ?? throw new ArgumentNullException(nameof(clearInteriorDepth));
        _drawExitSeals = drawExitSeals
            ?? throw new ArgumentNullException(nameof(drawExitSeals));
    }

    public void DrawSky() => _passes.DrawWalkSky(_frame);

    public void DrawLandCellBatch(
        IReadOnlyList<(uint LandblockId, int SideCellCount, int CellIndex)> cells) =>
        _passes.DrawWalkLandCellBatch(_frame, cells);

    public bool HasRenderableEmittersInCell(uint cellId) =>
        _passes.HasWalkRenderableEmittersInCell(cellId);

    public void DrawCellShell(uint cellId)
    {
        _singleCellScratch.Clear();
        _singleCellScratch.Add(cellId);
        _passes.DrawOpaqueCellShells(_singleCellScratch);
        if (_passes.CellHasTransparentShell(cellId))
        {
            _passes.SubmitOrDrawTransparentCellShell(cellId);
        }
    }

    public void FlushLandscape() => _flushLandscape();

    public void ClearInteriorDepth() => _clearInteriorDepth();

    public ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareStaticParticles(uint cellId) =>
        _passes.PrepareCellParticleAlpha(_frame, cellId);

    public ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareCellParticles(uint cellId) =>
        _passes.PrepareCellParticleAlpha(_frame, cellId);

    public int DrawExitSeals() => _drawExitSeals();

    public void DrawPunchFan(WalkPolygon worldPolygon, int activeViewIndex) =>
        _passes.DrawWalkPunchFan(_frame, _clipAssembly, worldPolygon, activeViewIndex);

    public void AlphaBarrier() => _passes.FlushBuildingAlpha();

    public void FlushSortCellExit() => _passes.FlushSortCellExitAlpha();
}
