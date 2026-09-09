using AcDream.Core.Combat;
using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Combat;

public sealed class SelectedObjectHealthPolicyTests
{
    private const uint PlayerId = 0x50000001u;

    private static ClientObject Obj(
        uint id,
        ItemType type = ItemType.Creature,
        uint flags = 0,
        uint petOwner = 0) => new()
    {
        ObjectId = id,
        Type = type,
        PublicWeenieBitfield = flags,
        PetOwnerId = petOwner,
    };

    [Fact]
    public void Toolbar_PlayerAndPetShortCircuitAttackability()
    {
        var local = Obj(PlayerId, flags: SelectedObjectHealthPolicy.BfPlayer);
        var otherPlayer = Obj(0x50000002u, flags: SelectedObjectHealthPolicy.BfPlayer);
        var pet = Obj(0x50000003u, petOwner: PlayerId);

        Assert.True(SelectedObjectHealthPolicy.ShouldQueryHealth(PlayerId, local, otherPlayer));
        Assert.True(SelectedObjectHealthPolicy.ShouldQueryHealth(PlayerId, local, pet));
    }

    [Fact]
    public void Toolbar_SelfSelectionGetsHealthQuery()
    {
        var local = Obj(PlayerId, flags: SelectedObjectHealthPolicy.BfPlayer);

        Assert.True(SelectedObjectHealthPolicy.ShouldQueryHealth(PlayerId, local, local));
    }

    [Fact]
    public void Toolbar_AttackableCreatureTrue_FriendlyNpcAndAttackableDoorFalse()
    {
        var local = Obj(PlayerId, flags: SelectedObjectHealthPolicy.BfPlayer);
        var monster = Obj(0x50000010u, flags: SelectedObjectHealthPolicy.BfAttackable);
        var friendlyNpc = Obj(0x50000011u);
        var door = Obj(0x50000012u, ItemType.Misc, SelectedObjectHealthPolicy.BfAttackable);

        Assert.True(SelectedObjectHealthPolicy.ShouldQueryHealth(PlayerId, local, monster));
        Assert.False(SelectedObjectHealthPolicy.ShouldQueryHealth(PlayerId, local, friendlyNpc));
        Assert.False(SelectedObjectHealthPolicy.ShouldQueryHealth(PlayerId, local, door));
    }

    [Fact]
    public void ObjectIsAttackable_FreePkOnEitherCreatureSideReturnsTrue()
    {
        var normalPlayer = Obj(PlayerId, flags: SelectedObjectHealthPolicy.BfPlayer);
        var freePlayer = Obj(PlayerId,
            flags: SelectedObjectHealthPolicy.BfPlayer | SelectedObjectHealthPolicy.BfFreePkStatus);
        var normalCreature = Obj(0x50000020u);
        var freeCreature = Obj(0x50000021u, flags: SelectedObjectHealthPolicy.BfFreePkStatus);

        Assert.True(SelectedObjectHealthPolicy.ObjectIsAttackable(
            PlayerId, normalPlayer, freeCreature.ObjectId, freeCreature));
        Assert.True(SelectedObjectHealthPolicy.ObjectIsAttackable(
            PlayerId, freePlayer, normalCreature.ObjectId, normalCreature));
    }

    [Fact]
    public void ObjectIsAttackable_PlayerRequiresMatchingPkPool()
    {
        var pkPlayer = Obj(PlayerId,
            flags: SelectedObjectHealthPolicy.BfPlayer | SelectedObjectHealthPolicy.BfPlayerKiller);
        var pkTarget = Obj(0x50000030u,
            flags: SelectedObjectHealthPolicy.BfPlayer | SelectedObjectHealthPolicy.BfPlayerKiller);
        var pkLiteTarget = Obj(0x50000031u,
            flags: SelectedObjectHealthPolicy.BfPlayer | SelectedObjectHealthPolicy.BfPkLiteStatus);

        Assert.True(SelectedObjectHealthPolicy.ObjectIsAttackable(
            PlayerId, pkPlayer, pkTarget.ObjectId, pkTarget));
        Assert.False(SelectedObjectHealthPolicy.ObjectIsAttackable(
            PlayerId, pkPlayer, pkLiteTarget.ObjectId, pkLiteTarget));
    }

    [Fact]
    public void ObjectIsAttackable_ZeroOrSelfTrue_MissingPlayerBlocksNormalCreature()
    {
        var creature = Obj(0x50000040u, flags: SelectedObjectHealthPolicy.BfAttackable);

        Assert.True(SelectedObjectHealthPolicy.ObjectIsAttackable(PlayerId, null, 0u, null));
        Assert.True(SelectedObjectHealthPolicy.ObjectIsAttackable(PlayerId, null, PlayerId, null));
        Assert.False(SelectedObjectHealthPolicy.ObjectIsAttackable(
            PlayerId, null, creature.ObjectId, creature));
    }

    [Fact]
    public void ObjectIsAttackable_AttackablePetIsRejected()
    {
        var player = Obj(PlayerId, flags: SelectedObjectHealthPolicy.BfPlayer);
        var pet = Obj(
            0x50000041u,
            flags: SelectedObjectHealthPolicy.BfAttackable,
            petOwner: PlayerId);

        Assert.False(SelectedObjectHealthPolicy.ObjectIsAttackable(
            PlayerId, player, pet.ObjectId, pet));
    }
}
