namespace AcDream.Core.Net.Tests;

public sealed class SubscriptionSetTests
{
    [Fact]
    public void Dispose_ReleasesInReverseOrderAndIsIdempotent()
    {
        var order = new List<int>();
        var subscriptions = new SubscriptionSet();
        subscriptions.Add(() => order.Add(1));
        subscriptions.Add(() => order.Add(2));
        subscriptions.Add(() => order.Add(3));

        subscriptions.Dispose();
        subscriptions.Dispose();

        Assert.Equal([3, 2, 1], order);
    }

    [Fact]
    public void Dispose_RetriesOnlyFailedReleasesUntilTheyConverge()
    {
        var order = new List<int>();
        var subscriptions = new SubscriptionSet();
        subscriptions.Add(() => order.Add(1));
        int secondFailures = 1;
        subscriptions.Add(() =>
        {
            order.Add(2);
            if (secondFailures-- > 0)
                throw new InvalidOperationException("second failed");
        });
        int thirdFailures = 1;
        subscriptions.Add(() =>
        {
            order.Add(3);
            if (thirdFailures-- > 0)
                throw new IOException("third failed");
        });

        AggregateException error = Assert.Throws<AggregateException>(
            subscriptions.Dispose);

        Assert.Equal([3, 2, 1], order);
        Assert.Collection(
            error.InnerExceptions,
            exception => Assert.IsType<IOException>(exception),
            exception => Assert.IsType<InvalidOperationException>(exception));

        subscriptions.Dispose();
        subscriptions.Dispose();

        Assert.Equal([3, 2, 1, 3, 2], order);
    }

    [Fact]
    public void Dispose_PersistentFailureNeverReplaysSuccessfulReleases()
    {
        var order = new List<int>();
        var subscriptions = new SubscriptionSet();
        subscriptions.Add(() => order.Add(1));
        subscriptions.Add(() =>
        {
            order.Add(2);
            throw new InvalidOperationException("persistent");
        });

        Assert.Throws<AggregateException>(subscriptions.Dispose);
        Assert.Throws<AggregateException>(subscriptions.Dispose);

        Assert.Equal([2, 1, 2], order);
    }
}
