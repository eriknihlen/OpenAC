using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class ResourceShutdownTransactionTests
{
    [Fact]
    public void AttemptsIndependentOperationsAndRetriesOnlyPendingOnes()
    {
        var calls = new List<string>();
        int retryAttempts = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owners",
            [
                new("first", () => calls.Add("first")),
                new("retry", () =>
                {
                    calls.Add("retry");
                    if (retryAttempts++ == 0)
                        throw new InvalidOperationException("synthetic transient failure");
                }),
                new("last", () => calls.Add("last")),
            ]),
            new ResourceShutdownStage("dependent",
            [
                new("dependent", () => calls.Add("dependent")),
            ]));

        transaction.CompleteOrThrow();

        Assert.Equal(["first", "retry", "last", "retry", "dependent"], calls);
        Assert.True(transaction.IsComplete);
    }

    [Fact]
    public void PersistentFailureNeverRunsDependentStage()
    {
        int failures = 0;
        int dependentCalls = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owner",
            [
                new("persistent", () =>
                {
                    failures++;
                    throw new InvalidOperationException("persistent");
                }),
            ]),
            new ResourceShutdownStage("dependent",
            [
                new("must-not-run", () => dependentCalls++),
            ]));

        Assert.Throws<AggregateException>(transaction.CompleteOrThrow);

        Assert.Equal(2, failures);
        Assert.Equal(0, dependentCalls);
        Assert.False(transaction.IsComplete);
    }

    [Fact]
    public void ReentrantCompletionDoesNotReplayActiveOperation()
    {
        ResourceShutdownTransaction? transaction = null;
        int calls = 0;
        transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("stage",
            [
                new("reentrant", () =>
                {
                    calls++;
                    transaction!.CompleteOrThrow();
                }),
            ]));

        transaction.CompleteOrThrow();

        Assert.Equal(1, calls);
        Assert.True(transaction.IsComplete);
    }

    [Fact]
    public void PersistentFailuresInOneStageAreAllAttemptedAndNamed()
    {
        int firstCalls = 0;
        int secondCalls = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owners",
            [
                new("first", () =>
                {
                    firstCalls++;
                    throw new InvalidOperationException("first failure");
                }),
                new("second", () =>
                {
                    secondCalls++;
                    throw new InvalidOperationException("second failure");
                }),
            ]));

        AggregateException error = Assert.Throws<AggregateException>(
            transaction.CompleteOrThrow);

        Assert.True(firstCalls > 0);
        Assert.Equal(firstCalls, secondCalls);
        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message.Contains("first", StringComparison.Ordinal));
        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message.Contains("second", StringComparison.Ordinal));
        Assert.False(transaction.IsComplete);
    }

    [Fact]
    public void ExplicitRetryAfterFailedCompletionDoesNotReplaySuccessfulOperations()
    {
        int completeCalls = 0;
        int pendingCalls = 0;
        bool allowPending = false;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("owners",
            [
                new("complete", () => completeCalls++),
                new("pending", () =>
                {
                    pendingCalls++;
                    if (!allowPending)
                        throw new InvalidOperationException("not yet");
                }),
            ]));

        Assert.Throws<AggregateException>(transaction.CompleteOrThrow);
        int pendingCallsBeforeRetry = pendingCalls;
        allowPending = true;

        transaction.CompleteOrThrow();

        Assert.Equal(1, completeCalls);
        Assert.Equal(pendingCallsBeforeRetry + 1, pendingCalls);
        Assert.True(transaction.IsComplete);
    }

    [Fact]
    public void EmptyStagesConvergeWithoutSkippingLaterOperations()
    {
        int calls = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("empty", []),
            new ResourceShutdownStage("owner",
            [
                new("owner", () => calls++),
            ]),
            new ResourceShutdownStage("empty tail", []));

        transaction.CompleteOrThrow();

        Assert.Equal(1, calls);
        Assert.True(transaction.IsComplete);
    }

    [Fact]
    public void ReportableFailureRetriesThenAllowsDependentStagesAndIsStructured()
    {
        var calls = new List<string>();
        var failure = new InvalidOperationException("physical detach failed");
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("physical ingress",
            [
                new(
                    "mouse callback",
                    () =>
                    {
                        calls.Add("detach");
                        throw failure;
                    },
                    ResourceShutdownOperationPolicy.ReportAndContinue),
            ]),
            new ResourceShutdownStage("dependent",
            [
                new("dependent", () => calls.Add("dependent")),
            ]));

        transaction.CompleteOrThrow();

        Assert.Equal(["detach", "detach", "dependent"], calls);
        Assert.True(transaction.IsComplete);
        ResourceShutdownCleanupFailure cleanup = Assert.Single(
            transaction.CleanupFailures);
        Assert.Equal("physical ingress", cleanup.Stage);
        Assert.Equal("mouse callback", cleanup.Operation);
        Assert.Same(failure, cleanup.Error);
    }

    [Fact]
    public void TransientReportableFailureConvergesWithoutCleanupFailure()
    {
        int calls = 0;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("physical ingress",
            [
                new(
                    "keyboard callback",
                    () =>
                    {
                        if (calls++ == 0)
                            throw new InvalidOperationException("transient");
                    },
                    ResourceShutdownOperationPolicy.ReportAndContinue),
            ]));

        transaction.CompleteOrThrow();

        Assert.Equal(2, calls);
        Assert.Empty(transaction.CleanupFailures);
    }

    [Fact]
    public void SettledReportableFailureNeverReplaysWhileHardBarrierRetries()
    {
        int softCalls = 0;
        int hardCalls = 0;
        bool allowHard = false;
        var transaction = new ResourceShutdownTransaction(
            new ResourceShutdownStage("mixed",
            [
                new(
                    "soft",
                    () =>
                    {
                        softCalls++;
                        throw new InvalidOperationException("soft");
                    },
                    ResourceShutdownOperationPolicy.ReportAndContinue),
                new("hard", () =>
                {
                    hardCalls++;
                    if (!allowHard)
                        throw new InvalidOperationException("hard");
                }),
            ]));

        Assert.Throws<AggregateException>(transaction.CompleteOrThrow);
        int settledSoftCalls = softCalls;
        allowHard = true;

        transaction.CompleteOrThrow();

        Assert.Equal(settledSoftCalls, softCalls);
        Assert.True(hardCalls > 1);
        Assert.True(transaction.IsComplete);
        Assert.Single(transaction.CleanupFailures);
    }
}
