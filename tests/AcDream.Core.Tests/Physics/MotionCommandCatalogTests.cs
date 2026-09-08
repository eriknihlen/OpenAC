using System.Collections.Generic;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class MotionCommandCatalogTests
{
    private static readonly AceModernCommandCatalog AceModern = new();
    private static readonly Retail2013CommandCatalog Retail2013 = new();


    [Theory]
    [InlineData(0x0153, 0x10000153u)] // LifestoneRecall
    [InlineData(0x0166, 0x10000166u)] // MarketplaceRecall
    [InlineData(0x0171, 0x10000171u)] // AllegianceHometownRecall
    [InlineData(0x0173, 0x10000173u)] // OffhandSlashHigh
    public void AceModern_ResolvesShiftedRecallAndActionCommands(ushort wire, uint expected)
    {
        Assert.Equal(expected, AceModern.ReconstructFullCommand(wire));
    }

    [Theory]
    [InlineData(0x0003, 0x41000003u)]   // Ready
    [InlineData(0x0005, 0x45000005u)]   // WalkForward
    [InlineData(0x0007, 0x44000007u)]   // RunForward
    [InlineData(0x0006, 0x45000006u)]   // WalkBackward
    [InlineData(0x000D, 0x6500000Du)]   // TurnRight
    [InlineData(0x000E, 0x6500000Eu)]   // TurnLeft
    [InlineData(0x000F, 0x6500000Fu)]   // SideStepRight
    [InlineData(0x0015, 0x40000015u)]   // Falling
    [InlineData(0x0011, 0x40000011u)]   // Dead
    [InlineData(0x0012, 0x41000012u)]
    [InlineData(0x0013, 0x41000013u)]   // Sitting
    [InlineData(0x0014, 0x41000014u)]   // Sleeping
    [InlineData(0x0057, 0x10000057u)]   // Sanctuary (death)
    [InlineData(0x0058, 0x10000058u)]   // ThrustMed
    [InlineData(0x005B, 0x1000005Bu)]   // SlashHigh
    [InlineData(0x0061, 0x10000061u)]   // Shoot
    [InlineData(0x004B, 0x1000004Bu)]   // Jumpup
    [InlineData(0x0050, 0x10000050u)]   // FallDown
    [InlineData(0x0087, 0x13000087u)]   // Wave
    [InlineData(0x0080, 0x13000080u)]   // Laugh
    [InlineData(0x007D, 0x1300007Du)]   // BowDeep
    public void AceModern_MatchesExistingResolverMatrix(ushort wire, uint expected)
    {
        Assert.Equal(expected, AceModern.ReconstructFullCommand(wire));
    }


    [Theory]
    [InlineData(0x0150, 0x10000150u)] // LifestoneRecall (2013 value)
    [InlineData(0x0163, 0x10000163u)] // MarketplaceRecall (2013 value)
    [InlineData(0x016E, 0x1000016Eu)] // AllegianceHometownRecall (2013 value)
    [InlineData(0x0170, 0x10000170u)] // OffhandSlashHigh (2013 value)
    public void Retail2013_ResolvesUnshiftedRecallAndActionCommands(ushort wire, uint expected)
    {
        Assert.Equal(expected, Retail2013.ReconstructFullCommand(wire));
    }

    [Theory]
    [InlineData(0x0000, 0x80000000u)]
    [InlineData(0x0003, 0x41000003u)]
    [InlineData(0x0005, 0x45000005u)]
    [InlineData(0x0007, 0x44000007u)]
    [InlineData(0x000D, 0x6500000Du)]
    [InlineData(0x0153, 0x09000153u)] // NOT LifestoneRecall in 2013 numbering — anchor only.
    public void Retail2013_AnchorValuesMatchExtractedTable(ushort wire, uint expected)
    {
        Assert.Equal(expected, Retail2013.ReconstructFullCommand(wire));
    }

    [Fact]
    public void Retail2013_OutOfRangeWireReturnsZero()
    {
        Assert.Equal(0u, Retail2013.ReconstructFullCommand(0xFFFF));
    }

    [Fact]
    public void Retail2013_LastInRangeIndexResolves()
    {
        Assert.Equal(0x10000197u, Retail2013.ReconstructFullCommand(0x0197));
    }

    [Fact]
    public void Retail2013_FirstOutOfRangeIndexReturnsZero()
    {
        // 0x198 is one past the table's last valid index.
        Assert.Equal(0u, Retail2013.ReconstructFullCommand(0x0198));
    }


    [Fact]
    public void ClassPriority_LowerClassByteWins()
    {
        var candidates = new Dictionary<ushort, uint>
        {
            [0x0042] = 0u,
        };

        uint[] colliding = { 0x80000042u, 0x20000042u, 0x10000042u };
        uint resolved = AceModernCommandCatalog.ResolveClassPriority(colliding);

        Assert.Equal(0x10000042u, resolved);
    }

    [Fact]
    public void ClassPriority_OrderOfInputDoesNotMatter()
    {
        uint[] collidingReversed = { 0x10000099u, 0x41000099u, 0x80000099u };
        uint[] collidingForward = { 0x80000099u, 0x41000099u, 0x10000099u };

        Assert.Equal(
            AceModernCommandCatalog.ResolveClassPriority(collidingForward),
            AceModernCommandCatalog.ResolveClassPriority(collidingReversed));
        Assert.Equal(0x10000099u, AceModernCommandCatalog.ResolveClassPriority(collidingForward));
    }
}
