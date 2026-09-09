using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailObjectManagerTailTests
{
    [Fact]
    public void Run_UsesNamedRetailManagerOrder()
    {
        var calls = new List<string>();

        RetailObjectManagerTail.Run(
            checkDetection: () => calls.Add("detection"),
            handleTargeting: () => calls.Add("target"),
            movementUseTime: () => calls.Add("movement"),
            partArrayHandleMovement: () => calls.Add("part-array"),
            positionUseTime: () => calls.Add("position"));

        Assert.Equal(
            ["detection", "target", "movement", "part-array", "position"],
            calls);
    }

    [Fact]
    public void Run_SkipsMissingManagersWithoutDuplicatingNeighbours()
    {
        var calls = new List<string>();

        RetailObjectManagerTail.Run(
            checkDetection: null,
            handleTargeting: () => calls.Add("target"),
            movementUseTime: null,
            partArrayHandleMovement: () => calls.Add("part-array"),
            positionUseTime: null);

        Assert.Equal(["target", "part-array"], calls);
    }
}
