using System.Numerics;
using AcDream.Core.Vfx;

namespace AcDream.App.Rendering.Vfx;

internal interface IWorldSceneParticleVisibility
{
    void MarkVisibleLandscapeCells(HashSet<uint> cellIds);

    void CompleteFrame();

    void AbortFrame();
}

internal readonly record struct RetailLandscapeVisibilityFrame(
    IReadOnlySet<uint> CellIds,
    bool HasCompletedWorldView)
{
    internal static RetailLandscapeVisibilityFrame None { get; } = new(
        System.Collections.Frozen.FrozenSet<uint>.Empty,
        HasCompletedWorldView: false);
}

public sealed class ParticleVisibilityController : IWorldSceneParticleVisibility
{
    public const float ExtendedRangeMultiplier = 2f;

    private readonly HashSet<uint> _buildingCellIds = new();
    private readonly HashSet<uint> _completedCellIds = new();
    private Vector3 _buildingViewerPosition;
    private Vector3 _completedViewerPosition;
    private bool _frameOpen;
    private bool _frameUsesWorldView;
    private bool _hasCompletedWorldView;

    public void BeginFrame(Vector3 viewerPosition)
    {
        _buildingCellIds.Clear();
        _buildingViewerPosition = viewerPosition;
        _frameUsesWorldView = false;
        _frameOpen = true;
    }

    public void UseWorldView()
    {
        if (_frameOpen)
            _frameUsesWorldView = true;
    }

    public void MarkVisibleLandscapeCells(HashSet<uint> cellIds)
    {
        ArgumentNullException.ThrowIfNull(cellIds);
        if (!_frameOpen || !_frameUsesWorldView)
            return;

        foreach (uint cellId in cellIds)
        {
            uint low = cellId & 0xFFFFu;
            if (low == 0u || low >= 0x0100u)
            {
                throw new ArgumentException(
                    $"Landscape visibility accepts only outdoor land cells; "
                    + $"0x{cellId:X8} is not one.",
                    nameof(cellIds));
            }
        }

        _buildingCellIds.UnionWith(cellIds);
    }

    public void CompleteFrame()
    {
        if (!_frameOpen)
            return;

        _frameOpen = false;
        if (!_frameUsesWorldView)
        {
            _completedCellIds.Clear();
            _completedViewerPosition = _buildingViewerPosition;
            _hasCompletedWorldView = false;
            return;
        }

        _completedCellIds.Clear();
        _completedCellIds.UnionWith(_buildingCellIds);
        _completedViewerPosition = _buildingViewerPosition;
        _hasCompletedWorldView = true;
    }

    public void AbortFrame()
    {
        _buildingCellIds.Clear();
        _frameUsesWorldView = false;
        _frameOpen = false;
    }

    public void Apply(ParticleSystem particles, float rangeMultiplier)
    {
        ArgumentNullException.ThrowIfNull(particles);
        particles.ApplyRetailView(
            _completedViewerPosition,
            _completedCellIds,
            _hasCompletedWorldView,
            rangeMultiplier);
    }

    internal RetailLandscapeVisibilityFrame CaptureCompletedLandscapeVisibility() =>
        new(_completedCellIds, _hasCompletedWorldView);

    public void Reset()
    {
        _buildingCellIds.Clear();
        _completedCellIds.Clear();
        _frameOpen = false;
        _frameUsesWorldView = false;
        _hasCompletedWorldView = false;
        _buildingViewerPosition = default;
        _completedViewerPosition = default;
    }
}
