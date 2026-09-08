using AcDream.App.Composition;

namespace AcDream.App.Tests.Composition;

public sealed class CompositionAcquisitionScopeTests
{
    [Fact]
    public void RollbackReleasesOnlyUnpublishedResourcesInReverseOrder()
    {
        var calls = new List<string>();
        var scope = new CompositionAcquisitionScope();
        var first = scope.Own("first", new Resource("first"), value => calls.Add(value.Name));
        var published = scope.Own("published", new Resource("published"), value => calls.Add(value.Name));
        scope.Own("last", new Resource("last"), value => calls.Add(value.Name));
        published.Transfer();

        InvalidOperationException original = Assert.Throws<InvalidOperationException>(() =>
            scope.RollbackAndThrow(new InvalidOperationException("construction failed")));

        Assert.Equal("construction failed", original.Message);
        Assert.Equal(["last", "first"], calls);
        Assert.True(scope.IsCleanupComplete);
        Assert.Equal("first", first.Resource.Name);
    }

    [Fact]
    public void CleanupFailureRetainsOriginalAndRetriesOnlyUnfinishedAction()
    {
        var calls = new List<string>();
        int middleFailures = 1;
        var scope = new CompositionAcquisitionScope();
        scope.Own("first", new Resource("first"), value => calls.Add(value.Name));
        scope.Own("middle", new Resource("middle"), value =>
        {
            calls.Add(value.Name);
            if (middleFailures-- > 0)
                throw new InvalidOperationException("release failed");
        });
        scope.Own("last", new Resource("last"), value => calls.Add(value.Name));

        CompositionAcquisitionException failure =
            Assert.Throws<CompositionAcquisitionException>(() =>
                scope.RollbackAndThrow(new InvalidOperationException("construction failed")));

        Assert.False(failure.IsCleanupComplete);
        Assert.Contains(
            failure.InnerExceptions,
            error => error.Message == "construction failed");
        Assert.Equal(["last", "middle", "first"], calls);

        failure.RetryCleanup();
        failure.RetryCleanup();

        Assert.True(failure.IsCleanupComplete);
        Assert.Equal(["last", "middle", "first", "middle"], calls);
    }

    [Fact]
    public void PublicationTransfersOnlyAfterPublisherReturns()
    {
        int releases = 0;
        var failing = new CompositionAcquisitionScope();
        var lease = failing.Own(
            "resource",
            new Resource("resource"),
            _ => releases++);

        Assert.Throws<InvalidOperationException>(() =>
            lease.Publish(_ => throw new InvalidOperationException("publish failed")));
        Assert.Throws<InvalidOperationException>(() =>
            failing.RollbackAndThrow(new InvalidOperationException("phase failed")));
        Assert.Equal(1, releases);

        var successful = new CompositionAcquisitionScope();
        var transferred = successful.Own(
            "resource",
            new Resource("resource"),
            _ => releases++);
        transferred.Publish(_ => { });
        successful.Complete();

        Assert.Equal(1, releases);
        Assert.Throws<InvalidOperationException>(transferred.Transfer);
    }

    [Fact]
    public void CompleteRejectsAnUnpublishedAcquisition()
    {
        var scope = new CompositionAcquisitionScope();
        scope.Own("pending", new Resource("pending"), _ => { });

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(scope.Complete);

        Assert.Contains("pending", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingFactoryNeverCreatesCleanupOwnership()
    {
        var scope = new CompositionAcquisitionScope();
        int releases = 0;

        Assert.Throws<InvalidOperationException>(() =>
            scope.Acquire<Resource>(
                "resource",
                () => throw new InvalidOperationException("factory failed"),
                _ => releases++));

        Assert.True(scope.IsCleanupComplete);
        Assert.Equal(0, releases);
    }

    private sealed record Resource(string Name);
}
