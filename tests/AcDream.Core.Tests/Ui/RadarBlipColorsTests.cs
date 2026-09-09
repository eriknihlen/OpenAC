using AcDream.Core.Items;
using AcDream.Core.Ui;

namespace AcDream.Core.Tests.Ui;

public sealed class RadarBlipColorsTests
{
    private const uint BfPlayer = 0x00000008u;
    private const uint BfAttackable = 0x00000010u;
    private const uint BfPlayerKiller = 0x00000020u;
    private const uint BfHiddenAdmin = 0x00000040u;
    private const uint BfVendor = 0x00000200u;
    private const uint BfPortal = 0x00040000u;
    private const uint BfAdmin = 0x00100000u;
    private const uint BfFreePk = 0x00200000u;
    private const uint BfPkLite = 0x02000000u;

    [Fact]
    public void Palette_UsesExactRetailFloatConstants()
    {
        Assert.Equal(new RadarBlipColors.Rgba(0.25f, 0.660000026f, 1f, 1f), RadarBlipColors.Blue);
        Assert.Equal(new RadarBlipColors.Rgba(1f, 0.670000017f, 0f, 1f), RadarBlipColors.Gold);
        Assert.Equal(new RadarBlipColors.Rgba(1f, 1f, 0.5f, 1f), RadarBlipColors.Yellow);
        Assert.Equal(new RadarBlipColors.Rgba(1f, 0.25f, 0.389999986f, 1f), RadarBlipColors.Red);
        Assert.Equal(new RadarBlipColors.Rgba(0.75f, 0.389999986f, 1f, 1f), RadarBlipColors.Purple);
        Assert.Equal(new RadarBlipColors.Rgba(1f, 0.660000026f, 0.75f, 1f), RadarBlipColors.Pink);
        Assert.Equal(new RadarBlipColors.Rgba(0f, 0.5f, 0.25f, 1f), RadarBlipColors.Green);
    }

    [Fact]
    public void Override_MapsValuesOneThroughTenInRetailOrder()
    {
        RadarBlipColors.Rgba[] expected =
        [
            RadarBlipColors.Blue,
            RadarBlipColors.Gold,
            RadarBlipColors.White,
            RadarBlipColors.Purple,
            RadarBlipColors.Red,
            RadarBlipColors.Pink,
            RadarBlipColors.Green,
            RadarBlipColors.Yellow,
            RadarBlipColors.Cyan,
            RadarBlipColors.BrightGreen,
        ];

        for (byte value = 1; value <= expected.Length; value++)
            Assert.Equal(expected[value - 1], RadarBlipColors.ForOverride(value));

        Assert.Equal(RadarBlipColors.Default, RadarBlipColors.ForOverride(11));
    }

    [Fact]
    public void WireOverride_WinsOverObjectClassification()
    {
        var traits = new RadarObjectTraits(
            BlipColorOverride: 1,
            IsPortal: true,
            IsVendor: true,
            IsPlayer: true,
            IsPlayerKiller: true);

        Assert.Equal(RadarBlipColors.Blue, RadarBlipColors.For(traits));
    }

    [Fact]
    public void PortalAndVendor_UsePurpleAndYellow()
    {
        Assert.Equal(
            RadarBlipColors.Purple,
            RadarBlipColors.For((uint)ItemType.Creature, BfPortal | BfVendor));
        Assert.Equal(
            RadarBlipColors.Yellow,
            RadarBlipColors.For((uint)ItemType.Creature, BfVendor));
    }

    [Fact]
    public void Creature_MustBeAttackableAndNotAPlayer()
    {
        Assert.Equal(
            RadarBlipColors.Gold,
            RadarBlipColors.For((uint)ItemType.Creature, BfAttackable));
        Assert.Equal(
            RadarBlipColors.White,
            RadarBlipColors.For((uint)ItemType.Creature, 0));
        Assert.Equal(
            RadarBlipColors.White,
            RadarBlipColors.For((uint)ItemType.Creature, BfAttackable | BfPlayer));
    }

    [Fact]
    public void Player_StatusDispatch_MatchesRetailPriority()
    {
        uint creature = (uint)ItemType.Creature;

        Assert.Equal(RadarBlipColors.Cyan, RadarBlipColors.For(creature, BfPlayer | BfAdmin));
        Assert.Equal(RadarBlipColors.Red, RadarBlipColors.For(creature, BfPlayer | BfPlayerKiller));
        Assert.Equal(RadarBlipColors.Pink, RadarBlipColors.For(creature, BfPlayer | BfPkLite));
        Assert.Equal(RadarBlipColors.Gold, RadarBlipColors.For(creature, BfPlayer | BfFreePk));
        Assert.Equal(
            RadarBlipColors.Red,
            RadarBlipColors.For(creature, BfPlayer | BfAdmin | BfHiddenAdmin | BfPlayerKiller));
    }

    [Fact]
    public void Fellowship_OverridesPlayerStatusColor()
    {
        var traits = new RadarObjectTraits(IsPlayer: true, IsPlayerKiller: true);
        var relationship = new RadarRelationshipTraits(
            IsFellowshipMember: true,
            IsFellowshipLeader: true);

        Assert.Equal(RadarBlipColors.BrightGreen, RadarBlipColors.For(traits, relationship));
    }

    [Fact]
    public void InvalidDescription_ReturnsDefault()
    {
        var traits = new RadarObjectTraits(IsValid: false, BlipColorOverride: 1, IsPortal: true);

        Assert.Equal(RadarBlipColors.Default, RadarBlipColors.For(traits));
    }

    [Fact]
    public void Rgba8Projection_RemainsAvailableToExistingUiConsumers()
    {
        Assert.Equal((byte)64, RadarBlipColors.Blue.R);
        Assert.Equal((byte)168, RadarBlipColors.Blue.G);
        Assert.Equal(0xFFFFA840u, RadarBlipColors.Blue.ToAbgr32());
    }
}
