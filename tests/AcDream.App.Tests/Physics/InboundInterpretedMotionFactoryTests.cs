using AcDream.App.Physics;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests.Physics;

public sealed class InboundInterpretedMotionFactoryTests
{
    [Fact]
    public void EmptyWireStateUsesRetailConstructorDefaults()
    {
        var wire = new CreateObject.ServerMotionState(0, null);

        var result = InboundInterpretedMotionFactory.Create(wire);

        Assert.Equal(0x8000003Du, result.CurrentStyle);
        Assert.Equal(0x41000003u, result.ForwardCommand);
        Assert.Equal(1f, result.ForwardSpeed);
        Assert.Equal(0u, result.SideStepCommand);
        Assert.Equal(1f, result.SideStepSpeed);
        Assert.Equal(0u, result.TurnCommand);
        Assert.Equal(1f, result.TurnSpeed);
        Assert.Null(result.Actions);
    }

    [Fact]
    public void AttackCommandPreservesStampAutonomyAndSpeed()
    {
        var commands = new[]
        {
            new CreateObject.MotionItem(
                Command: 0x0058, // ThrustMed
                PackedSequence: 0x8007,
                Speed: 1.75f),
        };
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x3E,
            ForwardCommand: 0x0003,
            Commands: commands);

        var result = InboundInterpretedMotionFactory.Create(wire);

        Assert.Equal(0x8000003Eu, result.CurrentStyle);
        Assert.NotNull(result.Actions);
        var action = Assert.Single(result.Actions);
        Assert.Equal(0x10000058u, action.Command);
        Assert.Equal(7, action.Stamp);
        Assert.True(action.Autonomous);
        Assert.Equal(1.75f, action.Speed);
    }

    [Fact]
    public void AxesAndForwardCommandsUseSharedRuntimeCatalog()
    {
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x3F,
            ForwardCommand: 0x0007,
            ForwardSpeed: 2.85f,
            SideStepCommand: 0x000F,
            SideStepSpeed: -1.2f,
            TurnCommand: 0x000D,
            TurnSpeed: 1.5f);

        var result = InboundInterpretedMotionFactory.Create(wire);

        Assert.Equal(0x44000007u, result.ForwardCommand);
        Assert.Equal(2.85f, result.ForwardSpeed);
        Assert.Equal(0x6500000Fu, result.SideStepCommand);
        Assert.Equal(-1.2f, result.SideStepSpeed);
        Assert.Equal(0x6500000Du, result.TurnCommand);
        Assert.Equal(1.5f, result.TurnSpeed);
    }
}
