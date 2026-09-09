using AcDream.Core.Items;

namespace AcDream.Core.Combat;

public static class SelectedObjectHealthPolicy
{
    public const uint BfPlayer = 0x00000008u;
    public const uint BfAttackable = 0x00000010u;
    public const uint BfPlayerKiller = 0x00000020u;
    public const uint BfFreePkStatus = 0x00200000u;
    public const uint BfPkLiteStatus = 0x02000000u;

    public static bool ShouldQueryHealth(
        uint playerId,
        ClientObject? player,
        ClientObject? selected)
    {
        if (selected is null)
            return false;

        uint flags = selected.PublicWeenieBitfield ?? 0u;
        return (flags & BfPlayer) != 0
            || selected.PetOwnerId != 0
            || ObjectIsAttackable(playerId, player, selected.ObjectId, selected);
    }

    public static bool ObjectIsAttackable(
        uint playerId,
        ClientObject? player,
        uint targetId,
        ClientObject? target)
    {
        if (targetId == 0 || targetId == playerId)
            return true;

        if (target is null || (target.Type & ItemType.Creature) == 0)
            return false;

        uint targetFlags = target.PublicWeenieBitfield ?? 0u;
        if ((targetFlags & BfFreePkStatus) != 0)
            return true;

        if (player is null)
            return false;

        uint playerFlags = player.PublicWeenieBitfield ?? 0u;
        if ((playerFlags & BfFreePkStatus) != 0)
            return true;

        if ((targetFlags & BfPlayer) != 0)
        {
            if ((targetFlags & BfPlayerKiller) != 0
                && (playerFlags & BfPlayerKiller) != 0)
                return true;

            return (targetFlags & BfPkLiteStatus) != 0
                && (playerFlags & BfPkLiteStatus) != 0;
        }

        if (target.PetOwnerId != 0)
            return false;

        return (targetFlags & BfAttackable) != 0;
    }
}
