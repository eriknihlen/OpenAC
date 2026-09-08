using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime;

public readonly record struct RuntimeSimulationOwnershipSnapshot(
    RuntimeEntityObjectOwnershipSnapshot EntityObjects,
    RuntimePhysicsOwnershipSnapshot Physics,
    RuntimeGameplayOwnershipSnapshot Gameplay)
{
    public bool IsConverged =>
        EntityObjects.IsConverged
        && Physics.IsConverged
        && Gameplay.IsConverged;
}

public static class RuntimeSimulationOwnership
{
    public static RuntimeSimulationOwnershipSnapshot Capture(
        RuntimeEntityObjectLifetime entityObjects,
        RuntimeInventoryState inventory,
        RuntimeCharacterState character,
        RuntimeCommunicationState communication,
        RuntimeActionState actions,
        RuntimeLocalPlayerMovementState movement,
        RuntimeFellowshipState fellowship,
        RuntimeAllegianceState allegiance,
        RuntimeTradeState trade)
    {
        ArgumentNullException.ThrowIfNull(entityObjects);
        return new RuntimeSimulationOwnershipSnapshot(
            entityObjects.CaptureOwnership(),
            entityObjects.Physics.CaptureOwnership(),
            RuntimeGameplayOwnership.Capture(
                inventory,
                character,
                communication,
                actions,
                movement,
                fellowship,
                allegiance,
                trade));
    }
}
