using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class OrderedResourceTeardownTests
{
    [Fact]
    public void FailureStopsDependentsAndRetryResumesExactStage()
    {
        var calls = new List<int>();
        int attempts = 0;
        var teardown = new OrderedResourceTeardown(
            () => calls.Add(1),
            () =>
            {
                calls.Add(2);
                if (attempts++ == 0)
                    throw new InvalidOperationException("synthetic stage failure");
            },
            () => calls.Add(3));

        Assert.Throws<InvalidOperationException>(teardown.Advance);
        Assert.Equal([1, 2], calls);
        Assert.Equal(1, teardown.NextStage);

        teardown.Advance();

        Assert.Equal([1, 2, 2, 3], calls);
        Assert.True(teardown.IsComplete);
    }

    [Fact]
    public void ReentrantAdvanceDoesNotReplayActiveStage()
    {
        OrderedResourceTeardown? teardown = null;
        int first = 0;
        int second = 0;
        teardown = new OrderedResourceTeardown(
            () =>
            {
                first++;
                teardown!.Advance();
            },
            () => second++);

        teardown.Advance();

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.True(teardown.IsComplete);
    }
}
