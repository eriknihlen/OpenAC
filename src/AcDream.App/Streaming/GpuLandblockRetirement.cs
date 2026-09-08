using AcDream.Core.World;

namespace AcDream.App.Streaming;

public sealed record GpuLandblockRetirement(
    uint LandblockId,
    LandblockRetirementKind Kind,
    IReadOnlyList<WorldEntity> Entities);

/// <summary>
/// Exact result of the atomic spatial-generation swap used before a shared
/// world-origin recenter. Spatial membership is already unreachable when this
/// value returns; the retained per-landblock receipts let presentation owners
/// retire their resources later under the ordinary frame budget.
/// </summary>
internal sealed record GpuWorldRecenterRetirement(
    IReadOnlyList<GpuLandblockRetirement> Landblocks,
    int SpatialOperationCount,
    Exception? ObserverFailure);

public enum LandblockRetirementKind
{
    Full,
    NearLayer,
}
