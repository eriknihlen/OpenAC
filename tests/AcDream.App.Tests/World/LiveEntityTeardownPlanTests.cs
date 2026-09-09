using AcDream.App.World;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityTeardownPlanTests
{
    [Fact]
    public void AdvanceRetriesOnlyFailedSteps()
    {
        int first = 0;
        int middle = 0;
        int last = 0;
        var plan = new LiveEntityTeardownPlan(
        [
            () => first++,
            () =>
            {
                middle++;
                if (middle == 1)
                    throw new InvalidOperationException("middle failed");
            },
            () => last++,
        ]);

        Assert.Throws<AggregateException>(() => plan.Advance());
        Assert.Equal(2, plan.CompletedCount);

        plan.Advance();

        Assert.True(plan.IsComplete);
        Assert.Equal(1, first);
        Assert.Equal(2, middle);
        Assert.Equal(1, last);
    }

    [Fact]
    public void AdvanceAttemptsEveryIndependentFailureInOnePass()
    {
        int first = 0;
        int last = 0;
        var plan = new LiveEntityTeardownPlan(
        [
            () =>
            {
                first++;
                throw new InvalidOperationException("first");
            },
            () => last++,
        ]);

        Assert.Throws<AggregateException>(() => plan.Advance());

        Assert.Equal(1, first);
        Assert.Equal(1, last);
        Assert.Equal(1, plan.CompletedCount);
    }
}
