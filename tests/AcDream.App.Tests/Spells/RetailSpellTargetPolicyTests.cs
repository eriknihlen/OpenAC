using AcDream.Core.Items;
using AcDream.Core.Spells;

namespace AcDream.App.Tests.Spells;

public sealed class RetailSpellTargetPolicyTests
{
    [Fact]
    public void RejectsStackedTarget()
    {
        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            1u,
            new ClientObject { ObjectId = 2u, Name = "Scarabs", Type = ItemType.SpellComponents, StackSize = 2 },
            Spell(targetMask: (uint)ItemType.SpellComponents));

        Assert.False(result.Allowed);
        Assert.Contains("stack", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsSelfWithoutRetailSpecialTargetBits()
    {
        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            1u,
            new ClientObject { ObjectId = 1u, Name = "Self", Type = ItemType.Creature },
            Spell(targetMask: (uint)ItemType.Creature));

        Assert.False(result.Allowed);
        Assert.Contains("yourself", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsSelfWithSpecialTargetBits()
    {
        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            1u,
            new ClientObject
            {
                ObjectId = 1u,
                Name = "Self",
                Type = ItemType.Creature,
                PublicWeenieBitfield = (uint)PublicWeenieFlags.Player,
            },
            Spell(targetMask: 0x8101u));

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData(PublicWeenieFlags.None, 0u, false)]
    [InlineData(PublicWeenieFlags.Attackable, 0u, true)]
    [InlineData(PublicWeenieFlags.Player, 0u, true)]
    [InlineData(PublicWeenieFlags.Attackable, 99u, false)]
    public void SpecialTargetBits_RequirePlayerOrAttackableNonPet(
        PublicWeenieFlags flags,
        uint petOwner,
        bool expected)
    {
        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            1u,
            new ClientObject
            {
                ObjectId = 2u,
                Name = "Target",
                Type = ItemType.Creature,
                PublicWeenieBitfield = (uint)flags,
                PetOwnerId = petOwner,
            },
            Spell(targetMask: 0x8101u));

        Assert.Equal(expected, result.Allowed);
    }

    [Fact]
    public void RejectsTypeMaskMismatchWithObjectName()
    {
        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            1u,
            new ClientObject { ObjectId = 2u, Name = "Sword", Type = ItemType.MeleeWeapon },
            Spell(targetMask: (uint)ItemType.Creature));

        Assert.False(result.Allowed);
        Assert.Contains("Sword", result.Message);
    }

    [Theory]
    [InlineData(PublicWeenieFlags.Attackable, 0u, true)]
    [InlineData(PublicWeenieFlags.None, 0u, false)]
    [InlineData(PublicWeenieFlags.Player, 0u, true)]
    [InlineData(PublicWeenieFlags.Attackable, 9u, false)]
    public void DirectTypeMatch_StillRequiresPlayerOrAttackableNonPet(
        PublicWeenieFlags flags,
        uint petOwner,
        bool expected)
    {
        SpellTargetPolicyResult result = RetailSpellTargetPolicy.Evaluate(
            1u,
            new ClientObject
            {
                ObjectId = 2u,
                Name = "Target",
                Type = ItemType.Misc,
                PublicWeenieBitfield = (uint)flags,
                PetOwnerId = petOwner,
            },
            Spell(targetMask: (uint)ItemType.Misc));

        Assert.Equal(expected, result.Allowed);
    }

    private static SpellMetadata Spell(uint targetMask) => new(
        1, "Test", "War Magic", 1, 0, "", 0, 0, false, false, "",
        0, 0, 0, 1, false, false, false, 0, 0, 0, targetMask, 0);
}
