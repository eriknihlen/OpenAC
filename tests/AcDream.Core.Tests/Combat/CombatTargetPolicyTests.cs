using AcDream.Core.Combat;
using AcDream.Core.Items;

namespace AcDream.Core.Tests.Combat;

public sealed class CombatTargetPolicyTests
{
    private const uint PlayerId = 0x50000001u;

    private static ClientObject Creature(
        uint id,
        uint flags = 0,
        uint petOwner = 0) => new()
    {
        ObjectId = id,
        Type = ItemType.Creature,
        PublicWeenieBitfield = flags,
        PetOwnerId = petOwner,
    };

    [Fact]
    public void AutomaticAcquisition_AcceptsOnlyAttackableNonPlayerMonster()
    {
        var player = Creature(PlayerId, SelectedObjectHealthPolicy.BfPlayer);
        var monster = Creature(0x50000010u, SelectedObjectHealthPolicy.BfAttackable);
        var friendlyNpc = Creature(0x50000011u);
        var pet = Creature(0x50000012u, petOwner: PlayerId);
        var hostilePlayer = Creature(
            0x50000013u,
            SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPlayerKiller);
        player.PublicWeenieBitfield |= SelectedObjectHealthPolicy.BfPlayerKiller;
        var attackableDoor = new ClientObject
        {
            ObjectId = 0x50000014u,
            Type = ItemType.Misc,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
        };

        Assert.True(CombatTargetPolicy.IsHostileMonster(PlayerId, player, monster));
        Assert.False(CombatTargetPolicy.IsHostileMonster(PlayerId, player, friendlyNpc));
        Assert.False(CombatTargetPolicy.IsHostileMonster(PlayerId, player, pet));
        Assert.False(CombatTargetPolicy.IsHostileMonster(PlayerId, player, hostilePlayer));
        Assert.False(CombatTargetPolicy.IsHostileMonster(PlayerId, player, attackableDoor));
    }
}
