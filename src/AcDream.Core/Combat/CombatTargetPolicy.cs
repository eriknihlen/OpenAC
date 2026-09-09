using AcDream.Core.Items;

namespace AcDream.Core.Combat;

/// <summary>
/// Eligibility policy for automatic monster acquisition.
/// </summary>
public static class CombatTargetPolicy
{
    public static bool IsHostileMonster(
        uint playerId,
        ClientObject? player,
        ClientObject? candidate)
    {
        if (candidate is null
            || candidate.ObjectId == playerId
            || (candidate.Type & ItemType.Creature) == 0
            || (candidate.PublicWeenieBitfield.GetValueOrDefault()
                & SelectedObjectHealthPolicy.BfPlayer) != 0
            || candidate.PetOwnerId != 0)
            return false;

        return SelectedObjectHealthPolicy.ObjectIsAttackable(
            playerId,
            player,
            candidate.ObjectId,
            candidate);
    }
}
