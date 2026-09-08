using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Packs;

internal interface IRenderPackReceiverPipelineCandidate : IDisposable
{
}

internal interface IRenderPackReceiverPipelineCoordinator
{
    IRenderPackReceiverPipelineCandidate Prepare(
        IDirectionalShadowReceiverSource? source,
        int sampleCount);

    void Publish(IRenderPackReceiverPipelineCandidate candidate);

    void Clear();
}

internal sealed class RenderPackReceiverPipelineCoordinator(
    TerrainModernRenderer terrain,
    WbDrawDispatcher worldMeshes) : IRenderPackReceiverPipelineCoordinator
{
    private readonly TerrainModernRenderer _terrain = terrain
        ?? throw new ArgumentNullException(nameof(terrain));
    private readonly WbDrawDispatcher _worldMeshes = worldMeshes
        ?? throw new ArgumentNullException(nameof(worldMeshes));

    public IRenderPackReceiverPipelineCandidate Prepare(
        IDirectionalShadowReceiverSource? source,
        int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        TerrainModernRenderer.DirectionalShadowReceiverPipelineState? terrainState =
            _terrain.PrepareDirectionalShadowReceiver(source, sampleCount);
        try
        {
            WbDrawDispatcher.DirectionalShadowReceiverPipelineState? worldState =
                _worldMeshes.PrepareDirectionalShadowReceiver(source, sampleCount);
            return new Candidate(this, terrainState, worldState);
        }
        catch
        {
            terrainState?.Dispose();
            throw;
        }
    }

    public void Publish(IRenderPackReceiverPipelineCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate is not Candidate prepared || !ReferenceEquals(prepared.Owner, this))
            throw new ArgumentException("Receiver candidate belongs to another coordinator.", nameof(candidate));

        (TerrainModernRenderer.DirectionalShadowReceiverPipelineState? terrainState,
            WbDrawDispatcher.DirectionalShadowReceiverPipelineState? worldState) = prepared.Take();
        TerrainModernRenderer.DirectionalShadowReceiverPipelineState? oldTerrain =
            _terrain.SwapDirectionalShadowReceiver(terrainState);
        WbDrawDispatcher.DirectionalShadowReceiverPipelineState? oldWorld =
            _worldMeshes.SwapDirectionalShadowReceiver(worldState);

        oldTerrain?.Dispose();
        oldWorld?.Dispose();
    }

    public void Clear()
    {
        TerrainModernRenderer.DirectionalShadowReceiverPipelineState? oldTerrain =
            _terrain.SwapDirectionalShadowReceiver(null);
        WbDrawDispatcher.DirectionalShadowReceiverPipelineState? oldWorld =
            _worldMeshes.SwapDirectionalShadowReceiver(null);
        oldTerrain?.Dispose();
        oldWorld?.Dispose();
    }

    private sealed class Candidate(
        RenderPackReceiverPipelineCoordinator owner,
        TerrainModernRenderer.DirectionalShadowReceiverPipelineState? terrain,
        WbDrawDispatcher.DirectionalShadowReceiverPipelineState? world) :
        IRenderPackReceiverPipelineCandidate
    {
        private TerrainModernRenderer.DirectionalShadowReceiverPipelineState? _terrain = terrain;
        private WbDrawDispatcher.DirectionalShadowReceiverPipelineState? _world = world;
        private bool _taken;

        internal RenderPackReceiverPipelineCoordinator Owner { get; } = owner;

        internal (TerrainModernRenderer.DirectionalShadowReceiverPipelineState?,
            WbDrawDispatcher.DirectionalShadowReceiverPipelineState?) Take()
        {
            ObjectDisposedException.ThrowIf(_taken, this);
            _taken = true;
            TerrainModernRenderer.DirectionalShadowReceiverPipelineState? terrainState = _terrain;
            WbDrawDispatcher.DirectionalShadowReceiverPipelineState? worldState = _world;
            _terrain = null;
            _world = null;
            return (terrainState, worldState);
        }

        public void Dispose()
        {
            if (_taken)
                return;
            _taken = true;
            _terrain?.Dispose();
            _world?.Dispose();
            _terrain = null;
            _world = null;
        }
    }
}
