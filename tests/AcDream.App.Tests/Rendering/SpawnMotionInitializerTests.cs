using AcDream.App.Rendering;
using AcDream.Core.Net.Messages;
using DatReaderWriter.DBObjs;
using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Tests.Rendering;

public sealed class SpawnMotionInitializerTests
{
    [Fact]
    public void CorpseUsesAuthoritativeDeadMotionFromWire()
    {
        var table = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)0x8000003Du,
        };
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x003D,
            ForwardCommand: 0x0011);

        SpawnMotionInitializer.Plan plan = SpawnMotionInitializer.ResolvePlan(table, wire);

        Assert.Equal(0x8000003Du, plan.Style);
        Assert.Equal(0x40000011u, plan.Motion);
    }

    [Theory]
    [InlineData(0x000B, 0x4000000Bu)]
    [InlineData(0x000C, 0x4000000Cu)]
    public void DoorUsesAuthoritativeOnOrOffMotionFromWire(
        ushort forwardCommand,
        uint expectedMotion)
    {
        var table = new MotionTable();
        var wire = new CreateObject.ServerMotionState(
            Stance: 0x003D,
            ForwardCommand: forwardCommand);

        SpawnMotionInitializer.Plan plan = SpawnMotionInitializer.ResolvePlan(table, wire);

        Assert.Equal(0x8000003Du, plan.Style);
        Assert.Equal(expectedMotion, plan.Motion);
    }

    [Fact]
    public void MissingWireStateUsesMotionTableDefaults()
    {
        var table = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)0x8000003Eu,
        };

        SpawnMotionInitializer.Plan plan = SpawnMotionInitializer.ResolvePlan(table, null);

        Assert.Equal(0x8000003Eu, plan.Style);
        Assert.Equal(0x41000003u, plan.Motion);
    }
}
