namespace AcDream.Core.Items;

public static class PaperdollSelectionPolicy
{
    public static uint GetUpperInventoryObject(
        ClientObjectTable objects,
        uint playerId,
        EquipMask bodyLocationMask)
    {
        if (playerId == 0 || bodyLocationMask == EquipMask.None || objects.Get(playerId) is null)
            return 0;

        ClientObject? winner = null;
        foreach (ClientObject candidate in objects.Objects)
        {
            if ((candidate.CurrentlyEquippedLocation & bodyLocationMask) == EquipMask.None)
                continue;
            if (candidate.WielderId != playerId && candidate.ContainerId != playerId)
                continue;

            if (winner is null
                || EffectivePriority(candidate, bodyLocationMask)
                    > EffectivePriority(winner, bodyLocationMask))
                winner = candidate;
        }

        return winner?.ObjectId ?? playerId;
    }

    private static uint EffectivePriority(ClientObject item, EquipMask bodyLocationMask)
    {
        uint priority = item.Priority;
        uint coveredLocation = (uint)(item.CurrentlyEquippedLocation & bodyLocationMask);
        if (priority == 0 && coveredLocation is >= 0x200u and <= 0x4000u)
            priority = 0x7Fu;
        return priority;
    }
}
