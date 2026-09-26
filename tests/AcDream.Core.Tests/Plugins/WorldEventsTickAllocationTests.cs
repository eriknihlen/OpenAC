using AcDream.Core.Plugins;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// The plugin tick runs about 67 times a second, with several subscribers
/// whether or not any plugin is loaded. Handing it out allocates nothing per
/// tick, however many subscribers there are: a copied handler list per tick
/// costs an array that grows with each subscriber. The bound is under one
/// byte per tick on average, so a one-time runtime allocation charged to the
/// thread cannot fail it but any per-tick allocation does.
/// </summary>
public class WorldEventsTickAllocationTests
{
    [Fact]
    public void TheTickReachesEverySubscriberWithoutAllocatingPerTick()
    {
        const int ticks = 10_000;
        var events = new WorldEvents(_ => { });
        var counts = new int[3];
        events.Tick += _ => counts[0]++;
        events.Tick += _ => counts[1]++;
        events.Tick += _ => counts[2]++;
        for (int warmup = 0; warmup < 64; warmup++)
            events.FireTick(0.015);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 0; tick < ticks; tick++)
            events.FireTick(0.015);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.All(counts, static count => Assert.Equal(64 + ticks, count));
        Assert.InRange(allocated, 0L, ticks - 1L);
    }
}
