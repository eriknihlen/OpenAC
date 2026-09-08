using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Entities;
using AcDream.Core.Physics;

namespace AcDream.App.World;

internal interface ILiveEntitySameGenerationUpdateSink
{
    void OnDescription(uint ownerGuid, PhysicsSpawnData description);
    void OnAppearance(ObjDescEvent.Parsed appearance);
    void OnParent(CreateParentUpdate parent);
    void OnPosition(WorldSession.EntityPositionUpdate position);
    void OnPickup(PickupEvent.Parsed pickup);
    void OnMovement(WorldSession.EntityMotionUpdate movement);
    void OnState(SetState.Parsed state);
    void OnVector(VectorUpdate.Parsed vector);
}

internal static class LiveEntitySameGenerationUpdateRouter
{
    public static void Apply(
        SameGenerationCreateObjectEvents refresh,
        ILiveEntitySameGenerationUpdateSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.OnDescription(refresh.Appearance.Guid, refresh.Description);
        sink.OnAppearance(refresh.Appearance);

        if (refresh.Parent is { } parent)
            sink.OnParent(parent);
        else if (refresh.Position is { } position)
            sink.OnPosition(position);
        else if (refresh.Pickup is { } pickup)
            sink.OnPickup(pickup);

        if (refresh.Movement is { } movement)
            sink.OnMovement(movement);
        sink.OnState(refresh.State);
        sink.OnVector(refresh.Vector);
    }
}
