namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeGameplayOwnershipSnapshot(
    RuntimeInventoryOwnershipSnapshot Inventory,
    RuntimeCharacterOwnershipSnapshot Character,
    RuntimeCommunicationOwnershipSnapshot Communication,
    RuntimeActionOwnershipSnapshot Actions,
    RuntimeLocalMovementOwnershipSnapshot Movement,
    RuntimeFellowshipOwnershipSnapshot Fellowship,
    RuntimeAllegianceOwnershipSnapshot Allegiance,
    RuntimeTradeOwnershipSnapshot Trade)
{
    public bool IsConverged =>
        Inventory.IsConverged
        && Character.IsConverged
        && Communication.IsConverged
        && Actions.IsConverged
        && Movement.IsConverged
        && Fellowship.IsConverged
        && Allegiance.IsConverged
        && Trade.IsConverged;
}

public static class RuntimeGameplayOwnership
{
    public static RuntimeGameplayOwnershipSnapshot Capture(
        RuntimeInventoryState inventory,
        RuntimeCharacterState character,
        RuntimeCommunicationState communication,
        RuntimeActionState actions,
        RuntimeLocalPlayerMovementState movement,
        RuntimeFellowshipState fellowship,
        RuntimeAllegianceState allegiance,
        RuntimeTradeState trade)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(communication);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(fellowship);
        ArgumentNullException.ThrowIfNull(allegiance);
        ArgumentNullException.ThrowIfNull(trade);
        return new RuntimeGameplayOwnershipSnapshot(
            inventory.CaptureOwnership(),
            character.CaptureOwnership(),
            communication.CaptureOwnership(),
            actions.CaptureOwnership(),
            movement.CaptureOwnership(),
            fellowship.CaptureOwnership(),
            allegiance.CaptureOwnership(),
            trade.CaptureOwnership());
    }
}
