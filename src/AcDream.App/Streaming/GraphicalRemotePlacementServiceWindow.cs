using AcDream.Runtime.Session;

namespace AcDream.App.Streaming;

internal sealed class GraphicalRemotePlacementServiceWindow
    : IRuntimeRemotePlacementServiceWindow
{
    private readonly GpuWorldState _state;
    private readonly Func<uint, bool> _isPresentationReady;

    internal GraphicalRemotePlacementServiceWindow(
        GpuWorldState state,
        Func<uint, bool> isPresentationReady)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _isPresentationReady = isPresentationReady
            ?? throw new ArgumentNullException(nameof(isPresentationReady));
    }

    public bool IsWithinServiceWindow(uint landblockId)
    {
        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        return _isPresentationReady(canonical)
            && _state.IsNearTier(canonical);
    }
}
