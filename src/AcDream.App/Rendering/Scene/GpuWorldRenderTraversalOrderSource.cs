using AcDream.App.Streaming;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Scene;

internal sealed class GpuWorldRenderTraversalOrderSource(
    GpuWorldState world) : IRenderTraversalOrderSource
{
    private readonly GpuWorldState _world =
        world ?? throw new ArgumentNullException(nameof(world));

    public bool TryGetTraversalSortKey(
        WorldEntity entity,
        out RenderSortKey sortKey) =>
        _world.TryGetRenderTraversalSortKey(entity, out sortKey);
}
