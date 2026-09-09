using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class MotionCommandResolverTests
{
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
    public void ReconstructsKnownCommands(ushort wire, uint expected)
    {
        uint got = MotionCommandResolver.ReconstructFullCommand(wire);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void ZeroWireReturnsZero()
    {
        Assert.Equal(0u, MotionCommandResolver.ReconstructFullCommand(0));
    }

    [Fact]
    public void UnknownWireReturnsZero()
    {
        // 0xFFFF is not a real MotionCommand low-16.
        Assert.Equal(0u, MotionCommandResolver.ReconstructFullCommand(0xFFFF));
    }
}
