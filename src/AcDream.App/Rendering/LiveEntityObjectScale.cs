using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

/// <summary>
/// The uniform scale a live object wears. It is a property of the object
/// itself — it arrives with the creation description and is what makes one
/// body larger or smaller than another — so every presentation of that object
/// has to read it from the same place.
///
/// Where the value is held depends on how far the object has been hydrated,
/// which is why this is a chain and not a field read: the animation owner has
/// it once the object animates, the creation snapshot has it before then, and
/// the projection carries the last fallback.
/// </summary>
internal static class LiveEntityObjectScale
{
    internal static float Resolve(LiveEntityRecord record, WorldEntity entity) =>
        Resolve(record.AnimationRuntime as LiveEntityAnimationState, record, entity);

    internal static float Resolve(
        LiveEntityAnimationState? animation,
        LiveEntityRecord record,
        WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entity);
        return animation?.Scale
            ?? record.Snapshot.Physics?.Scale
            ?? record.Snapshot.ObjScale
            ?? entity.Scale;
    }
}
