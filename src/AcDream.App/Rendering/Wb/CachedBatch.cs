using System.Numerics;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering.Wb;

internal readonly record struct CachedBatch(
    GroupKey Key,
    GpuTextureSlot TextureSlot,
    Matrix4x4 RestPose,
    WbDrawDispatcher.InstanceGroup? Group = null,
    long GroupRegistration = 0);

internal readonly record struct CachedSelectionPart(
    int PartIndex,
    uint GfxObjId,
    Matrix4x4 RestPose);

internal sealed class EntityCacheEntry
{
    public required uint EntityId { get; init; }
    public required uint LandblockHint { get; init; }
    public required CachedBatch[] Batches { get; init; }
    public CachedSelectionPart[] SelectionParts { get; init; } = [];
}
