using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class WorldRevealRenderResourceSchedulerTests
{
    [Fact]
    public void ReplacementGeneration_KeepsPriorityUntilExactGenerationEnds()
    {
        var first = new List<bool>();
        var second = new List<bool>();
        var scheduler = new WorldRevealRenderResourceScheduler(
            first.Add,
            second.Add);

        scheduler.BeginDestinationReveal(3);
        scheduler.BeginDestinationReveal(4);
        scheduler.EndDestinationReveal(3);
        scheduler.EndDestinationReveal(4);
        scheduler.EndDestinationReveal(4);

        Assert.Equal([true, true, false], first);
        Assert.Equal([true, true, false], second);
    }
}
